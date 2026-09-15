using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FolderSync.Core;

namespace FolderSync.App
{
    /// <summary>
    /// 托盘同步指示器：有执行轮在跑时——
    /// ① 悬浮文字实时显示各任务进度（Shell tooltip 单行，事件驱动 + 节流）；
    /// ② 图标切为动态帧（基础 logo 缩小居中 + 外圈圆弧旋转，运行时渲染 8 帧轮换）；
    /// 全部空闲后恢复静态图标与默认文字。
    ///
    /// 实测约束（2026-09-14，Win11 24H2 隔离 E2E）：
    /// · shell 只在有新 NIM_MODIFY 到达时才重渲染托盘图标状态：10Hz 图标+5Hz 文字的更新流会让该图标
    ///   被 shell 整体停刷（永久冻在某帧，同屏流量监控器 1Hz 无此问题）；降到 2.5fps 图标 + 600ms 文字后全程直播。
    /// · 停更后最后一条状态（恢复静态/默认文字）不会被呈现，需停更后冗余重发一次（Nudge）冲刷。
    /// 分析/预览轮（logRun=false，ActiveRunExecute=false）不触发，避免托盘假"同步中"。
    /// 事件可能晚到/丢失（取消路径没有 RunFinished），tick 按引擎 IsRunning 自愈收敛，不会卡死在动画态。
    /// 全部状态仅 UI 线程访问；对外方法均要求先经 Dispatcher 派发。
    /// </summary>
    public sealed class TraySyncIndicator : IDisposable
    {
        private const string DefaultTooltip = "FolderSync — 本地文件夹同步";
        private const int FrameCount = 8;
        private const int IconSize = 32;
        /// <summary>Shell tooltip szTip 上限 128 字符，留余量截到 120。</summary>
        private const int TooltipMaxChars = 120;
        private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(400);
        private static readonly TimeSpan TextMinInterval = TimeSpan.FromMilliseconds(600);
        private static readonly TimeSpan NudgeDelay = TimeSpan.FromSeconds(1.5);

        private readonly Hardcodet.Wpf.TaskbarNotification.TaskbarIcon _tray;
        private readonly BitmapSource? _baseIcon;
        private readonly DispatcherTimer _timer;

        private sealed class Entry
        {
            public string JobName = "";
            /// <summary>已采纳的最大进度轮次序号：丢弃旧轮晚到事件，防旧进度盖新轮。</summary>
            public long RunSeq;
            public string Summary = "准备中…";
        }

        /// <summary>运行中的执行轮：engine → 展示状态。</summary>
        private readonly Dictionary<SyncEngine, Entry> _active = new();

        private System.Drawing.Icon[]? _animIcons;    // 8 动画帧，常驻复用（hardcodet 换 Icon 不释放旧的，
                                                      // 每 tick 新建 HICON 会累计吃光 GDI 上限，故只建一次）
        private System.Drawing.Icon? _baseIconNative; // 静态底图
        private System.Drawing.Icon? _currentIcon;    // 已应用到托盘的图标（去重，省无谓 NIM_MODIFY）
        private bool _framesFailed;                   // 帧构建失败置粘性标志：退化为纯文字，不再重试
        private int _frameIndex;
        private bool _animating;
        private bool _textDirty;
        private TimeSpan _sinceTextRefresh;
        private string _lastTooltip = "";
        private bool _disposed;
        private DispatcherTimer? _nudgeTimer;

        public TraySyncIndicator(Hardcodet.Wpf.TaskbarNotification.TaskbarIcon tray, BitmapSource? baseIcon)
        {
            _tray = tray;
            _baseIcon = baseIcon;
            _timer = new DispatcherTimer(TickInterval, DispatcherPriority.Background, Tick, Dispatcher.CurrentDispatcher);
        }

        /// <summary>引擎状态迁移（运行起止都会触发 StateChanged）：执行轮开跑即激活，跑完即移除。</summary>
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

        /// <summary>RunFinished 兜底清除（触发时 _busy 尚未复位，Observe 会判成仍在运行）。</summary>
        public void Finished(SyncEngine engine)
        {
            if (_disposed) return;
            _active.Remove(engine);
            SyncUi();
        }

        /// <summary>进度事件：刷新对应任务的摘要文案。entry 缺失时 copy/delete 阶段兜底补建
        /// （启动事件万一分丢失，复制中也不该哑掉）。</summary>
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
            try { if (_animating) RestoreIdle(); }
            catch { /* OnExit 收尾时托盘可能已释放 */ }
        }

        private void SyncUi()
        {
            if (_disposed) return;
            if (_active.Count > 0)
            {
                _textDirty = true;
                if (!_timer.IsEnabled)
                {
                    // 刚进入同步态：文字下个 tick 马上刷；帧序号从 0 起
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
            // 自愈：事件丢失/晚到时按引擎真实状态收敛（取消路径没有 RunFinished 就靠这里）
            if (_active.Count > 0)
            {
                List<SyncEngine>? dead = null;
                foreach (var kv in _active)
                    if (!kv.Key.IsRunning) (dead ??= new List<SyncEngine>()).Add(kv.Key);
                if (dead != null)
                {
                    foreach (var d in dead) _active.Remove(d);
                    if (_active.Count == 0)
                    {
                        SyncUi();
                        return;
                    }
                }
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

        /// <summary>恢复静态图标与默认文字（幂等）。</summary>
        private void RestoreIdle()
        {
            if (_baseIconNative != null && !ReferenceEquals(_currentIcon, _baseIconNative))
            {
                _currentIcon = _baseIconNative;
                _tray.Icon = _baseIconNative;
            }
            SetTooltip(DefaultTooltip);
        }

        /// <summary>收尾冗余重发：shell 停更后不渲染最后一条状态，同值强推一次逼它刷新。
        /// hardcodet 的 Icon setter 无同值去重，同引用也会真实走 NIM_MODIFY。</summary>
        private void ScheduleNudge()
        {
            _nudgeTimer?.Stop();
            _nudgeTimer = new DispatcherTimer(NudgeDelay, DispatcherPriority.Background, (_, _) =>
            {
                _nudgeTimer?.Stop();
                if (_disposed || _active.Count > 0) return;
                try
                {
                    if (_baseIconNative != null)
                        _tray.Icon = _baseIconNative;
                    _lastTooltip = "";
                    SetTooltip(DefaultTooltip);
                }
                catch { /* 收尾冲刷失败不致命 */ }
            }, Dispatcher.CurrentDispatcher);
            _nudgeTimer.Start();
        }

        private void ApplyTooltip()
        {
            string text;
            if (_active.Count == 1)
            {
                Entry only = GetFirst();
                text = $"同步中【{only.JobName}】{only.Summary}";
            }
            else
            {
                var sb = new StringBuilder("同步中 ").Append(_active.Count).Append(" 个任务");
                foreach (var e in _active.Values)
                    sb.Append("｜").Append(e.JobName).Append(' ').Append(e.Summary);
                text = sb.ToString();
            }
            if (text.Length > TooltipMaxChars) text = text[..(TooltipMaxChars - 1)] + "…";
            SetTooltip(text);
        }

        private Entry GetFirst()
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

        // ---------- 动态帧渲染：基础 logo 缩小居中，外圈圆弧按 45° 步进旋转 ----------

        private void EnsureAnimIcons()
        {
            if (_animIcons != null || _baseIcon == null || _framesFailed) return;
            try
            {
                _baseIconNative = BuildNativeIcon(_baseIcon);
                var frames = new System.Drawing.Icon[FrameCount];
                for (int i = 0; i < FrameCount; i++)
                    frames[i] = BuildNativeIcon(RenderFrameBitmap(_baseIcon, i));
                _animIcons = frames;
            }
            catch
            {
                // 帧构建失败不致命：退化为只更新悬浮文字，图标保持静态（粘性，不重试）
                _animIcons = null;
                _baseIconNative = null;
                _framesFailed = true;
                _animating = false;
            }
        }

        /// <summary>第 i 帧位图：底图缩至 23px 居中 + 亮蓝 130° 圆弧（半径 13.5）按 45° 步进旋转。</summary>
        private static BitmapSource RenderFrameBitmap(ImageSource baseIcon, int i)
        {
            double cx = IconSize / 2.0, cy = IconSize / 2.0;
            double radius = IconSize / 2.0 - 2.5;
            const double sweep = 130;
            var brush = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF));   // Fluent 亮蓝：深色任务栏上比主界面蓝更醒目
            brush.Freeze();
            var pen = new Pen(brush, 2.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawImage(baseIcon, new Rect(4.5, 4.5, IconSize - 9, IconSize - 9));
                double start = i * 360.0 / FrameCount - 90;   // 帧首起点在正上方，顺时针转
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(PointOnCircle(cx, cy, radius, start), false, false);
                    g.ArcTo(PointOnCircle(cx, cy, radius, start + sweep),
                        new Size(radius, radius), 0, isLargeArc: false,
                        SweepDirection.Clockwise, true, false);
                }
                geo.Freeze();
                dc.DrawGeometry(null, pen, geo);
            }
            var bmp = new RenderTargetBitmap(IconSize, IconSize, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>BitmapSource → 自有所有权的 Icon（Clone 出独立副本后即销毁临时 HICON）。</summary>
        private static System.Drawing.Icon BuildNativeIcon(BitmapSource src)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using var ms = new MemoryStream();
            enc.Save(ms);
            ms.Position = 0;
            using var bmp = new System.Drawing.Bitmap(ms);
            var h = bmp.GetHicon();
            try
            {
                using var fromHandle = System.Drawing.Icon.FromHandle(h);
                return (System.Drawing.Icon)fromHandle.Clone();
            }
            finally
            {
                DestroyIcon(h);
            }
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private static Point PointOnCircle(double cx, double cy, double r, double angleDeg)
        {
            var rad = angleDeg * Math.PI / 180.0;
            return new Point(cx + r * Math.Sin(rad), cy - r * Math.Cos(rad));
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

        private static string ScanText(ProgressInfo p, string label)
        {
            return p.TotalItems > 0
                ? $"{label} {p.Percent:F0}% · 已发现 {p.DoneItems:N0} 项"
                : $"{label}中… 已发现 {p.DoneItems:N0} 项";
        }
    }
}
