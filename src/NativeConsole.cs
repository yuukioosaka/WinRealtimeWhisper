using System;
using System.Runtime.InteropServices;

namespace WinRealtimeWhisper
{
    /// <summary>
    /// WinExe はコンソールを持たないため、コマンドライン実行時は親プロセスの
    /// コンソールへ出力を付け替える。これで cmd から実行しても結果がその場で見える。
    /// </summary>
    internal static class NativeConsole
    {
        private const int AttachParentProcess = -1;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int processId);

        /// <summary>
        /// 親プロセスのコンソールに接続する。既にコンソールがある場合や、
        /// ダブルクリック起動で親にコンソールが無い場合は何もしない。
        /// </summary>
        public static bool Attach()
        {
            try
            {
                if (GetConsoleWindow() != IntPtr.Zero)
                {
                    return true;
                }

                return AttachConsole(AttachParentProcess);
            }
            catch (Exception)
            {
                return false;
            }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();
    }
}
