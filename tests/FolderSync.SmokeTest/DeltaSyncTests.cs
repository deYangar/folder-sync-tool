using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FolderSync.Core;
using Microsoft.Data.Sqlite;

namespace FolderSync.SmokeTest
{
    /// <summary>
    /// v1.4 块级增量同步专项：CDC 基线缓存 + 缺块传输 + 双闸校验降级。
    /// 铁律用例：正常增量收益、无基线现场建表、基线投毒终检兜底、目标篡改/漂移、
    /// 全零多实例块、取消清理、阈值边界、库损坏自愈、共享侧根。
    /// </summary>
    internal static class DeltaSyncTests
    {
        private static int _fail;

        private static void Check(bool cond, string name)
        {
            Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}");
            if (!cond) _fail++;
        }

        public static async Task<int> RunAll()
        {
            await TestBasicGain();              // 1 基础收益
            await TestNoBaselineRebuild();      // 2 无基线首改（现场建表）
            await TestPoisonedBaseline();       // 3 基线投毒（终检拦截）
            await TestTargetTampered();         // 4 目标内容被外部篡改（mtime 不变，终检兜底）
            await TestTargetMtimeDrift();       // 5 目标 mtime 被外部改动（预检闸拦截）
            await TestAllZeroMultiInstance();   // 6 全零文件（同 hash 多实例块）
            await TestCancelMidTransfer();      // 7 取消：tmp 清理 + 下轮正常
            await TestThresholdBoundary();      // 8 阈值：3.9MB 整文件 / 4.1MB 块级
            await TestCorruptBaselineDb();      // 10 库损坏自动降级
            await TestSharedSideRoot();         // 11 共享侧根两任务先后同步
            await TestBaselineGc();             // 9 基线 GC：删除清理 + 轮末对账
            await TestRunRecordPersist();       // 12 引擎路径：runs.delta_saved_bytes 落库
            await TestClearAllSites();          // 13 设置页全局清理（含孤儿目录）
            await TestE1BaselineAfterCopy();    // 14 E1：整文件落地即建基线，下轮 Update 直接增量
            await TestE1ArchiveFailNoBaseline();// 15 E1：归档失败不建基线

            Console.WriteLine(_fail == 0 ? "\n=== 增量同步全部通过 ===" : $"\n=== {_fail} 项失败 ===");
            return _fail;
        }

        // ---------- 工具 ----------

        private sealed class InlineProgress : IProgress<ProgressInfo>
        {
            private readonly Action<ProgressInfo>? _onReport;
            public InlineProgress(Action<ProgressInfo>? onReport = null) => _onReport = onReport;
            public void Report(ProgressInfo value) => _onReport?.Invoke(value);
        }

        private static string Sha256File(string path)
        {
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
        }

        /// <summary>造随机内容文件（确定性种子，重跑同内容）。</summary>
        private static void WriteRandom(string path, long totalBytes, int seed)
        {
            var rng = new Random(seed);
            var buf = new byte[1 << 20];
            using var fs = new FileStream(path, FileMode.Create);
            long written = 0;
            while (written < totalBytes)
            {
                var n = (int)Math.Min(buf.Length, totalBytes - written);
                rng.NextBytes(buf.AsSpan(0, n));
                fs.Write(buf, 0, n);
                written += n;
            }
        }

        /// <summary>就地改文件中段指定字节数（mtime 随写入更新 → Differ 判 Update）。</summary>
        private static void ModifyMiddle(string path, int bytes = 1024, long? at = null, int seed = 99)
        {
            var rng = new Random(seed);
            var data = new byte[bytes];
            rng.NextBytes(data);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Write);
            var pos = at ?? fs.Length / 2;
            fs.Seek(pos, SeekOrigin.Begin);
            fs.Write(data, 0, bytes);
        }

        private static (SyncJob job, Executor exec, List<PlanEntry> plan) MkUpdate(
            string left, string right, string rel)
        {
            var job = new SyncJob
            {
                Name = "delta", LeftPath = left, RightPath = right,
                Direction = SyncDirection.MirrorLeftToRight, DeltaSync = true
            };
            var exec = new Executor(job);
            var src = Path.Combine(left, rel.Replace('/', Path.DirectorySeparatorChar));
            var plan = new List<PlanEntry>
            {
                new PlanEntry { Action = SyncAction.UpdateRight, RelativePath = rel, IsDirectory = false, Size = new FileInfo(src).Length }
            };
            return (job, exec, plan);
        }

        // ---------- 用例 ----------

        /// <summary>1 基础收益：100MB 改 1KB → 第二轮实际写入 &lt; 5MB，产物 sha 与源一致，基线有块表。</summary>
        private static async Task TestBasicGain()
        {
            var root = Path.Combine(Path.GetTempPath(), "fsdelta_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            try
            {
                var rel = "big.bin";
                var src = Path.Combine(left, rel);
                var dst = Path.Combine(right, rel);
                WriteRandom(src, 100L * 1024 * 1024, seed: 42);

                // 第一轮：目标不存在 → Create 整文件（不走块级，不写基线）
                var job = new SyncJob { Name = "delta", LeftPath = left, RightPath = right,
                    Direction = SyncDirection.MirrorLeftToRight, DeltaSync = true };
                var exec = new Executor(job);
                var plan = new List<PlanEntry> { new PlanEntry
                {
                    Action = SyncAction.CreateRight, RelativePath = rel, IsDirectory = false, Size = new FileInfo(src).Length
                } };
                var (ok, _, failed, _) = await exec.ExecuteAsync(plan, null, CancellationToken.None);
                Check(ok == 1 && failed == 0, "①-1 首轮 Create 成功");
                Check(BaselineStore.GetEntry(right, rel) == null, "①-2 Create 不写基线");
                var shaAfterCreate = Sha256File(dst);
                Check(shaAfterCreate == Sha256File(src), "①-3 首轮产物 sha 正确");

                // 第二轮：改 1KB → Update 块级增量
                ModifyMiddle(src);
                var (job2, exec2, plan2) = MkUpdate(left, right, rel);
                var (ok2, _, failed2, _) = await exec2.ExecuteAsync(plan2, null, CancellationToken.None);
                Check(ok2 == 1 && failed2 == 0, "①-4 增量轮成功");
                Check(Sha256File(dst) == Sha256File(src), "①-5 增量产物 sha 与源逐字节一致");
                var written = 100L * 1024 * 1024 - exec2.DeltaSavedBytes;
                Check(exec2.DeltaSavedBytes > 95L * 1024 * 1024, $"①-6 实际写入 < 5MB（实测 {written / 1024.0 / 1024:F2} MB，省 {exec2.DeltaSavedBytes / 1024.0 / 1024:F1} MB）");
                var meta = BaselineStore.GetEntry(right, rel)?.meta;
                Check(meta != null && meta.Size == new FileInfo(src).Length && meta.ChunkCount > 0, "①-7 基线库有块表");
                Check(meta!.MtimeUtc == File.GetLastWriteTimeUtc(src), "①-8 基线记录源 mtime");

                // 第三轮：再改一次（这次有基线缓存，验证缓存路径）
                ModifyMiddle(src, at: 1024 * 1024 * 20, seed: 7);
                var (_, exec3, plan3) = MkUpdate(left, right, rel);
                var (ok3, _, failed3, _) = await exec3.ExecuteAsync(plan3, null, CancellationToken.None);
                Check(ok3 == 1 && failed3 == 0 && Sha256File(dst) == Sha256File(src), "①-9 有基线缓存路径的增量同样正确");
                Check(exec3.DeltaSavedBytes > 95L * 1024 * 1024, "①-10 缓存路径收益不劣化");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>2 无基线首改：删基线库 → 改 1KB → 现场建表路径成功 + sha 正确。</summary>
        private static async Task TestNoBaselineRebuild()
        {
            var root = Path.Combine(Path.GetTempPath(), "fsdelta_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            try
            {
                var rel = "f.bin";
                var src = Path.Combine(left, rel);
                var dst = Path.Combine(right, rel);
                WriteRandom(src, 8L * 1024 * 1024, seed: 1);
                File.Copy(src, dst);   // 目标=旧版本
                Thread.Sleep(20);
                ModifyMiddle(src);   // 源改 → Update

                var (_, exec, plan) = MkUpdate(left, right, rel);
                var (ok, _, failed, _) = await exec.ExecuteAsync(plan, null, CancellationToken.None);
                Check(ok == 1 && failed == 0, "②-2 无基线同步成功");
                Check(Sha256File(dst) == Sha256File(src), "②-3 现场建表路径产物正确");
                Check(exec.DeltaSavedBytes > 6L * 1024 * 1024, $"②-4 现场建表也有增量收益（省 {exec.DeltaSavedBytes / 1024.0 / 1024:F2} MB）");
                Check(BaselineStore.GetEntry(right, rel) != null, "②-5 增量成功后基线已建");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>3 基线投毒：交换基线块表两个块的 offset → 组装拷错内容 → 终检拦截 → 整文件兜底。</summary>
        private static async Task TestPoisonedBaseline()
        {
            var root = Path.Combine(Path.GetTempPath(), "fsdelta_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            try
            {
                var rel = "f.bin";
                var src = Path.Combine(left, rel);
                var dst = Path.Combine(right, rel);
                WriteRandom(src, 8L * 1024 * 1024, seed: 2);
                File.Copy(src, dst);
                Thread.Sleep(20);
                ModifyMiddle(src);

                // 先正常跑一轮建基线？不行——那轮就把改动同步掉了。改法：先跑增量建基线，再制造新改动+投毒
                var (_, exec0, plan0) = MkUpdate(left, right, rel);
                await exec0.ExecuteAsync(plan0, null, CancellationToken.None);
                ModifyMiddle(src, seed: 5);

                // 投毒：交换 seq=1/2 两块的 offset（hash 集合不变 → 预检/命中照常，但拷贝位置错 → 终检炸）
                var dbPath = Path.Combine(BaselineStore.RootOf(right), "baseline.db");
                using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWrite"))
                {
                    conn.Open();
                    conn.Execute(@"
CREATE TEMP TABLE swap AS SELECT seq, offset FROM chunks WHERE rel_path=@r AND seq IN (1,2);
UPDATE chunks SET offset=(SELECT offset FROM swap WHERE seq=CASE chunks.seq WHEN 1 THEN 2 ELSE 1 END)
WHERE rel_path=@r AND seq IN (1,2);", new { r = rel });
                }

                var (_, exec, plan) = MkUpdate(left, right, rel);
                var (ok, _, failed, _) = await exec.ExecuteAsync(plan, null, CancellationToken.None);
                Check(ok == 1 && failed == 0, "③-1 投毒基线下同步不失败");
                Check(Sha256File(dst) == Sha256File(src), "③-2 终检拦截后整文件兜底，产物正确");
                Check(exec.DeltaSavedBytes == 0, "③-3 增量被拦（saved=0，走整文件）");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>4 目标内容被外部篡改（mtime 不变绕过预检）→ 终检兜底。</summary>
        private static async Task TestTargetTampered()
        {
            var root = Path.Combine(Path.GetTempPath(), "fsdelta_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            try
            {
                var rel = "f.bin";
                var src = Path.Combine(left, rel);
                var dst = Path.Combine(right, rel);
                WriteRandom(src, 8L * 1024 * 1024, seed: 3);
                File.Copy(src, dst);
                Thread.Sleep(20);
                ModifyMiddle(src);

                var (_, exec0, plan0) = MkUpdate(left, right, rel);
                await exec0.ExecuteAsync(plan0, null, CancellationToken.None);   // 建基线

                // 外部篡改目标（同长度字节，改完把 mtime 恢复 → 预检闸被骗过）。
                // 篡改点必须在源的改动区之外：命中块从被篡改的目标拷贝 → 内容错 → 终检炸
                var savedMtime = File.GetLastWriteTimeUtc(dst);
                ModifyMiddle(dst, at: dst.Length / 10, seed: 666);
                File.SetLastWriteTimeUtc(dst, savedMtime);
                ModifyMiddle(src, seed: 8);

                var (_, exec, plan) = MkUpdate(left, right, rel);
                var (ok, _, failed, _) = await exec.ExecuteAsync(plan, null, CancellationToken.None);
                Check(ok == 1 && failed == 0, "④-1 篡改目标后同步不失败");
                Check(Sha256File(dst) == Sha256File(src), "④-2 终检兜底，产物与源一致");
                Check(exec.DeltaSavedBytes == 0, "④-3 篡改场景增量收益归零（正确降级）");
                Check(BaselineStore.GetEntry(right, rel) == null, "④-4 脏基线被清除（下轮重建）");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>5 目标 mtime 被外部改动（内容没变）→ 预检闸拦截 → 整文件，不误判不出错。</summary>
        private static async Task TestTargetMtimeDrift()
        {
            var root = Path.Combine(Path.GetTempPath(), "fsdelta_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            try
            {
                var rel = "f.bin";
                var src = Path.Combine(left, rel);
                var dst = Path.Combine(right, rel);
                WriteRandom(src, 8L * 1024 * 1024, seed: 4);
                File.Copy(src, dst);
                Thread.Sleep(20);
                ModifyMiddle(src);

                var (_, exec0, plan0) = MkUpdate(left, right, rel);
                await exec0.ExecuteAsync(plan0, null, CancellationToken.None);   // 建基线

                // 外部只 touch 目标 mtime（内容不动）+ 源正常改动
                File.SetLastWriteTimeUtc(dst, File.GetLastWriteTimeUtc(dst).AddHours(1));
                ModifyMiddle(src, seed: 9);

                var (_, exec, plan) = MkUpdate(left, right, rel);
                var (ok, _, failed, _) = await exec.ExecuteAsync(plan, null, CancellationToken.None);
                Check(ok == 1 && failed == 0, "⑤-1 mtime 漂移后同步不失败");
                Check(Sha256File(dst) == Sha256File(src), "⑤-2 预检闸拦截走整文件，产物正确");
                Check(exec.DeltaSavedBytes == 0, "⑤-3 预检拦截（saved=0）");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>6 全零文件：CDC 全部 MaxChunk 钳制块同 hash（多实例）→ 多重集合逐个消耗正确组装。</summary>
        private static async Task TestAllZeroMultiInstance()
        {
            var root = Path.Combine(Path.GetTempPath(), "fsdelta_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            try
            {
                var rel = "zeros.bin";
                var src = Path.Combine(left, rel);
                var dst = Path.Combine(right, rel);
                using (var fs = new FileStream(src, FileMode.Create))
                    fs.SetLength(10L * 1024 * 1024);   // 10MB 全零
                File.Copy(src, dst);
                Thread.Sleep(20);
                ModifyMiddle(src, bytes: 2048);   // 源中间掺 2KB 随机

                var (_, exec, plan) = MkUpdate(left, right, rel);
                var (ok, _, failed, _) = await exec.ExecuteAsync(plan, null, CancellationToken.None);
                Check(ok == 1 && failed == 0, "⑥-1 全零多实例块同步成功");
                Check(Sha256File(dst) == Sha256File(src), "⑥-2 产物与源逐字节一致（同 hash 块未被错用）");
                Check(exec.DeltaSavedBytes > 6L * 1024 * 1024, $"⑥-3 零块复用有收益（省 {exec.DeltaSavedBytes / 1024.0 / 1024:F2} MB）");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>7 取消：块级传输中途 Cancel → OCE 上抛 + tmp 清理 + 目标旧内容完好 + 下轮正常。</summary>
        private static async Task TestCancelMidTransfer()
        {
            var root = Path.Combine(Path.GetTempPath(), "fsdelta_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            try
            {
                var rel = "f.bin";
                var src = Path.Combine(left, rel);
                var dst = Path.Combine(right, rel);
                WriteRandom(src, 60L * 1024 * 1024, seed: 5);
                File.Copy(src, dst);
                var shaBefore = Sha256File(dst);
                Thread.Sleep(20);
                ModifyMiddle(src);

                var (job, exec, plan) = MkUpdate(left, right, rel);
                var progress = new InlineProgress(p =>
                {
                    if (p.CurrentItem.Contains("[增量]")) exec.Cancel();   // 首个增量进度回调即取消
                });
                var oce = false;
                try { await exec.ExecuteAsync(plan, progress, CancellationToken.None); }
                catch (OperationCanceledException) { oce = true; }
                Check(oce, "⑦-1 块级中途取消抛 OCE");
                Check(Sha256File(dst) == shaBefore, "⑦-2 目标旧内容字节级完好");
                Check(!Directory.EnumerateFiles(right, "*" + Scanner.TmpSuffix, SearchOption.AllDirectories).Any(), "⑦-3 无 tmp 残留");

                // 下轮重跑正常完成
                var (_, exec2, plan2) = MkUpdate(left, right, rel);
                var (ok2, _, failed2, _) = await exec2.ExecuteAsync(plan2, null, CancellationToken.None);
                Check(ok2 == 1 && failed2 == 0 && Sha256File(dst) == Sha256File(src), "⑦-4 下轮重跑增量正常");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>8 阈值：3.9MB 走整文件（不建基线）、4.1MB 走块级。</summary>
        private static async Task TestThresholdBoundary()
        {
            var root = Path.Combine(Path.GetTempPath(), "fsdelta_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            try
            {
                // 3.9MB：低于阈值 → 整文件，无基线
                var rel1 = "small.bin";
                var src1 = Path.Combine(left, rel1);
                var dst1 = Path.Combine(right, rel1);
                WriteRandom(src1, (long)(3.9 * 1024 * 1024), seed: 6);
                File.Copy(src1, dst1);
                Thread.Sleep(20);
                ModifyMiddle(src1);
                var (_, exec1, plan1) = MkUpdate(left, right, rel1);
                await exec1.ExecuteAsync(plan1, null, CancellationToken.None);
                Check(exec1.DeltaSavedBytes == 0 && Sha256File(dst1) == Sha256File(src1), "⑧-1 3.9MB 走整文件且产物正确");
                Check(BaselineStore.GetEntry(right, rel1) == null, "⑧-2 小文件不建基线");

                // 4.1MB：高于阈值 → 块级
                var rel2 = "large.bin";
                var src2 = Path.Combine(left, rel2);
                var dst2 = Path.Combine(right, rel2);
                WriteRandom(src2, (long)(4.1 * 1024 * 1024), seed: 7);
                File.Copy(src2, dst2);
                Thread.Sleep(20);
                ModifyMiddle(src2);
                var (_, exec2, plan2) = MkUpdate(left, right, rel2);
                await exec2.ExecuteAsync(plan2, null, CancellationToken.None);
                Check(exec2.DeltaSavedBytes > 0 && Sha256File(dst2) == Sha256File(src2), "⑧-3 4.1MB 走块级且产物正确");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>10 基线库损坏（db 写成垃圾）→ 同步不失败，自愈降级。</summary>
        private static async Task TestCorruptBaselineDb()
        {
            var root = Path.Combine(Path.GetTempPath(), "fsdelta_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            try
            {
                var rel = "f.bin";
                var src = Path.Combine(left, rel);
                var dst = Path.Combine(right, rel);
                WriteRandom(src, 8L * 1024 * 1024, seed: 8);
                File.Copy(src, dst);
                Thread.Sleep(20);
                ModifyMiddle(src);

                var (_, exec0, plan0) = MkUpdate(left, right, rel);
                await exec0.ExecuteAsync(plan0, null, CancellationToken.None);   // 建基线
                ModifyMiddle(src, seed: 11);

                // 库写坏（垃圾字节）。连接复用后基线库文件被长连接持有——直接覆写会撞句柄占用，
                // 先 ClearAll（内部关连接删目录）再重建目录注入垃圾；后续操作仍走 OpenDb 的损坏重建分支
                BaselineStore.ClearAll(right);
                Directory.CreateDirectory(BaselineStore.RootOf(right));
                var dbPath = Path.Combine(BaselineStore.RootOf(right), "baseline.db");
                File.WriteAllText(dbPath, "this is not a sqlite database at all. corrupted.");

                var (_, exec, plan) = MkUpdate(left, right, rel);
                var (ok, _, failed, _) = await exec.ExecuteAsync(plan, null, CancellationToken.None);
                Check(ok == 1 && failed == 0, "⑩-1 基线库损坏同步不失败");
                Check(Sha256File(dst) == Sha256File(src), "⑩-2 产物正确（自愈/降级其一）");
                // 损坏库被自愈删除重建 → 本轮现场建表照样增量，成功后基线恢复
                Check(BaselineStore.GetEntry(right, rel) != null, "⑩-3 损坏库自愈，基线恢复");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>11 共享侧根两任务先后同步 → 基线最后写者胜，产物始终正确。</summary>
        private static async Task TestSharedSideRoot()
        {
            var root = Path.Combine(Path.GetTempPath(), "fsdelta_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            try
            {
                var rel = "f.bin";
                var src = Path.Combine(left, rel);
                var dst = Path.Combine(right, rel);
                WriteRandom(src, 6L * 1024 * 1024, seed: 9);
                File.Copy(src, dst);
                Thread.Sleep(20);
                ModifyMiddle(src);

                // 任务 A：增量建基线
                var (_, execA, planA) = MkUpdate(left, right, rel);
                var (okA, _, fA, _) = await execA.ExecuteAsync(planA, null, CancellationToken.None);
                Check(okA == 1 && fA == 0 && Sha256File(dst) == Sha256File(src), "⑪-1 任务 A 增量成功");

                // 任务 B（同侧根不同任务对象）：再改再增量（用共享的基线缓存）
                ModifyMiddle(src, seed: 12);
                var jobB = new SyncJob
                {
                    Name = "delta-b", LeftPath = left, RightPath = right,
                    Direction = SyncDirection.MirrorLeftToRight, DeltaSync = true
                };
                var execB = new Executor(jobB);
                var planB = new List<PlanEntry> { new PlanEntry
                {
                    Action = SyncAction.UpdateRight, RelativePath = rel, IsDirectory = false, Size = new FileInfo(src).Length
                } };
                var (okB, _, fB, _) = await execB.ExecuteAsync(planB, null, CancellationToken.None);
                Check(okB == 1 && fB == 0 && Sha256File(dst) == Sha256File(src), "⑪-2 任务 B 复用共享基线增量成功");
                Check(execB.DeltaSavedBytes > 0, "⑪-3 共享基线对任务 B 有效（跨任务缓存）");
                Check(BaselineStore.CountEntries(right) == 1, "⑪-4 基线单条（最后写者胜，无重复条目）");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>9 基线 GC：DeleteOne 删文件 → 条目即删；手动绕过删文件 → 轮末对账清理。</summary>
        private static async Task TestBaselineGc()
        {
            var root = Path.Combine(Path.GetTempPath(), "fsdelta_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            try
            {
                var rel = "f.bin";
                var src = Path.Combine(left, rel);
                var dst = Path.Combine(right, rel);
                WriteRandom(src, 5L * 1024 * 1024, seed: 10);
                File.Copy(src, dst);
                Thread.Sleep(20);
                ModifyMiddle(src);

                // 建基线
                var (_, exec0, plan0) = MkUpdate(left, right, rel);
                await exec0.ExecuteAsync(plan0, null, CancellationToken.None);
                Check(BaselineStore.GetEntry(right, rel) != null, "⑨-1 基线已建");

                // 路径 A：计划内删除（DeleteOne）→ 条目即删
                var job = new SyncJob
                {
                    Name = "delta", LeftPath = left, RightPath = right,
                    Direction = SyncDirection.MirrorLeftToRight, DeltaSync = true,
                    DeleteToRecycleBin = false   // 永久删，避免回收站依赖
                };
                var execDel = new Executor(job);
                var planDel = new List<PlanEntry> { new PlanEntry
                {
                    Action = SyncAction.DeleteRight, RelativePath = rel, IsDirectory = false, Size = 5L * 1024 * 1024
                } };
                await execDel.ExecuteAsync(planDel, null, CancellationToken.None);
                Check(!File.Exists(dst), "⑨-2 计划删除成功");
                Check(BaselineStore.GetEntry(right, rel) == null, "⑨-3 删除即清基线条目");

                // 路径 B：手动绕过删文件 → 轮末对账（Reconcile）清理死条目
                WriteRandom(src, 5L * 1024 * 1024, seed: 13);
                File.Copy(src, dst);
                Thread.Sleep(20);
                ModifyMiddle(src);
                var (_, exec1, plan1) = MkUpdate(left, right, rel);
                await exec1.ExecuteAsync(plan1, null, CancellationToken.None);
                Check(BaselineStore.GetEntry(right, rel) != null, "⑨-4 基线重建");
                File.Delete(dst);   // 绕过工具直接删（基线条目变死条目）
                BaselineStore.Reconcile(right, new HashSet<string>(StringComparer.OrdinalIgnoreCase));   // 模拟轮末对账（该侧扫描为空）
                Check(BaselineStore.GetEntry(right, rel) == null, "⑨-5 轮末对账清死条目");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>12 引擎全路径：RunAsync 增量轮 → runs.delta_saved_bytes 落库 + 「增量省」状态文案。</summary>
        /// <summary>性能实测（delta-perf 模式）：大文件改 1KB，整文件 vs 块级的耗时/写入量对照。非断言，输出数值。</summary>
        public static int RunPerf(string? targetRoot)
        {
            // 源固定在 C:（系统盘 NVMe），目标给 D:（跨物理盘 NVMe→NVMe）；无 HDD 如实记录
            var root = Path.Combine(Path.GetTempPath(), "fsperf_" + Guid.NewGuid().ToString("N")[..8]);
            targetRoot ??= Path.Combine("D:\\", "fsperf_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(targetRoot, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            try
            {
                Console.WriteLine($"源: {left}");
                Console.WriteLine($"目标: {right}（跨物理盘）");
                Console.WriteLine($"| {"文件",-8} | {"方式",-4} | {"耗时s",7} | {"写入B",13} |");
                Console.WriteLine($"|{"-",-10}|{"-",-6}|{"-",9}|{"-",15}|");
                foreach (var size in new[] { 100L * 1024 * 1024, 1024L * 1024 * 1024 })
                {
                    var rel = $"perf_{size >> 20}mb.bin";
                    var src = Path.Combine(left, rel);
                    var dst = Path.Combine(right, rel);
                    WriteRandom(src, size, seed: 77);

                    foreach (var delta in new[] { false, true })
                    {
                        // 基态：目标=旧版本
                        File.Delete(dst);
                        File.Copy(src, dst);
                        // 预热轮：块级建基线并同步到 dst（整文件组只热缓存）；此后 dst=同步后状态
                        ModifyMiddle(src, seed: 101);
                        File.SetLastWriteTimeUtc(src, DateTime.UtcNow.AddMinutes(1));
                        RunOnce(left, right, rel, delta);
                        // 计时轮：dst 重置为预热轮产物（File.Copy 保留 mtime → 与基线记录一致，预检闸过，
                        // 测的是有基线缓存的稳态增量而非首跑现场建表），源再改 1KB 触发 Update
                        File.Delete(dst);
                        File.Copy(src, dst);
                        ModifyMiddle(src, seed: 202);
                        File.SetLastWriteTimeUtc(src, DateTime.UtcNow.AddMinutes(1));
                        var (ms, written, saved) = RunOnce(left, right, rel, delta);
                        var label = delta ? "块级" : "整文件";
                        var w = delta ? written : size;
                        Console.WriteLine($"| {size >> 20} MB | {label} | {ms / 1000.0,7:F2} | {w,13} |"
                            + (delta ? $" (省 {Executor.FormatSize(saved)})" : ""));
                    }
                    try { File.Delete(src); File.Delete(dst); } catch { }
                }
                return 0;
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
                try { BaselineStore.ClearAll(right); Directory.Delete(targetRoot, true); } catch { }
            }
        }

        private static (long ms, long written, long saved) RunOnce(string left, string right, string rel, bool delta)
        {
            var job = new SyncJob
            {
                Name = "perf", LeftPath = left, RightPath = right,
                Direction = SyncDirection.MirrorLeftToRight, DeltaSync = delta
            };
            var exec = new Executor(job);
            var plan = new List<PlanEntry> { new PlanEntry
            {
                Action = SyncAction.UpdateRight, RelativePath = rel, IsDirectory = false,
                Size = new FileInfo(Path.Combine(left, rel)).Length
            } };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            exec.ExecuteAsync(plan, null, CancellationToken.None).GetAwaiter().GetResult();
            sw.Stop();
            var size = plan[0].Size;
            var written = delta ? size - exec.DeltaSavedBytes : size;
            return (sw.ElapsedMilliseconds, written, exec.DeltaSavedBytes);
        }

        private static async Task TestRunRecordPersist()
        {
            var root = Path.Combine(Path.GetTempPath(), "fsdelta_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            try
            {
                var rel = "f.bin";
                var src = Path.Combine(left, rel);
                var dst = Path.Combine(right, rel);
                WriteRandom(src, 6L * 1024 * 1024, seed: 14);
                File.Copy(src, dst);
                ModifyMiddle(src);
                // 显式拉开 mtime：exFAT/FAT 卷时间戳粒度不一，赌 Sleep 时长不可靠（exFAT 真卷实测 120ms 仍判无差异）
                File.SetLastWriteTimeUtc(src, DateTime.UtcNow.AddMinutes(1));

                var job = new SyncJob
                {
                    Name = "delta-engine", LeftPath = left, RightPath = right,
                    Direction = SyncDirection.MirrorLeftToRight, DeltaSync = true
                };
                using var db = new Db(Path.Combine(root, "test.db"));
                job.Id = db.InsertJob(job);
                var engine = new SyncEngine(job, db);
                var (rec, _) = await engine.RunAsync("manual", execute: true);

                Check(rec.Status == "ok", "⑫-1 引擎轮成功");
                Check(rec.DeltaSavedBytes > 0, $"⑫-2 record.DeltaSavedBytes > 0（省 {rec.DeltaSavedBytes} B）");
                var runs = db.GetRecentRuns(job.Id);
                Check(runs.Count > 0 && runs[0].DeltaSavedBytes == rec.DeltaSavedBytes,
                    $"⑫-3 runs 表落库一致（库里 {runs.FirstOrDefault()?.DeltaSavedBytes ?? -1} B）");
                Check(engine.StatusText.Contains("增量省"), $"⑫-4 完成文案含「增量省」: {engine.StatusText}");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>13 设置页全局清理：多站点 + 孤儿目录全清，CountAllEntries 统计正确。</summary>
        private static async Task TestClearAllSites()
        {
            var root = Path.Combine(Path.GetTempPath(), "fsdelta_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            try
            {
                BaselineStore.ClearAllSites();   // 清场：前面用例在同根目录留下的站点（全局清理正是被测语义）
                // 两个侧根各建一条基线
                foreach (var (l, r, seed) in new[] { (left, right, 21), (right, left, 22) })
                {
                    var rel = $"f{seed}.bin";
                    var src = Path.Combine(l, rel);
                    var dst = Path.Combine(r, rel);
                    WriteRandom(src, 4L * 1024 * 1024 + 100 * 1024, seed);
                    File.Copy(src, dst);
                    ModifyMiddle(src, seed: seed);
                    File.SetLastWriteTimeUtc(src, DateTime.UtcNow.AddMinutes(1));
                    var (_, exec, plan) = MkUpdate(l, r, rel);
                    await exec.ExecuteAsync(plan, null, CancellationToken.None);
                    Check(exec.DeltaSavedBytes > 0, $"⑬-{seed - 20} 侧根 {seed - 20} 基线已建");
                }
                // 孤儿目录（无 side.txt，模拟异常残留）
                var orphanHash = new string('a', 16);
                Directory.CreateDirectory(Path.Combine(BaselineStore.RootBase, orphanHash));
                File.WriteAllText(Path.Combine(BaselineStore.RootBase, orphanHash, "baseline.db"), "junk");

                Check(BaselineStore.CountAllEntries() == 2, "⑬-3 全局统计=2 条（两站点各 1）");
                var sites = BaselineStore.ClearAllSites();
                Check(sites == 3, $"⑬-4 全局清理 3 处站点（含孤儿，实际 {sites}）");
                Check(BaselineStore.CountAllEntries() == 0 && !Directory.Exists(Path.Combine(BaselineStore.RootBase, orphanHash)),
                    "⑬-5 清后统计归零且孤儿目录已删");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
        /// <summary>14 E1 归档顺带建基线：开版本保留任务整文件落地（Create/Update）即建基线，
        /// 第二次改 1KB 直接走 DeltaTransfer（基线命中，无现场切目标）。</summary>
        private static async Task TestE1BaselineAfterCopy()
        {
            var root = Path.Combine(Path.GetTempPath(), "fsdelta_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            try
            {
                var rel = "e1.bin";
                var src = Path.Combine(left, rel);
                var dst = Path.Combine(right, rel);
                WriteRandom(src, 8L * 1024 * 1024, seed: 31);

                var job = new SyncJob
                {
                    Name = "e1", LeftPath = left, RightPath = right,
                    Direction = SyncDirection.MirrorLeftToRight, DeltaSync = true, VersionKeepCount = 3
                };
                using var db = new Db(Path.Combine(root, "t.db"));
                job.Id = db.InsertJob(job);
                var engine = new SyncEngine(job, db);

                var (r1, _) = await engine.RunAsync("manual");   // Create：整文件落地
                Check(r1.Status == "ok", "⑭-1 首轮（新建）ok");
                Check(BaselineStore.GetEntry(right, rel) != null, "⑭-2 E1：整文件落地即建基线（下轮免现场切）");
                var shaAfterCopy = Sha256File(dst);

                ModifyMiddle(src, bytes: 1024, seed: 41);
                Thread.Sleep(20);
                var (r2, _) = await engine.RunAsync("manual");   // Update：应走 DeltaTransfer 且基线命中
                Check(r2.Status == "ok", "⑭-3 二轮 ok");
                Check(Sha256File(dst) == Sha256File(src) && Sha256File(dst) != shaAfterCopy, "⑭-4 增量产物正确");
                Check(r2.DeltaSavedBytes > 5L * 1024 * 1024,
                    $"⑭-5 基线命中走增量（省 {r2.DeltaSavedBytes / 1024.0 / 1024:F2} MB，只传缺块非整文件）");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>15 E1：归档失败不建基线（方案约束：异常时保守不添乱）。</summary>
        private static async Task TestE1ArchiveFailNoBaseline()
        {
            var root = Path.Combine(Path.GetTempPath(), "fsdelta_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            var origCentral = VersionStore.CentralRootOverride;
            try
            {
                var rel = "e1f.bin";
                var src = Path.Combine(left, rel);
                var dst = Path.Combine(right, rel);
                WriteRandom(src, 8L * 1024 * 1024, seed: 51);
                File.Copy(src, dst);   // 目标旧版本在
                Thread.Sleep(20);
                WriteRandom(src, 8L * 1024 * 1024, seed: 999);   // 全新内容：全缺块 → DeltaTransfer 判纯亏降级整文件

                // 版本库中心根指向非法路径 → ArchiveFile 必败 → E1 不建基线（keep=3 走真归档失败路径）
                VersionStore.CentralRootOverride = Path.Combine(root, "bad<|>root");
                var job = new SyncJob
                {
                    Name = "e1fail", LeftPath = left, RightPath = right,
                    Direction = SyncDirection.MirrorLeftToRight, DeltaSync = true, VersionKeepCount = 3
                };
                var exec = new Executor(job);
                var plan = new List<PlanEntry>
                {
                    new PlanEntry { Action = SyncAction.UpdateRight, RelativePath = rel, IsDirectory = false, Size = new FileInfo(src).Length }
                };
                var (ok, _, failed, _) = await exec.ExecuteAsync(plan, null, CancellationToken.None);
                Check(ok == 1 && failed == 0, "⑮-1 归档失败同步仍成功（降级常规覆盖）");
                Check(Sha256File(dst) == Sha256File(src), "⑮-2 产物正确");
                Check(BaselineStore.GetEntry(right, rel) == null, "⑮-3 归档失败不建基线（keep>0 仍不建）");
            }
            finally
            {
                VersionStore.CentralRootOverride = origCentral;
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
