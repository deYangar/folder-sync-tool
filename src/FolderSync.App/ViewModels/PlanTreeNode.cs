using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Media;
using FolderSync.Core;
using FolderSync.App.Services;

namespace FolderSync.App.ViewModels;

/// <summary>树形对照表节点（平移 WPF 版）：目录节点带聚合计数，叶子节点挂 PlanEntry。
/// 平铺进 DataGrid（IsExpanded 控制可见行），MVVM 化后 Icon 换 IImage、画刷换 IBrush。</summary>
public class PlanTreeNode : INotifyPropertyChanged
{
    /// <summary>合成的“无差异”行标记（完整树里两侧一致的文件，动作列留空、不计筛选）。</summary>
    public const string NoDiffMarker = "__nodiff__";

    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public int Depth { get; init; }
    public PlanEntry? Entry { get; set; }      // 叶子（文件）或目录自身的动作条目
    public List<PlanTreeNode> Children { get; } = new();

    private IImage? _icon;
    /// <summary>类型图标（内嵌矢量集）；文件夹恒文件夹图标。INPC 供设置切换后刷新。</summary>
    public IImage Icon
    {
        get => _icon ?? TreeIcons.Default;
        set { _icon = value; Raise(nameof(Icon)); }
    }

    public bool ChildrenLoaded = true;

    public bool IsLeaf => Entry != null && !Entry.IsDirectory;
    public bool IsFolderLike => Entry == null || Entry.IsDirectory;
    public bool HasChildren => Children.Count > 0 || (IsFolderLike && !ChildrenLoaded);

    private bool _expanded = true;
    public bool IsExpanded
    {
        get => _expanded;
        set { _expanded = value; Raise(nameof(IsExpanded)); }
    }

    // ---- 子树聚合计数（仅统计将实际执行的动作；跳过项不计）----
    public int AggCreateRight, AggUpdateRight, AggDeleteRight;
    public int AggCreateLeft, AggUpdateLeft, AggDeleteLeft;
    public int AggConflict;
    public int AggMove;

    /// <summary>缩进宽度（Grid 平铺行首 spacer；WPF GridLength → double px）。</summary>
    public double IndentWidth => Depth * 15;

    // ---- 叶子：双侧属性转发 ----
    public string LeftSizeText => Entry != null ? Entry.LeftSizeText : "（文件夹）";
    public string RightSizeText => Entry != null ? Entry.RightSizeText : "（文件夹）";
    public string LeftMtimeText => Entry != null ? Entry.LeftMtimeText : "（文件夹）";
    public string RightMtimeText => Entry != null ? Entry.RightMtimeText : "（文件夹）";
    public string NoteText
    {
        get
        {
            if (Entry != null)
            {
                if (Entry.Note == NoDiffMarker) return "无差异";
                return Entry.Note;
            }
            return AggConflict > 0 ? $"⚠ {AggConflict} 项冲突待裁决" : "";
        }
    }

    public bool IsConflictRow => Entry?.Action == SyncAction.Conflict;
    public bool IsSkippedRow => Entry is { Action: SyncAction.None } && Entry.Note != NoDiffMarker;

    /// <summary>行名着色（跳过=灰）：DynamicResource 时代码画刷缓存，MVVM 下直接给字符串类名，
    /// 视图侧 classes 绑定换色（主题切换自动跟）。</summary>
    public string NameClasses => IsSkippedRow ? "skipped" : "";

    // ---- 动作图标（叶子）：字形 + 颜色类（主题感知由视图样式承接） ----
    public string LeftActionGlyph => ActionVisual(Entry).lg;
    public string LeftActionClass => ActionVisual(Entry).lc;
    public string RightActionGlyph => ActionVisual(Entry).rg;
    public string RightActionClass => ActionVisual(Entry).rc;

    internal static (string lg, string lc, string rg, string rc) ActionVisual(PlanEntry? e)
    {
        if (e == null) return ("", "", "", "");
        if (e.Action == SyncAction.None && e.Note == NoDiffMarker) return ("", "", "", "");
        // C2 反向执行：按重打后的动作渲染（箭头翻转、方向色互换；Create 反向=对侧无文件删本侧，显示删除）
        var a = e.UserOverride == UserOverrideKind.Reverse
            ? e.Action switch
            {
                SyncAction.UpdateRight => SyncAction.UpdateLeft,
                SyncAction.UpdateLeft => SyncAction.UpdateRight,
                SyncAction.CreateRight => SyncAction.DeleteLeft,
                SyncAction.CreateLeft => SyncAction.DeleteRight,
                _ => e.Action
            }
            : e.Action;
        return a switch
        {
            // 源侧 ● 绿（不动/发射方），目标侧方向箭头：绿=新建 蓝=覆盖
            SyncAction.CreateRight => ("●", "green", "→", "green"),
            SyncAction.UpdateRight => ("●", "green", "→", "blue"),
            SyncAction.CreateLeft => ("←", "green", "●", "green"),
            SyncAction.UpdateLeft => ("←", "blue", "●", "green"),
            SyncAction.DeleteRight => ("–", "", "✕", "red"),
            SyncAction.DeleteLeft => ("✕", "red", "–", ""),
            SyncAction.Conflict => ("⚠", "orange", "⚠", "orange"),
            SyncAction.Move => ("⇄", "teal", "⇄", "teal"),
            _ => ("–", "", "–", ""),
        };
    }

    // ---- 目录聚合徽标 ----
    public string AggLeftInText => $"← {AggCreateLeft + AggUpdateLeft}";
    public string AggLeftDelText => $" ✕ {AggDeleteLeft}";
    public string AggRightInText => $"→ {AggCreateRight + AggUpdateRight}";
    public string AggRightDelText => $" ✕ {AggDeleteRight}";
    public string AggConflictText => $" ⚠ {AggConflict}";
    public string AggMoveText => $" ⇄ {AggMove}";
    public bool HasLeftIn => AggCreateLeft + AggUpdateLeft > 0;
    public bool HasLeftDel => AggDeleteLeft > 0;
    public bool HasRightIn => AggCreateRight + AggUpdateRight > 0;
    public bool HasRightDel => AggDeleteRight > 0;
    public bool HasConflict => AggConflict > 0;
    public bool HasMove => AggMove > 0;
    /// <summary>目录行有徽章（聚合>0 才显示徽章区）。</summary>
    public bool HasAnyAgg => HasLeftIn || HasLeftDel || HasRightIn || HasRightDel || HasConflict || HasMove;

    /// <summary>行级样式类：冲突行橙底 / 跳过行灰字（视图 DataGridRow 样式选择器消费）。</summary>
    public string RowClasses => IsConflictRow ? "conflict" : "";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

    /// <summary>任务列表项：包 SyncEngine，状态点/状态行文案实时刷新（订阅 StateChanged，Detach 防泄漏）。
    /// 线程契约（参考#2）：引擎在后台线程直接 Raise PropertyChanged——当前依赖 Avalonia 的
    /// 隐式线程编排（非 UI 线程的绑定更新被调度回 UI）兜底，属框架隐式行为，勿在无此
    /// 兜底的宿主里复用本类；显式 Dispatcher.Post 化留待实际出现错帧时再做。</summary>
public class EngineItem : INotifyPropertyChanged
{
    public SyncEngine Engine { get; }
    public SyncJob Job => Engine.Job;
    public string StatusText => $"{(Job.Enabled ? Job.Trigger switch
    {
        TriggerType.Realtime => "实时监听",
        TriggerType.Interval => $"定时 {FormatInterval(Job.IntervalSeconds)}",
        TriggerType.Schedule => string.IsNullOrWhiteSpace(Job.ScheduleSpec)
            ? "定时" : $"定时 {ScheduleSpec.DescribeNext(Job.ScheduleSpec, DateTime.Now)}",
        _ => "手动"
    } : "已停用")} · {Engine.StatusText}";

    internal static string FormatInterval(int seconds) =>
        seconds >= 3600 && seconds % 3600 == 0 ? $"{seconds / 3600} 小时"
        : seconds >= 60 && seconds % 60 == 0 ? $"{seconds / 60} 分钟"
        : $"{seconds} 秒";

    /// <summary>状态点样式类（主题感知色）：disabled=灰 / media=灰蓝 / running=绿 / warn=橙 / idle=蓝。</summary>
    public string StatusClasses
    {
        get
        {
            if (!Job.Enabled) return "st-disabled";
            if (Engine.Status == JobStatus.WaitingMedia) return "st-media";
            if (Engine.IsRunning) return "st-run";
            if (Engine.LastConflictCount > 0 || Engine.LastWarning != null || Engine.LastError != null) return "st-warn";
            return "st-idle";
        }
    }

    private readonly Action? _detach;

    public EngineItem(SyncEngine engine)
    {
        Engine = engine;
        void Handler(SyncEngine _) => Raise();
        engine.StateChanged += Handler;
        // 列表刷新每次 new 新 EngineItem 包同一引擎——不退订则旧项被事件强引用永不回收，
        // 且第 N 次刷新后一次 StateChanged 触发 N×2 次 PropertyChanged（越用越卡）
        _detach = () => engine.StateChanged -= Handler;
    }

    /// <summary>退订引擎事件（列表刷新前调用，防订阅泄漏）。</summary>
    public void Detach() => _detach?.Invoke();

    public void Raise()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusClasses)));
    }
    public override string ToString() => $"{Job.Name} · {StatusText}";
    public event PropertyChangedEventHandler? PropertyChanged;
}
