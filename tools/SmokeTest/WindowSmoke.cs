using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using WinRealtimeWhisper;

namespace WinRealtimeWhisperSmokeTest
{
    /// <summary>
    /// 前回終了時のウィンドウ位置と大きさが、settings.json を経由して復元できることを確認する。
    /// 画面外に出た矩形は復元しない（モニタを外した後に画面外へ出ないように）。
    /// </summary>
    internal static class WindowSmoke
    {
        public static int Run()
        {
            string file = Path.Combine(Path.GetTempPath(), "WinRealtimeWhisper_window_smoke.json");
            if (File.Exists(file))
            {
                File.Delete(file);
            }

            Environment.SetEnvironmentVariable("WINREALTIMEWHISPER_SETTINGS", file);

            // 未保存のうちは復元しない（既定の位置のまま）
            Check(AppSettings.Load().ResolveWindowBounds() == null,
                "fresh settings must not restore a window rectangle");

            // 保存 → 読み直しで同じ矩形が戻る
            Rectangle saved = Screen.PrimaryScreen.WorkingArea;
            int x = saved.Left + 40;
            int y = saved.Top + 30;
            int w = Math.Min(1000, saved.Width - 80);
            int h = Math.Min(700, saved.Height - 60);

            var settings = AppSettings.Load();
            settings.WindowX = x;
            settings.WindowY = y;
            settings.WindowWidth = w;
            settings.WindowHeight = h;
            settings.WindowMaximized = false;
            settings.Save();

            AppSettings reloaded = AppSettings.Load();
            Rectangle? restored = reloaded.ResolveWindowBounds();
            Check(restored.HasValue, "saved rectangle must be restored");
            Check(restored.Value.X == x && restored.Value.Y == y,
                "position mismatch: " + restored.Value);
            Check(restored.Value.Width == w && restored.Value.Height == h,
                "size mismatch: " + restored.Value);
            Check(reloaded.WindowMaximized == false, "maximized flag mismatch");

            // 最大化していたことも残る
            reloaded.WindowMaximized = true;
            reloaded.Save();
            Check(AppSettings.Load().WindowMaximized, "maximized flag must round-trip");

            // 画面外の矩形は復元しない
            var offscreen = AppSettings.Load();
            offscreen.WindowX = saved.Right + 5000;
            offscreen.WindowY = saved.Bottom + 5000;
            offscreen.WindowWidth = 800;
            offscreen.WindowHeight = 600;
            offscreen.Save();
            Check(AppSettings.Load().ResolveWindowBounds() == null,
                "a rectangle off every screen must not be restored");

            // 極端に小さい値も無視する
            var tiny = AppSettings.Load();
            tiny.WindowX = 0;
            tiny.WindowY = 0;
            tiny.WindowWidth = 10;
            tiny.WindowHeight = 10;
            tiny.Save();
            Check(AppSettings.Load().ResolveWindowBounds() == null,
                "a degenerate rectangle must not be restored");

            File.Delete(file);
            Environment.SetEnvironmentVariable("WINREALTIMEWHISPER_SETTINGS", null);

            Console.WriteLine("WINDOW OK  " + new Size(w, h));
            return 0;
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
