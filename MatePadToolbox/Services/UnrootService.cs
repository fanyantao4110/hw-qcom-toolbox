using System.IO;

namespace MatePadToolbox.Services
{
    /// <summary>
    /// 取消 Root 服务：从底包（UPDATE.APP）中提取原厂 boot 与 ramdisk，
    /// 再在 Fastboot 模式下刷回并重启，从而移除 Root。
    /// </summary>
    public static class UnrootService
    {
        /// <summary>取消 Root 的解包工作目录。</summary>
        private static string WorkDir => Path.Combine(AppConfig.OutDir, "unroot");

        /// <summary>
        /// 从底包提取原厂 boot 与 ramdisk 分区镜像。
        /// </summary>
        /// <returns>成功返回 (boot 路径, ramdisk 路径)；失败返回 null。</returns>
        public static async Task<(string Boot, string Ramdisk)?> ExtractStockImagesAsync(
            string appPath, Action<string> log, CancellationToken ct = default)
        {
            if (!File.Exists(appPath))
            {
                log($"[错误] 底包不存在：{appPath}");
                return null;
            }
            log($"已选择底包：{Path.GetFileName(appPath)}");

            try
            {
                if (Directory.Exists(WorkDir)) Directory.Delete(WorkDir, true);
            }
            catch { /* 清理失败不阻断 */ }
            Directory.CreateDirectory(WorkDir);

            log("正在从底包提取 boot 与 ramdisk 分区...");
            var imgs = await UpdateAppService.ExtractAsync(
                appPath, WorkDir, log, ct, partitionFilter: new[] { "boot", "ramdisk" });
            if (imgs == null) return null;

            var boot = imgs.FirstOrDefault(f =>
                string.Equals(Path.GetFileNameWithoutExtension(f), "boot", StringComparison.OrdinalIgnoreCase));
            var ramdisk = imgs.FirstOrDefault(f =>
                string.Equals(Path.GetFileNameWithoutExtension(f), "ramdisk", StringComparison.OrdinalIgnoreCase));

            if (boot == null || ramdisk == null)
            {
                log($"[错误] 底包中未同时找到 boot 与 ramdisk 分区（boot={(boot != null)}，ramdisk={(ramdisk != null)}）。");
                return null;
            }
            log($"[OK] 提取完成：{Path.GetFileName(boot)}、{Path.GetFileName(ramdisk)}");
            return (boot, ramdisk);
        }

        /// <summary>
        /// 完整取消 Root 流程：提取原厂 boot/ramdisk → 进入 Fastboot → 刷入 → 重启。
        /// </summary>
        /// <param name="isSystemMode">true=设备在系统模式（自动 ADB 重启进 Fastboot）；false=已手动进入 Fastboot。</param>
        public static async Task<bool> UnrootAsync(
            string appPath, bool isSystemMode, Action<string> log, CancellationToken ct = default)
        {
            var stock = await ExtractStockImagesAsync(appPath, log, ct);
            if (stock == null) return false;
            var (boot, ramdisk) = stock.Value;

            log("正在准备进入 Fastboot 模式...");
            if (!await PrepareFastbootAsync(isSystemMode, log, ct)) return false;

            log("正在刷入原厂 boot 分区...");
            if (!await DeviceService.FlashPartitionAsync("boot", boot, log, ct)) return false;

            log("正在刷入原厂 ramdisk 分区...");
            if (!await DeviceService.FlashPartitionAsync("ramdisk", ramdisk, log, ct)) return false;

            await DeviceService.RebootAsync(log);
            log("取消 Root 完成！设备正在重启，Root 权限已移除。");
            return true;
        }

        /// <summary>
        /// 按用户所选模式进入 Fastboot：系统模式先经 ADB 重启到 bootloader，Fastboot 模式直接等待设备。
        /// </summary>
        private static async Task<bool> PrepareFastbootAsync(bool isSystemMode, Action<string> log, CancellationToken ct)
        {
            if (isSystemMode)
            {
                log("检测 ADB 设备...");
                if (!await DeviceService.WaitForAdbAsync(log, ct)) return false;
                await DeviceService.RebootToBootloaderAsync(log);
            }
            else
            {
                log("请确保设备已手动进入 Fastboot 模式（按住音量下键 + 电源键）。");
            }
            return await DeviceService.WaitForFastbootAsync(log, ct);
        }
    }
}
