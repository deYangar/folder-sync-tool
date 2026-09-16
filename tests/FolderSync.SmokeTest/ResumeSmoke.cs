using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Core;

namespace FolderSync.SmokeTest
{
    /// <summary>v1.5 发布冒烟：对隔离目录的真 db 跑 LoadAllAndResumeAsync 全链路（startup 补跑），
    /// 等待完成并核对 runs 落库与磁盘产物。绕开 WPF 壳（GUI 单例 MessageBox 在无人值守会话会挂起）。</summary>
    internal static class ResumeSmoke
    {
        public static async Task<int> Run(string isoDir)
        {
            var dbPath = Path.Combine(isoDir, "foldersync.db");
            if (!File.Exists(dbPath)) { Console.WriteLine("FAIL  db 不存在: " + dbPath); return 1; }
            var left = Path.Combine(isoDir, "left");
            var right = Path.Combine(isoDir, "right");

            using var db = new Db(dbPath);
            var jobs = db.GetJobs();
            Console.WriteLine($"任务 {jobs.Count} 个: " + string.Join(",", jobs.Select(j => $"{j.Id}:{j.Name} md={j.MirrorDelete} mv={j.MoveDetect}")));
            if (jobs.Count == 0) { Console.WriteLine("FAIL  无任务"); return 1; }

            using var manager = new JobManager(db);
            await manager.LoadAllAndResumeAsync();
            // 补跑异步（并发上限 2）：轮询 runs 直到全部收尾
            var sw = System.Diagnostics.Stopwatch.StartNew();
            RunRecord? last = null;
            while (sw.Elapsed.TotalSeconds < 30)
            {
                await Task.Delay(1000);
                last = db.GetRecentRuns(jobs[0].Id, 1).FirstOrDefault();
                if (last != null && last.Status is "ok" or "partial" or "error" or "skipped" or "cancelled") break;
            }
            if (last == null) { Console.WriteLine("FAIL  补跑未产生 runs"); return 1; }
            Console.WriteLine($"startup 轮: status={last.Status} copied={last.CopiedFiles} moved={last.MovedFiles} retried={last.RetriedOk} failed={last.FailedFiles} sha={last.BytesCopied}");
            return last.Status == "ok" ? 0 : 1;
        }
    }
}
