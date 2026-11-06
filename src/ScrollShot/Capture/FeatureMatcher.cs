using System;
using System.Windows.Media.Imaging;

namespace ScrollShot.Capture
{
    /// <summary>
    /// 重叠特征匹配：求相邻两帧之间内容上移了多少行（FR-20 / FR-21）。
    ///
    /// 语义：向下滚动时内容整体上移，curr 的第 i 行内容等于 prev 的第 i+s 行。
    ///      s = 内容上移行数 = 本次滚动像素数；curr 在卷内的底边 = prev 底边 + s。
    ///
    /// 为什么不再用"按候选位移各自取中部带"的老做法（实测导致局部重影 / 错位）：
    ///   老实现对每个候选 s 都重新算一次带区位置（prev 的 [s+…] 中部），
    ///   于是**不同候选比较的是不同的画面内容**，SAD 曲线没有可比性；
    ///   遇到聊天记录 / 代码这类"结构高度重复"的页面（同一段回复周期性重复 665px），
    ///   多个候选都接近 0，取最小值的那个经常是错的那一个 → 拼出来就是文字叠字、段落跳变。
    ///
    /// 现在的做法：
    ///   1) 模板固定在 curr 的 [pad, pad+patch) 行，**所有候选都拿这同一块内容**去 prev 里找位置，
    ///      SAD 曲线才是"同一块内容在不同位置的对齐程度"，可以比较；
    ///   2) 行签名（每行取若干列）把 2D 比较降成行序列比较，全范围逐行扫描也只要十几毫秒；
    ///   3) 精修在最佳点 ±3 内做全列比较，误差压到 1px（NFR-04）；
    ///   4) 置信度同时考虑**绝对误差**与**歧义度**（次佳与最佳相差多少），
    ///      周期性内容会因歧义度高而自动降置信度，交给上层做稳健处理，而不是硬拼。
    /// </summary>
    internal static class FeatureMatcher
    {
        /// <summary>低置信度阈值：平均灰度差超过此值认为不可靠（FR-24）。</summary>
        public const double LowConfidenceSad = 14.0;

        /// <summary>最少重叠行数下限：低于此值的位移视为不可信（防止"只重叠几行"的伪匹配）。</summary>
        private const int MinOverlapRows = 32;

        /// <summary>每行参与快速匹配的采样列数。</summary>
        private const int SigCols = 24;

        /// <summary>模板最多比较的行数（再多也不会明显提高精度，只是变慢）。</summary>
        private const int MaxPatchRows = 360;

        public static int FindVerticalShift(BitmapSource prev, BitmapSource curr,
            double bandRatio = 0.7, int colStep = 3)
            => FindVerticalShift(prev, curr, bandRatio, colStep, out _);

        /// <summary>返回 curr 相对 prev 的上移行数 s（1 ≤ s ≤ h-MinOverlapRows）。置信度 1.0 表示完全匹配且无歧义。</summary>
        public static int FindVerticalShift(BitmapSource prev, BitmapSource curr,
            double bandRatio, int colStep, out double confidence)
        {
            confidence = 0;
            int w = prev.PixelWidth, h = prev.PixelHeight;
            if (w <= 0 || h <= 0) return 1;
            if (w != curr.PixelWidth || h != curr.PixelHeight) return h; // 尺寸异常：退化为不重叠

            if (bandRatio <= 0.1) bandRatio = 0.1;
            if (bandRatio > 1.0) bandRatio = 1.0;
            if (colStep < 1) colStep = 1;

            byte[] pGray = ToGray(prev);
            byte[] cGray = ToGray(curr);

            int minOverlap = Math.Max(MinOverlapRows, h / 12);
            int maxShift = h - minOverlap;
            if (maxShift < 2) return 1;

            // ---- 模板：curr 的固定行段，避开最顶部（可能有吸顶栏/标题条）----
            int pad = Math.Max(2, h / 24);
            int patchH = (int)(h * bandRatio * 0.45);
            if (patchH > MaxPatchRows) patchH = MaxPatchRows;
            if (patchH < MinOverlapRows) patchH = Math.Min(h - pad, MinOverlapRows);
            if (patchH < 8) return 1;

            // ---- 行签名：全范围逐行粗搜 ----
            int[] cols = BuildSampleCols(w, SigCols);
            byte[] cSig = RowSig(cGray, w, h, cols);
            byte[] pSig = RowSig(pGray, w, h, cols);

            var sad = new double[maxShift + 1];
            for (int s = 0; s <= maxShift; s++) sad[s] = double.MaxValue;

            for (int s = 2; s <= maxShift; s++)
            {
                int rows = Math.Min(patchH, h - pad - s);
                if (rows < MinOverlapRows) continue;

                long sum = 0;
                for (int i = 0; i < rows; i++)
                {
                    int ci = (pad + i) * SigCols;
                    int pi = (pad + s + i) * SigCols;
                    for (int k = 0; k < SigCols; k++)
                        sum += Math.Abs(cSig[ci + k] - pSig[pi + k]);
                }
                sad[s] = sum / (double)(rows * SigCols);
            }

            int coarseBest = -1;
            double coarseBestSad = double.MaxValue;
            for (int s = 2; s <= maxShift; s++)
                if (sad[s] < coarseBestSad) { coarseBestSad = sad[s]; coarseBest = s; }
            if (coarseBest < 0) return 1;

            // 次佳（与最佳相距 ≥6 行）：用于判断"周期性重复导致无法分辨"
            double secondSad = double.MaxValue;
            for (int s = 2; s <= maxShift; s++)
                if (Math.Abs(s - coarseBest) >= 6 && sad[s] < secondSad) secondSad = sad[s];

            // ---- 精修：±3 内全列比较，定到 1px ----
            int best = coarseBest;
            double bestSad = coarseBestSad;
            int lo = Math.Max(2, coarseBest - 3);
            int hi = Math.Min(maxShift, coarseBest + 3);
            for (int s = lo; s <= hi; s++)
            {
                int rows = Math.Min(patchH, h - pad - s);
                if (rows < MinOverlapRows) continue;
                double avg = AvgSadAt(pGray, cGray, w, h, pad, rows, s, colStep);
                if (avg >= 0 && avg < bestSad) { bestSad = avg; best = s; }
            }

            // ---- 置信度 = 绝对匹配度 × 歧义度 ----
            double baseConf = Math.Max(0, Math.Min(1, 1.0 - bestSad / (LowConfidenceSad * 2)));
            double sep = secondSad == double.MaxValue
                ? 1.0
                : Math.Min(1.0, (secondSad - coarseBestSad) / Math.Max(1.5, coarseBestSad * 0.6 + 1.0));
            confidence = Math.Max(0, Math.Min(1, baseConf * (0.35 + 0.65 * sep)));
            return best;
        }

        private static int[] BuildSampleCols(int w, int k)
        {
            if (k > w) k = w;
            var cols = new int[k];
            for (int i = 0; i < k; i++) cols[i] = (int)((i + 0.5) * w / k);
            return cols;
        }

        /// <summary>
        /// 全重叠区一致性校验：按给定 s 把 curr 的 [0, h-s) 与 prev 的 [s, h) 全量比一遍。
        /// 用途不只是"再确认一次位移"，更重要的是抓出**未完成重绘的帧**：
        /// 浏览器滚动后分块重绘，个别区块可能还是空白/旧画面，这种帧的局部模板依然能匹配上，
        /// 但整块重叠区的平均差会明显偏大 —— 于是上层可以丢弃重抓，而不是把白块拼进结果。
        /// </summary>
        public static double VerifySad(BitmapSource prev, BitmapSource curr, int shift,
            int rowStep = 2, int colStep = 2)
        {
            int w = prev.PixelWidth, h = prev.PixelHeight;
            if (w <= 0 || h <= 0 || w != curr.PixelWidth || h != curr.PixelHeight) return double.MaxValue;
            if (shift < 0 || shift >= h) return double.MaxValue;

            byte[] p = ToGray(prev);
            byte[] c = ToGray(curr);

            long sad = 0, samples = 0;
            for (int y = 0; y + shift < h; y += rowStep)
            {
                int cRow = y * w;
                int pRow = (y + shift) * w;
                for (int x = 0; x < w; x += colStep)
                {
                    sad += Math.Abs(c[cRow + x] - p[pRow + x]);
                    samples++;
                }
            }
            return samples == 0 ? double.MaxValue : sad / (double)samples;
        }

        /// <summary>每行按固定列采样成 k 字节签名，把 2D 匹配降为行序列比较。</summary>
        private static byte[] RowSig(byte[] g, int w, int h, int[] cols)
        {
            int k = cols.Length;
            var sig = new byte[h * k];
            for (int y = 0; y < h; y++)
            {
                int gRow = y * w, sRow = y * k;
                for (int i = 0; i < k; i++) sig[sRow + i] = g[gRow + cols[i]];
            }
            return sig;
        }

        /// <summary>
        /// 偏移 s 下的平均绝对灰度差。比较关系固定为 curr[pad+i] ↔ prev[pad+s+i]，
        /// 与候选 s 无关地使用同一块 curr 内容（这是可比较性的前提）。
        /// </summary>
        private static double AvgSadAt(byte[] p, byte[] c, int w, int h,
            int pad, int rows, int s, int colStep)
        {
            long sad = 0;
            long samples = 0;
            for (int i = 0; i < rows; i++)
            {
                int pRow = (pad + s + i) * w;
                int cRow = (pad + i) * w;
                for (int x = 0; x < w; x += colStep)
                {
                    sad += Math.Abs(p[pRow + x] - c[cRow + x]);
                    samples++;
                }
            }
            return samples == 0 ? -1 : sad / (double)samples;
        }

        private static byte[] ToGray(BitmapSource b)
        {
            int w = b.PixelWidth, h = b.PixelHeight;
            int stride = w * 4;
            var buf = new byte[stride * h];
            b.CopyPixels(buf, stride, 0);
            var gray = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                int gRow = y * w;
                for (int x = 0; x < w; x++)
                {
                    int o = row + x * 4;
                    gray[gRow + x] = (byte)((buf[o] + buf[o + 1] + buf[o + 2]) / 3);
                }
            }
            return gray;
        }
    }
}
