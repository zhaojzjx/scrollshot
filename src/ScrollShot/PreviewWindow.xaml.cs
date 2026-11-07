using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScrollShot
{
    /// <summary>
    /// 结果预览窗：左预览（默认适应窗口宽度）+ 右卷列表导航 + 复制/打开文件夹/丢弃。
    /// 接收多卷文件列表，逐卷浏览。
    /// </summary>
    public partial class PreviewWindow : Window
    {
        private readonly List<string> _files = new();
        private readonly string _outDir;
        private int _current = 0;

        public PreviewWindow(IReadOnlyList<string> files, string summary, int warnCount, string outDir)
        {
            InitializeComponent();
            _files.Clear();
            _files.AddRange(files);
            _outDir = outDir;
            Title = $"滚动截图 · 预览 · 共 {files.Count} 张";

            ZoomCombo.SelectedIndex = 0;
            if (warnCount > 0)
            {
                WarnBadge.Visibility = Visibility.Visible;
                WarnBadge.Text = $"⚠ {warnCount} 处告警";
            }

            BuildList();
            if (_files.Count > 0) Show(0);
            else StatusText.Text = summary;
            Sv.SizeChanged += (s, e) => ApplyZoom();
        }

        /// <summary>标题栏跟随深色主题（见 NativeMethods.UseDarkTitleBar）。</summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            NativeMethods.UseDarkTitleBar(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        }

        private void BuildList()
        {
            List.Items.Clear();
            for (int i = 0; i < _files.Count; i++)
            {
                string p = _files[i];
                string dims = "";
                long kb = 0;
                try
                {
                    var dec = BitmapDecoder.Create(new Uri(p), BitmapCreateOptions.None, BitmapCacheOption.None);
                    var f = dec.Frames[0];
                    dims = $"{f.PixelWidth}×{f.PixelHeight}";
                    kb = new FileInfo(p).Length / 1024;
                }
                catch { }
                var item = new ListBoxItem
                {
                    Tag = p,
                    Foreground = System.Windows.Media.Brushes.White,
                    Padding = new Thickness(6, 4, 6, 4)
                };
                item.Content = $"{i + 1:D2}  {dims}  {kb}KB";
                List.Items.Add(item);
            }
        }

        private void Show(int idx)
        {
            if (idx < 0 || idx >= _files.Count) return;
            _current = idx;
            try
            {
                var img = BitmapFrame.Create(new Uri(_files[idx]));
                Img.Source = img;
                List.SelectedIndex = idx;
                ApplyZoom();
                var dec = BitmapDecoder.Create(new Uri(_files[idx]), BitmapCreateOptions.None, BitmapCacheOption.None);
                var f = dec.Frames[0];
                StatusText.Text = $"第 {idx + 1} / {_files.Count} 张 · {f.PixelWidth} × {f.PixelHeight} px · 保存于 {_outDir}";
            }
            catch { }
        }

        private void ApplyZoom()
        {
            if (Img.Source == null) return;
            int mode = ZoomCombo.SelectedIndex;
            if (mode == 1) // 100%
            {
                Vb.Stretch = Stretch.None;
                Vb.Width = double.NaN; Vb.Height = double.NaN;
                Sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                Sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            }
            else
            {
                Vb.Stretch = Stretch.Uniform;
                Vb.Width = Sv.ActualWidth;
                Sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                if (mode == 2) // 适应窗口
                {
                    Vb.Height = Sv.ActualHeight;
                    Sv.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                }
                else // 适应窗口宽度
                {
                    Vb.Height = double.NaN;
                    Sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                }
            }
        }

        private void ZoomCombo_SelectionChanged(object s, SelectionChangedEventArgs e) => ApplyZoom();

        private void List_SelectionChanged(object s, SelectionChangedEventArgs e)
        {
            if (List.SelectedItem is ListBoxItem it && it.Tag is string p)
                Show(_files.IndexOf(p));
        }

        private void CopyCurBtn_Click(object s, RoutedEventArgs e)
        {
            if (_current < 0 || _current >= _files.Count) return;
            try
            {
                var img = BitmapFrame.Create(new Uri(_files[_current]));
                Clipboard.SetImage(img);
            }
            catch { }
        }

        private void CopyAllBtn_Click(object s, RoutedEventArgs e)
        {
            try
            {
                var col = new StringCollection();
                foreach (var p in _files) col.Add(p);
                Clipboard.SetFileDropList(col);
            }
            catch { }
        }

        private void FolderBtn_Click(object s, RoutedEventArgs e)
        {
            if (Directory.Exists(_outDir))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_files[0]}\"") { UseShellExecute = true });
        }

        private void DiscardBtn_Click(object s, RoutedEventArgs e)
        {
            var d = MessageBox.Show("确定丢弃本次全部卷文件？此操作不可撤销。",
                "丢弃结果", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (d != MessageBoxResult.OK) return;
            foreach (var p in _files) { try { File.Delete(p); } catch { } }
            Close();
        }
    }
}
