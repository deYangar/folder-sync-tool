using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Core;

namespace FolderSync.SmokeTest
{
    /// <summary>
    /// 数据安全与触发链路专项（review-20260912 修复配套）：
    /// S-1 镜像删除安全阀（源根缺失/比例阈值）、H-1 执行期间实时变更补跑、
    /// M-1+P-8 计数守恒与回收站失败注入、M-3 基线对账磁盘确认、L-1/L-4/H-4 路径守卫与防御分支、
    /// M-9 扫描字典大小写口径。
    /// </summary>
    internal static class SafetyTests
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

        private static string TempDir(string tag) =>
            Path.Combine(Path.GetTempPath(), tag + "_" + Guid.NewGuid().ToString("N")[..8]);

        private static void Touch(string path, string content = "x", int ageSec = 0)
        {
            File.WriteAllText(path, content);
            if (ageSec > 0) File.SetLastWriteTime(path, DateTime.Now.AddSeconds(-ageSec));
        }

        public static async Task<int> RunAll()
        {
            await TestSourceRootMissingRefusesRun();
            await TestMirrorDeleteRatioSafetyValve();
            await TestRealtimeChangeDuringRunIsNotLost();
            await TestCountConservationWithRecycleFailure();
            TestBaselineReconcileKeepsOnDiskEntries();
            TestPathGuards();
            TestResolveConflictUnknownPolicyDefaultsToManual();
            TestScannerCaseInsensitiveAndAppDataExclusion();
            await TestProgressSequenceRealAndTerminal();
            Console.WriteLine(_fail == 0 ? "\n=== 安全专项 全部通过 ===" : $"\n=== {_fail} 项失败 ===");
            return _fail;
        }

        // ---------- S-1：源侧根缺失拒绝执行（空集 = 目标整盘删除的数据丢失路径） ----------

        private static async Task TestSourceRootMissingRefusesRun()
        {
            Console.WriteLine("--- S-1 源根缺失拒绝执行 ---");
            var root = TempDir("fss1a");
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(right);
            Touch(Path.Combine(right, "keep.txt"), "data");

            using var db = new Db(Path.Combine(root, "t.db"));
            var job = new SyncJob
            {
                Name = "s1", LeftPath = left, RightPath = right,
                Direction = SyncDirection.MirrorLeftToRight, MirrorDelete = true
            };
            job.Id = db.InsertJob(job);
            var engine = new SyncEngine(job, db);

            var refused = false;
            try { await engine.RunAsync("manual"); }
            catch (DirectoryNotFoundException) { refused = true; }
            Check(refused, "源根缺失（单向 l2r 的源=左）时 RunAsync 抛 DirectoryNotFoundException");
            Check(File.Exists(Path.Combine(right, "keep.txt")), "目标侧未被空集镜像删除");
            try { Directory.Delete(root, true); } catch { }
        }

        // ---------- S-1：删除比例阈值（源侧大量"消失"超阈值时禁用本轮删除） ----------

        private static async Task TestMirrorDeleteRatioSafetyValve()
        {
            Console.WriteLine("--- S-1 删除比例安全阀 ---");
            var root = TempDir("fss1b");
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            // 目标 200 个文件；源只有 10 个（比例 95% 删除）
            for (int i = 0; i < 200; i++) Touch(Path.Combine(right, $"f{i:000}.txt"), $"v{i}");
            for (int i = 0; i < 10; i++) Touch(Path.Combine(left, $"f{i:000}.txt"), $"v{i}");

            using var db = new Db(Path.Combine(root, "t.db"));
            var job = new SyncJob
            {
                Name = "s1b", LeftPath = left, RightPath = right,
                Direction = SyncDirection.MirrorLeftToRight, MirrorDelete = true
            };
            job.Id = db.InsertJob(job);
            var engine = new SyncEngine(job, db);

            var oldMin = SyncEngine.MirrorDeleteMinBulk;
            SyncEngine.MirrorDeleteMinBulk = 50;   // 默认值（显式设，防其他用例改过）
            var (rec, plan) = await engine.RunAsync("manual");
            var delCount = plan.Count(p => p.Action == SyncAction.DeleteRight);
            Check(delCount == 0, $"190/200 删除超 30% 阈值 → 计划里 0 条删除（实际 {delCount}）");
            Check(File.Exists(Path.Combine(right, "f199.txt")), "超阈值时目标孤儿保留");
            Check(engine.LastWarning?.Contains("安全阈值") == true, "LastWarning 提示阈值触发", engine.LastWarning ?? "<无>");

            // 阈值内（低于绝对豁免线）正常删
            SyncEngine.MirrorDeleteMinBulk = 5000;
            for (int i = 10; i < 200; i++) File.Delete(Path.Combine(right, $"f{i:000}.txt"));
            var (rec2, plan2) = await engine.RunAsync("manual");
            Check(plan2.Count(p => p.Action == SyncAction.DeleteRight) == 0 && File.Exists(Path.Combine(right, "f005.txt")),
                "小额删除走正常镜像路径（豁免线内，本轮实际无差异无删除）");
            SyncEngine.MirrorDeleteMinBulk = oldMin;
            try { Directory.Delete(root, true); } catch { }
        }

        // ---------- H-1：同步执行期间到达的实时变更不再丢失 ----------

        private static async Task TestRealtimeChangeDuringRunIsNotLost()
        {
            Console.WriteLine("--- H-1 执行期间变更补跑 ---");
            var root = TempDir("fsh1");
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            // 源侧大文件让执行窗口足够长（执行中注入变更的前提；CancelTests 同款量级 40×5MB）
            var payload = new string('a', 5 * 1024 * 1024);
            for (int i = 0; i < 20; i++) Touch(Path.Combine(left, $"big{i:00}.bin"), payload + i);

            using var db = new Db(Path.Combine(root, "t.db"));
            var job = new SyncJob
            {
                Name = "h1", LeftPath = left, RightPath = right,
                Direction = SyncDirection.MirrorLeftToRight,
                Trigger = TriggerType.Realtime, DebounceSeconds = 1
            };
            job.Id = db.InsertJob(job);
            var engine = new SyncEngine(job, db);
            engine.ApplyTriggers();   // 挂实时监听（非提权环境 USN 失败自动降级 FSW，事件照样来）
            Thread.Sleep(500);        // 等 watcher 就绪

            var runCount = 0;
            engine.RunFinished += (_, r) => { if (r.Trigger is "manual" or "realtime") Interlocked.Increment(ref runCount); };

            var t = Task.Run(async () =>
            {
                // 手动轮次开跑 → 执行期间源侧新增一个文件（单向任务只监听源侧——执行器写目标侧不触发，
                // H-1 修复前这条变更会因 _busy 撞车被静默吞掉，等下一个无关事件才可能被捞回）
                try
                {
                    var (rec, _) = await engine.RunAsync("manual");
                    return (rec, null as Exception);
                }
                catch (Exception ex) { return (null as RunRecord, ex); }
            });
            // 确定性执行中信号：进度进入 copy 阶段（只等"进入 copy"不等完成——本机 NVMe 热缓存下百 MB
            // 亚秒完成，但 CI 虚机共享磁盘写百 MB 可能超 15s，窗口放宽到 45s 防慢环境误报）
            var copyStarted = new ManualResetEventSlim(false);
            engine.Progress += (_, p) => { if (p.Phase == "copy") copyStarted.Set(); };
            if (!copyStarted.Wait(45000))
            {
                var (_, runErr) = await t;
                Check(false, "轮次进入执行阶段", runErr?.Message ?? "<45s 未见 copy 阶段>");
                return;
            }
            if (!WaitFor(() => engine.IsRunning, 2000))
            {
                Check(false, "copy 阶段时轮次在跑（IsRunning）");
                return;
            }
            Touch(Path.Combine(left, "during-run.txt"), "changed during run");
            Check(engine.IsRunning, "变更注入发生在轮次执行中（真补跑场景）");
            var (rec1, err1) = await t;
            Check(err1 == null && rec1!.Status == "ok", "主轮次正常完成", err1?.Message ?? rec1!.Status);

            // 补跑机制：轮次收尾发现 pending → 排一轮 realtime → during-run.txt 被捞回
            var got = WaitFor(() => File.Exists(Path.Combine(right, "during-run.txt")), 20000);
            Check(got, "执行期间到达的变更经补跑轮同步到目标侧（旧版被静默丢弃）");
            try { Directory.Delete(root, true); } catch { }
        }

        // ---------- M-1 + P-8：计数守恒 + 回收站删除失败注入 ----------

        private static async Task TestCountConservationWithRecycleFailure()
        {
            Console.WriteLine("--- M-1/P-8 计数守恒（回收站失败注入） ---");
            var root = TempDir("fsm1");
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            for (int i = 0; i < 3; i++) Touch(Path.Combine(left, $"keep{i}.txt"), "k" + i);
            for (int i = 0; i < 3; i++) Touch(Path.Combine(right, $"orphan{i}.txt"), "o" + i);
            // 常规复制项也有（守恒 = ok+skipped+failed == plan.Count 的全路径验证）
            Touch(Path.Combine(left, "new.txt"), "n");

            using var db = new Db(Path.Combine(root, "t.db"));
            var job = new SyncJob
            {
                Name = "m1", LeftPath = left, RightPath = right,
                Direction = SyncDirection.MirrorLeftToRight, MirrorDelete = true,
                DeleteToRecycleBin = true
            };
            job.Id = db.InsertJob(job);
            var engine = new SyncEngine(job, db);

            // 先做一轮干净同步，让目标有孤儿可删
            await engine.RunAsync("manual");

            // 制造删除计划：目标新增 3 个孤儿（低于豁免线）+ 其中 1 个「被占用」（回收站删除必失败；
            // FileShare.Read 手法 Unix 无强制力——TestFileOps 注入跳过该条，核对兜底按失败计）
            for (int i = 0; i < 3; i++) Touch(Path.Combine(right, $"locked{i}.txt"), "l" + i);
            using var blocker = TestFileOps.FailRecycle(Path.Combine(right, "locked1.txt"));

            try
            {
                var (rec, plan) = await engine.RunAsync("manual");
                var del = plan.Count(p => p.Action == SyncAction.DeleteRight);
                // 守恒断言（P-8）：ok + skipped + failed == plan.Count —— M-1 修复前回收站失败双计（虚高 1）
                Check(rec.CopiedFiles + rec.SkippedFiles + rec.FailedFiles == plan.Count,
                    "计数守恒 ok+skipped+failed == plan.Count",
                    $"ok={rec.CopiedFiles} skip={rec.SkippedFiles} fail={rec.FailedFiles} plan={plan.Count} del={del}");
                Check(rec.FailedFiles >= 1, "被占用文件的回收站删除计入 failed", $"failed={rec.FailedFiles}");
                Check(File.Exists(Path.Combine(right, "locked1.txt")), "被占用的文件还在（真失败，不是假成功）");
                Check(File.Exists(Path.Combine(right, "locked0.txt")) == false
                   && File.Exists(Path.Combine(right, "locked2.txt")) == false, "未占用的孤儿正常删除");
                Check(engine.LastFailedItems.Any(f => f.Action is "DeleteRight" or "DeleteLeft"),
                    "失败明细的动作可被 RetryFailedAsync 还原（不再是 \"RecycleDelete\" 死串）");
            }
            finally
            {
                blocker.Dispose();
                try { Directory.Delete(root, true); } catch { }
            }
        }

        // ---------- M-3：基线对账磁盘确认（共享侧根的单任务存活集不误删他任务条目） ----------

        private static void TestBaselineReconcileKeepsOnDiskEntries()
        {
            Console.WriteLine("--- M-3 基线对账磁盘确认 ---");
            var root = TempDir("fsm3");
            var side = Path.Combine(root, "S");
            Directory.CreateDirectory(side);
            var aFile = Path.Combine(side, "taskA.txt");
            var bFile = Path.Combine(side, "taskB.tmp");   // 任务 B 的文件（任务 A 排除 *.tmp 看不见它）
            Touch(aFile, "a");
            Touch(bFile, "b");

            // 手工写两条基线（模拟共享侧根下两个任务各建一条）
            BaselineStore.SaveFile(side, "taskA.txt",
                new BaselineFile { Size = 1, MtimeUtc = DateTime.UtcNow, FileSha = "00", ChunkCount = 0 },
                new List<BaselineChunk>());
            BaselineStore.SaveFile(side, "taskB.tmp",
                new BaselineFile { Size = 1, MtimeUtc = DateTime.UtcNow, FileSha = "11", ChunkCount = 0 },
                new List<BaselineChunk>());

            // 任务 A 的存活集只含 taskA.txt（*.tmp 被排除规则滤掉）——修复前 taskB.tmp 会被当死条目删
            var liveOfA = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "taskA.txt" };
            BaselineStore.Reconcile(side, liveOfA);

            Check(BaselineStore.GetEntry(side, "taskB.tmp") != null,
                "磁盘上仍存在的他任务条目不被误删（M-3）");
            Check(BaselineStore.GetEntry(side, "taskA.txt") != null, "存活条目保留");

            // 真删掉磁盘文件后，对账清死条目
            File.Delete(bFile);
            BaselineStore.Reconcile(side, liveOfA);
            Check(BaselineStore.GetEntry(side, "taskB.tmp") == null, "磁盘上真消失的死条目被清");
            try { BaselineStore.ClearAll(side); Directory.Delete(root, true); } catch { }
        }

        // ---------- L-1 / P-13 / H-4：执行入口路径守卫 ----------

        private static void TestPathGuards()
        {
            Console.WriteLine("--- L-1/P-13/H-4 路径守卫 ---");
            var guardRoot = TempDir("fsguard");
            Directory.CreateDirectory(guardRoot);
            using var db = new Db(Path.Combine(guardRoot, "t.db"));
            // async 入口的异常在返回的 Task 里：不 await 就 catch 不到
            string RunAndCapture(Func<Task> call)
            {
                try { call().Wait(); return ""; }
                catch (AggregateException ax) { return ax.InnerException?.Message ?? ax.Message; }
                catch (Exception ex) { return ex.Message; }
            }

            var msg1 = RunAndCapture(() => new SyncEngine(new SyncJob
            { Name = "g1", LeftPath = @"C:\a\b", RightPath = @"C:\a", Direction = SyncDirection.TwoWay }, db)
                .RunPlanAsync(new List<PlanEntry>()));
            Check(msg1.Contains("嵌套"), "两侧互嵌套被运行时守卫拒绝（RunPlanAsync 入口）", msg1);

            var appDir = AppContext.BaseDirectory.TrimEnd('\\');
            var msg2 = RunAndCapture(() => new SyncEngine(new SyncJob { Name = "g2", LeftPath = appDir, RightPath = TempDir("fsguard2") }, db)
                .ExecutePlanAsync(new List<PlanEntry>()));
            Check(msg2.Contains("重叠"), "任务侧覆盖程序/数据目录被运行时守卫拒绝（ExecutePlanAsync 入口，防数据自反馈）", msg2);

            var msg3 = RunAndCapture(() => new SyncEngine(new SyncJob { Name = "g3", LeftPath = "", RightPath = @"C:\a" }, db)
                .ExecutePlanAsync(new List<PlanEntry>()));
            Check(msg3.Contains("路径为空"), "空路径守卫对 ExecutePlanAsync 同样生效（L-1）", msg3);
        }

        // ---------- L-4：越界 ConflictPolicy → Manual（冲突不静默消失） ----------

        private static void TestResolveConflictUnknownPolicyDefaultsToManual()
        {
            Console.WriteLine("--- L-4 越界冲突策略兜底 ---");
            var l = new Dictionary<string, FileEntry> { ["a.txt"] = new() { Size = 1, MtimeUtc = DateTime.UtcNow } };
            var r = new Dictionary<string, FileEntry> { ["a.txt"] = new() { Size = 2, MtimeUtc = DateTime.UtcNow } };
            var job = new SyncJob { Direction = SyncDirection.TwoWay, ConflictPolicy = (ConflictPolicy)99 };
            var plan = Differ.ComputeTwoWay(job, l, r, new Dictionary<string, FileEntry>());
            Check(plan.Count == 1 && plan[0].Action == SyncAction.Conflict,
                "未知冲突策略落 Manual 冲突（不静默消失）");
        }

        // ---------- M-9 + H-4：扫描口径 ----------

        private static void TestScannerCaseInsensitiveAndAppDataExclusion()
        {
            Console.WriteLine("--- M-9/H-4 扫描口径 ---");
            var root = TempDir("fsm9");
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            Touch(Path.Combine(left, "Readme.TXT"), "same");
            Touch(Path.Combine(right, "readme.txt"), "same");   // 内容/大小一致，仅大小写不同

            var l = Scanner.ScanTreeAsync(left, "", null, CancellationToken.None).Result;
            var r = Scanner.ScanTreeAsync(right, "", null, CancellationToken.None).Result;
            var job = new SyncJob { LeftPath = left, RightPath = right, Direction = SyncDirection.MirrorLeftToRight };
            var plan = Differ.Compute(job, l.Entries, r.Entries);
            // v1.5 B3 起：跨侧仅大小写不同不再静默视为同一路径（旧逻辑内容一致时永远不纠正文件名写法），
            // 显式产冲突行（单向执行=按源覆盖纠正目标写法；键匹配仍 OrdinalIgnoreCase 不重复不漏）
            Check(plan.Count == 1 && plan[0].Action == SyncAction.Conflict
                  && plan[0].Note.Contains("仅大小写不同"),
                "跨侧大小写不同产出冲突行（B3：不再静默漏检）",
                $"plan={plan.Count} {plan.FirstOrDefault()?.Note}");

            // 程序目录排除：扫描结果里不该有任何程序数据文件（把 exe 目录当树根扫——哈希名唯一，挑自身 exe 验证）
            var exeDir = AppContext.BaseDirectory;
            var selfExe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrEmpty(selfExe) && File.Exists(selfExe))
            {
                var probe = Scanner.ScanTreeAsync(exeDir, "", null, CancellationToken.None).Result;
                Check(probe.Entries.Count == 0,
                    "程序目录子树被整体排除（H-4：exe/db/版本库不自反馈）",
                    $"entries={probe.Entries.Count}");
            }
            try { Directory.Delete(root, true); } catch { }
        }

        // ---------- 进度时序（UI 假卡死根因）+ 扫描真进度 ----------

        /// <summary>①无差异轮次必发 done 终态（执行器不跑就没有 done 事件，UI 永远停在对比阶段——
        /// 2026-09-12 实际事故：86ms 完成的轮次界面停在"②扫描右侧"）；
        /// ②扫描事件带目录分母（TotalItems>0）；
        /// ③事件带轮次序号 RunSeq（UI 据此丢弃过时事件）。</summary>
        private static async Task TestProgressSequenceRealAndTerminal()
        {
            Console.WriteLine("--- 进度时序 + 扫描真进度 ---");
            var root = TempDir("fsprog");
            var left = Path.Combine(root, "L", "sub", "deep");
            var right = Path.Combine(root, "R", "sub", "deep");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            File.WriteAllText(Path.Combine(left, "a.txt"), "same");
            File.WriteAllText(Path.Combine(right, "a.txt"), "same");

            using var db = new Db(Path.Combine(root, "t.db"));
            var job = new SyncJob { Name = "prog", LeftPath = Path.Combine(root, "L"), RightPath = Path.Combine(root, "R") };
            job.Id = db.InsertJob(job);
            var engine = new SyncEngine(job, db);

            var events = new System.Collections.Concurrent.ConcurrentQueue<ProgressInfo>();
            engine.Progress += (_, p) => events.Enqueue(p);

            // 第一轮（无差异，execute:true——走 RunAsync 主路径）
            await engine.RunAsync("manual", execute: true);
            WaitFor(() => events.Any(e => e.Phase == "done"), 5000);   // Progress<T> 经线程池投递，事件追平

            Check(events.Any(e => e.Phase == "done"), "无差异轮次也发 done 终态事件（UI 不再假卡死）");
            var scans = events.Where(e => e.Phase.StartsWith("scan")).ToList();
            Check(scans.Any(e => e.TotalItems > 0), "扫描事件带文件夹总数分母（真进度）",
                $"TotalItems: {string.Join(",", scans.Select(e => e.TotalItems).Distinct())}");
            Check(scans.All(e => e.DoneItems <= Math.Max(1, e.TotalItems)), "分子不超过分母（无跳变）");
            Check(scans.All(e => e.RunSeq > 0), "事件带轮次序号 RunSeq（UI 过期过滤依据）");
            Check(scans.Select(e => e.RunSeq).Distinct().Count() <= 2, "同轮事件序号一致");

            // 第二轮：序号必须比第一轮大（轮间递增——作废晚到事件的基础）
            var seqBefore = engine.CurrentProgressSeq;
            await engine.RunAsync("manual", execute: true);
            Check(engine.CurrentProgressSeq > seqBefore, "轮次收尾序号递增");
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
