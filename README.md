# 华为高通芯片通用工具箱（MatePadToolbox）

面向华为**高通（骁龙）平台**设备的一站式刷机工具箱，原生 **WinUI 3** 界面：安装驱动、9008 全分区备份/恢复、解锁 BL、Root、刷入第三方/官方系统、降级救砖、分区解包打包，全部集成在一个程序里。

**处理器型号在侧边栏顶部的【CPU型号】下拉框中全局选择**——解锁 BL、9008 备份、Root 提取、刷机 9008 模式等所有与 firehose 引导相关的功能，都按所选型号执行。

## 界面与全局约定

| 元素 | 说明 |
| --- | --- |
| 顶部设备状态栏 | 实时显示当前连接模式（ADB / Fastboot / 9008）与设备信息 |
| 侧边栏【CPU 型号】下拉框 | 全局处理器选择，右侧 **↻** 刷新列表。**打开程序时默认不选中**（防误刷）；未选型号时相关功能会提示并中止 |
| 故障排查按钮 | 每个功能页右上角，一键下载并展示该功能的常见问答（来自在线 faq json） |
| 取消按钮 | 耗时操作可中途取消；**降级页与驱动页不提供** |

## 功能一览

| # | 页面 | 说明 |
| --- | --- | --- |
| 1 | 主页 | 功能总览、**检查核心工具**、打开程序根目录 |
| 2 | 安装驱动程序 | 一键安装 ADB 驱动与高通 9008（EDL）驱动 |
| 3 | 9008备份/恢复 | **备份**：回读设备全部分区镜像，生成 `images/`、`gpt_and_xml/` 与 `flash_all.bat` 一键回刷脚本。**恢复**：选择备份目录把其中的 `rawprogram*.xml` 与 `patch*.xml` 依次刷回，恢复前询问是否本机备份以决定是否刷入基带/指纹分区 |
| 4 | 解锁BL | 按侧边栏所选型号刷入 ABL 解锁镜像；鸿蒙 2-3 自动经 ADB 进入 9008，鸿蒙 4 需工程线/短接或先降级 |
| 5 | Root | **APatch 方式**（9008 提取 boot → kptools 修补 → 刷回）与 **Magisk 方式**（9008 提取 ramdisk → MagiskPatcher 修补 → 刷回）；均支持一键 Root 与一键隐藏 Root |
| 6 | 优化模块 | 从在线 `optimize_modules.json` 读取模块列表并平铺展示，「直链下载」与「跳转网盘」两种方式，经 ADB 一键刷入 Magisk / APatch |
| 7 | 下载第三方系统/底包 | 从 Gitee 获取**按机型分组**的第三方系统列表与底包下载链接（`system_config.json` 配置） |
| 8 | 刷入第三方系统 | 底包刷 **UPDATE.APP**（内置解包器，自动排除 abl 与全部 super 分片）；super 分区单独从 `system/` 选择任意 img 刷入；支持 Fastboot / 9008 双模式、去 AVB 校验 |
| 9 | 高级刷机 | **Fastboot 模式**刷入任意分区镜像（可先获取分区表）；**9008 模式**读取设备分区表后按分区刷入 |
| 10 | 刷官方系统(救砖) | 刷 **UPDATE.APP**（内置解包器，仅排除 abl，super 照常刷入，多个 sparse 分片自动合并）；Fastboot 模式递归刷入全部镜像 |
| 11 | 降级系统/回锁BL | 旧版华为手机助手 + HiSuite Proxy + 修改系统时间，实现版本回退或重新上锁 |
| 12 | 高级工具箱 | **连接 9008 并发送引导**（仅下发 firehose，不写任何镜像）、**恢复出厂设置**，以及 ADB/Fastboot 重启快捷操作 |
| 13 | 分区解包/打包 | super 动态分区与 erofs / ext4 分区镜像的解包与打包（输出到 `img/`） |
| 14 | ADB工具集 | 启动外部 admt 工具集，**同时切换**到内置 ADB 命令行页面 |
| 15 | 设备管理器 | 快捷打开 Windows 设备管理器 |
| 16 | 关于 | 版本与开源信息 |

> 备注：原独立的「ADB命令行」侧边栏项已合并到 **ADB工具集**——点击它既会打开外部工具，也会导航到内置命令行页面。

### Root 页细节

- **Magisk 方式**页面顶部有一条**不可关闭的警告横幅**：该方式直接修补 ramdisk，成功率较低、风险较高，建议改用 APatch 方式，或自行提取并修补 ramdisk。
- 两种方式都提供 **一键 Root（提取+修补+刷入）**：提取完成后设备**保持在 9008 模式**，修补与刷入复用同一个 9008 会话，中途不重启。
- **一键隐藏 Root**：两个分页各有独立按钮（见下表）。
- **取消 Root 权限**：同时刷回 boot 与 ramdisk。
- **安装管理器**：把 `tools/apks/` 下的 Magisk / APatch 管理器 apk 安装到设备。

### 隐藏 Root 环境

| 方案 | 刷入的模块 | 实现要点 |
| --- | --- | --- |
| APatch | TEESimulator-RS、Zygisk Next、紫罗兰辅助、LSPosed(v2.2.0-7854) | `apd module install <zip>` |
| Magisk | TEESimulator-RS、紫罗兰辅助、LSPosed(v2.2.0-7854)、**Shamiko** | `magisk --install-module <zip>` |

- Magisk 方案**不安装 Zygisk Next**：Magisk 自带 Zygisk，再装 Zygisk Next 会冲突（这也是同类工具箱隐藏 Magisk 失败的常见原因），隐藏能力交给 **Shamiko**；程序会自动启用 Magisk 内置 Zygisk，并创建 Shamiko 白名单文件 `/data/adb/shamiko/whitelist`。
- 模块均由 ADB 推送到设备后调用 `apd` / `magisk` 安装，成败以**退出码**判定。

## 支持设备与处理器

处理器配置在 `json/processors.json`，内置 7 款（`chip` 字段为覆盖机型）：

| 型号 | 覆盖机型 |
| --- | --- |
| 骁龙865/870 | MatePad 11 (DBY-W09)、MatePadPro10.8 (MRR-W29)、MatePadPro11_2022 (GOT-W29) |
| 骁龙680（A） | Nova9SE、Nova10SE、畅享60Pro (MAO-AL00)（鸿蒙4.0.0.116） |
| 骁龙680（B） | Nova11SE、畅享70Pro、畅享70S (GFY-AL00)（鸿蒙4.2.0.193） |
| 骁龙695 | MatePad SE 11 |
| 骁龙690 | 智选HiNova9Z、雷鸟FF1 |
| 骁龙778G | 智选HiNova9 / HiNova9Pro / HiNova10 |
| 骁龙8+ | Mate50 / Mate50Pro / Mate50RS(保时捷) / MateX3 / P60 / P60Pro / P60Art |

- 系统：HarmonyOS 2 / 3 / 4（鸿蒙 4 解锁与 Root 可能需要先降级或使用工程线）。
- 第三方系统下载列表内置机型：**MatePad 11**、**MatePad SE 11**（可自行扩充，见下文）。

## 运行环境

- Windows 10 1809（内部版本 17763）及以上 / Windows 11，x64
- 自包含单文件发布，**无需安装 .NET 运行时**
- 一条可靠的数据线；进入 9008 模式的功能在鸿蒙 4 下可能需要工程线（普通线剪断 D+/D- 或测试点短接）

## 目录结构

所有资源目录与 exe 同级。`screenshots/ system/ base/ modules/ cache/ tmp/ out/ img/ log/ json/ backup/` 等运行时目录会在启动时自动创建。

```
MatePadToolbox.exe                  # 主程序（自包含单文件）

tools/                              # 第三方工具与刷机资源
├── adb.exe / fastboot.exe          # Android platform-tools
├── 7z.exe / 7z.dll                 # 7-Zip 命令行版
├── qsaharaserver.exe               # Qualcomm Sahara 协议（9008 上传 firehose）
├── fh_loader.exe                   # Qualcomm Firehose 加载器（9008 读/写）
├── ptool.exe                       # 可选：9008 备份后生成 gpt_and_xml/create
├── huawei_firmware_extractor.exe   # UPDATE.APP 解包器（刷底包/官方系统用）
├── busybox.exe / gpttool.exe / devcon.exe
├── driver/                         # adbdriver.exe / qcdriver.exe
├── apks/                           # Magisk.apk / APatch.apk / MtManger.apk
├── apatch_tools/                   # kptools.exe / kpimg-android（APatch 修补）
├── magisk_tools/                   # MagiskPatcher.exe / magiskboot.exe（Magisk 修补）
├── part/                           # 分区解包打包工具链
│   ├── extract.erofs.exe / mkfs.erofs.exe
│   ├── simg2img.exe / img2simg.exe
│   ├── mke2fs.exe / resize2fs.exe / busybox.exe
│   └── fs/ · super/                # erofs/ext4 与 super 动态分区（lpmake / lpunpack）
├── adbtoolkit/                     # adb-toolbox.exe（adb 工具集，侧边栏「ADB工具集」启动）
├── HiSuiteProxy/                   # HiSuite1.exe / HiSuite Proxy V3.exe（降级用）
└── rawprogram/
    └── misc.img                    # 恢复出厂设置与写 misc 分区所用镜像

unlock/                             # 各型号的 firehose 引导与解锁镜像
├── Huawei865870_devprg.elf / Huawei865870_abl_unlock.img
├── HuaweiCommon_680_devprg.elf / Huawei680_abl_unlock_A.img · _B.img
├── HuaweiCommon_685_devprg.elf / Huawei695_abl_unlock.img
├── ZhiXuanCommon_690_devprg.elf / ZhiXuan690_abl_unlock.img
├── HuaweiZhiXuan778G_devprg.elf / HuaweiZhiXuan778G_abl_unlock.img
└── Huawei8+_devprg.elf / Huawei8+_abl_unlock.img

json/                               # 全部配置 json（详见下文）
├── system_config.json
├── optimize_modules.json
├── processors.json
├── part_settings.json
└── faq/                            # console / downgrade / download / driver / flash / official / root / unlock .json

modules/                            # 优化模块 zip、隐藏 Root 所需 apk 与模块 zip
img/                                # 分区解包/打包工作槽（img_1 ~ img_3、config）
base/                               # 底包 UPDATE.APP 存放目录
system/                             # 官方系统包 UPDATE.APP、以及供 super 分区刷入的任意 img
```

> **外部工具不随源码分发**：因版权与分发限制，上表中第三方工具（adb、fastboot、7z、QSHaraServer、fh_loader、ptool、kptools、MagiskPatcher 等）需自行获取后放入对应目录。主界面「**检查核心工具**」按钮可校验必备文件是否齐全。

## 配置文件（JSON）

所有配置文件均为 **UTF-8 编码**、**不支持注释与尾逗号**；键名不区分大小写，多数字段支持别名。修改后若界面未变化，点击对应页面的「在线更新 / 刷新」按钮或重启程序。

**在线配置统一基地址**（`AppConfig.ConfigRepoRawBaseUrl`，Gitee 源码 master 分支的 raw）：

```
https://gitee.com/fanyantao4110/matepad11-system-config/raw/master/
```

| 文件 | 位置 | 作用 | 更新方式 |
| --- | --- | --- | --- |
| `system_config.json` | `json/` | 第三方系统/底包下载列表（按机型分组） | 【在线更新配置文件】 |
| `optimize_modules.json` | `json/` | 优化模块列表 | 【在线更新模块列表】 |
| `processors.json` | `json/` | 处理器型号（firehose 引导 + ABL 解锁镜像） | **仅本地读取**，侧边栏 **↻** 刷新 |
| `part_settings.json` | `json/` | 分区解包/打包页面的设置（本地状态，无需手工编辑） | — |
| `{功能}.json` | `json/faq/` | 各功能页故障排查问答 | 点故障排查按钮自动下载 |

> 在线更新会**覆盖**本地文件；如需自定义，请先备份，或更新后在此基础上增删。

### 1. 处理器配置（json/processors.json）

驱动侧边栏顶部的 **【CPU型号】全局选择**，解锁BL、9008备份、Root、发送引导、刷机9008 都按所选型号执行。支持 `{"processors":[...]}` 与顶层数组两种结构。

| 字段（正式名） | 别名 | 必填 | 说明 |
| --- | --- | --- | --- |
| `name` | — | ✅ | 处理器显示名称（显示在侧边栏下拉框）；无 `name` 的条目会被忽略 |
| `chip` | `soc` | 可选 | 芯片/机型说明，显示在下拉框下方状态栏，仅用于展示 |
| `devprg` | `firehose` / `elf` | ✅ | firehose 引导文件名（`unlock/` 下的 `.elf`）。9008备份 / 解锁BL / Root / 发送引导 / 刷机9008 都上传此文件 |
| `abl_unlock` | `ablunlock` / `abl` / `unlockimg` | ✅ | ABL 解锁镜像文件名（`unlock/` 下的 `.img`），**解锁BL 功能刷入此镜像到 abl 分区** |
| `description` | `desc` | 可选 | 处理器说明文字 |

`devprg` 缺失时相关 9008 上传功能不可用；`abl_unlock` 缺失时解锁BL 不可用。

**新增一个处理器型号**：往 `processors` 数组追加对象，把对应的 `.elf` / `.img` 放入 `unlock/`，再点侧边栏【CPU型号】旁的 **↻ 刷新**。示例：

```json
{
  "processors": [
    { "name": "骁龙865/870", "chip": "MatePad 11 (DBY-W09)", "devprg": "Huawei865870_devprg.elf", "abl_unlock": "Huawei865870_abl_unlock.img" },
    { "name": "新处理器", "chip": "SOMETHING / 其它机型", "devprg": "other_devprg.elf", "abl_unlock": "other_abl_unlock.img" }
  ]
}
```

### 2. 第三方系统下载列表（json/system_config.json）

「下载第三方系统/底包」页面读取该文件（缺失时自动从 Gitee 下载）。支持 `{"devices":[...]}` 与顶层数组两种结构。

| 字段（正式名） | 别名 | 必填 | 说明 |
| --- | --- | --- | --- |
| `name` | `device` | 推荐 | 机型显示名称；缺省显示为 "MatePad 11" |
| `systems` | — | 推荐 | 该系统所有可刷版本/底包的系统数组，每项含 `name`（显示名）与 `url`（下载链接） |

**示例**：

```json
{
  "devices": [
    {
      "name": "MatePad 11",
      "systems": [
        { "name": "鸿蒙4.2 日用版 by WQSQSQW", "url": "https://1821068313.share.123pan.cn/123pan/N3vxjv-QKnhh" }
      ]
    },
    {
      "name": "MatePad SE 11",
      "systems": [
        { "name": "鸿蒙4.3 by WQSQSQW", "url": "https://1821068313.share.123pan.cn/123pan/N3vxjv-vepQh" }
      ]
    }
  ]
}
```

> **新增机型**：在 `devices` 数组追加对象，并在其中 `systems` 里列出系统条目即可；重新打开页面或点【在线更新配置文件】生效。

### 3. 优化模块列表（json/optimize_modules.json）

「优化模块」页面读取该文件（缺失时自动下载）。支持 `{"modules":[...]}` 与顶层数组两种结构。

| 字段（正式名） | 别名 | 必填 | 说明 |
| --- | --- | --- | --- |
| `name` | — | ✅ | 模块显示名称；无 `name` 的条目会被忽略 |
| `file` | `filename` | 推荐 | 模块 zip 的文件名，用于与 `modules/` 目录对照安装；缺省时用 `name` 生成 |
| `type` | `mode` / `downloadType` / `download_type` | 可选 | 下载方式：`direct`（直链，默认）或 `netdisk`（跳转网盘） |
| `url` | `download` / `downloadurl` | 视需要 | 直链时为 zip 直链；网盘时为网盘分享链接 |
| `description` | `desc` | 可选 | 模块说明文字 |

- `type` 缺省或非法时一律按**直链**处理。
- 网盘模块点击【复制下载链接】复制链接并打开浏览器，需自行下载 zip 放入 `modules/` 目录（文件名与 `file` 一致）后再【一键刷入】。

```json
{
  "modules": [
    {
      "name": "shamiko",
      "file": "Shamiko-v1.2.5-414-release.zip",
      "type": "netdisk",
      "url": "https://wwaxi.lanzoub.com/i6AHz48g6jti",
      "description": "密码1234"
    }
  ]
}
```

### 4. 故障排查问答（json/faq/{功能}.json）

每个功能页右上角的故障排查按钮，读取 `json/faq/` 下对应的问答 json（缺失时自动从 Gitee 下载）。文件为顶层数组：

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `question` | ✅ | 问题标题 |
| `answer` | ✅ | 回答内容（可换行，用 `\n` 表示） |

```json
[
  { "question": "一键刷入提示找不到模块文件？", "answer": "先点击【下载】，确认 modules 目录下已存在 json 中 file 字段对应的 zip 文件。" },
  { "question": "一键刷入失败？", "answer": "请依次确认：\n①已开启 USB 调试并连接电脑；\n②ADB 驱动已正确安装；\n③设备已授权本电脑。" }
]
```

文件名即各页面传入故障排查按钮的 `FeatureKey`，现有：`console`、`downgrade`、`download`、`driver`、`flash`、`official`、`root`、`unlock`。

## 刷机逻辑说明（UPDATE.APP 解包）

**刷第三方系统** 与 **刷官方系统（救砖）** 均直接刷 **UPDATE.APP**（`*.app` 升级包），程序通过 `tools/huawei_firmware_extractor.exe`（来自 [Natsume324/HuaweiFirmwareExtractor](https://github.com/Natsume324/HuaweiFirmwareExtractor)，由 `huawei_firmware_extractor.py` 用 PyInstaller 打包）把 UPDATE.APP 解包为各分区 `.img` 后刷入。

- **底包目录**：`base/`（放 `*.app`）；**官方系统包**：`system/`（放 `*.app`）。解包产物默认落在 `cache/`。
- **始终跳过的分区**（刷底包与官方系统都排除）：`userdata`、`sha256rsa`、`package_type`、`metadata`、`efi`、`crc`、`base_verlist`、`base_ver`、`oeminfo`。
- **刷底包（第三方系统）**：解包后**排除 abl 与全部 super 分片**（不刷入），其余镜像刷全；super 分区由页面中另一个下拉框**从 `system/` 目录选择一个任意 `.img`** 单独刷入目标 **super** 分区。
- **刷官方系统**：解包后仅排除 **abl**，super **照常刷入**——多个 sparse 分片（`super`、`super_2` 等）会先合并为 `super_merged.img`，并以分区名重写的方式正确刷写到 **super** 分区（修复旧版误把 `super_merged` 当分区名导致 `fastboot flash super_merged` 失败的问题）。
- **去 AVB 校验**：从所选底包中仅解包 vbmeta 相关镜像（`vbmeta`、`vbmeta_cust`、`vbmeta_hw_product`、`vbmeta_odm`、`vbmeta_vendor` 等），以 `--disable-verity --disable-verification` 刷入；9008 模式不执行该步骤。

## 9008 备份输出说明

备份完成后在所选保存目录（默认 `backup/`）下生成 `QCTool_ParReadback_<时间戳>/`：

| 路径 | 内容 |
| --- | --- |
| `images/` | 全部分区镜像，以及 `rawprogram*.xml`（按 LUN 0-5） |
| `gpt_and_xml/orig/` | 设备 GPT 原始备份（`gpt_main*.bin`、`gpt_backup*.bin`）、`partition.xml` |
| `gpt_and_xml/create/` | 可刷回 GPT 与 `patch*.xml`（需 `tools/ptool.exe`） |
| `flash_all.bat` | fastboot 一键回刷脚本 |

默认不回读的分区：`userdata`、`last_parti`、`mindowsesp`、`mindowswin`、`mindowsdat`。

## 9008 恢复说明

侧边栏 **9008备份/恢复** 页面顶部切换「备份」与「恢复」两模式。

**恢复流程**：

1. 选择备份目录（`images/` 与 `gpt_and_xml/create/` 所在目录）。若所选目录不直接包含备份，程序会自动向下递归查找子目录中的备份，找到多份时取最后修改（最新）的一份并回填路径；
2. 点击【恢复】后弹窗询问该备份**是否本机备份**：
   - **本机备份**：一次性刷入全部 `rawprogram*.xml` 与 `patch*.xml`（含基带/指纹分区）；
   - **他人备份**：自动**剔除基带/指纹相关分区**（`modemst1`、`modemst2`、`fsg`、`fsc`、`oeminfo`、`persist`、`frp`、`devinfo`），避免刷入他人设备基带导致 **IMEI / 基带丢失或变砖**；
3. 程序自动进入 9008 模式（鸿蒙 2-3 经 ADB 自动进入，鸿蒙 4 需工程线/短接手动进入）并发送引导；
4. 依次把 `images/` 下的 `rawprogram*.xml` 通过 `fh_loader` 刷入，再刷入 `gpt_and_xml/create/` 下的 `patch*.xml` 以修正 GPT；结束后重启设备。

> **关于空占位条目**：`rawprogram*.xml` 中 `filename=""` 的占位条目（如 `userdata`、`last_parti`）**必须保留**——直接剔除会导致 `fh_loader` 刷入异常。因此程序**只**在「他人备份」模式下剔除上述受保护的基带/指纹分区条目，空占位条目一律原样保留。
>
> 另：给 `fh_loader` 的 XML 必须带 `<?xml version="1.0" ?>` 声明，否则其精简解析器会报 `Unrecognized tag 'data'` / `Could not find closing />`，导致整份文件刷不进去。

## 运行日志

程序从启动到关闭产生的日志统一写入 exe 同目录下的 `log/` 目录，每次运行生成一个 `run_<时间戳>.log`，便于排查问题。全局异常（界面线程、AppDomain、任务调度器）也会被捕获写入日志。

## 从源码构建

前置条件：[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（命令行构建无需 Visual Studio；使用 VS 2022 需安装「.NET 桌面开发」工作负载，Windows App SDK 依赖会自动还原）。

```bash
git clone <本仓库地址>
cd 华为MatePad11工具箱-WinUI3源码/华为MatePad11工具箱-WinUI3

# 构建（WinUI 3 不支持 AnyCPU，需指定平台）
dotnet build MatePadToolbox.sln -c Release -p:Platform=x64

# 自包含单文件发布
dotnet publish MatePadToolbox/MatePadToolbox.csproj -c Release -p:Platform=x64 \
  -r win-x64 --self-contained true -o publish
```

支持 `x86` / `x64` / `ARM64` 三个平台，日常使用推荐 `x64`。

发布产物为**自包含单文件 exe**：已启用 `EnableCompressionInSingleFile`，所有 DLL、原生库与 .NET 运行时内嵌并压缩进单个 exe（x64 约 60MB），用户无需安装运行时。发布后将 exe 放入程序目录（`tools/`、`unlock/`、`json/`、`modules/` 等资源目录需与 exe 同级）即可运行。

## 风险提示

- 解锁 BL、刷机、9008 深度读写**都会清空数据**，且存在变砖风险，操作前请务必备份并确保电量充足
- 骁龙 8+ 平台部分机型存在版本限制（如 Mate50 / P60 需低于特定版本，否则可能黑砖），请以 `processors.json` 中 `chip` 字段的说明为准
- 处理器型号务必选对，**选错型号刷错引导/解锁镜像可能直接变砖**
- 刷机过程中请勿拔线、请勿让电脑休眠
- 仅供个人学习与研究使用，因使用本工具造成的任何后果由使用者自行承担

## 致谢

- 酷安 **@某贼** — 原高通工具箱 bat 脚本框架；**MagiskPatcher**（[mouzei/MagiskPatcher](https://github.com/mouzei/MagiskPatcher)）Magisk 修补工具
- **SYXZ**（xda）— MagiskPatcher 共同作者
- **LACS-Official** — **admt**（[LACS-Official/admt](https://github.com/LACS-Official/admt)）adb 工具集
- Qualcomm — QSaharaServer / fh_loader / ptool
- [HuaweiFirmwareExtractor](https://github.com/Natsume324/HuaweiFirmwareExtractor)（Natsume324）— UPDATE.APP 解包工具
- [APatch](https://github.com/bmax121/APatch)（kptools）与 [Magisk](https://github.com/topjohnwu/Magisk)
- [Shamiko](https://github.com/LSPosed/LSPosed.github.io/releases) 与 [LSPosed](https://github.com/LSPosed/LSPosed)
- [7-Zip](https://www.7-zip.org/)、Android platform-tools、HiSuite Proxy

## 开源协议

本项目源代码以 [GPL-3.0-or-later](./LICENSE) 协议开源；随程序分发使用的第三方工具（adb、fastboot、7z、QSaharaServer、fh_loader、ptool、kptools、MagiskPatcher 等）版权归各自所有者所有。
