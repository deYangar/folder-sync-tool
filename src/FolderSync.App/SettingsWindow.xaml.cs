using System;
using System.Threading.Tasks;
using System.Windows;
using FolderSync.Core;

namespace FolderSync.App
{
    /// <summary>软件设置。选项勾选即生效（与旧版主窗口勾选行为一致），无需确定/取消。</summary>
    public partial class SettingsWindow : Window
    {
        /// <summary>回显初值期间不写注册表：XAML 已挂事件，InitializeComponent 后设 IsChecked 会触发 Changed。</summary>
        private bool _loading = true;

        /// <summary>用户动过自启勾选：晚到的后台回显不覆盖用户操作。</summary>
        private bool _autostartTouched;

        public SettingsWindow()
        {
            InitializeComponent();
            // 同 JobEditWindow：高度自适应内容 + 限工作区，防选项增多后按钮挤出窗外
            MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 16);
            Loaded += (_, _) => SizeToContent = SizeToContent.Manual;
            // 自启状态查询走 schtasks（EnsureUpToDate+IsEnabled+IsMinimized = 2-3 个子进程，慢机器数百 ms），
            // 曾在构造里同步跑——开窗顿一下；挪后台，查完回 UI 回显（用户已动过勾选则不覆盖）
            _ = LoadAutostartStateAsync();
            ChkShowIcons.IsChecked = AppSettings.ShowIcons;
            CmbTheme.SelectedIndex = AppSettings.ThemeMode;
            _loading = false;
            RefreshBaselineInfo();
        }

        private async Task LoadAutostartStateAsync()
        {
            var (enabled, minimized) = await Task.Run(() =>
            {
                Autostart.EnsureUpToDate();   // H-5 配套：exe 移位后自启任务里的旧路径刷新
                return (Autostart.IsEnabled(), Autostart.IsMinimized());
            }).ConfigureAwait(true);   // 回 UI 上下文设置勾选
            if (_autostartTouched || !IsLoaded) return;
            _loading = true;
            ChkAutostart.IsChecked = enabled;
            ChkAutostartMinimized.IsChecked = minimized;
            _loading = false;
        }

        private void ChkShowIcons_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;   // 回显初值不写库
            AppSettings.ShowIcons = ChkShowIcons.IsChecked == true;
        }

        private void CmbTheme_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading) return;
            ThemeManager.ThemeMode = CmbTheme.SelectedIndex;   // setter 内 Apply 即时切换
        }

        /// <summary>基线索引现状（打开时刷新一次；清理后也会刷新）。</summary>
        private void RefreshBaselineInfo()
        {
            var n = BaselineStore.CountAllEntries();
            TxtBaselineInfo.Text = n > 0 ? $"当前 {n} 条" : "当前无索引";
        }

        private void ChkAutostart_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _autostartTouched = true;
            try
            {
                if (ChkAutostart.IsChecked == true) Autostart.Enable(ChkAutostartMinimized.IsChecked == true);
                else Autostart.Disable();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "自启设置失败: " + ex.Message, "FolderSync",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ChkAutostartMinimized_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _autostartTouched = true;
            try
            {
                // 自启未开启时 SetMinimized 静默跳过：勾选状态在下次勾上「开机自启」时一并写入
                Autostart.SetMinimized(ChkAutostartMinimized.IsChecked == true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "自启模式设置失败: " + ex.Message, "FolderSync",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnClearBaseline_Click(object sender, RoutedEventArgs e)
        {
            var total = BaselineStore.CountAllEntries();
            if (total == 0)
            {
                TxtBaselineInfo.Text = "当前无索引";
                MessageBox.Show(this, "当前没有可清理的基线索引。", "清理基线索引",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var r = E2eSilent.Confirm(this,
                $"确定清理全部任务的块级增量基线索引？\n共 {total} 条（约 {Executor.FormatSize(total * 88L)} 索引量）。\n（不影响两侧文件内容；下次同步大文件时自动重建，首轮增量多读一遍目标）",
                "清理基线索引");
            if (r != MessageBoxResult.Yes) return;
            BtnClearBaseline.IsEnabled = false;
            try
            {
                var sites = await Task.Run(BaselineStore.ClearAllSites);
                RefreshBaselineInfo();
                MessageBox.Show(this, $"基线索引已清理（{sites} 处站点）。", "清理基线索引",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "清理失败: " + ex.Message, "FolderSync",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { BtnClearBaseline.IsEnabled = true; }
        }
    }
}
