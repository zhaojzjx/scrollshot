using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using ScrollShot.Capture;
using ScrollShot.Scroll;
using ScrollShot.Stitch;

namespace ScrollShot
{
    /// <summary>
    /// 采集编排：框选(DIP) → DPI 换算 → 滚顶 → 稳定帧检测 → 重叠特征匹配 → 流式封板（分卷）→ 落盘。
    /// 分卷封板 / 切点 / 阈值 / 多卷命名 由 VolumeManager 负责。
    /// </summary>
    internal class CaptureSession
    {
        public int MaxShots { get; set; } = 1000; // 安全上限，正常由"到底"或阈值结束

        /// <summary>最近若干次被采信的帧间位移，用于识别"匹配错了一格"的离群值。</summary>
        private readonly List<int> _recentShifts = new();

        private static int Median(List<int> values)
        {
            if (values.Count == 0) return 0;
            var copy = new List<int>(values);
            copy.Sort();
            return copy[copy.Count / 2];
        }

        public class Result
        {
            public bool Empty;
            public bool Aborted;
            public bool ThresholdReached;
            public int WarnCount;
            public IReadOnlyList<string> Files = Array.Empty<string>();
        }

        public async Task<Result> RunAsync(Rect dipRect, Config cfg, CaptureController ctrl,
            Action<int, int, int, string>? progress = null, double scale = 0)
        {
            var res = new Result();

            // 遮罩窗口刚关闭（松开鼠标即开始采集），稍等一帧让桌面重绘完成，
            // 避免首帧截到尚未消失的半透明遮罩。
            await Task.Delay(120);

            if (scale <= 0) scale = DpiHelper.GetScale(); // 兜底：未提供时按主屏 DPI
            int x = (int)Math.Round(dipRect.X * scale);
            int y = (int)Math.Round(dipRect.Y * scale);
            int w = (int)Math.Round(dipRect.Width * scale);
            int h = (int)Math.Round(dipRect.Height * scale);
            if (w <= 0 || h <= 0) { res.Empty = true; return res; }

            ICaptureProvider provider = MakeProvider(cfg);
            var fallback = new BitBltCapture();
            StartupLog.Write($"采集开始: 选区设备像素 {x},{y} {w}x{h} scale={scale} api={cfg.CaptureApi}");

            int stepPx = (int)(h * cfg.ScrollStepRatio);
            // 每滚轮刻度约 100px 只是初值，不同应用差异极大（VS Code 平滑滚动 ~50px/格，部分应用按屏滚）。
            // 循环内会依据特征匹配得到的实际位移自适应调整，见下方 notches 调整逻辑。
            int notches = Math.Max(1, (int)Math.Round(stepPx / 100.0));
            int cx = x + w / 2, cy = y + h / 2;

            IntPtr hwnd = ScrollController.WindowAt(cx, cy);
            ScrollController.Focus(hwnd);
            ScrollController.SendCtrlHome(); // 滚到顶部
            await Task.Delay(400);
            string baseName = $"ScrollShot_{DateTime.Now:yyyyMMdd_HHmmss}";
            var vm = new VolumeManager(w, cfg, cfg.OutputDir, baseName);

            // 隐藏光标：把光标移到选区之外，确保截图中不含鼠标指针
            int virtL = (int)Math.Round(SystemParameters.VirtualScreenLeft * scale);
            int virtT = (int)Math.Round(SystemParameters.VirtualScreenTop * scale);
            int virtW = (int)Math.Round(SystemParameters.VirtualScreenWidth * scale);
            int virtH = (int)Math.Round(SystemParameters.VirtualScreenHeight * scale);
            var (cursorX, cursorY) = CursorRestSpot(x, y, w, h, virtL, virtT, virtW, virtH);
            NativeMethods.SetCursorPos(cursorX, cursorY);
            NativeMethods.ShowCursor(false);

            try
            {
                BitmapSource? first = CaptureOutside(provider, x, y, w, h, cursorX, cursorY);
                // WGC 未实装时回退到 BitBlt
                if (first == null && cfg.CaptureApi == "WGC") { provider = fallback; first = CaptureOutside(provider, x, y, w, h, cursorX, cursorY); }
                if (first == null) { res.Empty = true; return res; }
                StartupLog.Write($"首帧已截取: {first.PixelWidth}x{first.PixelHeight}");
                vm.AddFrame(first, h); // 首帧整帧入画

                BitmapSource? prev = first;
                int frameCount = 1;
                int noChange = 0;   // 连续"整帧无变化"次数，用于判到底
                StartupLog.Write("进入采集循环");

                for (int i = 1; i < MaxShots; i++)
                {
                    if (ctrl.StopRequested) break;

                    // 暂停：挂起滚动，等待用户恢复或停止
                    while (ctrl.PauseRequested && !ctrl.StopRequested)
                        await Task.Delay(100);
                    if (ctrl.StopRequested) break;

                    if (cfg.ScrollMethod == "PageDown")
                    {
                        NativeMethods.SetCursorPos(cx, cy);   // 保证滚轮/键盘作用于选区内的窗口
                        ScrollController.PageDown();
                    }
                    else
                    {
                        // 真实滚轮事件按"光标下窗口"路由，故先定位到选区中心
                        ScrollController.WheelAt(cx, cy, -notches * 120);
                    }
                    await Task.Delay(cfg.WaitMs);

                    // 滚动事件已入队，立刻把光标移出选区，保证随后所有采样/截图都不含指针
                    NativeMethods.SetCursorPos(cursorX, cursorY);

                    BitmapSource? stable = StabilityDetector.WaitStable(provider, x, y, w, h,
                        stableFrames: cfg.StableFrames, intervalMs: cfg.StableIntervalMs);
                    if (stable == null) break;

                    double diff;
                    int shift = 0;
                    double conf = 0;

                    // ---- 抓帧确认：低置信度或重叠区对不上时，**原地重抓**（不重新滚动），最多 3 次 ----
                    // 原因：浏览器大跨度滚动后是分块重绘的，个别区块可能还空白或仍是旧画面。
                    // 这种"半成品"帧恰好能通过"连续两帧没变"的判稳（它自己停在半成品状态），
                    // 拼进去就是白块 / 叠字 —— 实测一次采集里出现过 150 多行纯白带。
                    // 原地再抓一帧基本就能拿到画完的画面。
                    for (int attempt = 0; ; attempt++)
                    {
                        diff = ImageUtil.DiffRatio(prev!, stable);

                        if (diff < 0.0005) { shift = 0; break; }   // 整帧没变，交给下面的"到底"分支

                        shift = FeatureMatcher.FindVerticalShift(prev!, stable, cfg.BandRatio, 3, out conf);
                        double vSad = FeatureMatcher.VerifySad(prev!, stable, shift);
                        // 重叠区几乎逐像素一致（vSad 很小）说明这帧本身是好的，conf 低只是因为
                        // 周期性内容有孪生解 —— 这种不必重抓，交给中位数兜底即可，省时间。
                        bool ok = vSad <= 6.0 && (conf >= 0.85 || vSad <= 2.0);
                        if (ok || attempt >= 3) break;

                        StartupLog.Write($"frame {frameCount + 1}: 重抓确认（conf={conf:F2} 重叠SAD={vSad:F2}，第 {attempt + 1} 次）");
                        await Task.Delay(160);
                        BitmapSource? again = provider.Capture(x, y, w, h);
                        if (again == null) break;
                        stable = again;
                    }

                    // ---- 整帧没变：绝不拼入 ----
                    // 内容一行都没动时，"位移"这个概念没有意义，可周期页面会让匹配器给出一个
                    // 看似合理的假位移（实测底部静止页面会报 shift=630 conf=0.35）。
                    // 若照它拼进去，就是把同一段内容反复追加（用户看到的"整段重复"就是这么来的）。
                    // 处理：先加倍滚轮确认一次（排除滚轮没送达），连续两次无变化才判定到底。
                    if (diff < 0.0005)
                    {
                        noChange++;
                        StartupLog.Write($"frame {frameCount + 1}: 整帧无变化（第 {noChange} 次），不拼入");
                        if (noChange >= 2)
                        {
                            StartupLog.Write($"frame {frameCount}: 已到底（连续 {noChange} 次整帧无变化）");
                            break;
                        }
                        notches = Math.Min(24, notches + 2);
                        prev = stable;
                        continue;
                    }
                    noChange = 0;

                    StartupLog.Write($"frame {frameCount + 1}: diff={diff:P2} shift={shift}px conf={conf:F2} 选区高={h} 齿数={notches}");

                    // 到底判定：画面几乎没变化 + 匹配位移极小。
                    // 不能要求像素完全相等 —— 编辑器插入光标闪烁会让最底部那一帧永远"有变化"。
                    if (diff < 0.01 && shift <= 2)
                    {
                        StartupLog.Write($"frame {frameCount}: 已到底（diff={diff:P3}, shift={shift}px）");
                        break;
                    }

                    // 置信度过低：不硬拼。有历史位移就按中位数推进，否则退回配置步长并减半滚轮齿数，
                    // 把"一次滚得太远、重叠不够匹配"的状态拉回来。
                    bool lowConf = false;
                    if (conf < 0.25)
                    {
                        lowConf = true;
                        if (_recentShifts.Count > 0)
                        {
                            int m = Median(_recentShifts);
                            StartupLog.Write($"frame {frameCount + 1}: conf={conf:F2} 过低，shift {shift}→{m}（沿用中位数）");
                            shift = m;
                        }
                        else
                        {
                            shift = Math.Max(1, Math.Min(h - 8, stepPx));
                        }
                        if (notches > 1) notches = Math.Max(1, notches / 2);
                        // 注意：这里不清 _recentShifts。历史位移恰恰是下一次低置信度时唯一可用的参照，
                        // 清掉等于"遇到异常就忘掉正常值"，下帧只能退回配置步长瞎猜。
                    }
                    else if (_recentShifts.Count >= 3 && conf < 0.95)
                    {
                        // 文本页每次滚轮位移几乎恒定，跳变基本等于匹配错了一格（周期性重复内容最容易这样）。
                        // 置信度不满分又偏离近三次中位数过多时，采用中位数并计一次告警。
                        int m = Median(_recentShifts);
                        int tol = Math.Max(12, (int)(m * 0.10));
                        if (Math.Abs(shift - m) > tol)
                        {
                            StartupLog.Write($"frame {frameCount + 1}: shift={shift} 偏离近三次中位数 {m}（conf={conf:F2}），改用中位数");
                            shift = m;
                            lowConf = true;
                        }
                    }

                    // 自适应滚轮齿数：把相邻帧位移控制在选区的 30%~55%。
                    // 位移一旦超过 0.62h，重叠不足 300px，周期性内容就分不清"滚了一屏"还是"滚了一个重复块"，
                    // 这是之前出现叠字 / 局部重复的直接原因，所以这里宁可多滚几次也要保住重叠。
                    double ratio = h > 0 ? (double)shift / h : 0;
                    if (ratio > 0.62) notches = Math.Max(1, notches - 2);
                    else if (ratio > 0.55) notches = Math.Max(1, notches - 1);
                    else if (ratio < 0.30) notches = Math.Min(24, notches + 1);

                    _recentShifts.Add(shift);
                    if (_recentShifts.Count > 3) _recentShifts.RemoveAt(0);

                    bool sealedNow = vm.AddFrame(stable, shift, lowConf);
                    prev = stable;
                    frameCount++;

                    progress?.Invoke(frameCount, MaxShots, vm.TotalHeight, sealedNow ? "writing" : "");
                    if (vm.ThresholdReached) break;
                }

                // 结束/中止：除非用户选择"丢弃当前卷"，否则把当前卷封板
                if (!ctrl.DiscardRequested)
                    vm.Finish();
                else
                    res.Aborted = true;

                res.ThresholdReached = vm.ThresholdReached;
                res.WarnCount = vm.WarnCount;
                res.Files = vm.Files;
                if (vm.VolumeCount == 0 && !res.Aborted) res.Empty = true;
            }
            finally
            {
                NativeMethods.ShowCursor(true); // 恢复光标显示
            }

            return res;
        }

        /// <summary>
        /// 选一个「在虚拟屏内、且在选区之外」的光标停放点。
        ///
        /// 旧实现直接拿设备像素坐标与 SystemParameters.VirtualScreenWidth 比较，而后者是 DIP。
        /// 200% 缩放下 1440 DIP 对应 2880 设备像素，判据 `x + w + 2 &lt; 1440` 在宽选区上恒为假，
        /// 于是走到 x - 2；若选区贴着左边缘（x = 0）就是一个负坐标，
        /// SetCursorPos 会把它夹回 (0, y) —— 正好落在选区里，鼠标指针被截进图。
        ///
        /// 现在按「右 → 左 → 下 → 上」的优先级找落点，全部用设备像素比较，
        /// 只有选区铺满整个虚拟屏时才退到右下角（此时无处可躲）。
        /// </summary>
        private static (int X, int Y) CursorRestSpot(int x, int y, int w, int h,
            int virtL, int virtT, int virtW, int virtH)
        {
            const int pad = 2;

            int right = x + w + pad;
            if (right <= virtL + virtW) return (right, y);

            int left = x - pad;
            if (left >= virtL) return (left, y);

            int bottom = y + h + pad;
            if (bottom <= virtT + virtH) return (x, bottom);

            int top = y - pad;
            if (top >= virtT) return (x, top);

            return (virtL + virtW - 1, virtT + virtH - 1);
        }

        /// <summary>截屏前确保光标在选区之外，避免指针入图。</summary>
        private static BitmapSource? CaptureOutside(ICaptureProvider cap, int x, int y, int w, int h,
            int cursorX, int cursorY)
        {
            NativeMethods.SetCursorPos(cursorX, cursorY);
            return cap.Capture(x, y, w, h);
        }

        private static ICaptureProvider MakeProvider(Config cfg)
        {
            if (cfg.CaptureApi == "WGC")
            {
                try { return new WgcCapture(); }
                catch { /* 回退 BitBlt */ }
            }
            return new BitBltCapture();
        }
    }
}
