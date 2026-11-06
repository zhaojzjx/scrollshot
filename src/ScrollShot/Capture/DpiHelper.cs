using System;
using System.Drawing;
using ScrollShot;

namespace ScrollShot.Capture
{
    /// <summary>
    /// DPI 换算：把 WPF 的 DIP 坐标转换为 BitBlt 需要的设备像素坐标。
    /// M1 仅按主屏 DPI 计算（混合 DPI 由 M3 处理）。
    /// </summary>
    internal static class DpiHelper
    {
        public static double GetScale()
        {
            IntPtr mon = NativeMethods.MonitorFromPoint(new Point(0, 0), 1); // MONITOR_DEFAULTTOPRIMARY
            if (mon == IntPtr.Zero) return 1.0;
            if (NativeMethods.GetDpiForMonitor(mon, 0, out uint dx, out uint dy) == 0)
                return dx / 96.0;
            return 1.0;
        }
    }
}
