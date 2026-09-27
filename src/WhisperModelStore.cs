using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace WinWhisper
{
    /// <summary>
    /// Whisper の ggml モデルをダウンロードして %LOCALAPPDATA%\WinWhisper\models\ に置く。
    ///
    /// モデルはアプリに同梱せず、初回に必要なものだけ取得する。
    /// CPU 実行なので small が速度と精度の妥協点、tiny/base は遅い PC 向け。
    /// </summary>
    internal static class WhisperModelStore
    {
        private const string BaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

        internal sealed class ModelInfo
        {
            public ModelInfo(string fileName, long approxBytes, string note)
            {
                FileName = fileName;
                ApproxBytes = approxBytes;
                Note = note;
            }

            public string FileName { get; private set; }
            public long ApproxBytes { get; private set; }
            public string Note { get; private set; }

            public string DisplayName
            {
                get
                {
                    return FileName + "  (" + (ApproxBytes / (1024 * 1024)) + " MB / " + Note + ")";
                }
            }

            public override string ToString()
            {
                return DisplayName;
            }
        }

        /// <summary>最初から使う想定のモデル。UI の初期選択と自動ダウンロードの対象。</summary>
        public const string DefaultModelFileName = "ggml-small.bin";

        public static readonly ModelInfo[] Models =
        {
            new ModelInfo("ggml-tiny.bin",   78L * 1024 * 1024,   "最速・精度低"),
            new ModelInfo("ggml-base.bin",   148L * 1024 * 1024,  "高速"),
            new ModelInfo("ggml-small.bin",  488L * 1024 * 1024,  "推奨"),
            new ModelInfo("ggml-medium.bin", 1536L * 1024 * 1024, "高精度・低速")
        };

        public static string UrlFor(string fileName)
        {
            return BaseUrl + fileName;
        }

        public static string PathFor(string fileName)
        {
            return Path.Combine(AppSettings.ModelDirectory, fileName);
        }

        public static bool Exists(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return false;
            }

            string path = Path.IsPathRooted(fileName) ? fileName : PathFor(fileName);
            return File.Exists(path) && new FileInfo(path).Length > 1024 * 1024;
        }

        /// <summary>設定に入っているモデルパスを、実際に存在する絶対パスへ解決する。</summary>
        public static string Resolve(string configured)
        {
            if (string.IsNullOrEmpty(configured))
            {
                string fallback = PathFor(DefaultModelFileName);
                return File.Exists(fallback) ? fallback : string.Empty;
            }

            return Path.IsPathRooted(configured) ? configured : PathFor(configured);
        }

        /// <summary>
        /// モデルをダウンロードする。拡張子 .part で受けてからリネームするため、
        /// 途中で失敗しても壊れたファイルが残らない。
        /// </summary>
        public static async Task DownloadAsync(
            string fileName,
            IProgress<long> progress,
            CancellationToken cancellationToken)
        {
            string dir = AppSettings.ModelDirectory;
            Directory.CreateDirectory(dir);

            string finalPath = PathFor(fileName);
            string tempPath = finalPath + ".part";

            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                }
            }

            var request = (HttpWebRequest)WebRequest.Create(UrlFor(fileName));
            request.UserAgent = "WinWhisper/1.0";
            request.Timeout = 30000;
            request.ReadWriteTimeout = 60000;

            using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
            using (var source = response.GetResponseStream())
            using (var target = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var buffer = new byte[1 << 20];
                long total = 0;
                int read;

                while ((read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                    total += read;

                    if (progress != null)
                    {
                        progress.Report(total);
                    }
                }
            }

            if (File.Exists(finalPath))
            {
                File.Delete(finalPath);
            }

            File.Move(tempPath, finalPath);
        }
    }
}
