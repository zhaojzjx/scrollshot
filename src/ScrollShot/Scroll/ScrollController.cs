using System;
using System.Drawing;
using System.Runtime.InteropServices;
using ScrollShot;

namespace ScrollShot.Scroll
{
    /// <summary>
    /// 滚动控制：
    ///  - WindowAt / Focus：定位光标下的顶层窗口并置前，滚轮与按键才能送到它身上；
    ///  - SendCtrlHome：先把内容滚回顶部；
    ///  - Wheel：发送**真实**滚轮事件（不搬光标，是否借用光标由 CaptureSession 决定）；
    ///  - PageDown：滚轮无效的应用改用 PageDown 翻页；
    ///  - Cursor / CursorOnWindow：判断用户光标位置，尽量一次都不去动它。
    ///
    /// 这里刻意不用 PostMessage 投 WM_MOUSEWHEEL：现代应用（记事本、UWP、Chromium/Electron）
    /// 普遍只响应真实输入事件，投递的消息会被直接忽略，表现为"采集只出 1 张"。
    /// </summary>
    internal static class ScrollController
    {
        /// <summary>
        /// 选区中心下的窗口。WindowFromPoint 返回的常是子控件（如编辑区），
        /// 而 SetForegroundWindow 只对顶层窗口有效，故统一提升到根窗口。
        /// </summary>
        public static IntPtr WindowAt(int xDev, int yDev)
        {
            IntPtr h = RawWindowAt(xDev, yDev);
            if (h == IntPtr.Zero) return h;
            IntPtr root = NativeMethods.GetAncestor(h, 2); // GA_ROOT
            return root != IntPtr.Zero ? root : h;
        }

        /// <summary>选区中心下的**实际**窗口（未提升到顶层），用于判断滚轮会落到哪个控件。</summary>
        public static IntPtr RawWindowAt(int xDev, int yDev) =>
            NativeMethods.WindowFromPoint(new Point(xDev, yDev));

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

        /// <summary>读取用户当前光标位置（设备像素）。失败时返回 (-1,-1)。</summary>
        public static Point Cursor() => NativeMethods.GetCursorPos(out var p) ? p : new Point(-1, -1);

        /// <summary>
        /// 用户光标下的窗口是否与选区中心下的窗口一致（滚轮会落到同一个控件）。
        /// 不能只比顶层窗口：VS 里鼠标放在解决方案资源管理器上、代码在编辑器里，
        /// 二者同属一个主窗口，滚轮却会滚错控件，画面不动会被误判成"到底"。
        /// </summary>
        public static bool CursorOnWindow(Point pt, IntPtr centerWnd)
        {
            if (centerWnd == IntPtr.Zero || pt.X < 0 || pt.Y < 0) return false;
            IntPtr h = NativeMethods.WindowFromPoint(pt);
            if (h == IntPtr.Zero) return false;
            if (h == centerWnd) return true;
            IntPtr root = NativeMethods.GetAncestor(h, 2); // GA_ROOT
            return root != IntPtr.Zero && root == centerWnd;
        }

        /// <summary>
        /// 发送真实滚轮事件，**不碰光标**。
        ///
        /// 为什么不用 PostMessage 投 WM_MOUSEWHEEL：现代应用（记事本、UWP、Electron/Chromium 等）
        /// 普遍只响应真实的输入事件，投递的消息会被直接忽略 —— 表现为"采集只出 1 张"。
        /// 是否需要在发事件前临时把光标借到选区中心，由 CaptureSession 按用户当前光标位置决定；
        /// 旧版本在本方法里无条件 SetCursorPos，并在每帧结束时把光标丢到固定停放点，
        /// 用户鼠标会被持续抢走，连采集胶囊都点不到。
        /// </summary>
        public static void Wheel(int delta)
        {
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

        /// <summary>PageDown 模式：发送 PageDown 键滚动一屏（兼容滚轮无效的应用）。</summary>
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
