using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MatePadToolbox.Dialogs;
using MatePadToolbox.Pages;
using MatePadToolbox.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace MatePadToolbox
{
    /// <summary>
    /// 主窗口：左侧导航 + 内容页 Frame。
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        // Windows 10 (1809+) 下让标题栏跟随深浅色：WinUI3 不会自动设置 DWM 深色标题栏，
        // 需要手动调用 DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE)。
        // 不同系统版本接受不同属性值：Win11 / Win10 1903+ 用 20；部分 Win10 版本（含本机 1909）
        // 只接受 19。因此依次尝试，成功即停止。
        private static readonly int[] DWM_IMMERSIVE_DARK_MODE_ATTRIBUTES = { 20, 19 };

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;

        private readonly Dictionary<string, Type> _pages = new()
        {
            { "home", typeof(HomePage) },
            { "driver", typeof(DriverPage) },
            { "unlock", typeof(UnlockBlPage) },
            { "root", typeof(RootPage) },
            { "optimize", typeof(OptimizeModulesPage) },
            { "download", typeof(DownloadRomPage) },
            { "flash", typeof(FlashSystemPage) },
            { "advflash", typeof(AdvancedFlashPage) },
            { "official", typeof(FlashOfficialPage) },
            { "downgrade", typeof(DowngradePage) },
            { "advanced", typeof(AdvancedToolboxPage) },
            { "parttool", typeof(PartToolPage) },
            { "console", typeof(AdbConsolePage) },
            { "backup9008", typeof(Backup9008Page) },
        };

        private bool _disclaimerShown;

        public MainWindow()
        {
            this.InitializeComponent();

            // 窗口标题与尺寸
            this.Title = "华为高通芯片通用工具箱";
            if (this.AppWindow is Microsoft.UI.Windowing.AppWindow appWindow)
            {
                appWindow.Resize(new Windows.Graphics.SizeInt32(1180, 780));
            }

            // 标题栏主题：内容加载完成即应用（不依赖激活事件，避免启动时标题栏仍是白色），
            // 激活与系统深浅色切换时实时更新。
            this.Activated += (_, _) => ApplyTitleBarTheme("Activated");
            if (Content is FrameworkElement root)
            {
                root.Loaded += (_, _) => ApplyTitleBarTheme("Loaded");
                root.ActualThemeChanged += (_, _) => ApplyTitleBarTheme("ActualThemeChanged");
            }
        }

        /// <summary>
        /// 根据当前实际主题设置 DWM 标题栏深浅色（Windows 10 必需）。
        /// </summary>
        private void ApplyTitleBarTheme(string why = "")
        {
            try
            {
                var hwnd = WindowNative.GetWindowHandle(this);
                if (hwnd == IntPtr.Zero) return;
                if (Content is not FrameworkElement root) return;

                int dark = root.ActualTheme == ElementTheme.Dark ? 1 : 0;
                foreach (int attribute in DWM_IMMERSIVE_DARK_MODE_ATTRIBUTES)
                {
                    int hr = DwmSetWindowAttribute(hwnd, attribute, ref dark, sizeof(int));
                    if (hr >= 0) break; // S_OK / S_FALSE 均视为成功
                }

                // 强制 DWM 立即按新属性重绘标题栏，避免首次显示时仍用浅色帧
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
            catch
            {
                // 标题栏主题设置失败不应影响主流程
            }
        }

        private void NavView_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshGlobalProcessors();
            NavView.SelectedItem = NavView.MenuItems[0];
            ContentFrame.Navigate(typeof(HomePage));

            if (!_disclaimerShown)
            {
                _disclaimerShown = true;
                _ = ShowDisclaimerAsync();
            }
        }

        /// <summary>
        /// 加载并选中全局处理器（数据来自 unlock\processors.json）。
        /// </summary>
        private void RefreshGlobalProcessors()
        {
            var list = GlobalProcessorService.LoadProcessors(_ => { });
            GlobalProcCombo.ItemsSource = list;
            if (GlobalProcCombo.Items.Count > 0)
            {
                var cur = GlobalProcessorService.Current;
                var idx = cur == null
                    ? -1
                    : list.FindIndex(p => string.Equals(p.Name, cur.Name, StringComparison.Ordinal));
                // 默认不自动选中，防止未选择型号就误操作（避免刷错型号）
                GlobalProcCombo.SelectedIndex = idx >= 0 ? idx : -1;
                if (idx < 0)
                {
                    GlobalProcessorService.Current = null;
                    GlobalProcStatus.Text = "请先选择 CPU 型号";
                }
            }
            else
            {
                GlobalProcCombo.SelectedIndex = -1;
                GlobalProcessorService.Current = null;
                GlobalProcStatus.Text = "未找到处理器配置";
            }
        }

        private void GlobalProcCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GlobalProcCombo.SelectedItem is Models.ProcessorEntry proc)
            {
                GlobalProcessorService.Current = proc;
                GlobalProcStatus.Text = proc.Chip;
            }
            else
            {
                GlobalProcessorService.Current = null;
                GlobalProcStatus.Text = string.Empty;
            }
        }

        private void RefreshGlobalProc_Click(object sender, RoutedEventArgs e)
        {
            RefreshGlobalProcessors();
        }

        private async System.Threading.Tasks.Task ShowDisclaimerAsync()
        {
            var dlg = new DisclaimerDialog
            {
                XamlRoot = NavView.XamlRoot
            };
            var result = await dlg.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                // 不同意免责声明 → 直接退出程序
                Application.Current.Exit();
            }
        }

        private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (args.InvokedItemContainer is not NavigationViewItem item) return;
            var tag = item.Tag?.ToString() ?? string.Empty;

            if (tag == "adbtools")
            {
                // ADB 工具集：既启动外部 adb 工具集软件，也在主界面显示内置 ADB 命令行
                // （原「ADB命令行」侧边栏项已合并到此处）。
                LaunchAdbToolkit();
                if (ContentFrame.CurrentSourcePageType != typeof(AdbConsolePage))
                {
                    ContentFrame.Navigate(typeof(AdbConsolePage));
                }
                return;
            }

            if (tag == "devmgr")
            {
                // 设备管理器：直接打开系统管理控制台
                try
                {
                    Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    ShowToast($"打开设备管理器失败：{ex.Message}");
                }
                return;
            }

            if (tag == "about")
            {
                // 关于：弹出软件信息 / 作者与贡献者对话框
                ShowAboutAsync();
                return;
            }

            if (_pages.TryGetValue(tag, out var pageType))
            {
                if (ContentFrame.CurrentSourcePageType != pageType)
                {
                    ContentFrame.Navigate(pageType);
                }
            }
        }

        /// <summary>ADB工具集：强制结束同名已运行实例后再启动，保证每次点击都是全新实例。</summary>
        private void LaunchAdbToolkit()
        {
            try
            {
                var exe = AppConfig.AdbToolkitExe;
                if (!File.Exists(exe))
                {
                    ShowToast($"未找到 ADB工具集：{exe}");
                    return;
                }

                // 强制结束所有同名已运行实例
                foreach (var p in Process.GetProcessesByName("adb-toolbox"))
                {
                    try { p.Kill(); p.WaitForExit(2000); } catch { /* 忽略结束失败 */ }
                }

                Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exe)!
                });
            }
            catch (Exception ex)
            {
                ShowToast($"启动 ADB工具集失败：{ex.Message}");
            }
        }

        /// <summary>显示“关于”对话框：软件信息、作者与贡献者、开源致谢。</summary>
        private async void ShowAboutAsync()
        {
            try
            {
                var version = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "未知";
                var text = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                    LineHeight = 22,
                    Text = "华为高通芯片通用工具箱（MatePadToolbox）\n" +
                           "版本：" + version + "\n" +
                           "\n" +
                           "作者：fanyantao\n" +
                           "\n" +
                           "特别感谢以下开源项目与贡献者：\n" +
                           "· MagiskPatcher（酷安@某贼 / xda@SYXZ）— Magisk 修补工具\n" +
                           "   https://github.com/mouzei/MagiskPatcher\n" +
                           "· ADMT（LACS-Official）— adb 工具集\n" +
                           "   https://github.com/LACS-Official/admt\n" +
                           "· Qualcomm — QSaharaServer / fh_loader / ptool\n" +
                           "· HuaweiFirmwareExtractor（Natsume324）— UPDATE.APP 解包工具\n" +
                           "   https://github.com/Natsume324/HuaweiFirmwareExtractor\n" +
                           "· APatch & Magisk — root 方案\n" +
                           "· 7-Zip、Android platform-tools、HiSuite Proxy\n" +
                           "\n" +
                           "本软件仅供学习研究使用，请遵守相关法律法规。\n" +
                           "\n" +
                           "感谢所有第三方系统的制作者。"
                };
                var dlg = new ContentDialog
                {
                    Title = "关于",
                    Content = text,
                    CloseButtonText = "确定",
                    XamlRoot = NavView.XamlRoot
                };
                await dlg.ShowAsync();
            }
            catch { /* 忽略弹窗异常 */ }
        }

        private async void ShowToast(string message)
        {
            try
            {
                var dlg = new ContentDialog
                {
                    Title = "提示",
                    Content = message,
                    CloseButtonText = "确定",
                    XamlRoot = NavView.XamlRoot
                };
                await dlg.ShowAsync();
            }
            catch { /* 忽略弹窗异常 */ }
        }
    }
}
