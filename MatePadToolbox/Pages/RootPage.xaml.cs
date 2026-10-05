using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MatePadToolbox.Dialogs;
using MatePadToolbox.Services;
using System.IO;

namespace MatePadToolbox.Pages
{
    /// <summary>
    /// Root 页：Magisk 方式（提取/修补/刷入 ramdisk）与 APatch 方式（提取/修补/刷入 boot）。
    /// 两种方式均为 9008 流程，分区位置按设备分区表动态定位。
    /// </summary>
    public sealed partial class RootPage : Page
    {
        private CancellationTokenSource? _cts;

        public RootPage()
        {
            this.InitializeComponent();
        }

        // ==================== Magisk ====================

        private async void MagiskOneClick_Click(object sender, RoutedEventArgs e)
        {
            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                var rebootViaAdb = MagiskOsGroup.SelectedIndex == 0;
                Log.Append("===== 步骤 1/3：提取 ramdisk 分区 =====");
                if (!await MagiskService.ExtractRamdiskAsync(rebootViaAdb, Log.Append, _cts.Token)) return;

                Log.Append("===== 步骤 2/3：Magisk 修补 ramdisk =====");
                if (!await MagiskService.PatchRamdiskAsync(Log.Append, _cts.Token))
                {
                    // 提取后设备停留在 9008，此处失败需明确告知如何收尾
                    Log.Append("[提示] 修补未完成，设备仍保持在 9008 模式（未重启）。");
                    Log.Append("       可重新点击【2. 修补 ramdisk】重试，成功后再点【3. 刷入 ramdisk】；");
                    Log.Append("       若不再继续，请长按电源键手动重启设备。");
                    return;
                }

                Log.Append("===== 步骤 3/3：刷入修补后的 ramdisk =====");
                if (!await MagiskService.FlashPatchedRamdiskAsync(rebootViaAdb, Log.Append, _cts.Token)) return;
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

        private async void MagiskExtract_Click(object sender, RoutedEventArgs e)
        {
            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                var rebootViaAdb = MagiskOsGroup.SelectedIndex == 0;
                await MagiskService.ExtractRamdiskAsync(rebootViaAdb, Log.Append, _cts.Token);
            }
            catch (OperationCanceledException) { Log.Append("[已取消]"); }
            finally { SetBusy(false); }
        }

        private async void MagiskPatch_Click(object sender, RoutedEventArgs e)
        {
            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                await MagiskService.PatchRamdiskAsync(Log.Append, _cts.Token);
            }
            catch (OperationCanceledException) { Log.Append("[已取消]"); }
            finally { SetBusy(false); }
        }

        private async void MagiskFlash_Click(object sender, RoutedEventArgs e)
        {
            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                var rebootViaAdb = MagiskOsGroup.SelectedIndex == 0;
                await MagiskService.FlashPatchedRamdiskAsync(rebootViaAdb, Log.Append, _cts.Token);
            }
            catch (OperationCanceledException) { Log.Append("[已取消]"); }
            finally { SetBusy(false); }
        }

        private void MagiskCancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        private async void InstallMagisk_Click(object sender, RoutedEventArgs e)
        {
            await InstallApkAsync(AppConfig.MagiskApk, "Magisk");
        }

        private async void MagiskUnroot_Click(object sender, RoutedEventArgs e)
        {
            await UnrootAsync();
        }

        // ==================== APatch ====================

        private async void ApatchOneClick_Click(object sender, RoutedEventArgs e)
        {
            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                var rebootViaAdb = ApachOsGroup.SelectedIndex == 0;
                Log.Append("===== 步骤 1/3：提取 boot 分区 =====");
                if (!await ApachService.ExtractBootAsync(rebootViaAdb, Log.Append, _cts.Token)) return;

                Log.Append("===== 步骤 2/3：APatch 修补 boot =====");
                if (!await ApachService.PatchBootAsync(Log.Append, _cts.Token))
                {
                    // 提取后设备停留在 9008，此处失败需明确告知如何收尾
                    Log.Append("[提示] 修补未完成，设备仍保持在 9008 模式（未重启）。");
                    Log.Append("       可重新点击【2. 修补 boot】重试，成功后再点【3. 刷入 boot】；");
                    Log.Append("       若不再继续，请长按电源键手动重启设备。");
                    return;
                }

                Log.Append("===== 步骤 3/3：刷入修补后的 boot =====");
                if (!await ApachService.FlashPatchedBootAsync(rebootViaAdb, Log.Append, _cts.Token)) return;
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

        private async void ApatchExtract_Click(object sender, RoutedEventArgs e)
        {
            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                var rebootViaAdb = ApachOsGroup.SelectedIndex == 0;
                await ApachService.ExtractBootAsync(rebootViaAdb, Log.Append, _cts.Token);
            }
            catch (OperationCanceledException) { Log.Append("[已取消]"); }
            finally { SetBusy(false); }
        }

        private async void ApatchPatch_Click(object sender, RoutedEventArgs e)
        {
            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                await ApachService.PatchBootAsync(Log.Append, _cts.Token);
            }
            catch (OperationCanceledException) { Log.Append("[已取消]"); }
            finally { SetBusy(false); }
        }

        private async void ApatchFlash_Click(object sender, RoutedEventArgs e)
        {
            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                var rebootViaAdb = ApachOsGroup.SelectedIndex == 0;
                await ApachService.FlashPatchedBootAsync(rebootViaAdb, Log.Append, _cts.Token);
            }
            catch (OperationCanceledException) { Log.Append("[已取消]"); }
            finally { SetBusy(false); }
        }

        private void ApatchCancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        /// <summary>
        /// 一键隐藏 Root 环境（仅 Magisk）：隐藏应用列表 + 配套 APK + 隐藏模块 + Shamiko。
        /// 与 APatch 版的区别：不装 Zygisk Next（Magisk 自带 Zygisk），改装 Shamiko，
        /// 并自动启用内置 Zygisk、建立 Shamiko 白名单文件（/data/adb/shamiko/whitelist）。
        /// 资源全部取自本地 modules 目录。
        /// </summary>
        private async void MagiskHideEnv_Click(object sender, RoutedEventArgs e)
        {
            if (!await UiHelpers.ConfirmAsync(this.XamlRoot, "一键隐藏 Root 环境",
                "将执行以下操作（仅适用于 Magisk）：\n\n" +
                "  [1/3] 安装隐藏应用列表（HMA）并导入配置\n" +
                "  [2/3] 安装配套 APK：密钥认证、Luna\n" +
                "  [3/3] 通过 magisk --install-module 刷入隐藏模块：\n" +
                "        TEESimulator-RS、紫罗兰辅助、LSPosed v2.2.0-7854、Shamiko\n" +
                "  另：自动启用内置 Zygisk，并建立 Shamiko 白名单文件\n" +
                "        （/data/adb/shamiko/whitelist，文件模式）\n\n" +
                "说明：特意不安装 Zygisk Next（Magisk 自带 Zygisk，装了会冲突）。\n\n" +
                "前置条件：\n" +
                "  · 设备已 Root 且已授予管理器 Root 权限\n" +
                "  · 已在开发者选项中关闭「监控 ADB 安装应用」\n\n" +
                "完成后需重启设备生效。是否继续？",
                primaryText: "开始隐藏", closeText: "取消")) return;

            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                await HideEnvService.RunMagiskAsync(Log.Append, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log.Append("[已取消] 隐藏环境已停止。");
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
        /// 一键隐藏 Root 环境（仅 APatch）：隐藏应用列表 + 配套 APK + 隐藏模块。
        /// 资源全部取自本地 modules 目录。
        /// </summary>
        private async void ApatchHideEnv_Click(object sender, RoutedEventArgs e)
        {
            if (!await UiHelpers.ConfirmAsync(this.XamlRoot, "一键隐藏 Root 环境",
                "将执行以下操作（仅适用于 APatch）：\n\n" +
                "  [1/3] 安装隐藏应用列表（HMA）并导入配置\n" +
                "  [2/3] 安装配套 APK：密钥认证、Luna\n" +
                "  [3/3] 通过 apd 刷入隐藏模块：\n" +
                "        TEESimulator-RS、Zygisk Next、紫罗兰辅助、LSPosed v2.2.0-7854\n\n" +
                "前置条件：\n" +
                "  · 设备已 Root 且已授予管理器 Root 权限\n" +
                "  · 已在开发者选项中关闭「监控 ADB 安装应用」\n\n" +
                "完成后需重启设备生效。是否继续？",
                primaryText: "开始隐藏", closeText: "取消")) return;

            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                await HideEnvService.RunAsync(Log.Append, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log.Append("[已取消] 隐藏环境已停止。");
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

        private async void InstallApatch_Click(object sender, RoutedEventArgs e)
        {
            await InstallApkAsync(AppConfig.ApatchApk, "APatch");
        }

        private async void ApatchUnroot_Click(object sender, RoutedEventArgs e)
        {
            await UnrootAsync();
        }

        /// <summary>
        /// 取消 Root：弹窗选择底包与设备模式 → 从底包提取原厂 boot/ramdisk → Fastboot 刷入并重启。
        /// </summary>
        private async Task UnrootAsync()
        {
            var dlg = new UnrootDialog { XamlRoot = this.XamlRoot };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

            var app = dlg.SelectedApp;
            if (string.IsNullOrEmpty(app)) return;

            if (!await UiHelpers.ConfirmAsync(this.XamlRoot, "取消 Root",
                $"将使用底包 {Path.GetFileName(app)} 中的原厂 boot 与 ramdisk 覆盖当前分区，移除 Root。\n\n是否继续？"))
                return;

            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                await UnrootService.UnrootAsync(app, dlg.IsSystemMode, Log.Append, _cts.Token);
            }
            catch (OperationCanceledException) { Log.Append("[已取消]"); }
            finally { SetBusy(false); }
        }

        private async Task InstallApkAsync(string apkPath, string name)
        {
            if (!File.Exists(apkPath))
            {
                Log.Append($"[错误] 未找到 {name} 安装包：{apkPath}");
                return;
            }

            Log.Append($"正在通过 ADB 安装 {name} 管理器...");
            SetBusy(true);
            try
            {
                var exitCode = await ProcessRunner.RunAsync(
                    AppConfig.AdbExe,
                    $"install -r \"{apkPath}\"",
                    Log.Append,
                    ct: CancellationToken.None);
                if (exitCode == 0)
                    Log.Append($"[OK] {name} 管理器安装成功！");
                else
                    Log.Append($"[错误] {name} 管理器安装失败，退出码：{exitCode}");
            }
            catch (Exception ex)
            {
                Log.Append($"[错误] 安装 {name} 失败：{ex.Message}");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void SetBusy(bool busy)
        {
            BtnMagiskOneClick.IsEnabled = !busy;
            BtnMagiskExtract.IsEnabled = !busy;
            BtnMagiskPatch.IsEnabled = !busy;
            BtnMagiskFlash.IsEnabled = !busy;
            BtnMagiskCancel.IsEnabled = busy;
            BtnInstallMagisk.IsEnabled = !busy;
            BtnMagiskUnroot.IsEnabled = !busy;
            BtnMagiskHideEnv.IsEnabled = !busy;
            MagiskOsGroup.IsEnabled = !busy;

            BtnApatchOneClick.IsEnabled = !busy;
            BtnApatchExtract.IsEnabled = !busy;
            BtnApatchPatch.IsEnabled = !busy;
            BtnApatchFlash.IsEnabled = !busy;
            BtnApatchCancel.IsEnabled = busy;
            BtnInstallApatch.IsEnabled = !busy;
            BtnApatchUnroot.IsEnabled = !busy;
            BtnApatchHideEnv.IsEnabled = !busy;
            ApachOsGroup.IsEnabled = !busy;
        }
    }
}
