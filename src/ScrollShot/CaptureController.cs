namespace ScrollShot
{
    /// <summary>
    /// 采集控制标志：由胶囊 UI 写入，采集循环读取。
    /// </summary>
    public class CaptureController
    {
        public volatile bool PauseRequested;
        public volatile bool StopRequested;
        public volatile bool DiscardRequested; // ✕ 停止并丢弃：丢弃当前未封板卷，已封板卷保留
    }
}
