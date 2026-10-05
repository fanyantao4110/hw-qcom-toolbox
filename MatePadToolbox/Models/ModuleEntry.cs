namespace MatePadToolbox.Models
{
    /// <summary>
    /// 优化模块条目：对应 optimize_modules.json 中一条模块。
    /// File 为打包文件名（英文），用于与 modules 目录中 zip 对照安装。
    /// DownloadType 决定下载方式：direct=直链（程序直接下载 zip）；netdisk=跳转网盘（复制链接到剪贴板并自动打开浏览器）。
    /// </summary>
    public class ModuleEntry
    {
        /// <summary>模块显示名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>模块文件英文名（如 optimization.zip），与 modules 目录中的 zip 对照。</summary>
        public string File { get; set; } = string.Empty;

        /// <summary>下载方式：direct（直链，默认）或 netdisk（跳转网盘）。</summary>
        public string DownloadType { get; set; } = string.Empty;

        /// <summary>模块下载地址（直链时为 zip 直链；跳转网盘时为网盘分享链接）。</summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>模块说明文字（可选）。</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>是否为跳转网盘方式。</summary>
        public bool IsNetdisk => DownloadType.Equals("netdisk", StringComparison.OrdinalIgnoreCase);

        /// <summary>是否为直链方式（缺省视为直链）。</summary>
        public bool IsDirect => !IsNetdisk;
    }
}
