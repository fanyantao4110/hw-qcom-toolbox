using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MatePadToolbox.Services;
using System.IO;

namespace MatePadToolbox.Pages
{
    /// <summary>
    /// 解锁 BL 页。
    /// 对应 bat 的 :unlock_submenu / :unlock_os23 / :unlock_os4_engineering / :unlock_os4_downgrade。
    /// </summary>
    public sealed partial class UnlockBlPage : Page
    {
        private CancellationTokenSource? _cts;

        public UnlockBlPage()
        {
            this.InitializeComponent();
            OsVersionGroup.SelectionChanged += (_, _) => UpdateVisibility();
        }

        private void UpdateVisibility()
        {
            Os4OptionsPanel.Visibility = OsVersionGroup.SelectedIndex == 1
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void StartUnlock_Click(object sender, RoutedEventArgs e)
        {
            if (OsVersionGroup.SelectedIndex == 1 && Os4MethodGroup.SelectedIndex == 1)
            {
                ShowDowngradeHint();
                return;
            }

            // 确认弹窗
            var confirm = new ContentDialog
            {
                Title = "确认解锁",
                Content = "解锁 BL 可能会丢失数据，请确认已备份。\n前置条件：平板已开启 USB 调试并连接电脑。\n\n是否继续？",
                PrimaryButtonText = "继续",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            var rebootViaAdb = OsVersionGroup.SelectedIndex == 0;
            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                await UnlockAsync(rebootViaAdb, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log.Append("[已取消] 解锁流程已停止。");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task UnlockAsync(bool rebootViaAdb, CancellationToken ct)
        {
            // 0. 处理器型号检查：必须在侧边栏顶部选择型号，防止误操作刷错型号
            if (GlobalProcessorService.Current == null)
            {
                Log.Append("[错误] 请先在侧边栏顶部的【CPU型号】下拉框中选择处理器型号。");
                return;
            }

            // 1. 文件检查（对应 bat 的 exist 检查）：使用侧边栏全局选择的处理器型号
            if (!File.Exists(GlobalProcessorService.DevprgPath))
            {
                Log.Append($"[错误] 缺少底层文件 {GlobalProcessorService.DevprgPath}");
                return;
            }
            if (!File.Exists(GlobalProcessorService.AblUnlockPath))
            {
                Log.Append($"[错误] 缺少ABL解锁文件 {GlobalProcessorService.AblUnlockPath}");
                return;
            }
            Log.Append($"[OK] 文件检查通过（处理器：{GlobalProcessorService.DisplayName}）。");

            // 2. 进入 9008 模式
            if (rebootViaAdb)
            {
                Log.Append("检测 ADB 设备...");
                if (!await DeviceService.WaitForAdbAsync(Log.Append, ct)) return;
                Log.Append("[OK] 设备已连接。");
                await DeviceService.RebootToEdlAsync(Log.Append);
                Log.Append("等待设备进入 9008 模式...");
            }
            else
            {
                Log.Append("等待设备进入 9008 模式（请确保已通过工程线/短接进入）...");
            }

            var port = await ComPortDetector.WaitFor9008Async(15, 2000, Log.Append, ct);
            if (!port.HasValue)
            {
                Log.Append("[错误] 等待超时，未检测到 9008 设备。");
                if (!rebootViaAdb) Log.Append("请重新确认已通过探针/短接方式进入 9008。");
                return;
            }
            Log.Append($"[OK] 检测到 9008 设备，端口 COM{port.Value}");

            // 3. 上传编程器（使用全局型号引导文件）
            if (!await EdlService.UploadFirehoseAsync(port.Value, Log.Append, ct, GlobalProcessorService.DevprgPath)) return;

            // 4. 配置端口
            if (!await EdlService.ConfigurePortAsync(port.Value, AppConfig.UnlockDir, Log.Append, ct)) return;

            // 5. 读取设备分区表，动态定位 ABL 分区并写入解锁镜像
            //    替代内置写死骁龙865分区位置的 rawprogram0.xml（参考高通工具箱 write.bat :QCEDL 的按分区名查找逻辑），
            //    从而适配 ABL 分区所在 LUN 与扇区不同的其它机型。
            Log.Append("正在读取设备分区表以定位 ABL 分区...");
            var abl = await EdlService.FindAblPartitionAsync(port.Value, Log.Append, ct);
            if (abl == null)
            {
                Log.Append("[错误] 无法定位 ABL 分区，解锁中止。正在重启设备...");
                await EdlService.RebootDeviceAsync(port.Value, AppConfig.UnlockDir, Log.Append, ct);
                return;
            }

            Log.Append("正在解锁...");
            var ok = await EdlService.SendXmlContentAsync(
                port.Value, "rawprogram0.xml",
                EdlService.BuildWriteAblXml(
                    GlobalProcessorService.AblUnlockFileName,
                    abl.Lun, abl.StartSector, abl.SizeSectors, abl.SectorSize),
                AppConfig.UnlockDir, Log.Append, ct, abl.MemType);

            if (!ok)
            {
                Log.Append("[错误] 解锁失败，正在重启设备...");
                await EdlService.RebootDeviceAsync(port.Value, AppConfig.UnlockDir, Log.Append, ct);
                return;
            }

            Log.Append("[OK] 解锁成功，设备将自动重启。");
            await EdlService.RebootDeviceAsync(port.Value, AppConfig.UnlockDir, Log.Append, ct);
            Log.Append("==========================================================");
            Log.Append(" 解锁完成！如果设备没有自动进入任何模式，然后长按电源键重新激活开机！");
            Log.Append("==========================================================");
        }

        /// <summary>鸿蒙4 降级说明（对应 bat 的 :unlock_os4_downgrade）。</summary>
        private void ShowDowngradeHint()
        {
            Log.Append("==========================================================");
            Log.Append("  请先返回侧边栏选择【降级系统/回锁BL】进行降级操作！");
            Log.Append("  降级完成后，再回到解锁BL页面选择【鸿蒙2 - 鸿蒙3】方式解锁。");
            Log.Append("==========================================================");
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
        }

        private void SetBusy(bool busy)
        {
            BtnStart.IsEnabled = !busy;
            BtnCancel.IsEnabled = busy;
            OsVersionGroup.IsEnabled = !busy;
            Os4MethodGroup.IsEnabled = !busy;
        }
    }
}
