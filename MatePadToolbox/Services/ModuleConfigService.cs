using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using MatePadToolbox.Models;

namespace MatePadToolbox.Services
{
    /// <summary>
    /// 优化模块配置服务：
    /// 从统一 json optimize_modules.json 读取模块列表，并支持下载模块 zip 到 modules 目录。
    /// 模块信息不区分设备。json 结构：
    ///   { "modules": [ { "name":"模块名", "file":"xxx.zip", "type":"direct|netdisk", "url":"下载地址", "description":"说明" } ] }
    /// </summary>
    public static class ModuleConfigService
    {
        /// <summary>JSON 配置文件下载基地址（与其它配置一致，统一读源码仓库 raw）。</summary>
        public const string JsonBaseUrl = AppConfig.ConfigRepoRawBaseUrl;

        /// <summary>优化模块 json 文件名。</summary>
        public const string ConfigFileName = "optimize_modules.json";

        private static readonly HttpClient Http = new(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        })
        { Timeout = TimeSpan.FromSeconds(60) };

        /// <summary>模块配置 json 本地完整路径（位于 json 目录）。</summary>
        public static string GetConfigPath() => Path.Combine(AppConfig.JsonDir, ConfigFileName);

        /// <summary>模块 zip 本地完整路径（文件名取 json 中的 file 字段）。</summary>
        public static string GetModuleZipPath(ModuleEntry entry)
        {
            var name = string.IsNullOrWhiteSpace(entry.File)
                ? $"{Sanitize(entry.Name)}.zip"
                : Path.GetFileName(entry.File);
            return Path.Combine(AppConfig.ModulesDir, name);
        }

        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c.ToString(), "_");
            return string.IsNullOrWhiteSpace(name) ? "module" : name;
        }

        /// <summary>下载最新的模块列表 json。</summary>
        public static async Task<bool> DownloadConfigAsync(Action<string> log, CancellationToken ct = default)
        {
            var url = JsonBaseUrl + ConfigFileName;
            var outPath = GetConfigPath();
            log($"正在下载 {ConfigFileName} ...");
            try
            {
                var bytes = await Http.GetByteArrayAsync(url, ct);
                Directory.CreateDirectory(AppConfig.JsonDir);
                await File.WriteAllBytesAsync(outPath, bytes, ct);
                log($"[OK] {ConfigFileName} 下载成功。");
                return true;
            }
            catch (OperationCanceledException)
            {
                log($"[已取消] {ConfigFileName} 下载已取消。");
                return false;
            }
            catch (Exception ex)
            {
                log($"[错误] 下载失败：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 解析模块 json。兼容两种结构：
        ///   A) { "modules": [ ... ] }
        ///   B) 顶层直接为模块数组：[ ... ]
        /// </summary>
        public static List<ModuleEntry> ParseModules(string jsonContent)
        {
            var list = new List<ModuleEntry>();
            if (string.IsNullOrWhiteSpace(jsonContent)) return list;

            try
            {
                using var doc = JsonDocument.Parse(jsonContent);
                var root = doc.RootElement;

                JsonElement arr = default;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("modules", out var m) &&
                    m.ValueKind == JsonValueKind.Array)
                {
                    arr = m;
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
                    var e = new ModuleEntry();
                    foreach (var p in el.EnumerateObject())
                    {
                        if (p.Value.ValueKind != JsonValueKind.String) continue;
                        var v = p.Value.GetString() ?? "";
                        switch (p.Name.ToLowerInvariant())
                        {
                            case "name": e.Name = v; break;
                            case "file": case "filename": e.File = v; break;
                            case "url": case "download": case "downloadurl": e.Url = v; break;
                            case "type": case "mode": case "downloadtype": case "download_type": e.DownloadType = v; break;
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
        /// 加载模块列表：优先读本地 json；缺失时从 Gitee 下载。
        /// </summary>
        public static async Task<List<ModuleEntry>> LoadModulesAsync(Action<string> log)
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
                    var content = await Task.Run(() => RomConfigService.ReadTextAutoDetect(path));
                    return ParseModules(content);
                }
                catch (Exception ex)
                {
                    log($"[错误] 解析 {ConfigFileName} 失败：{ex.Message}");
                }
            }
            return new List<ModuleEntry>();
        }

        /// <summary>
        /// 下载模块 zip 到 modules 目录（本地文件名取 json 中的 file 字段，英文）。
        /// </summary>
        public static async Task<bool> DownloadModuleAsync(ModuleEntry entry, Action<string> log, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(entry.Url))
            {
                log($"[错误] 模块「{entry.Name}」没有下载链接。");
                return false;
            }

            Directory.CreateDirectory(AppConfig.ModulesDir);
            var outPath = GetModuleZipPath(entry);
            log($"正在下载模块「{entry.Name}」→ {outPath} ...");
            try
            {
                var bytes = await Http.GetByteArrayAsync(entry.Url, ct);
                await File.WriteAllBytesAsync(outPath, bytes, ct);
                log($"[OK] 模块下载完成：{Path.GetFileName(outPath)}");
                return true;
            }
            catch (OperationCanceledException)
            {
                log($"[已取消] 模块「{entry.Name}」下载已取消。");
                return false;
            }
            catch (Exception ex)
            {
                log($"[错误] 下载失败：{ex.Message}");
                return false;
            }
        }
    }
}
