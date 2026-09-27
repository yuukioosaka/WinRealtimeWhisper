using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using WinRealtimeWhisper;

namespace WinRealtimeWhisperMakeIcon
{
    /// <summary>
    /// アプリのアイコンを .ico として書き出す。
    ///
    /// アイコンの絵は AppIcons.Draw() が単一の定義元なので、
    /// 実行時のウィンドウアイコンと .ico の中身が食い違わない。
    /// .ico は exe の埋め込みアイコンとインストーラーの ARPPRODUCTICON に使う。
    ///
    /// 使い方: MakeIcon.exe &lt;出力パス&gt;
    /// </summary>
    internal static class Program
    {
        private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

        private static int Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.Error.WriteLine("usage: MakeIcon.exe <output.ico>");
                return 2;
            }

            string path = args[0];
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var images = new Image[Sizes.Length];
            try
            {
                for (int i = 0; i < Sizes.Length; i++)
                {
                    // 小さいサイズは 32px から縮小すると潰れるので、その大きさで描き直す
                    images[i] = AppIcons.Draw(Sizes[i]);
                }

                using (var stream = File.Create(path))
                {
                    WriteIco(stream, images);
                }
            }
            finally
            {
                for (int i = 0; i < images.Length; i++)
                {
                    if (images[i] != null)
                    {
                        images[i].Dispose();
                    }
                }
            }

            var info = new FileInfo(path);
            Console.WriteLine(path + " (" + info.Length + " bytes, " + Sizes.Length + " sizes)");
            return 0;
        }

        /// <summary>
        /// PNG 圧縮を使う ICO を書く（Vista 以降の形式）。
        /// 各エントリは PNG データをそのまま格納する。
        /// </summary>
        private static void WriteIco(Stream output, Image[] images)
        {
            using (var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, true))
            {
                int count = images.Length;

                // ICONDIR
                writer.Write((ushort)0);        // reserved
                writer.Write((ushort)1);        // type: icon
                writer.Write((ushort)count);

                var pngs = new byte[count][];
                for (int i = 0; i < count; i++)
                {
                    using (var buffer = new MemoryStream())
                    {
                        images[i].Save(buffer, ImageFormat.Png);
                        pngs[i] = buffer.ToArray();
                    }
                }

                int offset = 6 + (16 * count);

                // ICONDIRENTRY (16 バイトずつ)
                for (int i = 0; i < count; i++)
                {
                    int size = images[i].Width;
                    writer.Write((byte)(size >= 256 ? 0 : size));   // width (0 = 256)
                    writer.Write((byte)(size >= 256 ? 0 : size));   // height
                    writer.Write((byte)0);                          // color count (PNG では未使用)
                    writer.Write((byte)0);                          // reserved
                    writer.Write((ushort)1);                        // color planes
                    writer.Write((ushort)32);                       // bits per pixel
                    writer.Write(pngs[i].Length);
                    writer.Write(offset);
                    offset += pngs[i].Length;
                }

                for (int i = 0; i < count; i++)
                {
                    writer.Write(pngs[i]);
                }
            }
        }
    }
}
