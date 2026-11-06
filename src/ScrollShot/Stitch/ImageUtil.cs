using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace ScrollShot.Stitch
{
    /// <summary>
    /// 图像工具：帧哈希（用于"连续两帧完全一致即到底"的停止判定 FR-16）、PNG 落盘。
    /// </summary>
    internal static class ImageUtil
    {
        public static long Hash(BitmapSource b)
        {
            int stride = b.PixelWidth * 4;
            var buf = new byte[stride * b.PixelHeight];
            b.CopyPixels(buf, stride, 0);
            // FNV-1a 采样哈希：每隔若干字节取一个，足够快速区分"是否到底"
            long h = 1469598103934665603L;
            for (int i = 0; i < buf.Length; i += 89)
                h = (h * 1099511628211L) ^ buf[i];
            return h;
        }

        /// <summary>
        /// 两帧的差异像素比例（0 = 完全一致，1 = 全不同）。
        ///
        /// 为什么不能再用整帧哈希判稳/判到底：编辑器有闪烁的插入光标（caret 由应用自绘，
        /// ShowCursor 管不到），每帧哈希都不同，稳定检测会一直等满采样上限，
        /// 导致每帧耗时数秒、上万行内容永远出不了卷。
        /// 采样步长取 2 行 2 列，闪烁光标这种极小面积变化只会落到 0.0x% 量级。
        /// </summary>
        public static double DiffRatio(BitmapSource a, BitmapSource b)
        {
            if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight) return 1.0;

            int w = a.PixelWidth, h = a.PixelHeight;
            int stride = w * 4;
            var ba = new byte[stride * h];
            var bb = new byte[stride * h];
            a.CopyPixels(ba, stride, 0);
            b.CopyPixels(bb, stride, 0);

            long diff = 0, total = 0;
            for (int y = 0; y < h; y += 2)
            {
                int row = y * stride;
                for (int x = 0; x < w; x += 2)
                {
                    int o = row + x * 4;
                    if (ba[o] != bb[o] || ba[o + 1] != bb[o + 1] || ba[o + 2] != bb[o + 2]) diff++;
                    total++;
                }
            }
            return total == 0 ? 0 : (double)diff / total;
        }

        /// <summary>按指定格式与质量保存（PNG 默认；JPG 支持质量，FR-35）。</summary>
        public static void SaveTo(BitmapSource img, string path, string format, int jpgQuality)
        {
            using var fs = File.Create(path);
            if (format.Equals("JPG", StringComparison.OrdinalIgnoreCase))
            {
                var enc = new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpgQuality, 10, 100) };
                enc.Frames.Add(BitmapFrame.Create(img));
                enc.Save(fs);
            }
            else
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(img));
                enc.Save(fs);
            }
        }

        public static string Save(BitmapSource img, string dir)
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"ScrollShot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            SaveTo(img, path, "PNG", 90);
            return path;
        }
    }
}
