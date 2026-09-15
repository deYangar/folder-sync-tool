using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using FolderSync.Core;

namespace FolderSync.App
{
    /// <summary>
    /// CLI 无头模式（v1.6 B4）：--run &lt;任务名&gt; / --run-all / --analyze &lt;任务名&gt; / --list——
    /// 不建主窗口不挂托盘，跑完即退，退出码供 schtasks/脚本复用。
    /// 与 GUI 实例并存：不走单例互斥（GUI 在跑时 CLI 照常工作；Db WAL 并发安全，
    /// 同任务并行跑会被引擎「正在运行中」挡——正是预期防重入语义）。
    /// 退出码：0=完全成功 / 3=部分失败 / 4=失败 / 5=任务名不存在 / 2=参数用法错。
    /// 输出：AttachConsole(ATTACH_PARENT_PROCESS) 尽力打到调用方控制台；
    /// 无论成败都写 logs\cli\cli-&lt;时间戳&gt;.log（保底观测面，UTF-8）。
    /// </summary>
    internal static class Cli
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);
        private const int ATTACH_PARENT_PROCESS = -1;

        public const int ExitOk = 0, ExitUsage = 2, ExitPartial = 3, ExitFailed = 4, ExitNoSuchJob = 5;

        /// <summary>args 命中 CLI 子集时执行并返回 true（exitCode 由 out 带出，调用方 Shutdown）。</summary>
        public static bool TryRun(string[] args, out int exitCode)
        {
            exitCode = ExitOk;
            // 只对已知 CLI 命令接管；其余 -- 参数（--minimized 自启 / --e2e 测试钩子）归 GUI 启动路径。
            // 旧写法「任何 -- 开头即 CLI」会让 --minimized 落进未知参数分支 ExitUsage(2)→Shutdown(2)，
            // 开机自启直接闪退、E2E 钩子全部失效。
            if (args == null || args.Length == 0) return false;
            if (args[0] is not ("--run" or "--run-all" or "--analyze" or "--list")) return false;

            var log = new StringBuilder();
            var stdout = AttachWriters();
            void Say(string line)
            {
                log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {line}");
                try { stdout?.WriteLine(line); } catch { }
            }

            try
            {
                exitCode = Run(args, Say);
                return true;
            }
            catch (Exception ex)
            {
                Say($"严重错误: {ex}");
                exitCode = ExitFailed;
                return true;
            }
            finally
            {
                WriteLogFile(log.ToString());
            }
        }

        private static int Run(string[] args, Action<string> say)
        {
            var cmd = args[0].ToLowerInvariant();
            using var db = new Db();
            var jobs = db.GetJobs();

            if (cmd == "--list")
            {
                say($"共 {jobs.Count} 个任务：");
                foreach (var j in jobs)
                    say($"  {j.Name}  [{j.Direction}{(j.Enabled ? "" : "，已停用")}]  {j.LeftPath} ⇄ {j.RightPath}");
                return ExitOk;
            }

            if (cmd == "--run-all")
            {
                if (jobs.Count == 0) { say("没有任务"); return ExitOk; }
                int worst = ExitOk;
                foreach (var j in jobs)
                {
                    var code = RunOne(j, db, execute: true, say);
                    if (code == ExitFailed) worst = ExitFailed;        // 失败最重
                    else if (code == ExitPartial && worst != ExitFailed) worst = ExitPartial;
                }
                return worst;
            }

            if (cmd is "--run" or "--analyze")
            {
                if (args.Length < 2) { say($"用法: FolderSync.App.exe {cmd} <任务名>"); return ExitUsage; }
                var job = jobs.FirstOrDefault(j => j.Name.Equals(args[1], StringComparison.OrdinalIgnoreCase));
                if (job == null)
                {
                    say($"任务不存在: {args[1]}（--list 查看全部任务名）");
                    return ExitNoSuchJob;
                }
                return RunOne(job, db, execute: cmd == "--run", say);
            }

            say($"未知参数: {args[0]}（支持 --run <任务名> / --run-all / --analyze <任务名> / --list）");
            return ExitUsage;
        }

        private static int RunOne(SyncJob job, Db db, bool execute, Action<string> say)
        {
            say($"{(execute ? "同步" : "分析")}任务「{job.Name}」…");
            var engine = new SyncEngine(job, db);   // 独立引擎：不经 JobManager（无补跑/触发器，CLI 就是执行器本身）
            try
            {
                var (rec, plan) = engine.RunAsync(execute ? "cli" : "cli-analyze", execute: execute).GetAwaiter().GetResult();
                say($"{(execute ? "同步" : "分析")}完成：状态 {rec.Status}，计划 {plan.Count} 项"
                    + (execute ? $"，成功 {rec.CopiedFiles}，失败 {rec.FailedFiles}，删除 {rec.DeletedFiles}"
                        + (rec.MovedFiles > 0 ? $"，移动 {rec.MovedFiles}" : "")
                        + (rec.RetriedOk > 0 ? $"，重试成功 {rec.RetriedOk}" : "")
                        + (rec.DeltaSavedBytes > 0 ? $"，增量省 {Executor.FormatSize(rec.DeltaSavedBytes)}" : "") : ""));
                if (!execute && plan.Count > 0)
                    foreach (var p in plan.Take(50))
                        say($"  {p.ActionText}  {p.RelativePath}" + (p.Note.Length > 0 ? $"  ({p.Note})" : ""));
                if (rec.FailedFiles > 0 && engine.LastFailedItems.Count > 0)
                {
                    say($"失败明细（前 10 条，共 {engine.LastFailedTotal}）：");
                    foreach (var f in engine.LastFailedItems.Take(10))
                        say($"  {f.Action}  {f.RelativePath}  {f.Error}");
                }
                return rec.Status switch
                {
                    "ok" or "skipped" => ExitOk,
                    "partial" => ExitPartial,
                    _ => ExitFailed
                };
            }
            catch (InvalidOperationException ex)
            {
                // 同任务 GUI/另一 CLI 正在跑：引擎 _busy 挡（预期防重入语义）
                say($"无法执行: {ex.Message}");
                return ExitFailed;
            }
            catch (Exception ex)
            {
                say($"执行失败: {ex.Message}");
                return ExitFailed;
            }
        }

        /// <summary>AttachConsole 接管父进程控制台（双击启动无父控制台时失败=只写日志文件）。
        /// stdout writer 惰性创建陷阱：Console 类首次使用后不再接管，这里 Attach 后显式 OpenStandardOutput。</summary>
        private static TextWriter? AttachWriters()
        {
            try
            {
                if (!AttachConsole(ATTACH_PARENT_PROCESS)) return null;
                Console.OutputEncoding = Encoding.UTF8;
                var sw = new StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding) { AutoFlush = true };
                return sw;
            }
            catch { return null; }
        }

        private static void WriteLogFile(string content)
        {
            try
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "logs", "cli");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, $"cli-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log"),
                    content, new UTF8Encoding(false));
                // 轮转：保留最近 200 档（文件名字典序=时间序）。高频计划任务每次运行一个新档，
                // 无上限常年累月写爆磁盘；crash.log 有 1MB 轮转，这里同纪律
                var logs = Directory.GetFiles(dir, "cli-*.log");
                if (logs.Length > 200)
                    foreach (var f in logs.OrderBy(f => f, StringComparer.Ordinal).Take(logs.Length - 200))
                        try { File.Delete(f); } catch { }
            }
            catch { /* 日志失败不影响退出码 */ }
        }
    }
}
