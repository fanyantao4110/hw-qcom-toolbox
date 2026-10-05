using System.IO;
using System.Text.RegularExpressions;

namespace MatePadToolbox.Services
{
    /// <summary>
    /// UPDATE.APP / update.bin 解包服务：
    /// 封装内置的 huawei_firmware_extractor.exe（由 huawei_firmware_extractor.py 用 PyInstaller 打包），
    /// 用于把官方底包 / 官方系统包解析为各分区 img，并支持合并多个 sparse super 分片。
    /// </summary>
    public static class UpdateAppService
    {
        /// <summary>是否安装了内置解包器。</summary>
        public static bool IsAvailable => File.Exists(AppConfig.FirmwareExtractorExe);

        /// <summary>列出固件包内的全部分区名。</summary>
        /// <returns>成功返回分区名列表；失败返回 null。</returns>
        public static async Task<List<string>?> ListPartitionsAsync(
            string firmwarePath, Action<string> log, CancellationToken ct = default)
        {
            if (!IsAvailable)
            {
                log("[错误] 缺少 tools\\huawei_firmware_extractor.exe（UPDATE.APP 解包器未内置）。");
                return null;
            }
            if (!File.Exists(firmwarePath))
            {
                log($"[错误] 固件文件不存在：{firmwarePath}");
                return null;
            }

            log($"正在读取固件分区表：{Path.GetFileName(firmwarePath)}");
            var (code, output) = await ProcessRunner.RunCaptureAsync(
                AppConfig.FirmwareExtractorExe, $"\"{firmwarePath}\" --list", AppConfig.ToolsDir, ct);

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
            var detected = lines.FirstOrDefault(l => l.StartsWith("Detected:"));
            if (detected != null) log(detected);

            var names = new List<string>();
            foreach (var line in lines)
            {
                // 输出形如：   10  abl  587.1 KiB  0x1D6AB8（分区名不含 .img 后缀）
                var m = Regex.Match(line, @"^\s*\d+\s+([\w.\-]+)\s+\d", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    var name = m.Groups[1].Value;
                    if (name.EndsWith(".img", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                    if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                        names.Add(name);
                }
            }

            if (names.Count == 0)
            {
                log("[错误] 未能解析固件包分区列表。");
                log(output);
                return null;
            }
            log($"固件共 {names.Count} 个分区。");
            return names;
        }

        /// <summary>
        /// 解包固件到目标目录。
        /// </summary>
        /// <param name="partitionFilter">可选：只提取这些分区（文件名，去 .img）。为空则提取全部分区。</param>
        /// <param name="mergeSuper">是否合并多个 sparse super 分片为 super_merged.img。</param>
        /// <param name="overwrite">是否覆盖已存在的输出镜像。</param>
        /// <returns>解包产出的 .img 文件完整路径列表。</returns>
        public static async Task<List<string>?> ExtractAsync(
            string firmwarePath, string outDir, Action<string> log, CancellationToken ct = default,
            IEnumerable<string>? partitionFilter = null, bool mergeSuper = false, bool overwrite = false)
        {
            if (!IsAvailable)
            {
                log("[错误] 缺少 tools\\huawei_firmware_extractor.exe（UPDATE.APP 解包器未内置）。");
                return null;
            }
            if (!File.Exists(firmwarePath))
            {
                log($"[错误] 固件文件不存在：{firmwarePath}");
                return null;
            }

            var args = new List<string> { $"\"{firmwarePath}\"", $"-o \"{outDir}\"", "--overwrite" };
            if (partitionFilter is not null)
            {
                var fs = partitionFilter.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
                if (fs.Count > 0)
                    args.Add($"-p {string.Join(",", fs.Select(Path.GetFileNameWithoutExtension))}");
            }
            if (mergeSuper) args.Add("--merge-super");

            log($"正在解包固件：{Path.GetFileName(firmwarePath)} → {outDir}");
            var code = await ProcessRunner.RunAsync(
                AppConfig.FirmwareExtractorExe, string.Join(" ", args),
                line => log($"[extractor] {line}"), AppConfig.ToolsDir, ct);

            if (code != 0)
            {
                log($"[错误] 解包失败！（退出码 {code}）");
                return null;
            }

            var imgs = Directory.Exists(outDir)
                ? Directory.EnumerateFiles(outDir, "*.img", SearchOption.AllDirectories)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();
            if (imgs.Count == 0)
            {
                log("[错误] 解包完成但未找到任何 .img 镜像。");
                return null;
            }
            return imgs;
        }

        /// <summary>
        /// 判断某个文件是否为 super 分区镜像（含多个 sparse 分片，如 super、super_2、super_a/b 等，及合并产物 super_merged）。
        /// </summary>
        public static bool IsSuperImage(string filePath)
        {
            var n = Path.GetFileNameWithoutExtension(filePath)?.Trim();
            if (string.IsNullOrEmpty(n)) return false;
            n = n.ToLowerInvariant();
            return n == "super" || n.StartsWith("super_") || n.StartsWith("super.");
        }

        /// <summary>判断某个文件是否为 abl 分区镜像（abl / abl_a / abl_b 等）。</summary>
        public static bool IsAblImage(string filePath)
        {
            var n = Path.GetFileNameWithoutExtension(filePath)?.Trim();
            if (string.IsNullOrEmpty(n)) return false;
            n = n.ToLowerInvariant();
            return n == "abl" || n.StartsWith("abl.");
        }

        /// <summary>判断某个文件是否为 vbmeta 分区镜像（vbmeta、vbmeta_a/b 等）。</summary>
        public static bool IsVbmetaImage(string filePath)
        {
            var n = Path.GetFileNameWithoutExtension(filePath)?.Trim();
            return IsVbmetaPartitionName(n);
        }

        /// <summary>判断某个分区名是否为 vbmeta 分区（vbmeta、vbmeta_cust、vbmeta_hw_product、vbmeta_odm、vbmeta_vendor 等）。</summary>
        public static bool IsVbmetaPartitionName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            name = name.ToLowerInvariant();
            return name == "vbmeta" || name.StartsWith("vbmeta_") || name.StartsWith("vbmeta.");
        }

        /// <summary>
        /// 刷包时始终跳过的分区（既不能用于底包，也不能用于官方系统）。
        /// 命名与 extractor 输出的分区名一致（已去 .img 后缀）。
        /// </summary>
        private static readonly HashSet<string> SkippedPartitionNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "userdata", "sha256rsa", "package_type", "metadata", "efi", "crc", "base_verlist", "base_ver", "oeminfo",
        };

        /// <summary>判断某个文件是否为“始终跳过刷写”的分区镜像。</summary>
        public static bool IsSkippedImage(string filePath)
        {
            var n = Path.GetFileNameWithoutExtension(filePath)?.Trim();
            if (string.IsNullOrEmpty(n)) return false;
            return SkippedPartitionNames.Contains(n);
        }

        /// <summary>
        /// 从解包产物中收集“需刷入”的 img（默认排除所有 super 分片与 abl 分片，以及始终跳过的分区）。
        /// </summary>
        /// <param name="excludeSuper">排除 super 分片（不刷 super 时传 true）。</param>
        /// <param name="excludeAbl">排除 abl 分片。</param>
        public static List<string> FilterImages(
            IEnumerable<string> images, Action<string> log,
            bool excludeSuper = true, bool excludeAbl = true)
        {
            var result = new List<string>();
            foreach (var img in images.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(img);
                if (IsSkippedImage(img))
                {
                    log($"[跳过] 分区：{name}");
                    continue;
                }
                if (excludeSuper && IsSuperImage(img))
                {
                    log($"[跳过] super 分片：{name}");
                    continue;
                }
                if (excludeAbl && IsAblImage(img))
                {
                    log($"[跳过] abl 分片：{name}");
                    continue;
                }
                result.Add(img);
            }
            return result;
        }
    }
}
