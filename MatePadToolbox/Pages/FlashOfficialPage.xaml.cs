using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MatePadToolbox.Models;
using MatePadToolbox.Services;
using System.IO;
using Windows.ApplicationModel.DataTransfer;

namespace MatePadToolbox.Pages
{
    /// <summary>
    /// 刷入官方鸿蒙系统（救砖）页。
    /// 对应 bat 的 :flash_official_zip。
    /// </summary>
    public sealed partial class FlashOfficialPage : Page
    {
        private CancellationTokenSource? _cts;
        private List<string> _appFiles = new();

        public FlashOfficialPage()
        {
            this.InitializeComponent();
            this.Loaded += (_, _) => RefreshAppList();
        }

        private void RefreshZipList_Click(object sender, RoutedEventArgs e) => RefreshAppList();

        private void RefreshAppList()
        {
            _appFiles = Directory.Exists(AppConfig.BaseRomDir)
                ? Directory.EnumerateFiles(AppConfig.BaseRomDir, "*.app").OrderBy(f => f).ToList()
                : new List<string>();

            ZipCombo.ItemsSource = _appFiles.Select(Path.GetFileName).ToList();
            if (_appFiles.Count > 0)
            {
                ZipCombo.SelectedIndex = 0;
            }
            else
            {
                Log.Append($"[提示] 未在 base 目录找到任何 UPDATE.APP（{AppConfig.BaseRomDir}）。");
            }
        }

        private void OpenOfficialUrl_Click(object sender, RoutedEventArgs e)
        {
            Log.Append("官方系统包下载链接已复制到剪贴板，将为您自动打开浏览器。如果未自动打开，请自行打开浏览器并粘贴网址。");
            try
            {
                var dp = new DataPackage();
                dp.SetText(AppConfig.OfficialRomShareUrl);
                dp.RequestedOperation = DataPackageOperation.Copy;
                Clipboard.SetContent(dp);
            }
            catch { /* 剪切板不可用时静默忽略 */ }
            ProcessRunner.OpenUrl(AppConfig.OfficialRomShareUrl);
        }

        private void OpenSystemDir_Click(object sender, RoutedEventArgs e)
        {
            ProcessRunner.ExploreFolder(AppConfig.BaseRomDir);
        }

        private async void FlashOfficial_Click(object sender, RoutedEventArgs e)
        {
            if (ZipCombo.SelectedIndex < 0 || ZipCombo.SelectedIndex >= _appFiles.Count)
            {
                Log.Append("[错误] 请先选择要刷入的 UPDATE.APP。");
                return;
            }
            var app = _appFiles[ZipCombo.SelectedIndex];

            if (!await UiHelpers.ConfirmAsync(this.XamlRoot, "救砖刷写",
                $"将解包并刷入：{Path.GetFileName(app)}\n此操作会覆盖全部分区（不刷 abl 与 oeminfo 分区）。\n\n是否继续？")) return;

            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                Log.Append($"已选择：{Path.GetFileName(app)}");

                AppConfig.ResetCacheDir();
                // 解包全部（合并 super 分片为 super_merged.img）
                var imgs = await UpdateAppService.ExtractAsync(app, AppConfig.CacheDir, Log.Append, _cts.Token,
                    mergeSuper: true);
                if (imgs == null) return;

                // 官方系统不刷 abl 分区；super 采用合并产物（super_merged.img），若未合并则保留唯一 super 分片
                var targets = BuildOfficialTargets(imgs, Log.Append);

                Log.Append("准备进入 Fastboot 模式...");
                if (!await UiHelpers.PrepareFastbootAsync(this.XamlRoot, Log.Append, _cts.Token)) return;

                Log.Append("设备已连接，开始递归刷入所有镜像...");
                FlashSummary summary = await DeviceService.FlashAllImagesAsync(
                    AppConfig.CacheDir, recursive: true, Log.Append, _cts.Token,
                    partitionFilter: targets.Select(f => Path.GetFileName(f)!).ToList(),
                    partitionNameOverrides: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [AppConfig.MergedSuperFileName] = "super",
                    });

                AppConfig.ResetCacheDir();

                if (summary.AllSuccess)
                {
                    Log.Append("===============================================");
                    Log.Append("  官方系统所有分区刷入成功！");
                    Log.Append("===============================================");
                }
                else
                {
                    Log.Append("===============================================");
                    Log.Append($"  刷写完成，但有 {summary.FailedParts.Count} 个分区失败：");
                    Log.Append($"  {string.Join(" ", summary.FailedParts)}");
                    Log.Append("===============================================");
                }

                // 救砖刷写完成后必须询问是否恢复出厂：跨版本/跨机型的官方包若带着旧 data 分区
                // 残留，很可能卡在开机动画无法进系统。选择"是"会先写 misc 分区再重启。
                var doFactoryReset = await UiHelpers.ConfirmAsync(this.XamlRoot,
                    "是否恢复出厂设置？",
                    "官方系统已刷入完成。\n\n" +
                    "强烈建议【恢复出厂】：旧的用户数据（data 分区）残留与新系统不匹配时，\n" +
                    "设备很可能卡在开机动画、反复重启甚至无法开机。\n\n" +
                    "· 选择【恢复出厂】：写入 misc 分区后重启，设备会自动进入 Recovery 清除数据。\n" +
                    "· 选择【直接重启】：跳过清除数据，直接重启（若无法开机，请再回到本工具执行恢复出厂）。\n\n" +
                    "此操作会清除设备上的全部用户数据，请先确认已备份。",
                    primaryText: "恢复出厂",
                    closeText: "直接重启");

                if (doFactoryReset)
                {
                    Log.Append("已选择【恢复出厂】，正在写入 misc 分区...");
                    var resetOk = await FactoryResetService.RunAsync(
                        fromSystem: false, log: Log.Append, ct: _cts.Token);
                    if (!resetOk)
                    {
                        Log.Append("[错误] 恢复出厂失败，将改为直接重启设备。");
                        await DeviceService.RebootAsync(Log.Append, _cts.Token);
                    }
                }
                else
                {
                    Log.Append("已选择【直接重启】，跳过恢复出厂。");
                    await DeviceService.RebootAsync(Log.Append, _cts.Token);
                    Log.Append("操作完成，设备正在重启！");
                }
            }
            catch (OperationCanceledException)
            {
                Log.Append("[已取消]");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        /// <summary>
        /// 组装官方系统刷入目标：排除全部 abl 分区；若存在合并产物 super_merged.img 则用其替代所有 super 分片，
        /// 否则保留唯一的 super 分片（无多分片时不需要合并）。
        /// </summary>
        private List<string> BuildOfficialTargets(IEnumerable<string> imgs, Action<string> log)
        {
            var merged = imgs.FirstOrDefault(f => string.Equals(Path.GetFileName(f), AppConfig.MergedSuperFileName, StringComparison.OrdinalIgnoreCase));
            var targets = new List<string>();
            var superCount = 0;

            foreach (var img in imgs.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(img);
                if (UpdateAppService.IsSkippedImage(img))
                {
                    log($"[跳过] 分区：{name}");
                    continue;
                }
                if (UpdateAppService.IsAblImage(img))
                {
                    log($"[跳过] abl 分区：{name}");
                    continue;
                }
                if (merged != null && UpdateAppService.IsSuperImage(img))
                {
                    // 有多分片且已合并：跳过原始 super 分片，稍后加入合并产物
                    log($"[跳过] super 分片：{name}（将由合并产物替代）");
                    continue;
                }
                if (UpdateAppService.IsSuperImage(img)) superCount++;
                targets.Add(img);
            }

            if (merged != null)
            {
                log($"[使用] 合并后的 super 镜像：{Path.GetFileName(merged)}");
                targets.Add(merged);
            }
            else if (superCount > 1)
            {
                log("[警告] 检测到多个 super 分片但未生成合并产物，请确认固件兼容性。");
            }
            return targets;
        }

        private void SetBusy(bool busy)
        {
            BtnFlash.IsEnabled = !busy;
            BtnCancel.IsEnabled = busy;
            ZipCombo.IsEnabled = !busy;
        }
    }
}
