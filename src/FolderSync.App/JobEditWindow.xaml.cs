using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FolderSync.Core;
using Microsoft.Win32;

namespace FolderSync.App
{
    public partial class JobEditWindow : Window
    {
        public string JobName => TxtName.Text.Trim();
        public string LeftPath => TxtLeft.Text.Trim();
        public string RightPath => TxtRight.Text.Trim();
        public SyncDirection Direction => CmbDirection.SelectedIndex switch
        {
            1 => SyncDirection.MirrorRightToLeft,
            2 => SyncDirection.TwoWay,
            3 => SyncDirection.BackupLeftToRight,
            4 => SyncDirection.BackupRightToLeft,
            _ => SyncDirection.MirrorLeftToRight
        };
        public ConflictPolicy ConflictPolicy => CmbConflictPolicy.SelectedIndex switch
        {
            1 => ConflictPolicy.LargestSize,
            2 => ConflictPolicy.Manual,
            3 => ConflictPolicy.ConflictCopy,
            _ => ConflictPolicy.NewestMtime
        };
        public TriggerType Trigger => CmbTrigger.SelectedIndex switch
        {
            1 => TriggerType.Realtime,
            2 => TriggerType.Interval,
            3 => TriggerType.Schedule,
            _ => TriggerType.Manual
        };

        /// <summary>Schedule 触发的规格串（"daily HH:mm" / "weekly D,D HH:mm"）；非法组合返回 null（保存校验拦截）。
        /// 数值解析一律 InvariantCulture：区域设置改成逗号小数（de-DE 等）时别把 "0.5" 解析成 5 万。</summary>
        public string? ScheduleSpec
        {
            get
            {
                if (Trigger != TriggerType.Schedule) return null;
                var hm = $"{CmbSchedHour.Text}:{int.Parse(CmbSchedMinute.Text, System.Globalization.CultureInfo.InvariantCulture):D2}";
                if (ChkSchedDaily.IsChecked == true) return $"daily {hm}";
                var days = new[] { (ChkD1, 1), (ChkD2, 2), (ChkD3, 3), (ChkD4, 4), (ChkD5, 5), (ChkD6, 6), (ChkD7, 7) }
                    .Where(x => x.Item1.IsChecked == true).Select(x => x.Item2).ToList();
                return days.Count == 0 ? null : $"weekly {string.Join(",", days)} {hm}";
            }
        }
        public int IntervalSeconds
        {
            get
            {
                if (!double.TryParse(TxtInterval.Text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var v) || v <= 0) v = 2;
                var mul = CmbIntervalUnit.SelectedIndex switch { 1 => 60, 2 => 1, _ => 3600 };
                return Math.Max(10, (int)Math.Round(v * mul));   // 任何单位最小 10 秒（半小时 = 0.5 小时也允许）
            }
        }
        public int DebounceSeconds => int.TryParse(TxtDebounce.Text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var i) ? Math.Max(1, i) : 10;
        public string ExcludePatterns => TxtExclude.Text.Trim();
        /// <summary>版本保留代数：0=关闭；非法输入按 0 处理；上限 999</summary>
        public int VersionKeepCount => int.TryParse(TxtVersionKeep.Text.Trim(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? Math.Clamp(n, 0, 999) : 0;
        public bool MirrorDelete => ChkMirrorDelete.IsChecked == true;
        public bool StrictMirror => ChkStrictMirror.IsChecked == true;
        public bool DeltaSync => ChkDeltaSync.IsChecked == true;
        public bool AutoRetry => ChkAutoRetry.IsChecked == true;
        public bool MoveDetect => ChkMoveDetect.IsChecked == true;
        public bool CopyVerify => ChkCopyVerify.IsChecked == true;
        public int DeepVerify => CmbDeepVerify.SelectedIndex switch { 1 => 1, 2 => 2, _ => 0 };
        public int CopyWorkers => CmbCopyWorkers.SelectedIndex switch { 0 => 1, 2 => 3, 3 => 4, _ => 2 };
        public bool DeleteToRecycleBin => ChkRecycle.IsChecked == true;
        public bool Enabled => ChkEnabled.IsChecked == true;

        public JobEditWindow(SyncJob? job)
        {
            InitializeComponent();
            // 高度自适应内容（曾固定 650：v1.4 加选项后内容超高，确定/取消被挤出可视区且 NoResize 无法自救）。
            // MaxHeight 限到工作区，小屏/高缩放时由内部 ScrollViewer 兜底；
            // Loaded 后切 Manual：保持贴合后的高度，同时允许用户自由拉伸窗口
            MaxHeight = Math.Max(480, SystemParameters.WorkArea.Height - 16);
            Loaded += (_, _) => SizeToContent = SizeToContent.Manual;
            // 事件必须在 InitializeComponent 之后挂：XAML 阶段 SelectedIndex 赋值会触发
            // SelectionChanged，而此时后声明的 PnlInterval/TxtDebounce 还未创建（NRE 闪退）
            CmbTrigger.SelectionChanged += CmbTrigger_Changed;
            CmbDirection.SelectionChanged += (_, _) => UpdateDirectionVisibility();
            // Schedule 时刻下拉填充（0-23 时 / 0-59 分）
            for (int h = 0; h <= 23; h++) CmbSchedHour.Items.Add(h.ToString("D2"));
            for (int m = 0; m <= 59; m++) CmbSchedMinute.Items.Add(m.ToString("D2"));
            CmbSchedHour.SelectedIndex = 3;
            CmbSchedMinute.SelectedIndex = 0;
            if (job != null)
            {
                TxtName.Text = job.Name;
                TxtLeft.Text = job.LeftPath;
                TxtRight.Text = job.RightPath;
                CmbDirection.SelectedIndex = job.Direction switch
                {
                    SyncDirection.MirrorRightToLeft => 1,
                    SyncDirection.TwoWay => 2,
                    SyncDirection.BackupLeftToRight => 3,
                    SyncDirection.BackupRightToLeft => 4,
                    _ => 0
                };
                CmbConflictPolicy.SelectedIndex = job.ConflictPolicy switch
                {
                    ConflictPolicy.LargestSize => 1,
                    ConflictPolicy.Manual => 2,
                    ConflictPolicy.ConflictCopy => 3,
                    _ => 0
                };
                CmbTrigger.SelectedIndex = job.Trigger switch
                {
                    TriggerType.Realtime => 1,
                    TriggerType.Interval => 2,
                    TriggerType.Schedule => 3,
                    _ => 0
                };
                // Schedule 回显：daily 取时刻掩星期；weekly 勾对应星期
                if (FolderSync.Core.ScheduleSpec.Parse(job.ScheduleSpec) is { } ps)
                {
                    CmbSchedHour.SelectedIndex = Math.Clamp(ps.Time.h, 0, 23);
                    CmbSchedMinute.SelectedIndex = Math.Clamp(ps.Time.m, 0, 59);
                    if (ps.DaysMask == null)
                    {
                        ChkSchedDaily.IsChecked = true;
                    }
                    else
                    {
                        ChkSchedDaily.IsChecked = false;
                        foreach (var (chk, iso) in new[] { (ChkD1, 1), (ChkD2, 2), (ChkD3, 3), (ChkD4, 4), (ChkD5, 5), (ChkD6, 6), (ChkD7, 7) })
                            chk.IsChecked = ((ps.DaysMask.Value >> iso) & 1) == 1;
                    }
                }
                // 回显：能整除小时用小时，整除分钟用分钟，否则秒
                if (job.IntervalSeconds >= 3600 && job.IntervalSeconds % 3600 == 0)
                { TxtInterval.Text = (job.IntervalSeconds / 3600).ToString(); CmbIntervalUnit.SelectedIndex = 0; }
                else if (job.IntervalSeconds >= 60 && job.IntervalSeconds % 60 == 0)
                { TxtInterval.Text = (job.IntervalSeconds / 60).ToString(); CmbIntervalUnit.SelectedIndex = 1; }
                else
                { TxtInterval.Text = job.IntervalSeconds.ToString(); CmbIntervalUnit.SelectedIndex = 2; }
                TxtDebounce.Text = job.DebounceSeconds.ToString();
                TxtExclude.Text = job.ExcludePatterns;
                TxtVersionKeep.Text = job.VersionKeepCount.ToString();
                ChkStrictMirror.IsChecked = job.StrictMirror;
                ChkDeltaSync.IsChecked = job.DeltaSync;
                ChkAutoRetry.IsChecked = job.AutoRetry;
                ChkMoveDetect.IsChecked = job.MoveDetect;
                ChkCopyVerify.IsChecked = job.CopyVerify;
                CmbDeepVerify.SelectedIndex = job.DeepVerify switch { 1 => 1, 2 => 2, _ => 0 };
                CmbCopyWorkers.SelectedIndex = job.CopyWorkers switch { 1 => 0, 3 => 2, 4 => 3, _ => 1 };
                ChkMirrorDelete.IsChecked = job.MirrorDelete;
                ChkRecycle.IsChecked = job.DeleteToRecycleBin;
                ChkEnabled.IsChecked = job.Enabled;
            }
            UpdateTriggerVisibility();
            UpdateDirectionVisibility();
        }

        private void UpdateDirectionVisibility()
        {
            if (PnlConflictPolicy == null || ChkMirrorDelete == null) return; // 防御：控件树未完成时不处理
            var twoWay = CmbDirection.SelectedIndex == 2;
            var backup = CmbDirection.SelectedIndex is 3 or 4;
            var oneWay = CmbDirection.SelectedIndex is 0 or 1;
            PnlConflictPolicy.Visibility = twoWay ? Visibility.Visible : Visibility.Collapsed;
            // 备份方向本就单向纯语义（永不回写源），严格镜像开关无意义 → 置灰；镜像单向正常可用
            ChkStrictMirror.Visibility = oneWay || backup ? Visibility.Visible : Visibility.Collapsed;
            ChkStrictMirror.IsEnabled = !backup;
            ChkStrictMirror.ToolTip = backup
                ? "备份方向本身就是单向覆盖语义（源新才覆盖、永不回写源），严格镜像开关不适用"
                : "单向任务有效。默认关：源和目标都修改过时，保留较新版本（不反向覆盖源）；开启后目标侧的修改永远被源覆盖，行为等同 GoodSync 的严格镜像";
            // 单向：镜像删除=盲删；双向：镜像删除=删除传播（快照确认才删）
            ChkMirrorDelete.Content = twoWay
                ? "删除传播：一侧删除的文件同步删除另一侧副本（快照确认后才删，不会盲删）"
                : backup
                    ? "清理目标多余文件：源里已删除的文件同步删除目标副本（关 = 目标保留多余文件，备份内容只增不减）"
                    : "镜像删除：源里已删除的文件同步删除目标副本（关 = 目标保留多余文件）";
        }

        private void UpdateTriggerVisibility()
        {
            if (PnlInterval == null || TxtDebounce == null) return; // 防御：控件树未完成时不处理
            PnlInterval.Visibility = CmbTrigger.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            PnlSchedule.Visibility = CmbTrigger.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
            var rt = CmbTrigger.SelectedIndex == 1;
            LblDebounce.Visibility = TxtDebounce.Visibility = rt ? Visibility.Visible : Visibility.Collapsed;
        }

        private void CmbTrigger_Changed(object sender, SelectionChangedEventArgs e) => UpdateTriggerVisibility();

        private void ChkSchedDaily_Changed(object sender, RoutedEventArgs e)
        {
            if (PnlSchedWeek != null)
                PnlSchedWeek.IsEnabled = ChkSchedDaily?.IsChecked != true;
        }

        private void BrowseLeft_Click(object sender, RoutedEventArgs e) => Browse(TxtLeft);
        private void BrowseRight_Click(object sender, RoutedEventArgs e) => Browse(TxtRight);

        private void Browse(TextBox target)
        {
            // 系统原生文件夹选择器（.NET 8 WPF OpenFolderDialog，IFileDialog 封装）。
            // 曾因提权进程首弹极慢自建 TreeView 选择器，但观感与系统不一致，按用户要求换回原生
            var suggest = SuggestStart(target);
            var dlg = new OpenFolderDialog
            {
                Title = target == TxtLeft ? "选择左侧文件夹" : "选择右侧文件夹",
                InitialDirectory = Directory.Exists(suggest) ? suggest : null
            };
            if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
                target.Text = dlg.FolderName;
        }

        private string SuggestStart(TextBox target)
        {
            foreach (var p in new[] { target.Text, TxtLeft.Text, TxtRight.Text })
            {
                var t = p?.Trim();
                if (!string.IsNullOrWhiteSpace(t) && Directory.Exists(t)) return t;
            }
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(JobName)) { Warn("任务名称不能为空"); return; }
            if (string.IsNullOrWhiteSpace(LeftPath) || string.IsNullOrWhiteSpace(RightPath)) { Warn("两侧文件夹路径都必须填写"); return; }
            if (string.Equals(LeftPath.TrimEnd('\\'), RightPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            { Warn("两侧不能是同一个文件夹"); return; }
            // 嵌套检查：一侧是另一侧的子目录时，同步会把目标内容拷进目标自身，无限膨胀
            try
            {
                var l = Path.GetFullPath(LeftPath).TrimEnd('\\') + "\\";
                var r = Path.GetFullPath(RightPath).TrimEnd('\\') + "\\";
                if (l.StartsWith(r, StringComparison.OrdinalIgnoreCase) || r.StartsWith(l, StringComparison.OrdinalIgnoreCase))
                { Warn("两侧文件夹不能互相嵌套（一侧包含另一侧会导致同步时自我复制）。请把目标选到被包含侧之外。"); return; }
                // 程序目录重叠（H-4）：任务侧覆盖程序目录时，同步中写入的 db/版本库/日志文件
                // 立刻成为新差异，形成永不收敛的自反馈循环；MirrorDelete 还可能镜像删除对侧程序数据
                var appRoot = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\') + "\\";
                if (l.StartsWith(appRoot, StringComparison.OrdinalIgnoreCase) || r.StartsWith(appRoot, StringComparison.OrdinalIgnoreCase)
                    || appRoot.StartsWith(l, StringComparison.OrdinalIgnoreCase) || appRoot.StartsWith(r, StringComparison.OrdinalIgnoreCase))
                { Warn("任务文件夹不能与程序所在目录重叠（同步程序数据会自我循环产生差异）。请把程序或任务文件夹移开后重试。"); return; }
            }
            catch (Exception ex) { Warn("路径无效: " + ex.Message); return; }
            // Schedule 校验：非每天必须至少勾一个星期
            if (Trigger == TriggerType.Schedule && ScheduleSpec == null)
            { Warn("定时（指定时刻）需要选择「每天」或至少勾选一个星期"); return; }
            // D1 保存确认：自动裁决（时间/大小优先）且无任何败者保留手段（版本保留关且非冲突副本）
            // → 败者内容将被直接丢弃，弹一次确认（可不再提示）；手动裁决不弹（人自己拍板）
            if (Direction == SyncDirection.TwoWay
                && ConflictPolicy is ConflictPolicy.NewestMtime or ConflictPolicy.LargestSize
                && VersionKeepCount == 0 && !AppSettings.SkipConflictLoseWarn)
            {
                var r = MessageBox.Show(this,
                    "当前组合下（自动裁决 + 版本保留 0 代），冲突败者的内容会被直接丢弃。\n\n" +
                    "建议：版本保留设为 N 代，或冲突策略改选「冲突副本 / 手动裁决」。\n\n" +
                    "[是] 继续保存并不再提示　[否] 继续保存（下次仍提示）　[取消] 返回修改",
                    "败者内容将丢失", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (r == MessageBoxResult.Cancel) return;
                if (r == MessageBoxResult.Yes) AppSettings.SkipConflictLoseWarn = true;
            }
            DialogResult = true;
        }

        private void Warn(string msg) => MessageBox.Show(this, msg, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
