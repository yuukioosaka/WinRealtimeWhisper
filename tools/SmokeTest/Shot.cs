using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using WinWhisper;

namespace WinWhisperSmokeTest
{
    /// <summary>
    /// 見た目の確認用。ダイアログのタブごとにスクリーンショットを撮り、
    /// 各コントロールの縦位置と高さを数値で出す。
    /// </summary>
    internal static class Shot
    {
        public static int Run()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var settings = AppSettings.Load();
            Loc.Current = settings.ResolveUiLanguage();

            using (var f = new SettingsForm(settings))
            {
                f.Show();
                Application.DoEvents();

                var tabs = FindTabs(f);
                for (int i = 0; i < tabs.TabPages.Count; i++)
                {
                    tabs.SelectedIndex = i;
                    Application.DoEvents();
                    Settle();

                    string name = "shot_tab" + i + ".png";
                    Save(f, name);
                    Console.WriteLine("=== tab " + i + " " + tabs.TabPages[i].Text + " -> " + name);
                    Dump(tabs.TabPages[i], 1);
                }

                f.Hide();
            }

            return 0;
        }

        private static void Save(Form f, string path)
        {
            var b = f.Bounds;
            using (var bmp = new Bitmap(b.Width, b.Height))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(b.Location, Point.Empty, b.Size);
                }

                bmp.Save(path, ImageFormat.Png);
            }
        }

        private static void Settle()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 400)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(30);
            }
        }

        private static TabControl FindTabs(Form f)
        {
            foreach (Control c in f.Controls)
            {
                var t = c as TabControl;
                if (t != null)
                {
                    return t;
                }
            }

            return null;
        }

        private static void Dump(Control parent, int depth)
        {
            string pad = new string(' ', depth * 2);
            foreach (Control c in parent.Controls)
            {
                Console.WriteLine(pad + c.GetType().Name
                    + " y=" + c.Top + " h=" + c.Height
                    + " pref=" + c.PreferredSize.Height
                    + " text='" + Short(c.Text) + "'");

                Dump(c, depth + 1);
            }
        }

        private static string Short(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return string.Empty;
            }

            s = s.Replace("\r", string.Empty).Replace("\n", "\\n");
            return s.Length > 34 ? s.Substring(0, 34) + "..." : s;
        }
    }
}
