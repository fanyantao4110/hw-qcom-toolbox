using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MatePadToolbox.Models;
using MatePadToolbox.Services;
using System.IO;

namespace MatePadToolbox.Pages
{
    /// <summary>
    /// 高级工具箱页。
    /// 提供进阶操作：连接9008并发送引导（仅下发 firehose 引导，不写入 ABL/解锁镜像）。
    /// 处理器型号在侧边栏顶部全局选择（数据来自 unlock\processors.json），此处直接使用全局选中型号。
    /// </summary>
    public sealed partial class AdvancedToolboxPage : Page
    {
        private CancellationTokenSource? _cts;

        public AdvancedToolboxPage()
        {
            this.InitializeComponent();
            this.Loaded += (_, _) => UpdateHint();
        }

        /// <summary>显示当前全局选中处理器的引导文件与ABL状态。</summary>
        private void UpdateHint()
        {
            var proc = GlobalProcessorService.Current;
            if (proc == null)
            {
                ProcHintText.Text = "请先在侧边栏顶部的【CPU型号】下拉框中选择处理器型号。";
                return;
            }
            var (ok, missing, _) = AdvancedToolService.ValidateFiles(proc);
            var parts = new List<string>();
            parts.Add($"当前处理器：{proc}");
            parts.Add($"引导文件：{Path.GetFileName(GlobalProcessorService.DevprgPath)}");
            parts.Add($"ABL解锁镜像：{GlobalProcessorService.AblUnlockFileName}");
            parts.Add(ok ? "✔ 文件齐全（位于 unlock 目录）" : $"✘ 缺失（{string.Join("、", missing)}），请放入 unlock 目录");
            ProcHintText.Text = string.Join("  ", parts);
        }

        private async void SendBoot_Click(object sender, RoutedEventArgs e)
        {
            var proc = GlobalProcessorService.Current;
            if (proc == null)
            {
                Log.Append("[错误] 请先在侧边栏顶部的【CPU型号】下拉框中选择处理器型号。");
                return;
            }

            // 对话框：选择系统版本 + 确认
            var osGroup = new RadioButtons { Header = "选择当前系统版本" };
            osGroup.Items.Add("鸿蒙2 - 鸿蒙3（ADB 自动进入 9008）");
            osGroup.Items.Add("鸿蒙4（需工程线/短接进入 9008，或先降级）");
            osGroup.SelectedIndex = 0;

            var panel = new StackPanel { Spacing = 10 };
            var hint = new TextBlock
            {
                Text = $"将执行：进入 9008 → 发送引导（{Path.GetFileName(GlobalProcessorService.DevprgPath)}）→ 配置端口。不写入任何镜像。",
                FontSize = 12,
                Opacity = 0.8,
                TextWrapping = TextWrapping.Wrap,
            };
            panel.Children.Add(new TextBlock
            {
                Text = $"目标处理器：{proc}",
                FontWeight = Microsoft.UI.Text.FontWeights.Bold
            });
            panel.Children.Add(osGroup);
            panel.Children.Add(hint);

            var dlg = new ContentDialog
            {
                Title = "连接9008并发送引导",
                Content = panel,
                PrimaryButtonText = "开始",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };

            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

            var rebootViaAdb = osGroup.SelectedIndex == 0;
            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                var ok = await AdvancedToolService.SendBootAsync(proc, rebootViaAdb, Log.Append, _cts.Token);
                if (ok)
                {
                    Log.Append("==========================================================");
                    Log.Append($" 「{proc.Name}」引导发送完成！设备停留在 9008 模式，可继续后续操作。");
                    Log.Append("==========================================================");
                }
            }
            catch (OperationCanceledException)
            {
                Log.Append("[已取消] 操作已停止。");
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

        private async void RebootFastboot_Click(object sender, RoutedEventArgs e)
        {
            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                if (!await DeviceService.WaitForAdbAsync(Log.Append, _cts.Token)) return;
                await DeviceService.RebootToBootloaderAsync(Log.Append);
            }
            catch (OperationCanceledException)
            {
                Log.Append("[已取消] 操作已停止。");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void RebootEdl_Click(object sender, RoutedEventArgs e)
        {
            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                if (!await DeviceService.WaitForAdbAsync(Log.Append, _cts.Token)) return;
                await DeviceService.RebootToEdlAsync(Log.Append);
                Log.Append("[OK] 已发送进入 9008 指令，设备将重启进入 9008 模式。");
            }
            catch (OperationCanceledException)
            {
                Log.Append("[已取消] 操作已停止。");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void RebootDevice_Click(object sender, RoutedEventArgs e)
        {
            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                await DeviceService.RebootAsync(Log.Append);
            }
            catch (OperationCanceledException)
            {
                Log.Append("[已取消] 操作已停止。");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void OpenUnlockDir_Click(object sender, RoutedEventArgs e)
        {
            ProcessRunner.ExploreFolder(AppConfig.UnlockDir);
        }

        /// <summary>
        /// 恢复出厂设置：先询问设备当前所处模式（系统 / Fastboot），
        /// 再把「恢复出厂」指令写入 misc 分区并重启，设备会自动进入 Recovery 清除数据。
        /// </summary>
        private async void FactoryReset_Click(object sender, RoutedEventArgs e)
        {
            var ask = new ContentDialog
            {
                Title = "恢复出厂设置",
                Content = "此操作会清除设备上的全部用户数据（等同恢复出厂），且不可撤销！\n\n" +
                          "请选择设备当前所处的模式：\n\n" +
                          "· 【系统模式】平板正常开机并已开启 USB 调试，程序会先重启进入 Fastboot 再写入 misc。\n" +
                          "· 【Fastboot模式】平板已手动进入 Fastboot，直接写入 misc 分区。\n\n" +
                          "写入完成后设备会自动重启并进入 Recovery 清除数据，请勿断开数据线。",
                PrimaryButtonText = "系统模式",
                SecondaryButtonText = "Fastboot模式",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };

            bool fromSystem;
            var r = await ask.ShowAsync();
            if (r == ContentDialogResult.Primary) fromSystem = true;
            else if (r == ContentDialogResult.Secondary) fromSystem = false;
            else { Log.Append("[已取消] 未执行恢复出厂。"); return; }

            if (!await UiHelpers.ConfirmAsync(this.XamlRoot, "再次确认",
                    "确认要清除设备上的全部数据吗？此操作不可撤销。",
                    primaryText: "确认清除", closeText: "取消")) return;

            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                Log.Append("===== 恢复出厂设置 =====");
                Log.Append(fromSystem
                    ? "已选择【系统模式】，将先重启进入 Fastboot 模式。"
                    : "已选择【Fastboot模式】，直接写入 misc 分区。");

                var ok = await FactoryResetService.RunAsync(fromSystem, Log.Append, _cts.Token);
                if (ok) Log.Append("[OK] 恢复出厂指令已下发完成。");
                else Log.Append("[错误] 恢复出厂未完成，请查看上方日志。");
            }
            catch (OperationCanceledException)
            {
                Log.Append("[已取消] 恢复出厂已停止。");
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

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
        }

        private void SetBusy(bool busy)
        {
            BtnSendBoot.IsEnabled = !busy;
            BtnCancel.IsEnabled = busy;
            BtnFactoryReset.IsEnabled = !busy;
        }
    }
}
