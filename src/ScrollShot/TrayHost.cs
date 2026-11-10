using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace ScrollShot
{
    /// <summary>
    /// 托盘宿主：一个不可见窗口，仅承载通知区图标与回调消息。
    /// 不依赖 WinForms / System.Drawing，HICON 由内嵌的 .ico 自己生成。
    /// </summary>
    internal class TrayHost : Window
    {
        // 注意：NIM_ADD 是 0，0x0001 是 NIM_MODIFY。写成 1 等于「修改一个不存在的图标」，注册静默失败。
        private const uint NIM_ADD = 0x00000000;
        private const uint NIM_MODIFY = 0x00000001;
        private const uint NIM_DELETE = 0x00000002;
        private const uint NIF_MESSAGE = 0x0001;
        private const uint NIF_ICON = 0x0002;
        private const uint NIF_TIP = 0x0004;
        private const uint NIF_INFO = 0x0010;
        private const uint NIIF_INFO = 0x0001;
        private const uint NIIF_NOSOUND = 0x0010;

        private const uint WM_TRAY = 0x0400 + 2000;
        private const uint WM_RBUTTONUP = 0x0205;
        private const uint WM_LBUTTONUP = 0x0202;
        private const int IDI_APPLICATION = 32512;
        private const uint IMAGE_ICON = 1;
        private const uint LR_SHARED = 0x8000;
        private const uint LR_DEFAULTSIZE = 0x0040;
        private const uint LR_DEFAULTCOLOR = 0x0000;
        private static readonly uint WM_TASKBARCREATED =
            NativeMethods.RegisterWindowMessage("TaskbarCreated");

        /// <summary>完整版 NOTIFYICONDATAW（x64 下 976 字节）。cbSize 可改成 296 走 V1 兼容路径。</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA data);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW")]
        private static extern uint ExtractIconEx(string lpszFile, int nIconIndex,
            out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateIconFromResourceEx(byte[] presbits, int dwResSize,
            bool fIcon, int dwVer, int cxDesired, int cyDesired, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadImage(IntPtr hInst, IntPtr lpName, uint uType,
            int cx, int cy, uint fuLoad);

        private const int IconId = 1;
        private readonly Action _onStart;
        private readonly Action _onShow;
        private readonly Action _onExit;
        private readonly Action _onSettings;
        private readonly Action _onStop;

        private IntPtr _hwnd;
        private IntPtr _hIcon;
        private bool _ownsIcon;      // LR_SHARED 的系统图标不能 DestroyIcon
        private bool _hookAdded;
        private bool _registered;
        private bool _hintShown;

        public bool IsRunning { get; set; }

        public TrayHost(Action onStart, Action onShow, Action onExit, Action onSettings, Action onStop)
        {
            _onStart = onStart;
            _onShow = onShow;
            _onExit = onExit;
            _onSettings = onSettings;
            _onStop = onStop;
            Title = "ScrollShot.TrayHost";   // 与 MainWindow 的「滚动截图」区分，避免 FindWindow 误命中
            Width = 0; Height = 0;
            WindowStyle = WindowStyle.None;
            ShowInTaskbar = false;
            Visibility = Visibility.Hidden;
            // SourceInitialized 比 Loaded 更适合依赖 HWND 的初始化：隐藏窗口也一定会触发。
            SourceInitialized += (s, e) => Init();
            Loaded += (s, e) => Init();
            Closed += (s, e) => Unregister();
        }

        private void Init()
        {
            if (_registered) return;
            _hwnd = new WindowInteropHelper(this).Handle;
            if (_hwnd == IntPtr.Zero) return;
            if (!_hookAdded)
            {
                HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
                _hookAdded = true;
            }
            if (_hIcon == IntPtr.Zero) (_hIcon, _ownsIcon) = LoadAppIcon();
            AddIcon();
        }

        /// <summary>注册 / 重新注册图标（Explorer 重启后托盘会清空，需要重来一次）。</summary>
        private void AddIcon()
        {
            if (_hwnd == IntPtr.Zero) return;
            var data = Base(_hwnd);
            data.hIcon = _hIcon;
            data.cbSize = Marshal.SizeOf<NOTIFYICONDATA>();       // V4：支持气泡提示
            bool ok = Shell_NotifyIcon(NIM_ADD, ref data);
            if (!ok)
            {
                // 老版本 Explorer 只认 V1 尺寸；同一块内存改 cbSize 重试即可
                data.cbSize = (int)Marshal.OffsetOf<NOTIFYICONDATA>(nameof(NOTIFYICONDATA.dwState));
                ok = Shell_NotifyIcon(NIM_ADD, ref data);
            }
            _registered = ok;
            StartupLog.Write($"托盘注册: ok={ok} cbSize={data.cbSize} hIcon={_hIcon} " +
                             $"err={Marshal.GetLastWin32Error()} hwnd={_hwnd}");
        }

        private static NOTIFYICONDATA Base(IntPtr hwnd) => new NOTIFYICONDATA
        {
            hWnd = hwnd,
            uID = IconId,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAY,
            szTip = "滚动截图 · Ctrl+Alt+S",
            szInfo = "",
            szInfoTitle = "",
            guidItem = Guid.Empty,
            hBalloonIcon = IntPtr.Zero
        };

        /// <summary>关闭到托盘时给一次气泡，说明图标在哪、怎么真正退出。</summary>
        public void NotifyMinimized()
        {
            if (!_registered || _hintShown) return;
            _hintShown = true;
            var data = Base(_hwnd);
            data.cbSize = Marshal.SizeOf<NOTIFYICONDATA>();
            data.uFlags = NIF_INFO;
            data.szInfoTitle = "滚动截图";
            data.szInfo = "已最小化到系统托盘。右键图标可打开窗口或退出；看不到图标请点任务栏的 ^ 展开。";
            data.dwInfoFlags = NIIF_INFO | NIIF_NOSOUND;
            data.uTimeoutOrVersion = 5000;
            bool ok = Shell_NotifyIcon(NIM_MODIFY, ref data);
            StartupLog.Write($"托盘气泡: ok={ok} err={Marshal.GetLastWin32Error()}");
        }

        private void Unregister()
        {
            if (_hwnd == IntPtr.Zero) return;
            var data = Base(_hwnd);
            data.cbSize = Marshal.SizeOf<NOTIFYICONDATA>();
            bool ok = Shell_NotifyIcon(NIM_DELETE, ref data);
            StartupLog.Write($"托盘注销: ok={ok}");
            _registered = false;
            if (_ownsIcon && _hIcon != IntPtr.Zero)
            {
                DestroyIcon(_hIcon);
                _hIcon = IntPtr.Zero;
            }
        }

        /// <summary>
        /// 取程序自己的图标。优先用内嵌资源（一定是我们设计的那枚），
        /// 其次从 exe 的 PE 资源里抽，最后退到系统默认图标。
        /// </summary>
        private static (IntPtr Icon, bool Owned) LoadAppIcon()
        {
            // 1) WPF 内嵌资源 Assets/app.ico
            try
            {
                Stream? s = null;
                foreach (var u in new[] { "pack://application:,,,/Assets/app.ico",
                                          "pack://application:,,,/assets/app.ico" })
                {
                    s = Application.GetResourceStream(new Uri(u))?.Stream;
                    if (s != null) break;
                }
                if (s != null)
                {
                    using (s)
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        IntPtr h = IconFromIco(ms.ToArray(), 16);
                        if (h != IntPtr.Zero)
                        {
                            StartupLog.Write($"托盘图标来自内嵌 app.ico: {h}");
                            return (h, true);
                        }
                    }
                }
            }
            catch (Exception ex) { StartupLog.WriteException("LoadAppIcon(resource)", ex); }

            // 2) 单文件 exe 的第一枚图标（<ApplicationIcon> 会被写进 apphost）
            try
            {
                string? exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe) &&
                    ExtractIconEx(exe, 0, out IntPtr big, out IntPtr small, 1) > 0)
                {
                    IntPtr picked = small != IntPtr.Zero ? small : big;
                    if (picked != IntPtr.Zero)
                    {
                        if (small != IntPtr.Zero && big != IntPtr.Zero) DestroyIcon(big);
                        StartupLog.Write($"托盘图标来自 exe: {picked}");
                        return (picked, true);
                    }
                }
            }
            catch (Exception ex) { StartupLog.WriteException("LoadAppIcon(exe)", ex); }

            // 3) 系统预定义图标。hInst 必须为 NULL：传 exe 句柄会去 exe 里找 32512 号资源，必然失败。
            IntPtr stock = LoadImage(IntPtr.Zero, new IntPtr(IDI_APPLICATION),
                IMAGE_ICON, 16, 16, LR_SHARED | LR_DEFAULTSIZE);
            StartupLog.Write($"托盘图标回退到系统默认: {stock}");
            return (stock, false);   // LR_SHARED 由系统持有，不能 DestroyIcon
        }

        /// <summary>解析 .ico 目录，取最接近 want 尺寸的一帧交给 CreateIconFromResourceEx 生成 HICON。</summary>
        private static IntPtr IconFromIco(byte[] ico, int want)
        {
            if (ico.Length < 22) return IntPtr.Zero;
            int count = BitConverter.ToUInt16(ico, 4);
            int best = -1, bestW = 0;
            for (int i = 0; i < count; i++)
            {
                int p = 6 + i * 16;
                if (p + 16 > ico.Length) break;
                int w = ico[p] == 0 ? 256 : ico[p];
                int len = (int)BitConverter.ToUInt32(ico, p + 8);
                int off = (int)BitConverter.ToUInt32(ico, p + 12);
                if (off <= 0 || len <= 0 || off + len > ico.Length) continue;
                if (best < 0 || Math.Abs(w - want) < Math.Abs(bestW - want))
                {
                    best = p; bestW = w;
                }
            }
            if (best < 0) return IntPtr.Zero;
            int size = (int)BitConverter.ToUInt32(ico, best + 8);
            int offset = (int)BitConverter.ToUInt32(ico, best + 12);
            var bits = new byte[size];
            Buffer.BlockCopy(ico, offset, bits, 0, size);
            return CreateIconFromResourceEx(bits, size, true, 0x00030000, want, want, LR_DEFAULTCOLOR);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == (int)WM_TASKBARCREATED)
            {
                StartupLog.Write("Explorer 重建任务栏，重新注册托盘图标");
                _registered = false;
                AddIcon();
                handled = true;
            }
            else if (msg == (int)WM_TRAY)
            {
                uint lp = unchecked((uint)lParam.ToInt64());
                if (lp == WM_RBUTTONUP) { ShowMenu(); handled = true; }
                else if (lp == WM_LBUTTONUP) { _onShow?.Invoke(); handled = true; }
            }
            return IntPtr.Zero;
        }

        private void ShowMenu()
        {
            var menu = new ContextMenu
            {
                PlacementTarget = this,
                Placement = PlacementMode.MousePoint
            };
            var show = new MenuItem { Header = "打开主窗口" };
            show.Click += (s, e) => _onShow?.Invoke();
            var start = new MenuItem { Header = "开始截图 (Ctrl+Alt+S)", IsEnabled = !IsRunning };
            start.Click += (s, e) => _onStart?.Invoke();
            var stop = new MenuItem { Header = "停止采集", IsEnabled = IsRunning };
            stop.Click += (s, e) => _onStop?.Invoke();
            var settings = new MenuItem { Header = "设置…" };
            settings.Click += (s, e) => _onSettings?.Invoke();
            var exit = new MenuItem { Header = "退出" };
            exit.Click += (s, e) => _onExit?.Invoke();
            menu.Items.Add(show);
            menu.Items.Add(new Separator());
            menu.Items.Add(start);
            menu.Items.Add(stop);
            menu.Items.Add(settings);
            menu.Items.Add(new Separator());
            menu.Items.Add(exit);
            menu.IsOpen = true;
        }
    }
}
