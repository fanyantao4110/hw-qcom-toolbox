using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MatePadToolbox.Services;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace MatePadToolbox.Pages
{
    /// <summary>
    /// 9008 备份/恢复页。
    /// 备份：回读全分区（READ-ALL），生成 images / gpt_and_xml / flash_all.bat。
    /// 恢复：选择备份目录，把其中的全部分区镜像（images\rawprogram0-5.xml）与
    ///       分区表补丁（gpt_and_xml\create\patch0-5.xml）依次刷入设备（对应原版高通
    ///       工具箱 write.bat 的 qcedlxml 刷写；patch 用于修复 GPT 备份表项）。
    ///       恢复前询问备份是否来自本机：本机备份刷入全部镜像（含基带 modemst1/modemst2/
    ///       fsg/fsc 与指纹 oeminfo/persist/frp/devinfo）；他人备份跳过这些分区，避免
    ///       基带丢失或指纹失效（对应 v3.1 的“保护基带指纹”）。
    /// </summary>
    public sealed partial class Backup9008Page : Page
    {
        /// <summary>受“保护基带指纹”保护、他人备份不刷的分区（对应 v3.1 Field94）。</summary>
        private static readonly HashSet<string> ProtectedBasebandParts = new(StringComparer.OrdinalIgnoreCase)
        {
            "modemst1", "modemst2", "fsg", "fsc", "oeminfo", "persist", "frp", "devinfo"
        };

        private CancellationTokenSource? _cts;

        public Backup9008Page()
        {
            this.InitializeComponent();
            this.Loaded += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(SavePathBox.Text))
                {
                    SavePathBox.Text = AppConfig.BackupDir;
                }
                if (string.IsNullOrWhiteSpace(RestorePathBox.Text))
                {
                    RestorePathBox.Text = AppConfig.BackupDir;
                }
            };
        }

        // ==================== 备份模式 ====================

        private async void StartBackup_Click(object sender, RoutedEventArgs e)
        {
            // 处理器型号检查：必须在侧边栏顶部选择型号，防止误操作
            if (GlobalProcessorService.Current == null)
            {
                Log.Append("[错误] 请先在侧边栏顶部的【CPU型号】下拉框中选择处理器型号。");
                return;
            }

            // 保存目录
            var saveRoot = (SavePathBox.Text ?? "").Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(saveRoot))
            {
                Log.Append("[错误] 请先选择保存目录。");
                return;
            }
            try
            {
                Directory.CreateDirectory(saveRoot);
            }
            catch (Exception ex)
            {
                Log.Append($"[错误] 无法创建保存目录：{ex.Message}");
                return;
            }

            // 文件检查
            var toolOk = CheckTools();
            if (!toolOk) return;
            Log.Append($"[OK] 文件检查通过（处理器：{GlobalProcessorService.DisplayName}）。");

            // 确认弹窗
            var confirm = new ContentDialog
            {
                Title = "回读全分区",
                Content = "将回读设备全部分区镜像（userdata、last_parti 等默认跳过），耗时较长。\n\n流程：程序将先按所选系统版本进入 9008 模式（鸿蒙2/鸿蒙3 通过 ADB 自动进入；鸿蒙4 需用工程线/短接进入），进入 9008 后再回读全部分区。\n\n是否继续？",
                PrimaryButtonText = "继续",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                var port = await Enter9008Async();
                if (port < 0) return;

                // 回读全分区（对应 qctool edlreadall 的完整流程）
                var ok = await EdlBackupService.RunReadAllAsync(saveRoot, port, Log.Append, _cts.Token);
                if (ok)
                {
                    Log.Append("9008回读全分区完成。");
                }
            }
            catch (OperationCanceledException)
            {
                Log.Append("[已取消] 回读全分区已停止。");
            }
            catch (Exception ex)
            {
                Log.Append($"[错误] 发生异常：{ex.Message}");
            }
            finally
            {
                SetBusy(false);
            }
        }

        // ==================== 恢复模式 ====================

        private async void StartRestore_Click(object sender, RoutedEventArgs e)
        {
            // 处理器型号检查
            if (GlobalProcessorService.Current == null)
            {
                Log.Append("[错误] 请先在侧边栏顶部的【CPU型号】下拉框中选择处理器型号。");
                return;
            }

            // 备份目录（自动下钻：若所选目录不直接含备份，则向下查找子目录）
            var selectedDir = (RestorePathBox.Text ?? "").Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(selectedDir))
            {
                Log.Append("[错误] 请先选择备份目录。");
                return;
            }
            if (!Directory.Exists(selectedDir))
            {
                Log.Append($"[错误] 找不到备份目录：{selectedDir}");
                return;
            }
            var backupDir = ResolveBackupDir(selectedDir);
            if (backupDir == null)
            {
                Log.Append($"[错误] 所选目录及其子目录中未找到备份（images\\rawprogram*.xml）：{selectedDir}");
                return;
            }
            if (!Path.GetFullPath(backupDir).Equals(Path.GetFullPath(selectedDir), StringComparison.OrdinalIgnoreCase))
            {
                RestorePathBox.Text = backupDir;
            }

            // 收集 rawprogram 与 patch xml（原版脚本最多 6 个 LUN：rawprogram0-5.xml + patch0-5.xml）
            var imagesDir = Path.Combine(backupDir, "images");
            var createDir = Path.Combine(backupDir, "gpt_and_xml", "create");
            var rawList = new List<string>();
            var patchList = new List<string>();
            for (int lun = 0; lun <= 5; lun++)
            {
                var rp = Path.Combine(imagesDir, $"rawprogram{lun}.xml");
                if (File.Exists(rp)) rawList.Add(rp);
                var pt = Path.Combine(createDir, $"patch{lun}.xml");
                if (File.Exists(pt)) patchList.Add(pt);
            }
            if (rawList.Count == 0)
            {
                Log.Append($"[错误] 备份目录中未找到 {Path.Combine(imagesDir, "rawprogram0-5.xml")}，请选择正确的备份目录。");
                return;
            }
            if (patchList.Count == 0)
            {
                Log.Append($"[警告] 未找到 {Path.Combine(createDir, "patch0-5.xml")}，将只刷入分区镜像，不修复分区表补丁。");
            }

            // 工具文件检查
            var toolOk = CheckTools();
            if (!toolOk) return;

            // 询问备份是否来自本机（决定是否刷入基带/指纹分区）
            var askOwn = new ContentDialog
            {
                Title = "基带指纹保护",
                Content = "该备份是否来自当前这台设备？\n\n" +
                          "· 【本机备份】将刷入全部镜像，包括基带（modemst1/modemst2/fsg/fsc）与指纹（oeminfo/persist/frp/devinfo）相关分区。\n" +
                          "· 【他人备份】将跳过上述基带与指纹分区，仅刷入系统/引导等镜像，避免基带丢失或指纹失效。",
                PrimaryButtonText = "本机备份",
                SecondaryButtonText = "他人备份",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };
            bool flashBaseband;
            var ownResult = await askOwn.ShowAsync();
            if (ownResult == ContentDialogResult.Primary) flashBaseband = true;
            else if (ownResult == ContentDialogResult.Secondary) flashBaseband = false;
            else return; // 取消

            // 最终确认
            var parts = rawList.Count + patchList.Count;
            var confirm = new ContentDialog
            {
                Title = "恢复备份",
                Content = $"将把备份目录中的 {rawList.Count} 个 rawprogram 与 {patchList.Count} 个 patch XML 依次刷入设备（共 {parts} 份）。\n\n" +
                          (flashBaseband
                              ? "已选择【本机备份】：将刷入全部镜像（含基带/指纹分区）。"
                              : "已选择【他人备份】：将跳过基带/指纹分区，保护基带指纹。") +
                          "\n\n进入 9008 的流程与备份一致。是否开始？",
                PrimaryButtonText = "开始恢复",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                var port = await Enter9008Async();
                if (port < 0) return;

                // 探测存储类型（ufs/emmc/spinor），供 fh_loader --memoryname 使用
                var info = await EdlBackupService.DetectMemoryInfoAsync(port, Log.Append, _cts.Token);
                if (info == null) return;
                Log.Append($"[OK] 存储类型：{info.MemType}，开始恢复刷机...");

                // 1) 依次刷入 rawprogram0-5.xml（对应原版 write.bat qcedlxml：--search_path 指向镜像目录）
                foreach (var rp in rawList)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    // 预处理：保留空 filename 的占位条目（如 userdata，剔除反而会导致刷入 bug），
                    // 仅按基带保护策略决定是否剔除受保护的基带/指纹分区。
                    var xmlToSend = PrepareRawXml(rp, flashBaseband);
                    if (xmlToSend == null)
                    {
                        Log.Append($"[OK] {Path.GetFileName(rp)} 无需刷入，已跳过。");
                        continue;
                    }
                    Log.Append($"正在刷入 {Path.GetFileName(rp)} ...");
                    var ok = await EdlService.SendXmlAsync(port, xmlToSend, imagesDir, Log.Append, _cts.Token, info.MemType);
                    Log.Append(ok ? $"[OK] {Path.GetFileName(rp)} 刷入成功。" : $"[错误] {Path.GetFileName(rp)} 刷入失败！");
                }

                // 2) 依次刷入 patch0-5.xml（对应原版“应用 patch”：修复 GPT 备份表项）
                foreach (var pt in patchList)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    Log.Append($"正在应用 {Path.GetFileName(pt)} ...");
                    var ok = await EdlService.SendXmlAsync(port, pt, createDir, Log.Append, _cts.Token, info.MemType);
                    Log.Append(ok ? $"[OK] {Path.GetFileName(pt)} 应用成功。" : $"[错误] {Path.GetFileName(pt)} 应用失败！");
                }

                // 3) 重启设备
                Log.Append("恢复完成，正在重启设备...");
                await EdlService.RebootDeviceAsync(port, imagesDir, Log.Append, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log.Append("[已取消] 恢复刷机已停止。");
            }
            catch (Exception ex)
            {
                Log.Append($"[错误] 发生异常：{ex.Message}");
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>
        /// 预处理待发送给 fh_loader 的 rawprogram XML。
        /// 空 filename 的占位条目（如 userdata / last_parti）一律保留，直接剔除会导致刷入 bug；
        /// 仅当 flashBaseband 为 false（他人备份）时剔除受“保护基带指纹”保护的分区条目。
        /// 无改动时返回原路径；有剔除时写出过滤后的临时 XML 并返回其路径；
        /// 若剔除后已无任何分区条目，返回 null 表示该文件无需再发送。
        /// </summary>
        private string? PrepareRawXml(string rawXmlPath, bool flashBaseband)
        {
            try
            {
                var doc = XDocument.Load(rawXmlPath);
                if (doc.Root == null) return rawXmlPath;

                int removedProtected = 0;
                // 保留空 filename 的占位条目（如 userdata）：直接剔除会导致 fh_loader 刷入 bug，
                // 因此只按"他人备份"策略剔除受保护的基带/指纹分区，空文件名占位条目一律保留。
                foreach (var prog in doc.Root.Elements("program").ToList())
                {
                    var label = (string?)prog.Attribute("label") ?? "";

                    // 他人备份：剔除基带/指纹分区
                    if (!flashBaseband && ProtectedBasebandParts.Contains(label.Trim()))
                    {
                        prog.Remove();
                        removedProtected++;
                    }
                }

                if (removedProtected == 0) return rawXmlPath;

                // 【修复】必须带 xml 声明重新写出，不能直接 XDocument.ToString()：
                // LINQ to XML 的 ToString() 不会输出 <?xml version="1.0" ?> 声明，而 fh_loader 自带的
                // 精简解析器依赖该声明定位文档起始，缺失时会直接报
                // “XML is not formatted correctly. Could not find closing />” 导致该文件完全无法刷入。
                // 因此这里按原版备份文件的格式重建：声明 + data + 每行一个 program（无缩进）。
                var programs = doc.Root.Elements().ToList();
                if (programs.Count == 0)
                {
                    Log.Append($"[基带保护] {Path.GetFileName(rawXmlPath)} 剔除后已无分区条目，跳过刷入。");
                    return null;
                }

                var filteredPath = AppConfig.WriteTmpFile(
                    Path.GetFileNameWithoutExtension(rawXmlPath) + "_nooem.xml",
                    EdlService.WrapRawProgramXml(programs.Select(p => p.ToString(SaveOptions.DisableFormatting))));
                Log.Append($"[基带保护] {Path.GetFileName(rawXmlPath)} 剔除 {removedProtected} 个基带/指纹分区条目。");
                return filteredPath;
            }
            catch (Exception ex)
            {
                Log.Append($"[警告] 解析 {Path.GetFileName(rawXmlPath)} 失败（{ex.Message}），按原样刷入。");
                return rawXmlPath;
            }
        }

        // ==================== 公共辅助 ====================

        /// <summary>
        /// 解析恢复用的备份目录：若所选目录自身含 images\rawprogram*.xml 则直接使用；
        /// 否则自动向下递归查找子目录中的备份。找到多个时取最后修改（最新）的一个。
        /// 未找到返回 null。
        /// </summary>
        private string? ResolveBackupDir(string selected)
        {
            if (IsBackupDir(selected)) return selected;

            var found = new List<string>();
            CollectBackupDirs(selected, 0, 12, found);
            if (found.Count == 0) return null;

            var latest = found
                .OrderByDescending(d => Directory.GetLastWriteTime(Path.Combine(d, "images"))).First();
            if (found.Count > 1)
            {
                Log.Append($"[提示] 所选目录下找到 {found.Count} 份备份，自动选用最新一份：{latest}");
            }
            else
            {
                Log.Append($"[OK] 已自动定位备份目录：{latest}");
            }
            return latest;
        }

        /// <summary>判断某目录是否为有效备份目录（含 images\rawprogram*.xml）。</summary>
        private static bool IsBackupDir(string dir)
        {
            var images = Path.Combine(dir, "images");
            return Directory.Exists(images) &&
                   Directory.EnumerateFiles(images, "rawprogram*.xml").Any();
        }

        /// <summary>递归收集所有符合备份结构的子目录（限定深度，避免遍历过深）。</summary>
        private static void CollectBackupDirs(string dir, int depth, int maxDepth, List<string> found)
        {
            if (depth > maxDepth) return;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    if (IsBackupDir(sub))
                    {
                        found.Add(sub);
                        continue; // 已是备份目录则不再深入
                    }
                    CollectBackupDirs(sub, depth + 1, maxDepth, found);
                }
            }
            catch
            {
                // 忽略无权限/异常目录
            }
        }

        /// <summary>校验所需工具文件是否存在。</summary>
        private bool CheckTools()
        {
            if (!File.Exists(AppConfig.QSaharaServerExe))
            {
                Log.Append("[错误] 缺少 tools\\QSaharaServer.exe");
                return false;
            }
            if (!File.Exists(AppConfig.FhLoaderExe))
            {
                Log.Append("[错误] 缺少 tools\\fh_loader.exe");
                return false;
            }
            if (!File.Exists(GlobalProcessorService.DevprgPath))
            {
                Log.Append($"[错误] 缺少底层文件 {GlobalProcessorService.DevprgPath}");
                return false;
            }
            return true;
        }

        /// <summary>进入 9008 模式，返回 COM 端口号（失败返回 -1）。</summary>
        private async Task<int> Enter9008Async()
        {
            var rebootViaAdb = OsVersionGroup.SelectedIndex == 0;
            if (rebootViaAdb)
            {
                Log.Append("检测 ADB 设备...");
                if (!await DeviceService.WaitForAdbAsync(Log.Append, _cts!.Token)) return -1;
                Log.Append("[OK] 设备已连接。");
                await DeviceService.RebootToEdlAsync(Log.Append);
                Log.Append("等待设备进入 9008 模式...");
            }
            else
            {
                Log.Append("等待设备进入 9008 模式（请确保已通过工程线/短接进入）...");
            }

            var port = await ComPortDetector.WaitFor9008Async(15, 2000, Log.Append, _cts!.Token);
            if (!port.HasValue)
            {
                Log.Append("[错误] 等待超时，未检测到 9008 设备。");
                if (!rebootViaAdb)
                {
                    Log.Append("请确认已通过工程线/短接进入 9008，或先降级到鸿蒙2/鸿蒙3。");
                }
                return -1;
            }
            Log.Append($"[OK] 已检测到 9008 设备，端口 COM{port.Value}");

            // 上传编程器（对应 write qcedlsendfh：QSaharaServer -s 13:firehose）
            if (!await EdlService.UploadFirehoseAsync(port.Value, Log.Append, _cts.Token, GlobalProcessorService.DevprgPath)) return -1;

            // 配置端口（对应 write qcedlsendfh %port% %fh% auto 的配置端口步骤）
            if (!await EdlService.ConfigurePortAutoAsync(port.Value, Log.Append, _cts.Token)) return -1;

            return port.Value;
        }

        private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            var picked = await PickFolderAsync();
            if (picked != null) SavePathBox.Text = picked;
        }

        private async void BrowseRestoreFolder_Click(object sender, RoutedEventArgs e)
        {
            var picked = await PickFolderAsync();
            if (picked != null) RestorePathBox.Text = picked;
        }

        private async Task<string?> PickFolderAsync()
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FolderPicker();
                picker.FileTypeFilter.Add("*");
                if (App.MainWindow != null)
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                }
                var folder = await picker.PickSingleFolderAsync();
                return folder?.Path;
            }
            catch (Exception ex)
            {
                Log.Append($"[提示] 打开目录选择器失败（{ex.Message}），可直接在输入框中填写路径。");
                return null;
            }
        }

        private void OpenBackupDir_Click(object sender, RoutedEventArgs e)
        {
            var dir = (SavePathBox.Text ?? "").Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                ProcessRunner.ExploreFolder(dir);
            }
            else
            {
                ProcessRunner.ExploreFolder(AppConfig.BackupDir);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
        }

        private void SetBusy(bool busy)
        {
            BtnStart.IsEnabled = !busy;
            BtnCancel.IsEnabled = busy;
            BtnBrowse.IsEnabled = !busy;
            SavePathBox.IsEnabled = !busy;
            BtnRestore.IsEnabled = !busy;
            BtnCancelRestore.IsEnabled = busy;
            BtnBrowseRestore.IsEnabled = !busy;
            RestorePathBox.IsEnabled = !busy;
            OsVersionGroup.IsEnabled = !busy;
            ModePivot.IsEnabled = !busy;
        }
    }
}
