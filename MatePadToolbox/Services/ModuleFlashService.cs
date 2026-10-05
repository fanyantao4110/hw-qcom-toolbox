using System.IO;
using MatePadToolbox.Models;

namespace MatePadToolbox.Services
{
    /// <summary>
    /// 优化模块一键刷入服务：
    /// 识别 modules 目录下的 zip，与 json 中的 file（英文）对照后，
    /// 通过 ADB 安装到 Magisk 或 APatch。
    ///
    /// 安装命令（如与你的 Magisk/APatch 版本不一致可在此调整）：
    ///   Magisk : adb shell "su -c 'magisk --install-module <zip>'"
    ///   APatch : adb shell "su -c 'apd module install <zip>'"
    ///            （apd 的模块操作走 module 子命令：apd module install/uninstall/enable/disable）
    /// </summary>
    public static class ModuleFlashService
    {
        /// <summary>安装目标：Magisk 或 APatch。</summary>
        public enum InstallTarget
        {
            Magisk,
            APatch
        }

        /// <summary>模块 zip 是否已存在于 modules 目录（与 json 中 file 字段对照）。</summary>
        public static bool IsModuleZipPresent(ModuleEntry entry)
        {
            return File.Exists(ModuleConfigService.GetModuleZipPath(entry));
        }

        /// <summary>模块 zip 本地完整路径。</summary>
        public static string GetModuleZipPath(ModuleEntry entry)
        {
            return ModuleConfigService.GetModuleZipPath(entry);
        }

        /// <summary>
        /// 一键刷入：推送 zip 到设备，并通过 ADB 安装到指定目标。
        /// </summary>
        public static async Task<bool> FlashAsync(ModuleEntry entry, InstallTarget target, Action<string> log, CancellationToken ct = default)
        {
            var zip = ModuleConfigService.GetModuleZipPath(entry);
            if (!File.Exists(zip))
            {
                log($"[错误] 未找到模块文件：{Path.GetFileName(zip)}。请先在弹窗中点击【下载】。");
                return false;
            }

            // 1. 等待 ADB 设备
            if (!await DeviceService.WaitForAdbAsync(log, ct)) return false;

            var remote = "/data/local/tmp/" + Path.GetFileName(zip);

            // 2. 推送模块到设备
            log($"正在推送模块到设备：{remote} ...");
            var push = await ProcessRunner.RunAsync(AppConfig.AdbExe, $"push \"{zip}\" {remote}", log, AppConfig.BaseDir, ct);
            if (push != 0)
            {
                log("[错误] 推送模块失败，请确认已开启 USB 调试、驱动已正确安装。");
                return false;
            }

            // 3. 安装到目标
            string display;
            string shellCmd;
            if (target == InstallTarget.Magisk)
            {
                display = "Magisk";
                shellCmd = $"su -c 'magisk --install-module {remote}'";
            }
            else
            {
                display = "APatch";
                shellCmd = $"su -c 'apd module install {remote}'";
            }

            log($"正在通过 ADB 安装到 {display} ...");
            var code = await ProcessRunner.RunAsync(AppConfig.AdbExe, $"shell \"{shellCmd}\"", log, AppConfig.BaseDir, ct);
            if (code != 0)
            {
                log($"[错误] 安装到 {display} 失败。请确认设备已开启 Root（{display} 已安装并授予 Root 权限）、已关闭 Shamiko 模块。");
                return false;
            }

            log($"[OK] 已成功安装到 {display}，建议重启设备使模块生效。");
            return true;
        }
    }
}
