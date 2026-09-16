using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FolderSync.Core;

namespace FolderSync.SmokeTest
{
    /// <summary>
    /// B3 大小写冲突检测专项：跨侧仅大小写不同（左 README.md vs 右 readme.md）不再静默归并，
    /// 显式产冲突行标人工（双向挂起/单向按源覆盖=纠正大小写）。
    /// </summary>
    internal static class CaseConflictTests
    {
        private static int _fail;

        private static void Check(bool cond, string name, string extra = "")
        {
            Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}" + (extra.Length > 0 ? $" ({extra})" : ""));
            if (!cond) _fail++;
        }

        private static (string root, string left, string right, SyncJob job, Db db, SyncEngine engine)
            Setup(string tag, SyncDirection dir)
        {
            var root = Path.Combine(Path.GetTempPath(), "fscase_" + tag + "_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            var job = new SyncJob { Name = "case-" + tag, LeftPath = left, RightPath = right, Direction = dir };
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
            await TestOneWayCaseConflictDetected();   // 1 单向：内容一致+大小写不同 → 冲突行（旧=静默无动作）
            await TestOneWayExecuteFixesCase();       // 2 单向执行：目标大小写被纠正成源写法
            await TestTwoWayCaseConflictPending();    // 3 双向：冲突行挂起，不自动动文件
            await TestNormalCaseNoFalsePositive();    // 4 大小写一致：无误报
            await TestCaseRenameWithMoveDetect();     // 5 大小写仅变的 rename：不产生 Move，产冲突行（A3 联动）
            Console.WriteLine(_fail == 0 ? "caseconflict: ALL PASS" : $"caseconflict: {_fail} FAILED");
            return _fail;
        }

        private static async Task TestOneWayCaseConflictDetected()
        {
            var (root, left, right, job, db, engine) = Setup("l2r", SyncDirection.MirrorLeftToRight);
            try
            {
                File.WriteAllText(Path.Combine(left, "README.md"), "same");
                File.WriteAllText(Path.Combine(right, "readme.md"), "same");
                var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(Path.Combine(left, "README.md"), t);
                File.SetLastWriteTimeUtc(Path.Combine(right, "readme.md"), t);

                var (_, plan) = await engine.RunAsync("manual", execute: false);
                var row = plan.FirstOrDefault(p => p.Action == SyncAction.Conflict);
                Check(row != null, "l2r: 大小写不一致产出冲突行（旧=内容一致静默无动作）",
                    string.Join(",", plan.Select(p => p.Action)));
                Check(row?.Note.Contains("仅大小写不同") == true && row?.Note.Contains("README.md") == true,
                    "l2r: 说明列写明两侧实际写法", row?.Note ?? "");
            }
            finally { Cleanup(root, db); }
        }

        private static async Task TestOneWayExecuteFixesCase()
        {
            var (root, left, right, job, db, engine) = Setup("fix", SyncDirection.MirrorLeftToRight);
            try
            {
                File.WriteAllText(Path.Combine(left, "README.md"), "content v2");
                File.WriteAllText(Path.Combine(right, "readme.md"), "content v1 old");
                Thread.Sleep(20);

                var (rec, plan) = await engine.RunAsync("manual");
                Check(rec.Status == "ok", "fix: 执行 ok", rec.Status);
                var names = Directory.GetFiles(right).Select(Path.GetFileName).ToList();
                Check(names.Contains("README.md") && names.All(n => !string.Equals(n, "readme.md", StringComparison.Ordinal)),
                    "fix: 目标侧大小写被纠正为源写法", string.Join("|", names));
                Check(File.ReadAllText(Path.Combine(right, "README.md")) == "content v2", "fix: 内容同源");
                var (_, plan2) = await engine.RunAsync("manual", execute: false);
                Check(plan2.Count == 0, "fix: 第二轮无差异（大小写已统一）", plan2.Count.ToString());
            }
            finally { Cleanup(root, db); }
        }

        private static async Task TestTwoWayCaseConflictPending()
        {
            var (root, left, right, job, db, engine) = Setup("tw", SyncDirection.TwoWay);
            try
            {
                File.WriteAllText(Path.Combine(left, "Readme.MD"), "x");
                File.WriteAllText(Path.Combine(right, "README.md"), "x");
                var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(Path.Combine(left, "Readme.MD"), t);
                File.SetLastWriteTimeUtc(Path.Combine(right, "README.md"), t);

                var (rec, plan) = await engine.RunAsync("manual");
                var row = plan.FirstOrDefault(p => p.Action == SyncAction.Conflict);
                Check(row != null && row.Note.Contains("仅大小写不同"), "tw: 冲突行挂起", row?.Note ?? "(none)");
                Check(engine.LastConflictCount == 1, "tw: 冲突计数 1", engine.LastConflictCount.ToString());
                Check(Directory.GetFiles(left).Length == 1 && Directory.GetFiles(right).Length == 1,
                    "tw: 双向不自动动文件（等人工裁决）");
                Check(File.Exists(Path.Combine(left, "Readme.MD")) && File.Exists(Path.Combine(right, "README.md")),
                    "tw: 两侧原大小写均未被动");
            }
            finally { Cleanup(root, db); }
        }

        private static async Task TestNormalCaseNoFalsePositive()
        {
            var (root, left, right, job, db, engine) = Setup("norm", SyncDirection.TwoWay);
            try
            {
                File.WriteAllText(Path.Combine(left, "a.txt"), "1");
                Directory.CreateDirectory(Path.Combine(left, "Sub"));
                File.WriteAllText(Path.Combine(left, "Sub", "b.txt"), "2");
                await engine.RunAsync("manual");   // 首轮建到右侧
                var (_, plan2) = await engine.RunAsync("manual", execute: false);
                Check(plan2.Count(p => p.Action == SyncAction.Conflict) == 0,
                    "norm: 大小写完全一致无误报", plan2.Count.ToString());
                // 右侧人工改大小写一致的文件内容 → 正常 Update 不被误标冲突
                File.WriteAllText(Path.Combine(right, "a.txt"), "changed");
                var (_, plan3) = await engine.RunAsync("manual", execute: false);
                Check(plan3.Any(p => p.RelativePath == "a.txt" && p.Action != SyncAction.Conflict),
                    "norm: 内容差异正常判 Update（非大小写冲突）",
                    string.Join(",", plan3.Select(p => $"{p.Action}:{p.RelativePath}")));
            }
            finally { Cleanup(root, db); }
        }

        private static async Task TestCaseRenameWithMoveDetect()
        {
            var (root, left, right, job, db, engine) = Setup("rename", SyncDirection.MirrorLeftToRight);
            try
            {
                var big = new byte[1536 * 1024];
                new Random(11).NextBytes(big);
                File.WriteAllBytes(Path.Combine(left, "File.bin"), big);
                await engine.RunAsync("manual");   // 右侧 File.bin

                // 用户把源改成大写（NTFS 大小写仅变的 rename）
                File.Move(Path.Combine(left, "File.bin"), Path.Combine(left, "FILE.tmp"));
                Thread.Sleep(20);
                File.Move(Path.Combine(left, "FILE.tmp"), Path.Combine(left, "FILE.bin"));
                Thread.Sleep(20);

                var (_, plan) = await engine.RunAsync("manual", execute: false);
                Check(plan.All(p => p.Action != SyncAction.Move), "rename: 大小写仅变不产生 Move（A3 联动）",
                    string.Join(",", plan.Select(p => p.Action)));
                Check(plan.Any(p => p.Action == SyncAction.Conflict && p.Note.Contains("仅大小写不同")),
                    "rename: 产出大小写冲突行");
            }
            finally { Cleanup(root, db); }
        }
    }
}
