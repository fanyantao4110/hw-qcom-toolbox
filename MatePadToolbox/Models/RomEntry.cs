namespace MatePadToolbox.Models
{
    /// <summary>
    /// 第三方系统下载条目（对应 Gitee 上 JSON 配置中的一条记录）。
    /// </summary>
    public class RomEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;

        public override string ToString() => Name;
    }

    /// <summary>
    /// 机型条目：对应合并配置 system_config.json 中的一台设备，
    /// 其 Systems 列出该机型可刷的所有系统（不再按系统版本拆分）。
    /// </summary>
    public class DeviceEntry
    {
        public string Name { get; set; } = string.Empty;
        public List<RomEntry> Systems { get; set; } = new();
    }

    /// <summary>
    /// 刷机结果统计。
    /// </summary>
    public class FlashSummary
    {
        public int SuccessCount { get; set; }
        public List<string> FailedParts { get; set; } = new();
        public bool AllSuccess => FailedParts.Count == 0;
    }
}
