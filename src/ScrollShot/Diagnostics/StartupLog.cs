using System;
using System.IO;
using System.Text;

namespace ScrollShot
{
    /// <summary>
    /// 启动诊断日志：只记元信息（时间、阶段、窗口位置），不记录任何图像内容。
    /// 文件：%AppData%\ScrollShot\startup.log
    /// 用途：双击后"没有反应"这类静默失败，靠它定位卡在哪一步。
    /// </summary>
    internal static class StartupLog
    {
        private static readonly object _lock = new();
        private static string? _path;

        public static string LogPath
        {
            get
            {
                if (_path != null) return _path;
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScrollShot");
                Directory.CreateDirectory(dir);
                _path = Path.Combine(dir, "startup.log");
                return _path;
            }
        }

        public static void Write(string msg)
        {
            try
            {
                lock (_lock)
                {
                    File.AppendAllText(LogPath,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}{Environment.NewLine}",
                        Encoding.UTF8);
                }
            }
            catch { /* 日志失败绝不能影响主流程 */ }
        }

        public static void WriteException(string tag, Exception ex)
        {
            Write($"{tag}: {ex.GetType().Name}: {ex.Message}");
            Write(ex.StackTrace ?? "(no stack)");
            if (ex.InnerException != null)
                Write($"  inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        }
    }
}
