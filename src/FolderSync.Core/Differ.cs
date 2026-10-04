using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FolderSync.Core
{
    /// <summary>差异计算：两边扫描结果 → 动作计划。</summary>
    public static class Differ
    {
        /// <summary>
        /// 计算差异。语义：源=发射方，目标=接收方（按 job.Direction 决定）。
        /// 源有目标无 → Create 目标侧；两边都有且不同 → 默认较新覆盖较旧（新者胜），
        /// StrictMirror 开时一律源覆盖目标（严格镜像）；目标孤儿 → 仅 MirrorDelete 时删除（默认关，安全）。
        /// allowMirrorDelete=false 时引擎侧安全阀生效：扫描不完整/删除比例超阈值时禁用本轮删除。</summary>
        public static List<PlanEntry> Compute(SyncJob job, Dictionary<string, FileEntry> left,
            Dictionary<string, FileEntry> right, bool allowMirrorDelete = true)
        {
            var plan = new List<PlanEntry>();
            bool l2r = job.Direction.ToRight();
            bool backup = job.Direction.IsBackup();   // 纯备份：目标较新不回写源，分不出新旧不动
            var (src, dst) = l2r ? (left, right) : (right, left);
            long tol = MtimeToleranceMs(job.LeftPath, job.RightPath);

            foreach (var (rel, sEntry) in src)
            {
                if (dst.TryGetValue(rel, out var dEntry))
                {
                    if (CaseDiffers(sEntry, dEntry))
                    {
                        // B3 跨侧仅大小写不同（左 README.md vs 右 readme.md）：Windows 文件名大小写
                        // 不敏感但保留——自动覆盖会静默改写对侧文件名且内容判定无差异时更会漏检，标人工裁决
                        plan.Add(Mk(SyncAction.Conflict, rel, sEntry, left, right,
                            $"仅大小写不同：{sEntry.RelativePath} vs {dEntry.RelativePath}，请选择保留哪侧写法"));
                    }
                    else if (sEntry.IsDirectory != dEntry.IsDirectory)
                    {
                        // 类型冲突（一边文件一边目录）：同一相对路径产出删除+重建两条动作，
                        // 执行器会把这种删除提前到新建之前（否则新建会撞上异类型目标报错）。
                        // 备份方向同样适用（删的是目标侧，不违反"永不删源"）
                        plan.Add(Mk(DeleteDst(l2r), rel, dEntry, left, right, "类型冲突：先删除目标侧"));
                        plan.Add(Mk(CreateDst(l2r), rel, sEntry, left, right, "类型冲突：按源侧类型重建"));
                    }
                    else if (!sEntry.IsDirectory && Changed(sEntry, dEntry, tol))
                    {
                        if (backup)
                        {
                            // 纯备份：源新才覆盖；目标新/同 mtime 内容不同都绝不回写源，记跳过行说明
                            if (sEntry.MtimeUtc > dEntry.MtimeUtc)
                                plan.Add(Mk(UpdateDst(l2r), rel, sEntry, left, right, "备份：源较新 → 覆盖目标"));
                            else
                                plan.Add(Mk(SyncAction.None, rel, sEntry, left, right,
                                    dEntry.MtimeUtc > sEntry.MtimeUtc ? "目标较新，备份方向不回写" : "修改时间相同但内容不同，备份方向不动"));
                        }
                        else if (job.StrictMirror)
                            plan.Add(Mk(UpdateDst(l2r), rel, sEntry, left, right, "严格镜像：源覆盖目标"));
                        else if (sEntry.MtimeUtc > dEntry.MtimeUtc)
                            plan.Add(Mk(UpdateDst(l2r), rel, sEntry, left, right));
                        else if (dEntry.MtimeUtc > sEntry.MtimeUtc)
                            plan.Add(Mk(UpdateSrc(l2r), rel, dEntry, left, right));
                        else
                            plan.Add(Mk(SyncAction.Conflict, rel, sEntry, left, right));
                    }
                }
                else
                {
                    plan.Add(Mk(CreateDst(l2r), rel, sEntry, left, right));
                }
            }

            if (job.MirrorDelete && allowMirrorDelete)
            {
                foreach (var (rel, dEntry) in dst)
                {
                    if (!src.ContainsKey(rel))
                        plan.Add(Mk(DeleteDst(l2r), rel, dEntry, left, right));
                }
            }

            return plan.OrderBy(p => p.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static SyncAction CreateDst(bool l2r) => l2r ? SyncAction.CreateRight : SyncAction.CreateLeft;
        private static SyncAction UpdateDst(bool l2r) => l2r ? SyncAction.UpdateRight : SyncAction.UpdateLeft;
        private static SyncAction UpdateSrc(bool l2r) => l2r ? SyncAction.UpdateLeft : SyncAction.UpdateRight;
        private static SyncAction DeleteDst(bool l2r) => l2r ? SyncAction.DeleteRight : SyncAction.DeleteLeft;

        /// <summary>B3：两条目忽略大小写同键但实际名不同（跨侧大小写不一致）。</summary>
        private static bool CaseDiffers(FileEntry a, FileEntry b) =>
            !string.Equals(a.RelativePath, b.RelativePath, StringComparison.Ordinal);

        /// <summary>
        /// mtime 比较容差：任一侧在 FAT/FAT32/exFAT 卷上时放宽到 2.1s（FAT 写入时间粒度 2 秒，
        /// exFAT 的粒度字段同样可达 2s，50ms 容差会让复制完的文件每轮都被判"已变更"重拷）；NTFS 用 50ms。
        /// </summary>
        public static long MtimeToleranceMs(string pathA, string pathB)
        {
            foreach (var p in new[] { pathA, pathB })
            {
                try
                {
                    var root = Path.GetPathRoot(Path.GetFullPath(p));
                    if (root == null) continue;
                    var fmt = new DriveInfo(root).DriveFormat;   // 网络路径会抛，落到默认
                    if (fmt.StartsWith("FAT", StringComparison.OrdinalIgnoreCase)
                        || fmt.Equals("exFAT", StringComparison.OrdinalIgnoreCase)) return 2100;
                }
                catch { }
            }
            return 50;
        }

        private static bool Changed(FileEntry a, FileEntry b, long tolMs)
        {
            if (a.Size != b.Size) return true;
            // 容差见 MtimeToleranceMs；更宽容差会把快速连续编辑误判为无变化，只在 FAT 上放宽
            var diff = Math.Abs((a.MtimeUtc - b.MtimeUtc).TotalMilliseconds);
            return diff > tolMs;
        }

        private static PlanEntry Mk(SyncAction action, string rel, FileEntry meta,
            Dictionary<string, FileEntry> left, Dictionary<string, FileEntry> right, string note = "")
        {
            left.TryGetValue(rel, out var l);
            right.TryGetValue(rel, out var r);
            return new PlanEntry
            {
                Action = action,
                RelativePath = rel,
                IsDirectory = meta.IsDirectory,
                Size = meta.Size,
                LeftSize = l?.Size ?? -1,
                RightSize = r?.Size ?? -1,
                LeftMtime = l?.MtimeUtc.ToLocalTime(),
                RightMtime = r?.MtimeUtc.ToLocalTime(),
                Note = note
            };
        }

        /// <summary>汇总统计（moves=移动检测命中数，v1.5）</summary>
        public static (int creates, int updates, int deletes, int moves, long bytes) Summarize(List<PlanEntry> plan)
        {
            int c = 0, u = 0, d = 0, m = 0; long b = 0;
            foreach (var e in plan)
            {
                switch (e.Action)
                {
                    case SyncAction.CreateRight: case SyncAction.CreateLeft: c++; b += e.Size; break;
                    case SyncAction.UpdateRight: case SyncAction.UpdateLeft: u++; b += e.Size; break;
                    case SyncAction.DeleteRight: case SyncAction.DeleteLeft: d++; break;
                    case SyncAction.Move: m++; break;
                }
            }
            return (c, u, d, m, b);
        }

        // ==================== 双向同步（三方对比：左 × 右 × 快照） ====================

        /// <summary>
        /// 双向差异。两侧平等，靠快照区分「删除」与「新建」：
        /// 单侧存在 + 快照无 → 视为新建（复制过去，绝不删）；
        /// 单侧存在 + 快照有 → 该侧删除过，MirrorDelete 开启才传播删除，否则复制回来；
        /// 两侧都有且不同 → 冲突，按 job.ConflictPolicy 裁决（Manual 则标记挂起）。
        /// allowMirrorDelete：引擎侧安全阀（扫描不完整时禁用删除传播，防 ACL 挡住的子树被误判"已删除"）。
        /// </summary>
        public static List<PlanEntry> ComputeTwoWay(SyncJob job, Dictionary<string, FileEntry> left,
            Dictionary<string, FileEntry> right, Dictionary<string, FileEntry> snapshot,
            bool allowMirrorDelete = true)
        {
            var plan = new List<PlanEntry>();
            long tol = MtimeToleranceMs(job.LeftPath, job.RightPath);
            var allPaths = new SortedSet<string>(left.Keys.Concat(right.Keys).Concat(snapshot.Keys),
                StringComparer.OrdinalIgnoreCase);

            // S-1：目录级删除传播延后判定（见方法尾），两侧对称收集
            var pendingDirDeletes = new List<(SyncAction action, string rel, FileEntry entry, string note)>();

            foreach (var rel in allPaths)
            {
                left.TryGetValue(rel, out var lEntry);   // null = 该侧不存在
                right.TryGetValue(rel, out var rEntry);
                snapshot.TryGetValue(rel, out var snap);

                if (lEntry != null && rEntry == null)
                {
                    if (snap != null)
                    {
                        // 快照里有、右侧现在没有 → 右侧删除过
                        if (!lEntry.IsDirectory && Changed(lEntry, snap, tol))
                        {
                            // 但左侧内容比快照新 → 删除后左侧又修改过，修改优先，恢复到右侧
                            plan.Add(Mk(SyncAction.CreateRight, rel, lEntry, left, right, "右侧已删除，但左侧有更新 → 恢复到右侧"));
                        }
                        else if (lEntry.IsDirectory && job.MirrorDelete && allowMirrorDelete)
                            pendingDirDeletes.Add((SyncAction.DeleteLeft, rel, lEntry, "右侧已删除（快照确认）→ 传播删除"));
                        else if (job.MirrorDelete && allowMirrorDelete)
                            plan.Add(Mk(SyncAction.DeleteLeft, rel, lEntry, left, right, "右侧已删除（快照确认）→ 传播删除"));
                        else
                            plan.Add(Mk(SyncAction.None, rel, lEntry, left, right, "右侧已删除，删除传播未开启 → 保留左侧副本"));
                    }
                    else
                        plan.Add(Mk(SyncAction.CreateRight, rel, lEntry, left, right));
                }
                else if (lEntry == null && rEntry != null)
                {
                    if (snap != null)
                    {
                        // 快照里有、左侧现在没有 → 左侧删除过
                        if (!rEntry.IsDirectory && Changed(rEntry, snap, tol))
                        {
                            plan.Add(Mk(SyncAction.CreateLeft, rel, rEntry, left, right, "左侧已删除，但右侧有更新 → 恢复到左侧"));
                        }
                        else if (rEntry.IsDirectory && job.MirrorDelete && allowMirrorDelete)
                            pendingDirDeletes.Add((SyncAction.DeleteRight, rel, rEntry, "左侧已删除（快照确认）→ 传播删除"));
                        else if (job.MirrorDelete && allowMirrorDelete)
                            plan.Add(Mk(SyncAction.DeleteRight, rel, rEntry, left, right, "左侧已删除（快照确认）→ 传播删除"));
                        else
                            plan.Add(Mk(SyncAction.None, rel, rEntry, left, right, "左侧已删除，删除传播未开启 → 保留右侧副本"));
                    }
                    else
                        plan.Add(Mk(SyncAction.CreateLeft, rel, rEntry, left, right));
                }
                else if (lEntry != null && rEntry != null)
                {
                    if (CaseDiffers(lEntry, rEntry))
                    {
                        // B3 跨侧仅大小写不同：标人工（内容判定无差异时旧逻辑会静默跳过，文件名差异永远不被纠正）
                        plan.Add(Mk(SyncAction.Conflict, rel, lEntry, left, right,
                            $"仅大小写不同：{lEntry.RelativePath} vs {rEntry.RelativePath}，请选择保留哪侧写法"));
                    }
                    else if (lEntry.IsDirectory != rEntry.IsDirectory)
                    {
                        // 类型冲突（一边文件一边目录）：双向不猜，标记人工
                        plan.Add(Mk(SyncAction.Conflict, rel, lEntry, left, right, "类型冲突（文件/目录）"));
                    }
                    else if (!lEntry.IsDirectory && Changed(lEntry, rEntry, tol))
                    {
                        ResolveConflict(job, plan, rel, lEntry, rEntry, left, right);
                    }
                    // 目录对目录或内容一致 → 无动作
                }
                else // 两边都无（快照里有）→ 双删收敛，无动作
                {
                }
            }

            // S-1：目录删除传播 vs 目录内新建的绞杀防线。右侧删了目录 D、左侧又在 D 内新建了
            // new.txt（快照无此文件 → 本轮计划 CreateRight(D/new.txt)）：执行三趟会先把 new.txt
            // 复制到右侧，随后 postDelete 趟递归 DeleteLeft(D) 连左侧原件一起删掉；下一轮
            // new.txt 只剩右侧、快照有、size/mtime 一致 → 判「左侧删除过」再传播 DeleteRight
            // → 两侧全灭且无任何冲突提示。目录之下存在本轮 Create 动作时整条降级为 Conflict
            // 人工裁决，绝不自动传播。
            foreach (var d in pendingDirDeletes)
            {
                var prefix = d.rel + "/";
                bool hasCreateUnder = plan.Any(p =>
                    (p.Action == SyncAction.CreateLeft || p.Action == SyncAction.CreateRight)
                    && p.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                if (hasCreateUnder)
                    plan.Add(Mk(SyncAction.Conflict, d.rel, d.entry, left, right,
                        "对侧已删除该目录，但本侧目录内有新建文件——自动传播会连同新建一起删除（两轮后两侧全灭），请人工裁决：确认要删请手动删除该目录"));
                else
                    plan.Add(Mk(d.action, d.rel, d.entry, left, right, d.note));
            }

            // allPaths 是 SortedSet(OrdinalIgnoreCase)，遍历序即最终序——无需再 OrderBy
            return plan.ToList();
        }

        private static void ResolveConflict(SyncJob job, List<PlanEntry> plan, string rel,
            FileEntry lEntry, FileEntry rEntry,
            Dictionary<string, FileEntry> left, Dictionary<string, FileEntry> right)
        {
            switch (job.ConflictPolicy)
            {
                case ConflictPolicy.NewestMtime:
                    if (lEntry.MtimeUtc > rEntry.MtimeUtc)
                        plan.Add(Mk(SyncAction.UpdateRight, rel, lEntry, left, right, "时间优先：左新"));
                    else if (rEntry.MtimeUtc > lEntry.MtimeUtc)
                        plan.Add(Mk(SyncAction.UpdateLeft, rel, rEntry, left, right, "时间优先：右新"));
                    else
                        plan.Add(Mk(SyncAction.Conflict, rel, lEntry, left, right, "修改时间相同但内容不同"));
                    break;

                case ConflictPolicy.LargestSize:
                    if (lEntry.Size > rEntry.Size)
                        plan.Add(Mk(SyncAction.UpdateRight, rel, lEntry, left, right, "大小优先：左大"));
                    else if (rEntry.Size > lEntry.Size)
                        plan.Add(Mk(SyncAction.UpdateLeft, rel, rEntry, left, right, "大小优先：右大"));
                    else
                        plan.Add(Mk(SyncAction.Conflict, rel, lEntry, left, right, "大小相同但内容不同"));
                    break;

                case ConflictPolicy.Manual:
                    plan.Add(Mk(SyncAction.Conflict, rel, lEntry, left, right, "等待人工裁决"));
                    break;

                case ConflictPolicy.ConflictCopy:
                    // D1 冲突副本：胜者照时间覆盖，败者由执行器改名 .sync-conflict-<时间戳> 留在原目录（替代版本归档）
                    if (lEntry.MtimeUtc > rEntry.MtimeUtc)
                        plan.Add(Mk(SyncAction.UpdateRight, rel, lEntry, left, right, "冲突副本：左新胜出，败者改名保留"));
                    else if (rEntry.MtimeUtc > lEntry.MtimeUtc)
                        plan.Add(Mk(SyncAction.UpdateLeft, rel, rEntry, left, right, "冲突副本：右新胜出，败者改名保留"));
                    else
                        plan.Add(Mk(SyncAction.Conflict, rel, lEntry, left, right, "修改时间相同但内容不同"));
                    break;

                default:
                    // 库里越界的枚举值（改库/版本错位）：冲突静默消失比多标记一次人工裁决危险得多
                    plan.Add(Mk(SyncAction.Conflict, rel, lEntry, left, right, "未知冲突策略，等待人工裁决"));
                    break;
            }
        }

        /// <summary>
        /// 同步成功后从双侧扫描结果推导新快照（并集，双侧都有取较新；删除动作的路径剔除）。
        /// 注意：仅在执行完全成功（failed==0）后调用，失败时保留旧快照让下轮重做。
        /// </summary>
        public static Dictionary<string, FileEntry> BuildSnapshot(Dictionary<string, FileEntry> left,
            Dictionary<string, FileEntry> right, List<PlanEntry> plan)
        {
            // 执行后该消失的路径：显式删除 + 移动走的旧路径（rename 执行后旧路径在两侧都不存在）
            var deleted = plan
                .Select(p => p.Action switch
                {
                    SyncAction.DeleteLeft or SyncAction.DeleteRight => p.RelativePath,
                    SyncAction.Move when p.FromPath != null => p.FromPath!,
                    _ => null
                })
                .Where(s => s != null)
                .Select(s => s!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var result = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var (rel, l) in left)
            {
                if (!deleted.Contains(rel)) result[rel] = l;
            }
            foreach (var (rel, r) in right)
            {
                if (deleted.Contains(rel)) continue;
                if (result.TryGetValue(rel, out var cur))
                {
                    // 两侧都在：正常同步后内容一致；冲突跳过时取较新，下轮继续按策略裁决
                    if (r.MtimeUtc > cur.MtimeUtc) result[rel] = r;
                }
                else result[rel] = r;
            }
            return result;
        }
    }
}
