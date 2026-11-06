using System;
using System.IO;
using System.Text.Json;

namespace ScrollShot
{
    /// <summary>
    /// 本地配置（%AppData%\ScrollShot\config.json）：不含任何图像数据或历史索引（FR-49）。
    /// 覆盖需求 FR-40~FR-49 全部设置项；设置窗口的改动即时写回此对象并持久化。
    /// </summary>
    internal class Config
    {
        // ---- 常规（FR-40 / UI-34） ----
        public string Hotkey { get; set; } = "Ctrl+Alt+S";
        public bool CloseToTray { get; set; } = true;     // UI-09：关闭主窗口最小化到托盘
        public string Language { get; set; } = "简体中文"; // 预留，本期仅中文

        // ---- 采集（FR-41/42/43/47/48） ----
        public double ScrollStepRatio { get; set; } = 0.70; // 每次前进占选区高度比例（50%–85%）
        public string ScrollMethod { get; set; } = "Wheel";  // Wheel | PageDown
        public int WaitMs { get; set; } = 300;               // 滚动后等待（100–1000）
        public int StableFrames { get; set; } = 3;           // 稳定帧数（FR-13）
        public int StableIntervalMs { get; set; } = 100;     // 稳定采样间隔
        public bool CountdownOn { get; set; } = false;        // FR-47 倒计时（默认关闭：框选完直接截图）
        public int CountdownSec { get; set; } = 3;           // FR-47 秒数
        public int MaxVolumes { get; set; } = 300;           // FR-48 最大卷数
        public int MaxTotalHeight { get; set; } = 300000;    // FR-48 累计总高度（≈15000 行）

        // ---- 拼接（FR-21/42a/42b/42c） ----
        public double BandRatio { get; set; } = 0.70;        // FR-21 中部带状匹配占比
        public int MaxVolumeHeight { get; set; } = 6000;     // FR-42a 单张最大高度（2000–15000）
        public int VolumeOverlap { get; set; } = 100;        // FR-42b 卷间重叠像素（50–300）
        public bool CutPointOn { get; set; } = true;         // FR-42c 切点优化

        // ---- 输出（FR-30/35/44/45） ----
        public string OutputDir { get; set; } = DefaultOutputDir;

        /// <summary>默认保存目录：图片\滚动截图（FR-44）。</summary>
        public static string DefaultOutputDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "滚动截图");
        public string OutputFormat { get; set; } = "PNG";    // PNG | JPG
        public int JpgQuality { get; set; } = 90;            // JPG 质量（FR-35）
        public bool AutoCopy { get; set; } = true;           // FR-45 自动复制
        public string CopyScope { get; set; } = "First";     // First | Current
        public string CaptureApi { get; set; } = "BitBlt";   // BitBlt | WGC

        // ---- 版本（配置迁移用） ----
        /// <summary>配置结构版本。0 表示旧版配置（无本字段）。</summary>
        public int ConfigVersion { get; set; }
        private const int CurrentVersion = 2;

        private static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScrollShot", "config.json");

        public static Config Load()
        {
            Config c = new Config();
            try
            {
                if (File.Exists(FilePath))
                {
                    var loaded = JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath));
                    if (loaded != null) c = loaded;
                }
            }
            catch { }

            // v2：移除"沿用上次区域"，并把倒计时改为默认关闭（框选完直接截图）
            if (c.ConfigVersion < CurrentVersion)
            {
                c.CountdownOn = false;
                c.ConfigVersion = CurrentVersion;
                try { c.Save(); } catch { }
            }
            return c;
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            ConfigVersion = CurrentVersion;
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
