using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FolderSync.Core;

namespace FolderSync.SmokeTest
{
    /// <summary>C1 轮末报告专项：成功/部分轮生成 logs\reports\job{id}\run{runid}.md，
    /// 关键字段（时间/触发/计数含移动重试/增量节省/失败清单）断言；取消轮不生成。</summary>
    internal static class ReportTests
    {
        private static int _fail;

        private static void Check(bool cond, string name, string extra = "")
        {
            Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}" + (extra.Length > 0 ? $" ({extra})" : ""));
            if (!cond) _fail++;
        }

        public static async Task<int> RunAll()
        {
            await TestReportGeneratedWithCounts();
            Console.WriteLine(_fail == 0 ? "report: ALL PASS" : $"report: {_fail} FAILED");
            return _fail;
        }

        private static async Task TestReportGeneratedWithCounts()
        {
            var root = Path.Combine(Path.GetTempPath(), "fsreport_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            try
            {
                // ok 轮：复制 2 个文件
                File.WriteAllText(Path.Combine(left, "a.txt"), "a");
                File.WriteAllText(Path.Combine(left, "b.txt"), "b");
                var job = new SyncJob { Name = "报表任务", LeftPath = left, RightPath = right };
                using var db = new Db(Path.Combine(root, "t.db"));
                job.Id = db.InsertJob(job);
                var engine = new SyncEngine(job, db);
                var (rec, _) = await engine.RunAsync("manual");
                Check(rec.Status == "ok", "ok 轮状态");
                var path = RunReport.Generate(engine, rec);   // UI 的 OnRunFinished 挂此生成；测试直调同一入口
                Check(path != null && File.Exists(path), "ok 轮报告已生成", path ?? "(null)");
                var text = File.ReadAllText(path!);
                Check(text.Contains("报表任务") && text.Contains("手动") && text.Contains("完成"), "报告含任务名/触发/状态");
                Check(text.Contains("复制（建+改）2"), "报告含复制计数", text.Split('\n').FirstOrDefault(l => l.Contains("复制")) ?? "");

                // partial 轮：锁源制造失败 → 报告含失败清单
                File.WriteAllText(Path.Combine(left, "c.txt"), "c");
                var hold = new FileStream(Path.Combine(left, "c.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
                try
                {
                    // 关自动重试让失败即时落（重试等待 42s 不值得）
                    job.AutoRetry = false;
                    var (rec2, _) = await engine.RunAsync("manual");
                    Check(rec2.Status == "partial", "partial 轮状态", rec2.Status);
                }
                finally { hold.Dispose(); }
                var path2 = RunReport.Generate(engine, db.GetRecentRuns(job.Id, 1)[0]);
                Check(path2 != null && File.Exists(path2), "partial 轮报告已生成");
                var text2 = File.ReadAllText(path2!);
                Check(text2.Contains("失败清单") && text2.Contains("c.txt") && text2.Contains("共 1"),
                    "partial 报告含失败清单与计数",
                    text2.Split('\n').FirstOrDefault(l => l.Contains("失败清单")) ?? "");
                Check(text2.Contains("logs\\failed"), "partial 报告含全量明细指引");
            }
            finally
            {
                // 报告落在测试 exe 的 bin 目录（AppContext.BaseDirectory）：按 job 精确清理
                try
                {
                    var reportsRoot = Path.Combine(AppContext.BaseDirectory, "logs", "reports");
                    if (Directory.Exists(reportsRoot))
                    {
                        var di = new DirectoryInfo(reportsRoot);
                        // 全空才删（并行测试保护）：本测试的 job 目录名唯一性不足，超 5 分钟的测试产物一并清
                        foreach (var d in di.GetDirectories())
                        {
                            if (d.GetFiles().All(f => (DateTime.Now - f.LastWriteTime).TotalMinutes < 5))
                                d.Delete(recursive: true);
                        }
                        if (!di.GetDirectories().Any()) di.Parent?.Delete(recursive: false);
                        Directory.Delete(reportsRoot, false);
                    }
                }
                catch { }
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
