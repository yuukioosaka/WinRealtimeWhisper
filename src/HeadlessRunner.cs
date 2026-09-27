using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WinRealtimeWhisper
{
    /// <summary>
    /// GUI を出さずに録音と文字起こしを行う。進行状況は標準出力、結果は
    /// --text で指定されたファイル（省略時は履歴フォルダ）へ書き出す。
    /// </summary>
    internal static class HeadlessRunner
    {
        public static int Run(CommandLineOptions options)
        {
            DiagLog.Start();

            CommandLineOptions.PrepareConsole();

            var settings = options.BuildSettings();
            if (!ApplyModelDirectory(settings, options))
            {
                return 1;
            }

            if (options.ListDevicesRequested)
            {
                ListDevices();
                return 0;
            }

            string modelPath = options.ResolveModelPath(settings);
            if (modelPath == null)
            {
                Console.Error.WriteLine(Loc.T("cli.modelMissing", settings.ModelPath));
                Console.Error.WriteLine(Loc.T("cli.modelMissingHint"));
                Console.Error.WriteLine(Loc.T("cli.modelSearchPath", AppSettings.ModelDirectory));
                return 1;
            }

            var session = new TranscriptionSession();
            string textPath = options.ResolveTextPath(session.StartedAt);

            string wavPath = null;
            if (options.InputFile == null)
            {
                wavPath = options.ResolveWavPath(session.StartedAt);
            }

            return Execute(options, settings, modelPath, session, textPath, wavPath);
        }

        private static int Execute(
            CommandLineOptions options,
            AppSettings settings,
            string modelPath,
            TranscriptionSession session,
            string textPath,
            string wavPath)
        {
            var stop = new ManualResetEventSlim(false);
            bool failed = false;
            string failure = null;
            bool started = false;

            using (var engine = new TranscriptionEngine())
            {
                engine.Started += (s, e) =>
                {
                    started = true;
                    Info(wavPath != null ? Loc.T("cli.recordingStartedWav", wavPath) : Loc.T("cli.recordingStarted"));
                };

                engine.Status += (s, e) => Info(e.Text);

                engine.FinalText += (s, e) =>
                {
                    var line = session.AppendFinal(e.Text, TimeSpan.FromTicks(e.OffsetTicks));
                    if (line != null)
                    {
                        Console.WriteLine(line.Text);
                        Flush();
                    }
                };

                engine.Failed += (s, e) =>
                {
                    failed = true;
                    failure = e.Message;
                    Console.Error.WriteLine(Loc.T("cli.engineError", e.Message));
                    if (e.Exception != null)
                    {
                        Console.Error.WriteLine("  " + DiagLog.Describe(e.Exception));
                    }

                    stop.Set();
                };

                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    Info(Loc.T("cli.interrupting"));
                    stop.Set();
                };

                TranscriptFileWriter writer = null;

                try
                {
                    if (options.InputFile != null)
                    {
                        Info(Loc.T("cli.audioFile", options.InputFile));
                    }
                    else
                    {
                        Info(Loc.T("cli.source", Describe(settings.SourceKind)));
                    }

                    Info(Loc.T("cli.model", modelPath));

                    writer = new TranscriptFileWriter(textPath, session);
                    writer.Flush();

                    if (options.InputFile != null)
                    {
                        engine.StartAsync(settings, wavPath, options.InputFile);
                    }
                    else
                    {
                        engine.StartAsync(settings, wavPath);
                    }

                    // 録音デバイスが開くのを待つ。開かなければ失敗として扱う。
                    var waitStart = DateTime.UtcNow;
                    while (!started && !failed && (DateTime.UtcNow - waitStart).TotalSeconds < 120)
                    {
                        Thread.Sleep(100);
                    }

                    // 録音が始まる前にファイルを読み終えていたら、自分で停止を促す。
                    // （ファイル入力は短いと一瞬で終わるため、ここで見ておかないと
                    //   指定時間まで待ってしまう）
                    var fileWait = DateTime.UtcNow;
                    while (options.InputFile != null
                        && !failed
                        && (DateTime.UtcNow - fileWait).TotalSeconds < 600)
                    {
                        if (!engine.IsRecording)
                        {
                            stop.Set();
                            break;
                        }

                        if (options.Seconds > 0
                            && (DateTime.UtcNow - waitStart).TotalSeconds >= options.Seconds)
                        {
                            stop.Set();
                            break;
                        }

                        Thread.Sleep(100);
                    }

                    if (options.InputFile == null)
                    {
                        if (options.Seconds > 0)
                        {
                            stop.Wait(TimeSpan.FromSeconds(options.Seconds) + TimeSpan.FromSeconds(5));
                        }
                        else
                        {
                            Info(Loc.T("cli.pressCtrlC"));
                            stop.Wait();
                        }
                    }
                    else if (!stop.IsSet)
                    {
                        Info(Loc.T("cli.pressCtrlC"));
                        stop.Wait();
                    }
                }
                finally
                {
                    try
                    {
                        engine.StopAsync().Wait(TimeSpan.FromSeconds(180));
                    }
                    catch (AggregateException ex)
                    {
                        Console.Error.WriteLine(Loc.T("cli.stopFailed", DiagLog.Describe(ex.GetBaseException())));
                        failed = true;
                        failure = failure ?? Loc.T("cli.stopFailedShort");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(Loc.T("cli.stopFailed", DiagLog.Describe(ex)));
                        failed = true;
                        failure = failure ?? Loc.T("cli.stopFailedShort");
                    }

                    if (writer != null)
                    {
                        writer.Flush();
                        writer.Dispose();
                    }
                }
            }

            Report(session, textPath, wavPath, options);

            if (failed)
            {
                Console.Error.WriteLine(failure);
                return 1;
            }

            return 0;
        }

        private static void Report(
            TranscriptionSession session,
            string textPath,
            string wavPath,
            CommandLineOptions options)
        {
            Console.WriteLine();
            Console.WriteLine(Loc.T("cli.resultHeader"));
            Console.WriteLine(Loc.T("cli.resultLines", session.Lines.Count));
            Console.WriteLine(Loc.T("cli.resultText", textPath));

            if (wavPath != null && File.Exists(wavPath))
            {
                long bytes = new FileInfo(wavPath).Length;
                Console.WriteLine(Loc.T("cli.resultWav", wavPath, bytes / 1024));
            }
        }

        /// <summary>--model-dir が指定されたら、モデルの置き場を差し替える。</summary>
        private static bool ApplyModelDirectory(AppSettings settings, CommandLineOptions options)
        {
            if (string.IsNullOrEmpty(options.ModelDirectory))
            {
                return true;
            }

            string dir;
            try
            {
                dir = Path.GetFullPath(options.ModelDirectory);
                System.IO.Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(Loc.T("cli.modelDirFailed", ex.Message));
                return false;
            }

            AppSettings.ModelDirectoryOverride = dir;

            // 設定に入っているパスは旧フォルダを指しているので、指定フォルダで解決し直す
            string name = Path.GetFileName(settings.ModelPath);
            settings.ModelPath = string.IsNullOrEmpty(name)
                ? WhisperModelStore.DefaultModelFileName
                : name;

            return true;
        }

        private static void ListDevices()
        {
            Console.WriteLine(Loc.T("cli.devicesOutput"));

            using (var enumerator = new MMDeviceEnumerator())
            {
                WriteDevices(enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active));

                Console.WriteLine();
                Console.WriteLine(Loc.T("cli.devicesInput"));
                WriteDevices(enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active));
            }

            Console.WriteLine();
            Console.WriteLine(Loc.T("cli.devicesHint"));
        }

        private static void WriteDevices(IEnumerable<MMDevice> devices)
        {
            int index = 0;
            foreach (var device in devices)
            {
                using (device)
                {
                    index++;
                    Console.WriteLine("  " + index.ToString(CultureInfo.InvariantCulture) + ". "
                        + device.FriendlyName);
                    Console.WriteLine("     id: " + device.ID);
                }
            }

            if (index == 0)
            {
                Console.WriteLine(Loc.T("cli.devicesNone"));
            }
        }

        private static string Describe(AudioSourceKind kind)
        {
            switch (kind)
            {
                case AudioSourceKind.Microphone:
                    return Loc.T("source.mic");
                case AudioSourceKind.SystemLoopback:
                    return Loc.T("source.speakers");
                default:
                    return Loc.T("source.both");
            }
        }

        private static void Info(string text)
        {
            Console.WriteLine("# " + text);
            Flush();
        }

        private static void Flush()
        {
            try
            {
                Console.Out.Flush();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 録音中もテキストを書き出しておき、強制終了されても内容が残るようにする。
        /// 出力先は BOM 付き UTF-8（メモ帳でそのまま開ける）。
        /// </summary>
        private sealed class TranscriptFileWriter : IDisposable
        {
            private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

            private readonly string _path;
            private readonly TranscriptionSession _session;
            private DateTime _nextWrite = DateTime.MinValue;

            public TranscriptFileWriter(string path, TranscriptionSession session)
            {
                _path = path;
                _session = session;
            }

            /// <summary>確定行が増えたとき、または一定時間ごとに書き出す。</summary>
            public void Flush()
            {
                DateTime now = DateTime.UtcNow;
                if (now < _nextWrite)
                {
                    return;
                }

                _nextWrite = now + Interval;

                try
                {
                    string dir = Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        System.IO.Directory.CreateDirectory(dir);
                    }

                    File.WriteAllText(_path, _session.ToPlainText(), new UTF8Encoding(true));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(Loc.T("cli.textWriteFailed", ex.Message));
                }
            }

            public void Dispose()
            {
                Flush();
            }
        }
    }
}
