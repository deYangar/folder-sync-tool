using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using FolderSync.App.ViewModels;
using FolderSync.App.Views;
using FolderSync.Core;

namespace FolderSync.App.Services;

/// <summary>
/// 托盘服务（Avalonia TrayIcon）：常驻托盘图标 + 右键菜单（打开主窗口/打开上次报告/退出），
/// 有执行轮在跑时图标切动态帧（基础图缩小居中 + 外圈圆弧旋转）+ 悬浮文字实时进度。
/// Win11 托盘三铁律（2026-09-14 实测）在新实现沿用：高频 NIM_MODIFY 会停刷冻帧——
/// 2.5fps 图标 + 600ms 文字节流；全部空闲恢复静态后冗余重发一次（Nudge）冲刷末态。
/// Linux GNOME 无托盘时 Avalonia 静默不显示（降级矩阵 §6：不阻塞主流程）。</summary>
public sealed class TrayService : IDisposable
{
    private const string DefaultTooltip = "FolderSync — 本地文件夹同步";
    private const int FrameCount = 8;
    private const int IconSize = 32;
    private const int TooltipMaxChars = 120;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan TextMinInterval = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan NudgeDelay = TimeSpan.FromSeconds(1.5);

    private readonly TrayIcon _tray;
    private readonly MainWindow _main;
    private readonly DispatcherTimer _timer;
    private WindowIcon? _baseIcon;
    private WindowIcon[]? _animIcons;
    private WindowIcon? _currentIcon;
    private bool _framesFailed;
    private int _frameIndex;
    private bool _animating;
    private bool _textDirty;
    private TimeSpan _sinceTextRefresh;
    private string _lastTooltip = "";
    private DispatcherTimer? _nudgeTimer;
    private bool _disposed;

    private sealed class Entry
    {
        public string JobName = "";
        public long RunSeq;
        public string Summary = "准备中…";
    }

    private readonly Dictionary<SyncEngine, Entry> _active = new();

    public TrayService(MainWindow main)
    {
        _main = main;
        // !! DispatcherTimer 带参构造默认即已启动：不显式 Stop 的话动画 tick 从进程
        // 启动起就无条件换帧——托盘常转根因（2026-09-16 探针日志实锤：Tick 狂奔而
        // Observe/SyncUi 从未被调用）。启停完全交给 SyncUi。
        _timer = new DispatcherTimer(TickInterval, DispatcherPriority.Background, Tick);
        _timer.Stop();

        _baseIcon = LoadAppIcon();

        var menu = new NativeMenu();
        var open = new NativeMenuItem { Header = "打开主窗口" };
        open.Click += (_, _) => _main.ShowFromTray();
        var report = new NativeMenuItem { Header = "打开上次运行报告" };
        report.Click += (_, _) => (_main.DataContext as MainWindowViewModel)?.OpenPendingReport();
        var exit = new NativeMenuItem { Header = "退出" };
        exit.Click += (_, _) => _main.ForceExit();
        menu.Items.Add(open);
        menu.Items.Add(report);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exit);

        _tray = new TrayIcon
        {
            Icon = _baseIcon,
            ToolTipText = DefaultTooltip,
            Menu = menu,
        };
        _tray.Clicked += (_, _) => _main.ShowFromTray();   // 单击恢复（Unix 常见单击语义；双击 Clicked 同样到达）
    }

    private static WindowIcon? LoadAppIcon()
    {
        try
        {
            using var s = AssetLoader.Open(new Uri("avares://FolderSync/assets/app.ico"));
            using var bmp = new Bitmap(s);
            return new WindowIcon(bmp);
        }
        catch { return null; }
    }

    /// <summary>引擎状态迁移（运行起止都触发 StateChanged）：执行轮开跑即激活，跑完即移除。</summary>
    public void Observe(SyncEngine engine)
    {
        if (_disposed) return;
        if (engine.IsRunning && engine.ActiveRunExecute)
        {
            if (!_active.ContainsKey(engine))
                _active[engine] = new Entry { JobName = engine.Job.Name };
        }
        else
            _active.Remove(engine);
        SyncUi();
    }

    public void Finished(SyncEngine engine)
    {
        if (_disposed) return;
        _active.Remove(engine);
        SyncUi();
    }

    public void Progress(SyncEngine engine, ProgressInfo p)
    {
        if (_disposed) return;
        if (!_active.TryGetValue(engine, out var e))
        {
            if (p.Phase is not ("copy" or "delete")) return;
            e = new Entry { JobName = engine.Job.Name };
            _active[engine] = e;
        }
        if (p.RunSeq != 0 && p.RunSeq < e.RunSeq) return;
        e.RunSeq = p.RunSeq;
        e.Summary = Describe(p);
        SyncUi();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _nudgeTimer?.Stop();
        try { _tray.Dispose(); } catch { }
    }

    private void SyncUi()
    {
        if (_disposed) return;
        if (_active.Count > 0)
        {
            _textDirty = true;
            if (!_timer.IsEnabled)
            {
                _sinceTextRefresh = TextMinInterval;
                _animating = true;
                _frameIndex = FrameCount - 1;
                _timer.Start();
            }
        }
        else
        {
            if (_timer.IsEnabled) _timer.Stop();
            if (_animating)
            {
                _animating = false;
                RestoreIdle();
            }
            ScheduleNudge();
        }
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        // 自愈：事件丢失/晚到/标志残留时按引擎真实状态收敛（IsRunning 且确在执行轮才配转圈）
        if (_active.Count > 0)
        {
            List<SyncEngine>? dead = null;
            foreach (var kv in _active)
                if (!kv.Key.IsRunning || !kv.Key.ActiveRunExecute)
                    (dead ??= new List<SyncEngine>()).Add(kv.Key);
            if (dead != null)
            {
                foreach (var d in dead) _active.Remove(d);
                if (_active.Count == 0) { SyncUi(); return; }
            }
        }

        // 防御：_active 空时不允许任何换帧（哪怕 timer 意外活着），空闲=图标必然静止
        if (_active.Count == 0)
        {
            if (_animating) { _animating = false; RestoreIdle(); }
            return;
        }

        _sinceTextRefresh += TickInterval;
        if (!_framesFailed)
        {
            _frameIndex = (_frameIndex + 1) % FrameCount;
            EnsureAnimIcons();
            var icon = _animIcons?[_frameIndex];
            if (icon != null && !ReferenceEquals(icon, _currentIcon))
            {
                _currentIcon = icon;
                _tray.Icon = icon;
            }
        }
        if (_textDirty && _sinceTextRefresh >= TextMinInterval)
        {
            _sinceTextRefresh = TimeSpan.Zero;
            _textDirty = false;
            ApplyTooltip();
        }
    }

    private void RestoreIdle()
    {
        if (_baseIcon != null)
        {
            _currentIcon = null;   // 绕过相等短路，壳限流期间可能没吃到上一次，每次都真发
            _tray.Icon = _baseIcon;
            _currentIcon = _baseIcon;
        }
        SetTooltip(DefaultTooltip);
    }

    /// <summary>收尾冗余重发：Win11 托盘壳在高频 NIM_MODIFY（动画）后会限流/冻帧，
    /// 动画刚停的 1-2 秒内推静态图会被限流窗口吞掉，壳上残留弧线帧=看起来永远在转。
    /// 对策：在 0.8s / 2s / 4s 三个时间点冗余重发静态图标+tooltip（递增间隔跨过限流窗口），
    /// 每次都清 _currentIcon 短路缓存确保真正下发。</summary>
    private void ScheduleNudge()
    {
        _nudgeTimer?.Stop();
        var delays = new[] { 0.8, 2.0, 4.0 };
        int index = 0;
        void FireNext()
        {
            if (_disposed || index >= delays.Length) return;
            var t = new DispatcherTimer(TimeSpan.FromSeconds(delays[index]), DispatcherPriority.Background, (_, _) =>
            {
                _nudgeTimer?.Stop();
                if (_disposed || _active.Count > 0) return;
                try
                {
                    _currentIcon = null;
                    if (_baseIcon != null) _tray.Icon = _baseIcon;
                    _lastTooltip = "";
                    SetTooltip(DefaultTooltip);
                }
                catch { }
                index++;
                FireNext();
            });
            _nudgeTimer = t;
            t.Start();
        }
        FireNext();
    }

    private void ApplyTooltip()
    {
        string text;
        if (_active.Count == 1)
        {
            text = $"同步中【{First().JobName}】{First().Summary}";
        }
        else
        {
            var sb = new StringBuilder("同步中 ").Append(_active.Count).Append(" 个任务");
            foreach (var e in _active.Values)
                sb.Append('｜').Append(e.JobName).Append(' ').Append(e.Summary);
            text = sb.ToString();
        }
        if (text.Length > TooltipMaxChars) text = text[..(TooltipMaxChars - 1)] + "…";
        SetTooltip(text);
    }

    private Entry First()
    {
        foreach (var e in _active.Values) return e;
        throw new InvalidOperationException("无活动任务");
    }

    private void SetTooltip(string text)
    {
        if (text == _lastTooltip) return;
        _lastTooltip = text;
        _tray.ToolTipText = text;
    }

    // ---------- 动态帧渲染（Skia：RenderTargetBitmap → WindowIcon） ----------

    private void EnsureAnimIcons()
    {
        if (_animIcons != null || _framesFailed) return;
        try
        {
            if (_baseIcon == null) { _framesFailed = true; _animating = false; return; }
            var frames = new WindowIcon[FrameCount];
            for (int i = 0; i < FrameCount; i++)
                frames[i] = new WindowIcon(RenderFrameBitmap(i));
            _animIcons = frames;
        }
        catch
        {
            // 帧构建失败不致命：退化为只更新悬浮文字，图标保持静态（粘性，不重试）
            _animIcons = null;
            _framesFailed = true;
            _animating = false;
        }
    }

    /// <summary>第 i 帧：弧线 + 中心点转圈（底图不可得时用纯弧线动画，视觉语义一致）。</summary>
    private Bitmap RenderFrameBitmap(int i)
    {
        var size = new Avalonia.PixelSize(IconSize, IconSize);
        var dpi = new Avalonia.Vector(96, 96);
        using var rtb = new RenderTargetBitmap(size, dpi);
        using (var dc = rtb.CreateDrawingContext())
        {
            double cx = IconSize / 2.0, cy = IconSize / 2.0;
            double radius = IconSize / 2.0 - 2.5;
            const double sweep = 130;
            var brush = new SolidColorBrush(Color.Parse("#4CC2FF"));   // Fluent 亮蓝
            var pen = new Pen(brush, 2.8) { LineCap = PenLineCap.Round };
            double start = i * 360.0 / FrameCount - 90;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(PointOnCircle(cx, cy, radius, start), false);
                g.ArcTo(PointOnCircle(cx, cy, radius, start + sweep),
                    new Avalonia.Size(radius, radius), 0, false, SweepDirection.Clockwise);
                g.EndFigure(false);
            }
            dc.DrawGeometry(null, pen, geo);
            dc.DrawEllipse(brush, null, new Avalonia.Point(cx, cy), 3.2, 3.2);
        }
        using var ms = new MemoryStream();
        rtb.Save(ms);
        ms.Position = 0;
        return new Bitmap(ms);
    }

    private static Avalonia.Point PointOnCircle(double cx, double cy, double r, double angleDeg)
    {
        var rad = angleDeg * Math.PI / 180.0;
        return new Avalonia.Point(cx + r * Math.Sin(rad), cy - r * Math.Cos(rad));
    }

    /// <summary>按阶段生成短摘要（与主界面进度条同口径，压进一行）。</summary>
    private static string Describe(ProgressInfo p)
    {
        switch (p.Phase)
        {
            case "scan-left":
            case "scan-right":
            case "scan":
                return ScanText(p, p.Phase == "scan-left" ? "扫描左侧" : p.Phase == "scan-right" ? "扫描右侧" : "扫描");
            case "compare":
                return "对比差异…";
            case "copy":
            {
                var pct = p.TotalBytes > 0 ? p.DoneBytes * 100.0 / p.TotalBytes : 0;
                var s = $"复制 {pct:F0}% · {p.DoneItems}/{p.TotalItems} 项 · " +
                        $"{Executor.FormatSize(p.DoneBytes)}/{Executor.FormatSize(p.TotalBytes)}";
                if (p.SpeedBytesPerSec > 1024) s += $" · {Executor.FormatSize((long)p.SpeedBytesPerSec)}/s";
                return s;
            }
            case "delete":
            {
                var pct = p.TotalItems > 0 ? p.DoneItems * 100.0 / p.TotalItems : 0;
                return $"清理删除 {pct:F0}% · {p.DoneItems}/{p.TotalItems} 项";
            }
            case "done":
                return "收尾…";
            default:
                return "处理中…";
        }
    }

    private static string ScanText(ProgressInfo p, string label) =>
        p.TotalItems > 0
            ? $"{label} {p.Percent:F0}% · 已发现 {p.DoneItems:N0} 项"
            : $"{label}中… 已发现 {p.DoneItems:N0} 项";
}
