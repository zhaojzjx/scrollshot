using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ScrollShot
{
    /// <summary>
    /// 框选遮罩：全屏半透明 + 选区挖洞 + 实时尺寸标签。
    /// 覆盖虚拟桌面（全部显示器），并暴露自身 DPI 缩放供截图换算。
    ///
    /// 交互：拖拽框选 → **松开鼠标即确认并开始采集**（不再需要二次点击"确认"）；Esc 取消。
    /// </summary>
    public partial class SelectionOverlay : Window
    {
        private Point? _start;
        private bool _dragging;
        private bool _ready;   // 布局完成前不画遮罩，避免用 0 尺寸几何导致全屏纯黑

        public Rect? SelectedRect { get; private set; }
        public bool Confirmed { get; private set; }

        /// <summary>
        /// 覆盖层实际使用的 DPI 缩放。WPF 以单一 DPI 渲染整个覆盖窗口，
        /// 故 DIP→物理像素统一乘此值，与 BitBlt 需要的设备像素坐标自洽。
        /// </summary>
        public double Scale { get; private set; } = 1.0;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                if (dpi.DpiScaleX > 0) Scale = dpi.DpiScaleX;
            }
            catch { }
        }

        public SelectionOverlay()
        {
            InitializeComponent();
            // 覆盖全部显示器（虚屏），满足多屏框选
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;

            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 布局完成后再画遮罩：此前 ActualWidth/Height 可能为 0
            UpdateShadeGeometry();
            _ready = true;
            ResetSel();

            // 全屏 Topmost 窗口未必自动获得焦点，主动抢一次，否则收不到 Esc
            Activate();
            Focus();
            Keyboard.Focus(this);

            StartupLog.Write($"SelectionOverlay.Loaded: {ActualWidth}x{ActualHeight} " +
                             $"at {Left},{Top} dpi={Scale}");
        }

        private void UpdateShadeGeometry()
        {
            double w = ActualWidth > 0 ? ActualWidth : Width;
            double h = ActualHeight > 0 ? ActualHeight : Height;
            if (double.IsNaN(w) || w <= 0) w = SystemParameters.VirtualScreenWidth;
            if (double.IsNaN(h) || h <= 0) h = SystemParameters.VirtualScreenHeight;
            FullGeo.Rect = new Rect(0, 0, w, h);
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            UpdateShadeGeometry();
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            if (!_ready) return;
            _start = e.GetPosition(RootCanvas);
            _dragging = true;
            Label.Visibility = Visibility.Collapsed;
            Sel.Visibility = Visibility.Visible;
            Sel.Width = 0; Sel.Height = 0;
            HoleGeo.Rect = new Rect(_start.Value.X, _start.Value.Y, 0, 0);
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (!_dragging || _start == null) return;
            Point p = e.GetPosition(RootCanvas);
            double x = Math.Min(_start.Value.X, p.X);
            double y = Math.Min(_start.Value.Y, p.Y);
            double w = Math.Abs(p.X - _start.Value.X);
            double h = Math.Abs(p.Y - _start.Value.Y);
            var r = new Rect(x, y, w, h);

            Sel.Visibility = Visibility.Visible;
            Sel.Width = w; Sel.Height = h;
            Canvas.SetLeft(Sel, x); Canvas.SetTop(Sel, y);
            HoleGeo.Rect = r;
            ShowLabel(x, y, w, h);
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;

            // 选区过小视为误触：回到待框选状态，不退出
            if (Sel.Width < 8 || Sel.Height < 8) { ResetSel(); return; }

            // 松开即确认：遮罩关闭后才开始采集，天然不进截图
            Confirm();
        }

        private void ShowLabel(double x, double y, double w, double h)
        {
            LabelText.Text = $"{Math.Round(w)} × {Math.Round(h)} px";
            Label.Visibility = Visibility.Visible;
            Canvas.SetLeft(Label, x);
            Canvas.SetTop(Label, y + h + 4);
        }

        private void ResetSel()
        {
            Sel.Visibility = Visibility.Collapsed;
            Sel.Width = 0; Sel.Height = 0;
            HoleGeo.Rect = new Rect(0, 0, 0, 0);
            Label.Visibility = Visibility.Collapsed;
            _start = null;
            _dragging = false;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Cancel();
        }

        /// <summary>
        /// 右键取消（用户习惯）：框选前后、拖拽过程中单击右键都直接退出，
        /// 与 Esc 等价。右键不参与框选，故不必区分是否正在拖拽。
        /// </summary>
        private void OnRightUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            StartupLog.Write("SelectionOverlay: 右键取消");
            Cancel();
        }

        /// <summary>
        /// 外部（全局 Esc 钩子）也可调用：遮罩窗口万一没拿到焦点，用户按 Esc 仍能退出，
        /// 不会出现"点了开始之后只能杀进程"的死局。
        /// </summary>
        public void Cancel()
        {
            if (!IsVisible) return;
            Confirmed = false;
            try { DialogResult = false; }
            catch { Close(); }   // 非 ShowDialog 场景兜底
        }

        private void Confirm()
        {
            if (Sel.Width < 8 || Sel.Height < 8) return;
            double left = Canvas.GetLeft(Sel), top = Canvas.GetTop(Sel);
            if (double.IsNaN(left)) left = 0;
            if (double.IsNaN(top)) top = 0;
            SelectedRect = new Rect(left, top, Sel.Width, Sel.Height);
            Confirmed = true;
            try { DialogResult = true; } catch { Close(); }
        }
    }
}
