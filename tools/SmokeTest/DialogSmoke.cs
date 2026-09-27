using System;
using System.IO;
using System.Windows.Forms;
using WinRealtimeWhisper;

namespace WinRealtimeWhisperSmokeTest
{
    /// <summary>設定/履歴ダイアログの構築とレイアウトが例外なく通るか確認する。</summary>
    internal static class DialogSmoke
    {
        internal static int Run()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // このテストは設定ファイルを書き換える。実設定を壊さないよう、
            // 環境変数で一時ファイルに差し替える。
            string settingsFile = Path.Combine(Path.GetTempPath(),
                "WinRealtimeWhisper_smoke_settings.json");
            Environment.SetEnvironmentVariable("WINREALTIMEWHISPER_SETTINGS", settingsFile);

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
                    Console.WriteLine("  storage: text='" + settings.ResolveHistoryDirectory()
                        + "' wav='" + settings.ResolveWavDirectory() + "'");

                    // 「OK」で保存先が実際の書き込み先に反映されるか。
                    // MainForm.ApplyStorageSettings と同じ代入を行う。
                    HistoryStore.RootDirectory = settings.ResolveHistoryDirectory();
                    if (settings.ResolveHistoryDirectory() != HistoryStore.RootDirectory)
                    {
                        Console.WriteLine("FAILED: HistoryStore.RootDirectory が設定と一致しません");
                        return 1;
                    }

                    f.Hide();
                }

                // 区切りサイクルの選択肢と反映
                {
                    foreach (int seconds in SettingsForm.CycleChoices)
                    {
                        var cycleSettings = AppSettings.Load();
                        cycleSettings.MaxChunkSeconds = seconds;
                        using (var f3 = new SettingsForm(cycleSettings))
                        {
                            f3.CreateControl();
                            f3.Show();
                            Application.DoEvents();
                            f3.ApplyToSettings();
                            f3.Hide();
                        }

                        Console.WriteLine("  cycle " + seconds + "s -> " + cycleSettings.MaxChunkSeconds + "s");
                        if (Math.Abs(cycleSettings.MaxChunkSeconds - seconds) > 0.001)
                        {
                            Console.WriteLine("FAILED: サイクル " + seconds + " 秒が反映されません");
                            return 1;
                        }
                    }

                    if (SettingsForm.CycleChoices.Length != 4)
                    {
                        Console.WriteLine("FAILED: サイクルの選択肢が 4 つではありません");
                        return 1;
                    }
                }

                // カスタムの保存先が解決結果と実際の書き込み先に反映されるか
                {
                    string custom = Path.Combine(Path.GetTempPath(), "WinRealtimeWhisper_smoke_storage");
                    var customSettings = AppSettings.Load();
                    customSettings.HistoryDirectory = Path.Combine(custom, "history");
                    customSettings.WavDirectory = Path.Combine(custom, "wav");
                    customSettings.ModelDirectory = Path.Combine(custom, "models");
                    customSettings.LogDirectory = Path.Combine(custom, "logs");
                    using (var f2 = new SettingsForm(customSettings))
                    {
                        f2.CreateControl();
                        f2.Show();
                        Application.DoEvents();
                        f2.ApplyToSettings();
                        f2.Hide();
                    }

                    Console.WriteLine("  custom: text='" + customSettings.ResolveHistoryDirectory()
                        + "'\n          wav='" + customSettings.ResolveWavDirectory()
                        + "'\n          model='" + customSettings.ResolveModelDirectory()
                        + "'\n          log='" + customSettings.ResolveLogDirectory() + "'");

                    if (customSettings.ResolveHistoryDirectory() != customSettings.HistoryDirectory
                        || customSettings.ResolveWavDirectory() != customSettings.WavDirectory
                        || customSettings.ResolveModelDirectory() != customSettings.ModelDirectory
                        || customSettings.ResolveLogDirectory() != customSettings.LogDirectory)
                    {
                        Console.WriteLine("FAILED: カスタム保存先が反映されません");
                        return 1;
                    }

                    // 空に戻すと既定値に戻るか
                    var cleared = new AppSettings();
                                    if (Math.Abs(cleared.MaxChunkSeconds - 30) > 0.001)
                                    {
                                        Console.WriteLine("FAILED: 既定のサイクルが 30 秒ではありません: " + cleared.MaxChunkSeconds);
                                        return 1;
                                    }

                                    Console.WriteLine("  default cycle: " + cleared.MaxChunkSeconds + "s");
                    if (cleared.ResolveHistoryDirectory().IndexOf("WinRealtimeWhisper", StringComparison.Ordinal) < 0
                        || cleared.ResolveWavDirectory().IndexOf("WinRealtimeWhisper", StringComparison.Ordinal) < 0
                        || cleared.ResolveModelDirectory().IndexOf("WinRealtimeWhisper", StringComparison.Ordinal) < 0
                        || cleared.ResolveLogDirectory().IndexOf("WinRealtimeWhisper", StringComparison.Ordinal) < 0)
                    {
                        Console.WriteLine("FAILED: 未設定時に既定値になりません");
                        return 1;
                    }

                    Console.WriteLine("  default: " + cleared.ResolveHistoryDirectory());

                    // 実際の設定ファイルに保存して読み戻す
                    var saved = AppSettings.Load();
                    saved.HistoryDirectory = customSettings.HistoryDirectory;
                    saved.WavDirectory = customSettings.WavDirectory;
                    saved.ModelDirectory = customSettings.ModelDirectory;
                    saved.LogDirectory = customSettings.LogDirectory;
                    saved.AlwaysOnTop = true;
                    saved.Save();

                    var reloaded = AppSettings.Load();
                    Console.WriteLine("  reload: text='" + reloaded.HistoryDirectory
                        + "'\n          model='" + reloaded.ModelDirectory
                        + "'\n          log='" + reloaded.LogDirectory
                        + "'\n          onTop=" + reloaded.AlwaysOnTop);

                    if (reloaded.HistoryDirectory != saved.HistoryDirectory
                        || reloaded.WavDirectory != saved.WavDirectory
                        || reloaded.ModelDirectory != saved.ModelDirectory
                        || reloaded.LogDirectory != saved.LogDirectory
                        || reloaded.AlwaysOnTop != true)
                    {
                        Console.WriteLine("FAILED: 保存先/最前面が設定ファイルを往復しません");
                        return 1;
                    }

                    // AlwaysOnTop を既定に戻しておく
                    reloaded.AlwaysOnTop = false;
                    reloaded.Save();
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
            finally
            {
                try
                {
                    if (File.Exists(settingsFile))
                    {
                        File.Delete(settingsFile);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("WARN: 一時設定の削除に失敗: " + ex.Message);
                }
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
