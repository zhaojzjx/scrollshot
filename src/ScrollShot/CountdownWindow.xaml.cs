using System;
using System.Windows;
using System.Windows.Threading;

namespace ScrollShot
{
    /// <summary>采集前倒计时浮层：可跳过；倒计时结束后自动关闭并开始采集。</summary>
    public partial class CountdownWindow : Window
    {
        private int _remain;
        private readonly DispatcherTimer _timer;

        public CountdownWindow(int seconds)
        {
            InitializeComponent();
            _remain = Math.Max(1, seconds);
            Num.Text = $"{_remain}";
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) =>
            {
                _remain--;
                if (_remain <= 0) { _timer.Stop(); DialogResult = true; return; }
                Num.Text = $"{_remain}";
            };
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            _timer.Start();
        }

        private void SkipBtn_Click(object s, RoutedEventArgs e)
        {
            _timer.Stop();
            DialogResult = true;
        }
    }
}
