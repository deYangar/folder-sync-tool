using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FolderSync.App.Services;
using FolderSync.Core;

namespace FolderSync.App.ViewModels;

/// <summary>任务编辑 VM（WPF JobEditWindow 平移）：全部表单字段双向 + 回显 + ApplyTo 落库。</summary>
public class JobEditViewModel : ViewModelBase
{
    private readonly SyncJob? _orig;

    public JobEditViewModel(SyncJob? job)
    {
        _orig = job;
        SchedHours = Enumerable.Range(0, 24).Select(h => h.ToString("D2")).ToArray();
        SchedMinutes = Enumerable.Range(0, 60).Select(m => m.ToString("D2")).ToArray();
        if (job != null)
        {
            Name = job.Name; LeftPath = job.LeftPath; RightPath = job.RightPath;
            DirectionIndex = job.Direction switch
            {
                SyncDirection.MirrorRightToLeft => 1,
                SyncDirection.TwoWay => 2,
                SyncDirection.BackupLeftToRight => 3,
                SyncDirection.BackupRightToLeft => 4,
                _ => 0
            };
            ConflictPolicyIndex = job.ConflictPolicy switch
            {
                ConflictPolicy.LargestSize => 1,
                ConflictPolicy.Manual => 2,
                ConflictPolicy.ConflictCopy => 3,
                _ => 0
            };
            TriggerIndex = job.Trigger switch
            {
                TriggerType.Realtime => 1,
                TriggerType.Interval => 2,
                TriggerType.Schedule => 3,
                _ => 0
            };
            if (ScheduleSpec.Parse(job.ScheduleSpec) is { } ps)
            {
                SchedHourIndex = Math.Clamp(ps.Time.h, 0, 23);
                SchedMinuteIndex = Math.Clamp(ps.Time.m, 0, 59);
                if (ps.DaysMask != null)
                {
                    SchedDaily = false;
                    Day1 = ((ps.DaysMask.Value >> 1) & 1) == 1;
                    Day2 = ((ps.DaysMask.Value >> 2) & 1) == 1;
                    Day3 = ((ps.DaysMask.Value >> 3) & 1) == 1;
                    Day4 = ((ps.DaysMask.Value >> 4) & 1) == 1;
                    Day5 = ((ps.DaysMask.Value >> 5) & 1) == 1;
                    Day6 = ((ps.DaysMask.Value >> 6) & 1) == 1;
                    Day7 = ((ps.DaysMask.Value >> 7) & 1) == 1;
                }
            }
            if (job.IntervalSeconds >= 3600 && job.IntervalSeconds % 3600 == 0)
            { IntervalText = (job.IntervalSeconds / 3600).ToString(System.Globalization.CultureInfo.InvariantCulture); IntervalUnitIndex = 0; }
            else if (job.IntervalSeconds >= 60 && job.IntervalSeconds % 60 == 0)
            { IntervalText = (job.IntervalSeconds / 60).ToString(System.Globalization.CultureInfo.InvariantCulture); IntervalUnitIndex = 1; }
            else
            { IntervalText = job.IntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture); IntervalUnitIndex = 2; }
            DebounceText = job.DebounceSeconds.ToString();
            ExcludeText = job.ExcludePatterns;
            VersionKeepText = job.VersionKeepCount.ToString();
            StrictMirror = job.StrictMirror;
            DeltaSync = job.DeltaSync;
            AutoRetry = job.AutoRetry;
            MoveDetect = job.MoveDetect;
            CopyVerify = job.CopyVerify;
            DeepVerifyIndex = job.DeepVerify switch { 1 => 1, 2 => 2, _ => 0 };
            CopyWorkersIndex = job.CopyWorkers switch { 1 => 0, 3 => 2, 4 => 3, _ => 1 };
            MirrorDelete = job.MirrorDelete;
            DeleteToRecycleBin = job.DeleteToRecycleBin;
            Enabled = job.Enabled;
        }
        RaiseAll();
    }

    private void RaiseAll()
    {
        Raise(nameof(ShowInterval)); Raise(nameof(ShowSchedule)); Raise(nameof(ShowDebounce));
        Raise(nameof(ShowConflictPolicy)); Raise(nameof(StrictMirrorEnabled)); Raise(nameof(MirrorDeleteText));
        Raise(nameof(StrictMirrorTip));
    }

    public string Name { get; set; } = "";
    // 浏览按钮走 VM→UI 回填：无通知则文本框不刷新（2026-09-16 实测 bug）
    private string _leftPath = "";
    public string LeftPath { get => _leftPath; set => Set(ref _leftPath, value); }
    private string _rightPath = "";
    public string RightPath { get => _rightPath; set => Set(ref _rightPath, value); }
    public string IntervalText { get; set; } = "2";
    public string DebounceText { get; set; } = "10";
    public string ExcludeText { get; set; } = "Thumbs.db; ._gstmp; .DS_Store";
    public string VersionKeepText { get; set; } = "5";

    private int _directionIndex;
    public int DirectionIndex
    {
        get => _directionIndex;
        set { _directionIndex = value; Raise(); RaiseAll(); }
    }
    private int _triggerIndex;
    public int TriggerIndex
    {
        get => _triggerIndex;
        set { _triggerIndex = value; Raise(); RaiseAll(); }
    }
    private int _conflictPolicyIndex;
    public int ConflictPolicyIndex { get => _conflictPolicyIndex; set { _conflictPolicyIndex = value; Raise(); } }
    private int _intervalUnitIndex;
    public int IntervalUnitIndex { get => _intervalUnitIndex; set { _intervalUnitIndex = value; Raise(); } }
    private int _deepVerifyIndex;
    public int DeepVerifyIndex { get => _deepVerifyIndex; set { _deepVerifyIndex = value; Raise(); } }
    private int _copyWorkersIndex;
    public int CopyWorkersIndex { get => _copyWorkersIndex; set { _copyWorkersIndex = value; Raise(); } }

    public bool ShowInterval => TriggerIndex == 2;
    public bool ShowSchedule => TriggerIndex == 3;
    public bool ShowDebounce => TriggerIndex == 1;
    public bool ShowConflictPolicy => DirectionIndex == 2;
    public bool StrictMirrorVisible => DirectionIndex is 0 or 1 or 3 or 4;
    public bool StrictMirrorEnabled => DirectionIndex is not (3 or 4);   // 备份方向单向纯语义，开关不适用
    public string MirrorDeleteText => DirectionIndex switch
    {
        2 => "删除传播：一侧删除的文件同步删除另一侧副本（快照确认后才删，不会盲删）",
        3 => "清理目标多余文件：源里已删除的文件同步删除目标副本（关 = 目标保留多余文件，备份内容只增不减）",
        4 => "清理目标多余文件：源里已删除的文件同步删除目标副本（关 = 目标保留多余文件，备份内容只增不减）",
        _ => "镜像删除：源里已删除的文件同步删除目标副本（关 = 目标保留多余文件）"
    };
    public string StrictMirrorTip => !StrictMirrorEnabled
        ? "备份方向本身就是单向覆盖语义（源新才覆盖、永不回写源），严格镜像开关不适用"
        : "单向任务有效。默认关：源和目标都修改过时，保留较新版本（不反向覆盖源）；开启后目标侧的修改永远被源覆盖，行为等同 GoodSync 的严格镜像";

    // Schedule
    public string[] SchedHours { get; }
    public string[] SchedMinutes { get; }
    private int _schedHourIndex = 3;
    public int SchedHourIndex { get => _schedHourIndex; set { _schedHourIndex = value; Raise(); } }
    private int _schedMinuteIndex;
    public int SchedMinuteIndex { get => _schedMinuteIndex; set { _schedMinuteIndex = value; Raise(); } }
    private bool _schedDaily = true;
    public bool SchedDaily { get => _schedDaily; set { _schedDaily = value; Raise(); Raise(nameof(WeekEnabled)); } }
    public bool WeekEnabled => !SchedDaily;
    public bool Day1 { get; set; } = true;
    public bool Day2 { get; set; }
    public bool Day3 { get; set; }
    public bool Day4 { get; set; }
    public bool Day5 { get; set; }
    public bool Day6 { get; set; }
    public bool Day7 { get; set; }

    public bool StrictMirror { get; set; }
    public bool DeltaSync { get; set; } = true;
    public bool AutoRetry { get; set; } = true;
    public bool MoveDetect { get; set; } = true;
    public bool CopyVerify { get; set; }
    public bool MirrorDelete { get; set; }
    public bool DeleteToRecycleBin { get; set; } = true;
    public bool Enabled { get; set; } = true;

    // ---- 导出（对话框关闭时读取） ----
    public SyncDirection Direction => DirectionIndex switch
    {
        1 => SyncDirection.MirrorRightToLeft,
        2 => SyncDirection.TwoWay,
        3 => SyncDirection.BackupLeftToRight,
        4 => SyncDirection.BackupRightToLeft,
        _ => SyncDirection.MirrorLeftToRight
    };
    public ConflictPolicy ConflictPolicy => ConflictPolicyIndex switch
    {
        1 => ConflictPolicy.LargestSize,
        2 => ConflictPolicy.Manual,
        3 => ConflictPolicy.ConflictCopy,
        _ => ConflictPolicy.NewestMtime
    };
    public TriggerType Trigger => TriggerIndex switch
    {
        1 => TriggerType.Realtime,
        2 => TriggerType.Interval,
        3 => TriggerType.Schedule,
        _ => TriggerType.Manual
    };

    /// <summary>Schedule 规格串；非法组合 null（保存校验拦截）。</summary>
    public string? ScheduleSpecValue
    {
        get
        {
            if (Trigger != TriggerType.Schedule) return null;
            var hm = $"{SchedHours[SchedHourIndex]}:{SchedMinutes[SchedMinuteIndex]}";
            if (SchedDaily) return $"daily {hm}";
            var days = new[] { (Day1, 1), (Day2, 2), (Day3, 3), (Day4, 4), (Day5, 5), (Day6, 6), (Day7, 7) }
                .Where(x => x.Item1).Select(x => x.Item2).ToList();
            return days.Count == 0 ? null : $"weekly {string.Join(",", days)} {hm}";
        }
    }

    public int IntervalSeconds
    {
        get
        {
            if (!double.TryParse(IntervalText, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v) || v <= 0) v = 2;
            var mul = IntervalUnitIndex switch { 1 => 60, 2 => 1, _ => 3600 };
            return Math.Max(10, (int)Math.Round(v * mul));   // 任何单位最小 10 秒
        }
    }

    public int DebounceSeconds => int.TryParse(DebounceText, System.Globalization.CultureInfo.InvariantCulture, out var i)
        ? Math.Max(1, i) : 10;

    /// <summary>版本保留数：解析失败回默认 5（G-2：兜底必须朝保守方向——归 0 等于关闭败者保护，
    /// 手滑输 "5。" 后所有冲突败者直接销毁不入库；非法输入同时被 Validate 拦下不允许保存）。</summary>
    public int VersionKeepCount => int.TryParse(VersionKeepText.Trim(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var n)
        ? Math.Clamp(n, 0, 999) : 5;

    /// <summary>表单 → 落库对象（一次性全量，防手工赋值漏项）。</summary>
    public void ApplyTo(SyncJob j)
    {
        j.Name = Name.Trim();
        j.LeftPath = LeftPath.Trim();
        j.RightPath = RightPath.Trim();
        j.Direction = Direction;
        j.Trigger = Trigger;
        j.ScheduleSpec = ScheduleSpecValue;
        j.IntervalSeconds = IntervalSeconds;
        j.DebounceSeconds = DebounceSeconds;
        j.ExcludePatterns = ExcludeText.Trim();
        j.MirrorDelete = MirrorDelete;
        j.DeleteToRecycleBin = DeleteToRecycleBin;
        j.StrictMirror = StrictMirror;
        j.ConflictPolicy = ConflictPolicy;
        j.VersionKeepCount = VersionKeepCount;
        j.DeltaSync = DeltaSync;
        j.AutoRetry = AutoRetry;
        j.MoveDetect = MoveDetect;
        j.CopyVerify = CopyVerify;
        j.DeepVerify = DeepVerifyIndex switch { 1 => 1, 2 => 2, _ => 0 };
        j.CopyWorkers = CopyWorkersIndex switch { 0 => 1, 2 => 3, 3 => 4, _ => 2 };
        j.Enabled = Enabled;
    }

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name.Trim())) return "任务名称不能为空";
        if (string.IsNullOrWhiteSpace(LeftPath.Trim()) || string.IsNullOrWhiteSpace(RightPath.Trim()))
            return "两侧文件夹路径都必须填写";
        if (string.Equals(LeftPath.Trim().TrimEnd('\\', '/'), RightPath.Trim().TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            return "两侧不能是同一个文件夹";
        try
        {
            var sep = Path.DirectorySeparatorChar;
            var l = Path.GetFullPath(LeftPath).TrimEnd('\\', '/') + sep;
            var r = Path.GetFullPath(RightPath).TrimEnd('\\', '/') + sep;
            if (l.StartsWith(r, StringComparison.OrdinalIgnoreCase) || r.StartsWith(l, StringComparison.OrdinalIgnoreCase))
                return "两侧文件夹不能互相嵌套（一侧包含另一侧会导致同步时自我复制）。请把目标选到被包含侧之外。";
            var appRoot = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\', '/') + sep;
            var dataRoot = Path.GetFullPath(Core.Platform.AppPaths.Root).TrimEnd('\\', '/') + sep;
            foreach (var root in new[] { appRoot, dataRoot })
                if (l.StartsWith(root, StringComparison.OrdinalIgnoreCase) || r.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    || root.StartsWith(l, StringComparison.OrdinalIgnoreCase) || root.StartsWith(r, StringComparison.OrdinalIgnoreCase))
                    return "任务文件夹不能与程序/数据目录重叠（同步程序数据会自我循环产生差异）。请把程序或任务文件夹移开后重试。";
        }
        catch (Exception ex) { return "路径无效: " + ex.Message; }
        if (Trigger == TriggerType.Schedule && ScheduleSpecValue == null)
            return "定时（指定时刻）需要选择「每天」或至少勾选一个星期";
        if (!int.TryParse(VersionKeepText.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out _))
            return "版本保留必须是整数（0=关闭，N=保留最近 N 代）";
        return null;
    }
}

/// <summary>设置窗口 VM。</summary>
public class SettingsViewModel : ViewModelBase
{
    public bool ShowIcons { get => AppSettings.ShowIcons; set { AppSettings.ShowIcons = value; Raise(); } }
    public int ThemeModeIndex { get => AppSettings.ThemeMode; set { ThemeService.ThemeMode = value; Raise(); } }

    public bool RememberWindow
    {
        get => AppSettings.RememberWindow;
        set
        {
            AppSettings.RememberWindow = value;
            if (!value) AppSettings.ClearWindowBounds();   // 关闭即清记录：再打开不会跳回很久前的位置
            Raise();
        }
    }

    public bool AutostartAvailable => Core.Platform.Platform.Autostart != null;

    private bool _autoEnabled;
    private bool _autoMinimized = true;
    private bool _autoLoaded;
    private bool _autoTouched;

    public bool AutostartEnabled
    {
        get => _autoEnabled;
        set
        {
            _autoEnabled = value;
            Raise();
            if (!_autoLoaded || _autoTouched) return;
            _autoTouched = true;
            try { if (value) Core.Platform.Platform.Autostart?.Enable(AutostartMinimized); else Core.Platform.Platform.Autostart?.Disable(); }
            catch (Exception ex) { _ = Dialogs.AlertAsync(null, "自启设置失败: " + ex.Message, "FolderSync"); }
        }
    }

    public bool AutostartMinimized
    {
        get => _autoMinimized;
        set
        {
            _autoMinimized = value;
            Raise();
            if (!_autoLoaded || _autoTouched) return;
            _autoTouched = true;
            try { Core.Platform.Platform.Autostart?.SetMinimized(value); }   // 未启用时静默跳过：下次勾上「开机自启」一并写入
            catch (Exception ex) { _ = Dialogs.AlertAsync(null, "自启模式设置失败: " + ex.Message, "FolderSync"); }
        }
    }

    public string BaselineInfo { get; private set; } = "";

    public SettingsViewModel()
    {
        RefreshBaselineInfo();
        _ = LoadAutostartAsync();
    }

    private async Task LoadAutostartAsync()
    {
        var auto = Core.Platform.Platform.Autostart;
        if (auto == null) { AutostartEnabled = false; return; }
        var (enabled, minimized) = await Task.Run(() =>
        {
            auto.EnsureUpToDate();
            return (auto.IsEnabled(), auto.IsMinimized());
        });
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_autoTouched) return;
            _autoLoaded = false;
            AutostartEnabled = enabled;
            AutostartMinimized = minimized;
            _autoLoaded = true;
        });
    }

    public void RefreshBaselineInfo()
    {
        var n = BaselineStore.CountAllEntries();
        BaselineInfo = n > 0 ? $"当前 {n} 条" : "当前无索引";
        Raise(nameof(BaselineInfo));
    }

    public async Task ClearBaselineAsync(Func<Task<bool>> confirm)
    {
        var total = BaselineStore.CountAllEntries();
        if (total == 0) return;
        if (!await confirm()) return;
        await Task.Run(BaselineStore.ClearAllSites);
        RefreshBaselineInfo();
    }
}

/// <summary>运行历史 VM（WPF HistoryWindow 平移）。</summary>
public class HistoryViewModel : ViewModelBase
{
    public sealed class Row
    {
        public RunRecord Record { get; init; } = null!;
        public string StartedText => Record.StartedAt.ToString("MM-dd HH:mm:ss");
        public string TriggerText => RunReport.TriggerZh(Record.Trigger);
        public string StatusText => RunReport.StatusZh(Record.Status);
        public string CountsText => $"{Record.CopiedFiles}/{Record.DeletedFiles}/{Record.SkippedFiles}/{Record.FailedFiles}";
        public string BytesText => Record.BytesCopied > 0 ? Executor.FormatSize(Record.BytesCopied) : "";
        public string DeltaText => Record.DeltaSavedBytes > 0 ? $"省 {Executor.FormatSize(Record.DeltaSavedBytes)}" : "";
        public string SpeedText => Record.AvgSpeedBytesPerSec is > 0 ? $"{Executor.FormatSize(Record.AvgSpeedBytesPerSec.Value)}/s" : "";
        public string DurationText => Record.FinishedAt.HasValue && Record.FinishedAt > Record.StartedAt
            ? (Record.FinishedAt.Value - Record.StartedAt).TotalSeconds.ToString("F0") + "s" : "";
        public string ErrorText => Record.ErrorMessage ?? "";
    }

    public SyncEngine Engine { get; }
    private readonly Db _db;
    public ObservableCollection<Row> Rows { get; } = new();
    public Row? SelectedRow { get; set; }

    public HistoryViewModel(SyncEngine engine, Db db)
    {
        Engine = engine;
        _db = db;
        Reload();
    }

    public void Reload()
    {
        Rows.Clear();
        foreach (var r in _db.GetRecentRuns(Engine.Job.Id, 100))
            Rows.Add(new Row { Record = r });
    }

    public void OpenReport()
    {
        var path = SelectedRow?.Record is { Id: > 0 } r ? RunReport.ReportPathOf(r) : null;
        if (path != null && File.Exists(path))
            Core.Platform.Platform.Shell.OpenPath(path);
    }

    public void OpenFailed()
    {
        if (SelectedRow?.Record is not { } r) return;
        var path = Path.Combine(Core.Platform.AppPaths.FailedDir(r.JobId), $"{r.Id}.txt");
        if (File.Exists(path))
            Core.Platform.Platform.Shell.OpenPath(path);
    }

    /// <summary>CSV 单元格公式注入防护（参考#5）：以 = + - @ 开头的自由文本在 Excel 会被当公式执行，
    /// 前置单引号强制按文本处理。</summary>
    private static string CsvSafe(string s) =>
        s.Length > 0 && (s[0] == '=' || s[0] == '+' || s[0] == '-' || s[0] == '@') ? "'" + s : s;

    public string ExportCsv(string file)
    {
        var lines = new[] { "开始,触发,状态,复制,删除,跳过,失败,字节,增量省,耗时s,均速,错误" }
            .Concat(Rows.Select(r => $"{r.StartedText},{r.TriggerText},{r.StatusText},{r.Record.CopiedFiles},{r.Record.DeletedFiles},{r.Record.SkippedFiles},{r.Record.FailedFiles},{r.Record.BytesCopied},{r.Record.DeltaSavedBytes},{r.DurationText},{r.SpeedText},\"{CsvSafe(r.ErrorText).Replace("\"", "\"\"")}\""));
        File.WriteAllLines(file, lines, System.Text.Encoding.UTF8);
        return file;
    }
}

/// <summary>版本库浏览器 VM（WPF VersionBrowserWindow 平移）。</summary>
public class VersionBrowserViewModel : ViewModelBase
{
    public sealed class FileRow
    {
        public string RelPath { get; init; } = "";
        public int VersionCount { get; init; }
        public long TotalSize { get; init; }
        public string TotalSizeText => Executor.FormatSize(TotalSize);
        public string LatestTsShort { get; init; } = "";
        public override string ToString() => RelPath;
    }

    public sealed class VersionRow
    {
        public int Id { get; init; }
        public string Ts { get; init; } = "";
        public long Size { get; init; }
        public string SizeText => Executor.FormatSize(Size);
        public string TsShort { get; init; } = "";
        public string MtimeShort { get; init; } = "";
        public override string ToString() => TsShort;
    }

    private readonly SyncJob _job;
    public long EngineJobId => _job.Id;
    public ObservableCollection<FileRow> Files { get; } = new();
    public ObservableCollection<VersionRow> Versions { get; } = new();
    public FileRow? SelectedFile { get; set; }
    public VersionRow? SelectedVersion { get; set; }

    public string[] SideOptions { get; }
    private int _sideIndex;
    public int SideIndex { get => _sideIndex; set { _sideIndex = value; Raise(); _ = ReloadAsync(); } }
    public string CurrentSide => SideIndex == 1 ? _job.RightPath : _job.LeftPath;

    public string StatsText { get; private set; } = "";
    public string VersionHeaderText { get; private set; } = "版本";
    public string StatusText { get; private set; } = "";
    public string WindowTitle => $"版本库 — {_job.Name}";

    private int _reloadGen;

    public VersionBrowserViewModel(SyncJob job)
    {
        _job = job;
        SideOptions = new[] { $"左侧：{job.LeftPath}", $"右侧：{job.RightPath}" };
        _ = ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        var gen = ++_reloadGen;
        var side = CurrentSide;
        var jobId = _job.Id;
        StatusText = "载入中…"; Raise(nameof(StatusText));
        var (stats, files) = await Task.Run(() =>
        {
            if (!VersionStore.Exists(side)) return (null, new List<StoredFile>());
            return (VersionStore.Stats(side), VersionStore.ListFiles(side, jobId));
        });
        if (gen != _reloadGen) return;
        StatsText = stats == null
            ? "（该侧尚无版本库）"
            : $"{stats.VersionCount} 版 · 逻辑 {Executor.FormatSize(stats.LogicalBytes)} · 落盘 {Executor.FormatSize(stats.StoredBytes)} · 省了 {stats.DedupRatio:P0}";
        Files.Clear();
        foreach (var f in files)
            Files.Add(new FileRow { RelPath = f.RelPath, VersionCount = f.VersionCount, TotalSize = f.TotalSize, LatestTsShort = FormatTs(f.LatestTs) });
        Versions.Clear();
        VersionHeaderText = "版本";
        StatusText = Files.Count == 0
            ? "该侧暂无版本记录（开启任务「版本保留」且发生过覆盖/删除后生成）"
            : $"共 {Files.Count} 个文件的历史版本";
        Raise(nameof(StatsText)); Raise(nameof(VersionHeaderText)); Raise(nameof(StatusText));
        await OnFileSelectedAsync();
    }

    public async Task OnFileSelectedAsync()
    {
        if (SelectedFile is not { } f) return;
        var gen = _reloadGen;
        var side = CurrentSide;
        var versions = await Task.Run(() => VersionStore.ListVersions(side, _job.Id, f.RelPath));
        if (gen != _reloadGen) return;
        Versions.Clear();
        foreach (var v in versions)
            Versions.Add(new VersionRow
            {
                Id = v.Id, Ts = v.Ts, Size = v.Size,
                TsShort = FormatTs(v.Ts), MtimeShort = v.MtimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            });
        VersionHeaderText = $"版本 — {f.RelPath}（{Versions.Count}）";
        StatusText = "选择版本后点「还原到原位置」或「另存到…」";
        Raise(nameof(VersionHeaderText)); Raise(nameof(StatusText));
    }

    private static string FormatTs(string ts) =>
        DateTime.TryParse(ts, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var t)
            ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : ts;

    public (string relPath, VersionRow version)? Selection =>
        SelectedFile != null && SelectedVersion != null ? (SelectedFile.RelPath, SelectedVersion) : null;

    public async Task RestoreAsync(Avalonia.Controls.Window owner, string relPath, int versionId, string sizeText)
    {
        var side = CurrentSide;
        var dest = Path.Combine(side, relPath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(dest) && !await E2eSilent.Confirm(owner,
            $"目标已存在：\n{dest}\n\n用所选版本（{sizeText}）覆盖它？", "还原到原位置")) return;
        StatusText = $"还原中… {relPath}"; Raise(nameof(StatusText));
        try
        {
            await Task.Run(() => VersionStore.RestoreVersion(side, versionId, dest));
            StatusText = $"已还原: {dest}"; Raise(nameof(StatusText));
            await E2eSilent.Info(owner, $"已还原到原位置：\n{dest}", "版本库");
        }
        catch (Exception ex)
        {
            StatusText = $"还原失败: {ex.Message}"; Raise(nameof(StatusText));
            await E2eSilent.Alert(owner, $"还原失败：{ex.Message}", "版本库");
        }
    }

    public async Task DeleteVersionAsync(Avalonia.Controls.Window owner)
    {
        if (Selection is not { } sel) { StatusText = "先在左侧选文件、右侧选版本"; Raise(nameof(StatusText)); return; }
        if (!await E2eSilent.Confirm(owner,
            $"永久删除该版本（{FormatTs(sel.version.Ts)}，{sel.version.SizeText}）？\n删除后不可恢复（其余版本不受影响）。",
            "删除此版本")) return;
        var side = CurrentSide;
        StatusText = "删除中…"; Raise(nameof(StatusText));
        try
        {
            await Task.Run(() => VersionStore.DeleteVersion(side, sel.version.Id));
            StatusText = "版本已删除（孤儿块已回收）"; Raise(nameof(StatusText));
            await ReloadAsync();
        }
        catch (Exception ex) { StatusText = $"删除失败: {ex.Message}"; Raise(nameof(StatusText)); }
    }

    public async Task ClearAllAsync(Avalonia.Controls.Window owner)
    {
        var side = CurrentSide;
        var stats = await Task.Run(() => VersionStore.Stats(side));
        if (stats == null || stats.VersionCount == 0) { StatusText = "当前侧没有可清空的版本"; Raise(nameof(StatusText)); return; }
        if (!await E2eSilent.Confirm(owner,
            $"永久清空当前侧的版本库？\n{side}\n共 {stats.VersionCount} 个版本、{Executor.FormatSize(stats.StoredBytes)} 占用。\n（不影响两侧已同步的文件内容，删除后不可恢复）",
            "清空全部版本")) return;
        StatusText = "清空中…"; Raise(nameof(StatusText));
        try
        {
            await Task.Run(() => VersionStore.ClearAll(side));
            StatusText = "版本库已清空"; Raise(nameof(StatusText));
            await ReloadAsync();
        }
        catch (Exception ex) { StatusText = $"清空失败: {ex.Message}"; Raise(nameof(StatusText)); }
    }
}

/// <summary>上次失败明细 VM。</summary>
public class FailedItemsViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _owner;
    public SyncEngine Engine { get; }
    public string Title { get; }
    public System.Collections.Generic.IReadOnlyList<FailedItem> Items => Engine.LastFailedItems;
    public string Tip => "重试按当前文件系统真实状态重建：源在则复制，源没了则跳过";

    public FailedItemsViewModel(MainWindowViewModel owner, SyncEngine engine)
    {
        _owner = owner;
        Engine = engine;
        Title = $"上次失败明细 — {engine.Job.Name}（共 {engine.LastFailedTotal} 条，明细 {engine.LastFailedItems.Count} 条）";
    }

    public async Task RetryAsync(Avalonia.Controls.Window dlg, Action<bool> setBusy)
    {
        setBusy(false);
        await _owner.RetryFailedAsync(Engine, setBusy);
        dlg.Close();
    }

    public void OpenLogFile() => _owner.Log("明细文件功能见日志");
}
