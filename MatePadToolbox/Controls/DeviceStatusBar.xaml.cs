using System;
using System.Threading;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Dispatching;
using MatePadToolbox.Services;

namespace MatePadToolbox.Controls
{
    /// <summary>
    /// 顶部设备状态栏：实时显示设备当前处于 ADB / Fastboot / EDL(9008) / 未连接，
    /// 并在 ADB 模式下展示机型、系统版本、BL 锁状态、电量与存储。
    /// 轮询间隔自适应：无设备 1 秒，有设备 5 秒（与工具箱 v3.1 一致）。
    /// </summary>
    public sealed partial class DeviceStatusBar : UserControl
    {
        private readonly DispatcherQueueTimer _timer;
        private bool _refreshing;

        public DeviceStatusBar()
        {
            this.InitializeComponent();

            _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += Timer_Tick;
            _timer.Start();

            this.Unloaded += (_, _) => _timer.Stop();

            // 首次立即探测一次
            _ = RefreshAsync();
        }

        private async void Timer_Tick(object sender, object e)
        {
            await RefreshAsync();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

        /// <summary>探测并刷新状态显示。重入安全。</summary>
        private async System.Threading.Tasks.Task RefreshAsync()
        {
            if (_refreshing) return;
            _refreshing = true;
            try
            {
                var status = await DeviceStatusService.DetectAsync(CancellationToken.None);
                ApplyStatus(status);

                // 自适应轮询间隔：有设备时降低频率，减少对正在执行的刷机操作的干扰
                _timer.Interval = TimeSpan.FromMilliseconds(status.HasDevice ? 5000 : 1000);
            }
            catch
            {
                // 探测异常（如 adb 尚未就绪）时忽略，下个周期重试
            }
            finally
            {
                _refreshing = false;
            }
        }

        private void ApplyStatus(DeviceStatus status)
        {
            ModeText.Text = status.ModeText;
            StatusDot.Fill = new SolidColorBrush(status.Mode switch
            {
                DeviceConnectionMode.Adb => Colors.SeaGreen,
                DeviceConnectionMode.Fastboot => Colors.Goldenrod,
                DeviceConnectionMode.Edl => Colors.OrangeRed,
                DeviceConnectionMode.NoDriver => Colors.DarkOrange,
                _ => Colors.Gray,
            });

            InfoPanel.Children.Clear();
            AddInfo("机型", status.Model);
            AddInfo("系统", status.OsVersion);
            AddInfo("BL锁", status.BlLock);
            AddInfo("电量", status.Battery);
            AddInfo("存储", status.Storage);
        }

        /// <summary>追加一条「标签 值」信息；值为空则显示占位。</summary>
        private void AddInfo(string label, string? value)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(new TextBlock
            {
                Text = label,
                Opacity = 0.6,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            });
            panel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(value) ? "—" : value,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            });
            InfoPanel.Children.Add(panel);
        }
    }
}
