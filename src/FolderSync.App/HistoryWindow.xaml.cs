using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FolderSync.Core;

namespace FolderSync.App
{
    /// <summary>运行历史窗口（v1.6 C1）：runs 表数据接 UI；双击详情；CSV 导出。</summary>
    public partial class HistoryWindow : Window
    {
        private readonly Db _db;
        private readonly SyncEngine _engine;
        private readonly List<RunRecord> _runs = new();
        private readonly List<HistoryRow> _rows = new();

        /// <summary>所属引擎（主窗口单例复用时判断是否换了任务）。</summary>
        public SyncEngine Engine => _engine;

        public HistoryWindow(SyncEngine engine, Db db)
        {
            InitializeComponent();
            _engine = engine;
            _db = db;
            TxtTitle.Text = $"任务「{engine.Job.Name}」最近 {_runs.Count} 轮运行";
            Load();
        }

        public void Load()
        {
            _runs.Clear();
            _rows.Clear();
            _runs.AddRange(_db.GetRecentRuns(_engine.Job.Id, 100));
            foreach (var r in _runs) _rows.Add(new HistoryRow(r));
            TxtTitle.Text = $"任务「{_engine.Job.Name}」最近 {_runs.Count} 轮运行";
            Grid.ItemsSource = null;
            Grid.ItemsSource = _rows;
        }

        private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetail();

        private void Grid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => ShowDetail();

        private HistoryRow? SelectedRow => Grid.SelectedItem as HistoryRow;

        private void ShowDetail()
        {
            var row = SelectedRow;
            if (row == null) return;
            var r = row.Record;
            var lines = new List<string>
            {
                $"第 {r.Id} 轮 · {r.StartedAt:yyyy-MM-dd HH:mm:ss} → {(r.FinishedAt?.ToString("HH:mm:ss") ?? "未完成")}"
                + $" · 触发 {RunReport.TriggerZh(r.Trigger)} · {RunReport.StatusZh(r.Status)}",
                $"扫描 {r.ScannedFiles} 项，复制 {r.CopiedFiles}，删除 {r.DeletedFiles}，移动 {r.MovedFiles}，"
                + $"跳过 {r.SkippedFiles}，失败 {r.FailedFiles}，重试成功 {r.RetriedOk}"
                + (r.DeltaSavedBytes > 0 ? $"，增量省 {Executor.FormatSize(r.DeltaSavedBytes)}" : "")
            };
            if (!string.IsNullOrEmpty(r.ErrorMessage))
                lines.Add("错误信息: " + r.ErrorMessage);
            var failed = _engine.LastFailedItems;
            if (r.Id == LastRunId() && failed.Count > 0)
            {
                lines.Add($"失败明细（前 10 条，共 {_engine.LastFailedTotal}）：");
                foreach (var f in failed.Take(10))
                    lines.Add($"  {f.Action}  {f.RelativePath}  {f.Error}");
            }
            TxtDetail.Text = string.Join("\n", lines);
            PnlReport.Visibility = Visibility.Visible;
        }

        private long LastRunId() => _runs.Count > 0 ? _runs[0].Id : -1;

        private void OpenReport_Click(object sender, RoutedEventArgs e)
        {
            var path = ReportPathOf(SelectedRow?.Record);
            if (path != null && File.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else if (path != null)
                MessageBox.Show(this, $"报告尚未生成（{path}）", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OpenFailed_Click(object sender, RoutedEventArgs e)
        {
            var r = SelectedRow?.Record;
            if (r == null) return;
            var path = Path.Combine(AppContext.BaseDirectory, "logs", "failed", $"job{r.JobId}", $"{r.Id}.txt");
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else
                MessageBox.Show(this, "该轮没有失败明细文件（无失败或已被清理）", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static string? ReportPathOf(RunRecord? r)
        {
            if (r == null || r.Id <= 0) return null;
            return Path.Combine(AppContext.BaseDirectory, "logs", "reports", $"job{r.JobId}", $"run{r.Id}.md");
        }

        private void ExportCsv_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出运行历史",
                Filter = "CSV 文件|*.csv",
                FileName = $"history-{_engine.Job.Name}-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("开始时间,完成时间,触发,状态,扫描,复制,删除,移动,失败,跳过,重试成功,字节,增量省字节,均速B/s,错误");
                foreach (var r in _runs)
                    sb.AppendLine(string.Join(",",
                        r.StartedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                        r.FinishedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                        r.Trigger, r.Status, r.ScannedFiles, r.CopiedFiles, r.DeletedFiles, r.MovedFiles,
                        r.FailedFiles, r.SkippedFiles, r.RetriedOk, r.BytesCopied, r.DeltaSavedBytes,
                        r.AvgSpeedBytesPerSec?.ToString("F0") ?? "",
                        "\"" + (r.ErrorMessage ?? "").Replace("\"", "\"\"") + "\""));
                File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));   // BOM：Excel 直开不乱码
                MessageBox.Show(this, $"已导出 {_runs.Count} 条到\n{dlg.FileName}", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                // 目标被 Excel 等占用/权限不足：本地提示即可，不能让异常冒到全局 handler 弹 crash 框（与 LogExport_Click 同纪律）
                MessageBox.Show(this, $"导出失败：{ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>DataGrid 行视图模型。</summary>
        public sealed class HistoryRow
        {
            public RunRecord Record { get; }
            public HistoryRow(RunRecord r) => Record = r;
            public string StartedText => Record.StartedAt.ToString("MM-dd HH:mm:ss");
            public string TriggerText => RunReport.TriggerZh(Record.Trigger);
            public string StatusText => RunReport.StatusZh(Record.Status);
            public Brush StatusBrush => Record.Status switch
            {
                "ok" => ThemeBrushes.C(ThemeBrushes.K.Success),
                "partial" => ThemeBrushes.C(ThemeBrushes.K.Warning),
                "running" => ThemeBrushes.C(ThemeBrushes.K.Accent),
                "cancelled" or "interrupted" or "skipped" => ThemeBrushes.C(ThemeBrushes.K.StatusDisabled),
                _ => ThemeBrushes.C(ThemeBrushes.K.Danger)
            };
            public int CopiedFiles => Record.CopiedFiles;
            public int DeletedFiles => Record.DeletedFiles;
            public int MovedFiles => Record.MovedFiles;
            public int FailedFiles => Record.FailedFiles;
            public int SkippedFiles => Record.SkippedFiles;
            public int RetriedOk => Record.RetriedOk;
            public string DeltaSavedText => Record.DeltaSavedBytes > 0 ? Executor.FormatSize(Record.DeltaSavedBytes) : "";
            public string DurationText => Record.FinishedAt is { } f
                ? (f - Record.StartedAt).TotalSeconds >= 60
                    ? $"{(f - Record.StartedAt).TotalMinutes:F1} 分"
                    : $"{(f - Record.StartedAt).TotalSeconds:F1} 秒"
                : "";
            public string SpeedText => Record.AvgSpeedBytesPerSec is > 1024
                ? $"{Executor.FormatSize(Record.AvgSpeedBytesPerSec.Value)}/s" : "";
        }
    }
}
