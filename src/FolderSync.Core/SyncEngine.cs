using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FolderSync.Core
{
    /// <summary>
    /// 单任务同步引擎：扫描 → 差异 → 执行，支持手动/实时/定时触发。
    /// 同一任务串行执行（信号量），实时事件去抖后触发。
    /// </summary>
    public class SyncEngine : IDisposable
    {
        private readonly Db _db;
        private readonly SemaphoreSlim _runLock = new(1, 1);
        // 当前轮次的软取消源：UI「停止」经 RequestCancel() 可打断任何来源（手动/实时/定时）的轮次。
        // _busy 闸保证同时最多一轮，写读无竞争；RequestCancel 与轮次收尾的竞态由 catch ODE 兜底
        private CancellationTokenSource? _activeRunCts;

        /// <summary>请求取消当前正在执行的同步轮次（不分触发来源；无轮次在跑则无效果）。</summary>
        public void RequestCancel()
        {
            var cts = _activeRunCts;
            if (cts != null) try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        // watcher 集合不可变替换（CheckMedia 在 timer 线程、ApplyTriggers 在 UI 线程都会换装，
        // 普通 List 并发 Add/Clear 会炸；数组原子交换 + 锁内瞬时替换，Dispose 在锁外防死锁）
        private IDisposable[] _watchers = Array.Empty<IDisposable>();  // 双向时监听两侧根目录
        private readonly object _triggerGate = new();
        private volatile bool _disposed;
        private bool _lastRunCompleted;   // 上轮是否正常收尾：false 时下轮 ExecuteAsync 全扫 tmp 崩溃残留
        private Timer? _intervalTimer;
        private Timer? _debounceTimer;
        private Timer? _mediaTimer;        // 介质轮询（热插拔监控），独立于 DisposeTriggers 生命周期
        private volatile bool _mediaMissing;
        private DateTime _lastReconnectAt = DateTime.MinValue;  // 恢复补跑防抖
        /// <summary>介质轮询间隔（测试可调；产品默认 10s）</summary>
        internal int MediaPollSeconds { get; set; } = 10;
        private readonly object _gate = new();
        private int _busy; // 0=idle, 1=running
        private long _progressSeq;   // 进度回调轮次：Progress<T>.Report 异步投递，晚到的旧轮回调会把
                                     // 已收尾的 StatusText 盖回中间态（如「正在对比两侧差异…」），按序号丢弃

        // ---- H-1：轮次执行期间到达的实时变更不再丢弃 ----
        // 旧行为：IsRunning 时去抖定时器照排，到期 RunQuiet 撞上 _busy 抛 InvalidOperationException 被静默吞——
        // 单向任务只监听源侧，执行器写目标侧不触发监听，执行期间的用户变更就此永久丢失。
        // 现：IsRunning 时只记 pending 不排定时器，轮次收尾（外层 finally，_busy 已复位）补排一轮。
        private int _realtimePending;

        // ---- M-5：自写抑制窗口 ----
        // 双向任务执行后监听两侧，自己写的目标文件必然再触发一轮全量空扫描（CPU/IO 近乎翻倍）。
        // 执行期间+结束后尾巴窗口内的监听事件只记 pending 不排去抖；窗口过后有 pending 再补一次
        // ScheduleDebounce（窗口内混有用户变更时不能丢——正确性优先，纯自写时多一次空扫描是可接受代价）。
        // 必须在 H-1（补跑机制）之后上线：拿掉"意外兜底"后丢事件更严重。
        private long _selfWriteUntilTicks;
        private volatile bool _selfWritePending;
        /// <summary>自写抑制尾巴（执行结束后再吞多久的监听事件；USN watcher 轮询 1s，3s 足够消化完自写记录）。测试可调。</summary>
        internal static TimeSpan SelfWriteTail = TimeSpan.FromSeconds(3);

        /// <summary>镜像删除安全阀（S-1）：删除数占目标侧条目比超过该值时本轮禁用删除。测试可调。</summary>
        internal static double MirrorDeleteSafetyRatio = 0.30;
        /// <summary>比例阈值的绝对豁免：删除总数低于此值时不启用比例判断（个人工具删几个文件常见，批量误删才是灾难）。测试可调。</summary>
        internal static int MirrorDeleteMinBulk = 50;

        public SyncJob Job { get; private set; }
        public JobStatus Status { get; private set; } = JobStatus.Idle;
        public string StatusText { get; set; } = "就绪";
        public DateTime? LastRunAt { get; private set; }
        public string? LastError { get; private set; }

        private int _watcherFireCount;
        /// <summary>测试诊断：监视器触发次数（双向两 watcher 并发递增，Interlocked 防丢计数）</summary>
        public int WatcherFireCount => Volatile.Read(ref _watcherFireCount);
        /// <summary>上轮计划中的待裁决冲突数（双向 Manual 冲突 + 大小写冲突 + 位腐冲突行——
        /// 都是 Conflict 行、都要人工裁决，托盘气泡「N 项冲突待人工裁决」对三者语义一致）</summary>
        public int LastConflictCount { get; private set; }
        public string? LastWarning { get; private set; }    // 非致命警告（如目标盘空间不足）

        // mtime 比对容差轮内缓存：Differ.MtimeToleranceMs 每次做 DriveFormat IO（网络路径还可能抛异常），
        // 一轮差异/移动检测/深度校验要调 3-4 次。-1=未算；UpdateJob 换路径时失效
        private long _mtimeTolMs = -1;
        internal long MtimeTolMs()
        {
            var v = _mtimeTolMs;
            if (v >= 0) return v;
            try { v = Differ.MtimeToleranceMs(Job.LeftPath, Job.RightPath); }
            catch { v = 50; }
            _mtimeTolMs = v;
            return v;
        }

        /// <summary>最近一轮执行失败明细（供 UI 直读 / 重试）。每轮执行后刷新。</summary>
        public IReadOnlyList<FailedItem> LastFailedItems { get; private set; } =
            Array.Empty<FailedItem>();
        /// <summary>最近一轮失败总数（含超出明细封顶的部分）</summary>
        public int LastFailedTotal { get; private set; }
        /// <summary>最近一轮失败明细落盘文件（null=无失败或未落盘）</summary>
        public string? LastFailedLogFile { get; private set; }

        /// <summary>上次扫描的两侧统计（非目录文件数/总字节），UI 展示用</summary>
        public (int leftFiles, int rightFiles, long leftBytes, long rightBytes) LastScanStats { get; private set; }

        /// <summary>最近一次扫描的双侧完整快照（供 UI 文件夹树懒加载浏览）；key 为 '/' 分隔的相对路径。</summary>
        public IReadOnlyDictionary<string, FileEntry>? LastLeftScan { get; private set; }
        public IReadOnlyDictionary<string, FileEntry>? LastRightScan { get; private set; }

        public event Action<SyncEngine, ProgressInfo>? Progress;
        public event Action<SyncEngine>? StateChanged;
        public event Action<SyncEngine, RunRecord>? RunFinished;

        public SyncEngine(SyncJob job, Db db)
        {
            Job = job;
            _db = db;
        }

        // 运行中到达的 UpdateJob 排队到轮次收尾生效：轮次中途整体替换 Job 引用会让
        // 在跑轮次「扫描用旧路径、删除判定用新配置」混合（一轮内读到两个版本的配置）
        private SyncJob? _pendingJob;
        private readonly object _jobUpdateGate = new();   // 「读 busy/写 pending」与收尾「取 pending」互斥，防丢更新

        public void UpdateJob(SyncJob job)
        {
            lock (_jobUpdateGate)
            {
                if (Volatile.Read(ref _busy) == 1)
                {
                    Volatile.Write(ref _pendingJob, job);   // 收尾 finally（_busy 复位后）应用
                    return;
                }
                Job = job;
                _mtimeTolMs = -1;   // 路径可能换卷：mtime 容差缓存失效
            }
            ApplyTriggers();
        }

        /// <summary>应用触发配置：实时监听 + 定时器 + 介质轮询。</summary>
        public void ApplyTriggers()
        {
            if (_disposed) return;
            DisposeTriggers();
            if (!Job.Enabled) { StopMediaTimer(); RaiseChanged(); return; }

            // 介质轮询（10s）：目标卷热插拔监控——消失卸监听防错误风暴，恢复自动重挂+补跑。
            // 无论触发类型都开（手动任务至少状态可见）。
            StopMediaTimer();
            var poll = TimeSpan.FromSeconds(Math.Max(1, MediaPollSeconds));
            _mediaTimer = new Timer(CheckMedia, null, poll, poll);

            if (Job.Trigger == TriggerType.Realtime)
            {
                var roots = Job.Direction == SyncDirection.TwoWay
                    ? new[] { Job.LeftPath, Job.RightPath }
                    : new[] { Job.Direction.ToRight() ? Job.LeftPath : Job.RightPath };

                if (roots.Any(r => string.IsNullOrWhiteSpace(r) || !Directory.Exists(r)))
                {
                    StatusText = "监听失败: 路径无效";
                    RaiseChanged();
                    return;
                }
                var fresh = new List<IDisposable>();
                foreach (var watchRoot in roots)
                {
                    try
                    {
                        // 平台工厂（Win: USN 优先，构造失败内部降级 FSW；Unix: inotify/kqueue 转正）
                        var (watcher, describe) = Core.Platform.Platform.Watchers.Create(watchRoot,
                            Job.ExcludePatterns,
                            onChange: () => { Interlocked.Increment(ref _watcherFireCount); OnWatchedChange(); },
                            // 监控通道断档（USN journal 回绕/禁用/重置、FSW 缓冲区溢出）会跳过中间的变更记录：
                            // 必须立刻触发一轮全量对比兜底，否则间隔期的变更要等下一个不相干事件
                            onOverflow: () => OnWatchedChange(),
                            // USN 连续重定位失败彻底放弃时回调：不通知则状态栏永远停在「实时监听中」
                            onDead: () =>
                            {
                                StatusText = "实时监听已停止（USN 日志不可用），文件变更不再自动触发，可重新启用任务或改用定时";
                                RaiseChanged();
                            });
                        fresh.Add(watcher);
                        StatusText = describe;   // 「实时监听中（USN）」/「实时监听中（FSW 兜底）」/「实时监听中（inotify/kqueue）」
                    }
                    catch (Exception ex)
                    {
                        StatusText = $"监听失败: {ex.Message}";
                    }
                }
                PublishWatchers(fresh);
            }

            // 纵深防御：空路径任务不排定时器（到期 RunAsync 只会撞路径守卫反复报错）
            if (Job.Trigger == TriggerType.Interval && Job.IntervalSeconds > 0
                && !JobManager.HasInvalidPath(Job))
            {
                lock (_triggerGate)
                {
                    _intervalTimer = new Timer(_ => RunQuiet("interval"), null,
                        TimeSpan.FromSeconds(Job.IntervalSeconds),
                        TimeSpan.FromSeconds(Job.IntervalSeconds));
                }
                StatusText = $"定时 {Job.IntervalSeconds}s";
            }
            RaiseChanged();
        }

        /// <summary>原子发布新 watcher 集合；与 Dispose 竞态时就地回收（防触发器复活泄漏）。</summary>
        private void PublishWatchers(List<IDisposable> fresh)
        {
            IDisposable[]? discard = null;
            lock (_triggerGate)
            {
                if (_disposed) discard = fresh.ToArray();
                else _watchers = fresh.ToArray();
            }
            if (discard != null)
                foreach (var w in discard) w.Dispose();
        }

        /// <summary>监听事件入口：自写抑制窗口内只记 pending，窗口外正常去抖。</summary>
        private void OnWatchedChange()
        {
            if (_disposed) return;
            if (DateTime.UtcNow.Ticks < Volatile.Read(ref _selfWriteUntilTicks))
            {
                _selfWritePending = true;   // 可能混有用户变更：窗口过后补偿（见 EndSelfWriteSuppress）
                return;
            }
            ScheduleDebounce();
        }

        private void ScheduleDebounce()
        {
            if (_disposed || Job.Trigger != TriggerType.Realtime) return;
            lock (_gate)
            {
                // H-1：轮次在跑时不排定时器（到期也只会撞 _busy 被吞），只记 pending，
                // 由轮次外层 finally 的 TrySchedulePendingRerun 补跑
                if (IsRunning)
                {
                    Volatile.Write(ref _realtimePending, 1);
                    return;
                }
                var now = DateTime.UtcNow;
                // 首次触发记录起点；去抖每次重置 timer，但总等待不超过 60s（防事件流饿死：
                // 事件间隔持续小于 DebounceSeconds 时滚动重排永不到期）。
                // 判 elapsed 必须在重置起点之前——旧写法先重置再判，差值恒 0，「超 60s 立即执行」永不可达
                if (_debounceFirst == null) _debounceFirst = now;
                double due;
                if ((now - _debounceFirst.Value).TotalSeconds > 60)
                {
                    due = 0.5;   // 超过最大等待，立即执行
                    _debounceFirst = now;   // 起点重置只发生在立即执行路径
                }
                else due = Job.DebounceSeconds;
                _debounceTimer?.Dispose();
                _debounceTimer = new Timer(_ =>
                {
                    lock (_gate) _debounceFirst = null;   // C-2：本轮去抖已到期开跑，起点清零——
                    // 不清的话静默 >60s 后新事件流的第一个事件 elapsed>60 → due=0.5s 抢跑，
                    // 用户配置的 DebounceSeconds 恰在最需要时失效（拷大文件 0.5s 即开跑撞共享冲突）
                    RunQuiet("realtime");
                }, null, TimeSpan.FromSeconds(Math.Max(0.5, due)), Timeout.InfiniteTimeSpan);
            }
        }

        private DateTime? _debounceFirst;

        /// <summary>H-1 补跑：轮次外层 finally（_busy 已复位）调用——执行期间有 pending 变更则补排一轮实时同步。</summary>
        private void TrySchedulePendingRerun()
        {
            if (Interlocked.Exchange(ref _realtimePending, 0) != 1) return;
            if (_disposed || Job.Trigger != TriggerType.Realtime || !Job.Enabled) return;
            lock (_gate)
            {
                _debounceFirst = null;   // 补跑是新一轮去抖的起点
                _debounceTimer?.Dispose();
                _debounceTimer = new Timer(_ => RunQuiet("realtime"),
                    null, TimeSpan.FromSeconds(Math.Max(0.5, Job.DebounceSeconds)), Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>M-5：执行阶段开始——无限抑制（自写事件全吞），并清掉上一轮遗留的 pending。</summary>
        private void BeginSelfWriteSuppress()
        {
            _selfWritePending = false;
            Volatile.Write(ref _selfWriteUntilTicks, DateTime.MaxValue.Ticks);
        }

        /// <summary>M-5：执行结束——尾巴窗口（SelfWriteTail）后检查 pending 补一次去抖（正确性：窗口内可能混有用户变更）。</summary>
        private void EndSelfWriteSuppress()
        {
            var until = DateTime.UtcNow + SelfWriteTail;
            Volatile.Write(ref _selfWriteUntilTicks, until.Ticks);
            _ = Task.Run(async () =>
            {
                await Task.Delay(SelfWriteTail + TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                if (_disposed) return;
                if (_selfWritePending && DateTime.UtcNow.Ticks >= Volatile.Read(ref _selfWriteUntilTicks))
                {
                    _selfWritePending = false;
                    OnWatchedChange();   // 窗口已过：正常路径排去抖（可能又撞上新一轮执行——ScheduleDebounce 自己会记 pending）
                }
            });
        }

        /// <summary>介质轮询回调：两侧根任一不可用 → 卸监听切 WaitingMedia；恢复 → 重挂监听 + 补跑一轮。</summary>
        private void CheckMedia(object? _)
        {
            try
            {
                if (_disposed || !Job.Enabled) return;
                bool missing = string.IsNullOrWhiteSpace(Job.LeftPath) || string.IsNullOrWhiteSpace(Job.RightPath)
                    || !Directory.Exists(Job.LeftPath) || !Directory.Exists(Job.RightPath);

                if (missing && !_mediaMissing)
                {
                    _mediaMissing = true;
                    DisposeTriggers();   // 卸 USN/FSW/定时器，防轮询错误风暴（_mediaTimer 不在其中）
                    Status = JobStatus.WaitingMedia;
                    StatusText = "等待介质（路径不可用）";
                    RaiseChanged();
                }
                else if (!missing && _mediaMissing)
                {
                    _mediaMissing = false;
                    Status = JobStatus.Idle;
                    StatusText = "介质已恢复，重新挂载监听…";
                    RaiseChanged();
                    ApplyTriggers();   // 重挂 watcher / 定时器（不动 _mediaTimer；内部自查 _disposed）
                    // 补跑一轮 reconnect（容错静默，同启动补跑语义）；10s 防抖防盘慢速挂载抖动
                    if ((DateTime.Now - _lastReconnectAt).TotalSeconds >= 10)
                    {
                        _lastReconnectAt = DateTime.Now;
                        RunQuiet("reconnect");
                    }
                }
            }
            catch { /* 轮询自身永不抛 */ }
        }

        private void StopMediaTimer()
        {
            _mediaTimer?.Dispose();
            _mediaTimer = null;
        }

        // ==================== 三执行入口公共骨架（RunAsync / ExecutePlanAsync / RunPlanAsync） ====================
        // 三入口曾各持 ~120 行近乎相同的骨架（锁/取消/落库/进度/收尾/异常），
        // "一处修两处漏"反复发生（M-7 漏回填 DeletedCount 是实锤）——差异收敛到这里，入口只留中段语义。

        /// <summary>公共轮次骨架：busy 已由入口抢占，这里管 runLock 串行 + 软取消链接 + runs 落库 +
        /// 进度回调（序号过滤）+ 成功/取消/异常三类收尾。body=各入口的中段差异逻辑，
        /// 返回 (计划, 终态文案)，收尾（AvgSpeed/落库/LastRunAt/RunFinished）由骨架统一完成。</summary>
        private async Task<(RunRecord record, List<PlanEntry> plan)> RunGuardedAsync(
            string trigger, bool logRun, CancellationToken ct,
            Func<RunRecord, CancellationToken, IProgress<ProgressInfo>, Task<(List<PlanEntry> plan, string finalStatusText)>> body)
        {
            var record = new RunRecord { JobId = Job.Id, StartedAt = DateTime.Now, Trigger = trigger };
            try
            {
                // 跨进程防重入（v1.6 B4）：CLI 与 GUI 并存时同任务并发执行会数据竞争，
                // _busy 只挡本引擎——named Mutex 按 jobId 全局互斥。持有到轮次结束（Dispose 关句柄即释放；
                // 不用 ReleaseMutex：await 跨线程违反 Mutex 线程亲和）；进程被杀由 OS 句柄回收自动释放
                _jobRunLock = TryAcquireJobLock();
                if (_jobRunLock == null)
                    throw new InvalidOperationException("任务正在运行中（另一进程/实例）");

                await _runLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    // 软取消：轮次 token 同时受外部 ct 与 RequestCancel() 控制
                    using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    _activeRunCts = runCts;
                    var token = runCts.Token;
                    if (logRun) record.Id = _db.InsertRun(record);

                    // 托盘同步态判定：执行轮（logRun=true）才显示动画。曾只在 RunAsync 设置——
                    // 执行计划/裁决轮残留上轮的 0，托盘对这两类轮次不进"同步中"态
                    Volatile.Write(ref _activeRunExecute, logRun ? 1 : 0);

                    var progSeq = Interlocked.Increment(ref _progressSeq);
                    IProgress<ProgressInfo> progress = new Progress<ProgressInfo>(p =>
                    {
                        if (Volatile.Read(ref _progressSeq) != progSeq) return;   // 旧轮晚到回调丢弃
                        p.RunSeq = progSeq;   // UI 侧二次过滤：已 BeginInvoke 排队的晚到事件按序号丢弃
                        StatusText = p.CurrentItem;
                        WriteStateFile(p);
                        Progress?.Invoke(this, p);
                    });

                    var (plan, finalStatusText) = await body(record, token, progress).ConfigureAwait(false);

                    record.FinishedAt = DateTime.Now;
                    record.AvgSpeedBytesPerSec = record.BytesCopied /
                        Math.Max((record.FinishedAt.Value - record.StartedAt).TotalSeconds, 0.1);
                    if (logRun) { _db.FinishRun(record); _db.CleanupOldRuns(); }

                    LastRunAt = DateTime.Now;
                    Status = JobStatus.Idle;
                    StatusText = finalStatusText;
                    RaiseChanged();
                    if (logRun) RunFinished?.Invoke(this, record);
                    return (record, plan);
                }
                catch (OperationCanceledException)
                {
                    // 用户取消不是错误：runs 落 cancelled、状态回 Idle（UI 层 catch 后另有「已取消」日志）
                    _lastRunCompleted = false;
                    record.Status = "cancelled";
                    record.FinishedAt = DateTime.Now;
                    if (logRun) try { _db.FinishRun(record); } catch { }
                    Status = JobStatus.Idle;
                    StatusText = $"已取消 {DateTime.Now:HH:mm:ss}";
                    RaiseChanged();
                    throw;
                }
                catch (Exception ex)
                {
                    _lastRunCompleted = false;   // 异常退出：下轮全扫 tmp 崩溃残留
                    Status = JobStatus.Error;
                    LastError = ex.Message;
                    record.ErrorMessage = ex.Message;
                    record.Status = "error";   // 异常路径的 runs 终态（FinishRun 未走到时至少内存态完整）
                    StatusText = $"错误: {ex.Message}";
                    RaiseChanged();
                    throw;
                }
                finally
                {
                    _activeRunCts = null;   // 先撤登记再放锁：RequestCancel 此后拿不到本轮引用
                    _runLock.Release();
                }
            }
            finally
            {
                _jobRunLock?.Dispose();   // 关句柄/关 fd 即释放跨进程互斥（Mutex 线程亲和约束下不走 ReleaseMutex）
                _jobRunLock = null;
                Interlocked.Exchange(ref _busy, 0);
                Volatile.Write(ref _activeRunExecute, 0);   // 托盘动画标志随轮次终结：不复位则 _busy 先清的窗口里 Observe 会用上轮残留误激活
                Interlocked.Increment(ref _progressSeq);   // 作废本轮进度回调：晚到的不再覆盖收尾状态
                TrySchedulePendingRerun();                 // H-1：执行期间到达的实时变更补跑（_busy 已复位，不会撞车）
                // UpdateJob 运行中排队的新配置在此生效（_busy 已复位，ApplyTriggers 不再与在跑轮次竞争）
                SyncJob? pending;
                lock (_jobUpdateGate)
                {
                    pending = Volatile.Read(ref _pendingJob);
                    Volatile.Write(ref _pendingJob, null);
                }
                if (pending != null && !_disposed)
                {
                    Job = pending;
                    _mtimeTolMs = -1;
                    try { ApplyTriggers(); } catch { }
                }
            }
        }

        private IDisposable? _jobRunLock;

        /// <summary>抢同任务的全局执行互斥（null=另一进程/实例正在跑该任务）。
        /// Windows：named Mutex——进程退出内核对象即销毁，AbandonedMutexException（上个持有进程被强杀）
        /// 时本调用已获得所有权，照常持有。
        /// Unix：flock 文件锁——.NET named Mutex 的 Unix 模拟层在进程快起快停场景误判持锁者仍存活
        /// （CI 套件连跑实测大面积误报「另一实例」）；flock 由内核管理，进程退出/被杀必然释放。</summary>
        private IDisposable? TryAcquireJobLock()
        {
            if (OperatingSystem.IsWindows())
            {
                var m = new Mutex(true, $"FolderSync_JobRun_{Job.Id}", out var mine);
                if (mine) return m;
                try
                {
                    if (!m.WaitOne(0)) { m.Dispose(); return null; }
                    return m;   // 上个持有者释放后抢到
                }
                catch (AbandonedMutexException)
                {
                    return m;   // 被杀残留：本调用已获得所有权
                }
            }
            return TryFlockLock(Core.Platform.AppPaths.LockFile(Job.Id));
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int flock(int fd, int operation);

        private const int LOCK_EX = 2, LOCK_NB = 4;   // BSD flock 常量（Linux/macOS 一致）

        /// <summary>flock 独占非阻塞抢锁：fd 开着锁就在，Dispose 关 fd 即释放；进程死亡内核回收。</summary>
        private static IDisposable? TryFlockLock(string lockPath)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
                var fs = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite);
                if (flock(fs.SafeFileHandle.DangerousGetHandle().ToInt32(), LOCK_EX | LOCK_NB) == 0)
                    return fs;
                fs.Dispose();
                return null;
            }
            catch { return null; }
        }

        /// <summary>执行器调用包裹：M-5 自写抑制 + tmp 清扫策略 + 正常收尾标记。
        /// 返回执行器本体（DeletedCount/失败明细/DiskFull 等统计还要从它读）。</summary>
        private async Task<(Executor executor, int ok, int skipped, int failed, long bytes)> RunExecutorAsync(
            List<PlanEntry> plan, IProgress<ProgressInfo> progress, CancellationToken token)
        {
            var executor = new Executor(Job);
            BeginSelfWriteSuppress();   // M-5：执行期间两侧监听吞自写事件
            try
            {
                var r = await executor.ExecuteAsync(plan, progress, token, sweepTmpResidue: !_lastRunCompleted)
                    .ConfigureAwait(false);
                _lastRunCompleted = true;   // 正常收尾（含 partial）：下轮跳过 tmp 全树清扫
                return (executor, r.ok, r.skipped, r.failed, r.bytes);
            }
            finally
            {
                EndSelfWriteSuppress();   // 尾巴窗口后按 pending 补偿（H-1 语义：执行期间变更不丢）
            }
        }

        /// <summary>执行器结果 → record 统计与状态（ok/partial/error=磁盘满）+ 失败明细捕获落盘 + 警告转发。
        /// 裁决入口曾缺磁盘满→error 判定（记成 partial），统一走本方法。</summary>
        private void ApplyExecutorStats(RunRecord record, Executor executor,
            int ok, int skipped, int failed, long copiedBytes)
        {
            record.CopiedFiles = ok;
            record.SkippedFiles = skipped;
            record.FailedFiles = failed;
            record.BytesCopied = copiedBytes;
            record.DeletedFiles = executor.DeletedCount;
            record.DeltaSavedBytes = executor.DeltaSavedBytes;
            record.RetriedOk = executor.RetriedOk;
            record.MovedFiles = executor.MovedCount;
            if (executor.LastWarning != null) LastWarning = executor.LastWarning;
            CaptureFailures(record, executor);
            if (executor.DiskFull)
            {
                record.Status = "error";
                LastError = executor.LastError;
                record.ErrorMessage = executor.LastError;   // runs.error_message 曾从不赋值，历史窗口查不到失败原因
            }
            else if (failed > 0)
            {
                record.Status = "partial";
                LastError = executor.LastError;
                record.ErrorMessage = executor.LastError;
            }
            else record.Status = "ok";
        }

        /// <summary>执行成功后的重扫：重建快照基线（v1.5 起全部方向——移动检测配对依据）；
        /// 刷新 UI 扫描快照（P-6：后置分析免再扫）；基线懒对账。</summary>
        private async Task RescanAfterSuccessAsync(List<PlanEntry> executedPlan, CancellationToken token)
        {
            var left = await Scanner.ScanTreeAsync(Job.LeftPath, Job.ExcludePatterns, null, token, "left").ConfigureAwait(false);
            var right = await Scanner.ScanTreeAsync(Job.RightPath, Job.ExcludePatterns, null, token, "right").ConfigureAwait(false);
            _db.SaveSnapshot(Job.Id, Differ.BuildSnapshot(left.Entries, right.Entries, executedPlan).Values);
            await RefreshUiScanState(left.Entries, right.Entries).ConfigureAwait(false);
            ScheduleBaselineReconcile(Job.LeftPath, Job.RightPath, left.Entries, right.Entries);
        }

        /// <summary>执行一轮：扫描→差异→执行→记录。返回完整计划供 UI 预览。
        /// logRun=false 时只分析不落运行历史（UI 自动预览用，避免污染 runs 表）。</summary>
        public async Task<(RunRecord record, List<PlanEntry> plan)> RunAsync(string trigger,
            bool execute = true, CancellationToken ct = default, bool logRun = true)
        {
            ValidatePathsForRun();
            // S-1 入口前置校验：源侧根不存在时（盘未挂载/网络断开）空扫描结果配上
            // MirrorDelete 就是目标整盘删除——直接拒绝执行（CheckMedia 仅 10s 轮询且只保护 Enabled 任务，
            // 轮询间隔内的手动轮次不受保护）。目标侧缺失是首建场景，允许（执行器会创建）。
            var srcRootMissing = Job.Direction switch
            {
                SyncDirection.TwoWay => !Directory.Exists(Job.LeftPath) && !Directory.Exists(Job.RightPath),   // 双向：两侧都缺才拒
                _ => !Directory.Exists(Job.Direction.ToRight() ? Job.LeftPath : Job.RightPath)   // 单向类（镜像/备份）：源侧必须存在
            };
            if (srcRootMissing && execute)
                throw new DirectoryNotFoundException(
                    $"源侧目录不存在: {(Job.Direction.ToRight() ? Job.LeftPath : Job.RightPath)}（拒绝以空扫描结果执行同步，请检查磁盘/网络）");

            if (Interlocked.Exchange(ref _busy, 1) == 1)
                throw new InvalidOperationException("任务正在运行中");
            return await RunGuardedAsync(trigger, logRun, ct, async (record, token, progress) =>
            {
                Status = JobStatus.Scanning;
                StatusText = "扫描中…";
                LastError = null;
                LastWarning = null;   // 警告是本轮语义：隔轮残留会让任务卡永远挂着旧 ⚠
                RaiseChanged();

                List<PlanEntry> plan;
                var sw = Stopwatch.StartNew();
                var leftScan = await Scanner.ScanTreeAsync(Job.LeftPath, Job.ExcludePatterns, progress, token, "left").ConfigureAwait(false);
                Status = JobStatus.Analyzing;
                var rightScan = await Scanner.ScanTreeAsync(Job.RightPath, Job.ExcludePatterns, progress, token, "right").ConfigureAwait(false);
                var left = leftScan.Entries;
                var right = rightScan.Entries;
                LastLeftScan = left;
                LastRightScan = right;
                LastScanFresh = false;   // 这是执行前的扫描（ExecutePlanAsync/RunPlanAsync 成功后会用执行后重扫刷新）

                // ---- S-1 镜像删除安全阀：扫描不完整 / 删除比例超阈值 → 本轮禁用删除 ----
                Dictionary<string, FileEntry>? twoWaySnapshot = Job.Direction == SyncDirection.TwoWay
                    ? _db.GetSnapshot(Job.Id) : null;
                var allowMirrorDelete = Job.MirrorDelete;
                if (allowMirrorDelete)
                {
                    var scanComplete = leftScan.Complete && rightScan.Complete;
                    if (!scanComplete)
                    {
                        allowMirrorDelete = false;
                        var why = leftScan.RootMissing || rightScan.RootMissing ? "根目录不可用" : "部分位置无法访问";
                        LastWarning = $"扫描不完整（{why}），本轮已自动禁用镜像删除——源侧子树被 ACL/IO 挡住时缺失条目会被误判为已删除";
                    }
                    else
                    {
                        // 删除数预估：单向=目标侧孤儿数；双向=快照中已从一侧消失的条目数（O(n) 一次遍历）
                        int potentialDeletes;
                        int denominator;
                        if (twoWaySnapshot != null)
                        {
                            var snap = twoWaySnapshot;
                            potentialDeletes = snap.Keys.Count(rel =>
                                (!left.ContainsKey(rel) && right.ContainsKey(rel)) ||
                                (!right.ContainsKey(rel) && left.ContainsKey(rel)));
                            denominator = Math.Max(left.Count, right.Count);
                        }
                        else
                        {
                            var (srcSide, dstSide) = Job.Direction.ToRight()
                                ? (left, right) : (right, left);
                            potentialDeletes = dstSide.Keys.Count(rel => !srcSide.ContainsKey(rel));
                            denominator = dstSide.Count;
                        }
                        if (potentialDeletes >= MirrorDeleteMinBulk &&
                            denominator > 0 &&
                            (double)potentialDeletes / denominator > MirrorDeleteSafetyRatio)
                        {
                            allowMirrorDelete = false;
                            LastWarning = $"本轮删除 {potentialDeletes} 项占目标侧 {denominator} 项的 {(double)potentialDeletes / denominator:P0}，" +
                                $"超过 {MirrorDeleteSafetyRatio:P0} 安全阈值——已自动禁用镜像删除，请人工核对源侧是否真的删除了这些文件";
                        }
                    }
                }

        // 对比阶段：大任务树差异计算可能耗时数秒，明确报阶段避免 UI 假死
                progress.Report(new ProgressInfo { Phase = "compare", CurrentItem = "正在对比两侧差异…" });
                if (twoWaySnapshot != null)
                    plan = Differ.ComputeTwoWay(Job, left, right, twoWaySnapshot, allowMirrorDelete);
                else
                    plan = Differ.Compute(Job, left, right, allowMirrorDelete);
                // B3：单侧内部仅大小写不同的实际名对（大小写敏感卷防御，NTFS 恒空）显式标人工，不静默归并
                AppendSideCaseConflicts(plan, leftScan.CaseConflicts, "左侧", left, right);
                AppendSideCaseConflicts(plan, rightScan.CaseConflicts, "右侧", left, right);
                // A3 移动检测（后处理趟）：快照可证的 (删除+新建) 配对替换为目标侧 rename。
                // 快照双向本就持有；单向从 v1.5 起同样维护（下方 SaveSnapshot 无方向限制）
                plan = MoveDetection.Apply(Job, plan,
                    twoWaySnapshot ?? _db.GetSnapshot(Job.Id), left, right,
                    MtimeTolMs());
                // B1 深度校验（执行轮，三档）：判无差异的文件比对内容哈希，位腐产冲突行
                // （好坏未知，双向/单向统一人工裁决——单向不再按源覆盖抛硬币）
                if (Job.DeepVerify > 0 && execute)
                {
                    var rot = DeepVerifyScan(Job, left, right,
                        Job.DeepVerify == 2 ? 0 : DeepVerifyMinBytes, MtimeTolMs(), progress, token);
                    if (rot.Count > 0)
                    {
                        plan.AddRange(rot);
                        plan = plan.OrderBy(p => p.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
                        // C-5：拼接而非覆盖——上面的镜像删除安全阀警告（扫描不完整/比例超限已禁用删除）
                        // 被「位腐发现」顶掉后保护还在、原因不可见
                        var rotMsg = $"深度校验发现 {rot.Count} 处位腐差异（大小/时间一致但内容不同），已标记待处理";
                        LastWarning = LastWarning == null ? rotMsg : $"{LastWarning}；{rotMsg}";
                    }
                }
                LastConflictCount = plan.Count(p => p.Action == SyncAction.Conflict);
                // 空间预检警告与安全阀警告拼接（不覆盖——覆盖会把"已禁用镜像删除"的提示吃掉）
                var spaceWarn = CheckSpace(plan);
                if (spaceWarn != null)
                    LastWarning = LastWarning == null ? spaceWarn : $"{LastWarning}；{spaceWarn}";
                var (lf, lb) = CountFiles(left);
                var (rf, rb) = CountFiles(right);
                LastScanStats = (lf, rf, lb, rb);
                record.ScannedFiles = Math.Max(left.Count, right.Count);
                StatusText = $"扫描 {record.ScannedFiles} 项，耗时 {sw.Elapsed.TotalSeconds:F1}s，差异 {plan.Count}" +
                             (LastWarning != null ? $" ⚠ {LastWarning}" : "");

                var (creates, updates, deletes, moves, bytes) = Differ.Summarize(plan);

                // 自动触发（启动补跑/定时/实时）：只有空间不足这类阻断性警告才跳过整轮。
                // 曾用 LastWarning != null 判定——安全阀警告（删除已被移出计划、复制照常安全）
                // 也触发 skip，ACL 常驻子树场景下实时任务的自动轮从此全部跳过，自动同步实质瘫痪
                var autoSkip = spaceWarn != null && trigger != "manual";

                if (execute && autoSkip)
                {
                    record.Status = "skipped";
                    LastError = $"自动同步跳过: {LastWarning}";
                    record.ErrorMessage = LastError;
                    return (plan, $"⚠ 空间不足，自动同步已跳过 {DateTime.Now:HH:mm:ss}");
                }

                if (execute && plan.Count > 0)
                {
                    Status = JobStatus.Syncing;
                    RaiseChanged();
                    var (executor, ok, skipped, failed, copiedBytes) =
                        await RunExecutorAsync(plan, progress, token).ConfigureAwait(false);
                    ApplyExecutorStats(record, executor, ok, skipped, failed, copiedBytes);
                    // 版本库清理：成功轮次后异步保留最近 N 代（失败轮不动，等下次成功一并清）；
                    // N=0（功能关闭）也跑——孤儿块/trash 的 GC 不该因关闭功能而停摆（版本行不裁）
                    if (record.Status == "ok")
                    {
                        ScheduleVersionPrune();
                        // 基线懒对账：同步成功的侧，条目不在本轮扫描结果里 → 删（手动绕过工具删文件的死条目）
                        ScheduleBaselineReconcile(Job.LeftPath, Job.RightPath, left, right);
                    }
                }
                else
                {
                    record.Status = "ok";
                    record.CopiedFiles = 0;
                    // 无差异轮次也发 done 终态：执行器不跑就没有 done 事件，UI 进度条会永远停在
                    // 对比阶段（真实事故：86ms 完成的轮次，界面停在"②扫描右侧"）
                    progress.Report(new ProgressInfo
                    {
                        Phase = "done",
                        CurrentItem = $"完成：两边一致，无需同步 {DateTime.Now:HH:mm:ss}"
                    }.Snapshot());
                }

                // 快照基线：执行完全成功（含无差异的空跑）才更新（用执行前的扫描推导）；
                // 失败/取消时保留旧快照，下轮重新推导，避免把半完成状态当成已完成。
                // v1.5 起单向（镜像/备份）同样维护——A3 移动检测的配对依据（一次事务 UPSERT，秒级）。
                // ExecutePlanAsync/RunPlanAsync 走执行后重扫（RescanAfterSuccessAsync），此处语义不同：自动轮不重扫
                if (execute && record.Status == "ok" && !token.IsCancellationRequested)
                    _db.SaveSnapshot(Job.Id, Differ.BuildSnapshot(left, right, plan).Values);

                return (plan, plan.Count == 0
                    ? $"已同步（无差异）{DateTime.Now:HH:mm:ss}"
                    : $"差异 {creates}建/{updates}改/{deletes}删" + (moves > 0 ? $"/{moves}移" : "")
                      + $" {StatusZh(record.Status)} {DateTime.Now:HH:mm:ss}"
                      + (record.DeltaSavedBytes > 0 ? $"，增量省 {Executor.FormatSize(record.DeltaSavedBytes)}" : "")
                      + (record.RetriedOk > 0 ? $"，重试后成功 {record.RetriedOk} 项" : "")
                      + (record.MovedFiles > 0 ? $"，移动 {record.MovedFiles} 项" : ""));
            }).ConfigureAwait(false);
        }

        public bool IsRunning => Volatile.Read(ref _busy) == 1;

        private int _activeRunExecute;
        /// <summary>当前运行轮是否为执行同步（logRun=true）。托盘动态图标/悬浮进度只认执行轮，
        /// 分析/预览轮（logRun=false）不该让托盘进入"同步中"状态。每轮 RunGuardedAsync 开头写入，
        /// 收尾 finally 复位 0——不复位的话，新轮 _busy 置位到本标志写入之间的窗口里，
        /// 观察者会拿上轮残留的 1 误判"执行中"（托盘空闲仍转圈的根因，2026-09-16）。</summary>
        public bool ActiveRunExecute => Volatile.Read(ref _activeRunExecute) == 1;

        /// <summary>当前进度序号（轮次收尾会 ++）。UI 渲染终态后记录该值，
        /// 丢弃更小序号的晚到进度事件（防旧"扫描中"盖掉"完成"）。</summary>
        public long CurrentProgressSeq => Volatile.Read(ref _progressSeq);

        /// <summary>单侧扫描统计一趟算完（原每侧 4 趟 LINQ 枚举，大目录树白扫 4 遍）。</summary>
        private static (int files, long bytes) CountFiles(Dictionary<string, FileEntry> side)
        {
            int n = 0; long b = 0;
            foreach (var v in side.Values)
                if (!v.IsDirectory) { n++; b += v.Size; }
            return (n, b);
        }

        /// <summary>执行成功后用执行后的真实状态刷新 UI 扫描快照（Last*Scan/LastScanStats/双向冲突数）。
        /// 供 UI 后置分析直接复用免重扫（P-6）；双向快照基线仍由调用方按自身语义写。</summary>
        public bool LastScanFresh { get; private set; }

        private Task RefreshUiScanState(Dictionary<string, FileEntry> left, Dictionary<string, FileEntry> right)
        {
            LastLeftScan = left;
            LastRightScan = right;
            var (lf, lb) = CountFiles(left);
            var (rf, rb) = CountFiles(right);
            LastScanStats = (lf, rf, lb, rb);
            if (Job.Direction == SyncDirection.TwoWay)
                LastConflictCount = Differ.ComputeTwoWay(Job, left, right, _db.GetSnapshot(Job.Id))
                    .Count(p => p.Action == SyncAction.Conflict);
            LastScanFresh = true;
            return Task.CompletedTask;
        }

        /// <summary>用最近扫描结果重算差异计划（不重扫不执行不落史）。仅当 LastScanFresh（执行成功后的重扫）时结果是当下的。</summary>
        public List<PlanEntry> ComputePlanFromLastScan()
        {
            if (!LastScanFresh || LastLeftScan == null || LastRightScan == null)
                throw new InvalidOperationException("扫描结果已过期，请重新分析");
            var left = new Dictionary<string, FileEntry>(LastLeftScan, StringComparer.OrdinalIgnoreCase);
            var right = new Dictionary<string, FileEntry>(LastRightScan, StringComparer.OrdinalIgnoreCase);
            var plan = Job.Direction == SyncDirection.TwoWay
                ? Differ.ComputeTwoWay(Job, left, right, _db.GetSnapshot(Job.Id))
                : Differ.Compute(Job, left, right);
            // 与 RunAsync 同口径：移动检测后处理（分析/执行的配对结果必须一致）
            return MoveDetection.Apply(Job, plan, _db.GetSnapshot(Job.Id), left, right, MtimeTolMs());
        }

        /// <summary>
        /// 执行 UI 已确认的计划（分析后用户在列表里勾选/裁决/逐行改向过的 plan），不重新扫描。
        /// C2 逐行改向：Reverse 行在此重打（Update 交换方向 / Create 的对侧无文件=删除本侧）——
        /// 重打后的动作走既有 CopyOne+归档+增量通道。全部成功后重扫两侧：双向重建快照基线，
        /// 同时刷新 UI 扫描快照（P-6）；失败/取消保留旧快照。
        /// </summary>
        public Task<(RunRecord record, List<PlanEntry> plan)> ExecutePlanAsync(List<PlanEntry> plan,
            CancellationToken ct = default, string trigger = "manual")
        {
            ValidatePathsForRun();
            if (Interlocked.Exchange(ref _busy, 1) == 1)
                throw new InvalidOperationException("任务正在运行中");
            ApplyUserOverrides(plan);
            return RunGuardedAsync(trigger, logRun: true, ct, async (record, token, progress) =>
            {
                Status = JobStatus.Syncing;
                StatusText = "同步中…";
                LastError = null;
                LastWarning = null;   // 与 RunAsync 同口径：警告是本轮语义，隔轮残留会让任务卡永远挂着旧 ⚠
                RaiseChanged();

                var (executor, ok, skipped, failed, copiedBytes) =
                    await RunExecutorAsync(plan, progress, token).ConfigureAwait(false);
                ApplyExecutorStats(record, executor, ok, skipped, failed, copiedBytes);
                if (record.Status == "ok")
                    ScheduleVersionPrune();

                // 成功后重扫两侧（双向建快照基线 + 刷新 UI 扫描快照 + 基线对账）。
                // 重扫失败不抹掉同步成功的事实——快照/UI 下轮重建；也别让 runs 残留 running
                if (record.Status == "ok" && !token.IsCancellationRequested)
                {
                    try { await RescanAfterSuccessAsync(plan, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch { /* 重扫尽力而为 */ }
                }

                return (plan, $"同步完成（{StatusZh(record.Status)}）{DateTime.Now:HH:mm:ss}"
                    + (record.DeltaSavedBytes > 0 ? $"，增量省 {Executor.FormatSize(record.DeltaSavedBytes)}" : ""));
            });
        }

        /// <summary>C2 逐行改向：Reverse 行按「以对侧现状为准」重打——Update 交换覆盖方向；
        /// Create 的对侧无此文件 = 删除本侧（镜像语义的逆）；其余动作不改（菜单层已挡）。
        /// 重打后的动作自然走既有执行通道（CopyOne/DeltaTransfer/归档/回收站）。</summary>
        private static void ApplyUserOverrides(List<PlanEntry> plan)
        {
            foreach (var p in plan)
            {
                if (p.UserOverride != UserOverrideKind.Reverse) continue;
                var rev = p.Action switch
                {
                    SyncAction.UpdateRight => SyncAction.UpdateLeft,
                    SyncAction.UpdateLeft => SyncAction.UpdateRight,
                    SyncAction.CreateRight => SyncAction.DeleteLeft,
                    SyncAction.CreateLeft => SyncAction.DeleteRight,
                    _ => p.Action
                };
                if (rev == p.Action) continue;
                p.Note = $"反向执行（原 {p.ActionText.Trim()}）: {p.Note}";
                p.Action = rev;
                p.UserOverride = UserOverrideKind.Default;   // 重打后视觉与执行一致，不再翻转
            }
        }

        /// <summary>
        /// 执行人工裁决后的计划（双向冲突处理）。全部成功后重新扫描重建快照基线。
        /// </summary>
        public Task<(RunRecord record, List<PlanEntry> plan)> RunPlanAsync(List<PlanEntry> plan, CancellationToken ct = default)
        {
            if (Job.Direction != SyncDirection.TwoWay)
                throw new InvalidOperationException("仅双向任务支持裁决执行");
            ValidatePathsForRun();
            if (Interlocked.Exchange(ref _busy, 1) == 1)
                throw new InvalidOperationException("任务正在运行中");
            return RunGuardedAsync("resolve", logRun: true, ct, async (record, token, progress) =>
            {
                Status = JobStatus.Syncing;
                StatusText = "执行裁决…";
                RaiseChanged();

                var (executor, ok, skipped, failed, bytes) =
                    await RunExecutorAsync(plan, progress, token).ConfigureAwait(false);
                // 统计走公共口径（曾缺磁盘满→error 判定：磁盘满被记成 partial）
                ApplyExecutorStats(record, executor, ok, skipped, failed, bytes);
                if (record.Status == "ok")
                    ScheduleVersionPrune();

                // 重新扫描建立真实基线（M-7：用链接后的 token，取消即时生效）
                // + 按真实状态重算剩余冲突（裁决子集之外可能还有未裁决项）+ 刷新 UI 扫描快照
                if (record.Status == "ok" && !token.IsCancellationRequested)
                {
                    try { await RescanAfterSuccessAsync(plan, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch { /* 重扫尽力而为：快照/UI 下轮重建 */ }
                }

                return (plan, $"裁决执行完成（{StatusZh(record.Status)}）{DateTime.Now:HH:mm:ss}"
                    + (record.DeltaSavedBytes > 0 ? $"，增量省 {Executor.FormatSize(record.DeltaSavedBytes)}" : ""));
            });
        }

        /// <summary>三个执行入口共用的路径守卫：空路径 / 路径无效 / 两侧互嵌套 / 侧与数据目录、程序目录重叠。
        /// 互嵌套与目录重叠在 UI 编辑时已挡（JobEditWindow），这里做运行时二次校验——
        /// 防直接改库绕过 UI（B18）与 junction/目录移动造成的关系漂移。
        /// 数据目录重叠检查覆盖新（平台规范目录）旧（程序目录，迁移回退时数据仍在此）两个位置。</summary>
        private void ValidatePathsForRun()
        {
            if (string.IsNullOrWhiteSpace(Job.LeftPath) || string.IsNullOrWhiteSpace(Job.RightPath))
                throw new InvalidOperationException("任务路径为空，请先编辑任务补全两侧路径");
            // 分隔符统一 '/' 再做边界比较：Unix 上 '\' 不是分隔符，含 '\' 的路径经 GetFullPath 后
            // TrimEnd 尾部混裁 + DirectorySeparatorChar 拼接会产生「/work/C:\a/b/」这类混合串，
            // 嵌套/重叠边界判断失效（CI 实测：safety 套件 Windows 风格守卫用例在 Unix 全放行）
            static string Norm(string p) =>
                Path.GetFullPath(p).Replace('\\', '/').TrimEnd('/') + "/";
            string l, r, appRoot, dataRoot;
            try
            {
                l = Norm(Job.LeftPath);
                r = Norm(Job.RightPath);
                appRoot = Norm(AppContext.BaseDirectory);
                dataRoot = Norm(Core.Platform.AppPaths.Root);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"任务路径无效: {ex.Message}");
            }
            if (l.StartsWith(r, StringComparison.OrdinalIgnoreCase) || r.StartsWith(l, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("两侧文件夹互相嵌套（同步会自我复制），请编辑任务调整路径");
            foreach (var root in new[] { appRoot, dataRoot })
            {
                if (l.StartsWith(root, StringComparison.OrdinalIgnoreCase) || r.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    || root.StartsWith(l, StringComparison.OrdinalIgnoreCase) || root.StartsWith(r, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("任务侧与程序/数据目录重叠（同步程序数据会自反馈：数据库/版本库边写边产生新差异），请调整路径");
            }
        }

        /// <summary>版本库清理目标侧：单向清目标侧，双向两侧都清。异步尽力而为。</summary>
        private void ScheduleVersionPrune()
        {
            var keep = Job.VersionKeepCount;
            var roots = Job.Direction == SyncDirection.TwoWay
                ? new[] { Job.LeftPath, Job.RightPath }
                : new[] { Job.Direction.ToRight() ? Job.RightPath : Job.LeftPath };
            _ = Task.Run(() =>
            {
                foreach (var r in roots)
                {
                    try { VersionStore.PruneAll(r, keep); } catch { }
                }
            });
        }

        /// <summary>基线懒对账：每侧条目不在该侧扫描结果里 → 删（手动绕过工具删文件的死条目）。异步尽力而为。</summary>
        private static void ScheduleBaselineReconcile(string leftRoot, string rightRoot,
            Dictionary<string, FileEntry> left, Dictionary<string, FileEntry> right)
        {
            var leftKeys = new HashSet<string>(left.Keys, StringComparer.OrdinalIgnoreCase);
            var rightKeys = new HashSet<string>(right.Keys, StringComparer.OrdinalIgnoreCase);
            _ = Task.Run(() =>
            {
                try { BaselineStore.Reconcile(leftRoot, leftKeys); } catch { }
                try { BaselineStore.Reconcile(rightRoot, rightKeys); } catch { }
            });
        }

        /// <summary>刷新失败明细（UI 直读 + 全量落盘 logs/failed/job{id}/{runId}.txt，UTF-8）。</summary>
        private void CaptureFailures(RunRecord record, Executor executor)
        {
            LastFailedItems = executor.FailedItems;
            LastFailedTotal = Math.Max(executor.FailedTotal, executor.FailedItems.Count);
            LastFailedLogFile = null;
            if (executor.FailedItems.Count == 0) return;
            try
            {
                var dir = Core.Platform.AppPaths.FailedDir(Job.Id);
                Directory.CreateDirectory(dir);
                var runKey = record.Id > 0
                    ? record.Id.ToString()
                    : DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var file = Path.Combine(dir, $"{runKey}.txt");
                var lines = new List<string>
                {
                    $"# 失败 {LastFailedTotal} 条（明细保留 {executor.FailedItems.Count} 条，封顶 {Executor.MaxFailedItems}）"
                    + $"  触发:{record.Trigger}  开始:{record.StartedAt:yyyy-MM-dd HH:mm:ss}"
                };
                lines.AddRange(executor.FailedItems.Select(f =>
                    $"{f.Action}\t{f.RelativePath}\t{f.Error}"));
                File.WriteAllLines(file, lines, System.Text.Encoding.UTF8);
                LastFailedLogFile = file;
            }
            catch { /* 明细落盘失败不影响同步结果 */ }
        }

        /// <summary>重试上轮失败项：按当前文件系统真实状态重建子计划
        /// （源在 → 复制/覆盖；源没了 → 跳过，留给下轮正常同步），复用 ExecutePlanAsync(trigger:"retry")。</summary>
        public async Task<(RunRecord record, List<PlanEntry> plan)> RetryFailedAsync(CancellationToken ct = default)
        {
            if (LastFailedItems.Count == 0)
                throw new InvalidOperationException("没有可重试的失败项");

            var sub = new List<PlanEntry>();
            foreach (var f in LastFailedItems)
            {
                if (f.RelativePath.Split('/').Contains("..")) continue;   // P-12：失败明细被篡改的路径逃逸直接跳过
                // 兼容旧明细的 "RecycleDelete"（攒批失败曾整体记这个串，TryParse 不出动作）：按任务方向还原成删除动作
                string actionName = f.Action;
                if (actionName == "RecycleDelete")
                {
                    var relPath0 = f.RelativePath.Replace('/', Path.DirectorySeparatorChar);
                    actionName = Job.Direction switch
                    {
                        SyncDirection.MirrorLeftToRight or SyncDirection.BackupLeftToRight => nameof(SyncAction.DeleteRight),
                        SyncDirection.MirrorRightToLeft or SyncDirection.BackupRightToLeft => nameof(SyncAction.DeleteLeft),
                        _ => File.Exists(Path.Combine(Job.LeftPath, relPath0))
                            ? nameof(SyncAction.DeleteLeft) : nameof(SyncAction.DeleteRight)
                    };
                }
                if (!Enum.TryParse<SyncAction>(actionName, out var action) ||
                    (action is (SyncAction.None or SyncAction.Conflict) && Job.Direction == SyncDirection.TwoWay))
                    continue;
                bool toRight;
                if (Job.Direction == SyncDirection.TwoWay)
                    toRight = action is SyncAction.CreateRight or SyncAction.UpdateRight or SyncAction.DeleteRight;
                else
                    toRight = Job.Direction.ToRight();
                var relPath = f.RelativePath.Replace('/', Path.DirectorySeparatorChar);
                var src = Path.Combine(toRight ? Job.LeftPath : Job.RightPath, relPath);
                var dst = Path.Combine(toRight ? Job.RightPath : Job.LeftPath, relPath);
                var rel = f.RelativePath;

                switch (action)
                {
                    case SyncAction.CreateLeft or SyncAction.CreateRight
                         or SyncAction.UpdateLeft or SyncAction.UpdateRight or SyncAction.Conflict:
                        // 复制类：源还在才重做（源没了跳过，留给下轮正常同步裁决）
                        if (File.Exists(src))
                            sub.Add(new PlanEntry { Action = action is SyncAction.Conflict
                                    ? (toRight ? SyncAction.UpdateRight : SyncAction.UpdateLeft) : action,
                                RelativePath = rel, IsDirectory = false,
                                Size = new FileInfo(src).Length });
                        else if (Directory.Exists(src))
                            sub.Add(new PlanEntry { Action = action, RelativePath = rel, IsDirectory = true });
                        break;

                    case SyncAction.DeleteLeft or SyncAction.DeleteRight:
                        // 删除类：目标还在才重删（版本化/回收站按任务配置走）
                        if (File.Exists(Executor.LongPath(dst)))
                            sub.Add(new PlanEntry { Action = action, RelativePath = rel, IsDirectory = false });
                        else if (Directory.Exists(Executor.LongPath(dst)))
                            sub.Add(new PlanEntry { Action = action, RelativePath = rel, IsDirectory = true });
                        break;
                }
            }

            if (sub.Count == 0)
                throw new InvalidOperationException("失败项的源均已不存在，无需重试（下轮正常同步会处理）");
            return await ExecutePlanAsync(sub, ct, trigger: "retry").ConfigureAwait(false);
        }

        private DateTime _lastStateWrite = DateTime.MinValue;

        /// <summary>写状态文件（挂起取证：记录每刻阶段+当前文件，卡死时能看最后一个动作）。
        /// 500ms 节流 + 后台线程写：进度回调在 UI 线程，不能同步写盘。</summary>
        private void WriteStateFile(ProgressInfo p)
        {
            if ((DateTime.UtcNow - _lastStateWrite).TotalMilliseconds < 500) return;
            _lastStateWrite = DateTime.UtcNow;
            var line = $"{DateTime.Now:HH:mm:ss.fff}|{p.Phase}|{p.CurrentItem}";
            var file = Core.Platform.AppPaths.StateFile(Job.Id);
            Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllText(file, line);
                }
                catch { }
            });
        }

        /// <summary>空间预检：按目标盘汇总待复制字节，剩余空间不足时返回警告文本。</summary>
        private string? CheckSpace(List<PlanEntry> plan)
        {
            var need = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in plan)
            {
                if (p.IsDirectory) continue;
                string? targetRoot = p.Action switch
                {
                    SyncAction.CreateRight or SyncAction.UpdateRight => Job.RightPath,
                    SyncAction.CreateLeft or SyncAction.UpdateLeft => Job.LeftPath,
                    _ => null
                };
                if (targetRoot == null) continue;
                try
                {
                    var drive = Path.GetPathRoot(Path.GetFullPath(targetRoot));
                    if (drive == null) continue;
                    need[drive] = need.GetValueOrDefault(drive) + p.Size;
                }
                catch { }
            }
            var warnings = new List<string>();
            foreach (var (drive, bytes) in need)
            {
                try
                {
                    var di = new DriveInfo(drive);
                    if (di.AvailableFreeSpace < bytes)
                        warnings.Add($"{drive} 空间不足：需 {Executor.FormatSize(bytes)}，仅剩 {Executor.FormatSize(di.AvailableFreeSpace)}");
                }
                catch { }
            }
            return warnings.Count > 0 ? string.Join("；", warnings) : null;
        }

        internal static string StatusZh(string s) => RunReport.StatusZh(s);   // 唯一实现收敛到 RunReport（App 侧共用）

        /// <summary>深度校验「仅大文件」档的阈值（deep_verify=1 档）。</summary>
        public const long DeepVerifyMinBytes = 50L * 1024 * 1024;

        /// <summary>B1 深度校验：对「两侧 size+mtime 一致（分析口径判无差异）」的文件比对全文 sha256，
        /// 检出位腐（磁盘静默损坏：元数据完好内容变坏）。minBytes=0 全量。读失败的文件跳过不误报
        /// （占用等交给正常差异路径）。哈希顺扫盘速率，候选按大小降序（大文件先出结果）。</summary>
        private static List<PlanEntry> DeepVerifyScan(SyncJob job,
            Dictionary<string, FileEntry> left, Dictionary<string, FileEntry> right,
            long minBytes, long tol, IProgress<ProgressInfo>? progress, CancellationToken ct)
        {
            var candidates = new List<(string rel, FileEntry l, FileEntry r)>();
            foreach (var (rel, l) in left)
            {
                if (l.IsDirectory || l.Size < Math.Max(minBytes, 1)) continue;   // 0 字节文件 sha 恒等，跳过
                if (!right.TryGetValue(rel, out var r) || r.IsDirectory) continue;
                if (l.Size != r.Size || Math.Abs((l.MtimeUtc - r.MtimeUtc).TotalMilliseconds) > tol) continue;
                candidates.Add((rel, l, r));
            }
            var rot = new List<PlanEntry>();
            if (candidates.Count == 0) return rot;
            candidates.Sort((a, b) => b.l.Size.CompareTo(a.l.Size));   // 大文件优先
            int done = 0;
            foreach (var (rel, l, r) in candidates)
            {
                ct.ThrowIfCancellationRequested();
                if (done % 4 == 0)
                    progress?.Report(new ProgressInfo
                    {
                        Phase = "verify",
                        CurrentItem = $"[校验] {rel}",
                        TotalItems = candidates.Count,
                        DoneItems = done
                    }.Snapshot());
                done++;
                string shaL, shaR;
                try
                {
                    var relWin = rel.Replace('/', Path.DirectorySeparatorChar);
                    shaL = Executor.Sha256OfFile(Executor.LongPath(Path.Combine(job.LeftPath, relWin)));
                    shaR = Executor.Sha256OfFile(Executor.LongPath(Path.Combine(job.RightPath, relWin)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
                { continue; }
                if (!string.Equals(shaL, shaR, StringComparison.OrdinalIgnoreCase))
                    rot.Add(new PlanEntry
                    {
                        Action = SyncAction.Conflict,
                        RelativePath = rel,
                        IsDirectory = false,
                        Size = l.Size,
                        LeftSize = l.Size,
                        RightSize = r.Size,
                        LeftMtime = l.MtimeUtc.ToLocalTime(),
                        RightMtime = r.MtimeUtc.ToLocalTime(),
                        BitRot = true,   // 好坏未知：单向也不许自动覆盖（参考#1）
                        Note = "位腐差异：大小/修改时间一致但内容不同（任一侧都可能是坏的一方，请人工裁决；裁决覆盖有版本库保护）"
                    });
            }
            return rot;
        }

        /// <summary>B1 工具栏「校验」：对当前任务立即跑一轮只读深度校验（全量，不受三档限制），
        /// 不执行任何写动作；结果=位腐冲突行（树中展示，好坏未知统一人工裁决：手动删坏侧再同步即修复）。</summary>
        public Task<(RunRecord record, List<PlanEntry> plan)> VerifyAsync(CancellationToken ct = default)
        {
            ValidatePathsForRun();
            if (Interlocked.Exchange(ref _busy, 1) == 1)
                throw new InvalidOperationException("任务正在运行中");
            return RunGuardedAsync("verify", logRun: true, ct, async (record, token, progress) =>
            {
                Status = JobStatus.Scanning;
                StatusText = "深度校验扫描中…";
                RaiseChanged();
                var left = (await Scanner.ScanTreeAsync(Job.LeftPath, Job.ExcludePatterns, progress, token, "left").ConfigureAwait(false)).Entries;
                var right = (await Scanner.ScanTreeAsync(Job.RightPath, Job.ExcludePatterns, progress, token, "right").ConfigureAwait(false)).Entries;
                record.ScannedFiles = Math.Max(left.Count, right.Count);
                Status = JobStatus.Analyzing;
                var rot = DeepVerifyScan(Job, left, right, minBytes: 0, MtimeTolMs(), progress, token);
                LastConflictCount = rot.Count;
                record.Status = "ok";
                var (vlf, vlb) = CountFiles(left);
                var (vrf, vrb) = CountFiles(right);
                LastScanStats = (vlf, vrf, vlb, vrb);
                progress.Report(new ProgressInfo { Phase = "done", CurrentItem = $"深度校验完成：{rot.Count} 处位腐" }.Snapshot());
                return (rot, rot.Count == 0
                    ? $"深度校验通过，未发现位腐 {DateTime.Now:HH:mm:ss}"
                    : $"深度校验发现 {rot.Count} 处位腐差异（已标记待裁决）{DateTime.Now:HH:mm:ss}");
            });
        }

        /// <summary>B3：扫描层收集的同侧「仅大小写不同」实际名对补进计划（去重防同键多覆盖链重复入队）。</summary>
        private static void AppendSideCaseConflicts(List<PlanEntry> plan,
            List<(string first, string second)> conflicts, string sideLabel,
            Dictionary<string, FileEntry> left, Dictionary<string, FileEntry> right)
        {
            foreach (var (first, second) in conflicts)
            {
                if (plan.Any(p => p.RelativePath.Equals(first, StringComparison.OrdinalIgnoreCase))) continue;
                left.TryGetValue(first, out var l);
                right.TryGetValue(first, out var r);
                var meta = l ?? r;
                plan.Add(new PlanEntry
                {
                    Action = SyncAction.Conflict,
                    RelativePath = first,
                    IsDirectory = meta?.IsDirectory ?? false,
                    Size = meta?.Size ?? 0,
                    LeftSize = l?.Size ?? -1,
                    RightSize = r?.Size ?? -1,
                    LeftMtime = l?.MtimeUtc.ToLocalTime(),
                    RightMtime = r?.MtimeUtc.ToLocalTime(),
                    Note = $"{sideLabel}内部仅大小写不同：{first} vs {second}，请选择保留哪个写法"
                });
            }
        }

        private void RaiseChanged() => StateChanged?.Invoke(this);

        /// <summary>timer/去抖/介质恢复触发的轮次：fire-and-forget 不能裸抛。
        /// 吞掉撞车（正在运行中）与取消；其余异常落状态文本（RunAsync rethrow 的出口收口在这里）。</summary>
        private async void RunQuiet(string trigger)
        {
            try { await RunAsync(trigger).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException) { /* 去抖到期瞬间新事件又排上：轮次撞车属正常，下轮照常 */ }
            catch (Exception ex)
            {
                try { StatusText = $"错误: {ex.Message}"; RaiseChanged(); } catch { }
            }
        }

        private void DisposeTriggers()
        {
            IDisposable[] old;
            Timer? oldInterval;
            lock (_triggerGate)
            {
                old = _watchers;
                _watchers = Array.Empty<IDisposable>();
                oldInterval = _intervalTimer;
                _intervalTimer = null;
            }
            lock (_gate)
            {
                _debounceTimer?.Dispose();
                _debounceTimer = null;
            }
            // Dispose 放锁外：watcher.Dispose 等轮询线程退出，轮询线程可能正回调 ScheduleDebounce 抢 _gate，
            // 持锁互等会死锁。
            // H-2：再挪后台——UsnWatcher.Dispose 最坏等 7s（轮询 catch 分支 2s+5s Sleep），UI 线程同步等
            // 就是 5-10s 假死；短暂并存期间旧 watcher 的多余触发只多排一轮去抖，无害
            foreach (var w in old) _ = Task.Run(() => { try { w.Dispose(); } catch { } });
            oldInterval?.Dispose();
        }

        public void Dispose()
        {
            _disposed = true;
            // H-2：先软取消在跑轮次（删除运行中任务时给执行器让路），再拆触发器
            try { _activeRunCts?.Cancel(); } catch (ObjectDisposedException) { }
            StopMediaTimer();
            DisposeTriggers();
            // H-2：_runLock 不 Dispose——轮次 finally 的 Release 与 Dispose 竞态会抛 ODE，
            // 且 finally 里抛异常会吞掉正常返回路径。信号量无句柄等非托管资源，交给 GC。
        }
    }
}
