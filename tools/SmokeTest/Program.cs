using System;
using System.IO;
using System.Speech.Synthesis;
using System.Threading;
using NAudio.Wave;
using WinRealtimeWhisper;

namespace WinRealtimeWhisperSmokeTest
{
    /// <summary>
    /// WinRealtimeWhisper の中核（変換 → 区切り → Whisper 推論）を、実際のモデルで確認する。
    /// テスト音声は Windows の音声合成で作るため、外部ファイルを用意しなくてよい。
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            DiagLog.Start();
            string modelPath = args.Length > 0 ? args[0] : FindModel();
            if (string.IsNullOrEmpty(modelPath))
            {
                Console.WriteLine("モデルが見つかりません。引数で ggml モデルのパスを指定してください。");
                return 2;
            }

            Console.WriteLine("model: " + modelPath);

            int exit = 0;
            try
            {
                TestConverter();

                if (args.Length > 1 && args[1] == "dialogs")
                {
                    exit = DialogSmoke.Run();
                }

                else if (args.Length > 1 && args[1] == "shot")
                {
                    exit = Shot.Run();
                }
                else if (args.Length > 1 && args[1] == "shotmain")
                {
                    exit = Shot.RunMain();
                }
                else if (args.Length > 1 && args[1] == "editor")
                {
                    exit = Shot.EditorCheck();
                }
                else if (args.Length > 1 && args[1] == "e2e")
                {
                    exit = IntegrationTest.Run(modelPath);
                }
                else if (args.Length > 1 && args[1] == "vtt")
                {
                    exit = VttSmoke.Run();
                }
                else if (args.Length > 1 && args[1] == "window")
                {
                    exit = WindowSmoke.Run();
                }
                else if (args.Length > 1 && args[1] == "realtime")
                {
                    exit = RealtimeSmoke.Run();
                }
                else
                {
                    TestRecognizer(modelPath);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL: " + ex);
                exit = 1;
            }

            return exit;
        }

        private static string FindModel()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            string local = AppSettings.ModelDirectoryEffective;

            // リポジトリ直下の models/、%LOCALAPPDATA%\WinRealtimeWhisper\models\ の順に探す
            foreach (string root in new[] { local, Path.Combine(RepoRoot(dir), "models") })
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                var files = Directory.GetFiles(root, "ggml-*.bin");
                if (files.Length > 0)
                {
                    Array.Sort(files, (a, b) => new FileInfo(a).Length.CompareTo(new FileInfo(b).Length));
                    return files[0];
                }
            }

            return null;
        }

        private static string RepoRoot(DirectoryInfo dir)
        {
            var current = dir;
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "WinRealtimeWhisper.csproj")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            return dir.FullName;
        }

        /// <summary>48kHz ステレオ → 16kHz モノラルの変換比を確認する。</summary>
        private static void TestConverter()
        {
            var converter = new SampleConverter();

            const int rate = 48000;
            const int channels = 2;
            int frames = rate;                       // 1 秒分
            var input = new float[frames * channels];

            for (int i = 0; i < frames; i++)
            {
                float v = (float)Math.Sin(2 * Math.PI * 440 * i / rate) * 0.5f;
                input[i * 2] = v;
                input[i * 2 + 1] = v;
            }

            float[] mono = converter.Convert(input, input.Length, rate, channels);
            Console.WriteLine("convert: in=" + input.Length + " samples -> out=" + mono.Length);

            if (Math.Abs(mono.Length - 16000) > 20)
            {
                throw new InvalidOperationException("リサンプル結果の長さが想定外です: " + mono.Length);
            }

            double peak = 0;
            foreach (float f in mono)
            {
                double a = Math.Abs(f);
                if (a > peak) peak = a;
            }

            Console.WriteLine("convert: peak=" + peak.ToString("F3"));
            if (peak < 0.3)
            {
                throw new InvalidOperationException("振幅が小さすぎます（無音の可能性）: " + peak);
            }
        }

        /// <summary>音声合成で日本語 WAV を作り、区切り → 推論まで通す。</summary>
        private static void TestRecognizer(string modelPath)
        {
            string speechWav = Path.Combine(Path.GetTempPath(), "winrealtimewhisper_smoke_speech.wav");
            SynthesizeJapanese(speechWav);

            // 実際のループバック相当（48kHz ステレオ float）に変換してから流す
            float[] samples = LoadResampled(speechWav, 48000, 2);
            Console.WriteLine("speech: " + (samples.Length / 2 / 48000.0).ToString("F1") + "s, "
                + samples.Length + " samples (48kHz stereo float)");

            var options = new WhisperOptions
            {
                ModelPath = modelPath,
                Threads = 4,
                MinChunkSeconds = 2.0,
                SilenceSplitSeconds = 0.6,
                MaxChunkSeconds = 8.0
            };

            var recognizer = new WhisperRecognizer(options);
            recognizer.Initialize();
            Console.WriteLine("runtime: " + recognizer.EffectiveThreads + " threads");

            var done = new ManualResetEventSlim(false);
            var results = new System.Collections.Generic.List<WhisperSegmentResult>();

            recognizer.ResultReady += (s, e) =>
            {
                lock (results)
                {
                    results.Add(e);
                }

                Console.WriteLine("  final: " + e);
            };

            recognizer.Start();

            // 20ms ずつマイク入力と同じ粒度で投入する
            var converter = new SampleConverter();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int step = 960 * 2;      // 20ms @48kHz ステレオ

            for (int i = 0; i < samples.Length; i += step)
            {
                int n = Math.Min(step, samples.Length - i);
                var slice = new float[n];
                Array.Copy(samples, i, slice, 0, n);

                float[] mono = converter.Convert(slice, n, 48000, 2);
                if (mono.Length > 0)
                {
                    recognizer.Enqueue(mono, mono.Length);
                }

                Thread.Sleep(20);
            }

            Console.WriteLine("feeding done in " + sw.ElapsedMilliseconds + "ms, flushing...");
            sw.Restart();
            recognizer.Flush();
            Console.WriteLine("flush took " + sw.ElapsedMilliseconds + "ms");

            recognizer.Dispose();
            done.Set();

            Console.WriteLine("chunks=" + recognizer.InferenceCount
                + " inferMs=" + recognizer.TotalInferenceMs
                + " dropped=" + recognizer.DroppedChunks);

            if (results.Count == 0)
            {
                throw new InvalidOperationException("認識結果が 1 件も得られませんでした");
            }

            var sb = new System.Text.StringBuilder();
            foreach (var r in results)
            {
                sb.Append(r.Text);
            }

            string text = sb.ToString();
            Console.WriteLine("combined: " + text);

            if (text.IndexOf("文字起こし", StringComparison.Ordinal) < 0
                && text.IndexOf("テスト", StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException(
                    "期待した語が認識結果に含まれていません（幻聴の可能性）: " + text);
            }

            Console.WriteLine("recognizer OK");
        }

        /// <summary>Windows の音声合成で日本語の WAV を作る。</summary>
        private static void SynthesizeJapanese(string path)
        {
            using (var synth = new SpeechSynthesizer())
            {
                synth.Rate = -1;
                synth.SetOutputToWaveFile(path);
                synth.Speak("これは文字起こしのテストです。今日はいい天気ですね。");
                synth.SetOutputToNull();
            }

            Console.WriteLine("tts wav: " + path + " (" + new FileInfo(path).Length + " bytes)");
        }

        /// <summary>WAV を読み込み、指定形式の float インターリーブに変換する。</summary>
        private static float[] LoadResampled(string path, int rate, int channels)
        {
            using (var reader = new AudioFileReader(path))
            {
                ISampleProvider provider = reader.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat
                    ? reader
                    : reader.ToSampleProvider();

                if (reader.WaveFormat.Channels == 1 && channels == 2)
                {
                    provider = new NAudio.Wave.SampleProviders.MonoToStereoSampleProvider(provider);
                }
                else if (reader.WaveFormat.Channels == 2 && channels == 1)
                {
                    provider = new NAudio.Wave.SampleProviders.StereoToMonoSampleProvider(provider);
                }

                if (reader.WaveFormat.SampleRate != rate)
                {
                    provider = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(provider, rate);
                }

                var result = new System.Collections.Generic.List<float>();
                var buffer = new float[rate * channels];
                int read;
                while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
                {
                    for (int i = 0; i < read; i++)
                    {
                        result.Add(buffer[i]);
                    }
                }

                return result.ToArray();
            }
        }
    }
}
