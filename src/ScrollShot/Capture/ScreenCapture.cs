using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScrollShot;

namespace ScrollShot.Capture
{
    /// <summary>
    /// 截屏提供器接口：输入设备像素矩形，返回 Bgr32 的 BitmapSource。
    /// M2 先用 BitBlt；Windows.Graphics.Capture（WGC）作为 M2.5 提供器接入此接口。
    /// </summary>
    internal interface ICaptureProvider
    {
        BitmapSource? Capture(int x, int y, int w, int h);
    }

    /// <summary>
    /// BitBlt 截屏提供器：可截 GPU 直绘/远程桌面等场景会黑屏（风险已记），
    /// 后续 WGC 提供器负责兜底。
    /// </summary>
    internal class BitBltCapture : ICaptureProvider
    {
        public BitmapSource? Capture(int x, int y, int w, int h)
        {
            if (w <= 0 || h <= 0) return null;

            IntPtr hdcSrc = NativeMethods.GetDC(IntPtr.Zero);
            if (hdcSrc == IntPtr.Zero) return null;

            IntPtr hdcDest = NativeMethods.CreateCompatibleDC(hdcSrc);
            IntPtr hBmp = NativeMethods.CreateCompatibleBitmap(hdcSrc, w, h);
            IntPtr hOld = NativeMethods.SelectObject(hdcDest, hBmp);
            bool ok = NativeMethods.BitBlt(hdcDest, 0, 0, w, h, hdcSrc, x, y, NativeMethods.SRCCOPY);
            NativeMethods.SelectObject(hdcDest, hOld);
            NativeMethods.DeleteDC(hdcDest);
            NativeMethods.ReleaseDC(IntPtr.Zero, hdcSrc);

            if (!ok)
            {
                NativeMethods.DeleteObject(hBmp);
                return null;
            }

            BitmapSource src = Imaging.CreateBitmapSourceFromHBitmap(
                hBmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            NativeMethods.DeleteObject(hBmp); // 源已拷贝，删除 GDI 位图

            // 统一规整为 Bgr32，便于拼接按 4 字节/像素寻址
            return new FormatConvertedBitmap(src, PixelFormats.Bgr32, null, 0);
        }
    }
}
