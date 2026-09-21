using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia;
using FolderSync.App.Services;
using FolderSync.App.ViewModels;

namespace FolderSync.App.Views;

/// <summary>主窗口（MVVM 薄壳）：树行点击/右键菜单/日志菜单/托盘交互（关闭→藏、最小化→藏）。
/// 窗口记忆：启动恢复上次位置和大小（设置可关），变化防抖落盘。</summary>
public partial class MainWindow : Window
{
    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;
    private DateTime _trayRestoreAt = DateTime.MinValue;
    private bool _forceExit;
    private DispatcherTimer? _boundsTimer;
    private PixelPoint _savePos;
    private Size _saveSize;
    private WindowState _saveState;

    public MainWindow()
    {
        InitializeComponent();
        // 行点击（任意位置=展开/收起）与右键改向菜单：Avalonia DataGrid 无专门事件，指针级处理
        GridPlan.AddHandler(PointerPressedEvent, GridPlan_PointerPressed, RoutingStrategies.Tunnel);
        _defaultColWidths = GridPlan.Columns.ToDictionary(c => (string)c.Header, c => c.Width);
        _defaultWinSize = new Size(Width, Height);
        RestoreSavedBounds();
        TrackBoundsChanges();
        RestoreColumnWidths();
        TrackColumnWidths();
        Closing += (_, e) =>
        {
            SaveBoundsNow();   // 真关/藏托盘都先落盘最新位置（防抖可能还没到点）
            if (_forceExit) return;
            if (Vm.ShouldHideOnClose())
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    // ---------- 列宽记忆（拖动列头边缘调整，全局一份跨任务） ----------

    private DispatcherTimer? _colWidthTimer;
    private readonly Dictionary<string, double> _pendingColWidths = new();
    private Dictionary<string, DataGridLength>? _defaultColWidths;
    private Size _defaultWinSize;
    private bool _resettingLayout;   // 还原默认布局期间跳过列宽落盘（回写默认值没有意义）

    private void RestoreColumnWidths()
    {
        var saved = AppSettings.ColumnWidths;
        if (saved.Count == 0) return;
        foreach (var col in GridPlan.Columns)
            if (col.Header is string h && saved.TryGetValue(h, out var w) && w >= 20)
                col.Width = new DataGridLength(w, DataGridLengthUnitType.Pixel);
    }

    private void TrackColumnWidths()
    {
        foreach (var col in GridPlan.Columns)
            col.PropertyChanged += (s, e) =>
            {
                if (e.Property != DataGridColumn.WidthProperty) return;
                if (_resettingLayout) return;
                if (s is DataGridColumn { Header: string h } c
                    && c.Width.IsAbsolute
                    && Math.Abs(c.Width.Value - (AppSettings.ColumnWidths.TryGetValue(h, out var old) ? old : -1)) > 0.5)
                {
                    _pendingColWidths[h] = c.Width.Value;
                    QueueColWidthSave();
                }
            };
    }

    /// <summary>拖动列边缘时 Width 连续变化：800ms 防抖后统一落盘（同窗口位置的做法）。</summary>
    private void QueueColWidthSave()
    {
        _colWidthTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(800), DispatcherPriority.Background, (_, _) =>
        {
            _colWidthTimer!.Stop();
            foreach (var (h, w) in _pendingColWidths)
                AppSettings.SaveColumnWidth(h, w);
            _pendingColWidths.Clear();
        });
        _colWidthTimer.Stop();
        _colWidthTimer.Start();
    }

    /// <summary>设置窗口「还原窗口布局」：立即回到默认尺寸/居中/默认列宽，并清掉已记住的记录。</summary>
    public void ResetWindowLayout()
    {
        AppSettings.ClearWindowLayout();
        _resettingLayout = true;
        try
        {
            if (_defaultColWidths != null)
                foreach (var col in GridPlan.Columns)
                    if (col.Header is string h && _defaultColWidths.TryGetValue(h, out var w))
                        col.Width = w;
            _pendingColWidths.Clear();
            WindowState = WindowState.Normal;
            Width = _defaultWinSize.Width;
            Height = _defaultWinSize.Height;
            var home = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (home != null)
                Position = new PixelPoint(
                    home.WorkingArea.Center.X - (int)(Width * RenderScaling) / 2,
                    home.WorkingArea.Center.Y - (int)(Height * RenderScaling) / 2);
        }
        finally
        {
            // 复位延迟到布局阶段之后：Avalonia 布局会再触发一轮 Width 规范化，
            // 立即复位的话默认值又被当"变化"回写进 settings（值无害但不干净）
            Dispatcher.UIThread.Post(() => _resettingLayout = false, DispatcherPriority.Background);
        }
    }

    // ---------- 窗口记忆（记住上次位置和大小） ----------

    private void RestoreSavedBounds()
    {
        if (!AppSettings.RememberWindow || AppSettings.WindowBounds is not { } b) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = b.w; Height = b.h;
        Position = new PixelPoint(b.x, b.y);
        if (b.max) WindowState = WindowState.Maximized;
    }

    /// <summary>越界自愈：上次位置落在如今不存在的屏幕上（拔显示器/改分辨率）→ 拉回主屏工作区居中。</summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (WindowState != WindowState.Normal)
            return;   // 最大化无需校验；首次触发 QueueSaveBounds 由状态变化路径覆盖
        var rect = new PixelRect(Position, new PixelSize((int)(Width * RenderScaling), (int)(Height * RenderScaling)));
        bool visible = false;
        foreach (var s in Screens.All)
            if (s.Bounds.Intersects(rect)) { visible = true; break; }
        if (!visible)
        {
            var home = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (home != null)
                Position = new PixelPoint(
                    home.WorkingArea.Center.X - rect.Width / 2,
                    home.WorkingArea.Center.Y - rect.Height / 2);
        }
        QueueSaveBounds();   // 记录初始 bounds：用户没动过窗口就关也有一份正确的记录
    }

    private void TrackBoundsChanges()
    {
        PositionChanged += (_, _) => QueueSaveBounds();
        Resized += (_, _) => QueueSaveBounds();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty) QueueSaveBounds();
        };
    }

    /// <summary>位置/大小/状态变化 → 800ms 防抖落盘（拖动窗口时 PositionChanged 每帧触发，不能每帧写文件）。</summary>
    private void QueueSaveBounds()
    {
        if (!AppSettings.RememberWindow) return;
        _savePos = Position; _saveSize = Bounds.Size; _saveState = WindowState;
        _boundsTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(800), DispatcherPriority.Background, (_, _) =>
        {
            _boundsTimer!.Stop();
            SaveBoundsNow();
        });
        _boundsTimer.Stop();
        _boundsTimer.Start();
    }

    private void SaveBoundsNow()
    {
        _boundsTimer?.Stop();
        if (!AppSettings.RememberWindow) return;
        if (_saveSize.Width <= 0) return;   // 从未记录过（窗口未显示即关）：跳过
        if (_saveState == WindowState.Minimized) return;   // 最小化的坐标无意义：保留上次
        AppSettings.SaveWindowBounds(_savePos.X, _savePos.Y, _saveSize.Width, _saveSize.Height,
            _saveState == WindowState.Maximized);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel vm) vm.View = this;
    }

    private void GridPlan_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && GridPlan.SelectedItem is PlanTreeNode node)
            vm.SelectedRow = node;
    }

    /// <summary>点树行任意位置 = 展开/收起文件夹；右键 = 按行动作弹改向菜单。</summary>
    private void GridPlan_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var node = FindNode(e.Source as Visual);
        if (node == null) return;
        var props = e.GetCurrentPoint(GridPlan).Properties;
        if (props.IsRightButtonPressed)
        {
            var cmds = Vm.ContextCommandsFor(node);
            if (cmds == null) return;
            var menu = new ContextMenu();
            foreach (var (header, cmd) in cmds)
                menu.Items.Add(new MenuItem { Header = header, Command = cmd });
            menu.Open(GridPlan);
            e.Handled = true;
        }
        // 左键行点击：行内按钮（Toggle/Button）自行处理（命中按钮时不动树展开）
        else if (e.Source is Visual src && !IsOverInteractiveControl(src))
        {
            Vm.RowClicked(node);
        }
    }

    private PlanTreeNode? FindNode(Visual? src)
    {
        while (src != null && !ReferenceEquals(src, GridPlan))
        {
            if (src is Control { DataContext: PlanTreeNode n }) return n;
            src = src.GetVisualParent();
        }
        return null;
    }

    /// <summary>命中树行内的按钮/箭头时不重复处理（防行展开与按钮动作双触发）。</summary>
    private static bool IsOverInteractiveControl(Visual src)
    {
        var v = src;
        for (int i = 0; i < 8 && v != null; i++)
        {
            if (v is Button or ToggleButton) return true;
            v = v.GetVisualParent();
        }
        return false;
    }

    public void Expander_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { DataContext: PlanTreeNode node } tb && DataContext is MainWindowViewModel vm)
        {
            // 展开懒加载文件夹：按需从快照加载直接子项（toggle 状态已由绑定先行翻转）
            vm.ToggleNode(node);
        }
    }

    public void LeftAction_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PlanTreeNode node } && DataContext is MainWindowViewModel vm)
            vm.LeftAction(node);
    }

    public void RightAction_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PlanTreeNode node } && DataContext is MainWindowViewModel vm)
            vm.RightAction(node);
    }

    // ---------- 筛选 ----------

    public void FilterAll_Click(object? sender, RoutedEventArgs e) => Vm.SetFilter("all");
    public void FilterChanged_Click(object? sender, RoutedEventArgs e) => Vm.SetFilter("changed");
    public void FilterCreate_Click(object? sender, RoutedEventArgs e) => Vm.SetFilter("create");
    public void FilterUpdate_Click(object? sender, RoutedEventArgs e) => Vm.SetFilter("update");
    public void FilterDelete_Click(object? sender, RoutedEventArgs e) => Vm.SetFilter("delete");
    public void FilterConflict_Click(object? sender, RoutedEventArgs e) => Vm.SetFilter("conflict");
    public void FilterSkip_Click(object? sender, RoutedEventArgs e) => Vm.SetFilter("skip");

    public void LstJobs_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm.Selected == null) return;   // 参考#4：空白处双击≠新建任务
        _ = Vm.EditJobAsync(Vm.Selected.Job);
    }

    // ---------- 日志菜单 ----------

    public async void LogCopySelected_Click(object? sender, RoutedEventArgs e) =>
        await Vm.CopyLogSelectionAsync(TxtLog.SelectedText);

    public async void LogCopyAll_Click(object? sender, RoutedEventArgs e) =>
        await Vm.CopyLogSelectionAsync(null);

    public void LogExport_Click(object? sender, RoutedEventArgs e) => Vm.ExportLog();

    public void LogClear_Click(object? sender, RoutedEventArgs e) => Vm.ClearLog();

    // ---------- 托盘交互 ----------

    /// <summary>从托盘恢复主窗口。</summary>
    public void ShowFromTray()
    {
        _trayRestoreAt = DateTime.Now;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>托盘「退出」：跳过关闭拦截真退出。</summary>
    public void ForceExit()
    {
        _forceExit = true;
        Close();
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desk)
            desk.Shutdown();
    }

    /// <summary>App 退出前清理（托盘指示器等）。</summary>
    public void ForceExitCleanup()
    {
    }
}
