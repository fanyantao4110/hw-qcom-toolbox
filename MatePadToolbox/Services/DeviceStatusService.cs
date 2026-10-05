using System.Text.RegularExpressions;

namespace MatePadToolbox.Services
{
    /// <summary>设备当前连接模式。</summary>
    public enum DeviceConnectionMode
    {
        /// <summary>未检测到设备。</summary>
        None,
        /// <summary>ADB 模式（系统已开机并开启 USB 调试）。</summary>
        Adb,
        /// <summary>Fastboot 模式。</summary>
        Fastboot,
        /// <summary>EDL 9008 模式。</summary>
        Edl,
        /// <summary>检测到设备但驱动未正确安装。</summary>
        NoDriver
    }

    /// <summary>一次设备状态探测的结果快照。</summary>
    public sealed record DeviceStatus(
        DeviceConnectionMode Mode,
        string? Model,
        string? OsVersion,
        string? BlLock,
        string? Battery,
        string? Storage,
        int? EdlPort)
    {
        /// <summary>状态显示文案。</summary>
        public string ModeText => Mode switch
        {
            DeviceConnectionMode.Adb => "已连接 (ADB模式)",
            DeviceConnectionMode.Fastboot => "已连接 (Fastboot模式)",
            DeviceConnectionMode.Edl => EdlPort.HasValue
                ? $"已连接 (EDL 9008模式 · COM{EdlPort.Value})"
                : "已连接 (EDL 9008模式)",
            DeviceConnectionMode.NoDriver => "检测到设备但驱动没装，也可能是没打开 USB 调试",
            _ => "未检测到设备 / 请连接设备！"
        };

        /// <summary>是否有设备。</summary>
        public bool HasDevice => Mode != DeviceConnectionMode.None;
    }

    /// <summary>
    /// 设备状态探测服务：判断设备当前处于 ADB / Fastboot / EDL(9008) / 未连接，
    /// 并在 ADB 模式下采集机型、系统版本、BL 锁状态、电量与存储信息。
    /// 采集口径与工具箱 v3.1 一致。
    /// </summary>
    public static partial class DeviceStatusService
    {
        [GeneratedRegex(@"level:\s*(\d+)", RegexOptions.IgnoreCase)]
        private static partial Regex BatteryRegex();

        [GeneratedRegex(@"(\d+(?:\.\d+)?[KMGT])\s+(\d+(?:\.\d+)?[KMGT])\s+(\d+(?:\.\d+)?[KMGT])")]
        private static partial Regex DfRegex();

        /// <summary>
        /// 探测当前设备状态。ADB 模式会额外读取设备信息；其余模式只判断连接模式。
        /// </summary>
        public static async Task<DeviceStatus> DetectAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            // ADB 优先：设备开机时最常用
            if (await DeviceService.HasAdbDeviceAsync())
            {
                return new DeviceStatus(
                    DeviceConnectionMode.Adb,
                    await GetPropAsync("ro.product.model") ?? await GetPropAsync("ro.product.name"),
                    await GetPropAsync("hw_sc.build.platform.version") ?? await GetPropAsync("ro.build.version.harmony"),
                    await ReadBlLockAsync(),
                    await ReadBatteryAsync(),
                    await ReadStorageAsync(),
                    null);
            }

            if (await DeviceService.HasFastbootDeviceAsync())
            {
                return new DeviceStatus(DeviceConnectionMode.Fastboot, null, null, null, null, null, null);
            }

            var port = ComPortDetector.Find9008Port();
            if (port.HasValue)
            {
                return new DeviceStatus(DeviceConnectionMode.Edl, null, null, null, null, null, port);
            }

            // 插着线但三种模式都识别不了：可能是驱动没装，也可能是系统里没打开 USB 调试
            return new DeviceStatus(HasUnknownUsbDevice() ? DeviceConnectionMode.NoDriver : DeviceConnectionMode.None,
                null, null, null, null, null, null);
        }

        /// <summary>读取单个系统属性，失败返回 null。</summary>
        private static async Task<string?> GetPropAsync(string prop)
        {
            try
            {
                var (code, output) = await ProcessRunner.RunCaptureAsync(AppConfig.AdbExe, $"shell getprop {prop}");
                if (code != 0) return null;
                var v = output.Trim();
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>判定 BL 锁状态：已解锁 / 已锁定 / 未知。</summary>
        private static async Task<string> ReadBlLockAsync()
        {
            var locked = await GetPropAsync("ro.boot.flash.locked");
            if (!string.IsNullOrWhiteSpace(locked))
            {
                return locked == "0" ? "已解锁" : "已锁定";
            }

            var state = await GetPropAsync("ro.boot.vbmeta.device_state");
            if (!string.IsNullOrWhiteSpace(state))
            {
                return state.Equals("unlocked", StringComparison.OrdinalIgnoreCase) ? "已解锁" : "已锁定";
            }
            return "未知";
        }

        /// <summary>读取电量百分比。</summary>
        private static async Task<string?> ReadBatteryAsync()
        {
            try
            {
                var (code, output) = await ProcessRunner.RunCaptureAsync(AppConfig.AdbExe, "shell dumpsys battery");
                if (code != 0) return null;
                var m = BatteryRegex().Match(output);
                return m.Success ? m.Groups[1].Value + "%" : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>读取 /data 分区可用空间与总空间。</summary>
        private static async Task<string?> ReadStorageAsync()
        {
            try
            {
                var (code, output) = await ProcessRunner.RunCaptureAsync(AppConfig.AdbExe, "shell df -h /data");
                if (code != 0) return null;

                foreach (var line in output.Split('\n'))
                {
                    var m = DfRegex().Match(line);
                    if (m.Success)
                    {
                        // df -h 输出列：容量 已用 可用
                        return $"{m.Groups[3].Value} / {m.Groups[1].Value}";
                    }
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 粗判是否插入了 USB 设备但驱动异常（ADB/Fastboot/9008 都识别不到时调用）。
        /// 只要 WMI 里存在带 VID 的未知/异常设备就认为是驱动问题。
        /// </summary>
        private static bool HasUnknownUsbDevice()
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT Name, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE DeviceID LIKE '%USB%'");
                foreach (var obj in searcher.Get())
                {
                    var name = obj["Name"]?.ToString() ?? string.Empty;
                    if (name.Contains("Huawei", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Android", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("QDLoader", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("9008", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // WMI 不可用时不臆断
            }
            return false;
        }
    }
}
