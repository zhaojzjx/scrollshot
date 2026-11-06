using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace ScrollShot
{
    /// <summary>
    /// 设置窗口（UI-33~UI-38）：左侧分类 + 右侧表单，改动即时写入 Config 并持久化。
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly Config _cfg;
        private readonly Action<string>? _onHotkeyChanged;

        /// <summary>
        /// 初始化完成标志。XAML 加载期间给 Slider 设 Minimum/Maximum 触发 ValueChanged 时，
        /// 同级控件（StepLabel 等）尚未构造、_cfg 也尚未赋值，直接访问即 NullReferenceException。
        /// 所有"即时生效"处理器都以本标志开头短路。
        /// </summary>
        private bool _ready;

        internal SettingsWindow(Config cfg, Action<string>? onHotkeyChanged = null)
        {
            InitializeComponent();
            _cfg = cfg;
            _onHotkeyChanged = onHotkeyChanged;
            LoadValues();
        }

        /// <summary>标题栏跟随深色主题（见 NativeMethods.UseDarkTitleBar）。</summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            NativeMethods.UseDarkTitleBar(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        }

        private void LoadValues()
        {
            _ready = false;

            // 左侧分类默认落在"常规"：此前没有选中项，导航栏看不出当前在哪一页。
            // 必须在 InitializeComponent 之后设置，否则 SelectionChanged 会先于 g0~g4 创建而空引用。
            if (CatList.SelectedIndex < 0) CatList.SelectedIndex = 0;

            HotkeyBox.Text = _cfg.Hotkey;
            CloseCombo.SelectedIndex = _cfg.CloseToTray ? 0 : 1;
            LangCombo.SelectedIndex = 0;

            StepSlider.Value = (int)Math.Round(_cfg.ScrollStepRatio * 100);
            StepLabel.Text = $"{StepSlider.Value}%";
            if (_cfg.ScrollMethod == "PageDown") PageRb.IsChecked = true; else WheelRb.IsChecked = true;
            WaitSlider.Value = _cfg.WaitMs; WaitLabel.Text = $"{_cfg.WaitMs}";
            StableSlider.Value = _cfg.StableFrames; StableLabel.Text = $"{_cfg.StableFrames}";
            StableIntSlider.Value = _cfg.StableIntervalMs; StableIntLabel.Text = $"{_cfg.StableIntervalMs}";
            CountdownCb.IsChecked = _cfg.CountdownOn;
            CountdownSlider.Value = _cfg.CountdownSec; CountdownLabel.Text = $"{_cfg.CountdownSec}s";
            MaxVolBox.Text = $"{_cfg.MaxVolumes}";
            MaxHBox.Text = $"{_cfg.MaxTotalHeight}";

            BandSlider.Value = (int)Math.Round(_cfg.BandRatio * 100); BandLabel.Text = $"{BandSlider.Value}%";
            MaxVHBox.Text = $"{_cfg.MaxVolumeHeight}";
            OverlapBox.Text = $"{_cfg.VolumeOverlap}";
            CutCb.IsChecked = _cfg.CutPointOn;

            DirBox.Text = _cfg.OutputDir;
            FormatCombo.SelectedIndex = _cfg.OutputFormat.Equals("JPG", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            JpgSlider.Value = _cfg.JpgQuality; JpgLabel.Text = $"{_cfg.JpgQuality}";
            AutoCopyCb.IsChecked = _cfg.AutoCopy;
            if (_cfg.CopyScope == "Current") CopyCurRb.IsChecked = true; else CopyFirstRb.IsChecked = true;
            ApiCombo.SelectedIndex = _cfg.CaptureApi == "WGC" ? 1 : 0;

            _ready = true;
        }

        private void Save()
        {
            if (!_ready) return;
            _cfg.Save();
        }

        private void CatList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var tag = (CatList.SelectedItem as ListBoxItem)?.Tag as string;
            g0.Visibility = tag == "g0" ? Visibility.Visible : Visibility.Collapsed;
            g1.Visibility = tag == "g1" ? Visibility.Visible : Visibility.Collapsed;
            g2.Visibility = tag == "g2" ? Visibility.Visible : Visibility.Collapsed;
            g3.Visibility = tag == "g3" ? Visibility.Visible : Visibility.Collapsed;
            g4.Visibility = tag == "g4" ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- 常规 ----
        private void RebindBtn_Click(object sender, RoutedEventArgs e)
        {
            _cfg.Hotkey = HotkeyBox.Text.Trim();
            Save();
            _onHotkeyChanged?.Invoke(_cfg.Hotkey);
        }
        private void CloseCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready || CloseCombo.SelectedIndex < 0) return;
            _cfg.CloseToTray = CloseCombo.SelectedIndex == 0; Save();
        }

        // ---- 采集 ----
        private void StepSlider_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            StepLabel.Text = $"{(int)StepSlider.Value}%";
            _cfg.ScrollStepRatio = StepSlider.Value / 100.0; Save();
        }
        private void ScrollMethod_Changed(object s, RoutedEventArgs e)
        {
            if (!_ready) return;
            _cfg.ScrollMethod = PageRb.IsChecked == true ? "PageDown" : "Wheel"; Save();
        }
        private void WaitSlider_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            WaitLabel.Text = $"{(int)WaitSlider.Value}"; _cfg.WaitMs = (int)WaitSlider.Value; Save();
        }
        private void StableSlider_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            StableLabel.Text = $"{(int)StableSlider.Value}"; _cfg.StableFrames = (int)StableSlider.Value; Save();
        }
        private void StableIntSlider_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            StableIntLabel.Text = $"{(int)StableIntSlider.Value}"; _cfg.StableIntervalMs = (int)StableIntSlider.Value; Save();
        }
        private void CountdownCb_Changed(object s, RoutedEventArgs e)
        {
            if (!_ready) return;
            _cfg.CountdownOn = CountdownCb.IsChecked == true; Save();
        }
        private void CountdownSlider_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            CountdownLabel.Text = $"{(int)CountdownSlider.Value}s"; _cfg.CountdownSec = (int)CountdownSlider.Value; Save();
        }
        private void MaxVolBox_LostFocus(object s, RoutedEventArgs e)
        {
            if (int.TryParse(MaxVolBox.Text, out int v) && v is >= 10 and <= 1000) { _cfg.MaxVolumes = v; Save(); }
            else MaxVolBox.Text = $"{_cfg.MaxVolumes}";
        }
        private void MaxHBox_LostFocus(object s, RoutedEventArgs e)
        {
            if (int.TryParse(MaxHBox.Text, out int v) && v is >= 10000 and <= 1000000) { _cfg.MaxTotalHeight = v; Save(); }
            else MaxHBox.Text = $"{_cfg.MaxTotalHeight}";
        }

        // ---- 拼接 ----
        private void BandSlider_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            BandLabel.Text = $"{(int)BandSlider.Value}%"; _cfg.BandRatio = BandSlider.Value / 100.0; Save();
        }
        private void MaxVHBox_LostFocus(object s, RoutedEventArgs e)
        {
            if (int.TryParse(MaxVHBox.Text, out int v) && v is >= 2000 and <= 15000) { _cfg.MaxVolumeHeight = v; Save(); }
            else MaxVHBox.Text = $"{_cfg.MaxVolumeHeight}";
        }
        private void OverlapBox_LostFocus(object s, RoutedEventArgs e)
        {
            if (int.TryParse(OverlapBox.Text, out int v) && v is >= 50 and <= 300) { _cfg.VolumeOverlap = v; Save(); }
            else OverlapBox.Text = $"{_cfg.VolumeOverlap}";
        }
        private void CutCb_Changed(object s, RoutedEventArgs e)
        {
            if (!_ready) return;
            _cfg.CutPointOn = CutCb.IsChecked == true; Save();
        }

        // ---- 输出 ----
        private void BrowseBtn_Click(object s, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择保存目录（任意文件后取其所在目录）",
                CheckFileExists = false,
                ValidateNames = false,
                FileName = "选择此目录内的任意文件以定位"
            };
            if (dlg.ShowDialog(this) == true)
            {
                string? dir = Path.GetDirectoryName(dlg.FileName);
                if (!string.IsNullOrEmpty(dir)) { DirBox.Text = dir; _cfg.OutputDir = dir; Save(); }
            }
        }
        private void FormatCombo_Changed(object s, SelectionChangedEventArgs e)
        {
            if (!_ready || FormatCombo.SelectedIndex < 0) return;
            _cfg.OutputFormat = FormatCombo.SelectedIndex == 1 ? "JPG" : "PNG"; Save();
        }
        private void JpgSlider_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            JpgLabel.Text = $"{(int)JpgSlider.Value}"; _cfg.JpgQuality = (int)JpgSlider.Value; Save();
        }
        private void AutoCopyCb_Changed(object s, RoutedEventArgs e)
        {
            if (!_ready) return;
            _cfg.AutoCopy = AutoCopyCb.IsChecked == true; Save();
        }
        private void CopyScope_Changed(object s, RoutedEventArgs e)
        {
            if (!_ready) return;
            _cfg.CopyScope = CopyCurRb.IsChecked == true ? "Current" : "First"; Save();
        }
        private void ApiCombo_Changed(object s, SelectionChangedEventArgs e)
        {
            if (!_ready || ApiCombo.SelectedIndex < 0) return;
            _cfg.CaptureApi = ApiCombo.SelectedIndex == 1 ? "WGC" : "BitBlt"; Save();
        }

        // ---- 高级 ----
        private void RestoreBtn_Click(object s, RoutedEventArgs e)
        {
            var d = new Config();
            _cfg.Hotkey = d.Hotkey; _cfg.CloseToTray = d.CloseToTray;
            _cfg.ScrollStepRatio = d.ScrollStepRatio; _cfg.ScrollMethod = d.ScrollMethod;
            _cfg.WaitMs = d.WaitMs; _cfg.StableFrames = d.StableFrames; _cfg.StableIntervalMs = d.StableIntervalMs;
            _cfg.CountdownOn = d.CountdownOn; _cfg.CountdownSec = d.CountdownSec;
            _cfg.MaxVolumes = d.MaxVolumes; _cfg.MaxTotalHeight = d.MaxTotalHeight;
            _cfg.BandRatio = d.BandRatio; _cfg.MaxVolumeHeight = d.MaxVolumeHeight;
            _cfg.VolumeOverlap = d.VolumeOverlap; _cfg.CutPointOn = d.CutPointOn;
            _cfg.OutputDir = d.OutputDir; _cfg.OutputFormat = d.OutputFormat; _cfg.JpgQuality = d.JpgQuality;
            _cfg.AutoCopy = d.AutoCopy; _cfg.CopyScope = d.CopyScope; _cfg.CaptureApi = d.CaptureApi;
            Save();
            LoadValues();
        }
        private void OpenCfgBtn_Click(object s, RoutedEventArgs e)
        {
            try
            {
                string dir = Path.GetDirectoryName(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScrollShot", "config.json"))!;
                Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
            }
            catch { }
        }

        private void DoneBtn_Click(object s, RoutedEventArgs e) => Close();
    }
}
