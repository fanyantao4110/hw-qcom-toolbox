using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MatePadToolbox.Services
{
    /// <summary>
    /// 分区解包 / 打包服务（参考 DNA-Android 工具链，去掉 dat/br 相关）：
    /// 处理 super 动态分区与 erofs/ext4 分区镜像的解包与打包。
    /// 依赖 tools\part 下的 extract.erofs / mkfs.erofs / simg2img / img2simg / lpmake / lpunpack，
    /// 以及 tools\7z.exe（ext4 解包）与 tools\mke2fs.exe（ext4 打包）。
    /// </summary>
    public static class PartitionToolService
    {
        // ==================== 文件系统类型常量 ====================
        public const string FsErofs = "erofs";
        public const string FsExt4 = "ext4";
        public const string FsSuper = "super";
        public const string FsUnknown = "unknown";

        /// <summary>固定统一时间戳（2009-01-01 UTC），与 DNA 一致，保证镜像打包可复现。</summary>
        public const long FixedTimestamp = 1230768000;

        // ==================== 探测 ====================

        /// <summary>
        /// 判断镜像是否为 Android sparse 稀疏格式。
        /// sparse 头魔数 0xED26FF3A（小端存储为 3A FF 26 ED）。
        /// </summary>
        public static bool IsSparse(string img)
        {
            try
            {
                using var fs = new FileStream(img, FileMode.Open, FileAccess.Read, FileShare.Read);
                var b = new byte[4];
                if (fs.Read(b, 0, 4) < 4) return false;
                return b[0] == 0x3A && b[1] == 0xFF && b[2] == 0x26 && b[3] == 0xED;
            }
            catch { return false; }
        }

        /// <summary>
        /// 判断镜像是否为 super 动态分区镜像（LPDISK 格式，参考 TIK gettype）。
        /// 识别方式：文件头部跳过前导 0x00 后出现 LPDISK 魔数 "gDla"（67 44 6C 61），
        /// 或偏移 4096 处为 "gDla"。
        /// </summary>
        public static bool IsSuper(string img)
        {
            try
            {
                var fs = new FileStream(img, FileMode.Open, FileAccess.Read, FileShare.Read);
                using (fs)
                {
                    var buf = new byte[5];
                    int n = fs.Read(buf, 0, 4);
                    if (n < 4) return false;
                    int i = 0;
                    while (i < n && buf[i] == 0x00) i++;
                    if (i >= n)
                    {
                        // 读取后续字节补偿
                        byte b;
                        while (true)
                        {
                            int r = fs.ReadByte();
                            if (r < 0) return false;
                            b = (byte)r;
                            if (b != 0x00)
                            {
                                var extra = new byte[4];
                                int en = fs.Read(extra, 0, 4);
                                if (en < 4) return false;
                                if (b == 0x67 && extra[0] == 0x44 && extra[1] == 0x6C && extra[2] == 0x61) return true;
                                break;
                            }
                        }
                        return false;
                    }
                    // buf[i] 是首个非 0 字节，其后需接 "Dla"（67 44 6C 61）
                    if (buf[i] == 0x67)
                    {
                        var rest = new byte[3];
                        int rn = fs.Read(rest, 0, 3);
                        if (rn >= 3 && rest[0] == 0x44 && rest[1] == 0x6C && rest[2] == 0x61) return true;
                    }
                }

                // 偏移 4096 处再检查一次
                fs = new FileStream(img, FileMode.Open, FileAccess.Read, FileShare.Read);
                using (var fs2 = fs)
                {
                    fs2.Seek(4096, SeekOrigin.Begin);
                    var m = new byte[4];
                    if (fs2.Read(m, 0, 4) == 4 && m[0] == 0x67 && m[1] == 0x44 && m[2] == 0x6C && m[3] == 0x61) return true;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// 探测完整镜像类型：super / erofs / ext4 / sparse / unknown。
        /// 参考 TIK gettype 签名识别：LPDISK("gDla")、erofs、ext 魔数、sparse 头。
        /// </summary>
        public static string DetectFs(string img)
        {
            if (IsSuper(img)) return FsSuper;
            try
            {
                using var fs = new FileStream(img, FileMode.Open, FileAccess.Read, FileShare.Read);
                var head = new byte[4096];
                int n = fs.Read(head, 0, head.Length);
                if (n < 1088) return IsSparse(img) ? "sparse" : FsUnknown;
                // ext4/ext2/ext3：偏移 1080 处两字节 0xEF53（小端 53 EF）
                if (head[1080] == 0x53 && head[1081] == 0xEF) return FsExt4;
                // erofs：偏移 1024 处魔数 E2 E1 F5 E0
                if (head[1024] == 0xE2 && head[1025] == 0xE1 && head[1026] == 0xF5 && head[1027] == 0xE0) return FsErofs;
            }
            catch { }
            return IsSparse(img) ? "sparse" : FsUnknown;
        }

        // ==================== 通用 ====================

        /// <summary>
        /// 把 extract.erofs 在 outDir\config 下生成的 fs_config / file_contexts 复制到目标 config 目录。
        /// </summary>
        private static void CopyConfigDir(string outDir, string cfgOutDir, Action<string> log)
        {
            try
            {
                var cfgSrc = Path.Combine(outDir, "config");
                if (!Directory.Exists(cfgSrc)) return;
                // 源目录与目标目录相同（extract 已直接在 cfgOutDir 生成 config）时无需复制
                if (string.Equals(Path.GetFullPath(cfgSrc), Path.GetFullPath(cfgOutDir), StringComparison.OrdinalIgnoreCase))
                {
                    log($"[OK] 已导出 fs_config 等配置到 {cfgOutDir}。");
                    return;
                }
                Directory.CreateDirectory(cfgOutDir);
                foreach (var f in Directory.GetFiles(cfgSrc))
                {
                    var target = Path.Combine(cfgOutDir, Path.GetFileName(f));
                    File.Copy(f, target, true);
                }
                log($"[OK] 已导出 fs_config 等配置到 {cfgOutDir}。");
            }
            catch (Exception ex)
            {
                log($"[警告] 复制 config 失败：{ex.Message}");
            }
        }

        /// <summary>运行外部工具，返回是否成功（退出码为 0）。</summary>
        private static async Task<bool> RunOkAsync(string exe, string args, Action<string> log,
            CancellationToken ct, string? workingDirectory = null)
        {
            if (!File.Exists(exe))
            {
                log($"[错误] 缺少工具：{exe}");
                return false;
            }
            var code = await ProcessRunner.RunAsync(exe, args, log, workingDirectory, ct);
            if (code != 0)
            {
                log($"[错误] {Path.GetFileName(exe)} 失败（退出码 {code}）。");
                return false;
            }
            return true;
        }

        /// <summary>把镜像转成 raw 完整镜像（sparse 则 simg2img，raw 则直接复制）。</summary>
        public static async Task<bool> ToRawAsync(string img, string raw, Action<string> log, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(raw)!);
            if (IsSparse(img))
            {
                log($"[步骤] 检测到 sparse 镜像，转换为 raw：{Path.GetFileName(raw)}");
                if (!await RunOkAsync(AppConfig.Simg2imgExe, $"\"{img}\" \"{raw}\"", log, ct)) return false;
            }
            else if (!string.Equals(Path.GetFullPath(img), Path.GetFullPath(raw), StringComparison.OrdinalIgnoreCase))
            {
                log($"[步骤] raw 镜像，直接复制为工作副本：{Path.GetFileName(raw)}");
                File.Copy(img, raw, true);
            }
            return true;
        }

        /// <summary>把 raw 镜像转成 sparse 稀疏镜像。</summary>
        public static async Task<bool> ToSparseAsync(string raw, string sparse, Action<string> log, CancellationToken ct)
        {
            if (!await RunOkAsync(AppConfig.Img2simgExe, $"\"{raw}\" \"{sparse}\"", log, ct)) return false;
            return true;
        }

        /// <summary>向上对齐到指定大小。</summary>
        public static long AlignUp(long size, long align) =>
            (size + align - 1) / align * align;

        /// <summary>递归统计目录总大小。</summary>
        public static long DirSize(string dir)
        {
            long total = 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
            }
            catch { }
            return total;
        }

        // ==================== super 解包 ====================

        /// <summary>
        /// 解包 super 动态分区：super.img → 各分区镜像。
        /// 流程：sparse 先 simg2img 转 raw，再用 lpunpack 解出各分区到 outDir。
        /// </summary>
        public static async Task<bool> ExtractSuperAsync(string superImg, string outDir, Action<string> log, CancellationToken ct)
        {
            if (!File.Exists(superImg))
            {
                log("[错误] 未找到 super 镜像文件。");
                return false;
            }
            Directory.CreateDirectory(outDir);

            var raw = Path.Combine(Path.GetTempPath(), $"super_{DateTime.Now:HHmmss}_{Environment.ProcessId}.raw");
            try
            {
                var ok = await ToRawAsync(superImg, raw, log, ct);
                if (!ok) return false;

                log($"[步骤] lpunpack 解包 super → {outDir}");
                if (!await RunOkAsync(AppConfig.LpunpackExe, $"\"{raw}\" \"{outDir}\"", log, ct)) return false;

                var parts = Directory.GetFiles(outDir, "*.img");
                log(parts.Length > 0
                    ? $"[OK] super 解包完成，共 {parts.Length} 个分区镜像。"
                    : "[警告] super 解包结束，但未解出任何分区镜像。");
                return true;
            }
            finally
            {
                try { if (File.Exists(raw)) File.Delete(raw); } catch { }
            }
        }

        // ==================== 分区镜像 解包 ====================

        /// <summary>
        /// 解包单个分区镜像到目录（支持 super / erofs / ext4）。
        /// fsType 为 null 时自动探测（含 LPDISK super 识别）。
        /// 若探测为 super，则自动转为 <see cref="ExtractSuperAsync"/> 解出各分区到 outDir。
        /// cfgOutDir 非空且为 erofs 时：单独把 fs_config 与 file_contexts 导出到该目录（打包的前置步骤）。
        /// </summary>
        public static async Task<bool> ExtractImageAsync(string img, string outDir, string? fsType,
            string? cfgOutDir, Action<string> log, CancellationToken ct)
        {
            if (!File.Exists(img))
            {
                log("[错误] 未找到分区镜像文件。");
                return false;
            }

            if (string.IsNullOrEmpty(fsType) && IsSuper(img))
            {
                log("[信息] 检测到 super 动态分区镜像，转用 super 解包。");
                return await ExtractSuperAsync(img, outDir, log, ct);
            }

            // 先转 raw（sparse → raw），保证后续工具可直接读取。
            // 注意：extract.erofs 依据输入文件名命名文件树子目录与 config（如输入 system.img → system\ 与 system_fs_config）。
            // 因此 raw 副本必须命名为 <分区名>.img，而不是 img.raw，否则文件树与 config 会被命名为 "img"。
            var partName = Path.GetFileNameWithoutExtension(img) ?? "partition";
            var tmpDir = Path.Combine(Path.GetTempPath(), $"partx_{DateTime.Now:HHmmss}_{Environment.ProcessId}");
            Directory.CreateDirectory(tmpDir);
            var rawImg = Path.Combine(tmpDir, partName + ".img");
            try
            {
                var ok = await ToRawAsync(img, rawImg, log, ct);
                if (!ok) return false;

                var detected = fsType ?? DetectFs(rawImg);
                log($"[信息] 分区镜像文件系统：{detected}");

                if (detected == FsSuper)
                {
                    log("[信息] 检测到 super 动态分区镜像，转用 super 解包。");
                    return await ExtractSuperAsync(rawImg, outDir, log, ct);
                }

                // erofs：extract.erofs
                if (detected == FsErofs || string.IsNullOrEmpty(fsType))
                {
                    // extract.erofs：完整解包 -x -f。会自动生成 <父目录>/config/<分区>_fs_config 与 _file_contexts。
                    // extract 会按输入文件名（rawImg 已命名为 <分区名>.img）在 -o 目录下创建同名子目录，
                    // 故此处把 outDir 的父目录交给 -o（如 -o img → img\system\），使文件树落在 outDir，config 落在其 config\ 下。
                    // extract.erofs/mkfs.erofs 原生支持 Windows 路径，勿转换成 /盘符/ 形式（Cygwin 不识别 /c，会报路径不存在）。
                    var erofsOut = Directory.GetParent(outDir.TrimEnd('\\', '/'))?.FullName ?? outDir;
                    log($"[步骤] extract.erofs 解包 → {erofsOut}");
                    Directory.CreateDirectory(erofsOut);
                    var args = $" -i \"{rawImg}\" -o \"{erofsOut}\" -x -f";
                    var done = await RunOkAsync(AppConfig.ExtractErofsExe, args, log, ct);
                    if (done)
                    {
                        if (!string.IsNullOrWhiteSpace(cfgOutDir))
                        {
                            CopyConfigDir(erofsOut, cfgOutDir, log);
                        }
                        log("[OK] 分区镜像解包完成，config 已自动导出。");
                        return true;
                    }
                    if (!string.IsNullOrEmpty(fsType)) return false; // 明确 erofs 但失败
                }

                // ext4 / 未知：用 TIK ext4 解包（生成文件树 + 三个 config：size/fs_config/file_contexts）
                if (detected == FsExt4 || detected == FsUnknown)
                {
                    log($"[步骤] TIK ext4 解包 → {outDir}");
                    Directory.CreateDirectory(outDir);
                    // work 目录：作为 config 的父目录（config 写到 <work>/config/）
                    var cfgParent = !string.IsNullOrWhiteSpace(cfgOutDir)
                        ? Path.GetDirectoryName(cfgOutDir)!
                        : Path.Combine(Path.GetTempPath(), $"tikcfg_{DateTime.Now:HHmmss}_{Environment.ProcessId}");
                    var tikArgs = $"extract \"{rawImg}\" \"{outDir}\" \"{cfgParent}\" \"{partName}\"";
                    var tikCode = await ProcessRunner.RunAsync(AppConfig.Ext4ExtractExe, tikArgs, log, null, ct);
                    if (tikCode == 0)
                    {
                        log("[OK] 分区镜像解包完成，并已导出 fs_config / size 等配置。");
                        return true;
                    }
                    log("[错误] TIK ext4 解包失败。");
                    return false;
                }

                log($"[错误] 不支持的文件系统：{detected}");
                return false;
            }
            finally
            {
                try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ==================== 分区镜像 打包 ====================

        /// <summary>
        /// 把目录打包成分区镜像（erofs / ext4）。
        /// </summary>
        /// <param name="dir">源目录</param>
        /// <param name="outImg">输出镜像路径</param>
        /// <param name="fsType">erofs 或 ext4</param>
        /// <param name="name">分区名（作为挂载点/卷标）</param>
        /// <param name="erofsAlgo">erofs 压缩算法 lz4 / lz4hc / lzma</param>
        /// <param name="erofsLevel">erofs 压缩等级 0-9</param>
        /// <param name="fsConfig">fs_config 文件路径（可选）</param>
        /// <param name="fileContexts">file_contexts 文件路径（可选）</param>
        /// <param name="ext4SizeBytes">ext4 目标大小（字节，可选；缺省自动按内容估算）</param>
        public static async Task<bool> PackImageAsync(string dir, string outImg, string fsType, string name,
            string erofsAlgo, int erofsLevel, string? fsConfig, string? fileContexts, long? ext4SizeBytes,
            Action<string> log, CancellationToken ct)
        {
            if (!Directory.Exists(dir))
            {
                log("[错误] 未找到源目录。");
                return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(outImg)!);

            if (fsType == FsErofs)
            {
                var st = PartitionSettings.Load();
                var args = new System.Text.StringBuilder();
                args.Append($"-T{st.UtcStamp} -z{erofsAlgo}");
                if (erofsLevel >= 0) args.Append($",{erofsLevel}");
                if (st.ErofsOldKernel) args.Append(" -E legacy-compress");
                args.Append($" --mount-point=/{name}");
                if (!string.IsNullOrWhiteSpace(fsConfig) && File.Exists(fsConfig))
                    args.Append($" --fs-config-file=\"{fsConfig}\"");
                if (!string.IsNullOrWhiteSpace(fileContexts) && File.Exists(fileContexts))
                    args.Append($" --file-contexts=\"{fileContexts}\"");
                args.Append($" --all-root \"{outImg}\" \"{dir}\"");

                log("[步骤] mkfs.erofs 打包 EROFS 镜像...");
                return await RunOkAsync(AppConfig.MkfsErofsExe, args.ToString(), log, ct);
            }

            if (fsType == FsExt4)
            {
                var st = PartitionSettings.Load();
                long size = ext4SizeBytes ?? EstimateExt4Size(dir);
                var sizeStr = (size / (1024 * 1024)) + "M";
                var args = $"-t ext4 -b {st.Ext4BlockSize} -L {name} -M /{name} -m 0 " +
                           $"-d \"{dir}\" " +
                           $"-O \"^has_journal,^metadata_csum,extent,huge_file,^flex_bg,^64bit,uninit_bg,dir_nlink,extra_isize\" " +
                           $"-F \"{outImg}\" {sizeStr}";
                log($"[步骤] mke2fs 打包 EXT4 镜像（目标约 {sizeStr}）...");
                return await RunOkAsync(AppConfig.Mke2fsExe, args, log, ct);
            }

            log($"[错误] 不支持的打包文件系统：{fsType}");
            return false;
        }

        /// <summary>按目录内容估算 ext4 镜像大小（内容 + 15% + 16MB 元数据余量，向上对齐 4MB）。</summary>
        public static long EstimateExt4Size(string dir)
        {
            long content = DirSize(dir);
            return AlignUp((long)(content * 1.15) + 16L * 1024 * 1024, 4L * 1024 * 1024);
        }

        // ==================== super 打包 ====================

        /// <summary>
        /// 打包 super 动态分区：把分区目录中的各 *.img 分区重新合成 sparse super.img。
        /// 流程：各分区 raw 转 sparse → lpmake（sparse 输出，嵌入各分区）。
        /// </summary>
        /// <param name="partDir">包含各分区镜像（*.img）的目录</param>
        /// <param name="outSuper">输出 super.img 路径</param>
        /// <param name="group">动态分区组名（默认 qti_dynamic_partitions）</param>
        /// <param name="slots">metadata 槽数（默认 2）</param>
        /// <param name="customDeviceSize">自定义 super 总大小（字节，可选；缺省按分区总和 + 余量自动计算）</param>
        public static async Task<bool> PackSuperAsync(string partDir, string outSuper, string group,
            int slots, long? customDeviceSize, Action<string> log, CancellationToken ct)
        {
            if (!Directory.Exists(partDir))
            {
                log("[错误] 未找到分区目录。");
                return false;
            }
            var parts = Directory.GetFiles(partDir, "*.img")
                .Where(f => !string.Equals(Path.GetFileName(f), AppConfig.MergedSuperFileName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (parts.Count == 0)
            {
                log("[错误] 分区目录中没有 .img 分区镜像，无法打包 super。");
                return false;
            }

            var tmpDir = Path.Combine(Path.GetTempPath(), $"pack_{DateTime.Now:HHmmss}_{Environment.ProcessId}");
            Directory.CreateDirectory(tmpDir);
            try
            {
                var st = PartitionSettings.Load();
                long metadataSize = st.MetadataSize > 0 ? st.MetadataSize : 65536;
                if (string.IsNullOrWhiteSpace(group)) group = st.SuperGroup;
                if (slots <= 0) slots = st.MetadataSlots;
                // lpmake 分区属性必须是 none（可读写）或 readonly（只读），空属性会报 "Attribute not recognized"。
                var partAttr = st.PartitionReadOnly ? "readonly" : "none";

                var args = new System.Text.StringBuilder();
                args.Append($"--metadata-size {metadataSize} --metadata-slots {slots} --super-name {st.SuperName} ");

                long sum = 0;
                var sparseParts = new List<(string Name, string Sparse, long Size)>();
                foreach (var p in parts.OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileNameWithoutExtension(p);
                    var raw = Path.Combine(tmpDir, name + "_raw.img");
                    var sparse = Path.Combine(tmpDir, name + ".img");

                    if (!await ToRawAsync(p, raw, log, ct)) return false;
                    if (!await ToSparseAsync(raw, sparse, log, ct)) return false;

                    var size = AlignUp(new FileInfo(raw).Length, 4096);
                    sum += size;
                    sparseParts.Add((name, sparse, size));
                }

                // device size：分区总和 + metadata 区 + 余量；缺省自定义时附加设置里的额外余量
                long deviceSize = sum + metadataSize * (slots + 1);
                if (customDeviceSize == null || customDeviceSize <= 0) deviceSize += st.ExtraBufferMB * 1024L * 1024L;
                else deviceSize = Math.Max(customDeviceSize.Value, deviceSize);
                deviceSize = AlignUp(deviceSize, 4096);

                args.Append($"--device super:{deviceSize} ");
                args.Append($"--group {group}:{deviceSize} ");
                foreach (var (name, sparse, size) in sparseParts)
                {
                    args.Append($"--partition {name}:{partAttr}:{size}:{group} --image {name}=\"{sparse}\" ");
                }
                args.Append(st.PackSparse ? $"--sparse --output \"{outSuper}\"" : $"--output \"{outSuper}\"");

                log($"[步骤] lpmake 打包 super（设备大小 {deviceSize} 字节，{sparseParts.Count} 个分区）...");
                if (!await RunOkAsync(AppConfig.LpmakeExe, args.ToString(), log, ct)) return false;
                log($"[OK] super 打包完成：{outSuper}");
                return true;
            }
            finally
            {
                try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
            }
        }
    }
}
