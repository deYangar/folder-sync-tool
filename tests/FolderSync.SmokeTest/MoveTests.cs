using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FolderSync.Core;

namespace FolderSync.SmokeTest
{
    /// <summary>
    /// A3 移动/重命名检测专项：快照可证的（删除+新建）配对替换为目标侧内部 rename。
    /// 铁律用例：单向×MirrorDelete 开（rename 生效+磁盘 sha 等价+旧路径消失+runs.moved_files+版本库零增长）/
    /// 关（保持 copy+保留）/ 双向同上 / 歧义多候选回退 / 小于 1MiB 跳过 / 移动后又改内容不配对 /
    /// 快照与基线路径键更新断言 / move_detect=0 回退。
    /// </summary>
    internal static class MoveTests
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

        private static readonly byte[] BigBuf = MakeBig();
        private static byte[] MakeBig()
        {
            var b = new byte[1536 * 1024];   // 1.5MB ≥ MoveMinBytes(1MiB)
            new Random(7).NextBytes(b);
            return b;
        }

        private static (string root, string left, string right, SyncJob job, Db db, SyncEngine engine)
            Setup(string tag, SyncDirection dir, bool mirrorDelete, bool moveDetect = true)
        {
            var root = Path.Combine(Path.GetTempPath(), "fsmove_" + tag + "_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            var job = new SyncJob
            {
                Name = "move-" + tag, LeftPath = left, RightPath = right,
                Direction = dir, MirrorDelete = mirrorDelete, MoveDetect = moveDetect,
                VersionKeepCount = 3
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

        /// <summary>版本库中心根下的文件总数（零增长断言用）</summary>
        private static int VersionFileCount()
        {
            var root = VersionStore.CentralRootOverride;
            if (root == null || !Directory.Exists(root)) return 0;
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count();
        }

        public static async Task<int> RunAll()
        {
            await TestOneWayMirrorDeleteOn();     // 1 单向+MD开：rename 生效（核心场景）
            await TestOneWayMirrorDeleteOff();    // 2 单向+MD关：保持 copy+保留
            await TestTwoWay();                   // 3 双向：删除传播配对 rename
            await TestAmbiguousFallback();        // 4 多候选歧义回退原计划
            await TestSmallFileSkipped();         // 5 <1MiB 不配对
            await TestContentChangedNotPaired();  // 6 移动后内容又改过（mtime 漂移）不配对
            await TestSameMetaDiffContent();      // 6b C-1：同 size 同 mtime 但内容不同——头部佐证拦下不配对
            await TestBaselineKeyRenamed();       // 7 基线块表路径键更新（零重算）
            await TestMoveDetectOff();            // 8 move_detect=0 回退
            TestSnapshotDapper();                 // 9 runs.moved_files / jobs.move_detect 往返
            Console.WriteLine(_fail == 0 ? "move: ALL PASS" : $"move: {_fail} FAILED");
            return _fail;
        }

        private static async Task TestOneWayMirrorDeleteOn()
        {
            var (root, left, right, job, db, engine) = Setup("l2r", SyncDirection.MirrorLeftToRight, mirrorDelete: true);
            try
            {
                File.WriteAllBytes(Path.Combine(left, "big.bin"), BigBuf);
                var (r1, _) = await engine.RunAsync("manual");
                Check(r1.Status == "ok", "l2r: 首轮基线 ok", r1.Status);
                var shaBig = Sha(Path.Combine(right, "big.bin"));

                // 用户把文件挪进子目录（rename：size/mtime 原样继承）
                Directory.CreateDirectory(Path.Combine(left, "moved"));
                File.Move(Path.Combine(left, "big.bin"), Path.Combine(left, "moved", "big.bin"));

                var filesBefore = VersionFileCount();
                var (r2, plan2) = await engine.RunAsync("manual");
                var mv = plan2.FirstOrDefault(p => p.Action == SyncAction.Move);

                Check(mv != null, "l2r: 计划产 Move 动作", string.Join(",", plan2.Select(p => p.Action)));
                Check(mv?.FromPath == "big.bin" && mv?.RelativePath == "moved/big.bin",
                    "l2r: Move 带旧→新相对路径", $"{mv?.FromPath} → {mv?.RelativePath}");
                Check(r2.MovedFiles == 1, "l2r: runs.moved_files=1", r2.MovedFiles.ToString());
                Check(!File.Exists(Path.Combine(right, "big.bin")), "l2r: 目标旧路径已消失");
                Check(File.Exists(Path.Combine(right, "moved", "big.bin")), "l2r: 目标新路径就位");
                Check(Sha(Path.Combine(right, "moved", "big.bin")) == shaBig, "l2r: 磁盘 sha 等价");
                Check(VersionFileCount() == filesBefore, "l2r: 版本库零增长（移动不归档）",
                    $"{filesBefore} → {VersionFileCount()}");
                var (_, plan3) = await engine.RunAsync("manual", execute: false);
                Check(plan3.Count == 0, "l2r: 第三轮收敛无差异", plan3.Count.ToString());
                // 快照路径键：旧键无、新键有
                var snap = db.GetSnapshot(job.Id);
                Check(!snap.ContainsKey("big.bin") && snap.ContainsKey("moved/big.bin"), "l2r: 快照路径键已更新");
            }
            finally { Cleanup(root, db); }
        }

        private static async Task TestOneWayMirrorDeleteOff()
        {
            var (root, left, right, job, db, engine) = Setup("mdoff", SyncDirection.MirrorLeftToRight, mirrorDelete: false);
            try
            {
                File.WriteAllBytes(Path.Combine(left, "big.bin"), BigBuf);
                await engine.RunAsync("manual");
                Directory.CreateDirectory(Path.Combine(left, "moved"));
                File.Move(Path.Combine(left, "big.bin"), Path.Combine(left, "moved", "big.bin"));

                var (rec, plan) = await engine.RunAsync("manual");
                Check(plan.All(p => p.Action != SyncAction.Move), "mdoff: MD关无 Move（目标旧文件按语义保留）",
                    string.Join(",", plan.Select(p => p.Action)));
                Check(File.Exists(Path.Combine(right, "big.bin")), "mdoff: 目标旧路径保留");
                Check(File.Exists(Path.Combine(right, "moved", "big.bin")), "mdoff: 新路径正常复制");
                Check(rec.MovedFiles == 0, "mdoff: moved_files=0");
            }
            finally { Cleanup(root, db); }
        }

        private static async Task TestTwoWay()
        {
            var (root, left, right, job, db, engine) = Setup("tw", SyncDirection.TwoWay, mirrorDelete: true);
            try
            {
                File.WriteAllBytes(Path.Combine(left, "big.bin"), BigBuf);
                await engine.RunAsync("manual");   // 首轮：CreateRight + 快照
                var shaBig = Sha(Path.Combine(left, "big.bin"));
                Directory.CreateDirectory(Path.Combine(left, "moved"));
                File.Move(Path.Combine(left, "big.bin"), Path.Combine(left, "moved", "big.bin"));

                var (rec, plan) = await engine.RunAsync("manual");
                var mv = plan.FirstOrDefault(p => p.Action == SyncAction.Move);
                Check(mv != null, "tw: 双向删除传播配对出 Move", string.Join(",", plan.Select(p => p.Action)));
                Check(mv?.MoveOnRight == true, "tw: 物理侧=右（DeleteRight+CreateRight 配对）");
                Check(!File.Exists(Path.Combine(right, "big.bin")) && File.Exists(Path.Combine(right, "moved", "big.bin")),
                    "tw: 右侧 rename 完成");
                Check(Sha(Path.Combine(right, "moved", "big.bin")) == shaBig, "tw: sha 等价");
                Check(rec.MovedFiles == 1, "tw: moved_files=1", rec.MovedFiles.ToString());
            }
            finally { Cleanup(root, db); }
        }

        private static async Task TestAmbiguousFallback()
        {
            var (root, left, right, job, db, engine) = Setup("amb", SyncDirection.MirrorLeftToRight, mirrorDelete: true);
            try
            {
                File.WriteAllBytes(Path.Combine(left, "a1.bin"), BigBuf);
                File.WriteAllBytes(Path.Combine(left, "a2.bin"), BigBuf);
                var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(Path.Combine(left, "a1.bin"), t);
                File.SetLastWriteTimeUtc(Path.Combine(left, "a2.bin"), t);
                await engine.RunAsync("manual");   // 快照 {a1,a2}（同 size 同 mtime）

                // 删两个、建一个（同 size 同 mtime）→ 2 删 1 建歧义 → 整组放弃
                File.Delete(Path.Combine(left, "a1.bin"));
                File.Delete(Path.Combine(left, "a2.bin"));
                File.WriteAllBytes(Path.Combine(left, "b.bin"), BigBuf);
                File.SetLastWriteTimeUtc(Path.Combine(left, "b.bin"), t);

                var (rec, plan) = await engine.RunAsync("manual");
                Check(plan.All(p => p.Action != SyncAction.Move), "amb: 歧义组放弃配对回退原计划",
                    string.Join(",", plan.Select(p => p.Action)));
                Check(rec.MovedFiles == 0, "amb: moved_files=0");
                Check(File.Exists(Path.Combine(right, "b.bin")), "amb: 新文件正常复制");
                Check(!File.Exists(Path.Combine(right, "a1.bin")) && !File.Exists(Path.Combine(right, "a2.bin")),
                    "amb: 孤儿正常清理");
            }
            finally { Cleanup(root, db); }
        }

        private static async Task TestSmallFileSkipped()
        {
            var (root, left, right, job, db, engine) = Setup("small", SyncDirection.MirrorLeftToRight, mirrorDelete: true);
            try
            {
                var small = new byte[512 * 1024];   // 512KB < 1MiB
                new Random(3).NextBytes(small);
                File.WriteAllBytes(Path.Combine(left, "s.bin"), small);
                await engine.RunAsync("manual");
                File.Move(Path.Combine(left, "s.bin"), Path.Combine(left, "t.bin"));

                var (rec, plan) = await engine.RunAsync("manual");
                Check(plan.All(p => p.Action != SyncAction.Move), "small: <1MiB 不配对（重拷代价低）",
                    string.Join(",", plan.Select(p => p.Action)));
                Check(File.Exists(Path.Combine(right, "t.bin")) && !File.Exists(Path.Combine(right, "s.bin")),
                    "small: 按普通复制+删除完成");
            }
            finally { Cleanup(root, db); }
        }

        private static async Task TestContentChangedNotPaired()
        {
            var (root, left, right, job, db, engine) = Setup("changed", SyncDirection.MirrorLeftToRight, mirrorDelete: true);
            try
            {
                File.WriteAllBytes(Path.Combine(left, "c.bin"), BigBuf);
                await engine.RunAsync("manual");
                // 挪动后又改内容：mtime 拉开超容差（50ms）→ 不配对
                File.Move(Path.Combine(left, "c.bin"), Path.Combine(left, "d.bin"));
                File.WriteAllBytes(Path.Combine(left, "d.bin"), BigBuf);   // 内容改写 → mtime=now
                File.SetLastWriteTimeUtc(Path.Combine(left, "d.bin"),
                    DateTime.UtcNow.AddSeconds(5));   // 显式拉开（防同毫秒）

                var (rec, plan) = await engine.RunAsync("manual");
                Check(plan.All(p => p.Action != SyncAction.Move), "changed: mtime 超容差不配对",
                    string.Join(",", plan.Select(p => p.Action)));
                Check(File.ReadAllText(Path.Combine(right, "d.bin")).Length >= 0
                    && Sha(Path.Combine(right, "d.bin")) == Sha(Path.Combine(left, "d.bin")),
                    "changed: 走正常复制内容一致");
            }
            finally { Cleanup(root, db); }
        }

        /// <summary>6b) C-1 内容佐证：删 X + 建 Y 同 size 同 mtime 但头部内容不同（备份工具/git
        /// 迁移保 mtime 的真实形态）→ 绝不配对成 Move（rename 会把幸存文件改名为异内容文件，
        /// 此后 size/mtime 恒等 Changed 永假形成永久静默分歧），回退 copy+delete 原计划。</summary>
        private static async Task TestSameMetaDiffContent()
        {
            var (root, left, right, job, db, engine) = Setup("meta", SyncDirection.MirrorLeftToRight, mirrorDelete: true);
            try
            {
                File.WriteAllBytes(Path.Combine(left, "x.bin"), BigBuf);
                await engine.RunAsync("manual");
                var mtime = File.GetLastWriteTimeUtc(Path.Combine(left, "x.bin"));

                // 模拟「保 mtime 的异内容替换」：删 X、建同长度异内容 Y、mtime 显式设回
                File.Delete(Path.Combine(left, "x.bin"));
                var mutated = (byte[])BigBuf.Clone();
                mutated[0] ^= 0xFF;   // 头部首字节翻转：头部 64KB 佐证必不相等
                mutated[^1] ^= 0xFF;
                File.WriteAllBytes(Path.Combine(left, "y.bin"), mutated);
                File.SetLastWriteTimeUtc(Path.Combine(left, "y.bin"), mtime);

                var (_, plan) = await engine.RunAsync("manual");
                Console.WriteLine($"同元异容计划: {string.Join("; ", plan.Select(p => $"{p.Action} {p.RelativePath}"))}");
                Check(!plan.Any(p => p.Action == SyncAction.Move), "同元异容: 不配对成 Move（内容佐证拦下）",
                    string.Join(",", plan.Select(p => p.Action)));
                Check(plan.Any(p => p.Action == SyncAction.DeleteRight && p.RelativePath == "x.bin")
                      && plan.Any(p => p.Action == SyncAction.CreateRight && p.RelativePath == "y.bin"),
                    "同元异容: 回退 copy+delete 原计划");
                // 执行后 y.bin 是异内容真身，两侧一致
                Check(Sha(Path.Combine(right, "y.bin")) == Sha(Path.Combine(left, "y.bin")),
                    "同元异容: 执行后两侧内容一致");
                var (_, plan3) = await engine.RunAsync("manual", execute: false);
                Check(plan3.Count == 0, "同元异容: 第三轮收敛", plan3.Count.ToString());
            }
            finally { Cleanup(root, db); }
        }

        private static async Task TestBaselineKeyRenamed()
        {
            var (root, left, right, job, db, engine) = Setup("baseline", SyncDirection.MirrorLeftToRight, mirrorDelete: true);
            try
            {
                File.WriteAllBytes(Path.Combine(left, "big.bin"), BigBuf);
                await engine.RunAsync("manual");
                // 预置基线条目（模拟上轮块级增量留下的块表）
                var fakeChunks = new System.Collections.Generic.List<BaselineChunk>
                {
                    new() { Offset = 0, Len = 65536, Hash = "deadbeef01" },
                    new() { Offset = 65536, Len = 65536, Hash = "deadbeef02" }
                };
                var fakeMeta = new BaselineFile
                {
                    Size = BigBuf.Length,
                    MtimeUtc = File.GetLastWriteTimeUtc(Path.Combine(right, "big.bin")),
                    FileSha = "ab".PadLeft(64, '0'),
                    ChunkCount = fakeChunks.Count
                };
                BaselineStore.SaveFile(right, "big.bin", fakeMeta, fakeChunks);
                Check(BaselineStore.GetEntry(right, "big.bin") != null, "baseline: 预置条目在");

                File.Move(Path.Combine(left, "big.bin"), Path.Combine(left, "renamed.bin"));
                await engine.RunAsync("manual");

                Check(BaselineStore.GetEntry(right, "big.bin") == null, "baseline: 旧路径键已删");
                var after = BaselineStore.GetEntry(right, "renamed.bin");
                Check(after != null && after!.Value.meta.ChunkCount == fakeChunks.Count
                    && after.Value.chunks[0].Hash == "deadbeef01",
                    "baseline: 新路径键继承块表（零重算）",
                    after == null ? "(null)" : $"{after.Value.meta.ChunkCount} 块");
            }
            finally { Cleanup(root, db); }
        }

        private static async Task TestMoveDetectOff()
        {
            var (root, left, right, job, db, engine) = Setup("off", SyncDirection.MirrorLeftToRight, mirrorDelete: true, moveDetect: false);
            try
            {
                File.WriteAllBytes(Path.Combine(left, "big.bin"), BigBuf);
                await engine.RunAsync("manual");
                File.Move(Path.Combine(left, "big.bin"), Path.Combine(left, "moved.bin"));

                var (rec, plan) = await engine.RunAsync("manual");
                Check(plan.All(p => p.Action != SyncAction.Move), "off: move_detect=0 回退（重拷+删除）",
                    string.Join(",", plan.Select(p => p.Action)));
                Check(rec.MovedFiles == 0 && File.Exists(Path.Combine(right, "moved.bin")),
                    "off: 旧路径行为正常完成");
            }
            finally { Cleanup(root, db); }
        }

        private static void TestSnapshotDapper()
        {
            var root = Path.Combine(Path.GetTempPath(), "fsmove_db_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(root);
            var dbPath = Path.Combine(root, "t.db");
            try
            {
                long jobId, runId;
                using (var db = new Db(dbPath))
                {
                    var j = new SyncJob
                    {
                        Name = "mv", LeftPath = Path.Combine(root, "L"), RightPath = Path.Combine(root, "R"),
                        MoveDetect = false   // 非默认值验读回
                    };
                    jobId = db.InsertJob(j);
                    var r = new RunRecord { JobId = jobId, StartedAt = DateTime.Now, Trigger = "manual" };
                    runId = db.InsertRun(r);
                    r.Id = runId;
                    r.FinishedAt = DateTime.Now;
                    r.Status = "ok";
                    r.MovedFiles = 4;
                    db.FinishRun(r);
                }
                using (var db2 = new Db(dbPath))
                {
                    Check(db2.GetJob(jobId)?.MoveDetect == false, "db: jobs.move_detect 往返");
                    var r2 = db2.GetRecentRuns(jobId).First(x => x.Id == runId);
                    Check(r2.MovedFiles == 4, "db: runs.moved_files 往返", r2.MovedFiles.ToString());
                }
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
