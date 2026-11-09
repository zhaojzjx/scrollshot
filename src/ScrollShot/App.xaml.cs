using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace ScrollShot
{
    public partial class App : Application
    {
        private Mutex? _mutex;
        private bool _ownsMutex;
        private MainWindow? _main;
        private TrayHost? _tray;

        /// <summary>第二个实例请已有实例显示主窗口用的跨进程消息（同名注册返回同一个 ID）。</summary>
        internal static readonly uint WM_SHOW_MAIN =
            NativeMethods.RegisterWindowMessage("ScrollShot.ShowMainWindow");

        protected override void OnStartup(StartupEventArgs e)
        {
            // 进入托管代码的第一行就落日志：若日志文件根本没生成，说明问题出在 exe 解压 / 杀软拦截，
            // 而非程序内部（单文件自包含 exe 首次运行需解压原生库）。
            StartupLog.Write("==== App.OnStartup 进入 ====");
            StartupLog.Write($"exe={Environment.ProcessPath}");
            StartupLog.Write($"runtime={Environment.Version} os={Environment.OSVersion}");

            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
                StartupLog.WriteException("AppDomain.Unhandled",
                    ex.ExceptionObject as Exception ?? new Exception(ex.ExceptionObject?.ToString() ?? "unknown"));

            DispatcherUnhandledException += (s, ex) =>
            {
                StartupLog.WriteException("Dispatcher.Unhandled", ex.Exception);
                MessageBox.Show($"发生未处理错误：{ex.Exception.Message}\n\n详情见：{StartupLog.LogPath}",
                    "滚动截图", MessageBoxButton.OK, MessageBoxImage.Error);
                ex.Handled = true;
            };

            try
            {
                // 单实例
                _mutex = new Mutex(true, "ScrollShot_SingleInstance", out bool created);
                _ownsMutex = created;
                StartupLog.Write($"单实例检查: created={created}");
                if (!created)
                {
                    // 已有实例：直接把它叫到前台，而不是只弹一句"已在运行"——
                    // 否则用户会以为双击没反应（窗口可能停留在副屏或托盘）。
                    bool activated = ActivateExistingWindow();
                    StartupLog.Write($"已有实例，尝试激活: {activated}");
                    if (!activated)
                        MessageBox.Show("滚动截图已在运行（可能已最小化到系统托盘，请查看任务栏右下角）。",
                            "ScrollShot", MessageBoxButton.OK, MessageBoxImage.Information);
                    Shutdown();
                    return;
                }

                // 主窗口 + 托盘（托盘为快捷键之外的备用入口）
                _main = new MainWindow();
                StartupLog.Write("MainWindow 构造完成");

                _tray = new TrayHost(
                    onStart: () => _main.TriggerCapture(),
                    onShow: () => _main.ShowAndActivate(),
                    onExit: () => _main.ForceClose(),
                    onSettings: () => _main.OpenSettings(),
                    onStop: () => _main.StopCapture());
                _main.TrayRef = _tray;
                StartupLog.Write("TrayHost 构造完成");

                _tray.Show();      // 隐藏宿主窗口，仅承载托盘图标与消息
                StartupLog.Write("托盘宿主已 Show");

                _main.Show();
                StartupLog.Write("主窗口已 Show");

                _main.Activate();
                StartupLog.Write("主窗口已 Activate");

                base.OnStartup(e);
                StartupLog.Write("==== 启动完成 ====");

                // --select：启动后直接进入框选（便于做快捷方式 / 自动化验证）
                if (e.Args != null && Array.IndexOf(e.Args, "--select") >= 0)
                {
                    StartupLog.Write("命令行参数 --select：直接进入框选");
                    Dispatcher.BeginInvoke(new Action(() => _main.TriggerCapture()));
                }
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("OnStartup 失败", ex);
                MessageBox.Show($"启动失败：{ex.Message}\n\n详情见：{StartupLog.LogPath}",
                    "滚动截图", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        /// <summary>
        /// 找到已运行实例的主窗口，请它自己显示/置前。
        ///
        /// 不能用 ShowWindow 硬显示：主窗口是 WPF Hide() 藏起来的（关闭到托盘 / 采集期间自动隐藏），
        /// Win32 只把 HWND 变可见，WPF 的渲染目标不会重建 —— 表现就是标题栏在、客户区全黑。
        /// 托盘菜单的「打开主窗口」走 ShowAndActivate()（WPF Show）一直正常，这里改成发一条
        /// 自定义消息，让已有实例的 UI 线程走同一条路径。
        /// </summary>
        private static bool ActivateExistingWindow()
        {
            try
            {
                IntPtr h = NativeMethods.FindWindow(null, "滚动截图");
                StartupLog.Write($"FindWindow(\"滚动截图\") = {h}");
                if (h == IntPtr.Zero) return false;

                bool sent = NativeMethods.PostMessage(h, WM_SHOW_MAIN, IntPtr.Zero, IntPtr.Zero);
                StartupLog.Write($"PostMessage(WM_SHOW_MAIN={WM_SHOW_MAIN}) = {sent}");
                return sent;
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("ActivateExistingWindow", ex);
                return false;
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            StartupLog.Write("==== App.OnExit ====");
            _tray?.Close();
            if (_ownsMutex)
            {
                try { _mutex?.ReleaseMutex(); } catch { }
            }
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}
