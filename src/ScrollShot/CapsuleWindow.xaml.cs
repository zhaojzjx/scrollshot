using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ScrollShot;

namespace ScrollShot
{
    /// <summary>
    /// M2 采集胶囊（UI-19~UI-23）：小窗、可拖动、不进截图、含进度与暂停/停止。
    /// 放置在选区之外（MainWindow 计算位置），因此不会被截入（FR-15 / UI-22）。
    /// </summary>
    public partial class CapsuleWindow : Window
    {
        private readonly CaptureController _ctrl;
        private readonly Action _onPauseToggle;
        private bool _paused;

        public CapsuleWindow(CaptureController ctrl, Action onPauseToggle)
        {
            InitializeComponent();
            _ctrl = ctrl;
            _onPauseToggle = onPauseToggle;
        }

        public void Update(int shot, int max, int heightPx, string phase)
        {
            Dispatcher.Invoke(() =>
            {
                if (phase == "writing")
                {
                    Info.Text = "正在写出…";
                    Dot.Fill = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x7D));
                    return;
                }
                Info.Text = $"采集  第 {shot}/{max} 张   已拼接 {heightPx} px";
                double pct = max > 0 ? (double)shot / max * 100 : 0;
                Bar.Value = pct;
                Bar.Foreground = pct >= 80
                    ? new SolidColorBrush(Color.FromRgb(0xE5, 0xA0, 0x3A))
                    : new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xE0));
            });
        }

        private void Pause_Click(object sender, RoutedEventArgs e)
        {
            _paused = !_paused;
            _ctrl.PauseRequested = _paused;
            PauseBtn.Content = _paused ? "▶" : "⏸";
            _onPauseToggle?.Invoke();
        }

        private void Stop_Click(object sender, RoutedEventArgs e) => _ctrl.StopRequested = true;

        private void Discard_Click(object sender, RoutedEventArgs e)
        {
            var r = MessageBox.Show("停止并丢弃当前未封板的卷？已封板的卷会保留。",
                "停止并丢弃", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (r != MessageBoxResult.OK) return;
            _ctrl.DiscardRequested = true;
            _ctrl.StopRequested = true;
        }

        private void OnDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is Button) return;
            DragMove();
        }
    }
}
