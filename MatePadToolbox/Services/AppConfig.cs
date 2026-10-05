using System.IO;

namespace MatePadToolbox.Services
{
    /// <summary>
    /// 全局路径与目录配置。
    /// 目录结构与原 bat 版保持一致：所有资源目录位于程序 exe 所在目录下。
    /// </summary>
    public static class AppConfig
    {
        /// <summary>程序根目录（exe 所在目录）。PublishSingleFile 模式下用 ProcessPath 替代 BaseDirectory。</summary>
        public static string BaseDir { get; } = Path.GetDirectoryName(Environment.ProcessPath)!.TrimEnd('\\');

        // ==================== 目录 ====================
        public static string ToolsDir => Path.Combine(BaseDir, "tools");
        public static string DriverDir => Path.Combine(ToolsDir, "driver");
        public static string RawprogramDir => Path.Combine(ToolsDir, "rawprogram");
        public static string ApatchToolsDir => Path.Combine(ToolsDir, "apatch_tools");
        public static string MagiskToolsDir => Path.Combine(ToolsDir, "magisk_tools");
        /// <summary>分区解包打包工具链目录（tools\part）。</summary>
        public static string PartToolsDir => Path.Combine(ToolsDir, "part");
        public static string PartFsToolsDir => Path.Combine(PartToolsDir, "fs");
        public static string PartSuperToolsDir => Path.Combine(PartToolsDir, "super");
        // ===== 分区 img 工作槽目录（放于 exe 根下 img\）=====
        public static string PartImgDir => Path.Combine(BaseDir, "img");
        public static string PartImg1Dir => Path.Combine(PartImgDir, "img_1");
        public static string PartImg2Dir => Path.Combine(PartImgDir, "img_2");
        public static string PartImg3Dir => Path.Combine(PartImgDir, "img_3");
        public static string PartImgCfgDir => Path.Combine(PartImgDir, "config");
        /// <summary>分区解包/打包设置文件（json\part_settings.json）。</summary>
        public static string PartSettingsFile => Path.Combine(JsonDir, "part_settings.json");
        public static string HiSuiteProxyDir => Path.Combine(ToolsDir, "HiSuiteProxy");
        public static string ApksDir => Path.Combine(ToolsDir, "apks");
        public static string UnlockDir => Path.Combine(BaseDir, "unlock");
        public static string SystemDir => Path.Combine(BaseDir, "system");
        public static string ModulesDir => Path.Combine(BaseDir, "modules");
        public static string CacheDir => Path.Combine(BaseDir, "cache");
        public static string TmpDir => Path.Combine(BaseDir, "tmp");
        public static string OutDir => Path.Combine(BaseDir, "out");
        public static string BaseRomDir => Path.Combine(BaseDir, "base");
        public static string LogDir => Path.Combine(BaseDir, "log");
        /// <summary>工具 JSON 配置文件目录（exe 下 json）。</summary>
        public static string JsonDir => Path.Combine(BaseDir, "json");
        public static string ScreenshotsDir => Path.Combine(BaseDir, "screenshots");

        // ==================== 可执行文件 ====================
        public static string AdbExe => Path.Combine(ToolsDir, "adb.exe");
        public static string FastbootExe => Path.Combine(ToolsDir, "fastboot.exe");
        public static string Zip7Exe => Path.Combine(ToolsDir, "7z.exe");
        public static string QSaharaServerExe => Path.Combine(ToolsDir, "QSaharaServer.exe");
        public static string FhLoaderExe => Path.Combine(ToolsDir, "fh_loader.exe");
        /// <summary>内置的 Huawei UPDATE.APP 解包器（由 huawei_firmware_extractor.py 用 PyInstaller 打包）。</summary>
        public static string FirmwareExtractorExe => Path.Combine(ToolsDir, "huawei_firmware_extractor.exe");
        /// <summary>分区镜像解包（extract.erofs）／打包（mkfs.erofs）。</summary>
        /// <summary>ext4 打包工具（tools 目录）。</summary>
        public static string Ext4ExtractExe => Path.Combine(PartFsToolsDir, "tik_ext4.exe");
        public static string Mke2fsExe => Path.Combine(PartFsToolsDir, "mke2fs.exe");
        public static string ExtractErofsExe => Path.Combine(PartFsToolsDir, "extract.erofs.exe");
        public static string MkfsErofsExe => Path.Combine(PartFsToolsDir, "mkfs.erofs.exe");
        public static string Simg2imgExe => Path.Combine(PartFsToolsDir, "simg2img.exe");
        public static string Img2simgExe => Path.Combine(PartFsToolsDir, "img2simg.exe");
        public static string Resize2fsExe => Path.Combine(PartFsToolsDir, "resize2fs.exe");
        /// <summary>super 动态分区打包（lpmake sparse）／解包（lpunpack）。</summary>
        public static string LpmakeExe => Path.Combine(PartSuperToolsDir, "lpmake.exe");
        public static string LpunpackExe => Path.Combine(PartSuperToolsDir, "lpunpack.exe");
        public static string AdbDriverInstaller => Path.Combine(DriverDir, "adbdriver.exe");
        public static string QcDriverInstaller => Path.Combine(DriverDir, "qcdriver.exe");
        public static string KptoolsExe => Path.Combine(ApatchToolsDir, "kptools.exe");
        public static string KpimgFile => Path.Combine(ApatchToolsDir, "kpimg-android");
        /// <summary>Magisk 修补工具（magiskboot 全流水线）。</summary>
        public static string MagiskbootExe => Path.Combine(MagiskToolsDir, "magiskboot.exe");
        /// <summary>Magisk 修补工具：MagiskPatcher.exe（一键修补，支持 apk/zip 与 boot/ramdisk）。</summary>
        public static string MagiskPatcherExe => Path.Combine(MagiskToolsDir, "MagiskPatcher.exe");
        public static string MagiskPatcherCsv => Path.Combine(MagiskToolsDir, "MagiskPatcher.csv");
        public static string MagiskinitFile => Path.Combine(MagiskToolsDir, "magiskinit");
        public static string Magisk64File => Path.Combine(MagiskToolsDir, "magisk64");
        public static string Magisk32File => Path.Combine(MagiskToolsDir, "magisk32"); // 可选
        public static string MagiskpolicyFile => Path.Combine(MagiskToolsDir, "magiskpolicy");
        public static string MagiskStubApk => Path.Combine(MagiskToolsDir, "stub.apk");
        public static string HiSuite1Exe => Path.Combine(HiSuiteProxyDir, "HiSuite1.exe");
        public static string HiSuiteProxyExe => Path.Combine(HiSuiteProxyDir, "HiSuite Proxy V3.exe");
        public static string MagiskApk => Path.Combine(ApksDir, "Magisk.apk");
        public static string ApatchApk => Path.Combine(ApksDir, "Apatch.apk");
        /// <summary>adb 工具集（可选外部工具，侧边栏 adb工具集 启动）。</summary>
        public static string AdbToolkitExe => Path.Combine(ToolsDir, "adbtoolkit", "adb-toolbox.exe");

        // ==================== 9008 回读全分区 ====================
        /// <summary>ptool.exe（可选）：存在时回读完成后自动生成分区表和 xml 文件到 gpt_and_xml\create。</summary>
        public static string PtoolExe => Path.Combine(ToolsDir, "ptool.exe");
        /// <summary>9008 备份默认保存目录。</summary>
        public static string BackupDir => Path.Combine(BaseDir, "backup");

        // ==================== 隐藏 Root 环境资源（放于 modules 目录，本地使用） ====================
        /// <summary>隐藏应用列表（HMA）安装包。</summary>
        public static string HideAppListApk => Path.Combine(ModulesDir, "隐藏应用列表.apk");
        /// <summary>隐藏应用列表配置（导入到设备）。</summary>
        public static string HideConfigJson => Path.Combine(ModulesDir, "config.json");
        /// <summary>密钥认证 APK。</summary>
        public static string KeyAuthApk => Path.Combine(ModulesDir, "密钥认证.apk");
        /// <summary>Luna APK。</summary>
        public static string LunaApk => Path.Combine(ModulesDir, "Luna.apk");
        /// <summary>TEE 模拟器模块。</summary>
        public static string TeeSimulatorZip => Path.Combine(ModulesDir, "TEESimulator-RS.zip");
        /// <summary>Zygisk Next 模块。</summary>
        public static string ZygiskNextZip => Path.Combine(ModulesDir, "Zygisk Next.zip");
        /// <summary>紫罗兰辅助模块。</summary>
        public static string VioletHelperZip => Path.Combine(ModulesDir, "紫罗兰辅助模块.zip");
        /// <summary>LSPosed 模块（v2.2.0-7854，替代旧的 LSPosed IT）。</summary>
        public static string LsposedZip => Path.Combine(ModulesDir, "LSPosed-v2.2.0-7854-release.zip");
        /// <summary>Shamiko 模块（Magisk 隐藏方案使用，APatch 不用）。</summary>
        public static string ShamikoZip => Path.Combine(ModulesDir, "Shamiko-v1.2.5-414-release.zip");

        // ==================== 固件/镜像文件 ====================
        public static string DevprgElf => Path.Combine(UnlockDir, "Huawei865870_devprg.elf");
        public static string AblUnlockImg => Path.Combine(UnlockDir, "Huawei865870_abl_unlock.img");
        public static string MiscImg => Path.Combine(RawprogramDir, "misc.img");

        // ==================== extractor 相关常量 ====================
        /// <summary>多个 sparse super 分片合并后的文件名。</summary>
        public const string MergedSuperFileName = "super_merged.img";

        // ==================== 其他常量 ====================
        /// <summary>官方系统包下载地址（下载官方系统 / 救砖）。</summary>
        public const string OfficialRomShareUrl = "https://ffusersubmission.byethost17.com/?i=2";
        /// <summary>底包下载地址（刷入第三方系统用）。</summary>
        public const string BasePackageUrl = "https://ffusersubmission.byethost17.com/?i=2";
        /// <summary>降级申请提交地址。</summary>
        public const string DowngradeSubmitUrl = "https://ffusersubmission.byethost17.com/";

        /// <summary>
        /// 配置 json 仓库的 raw 基地址（源码 master 分支下的 json/ 子目录）。
        /// 所有在线配置（system_config.json / optimize_modules.json / faq 的 {功能}.json）
        /// 均从该目录下载，统一在此维护，避免各服务地址漂移。
        /// 注意：原先挂在 release 附件下，release 已删除，改为直接读源码；
        /// 配置 json 统一放在仓库的 json/ 子目录，不再平铺在根目录。
        /// </summary>
        public const string ConfigRepoRawBaseUrl =
            "https://gitee.com/fanyantao4110/matepad11-system-config/raw/master/json/";

        /// <summary>创建所有必备目录（对应 bat 开头的 md 语句）。</summary>
        public static void EnsureDirectories()
        {
            foreach (var dir in new[]
            {
                ScreenshotsDir, SystemDir, ModulesDir, CacheDir, TmpDir, OutDir,
                BaseRomDir, RawprogramDir, LogDir, JsonDir,
                PartImgDir, PartImg1Dir, PartImg2Dir, PartImg3Dir, PartImgCfgDir
            })
            {
                try { Directory.CreateDirectory(dir); } catch { /* 忽略 */ }
            }
        }

        /// <summary>
        /// 检查核心工具是否齐全（对应 bat 开头的 exist 检查）。
        /// 返回缺失文件列表，空列表表示全部齐全。
        /// </summary>
        public static List<string> CheckCoreTools()
        {
            var missing = new List<string>();
            foreach (var f in new[] { Zip7Exe, FastbootExe, AdbExe, FirmwareExtractorExe })
            {
                if (!File.Exists(f)) missing.Add(f);
            }
            return missing;
        }

        /// <summary>清空并重建 cache 目录（对应 bat 中 rd /s /q cache + md cache）。</summary>
        public static void ResetCacheDir()
        {
            try
            {
                if (Directory.Exists(CacheDir)) Directory.Delete(CacheDir, true);
                Directory.CreateDirectory(CacheDir);
            }
            catch { /* 忽略占用异常 */ }
        }

        /// <summary>向 tmp 目录写入 XML 文件，返回完整路径。</summary>
        public static string WriteTmpFile(string fileName, string content)
        {
            Directory.CreateDirectory(TmpDir);
            var path = Path.Combine(TmpDir, fileName);
            File.WriteAllText(path, content);
            return path;
        }
    }
}
