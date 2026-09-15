using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FolderSync.Core;
using Microsoft.Win32;

namespace FolderSync.App
{
    /// <summary>
    /// 版本浏览器：按侧列出历史文件与版本，支持还原到原位置 / 另存 / 删版本。
    /// 归档库 = repo（v1.3 块级去重）。v1.2 旧格式批次目录已无存量数据，浏览器不提供入口
    /// （历史注：PruneLegacyBatches 清理兼容逻辑曾保留防外部旧库迁入，随旧位置概念一并移除）。
    /// </summary>
    public partial class VersionBrowserWindow : Window
    {
        private sealed class FileRow
        {
            public string RelPath { get; set; } = "";
            public int VersionCount { get; set; }
            public long TotalSize { get; set; }
            public string TotalSizeText => Executor.FormatSize(TotalSize);
            public string LatestTs { get; set; } = "";
            public string LatestTsShort => FormatTs(LatestTs);
            public override string ToString() => RelPath;   // UIA 行名（屏幕阅读器/E2E 可定位）
        }

        private sealed class VersionRow
        {
            public int Id { get; set; }
            public string Ts { get; set; } = "";
            public long Size { get; set; }
            public string SizeText => Executor.FormatSize(Size);
            public int ChunkCount { get; set; }
            public DateTime MtimeUtc { get; set; }
            public string TsShort => FormatTs(Ts);
            public string MtimeShort => MtimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            public override string ToString() => TsShort;   // UIA 行名
        }

        private readonly SyncJob _job;
        private string CurrentSide => (CmbSide.SelectedIndex == 1 ? _job.RightPath : _job.LeftPath);

        public VersionBrowserWindow(SyncJob job)
        {
            _job = job;
            InitializeComponent();
            Title = $"版本库 — {job.Name}";
            CmbSide.Items.Add($"左侧：{job.LeftPath}");
            CmbSide.Items.Add($"右侧：{job.RightPath}");
            CmbSide.SelectedIndex = 0;
        }

        private static string FormatTs(string ts)
        {
            // ts 为 'o' 圆整格式（带时区），浏览器里显示本地秒级；InvariantCulture 防 region 设置改变解析行为
            return DateTime.TryParse(ts, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var t)
                ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : ts;
        }

        private void CmbSide_SelectionChanged(object sender, SelectionChangedEventArgs e) => Reload();

        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => Reload();

        // 载入代数：切侧/刷新连续触发时后台查询并发，只让最新一代写 UI（旧结果晚到直接丢）
        private int _reloadGen;

        /// <summary>列表/统计查询全部后台化：万级文件的库在 UI 线程查会卡住整个窗口。</summary>
        private async void Reload()
        {
            var gen = ++_reloadGen;
            var side = CurrentSide;
            var jobId = _job.Id;
            TxtStatus.Text = "载入中…";
            var (stats, files) = await Task.Run(() =>
            {
                if (!VersionStore.Exists(side)) return (null, new List<StoredFile>());
                var s = VersionStore.Stats(side);
                var list = VersionStore.ListFiles(side, jobId);
                return (s, list);
            });
            if (gen != _reloadGen) return;   // 已被更新的切侧/刷新取代
            TxtStats.Text = stats == null
                ? "（该侧尚无版本库）"
                : $"{stats.VersionCount} 版 · 逻辑 {Executor.FormatSize(stats.LogicalBytes)} · 落盘 {Executor.FormatSize(stats.StoredBytes)} · 省了 {stats.DedupRatio:P0}";
            var rows = files.Select(f => new FileRow
            {
                RelPath = f.RelPath,
                VersionCount = f.VersionCount,
                TotalSize = f.TotalSize,
                LatestTs = f.LatestTs
            }).ToList();
            LstFiles.ItemsSource = rows;
            LstVersions.ItemsSource = null;
            TxtVersionHeader.Text = "版本";
            TxtStatus.Text = rows.Count == 0
                ? "该侧暂无版本记录（开启任务「版本保留」且发生过覆盖/删除后生成）"
                : $"共 {rows.Count} 个文件的历史版本";
        }

        private async void LstFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LstFiles.SelectedItem is not FileRow f) return;
            var gen = _reloadGen;   // 切侧触发的 Reload 已重置列表，此趟结果作废
            var side = CurrentSide;
            var jobId = _job.Id;
            var versions = await Task.Run(() => VersionStore.ListVersions(side, jobId, f.RelPath));
            if (gen != _reloadGen) return;
            var rows = versions.Select(v => new VersionRow
            {
                Id = v.Id, Ts = v.Ts, Size = v.Size, ChunkCount = v.ChunkCount, MtimeUtc = v.MtimeUtc
            }).ToList();
            TxtVersionHeader.Text = $"版本 — {f.RelPath}（{rows.Count}）";
            LstVersions.ItemsSource = rows;
            TxtStatus.Text = "选择版本后点「还原到原位置」或「另存到…」；双击版本直接还原到原位置";
        }

        private void LstFiles_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (LstFiles.SelectedItem is FileRow f && VersionStore.ListVersions(CurrentSide, _job.Id, f.RelPath).Count > 0)
                RestoreLatest(f.RelPath);
        }

        private void LstVersions_DoubleClick(object sender, MouseButtonEventArgs e) => RestoreToOriginal();

        private (string relPath, VersionRow version)? Selection
        {
            get
            {
                if (LstFiles.SelectedItem is not FileRow f) return null;
                if (LstVersions.SelectedItem is not VersionRow v) return null;
                return (f.RelPath, v);
            }
        }

        private async void RestoreLatest(string relPath)
        {
            var versions = VersionStore.ListVersions(CurrentSide, _job.Id, relPath);
            if (versions.Count == 0) return;
            var latest = versions.OrderByDescending(v => v.Ts).ThenByDescending(v => v.Id).First();
            await RestoreAsync(relPath, latest.Id, Executor.FormatSize(latest.Size));
        }

        private async void RestoreToOriginal()
        {
            var sel = Selection;
            if (sel == null) return;
            await RestoreAsync(sel.Value.relPath, sel.Value.version.Id, sel.Value.version.SizeText);
        }

        private async Task RestoreAsync(string relPath, int versionId, string sizeText)
        {
            var side = CurrentSide;   // UI 线程捕获：Task.Run 里再读会跨线程访问 CmbSide 抛 InvalidOperation
            var dest = Path.Combine(side, relPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(dest))
            {
                var r = E2eSilent.Confirm(this,
                    $"目标已存在：\n{dest}\n\n用所选版本（{sizeText}）覆盖它？",
                    "还原到原位置");
                if (r != MessageBoxResult.Yes) return;
            }
            BtnRestore.IsEnabled = BtnSaveAs.IsEnabled = false;
            TxtStatus.Text = $"还原中… {relPath}";
            try
            {
                await Task.Run(() => VersionStore.RestoreVersion(side, versionId, dest));
                TxtStatus.Text = $"已还原: {dest}";
                E2eSilent.Info(this, $"已还原到原位置：\n{dest}", "版本库");
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"还原失败: {ex.Message}";
                E2eSilent.Alert(this, $"还原失败：{ex.Message}", "版本库");
            }
            finally
            {
                BtnRestore.IsEnabled = BtnSaveAs.IsEnabled = true;
            }
        }

        private void BtnRestore_Click(object sender, RoutedEventArgs e) => RestoreToOriginal();

        private async void BtnSaveAs_Click(object sender, RoutedEventArgs e)
        {
            var sel = Selection;
            if (sel == null) { TxtStatus.Text = "先在左侧选文件、右侧选版本"; return; }
            var relPath = sel.Value.relPath;
            var side = CurrentSide;   // UI 线程捕获（Task.Run 里不能读 CmbSide）

            // 系统原生文件夹选择器（同 JobEditWindow.Browse：按用户要求弃自建选择器）
            var dlg = new OpenFolderDialog
            {
                Title = "另存版本到…",
                InitialDirectory = Directory.Exists(side) ? side : null
            };
            if (dlg.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dlg.FolderName)) return;

            var dest = Path.Combine(dlg.FolderName, Path.GetFileName(relPath.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(dest))
            {
                var r = E2eSilent.Confirm(this, $"目标已存在：\n{dest}\n覆盖？", "另存到");
                if (r != MessageBoxResult.Yes) return;
            }
            TxtStatus.Text = $"另存中… {dest}";
            try
            {
                await Task.Run(() => VersionStore.RestoreVersion(side, sel.Value.version.Id, dest));
                TxtStatus.Text = $"已另存: {dest}";
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"另存失败: {ex.Message}";
                E2eSilent.Alert(this, $"另存失败：{ex.Message}", "版本库");
            }
        }

        private async void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            var sel = Selection;
            if (sel == null) { TxtStatus.Text = "先在左侧选文件、右侧选版本"; return; }
            var r = E2eSilent.Confirm(this,
                $"永久删除该版本（{FormatTs(sel.Value.version.Ts)}，{sel.Value.version.SizeText}）？\n删除后不可恢复（其余版本不受影响）。",
                "删除此版本");
            if (r != MessageBoxResult.Yes) return;
            var side = CurrentSide;   // UI 线程捕获
            TxtStatus.Text = "删除中…";
            try
            {
                await Task.Run(() => VersionStore.DeleteVersion(side, sel.Value.version.Id));
                TxtStatus.Text = "版本已删除（孤儿块已回收）";
                Reload();
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"删除失败: {ex.Message}";
            }
        }

        /// <summary>清空当前侧版本库（先浏览后清空：看到有什么再决定全删，语义与「删除此版本」同作用域）。</summary>
        private async void BtnClearAll_Click(object sender, RoutedEventArgs e)
        {
            var side = CurrentSide;   // UI 线程捕获
            var stats = await Task.Run(() => VersionStore.Stats(side));
            if (stats == null || stats.VersionCount == 0)
            {
                TxtStatus.Text = "当前侧没有可清空的版本";
                return;
            }
            var r = E2eSilent.Confirm(this,
                $"永久清空当前侧的版本库？\n{side}\n共 {stats.VersionCount} 个版本、{Executor.FormatSize(stats.StoredBytes)} 占用。\n（不影响两侧已同步的文件内容，删除后不可恢复）",
                "清空全部版本");
            if (r != MessageBoxResult.Yes) return;
            TxtStatus.Text = "清空中…";
            try
            {
                await Task.Run(() => VersionStore.ClearAll(side));
                TxtStatus.Text = "版本库已清空";
                Reload();
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"清空失败: {ex.Message}";
            }
        }
    }
}
