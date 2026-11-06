using System;
using System.Threading;
using System.Windows.Media.Imaging;
using ScrollShot.Capture;
using ScrollShot.Stitch;

namespace ScrollShot.Capture
{
    /// <summary>
    /// 稳定帧检测（FR-13）：滚动后连续采样，直到画面基本静止才认为可以截图，
    /// 兼容平滑滚动 / 惯性滚动，避免抓拍时机不准导致的重影、半屏拼接。
    ///
    /// 判稳用「差异像素比例」而不是整帧哈希：编辑器插入光标会不停闪烁，
    /// 哈希永远不相等，会导致每帧都等满采样上限（实测 4 秒/帧，上万行根本跑不完）。
    /// </summary>
    internal static class StabilityDetector
    {
        /// <summary>差异像素比例低于此值即视为"画面没变"（≈0.2%，足够覆盖光标闪烁/抗锯齿抖动）。</summary>
        public const double SameFrameRatio = 0.002;

        public static BitmapSource? WaitStable(ICaptureProvider cap, int x, int y, int w, int h,
            int stableFrames = 3, int intervalMs = 100, int maxSamples = 25)
        {
            BitmapSource? last = null;
            BitmapSource? sample = null;
            int same = 0;

            for (int i = 0; i < maxSamples; i++)
            {
                sample = cap.Capture(x, y, w, h);
                if (sample == null) break;

                if (last != null && ImageUtil.DiffRatio(last, sample) < SameFrameRatio)
                {
                    if (++same >= stableFrames) return sample;
                }
                else
                {
                    same = 0;
                }

                last = sample;
                if (i < maxSamples - 1) Thread.Sleep(intervalMs);
            }
            return sample;
        }
    }
}
