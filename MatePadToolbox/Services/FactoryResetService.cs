using System.IO;

namespace MatePadToolbox.Services
{
    /// <summary>
    /// 恢复出厂设置：把现成的 misc 镜像（tools\rawprogram\misc.img，即 AppConfig.MiscImg）
    /// 刷入 misc 分区后重启，设备下次启动即进入 Recovery 执行恢复出厂。
    /// 与 FlashSystemPage 写 misc 分区用的是同一个镜像，不另行生成。
    /// </summary>
    public static class FactoryResetService
    {
        /// <summary>
        /// 执行恢复出厂：把 misc 镜像刷入 misc 分区并重启设备。
        /// </summary>
        /// <param name="fromSystem">
        /// true = 设备当前在系统（开机）模式，会先 adb reboot bootloader 进入 Fastboot；
        /// false = 设备已在 Fastboot 模式，直接刷写。
        /// </param>
        public static async Task<bool> RunAsync(bool fromSystem, Action<string> log, CancellationToken ct = default)
        {
            var miscImg = AppConfig.MiscImg;
            if (!File.Exists(miscImg))
            {
                log($"[错误] 缺少 misc 镜像：{miscImg}");
                log("请将 misc.img 放入 tools\\rawprogram 目录后重试。");
                return false;
            }
            log($"[信息] 使用 misc 镜像：{miscImg}");

            if (fromSystem)
            {
                log("设备当前在系统模式，正在重启进入 Fastboot 模式...");
                if (!await DeviceService.WaitForAdbAsync(log, ct)) return false;
                await DeviceService.RebootToBootloaderAsync(log, ct);
            }

            if (!await DeviceService.WaitForFastbootAsync(log, ct)) return false;

            log("正在把 misc 镜像刷入 misc 分区（恢复出厂指令）...");
            if (!await DeviceService.FlashPartitionAsync("misc", miscImg, log, ct))
            {
                log("[错误] 刷入 misc 分区失败，已中止（设备未重启）。");
                return false;
            }

            log("正在重启设备，设备将进入 Recovery 并执行恢复出厂...");
            await DeviceService.RebootAsync(log, ct);
            log("===============================================");
            log("  已下发恢复出厂指令，设备重启后会自动清除数据。");
            log("  此过程会清除全部用户数据，请勿断开数据线。");
            log("===============================================");
            return true;
        }
    }
}
