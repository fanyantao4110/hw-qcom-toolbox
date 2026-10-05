using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MatePadToolbox.Services
{
    /// <summary>
    /// Magisk Root 服务：9008 提取 ramdisk → magiskboot 修补 → 9008 刷回。
    /// 修补流水线严格对应原版 Class17 的实现（unpack → 压缩素材 → 注入 cpio → repack）。
    /// 分区位置全部按设备实际分区表动态定位（不写死 LUN/扇区）。
    /// </summary>
    public static class MagiskService
    {
        public static string RamdiskImgPath => Path.Combine(AppConfig.OutDir, "ramdisk.img");
        public static string PatchedRamdiskPath => Path.Combine(AppConfig.OutDir, "patched_ramdisk.img");

        /// <summary>刷写暂存目录：把修补后的镜像以 ramdisk.img 命名，供 fh_loader 按分区名查找。</summary>
        private static string FlashDir => Path.Combine(AppConfig.OutDir, "flash_ramdisk");

        /// <summary>
        /// 9008 连接准备：进入 9008 → 上传编程器 → 配置端口。成功返回端口号，失败返回 -1。
        /// </summary>
        private static async Task<int> ConnectEdlAsync(bool rebootViaAdb, Action<string> log, CancellationToken ct)
        {
            // 复用上一次建立好的 9008 会话：提取 ramdisk 后设备仍停留在 9008，
            // 后续「修补 → 刷入」直接续用同一会话，不必重启设备再重新进入 9008。
            var reused = EdlService.TryReuseActiveSession();
            if (reused.HasValue)
            {
                log($"[OK] 复用已建立的 9008 会话（COM{reused.Value}），跳过重新连接与编程器上传。");
                return reused.Value;
            }

            if (rebootViaAdb)
            {
                log("检测 ADB 设备...");
                if (!await DeviceService.WaitForAdbAsync(log, ct)) return -1;
                await DeviceService.RebootToEdlAsync(log);
                log("等待设备进入 9008 模式...");
            }
            else
            {
                log("等待设备进入 9008 模式（请确保已通过探针/短接进入）...");
            }

            var port = await ComPortDetector.WaitFor9008Async(15, 2000, log, ct);
            if (!port.HasValue)
            {
                log("[错误] 等待超时，未检测到 9008 设备。");
                return -1;
            }
            log($"[OK] 检测到 9008 设备，端口 COM{port.Value}");

            if (!await EdlService.UploadFirehoseAsync(port.Value, log, ct, GlobalProcessorService.DevprgPath)) return -1;
            if (!await EdlService.ConfigurePortAsync(port.Value, AppConfig.TmpDir, log, ct)) return -1;

            EdlService.SetActiveSession(port.Value);
            return port.Value;
        }

        /// <summary>
        /// 步骤一：通过 9008 模式动态读取设备分区表并提取 ramdisk 分区。
        /// </summary>
        /// <param name="rebootViaAdb">鸿蒙2/3 需要先 adb reboot edl；鸿蒙4 由用户自行进入 9008</param>
        public static async Task<bool> ExtractRamdiskAsync(bool rebootViaAdb, Action<string> log, CancellationToken ct = default)
        {
            if (GlobalProcessorService.Current == null)
            {
                log("[错误] 请先在侧边栏顶部的【CPU型号】下拉框中选择处理器型号。");
                return false;
            }
            if (!File.Exists(GlobalProcessorService.DevprgPath))
            {
                log($"[错误] 缺少编程文件 {GlobalProcessorService.DevprgPath}");
                return false;
            }
            log($"[OK] 文件检查通过（处理器：{GlobalProcessorService.DisplayName}）。");

            var port = await ConnectEdlAsync(rebootViaAdb, log, ct);
            if (port < 0) return false;

            log("正在读取设备分区表以定位 ramdisk 分区...");
            var table = await EdlService.LoadPartitionTableAsync(port, log, ct);
            if (table == null)
            {
                log("[错误] 读取设备分区表失败！正在重启设备...");
                await EdlService.RebootDeviceAsync(port, AppConfig.UnlockDir, log, ct);
                EdlService.ClearActiveSession();
                return false;
            }

            Directory.CreateDirectory(AppConfig.OutDir);
            var imgPath = await EdlService.ReadPartitionAsync(
                port, table.Value.MemType, "ramdisk", table.Value.Map, AppConfig.OutDir, log, ct);
            if (imgPath == null)
            {
                log("[错误] 提取 ramdisk 分区失败！正在重启设备...");
                await EdlService.RebootDeviceAsync(port, AppConfig.UnlockDir, log, ct);
                EdlService.ClearActiveSession();
                return false;
            }

            // 关键：提取完成后**不重启设备**，保持 9008 会话，供步骤 2/3 直接复用。
            log("[OK] ramdisk 已提取完成，设备保持在 9008 模式。");
            log("     可直接继续【2. 修补 ramdisk】与【3. 刷入 ramdisk】，无需重新进入 9008。");
            return true;
        }

        /// <summary>
        /// <summary>
        /// 步骤二：使用 MagiskPatcher.exe 修补 ramdisk.img（一键修补，自动从 Magisk.apk 解包素材）。
        /// </summary>
        public static async Task<bool> PatchRamdiskAsync(Action<string> log, CancellationToken ct = default)
        {
            if (!File.Exists(AppConfig.MagiskPatcherExe))
            {
                log("[错误] 缺少文件 tools\\\\magisk_tools\\\\MagiskPatcher.exe");
                return false;
            }
            if (!File.Exists(AppConfig.MagiskApk))
            {
                log("[错误] 缺少文件 tools\\\\apks\\\\Magisk.apk");
                return false;
            }
            if (!File.Exists(AppConfig.MagiskbootExe))
            {
                log("[错误] 缺少文件 tools\\\\magisk_tools\\\\magiskboot.exe");
                return false;
            }
            if (!File.Exists(AppConfig.Zip7Exe))
            {
                log("[错误] 缺少文件 tools\\\\7z.exe");
                return false;
            }
            if (!File.Exists(RamdiskImgPath))
            {
                log("[错误] 未找到 ramdisk.img，请先提取 ramdisk 分区。");
                return false;
            }
            log("[OK] 文件检查通过，开始修补...");

            var workDir = Path.Combine(AppConfig.TmpDir, "magisk_patch");
            try
            {
                if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
                Directory.CreateDirectory(workDir);
                Directory.CreateDirectory(AppConfig.OutDir);

                // MagiskPatcher.exe <apk路径> <boot路径> -out=... -wd=... -7z=... -mb=... -cfg=...
                var args = "\"" + AppConfig.MagiskApk + "\" \"" + RamdiskImgPath + "\""
                    + " -out=\"" + PatchedRamdiskPath + "\""
                    + " -wd=\"" + workDir + "\""
                    + " -7z=\"" + AppConfig.Zip7Exe + "\""
                    + " -mb=\"" + AppConfig.MagiskbootExe + "\""
                    + " -cfg=\"" + AppConfig.MagiskPatcherCsv + "\"";

                log("正在运行 MagiskPatcher 修补 ramdisk ...");
                var code = await ProcessRunner.RunAsync(AppConfig.MagiskPatcherExe, args,
                    line => log($"[MagiskPatcher] {line}"), workDir, ct);
                if (code != 0)
                {
                    log($"[错误] MagiskPatcher 执行失败（退出码 {code}）。");
                    return false;
                }

                if (!File.Exists(PatchedRamdiskPath))
                {
                    log("[错误] 修补后未找到输出文件 patched_ramdisk.img。");
                    return false;
                }
                log($"[OK] 修补完成，文件已保存到：{PatchedRamdiskPath}");
                return true;
            }
            finally
            {
                try { if (Directory.Exists(workDir)) Directory.Delete(workDir, true); } catch { }
            }
        }

        /// <summary>定位重打包产物并复制为 patched_ramdisk.img。</summary>
        private static Task<bool> FinishPatchAsync(string workDir, Action<string> log)
        {
            string? newImg = null;
            foreach (var name in new[] { "new-boot.img", "new-ramdisk.img", "ramdisk.img.new" })
            {
                var p = Path.Combine(workDir, name);
                if (File.Exists(p)) { newImg = p; break; }
            }
            if (newImg == null)
            {
                log("[错误] 未找到重打包产物。");
                return Task.FromResult(false);
            }

            Directory.CreateDirectory(AppConfig.OutDir);
            File.Copy(newImg, PatchedRamdiskPath, overwrite: true);
            log($"[OK] 修补完成，文件已保存到：{PatchedRamdiskPath}");
            return Task.FromResult(true);
        }

        /// <summary>
        /// 步骤三：通过 9008 模式动态定位 ramdisk 分区并刷入修补后的 ramdisk。
        /// </summary>
        public static async Task<bool> FlashPatchedRamdiskAsync(bool rebootViaAdb, Action<string> log, CancellationToken ct = default)
        {
            if (!File.Exists(PatchedRamdiskPath))
            {
                log("[错误] 未找到 patched_ramdisk.img，请先完成修补。");
                return false;
            }
            log("[OK] 找到修补后的镜像。");

            Directory.CreateDirectory(FlashDir);
            var flashImg = Path.Combine(FlashDir, "ramdisk.img");
            File.Copy(PatchedRamdiskPath, flashImg, overwrite: true);

            var port = await ConnectEdlAsync(rebootViaAdb, log, ct);
            if (port < 0) return false;

            log("正在读取设备分区表以定位 ramdisk 分区...");
            var table = await EdlService.LoadPartitionTableAsync(port, log, ct);
            if (table == null)
            {
                log("[错误] 读取设备分区表失败！正在重启设备...");
                await EdlService.RebootDeviceAsync(port, AppConfig.UnlockDir, log, ct);
                EdlService.ClearActiveSession();
                return false;
            }

            var ok = await EdlService.WritePartitionAsync(
                port, table.Value.MemType, "ramdisk", table.Value.Map, FlashDir, log, ct);

            // 刷写完成，重启设备退出 9008，会话随之失效
            await EdlService.RebootDeviceAsync(port, AppConfig.UnlockDir, log, ct);
            EdlService.ClearActiveSession();
            if (!ok)
            {
                log("[错误] 刷入修补后的 ramdisk 失败！");
                return false;
            }
            log("Magisk Root 完成！首次开机后请打开 Magisk 应用完成后续激活。");
            return true;
        }
    }
}
