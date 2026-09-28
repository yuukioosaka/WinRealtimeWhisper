using System;
using System.IO;
using System.Speech.Synthesis;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using WinRealtimeWhisper;

namespace WinRealtimeWhisperSmokeTest
{
    /// <summary>
    /// 実際の TranscriptionEngine をループバック経由で動かす統合テスト。
    /// スピーカーから音を鳴らし、それをループバックで拾って文字起こしさせる。
    /// </summary>
    internal static class IntegrationTest
    {
        public static int Run(string modelPath)
        {
            DiagLog.Start();

            // 音声合成でテスト音声を作る
            string speechWav = Path.Combine(Path.GetTempPath(), "winrealtimewhisper_e2e.wav");
            SynthesizeJapanese(speechWav);

            // 既定の出力デバイスを取得（ループバックと再生で同じデバイスを使う）
            MMDevice device;
            using (var enumerator = new MMDeviceEnumerator())
            {
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }

            var settings = new AppSettings
            {
                ModelPath = modelPath,
                WhisperLanguage = "ja",
                SourceKind = AudioSourceKind.SystemLoopback,
                OutputDeviceId = device.ID
            };

            string wavOut = Path.Combine(Path.GetTempPath(), "winrealtimewhisper_e2e_rec.wav");
            string historyPath = Path.Combine(Path.GetTempPath(), "winrealtimewhisper_e2e_session");
            var session = new TranscriptionSession();

            using (var engine = new TranscriptionEngine())
            {
                engine.Status += (s, e) => Console.WriteLine("  status: " + e.Text);
                engine.FinalText += (s, e) =>
                {
                    Console.WriteLine("  FINAL [" + TimeSpan.FromTicks(e.OffsetTicks).ToString(@"hh\:mm\:ss") + "] " + e.Text);
                    session.AppendFinal(e.Text, TimeSpan.FromTicks(e.OffsetTicks));
                };
                engine.Failed += (s, e) => Console.WriteLine("  ERROR: " + e.Message);

                Console.WriteLine("engine starting...");
                engine.StartAsync(settings, wavOut);

                // モデル読み込み + デバイスオープンを待つ
                for (int i = 0; i < 100 && !engine.IsRecording; i++)
                {
                    Thread.Sleep(100);
                }

                if (!engine.IsRecording)
                {
                    Console.WriteLine("FAIL: 録音が開始されませんでした");
                    return 1;
                }

                Console.WriteLine("recording. playing test audio through the speaker...");

                // テスト音声をスピーカーで再生する（それをループバックが拾う）
                using (var player = new WaveFileReader(speechWav))
                using (var output = new WasapiOut(device, AudioClientShareMode.Shared, false, 100))
                {
                    output.Init(player);
                    output.Volume = 0.7f;
                    output.Play();

                    while (output.PlaybackState != PlaybackState.Stopped)
                    {
                        Thread.Sleep(100);
                    }
                }

                Console.WriteLine("playback finished. waiting a moment for VAD...");
                Thread.Sleep(1500);

                Console.WriteLine("stopping (remaining audio gets transcribed)...");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                engine.StopAsync().Wait(180000);
                Console.WriteLine("stop took " + sw.ElapsedMilliseconds + "ms");

                Console.WriteLine("wav bytes = " + engine.WavBytes + "  duration = " + engine.WavDuration);
            }

            string combined = session.ToPlainText();
            Console.WriteLine();
            Console.WriteLine("=== session text ===");
            Console.WriteLine(combined);

            string flat = combined.Replace(" ", string.Empty);
            bool ok = flat.IndexOf("天気", StringComparison.Ordinal) >= 0
                   || flat.IndexOf("テスト", StringComparison.Ordinal) >= 0
                   || flat.IndexOf("文字", StringComparison.Ordinal) >= 0;

            // WAV が実データとして書けているか
            var wavInfo = new FileInfo(wavOut);
            Console.WriteLine("wav file: " + wavInfo.Length + " bytes");
            if (wavInfo.Length < 44100 * 4)
            {
                Console.WriteLine("FAIL: WAV が小さすぎます（録音できていない）");
                ok = false;
            }
            else if (!CheckWav(wavOut))
            {
                ok = false;
            }

            Console.WriteLine(ok ? "INTEGRATION OK" : "INTEGRATION FAILED: 期待した語がありません");
            return ok ? 0 : 1;
        }

        /// <summary>
        /// 書いた WAV が音声として成立しているかを確かめる。
        ///
        /// float のサンプルを 16bit のつもりで書くと、生の float バイトが PCM として
        /// 並び、振幅も波形も滅茶苦茶になる。長さだけ見ても気づけないので、
        /// 実際に復号して波形の性質（不連続の少なさ）を見る。
        /// </summary>
        private static bool CheckWav(string path)
        {
            using (var reader = new WaveFileReader(path))
            {
                Console.WriteLine("  format: " + reader.WaveFormat);

                if (reader.WaveFormat.BitsPerSample != 16
                    || reader.WaveFormat.SampleRate != 44100
                    || reader.WaveFormat.Channels != 2)
                {
                    Console.WriteLine("FAIL: WAV の形式が 44.1kHz/16bit/2ch ではありません");
                    return false;
                }

                var buffer = new byte[reader.Length > 4 * 1024 * 1024 ? 4 * 1024 * 1024 : (int)reader.Length];
                int read = reader.Read(buffer, 0, buffer.Length);
                int samples = read / 2;

                if (samples < 44100)
                {
                    Console.WriteLine("FAIL: WAV のサンプル数が足りません: " + samples);
                    return false;
                }

                double peak = 0;
                double sum = 0;
                double diffSum = 0;

                short previous = 0;
                for (int i = 0; i < samples; i++)
                {
                    short value = (short)(buffer[i * 2] | (buffer[i * 2 + 1] << 8));
                    double magnitude = Math.Abs((double)value);

                    if (magnitude > peak) peak = magnitude;
                    sum += magnitude;

                    if (i > 0) diffSum += Math.Abs(value - previous);
                    previous = value;
                }

                double mean = sum / samples;
                double step = diffSum / (samples - 1);

                Console.WriteLine("  peak=" + peak.ToString("F0")
                    + " mean=" + mean.ToString("F0")
                    + " step=" + step.ToString("F0"));

                if (peak < 300)
                {
                    Console.WriteLine("FAIL: 振幅が小さすぎます（無音かも）: peak=" + peak);
                    return false;
                }

                // 正しい PCM なら隣り合うサンプルの差は小さい。
                // float を生で書くと、指数部と仮数部が交互に並び、差が跳ね上がる。
                if (step > 8000)
                {
                    Console.WriteLine("FAIL: 波形が不連続です（ノイズ）: step=" + step);
                    return false;
                }

                return true;
            }
        }

        private static void SynthesizeJapanese(string path)
        {
            using (var synth = new SpeechSynthesizer())
            {
                synth.Rate = -1;
                synth.SetOutputToWaveFile(path);
                synth.Speak("今日はいい天気ですね。文字起こしのテストをしています。");
                synth.SetOutputToNull();
            }

            Console.WriteLine("tts wav: " + path + " (" + new FileInfo(path).Length + " bytes)");
        }
    }
}
