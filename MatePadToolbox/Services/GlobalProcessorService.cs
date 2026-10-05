using System.Collections.Generic;
using System.IO;
using MatePadToolbox.Models;

namespace MatePadToolbox.Services
{
    /// <summary>
    /// 全局处理器选择状态：在侧边栏顶部选择的 CPU 型号，供解锁BL、9008备份、
    /// 高级工具箱发送引导等功能统一使用。型号数据来自 unlock\processors.json。
    /// </summary>
    public static class GlobalProcessorService
    {
        private static ProcessorEntry? _current;

        /// <summary>当前全局选中的处理器（未选择时为 null，回退到默认骁龙865）。</summary>
        public static ProcessorEntry? Current
        {
            get => _current;
            set => _current = value;
        }

        /// <summary>当前选中处理器的显示名称。</summary>
        public static string DisplayName =>
            _current == null ? string.Empty : _current.ToString();

        /// <summary>
        /// 当前选中处理器的 firehose 引导文件完整路径。
        /// 选中了型号且配置了 Devprg 时精确返回该型号路径（是否缺失由调用方检查并提示）；
        /// 仅当未选中或未配置字段时才回退默认骁龙865文件。
        /// </summary>
        public static string DevprgPath
        {
            get
            {
                if (_current != null && !string.IsNullOrWhiteSpace(_current.Devprg))
                {
                    return Path.Combine(AppConfig.UnlockDir, Path.GetFileName(_current.Devprg));
                }
                return AppConfig.DevprgElf;
            }
        }

        /// <summary>
        /// 当前选中处理器的 ABL 解锁镜像完整路径。
        /// 选中了型号且配置了 AblUnlock 时精确返回该型号镜像（是否缺失由调用方检查并提示）；
        /// 仅当未选中或未配置字段时才回退默认骁龙865镜像。
        /// </summary>
        public static string AblUnlockPath
        {
            get
            {
                if (_current != null && !string.IsNullOrWhiteSpace(_current.AblUnlock))
                {
                    return Path.Combine(AppConfig.UnlockDir, Path.GetFileName(_current.AblUnlock));
                }
                return AppConfig.AblUnlockImg;
            }
        }

        /// <summary>当前选中处理器的 ABL 解锁镜像文件名（不含路径）。</summary>
        public static string AblUnlockFileName =>
            _current != null && !string.IsNullOrWhiteSpace(_current.AblUnlock)
                ? Path.GetFileName(_current.AblUnlock)
                : Path.GetFileName(AppConfig.AblUnlockImg);

        /// <summary>加载全部处理器列表（优先 read unlock\processors.json）。</summary>
        public static List<ProcessorEntry> LoadProcessors(Action<string> log) =>
            AdvancedToolService.LoadProcessors(log);
    }
}
