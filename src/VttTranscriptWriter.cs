using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace WinRealtimeWhisper
{
    /// <summary>
    /// 録音中の確定テキストを WebVTT として追記し続ける。
    ///
    /// 仕様: docs/transcript-vtt.md
    ///   - 各ブロックは空行で終端し、1 回の Write で書く（tail 側が途中の行を読まないように）
    ///   - 推論スレッドから呼ばれるため、内部で排他する
    ///   - 失敗しても録音は続ける（記録して握りつぶす）
    /// </summary>
    internal sealed class VttTranscriptWriter : IDisposable
    {
        private readonly object _sync = new object();
        private readonly string _directory;
        private readonly string _pointerPath;
        private readonly string _sessionId;
        private readonly string _language;
        private readonly string _model;
        private readonly double _chunkSeconds;
        private readonly DateTime _startedAt;

        private string _baseName;               // 例: 2026-09-28_2149（衝突時は連番付き）

        private StreamWriter _writer;
        private string _livePath;
        private string _finalPath;
        private int _cueId;
        private bool _finished;
        private DateTime _lastHeartbeatUtc = DateTime.MinValue;

        /// <summary>
        /// 書き込み中のファイルを作る。ヘッダと NOTE session はすぐには書かず、
        /// current.txt の更新と一緒に <see cref="Start"/> で行う。
        /// </summary>
        public VttTranscriptWriter(
            string directory,
            DateTime startedAt,
            string language,
            string model,
            double chunkSeconds)
        {
            _directory = directory;
            _startedAt = startedAt;
            _language = string.IsNullOrEmpty(language) ? "ja" : language;
            _model = model ?? string.Empty;
            _chunkSeconds = chunkSeconds;

            _sessionId = startedAt.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture);
            _baseName = _sessionId;
            _pointerPath = Path.Combine(directory, "current.txt");

            _livePath = Path.Combine(directory, _baseName + ".live.vtt");
            _finalPath = Path.Combine(directory, _baseName + ".vtt");
        }

        /// <summary>書き込み中のパス。<see cref="Start"/> が衝突回避のために変えることがある。</summary>
        public string LivePath
        {
            get { return _livePath; }
        }

        /// <summary>停止後のパス。</summary>
        public string FinalPath
        {
            get { return _finalPath; }
        }

        /// <summary>
        /// 同じ分に複数のセッションが始まったときのため、末尾に連番を足して衝突を避ける。
        /// </summary>
        private void EnsureUniquePath()
        {
            if (!File.Exists(_livePath) && !File.Exists(_finalPath))
            {
                return;
            }

            for (int i = 2; i < 1000; i++)
            {
                string basis = _sessionId + "-" + i.ToString(CultureInfo.InvariantCulture);
                string live = Path.Combine(_directory, basis + ".live.vtt");
                string final = Path.Combine(_directory, basis + ".vtt");

                if (!File.Exists(live) && !File.Exists(final))
                {
                    _baseName = basis;
                    _livePath = live;
                    _finalPath = final;
                    return;
                }
            }
        }

        /// <summary>ファイルを開き、ヘッダ・NOTE session を書き、current.txt を更新する。</summary>
        public void Start()
        {
            lock (_sync)
            {
                if (_writer != null || _finished)
                {
                    return;
                }

                try
                {
                    Directory.CreateDirectory(_directory);

                    EnsureUniquePath();
                    string path = _livePath;
                    var stream = new FileStream(
                        path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                    _writer = new StreamWriter(stream, new UTF8Encoding(false));
                    _writer.AutoFlush = true;

                    var sb = new StringBuilder();
                    sb.Append("WEBVTT\n");
                    sb.Append("Kind: captions\n");
                    sb.Append("Language: ").Append(_language).Append('\n');
                    sb.Append("X-WINREALTIMEWHISPER-SESSION: ").Append(_baseName).Append('\n');
                    sb.Append('\n');

                    sb.Append("NOTE session\n");
                    sb.Append("id: ").Append(_baseName).Append('\n');
                    sb.Append("started: ").Append(Iso(_startedAt)).Append('\n');
                    sb.Append("source: WinRealtimeWhisper\n");
                    sb.Append("model: ").Append(_model).Append('\n');
                    sb.Append("language: ").Append(_language).Append('\n');
                    sb.Append("chunk-seconds: ").Append(_chunkSeconds.ToString("0.##", CultureInfo.InvariantCulture)).Append('\n');
                    sb.Append('\n');

                    WriteBlock(sb.ToString());
                    WritePointer(Path.GetFileName(path));
                    _lastHeartbeatUtc = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    DiagLog.WriteException("[vtt] start failed", ex);
                    CloseWriter();
                }
            }
        }

        /// <summary>確定した 1 区間をキューとして追記する。<paramref name="speaker"/> が空なら &lt;v&gt; を付けない。</summary>
        public void WriteCue(TimeSpan offset, TimeSpan duration, string text, string speaker)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            lock (_sync)
            {
                if (_writer == null || _finished)
                {
                    return;
                }

                try
                {
                    if (offset < TimeSpan.Zero)
                    {
                        offset = TimeSpan.Zero;
                    }

                    if (duration <= TimeSpan.Zero)
                    {
                        duration = TimeSpan.FromSeconds(1);
                    }

                    _cueId++;

                    var sb = new StringBuilder();
                    sb.Append(_cueId.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    sb.Append(FormatTime(offset)).Append(" --> ").Append(FormatTime(offset + duration)).Append('\n');

                    if (!string.IsNullOrEmpty(speaker))
                    {
                        sb.Append("<v ").Append(speaker).Append('>');
                    }

                    sb.Append(SingleLine(text)).Append('\n');
                    sb.Append('\n');

                    WriteBlock(sb.ToString());
                }
                catch (Exception ex)
                {
                    DiagLog.WriteException("[vtt] cue write failed", ex);
                }
            }
        }

        /// <summary>一定間隔で生存を知らせる。呼ぶたびに間隔を見て、必要なら書く。</summary>
        public void Heartbeat(TimeSpan interval)
        {
            lock (_sync)
            {
                if (_writer == null || _finished)
                {
                    return;
                }

                DateTime now = DateTime.UtcNow;
                if (now - _lastHeartbeatUtc < interval)
                {
                    return;
                }

                _lastHeartbeatUtc = now;

                try
                {
                    WriteBlock("NOTE heartbeat " + Iso(DateTime.Now) + "\n\n");
                }
                catch (Exception ex)
                {
                    DiagLog.WriteException("[vtt] heartbeat write failed", ex);
                }
            }
        }

        /// <summary>session_end を書いて閉じ、.vtt へリネームして current.txt を消す。</summary>
        public void Finish()
        {
            lock (_sync)
            {
                if (_finished)
                {
                    return;
                }

                _finished = true;

                if (_writer == null)
                {
                    return;
                }

                string path = null;
                try
                {
                    path = ((FileStream)(_writer.BaseStream)).Name;
                    WriteBlock("NOTE session_end " + Iso(DateTime.Now) + "\n\n");
                }
                catch (Exception ex)
                {
                    DiagLog.WriteException("[vtt] session_end write failed", ex);
                }

                CloseWriter();
                ClearPointer();

                if (path != null)
                {
                    try
                    {
                        string target = path.EndsWith(".live.vtt", StringComparison.OrdinalIgnoreCase)
                            ? path.Substring(0, path.Length - ".live.vtt".Length) + ".vtt"
                            : path;
                        File.Move(path, target);
                    }
                    catch (Exception ex)
                    {
                        DiagLog.WriteException("[vtt] rename failed", ex);
                    }
                }
            }
        }

        public void Dispose()
        {
            Finish();
        }

        /// <summary>
        /// 停止後にできるファイルのパスを予測する（衝突回避の連番は考慮しない）。
        /// 実行後に場所を知らせるためだけに使う。
        /// </summary>
        public static string PredictFinalPath(string directory, DateTime startedAt)
        {
            string baseName = startedAt.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture);
            return Path.Combine(directory, baseName + ".vtt");
        }

        /// <summary>1 ブロックを 1 回の Write で書く。末尾の空行まで含める。</summary>
        private void WriteBlock(string block)
        {
            _writer.Write(block);
            _writer.Flush();
        }

        private void CloseWriter()
        {
            if (_writer == null)
            {
                return;
            }

            try
            {
                _writer.Dispose();
            }
            catch (Exception)
            {
            }

            _writer = null;
        }

        /// <summary>current.txt をアトミックに更新する。</summary>
        private void WritePointer(string fileName)
        {
            try
            {
                string tmp = _pointerPath + ".tmp";
                File.WriteAllText(tmp, fileName + "\n", new UTF8Encoding(false));

                if (File.Exists(_pointerPath))
                {
                    File.Replace(tmp, _pointerPath, null);
                }
                else
                {
                    File.Move(tmp, _pointerPath);
                }
            }
            catch (Exception ex)
            {
                DiagLog.WriteException("[vtt] pointer write failed", ex);
            }
        }

        private void ClearPointer()
        {
            try
            {
                if (File.Exists(_pointerPath))
                {
                    File.Delete(_pointerPath);
                }
            }
            catch (Exception ex)
            {
                DiagLog.WriteException("[vtt] pointer clear failed", ex);
            }
        }

        internal static string FormatTime(TimeSpan value)
        {
            if (value < TimeSpan.Zero)
            {
                value = TimeSpan.Zero;
            }

            int hours = (int)value.TotalHours;
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:00}:{1:00}:{2:00}.{3:000}",
                hours, value.Minutes, value.Seconds, value.Milliseconds);
        }

        internal static string Iso(DateTime value)
        {
            return value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        }

        /// <summary>VTT のキューは 1 行でなければならない。改行を空白に潰す。</summary>
        private static string SingleLine(string text)
        {
            return text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
        }
    }
}
