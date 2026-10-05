using System.IO;
using System.Text;
using System.Text.Json;
using MatePadToolbox.Models;

namespace MatePadToolbox.Services
{
    /// <summary>
    /// 高级工具箱服务：
    /// 读取处理器配置 unlock\processors.json（各处理器对应的引导文件）；
    /// 提供「连接9008并发送引导」流程：
    ///   按系统版本进入 9008（鸿蒙2-3 走 ADB 自动进入；鸿蒙4 需工程线/短接手动进入）
    ///   → 上传 firehose 引导编程器（unlock 目录下的 .elf）→ 配置端口。
    /// 仅下发引导，不写入任何 ABL/解锁镜像。
    /// </summary>
    public static class AdvancedToolService
    {
        /// <summary>处理器配置文件名（位于 json 目录）。</summary>
        public const string ProcessorConfigFileName = "processors.json";

        /// <summary>处理器配置本地完整路径。</summary>
        public static string GetProcessorConfigPath() =>
            Path.Combine(AppConfig.JsonDir, ProcessorConfigFileName);

        /// <summary>unlock 目录下某文件名的完整路径（防目录穿越）。</summary>
        public static string FilePathInUnlock(string fileName)
        {
            var name = Path.GetFileName(fileName);
            return Path.Combine(AppConfig.UnlockDir, name);
        }

        /// <summary>
        /// 解析处理器配置 json。
        /// 兼容：A) { "processors": [ ... ] }；B) 顶层直接为数组 [ ... ]。
        /// </summary>
        public static List<ProcessorEntry> ParseProcessors(string jsonContent)
        {
            var list = new List<ProcessorEntry>();
            if (string.IsNullOrWhiteSpace(jsonContent)) return list;

            try
            {
                using var doc = JsonDocument.Parse(jsonContent);
                var root = doc.RootElement;

                JsonElement arr = default;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("processors", out var p) &&
                    p.ValueKind == JsonValueKind.Array)
                {
                    arr = p;
                }
                else if (root.ValueKind == JsonValueKind.Array)
                {
                    arr = root;
                }
                else
                {
                    return list;
                }

                foreach (var el in arr.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;
                    var e = new ProcessorEntry();
                    foreach (var prop in el.EnumerateObject())
                    {
                        if (prop.Value.ValueKind != JsonValueKind.String) continue;
                        var v = prop.Value.GetString() ?? "";
                        switch (prop.Name.ToLowerInvariant())
                        {
                            case "name": e.Name = v; break;
                            case "chip": case "soc": e.Chip = v; break;
                            case "devprg": case "firehose": case "elf": e.Devprg = v; break;
                            case "ablunlock": case "abl_unlock": case "abl": case "unlockimg": e.AblUnlock = v; break;
                            case "description": case "desc": e.Description = v; break;
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(e.Name)) list.Add(e);
                }
            }
            catch { /* 忽略无效内容 */ }

            return list;
        }

        /// <summary>
        /// 加载处理器列表：优先读本地 processors.json；缺失时给出内置骁龙865默认值。
        /// </summary>
        public static List<ProcessorEntry> LoadProcessors(Action<string> log)
        {
            var path = GetProcessorConfigPath();
            if (File.Exists(path))
            {
                try
                {
                    var content = RomConfigService.ReadTextAutoDetect(path);
                    var list = ParseProcessors(content);
                    if (list.Count > 0)
                    {
                        log($"[OK] 已加载处理器配置：{path}");
                        return list;
                    }
                    log("[警告] processors.json 内容为空，使用内置默认配置。");
                }
                catch (Exception ex)
                {
                    log($"[错误] 解析 processors.json 失败：{ex.Message}");
                }
            }
            else
            {
                log($"[提示] 未找到处理器配置 {path}，使用内置默认配置（骁龙865）。可创建该文件以扩展其它处理器。");
            }

            return DefaultProcessors();
        }

        /// <summary>内置默认处理器：骁龙865（对应现有 unlock 目录引导文件）。</summary>
        public static List<ProcessorEntry> DefaultProcessors() => new()
        {
            new ProcessorEntry
            {
                Name = "骁龙865",
                Chip = "SM8250 / MatePad 11 (DBY-W09)",
                Devprg = "Huawei865870_devprg.elf",
                AblUnlock = "Huawei865870_abl_unlock.img",
                Description = "内置处理器。引导文件（firehose .elf）与 ABL 解锁镜像位于 unlock 目录。",
            }
        };

        /// <summary>
        /// 检查某处理器在 unlock 目录下的引导文件是否齐全。
        /// </summary>
        public static (bool Ok, List<string> Missing, string DevprgPath) ValidateFiles(ProcessorEntry proc)
        {
            var missing = new List<string>();
            var devprgPath = string.Empty;

            if (!string.IsNullOrWhiteSpace(proc.Devprg))
            {
                devprgPath = FilePathInUnlock(proc.Devprg);
                if (!File.Exists(devprgPath)) missing.Add(proc.Devprg);
            }
            else missing.Add("firehose(.elf)");

            // ABL 解锁镜像（解锁BL 使用）：
            if (!string.IsNullOrWhiteSpace(proc.AblUnlock))
            {
                var ablPath = FilePathInUnlock(proc.AblUnlock);
                if (!File.Exists(ablPath)) missing.Add(proc.AblUnlock);
            }
            else missing.Add("ABL_unlock(.img)");

            return (missing.Count == 0, missing, devprgPath);
        }

        /// <summary>
        /// 「连接9008并发送引导」完整流程：仅下发 firehose 引导，不写入 ABL/解锁镜像。
        /// </summary>
        /// <param name="rebootViaAdb">true=鸿蒙2-3（ADB 自动进入9008）；false=鸿蒙4（工程线/短接手动进入）</param>
        public static async Task<bool> SendBootAsync(
            ProcessorEntry proc, bool rebootViaAdb, Action<string> log, CancellationToken ct = default)
        {
            // 1. 引导文件检查
            var (ok, missing, devprgPath) = ValidateFiles(proc);
            if (!ok)
            {
                log($"[错误] 处理器「{proc.Name}」缺少引导文件：{string.Join("、", missing)}");
                log("[提示] 请将对应 firehose 引导文件放入 unlock 目录，或检查 processors.json 配置。");
                return false;
            }
            log($"[OK] 引导文件检查通过：{Path.GetFileName(devprgPath)}");

            // 2. 进入 9008 模式
            if (rebootViaAdb)
            {
                log("检测 ADB 设备...");
                if (!await DeviceService.WaitForAdbAsync(log, ct)) return false;
                log("[OK] 设备已连接。");
                await DeviceService.RebootToEdlAsync(log);
                log("等待设备进入 9008 模式...");
            }
            else
            {
                log("等待设备进入 9008 模式（请确保已通过工程线/短接进入）...");
            }

            var port = await ComPortDetector.WaitFor9008Async(15, 2000, log, ct);
            if (!port.HasValue)
            {
                log("[错误] 等待超时，未检测到 9008 设备。");
                if (!rebootViaAdb) log("请重新确认已通过探针/短接方式进入 9008。");
                return false;
            }
            log($"[OK] 检测到 9008 设备，端口 COM{port.Value}");

            // 3. 上传 firehose 引导（即“发送引导”，通过 QSaharaServer 下发 .elf）
            if (!File.Exists(AppConfig.QSaharaServerExe))
            {
                log("[错误] 缺少 tools\\QSaharaServer.exe");
                return false;
            }
            log($"正在发送引导：{Path.GetFileName(devprgPath)} ...");
            var args = $"-p COM{port.Value} -s 13:\"{devprgPath}\"";
            var code = await ProcessRunner.RunAsync(AppConfig.QSaharaServerExe, args,
                line => log($"[Sahara] {line}"), AppConfig.ToolsDir, ct);
            if (code != 0)
            {
                log("[错误] 发送引导失败!");
                return false;
            }
            log("[OK] 引导已发送成功。");

            // 4. 配置端口（让设备停留在可交互状态，供后续操作使用；不写入任何分区）
            if (!await EdlService.ConfigurePortAsync(port.Value, AppConfig.UnlockDir, log, ct)) return false;

            log("[OK] 端口配置完成。设备已停留在 9008 模式，可继续后续操作。");
            return true;
        }
    }
}
