using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Core;

namespace FolderSync.SmokeTest
{
    /// <summary>
    /// 停止（RequestCancel 软取消）专项：UI「停止」按钮曾只连手动轮次的 UI token，
    /// 实时/定时自动触发轮次（RunQuiet 不带 token）完全不受控 → 点停止无效、只能重启软件。
    /// 铁律用例：自动轮次可停、大文件复制中可停（CopyFileEx pbCancel）、空闲调用无害、
    /// 外部 token 取消仍生效（回归）、取消后引擎可立即跑下一轮。
    /// </summary>
    internal static class CancelTests
    {
        private static int _fail;

        private static void Check(bool cond, string name)
        {
            Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}");
            if (!cond) _fail++;
        }

        private static void Check(bool cond, string name, string extra)
        {
            Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name} ({extra})");
            if (!cond) _fail++;
        }

        /// <summary>等引擎进入指定状态（50ms 轮询），超时返回 false。</summary>
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
            Setup(string tag)
        {
            var root = Path.Combine(Path.GetTempPath(), "fscancel_" + tag + "_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            // 40 × 25MB：保证轮次执行窗口足够长（NVMe 上约 1~3s），中途可观测 Syncing 态
            var buf = new byte[25 * 1024 * 1024];
            new Random(42).NextBytes(buf);
            for (int i = 0; i < 40; i++)
                File.WriteAllBytes(Path.Combine(left, $"big{i:D3}.bin"), buf);

            var job = new SyncJob
            {
                Name = "cancel-" + tag, LeftPath = left, RightPath = right,
                Direction = SyncDirection.MirrorLeftToRight
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
            await TestAutoRoundCancellable();      // 1 自动轮次（不带 token，等价 RunQuiet）可被 RequestCancel 停
            await TestIdleCancelHarmless();        // 2 空闲时 RequestCancel 无害
            await TestExternalTokenStillWorks();   // 3 外部 ct 取消仍生效（手动路径回归）
            await TestNextRoundAfterCancel();      // 4 取消后引擎可立即跑下一轮（_busy/锁已释放）
            Console.WriteLine(_fail == 0 ? "cancel: ALL PASS" : $"cancel: {_fail} FAILED");
            return _fail;
        }

        private static async Task TestAutoRoundCancellable()
        {
            var (root, left, right, job, db, engine) = Setup("auto");
            try
            {
                // 不传 token —— 与 RunQuiet("realtime") 调用形态一致：自动触发轮次没有任何外部取消源
                var round = Task.Run(() => engine.RunAsync("realtime"));
                var reached = WaitFor(() => engine.Status == JobStatus.Syncing);
                engine.RequestCancel();
                OperationCanceledException? oce = null;
                try { await round; }
                catch (OperationCanceledException ex) { oce = ex; }
                Check(reached, "auto: 进入同步态");
                Check(oce != null, "auto: RequestCancel 抛 OCE", oce?.Message ?? "(no ex)");
                Check(engine.Status == JobStatus.Idle, "auto: 状态回 Idle");
                Check(engine.StatusText.Contains("已取消"), "auto: 状态文本「已取消」", engine.StatusText);
                var last = db.GetRecentRuns(job.Id).LastOrDefault();
                Check(last?.Status == "cancelled", "auto: runs 落 cancelled", last?.Status ?? "(none)");
                // 大概率停在大文件中途：目标侧有 tmp 残留属预期（下轮清扫），右根不应出现完整产物集
                Check(Directory.GetFiles(right, "big*.bin").Length < 40, "auto: 未跑完全部文件");
            }
            finally { Cleanup(root, db); }
        }

        private static async Task TestIdleCancelHarmless()
        {
            var (root, left, right, job, db, engine) = Setup("idle");
            try
            {
                engine.RequestCancel();   // 无轮次在跑：不得抛异常
                engine.RequestCancel();
                Check(true, "idle: RequestCancel 空闲调用无害");
                var (_, plan) = await engine.RunAsync("manual", execute: false);
                Check(plan.Count == 40, "idle: 引擎照常可分析", plan.Count.ToString());
            }
            finally { Cleanup(root, db); }
        }

        private static async Task TestExternalTokenStillWorks()
        {
            var (root, left, right, job, db, engine) = Setup("ext");
            try
            {
                using var cts = new CancellationTokenSource();
                var round = Task.Run(() => engine.RunAsync("manual", execute: true, ct: cts.Token));
                var reached = WaitFor(() => engine.Status == JobStatus.Syncing);
                cts.Cancel();
                OperationCanceledException? oce = null;
                try { await round; }
                catch (OperationCanceledException ex) { oce = ex; }
                Check(reached && oce != null, "ext: 外部 token 取消仍抛 OCE", oce?.Message ?? "(no ex)");
                Check(engine.Status == JobStatus.Idle && engine.StatusText.Contains("已取消"),
                    "ext: 状态收尾正常", engine.StatusText);
            }
            finally { Cleanup(root, db); }
        }

        private static async Task TestNextRoundAfterCancel()
        {
            var (root, left, right, job, db, engine) = Setup("next");
            try
            {
                var round = Task.Run(() => engine.RunAsync("realtime"));
                WaitFor(() => engine.Status == JobStatus.Syncing);
                engine.RequestCancel();
                try { await round; } catch (OperationCanceledException) { }
                // 取消后立即再跑：_busy 必须已归零、锁已释放、不撞「任务正在运行中」
                var (rec, plan) = await engine.RunAsync("manual", execute: true);
                Check(rec.Status == "ok", "next: 取消后下一轮正常执行", rec.Status);
                Check(Directory.GetFiles(right, "big*.bin").Length == 40, "next: 全量复制完成");
                var (_, plan2) = await engine.RunAsync("manual", execute: false);
                Check(plan2.Count == 0, "next: 再分析无差异");
            }
            finally { Cleanup(root, db); }
        }
    }
}
