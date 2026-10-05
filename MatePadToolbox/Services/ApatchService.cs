using System.IO;

namespace MatePadToolbox.Services
{
    /// <summary>
    /// APatch Root 服务：9008 提取 boot → kptools 修补 → 9008 刷回修补后的 boot。
    /// 分区位置全部按设备实际分区表动态定位（不写死 LUN/扇区）。
    /// </summary>
    public static class ApachService
    {
        public static string BootImgPath => Path.Combine(AppConfig.OutDir, "boot.img");
        public static string PatchedBootPath => Path.Combine(AppConfig.OutDir, "apatch_patched_boot.img");

        /// <summary>刷写暂存目录：把修补后的镜像以 boot.img 命名，供 fh_loader 按分区名查找。</summary>
        private static string FlashDir => Path.Combine(AppConfig.OutDir, "flash_boot");

        /// <summary>
        /// 9008 连接准备：进入 9008 → 上传编程器 → 配置端口。成功返回端口号，失败返回 -1。
        /// </summary>
        private static async Task<int> ConnectEdlAsync(bool rebootViaAdb, Action<string> log, CancellationToken ct)
        {
            // 复用上一次建立好的 9008 会话：提取 boot 后设备仍停留在 9008，
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
        /// 步骤一：通过 9008 模式动态读取设备分区表并提取 boot 分区。
        /// </summary>
        /// <param name="rebootViaAdb">鸿蒙2/3 需要先 adb reboot edl；鸿蒙4 由用户自行进入 9008</param>
        public static async Task<bool> ExtractBootAsync(bool rebootViaAdb, Action<string> log, CancellationToken ct = default)
        {
            // 处理器型号检查：必须在侧边栏顶部选择型号，防止误操作
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

            log("正在读取设备分区表以定位 boot 分区...");
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
                port, table.Value.MemType, "boot", table.Value.Map, AppConfig.OutDir, log, ct);
            if (imgPath == null)
            {
                log("[错误] 提取 boot 分区失败！正在重启设备...");
                await EdlService.RebootDeviceAsync(port, AppConfig.UnlockDir, log, ct);
                EdlService.ClearActiveSession();
                return false;
            }

            // 关键：提取完成后**不重启设备**，保持 9008 会话，供步骤 2/3 直接复用。
            // （此前提取后即 reboot，设备回到系统，刷入时又要重新进入 9008，鸿蒙4 需再次短接/工程线。）
            log("[OK] boot 已提取完成，设备保持在 9008 模式。");
            log("     可直接继续【2. 修补 boot】与【3. 刷入 boot】，无需重新进入 9008。");
            return true;
        }

        /// <summary>
        /// 步骤二：使用 kptools 修补 boot.img（unpack → 修补内核 → repack）。
        /// </summary>
        public static async Task<bool> PatchBootAsync(Action<string> log, CancellationToken ct = default)
        {
            if (!File.Exists(AppConfig.KptoolsExe))
            {
                log("[错误] 缺少文件 tools\\apatch_tools\\kptools.exe");
                return false;
            }
            if (!File.Exists(AppConfig.KpimgFile))
            {
                log("[错误] 缺少文件 tools\\apatch_tools\\kpimg-android");
                return false;
            }
            if (!File.Exists(BootImgPath))
            {
                log("[错误] 未找到 boot.img，请先提取 boot 分区。");
                return false;
            }
            log("[OK] 文件检查通过，开始修补...");

            var workDir = AppConfig.ApatchToolsDir;
            var kernelFile = Path.Combine(workDir, "kernel");
            var kernelBackup = Path.Combine(workDir, "kernel-b");
            var newBootFile = Path.Combine(workDir, "new-boot.img");

            // 1. 解包 boot.img
            log("正在解包 boot.img ...");
            var code = await ProcessRunner.RunAsync(AppConfig.KptoolsExe, $"unpack \"{BootImgPath}\"",
                line => log($"[kptools] {line}"), workDir, ct);
            if (code != 0)
            {
                log("[错误] 解包 boot.img 失败！");
                return false;
            }
            if (!File.Exists(kernelFile))
            {
                log("[错误] 解包后未找到 kernel 文件。");
                return false;
            }

            // 2. 修补内核
            if (File.Exists(kernelBackup)) File.Delete(kernelBackup);
            File.Move(kernelFile, kernelBackup);
            log("正在修补内核...");
            code = await ProcessRunner.RunAsync(AppConfig.KptoolsExe,
                "-p --image kernel-b --kpimg kpimg-android --out kernel",
                line => log($"[kptools] {line}"), workDir, ct);
            if (code != 0)
            {
                log("[错误] 内核修补失败！");
                return false;
            }

            // 3. 重新打包
            log("正在重新打包 boot.img ...");
            code = await ProcessRunner.RunAsync(AppConfig.KptoolsExe, $"repack \"{BootImgPath}\"",
                line => log($"[kptools] {line}"), workDir, ct);
            if (code != 0 || !File.Exists(newBootFile))
            {
                log("[错误] 重打包 boot.img 失败！");
                return false;
            }

            // 4. 整理产物
            Directory.CreateDirectory(AppConfig.OutDir);
            File.Move(newBootFile, PatchedBootPath, overwrite: true);
            try
            {
                if (File.Exists(kernelFile)) File.Delete(kernelFile);
                if (File.Exists(kernelBackup)) File.Delete(kernelBackup);
            }
            catch { /* 清理失败不影响结果 */ }

            log($"[OK] 修补完成，文件已保存到：{PatchedBootPath}");
            return true;
        }

        /// <summary>
        /// 步骤三：通过 9008 模式动态定位 boot 分区并刷入修补后的 boot。
        /// </summary>
        public static async Task<bool> FlashPatchedBootAsync(bool rebootViaAdb, Action<string> log, CancellationToken ct = default)
        {
            if (!File.Exists(PatchedBootPath))
            {
                log("[错误] 未找到 apatch_patched_boot.img，请先完成修补。");
                return false;
            }
            log("[OK] 找到修补后的镜像。");

            // 刷写目录内以 boot.img 命名，供 fh_loader 按分区名查找
            Directory.CreateDirectory(FlashDir);
            var flashImg = Path.Combine(FlashDir, "boot.img");
            File.Copy(PatchedBootPath, flashImg, overwrite: true);

            var port = await ConnectEdlAsync(rebootViaAdb, log, ct);
            if (port < 0) return false;

            log("正在读取设备分区表以定位 boot 分区...");
            var table = await EdlService.LoadPartitionTableAsync(port, log, ct);
            if (table == null)
            {
                log("[错误] 读取设备分区表失败！正在重启设备...");
                await EdlService.RebootDeviceAsync(port, AppConfig.UnlockDir, log, ct);
                EdlService.ClearActiveSession();
                return false;
            }

            var ok = await EdlService.WritePartitionAsync(
                port, table.Value.MemType, "boot", table.Value.Map, FlashDir, log, ct);

            // 刷写完成，重启设备退出 9008，会话随之失效
            await EdlService.RebootDeviceAsync(port, AppConfig.UnlockDir, log, ct);
            EdlService.ClearActiveSession();
            if (!ok)
            {
                log("[错误] 刷入修补后的 boot 失败！");
                return false;
            }
            log("APatch Root 完成！首次开机后请打开 APatch 应用完成后续激活。");
            return true;
        }
    }
}
