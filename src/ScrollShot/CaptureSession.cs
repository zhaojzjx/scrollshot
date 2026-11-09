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
            Action<int, int, int, string>? progress = null, double scale = 0,
            Rect? overlayRect = null, Action? hideOverlay = null)
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
            // 起步只发 2 格，之后靠自适应爬到目标位移。
            // 旧实现按「每格 100px」估算首帧齿数：实测 VS 是 147px/格，10 格 ≈1470px，
            // 几乎等于选区高 1476px —— 相邻帧几乎不重叠，匹配器最大只能表达 1353px，
            // 首帧被硬拼成 1029px，长图里直接少 441px（9 行代码）。
            // 各应用每格位移差异极大（VS 147px、VS Code 平滑滚动约 50px），绝不能靠猜。
            int notches = 2;
            double pxPerNotch = 0;   // 由可信帧学习的「每格滚轮实际滚动像素」
            int cx = x + w / 2, cy = y + h / 2;

            IntPtr hwnd = ScrollController.WindowAt(cx, cy);
            ScrollController.Focus(hwnd);
            ScrollController.SendCtrlHome(); // 滚到顶部
            await Task.Delay(400);
            string baseName = $"ScrollShot_{DateTime.Now:yyyyMMdd_HHmmss}";
            var vm = new VolumeManager(w, cfg, cfg.OutputDir, baseName);

            // 全程不主动搬动用户光标。BitBlt 抓的是屏幕合成结果、不含硬件鼠标指针，
            // 无需「把光标停到选区外」；旧版每帧 SetCursorPos 到固定停放点是鼠标被抢的根源。

            // 采集胶囊是 Topmost 窗口，而 BitBlt 抓的是屏幕合成结果：只要它与选区相交，
            // 就会被逐帧截进长图（表现为图片里反复出现的深色进度条 + 左侧内容被遮挡）。
            // MainWindow 已按四方向把它放到选区之外；这里再兜一次底：确实相交就整个采集期隐藏。
            bool capsuleHidden = false;
            if (overlayRect.HasValue && hideOverlay != null)
            {
                Rect over = overlayRect.Value;
                over.Inflate(2, 2); // 边框/圆角留 2px 余量
                if (over.IntersectsWith(new Rect(x, y, w, h)))
                {
                    StartupLog.Write("胶囊与选区相交，采集期间隐藏胶囊");
                    hideOverlay();
                    capsuleHidden = true;
                    ctrl.PointerOnCapsule = false;  // 胶囊已隐藏，别让残留的 hover 标志卡住采集
                    await Task.Delay(60);
                }
            }

            BitmapSource? first = provider.Capture(x, y, w, h);
            // WGC 未实装时回退到 BitBlt
            if (first == null && cfg.CaptureApi == "WGC") { provider = fallback; first = provider.Capture(x, y, w, h); }
            if (first == null) { res.Empty = true; return res; }
            StartupLog.Write($"首帧已截取: {first.PixelWidth}x{first.PixelHeight}");
            vm.AddFrame(first, h); // 首帧整帧入画

            BitmapSource? prev = first;
            int frameCount = 1;
            int noChange = 0;   // 连续"整帧无变化"次数，用于判到底
            StartupLog.Write($"进入采集循环: 起步 {notches} 格, 目标步长 {stepPx}px");

            for (int i = 1; i < MaxShots; i++)
            {
                if (ctrl.StopRequested) break;

                // 让行：用户把鼠标移到胶囊上（要按暂停/停止）或已点暂停时，不动鼠标、不滚动、不截帧。
                // 除 hover 事件外再轮询一次真实光标位置：程序搬运光标会打断 MouseEnter/Leave，
                // 只看事件标志容易漏；给胶囊外扩 12px 余量，鼠标靠近就先让开，按钮才点得到。
                // 余量刻意小于胶囊与选区的间距(16px)，避免框选刚松手就被误判成"悬停胶囊"。
                var userPt = ScrollController.Cursor();
                bool nearCapsule = !capsuleHidden && overlayRect.HasValue &&
                                   Near(userPt, overlayRect.Value, 12);
                while ((ctrl.PauseRequested || ctrl.PointerOnCapsule || nearCapsule) && !ctrl.StopRequested)
                {
                    await Task.Delay(100);
                    userPt = ScrollController.Cursor();
                    nearCapsule = !capsuleHidden && overlayRect.HasValue &&
                                  Near(userPt, overlayRect.Value, 12);
                }
                if (ctrl.StopRequested) break;
                if (userPt.X < 0 || userPt.Y < 0) userPt = new System.Drawing.Point(cx, cy); // 取位置失败时按"已在选区内"处理

                // 安全上限：一次滚动绝不能超过选区 55%，否则相邻帧重叠不足、特征匹配无解。
                // 还没学到「每格像素」时最多 3 格 —— 宁可多滚几帧，也不能漏内容。
                int safeMax = pxPerNotch > 1 ? Math.Max(1, (int)(h * 0.55 / pxPerNotch)) : 3;
                notches = Math.Max(1, Math.Min(Math.Min(24, safeMax), notches));
                int usedNotches = notches;

                if (cfg.ScrollMethod == "PageDown")
                {
                    // 键盘消息发给前台焦点窗口，与光标位置无关：一次都不碰鼠标。
                    ScrollController.PageDown();
                }
                else
                {
                    // 真实滚轮按「光标下窗口」路由。用户鼠标已经在选区内、或仍在目标窗口里时
                    // 直接发滚轮即可，完全不搬光标；只有鼠标落在别的窗口上才短暂借用，
                    // 发完立刻归还到用户原本的位置（而不是旧版的固定停放点）。
                    IntPtr centerWnd = ScrollController.RawWindowAt(cx, cy); // 每帧重取，遮罩消失后自动更新
                    bool borrow = !Inside(userPt, x, y, w, h) && !ScrollController.CursorOnWindow(userPt, centerWnd);
                    if (borrow) NativeMethods.SetCursorPos(cx, cy);
                    ScrollController.Wheel(-usedNotches * 120);
                    if (borrow) NativeMethods.SetCursorPos(userPt.X, userPt.Y);
                }

                await Task.Delay(cfg.WaitMs);

                BitmapSource? stable = StabilityDetector.WaitStable(provider, x, y, w, h,
                    stableFrames: cfg.StableFrames, intervalMs: cfg.StableIntervalMs);
                if (stable == null) break;

                double diff;
                int shift = 0;
                double conf = 0;
                double vSad = double.MaxValue;

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
                    vSad = FeatureMatcher.VerifySad(prev!, stable, shift);
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

                StartupLog.Write($"frame {frameCount + 1}: diff={diff:P2} shift={shift}px conf={conf:F2} 重叠SAD={vSad:F1} 选区高={h} 齿数={usedNotches} 每格≈{pxPerNotch:F0}px");

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

                // ---- 物理模型交叉校验：防止「重叠区认错位置」造成漏行 ----
                // pxPerNotch 由前几帧可信匹配学到；本帧发了 usedNotches 格，内容上移量
                // 理应接近 usedNotches × pxPerNotch。代码页高度重复时行签名偶尔会认错一格，
                // 给出的 shift 偏小 → 新帧盖住旧帧底部，那几行内容就永久丢了。
                // 用「全重叠区一致性」在两个候选间择优：当前匹配本来就好（SAD ≤ 3）就不动，
                // 只有明显对不上、而物理估计对得更齐时才纠正。
                if (pxPerNotch > 1)
                {
                    int physical = (int)Math.Round(pxPerNotch * usedNotches);
                    if (physical >= 2 && physical < h && Math.Abs(physical - shift) > 24)
                    {
                        double sadCur = FeatureMatcher.VerifySad(prev!, stable!, Math.Clamp(shift, 0, h - 1));
                        double sadPhys = FeatureMatcher.VerifySad(prev!, stable!, physical);
                        if (sadCur > 3.0 && sadPhys < sadCur - 0.5)
                        {
                            StartupLog.Write($"frame {frameCount + 1}: 位移按滚轮物理模型修正 {shift}→{physical}（重叠SAD {sadCur:F2}→{sadPhys:F2}）");
                            shift = physical;
                            conf = Math.Max(conf, 0.9);
                        }
                    }
                }

                // 学到「每格滚轮到底滚了多少像素」，供下一帧的安全上限与物理校验使用。
                // 只采信高置信度、重叠区也确实对得上的帧，避免把错配学进模型。
                if (conf >= 0.85 && vSad <= 6.0 && usedNotches > 0 && shift > 0)
                {
                    double observed = (double)shift / usedNotches;
                    pxPerNotch = pxPerNotch <= 0 ? observed : pxPerNotch * 0.6 + observed * 0.4;
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

            return res;
        }

        /// <summary>光标是否落在某个矩形（含外扩余量）内，用于「靠近胶囊就让行」。</summary>
        private static bool Near(System.Drawing.Point p, Rect r, int pad)
        {
            if (p.X < 0 || p.Y < 0) return false;
            var e = r;
            e.Inflate(pad, pad);
            return e.Contains(p.X, p.Y);
        }

        /// <summary>光标是否在选区内（设备像素）。</summary>
        private static bool Inside(System.Drawing.Point p, int x, int y, int w, int h) =>
            p.X >= x && p.X < x + w && p.Y >= y && p.Y < y + h;

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
