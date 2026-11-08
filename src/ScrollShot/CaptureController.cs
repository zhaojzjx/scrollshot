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

        /// <summary>
        /// 鼠标正悬停在胶囊上（用户准备按暂停/停止）。为 true 时采集循环完全让开鼠标：
        /// 不搬光标、不滚动、不截帧，否则每帧的 SetCursorPos 会把指针拉走，按钮根本点不到。
        /// </summary>
        public volatile bool PointerOnCapsule;
    }
}
