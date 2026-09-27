using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace WinRealtimeWhisper
{
    /// <summary>
    /// 録音 → Whisper 認識 → WAV 保存 をまとめて面倒みる。
    /// UI にはイベントだけを流し、UI スレッドのブロックを避ける。
    ///
    /// 認識はローカル（whisper.cpp の CPU 実行）で行うため、
    /// ネットワークも API キーも不要。
    /// </summary>
    internal sealed class TranscriptionEngine : IDisposable
    {
        private const int WhisperSampleRate = 16000;
        private const int WavSampleRate = 44100;
        private const int WavChannels = 2;

        private readonly object _sync = new object();

        private WasapiLoopbackCapture _loopback;
        private WasapiCapture _microphone;
        private WasapiCapture _mmDeviceCapture;
        private MMDevice _device;
        private WaveFileWriter _wavWriter;
        private SampleConverter _converter;
        private WhisperRecognizer _recognizer;
        private Task _startTask;
        private Task _stopTask;
        private AudioFileReader _fileInput;
        private volatile bool _isRecording;

        // セッションが生きているか（StopCoreAsync をまだ通していないか）。
        // ファイル入力は読み終わりで _isRecording が false になるため、
        // 停止処理の要否はこのフラグで判断する。
        private volatile bool _runActive;
        private volatile bool _disposed;
        private int _wavBytes;
        private string _wavPath;

        private long _audioBytes;
        private long _noiseBytes;
        private DateTime _lastSpeechUtc = DateTime.MinValue;
        private DateTime _lastAudioUtc = DateTime.MinValue;

        // 音声レベルをログに出すための集計（2 秒ごと）
        private long _logCallbacks;
        private long _logBytes;
        private double _logPeak;
        private double _logSumSquares;
        private long _logSampleCount;
        private DateTime _logNextUtc = DateTime.UtcNow.AddSeconds(2);

        public event EventHandler<FinalTextEventArgs> FinalText;
        public event EventHandler<StatusEventArgs> Status;

        /// <summary>録音デバイスが開いて録音が始まった直後に発火する。
        /// UI はこれを受けてボタンの活性状態を更新する（モデル読み込みが
        /// 終わるまで StartAsync は戻らないため、開始ボタンを押した直後の
        /// 状態更新だけでは追いつかない）。</summary>
        public event EventHandler Started;

        public event EventHandler<LevelEventArgs> Level;
        public event EventHandler<SpeechActivityEventArgs> SpeechActivity;
        public event EventHandler<ErrorEventArgs> Failed;

        public bool IsRecording
        {
            get { return _isRecording; }
        }

        public string CurrentWavPath
        {
            get { return _wavPath; }
        }

        public int WavBytes
        {
            get { return Volatile.Read(ref _wavBytes); }
        }

        /// <summary>推論が実時間に追いついているかの目安（実時間 / 音声長）。1 未満なら追いついている。</summary>
        public double RealTimeFactor
        {
            get
            {
                var r = _recognizer;
                if (r == null)
                {
                    return 0;
                }

                long audio = Volatile.Read(ref _audioBytes);
                double seconds = (double)audio / (WhisperSampleRate * 4);
                return seconds > 0 ? (r.TotalInferenceMs / 1000.0) / seconds : 0;
            }
        }

        public int DroppedChunks
        {
            get
            {
                var r = _recognizer;
                return r != null ? r.DroppedChunks : 0;
            }
        }

        public TimeSpan WavDuration
        {
            get
            {
                int bytes = WavBytes;
                if (bytes <= 0)
                {
                    return TimeSpan.Zero;
                }

                return TimeSpan.FromSeconds((double)bytes / (WavSampleRate * WavChannels * 2));
            }
        }

        public void StartAsync(AppSettings settings, string wavPath)
        {
            StartAsync(settings, wavPath, null);
        }

        /// <summary>
        /// 録音を開始する。<paramref name="inputFile"/> を指定した場合は録音デバイスを開かず、
        /// そのファイルを読み込んで文字起こしする（WAV への再保存はしない）。
        /// </summary>
        public void StartAsync(AppSettings settings, string wavPath, string inputFile)
        {
            lock (_sync)
            {
                if (_isRecording || (_startTask != null && !_startTask.IsCompleted))
                {
                    return;
                }

                _startTask = StartCoreAsync(settings, wavPath, inputFile);
            }
        }

        public Task StopAsync()
        {
            lock (_sync)
            {
                if (!_runActive)
                {
                    return _startTask ?? Task.FromResult(0);
                }

                if (_stopTask == null || _stopTask.IsCompleted)
                {
                    _stopTask = StopCoreAsync();
                }

                return _stopTask;
            }
        }

        /// <summary>
        /// ユーザー設定値と遅延プロファイルを突き合わせて実効値を決める。
        /// 設定値が 0 以下ならプロファイル既定値を使う。設定値があれば範囲内でそれを優先する。
        /// </summary>
        private static double ApplyProfile(
            double configured, int profile,
            double fast, double balanced, double precise,
            double fallback)
        {
            double baseline;
            switch (profile)
            {
                case 0: baseline = fast; break;
                case 2: baseline = precise; break;
                case 1: baseline = balanced; break;
                default: baseline = fallback; break;
            }

            return configured > 0 ? configured : baseline;
        }

        private async Task StartCoreAsync(AppSettings settings, string wavPath, string inputFile)
        {
            try
            {
                RaiseStatus(Loc.T("status.starting"));

                DiagLog.Start();
                DiagLog.Write("--- StartCoreAsync (Whisper) ---");
                DiagLog.Write("Source      : " + settings.SourceKind);
                DiagLog.Write("Output      : " + (string.IsNullOrEmpty(settings.OutputDeviceId) ? "(既定)" : settings.OutputDeviceId));
                DiagLog.Write("Input       : " + (string.IsNullOrEmpty(settings.InputDeviceId) ? "(既定)" : settings.InputDeviceId));
                DiagLog.Write("Language    : " + settings.WhisperLanguage);
                DiagLog.Write("ModelPath   : " + settings.ModelPath);
                DiagLog.Write("WavPath     : " + wavPath);
                DiagLog.Write("InputFile   : " + (inputFile ?? "(録音デバイス)"));

                string modelPath = WhisperModelStore.Resolve(settings.ModelPath);
                if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
                {
                    throw new FileNotFoundException(Loc.T("cli.modelMissing", modelPath ?? AppSettings.ModelDirectory));
                }

                _converter = new SampleConverter();

                var defaults = new WhisperOptions();
                var options = new WhisperOptions
                {
                    ModelPath = modelPath,
                    Language = string.IsNullOrEmpty(settings.WhisperLanguage) ? "ja" : settings.WhisperLanguage,
                    Threads = 0,
                    MaxChunkSeconds = ApplyProfile(
                        settings.MaxChunkSeconds, settings.LatencyProfile, 6.0, 8.0, 5.0, 25.0),
                    SilenceSplitSeconds = ApplyProfile(
                        settings.SilenceSplitSeconds, settings.LatencyProfile, 0.45, 0.8, 0.35, 3.0),
                    MinChunkSeconds = ApplyProfile(
                        defaults.MinChunkSeconds, settings.LatencyProfile, 2.0, 3.0, 8.0, defaults.MinChunkSeconds)
                };

                _recognizer = new WhisperRecognizer(options);
                _recognizer.ResultReady += OnSegmentReady;

                // モデル読み込みは重いので UI を止めないよう、別スレッドで待つ
                RaiseStatus(Loc.T("status.loadingModel"));
                DiagLog.Write("[whisper] loading model on background thread...");
                await Task.Run(() => _recognizer.Initialize()).ConfigureAwait(false);

                DiagLog.Write("[whisper] threads=" + _recognizer.EffectiveThreads);

                _wavPath = wavPath;

                if (inputFile != null)
                {
                    // ファイル入力は録音デバイスを開かない。再生位置に合わせて読み進める。
                    _fileInput = new AudioFileReader(inputFile);
                    DiagLog.Write("File input opened: " + _fileInput.WaveFormat);
                }
                else
                {
                    _wavWriter = new WaveFileWriter(wavPath, new WaveFormat(WavSampleRate, 16, WavChannels));

                    OpenCaptureDevices(settings);

                    if (_loopback == null && _microphone == null && _mmDeviceCapture == null)
                    {
                        throw new InvalidOperationException(Loc.T("cli.devicesNone"));
                    }

                    DiagLog.Write("Capture opened: loopback=" + (_loopback != null)
                        + " mic=" + (_microphone != null)
                        + " mmDevice=" + (_mmDeviceCapture != null));
                }

                _recognizer.Start();

                _audioBytes = 0;
                _noiseBytes = 0;
                _lastAudioUtc = DateTime.UtcNow;
                _lastSpeechUtc = DateTime.MinValue;

                _isRecording = true;
                _runActive = true;

                if (_fileInput != null)
                {
                    StartFilePump();
                }

                if (_loopback != null)
                {
                    _loopback.StartRecording();
                }

                if (_microphone != null)
                {
                    _microphone.StartRecording();
                }

                DiagLog.Write("Recording started.");
                RaiseStatus(inputFile != null
                    ? Loc.T("status.recognizingFile")
                    : Loc.T("status.recording"));
                RaiseSpeech(false);
                RaiseStarted();
            }
            catch (Exception ex)
            {
                _isRecording = false;
                _runActive = false;
                DiagLog.WriteException("StartCoreAsync failed", ex);
                CleanupAfterStop();
                RaiseError(Loc.T("cli.engineError", Describe(ex)), ex);
            }
        }

        /// <summary>例外から HRESULT と内部例外まで含めた 1 行説明を作る。</summary>
        internal static string Describe(Exception ex)
        {
            return DiagLog.Describe(ex);
        }

        private void OpenCaptureDevices(AppSettings settings)
        {
            switch (settings.SourceKind)
            {
                case AudioSourceKind.Both:
                    _loopback = CreateLoopback(settings.OutputDeviceId);
                    try
                    {
                        _microphone = CreateMicrophone(settings.InputDeviceId);
                    }
                    catch (Exception ex)
                    {
                        _microphone = null;
                        RaiseStatus(Loc.T("status.micOpenFailed", ex.Message));
                    }
                    break;

                case AudioSourceKind.Microphone:
                    _mmDeviceCapture = CreateDeviceCapture(settings.InputDeviceId);
                    break;

                default:
                    _loopback = CreateLoopback(settings.OutputDeviceId);
                    break;
            }
        }

        /// <summary>
        /// ファイル入力を実時間で読み進める。録音デバイスから音が来る代わりに、
        /// 20ms ごとに相当量を読んで同じ経路（OnDataAvailable 相当）へ流す。
        /// </summary>
        private void StartFilePump()
        {
            var reader = _fileInput;
            if (reader == null)
            {
                return;
            }

            var thread = new System.Threading.Thread(() =>
            {
                var provider = reader;
                var format = provider.WaveFormat;
                int step = Math.Max(160, format.SampleRate * format.Channels / 50); // 20ms 分
                var pending = new float[step];

                try
                {
                    while (_isRecording && !_disposed)
                    {
                        int read = provider.Read(pending, 0, pending.Length);
                        if (read <= 0)
                        {
                            DiagLog.Write("[file] end of input");
                            RaiseStatus(Loc.T("status.fileEnded"));
                            _isRecording = false;
                            break;
                        }

                        // 録音デバイスのコールバックと同じ経路に流す。
                        // WAV への再保存はしないが、レベルと発話判定は同じものを使う。

                        Interlocked.Add(ref _audioBytes, read * 4);
                        _lastAudioUtc = DateTime.UtcNow;

                        var samples = new float[read];
                        Array.Copy(pending, samples, read);

                        TrackAudioForLog(samples, format);
                        PushAudio(samples, format.SampleRate, format.Channels);
                        RaiseLevel(CalculateLevel(samples));
                        RaiseSpeech(IsSpeech(samples, format.SampleRate, format.Channels));

                        System.Threading.Thread.Sleep(20);
                    }
                }
                catch (Exception ex)
                {
                    if (_isRecording && !_disposed)
                    {
                        RaiseError(Loc.T("cli.fileReadFailed", Describe(ex)), ex);
                    }
                }
            });

            thread.IsBackground = true;
            thread.Name = "WinRealtimeWhisper.FilePump";
            thread.Start();
        }

        private WasapiCapture CreateMicrophone(string deviceId)
        {
            return CreateDeviceCapture(deviceId);
        }

        private WasapiLoopbackCapture CreateLoopback(string deviceId)
        {
            WasapiLoopbackCapture capture;

            if (!string.IsNullOrEmpty(deviceId))
            {
                try
                {
                    _device = new MMDeviceEnumerator().GetDevice(deviceId);
                    capture = new WasapiLoopbackCapture(_device);
                }
                catch (Exception)
                {
                    _device = null;
                    capture = new WasapiLoopbackCapture();
                }
            }
            else
            {
                capture = new WasapiLoopbackCapture();
            }

            // ループバックは Shared モードで開く
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            return capture;
        }

        private WasapiCapture CreateDeviceCapture(string deviceId)
        {
            WasapiCapture capture;

            if (!string.IsNullOrEmpty(deviceId))
            {
                try
                {
                    _device = new MMDeviceEnumerator().GetDevice(deviceId);
                    capture = new WasapiCapture(_device);
                }
                catch (Exception)
                {
                    _device = null;
                    capture = new WasapiCapture();
                }
            }
            else
            {
                capture = new WasapiCapture();
            }

            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            return capture;
        }

        private void OnDataAvailable(object sender, WaveInEventArgs e)
        {
            if (!_isRecording || e.BytesRecorded <= 0)
            {
                return;
            }

            try
            {
                Interlocked.Add(ref _audioBytes, e.BytesRecorded);
                _lastAudioUtc = DateTime.UtcNow;

                var capture = sender as IWaveIn;
                WaveFormat format = capture != null ? capture.WaveFormat : null;
                if (format == null || format.Channels <= 0 || format.SampleRate <= 0)
                {
                    return;
                }

                float[] samples = ToFloat(e.Buffer, e.BytesRecorded, format);
                if (samples.Length == 0)
                {
                    return;
                }

                TrackAudioForLog(samples, format);

                PushAudio(samples, format.SampleRate, format.Channels);

                WriteWav(samples, format);

                RaiseLevel(CalculateLevel(samples));
                RaiseSpeech(IsSpeech(samples, format.SampleRate, format.Channels));
            }
            catch (Exception ex)
            {
                RaiseError(Loc.T("status.audioError", Describe(ex)), ex);
            }
        }

        /// <summary>
        /// 取り込んだ音を 16kHz モノラルへ変換して認識器へ積む。
        /// 録音デバイスのコールバックとファイル入力のポンプで共通に使う。
        /// </summary>
        private void PushAudio(float[] samples, int sampleRate, int channels)
        {
            var recognizer = _recognizer;
            if (recognizer == null || samples.Length == 0)
            {
                return;
            }

            float[] mono = _converter.Convert(samples, samples.Length, sampleRate, channels);
            if (mono.Length > 0)
            {
                recognizer.Enqueue(mono, mono.Length);
            }
        }

        /// <summary>
        /// 「録音デバイスから実際に音が届いているか」を 2 秒ごとに記録する。
        /// 認識結果が空のときの切り分けに使う。
        /// </summary>
        private void TrackAudioForLog(float[] samples, WaveFormat format)
        {
            double peak = 0;
            double sumSquares = 0;

            for (int i = 0; i < samples.Length; i++)
            {
                double v = samples[i];
                double a = v < 0 ? -v : v;
                if (a > peak) peak = a;
                sumSquares += v * v;
            }

            Interlocked.Increment(ref _logCallbacks);
            Interlocked.Add(ref _logBytes, samples.Length);
            Interlocked.Add(ref _logSampleCount, samples.Length);

            // double の加算競合は許容する（ログ用の目安値のため）
            if (peak > _logPeak) _logPeak = peak;
            _logSumSquares += sumSquares;

            if (DateTime.UtcNow < _logNextUtc)
            {
                return;
            }

            _logNextUtc = DateTime.UtcNow.AddSeconds(2);

            long callbacks = Interlocked.Exchange(ref _logCallbacks, 0);
            long bytes = Interlocked.Exchange(ref _logBytes, 0);
            long sampleCount = Interlocked.Exchange(ref _logSampleCount, 0);
            double peakValue = _logPeak;
            double sumSq = _logSumSquares;
            _logPeak = 0;
            _logSumSquares = 0;

            if (callbacks == 0)
            {
                DiagLog.Write("[audio] 音声コールバックなし（2秒間）。デバイスが停止している可能性あり。");
                return;
            }

            double rms = sampleCount > 0 ? Math.Sqrt(sumSq / sampleCount) : 0;
            double peakDb = peakValue > 0 ? 20 * Math.Log10(peakValue) : -96;
            double rmsDb = rms > 0 ? 20 * Math.Log10(rms) : -96;

            string extra = string.Empty;
            var r = _recognizer;
            if (r != null)
            {
                extra = string.Format(" rtf={0:F2} chunks={1} dropped={2}",
                    RealTimeFactor, r.InferenceCount, r.DroppedChunks);
            }

            DiagLog.Write(string.Format(
                "[audio] cb={0} bytes={1} fmt={2}Hz/{3}ch/{4}bit peak={5:F4}({6:F1}dB) rms={7:F4}({8:F1}dB){9}",
                callbacks, bytes, format.SampleRate, format.Channels, format.BitsPerSample,
                peakValue, peakDb, rms, rmsDb, extra));
        }

        private static float[] ToFloat(byte[] buffer, int count, WaveFormat format)
        {
            if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            {
                int n = count / 4;
                var result = new float[n];
                for (int i = 0; i < n; i++)
                {
                    result[i] = BitConverter.ToSingle(buffer, i * 4);
                }
                return result;
            }

            var provider = new RawSourceWaveStream(new MemoryStream(buffer, 0, count, false), format);
            var sampleProvider = provider.ToSampleProvider();
            var tmp = new float[Math.Max(1, count)];
            var list = new List<float>(count);
            int read;
            while ((read = sampleProvider.Read(tmp, 0, tmp.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    list.Add(tmp[i]);
                }
            }
            return list.ToArray();
        }

        private void WriteWav(float[] samples, WaveFormat sourceFormat)
        {
            var writer = _wavWriter;
            if (writer == null || samples.Length == 0)
            {
                return;
            }

            ISampleProvider provider = new RawSampleProvider(samples, sourceFormat.SampleRate, sourceFormat.Channels);

            if (sourceFormat.Channels != WavChannels)
            {
                provider = new MonoToStereoSampleProvider(provider);
            }

            if (sourceFormat.SampleRate != WavSampleRate)
            {
                provider = new WdlResamplingSampleProvider(provider, WavSampleRate);
            }

            var toBytes = provider.ToWaveProvider();

            int bytes = 0;
            var buffer = new byte[32768];
            int read;
            while ((read = toBytes.Read(buffer, 0, buffer.Length)) > 0)
            {
                writer.Write(buffer, 0, read);
                bytes += read;
            }

            Interlocked.Add(ref _wavBytes, bytes);
        }

        private static float CalculateLevel(float[] samples)
        {
            double sum = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                double v = samples[i];
                sum += v * v;
            }

            double rms = Math.Sqrt(sum / Math.Max(1, samples.Length));
            double normalized = rms / 0.25;                 // 0.25 rms をフルスケールの目安にする
            if (normalized > 1.0) normalized = 1.0;
            return (float)normalized;
        }

        private bool IsSpeech(float[] samples, int sampleRate, int sourceChannels)
        {
            if (samples.Length == 0)
            {
                return false;
            }
            double sum = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                sum += samples[i] * samples[i];
            }

            double rms = Math.Sqrt(sum / Math.Max(1, samples.Length));
            double seconds = (double)samples.Length / (sampleRate * Math.Max(1, sourceChannels));
            long pcmBytes = (long)(seconds * 32000);

            if (rms < 0.0025)
            {
                Interlocked.Add(ref _noiseBytes, pcmBytes);
                return _lastSpeechUtc != DateTime.MinValue &&
                       (DateTime.UtcNow - _lastSpeechUtc).TotalMilliseconds < 1200;
            }

            _lastSpeechUtc = DateTime.UtcNow;
            return true;
        }

        private void RaiseStatus(string text)
        {
            var handler = Status;
            if (handler != null)
            {
                handler(this, new StatusEventArgs(text));
            }
        }

        private void RaiseStarted()
        {
            var handler = Started;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void RaiseLevel(float level)
        {
            var handler = Level;
            if (handler != null)
            {
                handler(this, new LevelEventArgs(level));
            }
        }

        private void RaiseSpeech(bool speaking)
        {
            var handler = SpeechActivity;
            if (handler != null)
            {
                handler(this, new SpeechActivityEventArgs(speaking));
            }
        }

        private void RaiseError(string message, Exception ex)
        {
            DiagLog.Write("[ERROR] " + message);
            if (ex != null)
            {
                DiagLog.WriteException("  caused by", ex);
            }

            var handler = Failed;
            if (handler != null)
            {
                handler(this, new ErrorEventArgs(message, ex));
            }
        }

        /// <summary>Whisper の推論スレッドから呼ばれる。確定テキストとして UI へ流す。</summary>
        private void OnSegmentReady(object sender, WhisperSegmentResult e)
        {
            if (string.IsNullOrEmpty(e.Text))
            {
                return;
            }

            var handler = FinalText;
            if (handler != null)
            {
                handler(this, new FinalTextEventArgs(e.Text, e.Offset.Ticks));
            }
        }

        private async Task StopCoreAsync()
        {
            _isRecording = false;
            _runActive = false;
            RaiseStatus(Loc.T("status.stopping"));

            // 先に読み込み元を閉じて、ファイルポンプを止める
            try
            {
                if (_fileInput != null)
                {
                    _fileInput.Dispose();
                    _fileInput = null;
                }
            }
            catch (Exception)
            {
            }

            try
            {
                if (_loopback != null)
                {
                    _loopback.StopRecording();
                }

                if (_microphone != null)
                {
                    _microphone.StopRecording();
                }

                if (_mmDeviceCapture != null)
                {
                    _mmDeviceCapture.StopRecording();
                }
            }
            catch (Exception)
            {
            }

            // 未処理の音声を全部推論してから終わる。
            // ここを待たないと、停止直前の発話が落ちる。
            var recognizer = _recognizer;
            if (recognizer != null)
            {
                RaiseStatus(Loc.T("status.flushing"));
                DiagLog.Write("[whisper] flushing remaining audio...");

                try
                {
                    await Task.Run(() => recognizer.Flush()).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    RaiseError(Loc.T("cli.stopFailed", Describe(ex)), ex);
                }

                DiagLog.Write(string.Format(
                    "[whisper] stopped: chunks={0} totalInfer={1}ms dropped={2}",
                    recognizer.InferenceCount, recognizer.TotalInferenceMs, recognizer.DroppedChunks));
            }

            string wavPath = _wavPath;
            bool wroteWav = false;

            try
            {
                if (_wavWriter != null)
                {
                    _wavWriter.Flush();
                    _wavWriter.Dispose();
                    _wavWriter = null;
                    wroteWav = !string.IsNullOrEmpty(wavPath);
                }
            }
            catch (Exception)
            {
            }

            CleanupAfterStop();

            RaiseSpeech(false);
            RaiseLevel(0f);

            if (wroteWav)
            {
                RaiseStatus(Loc.T("status.stoppedWithWav", wavPath));
            }
            else
            {
                RaiseStatus(Loc.T("status.stopped"));
            }
        }

        private void CleanupAfterStop()
        {
            try
            {
                if (_loopback != null)
                {
                    _loopback.DataAvailable -= OnDataAvailable;
                    _loopback.RecordingStopped -= OnRecordingStopped;
                    _loopback.Dispose();
                    _loopback = null;
                }

                if (_microphone != null)
                {
                    _microphone.DataAvailable -= OnDataAvailable;
                    _microphone.RecordingStopped -= OnRecordingStopped;
                    _microphone.Dispose();
                    _microphone = null;
                }

                if (_mmDeviceCapture != null)
                {
                    _mmDeviceCapture.DataAvailable -= OnDataAvailable;
                    _mmDeviceCapture.RecordingStopped -= OnRecordingStopped;
                    _mmDeviceCapture.Dispose();
                    _mmDeviceCapture = null;
                }

                if (_device != null)
                {
                    _device.Dispose();
                    _device = null;
                }
            }
            catch (Exception)
            {
            }

            try
            {
                if (_wavWriter != null)
                {
                    _wavWriter.Flush();
                    _wavWriter.Dispose();
                    _wavWriter = null;
                }
            }
            catch (Exception)
            {
            }

            try
            {
                if (_fileInput != null)
                {
                    _fileInput.Dispose();
                    _fileInput = null;
                }
            }
            catch (Exception)
            {
            }

            if (_recognizer != null)
            {
                try
                {
                    _recognizer.ResultReady -= OnSegmentReady;
                }
                catch (Exception)
                {
                }

                _recognizer.Dispose();
                _recognizer = null;
            }
        }

        private void OnRecordingStopped(object sender, StoppedEventArgs e)
        {
            if (e.Exception != null && _isRecording)
            {
                RaiseError(Loc.T("cli.engineError", Describe(e.Exception)), e.Exception);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _isRecording = false;
            _runActive = false;

            try
            {
                StopAsync().Wait(3000);
            }
            catch (Exception)
            {
            }

            CleanupAfterStop();
        }
    }

    /// <summary>メモリ上の float サンプル列を ISampleProvider として読ませるための薄いラッパー。</summary>
    internal sealed class RawSampleProvider : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public RawSampleProvider(float[] samples, int sampleRate, int channels)
        {
            _samples = samples;
            WaveFormat = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }

        public WaveFormat WaveFormat { get; private set; }

        public int Read(float[] buffer, int offset, int count)
        {
            int available = _samples.Length - _position;
            if (available <= 0)
            {
                return 0;
            }

            int n = Math.Min(available, count);
            Array.Copy(_samples, _position, buffer, offset, n);
            _position += n;
            return n;
        }
    }
}
