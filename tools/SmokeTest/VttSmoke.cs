using System;
using System.Globalization;
using System.IO;
using System.Text;
using WinRealtimeWhisper;

namespace WinRealtimeWhisperSmokeTest
{
    /// <summary>
    /// ライブ文字起こし(WebVTT)の書き出しを確認する。
    /// 仕様: docs/transcript-vtt.md
    ///
    /// 実際にファイルを作り、生成された内容と、tail する側の読み取り方を検証する。
    /// </summary>
    internal static class VttSmoke
    {
        public static int Run()
        {
            string dir = Path.Combine(Path.GetTempPath(), "WinRealtimeWhisper_vtt_smoke");
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }

            Directory.CreateDirectory(dir);

            var started = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local);
            string finalPath;

            using (var writer = new VttTranscriptWriter(dir, started, "ja", "ggml-small.bin", 30))
            {
                writer.Start();
                Check(writer.LivePath.EndsWith(".live.vtt", StringComparison.Ordinal),
                    "live path should end with .live.vtt: " + writer.LivePath);

                string pointer = Path.Combine(dir, "current.txt");
                Check(File.Exists(pointer), "current.txt should exist while recording");
                string pointed = File.ReadAllText(pointer).Trim();
                Check(pointed == Path.GetFileName(writer.LivePath),
                    "current.txt should point at the live file: " + pointed);

                writer.WriteCue(TimeSpan.FromSeconds(12.5), TimeSpan.FromSeconds(1.7),
                    "今日は天気が良いですね。", "Remote");
                writer.WriteCue(TimeSpan.FromSeconds(14.2), TimeSpan.FromSeconds(2.6),
                    "ところで明日の予定ですが、\n会議が三件入っています。", "You");

                // heartbeat は間隔を見るので、明示的に呼んでも直後は書かれない
                writer.Heartbeat(TimeSpan.FromSeconds(5));

                finalPath = writer.FinalPath;
            }

            Check(!File.Exists(finalPath) == false, "final .vtt should exist after stop: " + finalPath);
            Check(!File.Exists(Path.Combine(dir, Path.GetFileName(finalPath).Replace(".vtt", ".live.vtt"))),
                "live file should be renamed away");

            string text = File.ReadAllText(finalPath, Encoding.UTF8);

            Check(text.StartsWith("WEBVTT\n", StringComparison.Ordinal), "must start with WEBVTT");
            Check(text.IndexOf("X-WINREALTIMEWHISPER-SESSION: 2026-01-02_0304", StringComparison.Ordinal) >= 0,
                "session header missing");
            Check(text.IndexOf("NOTE session", StringComparison.Ordinal) >= 0, "NOTE session missing");
            Check(text.IndexOf("NOTE session_end", StringComparison.Ordinal) >= 0, "NOTE session_end missing");

            // キューは連番で、1 件目は Remote、2 件目は You
            Check(text.IndexOf("\n1\n00:00:12.500 --> 00:00:14.200\n<v Remote>今日は天気が良いですね。\n",
                StringComparison.Ordinal) >= 0, "first cue mismatch:\n" + text);

            // 改行は 1 行に潰される
            Check(text.IndexOf("<v You>ところで明日の予定ですが、 会議が三件入っています。",
                StringComparison.Ordinal) >= 0, "second cue should be single line:\n" + text);

            // 全ブロックが空行で終端されている（tail 安全性の根拠）
            Check(!text.Contains("\n\n\n"), "blocks must be separated by exactly one blank line");
            Check(text.EndsWith("\n", StringComparison.Ordinal), "file must end with a newline");

            // tail 側の読み取り: 完全なブロックだけを拾えることを確認する
            int cues = CountCues(text);
            Check(cues == 2, "expected 2 cues, found " + cues);

            Console.WriteLine("VTT OK  " + Path.GetFileName(finalPath));
            return 0;
        }

        /// <summary>tail 側と同じ考え方で、空行終端されたブロックからキューだけ数える。</summary>
        private static int CountCues(string text)
        {
            int idx = text.LastIndexOf("\n\n", StringComparison.Ordinal);
            if (idx < 0)
            {
                return 0;
            }

            string head = text.Substring(0, idx + 2);
            string[] blocks = head.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
            int count = 0;

            foreach (string block in blocks)
            {
                string[] lines = block.Split('\n');
                if (lines.Length >= 2
                    && !lines[0].StartsWith("NOTE", StringComparison.Ordinal)
                    && !lines[0].StartsWith("WEBVTT", StringComparison.Ordinal)
                    && !lines[0].Contains(":")
                    && lines[1].Contains("-->"))
                {
                    count++;
                }
            }

            return count;
        }

        private static void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
