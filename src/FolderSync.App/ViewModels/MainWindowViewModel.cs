using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using FolderSync.App.Services;
using FolderSync.App.Views;
using FolderSync.Core;

namespace FolderSync.App.ViewModels;

/// <summary>主窗口 ViewModel（WPF MainWindow.xaml.cs 全量平移 + MVVM 化）：
/// 任务列表/详情卡片/筛选/树对照表/裁决/进度/日志。窗口托盘与行点击交互在 MainWindow 视图侧，
/// 经本类公开方法进入。全部 UI 状态经 UIThread Dispatcher 收敛。</summary>
public class MainWindowViewModel : ViewModelBase
{
    private readonly Db _db;
    private readonly JobManager _manager;
    public ObservableCollection<EngineItem> Items { get; } = new();

    private EngineItem? _selected;
    public EngineItem? Selected
    {
        get => _selected;
        set
        {
            if (Equals(_selected, value)) return;
            _selected = value;
            Raise(nameof(Selected));
            OnSelectionChanged();
        }
    }

    // ---------- 详情卡片 ----------
    public string LeftPath { get; private set; } = "未选择任务";
    public string RightPath { get; private set; } = "—";
    public string LeftMeta { get; private set; } = "";
    public string RightMeta { get; private set; } = "";
    public string DirArrow { get; private set; } = "→";
    public string DirText { get; private set; } = "";

    // ---------- 筛选 ----------
    private string _filter = "all";
    public string Filter
    {
        get => _filter;
        private set
        {
            _filter = value;
            Raise();
            Raise(nameof(FilterIsAll)); Raise(nameof(FilterIsChanged)); Raise(nameof(FilterIsCreate));
            Raise(nameof(FilterIsUpdate)); Raise(nameof(FilterIsDelete)); Raise(nameof(FilterIsConflict));
            Raise(nameof(FilterIsSkip));
        }
    }
    public bool FilterIsAll { get => Filter == "all"; private set => Filter = value ? "all" : Filter; }
    public bool FilterIsChanged => Filter == "changed";
    public bool FilterIsCreate => Filter == "create";
    public bool FilterIsUpdate => Filter == "update";
    public bool FilterIsDelete => Filter == "delete";
    public bool FilterIsConflict => Filter == "conflict";
    public bool FilterIsSkip => Filter == "skip";
    public int CntAll { get; private set; }
    public int CntChanged { get; private set; }
    public int CntCreate { get; private set; }
    public int CntUpdate { get; private set; }
    public int CntDelete { get; private set; }
    public int CntConflict { get; private set; }
    public int CntSkip { get; private set; }

    public void SetFilter(string f)
    {
        Filter = f;
        RebuildVisibleRows();
    }

    // ---------- 树对照表 ----------
    public ObservableCollection<PlanTreeNode> Rows { get; } = new();
    public PlanTreeNode? SelectedRow { get; set; }

    private List<PlanEntry>? _lastPlan;
    private DateTime _lastPlanAt = DateTime.MinValue;   // _lastPlan 的生成时间（陈旧计划防护用）
    private PlanTreeNode? _root;
    private readonly Dictionary<PlanEntry, SyncAction> _originalAction = new();
    private Dictionary<string, PlanEntry> _planByPath = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, FileEntry>? _scanLeft;
    private IReadOnlyDictionary<string, FileEntry>? _scanRight;
    private string _scanLeftRoot = "", _scanRightRoot = "";
    private Dictionary<string, (SortedSet<string> dirs, SortedSet<string> files)>? _childIndex;
    private Dictionary<string, int[]>? _aggByFolder;
    private const int AggSlotChanged = 8;
    private const int AggSlots = 9;
    private int _totalEntries;
    private bool _autoPreviewing;

    // ---------- 冲突栏 ----------
    private bool _hasConflicts;
    public bool HasConflicts { get => _hasConflicts; private set => Set(ref _hasConflicts, value); }

    // ---------- 进度 ----------
    // 四步骤态：wait/active/done（字形与色由视图样式类渲染）；Classes 需 string[]（Avalonia 绑定契约）
    public string[] StepState { get; } = { "", "", "", "" };
    public string Step0Classes => StepState[0];
    public string Step1Classes => StepState[1];
    public string Step2Classes => StepState[2];
    public string Step3Classes => StepState[3];
    public string[] StepTexts { get; } = { "① 扫描左侧", "② 扫描右侧", "③ 对比差异", "④ 同步执行" };
    private static readonly string[] StepNames = { "扫描左侧", "扫描右侧", "对比差异", "同步执行" };
    public double ProgressValue { get; private set; }
    public bool ProgressIndeterminate { get; private set; }
    public string ProgressColorClass { get; private set; } = "blue";
    public string ProgressText { get; private set; } = "就绪";
    public string StatusText { get; private set; } = "就绪";

    // ---------- 日志 ----------
    private readonly List<string> _logLines = new();
    private const int LogMaxLines = 500;
    public string LogText => string.Join("\n", _logLines);

    // ---------- 命令 ----------
    public RelayCommand CmdNew { get; }
    public RelayCommand CmdEdit { get; }
    public RelayCommand CmdDelete { get; }
    public AsyncRelayCommand CmdAnalyze { get; }
    public AsyncRelayCommand CmdSync { get; }
    public RelayCommand CmdCancel { get; }
    public AsyncRelayCommand CmdVerify { get; }
    public RelayCommand CmdToggleWatch { get; }
    public RelayCommand CmdOpenVersions { get; }
    public RelayCommand CmdFailedItems { get; }
    public RelayCommand CmdHistory { get; }
    public RelayCommand CmdSettings { get; }
    public RelayCommand CmdKeepLeft { get; }
    public RelayCommand CmdKeepRight { get; }
    public RelayCommand CmdSkipConflict { get; }
    public AsyncRelayCommand CmdResolveRun { get; }

    // ---------- 托盘与通知 ----------
    public TrayService? Tray { get; set; }   // App 装配后注入
    private string? _pendingReportPath;      // 通知点击待打开的报告
    private readonly NotificationThrottle _notifyThrottle = new(TimeSpan.FromMinutes(5));

    private CancellationTokenSource? _cts;
    private bool _busyButtons;
    public bool BusyButtons { get => _busyButtons; private set => Set(ref _busyButtons, value); }

    private long _shownProgressSeq;   // 已渲染进度轮次序号：终态后丢弃晚到的更小序号事件

    /// <summary>主窗口视图引用（开子窗口的 owner）。</summary>
    internal MainWindow? View { get; set; }

    public MainWindowViewModel()
    {
        _db = new Db();
        _manager = new JobManager(_db);
        _manager.EngineAdded += Hook;
        CmdNew = new RelayCommand(() => _ = EditJobAsync(null));
        CmdEdit = new RelayCommand(() => _ = EditJobAsync(Selected?.Job ?? SelectedOrFirst()?.Job));
        CmdDelete = new RelayCommand(() => _ = DeleteJobAsync());
        CmdAnalyze = new AsyncRelayCommand(() => AnalyzeAsync());
        CmdSync = new AsyncRelayCommand(() => SyncAsync());
        CmdCancel = new RelayCommand(() =>
        {
            _cts?.Cancel();                                   // 手动轮次（UI await 链上的 token）
            SelectedOrFirst()?.Engine.RequestCancel();        // 任何来源的轮次：实时/定时自动触发不经 _cts
        });
        CmdVerify = new AsyncRelayCommand(() => VerifyAsync());
        CmdToggleWatch = new RelayCommand(ToggleWatch);
        CmdOpenVersions = new RelayCommand(() =>
        {
            var item = SelectedOrFirst();
            if (item != null && View != null)
                new VersionBrowserWindow { DataContext = new ViewModels.VersionBrowserViewModel(item.Job) }.Show(View);
        });
        CmdFailedItems = new RelayCommand(() => _ = ShowFailedAsync());
        CmdHistory = new RelayCommand(() =>
        {
            var item = SelectedOrFirst();
            if (item != null && View != null)
                new HistoryWindow { DataContext = new ViewModels.HistoryViewModel(item.Engine, _db) }.Show(View);
        });
        CmdSettings = new RelayCommand(() => _ = OpenSettingsAsync());
        CmdKeepLeft = new RelayCommand(() => ResolveSelected(SyncAction.UpdateRight, "人工：保留左侧 → 复制到右"));
        CmdKeepRight = new RelayCommand(() => ResolveSelected(SyncAction.UpdateLeft, "人工：保留右侧 → 复制到左"));
        CmdSkipConflict = new RelayCommand(() => ResolveSelected(SyncAction.None, "人工：跳过，保持现状"));
        CmdResolveRun = new AsyncRelayCommand(() => ResolveRunAsync());
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            await _manager.LoadAllAndResumeAsync();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                RefreshList();
                Log("启动完成，任务已加载" + (Items.Any(i => i.Job.AutoStart) ? "，自动任务恢复中" : ""));
            });
        }
        catch (Exception ex)
        {
            // G-8：任务库打不开（db 被占用/损坏）不能静默空白——用户看到「任务全丢了」却无线索
            App.WriteCrashLog(ex);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Log($"启动加载失败: {ex.Message}");
                Log($"任务数据目录: {Core.Platform.AppPaths.Root}（数据库被占用或损坏时请先关闭其他实例；崩损日志见同目录 crash.log）");
            });
        }
    }

    /// <summary>S-9：设置对话框必须 await 关闭后再应用图标设置——Avalonia 的 ShowDialog 返回 Task
    /// （WPF 是阻塞式），不等就跑 ApplyIconSetting 用的是旧值，用户改完永远不生效。</summary>
    private async Task OpenSettingsAsync()
    {
        if (View == null) return;
        await new SettingsWindow { DataContext = new ViewModels.SettingsViewModel() }.ShowDialog(View);
        ApplyIconSetting();
    }

    private void Hook(SyncEngine e)
    {
        e.Progress += OnProgress;
        e.RunFinished += OnRunFinished;
        e.StateChanged += OnEngineStateChangedForTray;
    }

    private void OnEngineStateChangedForTray(SyncEngine engine) =>
        Dispatcher.UIThread.Post(() => Tray?.Observe(engine), DispatcherPriority.Background);

    private void RefreshList()
    {
        var sel = Selected?.Job.Id;
        foreach (var i in Items) i.Detach();   // 旧 EngineItem 退订引擎事件，防订阅泄漏
        Items.Clear();
        foreach (var e in _manager.Engines.OrderBy(x => x.Job.Id))
            Items.Add(new EngineItem(e));
        Selected = sel != null ? Items.FirstOrDefault(i => i.Job.Id == sel) : Items.FirstOrDefault();
        if (Selected == null) UpdateDetail();
        else Raise(nameof(Selected));
    }

    // ---------- 详情卡片 ----------

    private void UpdateDetail()
    {
        UpdateWatchButton();
        var item = Selected;
        if (item == null)
        {
            LeftPath = "未选择任务"; RightPath = "—";
            LeftMeta = RightMeta = DirText = "";
            DirArrow = "→";
            Raise(nameof(LeftPath)); Raise(nameof(RightPath)); Raise(nameof(LeftMeta));
            Raise(nameof(RightMeta)); Raise(nameof(DirText)); Raise(nameof(DirArrow));
            return;
        }
        var j = item.Job;
        LeftPath = j.LeftPath;
        RightPath = j.RightPath;
        DirArrow = j.Direction switch
        {
            SyncDirection.TwoWay => "⇄",
            SyncDirection.MirrorRightToLeft or SyncDirection.BackupRightToLeft => "←",
            _ => "→"
        };
        DirText = j.Direction switch
        {
            SyncDirection.TwoWay => "双向同步",
            SyncDirection.MirrorRightToLeft => "右→左镜像",
            SyncDirection.BackupLeftToRight => "左→右备份",
            SyncDirection.BackupRightToLeft => "右→左备份",
            _ => "左→右镜像"
        };
        var st = item.Engine.LastScanStats;
        LeftMeta = st.leftFiles + st.rightFiles > 0
            ? $"{st.leftFiles:N0} 文件 · {Executor.FormatSize(st.leftBytes)}　{DriveHintCached(j.LeftPath)}"
            : "尚未扫描（点「分析」）";
        RightMeta = st.leftFiles + st.rightFiles > 0
            ? $"{st.rightFiles:N0} 文件 · {Executor.FormatSize(st.rightBytes)}　{DriveHintCached(j.RightPath)}"
            : "尚未扫描（点「分析」）";
        var warn = item.Engine.LastWarning != null ? $"　⚠ {item.Engine.LastWarning}" : "";
        RightMeta += warn;
        Raise(nameof(LeftPath)); Raise(nameof(RightPath)); Raise(nameof(LeftMeta));
        Raise(nameof(RightMeta)); Raise(nameof(DirText)); Raise(nameof(DirArrow));
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string hint, DateTime at)> _driveHintCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>DriveInfo/Directory.Exists 是磁盘 IO——网络盘 SMB 超时期间在 UI 线程做会卡数秒。
    /// 5s 缓存命中零 IO 直返；未命中/过期先回旧值（首次为空），后台刷新完再触发详情区重绘。</summary>
    private string DriveHintCached(string path)
    {
        if (_driveHintCache.TryGetValue(path, out var v) && (DateTime.Now - v.at).TotalSeconds < 5)
            return v.hint;
        _ = Task.Run(() =>
        {
            var hint = DriveHint(path);
            _driveHintCache[path] = (hint, DateTime.Now);
            Dispatcher.UIThread.Post(() =>
            {
                if (Selected?.Job is { } j && (j.LeftPath == path || j.RightPath == path))
                    UpdateDetail();
            });
        });
        return v.hint;
    }

    private static string DriveHint(string path)
    {
        try
        {
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path));
            if (root == null || !System.IO.Directory.Exists(path)) return "";
            var di = new System.IO.DriveInfo(root);
            return $"{di.Name.TrimEnd('\\', '/')} 可用 {Executor.FormatSize(di.AvailableFreeSpace)}";
        }
        catch { return ""; }
    }

    // ---------- 日志 ----------

    public void Log(string msg)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _logLines.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
            if (_logLines.Count > LogMaxLines) _logLines.RemoveAt(0);
            Raise(nameof(LogText));
        });
    }

    public void ClearLog() { _logLines.Clear(); Raise(nameof(LogText)); }

    public async Task CopyLogSelectionAsync(string? selection)
    {
        var text = selection;
        if (string.IsNullOrEmpty(text)) text = LogText;
        if (!string.IsNullOrEmpty(text))
        {
            if (View != null) await View.Clipboard!.SetTextAsync(text);
        }
    }

    public void ExportLog()
    {
        try
        {
            var file = System.IO.Path.Combine(Core.Platform.AppPaths.LogsRoot,
                $"log-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            System.IO.File.WriteAllText(file, LogText, System.Text.Encoding.UTF8);
            Log($"日志已导出: {file}");
        }
        catch (Exception ex) { Log("导出失败: " + ex.Message); }
    }

    // ---------- 进度渲染 ----------

    private void BumpProgressSeq(SyncEngine engine) =>
        _shownProgressSeq = Math.Max(_shownProgressSeq, engine.CurrentProgressSeq);

    private void OnProgress(SyncEngine engine, ProgressInfo p)
    {
        // Post 后台优先级：工作线程不再同步等 UI
        Dispatcher.UIThread.Post(() =>
        {
            Tray?.Progress(engine, p);   // 托盘聚合所有运行中任务，不受选中过滤影响
            if (Selected?.Engine != engine) return;
            if (p.RunSeq != 0 && p.RunSeq < _shownProgressSeq) return;   // 过时轮次的晚到事件
            if (p.RunSeq > _shownProgressSeq) _shownProgressSeq = p.RunSeq;
            RenderProgress(p);
        }, DispatcherPriority.Background);
    }

    private void SetStep(int idx, int state)  // 0=wait 1=active 2=done
    {
        StepState[idx] = state switch { 1 => "stepactive", 2 => "stepdone", _ => "" };
        StepTexts[idx] = state == 2 ? $"✓ {StepNames[idx]}" : $"{(char)('①' + idx)} {StepNames[idx]}";
        Raise(nameof(Step0Classes)); Raise(nameof(Step1Classes));
        Raise(nameof(Step2Classes)); Raise(nameof(Step3Classes));
        Raise(nameof(StepTexts));
    }

    private void ResetProgress()
    {
        for (int i = 0; i < 4; i++) SetStep(i, 0);
        ProgressIndeterminate = false;
        ProgressValue = 0;
        ProgressColorClass = BlueCls;
        ProgressText = "就绪";
        Raise(nameof(ProgressIndeterminate)); Raise(nameof(ProgressValue));
        Raise(nameof(ProgressColorClass)); Raise(nameof(ProgressText));
    }

    private void RenderProgress(ProgressInfo p)
    {
        switch (p.Phase)
        {
            case "scan-left":
                SetStep(0, 1); SetStep(1, 0); SetStep(2, 0); SetStep(3, 0);
                RenderScanProgress(p, 0, "①", "左侧");
                break;
            case "scan-right":
                SetStep(0, 2); SetStep(1, 1); SetStep(2, 0); SetStep(3, 0);
                RenderScanProgress(p, 50, "②", "右侧");
                break;
            case "scan":
                SetStep(0, 1);
                RenderScanProgress(p, 0, "", "");
                break;
            case "compare":
                SetStep(0, 2); SetStep(1, 2); SetStep(2, 1); SetStep(3, 0);
                ProgressIndeterminate = true; ProgressColorClass = BlueCls;
                ProgressText = "③ 正在对比两侧差异…";
                Raise(nameof(ProgressIndeterminate)); Raise(nameof(ProgressColorClass)); Raise(nameof(ProgressText));
                break;
            case "copy":
            {
                SetStep(0, 2); SetStep(1, 2); SetStep(2, 2); SetStep(3, 1);
                ProgressIndeterminate = false; ProgressColorClass = GreenCls;
                var bpct = p.TotalBytes > 0 ? p.DoneBytes * 100.0 / p.TotalBytes : 0;
                ProgressValue = bpct;
                var speed = p.SpeedBytesPerSec > 1024 ? $"{Executor.FormatSize(p.SpeedBytesPerSec)}/s" : "";
                var eta = p.Eta.HasValue ? $" · ETA {p.Eta.Value:hh\\:mm\\:ss}" : "";
                ProgressText = $"④ 复制中 {bpct:F0}%（{p.DoneItems}/{p.TotalItems} 项 · " +
                    $"{Executor.FormatSize(p.DoneBytes)}/{Executor.FormatSize(p.TotalBytes)}）{speed}{eta}" +
                    (string.IsNullOrEmpty(p.CurrentItem) ? "" : $" · {TrimPath(p.CurrentItem)}");
                Raise(nameof(ProgressIndeterminate)); Raise(nameof(ProgressColorClass));
                Raise(nameof(ProgressValue)); Raise(nameof(ProgressText));
                break;
            }
            case "delete":
            {
                SetStep(0, 2); SetStep(1, 2); SetStep(2, 2); SetStep(3, 1);
                ProgressIndeterminate = false; ProgressColorClass = OrangeCls;
                var dpct = p.TotalItems > 0 ? p.DoneItems * 100.0 / p.TotalItems : 0;
                ProgressValue = dpct;
                ProgressText = $"④ 镜像删除清理中 {dpct:F0}%（{p.DoneItems}/{p.TotalItems} 项）· {TrimPath(p.CurrentItem)}";
                Raise(nameof(ProgressIndeterminate)); Raise(nameof(ProgressColorClass));
                Raise(nameof(ProgressValue)); Raise(nameof(ProgressText));
                break;
            }
            case "done":
                for (int i = 0; i < 4; i++) SetStep(i, 2);
                ProgressIndeterminate = false; ProgressColorClass = GreenCls; ProgressValue = 100;
                ProgressText = p.CurrentItem;
                Raise(nameof(ProgressIndeterminate)); Raise(nameof(ProgressColorClass));
                Raise(nameof(ProgressValue)); Raise(nameof(ProgressText));
                break;
        }
        StatusText = p.CurrentItem.Length > 120 ? p.CurrentItem[..120] : p.CurrentItem;
        Raise(nameof(StatusText));
    }

    /// <summary>扫描阶段真进度：预数出的文件夹总数按"已扫/总数"走百分比，左右各占半程；
    /// 预数失败回退不确定动画。</summary>
    private void RenderScanProgress(ProgressInfo p, int halfFrom, string step, string sideName)
    {
        var real = p.TotalItems > 0;
        ProgressIndeterminate = !real;
        ProgressColorClass = BlueCls;
        if (real) ProgressValue = halfFrom + Math.Min(100.0, p.Percent) * 0.5;
        var extra = string.IsNullOrEmpty(p.ExtraInfo) ? $"已发现 {p.DoneItems:N0} 项" : p.ExtraInfo;
        var lead = step.Length == 0 ? "正在扫描" : $"{step} 正在扫描";
        ProgressText = $"{lead}{sideName}文件夹… {extra} · {TrimPath(p.CurrentItem)}";
        Raise(nameof(ProgressIndeterminate)); Raise(nameof(ProgressColorClass));
        Raise(nameof(ProgressValue)); Raise(nameof(ProgressText));
    }

    private const string BlueCls = "blue";
    private const string GreenCls = "green";
    private const string OrangeCls = "orange";

    private static string TrimPath(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\\', '/');
        if (s.Length <= 64) return s;
        return "…" + s[^60..];
    }

    private void OnRunFinished(SyncEngine engine, RunRecord r)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _shownProgressSeq = Math.Max(_shownProgressSeq, engine.CurrentProgressSeq);
            Tray?.Finished(engine);
            Log($"{engine.Job.Name}: {r.Trigger} · {r.Status} · 复制 {r.CopiedFiles} · 删除 {r.DeletedFiles} · 失败 {r.FailedFiles} · {Executor.FormatSize(r.BytesCopied)}");
            Selected?.Raise();
            UpdateDetail();
            // 自动轮（realtime/interval/schedule/startup/reconnect）执行过真实变更：正在看的树
            // 与扫描快照已陈旧，重预览刷新——否则实时轮同步新文件后「全部」视图不跟随（咩咩实测）。
            // 手动四轮（manual/resolve/retry/verify）由各自的 UI 流程 ShowPlan，_lastPlan==null 只兜底启动恢复轮。
            bool uiRefreshedPlan = r.Trigger is "manual" or "resolve" or "retry" or "verify";
            if (Selected?.Engine == engine && (_lastPlan == null || !uiRefreshedPlan))
                _ = AutoPreviewAsync();
            NotifyIfNeeded(engine, r);
            var report = RunReport.Generate(engine, r);
            if (report != null && r.Trigger != "manual") Log($"运行报告: {report}");
            else if (report != null) _pendingReportPath = report;
        }, DispatcherPriority.Background);
    }

    private void NotifyIfNeeded(SyncEngine engine, RunRecord r)
    {
        // 通知降级（方案 M2）：托盘气泡在 Avalonia TrayIcon 无等价 API——重要事件进日志 + 状态，
        // 报告路径经 _pendingReportPath 供托盘菜单「打开上次报告」消费
        if (r.Status is "error" or "partial")
        {
            var key = $"{engine.Job.Id}:{r.Status}";
            if (_notifyThrottle.ShouldShow(key))
                Log($"⚠ 【{engine.Job.Name}】{(r.Status == "error" ? $"同步失败：{engine.LastError}" : $"部分失败 {r.FailedFiles} 项，点「上次失败明细」可重试")}");
        }
        else if (engine.LastConflictCount > 0 && _notifyThrottle.ShouldShow($"{engine.Job.Id}:conflict"))
            Log($"⚠ 【{engine.Job.Name}】{engine.LastConflictCount} 项冲突待人工裁决");
    }

    /// <summary>托盘菜单「打开上次运行报告」。</summary>
    public void OpenPendingReport()
    {
        var path = _pendingReportPath;
        _pendingReportPath = null;
        if (path != null && System.IO.File.Exists(path))
            Core.Platform.Platform.Shell.OpenPath(path);
    }

    // ---------- 分析 / 同步 / 校验 ----------

    private EngineItem? SelectedOrFirst() =>
        Selected ?? (Items.Count > 0 ? Selected = Items[0] : null);

    private void SetPlan(List<PlanEntry> plan)
    {
        _lastPlan = plan;
        _lastPlanAt = DateTime.Now;
    }

    /// <summary>自动预览：扫描+对比（不执行、不落历史），填充「全部」视图的完整树。</summary>
    private async Task AutoPreviewAsync()
    {
        if (_autoPreviewing) return;
        _autoPreviewing = true;
        bool retryForNewSelection = false;
        try
        {
            var item = SelectedOrFirst();
            if (item == null) return;
            var engine = item.Engine;
            for (int i = 0; i < 120; i++)   // 等引擎空闲（给启动补跑让路）
            {
                if (!engine.IsRunning) break;
                await Task.Delay(500);
            }
            if (engine.IsRunning) return;
            var (_, plan) = await engine.RunAsync("preview", execute: false, logRun: false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                BumpProgressSeq(engine);
                if (Selected?.Engine != engine)
                {
                    retryForNewSelection = true;
                    return;
                }
                SetPlan(plan);
                CaptureScans(engine);
                ShowPlan(plan, "all");   // 自动预览=「全部」视图（对齐方法注释与切任务重置设计）
                var (c, u, d, m, b) = Differ.Summarize(plan);
                var conflicts = plan.Count(p => p.Action == SyncAction.Conflict);
                SetStep(0, 2); SetStep(1, 2); SetStep(2, 2); SetStep(3, 0);
                ProgressIndeterminate = false; ProgressValue = 0; Raise(nameof(ProgressIndeterminate)); Raise(nameof(ProgressValue));
                ProgressText = plan.Count == 0
                    ? "已加载：两边已一致（无差异）"
                    : $"已加载：差异 {plan.Count} 项（新建 {c} / 更新 {u} / 删除 {d}"
                    + (m > 0 ? $" / 移动 {m}" : "")
                    + (conflicts > 0 ? $" / ⚠ 冲突 {conflicts}" : "")
                    + "）——点「同步」执行";
                Raise(nameof(ProgressText));
            });
        }
        catch (InvalidOperationException) { /* 仍忙，OnRunFinished 兜底 */ }
        catch { }
        finally
        {
            _autoPreviewing = false;
            if (retryForNewSelection) Dispatcher.UIThread.Post(() => _ = AutoPreviewAsync());
        }
    }

    private void CaptureScans(SyncEngine engine)
    {
        _scanLeft = engine.LastLeftScan;
        _scanRight = engine.LastRightScan;
        _scanLeftRoot = engine.Job.LeftPath;
        _scanRightRoot = engine.Job.RightPath;
    }

    public async Task AnalyzeAsync()
    {
        var item = SelectedOrFirst();
        if (item == null) return;
        try
        {
            BusyButtons = true;
            ResetProgress();
            var (_, plan) = await item.Engine.RunAsync("manual", execute: false, logRun: false);
            if (Selected?.Engine != item.Engine) return;   // await 期间切任务：晚到结果不覆盖新视图
            BumpProgressSeq(item.Engine);
            SetPlan(plan);
            CaptureScans(item.Engine);
            _originalAction.Clear();
            ShowPlan(plan);
            var (c, u, d, m, b) = Differ.Summarize(plan);
            var conflicts = plan.Count(p => p.Action == SyncAction.Conflict);
            SetStep(0, 2); SetStep(1, 2); SetStep(2, 2); SetStep(3, 0);
            ProgressIndeterminate = false; ProgressValue = 0;
            ProgressText = $"分析完成：差异 {plan.Count} 项（新建 {c} / 更新 {u} / 删除 {d}" +
                           (m > 0 ? $" / 移动 {m}" : "") +
                           (conflicts > 0 ? $" / ⚠ 冲突 {conflicts}" : "") +
                           $"，待传 {Executor.FormatSize(b)}）——核对后点「同步」执行";
            Raise(nameof(ProgressIndeterminate)); Raise(nameof(ProgressValue)); Raise(nameof(ProgressText));
            Log($"分析 {item.Job.Name}: {plan.Count} 项差异" + (conflicts > 0 ? $"（{conflicts} 冲突）" : ""));
        }
        catch (InvalidOperationException ex) { Log(ex.Message); }
        catch (Exception ex) { Log("分析失败: " + ex.Message); }
        finally { BusyButtons = false; }
    }

    public async Task SyncAsync()
    {
        var item = SelectedOrFirst();
        if (item == null) return;
        // 陈旧计划防护：预览后若自动轮执行过，旧计划作废走完整分析+执行
        if (_lastPlan != null && item.Engine.LastRunAt is { } lastRun && lastRun > _lastPlanAt)
            _lastPlan = null;
        try
        {
            BusyButtons = true;
            Filter = "changed";   // 同步开始即看「所有变更」视图
            _cts = new CancellationTokenSource();
            if (_lastPlan != null)
            {
                // 所见即所执行：同步树里用户勾选/裁决过的计划
                var skipped = _lastPlan.Count(p => p.Action == SyncAction.None);
                var (rec, _) = await item.Engine.ExecutePlanAsync(_lastPlan, _cts.Token);
                Log($"同步完成: {rec.Status}，成功 {rec.CopiedFiles}，跳过 {skipped}，失败 {rec.FailedFiles}"
                    + (rec.DeltaSavedBytes > 0 ? $"，增量省 {Executor.FormatSize(rec.DeltaSavedBytes)}" : ""));
            }
            else
            {
                var (_, plan) = await item.Engine.RunAsync("manual", execute: true, ct: _cts.Token);
                if (Selected?.Engine != item.Engine) return;
                SetPlan(plan);
            }
            if (Selected?.Engine != item.Engine) return;
            BumpProgressSeq(item.Engine);
            // 执行后重新分析展示剩余差异：ExecutePlanAsync 成功后引擎已刷新 Last*Scan（免重扫）
            List<PlanEntry> remain;
            if (item.Engine.LastScanFresh)
            {
                try { remain = item.Engine.ComputePlanFromLastScan(); }
                catch (InvalidOperationException) { var (_, rp) = await item.Engine.RunAsync("manual", execute: false, logRun: false); remain = rp; }
            }
            else
            {
                var (_, rp) = await item.Engine.RunAsync("manual", execute: false, logRun: false);
                remain = rp;
            }
            if (Selected?.Engine != item.Engine) return;
            SetPlan(remain);
            CaptureScans(item.Engine);
            _originalAction.Clear();
            ShowPlan(remain);
        }
        catch (InvalidOperationException ex) { Log(ex.Message); }
        catch (OperationCanceledException) { Log("已取消"); }
        catch (Exception ex) { Log("同步失败: " + ex.Message); }
        finally { BusyButtons = false; _cts?.Dispose(); _cts = null; UpdateConflictUi(); }
    }

    public async Task VerifyAsync()
    {
        var item = SelectedOrFirst();
        if (item == null) return;
        try
        {
            BusyButtons = true;
            ResetProgress();
            _cts = new CancellationTokenSource();
            var (rec, rot) = await item.Engine.VerifyAsync(_cts.Token);
            if (Selected?.Engine != item.Engine) return;
            BumpProgressSeq(item.Engine);
            _scanLeft = _scanRight = null;   // 校验无扫描快照：树退化为纯差异树
            _scanLeftRoot = item.Job.LeftPath;
            _scanRightRoot = item.Job.RightPath;
            _originalAction.Clear();
            SetPlan(rot);
            ShowPlan(rot);
            SetStep(0, 2); SetStep(1, 2); SetStep(2, 2); SetStep(3, 0);
            ProgressIndeterminate = false; ProgressValue = 100;
            Raise(nameof(ProgressIndeterminate)); Raise(nameof(ProgressValue));
            Log(rot.Count == 0
                ? $"深度校验通过: {item.Job.Name}（两侧内容逐文件一致）"
                : $"深度校验: {item.Job.Name} 发现 {rot.Count} 处位腐差异（大小/时间一致但内容不同），已标记待裁决");
        }
        catch (OperationCanceledException) { Log("校验已取消"); }
        catch (InvalidOperationException ex) { Log(ex.Message); }
        catch (Exception ex) { Log("校验失败: " + ex.Message); }
        finally { BusyButtons = false; }
    }

    // ---------- 实时监控开关 ----------

    public bool WatchButtonVisible => Selected?.Job is { Trigger: TriggerType.Realtime };
    public string WatchButtonText => Selected?.Job is { Enabled: true } ? "停止实时监控" : "恢复实时监控";

    private void ToggleWatch()
    {
        var item = Selected;
        var j = item?.Job;
        if (item == null || j == null) return;
        j.Enabled = !j.Enabled;
        _db.UpdateJob(j);
        item.Engine.UpdateJob(j);   // 内部 ApplyTriggers：Enabled=false 卸载 watcher，true 重挂
        item.Raise();
        Raise(nameof(WatchButtonVisible)); Raise(nameof(WatchButtonText));
        Log(j.Enabled
            ? $"已恢复实时监控: {j.Name}"
            : $"已停止实时监控: {j.Name}（这一轮不受影响；重启后保持停用，手动「同步」仍可用）");
    }

    private void UpdateWatchButton()
    {
        Raise(nameof(WatchButtonVisible));
        Raise(nameof(WatchButtonText));
    }

    // ---------- 树形对照表 ----------

    private void ShowPlan(List<PlanEntry> plan, string filter = "changed")
    {
        _planByPath = new Dictionary<string, PlanEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in plan) _planByPath[p.RelativePath] = p;
        BuildTreeIndexes(plan);
        _root = BuildFullTree(plan);
        Filter = filter;
        RebuildVisibleRows();
        UpdateFilterCounts(plan);
        UpdateConflictUi();
        UpdateDetail();
        StartIconPass();   // 树建好即启动系统图标/缩略图后台加载（WPF 版同款纪律）
        var (c, u, d, m, b) = Differ.Summarize(plan);
        var conflicts = plan.Count(p => p.Action == SyncAction.Conflict);
        StatusText = plan.Count == 0
            ? $"两边已一致（无差异）{DateTime.Now:HH:mm:ss}"
            : $"差异 {plan.Count} 项：新建 {c} / 更新 {u} / 删除 {d}"
              + (m > 0 ? $" / 移动 {m}" : "")
              + (conflicts > 0 ? $" / ⚠ 冲突 {conflicts}" : "") + $" · {DateTime.Now:HH:mm:ss}";
        Raise(nameof(StatusText));
        Selected?.Raise();
    }

    private void BuildTreeIndexes(List<PlanEntry> plan)
    {
        var childIndex = new Dictionary<string, (SortedSet<string> dirs, SortedSet<string> files)>(StringComparer.OrdinalIgnoreCase);
        void CollectInto(IReadOnlyDictionary<string, FileEntry> snap)
        {
            foreach (var (key, entry) in snap)
            {
                var lastSlash = key.LastIndexOf('/');
                var parent = lastSlash < 0 ? "" : key[..lastSlash];
                var name = lastSlash < 0 ? key : key[(lastSlash + 1)..];
                if (name.Length == 0) continue;
                if (!childIndex.TryGetValue(parent, out var set))
                    childIndex[parent] = set = (new SortedSet<string>(StringComparer.OrdinalIgnoreCase),
                                                new SortedSet<string>(StringComparer.OrdinalIgnoreCase));
                if (entry.IsDirectory) set.dirs.Add(name);
                else set.files.Add(name);
            }
        }
        if (_scanLeft != null) CollectInto(_scanLeft);
        if (_scanRight != null) CollectInto(_scanRight);
        _childIndex = childIndex;
        _totalEntries = childIndex.Sum(kv => kv.Value.dirs.Count + kv.Value.files.Count);
        BuildAggIndex(plan);
    }

    private void BuildAggIndex(List<PlanEntry> plan)
    {
        _aggByFolder = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var pe in plan)
        {
            var slot = AggSlot(pe.Action);
            if (slot < 0) continue;
            var rel = pe.RelativePath;
            var start = 0;
            int slash;
            while ((slash = rel.IndexOf('/', start)) >= 0)
            {
                var folder = rel[..slash];
                if (!_aggByFolder.TryGetValue(folder, out var c)) _aggByFolder[folder] = c = new int[AggSlots];
                c[slot]++;
                c[AggSlotChanged]++;
                start = slash + 1;
            }
        }
    }

    private PlanTreeNode BuildFullTree(List<PlanEntry> plan)
    {
        var root = new PlanTreeNode { Depth = -1, ChildrenLoaded = false };
        if (_scanLeft == null || _scanRight == null)
            return BuildTree(plan);
        EnsureChildrenLoaded(root);
        // 咩咩要求：所有展示模式默认展开全部行——递归加载整棵树并全部置展开
        LoadAllExpanded(root);
        return root;
    }

    private void LoadAllExpanded(PlanTreeNode node)
    {
        if (!node.ChildrenLoaded) EnsureChildrenLoaded(node);
        foreach (var c in node.Children)
        {
            c.IsExpanded = true;
            if (c.IsFolderLike) LoadAllExpanded(c);
        }
    }

    private void EnsureChildrenLoaded(PlanTreeNode folder)
    {
        if (folder.ChildrenLoaded || _scanLeft == null || _scanRight == null || _childIndex == null) return;
        var prefix = folder.FullPath.Length == 0 ? "" : folder.FullPath + "/";
        var (dirs, files) = _childIndex.TryGetValue(folder.FullPath, out var set)
            ? set : (new SortedSet<string>(StringComparer.OrdinalIgnoreCase),
                     new SortedSet<string>(StringComparer.OrdinalIgnoreCase));

        foreach (var d in dirs)
        {
            var path = prefix + d;
            var node = new PlanTreeNode
            {
                Name = d,
                FullPath = path,
                Depth = folder.Depth + 1,
                ChildrenLoaded = false,
                IsExpanded = false
            };
            if (_planByPath.TryGetValue(path, out var de)) node.Entry = de;
            node.Icon = TreeIcons.ForFolder();
            AggFromPrefix(node, path);
            if (node.Entry != null) AddCount(node, node.Entry.Action);
            folder.Children.Add(node);
        }
        foreach (var f in files)
        {
            var path = prefix + f;
            _scanLeft.TryGetValue(path, out var l);
            _scanRight.TryGetValue(path, out var r);
            PlanEntry pe;
            if (_planByPath.TryGetValue(path, out var diff)) pe = diff;
            else
            {
                pe = new PlanEntry
                {
                    Action = SyncAction.None,
                    RelativePath = path,
                    IsDirectory = false,
                    LeftSize = l?.Size ?? -1,
                    RightSize = r?.Size ?? -1,
                    LeftMtime = l == null ? null : l.MtimeUtc.ToLocalTime(),
                    RightMtime = r == null ? null : r.MtimeUtc.ToLocalTime(),
                    Note = PlanTreeNode.NoDiffMarker
                };
            }
            var ext = System.IO.Path.GetExtension(f);
            folder.Children.Add(new PlanTreeNode
            {
                Name = f,
                FullPath = path,
                Depth = folder.Depth + 1,
                Entry = pe,
                Icon = AppSettings.ShowIcons ? TreeIcons.ForExtension(ext) : TreeIcons.Default,
                ChildrenLoaded = true
            });
        }
        SortChildren(folder);
        folder.ChildrenLoaded = true;
    }

    private void AggFromPrefix(PlanTreeNode node, string folderPath)
    {
        if (_aggByFolder != null && _aggByFolder.TryGetValue(folderPath, out var c)) ApplyAggCounts(node, c);
    }

    private static int AggSlot(SyncAction a) => a switch
    {
        SyncAction.CreateRight => 0,
        SyncAction.UpdateRight => 1,
        SyncAction.DeleteRight => 2,
        SyncAction.CreateLeft => 3,
        SyncAction.UpdateLeft => 4,
        SyncAction.DeleteLeft => 5,
        SyncAction.Conflict => 6,
        SyncAction.Move => 7,
        _ => -1,
    };

    private static void ApplyAggCounts(PlanTreeNode n, int[] c)
    {
        n.AggCreateRight += c[0]; n.AggUpdateRight += c[1]; n.AggDeleteRight += c[2];
        n.AggCreateLeft += c[3]; n.AggUpdateLeft += c[4]; n.AggDeleteLeft += c[5];
        n.AggConflict += c[6]; n.AggMove += c[7];
    }

    private PlanTreeNode BuildTree(List<PlanEntry> plan)
    {
        var root = new PlanTreeNode { Depth = -1 };
        var dirCache = new Dictionary<string, PlanTreeNode>(StringComparer.OrdinalIgnoreCase);

        PlanTreeNode GetDir(string fullPath, int depth)
        {
            if (dirCache.TryGetValue(fullPath, out var d)) return d;
            var idx = fullPath.LastIndexOf('/');
            var name = idx < 0 ? fullPath : fullPath[(idx + 1)..];
            var node = new PlanTreeNode { Name = name, FullPath = fullPath, Depth = depth, Icon = TreeIcons.ForFolder() };
            if (idx < 0) root.Children.Add(node);
            else GetDir(fullPath[..idx], depth - 1).Children.Add(node);
            dirCache[fullPath] = node;
            return node;
        }

        foreach (var pe in plan.OrderBy(p => p.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var parts = pe.RelativePath.Split('/');
            if (pe.IsDirectory)
            {
                var dir = GetDir(pe.RelativePath, parts.Length - 1);
                dir.Entry = pe;
            }
            else
            {
                PlanTreeNode parent = root;
                if (parts.Length > 1)
                    parent = GetDir(string.Join('/', parts.Take(parts.Length - 1)), parts.Length - 2);
                parent.Children.Add(new PlanTreeNode
                {
                    Name = parts[^1],
                    FullPath = pe.RelativePath,
                    Depth = parts.Length - 1,
                    Entry = pe,
                    Icon = pe.Note == PlanTreeNode.NoDiffMarker
                        ? TreeIcons.Default
                        : TreeIcons.ForExtension(System.IO.Path.GetExtension(parts[^1]))
                });
            }
        }
        SortChildren(root);
        foreach (var n in dirCache.Values) SortChildren(n);
        RecomputeAgg(root);
        return root;
    }

    private void RebuildVisibleRows()
    {
        Rows.Clear();
        if (_root == null) { Log("对照表: 尚未构建树"); return; }
        var rows = new List<PlanTreeNode>();
        Flatten(_root, rows, Filter);
        foreach (var r in rows) Rows.Add(r);
        Log($"对照表: 筛选={Filter}，树根子节点 {_root.Children.Count}，可见 {Rows.Count} 行");
    }

    private static bool LeafMatches(PlanEntry e, string filter) => filter switch
    {
        "changed" => e.Action != SyncAction.None,
        "create" => e.Action is SyncAction.CreateLeft or SyncAction.CreateRight,
        "update" => e.Action is SyncAction.UpdateLeft or SyncAction.UpdateRight,
        "delete" => e.Action is SyncAction.DeleteLeft or SyncAction.DeleteRight,
        "conflict" => e.Action == SyncAction.Conflict,
        "skip" => e.Action == SyncAction.None && e.Note != PlanTreeNode.NoDiffMarker,
        _ => true
    };

    private bool SubtreeMatches(PlanTreeNode dir, string filter)
    {
        if (dir.Entry != null && LeafMatches(dir.Entry, filter)) return true;
        if (filter == "skip")
        {
            if (_lastPlan != null)
            {
                var prefix = dir.FullPath + "/";
                foreach (var pe in _lastPlan)
                    if (pe.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && LeafMatches(pe, filter)) return true;
            }
            return false;
        }
        if (_aggByFolder == null) return false;
        if (!_aggByFolder.TryGetValue(dir.FullPath, out var c)) return false;
        return filter switch
        {
            "changed" => c[AggSlotChanged] > 0,
            "create" => c[0] + c[3] > 0,
            "update" => c[1] + c[4] > 0,
            "delete" => c[2] + c[5] > 0,
            "conflict" => c[6] > 0,
            _ => true
        };
    }

    private void Flatten(PlanTreeNode node, List<PlanTreeNode> list, string filter)
    {
        foreach (var c in node.Children)
        {
            if (filter == "all")
            {
                list.Add(c);
                if (c.HasChildren && c.IsExpanded) Flatten(c, list, filter);
            }
            else if (c.IsFolderLike)
            {
                if (SubtreeMatches(c, filter))
                {
                    list.Add(c);
                    FlattenForce(c, list, filter);
                }
            }
            else if (c.Entry != null && LeafMatches(c.Entry, filter))
                list.Add(c);
        }
    }

    private void FlattenForce(PlanTreeNode node, List<PlanTreeNode> list, string filter)
    {
        if (node.IsFolderLike && !node.ChildrenLoaded) EnsureChildrenLoaded(node);
        foreach (var c in node.Children)
        {
            if (c.IsFolderLike)
            {
                if (SubtreeMatches(c, filter))
                {
                    list.Add(c);
                    FlattenForce(c, list, filter);
                }
            }
            else if (c.Entry != null && LeafMatches(c.Entry, filter))
                list.Add(c);
        }
    }

    private static void SortChildren(PlanTreeNode node)
    {
        node.Children.Sort((a, b) =>
        {
            var ad = a.HasChildren || (a.Entry != null && a.Entry.IsDirectory);
            var bd = b.HasChildren || (b.Entry != null && b.Entry.IsDirectory);
            if (ad != bd) return ad ? -1 : 1;
            return string.CompareOrdinal(a.Name.ToLowerInvariant(), b.Name.ToLowerInvariant());
        });
    }

    private void RecomputeAgg(PlanTreeNode node)
    {
        if (!node.ChildrenLoaded) return;
        node.AggCreateRight = node.AggUpdateRight = node.AggDeleteRight = 0;
        node.AggCreateLeft = node.AggUpdateLeft = node.AggDeleteLeft = node.AggConflict = 0;
        foreach (var c in node.Children)
        {
            if (c.IsFolderLike)
            {
                RecomputeAgg(c);
                node.AggCreateRight += c.AggCreateRight;
                node.AggUpdateRight += c.AggUpdateRight;
                node.AggDeleteRight += c.AggDeleteRight;
                node.AggCreateLeft += c.AggCreateLeft;
                node.AggUpdateLeft += c.AggUpdateLeft;
                node.AggDeleteLeft += c.AggDeleteLeft;
                node.AggConflict += c.AggConflict;
            }
            else if (c.Entry != null) AddCount(node, c.Entry.Action);
        }
        if (node.Entry != null) AddCount(node, node.Entry.Action);
    }

    private static void AddCount(PlanTreeNode n, SyncAction a)
    {
        switch (a)
        {
            case SyncAction.CreateRight: n.AggCreateRight++; break;
            case SyncAction.UpdateRight: n.AggUpdateRight++; break;
            case SyncAction.DeleteRight: n.AggDeleteRight++; break;
            case SyncAction.CreateLeft: n.AggCreateLeft++; break;
            case SyncAction.UpdateLeft: n.AggUpdateLeft++; break;
            case SyncAction.DeleteLeft: n.AggDeleteLeft++; break;
            case SyncAction.Conflict: n.AggConflict++; break;
            case SyncAction.Move: n.AggMove++; break;
        }
    }

    private void UpdateFilterCounts(List<PlanEntry> plan)
    {
        CntAll = _scanLeft != null && _scanRight != null ? _totalEntries : plan.Count;
        CntChanged = plan.Count(p => p.Action != SyncAction.None);
        CntCreate = plan.Count(p => p.Action is SyncAction.CreateLeft or SyncAction.CreateRight);
        CntUpdate = plan.Count(p => p.Action is SyncAction.UpdateLeft or SyncAction.UpdateRight);
        CntDelete = plan.Count(p => p.Action is SyncAction.DeleteLeft or SyncAction.DeleteRight);
        CntConflict = plan.Count(p => p.Action == SyncAction.Conflict);
        CntSkip = plan.Count(p => p.Action == SyncAction.None);
        Raise(nameof(CntAll)); Raise(nameof(CntChanged)); Raise(nameof(CntCreate));
        Raise(nameof(CntUpdate)); Raise(nameof(CntDelete)); Raise(nameof(CntConflict)); Raise(nameof(CntSkip));
    }

    /// <summary>行点击（点整行任意位置）：文件夹=展开/收起；行内动作按钮由视图侧直接路由（不冲突）。</summary>
    public void RowClicked(PlanTreeNode node)
    {
        if (node.IsFolderLike && node.HasChildren)
        {
            node.IsExpanded = !node.IsExpanded;
            if (node.IsExpanded && !node.ChildrenLoaded)
            {
                EnsureChildrenLoaded(node);
                RecomputeAgg(node);
            }
            RebuildVisibleRows();
        }
    }

    /// <summary>展开箭头点击。</summary>
    public void ToggleNode(PlanTreeNode node)
    {
        if (node.IsExpanded && !node.ChildrenLoaded)
        {
            EnsureChildrenLoaded(node);
            RecomputeAgg(node);
        }
        RebuildVisibleRows();
    }

    // ---------- 行内动作：跳过/恢复/裁决 ----------

    public void LeftAction(PlanTreeNode node)
    {
        if (node.Entry != null) ResolveAtNode(node, keepLeft: true);
    }

    public void RightAction(PlanTreeNode node)
    {
        if (node.Entry != null) ResolveAtNode(node, keepLeft: false);
    }

    private void ResolveAtNode(PlanTreeNode node, bool keepLeft)
    {
        var pe = node.Entry!;
        if (pe.Note == PlanTreeNode.NoDiffMarker) return;
        if (pe.Action == SyncAction.Conflict)
        {
            if (keepLeft)
            {
                pe.Action = SyncAction.UpdateRight;
                pe.Note = "人工：保留左侧 → 复制到右";
            }
            else
            {
                pe.Action = SyncAction.UpdateLeft;
                pe.Note = "人工：保留右侧 → 复制到左";
            }
            pe.ManuallyResolved = true;
            Log($"裁决 {pe.RelativePath}: {pe.Note}");
        }
        else if (pe.Action == SyncAction.None)
        {
            if (_originalAction.TryGetValue(pe, out var orig))
            {
                pe.Action = orig;
                pe.Note = "已恢复执行（点图标可再跳过）";
                Log($"恢复执行 {pe.RelativePath}");
            }
        }
        else
        {
            _originalAction[pe] = pe.Action;
            pe.Action = SyncAction.None;
            pe.ManuallyResolved = true;
            pe.UserOverride = UserOverrideKind.Default;   // 跳过优先于反向，一并撤销
            pe.Note = "人工：跳过此项（点图标恢复）";
            Log($"跳过 {pe.RelativePath}");
        }
        AfterPlanTweaked();
    }

    private void AfterPlanTweaked()
    {
        if (_lastPlan != null) BuildAggIndex(_lastPlan);
        if (_root != null) RecomputeAgg(_root);
        if (_lastPlan != null) UpdateFilterCounts(_lastPlan);
        RebuildVisibleRows();
        UpdateConflictUi();
    }

    /// <summary>右键菜单命令构建（跳过/恢复 + 反向）。</summary>
    public (string header, System.Windows.Input.ICommand cmd)[]? ContextCommandsFor(PlanTreeNode node)
    {
        var pe = node.Entry;
        if (pe == null || pe.Note == PlanTreeNode.NoDiffMarker) return null;
        var list = new List<(string, System.Windows.Input.ICommand)>();
        if (pe.Action == SyncAction.None)
        {
            if (_originalAction.ContainsKey(pe))
                list.Add(("恢复执行", new RelayCommand(() => ResolveAtNode(node, false))));
        }
        else if (pe.Action != SyncAction.Conflict)
            list.Add(("跳过此项（本轮不同步该文件）", new RelayCommand(() => ResolveAtNode(node, false))));
        if (pe.UserOverride == UserOverrideKind.Reverse)
            list.Add(("取消反向执行", new RelayCommand(() => ToggleReverse(pe))));
        else if (pe.Action is SyncAction.UpdateRight or SyncAction.UpdateLeft or SyncAction.CreateRight or SyncAction.CreateLeft)
            list.Add((pe.Action is SyncAction.CreateRight or SyncAction.CreateLeft
                ? "反向执行（以对侧为准：删除本侧文件）"
                : "反向执行（以对侧现状覆盖本侧）", new RelayCommand(() => ToggleReverse(pe))));
        return list.Count > 0 ? list.ToArray() : null;
    }

    private void ToggleReverse(PlanEntry pe)
    {
        if (pe.UserOverride == UserOverrideKind.Reverse)
        {
            pe.UserOverride = UserOverrideKind.Default;
            pe.Note = pe.Note.StartsWith("已标记反向") ? "" : pe.Note;
            Log($"取消反向 {pe.RelativePath}");
        }
        else
        {
            pe.UserOverride = UserOverrideKind.Reverse;
            pe.Note = pe.Action is SyncAction.CreateRight or SyncAction.CreateLeft
                ? "已标记反向：对侧无此文件，执行时删除本侧"
                : "已标记反向：执行时以对侧现状覆盖本侧";
            Log($"反向执行 {pe.RelativePath}: {pe.Note}");
        }
        AfterPlanTweaked();
    }

    private void UpdateConflictUi()
    {
        HasConflicts = _lastPlan != null && _lastPlan.Any(p => p.Action == SyncAction.Conflict);
        Selected?.Raise();
    }

    private void ResolveSelected(SyncAction action, string note)
    {
        if (SelectedRow is not { Entry: { Action: SyncAction.Conflict } entry } node)
        {
            Log("请先在对照表中选中一条 ⚠ 冲突 行");
            return;
        }
        entry.Action = action;
        entry.ManuallyResolved = true;
        entry.Note = note;
        AfterPlanTweaked();
        Log($"裁决 {entry.RelativePath}: {note}");
    }

    private async Task ResolveRunAsync()
    {
        var item = Selected;
        if (item == null || _lastPlan == null) return;
        var resolved = _lastPlan.Where(p => p.ManuallyResolved).ToList();
        if (resolved.Count == 0) { Log("尚未裁决任何冲突项"); return; }
        try
        {
            BusyButtons = true;
            _cts = new CancellationTokenSource();
            var (rec, _) = await item.Engine.RunPlanAsync(resolved, _cts.Token);
            Log($"裁决执行: {rec.Status}，成功 {rec.CopiedFiles}，失败 {rec.FailedFiles}");
            if (Selected?.Engine != item.Engine) return;
            BumpProgressSeq(item.Engine);
            List<PlanEntry> remain;
            try { remain = item.Engine.ComputePlanFromLastScan(); }
            catch (InvalidOperationException) { var (_, rp) = await item.Engine.RunAsync("manual", execute: false, logRun: false); remain = rp; }
            if (Selected?.Engine != item.Engine) return;
            SetPlan(remain);
            CaptureScans(item.Engine);
            _originalAction.Clear();
            ShowPlan(remain);
        }
        catch (InvalidOperationException ex) { Log(ex.Message); }
        catch (Exception ex) { Log("裁决执行失败: " + ex.Message); }
        finally { BusyButtons = false; _cts?.Dispose(); _cts = null; UpdateConflictUi(); }
    }

    // ---------- 失败明细 ----------

    private async Task ShowFailedAsync()
    {
        var item = SelectedOrFirst();
        if (item == null) return;
        var engine = item.Engine;
        if (engine.LastFailedItems.Count == 0)
        {
            Log($"任务「{item.Job.Name}」最近一轮没有失败项");
            return;
        }
        if (View != null)
            await new FailedItemsWindow { DataContext = new FailedItemsViewModel(this, engine) }.ShowDialog(View);
    }

    public async Task RetryFailedAsync(SyncEngine engine, Action<bool> setBusy)
    {
        try
        {
            setBusy(true);
            var (rec, plan) = await engine.RetryFailedAsync();
            Log($"重试完成: {rec.Status}，重建 {plan.Count} 项，成功 {rec.CopiedFiles}，失败 {rec.FailedFiles}");
            BumpProgressSeq(engine);
            if (Selected?.Engine != engine) return;
            List<PlanEntry> remain;
            if (engine.LastScanFresh)
            {
                try { remain = engine.ComputePlanFromLastScan(); }
                catch (InvalidOperationException) { var (_, rp) = await engine.RunAsync("manual", execute: false, logRun: false); remain = rp; }
            }
            else
            {
                var (_, rp) = await engine.RunAsync("manual", execute: false, logRun: false);
                remain = rp;
            }
            if (Selected?.Engine != engine) return;
            SetPlan(remain);
            CaptureScans(engine);
            _originalAction.Clear();
            ShowPlan(remain);
        }
        catch (InvalidOperationException ex) { Log(ex.Message); }
        catch (Exception ex) { Log("重试失败: " + ex.Message); }
        finally { setBusy(false); }
    }

    // ---------- 任务 CRUD ----------

    internal async Task EditJobAsync(SyncJob? job)
    {
        if (View == null) return;
        var vm = new JobEditViewModel(job);
        var dlg = new JobEditWindow { DataContext = vm };
        await dlg.ShowDialog(View);
        if (dlg.Result != true) return;
        if (job == null)
        {
            var created = _manager.CreateNew(vm.Name, vm.LeftPath, vm.RightPath);
            vm.ApplyTo(created);
            _db.UpdateJob(created);
            _manager.Add(created);
            RefreshList();
            Log($"新建任务: {created.Name}");
        }
        else
        {
            vm.ApplyTo(job);
            _manager.Save(job);   // 经 JobManager.Save（调度 timer 随配置即时刷新）
            _lastPlan = null;
            RefreshList();
            Log($"任务已更新: {job.Name}");
        }
    }

    private async Task DeleteJobAsync()
    {
        var item = Selected;
        if (item == null || View == null) return;
        if (!await E2eSilent.Confirm(View, $"确定删除任务「{item.Job.Name}」？（不会删除文件夹内容）", "确认"))
            return;
        _manager.Remove(item.Job.Id);
        _lastPlan = null;
        RefreshList();
        Log("任务已删除: " + item.Job.Name);
    }

    private void OnSelectionChanged()
    {
        _lastPlan = null;
        _originalAction.Clear();
        _root = null;
        Rows.Clear();
        SelectedRow = null;   // G-1：旧任务的选中行是孤儿节点，不清则「保留左侧」作用在旧树上、日志却报已裁决
        // 切任务重置筛选到「全部」
        Filter = "all";
        ResetProgress();
        UpdateDetail();
        UpdateConflictUi();
        _ = AutoPreviewAsync();
    }

    /// <summary>按「显示图标」设置重建当前树图标（即时生效）。</summary>
    private void ApplyIconSetting()
    {
        if (_root == null) return;
        var on = AppSettings.ShowIcons;
        _iconGen++;   // 作废在途图标泵：设置切换后旧系统图标不得再覆盖新自绘态
        var nodes = new List<PlanTreeNode>();
        CollectAllNodes(_root, nodes);
        foreach (var n in nodes)
        {
            if (n.IsFolderLike) n.Icon = TreeIcons.ForFolder();
            else n.Icon = on ? TreeIcons.ForExtension(System.IO.Path.GetExtension(n.Name)) : TreeIcons.Default;
        }
        if (on) StartIconPass();
    }

    // ---------- 系统图标/缩略图后台泵（二期增强，移植 WPF 版 StartThumbnailPass 纪律） ----------

    /// <summary>缩略图/系统图标泵代数：新计划/切任务/设置切换时 ++，旧泵发现代数变了立即退出（防旧结果晚到覆盖）。</summary>
    private int _iconGen;

    /// <summary>树建好后后台逐节点取系统缩略图/真实图标（平台 IIconProvider；null=无平台实现或未命中，保持自绘），
    /// UI 线程 Background 优先级投递刷新，不与输入事件竞争。</summary>
    private void StartIconPass()
    {
        if (_root == null || !AppSettings.ShowIcons) return;
        var provider = FolderSync.Core.Platform.Platform.Icons;
        if (provider == null) return;   // 无平台实现（如 CLI/Bcl 兜底）：保持内嵌矢量图标
        var gen = ++_iconGen;
        var nodes = new List<PlanTreeNode>();
        CollectAllNodes(_root, nodes);
        var leftRoot = _scanLeftRoot; var rightRoot = _scanRightRoot;
        if (leftRoot == null && rightRoot == null) return;
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            foreach (var n in nodes)
            {
                if (_iconGen != gen) return;                 // 切任务/新计划/设置切换：旧泵停
                var fullPath = IconFullPathOf(n, leftRoot, rightRoot);
                if (fullPath == null) continue;
                byte[]? bytes;
                try { bytes = provider.GetSystemIcon(fullPath, n.IsFolderLike, 32); }
                catch { continue; }
                if (bytes == null) continue;
                try
                {
                    // DecodeToWidth：图片直读场景原图可能数 MB，统一降采样到图标尺寸控内存
                    var img = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(new MemoryStream(bytes), 32);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (_iconGen == gen) n.Icon = img;
                    }, Avalonia.Threading.DispatcherPriority.Background);
                }
                catch { /* 解码失败/窗口关闭：保持现状 */ }
            }
        });
    }

    /// <summary>节点图标用完整路径：叶子取内容所在侧（左侧优先），文件夹用相对路径拼存在侧根。</summary>
    private static string? IconFullPathOf(PlanTreeNode n, string? leftRoot, string? rightRoot)
    {
        var rel = n.FullPath.Replace('/', System.IO.Path.DirectorySeparatorChar);
        if (n.Entry == null)   // 纯目录条目（无 Entry 的文件夹壳）：左根优先
            return leftRoot != null ? leftRoot + PathSep + rel
                 : rightRoot != null ? rightRoot + PathSep + rel : null;
        var left = leftRoot != null ? leftRoot + PathSep + rel : null;
        var right = rightRoot != null ? rightRoot + PathSep + rel : null;
        return n.Entry.LeftMtime != null ? left ?? right
             : n.Entry.RightMtime != null ? right ?? left
             : left ?? right;
    }

    private static readonly char PathSep = System.IO.Path.DirectorySeparatorChar;

    private static void CollectAllNodes(PlanTreeNode node, List<PlanTreeNode> sink)
    {
        foreach (var c in node.Children)
        {
            sink.Add(c);
            if (c.IsFolderLike) CollectAllNodes(c, sink);
        }
    }

    /// <summary>关闭中拦截（任务在跑/自动任务启用 → 藏进托盘）。
    /// G-6：托盘不可用（Linux GNOME 无 StatusNotifier）时藏进去=主窗口永久失联，直接放行真退出。</summary>
    public bool ShouldHideOnClose() =>
        Services.TrayService.TrayLikelyAvailable
        && _manager.Engines.Any(x => x.IsRunning || x.Job.Enabled && x.Job.Trigger != TriggerType.Manual);

    internal JobManager Manager => _manager;
}
