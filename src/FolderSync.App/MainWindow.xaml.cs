using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using FolderSync.Core;

namespace FolderSync.App
{
    /// <summary>bool → Visibility（false=Collapsed）</summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is Visibility v && v == Visibility.Visible;
    }

    /// <summary>树形对照表节点：目录节点带聚合计数，叶子节点挂 PlanEntry。</summary>
    public class PlanTreeNode : INotifyPropertyChanged
    {
        /// <summary>合成的“无差异”行标记（完整树里两侧一致的文件，动作列留空、不计筛选）。</summary>
        public const string NoDiffMarker = "__nodiff__";

        public string Name { get; init; } = "";
        public string FullPath { get; init; } = "";
        public int Depth { get; init; }
        public PlanEntry? Entry { get; set; }      // 叶子（文件）或目录自身的动作条目
        public List<PlanTreeNode> Children { get; } = new();
        /// <summary>系统关联图标（按扩展名缓存；文件夹=系统文件夹图标）。未显式设置时回退通用文档图标。
        /// 缩略图后台加载完成后替换（INPC 通知刷新）。</summary>
        public ImageSource Icon
        {
            get => _icon ?? MainWindow.DefaultFileIcon;
            set
            {
                _icon = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            }
        }
        private ImageSource? _icon;

        /// <summary>文件实际存在一侧的全路径（exe 真实图标/缩略图提取用；文件行至少一侧存在）。</summary>
        public string? IconFullPath { get; init; }

        /// <summary>完整树懒加载：false 表示文件夹子项尚未从快照加载（仍显示展开箭头）。</summary>
        public bool ChildrenLoaded = true;

        public bool IsLeaf => Entry != null && !Entry.IsDirectory;
        public bool IsFolderLike => Entry == null || Entry.IsDirectory;
        public bool HasChildren => Children.Count > 0 || (IsFolderLike && !ChildrenLoaded);

        private bool _expanded = true;
        public bool IsExpanded
        {
            get => _expanded;
            set { _expanded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded))); }
        }

        // ---- 子树聚合计数（仅统计将实际执行的动作；跳过项不计）----
        public int AggCreateRight, AggUpdateRight, AggDeleteRight;
        public int AggCreateLeft, AggUpdateLeft, AggDeleteLeft;
        public int AggConflict;
        public int AggMove;

        public GridLength IndentWidth => new(Depth * 15);

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
                // 目录行：子树有冲突时提示
                return AggConflict > 0 ? $"⚠ {AggConflict} 项冲突待裁决" : "";
            }
        }

        public bool IsConflictRow => Entry?.Action == SyncAction.Conflict;
        public bool IsSkippedRow => Entry is { Action: SyncAction.None } && Entry.Note != NoDiffMarker;
        private static Brush SkippedGrayBrush => ThemeBrushes.C(ThemeBrushes.K.TextTertiary);
        private static Brush NameDarkBrush => ThemeBrushes.C(ThemeBrushes.K.TextPrimary);
        public Brush NameBrush => IsSkippedRow ? SkippedGrayBrush : NameDarkBrush;

        // ---- 动作图标（叶子）----
        public string LeftActionGlyph => ActionVisual(Entry).lg;
        public Brush LeftActionBrush => ActionVisual(Entry).lb;
        public string RightActionGlyph => ActionVisual(Entry).rg;
        public Brush RightActionBrush => ActionVisual(Entry).rb;

        private static Brush Green => ThemeBrushes.C(ThemeBrushes.K.Success);
        private static Brush Blue => ThemeBrushes.C(ThemeBrushes.K.Accent);
        private static Brush Red => ThemeBrushes.C(ThemeBrushes.K.Danger);
        private static Brush Orange => ThemeBrushes.C(ThemeBrushes.K.Warning);
        private static Brush Gray => ThemeBrushes.C(ThemeBrushes.K.TextTertiary);
        private static Brush Teal => ThemeBrushes.C(ThemeBrushes.K.Teal);   // 移动（青）

        internal static (string lg, Brush lb, string rg, Brush rb) ActionVisual(PlanEntry? e)
        {
            if (e == null) return ("", Gray, "", Gray);
            if (e.Action == SyncAction.None && e.Note == NoDiffMarker) return ("", Gray, "", Gray);  // 无差异行：动作列空白
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
                SyncAction.CreateRight => ("●", Green, "→", Green),
                SyncAction.UpdateRight => ("●", Green, "→", Blue),
                SyncAction.CreateLeft => ("←", Green, "●", Green),
                SyncAction.UpdateLeft => ("←", Blue, "●", Green),
                SyncAction.DeleteRight => ("–", Gray, "✕", Red),
                SyncAction.DeleteLeft => ("✕", Red, "–", Gray),
                SyncAction.Conflict => ("\uE7BA", Orange, "\uE7BA", Orange),
                SyncAction.Move => ("⇄", Teal, "⇄", Teal),   // 移动：目标侧内部 rename（FromPath → 本路径）
                _ => ("–", Gray, "–", Gray),
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

        public event PropertyChangedEventHandler? PropertyChanged;
    }

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

        private static Brush RunGreen => ThemeBrushes.C(ThemeBrushes.K.StatusRun);
        private static Brush WarnOrange => ThemeBrushes.C(ThemeBrushes.K.StatusWarn);
        private static Brush ReadyBlue => ThemeBrushes.C(ThemeBrushes.K.StatusIdle);   // 就绪（启用待命）
        private static Brush DisabledGray => ThemeBrushes.C(ThemeBrushes.K.StatusDisabled); // 仅已停用任务用灰（灰=停用，不再作空闲色）
        private static Brush WaitBlue => ThemeBrushes.C(ThemeBrushes.K.StatusMedia);  // 等待介质：灰蓝

        public Brush StatusBrush
        {
            get
            {
                if (!Job.Enabled) return DisabledGray;
                if (Engine.Status == JobStatus.WaitingMedia) return WaitBlue;
                if (Engine.IsRunning) return RunGreen;
                if (Engine.LastConflictCount > 0 || Engine.LastWarning != null || Engine.LastError != null) return WarnOrange;
                return ReadyBlue;
            }
        }

        private readonly Action? _detach;

        public EngineItem(SyncEngine engine)
        {
            Engine = engine;
            void Handler(SyncEngine _) => Raise();
            engine.StateChanged += Handler;
            // M-2：RefreshList 每次 new 新 EngineItem 包同一引擎——不退订则旧项被事件强引用永不回收，
            // 且第 N 次刷新后一次 StateChanged 触发 N×2 次 PropertyChanged（越用越卡）
            _detach = () => engine.StateChanged -= Handler;
        }

        /// <summary>退订引擎事件（列表刷新前调用，防订阅泄漏）。</summary>
        public void Detach() => _detach?.Invoke();
        public void Raise()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBrush)));
        }
        public override string ToString() => $"{Job.Name} · {StatusText}";
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public partial class MainWindow : Window
    {
        private readonly Db _db;
        private readonly JobManager _manager;
        private readonly List<EngineItem> _items = new();
        private CancellationTokenSource? _cts;
        private EngineItem? Selected => LstJobs.SelectedItem as EngineItem;

        private List<PlanEntry>? _lastPlan;
        private DateTime _lastPlanAt = DateTime.MinValue;   // _lastPlan 的生成时间（陈旧计划防护用）
        private PlanTreeNode? _root;
        private readonly Dictionary<PlanEntry, SyncAction> _originalAction = new();
        private Dictionary<string, PlanEntry> _planByPath = new(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyDictionary<string, FileEntry>? _scanLeft;
        private IReadOnlyDictionary<string, FileEntry>? _scanRight;
        // 快照对应任务的两个根路径（exe 真实图标/缩略图要拼「文件实际存在一侧」的全路径）
        private string _scanLeftRoot = "", _scanRightRoot = "";
        /// <summary>父目录 → (子目录名, 子文件名) 索引，ShowPlan 时单趟构建；EnsureChildrenLoaded O(子项) 查表。
        /// 原来 EnsureChildrenLoaded 每次全量扫两份快照：一侧路径缺失时全树都是差异、逐级自动展开，
        /// 复杂度 O(文件夹数×总条目数)，几万文件直接把 UI 线程卡死在「③对比差异」。</summary>
        private Dictionary<string, (SortedSet<string> dirs, SortedSet<string> files)>? _childIndex;
        /// <summary>文件夹路径 → 子树动作聚合计数（8 槽：0-6 对应 AddCount 的 switch 分支，7=changed 合计），
        /// ShowPlan 单趟按祖先累计。原来 AggFromPrefix/SubtreeMatches 每个树节点全量扫一遍计划：
        /// O(文件夹数×计划数)，一侧路径缺失全树皆差异时切筛选直接卡死 UI（H-3，_childIndex 同款前科）。</summary>
        private Dictionary<string, int[]>? _aggByFolder;
        private const int AggSlotChanged = 8;
        private const int AggSlots = 9;
        /// <summary>两侧并集总条目数（文件+文件夹），BuildTreeIndexes 顺带统计，「全部」徽章显示用。</summary>
        private int _totalEntries;
        private bool _autoPreviewing;

        public MainWindow()
        {
            InitializeComponent();
            GridPlan.ContextMenuOpening += GridPlan_ContextMenuOpening;   // C2 逐行改向右键菜单
            ThemeManager.ThemeChanged += OnThemeChanged;   // D3：主题切换后刷新代码侧画刷的绑定显示
            _db = new Db();
            _manager = new JobManager(_db);
            _manager.EngineAdded += Hook;
            _ = InitAsync();
        }

        private async Task InitAsync()
        {
            await _manager.LoadAllAndResumeAsync();   // Add() 经 EngineAdded 事件挂载，无需重复 Hook（重复订阅会让事件处理两次、日志打双份）
            RefreshList();
            Log("启动完成，任务已加载" + (_items.Any(i => i.Job.AutoStart) ? "，自动任务恢复中" : ""));
        }

        /// <summary>D3：主题切换——XAML DynamicResource 自动刷新；代码侧画刷属性（ThemeBrushes 缓存）
        /// 的绑定求值是单向一次性的，重建可见行/列表强制重读。</summary>
        private void OnThemeChanged()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                RefreshList();
                UpdateDetail();
                if (_root != null) RebuildVisibleRows();
            }));
        }

        private void Hook(SyncEngine e)
        {
            e.Progress += OnProgress;
            e.RunFinished += OnRunFinished;
            e.StateChanged += OnEngineStateChangedForTray;
        }

        /// <summary>托盘指示器跟引擎状态走（运行起止都触发），与选中哪个任务无关。</summary>
        private void OnEngineStateChangedForTray(SyncEngine engine) =>
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => _traySync?.Observe(engine)));

        private void RefreshList()
        {
            var sel = Selected?.Job.Id;
            foreach (var i in _items) i.Detach();   // M-2：旧 EngineItem 退订引擎事件，防订阅泄漏
            _items.Clear();
            foreach (var e in _manager.Engines.OrderBy(x => x.Job.Id))
                _items.Add(new EngineItem(e));
            LstJobs.ItemsSource = null;
            LstJobs.ItemsSource = _items;
            if (sel != null) LstJobs.SelectedItem = _items.FirstOrDefault(i => i.Job.Id == sel);
            else if (_items.Count > 0) LstJobs.SelectedIndex = 0;   // 启动后默认选中第一个任务
            UpdateDetail();
        }

        // ---------- 文件夹卡片 ----------

        // ---------- 系统图标与缩略图（扩展名/路径分别缓存；Freeze 冻结跨线程共享） ----------

        private static readonly Dictionary<string, ImageSource> _extIconCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, ImageSource> _exeIconCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, ImageSource?> _thumbCache = new(StringComparer.OrdinalIgnoreCase);
        private static ImageSource? _folderIconCache;
        private static ImageSource? _defaultFileIconCache;
        /// <summary>按完整路径缓存的条目数上限（P-4）：满了整表清空重建——切任务/大目录树浏览时
        /// 缓存无上限增长会吃光内存，清空只损失一次图标重取。</summary>
        private const int PathCacheLimit = 4096;

        /// <summary>切任务时清掉按完整路径缓存的 exe 图标/缩略图（P-4）。</summary>
        private static void ClearPathCaches()
        {
            lock (_exeIconCache) _exeIconCache.Clear();
            lock (_thumbCache) _thumbCache.Clear();
        }

        internal static ImageSource DefaultFileIcon => _defaultFileIconCache ??= IconForExtension(".txt")!;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private struct SHFILEINFOW
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes, ref SHFILEINFOW psfi, uint cbSizeFileInfo, uint uFlags);

        /// <summary>按扩展名取系统关联图标（SHGFI_USEFILEATTRIBUTES：假文件名即可，文件不存在/删除行同样有效）。
        /// 取 32px 大图标源（UI 显示 20px，缩小比放大清晰）。</summary>
        private static ImageSource? IconForExtension(string extension)
        {
            lock (_extIconCache)
            {
                if (_extIconCache.TryGetValue(extension, out var cached)) return cached;
                const uint SHGFI_ICON = 0x100, SHGFI_USEFILEATTRIBUTES = 0x10;
                const uint FILE_ATTRIBUTE_NORMAL = 0x80;
                var fi = default(SHFILEINFOW);
                // 无扩展名（空串）传 "x" 拿通用文件图标；扩展名带点直接拼假文件名
                var fakeName = extension.Length == 0 ? "x" : "x" + extension;
                var ok = SHGetFileInfoW(fakeName, FILE_ATTRIBUTE_NORMAL, ref fi,
                    (uint)System.Runtime.InteropServices.Marshal.SizeOf<SHFILEINFOW>(),
                    SHGFI_ICON | SHGFI_USEFILEATTRIBUTES);
                ImageSource? src = null;
                if (ok != IntPtr.Zero && fi.hIcon != IntPtr.Zero)
                {
                    try
                    {
                        using var icon = System.Drawing.Icon.FromHandle(fi.hIcon);
                        src = IconToSource(icon);
                    }
                    catch { }
                    finally { DestroyIcon(fi.hIcon); }
                }
                _extIconCache[extension] = src ?? IconToSource(System.Drawing.SystemIcons.Application);
                return _extIconCache[extension];
            }
        }

        /// <summary>exe 按路径取自身内嵌图标（每 exe 各自的；文件不存在回退通用 exe 图标）。</summary>
        private static ImageSource IconForExe(string fullPath)
        {
            lock (_exeIconCache)
            {
                if (_exeIconCache.TryGetValue(fullPath, out var cached)) return cached;
                if (_exeIconCache.Count >= PathCacheLimit) _exeIconCache.Clear();
                const uint SHGFI_ICON = 0x100;
                var fi = default(SHFILEINFOW);
                var ok = SHGetFileInfoW(fullPath, 0, ref fi,
                    (uint)System.Runtime.InteropServices.Marshal.SizeOf<SHFILEINFOW>(), SHGFI_ICON);
                ImageSource? src = null;
                if (ok != IntPtr.Zero && fi.hIcon != IntPtr.Zero)
                {
                    try
                    {
                        using var icon = System.Drawing.Icon.FromHandle(fi.hIcon);
                        src = IconToSource(icon);
                    }
                    catch { }
                    finally { DestroyIcon(fi.hIcon); }
                }
                _exeIconCache[fullPath] = src ?? IconForExtension(".exe") ?? DefaultFileIcon;
                return _exeIconCache[fullPath];
            }
        }

        private static ImageSource FolderIcon()
        {
            if (_folderIconCache != null) return _folderIconCache;
            const uint SHGFI_ICON = 0x100, SHGFI_USEFILEATTRIBUTES = 0x10;
            const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
            var fi = default(SHFILEINFOW);
            var ok = SHGetFileInfoW("x", FILE_ATTRIBUTE_DIRECTORY, ref fi,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf<SHFILEINFOW>(),
                SHGFI_ICON | SHGFI_USEFILEATTRIBUTES);
            if (ok != IntPtr.Zero && fi.hIcon != IntPtr.Zero)
            {
                try
                {
                    using var icon = System.Drawing.Icon.FromHandle(fi.hIcon);
                    _folderIconCache = IconToSource(icon);
                }
                catch { }
                finally { DestroyIcon(fi.hIcon); }
            }
            _folderIconCache ??= IconToSource(System.Drawing.SystemIcons.Application);
            return _folderIconCache;
        }

        // ---------- 缩略图（系统缩略图提供程序：图片/视频/Office 文档等系统里能出缩略图的类型） ----------

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct SIZE { public int cx; public int cy; }

        [System.Runtime.InteropServices.ComImport]
        [System.Runtime.InteropServices.Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [System.Runtime.InteropServices.PreserveSig]
            int GetImage(SIZE size, uint flags, out IntPtr phbm);
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid,
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        /// <summary>取系统缩略图（SIIGBF_THUMBNAILONLY：该类型没有缩略图则返回 null，保持类型图标）。</summary>
        private static ImageSource? ThumbnailFor(string fullPath)
        {
            lock (_thumbCache)
            {
                if (_thumbCache.TryGetValue(fullPath, out var cached)) return cached;
                if (_thumbCache.Count >= PathCacheLimit) _thumbCache.Clear();
                ImageSource? src = null;
                try
                {
                    var iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
                    if (SHCreateItemFromParsingName(fullPath, IntPtr.Zero, ref iid, out var factory) == 0)
                    {
                        const uint SIIGBF_THUMBNAILONLY = 0x8;   // 0x8！0x10 是 INCACHEONLY（首查必 E_FAIL，缩略图从未出过）
                        if (factory.GetImage(new SIZE { cx = 32, cy = 32 }, SIIGBF_THUMBNAILONLY, out var hbm) == 0
                            && hbm != IntPtr.Zero)
                        {
                            try
                            {
                                src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                                    hbm, IntPtr.Zero, Int32Rect.Empty,
                                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                                src.Freeze();
                            }
                            finally { DeleteObject(hbm); }
                        }
                    }
                }
                catch { }
                _thumbCache[fullPath] = src;   // null 也缓存：无缩略图的类型不重复尝试
                return src;
            }
        }

        /// <summary>缩略图后台泵代数：新计划/切任务时 ++，旧泵发现代数变了立即退出（防旧结果晚到覆盖）。</summary>
        private int _thumbGen;

        /// <summary>树建好后启动缩略图后台加载：逐文件取系统缩略图（有则替换类型图标），Dispatcher 回 UI 刷新。</summary>
        private void StartThumbnailPass()
        {
            if (!AppSettings.ShowIcons || _root == null) return;   // 设置关：不提取缩略图（省资源）
            var gen = ++_thumbGen;
            var files = new List<PlanTreeNode>();
            CollectFileNodes(_root, files);
            if (files.Count == 0) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var n in files)
                {
                    if (_thumbGen != gen) return;                 // 切换任务/新计划：旧泵停
                    if (n.IconFullPath == null) continue;
                    var src = ThumbnailFor(n.IconFullPath);
                    if (src == null) continue;
                    try
                    {
                        // Background 优先级（与 OnProgress 同纪律）：几万张图的图标投递不与鼠标/键盘输入同队列竞争
                        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
                        {
                            if (_thumbGen == gen) n.Icon = src;
                        }));
                    }
                    catch { /* 窗口关闭后 Dispatcher 停机 */ }
                }
            });
        }

        private static void CollectFileNodes(PlanTreeNode node, List<PlanTreeNode> sink)
        {
            foreach (var c in node.Children)
            {
                if (c.IsFolderLike) CollectFileNodes(c, sink);
                else sink.Add(c);
            }
        }

        private static ImageSource IconToSource(System.Drawing.Icon icon)
        {
            using var bmp = icon.ToBitmap();
            var h = bmp.GetHbitmap();
            try
            {
                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    h, IntPtr.Zero, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { DeleteObject(h); }
        }

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private void UpdateDetail()
        {
            UpdateWatchButton();   // 选中任务变化/列表刷新/编辑保存后：按钮可见性与文案跟随（含无选中分支）
            var item = Selected;
            if (item == null)
            {
                TxtLeftPath.Text = "未选择任务";
                TxtRightPath.Text = "—";
                TxtLeftPath.ToolTip = TxtRightPath.ToolTip = null;
                TxtLeftMeta.Text = TxtRightMeta.Text = TxtDirText.Text = "";
                TxtDirArrow.Text = "→";
                return;
            }
            var j = item.Job;
            TxtLeftPath.Text = j.LeftPath;
            TxtLeftPath.ToolTip = j.LeftPath;   // 卡片宽度有限长路径必截断：悬停看全路径
            TxtRightPath.Text = j.RightPath;
            TxtRightPath.ToolTip = j.RightPath;
            TxtDirArrow.Text = j.Direction switch
            {
                SyncDirection.TwoWay => "⇄",
                SyncDirection.MirrorRightToLeft or SyncDirection.BackupRightToLeft => "←",
                _ => "→"
            };
            TxtDirText.Text = j.Direction switch
            {
                SyncDirection.TwoWay => "双向同步",
                SyncDirection.MirrorRightToLeft => "右→左镜像",
                SyncDirection.BackupLeftToRight => "左→右备份",
                SyncDirection.BackupRightToLeft => "右→左备份",
                _ => "左→右镜像"
            };
            var st = item.Engine.LastScanStats;
            TxtLeftMeta.Text = st.leftFiles + st.rightFiles > 0
                ? $"{st.leftFiles:N0} 文件 · {Executor.FormatSize(st.leftBytes)}　{DriveHintCached(j.LeftPath)}"
                : "尚未扫描（点「分析」）";
            TxtRightMeta.Text = st.leftFiles + st.rightFiles > 0
                ? $"{st.rightFiles:N0} 文件 · {Executor.FormatSize(st.rightBytes)}　{DriveHintCached(j.RightPath)}"
                : "尚未扫描（点「分析」）";

            var warn = item.Engine.LastWarning != null ? $"　⚠ {item.Engine.LastWarning}" : "";
            TxtRightMeta.Text += warn;
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
                try
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        // 刷新期间选中任务可能已换：只对当前选中且路径一致的重绘
                        if (Selected?.Job is { } j && (j.LeftPath == path || j.RightPath == path))
                            UpdateDetail();
                    }));
                }
                catch { /* 窗口关闭 */ }
            });
            return v.hint;
        }

        private static string DriveHint(string path)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                if (root == null || !Directory.Exists(path)) return "";
                var di = new DriveInfo(root);
                return $"{di.Name.TrimEnd('\\')} 可用 {Executor.FormatSize(di.AvailableFreeSpace)}";
            }
            catch { return ""; }
        }

        private int _logLineCount;

        private void Log(string msg)
        {
            Dispatcher.Invoke(() =>
            {
                // 粘底滚动：用户往上翻历史时不拽回底部
                var atBottom = TxtLog.VerticalOffset >= TxtLog.ExtentHeight - TxtLog.ViewportHeight - 1;
                var selStart = TxtLog.SelectionStart;
                var selLen = TxtLog.SelectionLength;
                TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
                _logLineCount++;
                if (_logLineCount > 500)
                {
                    // 截掉最旧一行；选区若在被截内容之后则平移恢复
                    var nl = TxtLog.Text.IndexOf('\n');
                    if (nl >= 0)
                    {
                        TxtLog.Text = TxtLog.Text[(nl + 1)..];
                        _logLineCount--;
                        if (selStart > nl + 1) selStart -= nl + 1;
                        else { selStart = 0; selLen = 0; }
                    }
                }
                if (selLen > 0) TxtLog.Select(selStart, selLen);
                if (atBottom) TxtLog.ScrollToEnd();
            });
        }

        // ---------- 实时日志：右键复制/清空 ----------

        /// <summary>复制当前选区（任意跨行/行内范围）；无选区不动剪贴板。</summary>
        private void LogCopySelected_Click(object sender, RoutedEventArgs e)
        {
            var text = TxtLog.SelectedText;
            if (!string.IsNullOrEmpty(text)) TrySetClipboard(text);
        }

        private void LogCopyAll_Click(object sender, RoutedEventArgs e) =>
            TrySetClipboard(TxtLog.Text.TrimEnd('\r', '\n'));

        private void LogClear_Click(object sender, RoutedEventArgs e)
        {
            TxtLog.Clear();
            _logLineCount = 0;
        }

        private static void TrySetClipboard(string text)
        {
            try { Clipboard.SetText(text); }
            catch (Exception ex) { MessageBox.Show("复制失败: " + ex.Message, "FolderSync"); }
        }

        // ---------- 进度：阶段步骤条 + 进度条 ----------

        /// <summary>已渲染到 UI 的进度轮次序号：终态渲染后 bump 到引擎当前值，
        /// 晚到的更小序号事件直接丢弃——否则排队的旧"扫描中"会把"完成"盖回去（L-9 异步化的固有竞态）。</summary>
        private long _shownProgressSeq;

        /// <summary>终态文本写入前调用：挡掉该轮残留的排队进度事件（logRun:false 的分析/预览轮没有
        /// RunFinished 事件兜底，必须各完成点自行 bump）。</summary>
        private void BumpProgressSeq(SyncEngine engine) =>
            _shownProgressSeq = Math.Max(_shownProgressSeq, engine.CurrentProgressSeq);

        private void OnProgress(SyncEngine engine, ProgressInfo p)
        {
            // BeginInvoke(Background)：工作线程不再同步等 UI（大文件复制中 CopyFileEx 回调线程
            // 被卡住会拖慢传输）。引擎侧 seq 丢弃之外，已排队的事件在执行时再按序号滤一次
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(() =>
                {
                    _traySync?.Progress(engine, p);   // 托盘聚合所有运行中任务，不受下面选中过滤影响
                    if (Selected?.Engine != engine) return;
                    if (p.RunSeq != 0 && p.RunSeq < _shownProgressSeq) return;   // 过时轮次的晚到事件
                    if (p.RunSeq > _shownProgressSeq) _shownProgressSeq = p.RunSeq;
                    RenderProgress(p);
                }));
        }

        private static Brush StepGray => ThemeBrushes.C(ThemeBrushes.K.TextTertiary);
        private static Brush StepBlue => ThemeBrushes.C(ThemeBrushes.K.Accent);
        private static Brush StepGreen => ThemeBrushes.C(ThemeBrushes.K.StepDone);
        private static Brush StepOrange => ThemeBrushes.C(ThemeBrushes.K.WarningDeep);   // 删除阶段进度条
        private static readonly string[] StepNames = { "扫描左侧", "扫描右侧", "对比差异", "同步执行" };

        private TextBlock[] Steps => new[] { Step1, Step2, Step3, Step4 };

        private void SetStep(int idx, int state)  // 0=wait 1=active 2=done
        {
            var t = Steps[idx];
            if (state == 0) { t.Text = $"{(char)('①' + idx)} {StepNames[idx]}"; t.Foreground = StepGray; t.FontWeight = FontWeights.Normal; }
            else if (state == 1) { t.Text = $"{(char)('①' + idx)} {StepNames[idx]}"; t.Foreground = StepBlue; t.FontWeight = FontWeights.SemiBold; }
            else { t.Text = $"✓ {StepNames[idx]}"; t.Foreground = StepGreen; t.FontWeight = FontWeights.Normal; }
        }

        private void ResetProgress()
        {
            for (int i = 0; i < 4; i++) SetStep(i, 0);
            PbProgress.IsIndeterminate = false;
            PbProgress.Value = 0;
            PbProgress.Foreground = StepBlue;
            TxtProgress.Text = "就绪";
        }

        private void RenderProgress(ProgressInfo p)
        {
            var blue = StepBlue; var green = StepGreen;
            var orange = StepOrange;
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
                    PbProgress.IsIndeterminate = true; PbProgress.Foreground = blue;
                    TxtProgress.Text = "③ 正在对比两侧差异…";
                    break;
                case "copy":
                    SetStep(0, 2); SetStep(1, 2); SetStep(2, 2); SetStep(3, 1);
                    PbProgress.IsIndeterminate = false; PbProgress.Foreground = green;
                    var bpct = p.TotalBytes > 0 ? p.DoneBytes * 100.0 / p.TotalBytes : 0;
                    PbProgress.Value = bpct;
                    var speed = p.SpeedBytesPerSec > 1024 ? $"{Executor.FormatSize(p.SpeedBytesPerSec)}/s" : "";
                    var eta = p.Eta.HasValue ? $" · ETA {p.Eta.Value:hh\\:mm\\:ss}" : "";
                    TxtProgress.Text = $"④ 复制中 {bpct:F0}%（{p.DoneItems}/{p.TotalItems} 项 · " +
                        $"{Executor.FormatSize(p.DoneBytes)}/{Executor.FormatSize(p.TotalBytes)}）{speed}{eta}" +
                        (string.IsNullOrEmpty(p.CurrentItem) ? "" : $" · {TrimPath(p.CurrentItem)}");
                    break;
                case "delete":
                    SetStep(0, 2); SetStep(1, 2); SetStep(2, 2); SetStep(3, 1);
                    PbProgress.IsIndeterminate = false; PbProgress.Foreground = orange;
                    var dpct = p.TotalItems > 0 ? p.DoneItems * 100.0 / p.TotalItems : 0;
                    PbProgress.Value = dpct;
                    TxtProgress.Text = $"④ 镜像删除清理中 {dpct:F0}%（{p.DoneItems}/{p.TotalItems} 项）· {TrimPath(p.CurrentItem)}";
                    break;
                case "done":
                    for (int i = 0; i < 4; i++) SetStep(i, 2);
                    PbProgress.IsIndeterminate = false; PbProgress.Foreground = green; PbProgress.Value = 100;
                    TxtProgress.Text = p.CurrentItem;
                    break;
            }
            TxtStatus.Text = p.CurrentItem.Length > 120 ? p.CurrentItem[..120] : p.CurrentItem;
        }

        /// <summary>扫描阶段真进度：预数出的文件夹总数（TotalItems>0）按"已扫文件夹/总数"走百分比，
        /// 左右两侧各占半程（左 0-50%、右 50-100%）；预数失败（TotalItems=0，如权限/瞬时故障）回退不确定动画。</summary>
        private void RenderScanProgress(ProgressInfo p, int halfFrom, string step, string sideName)
        {
            var real = p.TotalItems > 0;
            PbProgress.IsIndeterminate = !real;
            PbProgress.Foreground = StepBlue;
            if (real)
                PbProgress.Value = halfFrom + Math.Min(100.0, p.Percent) * 0.5;
            var extra = string.IsNullOrEmpty(p.ExtraInfo) ? $"已发现 {p.DoneItems:N0} 项" : p.ExtraInfo;
            var lead = step.Length == 0 ? "正在扫描" : $"{step} 正在扫描";
            TxtProgress.Text = $"{lead}{sideName}文件夹… {extra} · {TrimPath(p.CurrentItem)}";
        }

        private static string TrimPath(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('\\', '/');
            if (s.Length <= 64) return s;
            return "…" + s[^60..];
        }

        private void OnRunFinished(SyncEngine engine, RunRecord r)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                _shownProgressSeq = Math.Max(_shownProgressSeq, engine.CurrentProgressSeq);   // 挡掉本轮残留的排队进度事件
                _traySync?.Finished(engine);   // RunFinished 触发时 _busy 尚未复位，显式收掉托盘动画
                Log($"{engine.Job.Name}: {r.Trigger} · {r.Status} · 复制 {r.CopiedFiles} · 删除 {r.DeletedFiles} · 失败 {r.FailedFiles} · {Executor.FormatSize(r.BytesCopied)}");
                if (Selected != null) Selected.Raise();
                UpdateDetail();
                // 启动补跑/自动同步完成后，若当前任务尚未展示过差异树，自动分析填充「全部」视图
                if (Selected?.Engine == engine && _lastPlan == null)
                    AutoPreviewAsync();
                NotifyIfNeeded(engine, r);
                // C1 轮末报告：成功/部分失败时生成 markdown（含移动/重试计数、增量节省、校验结果、失败清单前 50）
                var report = RunReport.Generate(engine, r);
                if (report != null)
                {
                    if (r.Trigger == "manual")
                    {
                        // 手动轮完成：气泡可点开报告（失败轮走 NotifyIfNeeded 的错误气泡，报告路径进日志区）
                        if (r.Status == "ok")
                        {
                            _pendingReportPath = report;
                            _trayIcon?.ShowBalloonTip("FolderSync",
                                $"【{engine.Job.Name}】同步完成，点击气泡查看运行报告",
                                Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
                        }
                    }
                    else
                        Log($"运行报告: {report}");
                }
            }));
        }

        private string? _pendingReportPath;   // 气泡点击待打开的报告（TrayBalloonTipClicked 消费）

        private HistoryWindow? _historyWindow;

        private void BtnHistory_Click(object sender, RoutedEventArgs e)
        {
            var item = SelectedOrFirst();
            if (item == null) return;
            // 单例防连点开一摞；换了任务则关旧开新（窗口内容跟选中任务走）
            if (_historyWindow != null && _historyWindow.IsLoaded)
            {
                if (_historyWindow.Engine == item.Engine) { _historyWindow.Activate(); return; }
                _historyWindow.Close();
            }
            _historyWindow = new HistoryWindow(item.Engine, _db) { Owner = this };
            _historyWindow.Show();
        }

        private void LogExport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出日志",
                Filter = "文本文件|*.txt",
                FileName = $"log-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                File.WriteAllText(dlg.FileName, TxtLog.Text, System.Text.Encoding.UTF8);
                Log($"日志已导出: {dlg.FileName}");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "导出失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ---------- 托盘通知（同类 5 分钟去重，防实时任务刷屏） ----------

        private readonly NotificationThrottle _notifyThrottle = new(TimeSpan.FromMinutes(5));

        private void NotifyIfNeeded(SyncEngine engine, RunRecord r)
        {
            string key; string msg;
            Hardcodet.Wpf.TaskbarNotification.BalloonIcon icon;
            if (r.Status == "error" && engine.LastError?.Contains("空间不足") == true)
            {
                key = $"{engine.Job.Id}:diskfull";
                msg = $"【{engine.Job.Name}】目标磁盘空间不足，同步已中止";
                icon = Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Error;
            }
            else if (r.Status is "error" or "partial")
            {
                key = $"{engine.Job.Id}:{r.Status}";
                msg = r.Status == "error"
                    ? $"【{engine.Job.Name}】同步失败：{engine.LastError}"
                    : $"【{engine.Job.Name}】部分失败 {r.FailedFiles} 项，点「上次失败明细」可重试";
                icon = Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Error;
            }
            else if (engine.LastConflictCount > 0)
            {
                key = $"{engine.Job.Id}:conflict";
                msg = $"【{engine.Job.Name}】{engine.LastConflictCount} 项冲突待人工裁决";
                icon = Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Warning;
            }
            else return;

            if (_notifyThrottle.ShouldShow(key))
                _trayIcon?.ShowBalloonTip("FolderSync", msg, icon);
        }

        // ---------- 分析 / 同步 ----------

        /// <summary>未显式选中时回退到第一个任务（启动后直接点分析/同步的常见场景）。</summary>
        private EngineItem? SelectedOrFirst()
        {
            if (Selected != null) return Selected;
            if (_items.Count > 0)
            {
                LstJobs.SelectedIndex = 0;
                return Selected;
            }
            return null;
        }

        /// <summary>统一记录当前计划与生成时间（陈旧计划防护：预览后有自动轮执行过则作废）。</summary>
        private void SetPlan(List<PlanEntry> plan)
        {
            _lastPlan = plan;
            _lastPlanAt = DateTime.Now;
        }

        /// <summary>自动预览：扫描+对比（不执行同步、不落运行历史），填充「全部」视图的完整文件夹树。启动/切换任务/补跑后自动触发。</summary>
        private async void AutoPreviewAsync()
        {
            if (_autoPreviewing) return;
            _autoPreviewing = true;
            bool retryForNewSelection = false;
            try
            {
                var item = SelectedOrFirst();
                if (item == null) return;
                var engine = item.Engine;
                // 等引擎空闲（给启动补跑让路）
                for (int i = 0; i < 120; i++)
                {
                    if (!engine.IsRunning) break;
                    await Task.Delay(500);
                }
                if (engine.IsRunning) return;   // 补跑太久，OnRunFinished 会兜底重试
                var (_, plan) = await engine.RunAsync("preview", execute: false, logRun: false);
                await Dispatcher.InvokeAsync(() =>
                {
                    BumpProgressSeq(engine);   // 预览轮不发 RunFinished：终态写入前先挡掉本轮残留进度事件
                    if (Selected?.Engine != engine)
                    {
                        // 预览期间用户已切走：旧结果丢弃，给新选中任务补一次预览
                        retryForNewSelection = true;
                        return;
                    }
                    SetPlan(plan);
                    _scanLeft = engine.LastLeftScan;
                    _scanRight = engine.LastRightScan;
                    _scanLeftRoot = engine.Job.LeftPath;
                    _scanRightRoot = engine.Job.RightPath;
                    _originalAction.Clear();
                    ShowPlan(plan);
                    var (c, u, d, m, b) = Differ.Summarize(plan);
                    var conflicts = plan.Count(p => p.Action == SyncAction.Conflict);
                    SetStep(0, 2); SetStep(1, 2); SetStep(2, 2); SetStep(3, 0);
                    PbProgress.IsIndeterminate = false;
                    PbProgress.Value = 0;
                    TxtProgress.Text = plan.Count == 0
                        ? "已加载：两边已一致（无差异）"
                        : $"已加载：差异 {plan.Count} 项（新建 {c} / 更新 {u} / 删除 {d}"
                      + (m > 0 ? $" / 移动 {m}" : "")
                      + (conflicts > 0 ? $" / ⚠ 冲突 {conflicts}" : "")
                      + "）——点「同步」执行";
                });
            }
            catch (InvalidOperationException) { /* 仍忙，OnRunFinished 兜底 */ }
            catch { }
            finally
            {
                _autoPreviewing = false;
                if (retryForNewSelection) _ = Dispatcher.BeginInvoke(() => AutoPreviewAsync());
            }
        }

        private async void BtnAnalyze_Click(object sender, RoutedEventArgs e)
        {
            var item = SelectedOrFirst();
            if (item == null) return;
            try
            {
                SetBusy(true);
                ResetProgress();
                // logRun=false（L-7）：分析不执行同步，落 runs 历史纯属污染（一次"分析→同步"最多 3 条）
                var (_, plan) = await item.Engine.RunAsync("manual", execute: false, logRun: false);
                if (Selected?.Engine != item.Engine) return;   // M-4：await 期间用户已切任务——晚到结果不得覆盖新任务视图
                BumpProgressSeq(item.Engine);   // 分析轮 logRun:false 无 RunFinished 兜底
                SetPlan(plan);
                _scanLeft = item.Engine.LastLeftScan;
                _scanRight = item.Engine.LastRightScan;
                _scanLeftRoot = item.Engine.Job.LeftPath;
                _scanRightRoot = item.Engine.Job.RightPath;
                _originalAction.Clear();
                ShowPlan(plan);
                var (c, u, d, m, b) = Differ.Summarize(plan);
                var conflicts = plan.Count(p => p.Action == SyncAction.Conflict);
                // 分析模式不执行同步：前三步标完成，④ 保持灰色待命；进度条复位
                SetStep(0, 2); SetStep(1, 2); SetStep(2, 2); SetStep(3, 0);
                PbProgress.IsIndeterminate = false;
                PbProgress.Value = 0;
                TxtProgress.Text = $"分析完成：差异 {plan.Count} 项（新建 {c} / 更新 {u} / 删除 {d}" +
                                   (m > 0 ? $" / 移动 {m}" : "") +
                                   (conflicts > 0 ? $" / ⚠ 冲突 {conflicts}" : "") +
                                   $"，待传 {Executor.FormatSize(b)}）——核对后点「同步」执行";
                Log($"分析 {item.Job.Name}: {plan.Count} 项差异" + (conflicts > 0 ? $"（{conflicts} 冲突）" : ""));
            }
            catch (InvalidOperationException ex) { Log(ex.Message); }   // 引擎原文可能不是"正在运行中"（路径守卫拒绝等）
            catch (Exception ex) { Log("分析失败: " + ex.Message); }
            finally { SetBusy(false); }
        }

        /// <summary>B1 工具栏「校验」：只读深度校验（全量），位腐行进树标人工裁决。</summary>
        private async void BtnVerify_Click(object sender, RoutedEventArgs e)
        {
            var item = SelectedOrFirst();
            if (item == null) return;
            try
            {
                SetBusy(true);
                ResetProgress();
                _cts = new CancellationTokenSource();
                var (rec, rot) = await item.Engine.VerifyAsync(_cts.Token);
                if (Selected?.Engine != item.Engine) return;   // M-4
                BumpProgressSeq(item.Engine);
                _scanLeft = _scanRight = null;   // 校验无扫描快照：树退化为纯差异树（只显示位腐行）
                _scanLeftRoot = item.Job.LeftPath;
                _scanRightRoot = item.Job.RightPath;
                _originalAction.Clear();
                SetPlan(rot);
                ShowPlan(rot);
                SetStep(0, 2); SetStep(1, 2); SetStep(2, 2); SetStep(3, 0);
                PbProgress.IsIndeterminate = false;
                PbProgress.Value = 100;
                Log(rot.Count == 0
                    ? $"深度校验通过: {item.Job.Name}（两侧内容逐文件一致）"
                    : $"深度校验: {item.Job.Name} 发现 {rot.Count} 处位腐差异（大小/时间一致但内容不同），已标记待裁决");
            }
            catch (OperationCanceledException) { Log("校验已取消"); }
            catch (InvalidOperationException ex) { Log(ex.Message); }
            catch (Exception ex) { Log("校验失败: " + ex.Message); }
            finally { SetBusy(false); }
        }

        private async void BtnSync_Click(object sender, RoutedEventArgs e)
        {
            var item = SelectedOrFirst();
            if (item == null) return;
            // 陈旧计划防护：预览之后若实时/定时/补跑自动执行过一轮，旧计划可能已过时 → 作废走完整分析+执行
            if (_lastPlan != null && item.Engine.LastRunAt is { } lastRun && lastRun > _lastPlanAt)
                _lastPlan = null;
            try
            {
                SetBusy(true);
                // 同步开始即看「所有变更」视图：本轮执行的就是这些动作（完成后 ShowPlan 仍会刷新为剩余差异）
                FilterChanged.IsChecked = true;
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
                    if (Selected?.Engine != item.Engine) return;   // M-4
                    SetPlan(plan);
                }
                if (Selected?.Engine != item.Engine) return;   // M-4：执行本身用捕获的引擎（数据不受损），但视图更新必须守卫
                BumpProgressSeq(item.Engine);   // await 恢复优先级高于排队的 OnRunFinished(bump)，终态文本写入前自行挡
                // 执行后重新分析，展示剩余差异：
                // ExecutePlanAsync 成功后引擎已用执行后状态刷新 Last*Scan（P-6）——直接复用免重扫；
                // RunAsync 路径的扫描是执行前的，需真重扫
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
                if (Selected?.Engine != item.Engine) return;   // M-4
                SetPlan(remain);
                _scanLeft = item.Engine.LastLeftScan;
                _scanRight = item.Engine.LastRightScan;
                _scanLeftRoot = item.Engine.Job.LeftPath;
                _scanRightRoot = item.Engine.Job.RightPath;
                _originalAction.Clear();
                ShowPlan(remain);
            }
            catch (InvalidOperationException ex) { Log(ex.Message); }   // 引擎原文可能不是"正在运行中"（路径守卫拒绝等）
            catch (OperationCanceledException) { Log("已取消"); }
            catch (Exception ex) { Log("同步失败: " + ex.Message); }
            finally { SetBusy(false); _cts?.Dispose(); _cts = null; UpdateConflictUi(); }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();                                  // 手动轮次（UI await 链上的 token）
            SelectedOrFirst()?.Engine.RequestCancel();        // 任何来源的轮次：实时/定时自动触发不经 _cts，
                                                             // 曾导致实时任务同步中点停止无效、只能重启软件
        }

        private void BtnToggleWatch_Click(object sender, RoutedEventArgs e)
        {
            var item = Selected;
            var j = item?.Job;
            if (item == null || j == null) return;
            j.Enabled = !j.Enabled;
            _db.UpdateJob(j);
            item.Engine.UpdateJob(j);   // 内部 ApplyTriggers：Enabled=false 卸载 watcher，true 重挂
            item.Raise();               // 状态行/状态点立即变（已停用→灰点）
            UpdateWatchButton();
            Log(j.Enabled
                ? $"已恢复实时监控: {j.Name}"
                : $"已停止实时监控: {j.Name}（这一轮不受影响；重启后保持停用，手动「同步」仍可用）");
        }

        /// <summary>「停止/恢复实时监控」按钮：只对选中的实时任务显示，文案随启用状态切换。</summary>
        private void UpdateWatchButton()
        {
            if (BtnToggleWatch == null) return;   // XAML 初始化早期 UpdateDetail 可能先于按钮就绪
            var j = Selected?.Job;
            var realtime = j != null && j.Trigger == TriggerType.Realtime;
            BtnToggleWatch.Visibility = realtime ? Visibility.Visible : Visibility.Collapsed;
            if (!realtime) return;
            BtnToggleWatch.Content = j!.Enabled ? "停止实时监控" : "恢复实时监控";
            BtnToggleWatch.Tag = j.Enabled ? "\uE769" : "\uE768";   // 暂停 ⏸ / 播放 ▶
        }

        private void SetBusy(bool busy)
        {
            // busy 时同步/分析/校验/裁决执行全部禁用（BtnCancel 保持可用）。只禁前两者时，
            // 同步跑着点「校验」会让 _cts 被 new 覆盖——取消链路错位、finally 提前解禁按钮
            BtnAnalyze.IsEnabled = BtnSync.IsEnabled = BtnVerify.IsEnabled = BtnResolveRun.IsEnabled = !busy;
            if (Selected != null) Selected.Raise();
        }

        // ---------- 树形对照表 ----------

        private void ShowPlan(List<PlanEntry> plan)
        {
            // 保存双侧完整快照与差异索引，供“全部”视图懒加载浏览。
            // 注意：单向类型冲突会同路径产出删除+重建两条，不能用 ToDictionary（重复键会炸），
            // 后写覆盖前写，树节点挂“重建”条目
            _planByPath = new Dictionary<string, PlanEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in plan) _planByPath[p.RelativePath] = p;
            BuildTreeIndexes(plan);
            _root = BuildFullTree(plan);
            StartThumbnailPass();   // 树建好即启动缩略图后台加载（有缩略图的类型陆续替换类型图标）
            // 计划就绪即切「所有变更」：分析/预览/同步完成/裁决后全部入口都走这里，
            // 用户"分析前"停留的「全部」视图在看到差异的第一时间被切走；手动已选其他筛选也会被
            // 新计划覆盖——同步/分析的结果语义就是"看变更"（已选中时赋值不重复触发事件）
            FilterChanged.IsChecked = true;
            RebuildVisibleRows();
            UpdateFilterCounts(plan);
            UpdateConflictUi();
            UpdateDetail();
            // 底部状态栏随计划刷新：分析/预览完成后没有再发进度事件，
            // 不主动更新的话会永远停在「正在对比两侧差异…」（用户看着像卡死）
            var (c, u, d, m, b) = Differ.Summarize(plan);
            var conflicts = plan.Count(p => p.Action == SyncAction.Conflict);
            TxtStatus.Text = plan.Count == 0
                ? $"两边已一致（无差异）{DateTime.Now:HH:mm:ss}"
                : $"差异 {plan.Count} 项：新建 {c} / 更新 {u} / 删除 {d}"
                  + (m > 0 ? $" / 移动 {m}" : "")
                  + (conflicts > 0 ? $" / ⚠ 冲突 {conflicts}" : "") + $" · {DateTime.Now:HH:mm:ss}";
            // 任务卡同步刷新：预览完成后引擎不再发进度/状态事件，任务卡文本会停在
            // 「正在对比两侧差异…」不动（与底部状态栏同款问题；用户看着像卡死）
            Selected?.Raise();
        }

        // ---------- 完整文件夹树（GoodSync 式：全部视图可浏览所有文件夹，差异高亮，懒加载） ----------

        /// <summary>单趟构建两张索引：父目录→子项名（懒加载查表）、文件夹→子树动作聚合（徽章计数）。</summary>
        private void BuildTreeIndexes(List<PlanEntry> plan)
        {
            // 子项索引：按「最后一个 '/'」求 key 的直接父目录（首段分组会把深层路径全错位到第一段名下，
            // 且目录条目被当文件行显示）；子项名取末段，目录/文件按条目类型分流。
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
            // 「全部」徽章：两侧并集总条目数（每条目仅作为其父目录的直接子项计入一次，无重复）
            _totalEntries = childIndex.Sum(kv => kv.Value.dirs.Count + kv.Value.files.Count);
            BuildAggIndex(plan);
        }

        /// <summary>重建文件夹聚合索引：每个差异条目按祖先文件夹逐级累计（O(计划数×平均深度)）。
        /// 人工裁决/跳过后条目动作已变，AfterPlanTweaked 里须重建（O(1) 查表依赖它与 _lastPlan 同步）。</summary>
        private void BuildAggIndex(List<PlanEntry> plan)
        {
            // 聚合索引：每个差异条目按祖先文件夹逐级累计（O(计划数×平均深度)，一次性）
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
                    c[AggSlotChanged]++;   // 第 8 槽：changed 合计（筛选"所有变更"O(1) 查表）
                    start = slash + 1;
                }
            }
        }

        /// <summary>构建完整树：根层直接加载，差异路径自动展开；无快照时退化为纯差异树。
        /// 差异超过 500 项（如一侧路径缺失导致全树皆差异）时跳过自动展开——
        /// 逐级展开会产生海量 UI 行且淹没差异位置，只加载根层，用户按需展开。</summary>
        private PlanTreeNode BuildFullTree(List<PlanEntry> plan)
        {
            var root = new PlanTreeNode { Depth = -1, ChildrenLoaded = false };
            if (_scanLeft == null || _scanRight == null)
            {
                var diffRoot = BuildTree(plan);
                return diffRoot;
            }
            EnsureChildrenLoaded(root);   // 根层：两侧顶层文件/文件夹全部可见（加载后置 true）
            // 咩咩要求：所有展示模式默认展开全部行——递归加载整棵树并全部置展开，
            // 用户只在想收起时手动点（点行任意位置）。原「差异>500 不自动展开」的保护取消
            LoadAllExpanded(root);
            return root;
        }

        /// <summary>递归加载全部子孙并全部置展开（一次构建，O(节点数)；DataGrid 行虚拟化兜住大树的 UI 行数）。</summary>
        private void LoadAllExpanded(PlanTreeNode node)
        {
            if (!node.ChildrenLoaded) EnsureChildrenLoaded(node);
            foreach (var c in node.Children)
            {
                c.IsExpanded = true;
                if (c.IsFolderLike) LoadAllExpanded(c);
            }
        }

        /// <summary>懒加载某文件夹的直接子项（查 _childIndex 索引），差异条目挂动作、无差异文件合成空白行。</summary>
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
                    IsExpanded = false   // 保守初始收起；正常路径由 LoadAllExpanded 统一置展开
                };
                if (_planByPath.TryGetValue(path, out var de)) node.Entry = de;   // 目录自身的差异动作
                node.Icon = FolderIcon();
                AggFromPrefix(node, path);
                // 目录条目自身动作也计入自身徽章（_aggByFolder 只按祖先累计，漏掉节点自己；
                // 与 RecomputeAgg 展开重算后的口径对齐，否则展开前后徽章数字会跳变）
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
                // 图标：按设置——关=通用文档图标（不查系统，省资源）；开=exe 按路径取自身图标，其余按扩展名
                var ext = Path.GetExtension(f);
                var iconRoot = l != null ? _scanLeftRoot : _scanRightRoot;
                var fullForIcon = Path.Combine(iconRoot, path.Replace('/', Path.DirectorySeparatorChar));
                ImageSource icon = !AppSettings.ShowIcons ? DefaultFileIcon
                    : string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase)
                        ? IconForExe(fullForIcon)
                        : IconForExtension(ext) ?? DefaultFileIcon;
                folder.Children.Add(new PlanTreeNode
                {
                    Name = f,
                    FullPath = path,
                    Depth = folder.Depth + 1,
                    Entry = pe,
                    Icon = icon,
                    IconFullPath = fullForIcon,   // 缩略图后台泵按路径取（有缩略图的类型陆续替换）
                    ChildrenLoaded = true
                });
            }
            SortChildren(folder);
            folder.ChildrenLoaded = true;
        }

        /// <summary>文件夹聚合计数：统计差异计划中位于该文件夹子树内的全部条目（不受懒加载影响）。</summary>
        /// <summary>挂子树聚合徽章：查 _aggByFolder 预聚合索引（ShowPlan 已按祖先一趟累计）。</summary>
        private void AggFromPrefix(PlanTreeNode node, string folderPath)
        {
            if (_aggByFolder != null && _aggByFolder.TryGetValue(folderPath, out var c)) ApplyAggCounts(node, c);
        }

        /// <summary>AddCount switch 的槽位映射；不参与聚合的动作返回 -1。槽 7=移动、8=changed 合计。</summary>
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
                var node = new PlanTreeNode { Name = name, FullPath = fullPath, Depth = depth };
                if (idx < 0) node.ParentAttach(root);
                else
                {
                    var parentPath = fullPath[..idx];
                    var parent = GetDir(parentPath, depth - 1);
                    node.ParentAttach(parent);
                }
                dirCache[fullPath] = node;
                return node;
            }

            foreach (var pe in plan.OrderBy(p => p.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                var parts = pe.RelativePath.Split('/');
                if (pe.IsDirectory)
                {
                    // 目录条目：挂到合成目录节点上（目录行显示聚合 + 自身动作计入聚合）
                    var dir = GetDir(pe.RelativePath, parts.Length - 1);
                    dir.Entry = pe;
                }
                else
                {
                    PlanTreeNode parent = root;
                    if (parts.Length > 1)
                        parent = GetDir(string.Join('/', parts.Take(parts.Length - 1)), parts.Length - 2);
                    var leaf = new PlanTreeNode
                    {
                        Name = parts[^1],
                        FullPath = pe.RelativePath,
                        Depth = parts.Length - 1,
                        Entry = pe
                    };
                    leaf.ParentAttach(parent);
                }
            }
            SortChildren(root);
            foreach (var n in dirCache.Values) SortChildren(n);   // 每节点排自己的直接子项（M-6：SortChildren 不再递归）
            RecomputeAgg(root);
            return root;
        }

        private void RebuildVisibleRows()
        {
            // 防御：XAML 初始化阶段（FilterAll IsChecked=True 触发 Checked）控件可能尚未就绪
            if (GridPlan == null || _root == null) return;
            var rows = new List<PlanTreeNode>();
            Flatten(_root, rows, CurrentFilter());
            GridPlan.ItemsSource = rows;
        }

        private string CurrentFilter()
        {
            if (FilterAll.IsChecked == true) return "all";
            // 「所有变更」必须有显式分支：初始默认已改为「全部」（FilterAll IsChecked=True），
            // 兜底不再代表 changed——漏分支会把选中 changed 误判成 all（切换视图时树不变）
            if (FilterChanged.IsChecked == true) return "changed";
            if (FilterCreate.IsChecked == true) return "create";
            if (FilterUpdate.IsChecked == true) return "update";
            if (FilterDelete.IsChecked == true) return "delete";
            if (FilterConflict.IsChecked == true) return "conflict";
            if (FilterSkip.IsChecked == true) return "skip";
            return "all";   // 无选中（XAML 初始化早期）兜底：与初始默认「全部」一致
        }

        private static bool LeafMatches(PlanEntry e, string filter) => filter switch
        {
            // 所有变更 = 有实际动作的条目；完整树合成的无差异行（Action=None）与人工跳过项都不算
            "changed" => e.Action != SyncAction.None,
            "create" => e.Action is SyncAction.CreateLeft or SyncAction.CreateRight,
            "update" => e.Action is SyncAction.UpdateLeft or SyncAction.UpdateRight,
            "delete" => e.Action is SyncAction.DeleteLeft or SyncAction.DeleteRight,
            "conflict" => e.Action == SyncAction.Conflict,
            // 完整树里合成的无差异行（__nodiff__）不算“已跳过”
            "skip" => e.Action == SyncAction.None && e.Note != PlanTreeNode.NoDiffMarker,
            _ => true
        };

        /// <summary>筛选模式：文件夹子树是否有匹配项。O(1) 查 _aggByFolder 聚合索引（H-3：
        /// 原实现对 _lastPlan 全表前缀扫描、Flatten/FlattenForce 对每个目录节点各调一次，O(文件夹数×计划数)）。
        /// skip 筛选例外：人工跳过项不入聚合索引，走计划扫描（量小——只能由用户逐条点出来）。</summary>
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
                _ => true   // "all"
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

        /// <summary>筛选模式下：匹配链上的目录全部展开显示；未加载的文件夹按需加载。</summary>
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

        /// <summary>只排当前层（M-6）：子层在自身 EnsureChildrenLoaded 尾部分别排序——
        /// 原尾部递归重排整棵已加载子树，EnsureChildrenLoaded 每次触发 = LoadAllExpanded 全树构建 O(N×深度)，
        /// 单点展开也重排整棵子树。文件夹在前、其余按名字。</summary>
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

        /// <summary>合并后的聚合重算（P-9，原 RecomputeAgg/RecomputeAggStatic 近乎重复）：
        /// 懒加载未展开的文件夹保留前缀统计值（来自 _aggByFolder），已加载的清零重算。</summary>
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
                else if (c.Entry != null)
                {
                    // 文件叶子的动作直接计入父目录徽章（子树口径），
                    // 叶子自身不携带徽章（自身动作用动作图标表达），与懒加载初始口径一致
                    AddCount(node, c.Entry.Action);
                }
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
            // 「全部」=两侧并集所有条目（含无差异文件与文件夹）；无快照时（预览前的空态）退化为计划数
            CntAll.Text = (_scanLeft != null && _scanRight != null ? _totalEntries : plan.Count).ToString();
            // 「所有变更」=有实际动作的条目（人工跳过的 Action=None 不算）
            CntChanged.Text = plan.Count(p => p.Action != SyncAction.None).ToString();
            CntCreate.Text = plan.Count(p => p.Action is SyncAction.CreateLeft or SyncAction.CreateRight).ToString();
            CntUpdate.Text = plan.Count(p => p.Action is SyncAction.UpdateLeft or SyncAction.UpdateRight).ToString();
            CntDelete.Text = plan.Count(p => p.Action is SyncAction.DeleteLeft or SyncAction.DeleteRight).ToString();
            CntConflict.Text = plan.Count(p => p.Action == SyncAction.Conflict).ToString();
            CntSkip.Text = plan.Count(p => p.Action == SyncAction.None).ToString();
        }

        private void Filter_Changed(object sender, RoutedEventArgs e) => RebuildVisibleRows();

        private void Expander_Click(object sender, RoutedEventArgs e)
        {
            // 展开懒加载文件夹：按需从快照加载直接子项
            if (sender is ToggleButton tb && tb.IsChecked == true
                && tb.DataContext is PlanTreeNode node && !node.ChildrenLoaded)
            {
                EnsureChildrenLoaded(node);
                RecomputeAgg(node);
            }
            RebuildVisibleRows();
        }

        /// <summary>点树行任意位置 = 展开/收起文件夹（名字/图标/空白处都行，与资源管理器一致）。
        /// 行内按钮（箭头/跳过/裁决）自行处理：向上爬树遇到即退出，防双重 toggle。</summary>
        private void GridPlan_RowClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var src = e.OriginalSource as System.Windows.DependencyObject;
            while (src != null && !ReferenceEquals(src, GridPlan))
            {
                if (src is System.Windows.Controls.Primitives.ButtonBase) return;   // 箭头/动作图标：不重复处理
                if (src is System.Windows.FrameworkElement fe && fe.DataContext is PlanTreeNode node)
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
                    return;
                }
                src = System.Windows.Media.VisualTreeHelper.GetParent(src);
            }
        }

        // ---------- 行内动作：点击图标 跳过/恢复/裁决 ----------

        private void LeftAction_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is PlanTreeNode node && node.Entry != null)
                ResolveAtNode(node, keepLeft: true);
        }

        private void RightAction_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is PlanTreeNode node && node.Entry != null)
                ResolveAtNode(node, keepLeft: false);
        }

        /// <summary>点行内动作图标：冲突行=裁决（左按钮保留左侧/右按钮保留右侧）；其他=跳过↔恢复。</summary>
        private void ResolveAtNode(PlanTreeNode node, bool keepLeft)
        {
            var pe = node.Entry!;
            if (pe.Note == PlanTreeNode.NoDiffMarker) return;   // 无差异行不可操作
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
                    pe.Note = "已恢复执行（点图标可再跳过）";   // 直接赋值：曾用 Replace 换"人工：跳过此项"，
                    // 残留的"（点图标恢复）"与"已恢复执行"自相矛盾
                    Log($"恢复执行 {pe.RelativePath}");
                }
            }
            else
            {
                _originalAction[pe] = pe.Action;
                pe.Action = SyncAction.None;
                pe.ManuallyResolved = true;
                pe.UserOverride = UserOverrideKind.Default;   // C2：跳过优先于反向，一并撤销
                pe.Note = "人工：跳过此项（点图标恢复）";
                Log($"跳过 {pe.RelativePath}");
            }
            AfterPlanTweaked();
        }

        private void AfterPlanTweaked()
        {
            if (_lastPlan != null) BuildAggIndex(_lastPlan);   // 条目动作已变：聚合索引须与计划同步（SubtreeMatches O(1) 查它）
            if (_root != null) RecomputeAgg(_root);
            if (_lastPlan != null) UpdateFilterCounts(_lastPlan);
            RebuildVisibleRows();
            UpdateConflictUi();
        }

        // ---------- C2 逐行改向（右键菜单：任意差异行跳过 / 反向执行） ----------

        /// <summary>对照表行右键：按当前行动作动态构建菜单（跳过↔恢复 / 反向↔取消反向）。</summary>
        private void GridPlan_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var src = e.OriginalSource as System.Windows.DependencyObject;
            PlanTreeNode? node = null;
            while (src != null && !ReferenceEquals(src, GridPlan))
            {
                if (src is System.Windows.FrameworkElement fe && fe.DataContext is PlanTreeNode n) { node = n; break; }
                src = System.Windows.Media.VisualTreeHelper.GetParent(src);
            }
            var pe = node?.Entry;
            if (pe == null || pe.Note == PlanTreeNode.NoDiffMarker) { GridPlan.ContextMenu = null; return; }

            var menu = new ContextMenu();
            if (pe.Action == SyncAction.None)
            {
                if (_originalAction.ContainsKey(pe))
                    menu.Items.Add(new MenuItem { Header = "恢复执行", Command = new MvvmCmd(() => ResolveAtNode(node!, false)) });
            }
            else if (pe.Action != SyncAction.Conflict)
            {
                menu.Items.Add(new MenuItem { Header = "跳过此项（本轮不同步该文件）",
                    Command = new MvvmCmd(() => ResolveAtNode(node!, false)) });
            }
            // 反向执行：仅 Update/Create 行（Delete/Move/冲突无反向语义——冲突已有保留左/右，Delete 反向无意义）
            if (pe.UserOverride == UserOverrideKind.Reverse)
                menu.Items.Add(new MenuItem { Header = "取消反向执行", Command = new MvvmCmd(() => ToggleReverse(pe)) });
            else if (pe.Action is SyncAction.UpdateRight or SyncAction.UpdateLeft or SyncAction.CreateRight or SyncAction.CreateLeft)
                menu.Items.Add(new MenuItem
                {
                    Header = pe.Action is SyncAction.CreateRight or SyncAction.CreateLeft
                        ? "反向执行（以对侧为准：删除本侧文件）"
                        : "反向执行（以对侧现状覆盖本侧）",
                    Command = new MvvmCmd(() => ToggleReverse(pe))
                });
            GridPlan.ContextMenu = menu.Items.Count > 0 ? menu : null;
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

        /// <summary>零依赖命令包装（右键菜单回调）。</summary>
        private sealed class MvvmCmd : System.Windows.Input.ICommand
        {
            private readonly Action _run;
            public MvvmCmd(Action run) => _run = run;
            public event EventHandler? CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object? parameter) => true;
            public void Execute(object? parameter) => _run();
        }

        // ---------- 双向冲突（底部裁决栏，备用入口）----------

        private void UpdateConflictUi()
        {
            var hasConflicts = _lastPlan != null && _lastPlan.Any(p => p.Action == SyncAction.Conflict);
            SpConflict.Visibility = hasConflicts ? Visibility.Visible : Visibility.Collapsed;
            Selected?.Raise();
        }

        private void ResolveSelected(SyncAction action, string note)
        {
            if (GridPlan.SelectedItem is not PlanTreeNode node || node.Entry == null || node.Entry.Action != SyncAction.Conflict)
            {
                Log("请先在对照表中选中一条 ⚠ 冲突 行");
                return;
            }
            node.Entry.Action = action;
            node.Entry.ManuallyResolved = true;
            node.Entry.Note = note;
            AfterPlanTweaked();
            Log($"裁决 {node.Entry.RelativePath}: {note}");
        }

        private void BtnKeepLeft_Click(object sender, RoutedEventArgs e) =>
            ResolveSelected(SyncAction.UpdateRight, "人工：保留左侧 → 复制到右");
        private void BtnKeepRight_Click(object sender, RoutedEventArgs e) =>
            ResolveSelected(SyncAction.UpdateLeft, "人工：保留右侧 → 复制到左");
        private void BtnSkipConflict_Click(object sender, RoutedEventArgs e) =>
            ResolveSelected(SyncAction.None, "人工：跳过，保持现状");

        private async void BtnResolveRun_Click(object sender, RoutedEventArgs e)
        {
            var item = Selected;
            if (item == null || _lastPlan == null) return;
            var resolved = _lastPlan.Where(p => p.ManuallyResolved).ToList();
            if (resolved.Count == 0) { Log("尚未裁决任何冲突项"); return; }
            try
            {
                SetBusy(true);
                _cts = new CancellationTokenSource();
                var (rec, _) = await item.Engine.RunPlanAsync(resolved, _cts.Token);
                Log($"裁决执行: {rec.Status}，成功 {rec.CopiedFiles}，失败 {rec.FailedFiles}");
                if (Selected?.Engine != item.Engine) return;   // M-4
                BumpProgressSeq(item.Engine);
                // 裁决执行成功后引擎已刷新 Last*Scan（RunPlanAsync 内部重扫），直接复用
                List<PlanEntry> remain;
                try { remain = item.Engine.ComputePlanFromLastScan(); }
                catch (InvalidOperationException) { var (_, rp) = await item.Engine.RunAsync("manual", execute: false, logRun: false); remain = rp; }
                if (Selected?.Engine != item.Engine) return;   // M-4
                SetPlan(remain);
                _scanLeft = item.Engine.LastLeftScan;
                _scanRight = item.Engine.LastRightScan;
                _scanLeftRoot = item.Engine.Job.LeftPath;
                _scanRightRoot = item.Engine.Job.RightPath;
                _originalAction.Clear();
                ShowPlan(remain);
            }
            catch (InvalidOperationException ex) { Log(ex.Message); }   // 引擎原文可能不是"正在运行中"（路径守卫拒绝等）
            catch (Exception ex) { Log("裁决执行失败: " + ex.Message); }
            finally { SetBusy(false); _cts?.Dispose(); _cts = null; UpdateConflictUi(); }
        }

        // ---------- 版本库 / 失败明细 ----------

        private void BtnOpenVersions_Click(object sender, RoutedEventArgs e)
        {
            var item = SelectedOrFirst();
            if (item == null) return;
            new VersionBrowserWindow(item.Job) { Owner = this }.NoActivateThenShowDialog();
        }

        private void BtnFailedItems_Click(object sender, RoutedEventArgs e)
        {
            var item = SelectedOrFirst();
            if (item == null) return;
            var engine = item.Engine;
            if (engine.LastFailedItems.Count == 0)
            {
                Log($"任务「{item.Job.Name}」最近一轮没有失败项");
                return;
            }
            ShowFailedDialog(item);
        }

        private void ShowFailedDialog(EngineItem item)
        {
            var engine = item.Engine;
            var dlg = new Window
            {
                Title = $"上次失败明细 — {item.Job.Name}（共 {engine.LastFailedTotal} 条，明细 {engine.LastFailedItems.Count} 条）",
                Width = 780, Height = 460, Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                MinWidth = 560, MinHeight = 300
            };
            var list = new ListView
            {
                FontSize = 12,
                ItemContainerStyle = CreateStretchItemStyle()
            };
            var gv = new GridView();
            gv.Columns.Add(new GridViewColumn { Header = "动作", DisplayMemberBinding = new System.Windows.Data.Binding("Action"), Width = 110 });
            gv.Columns.Add(new GridViewColumn { Header = "相对路径", DisplayMemberBinding = new System.Windows.Data.Binding("RelativePath"), Width = 260 });
            gv.Columns.Add(new GridViewColumn { Header = "错误", DisplayMemberBinding = new System.Windows.Data.Binding("Error"), Width = 340 });
            list.View = gv;
            list.ItemsSource = engine.LastFailedItems.Select(f => new { f.Action, f.RelativePath, f.Error }).ToList();

            var btnRetry = new Button { Content = "重试失败项", Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(0, 0, 8, 0), Style = (Style)FindResource("BtnPrimary") };
            var btnLog = new Button { Content = "打开明细文件", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
            var btnClose = new Button { Content = "关闭", Padding = new Thickness(12, 5, 12, 5), IsCancel = true };
            var tip = new TextBlock
            {
                Foreground = ThemeBrushes.C(ThemeBrushes.K.TextTertiary),
                FontSize = 11.5, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
                Text = "重试按当前文件系统真实状态重建：源在则复制，源没了则跳过"
            };
            var bottom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            right.Children.Add(btnClose);
            var dock = new DockPanel { Margin = new Thickness(12), LastChildFill = true };
            DockPanel.SetDock(bottom, Dock.Bottom);
            bottom.Children.Add(btnRetry);
            bottom.Children.Add(btnLog);
            bottom.Children.Add(tip);
            bottom.Children.Add(right);
            dock.Children.Add(bottom);
            dock.Children.Add(list);

            btnLog.Click += (_, _) =>
            {
                if (engine.LastFailedLogFile != null && File.Exists(engine.LastFailedLogFile))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(engine.LastFailedLogFile) { UseShellExecute = true });
                else Log("明细文件不存在（可能已被清理）");
            };

            btnRetry.Click += async (_, _) =>
            {
                btnRetry.IsEnabled = false;
                try
                {
                    SetBusy(true);
                    var (rec, plan) = await engine.RetryFailedAsync();
                    Log($"重试完成: {rec.Status}，重建 {plan.Count} 项，成功 {rec.CopiedFiles}，失败 {rec.FailedFiles}");
                    BumpProgressSeq(engine);
                    MessageBox.Show(dlg,
                        rec.FailedFiles == 0 ? $"重试完成：{plan.Count} 项全部成功" : $"重试后仍有 {rec.FailedFiles} 项失败，可再次查看明细",
                        "重试结果", MessageBoxButton.OK,
                        rec.FailedFiles == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
                    dlg.Close();
                    // 重试后刷新差异视图（M-4：await 期间切了任务就不动新任务的视图）
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
                    if (Selected?.Engine != engine) return;   // M-4
                    SetPlan(remain);
                    _scanLeft = engine.LastLeftScan;
                    _scanRight = engine.LastRightScan;
                    _scanLeftRoot = engine.Job.LeftPath;
                    _scanRightRoot = engine.Job.RightPath;
                    _originalAction.Clear();
                    ShowPlan(remain);
                }
                catch (InvalidOperationException ex) { MessageBox.Show(dlg, ex.Message, "无法重试", MessageBoxButton.OK, MessageBoxImage.Warning); }
                catch (Exception ex) { Log("重试失败: " + ex.Message); }
                finally { SetBusy(false); }
            };

            dlg.Content = dock;
            dlg.NoActivateThenShowDialog();
        }

        private static Style CreateStretchItemStyle()
        {
            var s = new Style(typeof(ListViewItem));
            s.Setters.Add(new Setter(ListViewItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            return s;
        }

        // ---------- 任务 CRUD ----------

        private void BtnNew_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new JobEditWindow(null) { Owner = this };
            if (dlg.NoActivateThenShowDialog() == true)
            {
                var job = _manager.CreateNew(dlg.JobName, dlg.LeftPath, dlg.RightPath);
                job.Direction = dlg.Direction;
                ApplyDialog(dlg, job);
                _db.UpdateJob(job);
                _manager.Add(job);
                RefreshList();
                Log($"新建任务: {job.Name}");
            }
        }

        private void BtnEdit_Click(object sender, RoutedEventArgs e)
        {
            var item = SelectedOrFirst();
            if (item == null) return;
            var j = item.Job;
            var dlg = new JobEditWindow(j) { Owner = this };
            if (dlg.NoActivateThenShowDialog() == true)
            {
                // 只赋 ApplyDialog 未覆盖的 4 项（其余 16 项统一走 ApplyDialog，别手工重复赋一遍）
                j.Name = dlg.JobName;
                j.LeftPath = dlg.LeftPath;
                j.RightPath = dlg.RightPath;
                j.Direction = dlg.Direction;
                ApplyDialog(dlg, j);
                _manager.Save(j);   // B2：经 JobManager.Save（调度 timer 随配置即时刷新）
                _lastPlan = null;
                RefreshList();
                Log($"任务已更新: {j.Name}");
            }
        }

        private static void ApplyDialog(JobEditWindow dlg, SyncJob j)
        {
            j.Trigger = dlg.Trigger;
            j.ScheduleSpec = dlg.ScheduleSpec;
            j.IntervalSeconds = dlg.IntervalSeconds;
            j.DebounceSeconds = dlg.DebounceSeconds;
            j.ExcludePatterns = dlg.ExcludePatterns;
            j.MirrorDelete = dlg.MirrorDelete;
            j.DeleteToRecycleBin = dlg.DeleteToRecycleBin;
            j.StrictMirror = dlg.StrictMirror;
            j.ConflictPolicy = dlg.ConflictPolicy;
            j.VersionKeepCount = dlg.VersionKeepCount;
            j.DeltaSync = dlg.DeltaSync;
            j.AutoRetry = dlg.AutoRetry;
            j.MoveDetect = dlg.MoveDetect;
            j.CopyVerify = dlg.CopyVerify;
            j.DeepVerify = dlg.DeepVerify;
            j.CopyWorkers = dlg.CopyWorkers;
            j.Enabled = dlg.Enabled;
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            var item = Selected;
            if (item == null) return;
            var r = MessageBox.Show(this, $"确定删除任务「{item.Job.Name}」？（不会删除文件夹内容）",
                "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            _manager.Remove(item.Job.Id);
            _lastPlan = null;
            RefreshList();
            Log("任务已删除: " + item.Job.Name);
        }

        // ---------- 其他 ----------

        private void LstJobs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _lastPlan = null;
            _originalAction.Clear();
            _root = null;
            GridPlan.ItemsSource = null;
            ClearPathCaches();   // P-4：旧任务的 exe 图标/缩略图缓存不再增长，切走即清
            // 切任务重置筛选到「全部」（回到"分析前"默认视图；已选中时再赋 true 不触发事件，
            // 随后 AutoPreview → ShowPlan 完成时再切到「所有变更」）
            FilterAll.IsChecked = true;
            ResetProgress();
            UpdateDetail();
            UpdateConflictUi();
            AutoPreviewAsync();   // 选中任务即自动扫描展示完整文件夹树（不执行同步）
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            new SettingsWindow { Owner = this }.NoActivateThenShowDialog();
            ApplyIconSetting();   // 设置窗口关闭后按开关刷新当前树（关=通用图标，开=恢复类型图标+缩略图）
        }

        /// <summary>按「显示图标和缩略图」设置重建当前树图标（即时生效）。</summary>
        private void ApplyIconSetting()
        {
            if (_root == null) return;
            var on = AppSettings.ShowIcons;
            if (!on) _thumbGen++;   // 关：作废在途缩略图泵，防晚到覆盖通用图标
            var nodes = new List<PlanTreeNode>();
            CollectAllNodes(_root, nodes);
            foreach (var n in nodes)
            {
                if (n.IsFolderLike)
                {
                    n.Icon = FolderIcon();   // 文件夹恒系统文件夹图标（仅一种，无成本）
                    continue;
                }
                if (!on) { n.Icon = DefaultFileIcon; continue; }
                var ext = Path.GetExtension(n.Name);
                n.Icon = string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase) && n.IconFullPath != null
                    ? IconForExe(n.IconFullPath)
                    : IconForExtension(ext) ?? DefaultFileIcon;
            }
            if (on) StartThumbnailPass();
        }

        private static void CollectAllNodes(PlanTreeNode node, List<PlanTreeNode> sink)
        {
            foreach (var c in node.Children)
            {
                sink.Add(c);
                if (c.IsFolderLike) CollectAllNodes(c, sink);
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            var running = _manager.Engines.Any(x => x.IsRunning || x.Job.Enabled && x.Job.Trigger != TriggerType.Manual);
            if (running && !_forceExit)
            {
                e.Cancel = true;
                Hide();
                _trayIcon?.ShowBalloonTip("FolderSync", "已最小化到系统托盘，同步继续运行。右键托盘图标可退出。", Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
                return;
            }
            base.OnClosing(e);
        }

        private bool _forceExit;
        private Hardcodet.Wpf.TaskbarNotification.TaskbarIcon? _trayIcon;
        private TraySyncIndicator? _traySync;

        /// <summary>最近一次托盘恢复动作的时间。
        /// Show() 期间 WPF 可能异步经过 Minimized 状态再触发 OnStateChanged（内部 hwnd 状态应用顺序不定），
        /// 恢复时间窗内不做最小化隐藏，否则刚恢复的窗口会被立刻藏回托盘。</summary>
        private DateTime _trayRestoreAt = DateTime.MinValue;

        /// <summary>最小化进托盘：点最小化按钮不占任务栏，直接收进托盘（恢复同关窗：双击托盘/右键菜单）。</summary>
        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState == WindowState.Minimized && IsVisible &&
                (DateTime.Now - _trayRestoreAt).TotalSeconds > 2)
                Hide();
        }

        public void ForceExit()
        {
            _forceExit = true;
            Close();
        }

        public void AttachTray(Hardcodet.Wpf.TaskbarNotification.TaskbarIcon tray, System.Windows.Media.Imaging.BitmapSource? baseIcon)
        {
            _trayIcon = tray;
            // C1：手动轮完成气泡点击打开运行报告
            tray.TrayBalloonTipClicked += (_, _) =>
            {
                var path = _pendingReportPath;
                _pendingReportPath = null;
                if (path != null && File.Exists(path))
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
                    catch { }
            };
            _traySync = new TraySyncIndicator(tray, baseIcon);
            // 启动补跑可能在 AttachTray 前已开跑（LoadAllAndResumeAsync 在构造函数里就启动了）：预热激活
            foreach (var e in _manager.Engines)
                _traySync.Observe(e);
        }

        /// <summary>App.OnExit 释放托盘前先停指示器（停动画轮询、还原静态图标），防对已释放托盘写入。</summary>
        public void DisposeTrayIndicator() => _traySync?.Dispose();

        /// <summary>从托盘恢复主窗口。
        /// 顺序必须是 Show() → WindowState=Normal：对隐藏窗口设置 WindowState 是空操作（WPF 不生效），
        /// 先设 Normal 再 Show 会以最小化状态显示（任务栏出现图标但窗口不展开）。
        /// 先 Show 则要靠 _trayRestoreAt 抑制窗吞掉恢复期间可能出现的伪 Minimized 事件，防止刚显示又被 Hide 回去。</summary>
        public void ShowFromTray()
        {
            _trayRestoreAt = DateTime.Now;
            Show();
            WindowState = WindowState.Normal;
            Activate();
            // 调度器排空后关闭抑制窗：伪 Minimized 已被吞掉，用户之后的真实最小化正常隐藏进托盘
            Dispatcher.BeginInvoke(new Action(() => _trayRestoreAt = DateTime.MinValue),
                System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    /// <summary>挂父节点（构建树时用）。</summary>
    internal static class TreeNodeAttach
    {
        public static void ParentAttach(this PlanTreeNode node, PlanTreeNode parent)
        {
            parent.Children.Add(node);
        }
    }
}
