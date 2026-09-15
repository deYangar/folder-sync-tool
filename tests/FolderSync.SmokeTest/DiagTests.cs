using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Core;

namespace FolderSync.SmokeTest
{
    /// <summary>诊断：对真实任务目录做 分析→执行，逐步打印计划与执行进度，找出挂起点。</summary>
    public static class DiagTests
    {
        public static async Task<int> Run(string left, string right)
        {
            var dbPath = Path.Combine(Path.GetTempPath(), "fsdiag_" + Guid.NewGuid().ToString("N")[..6] + ".db");
            using var db = new Db(dbPath);
            var job = new SyncJob
            {
                Name = "diag", LeftPath = left, RightPath = right,
                Direction = SyncDirection.MirrorLeftToRight, MirrorDelete = false
            };
            job.Id = db.InsertJob(job);
            var engine = new SyncEngine(job, db);

            Console.WriteLine("[1] 分析（不执行）…");
            var sw = Stopwatch.StartNew();
            var (_, plan) = await engine.RunAsync("manual", execute: false);
            Console.WriteLine($"[1] 完成 {sw.Elapsed.TotalSeconds:F1}s，差异 {plan.Count} 项：");
            foreach (var p in plan.Take(60))
                Console.WriteLine($"    {p.Action} {p.SizeText,10}  {p.RelativePath}");

            Console.WriteLine("[2] 执行（带 90s 看门狗）…");
            var execTask = engine.RunAsync("manual", execute: true);
            var done = await Task.WhenAny(execTask, Task.Delay(90_000));
            if (done != execTask)
            {
                Console.WriteLine("[2] ✋ 90s 未完成 —— 挂起确认！进程内最后状态：");
                Console.WriteLine($"    Status={engine.Status} StatusText={engine.StatusText}");
                Console.WriteLine("    请在 UI 线程外抓取线程栈定位。诊断结束(exit=9)");
                return 9;
            }
            var (rec, plan2) = await execTask;
            Console.WriteLine($"[2] 执行完成: ok={rec.CopiedFiles} failed={rec.FailedFiles} skipped={rec.SkippedFiles} bytes={rec.BytesCopied} status={rec.Status}");
            if (rec.ErrorMessage != null) Console.WriteLine($"    err: {rec.ErrorMessage}");
            foreach (var p in plan2.Where(p => p.Action != SyncAction.None).Take(50))
                Console.WriteLine($"    {p.Action} {p.RelativePath}");
            return 0;
        }
    }
}
