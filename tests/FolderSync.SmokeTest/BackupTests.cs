using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Core;

namespace FolderSync.SmokeTest
{
    /// <summary>
    /// A1 纯备份方向专项：BackupLeftToRight / BackupRightToLeft 只产源→目标的新建/更新
    /// （源新才覆盖），永不反向写源、永不删源；目标孤儿清理仍由 MirrorDelete 管。
    /// 铁律用例：目标新不回写（源 sha 前后一致）/ 源新与目标缺正常复制 / MirrorDelete 开关 /
    /// 删除动作永不出现在源侧 / 类型冲突纯单向 / 同 mtime 内容不同不动 / StrictMirror 无效 / RTL 反向 / Dapper 往返。
    /// </summary>
    internal static class BackupTests
    {
        private static int _fail;

        private static void Check(bool cond, string name, string extra = "")
        {
            Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}" + (extra.Length > 0 ? $" ({extra})" : ""));
            if (!cond) _fail++;
        }

        private static string Sha(string path)
        {
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(fs));
        }

        private static (string root, string left, string right, SyncJob job, Db db, SyncEngine engine)
            Setup(string tag, SyncDirection dir, bool mirrorDelete = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "fsbackup_" + tag + "_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            var job = new SyncJob
            {
                Name = "backup-" + tag, LeftPath = left, RightPath = right,
                Direction = dir, MirrorDelete = mirrorDelete
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

        /// <summary>显式拉开 mtime（绕开 50ms 容差；File.Copy 会连 mtime 一起拷，必须显式设）</summary>
        private static void Touch(string path, DateTime utc) => File.SetLastWriteTimeUtc(path, utc);

        public static async Task<int> RunAll()
        {
            await TestTargetNewerNoWriteback();   // 1 目标较新：不回写源（sha 前后一致）+ 跳过行说明
            await TestSourceNewerCopies();        // 2 源新/目标缺：正常复制
            await TestMirrorDeleteToggle();       // 3 MirrorDelete 开清孤儿、关保留
            await TestNeverDeletesSource();       // 4 计划中删除动作永不出现在源侧
            await TestTypeConflict();             // 5 类型冲突按纯单向处理（删目标异类型+按源重建）
            await TestSameMtimeDiffContent();     // 6 同 mtime 内容不同：跳过不动（不产 Conflict）
            await TestStrictMirrorIgnored();      // 7 StrictMirror 在备份下无效（目标新仍不回写）
            await TestRightToLeft();              // 8 BackupRightToLeft 反向抽查
            TestDirectionRoundTrip();             // 9 direction=3/4 Dapper 往返
            Console.WriteLine(_fail == 0 ? "backup: ALL PASS" : $"backup: {_fail} FAILED");
            return _fail;
        }

        private static async Task TestTargetNewerNoWriteback()
        {
            var (root, left, right, job, db, engine) = Setup("tgtnew", SyncDirection.BackupLeftToRight);
            try
            {
                File.WriteAllText(Path.Combine(left, "doc.txt"), "源版本旧");
                File.WriteAllText(Path.Combine(right, "doc.txt"), "目标版本新（外部改过）");
                Touch(Path.Combine(left, "doc.txt"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                Touch(Path.Combine(right, "doc.txt"), new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
                var shaBefore = Sha(Path.Combine(left, "doc.txt"));

                var (rec, plan) = await engine.RunAsync("manual");
                var entry = plan.FirstOrDefault(p => p.RelativePath == "doc.txt");

                Check(rec.Status == "ok", "tgtnew: 轮次 ok", rec.Status);
                Check(entry != null && entry.Action == SyncAction.None, "tgtnew: 目标较新产跳过行（None）",
                    entry?.Action.ToString() ?? "(none)");
                Check(entry?.Note.Contains("目标较新") == true, "tgtnew: 说明列写明不回写", entry?.Note ?? "");
                Check(Sha(Path.Combine(left, "doc.txt")) == shaBefore, "tgtnew: 源文件字节未动（sha 一致）");
                Check(File.ReadAllText(Path.Combine(right, "doc.txt")) == "目标版本新（外部改过）",
                    "tgtnew: 目标内容保持较新版本");
            }
            finally { Cleanup(root, db); }
        }

        private static async Task TestSourceNewerCopies()
        {
            var (root, left, right, job, db, engine) = Setup("srcnew", SyncDirection.BackupLeftToRight);
            try
            {
                File.WriteAllText(Path.Combine(left, "newer.txt"), "源新 v2");
                File.WriteAllText(Path.Combine(right, "newer.txt"), "目标旧 v1");
                Touch(Path.Combine(right, "newer.txt"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                Touch(Path.Combine(left, "newer.txt"), new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
                File.WriteAllText(Path.Combine(left, "fresh.txt"), "目标没有的新文件");
                Directory.CreateDirectory(Path.Combine(left, "sub"));
                File.WriteAllText(Path.Combine(left, "sub", "deep.txt"), "深层新文件");

                var (rec, plan) = await engine.RunAsync("manual");

                Check(rec.Status == "ok", "srcnew: 轮次 ok", rec.Status);
                Check(File.ReadAllText(Path.Combine(right, "newer.txt")) == "源新 v2", "srcnew: 源新覆盖目标");
                Check(File.ReadAllText(Path.Combine(right, "fresh.txt")) == "目标没有的新文件", "srcnew: 目标缺正常新建");
                Check(File.ReadAllText(Path.Combine(right, "sub", "deep.txt")) == "深层新文件", "srcnew: 深层路径新建");
                var (_, plan2) = await engine.RunAsync("manual", execute: false);
                Check(plan2.Count == 0, "srcnew: 第二轮无差异（收敛）", plan2.Count.ToString());
            }
            finally { Cleanup(root, db); }
        }

        private static async Task TestMirrorDeleteToggle()
        {
            // 关（默认）：目标孤儿保留
            var (root1, l1, r1, j1, db1, e1) = Setup("mdoff", SyncDirection.BackupLeftToRight, mirrorDelete: false);
            try
            {
                File.WriteAllText(Path.Combine(l1, "keep.txt"), "源有");
                File.WriteAllText(Path.Combine(r1, "orphan.txt"), "目标多余");
                var (_, plan1) = await e1.RunAsync("manual");
                Check(!plan1.Any(p => p.Action is SyncAction.DeleteLeft or SyncAction.DeleteRight),
                    "mdoff: 关=目标孤儿保留（无删除动作）");
                Check(File.Exists(Path.Combine(r1, "orphan.txt")), "mdoff: 孤儿文件还在");
            }
            finally { Cleanup(root1, db1); }

            // 开：目标孤儿清理（删的是目标侧，不是源）
            var (root2, l2, r2, j2, db2, e2) = Setup("mdon", SyncDirection.BackupLeftToRight, mirrorDelete: true);
            try
            {
                File.WriteAllText(Path.Combine(l2, "keep.txt"), "源有");
                File.WriteAllText(Path.Combine(r2, "orphan.txt"), "目标多余");
                var (rec2, plan2) = await e2.RunAsync("manual");
                var del = plan2.FirstOrDefault(p => p.RelativePath == "orphan.txt");
                Check(del != null && del.Action == SyncAction.DeleteRight, "mdon: 开=目标孤儿产 DeleteRight",
                    del?.Action.ToString() ?? "(none)");
                Check(!File.Exists(Path.Combine(r2, "orphan.txt")), "mdon: 孤儿已清理");
                Check(File.Exists(Path.Combine(l2, "keep.txt")), "mdon: 源侧文件完好");
                Check(rec2.Status == "ok", "mdon: 轮次 ok", rec2.Status);
            }
            finally { Cleanup(root2, db2); }
        }

        private static async Task TestNeverDeletesSource()
        {
            // L2R 备份：计划里任何 DeleteLeft 都不该出现（哪怕目标新/类型冲突，删除只对目标侧）
            var (root, left, right, job, db, engine) = Setup("nosrcdel", SyncDirection.BackupLeftToRight, mirrorDelete: true);
            try
            {
                File.WriteAllText(Path.Combine(left, "a.txt"), "源");
                File.WriteAllText(Path.Combine(right, "a.txt"), "目标更新");
                Touch(Path.Combine(left, "a.txt"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                Touch(Path.Combine(right, "a.txt"), new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
                File.WriteAllText(Path.Combine(right, "orphan.txt"), "孤儿");
                var (_, plan) = await engine.RunAsync("manual");
                Check(plan.All(p => p.Action != SyncAction.DeleteLeft),
                    "nosrcdel: 全计划无 DeleteLeft（删除永不落在源侧）",
                    string.Join(",", plan.Where(p => p.Action is SyncAction.DeleteLeft or SyncAction.DeleteRight).Select(p => p.Action.ToString())));
            }
            finally { Cleanup(root, db); }
        }

        private static async Task TestTypeConflict()
        {
            var (root, left, right, job, db, engine) = Setup("typeconf", SyncDirection.BackupLeftToRight);
            try
            {
                File.WriteAllText(Path.Combine(left, "x"), "源是文件");
                Directory.CreateDirectory(Path.Combine(right, "x"));
                File.WriteAllText(Path.Combine(right, "x", "inner.txt"), "目标目录内容");

                var (rec, plan) = await engine.RunAsync("manual");

                Check(rec.Status == "ok", "typeconf: 轮次 ok", rec.Status);
                Check(File.Exists(Path.Combine(right, "x")) && !Directory.Exists(Path.Combine(right, "x")),
                    "typeconf: 目标侧异类型已按源重建为文件");
                Check(File.ReadAllText(Path.Combine(right, "x")) == "源是文件", "typeconf: 内容与源一致");
            }
            finally { Cleanup(root, db); }
        }

        private static async Task TestSameMtimeDiffContent()
        {
            var (root, left, right, job, db, engine) = Setup("samemtime", SyncDirection.BackupLeftToRight);
            try
            {
                File.WriteAllText(Path.Combine(left, "s.txt"), "内容 A");
                File.WriteAllText(Path.Combine(right, "s.txt"), "内容 B（位腐/手动改过）");
                var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                Touch(Path.Combine(left, "s.txt"), t);
                Touch(Path.Combine(right, "s.txt"), t);

                var (_, plan) = await engine.RunAsync("manual");
                var entry = plan.FirstOrDefault(p => p.RelativePath == "s.txt");

                Check(entry != null && entry.Action == SyncAction.None, "samemtime: 产跳过行而非 Conflict",
                    entry?.Action.ToString() ?? "(none)");
                Check(File.ReadAllText(Path.Combine(left, "s.txt")) == "内容 A", "samemtime: 源未动");
                Check(File.ReadAllText(Path.Combine(right, "s.txt")) == "内容 B（位腐/手动改过）", "samemtime: 目标未动");
            }
            finally { Cleanup(root, db); }
        }

        private static async Task TestStrictMirrorIgnored()
        {
            var (root, left, right, job, db, engine) = Setup("strict", SyncDirection.BackupLeftToRight);
            try
            {
                job.StrictMirror = true;   // 备份下应被忽略（本就单向纯语义）
                File.WriteAllText(Path.Combine(left, "m.txt"), "源旧");
                File.WriteAllText(Path.Combine(right, "m.txt"), "目标新");
                Touch(Path.Combine(left, "m.txt"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                Touch(Path.Combine(right, "m.txt"), new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

                await engine.RunAsync("manual");
                Check(File.ReadAllText(Path.Combine(left, "m.txt")) == "源旧", "strict: StrictMirror 不改变备份语义（仍不回写源）");
                Check(File.ReadAllText(Path.Combine(right, "m.txt")) == "目标新", "strict: 目标保持较新");
            }
            finally { Cleanup(root, db); }
        }

        private static async Task TestRightToLeft()
        {
            var (root, left, right, job, db, engine) = Setup("rtl", SyncDirection.BackupRightToLeft);
            try
            {
                File.WriteAllText(Path.Combine(right, "only-right.txt"), "右源新文件");
                File.WriteAllText(Path.Combine(left, "only-right.txt"), "左侧旧（不该被回写场景：左新则不回写）");
                Touch(Path.Combine(right, "only-right.txt"), new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
                Touch(Path.Combine(left, "only-right.txt"), new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
                var shaLeft = Sha(Path.Combine(left, "only-right.txt"));

                var (rec, plan) = await engine.RunAsync("manual");
                Check(rec.Status == "ok", "rtl: 轮次 ok", rec.Status);
                Check(Sha(Path.Combine(left, "only-right.txt")) == shaLeft, "rtl: 目标侧（左）较新不被回写");
                var fresh = Path.Combine(root, "L", "fromright.txt");
                File.WriteAllText(Path.Combine(right, "fromright.txt"), "右源新 → 左建");
                await engine.RunAsync("manual");
                Check(File.ReadAllText(fresh) == "右源新 → 左建", "rtl: 源新文件正常建到左侧");
            }
            finally { Cleanup(root, db); }
        }

        private static void TestDirectionRoundTrip()
        {
            var root = Path.Combine(Path.GetTempPath(), "fsbackup_db_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(root);
            var dbPath = Path.Combine(root, "t.db");
            try
            {
                long id3, id4;
                using (var db = new Db(dbPath))
                {
                    id3 = db.InsertJob(new SyncJob { Name = "b-l2r", LeftPath = Path.Combine(root, "L"), RightPath = Path.Combine(root, "R"), Direction = SyncDirection.BackupLeftToRight });
                    id4 = db.InsertJob(new SyncJob { Name = "b-r2l", LeftPath = Path.Combine(root, "L"), RightPath = Path.Combine(root, "R"), Direction = SyncDirection.BackupRightToLeft });
                }
                using (var db2 = new Db(dbPath))
                {
                    Check(db2.GetJob(id3)?.Direction == SyncDirection.BackupLeftToRight, "db: direction=3 往返");
                    Check(db2.GetJob(id4)?.Direction == SyncDirection.BackupRightToLeft, "db: direction=4 往返");
                }
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
