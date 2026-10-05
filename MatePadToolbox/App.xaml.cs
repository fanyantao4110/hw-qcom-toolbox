using Microsoft.UI.Xaml;
using System;
using System.Text;
using System.Threading.Tasks;
using MatePadToolbox.Services;

namespace MatePadToolbox
{
    /// <summary>
    /// 应用程序入口。负责初始化窗口与全局异常处理。
    /// 启动到关闭的一切日志写入 log\ 目录（LogService）。
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;

        public App()
        {
            // 注册 GBK(936) 代码页，adb / fastboot / fh_loader 等工具在中文 Windows 下输出为 GBK
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            // 日志服务在首次访问时初始化并写入启动标记
            LogService.Info("应用程序初始化...");

            this.InitializeComponent();

            // UI 线程未处理异常
            this.UnhandledException += App_UnhandledException;

            // 非 UI 线程未处理异常
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                LogService.Exception("AppDomain 未处理异常", e.ExceptionObject as Exception);
            // 未观察的任务异常（防止后台任务静默失败）
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                LogService.Exception("未观察任务异常", e.Exception);
                e.SetObserved();
            };
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            LogService.Exception("UI 未处理异常", e.Exception);
            // 记录并吞掉异常，避免 UI 线程直接崩溃
            System.Diagnostics.Debug.WriteLine($"[未处理异常] {e.Exception}");
            e.Handled = true;
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            // 启动时创建程序所需的全部目录
            AppConfig.EnsureDirectories();

            LogService.Info("主窗口创建中...");
            _window = new MainWindow();
            _window.Closed += (_, _) => LogService.Shutdown("窗口已关闭");
            _window.Activate();
            LogService.Info("主窗口已激活。");
        }

        public static Window? MainWindow => ((App)Current)._window;
    }
}
