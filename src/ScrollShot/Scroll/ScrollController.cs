using System;
using System.Drawing;
using System.Runtime.InteropServices;
using ScrollShot;

namespace ScrollShot.Scroll
{
    /// <summary>
    /// M1 滚动控制：
    ///  - WindowAt / Focus：定位并把光标所在的选区下方窗口置前；
    ///  - SendCtrlHome：先滚到顶部（发送 Ctrl+Home）；
    ///  - WheelPost：用 PostMessage 直接向目标窗口投递滚轮事件，
    ///    不依赖光标位置，也顺带规避"光标出现在截图中"（FR-14，M1 近似实现）。
    /// 真正的 SendInput 前台方案与 PageDown 模式在 M2 完善。
    /// </summary>
    internal static class ScrollController
    {
        /// <summary>
        /// 选区中心下的窗口。WindowFromPoint 返回的常是子控件（如编辑区），
        /// 而 SetForegroundWindow 只对顶层窗口有效，故统一提升到根窗口。
        /// </summary>
        public static IntPtr WindowAt(int xDev, int yDev)
        {
            IntPtr h = NativeMethods.WindowFromPoint(new Point(xDev, yDev));
            if (h == IntPtr.Zero) return h;
            IntPtr root = NativeMethods.GetAncestor(h, 2); // GA_ROOT
            return root != IntPtr.Zero ? root : h;
        }

        public static void Focus(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;

            // 后台进程直接 SetForegroundWindow 常被系统前台锁策略拒绝，
            // 与当前前台线程 AttachThreadInput 后可以稳定抢到焦点（否则滚轮/按键发不到目标应用）。
            try
            {
                IntPtr fg = NativeMethods.GetForegroundWindow();
                uint fgThread = NativeMethods.GetWindowThreadProcessId(fg, IntPtr.Zero);
                uint curThread = NativeMethods.GetCurrentThreadId();
                bool attached = fgThread != 0 && fgThread != curThread &&
                                NativeMethods.AttachThreadInput(curThread, fgThread, true);
                NativeMethods.SetForegroundWindow(hwnd);
                if (attached) NativeMethods.AttachThreadInput(curThread, fgThread, false);
            }
            catch
            {
                NativeMethods.SetForegroundWindow(hwnd);
            }
        }

        public static void SendCtrlHome()
        {
            var inputs = new[]
            {
                KeyDown(0x11), // VK_CONTROL
                KeyDown(0x24), // VK_HOME
                KeyUp(0x24),
                KeyUp(0x11),
            };
            NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        }

        /// <summary>
        /// 向选区中心发送真实滚轮事件（FR-12）。
        ///
        /// 为什么不用 PostMessage 投 WM_MOUSEWHEEL：现代应用（记事本、UWP、Electron/Chromium 等）
        /// 普遍只响应真实的输入事件，投递的消息会被直接忽略 —— 表现为"采集只出 1 张"。
        /// SendInput 产生的滚轮事件由系统按"光标下窗口"路由，因此必须先把光标放回选区内。
        /// 截图前再把光标移出选区（FR-14），两步顺序见 CaptureSession。
        /// </summary>
        public static void WheelAt(int xDev, int yDev, int delta)
        {
            NativeMethods.SetCursorPos(xDev, yDev);
            var inputs = new[]
            {
                new NativeMethods.INPUT
                {
                    type = NativeMethods.INPUT_MOUSE,
                    u = new NativeMethods.InputUnion
                    {
                        mi = new NativeMethods.MOUSEINPUT
                        {
                            dwFlags = NativeMethods.MOUSEEVENTF_WHEEL,
                            mouseData = unchecked((uint)delta)
                        }
                    }
                }
            };
            NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        }

        public static void WheelPost(IntPtr hwnd, int xDev, int yDev, int delta)
        {
            if (hwnd == IntPtr.Zero) return;
            IntPtr wp = (IntPtr)(delta << 16);
            IntPtr lp = (IntPtr)((yDev << 16) | (xDev & 0xFFFF));
            NativeMethods.PostMessage(hwnd, NativeMethods.WM_MOUSEWHEEL, wp, lp);
        }

        /// <summary>PageDown 模式：发送 PageDown 键滚动一屏（FR-12，兼容滚轮无效的应用）。</summary>
        public static void PageDown()
        {
            var inputs = new[]
            {
                KeyDown(0x22), // VK_NEXT = PageDown
                KeyUp(0x22),
            };
            NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        }

        private static NativeMethods.INPUT KeyDown(ushort vk) => new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            u = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wVk = vk } }
        };

        private static NativeMethods.INPUT KeyUp(ushort vk) => new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            u = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wVk = vk, dwFlags = NativeMethods.KEYEVENTF_KEYUP } }
        };
    }
}
