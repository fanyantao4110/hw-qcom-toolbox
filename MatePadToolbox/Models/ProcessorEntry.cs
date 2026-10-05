namespace MatePadToolbox.Models
{
    /// <summary>
    /// 处理器型号条目：定义某一处理器的引导（firehose）文件与 ABL 解锁镜像。
    /// 通过 unlock\processors.json 配置，字段与 json 中一致。
    /// </summary>
    public class ProcessorEntry
    {
        /// <summary>处理器显示名称（如 骁龙865）。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>芯片型号说明（如 SM8250 / 骁龙865），仅用于展示。</summary>
        public string Chip { get; set; } = string.Empty;

        /// <summary>firehose 引导编程器文件名（unlock 目录下的 .elf）。</summary>
        public string Devprg { get; set; } = string.Empty;

        /// <summary>ABL 解锁镜像文件名（unlock 目录下的 .img，解锁BL时刷入此镜像）。</summary>
        public string AblUnlock { get; set; } = string.Empty;

        /// <summary>处理器说明文字（可选）。</summary>
        public string Description { get; set; } = string.Empty;

        public override string ToString() =>
            string.IsNullOrEmpty(Chip) ? Name : $"{Name}（{Chip}）";
    }
}
