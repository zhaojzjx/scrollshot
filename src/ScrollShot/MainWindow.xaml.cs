using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScrollShot
{
    public partial class MainWindow : Window
    {
        private const int HOTKEY_ID = 1;
        private bool _running;
        private readonly Config _config = Config.Load();
        private CaptureController? _ctrl;
        private bool _capturing;
        private readonly object _hookLock = new();
        internal TrayHost? TrayRef { get; set; }

        /// <summary>本次会话的结果（仅内存，关闭主窗口即清空）。</summary>
        private List<string> _lastFiles = new();
        private int _lastWarnCount;

        /// <summary>正在显示的框选遮罩（供全局 Esc 兜底关闭）。</summary>
        private SelectionOverlay? _overlay;

        // 全局键盘钩子（Esc 中止采集）
        private IntPtr _hook;
        private NativeMethods.LowLevelKeyboardProc? _hookProc;

        public MainWindow()
        {
            InitializeComponent();
            InstallKeyboardHook();

            // 强制落在主显示器工作区居中：WindowStartupLocation=CenterScreen 会按"当前屏幕"居中，
            // 双屏环境下常把窗口送到副屏，用户会以为程序没启动。
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + Math.Max(0, (wa.Width - Width) / 2);
            Top = wa.Top + Math.Max(0, (wa.Height - Height) / 2);

            // 启动自检：把窗口实际位置写进日志，便于排查"窗口跑到屏幕外 / 不可见"
            Loaded += (s, e) =>
            {
                StartupLog.Write($"MainWindow.Loaded: Left={Left} Top={Top} W={ActualWidth} H={ActualHeight} " +
                                 $"Visible={IsVisible} State={WindowState} dpi={VisualTreeHelper.GetDpi(this).DpiScaleX}");
                StartupLog.Write($"虚屏: L={SystemParameters.VirtualScreenLeft} T={SystemParameters.VirtualScreenTop} " +
                                 $"W={SystemParameters.VirtualScreenWidth} H={SystemParameters.VirtualScreenHeight}");
                StartupLog.Write($"快捷键注册结果={_hotkeyOk} (spec={_config.Hotkey})");
            };
        }

        public void TriggerCapture() => StartCapture();

        /// <summary>从托盘唤起主窗口：先还原，再显示并置前。</summary>
        public void ShowAndActivate()
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Show();
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.UseDarkTitleBar(hwnd);
            RebindHotkey(_config.Hotkey);
            HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        }

        // ---- 全局热键 ----
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_HOTKEY && (int)wParam == HOTKEY_ID)
            {
                if (!_running) StartCapture();
                handled = true;
            }
            return IntPtr.Zero;
        }

        private bool _hotkeyOk;

        private void RebindHotkey(string spec)
        {
            NativeMethods.UnregisterHotKey(new WindowInteropHelper(this).Handle, HOTKEY_ID);
            var (mods, vk) = ParseHotkey(spec);
            if (vk != 0)
                _hotkeyOk = NativeMethods.RegisterHotKey(new WindowInteropHelper(this).Handle, HOTKEY_ID, mods, vk);
            StartupLog.Write($"RegisterHotKey({spec}) = {_hotkeyOk}");
        }

        private static (uint mods, uint vk) ParseHotkey(string spec)
        {
            uint mods = 0; uint vk = 0;
            foreach (var part in spec.Split('+'))
            {
                var t = part.Trim().ToLowerInvariant();
                if (t == "ctrl" || t == "control") mods |= NativeMethods.MOD_CONTROL;
                else if (t == "alt") mods |= NativeMethods.MOD_ALT;
                else if (t == "shift") mods |= NativeMethods.MOD_SHIFT;
                else if (t == "win") mods |= NativeMethods.MOD_WIN;
                else if (t.Length == 1 && char.IsLetterOrDigit(t[0])) vk = (uint)char.ToUpperInvariant(t[0]);
            }
            return (mods, vk);
        }

        // ---- 启动入口 ----
        private void StartBtn_Click(object s, RoutedEventArgs e) => StartCapture();

        /// <summary>主窗口是否被"采集期间自动隐藏"藏起来了（区别于关闭到托盘）。</summary>
        private bool _hiddenByCapture;

        /// <summary>
        /// 采集前把主窗口藏起来：一来它不会挡住要截的目标窗口，二来不会出现在框选/截图里。
        /// 返回 true 表示这次确实执行了隐藏（调用方据此决定结束时是否恢复）。
        /// 已经是"关闭到托盘"状态（本来就不可见）时不重复处理，保持用户的选择。
        /// </summary>
        private bool HideMainForCapture()
        {
            if (!IsVisible) return false;
            _hiddenByCapture = true;
            Hide();
            StartupLog.Write("MainWindow: 采集开始，自动隐藏主窗口");
            return true;
        }

        /// <summary>采集结束（成功/中止/异常/取消框选）恢复主窗口显示。可重复调用（幂等）。</summary>
        private void RestoreMainAfterCapture()
        {
            if (!_hiddenByCapture) return;
            _hiddenByCapture = false;
            try
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Show();
                Activate();
                Topmost = true;   // 再立刻取消置顶：确保窗口浮到最前，但不长期霸占
                Topmost = false;
                StartupLog.Write("MainWindow: 恢复显示主窗口（采集结束或取消）");
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("RestoreMainAfterCapture", ex);
            }
        }

        private void StartCapture()
        {
            if (_running) return;

            // 点击按钮/按热键后立刻隐藏自己，随后的框选与采集都在"没有主窗口"的桌面上进行
            HideMainForCapture();
            bool started = false;
            try
            {
                var overlay = new SelectionOverlay();
                _overlay = overlay;
                try
                {
                    if (overlay.ShowDialog() != true) return; // 遮罩关闭后才采集，天然不进截图
                    if (overlay.SelectedRect == null) return;
                    started = BeginCapture(overlay.SelectedRect.Value, overlay.Scale);
                }
                finally
                {
                    _overlay = null;
                }
            }
            finally
            {
                // 没真正进入采集（取消框选 / 倒计时取消）：当场把窗口放回来。
                // 已进入采集的，恢复动作交给采集结束回调，避免采集过程中主窗口又盖上来。
                if (!started) RestoreMainAfterCapture();
            }
        }

        public void OpenSettings() => SettingsBtn_Click(this, new RoutedEventArgs());
        public void StopCapture() { if (_ctrl != null) _ctrl.StopRequested = true; }

        /// <summary>
        /// 真正开始采集。返回 true 表示已进入采集（结束时会由回调恢复主窗口），
        /// false 表示没起来（重复调用 / 倒计时期间取消），调用方需自行恢复窗口。
        /// </summary>
        private bool BeginCapture(Rect dipRect, double scale)
        {
            if (_running) return false;
            if (scale <= 0) scale = 1.0;

            // 倒计时（默认关闭：框选完直接开始采集）
            if (_config.CountdownOn)
            {
                var cd = new CountdownWindow(_config.CountdownSec);
                if (cd.ShowDialog() != true) return false; // 用户在倒计时期间取消（Esc/关闭）
            }

            _running = true;
            _capturing = true;
            if (TrayRef != null) TrayRef.IsRunning = true;
            StartBtn.IsEnabled = false;
            StatusText.Text = "采集中…";

            _ctrl = new CaptureController();
            var rect = dipRect;
            var capsule = new CapsuleWindow(_ctrl, () => { });
            double capW = 320;
            double left = rect.X - capW - 8;
            if (left < 0) left = rect.X + rect.Width + 8;
            if (left + capW > SystemParameters.VirtualScreenWidth) left = 20;
            capsule.Left = left;
            capsule.Top = rect.Y;
            capsule.Show();

            var session = new CaptureSession();
            Task.Run(async () =>
            {
                try
                {
                    var res = await session.RunAsync(rect, _config, _ctrl, capsule.Update, scale);
                    Dispatcher.Invoke(() =>
                    {
                        capsule.Close();
                        RestoreMainAfterCapture();   // 先放回主窗口，再弹预览，预览才会盖在它上面
                        OnCaptureFinished(res);
                    });
                }
                catch (Exception ex)
                {
                    StartupLog.WriteException("CaptureSession.RunAsync", ex);   // 完整堆栈落日志，便于定位
                    Dispatcher.Invoke(() =>
                    {
                        capsule.Close();
                        RestoreMainAfterCapture();
                        StatusText.Text = $"错误：{ex.Message}";
                        ResultText.Text = $"采集失败：{ex.Message}\n详情见 {StartupLog.LogPath}";
                    });
                }
                finally
                {
                    _capturing = false;
                    if (TrayRef != null) TrayRef.IsRunning = false;
                    Dispatcher.Invoke(() =>
                    {
                        _running = false;
                        StartBtn.IsEnabled = true;
                        RestoreMainAfterCapture();   // 兜底：任何路径都不会把主窗口永久留在隐藏状态
                    });
                }
            });

            return true;
        }

        private void OnCaptureFinished(CaptureSession.Result res)
        {
            if (res.Empty && !res.Aborted)
            {
                StatusText.Text = "未采集到内容";
                ResultText.Text = "未采集到内容，请确认选区在可滚动的内容区域。";
                ResultActions.Visibility = Visibility.Collapsed;
                return;
            }

            int count = res.Files.Count;
            _lastFiles = new List<string>(res.Files);
            _lastWarnCount = res.WarnCount;

            if (count == 0)
            {
                StatusText.Text = "已中止，未保存";
                ResultText.Text = "已中止：没有可保存的图片（内容未滚动或已选择丢弃）。";
                ResultActions.Visibility = Visibility.Collapsed;
                return;
            }

            string sizeNote = res.ThresholdReached ? "（触达阈值，内容可能不完整）" : "";
            string warnNote = res.WarnCount > 0 ? $" · 告警 {res.WarnCount} 处" : "";
            string first = res.Files[0];

            // 图片在封板时已即时写入保存目录，这里只做结果展示
            StatusText.Text = $"完成：共 {count} 张 · 已保存到 {_config.OutputDir}";
            ResultText.Text = $"共 {count} 张{sizeNote}{warnNote}\n已保存到：{_config.OutputDir}\n{Path.GetFileName(first)}";
            ResultActions.Visibility = Visibility.Visible;

            // 默认复制首张/当前张到剪贴板
            if (_config.AutoCopy) CopyToClipboard(_config.CopyScope == "Current" ? count - 1 : 0);

            ShowPreview(res.Files, $"共 {count} 张", res.WarnCount);
        }

        /// <summary>
        /// 打开预览窗。设 Owner 是必须的：采集结束刚把主窗口恢复并置前，
        /// 若无 owner 关系，新预览窗可能落到主窗口下面（实测过），用户看不到结果。
        /// 有 owner 时预览恒定在主窗口之上，关闭预览后主窗口自然露出来。
        /// </summary>
        private void ShowPreview(IReadOnlyList<string> files, string title, int warnCount)
        {
            var w = new PreviewWindow(files, title, warnCount, _config.OutputDir);
            // 主窗口在托盘里（不可见）时不设 Owner：隐藏的 owner 会连累预览窗的层级/任务栏表现
            if (IsVisible) { try { w.Owner = this; } catch { } }
            w.Show();
            w.Activate();
        }

        private void CopyToClipboard(int idx)
        {
            if (idx < 0 || idx >= _lastFiles.Count) return;
            try
            {
                var img = BitmapFrame.Create(new Uri(_lastFiles[idx]));
                Clipboard.SetImage(img);
            }
            catch { }
        }

        private void PreviewBtn_Click(object s, RoutedEventArgs e)
        {
            if (_lastFiles.Count == 0) return;
            ShowPreview(_lastFiles, $"共 {_lastFiles.Count} 张", _lastWarnCount);
        }

        private void CopyFirstBtn_Click(object s, RoutedEventArgs e) => CopyToClipboard(0);

        // ---- 按钮 ----
        private void SettingsBtn_Click(object s, RoutedEventArgs e)
        {
            var w = new SettingsWindow(_config, RebindHotkey);
            w.Owner = this;
            w.ShowDialog();
        }

        private void FolderBtn_Click(object s, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(_config.OutputDir);
                Process.Start(new ProcessStartInfo("explorer.exe", _config.OutputDir) { UseShellExecute = true });
            }
            catch { }
        }

        // ---- 关闭最小化到托盘 ----
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_config.CloseToTray && !_forceClose)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            UninstallKeyboardHook();
            base.OnClosing(e);
        }

        private bool _forceClose;
        public void ForceClose()
        {
            _forceClose = true;
            Close();
        }

        // ---- 全局键盘钩子：Esc 中止 ----
        private void InstallKeyboardHook()
        {
            _hookProc = HookCallback;
            _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _hookProc,
                NativeMethods.GetModuleHandle(IntPtr.Zero), 0);
        }

        private void UninstallKeyboardHook()
        {
            if (_hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)NativeMethods.WM_KEYDOWN)
            {
                int vk = Marshal.ReadInt32(lParam);
                if (vk == NativeMethods.VK_ESCAPE)
                {
                    if (_capturing && _ctrl != null)
                    {
                        _ctrl.StopRequested = true;
                    }
                    else if (_overlay != null)
                    {
                        // 遮罩窗口未获得焦点时，靠全局钩子兜底取消，避免"点了开始只能杀进程"
                        Dispatcher.BeginInvoke(new Action(() => _overlay?.Cancel()));
                    }
                }
            }
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }
    }
}
