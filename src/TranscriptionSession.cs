using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace WinRealtimeWhisper
{
    /// <summary>
    /// 1セッション(=1回の録音開始〜停止)の文字起こし結果。
    /// 行の状態は画面に表示したまま、テキスト/WAVへは確定した内容だけを書き出す。
    /// </summary>
    internal sealed class TranscriptionSession
    {
        private readonly List<TranscriptLine> _lines = new List<TranscriptLine>();

        public TranscriptionSession()
        {
            StartedAt = DateTime.Now;
        }

        public DateTime StartedAt { get; private set; }
        public DateTime? StoppedAt { get; set; }

        public IReadOnlyList<TranscriptLine> Lines
        {
            get { return _lines; }
        }

        /// <summary>録音中の一時行(リアルタイム表示用)。確定した行には含めない。</summary>
        public TranscriptLine Pending { get; set; }

        public TranscriptLine AppendFinal(string text, TimeSpan offset)
        {
            Pending = null;
            string clean = (text ?? string.Empty).Trim();
            if (clean.Length == 0)
            {
                return null;
            }

            var line = new TranscriptLine
            {
                IsFinal = true,
                Time = TimeOf(offset),
                Text = clean
            };
            _lines.Add(line);
            return line;
        }

        public void Reset()
        {
            _lines.Clear();
            Pending = null;
        }

        public string ToPlainText()
        {
            var sb = new StringBuilder();

            foreach (var line in _lines)
            {
                sb.AppendLine(line.Text);
            }

            return sb.ToString();
        }

        private string TimeOf(TimeSpan offset)
        {
            // 認識結果のオフセットは録音開始からの相対時間
            DateTime absolute = StartedAt + (offset < TimeSpan.Zero ? TimeSpan.Zero : offset);
            return absolute.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }
    }

    internal sealed class TranscriptLine
    {
        public bool IsFinal { get; set; }
        public string Time { get; set; }
        public string Text { get; set; }

        public override string ToString()
        {
            return string.IsNullOrEmpty(Time) ? Text : "[" + Time + "] " + Text;
        }
    }

    /// <summary>保存先: (設定)\history\session_yyyyMMdd_HHmmss.txt（既定はドキュメント\WinRealtimeWhisper\history）</summary>
    internal static class HistoryStore
    {
        /// <summary>現在のセッションで使う保存先。設定の「OK」で更新される。</summary>
        public static string RootDirectory { get; set; }

        static HistoryStore()
        {
            RootDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "WinRealtimeWhisper",
                "history");
        }

        public static string CreateSessionFilePath(DateTime startedAt)
        {
            Directory.CreateDirectory(RootDirectory);
            string name = "session_" + startedAt.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".txt";
            return Path.Combine(RootDirectory, name);
        }

        /// <summary>セッションファイルの一覧(新しい順)。</summary>
        public static IReadOnlyList<string> ListSessionFiles()
        {
            if (!Directory.Exists(RootDirectory))
            {
                return new List<string>();
            }

            try
            {
                return Directory.GetFiles(RootDirectory, "session_*.txt")
                    .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (IOException)
            {
                return new List<string>();
            }
        }

        public static string ReadAllText(string path)
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }

        public static void WriteAllText(string path, string text)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, text, new UTF8Encoding(true));
        }
    }
}
