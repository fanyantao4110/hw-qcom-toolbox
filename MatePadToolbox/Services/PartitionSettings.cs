using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MatePadToolbox.Services
{
    /// <summary>
    /// 分区解包/打包设置项（参照 TIK settings.json 语义）。
    /// 持久化到 json\part_settings.json，可通过顶部"设置"菜单调整。
    /// </summary>
    public class PartitionSettings
    {
        // ==================== Super 打包 ====================
        /// <summary>super 物理分区名（lpmake --super-name）。</summary>
        public string SuperName { get; set; } = "super";
        /// <summary>动态分区组名（lpmake --group / --partition 的组）。</summary>
        public string SuperGroup { get; set; } = "qti_dynamic_partitions";
        /// <summary>metadata 分区大小（字节，lpmake --metadata-size）。</summary>
        public long MetadataSize { get; set; } = 65536;
        /// <summary>metadata 槽数（lpmake --metadata-slots）。</summary>
        public int MetadataSlots { get; set; } = 2;
        /// <summary>打包为 sparse 稀疏镜像（否则为 raw）。</summary>
        public bool PackSparse { get; set; } = true;
        /// <summary>打包 super 时分区属性为只读（false=可读写，lpmake 分区属性）。</summary>
        public bool PartitionReadOnly { get; set; } = false;
        /// <summary>设备总大小在分区总和+m 后的额外余量（MB）。</summary>
        public long ExtraBufferMB { get; set; } = 8;
        /// <summary>自定义 super 总大小（字节，0=自动按分区总和+余量）。</summary>
        public long CustomDeviceSize { get; set; } = 0;

        // ==================== EROFS 打包 ====================
        /// <summary>erofs 压缩算法与等级，格式 "算法,等级"，如 lz4hc,8（对应 TIK erofslim）。</summary>
        public string ErofsCompress { get; set; } = "lz4hc,8";
        /// <summary>固定统一时间戳（2009-01-01 UTC，mkfs.erofs -T）。</summary>
        public long UtcStamp { get; set; } = 1230768000;
        /// <summary>erofs 老内核兼容（mkfs.erofs -E legacy-compress）。</summary>
        public bool ErofsOldKernel { get; set; } = false;

        // ==================== EXT4 打包 ====================
        /// <summary>ext4 块大小（mke2fs -b）。</summary>
        public int Ext4BlockSize { get; set; } = 4096;

        // ==================== 持久化 ====================

        [JsonIgnore]
        public static string SettingsFile => AppConfig.PartSettingsFile;

        public static PartitionSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    var json = File.ReadAllText(SettingsFile);
                    var data = JsonSerializer.Deserialize<PartitionSettings>(json, JsonOpts);
                    if (data != null) return data;
                }
            }
            catch { /* 解析失败则用默认值 */ }
            return new PartitionSettings();
        }

        public bool Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
                var json = JsonSerializer.Serialize(this, JsonOpts);
                File.WriteAllText(SettingsFile, json);
                return true;
            }
            catch { return false; }
        }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }
}
