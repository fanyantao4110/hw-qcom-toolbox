using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using MatePadToolbox.Models;

namespace MatePadToolbox.Services
{
    /// <summary>
    /// 第三方系统下载配置服务：
    /// 使用统一的合并配置文件 system_config.json，按"机型 → 系统列表"组织。
    /// （已移除旧版按版本拆分的 {版本}.json 逻辑，只维护 system_config.json 一份。）
    /// </summary>
    public static class RomConfigService
    {
        /// <summary>
        /// JSON 配置文件下载基地址。
        /// 已由 release 附件改为直接读取源码仓库（raw），与 system_config.json 的维护方式一致。
        /// </summary>
        public const string JsonBaseUrl = AppConfig.ConfigRepoRawBaseUrl;

        /// <summary>统一合并配置文件文件名（按机型分组）。</summary>
        public const string ConfigFileName = "system_config.json";

        /// <summary>机型名缺失时的默认名称。</summary>
        public const string DefaultDeviceName = "MatePad 11";

        private static readonly HttpClient Http = new(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        })
        { Timeout = TimeSpan.FromSeconds(60) };

        /// <summary>合并配置文件的本地完整路径。</summary>
        public static string GetConfigPath() => Path.Combine(AppConfig.JsonDir, ConfigFileName);

        /// <summary>下载最新的合并配置文件 system_config.json。</summary>
        public static async Task<bool> DownloadConfigAsync(Action<string> log)
        {
            var url = JsonBaseUrl + ConfigFileName;
            var outPath = GetConfigPath();
            log($"正在下载 {ConfigFileName} ...");
            try
            {
                var bytes = await Http.GetByteArrayAsync(url);
                Directory.CreateDirectory(AppConfig.SystemDir);
                await File.WriteAllBytesAsync(outPath, bytes);
                log($"[OK] {ConfigFileName} 下载成功。");
                return true;
            }
            catch (Exception ex)
            {
                log($"[错误] 下载失败：{ex.Message}");
                return false;
            }
        }

        /// <summary>在线更新配置文件（下载统一的 system_config.json）。</summary>
        public static async Task<bool> UpdateConfigAsync(Action<string> log)
        {
            log($"正在更新配置文件（{ConfigFileName}）...");
            return await DownloadConfigAsync(log);
        }

        /// <summary>
        /// 解析合并配置文件为机型列表。
        /// 兼容两种结构：
        ///   A) { "devices": [ { "name": "...", "systems": [ {"name":"..","url":".."}, ... ] }, ... ] }
        ///   B) 顶层直接为机型数组：[ { "name": "...", "systems": [...] }, ... ]
        /// </summary>
        public static List<DeviceEntry> ParseConfig(string jsonContent)
        {
            var devices = new List<DeviceEntry>();
            if (string.IsNullOrWhiteSpace(jsonContent)) return devices;

            try
            {
                using var doc = JsonDocument.Parse(jsonContent);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("devices", out var arr) &&
                    arr.ValueKind == JsonValueKind.Array)
                {
                    devices.AddRange(ParseDevicesArray(arr));
                    return devices;
                }

                if (root.ValueKind == JsonValueKind.Array)
                {
                    devices.AddRange(ParseDevicesArray(root));
                    return devices;
                }
            }
            catch { /* 忽略无效内容 */ }

            return devices;
        }

        private static IEnumerable<DeviceEntry> ParseDevicesArray(JsonElement arr)
        {
            var devices = new List<DeviceEntry>();
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;

                var name = ReadStringOf(el, "name", "device");
                var systems = new List<RomEntry>();

                if (el.TryGetProperty("systems", out var sysArr) && sysArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in sysArr.EnumerateArray())
                    {
                        var entry = ReadEntry(s);
                        if (entry != null) systems.Add(entry);
                    }
                }

                if (!string.IsNullOrWhiteSpace(name) || systems.Count > 0)
                {
                    devices.Add(new DeviceEntry
                    {
                        Name = string.IsNullOrWhiteSpace(name) ? DefaultDeviceName : name,
                        Systems = systems
                    });
                }
            }
            return devices;
        }

        private static string ReadStringOf(JsonElement el, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (el.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String)
                    return p.GetString() ?? "";
            }
            return "";
        }

        /// <summary>
        /// 加载机型列表：优先读本地合并配置；缺失时尝试在线下载；
        /// 仍失败则返回空列表。
        /// </summary>
        public static async Task<List<DeviceEntry>> LoadDeviceListAsync(Action<string> log)
        {
            var path = GetConfigPath();
            if (!File.Exists(path))
            {
                log($"[提示] 未找到 {ConfigFileName}，尝试从 Gitee 下载...");
                await DownloadConfigAsync(log);
            }

            if (File.Exists(path))
            {
                try
                {
                    var content = await Task.Run(() => ReadTextAutoDetect(path));
                    var devices = ParseConfig(content);
                    if (devices.Count > 0) return devices;
                }
                catch (Exception ex)
                {
                    log($"[错误] 解析 {ConfigFileName} 失败：{ex.Message}");
                }
            }

            return new List<DeviceEntry>();
        }

        /// <summary>读取某个机型下的系统列表。</summary>
        public static List<RomEntry> GetSystems(DeviceEntry? device) => device?.Systems ?? new List<RomEntry>();

        private static RomEntry? ReadEntry(JsonElement el)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            string name = "", url = "";
            foreach (var prop in el.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                var key = prop.Name.ToLowerInvariant();
                var val = prop.Value.GetString() ?? "";
                if (key == "name") name = val;
                else if (key == "url") url = val;
            }
            if (string.IsNullOrEmpty(name)) return null;
            return new RomEntry { Name = name, Url = url };
        }

        /// <summary>
        /// 自动识别编码读取文本文件：
        /// 优先识别 UTF-8 BOM，再尝试严格 UTF-8 解码，失败则按 ANSI(GBK) 解码。
        /// </summary>
        public static string ReadTextAutoDetect(string path)
        {
            var bytes = File.ReadAllBytes(path);

            // 1. UTF-8 BOM
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }
            // 2. UTF-16 BOM
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            }

            // 3. 严格 UTF-8 解码，失败则按 ANSI(GBK)
            try
            {
                var utf8Strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
                return utf8Strict.GetString(bytes);
            }
            catch (ArgumentException)
            {
                return ProcessRunner.Gbk.GetString(bytes);
            }
        }
    }
}
