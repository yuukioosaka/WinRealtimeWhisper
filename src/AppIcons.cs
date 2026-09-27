using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace WinRealtimeWhisper
{
    /// <summary>
    /// 実行時にアイコンを描く。外部の .ico を同梱せずに済ませるため。
    /// マイクと波形をモチーフにした 32x32 のビットマップから作る。
    /// </summary>
    internal static class AppIcons
    {
        private static Icon _cached;

        /// <summary>ウィンドウとタスクバーに使うアイコン。一度作ったら使い回す。</summary>
        public static Icon Create()
        {
            if (_cached != null)
            {
                return _cached;
            }

            _cached = Build();
            return _cached;
        }

        private static Icon Build()
        {
            return IconFromBitmap(Draw(32));
        }

        /// <summary>
        /// アイコンを描く。32px を基準に、指定サイズへ拡大して使う。
        /// ビルドツールが .ico を書き出すときにも使う。
        /// </summary>
        internal static Bitmap Draw(int size)
        {
            var bitmap = new Bitmap(size, size);
            float scale = size / 32f;

            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                // 背景の角丸
                using (var background = new SolidBrush(Color.FromArgb(255, 25, 90, 155)))
                using (var path = RoundedRect(Scaled(0, 0, 31, 31, scale), (int)Math.Round(7 * scale)))
                {
                    g.FillPath(background, path);
                }

                // マイクのカプセル
                using (var body = new SolidBrush(Color.White))
                using (var capsule = RoundedRect(Scaled(12, 6, 8, 13, scale), (int)Math.Round(4 * scale)))
                {
                    g.FillPath(body, capsule);
                }

                // マイクの受け皿
                using (var pen = new Pen(Color.White, Math.Max(1f, 2f * scale)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawArc(pen, Scaled(8, 13, 16, 10, scale), 0, 180);
                }

                // マイクの脚
                using (var pen = new Pen(Color.White, Math.Max(1f, 2f * scale)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, 16 * scale, 24 * scale, 16 * scale, 27 * scale);
                    g.DrawLine(pen, 12 * scale, 28 * scale, 20 * scale, 28 * scale);
                }

                // 左下の入力レベル（小さいバー）
                using (var accent = new SolidBrush(Color.FromArgb(230, 255, 214, 102)))
                {
                    g.FillRectangle(accent, 4 * scale, 21 * scale, 2 * scale, 7 * scale);
                    g.FillRectangle(accent, 7 * scale, 18 * scale, 2 * scale, 10 * scale);
                }
            }

            return bitmap;
        }

        private static Rectangle Scaled(int x, int y, int width, int height, float scale)
        {
            return new Rectangle(
                (int)Math.Round(x * scale),
                (int)Math.Round(y * scale),
                Math.Max(1, (int)Math.Round(width * scale)),
                Math.Max(1, (int)Math.Round(height * scale)));
        }

        private static Icon IconFromBitmap(Bitmap bitmap)
        {
            IntPtr handle = bitmap.GetHicon();
            try
            {
                using (var fromHandle = Icon.FromHandle(handle))
                {
                    // GetHicon のハンドルは呼び出し側が破棄する必要があるため、複製して返す
                    return (Icon)fromHandle.Clone();
                }
            }
            finally
            {
                NativeMethods.DestroyIcon(handle);
            }
        }

        /// <summary>ツールバーの「録音開始」用。赤い丸。</summary>
        public static Image Record()
        {
            return Circle(Color.FromArgb(214, 48, 49));
        }

        /// <summary>ツールバーの「録音停止」用。暗い四角。</summary>
        public static Image Stop()
        {
            var bitmap = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (var brush = new SolidBrush(Color.FromArgb(86, 92, 100)))
                {
                    g.FillRectangle(brush, 2, 2, 12, 12);
                }
            }

            return bitmap;
        }

        private static Image Circle(Color color)
        {
            var bitmap = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (var brush = new SolidBrush(color))
                {
                    g.FillEllipse(brush, 2, 2, 12, 12);
                }
            }

            return bitmap;
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int diameter = radius * 2;
            var path = new GraphicsPath();

            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();

            return path;
        }

        private static class NativeMethods
        {
            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
            public static extern bool DestroyIcon(IntPtr handle);
        }
    }
}
