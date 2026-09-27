using System;
using System.IO;
using System.Text;
using System.Threading;

namespace WinRealtimeWhisper
{
    /// <summary>
    /// Azure Speech SDK の診断ログを 1 箇所に集約する。
    /// %LOCALAPPDATA%\WinRealtimeWhisper\logs\winrealtimewhisper-yyyyMMdd-HHmmss.log に追記する。
    ///
    /// Speech SDK の内部ログ (Speech SDK の verbose ログ) と、アプリ側で
    /// 明示的に記録するイベントを同じファイルに並べて出すことで、
    /// 「どの設定で何が起きたか」を後から追えるようにする。
    /// </summary>
    internal static class DiagLog
    {
        private static readonly object Sync = new object();
        private static string _path;
        private static string _sdkPath;
        private static bool _enabled;

        public static string CurrentPath
        {
            get { return _path; }
        }

        /// <summary>
        /// Speech SDK 内部ログの出力先。
        /// SDK は起動時にファイルを切り詰めるため、アプリ側ログとは別ファイルにする。
        /// 同じファイルを指定するとアプリのログが消える。
        /// </summary>
        public static string SdkLogPath
        {
            get { return _sdkPath; }
        }

        public static bool Enabled
        {
            get { return _enabled; }
        }

        /// <summary>ログの保存先。既定は %LOCALAPPDATA%\WinRealtimeWhisper\logs。設定で差し替えられる。</summary>
        public static string Directory { get; set; }

        static DiagLog()
        {
            Directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinRealtimeWhisper",
                "logs");
        }

        /// <summary>ログファイルを開く。既に開いていれば何もしない。</summary>
        public static string Start()
        {
            lock (Sync)
            {
                if (_enabled)
                {
                    return _path;
                }

                try
                {
                    string dir = Directory;

                    System.IO.Directory.CreateDirectory(dir);

                    string stamp = "winrealtimewhisper-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    _path = Path.Combine(dir, stamp + ".log");
                    _sdkPath = Path.Combine(dir, stamp + ".sdk.log");

                    _enabled = true;

                    Write("================================================================");
                    Write("WinRealtimeWhisper log started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                    Write("OS      : " + Environment.OSVersion.Version);
                    Write("Runtime : " + Environment.Version);
                    Write("Process : " + (Environment.Is64BitProcess ? "x64" : "x86"));
                    Write("LogFile : " + _path);
                    Write("SdkLog  : " + _sdkPath);
                    Write("===============================================================");
                }
                catch
                {
                    // ログが書けないこと自体でアプリを止めない
                    _enabled = false;
                    _path = null;
                    _sdkPath = null;
                }

                return _path;
            }
        }

        /// <summary>任意のメッセージを記録する。</summary>
        public static void Write(string message)
        {
            if (!_enabled || _path == null)
            {
                return;
            }

            lock (Sync)
            {
                try
                {
                    File.AppendAllText(
                        _path,
                        DateTime.Now.ToString("HH:mm:ss.fff") + " [app] " + message + Environment.NewLine,
                        Encoding.UTF8);
                }
                catch
                {
                }
            }
        }

        /// <summary>例外をスタックトレース付きで記録する。</summary>
        public static void WriteException(string label, Exception ex)
        {
            if (ex == null)
            {
                Write(label + ": (null exception)");
                return;
            }

            var sb = new StringBuilder();
            sb.Append(label).Append(": ").Append(ex.GetType().FullName);
            sb.Append(" HRESULT=0x").Append(ex.HResult.ToString("X8"));
            sb.Append(" : ").Append(ex.Message);

            Exception inner = ex.InnerException;
            int depth = 0;
            while (inner != null && depth < 5)
            {
                sb.Append(Environment.NewLine)
                  .Append("    inner[").Append(depth).Append("]: ")
                  .Append(inner.GetType().FullName)
                  .Append(" : ").Append(inner.Message);
                inner = inner.InnerException;
                depth++;
            }

            sb.Append(Environment.NewLine).Append(ex.StackTrace);
            Write(sb.ToString());
        }
        /// <summary>例外から HRESULT と内部例外まで含めた 1 行説明を作る。</summary>
        public static string Describe(Exception ex)
        {
            if (ex == null)
            {
                return "(null)";
            }

            var sb = new StringBuilder();
            sb.Append(ex.GetType().Name)
              .Append(" HRESULT=0x").Append(ex.HResult.ToString("X8"))
              .Append(" : ").Append(ex.Message);

            Exception inner = ex.InnerException;
            int depth = 0;
            while (inner != null && depth < 3)
            {
                sb.Append(" -> ").Append(inner.GetType().Name).Append(": ").Append(inner.Message);
                inner = inner.InnerException;
                depth++;
            }

            return sb.ToString();
        }
    }
}
