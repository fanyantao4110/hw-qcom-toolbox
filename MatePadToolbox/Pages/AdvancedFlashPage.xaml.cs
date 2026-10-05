using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MatePadToolbox.Services;
using System.IO;

namespace MatePadToolbox.Pages
{
    /// <summary>
    /// 高级刷机页：Fastboot 模式刷入任意分区镜像；9008 模式读取设备分区表后按分区刷入。
    /// </summary>
    public sealed partial class AdvancedFlashPage : Page
    {
        // 9008 分区表条目
        public sealed record PartitionItem(string Name, int Lun, ulong StartSector, ulong SizeSectors, int SectorSize, string Display);

        private CancellationTokenSource? _cts;
        private int? _q9008Port;
        private string _q9008Memory = "ufs";
        private System.Collections.Generic.Dictionary<string, EdlService.PartitionSlot>? _partitionMap;
        private PartitionItem? _selectedPartition;

        public AdvancedFlashPage()
        {
            this.InitializeComponent();
        }

        // ==================== Fastboot ====================

        private void SetBusy(bool busy)
        {
            BtnFlash.IsEnabled = !busy;
            BtnCancel.IsEnabled = busy;
            PartitionNameBox.IsEnabled = !busy;
            MirrorPathBox.IsEnabled = !busy;
            BtnGetPartitionTable.IsEnabled = !busy;
            Btn9008Flash.IsEnabled = !busy;
            Btn9008Reboot.IsEnabled = !busy;
            Btn9008Cancel.IsEnabled = busy;
            MirrorPathBox9008.IsEnabled = !busy;
            PartitionList.IsEnabled = !busy;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
        private void Cancel9008_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        private void BrowseImage_Click(object sender, RoutedEventArgs e) => PickImage(MirrorPathBox);
        private void BrowseImage9008_Click(object sender, RoutedEventArgs e) => PickImage(MirrorPathBox9008);

        private async void PickImage(TextBox target)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                picker.FileTypeFilter.Add(".img");
                if (App.MainWindow != null)
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                }
                var file = await picker.PickSingleFileAsync();
                if (file != null) target.Text = file.Path;
            }
            catch (Exception ex)
            {
                Log.Append($"[提示] 打开镜像选择器失败（{ex.Message}），可直接在输入框中填写路径。");
            }
        }

        private async void Flash_Click(object sender, RoutedEventArgs e)
        {
            var partition = (PartitionNameBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(partition))
            {
                Log.Append("[错误] 请先输入要刷入的分区名。");
                return;
            }
            if (partition.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                partition = partition[..^4];

            var mirror = (MirrorPathBox.Text ?? string.Empty).Trim().Trim('"');
            if (string.IsNullOrEmpty(mirror))
            {
                Log.Append("[错误] 请先选择要刷入的镜像文件。");
                return;
            }
            if (!File.Exists(mirror))
            {
                Log.Append($"[错误] 镜像文件不存在：{mirror}");
                return;
            }

            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                Log.Append("===== 高级刷机（Fastboot）=====");
                if (!await UiHelpers.PrepareFastbootAsync(this.XamlRoot, Log.Append, _cts.Token)) return;

                Log.Append($"[信息] 开始刷写分区 {partition}，镜像 {Path.GetFileName(mirror)}");
                var ok = await DeviceService.FlashPartitionAsync(partition, mirror, Log.Append, _cts.Token);
                if (!ok)
                {
                    Log.Append("[错误] 分区刷写失败！");
                    return;
                }

                Log.Append("===============================================");
                Log.Append($"  {partition} 分区刷写成功！");
                Log.Append("===============================================");

                if (await UiHelpers.ConfirmAsync(this.XamlRoot, "刷写完成", "是否重启设备？", "重启", "不重启"))
                {
                    await DeviceService.RebootAsync(Log.Append);
                }
            }
            catch (OperationCanceledException) { Log.Append("[已取消]"); }
            finally { SetBusy(false); }
        }

        // ==================== 9008 ====================

        /// <summary>获取分区表：连接 9008（自动上传引导编程器）→ 从设备读取分区表并展示。</summary>
        private async void GetPartitionTable_Click(object sender, RoutedEventArgs e)
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
                Log.Append("===== 获取分区表（9008）=====");
                Log.Append("[信息] 连接 9008 并上传引导编程器...");
                var port = await EdlService.ConnectAsync(rebootViaAdb.Value, Log.Append, _cts.Token, GlobalProcessorService.DevprgPath);
                if (port <= 0) return;
                _q9008Port = port;
                PortStatusText.Text = $"已连接：COM{port}";
                Log.Append($"[OK] 已连接 9008，端口 COM{port}");

                Log.Append("[信息] 正在从设备读取分区表...");
                var table = await EdlService.LoadPartitionTableAsync(port, Log.Append, _cts.Token);
                if (table == null)
                {
                    Log.Append("[错误] 读取设备分区表失败！");
                    return;
                }
                _q9008Memory = table.Value.MemType;
                _partitionMap = table.Value.Map;

                var items = _partitionMap
                    .OrderBy(kv => (kv.Value.Lun, kv.Value.StartSector))
                    .Select(kv =>
                    {
                        var s = kv.Value;
                        var sizeMb = (long)(s.SizeSectors * (ulong)s.SectorSize / 1024 / 1024);
                        return new PartitionItem(kv.Key, s.Lun, s.StartSector, s.SizeSectors, s.SectorSize,
                            $"{kv.Key,-16} LUN{s.Lun} 起始 {s.StartSector,10} 大小 {sizeMb} MB");
                    })
                    .ToList();
                PartitionList.ItemsSource = items;
                PartitionCountText.Text = $"已读取 {items.Count} 个分区。";
                _selectedPartition = null;
                SelectedPartitionText.Text = string.Empty;
                Log.Append($"[OK] 分区表读取完成，共 {items.Count} 个分区。请选择目标分区并选择镜像后开始刷入。");
            }
            catch (OperationCanceledException) { Log.Append("[已取消]"); }
            finally { SetBusy(false); }
        }

        private void PartitionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PartitionList.SelectedItem is PartitionItem item)
            {
                _selectedPartition = item;
                SelectedPartitionText.Text = $"已选择分区：{item.Name}（LUN{item.Lun}，起始扇区 {item.StartSector}）";
            }
            else
            {
                _selectedPartition = null;
                SelectedPartitionText.Text = string.Empty;
            }
        }

        private async void Flash9008_Click(object sender, RoutedEventArgs e)
        {
            if (!_q9008Port.HasValue || _partitionMap == null)
            {
                Log.Append("[错误] 尚未获取设备分区表，请先点击【获取分区表】。");
                return;
            }
            if (_selectedPartition == null)
            {
                Log.Append("[错误] 请先在分区表列表中选择目标分区。");
                return;
            }
            var mirror = (MirrorPathBox9008.Text ?? string.Empty).Trim().Trim('"');
            if (string.IsNullOrEmpty(mirror))
            {
                Log.Append("[错误] 请先选择要刷入的镜像文件。");
                return;
            }
            if (!File.Exists(mirror))
            {
                Log.Append($"[错误] 镜像文件不存在：{mirror}");
                return;
            }

            var part = _selectedPartition.Name;
            if (!await UiHelpers.ConfirmAsync(this.XamlRoot, "警告",
                $"即将向 {part} 分区刷入镜像 {Path.GetFileName(mirror)}。\n此操作会覆盖该分区的数据！\n\n是否继续？")) return;

            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                Log.Append($"===== 开始刷写 {part} 分区（9008）=====");
                if (!_partitionMap.TryGetValue(part, out var slot))
                {
                    Log.Append($"[错误] 分区表中未找到 {part} 分区。");
                    return;
                }

                var xml = EdlService.BuildWritePartitionXml(Path.GetFileName(mirror), slot);
                var dir = Path.GetDirectoryName(mirror) ?? AppConfig.SystemDir;
                var ok = await EdlService.SendXmlContentAsync(_q9008Port.Value, $"write_{part}.xml",
                    xml, dir, Log.Append, _cts.Token, _q9008Memory);
                if (!ok)
                {
                    Log.Append("[错误] 分区刷写失败！");
                    return;
                }

                Log.Append("===============================================");
                Log.Append($"  {part} 分区刷写成功！");
                Log.Append("===============================================");

                if (await UiHelpers.ConfirmAsync(this.XamlRoot, "刷写完成", "是否重启设备？", "重启", "不重启"))
                {
                    await EdlService.RebootDeviceAsync(_q9008Port.Value, AppConfig.UnlockDir, Log.Append, _cts.Token);
                    Log.Append("如果设备没有自动开机，请长按电源键重新激活。");
                    _q9008Port = null;
                    PortStatusText.Text = "未连接";
                }
            }
            catch (OperationCanceledException) { Log.Append("[已取消]"); }
            finally { SetBusy(false); }
        }

        private async void Reboot9008_Click(object sender, RoutedEventArgs e)
        {
            if (!_q9008Port.HasValue)
            {
                Log.Append("[错误] 尚未连接 9008 设备。");
                return;
            }
            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                await EdlService.RebootDeviceAsync(_q9008Port.Value, AppConfig.UnlockDir, Log.Append, _cts.Token);
                Log.Append("如果设备没有自动开机，请长按电源键重新激活。");
                _q9008Port = null;
                PortStatusText.Text = "未连接";
            }
            catch (OperationCanceledException) { Log.Append("[已取消]"); }
            finally { SetBusy(false); }
        }
    }
}
