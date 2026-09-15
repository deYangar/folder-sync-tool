using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Core;

namespace FolderSync.SmokeTest
{
    /// <summary>真实数据集成测试：真盘真复制，验证计数/字节/二次无差异/回收站删除链路。</summary>
    public static class RealDataTests
    {
        private static int _fail;

        private static void Check(bool cond, string name)
        {
            Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}");
            if (!cond) _fail++;
        }

        public static async Task<int> Run(string src, string dstRoot)
        {
            using var wi = WindowsIdentity.GetCurrent();
            Console.WriteLine($"[env] 用户={wi.Name} 提权={new WindowsPrincipal(wi).IsInRole(WindowsBuiltInRole.Administrator)}");
            Console.WriteLine($"[env] 源={src}");
            Console.WriteLine($"[env] 目标根={dstRoot}");

            var dbPath = Path.Combine(dstRoot, "_test.db");
            using var db = new Db(dbPath);

            // ---------- 任务1：小文件多目录（skills） ----------
            var dst1 = Path.Combine(dstRoot, "skills_copy");
            var r1 = await RunOne(db, "skills", src, dst1, mirrorDelete: false);
            if (r1) Console.WriteLine("--- 任务1 (多小文件) 完成 ---");

            // ---------- 任务2：大文件吞吐（template-finder） ----------
            var dst2 = Path.Combine(dstRoot, "template_finder_copy");
            var r2 = await RunOne(db, "template-finder",
                @"C:\Users\Yang\.openclaw\workspace\projects\template-finder", dst2, mirrorDelete: false);
            if (r2) Console.WriteLine("--- 任务2 (157MB 吞吐) 完成 ---");

            // ---------- 任务3：回收站删除链路（在 dst1 上） ----------
            await TestRecycleDelete(db, src, dst1);

            Console.WriteLine(_fail == 0 ? "\n=== 真实数据测试全部通过 ===" : $"\n=== {_fail} 项失败 ===");
            Console.WriteLine($"CLEANUP_HINT: {dstRoot}");
            return _fail;
        }

        private static async Task<bool> RunOne(Db db, string name, string src, string dst, bool mirrorDelete)
        {
            var job = new SyncJob
            {
                Name = name, LeftPath = src, RightPath = dst,
                Direction = SyncDirection.MirrorLeftToRight,
                ExcludePatterns = ".git; node_modules; bin; obj",
                MirrorDelete = mirrorDelete
            };
            job.Id = db.InsertJob(job);
            var engine = new SyncEngine(job, db);

            var sw = Stopwatch.StartNew();
            var (rec, plan) = await engine.RunAsync("manual", execute: true);
            var copySecs = sw.Elapsed.TotalSeconds;

            var srcCount = RealFiles(src).Count;
            var dstCount = RealFiles(dst).Count;
            var srcBytes = DirBytes(src);
            var dstBytes = DirBytes(dst);

            Console.WriteLine($"[{name}] 计划 {plan.Count} 项 | 复制 {rec.CopiedFiles} 失败 {rec.FailedFiles} " +
                $"| {Executor.FormatSize(rec.BytesCopied)} | {copySecs:F1}s ({Executor.FormatSize(rec.BytesCopied / Math.Max(copySecs, 0.1))}/s)");
            Console.WriteLine($"[{name}] 源 {srcCount} 文件/{Executor.FormatSize(srcBytes)}  目标 {dstCount} 文件/{Executor.FormatSize(dstBytes)}");

            Check(rec.FailedFiles == 0, $"[{name}] 零失败");
            Check(srcCount == dstCount, $"[{name}] 文件数一致 ({srcCount}=={dstCount})");
            Check(srcBytes == dstBytes, $"[{name}] 字节数一致 ({srcBytes}=={dstBytes})");

            // 二次分析应无差异
            var (_, plan2) = await engine.RunAsync("manual", execute: false);
            Check(plan2.Count == 0, $"[{name}] 二次分析无差异 (实际 {plan2.Count})");

            engine.Dispose();
            return true;
        }

        /// <summary>回收站删除链路：源建文件→同步→源删→镜像删→目标消失 + 文件进回收站。</summary>
        private static async Task TestRecycleDelete(Db db, string src, string dst)
        {
            Console.WriteLine("--- 回收站删除链路 ---");
            var probe = Path.Combine(src, "___recycle_probe.txt");
            var probeDst = Path.Combine(dst, "___recycle_probe.txt");

            await File.WriteAllTextAsync(probe, "recycle test");
            var job = new SyncJob
            {
                Name = "recycle", LeftPath = src, RightPath = dst,
                Direction = SyncDirection.MirrorLeftToRight,
                MirrorDelete = true, DeleteToRecycleBin = true,
                ExcludePatterns = ".git; node_modules; bin; obj"
            };
            job.Id = db.InsertJob(job);
            var engine = new SyncEngine(job, db);

            var (_, p1) = await engine.RunAsync("manual", execute: true);
            Check(File.Exists(probeDst), "探针文件已同步到目标");

            File.Delete(probe);   // 源删除
            var (_, p2) = await engine.RunAsync("manual", execute: true);
            Check(!File.Exists(probeDst), "镜像删除后目标副本消失");
            Check(p2.Any(x => x.Action == SyncAction.DeleteRight && x.RelativePath == "___recycle_probe.txt"),
                "计划中包含 DeleteRight 动作");

            engine.Dispose();
            // 回收站验证由外部 PowerShell 做（打印 RECYCLE_CHECK 行供脚本捕获）
            Console.WriteLine("RECYCLE_CHECK: ___recycle_probe.txt");
        }

        private static readonly string[] VerifyExcludes = { ".git", "node_modules", "bin", "obj" };

        private static System.Collections.Generic.List<string> RealFiles(string dir) =>
            Directory.EnumerateFiles(dir, "*", new EnumerationOptions
            { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
                .Where(f => !Scanner.IsExcluded(
                    f[dir.Length..].TrimStart('\\', '/').Replace('\\', '/'),
                    Path.GetFileName(f), VerifyExcludes))
                .ToList();

        private static long DirBytes(string dir) =>
            RealFiles(dir).Sum(f => new FileInfo(f).Length);
    }
}
