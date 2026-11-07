using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScrollShot;

namespace ScrollShot.Stitch
{
    /// <summary>
    /// 分卷封板引擎。
    ///
    /// 设计要点：
    ///  - 内存中同一时刻只持有"当前卷"（内存可控的根本原因）；
    ///  - 每拼入一帧后检查高度：达到「单张最大高度」即触发切点搜索→编码写出→用尾部 overlapPx 初始化下一卷开头；
    ///  - 切点优化：在目标高度上方 0–cutSearch 范围内搜索"行间空白行"（方差最低），使切点落在代码行之间；
    ///  - 卷间重叠：封板时截取当前卷尾部 overlapPx 作为下一卷开头，保证上下卷上下文连续；
    ///  - 阈值保护：卷数 / 累计总高度触顶即停止，已封板卷保留可用；
    ///  - 输出命名：ScrollShot_yyyyMMdd_HHmmss_01.png …，仅写图片、不建索引。
    /// </summary>
    internal class VolumeManager
    {
        private class Frame
        {
            public BitmapSource Bmp = null!;

            /// <summary>
            /// 该帧**底边**在「当前卷」内的 y 坐标。用底边而不是顶边定位，
            /// 是因为相邻帧的重叠量由"内容上移 s 行"决定：
            /// 新帧底边 = 上一帧底边 + s，帧高固定为选区高。
            /// 这样当 s &lt; 帧高时，新帧顶边会落在上一帧内部（重叠区被后帧覆盖），
            /// 卷内既不会重复也不会出现空洞。
            /// 旧实现按"上一帧底边 = 新帧顶边"摆放，每帧平白多出 (帧高 - s) 行，
            /// 在 70% 步长下就是每帧重复 180 行。
            /// </summary>
            public int Bottom;
        }

        private readonly int _width;
        private readonly int _maxHeight;
        private readonly int _overlapPx;
        private readonly int _maxVolumes;
        private readonly int _maxTotalHeight;
        private readonly string _outDir;
        private readonly string _baseName;
        private readonly string _format;
        private readonly int _jpgQuality;
        private readonly bool _cutPointOn;
        private readonly int _cutSearch;

        private readonly List<Frame> _cur = new();
        private int _volIndex;
        private int _totalHeight;
        private bool _thresholdReached;

        public VolumeManager(int width, Config cfg, string outDir, string baseName)
        {
            _width = width;
            _maxHeight = cfg.MaxVolumeHeight;
            _overlapPx = cfg.VolumeOverlap;
            _maxVolumes = cfg.MaxVolumes;
            _maxTotalHeight = cfg.MaxTotalHeight;
            _outDir = outDir;
            _baseName = baseName;
            _format = cfg.OutputFormat;
            _jpgQuality = cfg.JpgQuality;
            _cutPointOn = cfg.CutPointOn;
            _cutSearch = 120; // 切点微调范围 0–120px
        }

        public int VolumeCount => _volIndex;
        public int TotalHeight => _totalHeight;
        public bool ThresholdReached => _thresholdReached;
        /// <summary>低置信度拼接次数：>0 时结果页需展示告警。</summary>
        public int WarnCount => _warnCount;
        public IReadOnlyList<string> Files => _sealedFiles;

        private int _warnCount;
        private readonly List<string> _sealedFiles = new();

        private int CurHeight() => _cur.Count == 0 ? 0 : _cur[^1].Bottom;

        /// <summary>
        /// 拼入一帧。contribute = 该帧相对上一帧的内容上移行数（首帧传帧高，表示整帧入画）。
        /// 返回是否触发了一次封板。
        /// </summary>
        public bool AddFrame(BitmapSource frame, int contribute, bool lowConfidence = false)
        {
            if (_thresholdReached) return false;

            if (lowConfidence) _warnCount++;

            int bottom = CurHeight() + Math.Max(1, contribute);
            _cur.Add(new Frame { Bmp = frame, Bottom = bottom });

            if (ShouldSeal())
            {
                Seal();
                return true;
            }
            return false;
        }

        private bool ShouldSeal()
        {
            int h = CurHeight();
            if (h >= _maxHeight) return true;
            if (_volIndex >= _maxVolumes) return true;                       // 已达卷数上限
            if (_totalHeight + h >= _maxTotalHeight && h > 0) return true;   // 累计高度触顶
            return false;
        }

        /// <summary>任务结束/中止时调用：把当前卷（无论是否达到最大高度）封板。</summary>
        public void Finish()
        {
            if (_thresholdReached) return;
            if (CurHeight() > 0) Seal();
        }

        private void Seal()
        {
            int h = CurHeight();
            if (h <= 0) return;

            int cut;
            try { cut = _cutPointOn ? FindCutPoint(h) : Math.Min(h, _maxHeight); }
            catch { cut = Math.Min(h, _maxHeight); }   // 切点搜索失败不得中断封板
            if (cut <= 0) cut = Math.Min(h, _maxHeight);

            BitmapSource? vol = RenderRange(0, cut);
            if (vol != null)
            {
                _volIndex++;
                string path = SaveVolume(vol, _volIndex);
                _sealedFiles.Add(path);
                _totalHeight += cut;
            }

            // 边界重叠：截取当前卷尾部 overlapPx 作为下一卷开头
            bool canContinue = _volIndex < _maxVolumes && _totalHeight < _maxTotalHeight;
            if (canContinue)
            {
                int seedTop = Math.Max(0, cut - _overlapPx);
                BitmapSource? seed = RenderRange(seedTop, h);
                _cur.Clear();
                if (seed != null)
                {
                    // seed 是"当前卷尾部"的切片，其底边与新卷内容底边对齐
                    _cur.Add(new Frame { Bmp = seed, Bottom = seed.PixelHeight });
                    return;
                }
            }

            // 不能继续（已达阈值）：丢弃当前未封板部分，置阈值标志
            _cur.Clear();
            _thresholdReached = _volIndex >= _maxVolumes || _totalHeight >= _maxTotalHeight;
        }

        /// <summary>切点优化：在 [target-cutSearch, target] 内找方差最低的行作为切点。</summary>
        private int FindCutPoint(int h)
        {
            // 未达单张上限（典型是任务结束时的最后一卷）：整卷保留，不要在中间切，
            // 否则会白丢尾部内容。这条同时也是越界崩溃的根因修复：
            // 旧代码 target = min(h, maxHeight)，当 h <= maxHeight 时 target == h，
            // 而下面的行扫描是 y <= target，会读到 buf[h * stride + ...] —— 正好越过数组末尾一格，
            // 于是每次收尾封板必然抛 IndexOutOfRangeException（表现为"采集失败：Index was outside the bounds of the array."）。
            if (h <= _maxHeight) return h;

            int target = _maxHeight;
            int hi = Math.Min(target, h - 1);       // 严格小于 h，保证读行不越界
            int lo = Math.Max(1, hi - _cutSearch);
            if (lo >= hi) return target;

            BitmapSource? full = RenderRange(0, h);
            if (full == null) return target;

            int w = full.PixelWidth;
            int stride = w * 4;
            int rows = Math.Min(h, full.PixelHeight);   // 实际可读行数，兜底
            var buf = new byte[stride * rows];
            full.CopyPixels(buf, stride, 0);
            if (hi > rows - 1) hi = rows - 1;
            if (lo >= hi) return target;

            int bandX = w / 4, bandW = Math.Min(w / 2, w - bandX);
            if (bandW <= 0) bandW = w;

            int bestY = target;
            double bestVar = double.MaxValue;
            for (int y = lo; y <= hi; y++)
            {
                double mean = 0; int n = 0;
                for (int x = bandX; x < bandX + bandW; x += 2)
                {
                    int o = y * stride + x * 4;
                    mean += (buf[o] + buf[o + 1] + buf[o + 2]) / 3.0; n++;
                }
                if (n == 0) continue;
                mean /= n;
                double var = 0;
                for (int x = bandX; x < bandX + bandW; x += 2)
                {
                    int o = y * stride + x * 4;
                    double v = (buf[o] + buf[o + 1] + buf[o + 2]) / 3.0;
                    var += (v - mean) * (v - mean);
                }
                var /= n;
                if (var < bestVar) { bestVar = var; bestY = y; }
            }
            return bestY;
        }

        /// <summary>
        /// 渲染当前卷的 [a, b) 区间。逐帧按"底边对齐"绘制（后帧覆盖前帧的重叠区），
        /// 仅拷贝与目标区间相交的行，避免为整卷分配超长位图（内存仅与封板高度相关）。
        /// </summary>
        private BitmapSource? RenderRange(int a, int b)
        {
            int height = b - a;
            if (height <= 0) return null;

            int tStride = _width * 4;
            var canvas = new byte[tStride * height];

            foreach (var f in _cur)
            {
                int fh = f.Bmp.PixelHeight;
                int fTop = f.Bottom - fh;                  // 帧顶边（可能为负：该帧顶部已被 seed 覆盖）
                int copyStart = Math.Max(a, fTop);         // 拷贝区间（卷坐标）
                int copyEnd = Math.Min(b, f.Bottom);
                if (copyEnd <= copyStart) continue;

                int srcTop = copyStart - fTop;
                int copyH = copyEnd - copyStart;
                int dstY = copyStart - a;
                int sStride = f.Bmp.PixelWidth * 4;
                int cw = Math.Min(_width, f.Bmp.PixelWidth);   // 宽不一致时取小，避免越界
                if (cw <= 0) continue;
                if (srcTop < 0 || srcTop + copyH > fh) continue;
                if (dstY < 0 || dstY + copyH > height) continue;

                try
                {
                    var buf = new byte[sStride * copyH];
                    f.Bmp.CopyPixels(new Int32Rect(0, srcTop, cw, copyH), buf, sStride, 0);
                    for (int row = 0; row < copyH; row++)
                        Buffer.BlockCopy(buf, row * sStride, canvas, (dstY + row) * tStride, cw * 4);
                }
                catch
                {
                    // 单帧异常不得拖垮整卷：跳过该帧，其余帧继续拼
                }
            }

            var wb = new WriteableBitmap(_width, height, 96, 96, PixelFormats.Bgr32, null);
            wb.WritePixels(new Int32Rect(0, 0, _width, height), canvas, tStride, 0);
            return wb;
        }

        private string SaveVolume(BitmapSource img, int index)
        {
            Directory.CreateDirectory(_outDir);
            string ext = _format.Equals("JPG", StringComparison.OrdinalIgnoreCase) ? "jpg" : "png";
            string name = $"{_baseName}_{index:D2}.{ext}";
            string path = Path.Combine(_outDir, name);
            ImageUtil.SaveTo(img, path, _format, _jpgQuality);
            return path;
        }
    }
}
