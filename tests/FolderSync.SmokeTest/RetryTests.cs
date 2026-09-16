using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Core;

namespace FolderSync.SmokeTest
{
    /// <summary>
    /// A2 占用/瞬时错误自动重试专项：复制/删除撞共享冲突不再直接进失败列表，
    /// 轮末统一退避重试（测试把退避缩短到 100ms×3），仍失败才落 failed 且明细带标注。
    /// 铁律用例：真锁文件释放后重试成功 / 全程锁死 3 轮落 failed / 非瞬时错误不进队列 /
    /// 重试中途取消 OCE / auto_retry=0 回退旧行为 / 新列 Dapper 往返。
    /// </summary>
    internal static class RetryTests
    {
        private static int _fail;

        private static void Check(bool cond, string name, string extra = "")
        {
            Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}" + (extra.Length > 0 ? $" ({extra})" : ""));
            if (!cond) _fail++;
        }

        private static bool WaitFor(Func<bool> pred, int timeoutMs = 15000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (pred()) return true;
                Thread.Sleep(50);
            }
            return pred();
        }

        private static (string root, string left, string right, SyncJob job, Db db, SyncEngine engine)
            Setup(string tag, bool autoRetry = true)
        {
            var root = Path.Combine(Path.GetTempPath(), "fsretry_" + tag + "_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            File.WriteAllText(Path.Combine(left, "locked.txt"), "v1 重要内容");
            File.WriteAllText(Path.Combine(left, "free.txt"), "不受锁影响");

            var job = new SyncJob
            {
                Name = "retry-" + tag, LeftPath = left, RightPath = right,
                Direction = SyncDirection.MirrorLeftToRight,
                AutoRetry = autoRetry
            };
            var db = new Db(Path.Combine(root, "test.db"));
            job.Id = db.InsertJob(job);
            return (root, left, right, job, db, new SyncEngine(job, db));
        }

        private static void Cleanup(string root, Db db)
        {
            db.Dispose();
            try { Directory.Delete(root, true); } catch { }
        }

        public static async Task<int> RunAll()
        {
            // 退避缩短到 100ms×3（产品默认 2s/10s/30s，全等要 42s）
            var orig = Executor.RetryDelays;
            Executor.RetryDelays = new[] { TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100) };
            try
            {
                await TestLockedThenRelease();     // 1 锁住的文件释放后轮末重试成功
                await TestLockedForever();         // 2 全程锁死 → 3 轮后落 failed 带标注
                await TestNonTransientNoQueue();   // 3 非瞬时错误（源不存在）直接失败不进队列
                await TestCancelDuringRetry();     // 4 重试中途取消 OCE 正常
                await TestAutoRetryOffFallback();  // 5 auto_retry=0 回退旧行为（直接失败）
                TestDapperRoundTrip();             // 6 jobs.auto_retry / runs.retried_ok 往返
            }
            finally { Executor.RetryDelays = orig; }
            Console.WriteLine(_fail == 0 ? "retry: ALL PASS" : $"retry: {_fail} FAILED");
            return _fail;
        }

        /// <summary>1：源文件被占用（FileOps 注入 win32 32）→ 进重试队列；
        /// 主线程 250ms 后解除 → 第 2/3 轮重试成功。retried_ok=1、failed=0、runs ok。
        /// Unix 无强制共享锁，FileShare.None 手法失效——统一 TestFileOps 注入，三平台一份代码。</summary>
        private static async Task TestLockedThenRelease()
        {
            var (root, left, right, job, db, engine) = Setup("release");
            try
            {
                using var blocker = TestFileOps.FailCopy(Path.Combine(left, "locked.txt"), int.MaxValue);
                var round = Task.Run(() => engine.RunAsync("manual"));
                Thread.Sleep(250);   // 主趟失败进队列（100ms 第一轮大概率也撞锁），此后解除
                blocker.Release();
                var (rec, plan) = await round;

                Check(rec.Status == "ok", "release: 轮次最终 ok", rec.Status);
                Check(rec.RetriedOk >= 1, "release: retried_ok ≥ 1", rec.RetriedOk.ToString());
                Check(rec.FailedFiles == 0, "release: 无失败", rec.FailedFiles.ToString());
                Check(File.ReadAllText(Path.Combine(right, "locked.txt")) == "v1 重要内容",
                    "release: 被锁文件最终复制到位");
                Check(File.ReadAllText(Path.Combine(right, "free.txt")) == "不受锁影响",
                    "release: 未锁文件正常复制");
                var back = db.GetRecentRuns(job.Id).Last();
                Check(back.RetriedOk == rec.RetriedOk, "release: runs.retried_ok 落库读回一致", $"{back.RetriedOk} vs {rec.RetriedOk}");
            }
            finally { Cleanup(root, db); }
        }

        /// <summary>2：全程锁死 → 3 轮重试全部失败 → failed=1、明细带「已自动重试 3 次」、retried_ok=0。</summary>
        private static async Task TestLockedForever()
        {
            var (root, left, right, job, db, engine) = Setup("forever");
            try
            {
                using var blocker = TestFileOps.FailCopy(Path.Combine(left, "locked.txt"), int.MaxValue);
                var (rec, _) = await engine.RunAsync("manual");

                Check(rec.Status == "partial", "forever: 轮次 partial", rec.Status);
                Check(rec.FailedFiles == 1, "forever: 恰 1 项失败", rec.FailedFiles.ToString());
                Check(rec.RetriedOk == 0, "forever: retried_ok=0");
                Check(engine.LastFailedItems.Count == 1
                    && engine.LastFailedItems[0].RelativePath == "locked.txt"
                    && engine.LastFailedItems[0].Error.Contains("已自动重试 3 次"),
                    "forever: 失败明细带「已自动重试 3 次」标注",
                    engine.LastFailedItems.Count > 0 ? engine.LastFailedItems[0].Error : "(empty)");
                Check(File.Exists(Path.Combine(right, "free.txt")), "forever: 其余文件照常同步");
                Check(!File.Exists(Path.Combine(right, "locked.txt")), "forever: 被锁文件未落地");
                Check(!Directory.EnumerateFiles(right, "*" + Scanner.TmpSuffix, SearchOption.AllDirectories).Any(),
                    "forever: 无 tmp 残留");
            }
            finally { Cleanup(root, db); }
        }

        /// <summary>3：源文件不存在的 CreateRight（分析后源被外部删除）→ win32 3 非瞬时 → 直接 failed，
        /// 不进重试队列（无退避等待）、明细不带重试标注。</summary>
        private static async Task TestNonTransientNoQueue()
        {
            var (root, left, right, job, db, engine) = Setup("nontrans");
            try
            {
                var plan = new System.Collections.Generic.List<PlanEntry>
                {
                    new() { Action = SyncAction.CreateRight, RelativePath = "ghost.txt", IsDirectory = false, Size = 10 }
                };
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var ex = new Executor(job);
                var (ok, skipped, failed, bytes) = await ex.ExecuteAsync(plan, null, CancellationToken.None);
                sw.Stop();
                Check(failed == 1 && ok == 0, "nontrans: 直接失败", $"failed={failed} ok={ok}");
                Check(ex.FailedItems.Count == 1 && !ex.FailedItems[0].Error.Contains("已自动重试"),
                    "nontrans: 明细无重试标注", ex.FailedItems.Count > 0 ? ex.FailedItems[0].Error : "(empty)");
                Check(sw.ElapsedMilliseconds < 500, "nontrans: 未消耗退避等待（<0.5s）", $"{sw.ElapsedMilliseconds}ms");
            }
            finally { Cleanup(root, db); }
        }

        /// <summary>4：锁住的文件进重试趟后 RequestCancel → OCE 上抛、runs cancelled、状态回 Idle。</summary>
        private static async Task TestCancelDuringRetry()
        {
            var (root, left, right, job, db, engine) = Setup("cancelretry");
            try
            {
                using var blocker = TestFileOps.FailCopy(Path.Combine(left, "locked.txt"), int.MaxValue);
                var round = Task.Run(() => engine.RunAsync("manual"));
                var inRetry = WaitFor(() => engine.StatusText.Contains("自动重试"), 10000);
                engine.RequestCancel();
                OperationCanceledException? oce = null;
                try { await round; }
                catch (OperationCanceledException ex) { oce = ex; }
                Check(inRetry, "cancelretry: 进入重试趟（状态文本可见）", engine.StatusText);
                Check(oce != null, "cancelretry: 取消抛 OCE", oce?.Message ?? "(no ex)");
                Check(engine.Status == JobStatus.Idle, "cancelretry: 状态回 Idle", engine.Status.ToString());
                var last = db.GetRecentRuns(job.Id).LastOrDefault();
                Check(last?.Status == "cancelled", "cancelretry: runs 落 cancelled", last?.Status ?? "(none)");
            }
            finally { Cleanup(root, db); }
        }

        /// <summary>5：AutoRetry=false → 锁住时直接失败（旧行为）：failed=1、明细不带重试标注、耗时 < 1s。</summary>
        private static async Task TestAutoRetryOffFallback()
        {
            var (root, left, right, job, db, engine) = Setup("off", autoRetry: false);
            try
            {
                using var blocker = TestFileOps.FailCopy(Path.Combine(left, "locked.txt"), int.MaxValue);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (rec, _) = await engine.RunAsync("manual");
                sw.Stop();
                Check(rec.Status == "partial" && rec.FailedFiles == 1, "off: 直接失败（旧行为）", rec.Status);
                Check(rec.RetriedOk == 0, "off: retried_ok=0");
                Check(engine.LastFailedItems.Count == 1 && !engine.LastFailedItems[0].Error.Contains("已自动重试"),
                    "off: 明细无重试标注", engine.LastFailedItems.Count > 0 ? engine.LastFailedItems[0].Error : "(empty)");
                Check(sw.ElapsedMilliseconds < 1000, "off: 无退避等待（<1s）", $"{sw.ElapsedMilliseconds}ms");
            }
            finally { Cleanup(root, db); }
        }

        /// <summary>6：jobs.auto_retry / runs.retried_ok 列 Dapper 往返（trigger_type 读库丢值同款坑预防）。</summary>
        private static void TestDapperRoundTrip()
        {
            var root = Path.Combine(Path.GetTempPath(), "fsretry_db_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(root);
            var dbPath = Path.Combine(root, "t.db");
            try
            {
                long jobId, runId;
                using (var db = new Db(dbPath))
                {
                    var j = new SyncJob
                    {
                        Name = "rt", LeftPath = Path.Combine(root, "L"), RightPath = Path.Combine(root, "R"),
                        AutoRetry = false   // 非默认值才能验出读回丢失
                    };
                    jobId = db.InsertJob(j);
                    var r = new RunRecord { JobId = jobId, StartedAt = DateTime.Now, Trigger = "manual" };
                    runId = db.InsertRun(r);
                    r.Id = runId;
                    r.FinishedAt = DateTime.Now;
                    r.Status = "ok";
                    r.RetriedOk = 7;
                    db.FinishRun(r);
                }
                using (var db2 = new Db(dbPath))   // 新连接 = 模拟重启后读库
                {
                    var j2 = db2.GetJob(jobId);
                    Check(j2 != null && j2!.AutoRetry == false, "db: jobs.auto_retry 往返", j2?.AutoRetry.ToString() ?? "(null)");
                    var r2 = db2.GetRecentRuns(jobId).First(x => x.Id == runId);
                    Check(r2.RetriedOk == 7, "db: runs.retried_ok 往返", r2.RetriedOk.ToString());
                }
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
