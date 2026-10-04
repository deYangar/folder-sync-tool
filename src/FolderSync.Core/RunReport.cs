using System;
using System.IO;
using System.Linq;
using System.Text;

namespace FolderSync.Core
{
    /// <summary>C1 轮末报告（v1.6）：RunFinished（成功/部分失败）时生成
    /// logs\reports\job{id}\run{runid}.md——时间、触发源、动作计数（含移动/重试）、增量节省、
    /// 校验结果、失败清单（前 50 条 + 全量文件指引）。UTF-8 无 BOM；尽力而为。</summary>
    public static class RunReport
    {
        public static string? ReportPathOf(RunRecord r) =>
            r.Id <= 0 ? null : Platform.AppPaths.ReportPath(r.JobId, r.Id);

        /// <summary>生成报告并返回路径；非成功/部分失败或落盘失败返回 null。</summary>
        public static string? Generate(SyncEngine engine, RunRecord r)
        {
            if (r.Status is not ("ok" or "partial") || r.Id <= 0) return null;
            try
            {
                var path = ReportPathOf(r)!;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var sb = new StringBuilder();
                sb.AppendLine($"# {engine.Job.Name} · 运行报告 #{r.Id}");
                sb.AppendLine();
                sb.AppendLine($"- 时间：{r.StartedAt:yyyy-MM-dd HH:mm:ss} → {r.FinishedAt:HH:mm:ss}");
                sb.AppendLine($"- 触发：{TriggerZh(r.Trigger)}　状态：{SyncEngine.StatusZh(r.Status)}");
                sb.AppendLine($"- 扫描 {r.ScannedFiles} 项 · 复制（建+改）{r.CopiedFiles} · 删除 {r.DeletedFiles}"
                    + $" · 移动 {r.MovedFiles} · 跳过 {r.SkippedFiles} · 失败 {r.FailedFiles} · 重试成功 {r.RetriedOk}");
                if (r.DeltaSavedBytes > 0)
                    sb.AppendLine($"- 增量传输节省：{Executor.FormatSize(r.DeltaSavedBytes)}（实际复制 {Executor.FormatSize(r.BytesCopied)}）");
                else if (r.BytesCopied > 0)
                    sb.AppendLine($"- 复制量：{Executor.FormatSize(r.BytesCopied)}");
                if (r.AvgSpeedBytesPerSec is > 0)
                    sb.AppendLine($"- 平均速率：{Executor.FormatSize(r.AvgSpeedBytesPerSec.Value)}/s");
                if (r.Trigger == "verify")
                    sb.AppendLine($"- 深度校验轮：发现 {engine.LastConflictCount} 处位腐差异（已标记待裁决）");
                if (!string.IsNullOrEmpty(r.ErrorMessage))
                    sb.AppendLine($"- 错误信息：{r.ErrorMessage}");
                var failed = engine.LastFailedItems;
                if (r.FailedFiles > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"## 失败清单（前 50 条，共 {r.FailedFiles}；全量见 logs\\failed\\job{r.JobId}\\{r.Id}.txt）");
                    foreach (var f in failed.Take(50))
                        sb.AppendLine($"- {f.Action}　{f.RelativePath}　{f.Error}");
                }
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                return path;
            }
            catch { return null; }
        }

        /// <summary>触发源中文（HistoryWindow 共用）。</summary>
        public static string TriggerZh(string t) => t switch
        {
            "manual" => "手动", "realtime" => "实时", "interval" => "定时", "startup" => "启动补跑",
            "schedule" => "定时(时刻)", "resolve" => "裁决", "retry" => "重试", "reconnect" => "重连",
            "cli" => "CLI", "cli-analyze" => "CLI分析", "verify" => "校验", _ => t
        };

        /// <summary>运行状态中文（App 侧共用；原 SyncEngine.StatusZh 为 internal 不跨程序集）。</summary>
        public static string StatusZh(string s) => s switch
        {
            "ok" => "完成", "partial" => "部分失败", "error" => "失败", "skipped" => "已跳过",
            "cancelled" => "已取消", "interrupted" => "上次中断", _ => s
        };
    }
}
