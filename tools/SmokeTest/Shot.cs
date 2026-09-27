using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using WinRealtimeWhisper;

namespace WinRealtimeWhisperSmokeTest
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

        /// <summary>メイン画面のスクリーンショットを撮り、コントロールの寸法を出す。</summary>
        public static int RunMain()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var settings = AppSettings.Load();
            Loc.Current = settings.ResolveUiLanguage();

            using (var f = new MainForm())
            {
                f.Show();
                Settle();

                Save(f, "shot_main.png");
                Console.WriteLine("=== main -> shot_main.png");
                Dump(f, 1);

                f.Hide();
            }

            return 0;
        }

        /// <summary>
        /// 本文が読み取り専用の TextBox として選択・コピーできること、
        /// 長い本文で末尾が見えることを確認する。
        /// </summary>
        public static int EditorCheck()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var settings = AppSettings.Load();
            Loc.Current = settings.ResolveUiLanguage();

            int exit = 0;
            using (var f = new MainForm())
            {
                f.Show();
                Settle();

                var box = FindTextBox(f);
                if (box == null)
                {
                    Console.WriteLine("EDITOR FAIL: 本文の TextBox が見つかりません");
                    return 1;
                }

                // 見た目の確認用に、コントロールの上に実際の文字を乗せて描く
                var live = (MainForm)f;
                live.EditorBox.Text = string.Join(Environment.NewLine, new[]
                {
                    "こんにちは、これはリアルタイム文字起こしの表示テストです。",
                    "テキストは選択してコピーできます。",
                    "Ctrl+A で全選択、Ctrl+C でコピーできます。",
                    "行をまたいでドラッグして選択することもできます。",
                    "この行は選択状態の見た目を確認するためのものです。"
                });
                live.EditorBox.Select(60, 24);
                live.EditorBox.Select(0, 0);
                Application.DoEvents();
                Settle();
                Save(f, "shot_main_text.png");
                Console.WriteLine("=== text -> shot_main_text.png");
                live.EditorBox.Clear();
                Application.DoEvents();

                Console.WriteLine("readonly=" + box.ReadOnly + " multiline=" + box.Multiline
                    + " border=" + box.BorderStyle + " shortcuts=" + box.ShortcutsEnabled);

                if (!box.ReadOnly || !box.Multiline)
                {
                    Console.WriteLine("EDITOR FAIL: 読み取り専用の複数行 TextBox ではありません");
                    exit = 1;
                }

                // 長い本文を入れて、末尾が見える位置に自動スクロールするか確認する
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < 200; i++)
                {
                    sb.AppendLine("行 " + i + " の本文がここに入ります。コピーの確認用テキストです。");
                }

                box.Text = sb.ToString();
                Application.DoEvents();

                box.Select(0, 12);
                string copied = box.SelectedText;
                Console.WriteLine("selected='" + copied + "'");
                if (copied.Length != 12)
                {
                    Console.WriteLine("EDITOR FAIL: 選択ができません");
                    exit = 1;
                }

                box.SelectAll();
                if (box.SelectedText.Length != box.TextLength)
                {
                    Console.WriteLine("EDITOR FAIL: 全選択ができません");
                    exit = 1;
                }

                box.DeselectAll();
                Console.WriteLine("firstVisibleLine=" + box.GetFirstCharIndexFromLine(
                    box.GetLineFromCharIndex(box.GetCharIndexFromPosition(new Point(2, 2)))));

                // 「常に最前面」のトグルがメニューから働くか
                var miTop = FindMenuItem(f.MainMenuStrip, Loc.T("menu.view.alwaysOnTop"));
                if (miTop == null)
                {
                    Console.WriteLine("EDITOR FAIL: 「常に最前面」メニューが見つかりません");
                    exit = 1;
                }
                else
                {
                    Console.WriteLine("alwaysOnTop: checked=" + miTop.Checked + " topmost=" + f.TopMost);
                    if (miTop.Checked != f.TopMost)
                    {
                        Console.WriteLine("EDITOR FAIL: メニューのチェックと TopMost が不一致");
                        exit = 1;
                    }

                    miTop.PerformClick();
                    Application.DoEvents();
                    bool on = f.TopMost;
                    Console.WriteLine("  after click: checked=" + miTop.Checked + " topmost=" + f.TopMost);

                    if (on == (miTop.Checked == false) || f.TopMost != miTop.Checked)
                    {
                        Console.WriteLine("EDITOR FAIL: トグル後にメニューと TopMost が不一致");
                        exit = 1;
                    }

                    if (on != true)
                    {
                        Console.WriteLine("EDITOR FAIL: クリックで最前面になりません");
                        exit = 1;
                    }

                    miTop.PerformClick();
                    Application.DoEvents();
                    Console.WriteLine("  after 2nd click: checked=" + miTop.Checked + " topmost=" + f.TopMost);
                    if (f.TopMost != false)
                    {
                        Console.WriteLine("EDITOR FAIL: クリックで最前面が解除されません");
                        exit = 1;
                    }
                }

                Console.WriteLine(exit == 0 ? "EDITOR OK" : "EDITOR FAIL");
            }

            return exit;
        }

        /// <summary>メニューを表示テキストで探す。</summary>
        private static ToolStripMenuItem FindMenuItem(MenuStrip menu, string text)
        {
            if (menu == null)
            {
                return null;
            }

            foreach (ToolStripItem item in menu.Items)
            {
                var found = FindMenuItem(item, text);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static ToolStripMenuItem FindMenuItem(ToolStripItem item, string text)
        {
            var menuItem = item as ToolStripMenuItem;
            if (menuItem == null)
            {
                return null;
            }

            string plain = menuItem.Text.Replace("&", string.Empty);
            if (plain == text.Replace("&", string.Empty))
            {
                return menuItem;
            }

            foreach (ToolStripItem child in menuItem.DropDownItems)
            {
                var found = FindMenuItem(child, text);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static TextBox FindTextBox(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                var box = c as TextBox;
                if (box != null)
                {
                    return box;
                }

                var found = FindTextBox(c);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static void Save(Form f, string path)
        {
            // CopyFromScreen はウィンドウが最前面でないと別のものが写る。
            // フォーム自身に自分の姿を描かせる。
            var b = f.Bounds;
            using (var bmp = new Bitmap(f.ClientSize.Width, f.ClientSize.Height))
            {
                bool ok = false;
                for (int attempt = 0; attempt < 3 && !ok; attempt++)
                {
                    try
                    {
                        f.Invoke(new Action(() =>
                        {
                            using (var g = Graphics.FromImage(bmp))
                            {
                                var target = new Rectangle(0, 0, bmp.Width, bmp.Height);
                                f.DrawToBitmap(bmp, target);
                            }
                        }));
                        ok = true;
                    }
                    catch (Exception)
                    {
                        System.Threading.Thread.Sleep(50);
                    }
                }

                if (!ok)
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(b.Location, Point.Empty, f.ClientSize);
                    }
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
