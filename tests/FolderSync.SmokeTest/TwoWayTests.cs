using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Core;

namespace FolderSync.SmokeTest
{
    /// <summary>双向同步测试组：三方对比、删除传播、冲突三策略、裁决执行、双删收敛。</summary>
    internal static class TwoWayTests
    {
        private static int _fail;

        private static void Check(bool cond, string name)
        {
            Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}");
            if (!cond) _fail++;
        }

        private static (string root, string left, string right) MakeDirs()
        {
            var root = Path.Combine(Path.GetTempPath(), "fstest2_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            return (root, left, right);
        }

        private static SyncJob NewJob(string left, string right, ConflictPolicy policy, bool mirrorDelete)
        {
            return new SyncJob
            {
                Name = "twoway",
                LeftPath = left,
                RightPath = right,
                Direction = SyncDirection.TwoWay,
                ConflictPolicy = policy,
                MirrorDelete = mirrorDelete
            };
        }

        public static async Task<int> RunAll()
        {
            await TestFirstRunMerge();
            await TestDeletePropagation();
            await TestDeletePropagationOff();
            await TestConflictNewestMtime();
            await TestConflictLargestSize();
            await TestConflictManualResolve();
            await TestBothDeletedConverge();
            await TestModifiedBeatsDeleted();

            Console.WriteLine(_fail == 0 ? "\n=== 双向全部通过 ===" : $"\n=== {_fail} 项失败 ===");
            return _fail;
        }

        /// <summary>1) 首跑（快照为空）：双侧差异互相补齐，绝无删除</summary>
        private static async Task TestFirstRunMerge()
        {
            var (root, left, right) = MakeDirs();
            try
            {
                File.WriteAllText(Path.Combine(left, "only-left.txt"), "L1");
                File.WriteAllText(Path.Combine(right, "only-right.txt"), "R1");
                File.WriteAllText(Path.Combine(right, "shared.txt"), "base");
                Thread.Sleep(150);
                File.WriteAllText(Path.Combine(left, "shared.txt"), "base-new"); // 左改，右旧

                using var db = new Db(Path.Combine(root, "test.db"));
                var job = NewJob(left, right, ConflictPolicy.NewestMtime, mirrorDelete: true);
                job.Id = db.InsertJob(job);
                var engine = new SyncEngine(job, db);

                var (rec, plan) = await engine.RunAsync("manual", execute: true);
                Console.WriteLine($"首跑计划: {string.Join("; ", plan.Select(p => $"{p.Action} {p.RelativePath}"))}");
                Check(File.Exists(Path.Combine(right, "only-left.txt")), "首跑: 左独有 → 右");
                Check(File.Exists(Path.Combine(left, "only-right.txt")), "首跑: 右独有 → 左");
                Check(File.ReadAllText(Path.Combine(left, "shared.txt")) == "base-new" &&
                      File.ReadAllText(Path.Combine(right, "shared.txt")) == "base-new", "首跑: 左新版本覆盖右");
                Check(rec.FailedFiles == 0 && rec.DeletedFiles == 0, "首跑: 无失败无删除（快照空不盲删）");
                Check(db.GetSnapshot(job.Id).Count == 3, "首跑: 快照已建基线（3 项）");

                var (_, plan2) = await engine.RunAsync("manual", execute: false);
                Check(plan2.Count == 0, "首跑: 二次分析无差异（收敛）");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>2) 删除传播开：左侧删除 → 快照确认 → 右侧跟随删除</summary>
        private static async Task TestDeletePropagation()
        {
            var (root, left, right) = MakeDirs();
            try
            {
                File.WriteAllText(Path.Combine(left, "a.txt"), "A");
                File.WriteAllText(Path.Combine(left, "del.txt"), "will delete");

                using var db = new Db(Path.Combine(root, "test.db"));
                var job = NewJob(left, right, ConflictPolicy.NewestMtime, mirrorDelete: true);
                job.Id = db.InsertJob(job);
                var engine = new SyncEngine(job, db);

                await engine.RunAsync("manual", execute: true);   // 建基线
                Check(File.Exists(Path.Combine(right, "del.txt")), "删除传播: 基线建立");

                File.Delete(Path.Combine(left, "del.txt"));
                var (_, plan) = await engine.RunAsync("manual", execute: true);
                Console.WriteLine($"删除传播计划: {string.Join("; ", plan.Select(p => $"{p.Action} {p.RelativePath}"))}");
                Check(plan.Any(p => p.Action == SyncAction.DeleteRight && p.RelativePath == "del.txt"),
                    "删除传播: 左删 → 计划删右");
                Check(!File.Exists(Path.Combine(right, "del.txt")), "删除传播: 右侧副本已删");
                Check(File.Exists(Path.Combine(right, "a.txt")), "删除传播: 未删文件不受影响");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>3) 删除传播关：一侧删除 → 另一侧保留，绝不自动删</summary>
        private static async Task TestDeletePropagationOff()
        {
            var (root, left, right) = MakeDirs();
            try
            {
                File.WriteAllText(Path.Combine(left, "a.txt"), "A");
                using var db = new Db(Path.Combine(root, "test.db"));
                var job = NewJob(left, right, ConflictPolicy.NewestMtime, mirrorDelete: false);
                job.Id = db.InsertJob(job);
                var engine = new SyncEngine(job, db);

                await engine.RunAsync("manual", execute: true);
                File.Delete(Path.Combine(left, "a.txt"));

                var (_, plan) = await engine.RunAsync("manual", execute: true);
                Console.WriteLine($"传播关计划: {string.Join("; ", plan.Select(p => $"{p.Action} {p.RelativePath} [{p.Note}]"))}");
                Check(plan.Any(p => p.Action == SyncAction.None && p.RelativePath == "a.txt"),
                    "传播关: 记为跳过（保留右侧副本）");
                Check(File.Exists(Path.Combine(right, "a.txt")), "传播关: 右侧副本保留");
                Check(File.Exists(Path.Combine(left, "a.txt")) == false, "传播关: 左侧保持已删状态");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>4) 冲突策略=最新修改日期：新的覆盖旧的</summary>
        private static async Task TestConflictNewestMtime()
        {
            var (root, left, right) = MakeDirs();
            try
            {
                File.WriteAllText(Path.Combine(left, "c.txt"), "base");
                using var db = new Db(Path.Combine(root, "test.db"));
                var job = NewJob(left, right, ConflictPolicy.NewestMtime, mirrorDelete: false);
                job.Id = db.InsertJob(job);
                var engine = new SyncEngine(job, db);
                await engine.RunAsync("manual", execute: true);

                // 双侧各自修改（内容/大小都不同 → 必检出冲突）；左后写 → 左更新
                File.WriteAllText(Path.Combine(right, "c.txt"), "right-older");
                Thread.Sleep(300);
                File.WriteAllText(Path.Combine(left, "c.txt"), "left-newer-content");

                var (_, plan) = await engine.RunAsync("manual", execute: true);
                Console.WriteLine($"时间优先计划: {string.Join("; ", plan.Select(p => $"{p.Action} {p.RelativePath} [{p.Note}]"))}");
                Check(plan.Any(p => p.Action == SyncAction.UpdateRight && p.RelativePath == "c.txt"),
                    "时间优先: 左新 → 覆盖右");
                Check(File.ReadAllText(Path.Combine(right, "c.txt")) == "left-newer-content",
                    "时间优先: 右侧内容为左版本");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>5) 冲突策略=文件大小：大的覆盖小的（右侧更晚写但更小，检验不按时间）</summary>
        private static async Task TestConflictLargestSize()
        {
            var (root, left, right) = MakeDirs();
            try
            {
                File.WriteAllText(Path.Combine(left, "s.txt"), "12345");
                using var db = new Db(Path.Combine(root, "test.db"));
                var job = NewJob(left, right, ConflictPolicy.LargestSize, mirrorDelete: false);
                job.Id = db.InsertJob(job);
                var engine = new SyncEngine(job, db);
                await engine.RunAsync("manual", execute: true);

                Thread.Sleep(300);
                File.WriteAllText(Path.Combine(left, "s.txt"), "tiny");            // 左后写但更小
                Thread.Sleep(300);
                File.WriteAllText(Path.Combine(right, "s.txt"), "much-bigger-size"); // 右大

                var (_, plan) = await engine.RunAsync("manual", execute: true);
                Console.WriteLine($"大小优先计划: {string.Join("; ", plan.Select(p => $"{p.Action} {p.RelativePath} [{p.Note}]"))}");
                Check(plan.Any(p => p.Action == SyncAction.UpdateLeft && p.RelativePath == "s.txt"),
                    "大小优先: 右大 → 覆盖左");
                Check(File.ReadAllText(Path.Combine(left, "s.txt")) == "much-bigger-size",
                    "大小优先: 左侧内容为右版本");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>6) 冲突策略=手动：挂起 → 人工裁决 → RunPlanAsync 执行</summary>
        private static async Task TestConflictManualResolve()
        {
            var (root, left, right) = MakeDirs();
            try
            {
                File.WriteAllText(Path.Combine(left, "m.txt"), "base");
                using var db = new Db(Path.Combine(root, "test.db"));
                var job = NewJob(left, right, ConflictPolicy.Manual, mirrorDelete: false);
                job.Id = db.InsertJob(job);
                var engine = new SyncEngine(job, db);
                await engine.RunAsync("manual", execute: true);

                File.WriteAllText(Path.Combine(left, "m.txt"), "left-side-edit");
                Thread.Sleep(300);
                File.WriteAllText(Path.Combine(right, "m.txt"), "right-side-edit");

                var (_, plan) = await engine.RunAsync("manual", execute: true);
                var conflict = plan.FirstOrDefault(p => p.Action == SyncAction.Conflict && p.RelativePath == "m.txt");
                Check(conflict != null, "手动: 冲突被标记挂起");
                Check(engine.LastConflictCount == 1, "手动: LastConflictCount=1");
                Check(File.ReadAllText(Path.Combine(left, "m.txt")) == "left-side-edit" &&
                      File.ReadAllText(Path.Combine(right, "m.txt")) == "right-side-edit",
                    "手动: 执行后两侧都未被自动覆盖");

                // 人工裁决：保留左侧
                conflict!.Action = SyncAction.UpdateRight;
                conflict.ManuallyResolved = true;
                var (rec2, _) = await engine.RunPlanAsync(new System.Collections.Generic.List<PlanEntry> { conflict });
                Check(rec2.Status == "ok" && rec2.CopiedFiles == 1, "手动: 裁决执行成功");
                Check(File.ReadAllText(Path.Combine(right, "m.txt")) == "left-side-edit", "手动: 右侧已按裁决更新");
                Check(engine.LastConflictCount == 0, "手动: 裁决后冲突清零");

                var (_, plan3) = await engine.RunAsync("manual", execute: false);
                Check(plan3.Count == 0, "手动: 收敛无差异");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>7) 双删收敛：两侧都删 → 无动作，快照清除</summary>
        private static async Task TestBothDeletedConverge()
        {
            var (root, left, right) = MakeDirs();
            try
            {
                File.WriteAllText(Path.Combine(left, "gone.txt"), "G");
                using var db = new Db(Path.Combine(root, "test.db"));
                var job = NewJob(left, right, ConflictPolicy.NewestMtime, mirrorDelete: true);
                job.Id = db.InsertJob(job);
                var engine = new SyncEngine(job, db);
                await engine.RunAsync("manual", execute: true);

                File.Delete(Path.Combine(left, "gone.txt"));
                File.Delete(Path.Combine(right, "gone.txt"));

                var (_, plan) = await engine.RunAsync("manual", execute: true);
                Check(plan.Count == 0, "双删: 无动作收敛");
                Check(!db.GetSnapshot(job.Id).ContainsKey("gone.txt"), "双删: 快照条目已清除");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>8) 删除 vs 修改：一侧删除、另一侧在基线后又修改 → 修改优先恢复，绝不删新内容</summary>
        private static async Task TestModifiedBeatsDeleted()
        {
            var (root, left, right) = MakeDirs();
            try
            {
                File.WriteAllText(Path.Combine(left, "x.txt"), "base");
                using var db = new Db(Path.Combine(root, "test.db"));
                var job = NewJob(left, right, ConflictPolicy.NewestMtime, mirrorDelete: true);
                job.Id = db.InsertJob(job);
                var engine = new SyncEngine(job, db);
                await engine.RunAsync("manual", execute: true);

                File.Delete(Path.Combine(left, "x.txt"));      // 左删
                Thread.Sleep(300);
                File.WriteAllText(Path.Combine(right, "x.txt"), "right-modified-after"); // 右改

                var (_, plan) = await engine.RunAsync("manual", execute: true);
                Console.WriteLine($"改胜删计划: {string.Join("; ", plan.Select(p => $"{p.Action} {p.RelativePath} [{p.Note}]"))}");
                Check(plan.Any(p => p.Action == SyncAction.CreateLeft && p.RelativePath == "x.txt"),
                    "改胜删: 右侧修改恢复到左侧，不跟随删除");
                Check(File.ReadAllText(Path.Combine(left, "x.txt")) == "right-modified-after",
                    "改胜删: 左侧拿回修改后内容");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
