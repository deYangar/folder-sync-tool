using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FolderSync.Core;

namespace FolderSync.SmokeTest
{
    /// <summary>
    /// v1.3 块级去重版本库专项（VersionStore/CdcChunker 直调单测）。
    /// 端到端集成（引擎级归档挂点）在 FixRegressionTests；这里打存储层本身。
    /// </summary>
    internal static class VersionRepoTests
    {
        private static int _fail;

        private static void Check(bool cond, string name)
        {
            Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}");
            if (!cond) _fail++;
        }

        public static async Task<int> RunAll()
        {
            VersionStore.PruneThrottle = TimeSpan.Zero;   // P-1 节流会让连跑的多轮清理被跳过，测试关掉
            TestChunkerProperties();
            await TestArchiveBasic();
            await TestEmptyFile();
            await TestCompressionAndDirectStore();
            await TestDedupIdentical();
            await TestDedupSmallDelta();
            await TestPruneGenerations();
            await TestGcOrphans();
            await TestCrashResidue();
            await TestRestoreTamper();
            await TestDeepPath();
            await TestConcurrentSameSide();
            await TestStats();
            await TestCentralLayout();

            Console.WriteLine(_fail == 0 ? "\n=== 版本库专项全部通过 ===" : $"\n=== {_fail} 项失败 ===");
            return _fail;
        }

        private static string TempDir(string tag) =>
            Path.Combine(Path.GetTempPath(), $"fsrepo_{tag}_" + Guid.NewGuid().ToString("N")[..8]);

        private static byte[] RandomBytes(int len, int seed)
        {
            var b = new byte[len];
            new Random(seed).NextBytes(b);
            return b;
        }

        private static string Sha256(byte[] data) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));

        /// <summary>切块器性质：确定性 / 位移稳定（头部插入复用 ≥90% 块）/ max 钳制 / 平均块大小合理</summary>
        private static void TestChunkerProperties()
        {
            var data = RandomBytes(8 * 1024 * 1024, 7);
            List<byte[]> Chunks(byte[] d) { using var ms = new MemoryStream(d); return CdcChunker.Chunk(ms).ToList(); }
            string Sig(List<byte[]> cs) => string.Join(",", cs.Select(c => Sha256(c)));

            var a = Chunks(data);
            Check(Sig(Chunks(data)) == Sig(a), "切块器: 同内容两次切块哈希序列一致（确定性）");

            var shifted = new byte[data.Length + 1];
            Buffer.BlockCopy(data, 0, shifted, 1, data.Length);
            shifted[0] = 0xAB;
            var c2 = Chunks(shifted);
            var set2 = c2.Select(Sha256).ToHashSet();
            var reused = a.Count(c => set2.Contains(Sha256(c)));
            Check(reused >= a.Count * 9 / 10,
                $"切块器: 头部插入 1 字节尾部块复用 ≥90%（实际 {reused}/{a.Count}）");

            Check(a.Max(c => c.Length) <= CdcChunker.MaxChunkSize, "切块器: 块 ≤ 2MiB 硬上限");
            var avg = a.Average(c => (double)c.Length);
            Check(avg > 200 * 1024 && avg < 1100 * 1024, $"切块器: 平均块 {avg / 1024:F0}KiB 在合理区间（名义 512KiB）");
            using var tiny = new MemoryStream(new byte[100]);
            var t = CdcChunker.Chunk(tiny).ToList();
            Check(t.Count == 1 && t[0].Length == 100, "切块器: 小文件单块原样输出");
        }

        /// <summary>归档→索引→删原文件→还原：字节级一致 + mtime 恢复；还原目标已存在时覆盖</summary>
        private static async Task TestArchiveBasic()
        {
            var root = TempDir("basic");
            Directory.CreateDirectory(root);
            try
            {
                var side = Path.Combine(root, "side");
                Directory.CreateDirectory(side);
                var src = Path.Combine(side, "doc.txt");
                await File.WriteAllBytesAsync(src, RandomBytes(3 * 1024 * 1024 + 12345, 1));  // 多块文件
                File.SetLastWriteTimeUtc(src, new DateTime(2020, 5, 17, 8, 30, 0, DateTimeKind.Utc));

                var ok = VersionStore.ArchiveFile(side, src, "doc.txt", jobId: 1, ts: "t1", out var err);
                Check(ok, $"归档: 成功（{err}）");
                Check(!File.Exists(src), "归档: 原文件已删除");

                var objs = Directory.EnumerateFiles(Path.Combine(VersionStore.RepoDirOf(side), "objects"),
                    "*", SearchOption.AllDirectories).ToList();
                Check(objs.Count > 1, $"归档: objects 落盘多块（实际 {objs.Count}）");
                Check(objs.All(o => !o.EndsWith(Scanner.TmpSuffix, StringComparison.OrdinalIgnoreCase)),
                    "归档: 无 tmp 残留");
                Check((new DirectoryInfo(VersionStore.StoreRootOf(side)).Attributes
                        & FileAttributes.Hidden) != 0, "归档: 库根目录自动设为隐藏");

                // 还原到新路径：字节 + mtime
                var dest = Path.Combine(root, "restored.txt");
                VersionStore.RestoreVersion(side, 1, dest);
                var expect = new byte[3 * 1024 * 1024 + 12345];
                new Random(1).NextBytes(expect);
                Check(Sha256(await File.ReadAllBytesAsync(dest)) == Sha256(expect),
                    "还原: 逐字节一致（sha256）");
                Check(File.GetLastWriteTimeUtc(dest) == new DateTime(2020, 5, 17, 8, 30, 0, DateTimeKind.Utc),
                    "还原: mtime 恢复为归档前值");

                // 还原到已存在目标：overwrite 语义
                await File.WriteAllTextAsync(dest, "old-content");
                VersionStore.RestoreVersion(side, 1, dest);
                Check(new FileInfo(dest).Length == expect.Length, "还原: 目标已存在时原子覆盖");

                var files = VersionStore.ListFiles(side);
                Check(files.Count == 1 && files[0].RelPath == "doc.txt" && files[0].VersionCount == 1,
                    "归档: ListFiles 聚合正确");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>0 字节文件：切块器产 0 块，归档/列表/还原全程正常（修复前 RestoreVersion 误报「库损坏」）</summary>
        private static async Task TestEmptyFile()
        {
            var root = TempDir("empty");
            Directory.CreateDirectory(root);
            var side = Path.Combine(root, "side");
            Directory.CreateDirectory(side);
            try
            {
                var f = Path.Combine(side, "empty.txt");
                await File.WriteAllBytesAsync(f, Array.Empty<byte>());
                File.SetLastWriteTimeUtc(f, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                var ok = VersionStore.ArchiveFile(side, f, "empty.txt", 1, "t1", out var err);
                Check(ok && !File.Exists(f), $"空文件: 归档成功且原文件已删（err={err}）");

                var versions = VersionStore.ListVersions(side, 1, "empty.txt");
                Check(versions.Count == 1 && versions[0].ChunkCount == 0 && versions[0].Size == 0,
                    "空文件: 版本记录 0 块 0 字节");

                var dest = Path.Combine(root, "restored.txt");
                var threw = "";
                try { VersionStore.RestoreVersion(side, versions[0].Id, dest); }
                catch (Exception ex) { threw = ex.Message; }
                Check(threw == "" && File.Exists(dest) && new FileInfo(dest).Length == 0,
                    threw != "" ? $"空文件: 还原失败（{threw}）" : "空文件: 还原出 0 字节文件");
                Check(File.GetLastWriteTimeUtc(dest) == new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    "空文件: mtime 恢复");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>压缩：文本块 Brotli 生效（csize<size）；媒体魔数直存；随机数据膨胀保险直存</summary>
        private static async Task TestCompressionAndDirectStore()
        {
            var root = TempDir("comp");
            Directory.CreateDirectory(root);
            var side = Path.Combine(root, "side");
            Directory.CreateDirectory(side);
            try
            {
                // 可压文本
                var textFile = Path.Combine(side, "text.log");
                await File.WriteAllTextAsync(textFile, string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog; ", 3000)));
                VersionStore.ArchiveFile(side, textFile, "text.log", 1, "t1", out _);

                // jpg 魔数（直存）
                var jpg = Path.Combine(side, "img.jpg");
                var jpgData = RandomBytes(5000, 3);
                jpgData[0] = 0xFF; jpgData[1] = 0xD8; jpgData[2] = 0xFF;
                await File.WriteAllBytesAsync(jpg, jpgData);
                VersionStore.ArchiveFile(side, jpg, "img.jpg", 1, "t1", out _);

                // 全随机（不可压 → 膨胀保险直存）
                var rndFile = Path.Combine(side, "rnd.bin");
                await File.WriteAllBytesAsync(rndFile, RandomBytes(300 * 1024, 9));
                VersionStore.ArchiveFile(side, rndFile, "rnd.bin", 1, "t1", out _);

                // 读索引验证 csize
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                    new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                    {
                        DataSource = Path.Combine(VersionStore.RepoDirOf(side), "index.db"),
                        Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly
                    }.ToString());
                conn.Open();
                var rows = conn.Query<(string hash, long size, long csize)>(
                    "SELECT hash, size, csize FROM chunks ORDER BY hash").ToList();
                Check(rows.Count == 3, $"压缩: 三文件各 1 块（实际 {rows.Count}）");
                var compressed = rows.Where(r => r.csize < r.size).ToList();   // 直存块 = size+1（格式头），压缩块 < size
                var textRow = compressed.OrderBy(r => r.size - r.csize).First();   // 文本压缩收益最大
                Check(compressed.Count == 1, $"压缩: 仅文本块被压缩（压缩块数 {compressed.Count}，明细 {string.Join(",", rows.Select(r => $"{r.size}->{r.csize}"))}）");
                Check(textRow.csize < textRow.size, $"压缩: 文本块 Brotli 生效（{textRow.size}→{textRow.csize}B）");
                Check(rows.Count(r => r.csize == r.size + 1) == 2, "压缩: jpg 魔数直存 + 随机数据膨胀保险直存（csize==size+1 头）");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>同内容 A→B→A：第三版零新块，objects 数量不变</summary>
        private static async Task TestDedupIdentical()
        {
            var root = TempDir("dedup");
            Directory.CreateDirectory(root);
            var side = Path.Combine(root, "side");
            Directory.CreateDirectory(side);
            try
            {
                var content = RandomBytes(150 * 1024, 5);
                for (int i = 0; i < 3; i++)
                {
                    var f = Path.Combine(side, $"f{i}.bin");
                    await File.WriteAllBytesAsync(f, content);
                    VersionStore.ArchiveFile(side, f, $"f{i}.bin", 1, $"t{i}", out var err);
                    if (err != null) throw new IOException(err);
                }
                var objs = Directory.EnumerateFiles(Path.Combine(VersionStore.RepoDirOf(side), "objects"),
                    "*", SearchOption.AllDirectories).ToList();
                Check(objs.Count == 1, $"去重: 同内容三版共享 1 块（实际 {objs.Count}）");
                var versions = VersionStore.ListVersions(side, 1, "f2.bin");
                Check(versions.Count == 1 && versions[0].ChunkCount == 1, "去重: 版本行与块引用正确");
                var stats = VersionStore.Stats(side)!;
                Check(stats.LogicalBytes == 150L * 1024 * 3 && stats.ChunkCount == 1,
                    $"去重: 统计=3 版逻辑 450KiB / 落盘 1 块（实际 {stats.LogicalBytes}B/{stats.ChunkCount} 块）");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>100MB 文件中部改 1KB 再归档：库增量 <5MB（旧全量方案 = 100MB）</summary>
        private static async Task TestDedupSmallDelta()
        {
            var root = TempDir("delta");
            Directory.CreateDirectory(root);
            var side = Path.Combine(root, "side");
            Directory.CreateDirectory(side);
            try
            {
                var big = RandomBytes(100 * 1024 * 1024, 11);
                var f1 = Path.Combine(side, "big.bin");
                await File.WriteAllBytesAsync(f1, big);
                VersionStore.ArchiveFile(side, f1, "big.bin", 1, "t1", out _);
                var sizeAfterV1 = VersionStore.Stats(side)!.StoredBytes;

                Array.Copy(RandomBytes(1024, 12), 0, big, 50 * 1024 * 1024, 1024);   // 中部改 1KB
                var f2 = Path.Combine(side, "big2.bin");
                await File.WriteAllBytesAsync(f2, big);
                VersionStore.ArchiveFile(side, f2, "big2.bin", 1, "t2", out _);
                var sizeAfterV2 = VersionStore.Stats(side)!.StoredBytes;

                var growth = sizeAfterV2 - sizeAfterV1;
                Check(growth is > 0 and < 5 * 1024 * 1024,
                    $"去重: 100MB 改 1KB 库增量 {growth / 1024.0:F0}KiB < 5MB（旧方案此场景 +100MB）");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>同文件 7 版保 5：versions 行数与内容正确，多余已删</summary>
        private static async Task TestPruneGenerations()
        {
            var root = TempDir("prune");
            Directory.CreateDirectory(root);
            var side = Path.Combine(root, "side");
            Directory.CreateDirectory(side);
            try
            {
                for (int v = 1; v <= 7; v++)
                {
                    var f = Path.Combine(side, "f.txt");
                    await File.WriteAllTextAsync(f, $"content-v{v}");
                    VersionStore.ArchiveFile(side, f, "f.txt", 1, $"2026-09-09T10:00:{v:00}.5+08:00", out _);
                }
                VersionStore.PruneAll(side, 5);
                var versions = VersionStore.ListVersions(side, 1, "f.txt");
                Check(versions.Count == 5, $"代数清理: 7 版保 5（实际 {versions.Count}）");
                // 还原最老保留版（ts 第 3 秒）应为 content-v3
                var dest = Path.Combine(root, "r.txt");
                VersionStore.RestoreVersion(side, versions.OrderBy(v => v.Ts).First().Id, dest);
                Check(await File.ReadAllTextAsync(dest) == "content-v3", "代数清理: 保留的是最近 5 代（v1/v2 已清）");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>GC：删版本后零引用块进 trash，下轮 PruneAll 真删</summary>
        private static async Task TestGcOrphans()
        {
            var root = TempDir("gc");
            Directory.CreateDirectory(root);
            var side = Path.Combine(root, "side");
            Directory.CreateDirectory(side);
            try
            {
                var f1 = Path.Combine(side, "a.bin");
                await File.WriteAllBytesAsync(f1, RandomBytes(200 * 1024, 21));
                VersionStore.ArchiveFile(side, f1, "a.bin", 1, "t1", out _);
                var f2 = Path.Combine(side, "b.bin");
                await File.WriteAllBytesAsync(f2, RandomBytes(200 * 1024, 22));
                VersionStore.ArchiveFile(side, f2, "b.bin", 1, "t1", out _);
                var objCount = Directory.EnumerateFiles(Path.Combine(VersionStore.RepoDirOf(side), "objects"),
                    "*", SearchOption.AllDirectories).Count();
                Check(objCount == 2, "GC 前置: 两个独立文件两块");

                VersionStore.DeleteVersion(side, 1);   // 删 a.bin 的版本 → 其块零引用
                var trash = Path.Combine(VersionStore.RepoDirOf(side), "trash");
                Check(Directory.Exists(trash) && Directory.EnumerateFiles(trash).Any(),
                    "GC: 零引用块已移入 trash 缓冲");
                Check(Directory.EnumerateFiles(Path.Combine(VersionStore.RepoDirOf(side), "objects"),
                    "*", SearchOption.AllDirectories).Count() == 1, "GC: objects 只剩被引用块");

                VersionStore.PruneAll(side, 5);        // 下轮：trash 真删
                Check(!Directory.EnumerateFiles(trash).Any(), "GC: 下一轮 trash 已清空");
                var dest = Path.Combine(root, "b2.bin");
                VersionStore.RestoreVersion(side, 2, dest);   // 存活版本还原不受影响
                Check(new FileInfo(dest).Length == 200 * 1024, "GC: 幸存版本可正常还原");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>崩溃残留：objects 里的 tmp 半截件 + 表外孤儿块 → PruneAll 清理，索引完好</summary>
        private static async Task TestCrashResidue()
        {
            var root = TempDir("crash");
            Directory.CreateDirectory(root);
            var side = Path.Combine(root, "side");
            Directory.CreateDirectory(side);
            try
            {
                var f = Path.Combine(side, "f.bin");
                await File.WriteAllBytesAsync(f, RandomBytes(100 * 1024, 31));
                VersionStore.ArchiveFile(side, f, "f.bin", 1, "t1", out _);

                var objects = Path.Combine(VersionStore.RepoDirOf(side), "objects", "zz");
                Directory.CreateDirectory(objects);
                await File.WriteAllBytesAsync(Path.Combine(objects, "deadbeef" + Scanner.TmpSuffix), new byte[100]);  // 半截中转件
                var orphan = Path.Combine(objects, "cafebabe0123456789");
                await File.WriteAllBytesAsync(orphan, RandomBytes(50 * 1024, 32)); // 表外孤儿
                // mtime 回拨 1 小时：真实崩溃残留一定是过去落盘的；GC 对最近 N 分钟的表外文件
                // 有跨进程归档窗口豁免（双实例同侧时另一进程可能块已落盘、索引未提交）
                File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow - TimeSpan.FromHours(1));

                VersionStore.PruneAll(side, 5);
                Check(!Directory.Exists(objects) ||
                      !Directory.EnumerateFiles(objects).Any(), "崩溃残留: tmp 与表外孤儿已清");
                var dest = Path.Combine(root, "r.bin");
                VersionStore.RestoreVersion(side, 1, dest);   // 正式块不受清理波及
                Check(new FileInfo(dest).Length == 100 * 1024, "崩溃残留: 正常版本完好");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>篡改检测：object 字节被改 → 还原中止报错，无半截文件落地</summary>
        private static async Task TestRestoreTamper()
        {
            var root = TempDir("tamper");
            Directory.CreateDirectory(root);
            var side = Path.Combine(root, "side");
            Directory.CreateDirectory(side);
            try
            {
                var f = Path.Combine(side, "f.bin");
                await File.WriteAllBytesAsync(f, RandomBytes(2 * 1024 * 1024 + 7, 41));   // 2 块
                VersionStore.ArchiveFile(side, f, "f.bin", 1, "t1", out _);

                var objs = Directory.EnumerateFiles(Path.Combine(VersionStore.RepoDirOf(side), "objects"),
                    "*", SearchOption.AllDirectories).ToList();
                var victim = objs[0];
                var bytes = await File.ReadAllBytesAsync(victim);
                bytes[^1] ^= 0xFF;
                await File.WriteAllBytesAsync(victim, bytes);

                var dest = Path.Combine(root, "r.bin");
                var threw = false;
                try { VersionStore.RestoreVersion(side, 1, dest); }
                catch { threw = true; }
                Check(threw, "篡改: 还原以异常中止");
                Check(!File.Exists(dest), "篡改: 无半截文件落地");
                Check(!Directory.EnumerateFiles(root, "*" + Scanner.TmpSuffix).Any(), "篡改: tmp 已清理");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>深路径：>260 字符侧根全程归档/还原（末段动态补长，任何 TEMP 根下都成立）</summary>
        private static async Task TestDeepPath()
        {
            var root = TempDir("deep");
            var side = Path.Combine(root, new string('d', 40), new string('e', 40), new string('f', 40),
                new string('g', 40), new string('h', 40), "leaf");
            while (side.Length <= 260) side += "x";   // TEMP 根短（如重定向到 X:\t）时补足深度
            Directory.CreateDirectory(side);
            try
            {
                Check(side.Length > 260, $"深路径前置: 侧根 {side.Length} 字符 >260");
                var f = Path.Combine(side, "deep.bin");
                await File.WriteAllBytesAsync(f, RandomBytes(70 * 1024, 51));
                var ok = VersionStore.ArchiveFile(side, f, "deep.bin", 1, "t1", out var err);
                Check(ok, $"深路径: 归档成功（{err}）");
                VersionStore.RestoreVersion(side, 1, Path.Combine(side, "restored.bin"));
                Check(new FileInfo(Path.Combine(side, "restored.bin")).Length == 70 * 1024, "深路径: 还原成功");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>并发：两线程同时向同一侧归档 → 全成功且索引无锁炸</summary>
        private static async Task TestConcurrentSameSide()
        {
            var root = TempDir("conc");
            Directory.CreateDirectory(root);
            var side = Path.Combine(root, "side");
            Directory.CreateDirectory(side);
            try
            {
                var t1 = Task.Run(() => ArchiveOne(side, "a.bin", 71));
                var t2 = Task.Run(() => ArchiveOne(side, "b.bin", 72));
                await Task.WhenAll(t1, t2);
                var files = VersionStore.ListFiles(side);
                Check(files.Count == 2, $"并发: 两文件均入库（实际 {files.Count}）");
                Check(VersionStore.Stats(side)!.VersionCount == 2, "并发: 索引两版本行");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        private static void ArchiveOne(string side, string name, int seed)
        {
            var f = Path.Combine(side, name);
            File.WriteAllBytes(f, RandomBytes(300 * 1024, seed));
            if (!VersionStore.ArchiveFile(side, f, name, 1, "t1", out var err))
                throw new IOException(err);
        }

        /// <summary>Stats：版本数/逻辑量/去重率</summary>
        private static async Task TestStats()
        {
            var root = TempDir("stats");
            Directory.CreateDirectory(root);
            var side = Path.Combine(root, "side");
            Directory.CreateDirectory(side);
            try
            {
                Check(VersionStore.Stats(side) == null, "统计: 库不存在返回 null");
                var content = RandomBytes(100 * 1024, 81);
                for (int i = 0; i < 2; i++)
                {
                    var f = Path.Combine(side, $"f{i}.txt");
                    await File.WriteAllBytesAsync(f, content);
                    VersionStore.ArchiveFile(side, f, $"f{i}.txt", 1, $"t{i}", out _);
                }
                var s = VersionStore.Stats(side)!;
                Check(s.VersionCount == 2 && s.LogicalBytes == 200 * 1024, $"统计: 2 版 200KiB（实际 {s.VersionCount}/{s.LogicalBytes}）");
                Check(s.ChunkCount == 1 && s.DedupRatio > 0.49, $"统计: 去重率 {(s.DedupRatio):P0} ≈ 50%（同内容双版共享一块）");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>中心化布局：新侧首库直落程序目录中心；side.txt 记录侧根；键大小写不敏感；
        /// 共享侧根的两任务共用一库（job_id 区分）</summary>
        private static async Task TestCentralLayout()
        {
            var root = TempDir("central");
            Directory.CreateDirectory(root);
            var side = Path.Combine(root, "side");
            Directory.CreateDirectory(side);
            try
            {
                var f = Path.Combine(side, "a.bin");
                await File.WriteAllBytesAsync(f, RandomBytes(100 * 1024, 91));
                VersionStore.ArchiveFile(side, f, "a.bin", 1, "t1", out _);

                Check(!Directory.Exists(Path.Combine(side, Scanner.VersionStoreDir)),
                    "中心化: 侧根下不再产生 _FolderSync_Versions");
                var store = VersionStore.StoreRootOf(side);
                Check(store.StartsWith(VersionStore.CentralRoot, StringComparison.OrdinalIgnoreCase),
                    $"中心化: 库落程序目录中心（实际 {store}）");
                Check(File.Exists(Path.Combine(store, "side.txt")) &&
                      File.ReadAllText(Path.Combine(store, "side.txt")) == side,
                    "中心化: side.txt 记录侧根路径");
                Check(VersionStore.StoreRootOf(side.ToUpperInvariant()) == store,
                    "中心化: 侧根大小写不敏感同键");

                // 共享同侧根的另一任务（job 7）归档 → 同一库，job_id 区分
                var f2 = Path.Combine(side, "b.bin");
                await File.WriteAllBytesAsync(f2, RandomBytes(100 * 1024, 92));
                VersionStore.ArchiveFile(side, f2, "b.bin", jobId: 7, ts: "t1", out _);
                Check(VersionStore.Stats(side)!.VersionCount == 2 &&
                      VersionStore.ListFiles(side, jobId: 7).Count == 1,
                    "中心化: 共享侧根的两任务共用一库（job_id 区分）");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
