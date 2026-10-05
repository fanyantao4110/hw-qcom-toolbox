using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MatePadToolbox.Services;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MatePadToolbox.Pages
{
    /// <summary>
    /// 分区解包 / 打包页：super 动态分区与 erofs/ext4 分区镜像的解包与打包。
    /// 交互：默认路径自动填充，镜像/文件树目录由列表选择并自动识别类型，解包自动导出 config，
    /// 分区解包输出到 img\\分区名，成功后在右上角轻提示。
    /// </summary>
    public sealed partial class PartToolPage : Page
    {
        private CancellationTokenSource? _cts;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _tiptimer;

        public PartToolPage()
        {
            this.InitializeComponent();
            Loaded += PartToolPage_Loaded;
        }

        private void PartToolPage_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshSuperList();
            RefreshPartList();
            RefreshPartSrcList();
            SeedErofsSettings();
        }

        /// <summary>用设置里的 erofs 压缩（如 lz4hc,8）初始化压缩算法/等级下拉框默认值。</summary>
        private void SeedErofsSettings()
        {
            var st = PartitionSettings.Load();
            var comp = st.ErofsCompress.Split(',');
            if (comp.Length > 0 && !string.IsNullOrWhiteSpace(comp[0]))
            {
                var algo = comp[0].Trim();
                PartAlgoCombo.SelectedIndex = algo switch { "lz4" => 0, "lzma" => 2, _ => 1 };
            }
            if (comp.Length > 1 && int.TryParse(comp[1].Trim(), out var lv) && lv >= 0 && lv <= 9)
                PartLevelBox.Text = lv.ToString();
        }

        /// <summary>右上角成功提示，数秒后自动淡出。</summary>
        private void ShowTopRightTip(string title, string detail)
        {
            TopRightTipText.Text = title;
            TopRightTipDetail.Text = detail;
            TopRightTip.Opacity = 1;
            _tiptimer?.Stop();
            _tiptimer = DispatcherQueue.CreateTimer();
            _tiptimer.Interval = TimeSpan.FromSeconds(6);
            _tiptimer.Tick += (_, _) => { TopRightTip.Opacity = 0; _tiptimer?.Stop(); };
            _tiptimer.Start();
        }

        private void SetBusy(bool busy)
        {
            BtnExtractSuper.IsEnabled = !busy;
            BtnPackSuper.IsEnabled = !busy;
            BtnExtractPart.IsEnabled = !busy;
            BtnPackPart.IsEnabled = !busy;
            BtnCancelSuper.IsEnabled = busy;
            BtnCancelPart.IsEnabled = busy;
        }
        private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
        private void ClearLog_Click(object sender, RoutedEventArgs e) => Log.Clear();

        // ==================== 列表：super 镜像 ====================

        private void RefreshSuperList_Click(object sender, RoutedEventArgs e) => RefreshSuperList();
        /// <summary>扫描 img 目录与 exe 根下的 .img，自动识别出 super 镜像加入列表。</summary>
        private void RefreshSuperList()
        {
            var selected = SuperImgCombo.SelectedItem as string;
            SuperImgCombo.Items.Clear();
            var found = new System.Collections.Generic.List<string>();
            foreach (var dir in new[] { AppConfig.PartImg1Dir, AppConfig.PartImg2Dir, AppConfig.BaseDir })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, "*.img"))
                {
                    try { if (PartitionToolService.IsSuper(f)) found.Add(f); } catch { }
                }
            }
            foreach (var f in found.OrderBy(x => x)) SuperImgCombo.Items.Add(f);
            if (SuperImgCombo.Items.Count == 0)
                Log.Append("[信息] 未在 img 目录/exe 根下发现 super 镜像，可点击“浏览…”手动选择，或把 super.img 放入 img 目录。");
            if (selected != null && SuperImgCombo.Items.Contains(selected)) SuperImgCombo.SelectedItem = selected;
        }

        private void SuperImgCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var f = SuperImgCombo.SelectedItem as string;
            if (string.IsNullOrEmpty(f)) { SuperDetectText.Text = "选择后自动检测镜像类型。"; return; }
            var type = PartitionToolService.DetectFs(f);
            SuperDetectText.Text = type == PartitionToolService.FsSuper
                ? $"已选择：{Path.GetFileName(f)} —— 检测为 super（LPDISK），解包输出到 img\\img_1。"
                : $"已选择：{Path.GetFileName(f)} —— 未检测到 super 签名（类型：{type}），解包可能失败。";
        }

        private void BrowseSuperImg_Click(object sender, RoutedEventArgs e) => PickImage();

        private async void PickImage()
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                picker.FileTypeFilter.Add(".img");
                picker.FileTypeFilter.Add("*");
                var hwnd = GetHwnd();
                if (hwnd.HasValue) WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd.Value);
                var file = await picker.PickSingleFileAsync();
                if (file == null) return;
                if (!SuperImgCombo.Items.Contains(file.Path)) SuperImgCombo.Items.Add(file.Path);
                SuperImgCombo.SelectedItem = file.Path;
            }
            catch (Exception ex)
            {
                Log.Append($"[提示] 打开文件选择器失败（{ex.Message}）。");
            }
        }

        // ==================== 列表：分区镜像（过滤 super） ====================

        private void RefreshPartList_Click(object sender, RoutedEventArgs e) => RefreshPartList();
        /// <summary>列出 img\\img_1 下的分区镜像，过滤掉 super（分区模式不处理 super）。</summary>
        private void RefreshPartList()
        {
            var selected = PartImgCombo.SelectedItem as FileInfo;
            PartImgCombo.Items.Clear();
            var dir = AppConfig.PartImg1Dir;
            if (!Directory.Exists(dir)) { Log.Append($"[信息] 目录不存在，忽略：{dir}"); return; }
            foreach (var f in Directory.GetFiles(dir, "*.img").OrderBy(x => x))
            {
                try { if (PartitionToolService.IsSuper(f)) continue; } catch { }
                PartImgCombo.Items.Add(new FileInfo(f));
            }
            if (PartImgCombo.Items.Count == 0)
                Log.Append("[信息] img\\img_1 下暂无可用分区镜像，可先在“Super 动态分区”Tab 解包 super。");
            if (selected != null)
            {
                var sel = PartImgCombo.Items.OfType<FileInfo>().FirstOrDefault(x => x.FullName == selected.FullName);
                if (sel != null) PartImgCombo.SelectedItem = sel;
            }
        }

        private void PartImgCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PartImgCombo.SelectedItem is not FileInfo fi) { PartDetectText.Text = "请在列表中选择要解包的分区镜像。"; return; }
            var name = Path.GetFileNameWithoutExtension(fi.Name);
            var fsType = PartitionToolService.DetectFs(fi.FullName);
            var label = fsType switch
            {
                PartitionToolService.FsErofs => "erofs",
                PartitionToolService.FsExt4 => "ext4",
                "sparse" => "sparse（解包时自动转 raw）",
                _ => "未知"
            };
            PartDetectText.Text = $"已选择：{fi.Name} —— 类型：{label}，解包输出到 img\\{name}\\，并自动导出 config。";
        }

        // ==================== 列表：源文件树目录 ====================

        private void RefreshPartSrcList_Click(object sender, RoutedEventArgs e) => RefreshPartSrcList();
        /// <summary>列出 img 根下的文件树子目录（如 system / vendor）。</summary>
        private void RefreshPartSrcList()
        {
            var selected = PartSrcCombo.SelectedItem as DirectoryInfo;
            PartSrcCombo.Items.Clear();
            if (!Directory.Exists(AppConfig.PartImgDir)) return;
            foreach (var d in Directory.GetDirectories(AppConfig.PartImgDir))
            {
                var name = Path.GetFileName(d.TrimEnd('\\'));
                if (name.StartsWith("img_") || name == "config") continue;
                PartSrcCombo.Items.Add(new DirectoryInfo(d));
            }
            if (selected != null)
            {
                var sel = PartSrcCombo.Items.OfType<DirectoryInfo>().FirstOrDefault(x => x.FullName == selected.FullName);
                if (sel != null) PartSrcCombo.SelectedItem = sel;
            }
        }

        private void PartSrcCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PartSrcCombo.SelectedItem is not DirectoryInfo di) return;
            var name = di.Name;
            PartNameBox.Text = name;
            PartFsCfgBox.Text = Path.Combine(AppConfig.PartImgCfgDir, name + "_fs_config");
            PartFctxBox.Text = Path.Combine(AppConfig.PartImgCfgDir, name + "_file_contexts");
        }

        private IntPtr? GetHwnd()
        {
            try
            {
                return App.MainWindow == null ? null : WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            }
            catch { return null; }
        }

        // ==================== Super 解包 ====================

        private async void ExtractSuper_Click(object sender, RoutedEventArgs e)
        {
            var img = SuperImgCombo.SelectedItem as string;
            if (string.IsNullOrEmpty(img) || !File.Exists(img))
            {
                Log.Append("[错误] 请先在列表中选择 super 镜像，或点击“浏览…”选择。");
                return;
            }
            var outDir = AppConfig.PartImg1Dir;

            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                Log.Append("===== 解包 Super =====");
                Log.Append($"[信息] 镜像：{img}；输出：{outDir}");
                var ok = await PartitionToolService.ExtractSuperAsync(img, outDir, Log.Append, _cts.Token);
                Log.Append(ok ? "[OK] Super 解包完成。" : "[错误] Super 解包失败。");
                if (ok)
                {
                    ShowTopRightTip("Super 解包完成",
                        $"已解出各分区镜像到 img\\img_1，共 {Directory.GetFiles(outDir, "*.img").Length} 个。");
                    RefreshPartList();
                    await AskContinuePartitionsAsync();
                }
            }
            catch (OperationCanceledException) { Log.Append("[已取消]"); }
            catch (Exception ex) { Log.Append($"[错误] 解包异常：{ex.Message}"); }
            finally { SetBusy(false); }
        }

        /// <summary>解完 super 后询问是否继续解包其它分区，并把已解出的分区加入列表供选择。</summary>
        private async Task AskContinuePartitionsAsync()
        {
            var parts = Directory.GetFiles(AppConfig.PartImg1Dir, "*.img");
            if (parts.Length == 0) return;
            if (!await UiHelpers.ConfirmAsync(this.XamlRoot, "继续解包分区",
                $"img\\img_1 下已解出 {parts.Length} 个分区镜像。是否继续解包其中的分区（自动生成 config）？",
                "继续解包分区", "暂不"))
            {
                return;
            }
            PartPivot.SelectedIndex = 1; // 切到"分区镜像"页
            RefreshPartList();
            if (PartImgCombo.Items.Count > 0) PartImgCombo.SelectedIndex = 0;
            Log.Append("[信息] 请在“分区镜像”列表中选择要解包的分区。");
        }

        // ==================== Super 打包 ====================

        private async void PackSuper_Click(object sender, RoutedEventArgs e)
        {
            var partDir = AppConfig.PartImg2Dir;
            var outSuper = Path.Combine(AppConfig.PartImg3Dir, "super.img");
            if (!Directory.Exists(partDir) || Directory.GetFiles(partDir, "*.img").Length == 0)
            {
                Log.Append("[错误] 请先把分区镜像放入 img\\img_2。");
                return;
            }

            var st = PartitionSettings.Load();
            var group = string.IsNullOrWhiteSpace(st.SuperGroup) ? "qti_dynamic_partitions" : st.SuperGroup.Trim();
            int slots = st.MetadataSlots > 0 ? st.MetadataSlots : 2;
            long? size = st.CustomDeviceSize > 0 ? st.CustomDeviceSize : (long?)null;

            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                Log.Append("===== 打包 Super =====");
                Log.Append($"[信息] 分区目录：{partDir}；输出：{outSuper}");
                var ok = await PartitionToolService.PackSuperAsync(partDir, outSuper, group, slots, size, Log.Append, _cts.Token);
                Log.Append(ok ? "[OK] Super 打包完成。" : "[错误] Super 打包失败。");
                if (ok) ShowTopRightTip("Super 打包完成", $"已生成 super.img 到 img\\img_3。");
            }
            catch (OperationCanceledException) { Log.Append("[已取消]"); }
            catch (Exception ex) { Log.Append($"[错误] 打包异常：{ex.Message}"); }
            finally { SetBusy(false); }
        }

        // ==================== 分区镜像 解包 ====================

        private async void ExtractPart_Click(object sender, RoutedEventArgs e)
        {
            if (PartImgCombo.SelectedItem is not FileInfo fi)
            {
                Log.Append("[错误] 请先在列表中选择要解包的分区镜像。");
                return;
            }
            var img = fi.FullName;
            var name = Path.GetFileNameWithoutExtension(fi.Name);
            var outDir = Path.Combine(AppConfig.PartImgDir, name);

            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                Log.Append("===== 解包分区镜像 =====");
                Log.Append($"[信息] 镜像：{img}；输出：{outDir}");
                // 强制导出 config 到 img\\config（打包前置），不再提供开关
                var ok = await PartitionToolService.ExtractImageAsync(img, outDir, null,
                    AppConfig.PartImgCfgDir, Log.Append, _cts.Token);
                if (ok)
                {
                    Log.Append("[OK] 分区镜像解包完成，config 已自动导出。");
                    ShowTopRightTip("分区解包完成", $"文件树已输出到 img\\{name}\\，config 已导出到 img\\config。");
                    RefreshPartSrcList();
                }
                else
                {
                    Log.Append("[错误] 分区镜像解包失败。");
                }
            }
            catch (OperationCanceledException) { Log.Append("[已取消]"); }
            catch (Exception ex) { Log.Append($"[错误] 解包异常：{ex.Message}"); }
            finally { SetBusy(false); }
        }

        // ==================== 分区镜像 打包 ====================

        private async void PackPart_Click(object sender, RoutedEventArgs e)
        {
            if (PartSrcCombo.SelectedItem is not DirectoryInfo di)
            {
                Log.Append("[错误] 请先在列表中选择源文件树目录（img 根下的 system / vendor 等）。");
                return;
            }
            var srcDir = di.FullName;
            var name = string.IsNullOrWhiteSpace(PartNameBox.Text) ? di.Name : PartNameBox.Text.Trim();
            var outImg = Path.Combine(AppConfig.PartImg2Dir, name + ".img");

            var fsType = PartFsCombo.SelectedIndex == 1 ? PartitionToolService.FsExt4 : PartitionToolService.FsErofs;
            string algo = "lz4hc";
            switch (PartAlgoCombo.SelectedIndex)
            {
                case 0: algo = "lz4"; break;
                case 2: algo = "lzma"; break;
            }
            int level = 8;
            if (PartLevelBox.Text != null && int.TryParse(PartLevelBox.Text.Trim(), out var lv) && lv >= 0 && lv <= 9) level = lv;

            string? fsCfg = null, fctx = null;
            if (!string.IsNullOrWhiteSpace(PartFsCfgBox.Text)) fsCfg = PartFsCfgBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(PartFctxBox.Text)) fctx = PartFctxBox.Text.Trim();

            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                Log.Append("===== 打包分区镜像 =====");
                Log.Append($"[信息] 源目录：{srcDir}；输出：{outImg}；类型：{fsType}");
                var ok = await PartitionToolService.PackImageAsync(srcDir, outImg, fsType, name,
                    algo, level, fsCfg, fctx, null, Log.Append, _cts.Token);
                Log.Append(ok ? "[OK] 分区镜像打包完成。" : "[错误] 分区镜像打包失败。");
                if (ok) ShowTopRightTip("分区打包完成", $"已生成 {name}.img 到 img\\img_2。");
            }
            catch (OperationCanceledException) { Log.Append("[已取消]"); }
            catch (Exception ex) { Log.Append($"[错误] 打包异常：{ex.Message}"); }
            finally { SetBusy(false); }
        }

        // ==================== 顶部设置菜单 ====================

        private static TextBlock SectionHeader(string text) => new()
        {
            Text = text,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 6, 0, 0)
        };

        private async void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var st = PartitionSettings.Load();

            var superName = new TextBox { Text = st.SuperName, Header = "Super 名（lpmake --super-name）", MinWidth = 260 };
            var group = new TextBox { Text = st.SuperGroup, Header = "动态分区组名" };
            var metaSize = new TextBox { Text = st.MetadataSize.ToString(), Header = "Metadata 大小（字节）" };
            var slots = new TextBox { Text = st.MetadataSlots.ToString(), Header = "Metadata 槽数" };
            var extraBuffer = new TextBox { Text = st.ExtraBufferMB.ToString(), Header = "设备总大小额外余量（MB）" };
            var customSize = new TextBox { Text = st.CustomDeviceSize > 0 ? st.CustomDeviceSize.ToString() : "", Header = "自定义 super 总大小（字节，留空=自动）" };
            var packSparse = new ToggleSwitch { Header = "打包为 sparse 稀疏镜像", IsOn = st.PackSparse, OnContent = "", OffContent = "" };
            var readOnly = new ToggleSwitch { Header = "分区属性：只读（关闭=可读写）", IsOn = st.PartitionReadOnly, OnContent = "", OffContent = "" };
            var erofsCompress = new TextBox { Text = st.ErofsCompress, Header = "erofs 压缩（算法,等级，如 lz4hc,8）" };
            var utcStamp = new TextBox { Text = st.UtcStamp.ToString(), Header = "erofs 固定时间戳（-T，2009-01-01 = 1230768000）" };
            var oldKernel = new ToggleSwitch { Header = "erofs 老内核兼容（-E legacy-compress）", IsOn = st.ErofsOldKernel, OnContent = "", OffContent = "" };
            var ext4Block = new TextBox { Text = st.Ext4BlockSize.ToString(), Header = "ext4 块大小（-b）" };

            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(SectionHeader("Super 打包"));
            panel.Children.Add(superName);
            panel.Children.Add(group);
            panel.Children.Add(metaSize);
            panel.Children.Add(slots);
            panel.Children.Add(extraBuffer);
            panel.Children.Add(customSize);
            panel.Children.Add(packSparse);
            panel.Children.Add(readOnly);
            panel.Children.Add(SectionHeader("EROFS 打包"));
            panel.Children.Add(erofsCompress);
            panel.Children.Add(utcStamp);
            panel.Children.Add(oldKernel);
            panel.Children.Add(SectionHeader("EXT4 打包"));
            panel.Children.Add(ext4Block);

            var dlg = new ContentDialog
            {
                Title = "打包 / 解包设置",
                Content = new ScrollViewer { Content = panel, MaxHeight = 500 },
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };

            var result = await dlg.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            try
            {
                st.SuperName = superName.Text.Trim();
                st.SuperGroup = group.Text.Trim();
                st.MetadataSize = long.Parse(metaSize.Text.Trim());
                st.MetadataSlots = int.Parse(slots.Text.Trim());
                st.ExtraBufferMB = long.Parse(extraBuffer.Text.Trim());
                st.CustomDeviceSize = string.IsNullOrWhiteSpace(customSize.Text) ? 0 : long.Parse(customSize.Text.Trim());
                st.PackSparse = packSparse.IsOn;
                st.PartitionReadOnly = readOnly.IsOn;
                st.ErofsCompress = erofsCompress.Text.Trim();
                st.UtcStamp = long.Parse(utcStamp.Text.Trim());
                st.ErofsOldKernel = oldKernel.IsOn;
                st.Ext4BlockSize = int.Parse(ext4Block.Text.Trim());
                if (st.Save())
                {
                    Log.Append($"[OK] 设置已保存：{PartitionSettings.SettingsFile}");
                    ShowTopRightTip("设置已保存", "下次打包/解包将使用新设置。");
                }
                else
                {
                    Log.Append("[错误] 设置保存失败。");
                }
            }
            catch (Exception ex)
            {
                Log.Append($"[错误] 设置格式不正确，未保存：{ex.Message}");
            }
        }
    }
}
