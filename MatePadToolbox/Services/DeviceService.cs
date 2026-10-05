using System.IO;
using MatePadToolbox.Models;

namespace MatePadToolbox.Services
{
    /// <summary>
    /// Fastboot 模式操作服务：设备检测、分区刷写、重启。
    /// 对应 bat 中的 fastboot 命令与 :wait_fastboot_device / :wait_adb_device 子过程。
    /// </summary>
    public static class DeviceService
    {
        /// <summary>
        /// 检查是否存在 ADB 设备（输出中包含以 device 结尾的行）。
        /// </summary>
        public static async Task<bool> HasAdbDeviceAsync()
        {
            var (code, output) = await ProcessRunner.RunCaptureAsync(AppConfig.AdbExe, "devices");
            if (code != 0) return false;
            return output.Split('\n')
                .Select(l => l.Trim())
                .Any(l => l.EndsWith("device") && !l.StartsWith("List"));
        }

        /// <summary>
        /// 检查是否存在 Fastboot 设备。
        /// </summary>
        public static async Task<bool> HasFastbootDeviceAsync()
        {
            var (code, output) = await ProcessRunner.RunCaptureAsync(AppConfig.FastbootExe, "devices");
            if (code != 0) return false;
            return output.Contains("fastboot", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 等待 ADB 设备连接（循环检测，用户可随时取消）。
        /// </summary>
        public static async Task<bool> WaitForAdbAsync(Action<string> log, CancellationToken ct = default)
        {
            log("正在检测 ADB 设备...");
            while (!ct.IsCancellationRequested)
            {
                if (await HasAdbDeviceAsync())
                {
                    log("[OK] ADB 设备已连接。");
                    return true;
                }
                log("[提示] 未检测到 ADB 设备，请开启 USB 调试并连接后重试（3 秒后自动重试）...");
                try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { break; }
            }
            log("[已取消] ADB 设备检测已停止。");
            return false;
        }

        /// <summary>
        /// 等待 Fastboot 设备连接（循环检测，用户可随时取消）。
        /// </summary>
        public static async Task<bool> WaitForFastbootAsync(Action<string> log, CancellationToken ct = default)
        {
            log("正在检测 Fastboot 设备...");
            while (!ct.IsCancellationRequested)
            {
                if (await HasFastbootDeviceAsync())
                {
                    log("[OK] Fastboot 设备已连接。");
                    return true;
                }
                log("[提示] 未检测到 Fastboot 设备，请连接后重试（3 秒后自动重试）...");
                try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { break; }
            }
            log("[已取消] Fastboot 设备检测已停止。");
            return false;
        }

        /// <summary>
        /// 通过 ADB 重启到 bootloader（Fastboot）模式。
        /// 等待设备重连的延时受 ct 控制，取消时可立即返回（此前写死的 10 秒无法打断，
        /// 导致点了取消按钮后界面长时间无反应）。
        /// </summary>
        public static async Task RebootToBootloaderAsync(Action<string> log, CancellationToken ct = default)
        {
            log("正在通过 ADB 重启到 Fastboot 模式...");
            await ProcessRunner.RunAsync(AppConfig.AdbExe, "reboot bootloader", log, null, ct);
            log("等待设备进入 Fastboot 模式（10 秒）...");
            try { await Task.Delay(10000, ct); }
            catch (OperationCanceledException) { log("[已取消] 等待进入 Fastboot 模式已中断。"); }
        }

        /// <summary>通过 ADB 重启到 9008（EDL）模式。</summary>
        public static async Task RebootToEdlAsync(Action<string> log, CancellationToken ct = default)
        {
            log("正在进入 9008 模式...");
            await ProcessRunner.RunAsync(AppConfig.AdbExe, "reboot edl", log, null, ct);
        }

        /// <summary>
        /// 刷写单个分区。
        /// </summary>
        public static async Task<bool> FlashPartitionAsync(string partition, string filePath, Action<string> log, CancellationToken ct = default)
        {
            log($"正在刷写 {partition} 分区...");
            var code = await ProcessRunner.RunAsync(
                AppConfig.FastbootExe, $"flash {partition} \"{filePath}\"", log, AppConfig.BaseDir, ct);
            if (code != 0)
            {
                log($"[错误] 刷写 {partition} 失败！");
                return false;
            }
            log($"[OK] {partition} 刷写成功。");
            return true;
        }

        /// <summary>
        /// 去除指定分区的 AVB 校验并刷写（等于 fastboot --disable-verity --disable-verification flash 分区 镜像）。
        /// 用于刷入第三方系统时禁用 vbmeta 等分区的启动校验。
        /// </summary>
        public static async Task<bool> FlashVbmetaPartitionAsync(string partition, string filePath, Action<string> log, CancellationToken ct = default)
        {
            log($"正在去除 {partition} 的校验并刷写（--disable-verity --disable-verification）...");
            var code = await ProcessRunner.RunAsync(
                AppConfig.FastbootExe, $"--disable-verity --disable-verification flash {partition} \"{filePath}\"",
                log, AppConfig.BaseDir, ct);
            if (code != 0)
            {
                log($"[错误] 刷写 {partition} 失败！");
                return false;
            }
            log($"[OK] {partition} 刷写成功（已禁用校验）。");
            return true;
        }

        /// <summary>
        /// 批量刷写目录下所有 .img 镜像（按文件名作为分区名）。
        /// 对应 bat 中的 for %%f in (*.img) fastboot flash %%~nf 循环。
        /// </summary>
        /// <param name="recursive">是否递归子目录（救砖刷官方包时需要）</param>
        /// <param name="partitionFilter">可选：仅刷这些分区名（去扩展名的文件名）；为空则刷全部。</param>
        /// <param name="partitionNameOverrides">可选：文件名 → 实际刷写分区名 的覆盖映射。
        /// 例如 super_merged.img 合并产物应刷入 super 分区，即 { "super_merged.img": "super" }。</param>
        public static async Task<FlashSummary> FlashAllImagesAsync(
            string dir, bool recursive, Action<string> log, CancellationToken ct = default,
            IEnumerable<string>? partitionFilter = null,
            IReadOnlyDictionary<string, string>? partitionNameOverrides = null)
        {
            var summary = new FlashSummary();
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var imgs = Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "*.img", option)
                    .Where(f => partitionFilter == null ||
                                partitionFilter.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
                    .OrderBy(f => f).ToList()
                : new List<string>();

            foreach (var img in imgs)
            {
                ct.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(img);
                var part = partitionNameOverrides != null && partitionNameOverrides.TryGetValue(fileName, out var mapped)
                    ? mapped
                    : Path.GetFileNameWithoutExtension(img);
                var ok = await FlashPartitionAsync(part, img, log, ct);
                if (ok) summary.SuccessCount++;
                else summary.FailedParts.Add(part);
            }
            return summary;
        }

        /// <summary>fastboot reboot 重启设备。</summary>
        public static async Task RebootAsync(Action<string> log, CancellationToken ct = default)
        {
            log("正在重启设备...");
            await ProcessRunner.RunAsync(AppConfig.FastbootExe, "reboot", log, null, ct);
            log("[OK] 重启指令已发送，设备正在重启。");
        }

        /// <summary>adb reboot 重启设备（系统模式下使用）。</summary>
        public static async Task RebootSystemAsync(Action<string> log, CancellationToken ct = default)
        {
            log("正在重启设备...");
            await ProcessRunner.RunAsync(AppConfig.AdbExe, "reboot", log, null, ct);
            log("[OK] 重启指令已发送，设备正在重启。");
        }
    }
}
