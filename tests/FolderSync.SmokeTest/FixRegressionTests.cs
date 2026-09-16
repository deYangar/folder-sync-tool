using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FolderSync.Core;

namespace FolderSync.SmokeTest
{
    /// <summary>
    /// 2026-09-08 review 修复的专项回归：
    /// 1) 单向类型冲突（文件 vs 目录）——同路径删除+重建对，删除必须先行，执行零失败且收敛；
    /// 2) Db 并发写库——单连接多线程压测（修复前会抛 "does not support parallel transactions"）；
    /// 3) DirWatcher 排除过滤——排除项不触发、正常文件触发（修复前 Filters 语义反转，正常变更不触发）。
    /// </summary>
    internal static class FixRegressionTests
    {
        private static int _fail;

        private static void Check(bool cond, string name)
        {
            Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}");
            if (!cond) _fail++;
        }

        public static async Task<int> RunAll()
        {
            await TestTypeConflictOneWay();
            TestDbConcurrentHammer();
            TestDirWatcherExcludeFilter();
            await TestStrictMirror();
            TestMtimeTolerance();
            await TestDeletedCountAndBatchRecycle();
            await TestPreviewNoRunRecord();
            await TestAtomicReplace();
            await TestTmpSweepAndNoBackflow();
            await TestCopyMtimeRegression();
            await TestVersionKeepBasic();
            await TestVersionKeepOnDelete();
            VersionStore.PruneThrottle = TimeSpan.Zero;   // P-1 节流会让连跑的多轮清理被跳过，测试关掉
            await TestVersionPruneGenerations();
            await TestVersionDisabledRegression();
            await TestTwoWayVersionStoreIsolation();
            await TestVersionStoreReadonlyFallback();
            await TestDirDeleteFilesVersioned();
            TestOldDbMigrateVersionColumn();
            await TestHotplugMedia();
            await TestFailedItemsAndRetry();
            await TestFailedItemsCap();
            TestNotificationThrottle();
            TestRootOfPathBoundary();
            TestJobRoundTripAllColumns();

            Console.WriteLine(_fail == 0 ? "\n=== 修复回归全部通过 ===" : $"\n=== {_fail} 项失败 ===");
            return _fail;
        }

        /// <summary>jobs/runs 全字段往返：Insert → 新连接 GetJobs/GetJobById/GetRecentRuns 读回逐字段一致。
        /// 防复发：MatchNamesWithUnderscores 只去下划线，trigger_type(triggertype)≠Trigger、
        /// avg_speed(avgspeed)≠AvgSpeedBytesPerSec 曾静默丢映射——重启后实时任务全变手动。</summary>
        private static void TestJobRoundTripAllColumns()
        {
            var root = Path.Combine(Path.GetTempPath(), "fstestrt_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(root);
            try
            {
                var dbPath = Path.Combine(root, "rt.db");
                long jid, rid;
                using (var db = new Db(dbPath))
                {
                    var job = new SyncJob
                    {
                        Name = "rt", LeftPath = Path.Combine(root, "L"), RightPath = Path.Combine(root, "R"),
                        Direction = SyncDirection.TwoWay, Trigger = TriggerType.Interval,
                        IntervalSeconds = 1800, DebounceSeconds = 3, ExcludePatterns = "*.tmp; .git",
                        AutoStart = false, Enabled = true, DeleteToRecycleBin = false, MirrorDelete = true,
                        StrictMirror = true, ConflictPolicy = ConflictPolicy.LargestSize,
                        VersionKeepCount = 7, DeltaSync = false
                    };
                    jid = db.InsertJob(job);
                    rid = db.InsertRun(new RunRecord { JobId = jid, StartedAt = DateTime.Now, Trigger = "interval" });
                    db.FinishRun(new RunRecord
                    {
                        Id = rid, JobId = jid, Status = "ok", FinishedAt = DateTime.Now,
                        CopiedFiles = 2, BytesCopied = 123456, DeltaSavedBytes = 999, AvgSpeedBytesPerSec = 42.5
                    });
                }
                // 新连接 = 重启软件后的读路径
                using (var db2 = new Db(dbPath))
                {
                    var back = db2.GetJobs().Single(j => j.Id == jid);
                    Check(back.Trigger == TriggerType.Interval, "roundtrip Trigger（重启后实时/定时不再丢）", ((int)back.Trigger).ToString());
                    Check(back.Direction == SyncDirection.TwoWay, "roundtrip Direction");
                    Check(back.IntervalSeconds == 1800 && back.DebounceSeconds == 3, "roundtrip interval/debounce");
                    Check(back.ExcludePatterns == "*.tmp; .git", "roundtrip exclude");
                    Check(!back.AutoStart && back.Enabled, "roundtrip auto_start/enabled");
                    Check(!back.DeleteToRecycleBin && back.MirrorDelete && back.StrictMirror, "roundtrip recycle/mirror/strict");
                    Check(back.ConflictPolicy == ConflictPolicy.LargestSize, "roundtrip conflict_policy");
                    Check(back.VersionKeepCount == 7 && !back.DeltaSync, "roundtrip version_keep/delta");

                    var byId = db2.GetJob(jid);
                    Check(byId?.Trigger == TriggerType.Interval && byId?.VersionKeepCount == 7, "roundtrip GetJobById");

                    var run = db2.GetRecentRuns(jid).Single();
                    Check(run.Trigger == "interval" && run.Status == "ok", "roundtrip run trigger/status");
                    Check(run.AvgSpeedBytesPerSec == 42.5, "roundtrip avg_speed", run.AvgSpeedBytesPerSec?.ToString() ?? "(null)");
                    Check(run.BytesCopied == 123456 && run.DeltaSavedBytes == 999, "roundtrip bytes/delta_saved");
                }
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        private static void Check(bool cond, string name, string extra)
        {
            Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name} ({extra})");
            if (!cond) _fail++;
        }

        private sealed class InlineProgress : IProgress<ProgressInfo>
        {
            private readonly Action<ProgressInfo> _onReport;
            public InlineProgress(Action<ProgressInfo> onReport) => _onReport = onReport;
            public void Report(ProgressInfo value) => _onReport(value);
        }

        private static string Sha256File(string path)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(fs));
        }

        /// <summary>①-1/2 原子替换：覆盖大文件中途取消 → 目标旧内容字节级完好 + 无 tmp 残留</summary>
        private static async Task TestAtomicReplace()
        {
            var root = Path.Combine(Path.GetTempPath(), "fstestar_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            try
            {
                // 300MB 大文件：保证取消发生在传输中（首次进度回调即取消，时机最早）
                var big = Path.Combine(left, "big.bin");
                var chunk = new byte[1 << 20];
                new Random(42).NextBytes(chunk);
                using (var fs = new FileStream(big, FileMode.Create))
                    for (int i = 0; i < 300; i++) fs.Write(chunk, 0, chunk.Length);
                File.WriteAllText(Path.Combine(right, "big.bin"), "OLD");

                var job = new SyncJob { Name = "atomic", LeftPath = left, RightPath = right,
                    Direction = SyncDirection.MirrorLeftToRight };
                var exec = new Executor(job);
                var plan = new List<PlanEntry> { new PlanEntry
                {
                    Action = SyncAction.UpdateRight, RelativePath = "big.bin",
                    IsDirectory = false, Size = new FileInfo(big).Length
                } };
                var cancelled = false;
                var progress = new InlineProgress(p =>
                {
                    if (p.DoneBytes > 0) exec.Cancel();   // 传输一开始就取消
                });
                try { await exec.ExecuteAsync(plan, progress, CancellationToken.None); }
                catch (OperationCanceledException) { cancelled = true; }

                Check(cancelled, "原子替换: 中途取消以 OperationCanceledException 上报");
                Check(File.ReadAllText(Path.Combine(right, "big.bin")) == "OLD",
                    "原子替换: 取消后目标旧内容字节级完好（修复前会被半截文件替换）");
                Check(!Directory.EnumerateFiles(right, "*" + Scanner.TmpSuffix,
                          SearchOption.AllDirectories).Any(),
                    "原子替换: 取消后无 .foldersync-tmp 残留");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>①-3/5 残留清扫 + 防反向扑源：垃圾 tmp 下轮被清且不产生差异；tmp 形态断电残留不会被"新者胜"扑源</summary>
        private static async Task TestTmpSweepAndNoBackflow()
        {
            var root = Path.Combine(Path.GetTempPath(), "fstestsw_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            try
            {
                // 垃圾 tmp（含嵌套目录）+ 一个真实差异 → 执行轮应清扫 tmp 且 tmp 不进差异
                Directory.CreateDirectory(Path.Combine(right, "sub"));
                File.WriteAllText(Path.Combine(right, "sub", "junk.txt" + Scanner.TmpSuffix), "garbage");
                File.WriteAllText(Path.Combine(right, "root.foldersync-tmp"), "garbage");
                File.WriteAllText(Path.Combine(left, "new.txt"), "fresh");

                var job = new SyncJob { Name = "sweep", LeftPath = left, RightPath = right,
                    Direction = SyncDirection.MirrorLeftToRight };
                using var db = new Db(Path.Combine(root, "t.db"));
                job.Id = db.InsertJob(job);
                var engine = new SyncEngine(job, db);
                var (rec, plan) = await engine.RunAsync("manual", execute: true);

                Check(plan.Count == 1 && plan[0].RelativePath == "new.txt" && plan[0].Action == SyncAction.CreateRight,
                    $"残留清扫: tmp 不进差异（计划仅含 new.txt，实际 {string.Join(",", plan.Select(p => p.RelativePath))}）");
                Check(!Directory.EnumerateFiles(right, "*" + Scanner.TmpSuffix, SearchOption.AllDirectories).Any(),
                    "残留清扫: 执行轮开始时两侧垃圾 tmp 全部被清扫");
                Check(File.ReadAllText(Path.Combine(right, "new.txt")) == "fresh", "残留清扫: 真实差异正常同步");

                // 断电残留形态：目标本体完好（旧），旁边躺 mtime 较新的半截 tmp → 下轮必须源覆盖目标，绝不反向扑
                File.WriteAllText(Path.Combine(right, "big.bin"), "OLD-COMPLETE");   // 右侧旧版本先落盘
                Thread.Sleep(1500);   // 拉开 mtime（> 50ms NTFS 容差），保证左侧是"新者"
                var srcFile = Path.Combine(left, "big.bin");
                var chunk = new byte[1 << 20];
                new Random(7).NextBytes(chunk);
                using (var fs = new FileStream(srcFile, FileMode.Create))
                    for (int i = 0; i < 8; i++) fs.Write(chunk, 0, chunk.Length);
                File.WriteAllText(Path.Combine(right, "big.bin.foldersync-tmp"), "HALF");
                File.SetLastWriteTimeUtc(Path.Combine(right, "big.bin.foldersync-tmp"), DateTime.UtcNow.AddHours(1));

                var (rec2, plan2) = await engine.RunAsync("manual", execute: true);
                var bigPlan = plan2.Where(p => p.RelativePath == "big.bin").ToList();
                Check(bigPlan.Count == 1 && bigPlan[0].Action == SyncAction.UpdateRight,
                    $"防反向扑源: 断电残留场景判定为源覆盖目标（实际 {bigPlan.FirstOrDefault()?.Action}）");
                Check(!File.Exists(Path.Combine(right, "big.bin.foldersync-tmp")),
                    "防反向扑源: 半截 tmp 已被清扫");
                Check(Sha256File(Path.Combine(right, "big.bin")) == Sha256File(srcFile),
                    "防反向扑源: 目标最终为完整源内容（字节级）");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>②-6 版本保留基本：N=5 覆盖 → 旧版进版本库（批次/相对路径正确）+ 新内容到位</summary>
        private static async Task TestVersionKeepBasic()
        {
            var root = Path.Combine(Path.GetTempPath(), "fstestvk_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(Path.Combine(left, "sub"));
            Directory.CreateDirectory(Path.Combine(right, "sub"));
            try
            {
                File.WriteAllText(Path.Combine(left, "sub", "doc.txt"), "v1");
                File.WriteAllText(Path.Combine(right, "sub", "doc.txt"), "v0");
                File.WriteAllText(Path.Combine(left, "sub", "doc.txt"), "v2-newer");
                File.SetLastWriteTimeUtc(Path.Combine(left, "sub", "doc.txt"), DateTime.UtcNow.AddMinutes(1));

                var job = new SyncJob { Name = "vk", LeftPath = left, RightPath = right,
                    Direction = SyncDirection.MirrorLeftToRight, VersionKeepCount = 5 };
                using var db = new Db(Path.Combine(root, "t.db"));
                job.Id = db.InsertJob(job);
                var engine = new SyncEngine(job, db);
                await engine.RunAsync("manual", execute: true);

                var store = Path.Combine(right, Scanner.VersionStoreDir);
                Check(File.ReadAllText(Path.Combine(right, "sub", "doc.txt")) == "v2-newer",
                    "版本保留: 新内容已到位");
                var versions = VersionStore.ListVersions(right, job.Id, "sub/doc.txt");
                Check(versions.Count == 1, $"版本保留: 旧版 1 版入库（实际 {versions.Count}）");
                Check(versions.Count == 1 && versions[0].Size == 2, "版本保留: 索引记录相对路径/大小（v0=2B）");
                var restored = Path.Combine(root, "restored-v0.txt");
                VersionStore.RestoreVersion(right, versions[0].Id, restored);
                Check(File.ReadAllText(restored) == "v0", "版本保留: 还原内容为覆盖前旧版 v0");
                Check(Directory.EnumerateFiles(Path.Combine(VersionStore.RepoDirOf(right), "objects"), "*", SearchOption.AllDirectories).Any(),
                    "版本保留: repo objects 落盘有块");
                Check(!Directory.Exists(store), "版本保留: 库不落在被同步侧根下（已中心化）");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>②-7 文件删除（MirrorDelete 孤儿）→ 进版本库而非回收站</summary>
        private static async Task TestVersionKeepOnDelete()
        {
            var root = Path.Combine(Path.GetTempPath(), "fstestvd_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            try
            {
                File.WriteAllText(Path.Combine(right, "orphan.txt"), "to-be-deleted");
                var job = new SyncJob { Name = "vdel", LeftPath = left, RightPath = right,
                    Direction = SyncDirection.MirrorLeftToRight, MirrorDelete = true,
                    DeleteToRecycleBin = true, VersionKeepCount = 3 };
                using var db = new Db(Path.Combine(root, "t.db"));
                job.Id = db.InsertJob(job);
                var engine = new SyncEngine(job, db);
                var (rec, _) = await engine.RunAsync("manual", execute: true);

                Check(!File.Exists(Path.Combine(right, "orphan.txt")), "删除入库: 目标文件已删除");
                var versions = VersionStore.ListVersions(right, job.Id, "orphan.txt");
                Check(versions.Count == 1, $"删除入库: 旧文件进版本库而非回收站（实际 {versions.Count} 版）");
                var restored = Path.Combine(root, "restored-orphan.txt");
                VersionStore.RestoreVersion(right, versions[0].Id, restored);
                Check(File.ReadAllText(restored) == "to-be-deleted", "删除入库: 还原内容正确");
                Check(rec.FailedFiles == 0, "删除入库: 全程零失败");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>②-8 连续覆盖 7 次 → 只保留最近 5 代（N=5），老的已清</summary>
        private static async Task TestVersionPruneGenerations()
        {
            var root = Path.Combine(Path.GetTempPath(), "fstestvp_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            try
            {
                var job = new SyncJob { Name = "vprune", LeftPath = left, RightPath = right,
                    Direction = SyncDirection.MirrorLeftToRight, VersionKeepCount = 5 };
                using var db = new Db(Path.Combine(root, "t.db"));
                job.Id = db.InsertJob(job);
                var engine = new SyncEngine(job, db);

                for (int v = 1; v <= 7; v++)
                {
                    File.WriteAllText(Path.Combine(left, "f.txt"), $"v{v}");
                    File.WriteAllText(Path.Combine(right, "f.txt"), $"v{v-1}");   // 目标旧版，触发覆盖
                    File.SetLastWriteTimeUtc(Path.Combine(left, "f.txt"), DateTime.UtcNow.AddMinutes(v));
                    var (rec, _) = await engine.RunAsync("manual", execute: true);
                    if (rec.FailedFiles > 0) break;
                }

                var versions = new List<StoredVersion>();
                for (int i = 0; i < 100; i++)   // prune 在成功后异步执行：轮询等待收敛到 N 代
                {
                    versions = VersionStore.ListVersions(right, job.Id, "f.txt");
                    if (versions.Count == 5) break;
                    await Task.Delay(100);
                }
                Check(versions.Count == 5, $"代数清理: 7 次覆盖后仅存最近 5 代（实际 {versions.Count}）");
                // 还原各版本核对内容：应为 v2~v6（v0/v1 已清）
                var contents = new List<string>();
                foreach (var ver in versions.OrderBy(v => v.Ts))
                {
                    var rp = Path.Combine(root, $"chk-{ver.Id}.txt");
                    VersionStore.RestoreVersion(right, ver.Id, rp);
                    contents.Add(File.ReadAllText(rp));
                }
                Check(contents.OrderBy(s => s).SequenceEqual(new[] { "v2", "v3", "v4", "v5", "v6" }),
                    $"代数清理: 保留的是最近 5 代 v2~v6（实际 {string.Join(",", contents.OrderBy(s => s))}）");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>②-9 N=0 回归：行为与现状完全一致（无版本库目录产生）</summary>
        private static async Task TestVersionDisabledRegression()
        {
            var root = Path.Combine(Path.GetTempPath(), "fstestv0_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            try
            {
                File.WriteAllText(Path.Combine(left, "a.txt"), "new");
                File.WriteAllText(Path.Combine(right, "a.txt"), "old");
                File.WriteAllText(Path.Combine(right, "orphan.txt"), "x");
                Thread.Sleep(1200);
                File.SetLastWriteTimeUtc(Path.Combine(left, "a.txt"), DateTime.UtcNow.AddMinutes(1));

                var job = new SyncJob { Name = "v0", LeftPath = left, RightPath = right,
                    Direction = SyncDirection.MirrorLeftToRight, MirrorDelete = true,
                    DeleteToRecycleBin = false, VersionKeepCount = 0 };
                using var db = new Db(Path.Combine(root, "t.db"));
                job.Id = db.InsertJob(job);
                var engine = new SyncEngine(job, db);
                var (rec, plan) = await engine.RunAsync("manual", execute: true);

                Check(File.ReadAllText(Path.Combine(right, "a.txt")) == "new", "N=0 回归: 覆盖正常");
                Check(!File.Exists(Path.Combine(right, "orphan.txt")), "N=0 回归: 孤儿正常删除");
                Check(!Directory.Exists(Path.Combine(right, Scanner.VersionStoreDir)) &&
                      !Directory.Exists(Path.Combine(left, Scanner.VersionStoreDir)),
                    "N=0 回归: 两侧均不产生版本库目录");
                Check(rec.FailedFiles == 0, "N=0 回归: 零失败");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>②-10 双向：两侧各有版本库且互不复制（同步后差异 0、快照无 _FolderSync_Versions 条目）</summary>
        private static async Task TestTwoWayVersionStoreIsolation()
        {
            var root = Path.Combine(Path.GetTempPath(), "fstestvt_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            try
            {
                File.WriteAllText(Path.Combine(left, "init.txt"), "same");
                File.WriteAllText(Path.Combine(right, "init.txt"), "same");
                var job = new SyncJob { Name = "vtw", LeftPath = left, RightPath = right,
                    Direction = SyncDirection.TwoWay, VersionKeepCount = 5,
                    ConflictPolicy = ConflictPolicy.NewestMtime };
                using var db = new Db(Path.Combine(root, "t.db"));
                job.Id = db.InsertJob(job);
                var engine = new SyncEngine(job, db);
                await engine.RunAsync("manual", execute: true);   // 建立快照基线

                // 两侧各新建一个文件（互传）+ 左侧单独改基线文件（较新 → 覆盖右侧旧版 → 右侧产生版本库）
                Thread.Sleep(1100);
                File.WriteAllText(Path.Combine(left, "left-edit.txt"), "from-left");
                Thread.Sleep(1100);
                File.WriteAllText(Path.Combine(right, "right-edit.txt"), "from-right");
                File.WriteAllText(Path.Combine(left, "init.txt"), "left-wins");
                await engine.RunAsync("manual", execute: true);

                var lStore = Path.Combine(left, Scanner.VersionStoreDir);
                var rStore = Path.Combine(right, Scanner.VersionStoreDir);
                var rVersions = VersionStore.Exists(right)
                    ? VersionStore.ListVersions(right, job.Id, "init.txt") : new List<StoredVersion>();
                Check(rVersions.Count == 1, $"双向: 被覆盖侧旧版进版本库（实际 {rVersions.Count}）");
                if (rVersions.Count == 1)
                {
                    var rp = Path.Combine(root, "chk-init.txt");
                    VersionStore.RestoreVersion(right, rVersions[0].Id, rp);
                    Check(File.ReadAllText(rp) == "same", "双向: 还原内容为覆盖前旧版");
                }

                // 预置两侧版本库后再跑一轮：版本库目录不得被当成差异复制/入库
                Directory.CreateDirectory(Path.Combine(lStore, "20200101-000000", "x"));
                File.WriteAllText(Path.Combine(lStore, "20200101-000000", "x", "f.txt"), "lv");
                Directory.CreateDirectory(Path.Combine(rStore, "20200101-000000", "x"));
                File.WriteAllText(Path.Combine(rStore, "20200101-000000", "x", "f.txt"), "rv");
                var (rec, plan) = await engine.RunAsync("manual", execute: true);
                Check(rec.FailedFiles == 0, "双向: 含版本库目录的同步零失败");
                var storePlan = plan.Where(p => p.RelativePath.Contains(Scanner.VersionStoreDir)).ToList();
                Check(storePlan.Count == 0, "双向: 版本库目录不进差异（不自我复制）");
                Check(!db.GetSnapshot(job.Id).Keys.Any(k => k.Contains(Scanner.VersionStoreDir)),
                    "双向: 快照无 _FolderSync_Versions 条目");
                Check(File.ReadAllText(Path.Combine(left, "right-edit.txt")) == "from-right" &&
                      File.ReadAllText(Path.Combine(right, "left-edit.txt")) == "from-left",
                    "双向: 两侧内容正常互传");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>②-11 版本库只读降级：入库失败 → 降级常规删除，不阻断同步</summary>
        private static async Task TestVersionStoreReadonlyFallback()
        {
            var root = Path.Combine(Path.GetTempPath(), "fstestvr_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            try
            {
                File.WriteAllText(Path.Combine(left, "a.txt"), "new");
                File.WriteAllText(Path.Combine(right, "a.txt"), "old");
                Thread.Sleep(1200);
                File.SetLastWriteTimeUtc(Path.Combine(left, "a.txt"), DateTime.UtcNow.AddMinutes(1));

                var job = new SyncJob { Name = "vro", LeftPath = left, RightPath = right,
                    Direction = SyncDirection.MirrorLeftToRight, VersionKeepCount = 5,
                    DeleteToRecycleBin = false };
                using var db = new Db(Path.Combine(root, "t.db"));
                job.Id = db.InsertJob(job);
                var engine = new SyncEngine(job, db);

                // 中心版本库路径被同名文件占位（目录建不起来）→ 模拟中心盘只读/超长等入库失败
                Directory.CreateDirectory(VersionStore.CentralRoot);
                File.WriteAllText(VersionStore.CentralStoreOf(right), "blocker");

                var (rec, plan) = await engine.RunAsync("manual", execute: true);
                Check(rec.FailedFiles == 0,
                    $"只读降级: 入库失败不阻断同步（failed={rec.FailedFiles}）");
                Check(File.ReadAllText(Path.Combine(right, "a.txt")) == "new",
                    "只读降级: 覆盖仍完成（旧版走常规删除）");
                Check(engine.LastWarning != null && engine.LastWarning.Contains("版本入库失败"),
                    $"只读降级: 记录 LastWarning（实际: {engine.LastWarning}）");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>②-12 目录删除：目录本身走回收站/常规删除，目录下文件先入库</summary>
        private static async Task TestDirDeleteFilesVersioned()
        {
            var root = Path.Combine(Path.GetTempPath(), "fstestvdd_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(Path.Combine(right, "deldir", "inner"));
            try
            {
                File.WriteAllText(Path.Combine(right, "deldir", "f1.txt"), "one");
                File.WriteAllText(Path.Combine(right, "deldir", "inner", "f2.txt"), "two");
                var job = new SyncJob { Name = "vdd", LeftPath = left, RightPath = right,
                    Direction = SyncDirection.MirrorLeftToRight, MirrorDelete = true,
                    DeleteToRecycleBin = false, VersionKeepCount = 3 };
                using var db = new Db(Path.Combine(root, "t.db"));
                job.Id = db.InsertJob(job);
                var engine = new SyncEngine(job, db);
                var (rec, _) = await engine.RunAsync("manual", execute: true);

                Check(!Directory.Exists(Path.Combine(right, "deldir")), "目录删除: 目录已删除");
                Check(rec.FailedFiles == 0, "目录删除: 零失败");
                var v1 = VersionStore.ListVersions(right, job.Id, "deldir/f1.txt");
                var v2 = VersionStore.ListVersions(right, job.Id, "deldir/inner/f2.txt");
                Check(v1.Count == 1 && v2.Count == 1, $"目录删除: 目录下 2 个文件全部入库（实际 {v1.Count}/{v2.Count}）");
                if (v1.Count == 1 && v2.Count == 1)
                {
                    var r1 = Path.Combine(root, "chk-f1.txt");
                    var r2 = Path.Combine(root, "chk-f2.txt");
                    VersionStore.RestoreVersion(right, v1[0].Id, r1);
                    VersionStore.RestoreVersion(right, v2[0].Id, r2);
                    Check(File.ReadAllText(r1) == "one" && File.ReadAllText(r2) == "two",
                        "目录删除: 入库保留原相对路径结构且内容正确");
                }
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>②-13 老库迁移：无 version_keep_count 列的库打开 → 自动补列，默认 0 行为不变</summary>
        private static void TestOldDbMigrateVersionColumn()
        {
            var root = Path.Combine(Path.GetTempPath(), "fstestvm_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(root);
            try
            {
                var dbPath = Path.Combine(root, "old.db");
                // 手工造一个 v1.1 形态的老库（无 version_keep_count 列）
                using (var raw = new Microsoft.Data.Sqlite.SqliteConnection(
                           new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
                {
                    raw.Open();
                    raw.Execute("CREATE TABLE jobs(" +
                        "id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, left_path TEXT NOT NULL, " +
                        "right_path TEXT NOT NULL, direction INTEGER NOT NULL DEFAULT 0, " +
                        "trigger_type INTEGER NOT NULL DEFAULT 0, interval_seconds INTEGER NOT NULL DEFAULT 7200, " +
                        "debounce_seconds INTEGER NOT NULL DEFAULT 10, exclude_patterns TEXT NOT NULL DEFAULT '', " +
                        "auto_start INTEGER NOT NULL DEFAULT 1, enabled INTEGER NOT NULL DEFAULT 1, " +
                        "delete_to_recycle_bin INTEGER NOT NULL DEFAULT 1, mirror_delete INTEGER NOT NULL DEFAULT 0, " +
                        "created_at TEXT NOT NULL); " +
                        "INSERT INTO jobs(name,left_path,right_path,created_at) VALUES('legacy','C:\\a','C:\\b','2026-01-01');");
                }
                using (var db = new Db(dbPath))
                {
                    var jobs = db.GetJobs();
                    Check(jobs.Count == 1 && jobs[0].VersionKeepCount == 0,
                        "老库迁移: 自动补列且老任务默认 0（版本保留关闭）");
                }
                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                           new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
                {
                    conn.Open();
                    var cols = conn.Query<string>("SELECT name FROM pragma_table_info('jobs')").AsList();
                    Check(cols.Contains("version_keep_count"), "老库迁移: 物理列已补齐");
                }
                // 老任务读出-写回不炸且值不变
                using (var db2 = new Db(dbPath))
                {
                    var j = db2.GetJobs()[0];
                    db2.UpdateJob(j);
                    Check(db2.GetJobs()[0].VersionKeepCount == 0, "老库迁移: 老任务写回后仍为 0");
                }
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>③-14/15/16 热插拔：拔盘 → WaitingMedia + 卸监听；插回 → 重挂 + reconnect 补跑；消失期写入不炸</summary>
        private static async Task TestHotplugMedia()
        {
            var root = Path.Combine(Path.GetTempPath(), "fstesthp_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            var rightGone = Path.Combine(root, "R_gone");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            try
            {
                File.WriteAllText(Path.Combine(left, "a.txt"), "base");
                File.WriteAllText(Path.Combine(right, "a.txt"), "base");

                var job = new SyncJob { Name = "hotplug", LeftPath = left, RightPath = right,
                    Direction = SyncDirection.MirrorLeftToRight,
                    Trigger = TriggerType.Realtime, Enabled = true, DebounceSeconds = 10 };
                using var db = new Db(Path.Combine(root, "t.db"));
                job.Id = db.InsertJob(job);
                var engine = new SyncEngine(job, db);
                engine.MediaPollSeconds = 1;   // 测试加速：1s 轮询
                engine.ApplyTriggers();

                var watchersField = typeof(SyncEngine).GetField("_watchers",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                int WatcherCount() => ((IDisposable[])watchersField.GetValue(engine)!).Length;

                var deadline = DateTime.UtcNow.AddSeconds(8);
                while (WatcherCount() < 1 && DateTime.UtcNow < deadline) Thread.Sleep(200);
                Check(WatcherCount() >= 1, "热插拔: 初始监听已挂");

                // 模拟拔盘：目标侧根改名消失
                Directory.Move(right, rightGone);
                deadline = DateTime.UtcNow.AddSeconds(8);
                while (engine.Status != JobStatus.WaitingMedia && DateTime.UtcNow < deadline) Thread.Sleep(300);
                Check(engine.Status == JobStatus.WaitingMedia, "热插拔: 拔盘后状态切 WaitingMedia");
                Check(engine.StatusText.Contains("等待介质"), $"热插拔: 状态文本正确（实际 {engine.StatusText}）");
                Check(WatcherCount() == 0, "热插拔: 监听已卸（防轮询错误风暴）");

                // 消失期间在存活侧写入：不炸、不刷错误
                File.WriteAllText(Path.Combine(left, "during-gone.txt"), "written while gone");
                await Task.Delay(3000);
                Check(engine.Status == JobStatus.WaitingMedia,
                    $"热插拔: 消失期间写入事件不触发错误轮（状态仍 WaitingMedia，实际 {engine.Status}/{engine.StatusText}）");
                Check(engine.LastError == null, "热插拔: 消失期间无错误刷屏");

                // 模拟插回：目录改回原名
                Directory.Move(rightGone, right);
                deadline = DateTime.UtcNow.AddSeconds(20);
                RunRecord? reconnectRun = null;
                while (DateTime.UtcNow < deadline)
                {
                    var runs = db.GetRecentRuns(job.Id, 50);
                    reconnectRun = runs.FirstOrDefault(r => r.Trigger == "reconnect");
                    if (reconnectRun != null) break;
                    Thread.Sleep(500);
                }
                Check(reconnectRun != null, "热插拔: 插回后自动触发 reconnect 补跑");
                Check(WatcherCount() >= 1, "热插拔: 插回后监听自动重挂");
                Check(engine.Status != JobStatus.WaitingMedia, "热插拔: 状态离开 WaitingMedia");
                // reconnect 的 run 记录在轮次开始时插入（InsertRun 先于执行），记录可见 ≠ 文件已落盘；
                // 慢机（arm 真机 runner）上瞬查会竞态（2026-09-16 CI 实锤：记录出现后 1ms 断言即 FAIL），
                // 给 deadline 轮询等文件到位
                var synced = Path.Combine(right, "during-gone.txt");
                var syncDeadline = DateTime.UtcNow.AddSeconds(15);
                while (!File.Exists(synced) && DateTime.UtcNow < syncDeadline) Thread.Sleep(300);
                Check(File.Exists(synced), "热插拔: 补跑把消失期间的写入同步到位");
            }
            finally
            {
                try { if (Directory.Exists(rightGone)) Directory.Move(rightGone, right); } catch { }
                try { Directory.Delete(root, true); } catch { }
            }
        }

        /// <summary>④-17/18 独占句柄锁目标 → partial + 明细落盘含该路径；释放后重试成功且其余不动</summary>
        private static async Task TestFailedItemsAndRetry()
        {
            var root = Path.Combine(Path.GetTempPath(), "fstestfi_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            var logDir = Core.Platform.AppPaths.FailedDir(0);
            try
            {
                File.WriteAllText(Path.Combine(left, "locked.txt"), "NEW-CONTENT-LONGER");
                File.WriteAllText(Path.Combine(right, "locked.txt"), "old");
                File.WriteAllText(Path.Combine(left, "fresh.txt"), "fresh");
                Thread.Sleep(1200);
                File.SetLastWriteTimeUtc(Path.Combine(left, "locked.txt"), DateTime.UtcNow.AddMinutes(1));

                var job = new SyncJob { Name = "failed", LeftPath = left, RightPath = right,
                    Direction = SyncDirection.MirrorLeftToRight };
                using var db = new Db(Path.Combine(root, "t.db"));
                job.Id = db.InsertJob(job);
                var engine = new SyncEngine(job, db);

                // 「目标被占用」→ 该条失败，其余正常（FileOps 注入恒失败——Unix 无强制共享锁，
                // 原 FileShare.None 锁目标手法失效；效果等价：条目进失败明细，解除后 RetryFailedAsync 成功）
                using (var blocker = TestFileOps.FailCopy(Path.Combine(left, "locked.txt"), int.MaxValue))
                {
                    var (rec, plan) = await engine.RunAsync("manual", execute: true);
                    Check(rec.Status == "partial", $"失败明细: 锁定目标 → partial（实际 {rec.Status}）");
                    Check(File.ReadAllText(Path.Combine(right, "fresh.txt")) == "fresh",
                        "失败明细: 未锁定文件正常同步");
                    Check(engine.LastFailedItems.Count(f => f.RelativePath == "locked.txt") == 1,
                        "失败明细: LastFailedItems 含被锁路径");
                    Check(engine.LastFailedLogFile != null && File.Exists(engine.LastFailedLogFile),
                        "失败明细: 明细文件已落盘");
                    Check(engine.LastFailedLogFile != null &&
                          File.ReadAllText(engine.LastFailedLogFile).Contains("locked.txt"),
                        "失败明细: 明细文件内容含该路径");
                }

                // 释放后重试：仅重建失败项，成功且其余不动
                var before = File.GetLastWriteTimeUtc(Path.Combine(right, "fresh.txt"));
                var (retryRec, retryPlan) = await engine.RetryFailedAsync();
                Check(retryPlan.Count == 1 && retryPlan[0].RelativePath == "locked.txt",
                    $"重试: 子计划仅含失败项（实际 {retryPlan.Count} 条）");
                Check(retryRec.FailedFiles == 0 && retryRec.Status == "ok",
                    $"重试: 释放后重试成功（{retryRec.Status}）");
                Check(File.ReadAllText(Path.Combine(right, "locked.txt")) == "NEW-CONTENT-LONGER",
                    "重试: 被锁文件最终到位");
                Check(File.GetLastWriteTimeUtc(Path.Combine(right, "fresh.txt")) == before,
                    "重试: 其余文件未被重试轮触碰");
                Check(engine.LastFailedItems.Count == 0, "重试: 成功后失败明细清空");
            }
            finally
            {
                try { if (Directory.Exists(logDir)) Directory.Delete(logDir, true); } catch { }
                try { Directory.Delete(root, true); } catch { }
            }
        }

        /// <summary>④-19 失败超 2000 条 → 明细封顶 2000，总数如实</summary>
        private static async Task TestFailedItemsCap()
        {
            var root = Path.Combine(Path.GetTempPath(), "fstestfc_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            try
            {
                var job = new SyncJob { Name = "cap", LeftPath = left, RightPath = right,
                    Direction = SyncDirection.MirrorLeftToRight };
                // 2500 条指向不存在源的复制计划：全部 Win32 错误（非磁盘满，不触发快速中止）
                var plan = Enumerable.Range(0, 2500)
                    .Select(i => new PlanEntry
                    {
                        Action = SyncAction.CreateRight, RelativePath = $"ghost{i}.txt",
                        IsDirectory = false, Size = 0
                    }).ToList();
                var exec = new Executor(job);
                var (ok, skipped, failed, bytes) = await exec.ExecuteAsync(plan, null, CancellationToken.None);

                Check(failed == 2500, $"失败封顶: 总数如实 2500（实际 {failed}）");
                Check(exec.FailedItems.Count == Executor.MaxFailedItems,
                    $"失败封顶: 明细封顶 {Executor.MaxFailedItems}（实际 {exec.FailedItems.Count}）");
                Check(exec.FailedTotal == 2500, $"失败封顶: FailedTotal 含溢出（实际 {exec.FailedTotal}）");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>④-20 通知节流：同类 5 分钟内抑制，不同类互不影响</summary>
        private static void TestNotificationThrottle()
        {
            var t = new NotificationThrottle(TimeSpan.FromMinutes(5));
            Check(t.ShouldShow("job1:error"), "通知节流: 同类首次放行");
            Check(!t.ShouldShow("job1:error"), "通知节流: 窗口期内同类抑制");
            Check(t.ShouldShow("job1:conflict"), "通知节流: 不同类互不影响");
            Check(!t.ShouldShow("job1:error"), "通知节流: 抑制状态持续（第三次仍抑制）");
            Check(t.ShouldShow("job2:error"), "通知节流: 不同任务互不影响");
        }
        private static async Task TestCopyMtimeRegression()
        {
            var root = Path.Combine(Path.GetTempPath(), "fstestmt_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            try
            {
                var src = Path.Combine(left, "m.txt");
                File.WriteAllText(src, "v1");
                File.WriteAllText(Path.Combine(right, "m.txt"), "v0-longer-old");
                var stamp = DateTime.UtcNow.AddHours(-3);
                File.SetLastWriteTimeUtc(src, stamp);
                // 目标设更旧 → UpdateRight（源覆盖目标）。本用例测 mtime 保留而非裁决方向；
                // v1.7 起 MapFor 按动作后缀定侧，目标较新的 UpdateLeft 会真正回写源（新者胜语义生效）
                File.SetLastWriteTimeUtc(Path.Combine(right, "m.txt"), stamp.AddHours(-1));

                var job = new SyncJob { Name = "mtime", LeftPath = left, RightPath = right,
                    Direction = SyncDirection.MirrorLeftToRight };
                using var db = new Db(Path.Combine(root, "t.db"));
                job.Id = db.InsertJob(job);
                var engine = new SyncEngine(job, db);
                await engine.RunAsync("manual", execute: true);

                Check(File.GetLastWriteTimeUtc(Path.Combine(right, "m.txt")) == stamp,
                    "mtime 回归: 覆盖后目标 mtime=源 mtime（tmp+rename 路径）");
                var (_, plan2) = await engine.RunAsync("manual", execute: false);
                Check(plan2.Count == 0, "mtime 回归: mtime 一致 → 二次分析无差异");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>4) 严格镜像：两边都改过时默认保留较新一侧（反向覆盖），开启后一律源覆盖目标</summary>
        private static async Task TestStrictMirror()
        {
            var root = Path.Combine(Path.GetTempPath(), "fstestsm_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            try
            {
                File.WriteAllText(Path.Combine(left, "both.txt"), "LEFT-EDIT");
                Thread.Sleep(200);
                File.WriteAllText(Path.Combine(right, "both.txt"), "RIGHT-EDIT-NEWER"); // 右侧改得更晚

                // 默认（新者胜）：右侧较新 → 反向覆盖左
                var jobDefault = new SyncJob
                {
                    Name = "sm-default", LeftPath = left, RightPath = right,
                    Direction = SyncDirection.MirrorLeftToRight
                };
                var planDefault = RunEngineAnalysis(jobDefault, Path.Combine(root, "d.db"));
                Check(planDefault.Any(p => p.Action == SyncAction.UpdateLeft && p.RelativePath == "both.txt"),
                    "严格镜像: 默认关 = 右侧较新时保留右侧（新者胜）");

                // 严格镜像：一律源覆盖目标
                var jobStrict = new SyncJob
                {
                    Name = "sm-strict", LeftPath = left, RightPath = right,
                    Direction = SyncDirection.MirrorLeftToRight, StrictMirror = true
                };
                using (var db = new Db(Path.Combine(root, "s.db")))
                {
                    jobStrict.Id = db.InsertJob(jobStrict);
                    var engine = new SyncEngine(jobStrict, db);
                    var (rec, plan) = await engine.RunAsync("manual", execute: true);
                    Check(plan.Any(p => p.Action == SyncAction.UpdateRight && p.RelativePath == "both.txt"),
                        "严格镜像: 计划为源覆盖目标");
                    Check(rec.FailedFiles == 0 &&
                          File.ReadAllText(Path.Combine(right, "both.txt")) == "LEFT-EDIT",
                        "严格镜像: 执行后目标为源版本（忽略目标较新）");
                    var (_, plan2) = await engine.RunAsync("manual", execute: false);
                    Check(plan2.Count == 0, "严格镜像: 二次分析无差异");
                }
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        private static List<PlanEntry> RunEngineAnalysis(SyncJob job, string dbPath)
        {
            using var db = new Db(dbPath);
            job.Id = db.InsertJob(job);
            var engine = new SyncEngine(job, db);
            return engine.RunAsync("manual", execute: false).GetAwaiter().GetResult().plan;
        }

        /// <summary>5) mtime 容差：NTFS 两侧 50ms；MtimeToleranceMs 可探测</summary>
        private static void TestMtimeTolerance()
        {
            var tol = Differ.MtimeToleranceMs(Path.GetTempPath(), Path.GetTempPath());
            Check(tol == 50, $"mtime 容差: NTFS 路径返回 50ms（实际 {tol}）——FAT 分支需 FAT 卷实测，本机无则跳过");
        }

        /// <summary>6) 删除计数 + 回收站批量删除：3 个孤儿文件 MirrorDelete 删除，DeletedFiles 应如实记录</summary>
        private static async Task TestDeletedCountAndBatchRecycle()
        {
            var root = Path.Combine(Path.GetTempPath(), "fstestdc_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            try
            {
                foreach (var i in new[] { 1, 2, 3 })
                    File.WriteAllText(Path.Combine(right, $"orphan{i}.txt"), $"orphan {i}");

                var job = new SyncJob
                {
                    Name = "delcount", LeftPath = left, RightPath = right,
                    Direction = SyncDirection.MirrorLeftToRight, MirrorDelete = true,
                    DeleteToRecycleBin = true   // 走回收站攒批路径
                };
                using var db = new Db(Path.Combine(root, "test.db"));
                job.Id = db.InsertJob(job);
                var engine = new SyncEngine(job, db);

                var (rec, plan) = await engine.RunAsync("manual", execute: true);
                Check(plan.Count(p => p.Action == SyncAction.DeleteRight) == 3, "删除计数: 计划含 3 个删除");
                Check(rec.DeletedFiles == 3, $"删除计数: rec.DeletedFiles=3（实际 {rec.DeletedFiles}，修复前恒 0）");
                Check(rec.FailedFiles == 0, "删除计数: 批量回收站删除零失败");
                Check(!File.Exists(Path.Combine(right, "orphan1.txt")) &&
                      !File.Exists(Path.Combine(right, "orphan2.txt")) &&
                      !File.Exists(Path.Combine(right, "orphan3.txt")),
                    "删除计数: 3 个孤儿全部消失（回收站批量路径）");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>7) 预览不落库：logRun=false 时 runs 表不增加记录</summary>
        private static async Task TestPreviewNoRunRecord()
        {
            var root = Path.Combine(Path.GetTempPath(), "fstestpv_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            try
            {
                File.WriteAllText(Path.Combine(left, "a.txt"), "A");
                using var db = new Db(Path.Combine(root, "test.db"));
                var job = new SyncJob { Name = "pv", LeftPath = left, RightPath = right };
                job.Id = db.InsertJob(job);
                var engine = new SyncEngine(job, db);

                await engine.RunAsync("manual", execute: false);          // 手动分析：落库
                var afterManual = db.GetRecentRuns(job.Id, 1000).Count;
                await engine.RunAsync("preview", execute: false, logRun: false);  // UI 自动预览：不落库
                var afterPreview = db.GetRecentRuns(job.Id, 1000).Count;
                Check(afterManual == 1, "preview 不落库: 手动分析正常记录（1 条）");
                Check(afterPreview == afterManual, "preview 不落库: 自动预览不新增 runs 记录（修复前每次切换任务塞一条）");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>1) 单向类型冲突：左边是文件、右边是同名目录 → 删除+重建对，先删后建，一轮收敛</summary>
        private static async Task TestTypeConflictOneWay()
        {
            var root = Path.Combine(Path.GetTempPath(), "fstestfx_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(Path.Combine(right, "tc"));
            Directory.CreateDirectory(left);
            try
            {
                File.WriteAllText(Path.Combine(left, "tc"), "TC-FILE");
                File.WriteAllText(Path.Combine(right, "tc", "inner.txt"), "inside dir");

                var job = new SyncJob
                {
                    Name = "typeconflict", LeftPath = left, RightPath = right,
                    Direction = SyncDirection.MirrorLeftToRight, MirrorDelete = false
                };
                using var db = new Db(Path.Combine(root, "test.db"));
                job.Id = db.InsertJob(job);
                var engine = new SyncEngine(job, db);

                var (rec, plan) = await engine.RunAsync("manual", execute: true);
                Console.WriteLine($"类型冲突计划: {string.Join("; ", plan.Select(p => $"{p.Action} {p.RelativePath}"))}");
                Check(plan.Any(p => p.Action == SyncAction.DeleteRight && p.RelativePath == "tc")
                      && plan.Any(p => p.Action == SyncAction.CreateRight && p.RelativePath == "tc"),
                    "类型冲突: 计划含同路径删除+重建对");
                Check(rec.FailedFiles == 0, "类型冲突: 先删后建，执行零失败");
                Check(File.Exists(Path.Combine(right, "tc")) &&
                      !Directory.Exists(Path.Combine(right, "tc")) &&
                      File.ReadAllText(Path.Combine(right, "tc")) == "TC-FILE",
                    "类型冲突: 右侧同名目录已被替换为文件");
                var (_, plan2) = await engine.RunAsync("manual", execute: false);
                Check(plan2.Count == 0, "类型冲突: 二次分析无差异（一轮收敛）");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>2) Db 并发压测：单实例多线程混合 InsertRun/FinishRun/SaveSnapshot/查询</summary>
        private static void TestDbConcurrentHammer()
        {
            var root = Path.Combine(Path.GetTempPath(), "fstestdb_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(root);
            try
            {
                using var db = new Db(Path.Combine(root, "test.db"));
                var job = new SyncJob { Name = "hammer", LeftPath = root, RightPath = root + "_r" };
                job.Id = db.InsertJob(job);
                db.InsertJob(new SyncJob { Name = "hammer2", LeftPath = root, RightPath = root + "_r2" });

                var snapshotEntries = Enumerable.Range(0, 300).Select(i => new FileEntry
                {
                    RelativePath = $"dir{i % 5}/f{i}.txt", Size = i, IsDirectory = false,
                    MtimeUtc = DateTime.UtcNow
                }).ToList();

                Exception? firstErr = null;
                var tasks = new List<Task>();
                for (int t = 0; t < 4; t++)
                {
                    var tid = t;
                    tasks.Add(Task.Run(() =>
                    {
                        try
                        {
                            for (int i = 0; i < 50; i++)
                            {
                                var r = new RunRecord { JobId = job.Id, StartedAt = DateTime.Now, Trigger = "hammer" };
                                r.Id = db.InsertRun(r);
                                r.Status = "ok"; r.FinishedAt = DateTime.Now;
                                db.FinishRun(r);
                            }
                        }
                        catch (Exception ex) { Interlocked.Exchange(ref firstErr, ex); }
                    }));
                }
                // 快照写事务与普通写并发（修复前这里最容易撞事务）
                tasks.Add(Task.Run(() =>
                {
                    try { for (int i = 0; i < 5; i++) db.SaveSnapshot(job.Id, snapshotEntries); }
                    catch (Exception ex) { Interlocked.Exchange(ref firstErr, ex); }
                }));
                tasks.Add(Task.Run(() =>
                {
                    try { for (int i = 0; i < 50; i++) { db.GetJobs(); db.GetSnapshot(job.Id); } }
                    catch (Exception ex) { Interlocked.Exchange(ref firstErr, ex); }
                }));
                Task.WaitAll(tasks.ToArray());

                Check(firstErr == null, $"并发写库: 250+ 次混合读写无异常" +
                      (firstErr == null ? "" : $"（{firstErr.GetType().Name}: {firstErr.Message}）"));
                Check(db.GetRecentRuns(job.Id, 1000).Count == 200, "并发写库: 200 条记录完整落库");
                Check(db.GetSnapshot(job.Id).Count == 300, "并发写库: 快照原子替换（300 项）");
                db.CleanupOldRuns(keepPerJob: 100);
                Check(db.GetRecentRuns(job.Id, 1000).Count == 100, "并发写库: 历史裁剪保留最近 100 条");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>3) DirWatcher：正常文件变更触发、排除项不触发（FSW Filters 反转修复）</summary>
        private static void TestDirWatcherExcludeFilter()
        {
            var root = Path.Combine(Path.GetTempPath(), "fstestdw_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(root, "tmpdir"));
            try
            {
                var fired = new ManualResetEvent(false);
                using (var w = new DirWatcher(root, "*.log;tmpdir",
                             onChange: () => fired.Set(),
                             onOverflow: () => { }))
                {
                    File.WriteAllText(Path.Combine(root, "excluded.log"), "should not fire");
                    var falseFire = fired.WaitOne(1500);
                    Check(!falseFire, "DirWatcher: 排除项(*.log)变更不触发");

                    File.WriteAllText(Path.Combine(root, "tmpdir", "inside.txt"), "should not fire");
                    var falseFire2 = fired.WaitOne(1500);
                    Check(!falseFire2, "DirWatcher: 排除目录内变更不触发");

                    File.WriteAllText(Path.Combine(root, "normal.txt"), "should fire");
                    var okFire = fired.WaitOne(5000);
                    Check(okFire, "DirWatcher: 正常文件变更触发（修复前被 Filters 反转滤掉）");
                }
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>2026-09-09 review：Executor.RootOf 必须带路径边界——字符串前缀兄弟目录
        /// （D:\Doc vs D:\Documents，UI 嵌套检查拦不住）不能误判侧，否则归档错库、还原错位置。</summary>
        private static void TestRootOfPathBoundary()
        {
            var m = typeof(Executor).GetMethod("RootOf",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (m == null) { Check(false, "RootOf: 找不到方法（签名变更？）"); return; }
            string RootOf(Executor e, string p) => (string)m.Invoke(e, new object[] { p })!;

            // 测试路径按平台构造（RootOf 边界匹配用平台分隔符——Windows 风格字面量在 Unix 永不命中，
            // 结构等价：doc 是 documents 的字符串前缀兄弟目录）
            var doc = OperatingSystem.IsWindows() ? @"D:\Doc" : "/vol/doc";
            var documents = doc + "uments";
            var sep = Path.DirectorySeparatorChar;

            // 前缀兄弟目录：短根是长根的字符串前缀
            var e1 = new Executor(new SyncJob { LeftPath = doc, RightPath = documents });
            Check(RootOf(e1, documents + sep + "a.txt") == documents, "RootOf: 前缀兄弟目录右根文件判右（修复前误判左）");
            Check(RootOf(e1, doc + sep + "a.txt") == doc, "RootOf: 左根文件判左");
            Check(RootOf(e1, doc) == doc, "RootOf: 根路径自身判该侧");

            // 左右互换（长根在左）
            var e2 = new Executor(new SyncJob { LeftPath = documents, RightPath = doc });
            Check(RootOf(e2, documents + sep + "a.txt") == documents, "RootOf: 长左根文件判左");
            Check(RootOf(e2, doc + sep + "a.txt") == doc, "RootOf: 短右根文件判右（修复前误判左）");

            // 尾分隔符形态（用户配置路径带尾杠）
            var e3 = new Executor(new SyncJob { LeftPath = doc + sep, RightPath = documents + sep });
            Check(RootOf(e3, documents + sep + "a.txt") == documents + sep, "RootOf: 尾杠形态判右");
            Check(RootOf(e3, doc + sep + "a.txt") == doc + sep, "RootOf: 尾杠形态判左");

            // 常规不相关兄弟目录（回归确认不受影响）
            var l = OperatingSystem.IsWindows() ? @"E:\L" : "/vol/l";
            var r = OperatingSystem.IsWindows() ? @"E:\R" : "/vol/r";
            var e4 = new Executor(new SyncJob { LeftPath = l, RightPath = r });
            Check(RootOf(e4, r + sep + "x") == r && RootOf(e4, l + sep + "x") == l,
                "RootOf: 常规兄弟目录不受影响");
        }
    }
}
