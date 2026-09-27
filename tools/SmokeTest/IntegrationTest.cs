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

            Console.WriteLine(ok ? "INTEGRATION OK" : "INTEGRATION FAILED: 期待した語がありません");
            return ok ? 0 : 1;
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
