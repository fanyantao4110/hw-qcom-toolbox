using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MatePadToolbox.Services;
using System.IO;

namespace MatePadToolbox.Dialogs
{
    /// <summary>
    /// 取消 Root 弹窗：选择底包（UPDATE.APP）与设备当前模式。
    /// </summary>
    public sealed partial class UnrootDialog : ContentDialog
    {
        private readonly List<string> _appFiles = new();

        /// <summary>用户选中的底包完整路径（仅在主按钮提交后有效）。</summary>
        public string? SelectedApp { get; private set; }

        /// <summary>true=系统模式（自动 ADB 进 Fastboot）；false=已在 Fastboot 模式。</summary>
        public bool IsSystemMode { get; private set; }

        public UnrootDialog()
        {
            this.InitializeComponent();
            RefreshList();
            this.PrimaryButtonClick += UnrootDialog_PrimaryButtonClick;
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshList();

        private void RefreshList()
        {
            _appFiles.Clear();
            if (Directory.Exists(AppConfig.BaseRomDir))
                _appFiles.AddRange(Directory.EnumerateFiles(AppConfig.BaseRomDir, "*.app").OrderBy(f => f));

            AppCombo.ItemsSource = _appFiles.Select(Path.GetFileName).ToList();
            if (_appFiles.Count > 0)
            {
                AppCombo.SelectedIndex = 0;
                StatusBar.IsOpen = false;
            }
            else
            {
                StatusBar.Message = $"未在 base 目录找到任何 UPDATE.APP（{AppConfig.BaseRomDir}），请先放入底包。";
                StatusBar.Severity = InfoBarSeverity.Warning;
                StatusBar.IsOpen = true;
            }
        }

        private void UnrootDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            if (AppCombo.SelectedIndex < 0 || AppCombo.SelectedIndex >= _appFiles.Count)
            {
                args.Cancel = true;
                StatusBar.Message = "请先选择要使用的底包（UPDATE.APP）。";
                StatusBar.Severity = InfoBarSeverity.Error;
                StatusBar.IsOpen = true;
                return;
            }

            SelectedApp = _appFiles[AppCombo.SelectedIndex];
            IsSystemMode = ModeGroup.SelectedIndex == 0;
        }
    }
}
