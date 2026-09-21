using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FolderSync.Core
{
    /// <summary>管理全部任务引擎：加载/保存任务、启动时自动恢复。
    /// 引擎表用 ConcurrentDictionary：调度 timer 线程（Tick/RefreshScheduleTimer）与 UI 线程
    /// （Add/Remove/Save）无保护并发读写普通 Dictionary 会 InvalidOperationException（被吞→该轮
    /// 调度静默丢失）甚至写坏内部状态；遍历一律 ToArray 快照，避免枚举中结构性修改。</summary>
    public class JobManager : IDisposable
    {
        private readonly Db _db;
        private readonly ConcurrentDictionary<long, SyncEngine> _engines = new();

        public event Action<SyncEngine>? EngineAdded;

        public JobManager(Db db) => _db = db;

        public IReadOnlyCollection<SyncEngine> Engines => _engines.Values.ToArray();

        // ---- B2 指定时刻调度：单个 30s timer 扫全部 Schedule 任务 ----
        private Timer? _scheduleTimer;
        /// <summary>调度器 tick 间隔（产品 30s；测试调小或直接调 Tick(DateTime) 注入时钟）。</summary>
        internal int ScheduleTickSeconds { get; set; } = 30;
        /// <summary>每任务上次调度触发时刻（内存态；进程重启后从启动时刻重算，错过的由启动补跑兜底）。</summary>
        private readonly ConcurrentDictionary<long, DateTime> _lastScheduleFire = new();

        private void StartScheduleTimer()
        {
            StopScheduleTimer();
            var period = TimeSpan.FromSeconds(Math.Max(1, ScheduleTickSeconds));
            _scheduleTimer = new Timer(_ => { try { Tick(DateTime.Now); } catch { /* 调度自身永不抛 */ } },
                null, period, period);
        }

        private void StopScheduleTimer()
        {
            _scheduleTimer?.Dispose();
            _scheduleTimer = null;
        }

        /// <summary>调度 tick（internal 供测试注入时钟）：Schedule 任务 next_due ≤ now → RunQuiet("schedule")。
        /// 正在跑 → 跳过本轮（lastFire 前移，不补触发——下个调度点再跑）；失败/无效规格静默跳过。</summary>
        internal void Tick(DateTime now)
        {
            foreach (var e in _engines.Values.ToArray())
            {
                var job = e.Job;
                if (job.Trigger != TriggerType.Schedule || !job.Enabled
                    || string.IsNullOrWhiteSpace(job.ScheduleSpec)) continue;
                // 锚点：上次调度触发；进程重启后用引擎最近一次运行（无则进程起点），确保 due 始终在未来
                if (!_lastScheduleFire.TryGetValue(job.Id, out var anchor))
                    anchor = e.LastRunAt ?? _startedAt;
                var due = ScheduleSpec.NextDue(job.ScheduleSpec, anchor);
                if (due == null || now < due.Value) continue;
                _lastScheduleFire[job.Id] = now;   // 无论是否真跑都前移（正在跑=跳过本轮，不堆积补跑）
                if (e.IsRunning) continue;
                _ = Task.Run(async () =>
                {
                    try { await e.RunAsync("schedule").ConfigureAwait(false); }
                    catch { /* 引擎内部已记录状态 */ }
                });
            }
        }

        private readonly DateTime _startedAt = DateTime.Now;

        public SyncEngine Add(SyncJob job)
        {
            var e = new SyncEngine(job, _db);
            _engines[job.Id] = e;
            // 脏数据防御：空路径任务（遗留脏数据）不挂任何触发器——Interval 定时器到期只会
            // 反复报错并覆盖状态文案，Realtime 监听必失败；标灰等用户编辑或删除
            // （"路径无效"文案由 LoadAllAndResumeAsync 设置）
            if (job.Enabled && !HasInvalidPath(job)) e.ApplyTriggers();
            RefreshScheduleTimer();
            EngineAdded?.Invoke(e);
            return e;
        }

        internal static bool HasInvalidPath(SyncJob job) =>
            string.IsNullOrWhiteSpace(job.LeftPath) || string.IsNullOrWhiteSpace(job.RightPath);

        public void Remove(long jobId)
        {
            if (_engines.TryRemove(jobId, out var e))
            {
                // 先 Dispose（软取消在跑轮次）再删库：轮次收尾的 FinishRun 在 job 删除后 UPDATE 零行无害，
                // 且 DeleteJob 已级联清 runs——不留孤儿历史
                e.Dispose();
                _db.DeleteJob(jobId);
            }
        }

        public void Save(SyncJob job)
        {
            _db.UpdateJob(job);
            if (_engines.TryGetValue(job.Id, out var e)) e.UpdateJob(job);
            _lastScheduleFire.TryRemove(job.Id, out _);   // 规格变更：锚点重置，从当下重算下次触发
            RefreshScheduleTimer();
        }

        public SyncJob CreateNew(string name, string left, string right)
        {
            var job = new SyncJob { Name = name, LeftPath = left, RightPath = right };
            job.Id = _db.InsertJob(job);
            return job;
        }

        /// <summary>启动时加载全部任务；auto_start 的任务触发器生效，并立即补跑一次同步（startup 触发，容错静默）。
        /// 补跑并发上限 2（P-3）：几十个任务同时全量扫描会抢光磁盘，排队消化。</summary>
        public Task LoadAllAndResumeAsync()
        {
            // 上次进程退出/崩溃时没写完的 running 记录，统一标为 interrupted（不是真在跑）
            _db.MarkInterruptedRuns();

            var gate = new SemaphoreSlim(2);
            foreach (var job in _db.GetJobs())
            {
                var e = Add(job);
                if (HasInvalidPath(job))
                {
                    // 脏数据防御：空路径任务（遗留脏数据）不补跑不建监听（Add 内已挡触发器），标灰等用户编辑或删除
                    e.StatusText = "路径无效（遗留脏任务），请编辑或删除";
                    continue;
                }
                if (job.Enabled && job.AutoStart)
                {
                    // 启动补跑：不阻塞 UI，失败不弹（记入历史）。
                    // Schedule 任务的启动补跑即「错过补跑」语义（关机期间到点 → 启动即跑一轮）
                    _ = Task.Run(async () =>
                    {
                        await gate.WaitAsync().ConfigureAwait(false);
                        try { await e.RunAsync("startup").ConfigureAwait(false); }
                        catch { /* 引擎内部已记录错误状态 */ }
                        finally { gate.Release(); }
                    });
                }
            }
            RefreshScheduleTimer();
            return Task.CompletedTask;
        }

        /// <summary>有启用的 Schedule 任务才开 30s 调度 timer（Save 后也重查：改配置即时生效）。</summary>
        private void RefreshScheduleTimer()
        {
            var need = _engines.Values.ToArray().Any(e =>
                e.Job.Trigger == TriggerType.Schedule && e.Job.Enabled &&
                ScheduleSpec.Parse(e.Job.ScheduleSpec) != null);
            if (need && _scheduleTimer == null) StartScheduleTimer();
            else if (!need) StopScheduleTimer();
        }

        public void Dispose()
        {
            StopScheduleTimer();
            foreach (var e in _engines.Values.ToArray()) e.Dispose();
            _engines.Clear();
        }
    }
}
