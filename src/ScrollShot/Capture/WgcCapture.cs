using System.Windows.Media.Imaging;
using ScrollShot;

namespace ScrollShot.Capture
{
    /// <summary>
    /// WGC（Windows.Graphics.Capture）截屏提供器 —— 计划中、本期未实装。
    ///
    /// 说明：WGC 需要 D3D11 设备互操作（Direct3D11CaptureFramePool + IDirect3DDevice），
    /// 只能在装有 GPU 窗口的真机上调试验证，无法在当前无显示环境验证，故本期不实装，
    /// 选择 WGC 时由 CaptureSession 自动回退到 BitBlt（NFR-05 的兜底路径）。
    /// 后续接入：EnableWindowsTargeting + Windows.Graphics.Capture + D3D11 互操作，
    /// 初始化失败同样回退 BitBlt，不影响主链路。
    /// </summary>
    internal class WgcCapture : ICaptureProvider
    {
        public BitmapSource? Capture(int x, int y, int w, int h) => null; // 回退到 BitBlt
    }
}
