using System;
using System.Collections.Generic;

namespace FolderSync.Core
{
    public enum SyncDirection
    {
        MirrorLeftToRight = 0, MirrorRightToLeft = 1, TwoWay = 2,
        /// <summary>纯备份（v1.5）：左→右，只产源→目标的新建/更新（源新才覆盖），永不反向写源、永不删源</summary>
        BackupLeftToRight = 3,
        /// <summary>纯备份（v1.5）：右→左，同上语义反向</summary>
        BackupRightToLeft = 4
    }

    /// <summary>方向语义辅助：镜像/备份同属单向类（源→目标），各处判断统一走这里，
    /// 新增方向值时只改这一处语义定义。</summary>
    public static class SyncDirectionExt
    {
        /// <summary>动作朝右（源=左，目标=右）；镜像与备份共用</summary>
        public static bool ToRight(this SyncDirection d) =>
            d is SyncDirection.MirrorLeftToRight or SyncDirection.BackupLeftToRight;
        /// <summary>备份方向（v1.5）：目标较新/分不出新旧时不回写源，跳过并说明</summary>
        public static bool IsBackup(this SyncDirection d) =>
            d is SyncDirection.BackupLeftToRight or SyncDirection.BackupRightToLeft;
    }

    /// <summary>双向同步冲突解决策略（两边同名文件都被修改时）。
    /// 三种败者去向三选一：Manual 人工 / 自动裁决+版本库（VersionKeepCount&gt;0）/ 自动裁决+冲突副本。</summary>
    public enum ConflictPolicy
    {
        NewestMtime = 0, LargestSize = 1, Manual = 2,
        /// <summary>冲突副本（v1.5）：自动裁决胜者覆盖，败者改名 原名.sync-conflict-时间戳.扩展 留在原目录
        /// （Syncthing 式）；副本视作普通新文件参与后续同步</summary>
        ConflictCopy = 3
    }

    public enum TriggerType { Manual = 0, Realtime = 1, Interval = 2, Schedule = 3 }
    public enum JobStatus { Idle = 0, Scanning = 1, Analyzing = 2, Syncing = 3, Error = 4, WaitingMedia = 5 }

    /// <summary>同步动作类型</summary>
    public enum SyncAction
    {
        None = 0,
        CreateLeft,      // 右边有，左边无（双向/RTL 用）
        CreateRight,     // 左边有，右边无
        UpdateRight,     // 左边较新，覆盖右边
        UpdateLeft,      // 右边较新，覆盖左边
        DeleteRight,     // 左边已删除，镜像删除右边
        DeleteLeft,      // 右边已删除（仅双向时出现）
        Conflict,        // 两边都改过，按冲突策略
        /// <summary>移动/重命名（v1.5）：目标侧内部 rename（FromPath → RelativePath），代替重拷+删除</summary>
        Move
    }

    /// <summary>用户逐行改向（v1.7 C2）：反向执行=以对侧现状覆盖本侧（Update 交换方向；Create 的对侧无文件=删除本侧）。
    /// 跳过不在此列——沿用 Action=None 的既有跳过机制（点图标/右键同一逻辑）。</summary>
    public enum UserOverrideKind { Default = 0, Reverse = 1 }

    /// <summary>待执行的动作条目</summary>
    public class PlanEntry
    {
        public SyncAction Action { get; set; }
        public string RelativePath { get; set; } = "";
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        /// <summary>左侧条目大小（字节）；-1 = 左侧不存在。目录为 0。</summary>
        public long LeftSize { get; set; } = -1;
        /// <summary>右侧条目大小（字节）；-1 = 右侧不存在。目录为 0。</summary>
        public long RightSize { get; set; } = -1;
        public DateTime? LeftMtime { get; set; }
        public DateTime? RightMtime { get; set; }
        public string ActionText => Action switch
        {
            SyncAction.CreateLeft => "← 新建(左)",
            SyncAction.CreateRight => "新建(右) →",
            SyncAction.UpdateLeft => "← 覆盖(左)",
            SyncAction.UpdateRight => "覆盖(右) →",
            SyncAction.DeleteLeft => "删除(左)",
            SyncAction.DeleteRight => "删除(右)",
            SyncAction.Conflict => "⚠ 冲突",
            SyncAction.Move => "⇄ 移动",
            _ => "—"
        };

        /// <summary>移动/重命名检测（v1.5）：Move 动作的旧相对路径（'/' 分隔）；其余动作为 null。</summary>
        public string? FromPath { get; set; }

        /// <summary>Move 动作的物理侧（true=右侧）：由配对来源的删除/新建动作后缀决定，
        /// 双向任务两侧都可能是写入方，不能从 job.Direction 推导。</summary>
        public bool MoveOnRight { get; set; }

        /// <summary>策略自动裁决说明（时间优先/大小优先）或人工裁决记录</summary>
        public string Note { get; set; } = "";

        /// <summary>双向手动裁决后置 true，仅这些条目会被重新执行</summary>
        public bool ManuallyResolved { get; set; }

        /// <summary>用户逐行改向（v1.7 C2）：Reverse=执行时按对侧现状覆盖本侧重打动作。视觉层箭头翻转提示。</summary>
        public UserOverrideKind UserOverride { get; set; }

        public string SizeText => IsDirectory ? "<目录>" : Executor.FormatSize(Size);
        public string LeftSizeText => LeftSize < 0 ? "（不存在）" : IsDirectory ? "（文件夹）" : Executor.FormatSize(LeftSize);
        public string RightSizeText => RightSize < 0 ? "（不存在）" : IsDirectory ? "（文件夹）" : Executor.FormatSize(RightSize);
        public string LeftMtimeText => LeftMtime == null ? "（不存在）" : IsDirectory ? "（文件夹）" : LeftMtime.Value.ToString("yyyy-MM-dd HH:mm");
        public string RightMtimeText => RightMtime == null ? "（不存在）" : IsDirectory ? "（文件夹）" : RightMtime.Value.ToString("yyyy-MM-dd HH:mm");
    }

    public class SyncJob
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string LeftPath { get; set; } = "";
        public string RightPath { get; set; } = "";
        public SyncDirection Direction { get; set; } = SyncDirection.MirrorLeftToRight;
        public TriggerType Trigger { get; set; } = TriggerType.Manual;
        public int IntervalSeconds { get; set; } = 7200;     // 定时间隔（默认 2 小时）
        public int DebounceSeconds { get; set; } = 10;       // 实时去抖
        /// <summary>指定时刻调度规格（v1.6 B2，Trigger=Schedule 时有效）：
        /// "daily HH:mm" / "weekly 1,3,5 HH:mm"（1=周一…7=周日，本地时区）。null/空=未配置（回退手动）。
        /// 错过的时刻（关机）由启动补跑天然覆盖（AutoStart）。</summary>
        public string? ScheduleSpec { get; set; }
        public string ExcludePatterns { get; set; } = "";    // 分号分隔的通配符
        public bool AutoStart { get; set; } = true;          // 开机自动恢复
        public bool Enabled { get; set; } = true;
        public bool DeleteToRecycleBin { get; set; } = true; // 删除进回收站
        public bool MirrorDelete { get; set; } = false;      // 镜像删除孤儿（默认关，防误删）；双向模式下语义为"删除传播"（快照确认才删）
        /// <summary>严格镜像（仅单向有效）：两边都改过时一律源覆盖目标；关 = 保留较新一侧（"新者胜"）</summary>
        public bool StrictMirror { get; set; } = false;
        public ConflictPolicy ConflictPolicy { get; set; } = ConflictPolicy.NewestMtime; // 双向模式冲突策略
        /// <summary>版本保留代数：0=关闭（默认，兼容现有任务）；N>0 时覆盖/删除的旧文件入
        /// 中心版本库（&lt;程序目录&gt;\versions\&lt;侧根哈希&gt;\repo），保留最近 N 代</summary>
        public int VersionKeepCount { get; set; } = 0;
        /// <summary>块级增量传输（v1.4）：Update 动作只传目标侧缺的数据块（CDC 对比 + 全文 sha256 终检，
        /// 校验不过自动整文件复制）。适合大文件小改动与 NAS/移动盘等慢速目标；本地 NVMe 对拷可关闭换速度。</summary>
        public bool DeltaSync { get; set; } = true;
        /// <summary>占用/瞬时错误自动重试（v1.5）：复制/删除撞共享冲突、网络抖动等瞬时错误的条目
        /// 轮末统一退避重试（2s/10s/30s × 3 轮），仍失败才落 failed。关闭 = 失败直接进列表（旧行为）。
        /// 实时任务本就有下一轮兜底，此开关主要救手动/定时轮。</summary>
        public bool AutoRetry { get; set; } = true;
        /// <summary>移动/重命名检测（v1.5）：一侧删除+同侧新建中 size/mtime 匹配的配对识别为移动，
        /// 改执行目标侧内部 rename（同卷瞬时），代替重拷+删除。仅在 MirrorDelete 开启时生效
        /// （关闭时目标旧文件按语义保留，替换成 rename 等于隐式删除）。多候选歧义时放弃配对绝不猜。</summary>
        public bool MoveDetect { get; set; } = true;
        /// <summary>复制后复核（v1.5 B1，默认关）：CopyOne 原子替换完成后读回目标全文 sha256 与源比对，
        /// 不符=该项失败（介质静默损坏信号）。代价=目标侧全读一遍。</summary>
        public bool CopyVerify { get; set; } = false;
        /// <summary>深度校验（v1.5 B1，默认关）：分析阶段对「两侧 size+mtime 一致（判无差异）」的文件
        /// 比对内容哈希，检出位腐。0=关 / 1=仅 ≥50MB / 2=全量。哈希顺扫盘速率，全量档大任务耗时数倍。</summary>
        public int DeepVerify { get; set; } = 0;
        /// <summary>并行复制 worker 数（v1.7 D2）：复制/归档趟多 worker 并行（前置/末尾删除趟保持串行——
        /// 同路径先删后建的顺序依赖）。默认 2；1=串行旧路径（逃生门，与旧实现等价）；上限 4。
        /// 机械盘/高并发小文件场景可设 1。</summary>
        public int CopyWorkers { get; set; } = 2;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    /// <summary>失败明细条目（重试用）：路径 + 错误 + 原计划动作</summary>
    public class FailedItem
    {
        public string RelativePath { get; set; } = "";
        public string Error { get; set; } = "";
        /// <summary>SyncAction 枚举名（重试时 TryParse 还原）</summary>
        public string Action { get; set; } = "";
    }

    /// <summary>托盘通知节流：同类事件窗口期内只放行一次（实时任务高频运行防刷屏）。</summary>
    public class NotificationThrottle
    {
        private readonly Dictionary<string, DateTime> _lastShown = new();
        private readonly TimeSpan _window;

        public NotificationThrottle(TimeSpan window) => _window = window;

        /// <summary>该类事件本次是否应显示（显示即记录时刻）</summary>
        public bool ShouldShow(string key)
        {
            var now = DateTime.UtcNow;
            if (_lastShown.TryGetValue(key, out var last) && now - last < _window) return false;
            _lastShown[key] = now;
            return true;
        }
    }

    /// <summary>文件系统条目（扫描结果）</summary>
    public class FileEntry
    {
        public string RelativePath { get; set; } = "";  // 相对任务根路径，'/' 分隔
        public long Size { get; set; }
        public DateTime MtimeUtc { get; set; }
        public bool IsDirectory { get; set; }
        public uint Attributes { get; set; }
    }

    public class RunRecord
    {
        public long Id { get; set; }
        public long JobId { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
        public string Trigger { get; set; } = "manual";  // manual/realtime/interval/startup
        public string Status { get; set; } = "running";  // running/ok/error/partial
        public int ScannedFiles { get; set; }
        public int CopiedFiles { get; set; }
        public int DeletedFiles { get; set; }
        public int SkippedFiles { get; set; }
        public int FailedFiles { get; set; }
        public long BytesCopied { get; set; }
        /// <summary>块级增量比整文件少写的字节（v1.4）</summary>
        public long DeltaSavedBytes { get; set; }
        /// <summary>轮末退避重试后成功的条目数（v1.5）</summary>
        public int RetriedOk { get; set; }
        /// <summary>移动/重命名检测命中数（v1.5，runs.moved_files）</summary>
        public int MovedFiles { get; set; }
        public double? AvgSpeedBytesPerSec { get; set; }
        public string? ErrorMessage { get; set; }
    }

    /// <summary>进度事件（扫描/同步阶段统一）</summary>
    public class ProgressInfo
    {
        public string Phase { get; set; } = "";          // scan/compare/copy/delete/done
        public string CurrentItem { get; set; } = "";
        /// <summary>补充信息（自由文本，如扫描阶段的"已发现 N 项"）；UI 直接展示。</summary>
        public string ExtraInfo { get; set; } = "";
        /// <summary>轮次序号（引擎 Progress 回调转发前盖上）：UI 丢弃过时轮次的晚到事件，
        /// 防止旧"扫描中"覆盖已渲染的终态（异步投递下晚到必发生）。0=未盖（直调测试场景，不过滤）。</summary>
        public long RunSeq { get; set; }
        /// <summary>工作量总数。扫描阶段=文件夹总数（预数，真进度分母）；执行阶段=计划条目数。</summary>
        public long TotalItems { get; set; }
        public long DoneItems { get; set; }
        public long TotalBytes { get; set; }
        public long DoneBytes { get; set; }
        public double SpeedBytesPerSec { get; set; }
        public TimeSpan? Eta => SpeedBytesPerSec > 1024 && TotalBytes > DoneBytes
            ? TimeSpan.FromSeconds((TotalBytes - DoneBytes) / SpeedBytesPerSec) : null;
        public double Percent => TotalItems > 0 ? DoneItems * 100.0 / TotalItems : 0;

        /// <summary>克隆一份再 Report：Progress&lt;T&gt; 经 SynchronizationContext 异步投递的是引用，
        /// 复用的可变对象会在投递途中被工作线程继续改写——进度显示跳变、
        /// job{n}.state.txt 的"卡死前最后动作"取证失真。Report 一律传 Snapshot。</summary>
        public ProgressInfo Snapshot() => new()
        {
            Phase = Phase,
            CurrentItem = CurrentItem,
            ExtraInfo = ExtraInfo,
            RunSeq = RunSeq,
            TotalItems = TotalItems,
            DoneItems = DoneItems,
            TotalBytes = TotalBytes,
            DoneBytes = DoneBytes,
            SpeedBytesPerSec = SpeedBytesPerSec
        };
    }
}
