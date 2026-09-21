using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderSync.Core
{
    /// <summary>
    /// 移动/重命名检测（v1.5 A3）：Differ 产计划后的后处理趟。
    /// 把「删除 X + 新建 Y」（物理动作同侧）中快照可证 size/mtime 匹配的配对识别为移动，
    /// 替换成一条 Move 动作——执行层改做目标侧内部 rename（同根必同卷，瞬时），
    /// 代替「重拷整文件 + 删除」，大文件挪目录场景秒级完成且版本库零增长。
    ///
    /// 配对依据：上一轮快照记录的 X 与本轮新建 Y 满足 size 完全相等 && mtime 差 ≤ 既有容差
    /// （rename 不改 size/mtime；移动后又改过内容 → mtime 漂移 → 不配对，走正常复制）。
    /// 触发前提：MirrorDelete 开启（关闭时 Differ 不产删除动作，配对无从发生；
    /// 且目标旧文件按语义保留，替换成 rename 等于隐式删除，不做）。
    /// 多候选歧义（同 size 同 mtime 的删除/新建数不相等）→ 整组放弃配对回退原计划，绝不猜。
    /// </summary>
    public static class MoveDetection
    {
        /// <summary>参与配对的最小文件阈值：小于此值重拷代价低，不值得冒误配风险。</summary>
        public const long MoveMinBytes = 1L * 1024 * 1024;   // 1 MiB

        /// <summary>配对的头部内容佐证（C-1）：删除动作的物理侧副本（此刻仍在盘上，执行才删）
        /// 与新建动作的内容来源（CreateXxx 的源在对侧磁盘）各读头 min(size,64KB) 比对。
        /// 读失败（外部竞态已删/占用/来源消失）按不匹配处理。</summary>
        private static bool HeadContentMatches(SyncJob job, bool physicalSideRight,
            (PlanEntry entry, FileEntry snap) del, PlanEntry create)
        {
            var delPath = Path.Combine(
                physicalSideRight ? job.RightPath : job.LeftPath,
                del.entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var createPath = Path.Combine(
                physicalSideRight ? job.LeftPath : job.RightPath,
                create.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var head = (int)Math.Min(Math.Min(del.snap.Size, 64L * 1024), int.MaxValue);
            if (head == 0) return true;   // 空/极小文件：size 相等本身就是完整佐证
            try
            {
                Span<byte> a = stackalloc byte[head];
                Span<byte> b = stackalloc byte[head];
                return ReadHead(delPath, a) && ReadHead(createPath, b) && a.SequenceEqual(b);
            }
            catch { return false; }
        }

        private static bool ReadHead(string path, Span<byte> buf)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            int filled = 0;
            while (filled < buf.Length)
            {
                var n = fs.Read(buf[filled..]);
                if (n <= 0) return false;   // 文件比声称的 size 短：内容状态不可信，不配对
                filled += n;
            }
            return true;
        }

        /// <summary>对计划应用移动检测。返回新计划（原列表不变）；无可配对时返回原列表引用。
        /// left/right 为本轮扫描结果（新建条目的源侧 mtime 从这里取——PlanEntry 的展示 mtime
        /// 已本地化，与快照的 UTC 比较会差时区）。</summary>
        public static List<PlanEntry> Apply(SyncJob job, List<PlanEntry> plan,
            Dictionary<string, FileEntry> snapshot,
            Dictionary<string, FileEntry> left, Dictionary<string, FileEntry> right,
            long mtimeTolMs)
        {
            if (!job.MoveDetect || !job.MirrorDelete || plan.Count == 0 || snapshot.Count == 0)
                return plan;

            // 候选收集：删除动作（有快照记录且 ≥ 阈值的文件）与新建动作（≥ 阈值的文件），按物理侧分桶
            // （动作后缀即物理侧：DeleteRight/CreateRight=右，DeleteLeft/CreateLeft=左）
            var dels = new List<(PlanEntry entry, FileEntry snap)>();
            var creates = new List<PlanEntry>();
            foreach (var p in plan)
            {
                if (p.IsDirectory) continue;
                switch (p.Action)
                {
                    case SyncAction.DeleteRight:
                    case SyncAction.DeleteLeft:
                        // 源侧已删，快照是上一轮它还在时的画像（size/mtime 的可信来源）
                        if (snapshot.TryGetValue(p.RelativePath, out var snap) && !snap.IsDirectory
                            && snap.Size >= MoveMinBytes)
                            dels.Add((p, snap));
                        break;
                    case SyncAction.CreateRight:
                    case SyncAction.CreateLeft:
                        if (p.Size >= MoveMinBytes) creates.Add(p);
                        break;
                }
            }
            if (dels.Count == 0 || creates.Count == 0) return plan;

            DateTime CreateUtcMtime(PlanEntry c) =>
                (c.Action == SyncAction.CreateRight ? left : right).TryGetValue(c.RelativePath, out var e)
                    ? e.MtimeUtc : DateTime.MinValue;

            // 分组配对：物理侧 + size 全等 → 组内 mtime 一一对应（排序后相邻配对，差 ≤ 容差）。
            // 组内删除数 ≠ 新建数 = 歧义（同 size 同 mtime 多对一），整组放弃回退原计划。
            var pairs = new List<(PlanEntry del, PlanEntry create, FileEntry snap)>();
            foreach (var sideGroup in dels.GroupBy(d => d.entry.Action == SyncAction.DeleteRight))
            {
                var onRight = sideGroup.Key;
                var sideCreates = creates.Where(c => (c.Action == SyncAction.CreateRight) == onRight).ToList();
                if (sideCreates.Count == 0) continue;

                foreach (var sizeGroup in sideGroup.GroupBy(d => d.snap.Size))
                {
                    var sizeDels = sizeGroup.OrderBy(d => d.snap.MtimeUtc).ToList();
                    var sizeCreates = sideCreates.Where(c => c.Size == sizeGroup.Key)
                        .OrderBy(c => CreateUtcMtime(c)).ToList();
                    if (sizeDels.Count != sizeCreates.Count) continue;   // 歧义：整组放弃，绝不猜

                    bool allMatch = true;
                    for (int i = 0; i < sizeDels.Count; i++)
                    {
                        if (Math.Abs((sizeDels[i].snap.MtimeUtc - CreateUtcMtime(sizeCreates[i])).TotalMilliseconds) > mtimeTolMs)
                        { allMatch = false; break; }   // 组内任一对超容差（移动后内容又改过）：整组放弃
                        // C-1 内容佐证：size+mtime 配对可被「同 size 同 mtime 的删 X + 建异内容 Y」冒充
                        // （备份工具/git 迁移保 mtime 现实存在），rename 一旦执行幸存文件被改成异内容，
                        // 此后 size/mtime 恒等 Changed 永假 → 永久静默分歧。此刻删除源仍在物理侧盘上
                        // （计划未执行），比对双方头 64KB 哈希；不等/读失败整组放弃回退原计划（重拷，安全）
                        if (!HeadContentMatches(job, onRight, sizeDels[i], sizeCreates[i]))
                        { allMatch = false; break; }
                    }
                    if (allMatch)
                        for (int i = 0; i < sizeDels.Count; i++)
                            pairs.Add((sizeDels[i].entry, sizeCreates[i], sizeDels[i].snap));
                }
            }
            if (pairs.Count == 0) return plan;

            // 替换：从计划移除配对的删除+新建两条，加一条 Move（保留原条目的双侧元信息便于 UI 展示）
            var replaced = new HashSet<PlanEntry>(ReferenceEqualityComparer.Instance);
            foreach (var (del, create, _) in pairs) { replaced.Add(del); replaced.Add(create); }
            var result = new List<PlanEntry>(plan.Count - pairs.Count);
            foreach (var p in plan)
            {
                if (replaced.Contains(p)) continue;
                result.Add(p);
            }
            foreach (var (del, create, snap) in pairs)
            {
                create.Action = SyncAction.Move;
                create.FromPath = del.RelativePath;
                create.MoveOnRight = del.Action == SyncAction.DeleteRight;   // 与新建动作同物理侧（配对前提）
                create.Note = $"移动: {del.RelativePath} → {create.RelativePath}";
                create.LeftSize = del.LeftSize;
                create.RightSize = del.RightSize;
                create.LeftMtime = del.LeftMtime;
                create.RightMtime = del.RightMtime;
                result.Add(create);
            }
            return result.OrderBy(p => p.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>引用相等比较器（配对替换需按条目对象身份移除，同路径不同对象不能误伤）。</summary>
    internal sealed class ReferenceEqualityComparer : IEqualityComparer<PlanEntry>
    {
        public static readonly ReferenceEqualityComparer Instance = new();
        public bool Equals(PlanEntry? a, PlanEntry? b) => ReferenceEquals(a, b);
        public int GetHashCode(PlanEntry obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
