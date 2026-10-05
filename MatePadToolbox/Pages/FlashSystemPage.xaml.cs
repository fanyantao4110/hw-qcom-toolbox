using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MatePadToolbox.Models;
using MatePadToolbox.Services;
using System.IO;

namespace MatePadToolbox.Pages
{
    /// <summary>
    /// 刷入第三方系统页：Fastboot 模式与 9008 模式。
    /// 对应 bat 的 :flash_entry_menu / :flash_system_entry / :flash_9008_menu 及其子流程。
    /// </summary>
    public sealed partial class FlashSystemPage : Page
    {
        private CancellationTokenSource? _cts;
        private List<string> _baseApps = new();
        private List<string> _superImgs = new();
        private int? _q9008Port;

        public FlashSystemPage()
        {
            this.InitializeComponent();
            this.Loaded += (_, _) => RefreshBaseList();
        }

        // ==================== 公共 ====================

        private void RefreshBaseList_Click(object sender, RoutedEventArgs e) => RefreshBaseList();

        private void RefreshBaseList()
        {
            _baseApps = Directory.Exists(AppConfig.BaseRomDir)
                ? Directory.EnumerateFiles(AppConfig.BaseRomDir, "*.app").OrderBy(f => f).ToList()
                : new List<string>();

            var names = _baseApps.Select(Path.GetFileName).ToList();
            FbBaseCombo.ItemsSource = names;
            Q9008BaseCombo.ItemsSource = names;
            if (_baseApps.Count > 0)
            {
                FbBaseCombo.SelectedIndex = 0;
                Q9008BaseCombo.SelectedIndex = 0;
            }
            else
            {
                Log.Append($"[提示] 未在 base 目录找到任何 UPDATE.APP（{AppConfig.BaseRomDir}）。");
            }

            RefreshSuperList();
        }

        private void RefreshSuperList()
        {
            _superImgs = Directory.Exists(AppConfig.SystemDir)
                ? Directory.EnumerateFiles(AppConfig.SystemDir, "*.img").OrderBy(f => f).ToList()
                : new List<string>();

            var names = _superImgs.Select(Path.GetFileName).ToList();
            FbSuperImgCombo.ItemsSource = names;
            Q9008SuperImgCombo.ItemsSource = names;
            if (_superImgs.Count > 0)
            {
                FbSuperImgCombo.SelectedIndex = 0;
                Q9008SuperImgCombo.SelectedIndex = 0;
            }
        }

        private string? GetSelectedBaseApp(ComboBox combo)
        {
            if (combo.SelectedIndex < 0 || combo.SelectedIndex >= _baseApps.Count) return null;
            return _baseApps[combo.SelectedIndex];
        }

        private string? GetSelectedSuperImg(ComboBox combo)
        {
            if (combo.SelectedIndex < 0 || combo.SelectedIndex >= _superImgs.Count) return null;
            return _superImgs[combo.SelectedIndex];
        }

        private void LogSummary(FlashSummary s, string successMsg)
        {
            if (s.AllSuccess)
            {
                Log.Append("===============================================");
                Log.Append($"  {successMsg}");
                Log.Append("===============================================");
            }
            else
            {
                Log.Append("===============================================");
                Log.Append($"  刷写完成，但有 {s.FailedParts.Count} 个分区失败：");
                Log.Append($"  {string.Join(" ", s.FailedParts)}");
                Log.Append("===============================================");
            }
        }

        /// <summary>刷入 misc 分区（清除恢复指令），对应 bat 中的 misc 处理。</summary>
        private async Task FlashMiscFastbootAsync(CancellationToken ct)
        {
            Log.Append("尝试刷写 misc 分区（清除恢复指令）...");
            if (!File.Exists(AppConfig.MiscImg))
            {
                Log.Append("[错误] 缺少 tools\\rawprogram\\misc.img");
                return;
            }
            var ok = await DeviceService.FlashPartitionAsync("misc", AppConfig.MiscImg, Log.Append, ct);
            if (!ok) Log.Append("[警告] 写入 misc 分区失败！");
        }

        private void SetBusy(bool busy)
        {
            BtnFbBase.IsEnabled = !busy;
            BtnFbVbmeta.IsEnabled = !busy;
            BtnFbSuper.IsEnabled = !busy;
            BtnFbOneClick.IsEnabled = !busy;
            BtnFbCancel.IsEnabled = busy;
            Btn9008Connect.IsEnabled = !busy;
            Btn9008Base.IsEnabled = !busy;
            Btn9008Super.IsEnabled = !busy;
            Btn9008OneClick.IsEnabled = !busy;
            Btn9008Cancel.IsEnabled = busy;
            FbBaseCombo.IsEnabled = !busy;
            Q9008BaseCombo.IsEnabled = !busy;
            FbSuperImgCombo.IsEnabled = !busy;
            Q9008SuperImgCombo.IsEnabled = !busy;
        }

        // ==================== Fastboot 模式 ====================

        private async void FbFlashBase_Click(object sender, RoutedEventArgs e)
        {
            var app = GetSelectedBaseApp(FbBaseCombo);
            if (app == null) { Log.Append("[错误] 请先选择底包 UPDATE.APP。"); return; }
            if (!await UiHelpers.ConfirmAsync(this.XamlRoot, "警告", "此操作将会清除所有数据！\n\n是否继续？")) return;

            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                Log.Append("准备进入 Fastboot 模式...");
                if (!await UiHelpers.PrepareFastbootAsync(this.XamlRoot, Log.Append, _cts.Token)) return;
                await FlashBaseAppViaFastbootAsync(app, _cts.Token);

                if (!await UiHelpers.ConfirmAsync(this.XamlRoot, "底包刷写完成", "是否继续去除 avb 校验？", "继续", "返回"))
                    return;
                await FlashVbmetaFromCacheAsync(_cts.Token);
            }
            catch (OperationCanceledException) { Log.Append("[已取消]"); }
            finally { SetBusy(false); }
        }

        private async void FbFlashVbmeta_Click(object sender, RoutedEventArgs e)
        {
            var app = GetSelectedBaseApp(FbBaseCombo);
            if (app == null) { Log.Append("[错误] 请先选择底包 UPDATE.APP，将只从中解包 vbmeta 相关镜像。"); return; }
            if (!await UiHelpers.ConfirmAsync(this.XamlRoot, "警告", "此操作将会清除所有数据！\n\n是否继续？")) return;

            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                Log.Append("准备进入 Fastboot 模式...");
                if (!await UiHelpers.PrepareFastbootAsync(this.XamlRoot, Log.Append, _cts.Token)) return;
                await FlashVbmetaViaFastbootAsync(app, _cts.Token);
            }
            catch (OperationCanceledException) { Log.Append("[已取消]"); }
            finally { SetBusy(false); }
        }

        private async void FbFlashSuper_Click(object sender, RoutedEventArgs e)
        {
            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                Log.Append("准备进入 Fastboot 模式...");
                if (!await UiHelpers.PrepareFastbootAsync(this.XamlRoot, Log.Append, _cts.Token)) return;
                await FlashSuperViaFastbootAsync(_cts.Token);
            }
            catch (OperationCanceledException) { Log.Append("[已取消]"); }
            finally { SetBusy(false); }
        }

        private async void FbOneClick_Click(object sender, RoutedEventArgs e)
        {
            var app = GetSelectedBaseApp(FbBaseCombo);
            if (app == null) { Log.Append("[错误] 请先选择底包 UPDATE.APP。"); return; }
            if (!await UiHelpers.ConfirmAsync(this.XamlRoot, "一键执行",
                "将依次执行：刷入底包 → 去除 avb 校验 → 刷入 super。\n此操作将会清除所有数据！\n\n是否继续？")) return;

            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                Log.Append("准备进入 Fastboot 模式...");
                if (!await UiHelpers.PrepareFastbootAsync(this.XamlRoot, Log.Append, _cts.Token)) return;

                Log.Append("===== 步骤 1/3：刷入底包 =====");
                if (!await FlashBaseAppViaFastbootAsync(app, _cts.Token)) return;

                Log.Append("===== 步骤 2/3：去除 avb 校验 =====");
                if (!await FlashVbmetaFromCacheAsync(_cts.Token)) return;

                Log.Append("===== 步骤 3/3：刷入 super =====");
                await FlashSuperViaFastbootAsync(_cts.Token);
            }
            catch (OperationCanceledException) { Log.Append("[已取消]"); }
            finally { SetBusy(false); }
        }

        private void FbCancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        /// <summary>Fastboot 刷底包：解包 UPDATE.APP → 过滤 abl/super → 刷入其余分区 → 刷 misc。
        /// （不解包后立即清理 cache：后续去除 AVB 校验步骤会复用 cache 中已解包的 vbmeta 镜像。）</summary>
        private async Task<bool> FlashBaseAppViaFastbootAsync(string app, CancellationToken ct)
        {
            Log.Append($"已选择：{Path.GetFileName(app)}");
            AppConfig.ResetCacheDir();
            var imgs = await UpdateAppService.ExtractAsync(app, AppConfig.CacheDir, Log.Append, ct);
            if (imgs == null) return false;

            // 底包不刷 abl 与 super 分区
            var targets = UpdateAppService.FilterImages(imgs, Log.Append, excludeSuper: true, excludeAbl: true);

            var summary = await DeviceService.FlashAllImagesAsync(AppConfig.CacheDir, recursive: true, Log.Append, ct,
                partitionFilter: targets.Select(f => Path.GetFileName(f)!).ToList());
            LogSummary(summary, "底包刷入全部成功！（已跳过 abl / super 分区）");
            await FlashMiscFastbootAsync(ct);
            return true;
        }

        /// <summary>
        /// 独立去除 avb 校验：从选中的底包 UPDATE.APP 中仅解包 vbmeta 相关镜像，
        /// 逐个执行 fastboot --disable-verity --disable-verification flash <分区> <镜像>，最后刷 misc 并清理 cache。
        /// </summary>
        private async Task<bool> FlashVbmetaViaFastbootAsync(string baseApp, CancellationToken ct, bool askReboot = true)
        {
            if (!File.Exists(baseApp))
            {
                Log.Append($"[错误] 底包不存在：{baseApp}");
                return false;
            }

            Log.Append($"正在读取底包分区列表：{Path.GetFileName(baseApp)}");
            var partitions = await UpdateAppService.ListPartitionsAsync(baseApp, Log.Append, ct);
            if (partitions == null) return false;

            var vbmetaNames = partitions.Where(UpdateAppService.IsVbmetaPartitionName).ToList();
            if (vbmetaNames.Count == 0)
            {
                Log.Append("[错误] 底包中未找到任何 vbmeta 分区（vbmeta、vbmeta_cust、vbmeta_hw_product、vbmeta_odm、vbmeta_vendor 等），无法去除 AVB 校验。");
                return false;
            }
            Log.Append($"检测到 {vbmetaNames.Count} 个 vbmeta 分区：{string.Join(", ", vbmetaNames)}");

            AppConfig.ResetCacheDir();
            var imgs = await UpdateAppService.ExtractAsync(baseApp, AppConfig.CacheDir, Log.Append, ct, partitionFilter: vbmetaNames);
            if (imgs == null) return false;

            var targets = imgs.Where(UpdateAppService.IsVbmetaImage)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            if (targets.Count == 0)
            {
                Log.Append("[错误] 解包后未找到任何 vbmeta 镜像。");
                AppConfig.ResetCacheDir();
                return false;
            }

            var allOk = await FlashVbmetaImagesAsync(targets, ct);
            AppConfig.ResetCacheDir();

            if (askReboot)
            {
                if (await UiHelpers.ConfirmAsync(this.XamlRoot, "操作完成", "是否重启设备？", "重启", "不重启"))
                {
                    await DeviceService.RebootAsync(Log.Append);
                }
            }
            return allOk;
        }

        /// <summary>
        /// 一键执行中的去除 avb 校验：直接复用底包刷写时留在 cache 的已解包镜像（不再二次解包），
        /// 刷入完成后清理 cache。
        /// </summary>
        private async Task<bool> FlashVbmetaFromCacheAsync(CancellationToken ct)
        {
            var allImgs = Directory.Exists(AppConfig.CacheDir)
                ? Directory.EnumerateFiles(AppConfig.CacheDir, "*.img", SearchOption.TopDirectoryOnly)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();
            if (allImgs.Count == 0)
            {
                Log.Append("[错误] cache 目录中没有已解包的镜像，请先执行「刷入底包」以解包底包。");
                return false;
            }

            var targets = allImgs.Where(UpdateAppService.IsVbmetaImage).ToList();
            if (targets.Count == 0)
            {
                Log.Append("[错误] cache 中未找到任何 vbmeta 镜像（vbmeta、vbmeta_cust、vbmeta_hw_product、vbmeta_odm、vbmeta_vendor 等），无法去除 AVB 校验。");
                return false;
            }
            Log.Append($"检测到 {targets.Count} 个 vbmeta 镜像：{string.Join(", ", targets.Select(Path.GetFileName))}");

            var allOk = await FlashVbmetaImagesAsync(targets, ct);
            AppConfig.ResetCacheDir();
            return allOk;
        }

        /// <summary>逐个刷入 vbmeta 镜像（--disable-verity --disable-verification），并刷 misc。</summary>
        private async Task<bool> FlashVbmetaImagesAsync(IEnumerable<string> targets, CancellationToken ct)
        {
            var allOk = true;
            foreach (var img in targets)
            {
                ct.ThrowIfCancellationRequested();
                var part = Path.GetFileNameWithoutExtension(img);
                allOk &= await DeviceService.FlashVbmetaPartitionAsync(part, img, Log.Append, ct);
            }
            await FlashMiscFastbootAsync(ct);

            if (allOk)
            {
                Log.Append("===============================================");
                Log.Append("  去除 AVB 校验刷写成功！");
                Log.Append("===============================================");
            }
            else
            {
                Log.Append("[警告] 部分 vbmeta 分区刷写失败，请检查上方日志。");
            }
            return allOk;
        }

        /// <summary>Fastboot 刷入 super 分区：使用 system 目录中用户选择的 img 镜像。</summary>
        private async Task<bool> FlashSuperViaFastbootAsync(CancellationToken ct)
        {
            var imgFile = GetSelectedSuperImg(FbSuperImgCombo);
            if (imgFile == null)
            {
                Log.Append("[错误] 未在 system 目录找到任何 .img 文件。");
                Log.Append($"请确认已将镜像文件放入：{AppConfig.SystemDir}");
                return false;
            }

            Log.Append($"检测到选择的 super 镜像：{Path.GetFileName(imgFile)}，将刷入 super 分区。");
            var ok = await DeviceService.FlashPartitionAsync("super", imgFile, Log.Append, ct);
            if (!ok) { Log.Append("[错误] super 分区刷写失败！"); return false; }

            Log.Append("正在重启设备...");
            await DeviceService.RebootAsync(Log.Append);
            Log.Append("刷入完成，设备正在重启！");
            return true;
        }

        // ==================== 9008 模式 ====================

        private async void Q9008Connect_Click(object sender, RoutedEventArgs e)
        {
            if (GlobalProcessorService.Current == null)
            {
                Log.Append("[错误] 请先在侧边栏顶部的【CPU型号】下拉框中选择处理器型号。");
                return;
            }
            var rebootViaAdb = await UiHelpers.Ask9008StateAsync(this.XamlRoot);
            if (rebootViaAdb == null) return;

            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                var port = await EdlService.ConnectAsync(rebootViaAdb.Value, Log.Append, _cts.Token, GlobalProcessorService.DevprgPath);
                if (port > 0)
                {
                    _q9008Port = port;
                    PortStatusText.Text = $"已连接：COM{port}";
                    Log.Append("编程器和端口配置完成，设备已经连接。");
                }
                else
                {
                    _q9008Port = null;
                    PortStatusText.Text = "未连接";
                }
            }
            catch (OperationCanceledException) { Log.Append("[已取消]"); }
            finally { SetBusy(false); }
        }

        private bool Require9008Connection()
        {
            if (_q9008Port.HasValue) return true;
            Log.Append("[错误] 尚未连接 9008 设备，请先点击【连接9008端口】。");
            return false;
        }

        private async void Q9008FlashBase_Click(object sender, RoutedEventArgs e)
        {
            if (!Require9008Connection()) return;
            var app = GetSelectedBaseApp(Q9008BaseCombo);
            if (app == null) { Log.Append("[错误] 请先选择底包 UPDATE.APP。"); return; }
            if (!await UiHelpers.ConfirmAsync(this.XamlRoot, "警告", "此操作将会清除所有数据！\n\n是否继续？")) return;

            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                await FlashBaseVia9008Async(_q9008Port!.Value, app, _cts.Token);
                await AskReboot9008Async(_q9008Port.Value, _cts.Token);
            }
            catch (OperationCanceledException) { Log.Append("[已取消]"); }
            finally { SetBusy(false); }
        }

        private async void Q9008FlashSuper_Click(object sender, RoutedEventArgs e)
        {
            if (!Require9008Connection()) return;

            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                await FlashSuperVia9008Async(_q9008Port!.Value, _cts.Token);
                await AskReboot9008Async(_q9008Port.Value, _cts.Token);
            }
            catch (OperationCanceledException) { Log.Append("[已取消]"); }
            finally { SetBusy(false); }
        }

        private async void Q9008OneClick_Click(object sender, RoutedEventArgs e)
        {
            if (GlobalProcessorService.Current == null)
            {
                Log.Append("[错误] 请先在侧边栏顶部的【CPU型号】下拉框中选择处理器型号。");
                return;
            }
            var app = GetSelectedBaseApp(Q9008BaseCombo);
            if (app == null) { Log.Append("[错误] 请先选择底包 UPDATE.APP。"); return; }
            if (!await UiHelpers.ConfirmAsync(this.XamlRoot, "9008 一键执行",
                "将依次执行：选择系统版本 → 连接9008 → 刷入底包 → 刷入super。\n此操作将会清除所有数据！\n\n是否继续？")) return;

            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                Log.Append("===== 步骤 1/4：选择系统版本 =====");
                var rebootViaAdb = await UiHelpers.Ask9008StateAsync(this.XamlRoot);
                if (rebootViaAdb == null) { Log.Append("[已取消]"); return; }

                Log.Append("===== 步骤 2/4：连接 9008 =====");
                var port = await EdlService.ConnectAsync(rebootViaAdb.Value, Log.Append, _cts.Token, GlobalProcessorService.DevprgPath);
                if (port <= 0) return;
                _q9008Port = port;
                PortStatusText.Text = $"已连接：COM{port}";

                Log.Append("===== 步骤 3/4：刷入底包 =====");
                if (!await FlashBaseVia9008Async(port, app, _cts.Token)) return;

                Log.Append("[提示] 9008 模式不执行去除 AVB 校验；如需去除，请切到【Fastboot模式】选项卡执行（fastboot --disable-verity --disable-verification flash vbmeta vbmeta.img 等）。");

                Log.Append("===== 步骤 4/4：刷入 super =====");
                if (!await FlashSuperVia9008Async(port, _cts.Token)) return;

                Log.Append("正在重启设备...");
                await EdlService.RebootDeviceAsync(port, AppConfig.UnlockDir, Log.Append, _cts.Token);
                Log.Append("如果设备没有自动进入任何模式，请长按电源键重新激活开机！");
                Log.Append("===============================================");
                Log.Append("  9008模式一键执行完成！");
                Log.Append("===============================================");
                _q9008Port = null;
                PortStatusText.Text = "未连接";
            }
            catch (OperationCanceledException) { Log.Append("[已取消]"); }
            finally { SetBusy(false); }
        }

        private void Q9008Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        /// <summary>9008 刷底包：解包 UPDATE.APP → 过滤 abl/super → 读取分区表 → 为剩余镜像动态生成 xml 并刷写。</summary>
        private async Task<bool> FlashBaseVia9008Async(int port, string app, CancellationToken ct)
        {
            Log.Append($"已选择：{Path.GetFileName(app)}");
            AppConfig.ResetCacheDir();
            var imgs = await UpdateAppService.ExtractAsync(app, AppConfig.CacheDir, Log.Append, ct);
            if (imgs == null) return false;

            // 底包不刷 abl 与 super 分区
            var targets = UpdateAppService.FilterImages(imgs, Log.Append, excludeSuper: true, excludeAbl: true);
            Log.Append($"底包过滤后共 {targets.Count} 个分区可刷（已排除 abl / super）。");

            // 动态读取设备分区表，为所有镜像定位分区并生成 xml
            Log.Append("正在读取设备分区表...");
            var table = await EdlService.LoadPartitionTableAsync(port, Log.Append, ct);
            if (table == null)
            {
                Log.Append("[错误] 读取设备分区表失败，中止底包刷写。");
                return false;
            }

            var result = await EdlService.FlashImagesAsync(
                port, table.Value.MemType, AppConfig.CacheDir, table.Value.Map, targets, Log.Append, ct);
            AppConfig.ResetCacheDir();

            Log.Append(result.FailedParts.Count == 0
                ? $"底包刷写完成，成功 {result.SuccessCount} 个分区，全部成功！"
                : $"底包刷写完成：成功 {result.SuccessCount} 个，失败/跳过 {result.FailedParts.Count} 个（{string.Join(" ", result.FailedParts)}）。");
            if (result.FailedParts.Count > 0)
                Log.Append("[警告] 存在未能匹配分区的镜像，请确认底包与当前设备的兼容性。");
            return result.SuccessCount > 0;
        }

        /// <summary>9008 刷入 super 分区：使用 system 目录中用户选择的 img 镜像，动态定位 super 分区后刷写。</summary>
        private async Task<bool> FlashSuperVia9008Async(int port, CancellationToken ct)
        {
            var imgFile = GetSelectedSuperImg(Q9008SuperImgCombo);
            if (imgFile == null)
            {
                Log.Append("[错误] 未在 system 目录找到任何 .img 文件。");
                Log.Append($"请将镜像文件放入：{AppConfig.SystemDir}");
                return false;
            }

            Log.Append("正在读取设备分区表以定位 super 分区...");
            var table = await EdlService.LoadPartitionTableAsync(port, Log.Append, ct);
            if (table == null)
            {
                Log.Append("[错误] 读取设备分区表失败，中止 super 刷写。");
                return false;
            }
            if (!table.Value.Map.TryGetValue("super", out var slot))
            {
                Log.Append("[错误] 设备分区表中未找到 super 分区。");
                return false;
            }

            Log.Append($"已定位 super 分区：LUN{slot.Lun}，起始扇区 {slot.StartSector}。正在刷写 {Path.GetFileName(imgFile)} ...");
            var xml = AppConfig.WriteTmpFile("rawprogram_super.xml", EdlService.BuildWritePartitionXml(imgFile, slot));
            var ok = await EdlService.SendXmlAsync(port, xml, AppConfig.SystemDir, Log.Append, ct, table.Value.MemType);
            Log.Append(ok ? "[OK] super 分区刷写成功。" : "[错误] super 分区刷写失败！");
            return ok;
        }

        /// <summary>9008 操作完成后询问是否重启。</summary>
        private async Task AskReboot9008Async(int port, CancellationToken ct)
        {
            if (await UiHelpers.ConfirmAsync(this.XamlRoot, "刷写完成", "是否重启设备？", "重启", "不重启"))
            {
                await EdlService.RebootDeviceAsync(port, AppConfig.UnlockDir, Log.Append, ct);
                Log.Append("如果设备没有自动进入任何模式，请长按电源键重新激活开机！");
                _q9008Port = null;
                PortStatusText.Text = "未连接";
            }
        }
    }
}
