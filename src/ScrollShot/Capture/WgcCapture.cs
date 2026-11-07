using System.Windows.Media.Imaging;
using ScrollShot;

namespace ScrollShot.Capture
{
    /// <summary>
    /// WGC（Windows.Graphics.Capture）截屏提供器 —— 预留接口，尚未实装。
    ///
    /// WGC 需要 D3D11 设备互操作（Direct3D11CaptureFramePool + IDirect3DDevice）。
    /// 目前 Capture() 恒返回 null，由 CaptureSession 自动回退到 BitBlt。
    /// 后续接入需启用 EnableWindowsTargeting 并实现 D3D11 互操作；
    /// 初始化失败同样回退 BitBlt，不影响主链路。
    /// </summary>
    internal class WgcCapture : ICaptureProvider
    {
        public BitmapSource? Capture(int x, int y, int w, int h) => null; // 回退到 BitBlt
    }
}
