using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace WinRealtimeWhisper
{
    /// <summary>
    /// Whisper のワーカー設定。UI から渡される実行時パラメータ。
    /// </summary>
    internal sealed class WhisperOptions
    {
        /// <summary>ggml モデルファイルのパス。</summary>
        public string ModelPath { get; set; }

        /// <summary>認識させる言語（ja / en / auto）。</summary>
        public string Language { get; set; }

        /// <summary>推論スレッド数。0 で自動（物理コア数を目安に決める）。</summary>
        public int Threads { get; set; }

        /// <summary>認識区切りの最小長（秒）。短すぎる区間は文脈がなく精度が落ちる。</summary>
        public double MinChunkSeconds { get; set; }

        /// <summary>無音がこの秒数続いたら区切る。</summary>
        public double SilenceSplitSeconds { get; set; }

        /// <summary>認識区切りの最大長（秒）。無音が来なくてもここで切る。</summary>
        public double MaxChunkSeconds { get; set; }

        /// <summary>推論に回す区間の末尾に残す余白（秒）。語尾の欠けを防ぐ。</summary>
        public double PadSeconds { get; set; }

        /// <summary>RMS がこれ未満なら無音とみなす。</summary>
        public double SilenceRms { get; set; }

        /// <summary>これ未満の長さの区間は推論しない（秒）。</summary>
        public double MinProcessSeconds { get; set; }

        /// <summary>Vulkan（GPU）を優先するか。使えなければ CPU へ落ちる。</summary>
        public bool PreferGpu { get; set; }

        public WhisperOptions()
        {
            ModelPath = string.Empty;
            Language = "ja";
            Threads = 0;
            MinChunkSeconds = 6.0;
            SilenceSplitSeconds = 0.8;
            MaxChunkSeconds = 25.0;
            PadSeconds = 0.25;
            SilenceRms = 0.0022;
            MinProcessSeconds = 0.5;
            PreferGpu = true;
        }
    }

    /// <summary>
    /// 区間の認識結果。確定テキストと、録音開始からの相対時刻。
    /// </summary>
    internal sealed class WhisperSegmentResult
    {
        public WhisperSegmentResult(string text, TimeSpan offset)
            : this(text, offset, TimeSpan.Zero)
        {
        }

        public WhisperSegmentResult(string text, TimeSpan offset, TimeSpan duration)
        {
            Text = text;
            Offset = offset;
            Duration = duration;
        }

        public string Text { get; private set; }

        /// <summary>録音開始からの相対時刻。</summary>
        public TimeSpan Offset { get; private set; }

        /// <summary>区間の長さ。0 のときは不明。</summary>
        public TimeSpan Duration { get; private set; }

        public override string ToString()
        {
            return "[" + Offset.ToString(@"hh\:mm\:ss") + "] " + Text;
        }
    }

    /// <summary>
    /// 16kHz モノラルの float サンプルを受け取り、Whisper で区間ごとに文字起こしする。
    ///
    /// 動作:
    ///   1. Enqueue() でサンプルを溜める（NAudio のコールバックスレッドから呼ばれる）
    ///   2. 区切りスレッドが無音/長さで区間を切り出す
    ///   3. 推論スレッドが区間を 1 つずつ Whisper に渡し、結果を ResultReady で通知
    ///
    /// 推論は CPU 実行で実時間より遅くなりうるため、区切りと推論を別スレッドに分けている。
    /// 推論待ちが溜まりすぎた場合は古い区間を捨てて、遅延が無限に伸びないようにする。
    /// </summary>
    internal sealed class WhisperRecognizer : IDisposable
    {
        internal const int SampleRate = 16000;

        private readonly WhisperOptions _options;
        private readonly object _bufferSync = new object();     // _pending / _baseSamples
        private readonly object _queueSync = new object();      // _chunks
        private List<float> _pending = new List<float>();
        private readonly Queue<Chunk> _chunks = new Queue<Chunk>();
        private readonly ManualResetEventSlim _hasWork = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _hasChunk = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _segmentDone = new ManualResetEventSlim(false);

        private WhisperRunner _runner;
        private Thread _segmentThread;
        private Thread _inferThread;
        private volatile bool _stopping;
        private volatile bool _running;

        private long _baseSamples;          // 既に切り出したサンプル数（相対時刻の基準）
        private int _trailingSilence;       // 現在の区間の末尾にある連続無音サンプル数

        private long _inferenceCount;
        private long _inferenceMs;
        private long _audioSamples;
        private int _droppedChunks;
        private int _emittedChunks;

        public event EventHandler<WhisperSegmentResult> ResultReady;

        public WhisperRecognizer(WhisperOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException("options");
            }

            _options = options;
        }

        /// <summary>
        /// 読み込み済みのファクトリを共有して使う。複数の録音系統で
        /// 同一モデルを使い回すとき、モデルのメモリを 1 つ分に抑えられる。
        /// </summary>
        public WhisperRecognizer(WhisperOptions options, WhisperFactory factory)
            : this(options)
        {
            _sharedFactory = factory;
        }

        /// <summary>共有する場合のファクトリ。null なら Initialize で自前ロードする。</summary>
        private readonly WhisperFactory _sharedFactory;

        /// <summary>実際に使われた推論スレッド数。</summary>
        public int EffectiveThreads
        {
            get { return _runner != null ? _runner.Threads : 0; }
        }

        /// <summary>推論にかかった合計時間。認識が追いついているかの判断に使う。</summary>
        public long TotalInferenceMs
        {
            get { return Interlocked.Read(ref _inferenceMs); }
        }

        public long InferenceCount
        {
            get { return Interlocked.Read(ref _inferenceCount); }
        }

        /// <summary>切り出した区間の総数。</summary>
        public int EmittedChunks
        {
            get { return Volatile.Read(ref _emittedChunks); }
        }

        /// <summary>推論待ちが溢れて捨てた区間数。0 であることが望ましい。</summary>
        public int DroppedChunks
        {
            get { return Volatile.Read(ref _droppedChunks); }
        }

        /// <summary>推論待ちの区間数。停止時にこれが多いほど、確定まで時間がかかる。</summary>
        public int PendingChunks
        {
            get
            {
                lock (_queueSync)
                {
                    return _chunks.Count;
                }
            }
        }

        /// <summary>まだ区間に切り出されていない音声のサンプル数（16kHz モノラル）。</summary>
        public int PendingSamples
        {
            get
            {
                lock (_bufferSync)
                {
                    return _pending.Count;
                }
            }
        }

        /// <summary>
        /// 停止時に確定が必要な残り時間（秒）。
        /// 「未処理の音声」＋「推論待ちの区間」を合わせた長さ。
        /// </summary>
        public double BacklogSeconds
        {
            get
            {
                long samples;
                lock (_bufferSync)
                {
                    samples = _pending.Count;
                }

                lock (_queueSync)
                {
                    foreach (var chunk in _chunks)
                    {
                        samples += chunk.Samples.Length;
                    }
                }

                return (double)samples / SampleRate;
            }
        }

        /// <summary>推論の準備（モデル読み込み）を行う。失敗時は例外を投げる。</summary>
        public void Initialize()
        {
            string modelPath = _options.ModelPath;
            if (_sharedFactory == null && (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath)))
            {
                throw new FileNotFoundException(
                    "Whisper のモデルファイルが見つかりません: " + modelPath, modelPath);
            }

            int threads = _options.Threads > 0
                ? _options.Threads
                : Math.Max(1, Math.Min(8, Environment.ProcessorCount / 2 + 1));

            if (_sharedFactory != null)
            {
                DiagLog.Write("[whisper] reusing shared factory (threads=" + threads + ")");
            }
            else
            {
                DiagLog.Write("[whisper] model=" + modelPath
                    + " (" + new FileInfo(modelPath).Length / (1024 * 1024) + " MB)");
            }

            var sw = Stopwatch.StartNew();
            _runner = _sharedFactory != null
                ? new WhisperRunner(_sharedFactory, threads, _options.Language)
                : new WhisperRunner(modelPath, threads, _options.Language, _options.PreferGpu);
            sw.Stop();

            DiagLog.Write("[whisper] model loaded in " + sw.ElapsedMilliseconds + " ms");
            DiagLog.Write("[whisper] threads=" + _runner.Threads + " cpu=" + Environment.ProcessorCount);
            DiagLog.Write("[whisper] backend=" + WhisperRunner.LoadedLibrary
                + " preferGpu=" + _options.PreferGpu);
            DiagLog.Write("[whisper] runtime: " + _runner.RuntimeInfo);
            DiagLog.Write(string.Format(
                "[whisper] chunk: min={0}s silenceSplit={1}s max={2}s silenceRms={3}",
                _options.MinChunkSeconds, _options.SilenceSplitSeconds,
                _options.MaxChunkSeconds, _options.SilenceRms));
        }

        public void Start()
        {
            if (_running || _runner == null)
            {
                return;
            }

            _stopping = false;
            _running = true;
            _segmentDone.Reset();

            _segmentThread = new Thread(SegmentLoop);
            _segmentThread.IsBackground = true;
            _segmentThread.Name = "WinRealtimeWhisper whisper segmenter";
            _segmentThread.Start();

            _inferThread = new Thread(InferenceLoop);
            _inferThread.IsBackground = true;
            _inferThread.Name = "WinRealtimeWhisper whisper inference";
            _inferThread.Start();
        }

        /// <summary>16kHz モノラルのサンプルを投入する。呼び出し側は待たされない。</summary>
        public void Enqueue(float[] samples, int count)
        {
            if (_stopping || samples == null || count <= 0)
            {
                return;
            }

            int n = Math.Min(count, samples.Length);
            int limit = SampleRate * (int)Math.Ceiling(_options.MaxChunkSeconds * 4);

            lock (_bufferSync)
            {
                for (int i = 0; i < n; i++)
                {
                    _pending.Add(samples[i]);
                }

                // 遅延が無限に伸びないよう、古い分から捨てる
                if (_pending.Count > limit)
                {
                    int excess = _pending.Count - limit;
                    _pending.RemoveRange(0, excess);
                    _baseSamples += excess;
                    Interlocked.Increment(ref _droppedChunks);
                }
            }

            Interlocked.Add(ref _audioSamples, n);
            _hasWork.Set();
        }

        /// <summary>
        /// 残っている音声をすべて推論してから返る。停止時に呼ぶ。
        /// 戻った時点で ResultReady はすべて発火済み。
        /// </summary>
        public void Flush()
        {
            if (!_running)
            {
                return;
            }

            _stopping = true;
            _hasWork.Set();

            Thread segment = _segmentThread;
            if (segment != null && segment.IsAlive && segment != Thread.CurrentThread)
            {
                segment.Join(60000);
            }

            _segmentThread = null;

            // 区切りが終わったので、推論側に残りを消化させる
            _segmentDone.Set();
            _hasChunk.Set();

            Thread infer = _inferThread;
            if (infer != null && infer.IsAlive && infer != Thread.CurrentThread)
            {
                infer.Join(600000);
            }

            _inferThread = null;
            _running = false;
        }

        /// <summary>
        /// 区切りスレッド。共有バッファからサンプルを取り出し、自分の作業バッファに積む。
        /// 取り出したサンプルを共有バッファに戻さないのが要点（戻すと同一データを
        /// 何度も処理する無限ループになる）。
        /// </summary>
        private void SegmentLoop()
        {
            int silenceLimit = (int)(_options.SilenceSplitSeconds * SampleRate);
            int maxChunk = (int)(_options.MaxChunkSeconds * SampleRate);
            int minChunk = (int)(_options.MinChunkSeconds * SampleRate);

            // 現在処理中の区間。区切りスレッドだけが触る。
            var work = new List<float>();

            while (true)
            {
                List<float> pulled = null;

                lock (_bufferSync)
                {
                    if (_pending.Count > 0)
                    {
                        pulled = _pending;
                        _pending = new List<float>();
                    }
                    else
                    {
                        _hasWork.Reset();
                    }
                }

                if (pulled != null)
                {
                    work.AddRange(pulled);
                    Consume(work, silenceLimit, minChunk, maxChunk);
                    continue;
                }

                if (_stopping)
                {
                    break;
                }

                WaitHandle.WaitAny(new WaitHandle[] { _hasWork.WaitHandle }, 150);
            }

            // 押し出せていない残りを最後の区間として出す
            ConsumeAll(work);
        }

        private static double Rms(List<float> samples, int offset, int count)
        {
            double sum = 0;
            for (int i = 0; i < count; i++)
            {
                double v = samples[offset + i];
                sum += v * v;
            }

            return count > 0 ? Math.Sqrt(sum / count) : 0;
        }

        /// <summary>work の先頭から区間を切り出す。無音と長さで区切り位置を決める。</summary>
        private void Consume(List<float> work, int silenceLimit, int minChunk, int maxChunk)
        {
            int scanned = 0;

            while (scanned < work.Count)
            {
                int step = Math.Min(SampleRate / 100, work.Count - scanned);
                double rms = Rms(work, scanned, step);

                _trailingSilence = rms < _options.SilenceRms ? _trailingSilence + step : 0;
                scanned += step;

                int length = work.Count;

                if (length >= maxChunk)
                {
                    // 無音が来ないまま長くなった。語尾の余白を残して切る。
                    EmitFrom(work, length - (int)(_options.PadSeconds * SampleRate));
                }
                else if (length >= minChunk && _trailingSilence >= silenceLimit)
                {
                    // 無音で切る。末尾の無音の前半だけを残して語尾の欠けを防ぐ。
                    int keep = Math.Min(
                        _trailingSilence - silenceLimit / 2,
                        (int)(_options.PadSeconds * SampleRate));

                    EmitFrom(work, length - _trailingSilence + Math.Max(0, keep));
                }
                else if (length < minChunk && _trailingSilence >= minChunk)
                {
                    // 区間がまだ短いのに無音だけが続いている。
                    // 推論すると幻聴が出るので、捨てて相対時刻だけ進める。
                    work.Clear();
                    _baseSamples += length;
                    _trailingSilence = 0;
                    scanned = 0;
                }
            }
        }

        /// <summary>停止時に、残っているサンプルを 1 区間として推論に回す。</summary>
        private void ConsumeAll(List<float> work)
        {
            if (work.Count == 0)
            {
                return;
            }

            EmitFrom(work, work.Count);
        }

        /// <summary>
        /// work の先頭 cutSamples を区間として切り出し、推論キューに入れる。
        /// 区切りスレッドからのみ呼ぶ。
        /// </summary>
        private void EmitFrom(List<float> work, int cutSamples)
        {
            int consumed = Math.Min(Math.Max(0, cutSamples), work.Count);
            if (consumed <= 0)
            {
                return;
            }

            var chunk = new float[consumed];
            work.CopyTo(0, chunk, 0, consumed);
            work.RemoveRange(0, consumed);

            long offsetSamples = _baseSamples;
            _baseSamples += consumed;
            _trailingSilence = 0;

            if (consumed < (int)(_options.MinProcessSeconds * SampleRate))
            {
                return;      // 短すぎる端数は推論しない
            }

            var item = new Chunk(chunk, TimeSpan.FromSeconds((double)offsetSamples / SampleRate));
            int depth;

            lock (_queueSync)
            {
                if (_chunks.Count >= 8)
                {
                    // 推論が追いついていない。古い区間を捨てて遅延を抑える。
                    _chunks.Dequeue();
                    Interlocked.Increment(ref _droppedChunks);
                }

                _chunks.Enqueue(item);
                depth = _chunks.Count;
            }

            Interlocked.Increment(ref _emittedChunks);
            DiagLog.Write("[whisper] chunk#" + _emittedChunks + " offset="
                + item.Offset.ToString(@"hh\:mm\:ss") + " len="
                + ((double)chunk.Length / SampleRate).ToString("F2") + "s depth=" + depth);

            _hasChunk.Set();
        }

        /// <summary>推論スレッドが 1 区間ずつ処理する。終了は _stopping かつ _segmentDone で判定する。</summary>
        private void InferenceLoop()
        {
            while (true)
            {
                Chunk item = null;

                lock (_queueSync)
                {
                    if (_chunks.Count > 0)
                    {
                        item = _chunks.Dequeue();
                    }
                    else
                    {
                        _hasChunk.Reset();
                    }
                }

                if (item == null)
                {
                    if (_stopping && _segmentDone.IsSet)
                    {
                        break;
                    }

                    _hasChunk.WaitHandle.WaitOne(100);
                    continue;
                }

                Transcribe(item);
            }
        }

        private void Transcribe(Chunk item)
        {
            var sw = Stopwatch.StartNew();
            string text;

            try
            {
                text = _runner.Transcribe(item.Samples);
            }
            catch (Exception ex)
            {
                sw.Stop();
                DiagLog.WriteException("[whisper] inference failed", ex);
                return;
            }

            sw.Stop();
            Interlocked.Increment(ref _inferenceCount);
            Interlocked.Add(ref _inferenceMs, sw.ElapsedMilliseconds);

            double seconds = (double)item.Samples.Length / SampleRate;
            string clean = Normalize(text);

            DiagLog.Write(string.Format(
                "[whisper] audio={0:F2}s infer={1}ms rtf={2:F2} text=\"{3}\"",
                seconds, sw.ElapsedMilliseconds,
                seconds > 0 ? sw.Elapsed.TotalSeconds / seconds : 0,
                clean));

            if (clean.Length == 0)
            {
                return;
            }

            var handler = ResultReady;
            if (handler != null)
            {
                handler(this, new WhisperSegmentResult(
                    clean, item.Offset, TimeSpan.FromSeconds(seconds)));
            }
        }

        /// <summary>Whisper が無音区間や音楽区間に対して出す定型句を取り除く。</summary>
        internal static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            string s = text.Replace("\u200b", string.Empty).Trim();

            // 先頭の [BLANK_AUDIO] (音楽) (拍手) などの注記を落とす
            while (s.Length > 0 && (s[0] == '(' || s[0] == '[' || s[0] == '（'))
            {
                int close = s.IndexOfAny(new[] { ')', ']', '）' });
                if (close < 0)
                {
                    break;
                }

                s = s.Substring(close + 1).Trim();
            }

            if (s.Length == 0 || string.Equals(s, "[BLANK_AUDIO]", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            // 日本語モデルの幻聴でよく出る定型句。完全一致のときだけ落とす。
            string[] boilerplate =
            {
                "ご視聴ありがとうございました",
                "ご視聴ありがとうございます",
                "おやすみなさい",
                "字幕",
                "チャンネル登録お願いします",
                "thanks for watching",
                "thank you for watching",
                "subtitles by",
                "amara.org"
            };

            foreach (string b in boilerplate)
            {
                if (string.Equals(s, b, StringComparison.OrdinalIgnoreCase))
                {
                    return string.Empty;
                }
            }

            return s;
        }

        public void Dispose()
        {
            try
            {
                Flush();
            }
            catch (Exception ex)
            {
                DiagLog.WriteException("[whisper] Flush failed", ex);
            }

            _hasWork.Dispose();
            _hasChunk.Dispose();
            _segmentDone.Dispose();

            if (_runner != null)
            {
                _runner.Dispose();
                _runner = null;
            }
        }

        private sealed class Chunk
        {
            public Chunk(float[] samples, TimeSpan offset)
            {
                Samples = samples;
                Offset = offset;
            }

            public float[] Samples { get; private set; }
            public TimeSpan Offset { get; private set; }
        }
    }

    /// <summary>
    /// WhisperFactory / WhisperProcessor を 1 本のスレッドからだけ触るようにまとめた薄いラッパー。
    /// WhisperProcessor はスレッドセーフではないため、呼び出し側で排他する必要がある。
    /// </summary>
    internal sealed class WhisperRunner : IDisposable
    {
        // ネイティブライブラリの選択はプロセスで 1 回だけ。
        // ここで設定しないと、モデルごとに別のバックエンドを選べてしまう。
        private static bool _runtimeConfigured;
        private static readonly object _runtimeSync = new object();

        private readonly WhisperFactory _factory;
        private readonly bool _ownsFactory;
        private readonly string _language;
        private readonly object _sync = new object();

        public WhisperRunner(string modelPath, int threads, string language, bool preferGpu)
        {
            lock (_runtimeSync)
            {
                if (!_runtimeConfigured)
                {
                    ConfigureRuntime(preferGpu);
                    _runtimeConfigured = true;
                }
            }

            _factory = WhisperFactory.FromPath(modelPath);
            _ownsFactory = true;
            _language = string.IsNullOrEmpty(language) ? "ja" : language;
            Threads = threads;
            RuntimeInfo = WhisperFactory.GetRuntimeInfo();
        }

        /// <summary>
        /// 既に読み込み済みのファクトリを共有する。モデルは 1 回だけロードされ、
        /// 複数の録音系統（スピーカー/マイク）で同じ重みを使い回せる。
        /// このインスタンスはファクトリを破棄しない。
        /// </summary>
        public WhisperRunner(WhisperFactory factory, int threads, string language)
        {
            if (factory == null)
            {
                throw new ArgumentNullException("factory");
            }

            _factory = factory;
            _ownsFactory = false;
            _language = string.IsNullOrEmpty(language) ? "ja" : language;
            Threads = threads;
            RuntimeInfo = WhisperFactory.GetRuntimeInfo();
        }

        /// <summary>
        /// モデルを 1 回だけ読み込むためのファクトリ。
        /// 複数系統で使うときに、これを共有して <see cref="WhisperRunner(WhisperFactory,int,string)"/> に渡す。
        /// </summary>
        public static WhisperFactory CreateFactory(string modelPath, bool preferGpu)
        {
            lock (_runtimeSync)
            {
                if (!_runtimeConfigured)
                {
                    ConfigureRuntime(preferGpu);
                    _runtimeConfigured = true;
                }
            }

            return WhisperFactory.FromPath(modelPath);
        }

        public int Threads { get; private set; }

        public string RuntimeInfo { get; private set; }

        /// <summary>実際に読み込まれたネイティブライブラリ。Vulkan が使えない環境では Cpu になる。</summary>
        public static RuntimeLibrary LoadedLibrary
        {
            get
            {
                var loaded = RuntimeOptions.LoadedLibrary;
                return loaded.HasValue ? loaded.Value : RuntimeLibrary.Cpu;
            }
        }

        /// <summary>
        /// 使うネイティブライブラリの優先順位を決める。
        /// Vulkan があれば GPU で動き、無ければ CPU へ自動で落ちる。
        /// 順序に Cpu を必ず残しておくのがフォールバックの条件。
        /// </summary>
        public static void ConfigureRuntime(bool preferGpu)
        {
            RuntimeOptions.RuntimeLibraryOrder = preferGpu
                ? new List<RuntimeLibrary> { RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx }
                : new List<RuntimeLibrary> { RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx };
        }

        /// <summary>16kHz モノラルの float サンプルを文字起こしする（同期・ブロッキング）。</summary>
        public string Transcribe(float[] samples)
        {
            lock (_sync)
            {
                var sb = new System.Text.StringBuilder();

                var builder = _factory.CreateBuilder()
                    .WithLanguage(_language)
                    .WithThreads(Threads)
                    .WithNoContext()                     // 区間ごとに独立させる（前区間の文脈を持ち越さない）
                    .WithMaxSegmentLength(0)
                    .WithSegmentEventHandler(segment =>
                    {
                        if (!string.IsNullOrEmpty(segment.Text))
                        {
                            if (sb.Length > 0)
                            {
                                sb.Append(' ');
                            }

                            sb.Append(segment.Text.Trim());
                        }
                    });

                using (var processor = builder.Build())
                {
                    processor.Process(samples);
                }

                return sb.ToString();
            }
        }

        public void Dispose()
        {
            if (_ownsFactory)
            {
                _factory.Dispose();
            }
        }
    }
}
