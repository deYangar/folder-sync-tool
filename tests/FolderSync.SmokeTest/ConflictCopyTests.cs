using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FolderSync.Core;

namespace FolderSync.SmokeTest
{
    /// <summary>
    /// D1 冲突副本专项：ConflictCopy 策略=胜者覆盖 + 败者改名 .sync-conflict-时间戳 留原目录（Syncthing 式）。
    /// 组合矩阵：Manual 挂起 / 自动+keep0 直接丢败者 / 自动+keepN 版本归档 / ConflictCopy+keep0 副本 /
    /// ConflictCopy+keepN 副本优先不归档（三选一互斥）/ 副本下轮作为新建正常同步。
    /// </summary>
    internal static class ConflictCopyTests
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

        private static (string root, string left, string right, Db db, SyncEngine engine, SyncJob job)
            Setup(string tag, ConflictPolicy policy, int keepCount)
        {
            var root = Path.Combine(Path.GetTempPath(), "fscc_" + tag + "_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            var job = new SyncJob
            {
                Name = "cc-" + tag, LeftPath = left, RightPath = right,
                Direction = SyncDirection.TwoWay, ConflictPolicy = policy, VersionKeepCount = keepCount
            };
            var db = new Db(Path.Combine(root, "test.db"));
            job.Id = db.InsertJob(job);
            return (root, left, right, db, new SyncEngine(job, db), job);
        }

        private static void Cleanup(string root, Db db)
        {
            db.Dispose();
            try { Directory.Delete(root, true); } catch { }
        }

        private static int VersionFileCount()
        {
            var root = VersionStore.CentralRootOverride;
            if (root == null || !Directory.Exists(root)) return 0;
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count();
        }

        /// <summary>构造双侧真冲突：首轮同内容建快照，然后左改成 A、右改成 B（都改过）。</summary>
        private static async Task SeedConflict(string left, string right, SyncEngine engine,
            string leftContent, string rightContent)
        {
            File.WriteAllText(Path.Combine(left, "doc.txt"), "base");
            File.WriteAllText(Path.Combine(right, "doc.txt"), "base");
            await engine.RunAsync("manual");   // 首轮：无差异 + 快照
            Thread.Sleep(30);
            File.WriteAllText(Path.Combine(left, "doc.txt"), leftContent);
            File.SetLastWriteTimeUtc(Path.Combine(left, "doc.txt"), DateTime.UtcNow.AddSeconds(10));   // 左新=胜者
            File.WriteAllText(Path.Combine(right, "doc.txt"), rightContent);
            File.SetLastWriteTimeUtc(Path.Combine(right, "doc.txt"), DateTime.UtcNow);   // 右旧=败者
        }

        public static async Task<int> RunAll()
        {
            await TestConflictCopyBasic();       // 1 胜者到位+副本存在且字节等价败者
            await TestCopySyncsNextRound();      // 2 副本下轮作为新建正常同步
            await TestMatrixManualPending();     // 3 矩阵：Manual 挂起
            await TestMatrixAutoKeep0Drops();    // 4 矩阵：自动+keep0 直接覆盖（旧=丢败者）
            await TestMatrixAutoKeepNArchives(); // 5 矩阵：自动+keepN 败者入版本库
            await TestMatrixCopyKeepNNoArchive();// 6 矩阵：ConflictCopy+keepN 副本优先不归档（互斥）
            TestPolicyRoundTrip();               // 7 conflict_policy=3 往返
            Console.WriteLine(_fail == 0 ? "conflictcopy: ALL PASS" : $"conflictcopy: {_fail} FAILED");
            return _fail;
        }

        private static async Task TestConflictCopyBasic()
        {
            var (root, left, right, db, engine, job) = Setup("basic", ConflictPolicy.ConflictCopy, 0);
            try
            {
                await SeedConflict(left, right, engine, "左胜版本", "右败版本");
                var shaLoser = Sha(Path.Combine(right, "doc.txt"));

                var (rec, plan) = await engine.RunAsync("manual");
                Check(rec.Status == "ok", "basic: 轮次 ok", rec.Status);
                Check(File.ReadAllText(Path.Combine(left, "doc.txt")) == "左胜版本", "basic: 胜者侧不动");
                Check(File.ReadAllText(Path.Combine(right, "doc.txt")) == "左胜版本", "basic: 胜者覆盖到败者侧");
                var copy = Directory.GetFiles(right, "doc.sync-conflict-*").FirstOrDefault();
                Check(copy != null, "basic: 败者副本存在", string.Join("|", Directory.GetFiles(right).Select(Path.GetFileName)));
                Check(copy != null && Sha(copy) == shaLoser, "basic: 副本字节等价败者原内容");
                Check(copy != null && Path.GetFileName(copy).StartsWith("doc.sync-conflict-", StringComparison.Ordinal)
                    && Path.GetFileName(copy).EndsWith(".txt", StringComparison.Ordinal),
                    "basic: 副本命名 原名.sync-conflict-时间戳.扩展", copy == null ? "" : Path.GetFileName(copy));
            }
            finally { Cleanup(root, db); }
        }

        private static async Task TestCopySyncsNextRound()
        {
            var (root, left, right, db, engine, job) = Setup("nextround", ConflictPolicy.ConflictCopy, 0);
            try
            {
                await SeedConflict(left, right, engine, "V2L", "V2R");
                await engine.RunAsync("manual");   // 产生右侧副本

                // 副本=右侧新文件 → 下轮作为新建正常同步到左侧（Syncthing 语义）
                var (rec2, plan2) = await engine.RunAsync("manual");
                var copyName = Directory.GetFiles(right, "doc.sync-conflict-*").Select(Path.GetFileName).FirstOrDefault();
                Check(copyName != null && File.Exists(Path.Combine(left, copyName)),
                    "nextround: 副本下轮作为新建同步到对侧", copyName ?? "(none)");
                Check(rec2.Status == "ok", "nextround: 轮次 ok", rec2.Status);
            }
            finally { Cleanup(root, db); }
        }

        private static async Task TestMatrixManualPending()
        {
            var (root, left, right, db, engine, job) = Setup("manual", ConflictPolicy.Manual, 0);
            try
            {
                await SeedConflict(left, right, engine, "L", "R");
                var (rec, plan) = await engine.RunAsync("manual");
                var row = plan.FirstOrDefault(p => p.RelativePath == "doc.txt");
                Check(row?.Action == SyncAction.Conflict, "manual: 挂起等裁决", row?.Action.ToString() ?? "(none)");
                Check(File.ReadAllText(Path.Combine(left, "doc.txt")) == "L"
                    && File.ReadAllText(Path.Combine(right, "doc.txt")) == "R",
                    "manual: 两侧均未动");
            }
            finally { Cleanup(root, db); }
        }

        private static async Task TestMatrixAutoKeep0Drops()
        {
            var (root, left, right, db, engine, job) = Setup("keep0", ConflictPolicy.NewestMtime, 0);
            try
            {
                await SeedConflict(left, right, engine, "L", "R");
                await engine.RunAsync("manual");
                Check(File.ReadAllText(Path.Combine(right, "doc.txt")) == "L", "keep0: 胜者覆盖（旧行为）");
                Check(Directory.GetFiles(right, "doc.sync-conflict-*").Length == 0, "keep0: 无副本（keep=0 且非 ConflictCopy）");
                Check(VersionFileCount() == 0, "keep0: 无版本归档");
            }
            finally { Cleanup(root, db); }
        }

        private static async Task TestMatrixAutoKeepNArchives()
        {
            var (root, left, right, db, engine, job) = Setup("keepn", ConflictPolicy.NewestMtime, 3);
            try
            {
                await SeedConflict(left, right, engine, "L", "R");
                var before = VersionFileCount();
                await engine.RunAsync("manual");
                Check(File.ReadAllText(Path.Combine(right, "doc.txt")) == "L", "keepn: 胜者覆盖");
                Check(VersionFileCount() > before, "keepn: 败者入版本库", $"{before} → {VersionFileCount()}");
                Check(Directory.GetFiles(right, "doc.sync-conflict-*").Length == 0, "keepn: 无副本（策略非 ConflictCopy）");
            }
            finally { Cleanup(root, db); }
        }

        private static async Task TestMatrixCopyKeepNNoArchive()
        {
            var (root, left, right, db, engine, job) = Setup("mix", ConflictPolicy.ConflictCopy, 3);
            try
            {
                await SeedConflict(left, right, engine, "L", "R");
                var before = VersionFileCount();
                await engine.RunAsync("manual");
                Check(File.ReadAllText(Path.Combine(right, "doc.txt")) == "L", "mix: 胜者覆盖");
                Check(Directory.GetFiles(right, "doc.sync-conflict-*").Length == 1, "mix: 副本保留");
                Check(VersionFileCount() == before, "mix: ConflictCopy 下不再版本归档（三选一互斥）",
                    $"{before} → {VersionFileCount()}");
            }
            finally { Cleanup(root, db); }
        }

        private static void TestPolicyRoundTrip()
        {
            var root = Path.Combine(Path.GetTempPath(), "fscc_db_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(root);
            var dbPath = Path.Combine(root, "t.db");
            try
            {
                long id;
                using (var db = new Db(dbPath))
                    id = db.InsertJob(new SyncJob
                    {
                        Name = "cc", LeftPath = Path.Combine(root, "L"), RightPath = Path.Combine(root, "R"),
                        Direction = SyncDirection.TwoWay, ConflictPolicy = ConflictPolicy.ConflictCopy
                    });
                using (var db2 = new Db(dbPath))
                    Check(db2.GetJob(id)?.ConflictPolicy == ConflictPolicy.ConflictCopy, "db: conflict_policy=3 往返");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
