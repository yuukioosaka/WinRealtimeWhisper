using System;
using System.Windows.Forms;
using WinWhisper;

namespace WinWhisperSmokeTest
{
    /// <summary>設定/履歴ダイアログの構築とレイアウトが例外なく通るか確認する。</summary>
    internal static class DialogSmoke
    {
        internal static int Run()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var settings = AppSettings.Load();

            try
            {
                using (var f = new SettingsForm(settings))
                {
                    f.CreateControl();
                    f.Show();
                    Application.DoEvents();
                    Console.WriteLine("SettingsForm OK  size=" + f.Size + " tabs=" + CountTabs(f));
                    f.ApplyToSettings();
                    Console.WriteLine("  applied: source=" + settings.SourceKind
                        + " out='" + settings.OutputDeviceId + "'"
                        + " in='" + settings.InputDeviceId + "'"
                        + " model=" + settings.ModelPath);
                    f.Hide();
                }

                using (var h = new HistoryForm())
                {
                    h.CreateControl();
                    h.Show();
                    Application.DoEvents();
                    Console.WriteLine("HistoryForm OK  size=" + h.Size);
                    h.Hide();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAILED: " + ex);
                return 1;
            }

            Console.WriteLine("DIALOGS OK");
            return 0;
        }

        private static int CountTabs(Form f)
        {
            foreach (Control c in f.Controls)
            {
                var tabs = c as TabControl;
                if (tabs != null)
                {
                    return tabs.TabPages.Count;
                }
            }

            return 0;
        }
    }
}
