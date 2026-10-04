using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Core.Platform;

namespace FolderSync.Core
{
    /// <summary>扫描结果：条目表 + 完整性元信息。不完整的扫描禁止驱动镜像删除（空缺子树会被当成"已删除"误删目标侧）。</summary>
    public sealed class ScanResult
    {
        public required Dictionary<string, FileEntry> Entries { get; init; }
        /// <summary>根目录不存在（目标侧首建属正常；源侧缺失是数据丢失路径，由引擎入口挡）</summary>
        public bool RootMissing { get; init; }
        /// <summary>枚举失败的位置数（ACL 拒绝/IO 错误的子目录等）</summary>
        public int InaccessibleCount { get; init; }
        /// <summary>单侧内部「仅大小写不同」的实际文件名对（v1.5 B3）：NTFS 大小写不敏感不可能出现，
        /// 防御大小写敏感卷（WSL/SMB）；引擎据此产冲突行标人工，不静默归并。</summary>
        public List<(string firstName, string secondName)> CaseConflicts { get; init; } = new();
        public bool Complete => !RootMissing && InaccessibleCount == 0;
    }

    /// <summary>并行目录枚举器：按顶层子目录分片并行，20 万文件目标秒级。</summary>
    public static class Scanner
    {
        /// <summary>拷贝临时文件后缀（同目录 tmp → rename 原子替换的中转件）</summary>
        public const string TmpSuffix = ".foldersync-tmp";

        /// <summary>版本库目录名（旧位置 &lt;侧根&gt;\_FolderSync_Versions 的历史遗留名，防手工残留/外部回流被误同步）</summary>
        public const string VersionStoreDir = "_FolderSync_Versions";

        /// <summary>程序数据根（AppPaths.Root，含 foldersync.db/versions/sync-index/logs）与 exe 目录：
        /// 任务侧覆盖任一位置时整个子树排除——同步程序自身文件/数据无意义，且同步中写入的 db/块文件
        /// 会立刻成为新差异，形成永不收敛的自反馈循环。两个位置都查：正常数据在规范目录，
        /// 迁移失败回退时数据仍在程序目录。</summary>
        internal static bool IsUnderAppData(string fullPath) =>
            Under(fullPath, AppPaths.Root) || Under(fullPath, AppContext.BaseDirectory);

        private static bool Under(string fullPath, string root)
        {
            var sep = Path.DirectorySeparatorChar;
            var r = Path.GetFullPath(root).TrimEnd('\\', '/');
            return fullPath.StartsWith(r + sep, StringComparison.OrdinalIgnoreCase)
                || fullPath.Equals(r, StringComparison.OrdinalIgnoreCase);
        }

        private class ScanState
        {
            public long Counter;
            public int Inaccessible;
            public int DirsDone;        // 已完成枚举的文件夹数（真进度分子）
            public int DirsTotal;       // 预数的文件夹总数（真进度分母；预数失败=0 → UI 回退动画）
            public readonly object Lock = new();
            public ProgressInfo Pi = new() { Phase = "scan" };
            /// <summary>单侧内部仅大小写不同的实际名对（B3 防御：大小写敏感卷才可能，NTFS 恒空）</summary>
            public readonly System.Collections.Concurrent.ConcurrentQueue<(string, string)> CaseConflicts = new();
        }

        /// <summary>预数文件夹总数（真进度分母）：只枚举目录的快速遍历，跳过 reparse/排除规则，
        /// 与主扫描的目录集合口径一致（排除判定同样走 IsExcludedDirForPrune 安全子集）；
        /// 任何失败返回 0（UI 回退不确定动画，正确性优先）。</summary>
        private static int CountDirs(string root, string[] excludes, CancellationToken ct)
        {
            try
            {
                var opt = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.None,
                    ReturnSpecialDirectories = false
                };
                int n = 0;
                var stack = new Stack<string>();
                stack.Push(root);
                while (stack.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();   // 十万+目录的预数也要能被「停止」打断
                    var cur = stack.Pop();
                    n++;
                    try
                    {
                        foreach (var d in new DirectoryInfo(cur).EnumerateDirectories("*", opt))
                        {
                            if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;   // 与主扫描同口径：不递归
                            var rel = RelOf(root, d.FullName);
                            if (IsExcludedDirForPrune(rel, d.Name, excludes)) continue;
                            if (IsUnderAppData(d.FullName)) continue;
                            stack.Push(d.FullName);
                        }
                    }
                    catch { /* 单目录枚举失败：它本身还是会被主扫描处理，计数不缺 */ }
                }
                return n;
            }
            catch (OperationCanceledException) { throw; }
            catch { return 0; }
        }

        /// <summary>枚举一棵目录树。返回相对路径('/') → FileEntry（目录也包含在内）+ 完整性元信息。
        /// 字典按 OrdinalIgnoreCase 比较（与双向快照/基线口径一致：跨侧大小写不同时不算同一文件会重复+抖动）。
        /// 注意：junction/符号链接目录不参与同步（防把外部目录内容当成树内文件复制，双向模式下还有误删风险）。
        /// sideLabel: "left"/"right"，用于进度阶段区分（scan-left/scan-right）；空则为 "scan"。</summary>
        public static async Task<ScanResult> ScanTreeAsync(
            string root, string excludePatterns, IProgress<ProgressInfo>? progress,
            CancellationToken ct, string sideLabel = "")
        {
            root = Path.GetFullPath(root);
            var phase = string.IsNullOrEmpty(sideLabel) ? "scan" : $"scan-{sideLabel}";
            var sideName = sideLabel == "left" ? "左侧" : sideLabel == "right" ? "右侧" : "";
            if (!Directory.Exists(root))
            {
                // 首次同步目标侧往往不存在：视为空集（执行器会创建），不炸；
                // 但必须带 RootMissing 标记——源侧根瞬时不可用（盘未挂载/网络断开）时
                // 空集若配上 MirrorDelete 就是目标整盘删除，由引擎按完整性元信息拦截
                progress?.Report(new ProgressInfo { Phase = phase, DoneItems = 0, CurrentItem = $"{sideName}目录不存在，视为空: {root}" });
                return new ScanResult { Entries = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase), RootMissing = true };
            }

            var excludes = SplitPatterns(excludePatterns);
            // 必须与最终字典同比较器（OrdinalIgnoreCase）：默认 Ordinal 时同侧仅大小写不同的两个文件
            // （大小写敏感卷）成为两个键，TryGetValue 命中不了异写法 prev → CaseConflicts 恒空、
            // 复制进 OrdinalIgnoreCase 字典时同键互相覆盖 → 物理文件从扫描结果静默消失
            var result = new ConcurrentDictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
            var state = new ScanState { Pi = new ProgressInfo { Phase = phase } };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // 预数文件夹总数（一遍只枚举目录，快）→ 扫描阶段有真进度分母；
            // 预数中/失败（TotalItems=0）时 UI 回退不确定动画
            progress?.Report(new ProgressInfo { Phase = phase, CurrentItem = $"正在统计{sideName}文件夹数… {root}" }.Snapshot());
            state.DirsTotal = CountDirs(root, excludes, ct);
            progress?.Report(new ProgressInfo
            {
                Phase = phase,
                TotalItems = state.DirsTotal,
                CurrentItem = $"正在扫描{sideName}文件夹… {root}",
                ExtraInfo = $"共 {state.DirsTotal:N0} 个文件夹"
            }.Snapshot());

            List<string> topLevelDirs;
            try { topLevelDirs = Directory.EnumerateDirectories(root).ToList(); }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                // 根下子目录枚举整体失败：该层信息缺失，计入不完整（顶层文件还可能枚举得到）
                Interlocked.Increment(ref state.Inaccessible);
                topLevelDirs = new List<string>();
            }

            // 根下直属文件先收
            try
            {
                foreach (var f in EnumerateSafe(root))
                {
                    ct.ThrowIfCancellationRequested();
                    AddEntry(result, root, f, excludes, state);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            { Interlocked.Increment(ref state.Inaccessible); }
            Interlocked.Increment(ref state.DirsDone);   // 根目录本身枚举完成

            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount * 2, Math.Max(topLevelDirs.Count, 1)),
                CancellationToken = ct
            };

            await Task.Run(() => Parallel.ForEach(topLevelDirs, options, topDir =>
            {
                try
                {
                    ScanSubtree(root, topDir, excludes, result, progress, state, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (UnauthorizedAccessException) { Interlocked.Increment(ref state.Inaccessible); }
                catch (IOException) { Interlocked.Increment(ref state.Inaccessible); }
            })).ConfigureAwait(false);

            // 完成事件：强制收敛到 100%（中途跳过的目录在 ScanSubtree 里也计数，理论已齐；兜底拉满）
            state.Pi.DoneItems = state.DirsTotal > 0 ? state.DirsTotal : Volatile.Read(ref state.DirsDone);
            state.Pi.TotalItems = state.DirsTotal;
            state.Pi.ExtraInfo = $"共 {state.Counter:N0} 项";
            state.Pi.CurrentItem = $"{sideName}扫描完成，共 {state.Counter} 项，耗时 {sw.Elapsed.TotalSeconds:F1}s"
                + (Volatile.Read(ref state.Inaccessible) > 0 ? $" ⚠ {Volatile.Read(ref state.Inaccessible)} 个位置无法访问" : "");
            progress?.Report(state.Pi.Snapshot());
            return new ScanResult
            {
                Entries = new Dictionary<string, FileEntry>(result, StringComparer.OrdinalIgnoreCase),
                InaccessibleCount = Volatile.Read(ref state.Inaccessible),
                CaseConflicts = state.CaseConflicts.ToList()
            };
        }

        private static string RelOf(string root, string fullPath) =>
            fullPath[root.Length..].TrimStart('\\', '/').Replace('\\', '/');

        /// <summary>目录级排除判定（剪枝安全子集）：只认「目录名/路径段精确匹配」的规则。
        /// 通配规则不参与剪枝——它对子文件的匹配走完整 rel（* 可跨段），目录命中不代表子树全排除。</summary>
        private static bool IsExcludedDirForPrune(string relativePath, string name, string[] excludes)
        {
            if (name.Equals(VersionStoreDir, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var seg in relativePath.Split('/'))
                if (seg.Equals(VersionStoreDir, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var p in excludes)
            {
                var pat = p.Replace('\\', '/').TrimEnd('/');
                if (pat.Length == 0 || pat.Contains('*') || pat.Contains('?')) continue;
                if (name.Equals(pat, StringComparison.OrdinalIgnoreCase)) return true;
                foreach (var seg in relativePath.Split('/'))
                    if (seg.Equals(pat, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static void ScanSubtree(string root, string dir, string[] excludes,
            ConcurrentDictionary<string, FileEntry> result, IProgress<ProgressInfo>? progress,
            ScanState state, CancellationToken ct)
        {
            // 顶层分片入口：排除目录整树跳过。与 CountDirs 同口径（被排除的不计入分母，这里不计数分子）；
            // 旧代码只跳 VersionStoreDir 且多计一次 DirsDone（预数并未把它算进总数）
            if (IsExcludedDirForPrune(RelOf(root, dir), Path.GetFileName(dir.TrimEnd('\\', '/')), excludes))
                return;
            var stack = new Stack<string>();
            stack.Push(dir);
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var current = stack.Pop();
                try
                {
                    foreach (var f in EnumerateSafe(current))
                    {
                        // junction/symlink（目录与文件同口径）：不入结果、不下递归
                        // （防环 + 防把外部内容当实体副本同步进来；硬链接无 ReparsePoint 不受影响）
                        if (((FileAttributes)f.Attrs & FileAttributes.ReparsePoint) != 0) continue;
                        AddEntry(result, root, f, excludes, state);
                        // 排除目录剪枝（安全子集判定）：旧代码排除目录照常递归——node_modules/AppData
                        // 大树白枚举，且 CountDirs 跳过它们 → DirsDone 可超 DirsTotal、进度超 100%。
                        // 精确段匹配的子文件 rel 必含该段，本来也会被 IsExcluded 挡掉：剪枝不改变结果集
                        if (f.IsDir && !IsExcludedDirForPrune(RelOf(root, f.FullPath), f.Name, excludes))
                            stack.Push(f.FullPath);
                        if ((state.Counter & 0x3FF) == 0)
                        {
                            state.Pi.DoneItems = Volatile.Read(ref state.DirsDone);
                            state.Pi.TotalItems = state.DirsTotal;
                            state.Pi.ExtraInfo = $"已发现 {Volatile.Read(ref state.Counter):N0} 项";
                            state.Pi.CurrentItem = f.FullPath;
                            progress?.Report(state.Pi.Snapshot());
                        }
                    }
                }
                catch (UnauthorizedAccessException) { Interlocked.Increment(ref state.Inaccessible); }
                catch (IOException) { Interlocked.Increment(ref state.Inaccessible); }
                Interlocked.Increment(ref state.DirsDone);   // 该目录枚举完成（含失败——工作量角度算做完）
            }
        }

        /// <summary>扫描条目（枚举产物，属性名与旧元组一致）。</summary>
        internal readonly record struct ScanItem(string FullPath, string Name, bool IsDir, long Size, DateTime MtimeUtc, uint Attrs);

        /// <summary>枚举一层目录。
        /// IgnoreInaccessible 必须显式 false（曾为 true）：true 时 BCL 对 ACL 拒绝的目录
        /// 静默返回空集合、不抛任何异常（2026-09-15 tmp/acl-probe 实测；Windows 实现里
        /// (ACCESS_DENIED &amp;&amp; IgnoreInaccessible) 与回调是 || 短路，派生类也拦不到），
        /// ScanSubtree/ScanTreeAsync 的 catch 永不触发 → InaccessibleCount 恒 0 →
        /// ScanResult.Complete 恒 true → S-1 镜像删除安全阀第一道闸被绕过，被挡子树被误判"已删除"。
        /// false 让错误抛给现有 catch 计数，S-1 恢复工作。</summary>
        private static IEnumerable<ScanItem> EnumerateSafe(string dir)
        {
            var opt = new EnumerationOptions
            {
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.None,
                ReturnSpecialDirectories = false
            };
            foreach (var fsi in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opt))
            {
                if (fsi is FileInfo fi)
                    yield return new ScanItem(fi.FullName, fi.Name, false, fi.Length, fi.LastWriteTimeUtc, (uint)fi.Attributes);
                else
                    yield return new ScanItem(fsi.FullName, fsi.Name, true, 0, fsi.LastWriteTimeUtc, (uint)fsi.Attributes);
            }
        }

        private static void AddEntry(ConcurrentDictionary<string, FileEntry> result, string root,
            ScanItem f, string[] excludes, ScanState state)
        {
            // 程序自身数据目录（exe 所在目录）整个子树不同步：db/版本库/日志边写边成为新差异，自反馈永不收敛
            if (IsUnderAppData(f.FullPath)) return;
            var rel = f.FullPath[root.Length..].TrimStart('\\', '/').Replace('\\', '/');
            if (IsExcluded(rel, f.Name, excludes)) return;
            // B3：同侧「仅大小写不同」的实际名不静默归并（NTFS 不可能，防御大小写敏感卷；
            // 忽略大小写字典仍只留后到的一条——物理上两个文件都在，冲突行由引擎按此清单补标）
            if (result.TryGetValue(rel, out var prev) &&
                !string.Equals(prev.RelativePath, rel, StringComparison.Ordinal))
                state.CaseConflicts.Enqueue((prev.RelativePath, rel));
            result[rel] = new FileEntry
            {
                RelativePath = rel,
                IsDirectory = f.IsDir,
                Size = f.Size,
                MtimeUtc = f.MtimeUtc,
                Attributes = f.Attrs
            };
            lock (state.Lock)
            {
                state.Counter++;
                // 总量未知：TotalItems 保持 0，UI 用不确定进度条 + DoneItems 显示已发现数
            }
        }

        public static string[] SplitPatterns(string patterns) =>
            (patterns ?? "").Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();

        /// <summary>匹配排除规则：支持通配符和路径段名（如 .git、Thumbs.db）。
        /// 内部机制文件硬编码排除（不依赖用户规则）：*.foldersync-tmp 拷贝中转件、_FolderSync_Versions 版本库。</summary>
        public static bool IsExcluded(string relativePath, string name, string[] excludes)
        {
            if (name.EndsWith(TmpSuffix, StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Equals(VersionStoreDir, StringComparison.OrdinalIgnoreCase)) return true;
            // 段数组只切一次：旧写法在每条规则的循环体内 Split，20 万文件 × N 规则重复分配上百万次
            var segs = relativePath.Split('/');
            foreach (var seg in segs)
                if (seg.Equals(VersionStoreDir, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var p in excludes)
            {
                var pat = p.Replace('\\', '/').TrimEnd('/');
                if (pat.Length == 0) continue;
                if (pat.Contains('*') || pat.Contains('?'))
                {
                    if (MatchesPattern(name, pat) || MatchesPattern(relativePath, pat)) return true;
                }
                else
                {
                    if (name.Equals(pat, StringComparison.OrdinalIgnoreCase)) return true;
                    foreach (var seg in segs)
                        if (seg.Equals(pat, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }

        private static bool MatchesPattern(string text, string pattern) =>
            LikeMatch(text, pattern, 0, 0);

        private static bool LikeMatch(string text, string pattern, int ti, int pi)
        {
            while (pi < pattern.Length)
            {
                var c = pattern[pi];
                if (c == '*')
                {
                    for (var k = ti; k <= text.Length; k++)
                        if (LikeMatch(text, pattern, k, pi + 1)) return true;
                    return false;
                }
                if (ti >= text.Length) return false;
                if (c != '?' && char.ToLowerInvariant(c) != char.ToLowerInvariant(text[ti])) return false;
                ti++; pi++;
            }
            return ti == text.Length;
        }
    }
}
