using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MatePadToolbox.Models;
using MatePadToolbox.Services;
using Windows.ApplicationModel.DataTransfer;

namespace MatePadToolbox.Pages
{
    /// <summary>
    /// 下载第三方系统页。
    /// 新交互：选择机型后，底部系统列表直接列出该机型全部系统（不再按系统版本选择）。
    /// 配置文件统一为 system/system_config.json（按机型分组）。
    /// </summary>
    public sealed partial class DownloadRomPage : Page
    {
        private List<DeviceEntry> _devices = new();

        public DownloadRomPage()
        {
            this.InitializeComponent();
            this.Loaded += DownloadRomPage_Loaded;
        }

        private async void DownloadRomPage_Loaded(object sender, RoutedEventArgs e)
        {
            await RefreshAsync();
        }

        /// <summary>加载/刷新配置：填充机型下拉，并默认选中第一个机型。</summary>
        private async Task RefreshAsync()
        {
            DeviceCombo.IsEnabled = false;
            BtnUpdateJson.IsEnabled = false;
            try
            {
                _devices = await RomConfigService.LoadDeviceListAsync(Log.Append);

                DeviceCombo.ItemsSource = _devices;

                if (_devices.Count == 0)
                {
                    Log.Append("[错误] 没有找到任何机型/系统配置，请检查 system\\system_config.json，或点击【在线更新配置文件】。");
                    RomList.ItemsSource = null;
                    return;
                }

                DeviceCombo.SelectedIndex = 0; // 触发 SelectionChanged → 自动列出系统
                var names = string.Join("、", _devices.Select(d => d.Name));
                Log.Append($"[OK] 加载配置成功，共 {_devices.Count} 个机型：{names}");
            }
            finally
            {
                DeviceCombo.IsEnabled = true;
                BtnUpdateJson.IsEnabled = true;
            }
        }

        /// <summary>选定机型后，直接列出该机型的所有系统（不再选择系统版本）。</summary>
        private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DeviceCombo.SelectedItem is not DeviceEntry device)
            {
                RomList.ItemsSource = null;
                return;
            }

            var systems = RomConfigService.GetSystems(device);
            RomList.ItemsSource = systems;

            if (systems.Count == 0)
            {
                Log.Append($"[提示] 机型「{device.Name}」下没有系统列表，可检查 system\\system_config.json 内容。");
            }
            else
            {
                Log.Append($"当前机型「{device.Name}」共 {systems.Count} 个系统，请从下方列表中选择要下载的项。");
            }
        }

        private async void UpdateJson_Click(object sender, RoutedEventArgs e)
        {
            BtnUpdateJson.IsEnabled = false;
            try
            {
                await RomConfigService.UpdateConfigAsync(Log.Append);
                await RefreshAsync();
            }
            finally
            {
                BtnUpdateJson.IsEnabled = true;
            }
        }

        private void DownloadRom_Click(object sender, RoutedEventArgs e)
        {
            if (RomList.SelectedItem is not RomEntry rom)
            {
                Log.Append("[提示] 请先选择要下载的系统。");
                return;
            }
            if (string.IsNullOrWhiteSpace(rom.Url))
            {
                Log.Append("[错误] 该系统没有可用的下载链接。");
                return;
            }

            Log.Append($"已选择系统：{rom.Name}");
            Log.Append("下载链接已复制到剪贴板，将为您自动打开浏览器。如果未自动打开，请自行打开浏览器并粘贴网址。");
            CopyToClipboard(rom.Url);
            ProcessRunner.OpenUrl(rom.Url);
        }

        private void OpenSystemDir_Click(object sender, RoutedEventArgs e)
        {
            ProcessRunner.ExploreFolder(AppConfig.SystemDir);
        }

        private void OpenBaseDir_Click(object sender, RoutedEventArgs e)
        {
            ProcessRunner.ExploreFolder(AppConfig.BaseRomDir);
        }

        private void BaseUrl_Click(object sender, RoutedEventArgs e)
        {
            Log.Append("底包下载链接已复制到剪贴板，将为您自动打开。如果未自动打开，请自行打开浏览器并粘贴网址。");
            CopyToClipboard(AppConfig.BasePackageUrl);
            ProcessRunner.OpenUrl(AppConfig.BasePackageUrl);
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
