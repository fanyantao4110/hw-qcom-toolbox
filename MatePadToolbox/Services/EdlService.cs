using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MatePadToolbox.Services
{
    /// <summary>
    /// 高通 9008（EDL）模式操作服务：
    /// 封装 QSaharaServer（上传 firehose 编程器）与 fh_loader（端口配置/发送 XML/重启）。
    /// 对应 bat 中反复出现的 9008 刷机流程。
    /// </summary>
    public static class EdlService
    {
        // ==================== XML 模板（与 bat 中 echo 生成的内容完全一致） ====================

        /// <summary>端口配置 XML。</summary>
        public const string ConfigureXml =
            "<?xml version=\"1.0\" ?><data><configure MemoryName=\"ufs\" Verbose=\"0\" AlwaysValidate=\"0\" " +
            "MaxDigestTableSizeInBytes=\"8192\" MaxPayloadSizeToTargetInBytes=\"1048576\" ZlpAwareHost=\"1\" " +
            "SkipStorageInit=\"0\" /></data>";

        /// <summary>
        /// 解锁 BL：写入已解锁 ABL 镜像的 XML。
        /// </summary>
        /// <param name="ablImgName">ABL 解锁镜像文件名（仅取文件名，防目录穿越）。</param>
        public static string BuildUnlockAblXml(string ablImgName)
        {
            var fileName = string.IsNullOrWhiteSpace(ablImgName)
                ? "Huawei865870_abl_unlock.img"
                : Path.GetFileName(ablImgName);
            return "<?xml version=\"1.0\" ?><data><program filename=\"" + fileName + "\" label=\"abl\" " +
                   "physical_partition_number=\"4\" start_sector=\"49670\" num_partition_sectors=\"1024\" " +
                   "SECTOR_SIZE_IN_BYTES=\"4096\"/></data>";
        }

        /// <summary>
        /// 解锁 BL：按运行时从设备分区表解析出的 ABL 分区位置动态构造写入 XML。
        /// 替代 BuildUnlockAblXml 中写死的骁龙865分区位置（physical_partition_number=4 / start_sector=49670），
        /// 从而适配不同机型的 ABL 分区实际所在 LUN 与扇区。
        /// </summary>
        /// <param name="ablImgName">ABL 解锁镜像文件名（仅取文件名，防目录穿越）。</param>
        /// <param name="lun">ABL 分区所在 LUN（physical_partition_number）。</param>
        /// <param name="startSector">ABL 分区起始扇区。</param>
        /// <param name="sizeSectors">ABL 分区扇区数。</param>
        /// <param name="sectorSize">设备扇区大小（字节，ufs 通常 4096，emmc 通常 512）。</param>
        public static string BuildWriteAblXml(string ablImgName, int lun, ulong startSector, ulong sizeSectors, int sectorSize)
        {
            var fileName = string.IsNullOrWhiteSpace(ablImgName)
                ? "Huawei865870_abl_unlock.img"
                : Path.GetFileName(ablImgName);
            return $"<?xml version=\"1.0\" ?><data><program filename=\"{fileName}\" label=\"abl\" " +
                   $"physical_partition_number=\"{lun}\" start_sector=\"{startSector}\" " +
                   $"num_partition_sectors=\"{sizeSectors}\" SECTOR_SIZE_IN_BYTES=\"{sectorSize}\"/></data>";
        }

        /// <summary>重启设备 XML。</summary>
        public const string RebootXml =
            "<?xml version=\"1.0\" ?><data><power DelayInSeconds=\"0\" value=\"reset\" /></data>";

        /// <summary>写入 misc 分区的 XML。</summary>
        public const string MiscXml =
            "<?xml version=\"1.0\" ?><data><program filename=\"misc.img\" label=\"misc\" " +
            "physical_partition_number=\"0\" start_sector=\"9480\" num_partition_sectors=\"512\" " +
            "SECTOR_SIZE_IN_BYTES=\"4096\"/></data>";

        private static string ComPath(int port) => $@"\\.\COM{port}";

        // ==================== 9008 会话状态（供跨步骤复用连接） ====================
        // 一次 9008 会话 = 进入 9008 + 上传 firehose 编程器 + 配置端口。
        // 会话建立后设备停留在 firehose 模式，可直接继续做多次读/写，无需中途重启设备
        // （重启会让设备回到系统，后续刷入又得重新进入 9008，对鸿蒙4 用户尤其麻烦）。

        private static int _activeSessionPort = -1;

        /// <summary>当前已就绪的 9008 会话端口；-1 表示无。</summary>
        public static int ActiveSessionPort => _activeSessionPort;

        /// <summary>登记一个已就绪的 9008 会话（编程器已上传、端口已配置）。</summary>
        public static void SetActiveSession(int port) => _activeSessionPort = port;

        /// <summary>清除会话状态（设备已重启/断开时调用）。</summary>
        public static void ClearActiveSession() => _activeSessionPort = -1;

        /// <summary>
        /// 尝试复用已建立的 9008 会话：设备仍在 9008 且 COM 口与登记一致时返回该端口，否则返回 null。
        /// 端口已被占用或设备已离开 9008 时会自动清除失效的会话状态。
        /// </summary>
        public static int? TryReuseActiveSession()
        {
            if (_activeSessionPort < 0) return null;
            var current = ComPortDetector.Find9008Port();
            if (current.HasValue && current.Value == _activeSessionPort) return _activeSessionPort;
            ClearActiveSession();
            return null;
        }

        /// <summary>
        /// 通过 QSaharaServer 上传 firehose 编程器。
        /// </summary>
        /// <param name="devprgPath">可选：要上传的引导 .elf 完整路径；为 null 时使用默认 骁龙865 文件（不随全局型号变化）。</param>
        public static async Task<bool> UploadFirehoseAsync(int port, Action<string> log, CancellationToken ct = default, string? devprgPath = null)
        {
            var elf = devprgPath ?? AppConfig.DevprgElf;
            if (!File.Exists(AppConfig.QSaharaServerExe))
            {
                log("[错误] 缺少 tools\\QSaharaServer.exe");
                return false;
            }
            if (!File.Exists(elf))
            {
                log($"[错误] 缺少底层文件 {elf}");
                return false;
            }

            log("开始上传编程器...");
            var args = $"-p {ComPath(port)} -s 13:\"{elf}\"";
            var code = await ProcessRunner.RunAsync(AppConfig.QSaharaServerExe, args,
                line => log($"[Sahara] {line}"), AppConfig.ToolsDir, ct);
            if (code != 0)
            {
                log("[错误] 上传编程器失败!");
                return false;
            }
            log("[OK] 编程器上传成功。");
            return true;
        }

        /// <summary>
        /// 使用 fh_loader 配置端口。
        /// </summary>
        /// <param name="searchPath">镜像搜索目录（对应 --search_path）</param>
        public static async Task<bool> ConfigurePortAsync(int port, string searchPath, Action<string> log, CancellationToken ct = default)
        {
            if (!File.Exists(AppConfig.FhLoaderExe))
            {
                log("[错误] 缺少 tools\\fh_loader.exe");
                return false;
            }

            var xmlPath = AppConfig.WriteTmpFile("configure.xml", ConfigureXml);
            log("正在配置端口...");
            var args = $"--port={ComPath(port)} --memoryname=ufs --configure=\"{xmlPath}\" " +
                       $"--search_path=\"{searchPath}\" --mainoutputdir=\"{AppConfig.LogDir}\" --noprompt";
            var code = await ProcessRunner.RunAsync(AppConfig.FhLoaderExe, args,
                line => log($"[fh_loader] {line}"), AppConfig.ToolsDir, ct);
            if (code != 0)
            {
                log("[错误] 配置端口失败!");
                return false;
            }
            log("[OK] 端口配置成功。");
            return true;
        }

        /// <summary>
        /// 自动配置端口（对应原版 write.bat qcedlsendfh 的 auto 配置端口步骤）：
        /// 依次尝试 ufs / emmc / spinor，每次由 fh_loader 自动发送 configure 初始化存储
        /// （不传 --skip_configure），并依据 port_trace.txt 中的 configure ACK 判断是否成功。
        /// 上传编程器后必须先执行此步骤，后续带 --skip_configure 的读取/回读才能访问存储。
        /// </summary>
        public static async Task<bool> ConfigurePortAutoAsync(int port, Action<string> log, CancellationToken ct = default)
        {
            if (!File.Exists(AppConfig.FhLoaderExe))
            {
                log("[错误] 缺少 tools\\fh_loader.exe");
                return false;
            }

            (string Mem, int SecSize, int Sectors)[] probes =
            {
                ("ufs", 4096, 6),
                ("emmc", 512, 34),
                ("spinor", 4096, 6),
            };

            foreach (var (mem, secSize, sectors) in probes)
            {
                ct.ThrowIfCancellationRequested();
                log($"尝试{mem}模式配置端口...");
                try
                {
                    var traceFile = Path.Combine(AppConfig.TmpDir, "port_trace.txt");
                    try { if (File.Exists(traceFile)) File.Delete(traceFile); } catch { }

                    var xml = AppConfig.WriteTmpFile("configure_probe.xml",
                        $"<?xml version=\"1.0\" ?><data><program SECTOR_SIZE_IN_BYTES=\"{secSize}\" filename=\"tmp.bin\" " +
                        $"physical_partition_number=\"0\" label=\"PrimaryGPT\" start_sector=\"0\" num_partition_sectors=\"{sectors}\" /></data>");

                    var args = $"--port={ComPath(port)} --memoryname={mem} --sendxml=\"{xml}\" --convertprogram2read " +
                               $"--mainoutputdir=\"{AppConfig.TmpDir}\" --noprompt";
                    await ProcessRunner.RunAsync(AppConfig.FhLoaderExe, args,
                        line => log($"[fh_loader] {line}"), AppConfig.ToolsDir, ct);

                    if (File.Exists(traceFile))
                    {
                        var trace = File.ReadAllText(traceFile);
                        if (trace.Contains("Got the ACK for the <configure>") ||
                            trace.Contains("Target returned NAK for your <configure> but it does not seem to be an error"))
                        {
                            log($"[OK] {mem}模式配置端口成功。");
                            return true;
                        }
                    }
                    log($"{mem}模式配置端口失败。");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    log($"[警告] {mem}模式配置端口异常：{ex.Message}");
                }
            }

            log("[错误] 自动配置端口失败（已尝试 ufs / emmc / spinor）。");
            return false;
        }

        /// <summary>
        /// 使用 fh_loader 发送一个 rawprogram XML 到设备。
        /// </summary>
        /// <param name="memoryName">存储类型（ufs/emmc/spinor），默认 ufs；通用机型写入时请按设备实际存储类型传入。</param>
        public static async Task<bool> SendXmlAsync(
            int port, string xmlPath, string searchPath, Action<string> log, CancellationToken ct = default,
            string memoryName = "ufs")
        {
            var args = $"--port={ComPath(port)} --memoryname={memoryName} --sendxml=\"{xmlPath}\" " +
                       $"--search_path=\"{searchPath}\" --mainoutputdir=\"{AppConfig.LogDir}\" " +
                       $"--skip_configure --noprompt";
            var code = await ProcessRunner.RunAsync(AppConfig.FhLoaderExe, args,
                line => log($"[fh_loader] {line}"), AppConfig.ToolsDir, ct);
            return code == 0;
        }

        /// <summary>
        /// 写入临时 XML 文件并发送到设备。
        /// </summary>
        /// <param name="memoryName">存储类型（ufs/emmc/spinor），默认 ufs；通用机型写入时请按设备实际存储类型传入。</param>
        public static async Task<bool> SendXmlContentAsync(
            int port, string fileName, string xmlContent, string searchPath, Action<string> log, CancellationToken ct = default,
            string memoryName = "ufs")
        {
            var xmlPath = AppConfig.WriteTmpFile(fileName, xmlContent);
            return await SendXmlAsync(port, xmlPath, searchPath, log, ct, memoryName);
        }

        /// <summary>
        /// 通过 9008 重启设备。
        /// </summary>
        public static async Task<bool> RebootDeviceAsync(int port, string searchPath, Action<string> log, CancellationToken ct = default)
        {
            log("正在重启设备...");
            var ok = await SendXmlContentAsync(port, "reboot.xml", RebootXml, searchPath, log, ct);
            if (ok) log("[OK] 重启指令已发送。");
            return ok;
        }

        /// <summary>ABL 分区在设备分区表中的位置信息。</summary>
        public sealed record AblPartitionInfo(
            string MemType, int Lun, int SectorSize, ulong StartSector, ulong SizeSectors);

        /// <summary>
        /// 遍历设备各 LUN 回读主 GPT，按分区名查找 ABL 分区。
        /// 对应高通工具箱 write.bat :QCEDL 的“info qcedl → partable readgpt → 按分区名查找”逻辑，
        /// 用于在不固定内置 xml 的前提下适配不同机型（替代写死 physical_partition_number=4 / start_sector=49670）。
        /// </summary>
        /// <returns>找到返回 ABL 分区位置信息，未找到返回 null。</returns>
        public static async Task<AblPartitionInfo?> FindAblPartitionAsync(
            int port, Action<string> log, CancellationToken ct = default)
        {
            var info = await EdlBackupService.DetectMemoryInfoAsync(port, log, ct);
            if (info == null) return null;
            log($"存储类型:{info.MemType}  扇区大小:{info.SectorSize}b  lun总数:{info.LunNum}");

            for (int lun = 0; lun < info.LunNum; lun++)
            {
                ct.ThrowIfCancellationRequested();
                var gptPath = Path.Combine(AppConfig.TmpDir, $"gpt_main_abl{lun}.bin");
                try { if (File.Exists(gptPath)) File.Delete(gptPath); } catch { }

                log($"正在读取分区表{lun}，查找 ABL 分区...");
                if (!await EdlBackupService.ReadGptAsync(port, info, lun, false, gptPath, log, ct))
                    continue;

                var parts = EdlBackupService.ParseGpt(File.ReadAllBytes(gptPath), info.SectorSize);

                // 优先精确匹配 "abl"，兼容 A/B 槽位回退匹配以 "abl" 开头的分区（如 abl_a / abl_b）
                EdlBackupService.GptPartition? abl = null;
                foreach (var p in parts)
                {
                    if (string.Equals(p.Name.Trim(), "abl", StringComparison.OrdinalIgnoreCase)) { abl = p; break; }
                }
                if (abl == null)
                {
                    foreach (var p in parts)
                    {
                        if (p.Name.Trim().StartsWith("abl", StringComparison.OrdinalIgnoreCase)) { abl = p; break; }
                    }
                }

                if (abl == null)
                {
                    log($"分区表{lun}中未找到 ABL 分区（共 {parts.Count} 个分区）。");
                    continue;
                }

                log($"[OK] 找到 ABL 分区：“{abl.Name}”位于 LUN{lun}，起始扇区 {abl.StartSector}，" +
                    $"扇区数 {abl.SizeSectors}（约 {abl.SizeSectors * (ulong)info.SectorSize / 1024 / 1024}MB）。");
                return new AblPartitionInfo(info.MemType, lun, info.SectorSize, abl.StartSector, abl.SizeSectors);
            }

            log("[错误] 未在设备分区表中找到 ABL 分区。");
            return null;
        }

        /// <summary>某个分区在设备分区表中的位置信息。</summary>
        public sealed record PartitionSlot(int Lun, int SectorSize, ulong StartSector, ulong SizeSectors);

        /// <summary>
        /// 探测设备存储并读取所有 LUN 的主分区表，返回「分区名→位置」映射。
        /// 用于在不依赖外置 rawprogram xml 的前提下，为任意机型动态定位待刷分区。
        /// </summary>
        /// <returns>成功返回 (存储类型, 分区映射)；探测/读取失败返回 null。</returns>
        public static async Task<(string MemType, Dictionary<string, PartitionSlot> Map)?> LoadPartitionTableAsync(
            int port, Action<string> log, CancellationToken ct = default)
        {
            var info = await EdlBackupService.DetectMemoryInfoAsync(port, log, ct);
            if (info == null) return null;
            log($"存储类型:{info.MemType}  扇区大小:{info.SectorSize}b  lun总数:{info.LunNum}");

            var map = new Dictionary<string, PartitionSlot>(StringComparer.OrdinalIgnoreCase);
            for (int lun = 0; lun < info.LunNum; lun++)
            {
                ct.ThrowIfCancellationRequested();
                var gptPath = Path.Combine(AppConfig.TmpDir, $"gpt_map{lun}.bin");
                try { if (File.Exists(gptPath)) File.Delete(gptPath); } catch { }

                log($"正在读取分区表{lun}...");
                if (!await EdlBackupService.ReadGptAsync(port, info, lun, false, gptPath, log, ct))
                    continue;

                var parts = EdlBackupService.ParseGpt(File.ReadAllBytes(gptPath), info.SectorSize);
                foreach (var p in parts)
                    map[p.Name] = new PartitionSlot(lun, info.SectorSize, p.StartSector, p.SizeSectors);
                log($"分区表{lun}解析完成，共 {parts.Count} 个分区。");
            }
            log($"分区表读取完成，共建立 {map.Count} 个分区映射。");
            return (info.MemType, map);
        }

        /// <summary>
        /// 只生成单个写入用的 program 元素片段（不含 xml 声明与 data 外壳）。
        /// 用于在一个 data 节点内拼接多个分区条目；切勿把 BuildWritePartitionXml 的返回值
        /// （那是完整的 XML 文档）塞进 data，否则会嵌套 data 标签，
        /// 导致 fh_loader 报 “Unrecognized tag 'data' / XML is not formatted correctly. Could not find closing />”。
        /// </summary>
        public static string BuildProgramElement(string imgFileName, PartitionSlot slot)
        {
            var fileName = Path.GetFileName(imgFileName);
            var name = Path.GetFileNameWithoutExtension(fileName);
            return $"<program filename=\"{fileName}\" label=\"{name}\" " +
                   $"physical_partition_number=\"{slot.Lun}\" start_sector=\"{slot.StartSector}\" " +
                   $"num_partition_sectors=\"{slot.SizeSectors}\" SECTOR_SIZE_IN_BYTES=\"{slot.SectorSize}\"/>";
        }

        /// <summary>为单个分区按其实际位置动态构造写入 XML（完整文档，可直接落盘发送）。</summary>
        public static string BuildWritePartitionXml(string imgFileName, PartitionSlot slot)
        {
            return "<?xml version=\"1.0\" ?><data>" + BuildProgramElement(imgFileName, slot) + "</data>";
        }

        /// <summary>
        /// 把若干 program 片段包装成一份完整的 rawprogram XML 文档。
        /// 必须带 xml 声明：fh_loader 自带的精简解析器依赖声明识别文档起始，
        /// 缺失会直接报 “XML is not formatted correctly. Could not find closing />”。
        /// 格式与原版 ptool 生成的 rawprogram 保持一致（声明 + data + 每行一个 program，无缩进）。
        /// </summary>
        public static string WrapRawProgramXml(IEnumerable<string> programElements)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" ?>").Append(Environment.NewLine);
            sb.Append("<data>").Append(Environment.NewLine);
            foreach (var e in programElements)
                sb.Append(e).Append(Environment.NewLine);
            sb.Append("</data>").Append(Environment.NewLine);
            return sb.ToString();
        }

        /// <summary>为单个分区按其实际位置动态构造回读（读取）XML，配合 fh_loader 的 --convertprogram2read 使用。</summary>
        public static string BuildReadPartitionXml(string imgFileName, PartitionSlot slot)
        {
            var fileName = Path.GetFileName(imgFileName);
            var name = Path.GetFileNameWithoutExtension(fileName);
            return $"<?xml version=\"1.0\" ?><data><program filename=\"{fileName}\" label=\"{name}\" " +
                   $"physical_partition_number=\"{slot.Lun}\" start_sector=\"{slot.StartSector}\" " +
                   $"num_partition_sectors=\"{slot.SizeSectors}\" SECTOR_SIZE_IN_BYTES=\"{slot.SectorSize}\" sparse=\"false\"/></data>";
        }

        /// <summary>
        /// 从 9008 设备按分区表动态回读单个分区到指定目录。
        /// </summary>
        /// <param name="outDir">保存目录，读取结果命名为 {partName}.img。</param>
        /// <returns>成功返回镜像完整路径，失败返回 null。</returns>
        public static async Task<string?> ReadPartitionAsync(
            int port, string memoryName, string partitionName,
            Dictionary<string, PartitionSlot> map, string outDir,
            Action<string> log, CancellationToken ct = default)
        {
            if (!map.TryGetValue(partitionName, out var slot))
            {
                log($"[错误] 设备分区表中未找到 {partitionName} 分区。");
                return null;
            }
            log($"[OK] 已定位 {partitionName} 分区：LUN{slot.Lun}，起始扇区 {slot.StartSector}，" +
                $"扇区数 {slot.SizeSectors}（约 {slot.SizeSectors * (ulong)slot.SectorSize / 1024 / 1024}MB）。");

            Directory.CreateDirectory(outDir);
            var imgFileName = partitionName + ".img";
            var xml = AppConfig.WriteTmpFile($"read_{partitionName}.xml", BuildReadPartitionXml(imgFileName, slot));

            log($"正在读取 {partitionName} 分区（可能需要几分钟）...");
            var args = $"--port={ComPath(port)} --memoryname={memoryName} --sendxml=\"{xml}\" " +
                       $"--convertprogram2read --mainoutputdir=\"{outDir}\" --skip_configure --noprompt";
            var code = await ProcessRunner.RunAsync(AppConfig.FhLoaderExe, args,
                line => log($"[fh_loader] {line}"), AppConfig.ToolsDir, ct);

            var imgPath = Path.Combine(outDir, imgFileName);
            if (code != 0 || !File.Exists(imgPath) || new FileInfo(imgPath).Length == 0)
            {
                log($"[错误] 读取 {partitionName} 分区失败！");
                return null;
            }
            log($"[OK] {partitionName} 提取成功，已保存到：{imgPath}");
            return imgPath;
        }

        /// <summary>
        /// 将镜像按分区表动态定位后写入 9008 设备（替代写死 LUN/扇区的旧 xml）。
        /// </summary>
        /// <param name="searchPath">镜像所在目录，fh_loader 在此查找 {partName}.img。</param>
        public static async Task<bool> WritePartitionAsync(
            int port, string memoryName, string partitionName,
            Dictionary<string, PartitionSlot> map, string searchPath,
            Action<string> log, CancellationToken ct = default)
        {
            if (!map.TryGetValue(partitionName, out var slot))
            {
                log($"[错误] 设备分区表中未找到 {partitionName} 分区。");
                return false;
            }
            log($"[OK] 已定位 {partitionName} 分区：LUN{slot.Lun}，起始扇区 {slot.StartSector}，正在刷写...");
            var ok = await SendXmlContentAsync(port, $"write_{partitionName}.xml",
                BuildWritePartitionXml(partitionName + ".img", slot), searchPath, log, ct, memoryName);
            if (ok) log($"[OK] {partitionName} 刷写成功。");
            else log($"[错误] {partitionName} 刷写失败！");
            return ok;
        }

        /// <summary>动态刷写一组镜像的结果统计。</summary>
        public sealed record FlashImageResult(int SuccessCount, List<string> FailedParts);

        /// <summary>
        /// 依据分区表映射，将一组镜像按 LUN 分组动态生成 rawprogram{LUN}.xml 并逐份刷写，
        /// 替代依赖外置 rawprogram0-5.xml 的固定底包刷写方式。
        /// </summary>
        /// <param name="imagePaths">待刷写镜像的完整路径（分区名取文件名去扩展名）。</param>
        public static async Task<FlashImageResult> FlashImagesAsync(
            int port, string memoryName, string searchPath,
            Dictionary<string, PartitionSlot> map, IEnumerable<string> imagePaths,
            Action<string> log, CancellationToken ct = default)
        {
            int success = 0;
            var failed = new List<string>();

            // 按分区名匹配，按 LUN 分组
            var byLun = new SortedDictionary<int, List<(string Img, PartitionSlot Slot)>>();
            foreach (var img in imagePaths)
            {
                ct.ThrowIfCancellationRequested();
                var part = Path.GetFileNameWithoutExtension(img);
                if (map.TryGetValue(part, out var slot))
                {
                    if (!byLun.TryGetValue(slot.Lun, out var list))
                        byLun[slot.Lun] = list = new List<(string, PartitionSlot)>();
                    list.Add((img, slot));
                }
                else
                {
                    failed.Add(part);
                    log($"[警告] 分区表中未找到与镜像 {part} 匹配的分区，跳过。");
                }
            }

            foreach (var kv in byLun)
            {
                ct.ThrowIfCancellationRequested();
                int lun = kv.Key;
                // 每个分区只取 program 片段，由 WrapRawProgramXml 统一套一层 data，
                // 避免把完整文档当作片段拼接而产生嵌套 data 标签。
                var elements = kv.Value.Select(x => BuildProgramElement(x.Img, x.Slot)).ToList();
                var xml = WrapRawProgramXml(elements);

                var xmlPath = AppConfig.WriteTmpFile($"rawprogram{lun}.xml", xml);
                log($"正在刷写 LUN{lun} 分区组（{kv.Value.Count} 个分区）...");
                var ok = await SendXmlAsync(port, xmlPath, searchPath, log, ct, memoryName);
                log(ok ? $"[OK] LUN{lun} 分区组刷写成功。" : $"[错误] LUN{lun} 分区组刷写失败！");
                if (ok) success += kv.Value.Count; else failed.AddRange(kv.Value.Select(x => x.Img));
            }

            return new FlashImageResult(success, failed);
        }

        /// <summary>
        /// 完整的 9008 连接流程：（可选 ADB 重启进 9008）→ 等待端口 → 上传编程器 + 配置端口。
        /// 对应 bat 的 :q9008_do_connect，并按解锁BL的逻辑支持鸿蒙2-3 自动进入 / 鸿蒙4 手动进入。
        /// </summary>
        /// <param name="rebootViaAdb">true=鸿蒙2-3：先等 ADB 设备再 adb reboot edl；false=鸿蒙4：等待用户手动进入 9008</param>
        /// <param name="devprgPath">可选：要上传的引导 .elf 完整路径；为 null 时使用默认 骁龙865 文件。</param>
        public static async Task<int> ConnectAsync(
            bool rebootViaAdb, Action<string> log, CancellationToken ct = default, string? devprgPath = null)
        {
            var elf = devprgPath ?? AppConfig.DevprgElf;
            if (!File.Exists(elf))
            {
                log($"[错误] 缺少编程文件 {elf}");
                return -1;
            }

            if (rebootViaAdb)
            {
                log("检测 ADB 设备...");
                if (!await DeviceService.WaitForAdbAsync(log, ct)) return -1;
                log("[OK] 设备已连接。");
                await DeviceService.RebootToEdlAsync(log);
                log("等待设备进入 9008 模式...");
            }
            else
            {
                log("请确认设备已进入 9008 模式（鸿蒙4 需通过工程线/短接方式进入），正在等待设备连接...");
            }

            var port = await ComPortDetector.WaitFor9008Async(15, 2000, log, ct);
            if (!port.HasValue)
            {
                log("[错误] 等待超时，未检测到 9008 设备。");
                if (!rebootViaAdb) log("请重新确认已通过探针/短接方式进入 9008。");
                return -1;
            }
            log($"[OK] 已检测到 9008 设备，端口 COM{port.Value}");

            if (!await UploadFirehoseAsync(port.Value, log, ct, elf)) return -1;
            if (!await ConfigurePortAsync(port.Value, AppConfig.CacheDir, log, ct)) return -1;
            return port.Value;
        }
    }
}
