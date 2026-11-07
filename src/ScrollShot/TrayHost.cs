using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using ScrollShot;

namespace ScrollShot
{
    /// <summary>
    /// 托盘宿主：一个隐藏窗口，仅用于承载通知区图标与回调消息（备用入口）。
    /// 不依赖 WinForms / System.Drawing，图标用系统 IDI_APPLICATION。
    /// </summary>
    internal class TrayHost : Window
    {
        private const uint NIM_ADD = 0x0001;
        private const uint NIM_DELETE = 0x0002;
        private const uint NIF_MESSAGE = 0x0001;
        private const uint NIF_ICON = 0x0002;
        private const uint NIF_TIP = 0x0004;
        private const uint WM_TRAY = 0x0400 + 2000;
        private const uint WM_RBUTTONUP = 0x0205;
        private const uint WM_LBUTTONUP = 0x0202;
        private const int IDI_APPLICATION = 32512;
        private const uint IMAGE_ICON = 1;
        private const uint LR_SHARED = 0x8000;
        private const uint LR_DEFAULTSIZE = 0x0040;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA data);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadImage(IntPtr hInst, IntPtr lpName, uint uType, int cx, int cy, uint fuLoad);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(IntPtr lpModuleName);

        private readonly int _id = 1;
        private readonly Action _onStart;
        private readonly Action _onShow;
        private readonly Action _onExit;
        private readonly Action _onSettings;
        private readonly Action _onStop;

        public bool IsRunning { get; set; }

        public TrayHost(Action onStart, Action onShow, Action onExit, Action onSettings, Action onStop)
        {
            _onStart = onStart;
            _onShow = onShow;
            _onExit = onExit;
            _onSettings = onSettings;
            _onStop = onStop;
            Width = 0; Height = 0;
            WindowStyle = WindowStyle.None;
            ShowInTaskbar = false;
            Visibility = Visibility.Hidden;
            Loaded += (s, e) => Register();
            Closed += (s, e) => Unregister();
        }

        private void Register()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            IntPtr hIcon = LoadImage(GetModuleHandle(IntPtr.Zero), new IntPtr(IDI_APPLICATION),
                IMAGE_ICON, 16, 16, LR_SHARED | LR_DEFAULTSIZE);
            var data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = hwnd,
                uID = _id,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_TRAY,
                hIcon = hIcon,
                szTip = "滚动截图"
            };
            Shell_NotifyIcon(NIM_ADD, ref data);
            HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        }

        private void Unregister()
        {
            var data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = new WindowInteropHelper(this).Handle,
                uID = _id
            };
            Shell_NotifyIcon(NIM_DELETE, ref data);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == (int)WM_TRAY)
            {
                uint lp = (uint)lParam.ToInt32();
                if (lp == WM_RBUTTONUP) { ShowMenu(); handled = true; }
                else if (lp == WM_LBUTTONUP) { _onStart?.Invoke(); handled = true; }
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
            var start = new MenuItem { Header = "开始截图 (Ctrl+Alt+S)", IsEnabled = !IsRunning };
            start.Click += (s, e) => _onStart?.Invoke();
            var show = new MenuItem { Header = "打开主窗口" };
            show.Click += (s, e) => _onShow?.Invoke();
            var settings = new MenuItem { Header = "设置…" };
            settings.Click += (s, e) => _onSettings?.Invoke();
            var stop = new MenuItem { Header = "停止采集", IsEnabled = IsRunning };
            stop.Click += (s, e) => _onStop?.Invoke();
            var exit = new MenuItem { Header = "退出" };
            exit.Click += (s, e) => _onExit?.Invoke();
            menu.Items.Add(start);
            menu.Items.Add(new Separator());
            menu.Items.Add(show);
            menu.Items.Add(settings);
            menu.Items.Add(new Separator());
            menu.Items.Add(exit);
            menu.IsOpen = true;
        }
    }
}
