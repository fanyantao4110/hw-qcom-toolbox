using System.IO;
using System.Text;

namespace MatePadToolbox.Services
{
    /// <summary>
    /// 一键隐藏 Root 环境。支持两种 Root 方案：
    ///   · APatch：通过 apd 刷入隐藏模块（TEESimulator-RS / Zygisk Next / 紫罗兰辅助 / LSPosed）。
    ///   · Magisk：通过 magisk --install-module 刷入隐藏模块（TEESimulator-RS / 紫罗兰辅助 /
    ///     LSPosed / Shamiko），并自动启用内置 Zygisk、建立 Shamiko 白名单文件
    ///     （/data/adb/shamiko/whitelist，是「文件」不是「目录」）。
    ///
    /// **Magisk 方案特意不安装 Zygisk Next** —— Magisk 自带 Zygisk，再装 Zygisk Next 会冲突，
    /// 这也是同类工具箱隐藏 Magisk 失败的常见原因；隐藏能力交给 Shamiko。
    ///
    /// 所有资源均取自本地 modules 目录（不联网下载）；LSPosed 使用
    /// LSPosed-v2.2.0-7854-release.zip 替代旧的 LSPosed IT。
    /// </summary>
    public static class HideEnvService
    {
        private const string RemoteTmpDir = "/data/local/tmp";
        private const string HmaPackage = "com.tsng.hidemyapplist";
        private const string HmaActivityFull =
            "com.tsng.hidemyapplist/icu.nullptr.hidemyapplist.ui.activity.MainActivity";
        private const string HmaFilesDir = "/data/user/0/com.tsng.hidemyapplist/files";
        private const string RemoteConfig = "/sdcard/Download/config.json";

        /// <summary>Shamiko 配置目录与白名单文件（白名单是「文件」，不是「目录」）。</summary>
        private const string ShamikoDir = "/data/adb/shamiko";
        private const string ShamikoWhitelist = ShamikoDir + "/whitelist";

        /// <summary>Magisk 收尾脚本在设备上的临时路径。</summary>
        private const string RemoteMagiskScript = RemoteTmpDir + "/hideenv_magisk.sh";

        /// <summary>本地资源暂存目录（统一用 ASCII 文件名，规避 adb 对中文路径的编码问题）。</summary>
        private static string StagingDir => Path.Combine(AppConfig.TmpDir, "hideenv");

        // ==================== 方案配置 ====================

        /// <summary>一套隐藏方案（APatch / Magisk）的差异配置。</summary>
        private sealed class HideProfile
        {
            /// <summary>期望检测到的 Root 方案关键字（apatch / magisk）。</summary>
            public required string SchemeName { get; init; }
            /// <summary>方案显示名（APatch / Magisk）。</summary>
            public required string SchemeDisplay { get; init; }
            /// <summary>要刷入的隐藏模块（暂存名 → 显示名），按顺序安装。</summary>
            public required (string Staged, string Name)[] Modules { get; init; }
            /// <summary>模块安装命令模板，含 {zip} 占位符。</summary>
            public required string InstallCmdTemplate { get; init; }
            /// <summary>是否启用 Magisk 内置 Zygisk（Shamiko 依赖）。</summary>
            public bool EnableMagiskZygisk { get; init; }
            /// <summary>是否创建 Shamiko 白名单文件。</summary>
            public bool CreateShamikoWhitelist { get; init; }
        }

        private static HideProfile ApatchProfile => new()
        {
            SchemeName = "apatch",
            SchemeDisplay = "APatch",
            InstallCmdTemplate = "apd module install {zip}",
            Modules = new[]
            {
                ("teesimulator.zip", "TEESimulator-RS"),
                ("zygisknext.zip", "Zygisk Next"),
                ("violethelper.zip", "紫罗兰辅助模块"),
                ("lsposed.zip", "LSPosed(v2.2.0-7854)"),
            },
        };

        private static HideProfile MagiskProfile => new()
        {
            SchemeName = "magisk",
            SchemeDisplay = "Magisk",
            InstallCmdTemplate = "magisk --install-module {zip}",
            // 与 APatch 一致，但去掉 Zygisk Next（Magisk 自带 Zygisk），并新增 Shamiko。
            Modules = new[]
            {
                ("teesimulator.zip", "TEESimulator-RS"),
                ("violethelper.zip", "紫罗兰辅助模块"),
                ("lsposed.zip", "LSPosed(v2.2.0-7854)"),
                ("shamiko.zip", "Shamiko"),
            },
            EnableMagiskZygisk = true,
            CreateShamikoWhitelist = true,
        };

        /// <summary>全部可能的本地资源：(源路径, 暂存 ASCII 名, 显示名)。</summary>
        private static (string Src, string Staged, string Name)[] AllAssets() => new[]
        {
            (AppConfig.HideAppListApk, "hma.apk", "隐藏应用列表"),
            (AppConfig.HideConfigJson, "config.json", "隐藏应用列表配置"),
            (AppConfig.KeyAuthApk, "keyauth.apk", "密钥认证"),
            (AppConfig.LunaApk, "luna.apk", "Luna"),
            (AppConfig.TeeSimulatorZip, "teesimulator.zip", "TEESimulator-RS 模块"),
            (AppConfig.ZygiskNextZip, "zygisknext.zip", "Zygisk Next 模块"),
            (AppConfig.VioletHelperZip, "violethelper.zip", "紫罗兰辅助模块"),
            (AppConfig.LsposedZip, "lsposed.zip", "LSPosed(v2.2.0-7854) 模块"),
            (AppConfig.ShamikoZip, "shamiko.zip", "Shamiko 模块"),
        };

        /// <summary>某个方案实际需要的资源（公共部分 + 该方案的模块）。</summary>
        private static (string Src, string Staged, string Name)[] RequiredAssets(HideProfile profile)
        {
            var needed = new HashSet<string>(StringComparer.Ordinal)
            {
                "hma.apk", "config.json", "keyauth.apk", "luna.apk",
            };
            foreach (var (staged, _) in profile.Modules) needed.Add(staged);
            return AllAssets().Where(a => needed.Contains(a.Staged)).ToArray();
        }

        // ==================== 对外入口 ====================

        /// <summary>一键隐藏 Root 环境（APatch）。返回是否整体成功。</summary>
        public static Task<bool> RunAsync(Action<string> log, CancellationToken ct = default)
            => RunCoreAsync(ApatchProfile, log, ct);

        /// <summary>一键隐藏 Root 环境（Magisk）。返回是否整体成功。</summary>
        public static Task<bool> RunMagiskAsync(Action<string> log, CancellationToken ct = default)
            => RunCoreAsync(MagiskProfile, log, ct);

        // ==================== 主流程 ====================

        private static async Task<bool> RunCoreAsync(HideProfile profile, Action<string> log, CancellationToken ct)
        {
            // ---------- 1. 本地资源检查 ----------
            var assets = RequiredAssets(profile);
            var missing = assets.Where(a => !File.Exists(a.Src))
                                .Select(a => Path.GetFileName(a.Src)).ToList();
            if (missing.Count > 0)
            {
                log("[错误] 缺少隐藏环境资源文件，请放入 modules 目录后重试：");
                foreach (var m in missing) log($"    · {m}");
                log($"    目录：{AppConfig.ModulesDir}");
                return false;
            }
            log($"[OK] 隐藏环境资源检查通过（共 {assets.Length} 个文件，方案：{profile.SchemeDisplay}）。");

            // ---------- 2. 暂存为 ASCII 文件名 ----------
            try
            {
                if (Directory.Exists(StagingDir)) Directory.Delete(StagingDir, true);
                Directory.CreateDirectory(StagingDir);
            }
            catch (Exception ex)
            {
                log($"[错误] 无法创建暂存目录：{ex.Message}");
                return false;
            }
            var staged = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (src, name, _) in assets)
            {
                var dst = Path.Combine(StagingDir, name);
                try { File.Copy(src, dst, true); }
                catch (Exception ex)
                {
                    log($"[错误] 暂存 {name} 失败：{ex.Message}");
                    return false;
                }
                staged[name] = dst;
            }

            try
            {
                // ---------- 3. 连接与权限检查 ----------
                log("检测 ADB 设备...");
                if (!await DeviceService.WaitForAdbAsync(log, ct)) return false;

                log("检测 Root 权限...");
                if (!await HasRootAsync(ct))
                {
                    log("[错误] 未检测到 Root 权限。请先完成 Root 并授予管理器 Root 权限。");
                    return false;
                }
                log("[OK] 已获取 Root 权限。");

                log("识别 Root 方案...");
                var scheme = await DetectRootSchemeAsync(ct);
                log($"[信息] 检测到 Root 方案：{scheme}");
                if (!string.Equals(scheme, profile.SchemeName, StringComparison.OrdinalIgnoreCase))
                {
                    log($"[错误] 本功能仅支持 {profile.SchemeDisplay} 方案，当前检测到「{scheme}」，已中止。");
                    return false;
                }

                // ---------- 4. [1/3] 安装隐藏应用列表并导入配置 ----------
                log("===== [1/3] 安装隐藏应用列表（HMA）=====");
                if (!await InstallHmaAsync(staged, log, ct)) return false;

                // ---------- 5. [2/3] 安装配套 APK ----------
                log("===== [2/3] 安装配套 APK =====");
                foreach (var (key, name) in new[] { ("keyauth.apk", "密钥认证"), ("luna.apk", "Luna") })
                {
                    ct.ThrowIfCancellationRequested();
                    log($"正在安装：{name} ...");
                    if (await InstallApkAsync(staged[key], log, ct))
                        log($"[OK] {name} 安装成功。");
                    else
                        log($"[警告] {name} 安装失败，继续后续步骤。");
                }

                // ---------- 6. [3/3] 刷入隐藏模块 ----------
                log($"===== [3/3] 刷入隐藏模块（{profile.SchemeDisplay}）=====");
                int moduleOk = 0;
                foreach (var (stagedName, display) in profile.Modules)
                {
                    ct.ThrowIfCancellationRequested();
                    if (await InstallModuleAsync(staged[stagedName], stagedName, display,
                                                profile.InstallCmdTemplate, log, ct))
                        moduleOk++;
                }
                log($"[信息] 隐藏模块安装完成 {moduleOk}/{profile.Modules.Length} 个。");

                // ---------- 7. Magisk 专属收尾：启用 Zygisk + Shamiko 白名单文件 ----------
                bool extrasOk = true;
                if (profile.EnableMagiskZygisk || profile.CreateShamikoWhitelist)
                    extrasOk = await ApplyMagiskHideSetupAsync(profile, log, ct);

                // ---------- 8. 收尾 ----------
                log("===============================================");
                log("  一键隐藏 Root 环境已完成，请重启设备以生效。");
                log("  提示：隐藏 BL 解锁状态与 Root 环境后，");
                log("        工具箱显示「已锁定」属正常现象。");
                log("===============================================");
                return moduleOk == profile.Modules.Length && extrasOk;
            }
            finally
            {
                try { Directory.Delete(StagingDir, true); } catch { /* 忽略 */ }
            }
        }

        /// <summary>安装隐藏应用列表（HMA）并导入 config.json。</summary>
        private static async Task<bool> InstallHmaAsync(
            Dictionary<string, string> staged, Action<string> log, CancellationToken ct)
        {
            log("正在安装隐藏应用列表...");
            if (!await InstallApkAsync(staged["hma.apk"], log, ct))
            {
                log("[错误] 隐藏应用列表安装失败。");
                log("      请在开发者选项中关闭「监控 ADB 安装应用」后重试。");
                return false;
            }

            // 启动一次以创建数据目录，5 秒后强停
            log("正在初始化隐藏应用列表数据目录...");
            await AdbAsync($"shell am start -n {HmaActivityFull}", log, ct);
            try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { throw; }
            await AdbAsync($"shell am force-stop {HmaPackage}", log, ct);

            // 推送配置到设备再拷入应用私有目录（需 Root）
            log("正在导入隐藏应用列表配置...");
            if (!await AdbAsync($"push \"{staged["config.json"]}\" {RemoteConfig}", log, ct))
            {
                log("[错误] 推送配置文件失败。");
                return false;
            }

            if (!await SuAsync(
                    $"mkdir -p {HmaFilesDir} && cp {RemoteConfig} {HmaFilesDir}/config.json && chmod 600 {HmaFilesDir}/config.json",
                    log, ct))
            {
                log("[错误] 写入配置到应用私有目录失败。");
                return false;
            }

            // 归属为应用自身 uid
            var (uidCode, uidOut) = await ProcessRunner.RunCaptureAsync(
                AppConfig.AdbExe, $"shell stat -c %u {HmaFilesDir}", AppConfig.BaseDir, ct);
            var uid = uidOut.Trim();
            if (uidCode == 0 && int.TryParse(uid, out _))
            {
                await SuAsync($"chown {uid}:{uid} {HmaFilesDir}/config.json", log, ct);
                log($"[信息] 配置归属已设置为 uid {uid}。");
            }

            await AdbAsync($"shell rm -f {RemoteConfig}", log, ct);
            log("[OK] 隐藏应用列表配置导入完成。");
            return true;
        }

        /// <summary>推送并安装单个模块（APatch：apd module install；Magisk：magisk --install-module）。</summary>
        private static async Task<bool> InstallModuleAsync(
            string localZip, string stagedName, string display, string installCmdTemplate,
            Action<string> log, CancellationToken ct)
        {
            var remote = $"{RemoteTmpDir}/{stagedName}";

            log($"正在推送模块：{display} ...");
            if (!await AdbAsync($"push \"{localZip}\" {remote}", log, ct))
            {
                log($"[警告] 推送 {display} 失败，跳过该模块。");
                return false;
            }

            log($"正在安装模块：{display} ...");
            var installCmd = installCmdTemplate.Replace("{zip}", remote);
            var (code, output) = await ProcessRunner.RunCaptureAsync(
                AppConfig.AdbExe, $"shell \"su -c '{installCmd}'\"", AppConfig.BaseDir, ct);

            // 成败以退出码为准（与 ModuleFlashService 保持一致）。
            // 不能用关键字匹配判断：模块安装脚本会输出大量探测日志，
            // 例如 LSPosed 的 customize.sh 探测 magisk 会打印 "magisk: not found"，
            // 但流程继续并最终成功（输出 "- Welcome to LSPosed!" / "- Done"）。
            var failed = code != 0;

            if (failed)
            {
                log($"[警告] {display} 安装失败（退出码 {code}）。");
                if (!string.IsNullOrWhiteSpace(output)) log($"      输出：{output.Trim()}");
            }
            else
            {
                log($"[OK] {display} 安装完成。");
            }

            await AdbAsync($"shell rm -f {remote}", log, ct);
            return !failed;
        }

        /// <summary>
        /// Magisk 专属收尾：启用内置 Zygisk + 建立 Shamiko 白名单文件。
        /// 通过推送一个临时 shell 脚本执行，避免「adb shell → su -c → 内含引号」的多层转义问题。
        /// </summary>
        private static async Task<bool> ApplyMagiskHideSetupAsync(
            HideProfile profile, Action<string> log, CancellationToken ct)
        {
            bool ok = true;

            // 生成脚本（LF 换行、纯 ASCII，避免设备端解析问题）
            var sb = new StringBuilder();
            sb.Append("#!/system/bin/sh\n");
            if (profile.EnableMagiskZygisk)
            {
                sb.Append("# enable Magisk built-in Zygisk (required by Shamiko)\n");
                sb.Append("if magisk --sqlite \"REPLACE INTO settings (key,value) VALUES('zygisk',1)\" >/dev/null 2>&1; then\n");
                sb.Append("  echo \"ZYGISK_SET_OK\"\n");
                sb.Append("else\n");
                sb.Append("  echo \"ZYGISK_SET_FAIL\"\n");
                sb.Append("fi\n");
            }
            if (profile.CreateShamikoWhitelist)
            {
                sb.Append("# create Shamiko whitelist FILE (not a directory)\n");
                sb.Append($"mkdir -p {ShamikoDir}\n");
                sb.Append($"rm -rf {ShamikoWhitelist}\n");
                sb.Append($": > {ShamikoWhitelist}\n");
                sb.Append($"if [ -f {ShamikoWhitelist} ] && [ ! -d {ShamikoWhitelist} ]; then\n");
                sb.Append("  echo \"SHAMIKO_WHITELIST_OK\"\n");
                sb.Append("else\n");
                sb.Append("  echo \"SHAMIKO_WHITELIST_FAIL\"\n");
                sb.Append("fi\n");
            }

            var scriptLocal = Path.Combine(StagingDir, "hideenv_magisk.sh");
            try
            {
                File.WriteAllText(scriptLocal, sb.ToString().Replace("\r\n", "\n"), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                log($"[警告] 生成 Magisk 收尾脚本失败：{ex.Message}");
                return false;
            }

            if (!await AdbAsync($"push \"{scriptLocal}\" {RemoteMagiskScript}", log, ct))
            {
                log("[警告] 推送 Magisk 收尾脚本失败。");
                return false;
            }

            log("正在应用 Magisk 隐藏设置（启用内置 Zygisk、建立 Shamiko 白名单文件）...");
            var (code, output) = await ProcessRunner.RunCaptureAsync(
                AppConfig.AdbExe, $"shell \"su -c '/system/bin/sh {RemoteMagiskScript}'\"", AppConfig.BaseDir, ct);

            await AdbAsync($"shell rm -f {RemoteMagiskScript}", log, ct);

            if (profile.EnableMagiskZygisk)
            {
                if (output.Contains("ZYGISK_SET_OK")) log("[OK] 已启用 Magisk 内置 Zygisk。");
                else { log("[警告] 启用 Magisk 内置 Zygisk 失败（可稍后在 Magisk 应用内手动开启）。"); ok = false; }
            }
            if (profile.CreateShamikoWhitelist)
            {
                if (output.Contains("SHAMIKO_WHITELIST_OK"))
                    log($"[OK] 已创建 Shamiko 白名单文件 {ShamikoWhitelist}（文件模式，白名单已启用）。");
                else
                { log("[警告] 创建 Shamiko 白名单文件失败。"); ok = false; }
            }
            if (code != 0) log($"[警告] Magisk 收尾脚本退出码 {code}。");
            return ok;
        }

        // ==================== 基础命令封装 ====================

        /// <summary>执行 adb 命令（不回显输出，仅返回是否成功）。</summary>
        private static async Task<bool> AdbAsync(string args, Action<string> log, CancellationToken ct)
        {
            var code = await ProcessRunner.RunAsync(AppConfig.AdbExe, args, log, AppConfig.BaseDir, ct);
            return code == 0;
        }

        /// <summary>通过 su 执行 shell 命令。</summary>
        private static async Task<bool> SuAsync(string cmd, Action<string> log, CancellationToken ct)
        {
            var code = await ProcessRunner.RunAsync(
                AppConfig.AdbExe, $"shell \"su -c '{cmd}'\"", log, AppConfig.BaseDir, ct);
            return code == 0;
        }

        /// <summary>adb install -r 安装 APK。</summary>
        private static async Task<bool> InstallApkAsync(string apk, Action<string> log, CancellationToken ct)
        {
            var (code, output) = await ProcessRunner.RunCaptureAsync(
                AppConfig.AdbExe, $"install -r \"{apk}\"", AppConfig.BaseDir, ct);
            if (code == 0 || output.Contains("Success", StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrWhiteSpace(output)) log($"      输出：{output.Trim()}");
            return false;
        }

        /// <summary>su -c 'id' 是否包含 uid=0，判定是否已有 Root。</summary>
        private static async Task<bool> HasRootAsync(CancellationToken ct)
        {
            var (_, output) = await ProcessRunner.RunCaptureAsync(
                AppConfig.AdbExe, "shell \"su -c 'id'\"", AppConfig.BaseDir, ct);
            return output.Contains("uid=0");
        }

        /// <summary>识别 Root 方案：kernelsu / apatch / magisk / unknown。</summary>
        private static async Task<string> DetectRootSchemeAsync(CancellationToken ct)
        {
            foreach (var (cmd, name) in new[]
                     {
                         ("ksud -V", "kernelsu"),
                         ("apd -V", "apatch"),
                         ("magisk -V", "magisk"),
                     })
            {
                ct.ThrowIfCancellationRequested();
                var (code, output) = await ProcessRunner.RunCaptureAsync(
                    AppConfig.AdbExe, $"shell \"su -c '{cmd}'\"", AppConfig.BaseDir, ct);
                if (code == 0 && !string.IsNullOrWhiteSpace(output)) return name;
            }
            return "unknown";
        }
    }
}
