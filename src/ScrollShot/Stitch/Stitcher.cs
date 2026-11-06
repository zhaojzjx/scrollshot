using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScrollShot.Stitch
{
    /// <summary>
    /// M2 拼接：按匹配得到的"新增行数"逐帧堆叠，重叠部分被自然覆盖，保证卷内无重复行。
    /// 首帧以整帧高度入画；其后每帧贡献 shift 行（内容相对上帧下移 shift 行）。
    /// </summary>
    internal class Stitcher
    {
        private readonly int _width;
        private readonly List<(BitmapSource frame, int contribute)> _items = new();

        public Stitcher(int width) => _width = width;

        public int Count => _items.Count;

        public void Add(BitmapSource frame, int contribute) => _items.Add((frame, contribute));

        public BitmapSource? Render()
        {
            if (_items.Count == 0) return null;

            int total = 0;
            foreach (var it in _items) total += it.contribute;
            if (total < 1) total = 1;

            var wb = new WriteableBitmap(_width, total, 96, 96, PixelFormats.Bgr32, null);
            int y = 0;
            foreach (var it in _items)
            {
                int stride = it.frame.PixelWidth * 4;
                var buf = new byte[stride * it.frame.PixelHeight];
                it.frame.CopyPixels(buf, stride, 0);
                wb.WritePixels(new Int32Rect(0, 0, it.frame.PixelWidth, it.frame.PixelHeight), buf, stride, 0, y);
                y += it.contribute;
            }
            return wb;
        }
    }
}
