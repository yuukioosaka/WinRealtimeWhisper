using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAudio.CoreAudioApi;

namespace WinWhisper
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            CommandLineOptions options;

            try
            {
                options = CommandLineOptions.Parse(args);
            }
            catch (ArgumentException ex)
            {
                // 引数が壊れていても、言語指定だけは先に拾ってメッセージを合わせる。
                // コンソールへ出るメッセージなので、既定は英語にする。
                var lenient = CommandLineOptions.ParseLenient(args);
                CommandLineOptions.ApplyUiLanguage(lenient);
                if (lenient == null || !lenient.UiLanguage.HasValue)
                {
                    Loc.Current = UiLanguage.English;
                }

                CommandLineOptions.PrepareConsole();
                return CommandLineOptions.ReportBadArguments(ex.Message, Console.Error);
            }

            // 以降のメッセージはすべてこの言語で出す
            CommandLineOptions.ApplyUiLanguage(options);

            if (options.HelpRequested)
            {
                CommandLineOptions.PrepareConsole();
                CommandLineOptions.WriteHelp(Console.Out);
                return 0;
            }

            if (options.VersionRequested)
            {
                CommandLineOptions.PrepareConsole();
                Console.WriteLine(Loc.T("cli.version", CommandLineOptions.Version));
                return 0;
            }

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                MessageBox.Show(
                    ex != null ? ex.ToString() : Loc.T("dialog.unknownError"),
                    Loc.T("dialog.errorTitle"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            };

            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                e.SetObserved();
            };

            // 録音や文字起こしを伴う指定があれば、GUI を出さずに実行する。
            // 出力をリダイレクトできるようコンソールを親プロセスに付け替える。
            if (options.RunsHeadless)
            {
                NativeConsole.Attach();
                return HeadlessRunner.Run(options);
            }

            // GUI 起動時は、指定されたオプションで設定を上書きしてから開始する
            var settings = options.BuildSettings();
            if (!string.IsNullOrEmpty(options.ModelDirectory))
            {
                try
                {
                    AppSettings.ModelDirectoryOverride = Path.GetFullPath(options.ModelDirectory);
                }
                catch (Exception)
                {
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(settings));
            return 0;
        }
    }
}
