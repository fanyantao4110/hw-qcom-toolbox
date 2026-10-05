using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MatePadToolbox.Models;
using MatePadToolbox.Services;
using Windows.ApplicationModel.DataTransfer;

namespace MatePadToolbox.Pages
{
    /// <summary>
    /// 优化模块页：
    /// 从 json/optimize_modules.json 读取模块列表并平铺展示；点击模块弹出操作面板。
    /// 模块支持两种下载方式（json 中 type/mode 字段）：
    ///   - direct（直链）：程序直接下载 zip 到 modules 目录；
    ///   - netdisk（跳转网盘）：复制下载链接到剪贴板并自动打开浏览器，用户自行下载后放入 modules 目录。
    /// 一键刷入通过 ADB 安装到 Magisk 或 APatch。
    /// </summary>
    public sealed partial class OptimizeModulesPage : Page
    {
        private List<ModuleEntry> _modules = new();
        private CancellationTokenSource? _cts;

        public OptimizeModulesPage()
        {
            this.InitializeComponent();
            this.Loaded += async (_, _) => await RefreshModulesAsync();
        }

        /// <summary>忙碌状态：禁用操作按钮并启用【取消】。</summary>
        private void SetBusy(bool busy)
        {
            BtnRefresh.IsEnabled = !busy;
            BtnCancel.IsEnabled = busy;
            ModuleGrid.IsEnabled = !busy;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Log.Append("[提示] 正在取消，请稍候...");
            _cts?.Cancel();
        }

        private async Task RefreshModulesAsync()
        {
            BtnRefresh.IsEnabled = false;
            try
            {
                _modules = await ModuleConfigService.LoadModulesAsync(Log.Append);
                ModuleGrid.ItemsSource = _modules;
                ModuleCountText.Text = _modules.Count == 0
                    ? "未找到任何模块，可点击左侧按钮在线更新。"
                    : $"共 {_modules.Count} 个模块，点击任意模块进行操作。";
            }
            finally
            {
                BtnRefresh.IsEnabled = true;
            }
        }

        private async void RefreshModules_Click(object sender, RoutedEventArgs e)
        {
            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                await ModuleConfigService.DownloadConfigAsync(Log.Append, _cts.Token);
                await RefreshModulesAsync();
            }
            catch (OperationCanceledException)
            {
                Log.Append("[已取消] 模块列表更新已停止。");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void ModuleItem_Click(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not ModuleEntry entry) return;
            await ShowModuleDialogAsync(entry);
        }

        /// <summary>弹出模块操作面板：直链支持下载/一键刷入；网盘支持复制链接+一键刷入。</summary>
        private async Task ShowModuleDialogAsync(ModuleEntry entry)
        {
            var title = new TextBlock
            {
                Text = entry.Name,
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
            };

            var desc = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(entry.Description) ? "(无说明)" : entry.Description,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8,
            };

            var fileText = new TextBlock
            {
                Text = $"文件名：{entry.File}",
                Opacity = 0.6,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            };

            var downloaded = ModuleFlashService.IsModuleZipPresent(entry);
            var statusText = new TextBlock
            {
                Text = downloaded ? "✔ 模块文件已下载到 modules 目录" : "模块文件尚未下载（一键刷入前需先下载）",
                Foreground = downloaded
                    ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.SeaGreen)
                    : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            };

            bool isNetdisk = entry.IsNetdisk;
            var modeText = new TextBlock
            {
                Text = isNetdisk
                    ? "下载方式：跳转网盘。点击【复制下载链接】会把链接复制到剪贴板并自动打开浏览器；请在浏览器中下载 zip 后放入 modules 目录（文件名需与上方一致），再点【一键刷入】。"
                    : "下载方式：直链。点击【下载】直接下载 zip 文件到 modules 目录。",
                FontSize = 12,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
            };

            var targetGroup = new RadioButtons
            {
                Header = "安装目标",
            };
            targetGroup.Items.Add("Magisk");
            targetGroup.Items.Add("APatch");
            targetGroup.SelectedIndex = 0;

            var btnFlash = new Button { Content = "一键刷入", MinWidth = 140 };
            var btnDownload = new Button { Content = "下载模块", MinWidth = 140 };
            var btnCopy = new Button { Content = "复制下载链接", MinWidth = 150, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };

            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(title);
            panel.Children.Add(fileText);
            panel.Children.Add(statusText);
            panel.Children.Add(modeText);
            panel.Children.Add(desc);
            panel.Children.Add(targetGroup);

            var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (isNetdisk)
            {
                buttonRow.Children.Add(btnCopy);
                buttonRow.Children.Add(btnFlash);
            }
            else
            {
                btnDownload.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
                buttonRow.Children.Add(btnDownload);
                buttonRow.Children.Add(btnFlash);
            }
            panel.Children.Add(buttonRow);

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = entry.Name,
                Content = panel,
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close,
            };

            btnFlash.Click += async (_, _) =>
            {
                dialog.Hide();
                var target = (ModuleFlashService.InstallTarget)targetGroup.SelectedIndex;
                _cts = new CancellationTokenSource();
                SetBusy(true);
                try
                {
                    await FlashModuleWithConfirmAsync(entry, target);
                }
                catch (OperationCanceledException)
                {
                    Log.Append("[已取消] 模块刷入已停止。");
                }
                finally
                {
                    SetBusy(false);
                }
            };

            btnDownload.Click += async (_, _) =>
            {
                dialog.Hide();
                _cts = new CancellationTokenSource();
                SetBusy(true);
                try
                {
                    await ModuleConfigService.DownloadModuleAsync(entry, Log.Append, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    Log.Append("[已取消] 模块下载已停止。");
                }
                finally
                {
                    SetBusy(false);
                }
            };

            btnCopy.Click += (_, _) =>
            {
                dialog.Hide();
                if (string.IsNullOrWhiteSpace(entry.Url))
                {
                    Log.Append("[错误] 该模块没有可用的下载链接。");
                    return;
                }
                CopyToClipboard(entry.Url);
                ProcessRunner.OpenUrl(entry.Url);
                Log.Append($"已选择模块：{entry.Name}");
                Log.Append("下载链接已复制到剪贴板，将为您自动打开浏览器。如果未自动打开，请自行打开浏览器并粘贴网址。下载完成后将 zip 放入 modules 目录（文件名与上方「文件名」一致），再点【一键刷入】。");
            };

            await dialog.ShowAsync();
        }

        /// <summary>一键刷入：前置条件确认 → 校验本地文件 → 执行 ADB 安装。</summary>
        private async Task FlashModuleWithConfirmAsync(ModuleEntry entry, ModuleFlashService.InstallTarget target)
        {
            var targetName = target == ModuleFlashService.InstallTarget.Magisk ? "Magisk" : "APatch";

            var confirm = new ContentDialog
            {
                Title = $"一键刷入到 {targetName}",
                Content = $"将安装模块「{entry.Name}」到 {targetName}。\n\n" +
                          "请先确认以下前置条件：\n" +
                          "  1. 平板已开启「USB 调试」并连接电脑\n" +
                          "  2. 已安装正确的 ADB 驱动\n" +
                          "  3. 已关闭 Shamiko（或其他反检测）模块\n" +
                          "  4. 设备已获取 Root，且已安装并授权 " + targetName + "\n\n是否继续？",
                PrimaryButtonText = "继续",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            var ok = await ModuleFlashService.FlashAsync(entry, target, Log.Append, _cts?.Token ?? default);
            if (ok)
            {
                Log.Append("==========================================================");
                Log.Append($" 模块「{entry.Name}」已安装到 {targetName}，建议重启设备后生效。");
                Log.Append("==========================================================");
            }
        }

        private static void CopyToClipboard(string url)
        {
            try
            {
                var dp = new DataPackage();
                dp.SetText(url);
                dp.RequestedOperation = DataPackageOperation.Copy;
                Clipboard.SetContent(dp);
            }
            catch
            {
                // 剪切板不可用时静默忽略
            }
        }
    }
}
