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
    /// C2 差异行逐行改向专项：任意差异行可跳过（既有机制回归）或反向执行（Update 交换方向 /
    /// Create 对侧无文件=删除本侧）。反向走既有 CopyOne+归档通道。
    /// </summary>
    internal static class OverrideTests
    {
        private static int _fail;

        private static void Check(bool cond, string name, string extra = "")
        {
            Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}" + (extra.Length > 0 ? $" ({extra})" : ""));
            if (!cond) _fail++;
        }

        private static string Sha(string p)
        {
            using var fs = File.OpenRead(p);
            return Convert.ToHexString(SHA256.HashData(fs));
        }

        private static (string root, string left, string right, Db db, SyncEngine engine, SyncJob job)
            Setup(string tag, SyncDirection dir, int keep = 0)
        {
            var root = Path.Combine(Path.GetTempPath(), "fsovr_" + tag + "_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            var job = new SyncJob
            {
                Name = "ovr-" + tag, LeftPath = left, RightPath = right,
                Direction = dir, VersionKeepCount = keep
            };
            var db = new Db(Path.Combine(root, "t.db"));
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
            var r = VersionStore.CentralRootOverride;
            return r == null || !Directory.Exists(r) ? 0
                : Directory.EnumerateFiles(r, "*", SearchOption.AllDirectories).Count();
        }

        public static async Task<int> RunAll()
        {
            await TestSkipKeepsFileIntact();     // 1 跳过：该项不动
            await TestReverseUpdateSwaps();      // 2 反向 Update：两侧内容对调等价
            await TestReverseCreateDeletes();    // 3 反向 Create：对侧无文件=删除本侧（版本保底）
            await TestReverseWithArchive();      // 4 反向+版本保留：被覆盖侧归档
            Console.WriteLine(_fail == 0 ? "override: ALL PASS" : $"override: {_fail} FAILED");
            return _fail;
        }

        private static async Task TestSkipKeepsFileIntact()
        {
            var (root, left, right, db, engine, job) = Setup("skip", SyncDirection.MirrorLeftToRight);
            try
            {
                File.WriteAllText(Path.Combine(left, "a.txt"), "新内容");
                File.WriteAllText(Path.Combine(right, "a.txt"), "旧内容");
                Thread.Sleep(20);
                File.SetLastWriteTimeUtc(Path.Combine(left, "a.txt"), DateTime.UtcNow.AddSeconds(3));   // 左新（超容差）
                var (rec, plan) = await engine.RunAsync("manual", execute: false);
                var pe = plan.First(p => p.RelativePath == "a.txt");
                pe.Action = SyncAction.None;   // 用户跳过（UI 同款：Action→None）

                var (rec2, _) = await engine.ExecutePlanAsync(plan);
                Check(rec2.Status == "ok" && rec2.CopiedFiles == 0, "skip: 跳过项未执行", $"{rec2.Status} copied={rec2.CopiedFiles}");
                Check(File.ReadAllText(Path.Combine(right, "a.txt")) == "旧内容", "skip: 目标保持原状");
            }
            finally { Cleanup(root, db); }
        }

        private static async Task TestReverseUpdateSwaps()
        {
            var (root, left, right, db, engine, job) = Setup("rev", SyncDirection.TwoWay);
            try
            {
                File.WriteAllText(Path.Combine(left, "a.txt"), "基线");
                File.WriteAllText(Path.Combine(right, "a.txt"), "基线");
                await engine.RunAsync("manual");   // 快照
                Thread.Sleep(30);
                File.WriteAllText(Path.Combine(left, "a.txt"), "左改动");
                File.SetLastWriteTimeUtc(Path.Combine(left, "a.txt"), DateTime.UtcNow.AddSeconds(5));
                File.WriteAllText(Path.Combine(right, "a.txt"), "右改动");
                var (_, plan) = await engine.RunAsync("manual", execute: false);
                var pe = plan.First(p => p.RelativePath == "a.txt");
                Check(pe.Action == SyncAction.UpdateRight, "rev: 原计划=左胜覆盖右（时间优先）", pe.Action.ToString());

                pe.UserOverride = UserOverrideKind.Reverse;   // 用户反向：以右侧为准
                var (rec2, _) = await engine.ExecutePlanAsync(plan);
                Check(rec2.Status == "ok", "rev: 反向执行 ok", rec2.Status);
                Check(File.ReadAllText(Path.Combine(left, "a.txt")) == "右改动", "rev: 左被右侧覆盖（sha 对调）");
                Check(File.ReadAllText(Path.Combine(right, "a.txt")) == "右改动", "rev: 右保持胜者内容");
                Check(pe.UserOverride == UserOverrideKind.Default && pe.Action == SyncAction.UpdateLeft,
                    "rev: 计划内重打为 UpdateLeft 且标记复位", pe.Action.ToString());
            }
            finally { Cleanup(root, db); }
        }

        private static async Task TestReverseCreateDeletes()
        {
            var (root, left, right, db, engine, job) = Setup("revcreate", SyncDirection.MirrorLeftToRight, keep: 3);
            try
            {
                File.WriteAllText(Path.Combine(left, "brand-new.txt"), "本侧新文件");
                var before = VersionFileCount();
                var (_, plan) = await engine.RunAsync("manual", execute: false);
                var pe = plan.First(p => p.RelativePath == "brand-new.txt");
                Check(pe.Action == SyncAction.CreateRight, "revcreate: 原计划=新建", pe.Action.ToString());

                pe.UserOverride = UserOverrideKind.Reverse;   // 反向：对侧（右）无此文件 → 以对侧为准删本侧
                var (rec2, _) = await engine.ExecutePlanAsync(plan);
                Check(rec2.Status == "ok", "revcreate: 反向执行 ok", rec2.Status);
                Check(!File.Exists(Path.Combine(left, "brand-new.txt")), "revcreate: 本侧新文件已删（以对侧为准）");
                Check(!File.Exists(Path.Combine(right, "brand-new.txt")), "revcreate: 对侧未新建");
                Check(VersionFileCount() > before, "revcreate: 删除前入版本库（可还原）");
            }
            finally { Cleanup(root, db); }
        }

        private static async Task TestReverseWithArchive()
        {
            var (root, left, right, db, engine, job) = Setup("revarch", SyncDirection.TwoWay, keep: 3);
            try
            {
                File.WriteAllText(Path.Combine(left, "doc.txt"), "v1");
                File.WriteAllText(Path.Combine(right, "doc.txt"), "v1");
                await engine.RunAsync("manual");
                Thread.Sleep(30);
                File.WriteAllText(Path.Combine(left, "doc.txt"), "左新版本");
                File.SetLastWriteTimeUtc(Path.Combine(left, "doc.txt"), DateTime.UtcNow.AddSeconds(5));
                File.WriteAllText(Path.Combine(right, "doc.txt"), "右旧但将被反向保留");

                var before = VersionFileCount();
                var (_, plan) = await engine.RunAsync("manual", execute: false);
                plan.First(p => p.RelativePath == "doc.txt").UserOverride = UserOverrideKind.Reverse;
                await engine.ExecutePlanAsync(plan);

                Check(File.ReadAllText(Path.Combine(left, "doc.txt")) == "右旧但将被反向保留",
                    "revarch: 反向覆盖落地");
                Check(VersionFileCount() > before, "revarch: 被覆盖的左侧旧内容入版本库（归档通道复用）");
            }
            finally { Cleanup(root, db); }
        }
    }
}
