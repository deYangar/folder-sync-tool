using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Core.Platform;
using static FolderSync.Core.Platform.Platform;

namespace FolderSync.Core
{
    /// <summary>同步执行器：平台 FileOps（Windows=CopyFileEx，Unix=分块流复制）+ 进度/速率/ETA + 回收站删除。</summary>
    public class Executor
    {
        private readonly SyncJob _job;
        private volatile bool _cancel;
        private CancellationToken _ct = CancellationToken.None;   // ExecuteAsync 注入：CopyFileEx 回调/增量循环即时响应取消
        private volatile bool _diskFull;
        private long _doneBytes;          // 字节进度（跨文件累计；D2 并行 worker 共享 → Interlocked）
        private ProgressInfo _pi = new();
        private IProgress<ProgressInfo>? _progress;
        private DateTime _lastLoopUi = DateTime.MinValue;   // 主循环进度节流（万级条目别刷爆 UI）
        private readonly List<(string path, SyncAction action)> _recyclePending = new();  // 回收站删除攒批（SHFileOperation 逐文件调用极慢；带原动作供失败重试还原）
        private readonly object _recycleGate = new();     // D2：复制趟的类型冲突/D1 副本路径也会攒批，worker 并发下加锁
        private int _recycleChars;      // 攒批 pFrom 累计字符数（每路径+1 个 \0）：SHFileOperation pFrom 上限约 32K，
                                        // 只按条目数（500）攒时长路径批可超限，批量失败退化逐文件（慢）——按字符提前分批
        private int _deleteFailed;        // 刷批阶段确认失败的删除数
        private string _versionTs = "";     // 本轮版本入库时间戳（同轮共用，版本排序键）
        private readonly List<FailedItem> _failedItems = new();  // 失败明细（封顶，防万级失败爆内存）
        private readonly object _failedGate = new();    // D2 并行 worker 的失败明细收集

        // ---- D2 并行复制（v1.7）：worker 计数与 worker 级缓冲 ----
        private long _okCount, _skipCount, _failCount, _bytesAll;   // Interlocked 计数（串行路径同样走这里，语义统一）
        private volatile bool _cancelOce;   // D2：并行 body 里发生过取消 OCE（ForEach 后补抛用；磁盘满不置）
        private int _workerSeq;             // worker 编号分配（[Wn] 进度前缀）
        private readonly ThreadLocal<int> _workerNo = new(() => 0); // 0=串行主线程（进度不加前缀，兼容既有 E2E 文案）
        private readonly ThreadLocal<byte[]?> _deltaBufTl = new(() => null);
        private readonly ThreadLocal<byte[]?> _chunkBufTl = new(() => null);
        private readonly ThreadLocal<byte[]?> _chunkReadBufTl = new(() => null);

        /// <summary>失败明细（最多 MaxFailedItems 条，超出仅计 FailedTotal）</summary>
        public IReadOnlyList<FailedItem> FailedItems => _failedItems;
        private int _failedTotal;
        /// <summary>本轮失败总数（含超出封顶未保留明细的部分）</summary>
        public int FailedTotal => Volatile.Read(ref _failedTotal);
        public const int MaxFailedItems = 2000;

        // ---- A2 占用/瞬时错误自动重试 ----
        /// <summary>重试退避间隔（默认 2s/10s/30s 三轮）。测试可调短。</summary>
        internal static TimeSpan[] RetryDelays =
            { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30) };

        private sealed class RetryCandidate
        {
            public required PlanEntry Entry;
            public required Exception Error;   // 首次失败原因（轮末仍失败时进明细）
        }

        private readonly System.Collections.Concurrent.ConcurrentQueue<RetryCandidate> _retryQueue = new();  // 瞬时失败待重试（不占 failed 计数；D2 并行 worker 入队线程安全）

        private int _retriedOk;
        /// <summary>轮末退避重试后成功的条目数（runs.retried_ok 数据源）</summary>
        public int RetriedOk => Volatile.Read(ref _retriedOk);

        private int _movedCount;
        /// <summary>移动/重命名检测命中数（runs.moved_files 数据源，v1.5）</summary>
        public int MovedCount => Volatile.Read(ref _movedCount);

        private long _deltaSaved;
        /// <summary>本轮块级增量比整文件少写的字节（runs.delta_saved_bytes 数据源）。</summary>
        public long DeltaSavedBytes => Volatile.Read(ref _deltaSaved);

        private int _deletedCount;
        /// <summary>本轮实际删除成功的条目数（回收站批删除在刷盘后统计）</summary>
        public int DeletedCount => Volatile.Read(ref _deletedCount);

        private long _mtimeTolMs = 50;   // mtime 比对容差（轮内缓存：MtimeToleranceMs 每次做 DriveFormat IO，网络路径还可能抛异常）

        /// <summary>瞬时错误（值得退避重试）：共享冲突/锁冲突/网络类抖动。
        /// win32 清单——32 ERROR_SHARING_VIOLATION / 33 ERROR_LOCK_VIOLATION / 64 ERROR_NETNAME_DELETED /
        /// 53 ERROR_BAD_NETPATH / 121 ERROR_SEM_TIMEOUT / 170 ERROR_BUSY / 1236 ERROR_NETWORK_UNREACHABLE。
        /// 磁盘满(112/395)、路径不存在(2/3)、权限(5) 等重试无意义，不在此列。
        /// Win32Exception 看 NativeErrorCode；IOException 看 HResult 低 16 位（File.Move/流写入路径）。</summary>
        private static bool IsTransient(Exception ex) =>
            ex is Win32Exception w && w.NativeErrorCode is 32 or 33 or 64 or 53 or 121 or 170 or 1236
            || ex is IOException io && (io.HResult & 0xFFFF) is 32 or 33 or 64 or 53 or 121 or 170 or 1236;

        public Executor(SyncJob job) => _job = job;

        public void Cancel() => _cancel = true;

        /// <summary>目标盘空间不足触发过（快速中止本次运行）</summary>
        public bool DiskFull => _diskFull;


        /// <summary>记失败明细（封顶 2000 条；溢出只累计总数）。</summary>
        private void RecordFailure(string relPath, string action, string error)
        {
            Interlocked.Increment(ref _failedTotal);
            lock (_failedGate)
            {
                if (_failedItems.Count < MaxFailedItems)
                    _failedItems.Add(new FailedItem { RelativePath = relPath, Action = action, Error = error });
            }
        }

        /// <summary>按动作条目映射源/目标：物理侧由动作后缀决定（Right=朝右，Left=朝左）。
        /// 历史上单向任务按 job.Direction 整体定向（合法动作集内与后缀等价）；v1.7 C2 逐行反向
        /// 会产生与方向相反的动作（如 L2R 的 UpdateLeft），后缀判定是唯一正确口径。
        /// Conflict/Move 无后缀语义：Conflict 按任务方向（单向=源覆盖目标）；Move 自带物理侧不走这里。
        /// 纵深防御：构造出的完整路径必须落在对应任务根之下（改库注入/失败明细被篡改的 ../ 逃逸在这里拦下，
        /// 抛 IOException 计入该条目失败而非炸整轮）。</summary>
        private (string src, string dst) MapFor(SyncAction action, string rel)
        {
            bool toRight = action is SyncAction.CreateRight or SyncAction.UpdateRight or SyncAction.DeleteRight
                || action == SyncAction.Conflict && _job.Direction.ToRight();
            var relPath = rel.Replace('/', Path.DirectorySeparatorChar);
            var src = toRight ? _job.LeftPath : _job.RightPath;
            var dst = toRight ? _job.RightPath : _job.LeftPath;
            var srcFull = Path.Combine(src, relPath);
            var dstFull = Path.Combine(dst, relPath);
            if (!IsUnderRoot(Path.GetFullPath(srcFull), src) || !IsUnderRoot(Path.GetFullPath(dstFull), dst))
                throw new IOException($"计划条目路径越出任务根（疑似数据被篡改）: {rel}");
            return (srcFull, dstFull);
        }

        /// <summary>同侧根目录（双向两侧都可能是写入方；单向也扫两侧，代价可忽略）。</summary>
        private IEnumerable<string> BothRoots
        {
            get
            {
                yield return _job.LeftPath;
                yield return _job.RightPath;
            }
        }

        /// <summary>path 是否在 root 之下（带路径边界：D:\Doc 不吞 D:\Documents 这类字符串前缀兄弟目录）。
        /// 分隔符按平台取（Unix 为 /）。</summary>
        private static bool IsUnderRoot(string path, string root)
        {
            var sep = Path.DirectorySeparatorChar;
            var r = root.TrimEnd('\\', '/');
            return path.Length == r.Length && path.Equals(r, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(r + sep, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>path 所属的任务侧根（左侧/右侧，边界匹配 + 长根优先：两根都命中时取更长者）。</summary>
        private string RootOf(string path)
        {
            bool hitL = IsUnderRoot(path, _job.LeftPath), hitR = IsUnderRoot(path, _job.RightPath);
            if (hitL && hitR)
                return _job.LeftPath.Length >= _job.RightPath.Length ? _job.LeftPath : _job.RightPath;
            return hitR ? _job.RightPath : _job.LeftPath;
        }

        /// <summary>文件入版本库（v1.3 块级去重）：切块+去重+压缩进中心库 &lt;程序目录&gt;\versions\&lt;侧根哈希&gt;\repo，
        /// 索引提交成功才删原文件；失败（中心盘只读/超长/被锁）返回 false，由调用方降级常规删除路径并记警告。</summary>
        private bool ArchiveFileSafe(string path)
        {
            var root = RootOf(path);
            var rel = path[root.Length..].TrimStart('\\', '/').Replace('\\', '/');
            var pi = _pi;
            var progress = _progress;
            var name = Path.GetFileName(path);
            var lastUi = DateTime.MinValue;
            var ok = VersionStore.ArchiveFile(root, path, rel, _job.Id, _versionTs,
                cancelled: () => _cancel || _ct.IsCancellationRequested,
                progress: (done, total) =>
                {
                    if ((DateTime.Now - lastUi).TotalMilliseconds > 250)
                    {
                        lastUi = DateTime.Now;
                        pi.CurrentItem = $"版本入库: {name}  {FormatSize(done)}/{FormatSize(total)}";
                        progress?.Report(pi.Snapshot());
                    }
                },
                error: out var err);
            if (!ok) LastWarning = $"版本入库失败已降级常规删除: {path}: {err}";
            return ok;
        }

        /// <summary>清扫两侧根下崩溃残留的 *.foldersync-tmp（上轮 CopyFileEx 中断留下的半截中转件）。
        /// 尽力而为：单个删不掉不影响本轮同步。</summary>
        private static void SweepTmpResidue(IEnumerable<string> roots)
        {
            foreach (var root in roots)
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
                try
                {
                    var opt = new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = true,
                        AttributesToSkip = FileAttributes.None
                    };
                    foreach (var fi in new DirectoryInfo(root).EnumerateFiles("*" + Scanner.TmpSuffix, opt))
                    {
                        try { fi.Attributes = FileAttributes.Normal; fi.Delete(); }
                        catch { /* 正被占用等：留给下轮 */ }
                    }
                }
                catch { }
            }
        }

        /// <summary>递归清目录树内文件的只读属性（dotnet/runtime#1043：.NET 的 Directory.Delete(recursive)
        /// 遇内部只读文件抛 UnauthorizedAccessException，不像 cmd rd /s 自清——镜像删除相机/光盘素材
        /// 等只读大树时整树失败）。尽力而为：单个清不掉交给 Delete 原样报错。</summary>
        private static void ClearReadOnlyTree(string dirL)
        {
            try
            {
                var opt = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.None   // 只读/系统/隐藏都要摸到
                };
                foreach (var f in new DirectoryInfo(dirL).EnumerateFiles("*", opt))
                    try { f.Attributes &= ~FileAttributes.ReadOnly; } catch { }
            }
            catch { /* 清属性尽力而为 */ }
        }

        /// <summary>执行计划。返回 (成功, 跳过, 失败, 字节)。
        /// sweepTmpResidue=false 时跳过全树 tmp 残留清扫（调用方确认上轮正常收尾才关：全递归扫两侧代价大，
        /// 实时任务每轮去抖都白扫；默认 true 保守，直调测试不受影响）。
        /// 主体是同步 P/Invoke（CopyFileEx/SHFileOperation），Task.Run 包一层真异步——
        /// 假 async（无 await）曾是 CS1998 常驻警告，且误导调用方以为不阻塞。</summary>
        public Task<(int ok, int skipped, int failed, long bytes)> ExecuteAsync(
            List<PlanEntry> plan, IProgress<ProgressInfo>? progress, CancellationToken ct,
            bool sweepTmpResidue = true)
            => Task.Run(() => Execute(plan, progress, ct, sweepTmpResidue));

        /// <summary>包装：取消（ct 抛出）/磁盘满中止路径也要核对攒批删除的乐观计数——
        /// Core 的收尾刷盘在这些路径不会执行，攒批文件不对账曾致统计失真（下轮扫描才自愈）。
        /// 正常路径 Core 已刷过并入账归零（Exchange），这里对空值幂等、双刷无害。</summary>
        private (int ok, int skipped, int failed, long bytes) Execute(
            List<PlanEntry> plan, IProgress<ProgressInfo>? progress, CancellationToken ct,
            bool sweepTmpResidue)
        {
            try
            {
                ExecuteCore(plan, progress, ct, sweepTmpResidue);
            }
            finally
            {
                FlushRecycle();
                // 入账即取出（Exchange 归零）：Core 正常收尾已全额入账过，这里取到 0 幂等；
                // 双读（Read 后 Add）的旧写法在同一轮两次入账 → failed+2/ok-2、runs 统计虚高
                var dfFinal = Interlocked.Exchange(ref _deleteFailed, 0);
                Interlocked.Add(ref _failCount, dfFinal);
                Interlocked.Add(ref _okCount, -dfFinal);
                // D2：worker 级 ThreadLocal 随轮次释放（线程池长存线程不积累 Executor 的槽位）
                _workerNo.Dispose();
                _deltaBufTl.Dispose();
                _chunkBufTl.Dispose();
                _chunkReadBufTl.Dispose();
            }
            return ((int)Volatile.Read(ref _okCount), (int)Volatile.Read(ref _skipCount),
                (int)Volatile.Read(ref _failCount), Volatile.Read(ref _bytesAll));
        }

        private void ExecuteCore(
            List<PlanEntry> plan, IProgress<ProgressInfo>? progress, CancellationToken ct,
            bool sweepTmpResidue)
        {
            // 清扫上轮崩溃残留的拷贝中转件（不判差异直接删，tmp 永远不是有效数据）
            if (sweepTmpResidue) SweepTmpResidue(BothRoots);
            // 版本入库时间戳：同轮共用（prune 排序键，格式 o 便于排序且带毫秒，轮间天然区分）
            _versionTs = DateTime.Now.ToString("o");
            // mtime 容差轮内只算一次（原 DeltaTransfer 每文件调一次，DriveFormat IO 还会对网络路径抛异常）
            try { _mtimeTolMs = Differ.MtimeToleranceMs(_job.LeftPath, _job.RightPath); } catch { }

            var fileEntries = plan.Where(p => !p.IsDirectory &&
                (p.Action == SyncAction.CreateLeft || p.Action == SyncAction.CreateRight ||
                 p.Action == SyncAction.UpdateLeft || p.Action == SyncAction.UpdateRight)).ToList();
            var totalBytes = fileEntries.Sum(p => p.Size);

            var pi = new ProgressInfo { Phase = "copy", TotalItems = plan.Count, TotalBytes = totalBytes };
            _pi = pi;
            _progress = progress;
            _ct = ct;   // 大文件复制中取消即中断（CopyFileEx 进度回调判定），不再等整个文件拷完
            _doneBytes = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var lastReport = DateTime.MinValue;

            // 三趟执行：类型冲突的前置删除（同路径还有新建动作，必须先清掉异类型目标）
            // → 建/更新（升序，父目录在前）→ 其余删除（降序，子目录在前）
            var buildPass = plan.Where(p => p.Action is not (SyncAction.DeleteLeft or SyncAction.DeleteRight)).ToList();
            var buildPaths = new HashSet<string>(
                buildPass.Where(p => p.Action is SyncAction.CreateLeft or SyncAction.CreateRight
                    or SyncAction.UpdateLeft or SyncAction.UpdateRight)
                .Select(p => p.RelativePath), StringComparer.OrdinalIgnoreCase);
            var allDeletes = plan.Where(p => p.Action is SyncAction.DeleteLeft or SyncAction.DeleteRight)
                .OrderByDescending(p => p.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
            var preDeletePass = allDeletes.Where(p => buildPaths.Contains(p.RelativePath)).ToList();
            var postDeletePass = allDeletes.Where(p => !buildPaths.Contains(p.RelativePath)).ToList();

            pi.TotalItems = buildPass.Count;
            int delDone = 0;
            foreach (var (pass, isDelete) in new[]
                     { (preDeletePass, true), (buildPass, false), (postDeletePass, true) })
            {
                if (pass.Count == 0) continue;
                // 每趟入口各自设置进度相位。曾用 allDeletes.Count>0 判"进入删除阶段"——空的前置删除趟
                // 也发 delete 头，Phase/TotalItems 污染整个复制趟（实测：复制事件 Phase=delete、
                // items=2/1 超分母，UI 全程显示"镜像删除清理中"橙色进度条）
                if (isDelete)
                {
                    // 删除趟：独立进度（按项数），字节进度拉满，颜色由 UI 区分
                    pi.Phase = "delete";
                    pi.TotalItems = allDeletes.Count;
                    pi.DoneItems = delDone;
                    pi.DoneBytes = totalBytes;
                    pi.SpeedBytesPerSec = 0;
                    pi.CurrentItem = $"镜像删除清理开始，共 {allDeletes.Count} 项";
                }
                else
                {
                    // 建/更趟：恢复复制相位（上一趟若是前置删除，Phase/分母还停在 delete）
                    pi.Phase = "copy";
                    pi.TotalItems = buildPass.Count;
                    pi.DoneItems = DoneSoFar();
                    pi.DoneBytes = Math.Min(Volatile.Read(ref _doneBytes), totalBytes);
                    pi.CurrentItem = "复制/更新阶段";
                }
                progress?.Report(pi.Snapshot());

                if (isDelete)
                {
                    // 删除趟恒串行（D2）：同路径先删后建的顺序依赖 + 回收站攒批单点
                    foreach (var entry in pass)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (_cancel) break;
                        pi.DoneItems = delDone;
                        pi.CurrentItem = "[删] " + entry.RelativePath;
                        if ((DateTime.Now - _lastLoopUi).TotalMilliseconds > 250)
                        {
                            progress?.Report(pi.Snapshot());
                            _lastLoopUi = DateTime.Now;
                        }
                        ProcessEntry(entry);
                        delDone++;   // 成败都算处理过一项（失败汇总在 done 事件）
                    }
                }
                else if (_job.CopyWorkers > 1)
                {
                    // D2 并行复制：建/更趟多 worker（前置/末尾删除趟保持串行）。
                    // 取消全 worker 传播（token 触发即停）；磁盘满经 _cancel 熔断；进度按完成时间追加（顺序不保证=如实）
                    var workers = Math.Min(4, _job.CopyWorkers);
                    var opts = new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct };
                    try
                    {
                        Parallel.ForEach(pass, opts,
                            () => Interlocked.Increment(ref _workerSeq),   // localInit：领 worker 号（进度 [Wn] 前缀）
                            (entry, state, workerNo) =>
                            {
                                _workerNo.Value = workerNo;
                                if (_cancel) { state.Stop(); return workerNo; }
                                ReportLoopProgress(pi, totalBytes, sw, entry);
                                try { ProcessEntry(entry); }
                                catch (OperationCanceledException)
                                {
                                    // CopyOne/DeltaTransfer 的取消（token/软取消）：body 内 OCE 会被
                                    // Parallel 聚合成 AggregateException 破坏取消链——改为熔断 + Stop，
                                    // 取消的统一上抛由 ForEach 的 token 或下方补抛完成
                                    _cancel = true;
                                    _cancelOce = true;
                                    state.Stop();
                                }
                                return workerNo;
                            },
                            _ => { });
                    }
                    catch (OperationCanceledException) { throw; }
                    Interlocked.Exchange(ref _workerSeq, 0);
                    // 软取消（RequestCancel/磁盘满熔断只置 _cancel 不触发 token）：ForEach 正常返回，
                    // 此处补抛保持与串行路径一致的「轮次=cancelled」语义。
                    // 磁盘满时在途 worker 的 OCE 是熔断副作用而非用户取消，必须豁免——走 error 收尾
                    if (_cancelOce && !_diskFull) throw new OperationCanceledException("用户取消");
                }
                else
                {
                    foreach (var entry in pass)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (_cancel) break;
                        pi.DoneItems = DoneSoFar();
                        pi.DoneBytes = Math.Min(Volatile.Read(ref _doneBytes), totalBytes);
                        pi.CurrentItem = entry.RelativePath;
                        if (totalBytes > 0 && sw.ElapsedMilliseconds > 200)
                            pi.SpeedBytesPerSec = Volatile.Read(ref _doneBytes) / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
                        // 小条目（跳过/目录等）成千上万时逐条报进度会刷爆 UI 线程：250ms 节流
                        if ((DateTime.Now - _lastLoopUi).TotalMilliseconds > 250)
                        {
                            progress?.Report(pi.Snapshot());
                            _lastLoopUi = DateTime.Now;
                        }
                        ProcessEntry(entry);
                    }
                }
            }

            // 收尾：刷盘攒批的回收站删除（核对实际删除数，失败项回退计数并入 failed——
            // 攒批路径的 ok 是乐观计数，核对失败必须同步回退，否则 ok+skipped+failed > 计划数、runs 落库虚高）
            FlushRecycle();
            var df = Interlocked.Exchange(ref _deleteFailed, 0);   // 入账即取出：防 finally 二次累计
            Interlocked.Add(ref _failCount, df);
            Interlocked.Add(ref _okCount, -df);

            // A2 重试趟：轮末退避重试瞬时失败（2s/10s/30s × 3 轮），成功计入正常统计；
            // 重试中又攒下的回收站批（重试的删除动作）再刷一次核对
            if (!_retryQueue.IsEmpty && _job.AutoRetry && !_cancel && !ct.IsCancellationRequested)
            {
                RunRetryPass(progress, ct, plan.Count, DoneSoFar());
                FlushRecycle();
                var dfRetry = Interlocked.Exchange(ref _deleteFailed, 0);
                Interlocked.Add(ref _failCount, dfRetry);
                Interlocked.Add(ref _okCount, -dfRetry);
            }
            else if (!_retryQueue.IsEmpty)
            {
                // 开关关闭/已中止（磁盘满等）：队列里的条目按普通失败收口；
                // 不覆盖 LastError（磁盘满的中止文案优先级更高）
                while (_retryQueue.TryDequeue(out var cand))
                {
                    Interlocked.Increment(ref _failCount);
                    RecordFailure(cand.Entry.RelativePath, cand.Entry.Action.ToString(), cand.Error.Message);
                }
            }

            var ok = (int)Volatile.Read(ref _okCount);
            var skipped = (int)Volatile.Read(ref _skipCount);
            var failed = (int)Volatile.Read(ref _failCount);
            var bytes = Volatile.Read(ref _bytesAll);
            pi.DoneItems = plan.Count;
            pi.TotalItems = plan.Count;   // 终态分母与分子同口径（残留上一趟的分母会让 UI 百分比超 100）
            pi.DoneBytes = totalBytes;
            pi.Phase = "done";
            pi.SpeedBytesPerSec = sw.Elapsed.TotalSeconds > 0 ? totalBytes / sw.Elapsed.TotalSeconds : 0;
            pi.CurrentItem = $"完成：成功 {ok}，跳过 {skipped}，失败 {failed}，{FormatSize(bytes)}，耗时 {sw.Elapsed.TotalSeconds:F1}s"
                + (RetriedOk > 0 ? $"（重试后成功 {RetriedOk}）" : "");
            progress?.Report(pi.Snapshot());
        }

        private long DoneSoFar() =>
            Volatile.Read(ref _okCount) + Volatile.Read(ref _skipCount) + Volatile.Read(ref _failCount);

        /// <summary>并行 worker 的条目级进度（[Wn] 前缀区分来源；250ms 节流）。</summary>
        private void ReportLoopProgress(ProgressInfo pi, long totalBytes,
            System.Diagnostics.Stopwatch sw, PlanEntry entry)
        {
            var w = _workerNo.Value;
            pi.DoneItems = DoneSoFar();
            pi.DoneBytes = Math.Min(Volatile.Read(ref _doneBytes), totalBytes);
            pi.CurrentItem = (w > 0 ? $"[W{w}] " : "") + entry.RelativePath;
            if (totalBytes > 0 && sw.ElapsedMilliseconds > 200)
                pi.SpeedBytesPerSec = Volatile.Read(ref _doneBytes) / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
            if ((DateTime.Now - _lastLoopUi).TotalMilliseconds > 250)
            {
                _progress?.Report(pi.Snapshot());
                _lastLoopUi = DateTime.Now;
            }
        }

        /// <summary>处理单条目 + 计数 + 瞬时错误进重试队列 + 磁盘满熔断（串行趟与并行 worker 共用）。</summary>
        private void ProcessEntry(PlanEntry entry)
        {
            try
            {
                var (entryOk, entryBytes) = ExecuteEntry(entry);
                if (entryOk)
                {
                    Interlocked.Increment(ref _okCount);
                    Interlocked.Add(ref _bytesAll, entryBytes);
                }
                else Interlocked.Increment(ref _skipCount);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                || ex is Win32Exception || ex is DirectoryNotFoundException || ex is FileNotFoundException)
            {
                // A2：瞬时错误（共享冲突/网络抖动）不直接失败，轮末退避重试
                if (_job.AutoRetry && IsTransient(ex))
                {
                    _retryQueue.Enqueue(new RetryCandidate { Entry = entry, Error = ex });
                }
                else
                {
                    Interlocked.Increment(ref _failCount);
                    LastError = $"{entry.RelativePath}: {ex.Message}";
                    RecordFailure(entry.RelativePath, entry.Action.ToString(), ex.Message);
                }
                // 磁盘满/配额超限：后面的文件也写不进去，立即中止整轮，别磨 23 万个失败。
                // Win32Exception：CopyFileEx 路径；IOException(HResult 低 16 位 112/395)：
                // File.Move 落地/流写入路径（CopyOne 尾段与块级组装同走这里）
                if (IsDiskFullEx(ex))
                {
                    _diskFull = true;
                    _cancel = true;   // 并行 worker 经 ProcessEntry 入口判定停跑；串行趟由主循环 break
                    LastError = $"目标磁盘空间不足，已中止本次同步（首个失败: {entry.RelativePath}: {ex.Message}）";
                }
            }
        }

        /// <summary>执行单条计划条目（主趟与 A2 重试趟共用）。返回 (true=成功, 复制字节)；
        /// 跳过条目返回 (false, 0)；一切失败以异常抛出（含瞬时错误，由调用方分类进重试队列）。
        /// MapFor 在本方法内调用：路径越界防御抛 IOException 也按该条目失败处理（注释语义与本处一致）。</summary>
        private (bool ok, long bytes) ExecuteEntry(PlanEntry entry)
        {
            var (entrySrc, targetPath) = MapFor(entry.Action, entry.RelativePath);
            switch (entry.Action)
            {
                case SyncAction.CreateRight:
                case SyncAction.CreateLeft:
                    if (entry.IsDirectory)
                    {
                        Directory.CreateDirectory(LongPath(targetPath));
                        return (true, 0);
                    }
                    CopyOne(entrySrc, targetPath);
                    // E1：开版本保留的任务新建落地即建基线（下轮 Update 免现场切目标）；
                    // keep=0 维持现状（首改现场建表，方案约束）
                    if (_job.VersionKeepCount > 0)
                        BuildBaselineAfterCopy(targetPath);
                    return (true, entry.Size);

                case SyncAction.UpdateRight:
                case SyncAction.UpdateLeft:
                    if (entry.IsDirectory) return (true, 0);
                    // 目标侧是目录（类型冲突人工裁决"保留另一侧"后可能出现）：目录无法 rename 覆盖，先清掉
                    if (Directory.Exists(LongPath(targetPath)))
                    {
                        if (DeleteOne(targetPath, isDirectory: true, entry.Action)) Interlocked.Increment(ref _deletedCount);
                    }
                    // D1 冲突副本：双向 + ConflictCopy 策略（Update 全部源自冲突裁决）——败者改名留在原目录，
                    // 替代版本归档（三选一互斥）；改名后目标侧空位，块级增量自然降级整文件
                    if (NeedConflictCopy() && File.Exists(LongPath(targetPath)))
                    {
                        MakeConflictCopy(targetPath);
                        CopyOne(entrySrc, targetPath);
                        if (_job.VersionKeepCount > 0)
                            BuildBaselineAfterCopy(targetPath);
                        return (true, entry.Size);
                    }
                    // 块级增量（开关开 + 够阈值 + 双闸全过）：内部已含归档+时间戳+rename+基线更新
                    if (_job.DeltaSync && entry.Size >= DeltaMinBytes && DeltaTransfer(entrySrc, targetPath))
                        return (true, entry.Size);
                    // 降级/关闭/小文件：现状整文件路径
                    // 旧目标版本化(N>0)由 CopyOne 在 tmp 完整后执行（原子窗口见其注释），
                    // 再 tmp+rename 原子替换（不再先删旧目标，一次中断不丢两代数据）
                    bool archived = false;
                    if (_job.VersionKeepCount > 0 && File.Exists(LongPath(targetPath)))
                        CopyOne(entrySrc, targetPath, () => archived = ArchiveFileSafe(targetPath));
                    else
                        CopyOne(entrySrc, targetPath);
                    // E1 归档顺带建基线：整文件落地后切目标建块表，下轮 Update 直接命中基线，
                    // 省「首次增量现场切目标」整遍读（代价=本轮多读目标一遍，时间转移）；
                    // keep=0 维持现状；归档失败不建（异常时保守不添乱）
                    if (archived)
                        BuildBaselineAfterCopy(targetPath);
                    return (true, entry.Size);

                case SyncAction.Conflict:
                    if (_job.Direction == SyncDirection.TwoWay)
                        return (false, 0);   // 双向 Manual/类型冲突：不自动动，等人工裁决
                    // 单向冲突（同 mtime 内容不同）：按源覆盖目标（旧目标同样先版本化；可用块级增量）
                    if (entry.IsDirectory) return (true, 0);
                    if (Directory.Exists(LongPath(targetPath)))
                    {
                        if (DeleteOne(targetPath, isDirectory: true, entry.Action)) Interlocked.Increment(ref _deletedCount);
                    }
                    // B3 大小写写法统一：目标侧可能存在与源仅大小写不同的旧文件（README.md vs readme.md）。
                    // rename 覆盖语义三平台不一致——NTFS 换用新名字、APFS 保留旧目录项、敏感卷直接并存；
                    // 先显式删除 IgnoreCase 命中的异写法旧文件，统一按源写法落地
                    DeleteCaseVariants(targetPath, entry.Action);
                    if (_job.DeltaSync && entry.Size >= DeltaMinBytes && DeltaTransfer(entrySrc, targetPath))
                        return (true, entry.Size);
                    bool conflictArchived = false;
                    if (_job.VersionKeepCount > 0 && File.Exists(LongPath(targetPath)))
                        CopyOne(entrySrc, targetPath, () => conflictArchived = ArchiveFileSafe(targetPath));
                    else
                        CopyOne(entrySrc, targetPath);
                    if (conflictArchived)
                        BuildBaselineAfterCopy(targetPath);
                    return (true, entry.Size);

                case SyncAction.DeleteRight:
                case SyncAction.DeleteLeft:
                    if (DeleteOne(targetPath, entry.IsDirectory, entry.Action)) Interlocked.Increment(ref _deletedCount);
                    return (true, 0);

                case SyncAction.Move:
                {
                    // A3 移动/重命名：目标侧内部 rename（同根必同卷，瞬时）。物理侧由配对来源决定
                    // （双向两侧都可能是写入方），不走 MapFor。内容未变 → 跳过版本归档与基线重算，
                    // 基线块表只换路径键；时间戳 rename 天然保留
                    var sideRoot = entry.MoveOnRight ? _job.RightPath : _job.LeftPath;
                    var fromRel = (entry.FromPath ?? "").Replace('/', Path.DirectorySeparatorChar);
                    var toRel = entry.RelativePath.Replace('/', Path.DirectorySeparatorChar);
                    var fromFull = Path.Combine(sideRoot, fromRel);
                    var toFull = Path.Combine(sideRoot, toRel);
                    if (!IsUnderRoot(Path.GetFullPath(fromFull), sideRoot) || !IsUnderRoot(Path.GetFullPath(toFull), sideRoot))
                        throw new IOException($"移动条目路径越出任务根（疑似数据被篡改）: {entry.FromPath} → {entry.RelativePath}");
                    Directory.CreateDirectory(LongPath(Path.GetDirectoryName(toFull)!));
                    File.Move(LongPath(fromFull), LongPath(toFull));   // 目标不存在（Create 前提），被占/外部竞态按条目失败
                    BaselineStore.RenameEntry(sideRoot,
                        entry.FromPath!.Replace('\\', '/'), entry.RelativePath);
                    Interlocked.Increment(ref _movedCount);
                    return (true, 0);
                }

                case SyncAction.None:
                    return (false, 0);

                default:
                    return (false, 0);
            }
        }

        /// <summary>删除与 targetPath 同名但大小写写法不同的文件（IgnoreCase 命中、Ordinal 不同）。
        /// 大小写不敏感卷上 rename 覆盖不会更新目录项写法（APFS 保留旧名），敏感卷上会并存两个文件——
        /// 两者都达不到「按源写法纠正」的目的，先删旧写法再落地才三平台一致。目录名写法差异罕见，不处理。</summary>
        private void DeleteCaseVariants(string targetPath, SyncAction origAction)
        {
            try
            {
                var dir = Path.GetDirectoryName(LongPath(targetPath));
                if (dir == null) return;
                var name = Path.GetFileName(targetPath);
                foreach (var hit in Directory.EnumerateFiles(dir, name,
                             new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, RecurseSubdirectories = false }))
                {
                    if (!string.Equals(Path.GetFileName(hit), name, StringComparison.Ordinal))
                        DeleteOne(hit, isDirectory: false, origAction, immediate: true);
                }
            }
            catch { /* 尽力而为：删不掉时按原语义走（rename 覆盖/失败链路处理） */ }
        }

        /// <summary>E1 归档顺带建基线：整文件落地后切目标建块表（DeltaSync 开且 ≥ 增量阈值才有意义——
        /// 小文件/关增量时永远走整文件，基线无用）。下轮 Update 直接 GetEntry 命中，省「首次增量
        /// 现场切目标」的整遍额外读（代价=本轮多读目标一遍，时间转移）。基线是缓存：任何失败静默。</summary>
        private void BuildBaselineAfterCopy(string targetPath)
        {
            if (!_job.DeltaSync) return;
            try
            {
                var dstL = LongPath(targetPath);
                if (!File.Exists(dstL) || new FileInfo(dstL).Length < DeltaMinBytes) return;
                var root = RootOf(targetPath);
                var rel = targetPath[root.TrimEnd('\\').Length..].TrimStart('\\', '/').Replace('\\', '/');
                var mtime = File.GetLastWriteTimeUtc(dstL);
                var (chunks, sha, size) = ChunkFileForDelta(dstL);
                if (size == 0) return;
                BaselineStore.SaveFile(root, rel,
                    new BaselineFile { Size = size, MtimeUtc = mtime, FileSha = sha, ChunkCount = chunks.Count },
                    chunks.Select(c => new BaselineChunk { Offset = c.Offset, Len = c.Len, Hash = c.Hash }).ToList());
            }
            catch { /* 基线是缓存，丢一轮增量收益不丢数据 */ }
        }

        /// <summary>A2 重试趟：三趟 + 回收站刷批之后的轮末统一退避重试（轮末串行单线程，无并发）。
        /// 每轮先等退避间隔（尊重取消 token），再重跑队列剩余项；重试成功计入正常统计（RetriedOk+_okCount），
        /// 转成不可重试错误（如磁盘满）直接落 failed；全部轮次后仍失败的进失败明细（带「已自动重试」标注）。</summary>
        private void RunRetryPass(
            IProgress<ProgressInfo>? progress, CancellationToken ct, int planCount, long doneSoFar)
        {
            var pi = _pi;
            for (int round = 0; round < RetryDelays.Length && !_retryQueue.IsEmpty; round++)
            {
                if (_cancel || ct.IsCancellationRequested) break;
                pi.Phase = "retry";
                pi.CurrentItem = $"自动重试 {round + 1}/{RetryDelays.Length} 轮（{RetryDelays[round].TotalSeconds:F0}s 后）：待重试 {_retryQueue.Count} 项";
                pi.DoneItems = Math.Min(DoneSoFar(), planCount);
                progress?.Report(pi.Snapshot());
                Task.Delay(RetryDelays[round], ct).GetAwaiter().GetResult();   // OCE 直接上抛沿取消链

                var still = new List<RetryCandidate>();
                while (_retryQueue.TryDequeue(out var cand))
                {
                    ct.ThrowIfCancellationRequested();
                    if (_cancel) { still.Add(cand); continue; }   // CopyOne 内部取消：留给 3 轮后统一落 failed
                    pi.Phase = "retry";
                    pi.CurrentItem = $"[重试] {cand.Entry.RelativePath}";
                    progress?.Report(pi.Snapshot());
                    try
                    {
                        var (entryOk, entryBytes) = ExecuteEntry(cand.Entry);
                        if (entryOk)
                        {
                            Interlocked.Increment(ref _okCount);
                            Interlocked.Add(ref _bytesAll, entryBytes);
                            Interlocked.Increment(ref _retriedOk);
                        }
                        // 跳过语义不该出现在重试路径（Conflict 双向从不进队），防御：按失败回队
                        else still.Add(cand);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                        || ex is Win32Exception || ex is DirectoryNotFoundException || ex is FileNotFoundException)
                    {
                        if (IsTransient(ex)) { still.Add(cand); continue; }   // 仍瞬时 → 下一轮
                        Interlocked.Increment(ref _failCount);   // 重试中变成确定性错误 → 直接失败
                        LastError = $"{cand.Entry.RelativePath}: {ex.Message}";
                        RecordFailure(cand.Entry.RelativePath, cand.Entry.Action.ToString(), ex.Message);
                        if (IsDiskFullEx(ex))
                        {
                            _diskFull = true;
                            _cancel = true;
                            LastError = $"目标磁盘空间不足，已中止重试（{cand.Entry.RelativePath}: {ex.Message}）";
                            foreach (var c2 in still) _retryQueue.Enqueue(c2);   // 剩余项留给收口路径按普通失败处理
                            return;
                        }
                    }
                }
                foreach (var c in still) _retryQueue.Enqueue(c);
            }

            // 退避轮次用尽仍有残留 → 落 failed，明细标注已重试次数
            while (_retryQueue.TryDequeue(out var cand))
            {
                Interlocked.Increment(ref _failCount);
                LastError = $"{cand.Entry.RelativePath}: {cand.Error.Message}";
                RecordFailure(cand.Entry.RelativePath, cand.Entry.Action.ToString(),
                    $"已自动重试 {RetryDelays.Length} 次仍失败: {cand.Error.Message}");
            }
        }

        public string? LastError { get; private set; }

        /// <summary>非致命警告（版本入库降级等），随轮次刷新</summary>
        public string? LastWarning { get; private set; }

        /// <summary>拷贝单个文件：先写同目录 tmp → 设源 mtime → rename 原子替换。
        /// 任何时刻（断电/取消/磁盘满）目标旧版本都完好，杜绝半截文件反向扑源。
        /// archiveOldTarget：旧目标版本化回调——挪到 tmp 完整之后、rename 之前执行，
        /// 消除「归档已删旧目标、新目标尚未落地」的中断空窗（tmp 在，数据不丢）。</summary>
        private void CopyOne(string src, string dst, Action? archiveOldTarget = null)
        {
            var srcL = LongPath(src);
            var dstL = LongPath(dst);
            Directory.CreateDirectory(Path.GetDirectoryName(dstL)!);
            var tmpL = dstL + Scanner.TmpSuffix;   // 同目录同卷：rename 语义，纳秒级

            var pi = _pi;
            var progress = _progress;
            var totalBytes = pi.TotalBytes;
            var lastCb = 0L;
            var dstName = Path.GetFileName(dst);

            try
            {
                bool ok = FileOps.CopyFile(srcL, tmpL, (total, transferred) =>
                {
                    var delta = transferred - lastCb;
                    lastCb = transferred;
                    if (delta > 0)
                    {
                        Interlocked.Add(ref _doneBytes, delta);
                        // UI 字段构造（速率/ETA/字符串）整体在 250ms 节流内：回调每 64-256KB 一次，
                        // 只在真要刷 UI 时才算，别在节流门外白构造字符串
                        if (total > 0 && (DateTime.Now - _lastUi).TotalMilliseconds > 250)
                        {
                            pi.DoneBytes = Math.Min(Interlocked.Read(ref _doneBytes), totalBytes);
                            pi.SpeedBytesPerSec = _doneBytes / Math.Max((DateTime.Now - StartTime).TotalSeconds, 0.1);
                            var eta = pi.SpeedBytesPerSec > 1024 && totalBytes > _doneBytes
                                ? TimeSpan.FromSeconds((totalBytes - _doneBytes) / pi.SpeedBytesPerSec)
                                : (TimeSpan?)null;
                            pi.CurrentItem = $"{dstName}  {FormatSize(transferred)}/{FormatSize(total)}"
                                + (eta.HasValue ? $"  ETA {eta.Value:hh\\:mm\\:ss}" : "")
                                + $"  {FormatSize(pi.SpeedBytesPerSec)}/s";
                            progress?.Report(pi.Snapshot());
                            _lastUi = DateTime.Now;
                        }
                    }
                    // 取消即时生效：token 触发（引擎 RequestCancel/UI _cts）立刻中止复制，不拷完整个文件
                    // （返回 false → 平台实现中止：Win=PROGRESS_CANCEL，Unix=停止读写循环）
                    return !(_cancel || _ct.IsCancellationRequested);
                }, out var err);

                if (!ok)
                {
                    if (_cancel || _ct.IsCancellationRequested) throw new OperationCanceledException("用户取消");
                    if (err == 1235) throw new OperationCanceledException($"复制中止 (win32={err})");
                    throw new Win32Exception(err, $"复制失败 (Win32 {err})");
                }
                // 中转件完整：带上源 mtime/ctime（创建时间），再原子替换目标（旧版本任何时刻都在）。
                // mtime 另有平台复制通道原生兜底；ctime 必须显式设（Win=SetFileTime / mac=setattrlist，
                // Linux 无 ctime 概念见方案 §6），设完再挪即保留
                FileOps.CopyTimestamps(srcL, tmpL);
                // 旧目标版本化（内部已删旧目标）在此刻才执行：与相邻 rename 的间隙只剩语句级，
                // 中断时 tmp 完整在场（版本库也留有旧版），不再出现目标凭空消失的窗口
                archiveOldTarget?.Invoke();
                // 目标带只读属性时 Move(overwrite) 拒绝访问：CopyFileEx 会把源只读属性原样带到落地文件，
                // 只读源的首次 Create 之后下轮 Update 必然 Access Denied（非瞬时错误不进重试）——覆盖前先清
                try { if (File.Exists(dstL)) File.SetAttributes(dstL, FileAttributes.Normal); } catch { }
                File.Move(tmpL, dstL, overwrite: true);
                // B1 复制后复核：读回目标全文 sha256 与源比对——不符=介质静默损坏（写坏盘/内存翻转），
                // 按该项失败处理（不静默放过，也不回滚——旧版本若开版本库仍有保留）
                if (_job.CopyVerify && !Sha256OfFile(dstL).Equals(Sha256OfFile(srcL), StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"复制后复核失败：目标读回内容与源不一致（疑似介质损坏）: {Path.GetFileName(dst)}");
            }
            finally
            {
                // 中断/失败路径统一清半截 tmp（正常路径此时 tmp 已改名，Exists=false 不动）
                try { if (File.Exists(tmpL)) { File.SetAttributes(tmpL, FileAttributes.Normal); File.Delete(tmpL); } }
                catch { /* 清残留尽力而为：锁死时留给下轮 ExecuteAsync 开头清扫 */ }
            }
        }

        private readonly DateTime StartTime = DateTime.Now;
        private DateTime _lastUi = DateTime.MinValue;

        #region 块级增量传输（v1.4）

        /// <summary>块级增量的最小文件阈值：小于此值整文件复制更快（切块 CPU + 随机读开销不值）。</summary>
        public const long DeltaMinBytes = 4L * 1024 * 1024;


        internal readonly record struct DeltaChunk(long Offset, int Len, string Hash);

        /// <summary>组装段：从目标旧文件（FromDst=true）或源读一段，写到 tmp 的指定位置。</summary>
        private readonly record struct DeltaSeg(bool FromDst, long Read, long Write, long Len);

        /// <summary>
        /// 块级增量传输：源与目标旧文件 CDC 切块对比，只把目标缺的块写进 tmp，
        /// 全文 sha256 终检通过才原子替换。返回 true=块级成功（归档/时间戳/rename/基线均已收尾）；
        /// false=降级整文件（目标未被动过，调用方走 CopyOne）。
        /// 双闸：①入口预检目标 (size,mtime) 须与基线一致；②产物全文 sha256 须等于源——
        /// 数学上最坏退化为整文件复制，永不产出与源不同的文件。
        /// </summary>
        private bool DeltaTransfer(string src, string dst)
        {
            var srcL = LongPath(src);
            var dstL = LongPath(dst);
            var sideRoot = RootOf(dst);
            var rel = dst[sideRoot.TrimEnd('\\').Length..].TrimStart('\\', '/').Replace('\\', '/');
            var tmpL = dstL + Scanner.TmpSuffix;
            try
            {
                if (!File.Exists(dstL)) return false;   // 目标旧文件必须存在可读（Create 不走这里）
                long dstSize = new FileInfo(dstL).Length;

                // ---- 第一道闸：预检（目标现状须与基线记录一致，防基线漂移；FAT 2s 粒度用同容差） ----
                long tol = _mtimeTolMs;
                var entry = BaselineStore.GetEntry(sideRoot, rel);   // 一次锁一次连接拿 meta+块表
                BaselineFile? meta = entry?.meta;
                List<BaselineChunk>? dstChunks = null;
                if (meta != null)
                {
                    var dmt = File.GetLastWriteTimeUtc(dstL);
                    if (dstSize != meta.Size || Math.Abs((dmt - meta.MtimeUtc).TotalMilliseconds) > tol)
                    {
                        // 对不上了（外部工具动过目标）：条目已失真，删掉让下轮现场重建，本轮整文件
                        BaselineStore.RemoveEntry(sideRoot, rel);
                        return false;
                    }
                    dstChunks = entry!.Value.chunks;
                }

                // ---- 源切块（顺带全文 sha256 累计） ----
                var (srcChunks, srcSha, srcSize) = ChunkFileForDelta(srcL);
                if (srcSize == 0) return false;   // 空文件不进块级（阈值已滤，防御）

                // ---- 目标旧块表：基线缺/脏 → 现场切目标（首次增量多读一遍目标，此后有缓存） ----
                if (dstChunks == null)
                {
                    var (liveChunks, liveSha, _) = ChunkFileForDelta(dstL);
                    if (meta != null && !string.Equals(liveSha, meta.FileSha, StringComparison.OrdinalIgnoreCase))
                    {
                        // size+mtime 骗过预检但内容与基线不符（罕见篡改）：基线脏，删条目降级
                        BaselineStore.RemoveEntry(sideRoot, rel);
                        return false;
                    }
                    dstChunks = liveChunks.Select(c => new BaselineChunk { Offset = c.Offset, Len = c.Len, Hash = c.Hash }).ToList();
                }

                // ---- 匹配：目标块多重集合（同 sha 多实例逐个消耗，全零块场景正确） ----
                var pool = new Dictionary<string, Queue<(long off, int len)>>();
                foreach (var c in dstChunks)
                {
                    if (!pool.TryGetValue(c.Hash, out var q)) pool[c.Hash] = q = new();
                    q.Enqueue((c.Offset, c.Len));
                }

                // ---- 写计划：命中块从目标旧文件拷、缺块读源；连续同源段合并（头尾不变区各一次大顺序读） ----
                var segs = new List<DeltaSeg>();
                long missStart = -1;
                foreach (var sc in srcChunks)
                {
                    DeltaSeg? hit = null;
                    if (pool.TryGetValue(sc.Hash, out var q) && q.Count > 0)
                    {
                        var (off, len) = q.Peek();
                        if (len == sc.Len) { q.Dequeue(); hit = new DeltaSeg(true, off, sc.Offset, len); }
                        // len 不等 = 同 sha 不同长（理论碰撞），按未命中处理
                    }
                    if (hit != null)
                    {
                        if (missStart >= 0) { AppendSeg(segs, new DeltaSeg(false, missStart, missStart, sc.Offset - missStart)); missStart = -1; }
                        AppendSeg(segs, hit.Value);
                    }
                    else if (missStart < 0) missStart = sc.Offset;
                }
                if (missStart >= 0) AppendSeg(segs, new DeltaSeg(false, missStart, missStart, srcSize - missStart));

                long needWrite = segs.Where(s => !s.FromDst).Sum(s => s.Len);
                if (needWrite >= srcSize) return false;   // 全是缺块（内容完全不同）：块级纯亏，整文件更快

                // ---- 组装 tmp：预分配 + 按段拷贝（目标旧文件句柄只在本块内持有，收尾 rename 不被占用） ----
                var buf = _deltaBufTl.Value ??= new byte[CdcChunker.MaxChunkSize];
                long written = 0;
                var dstName = Path.GetFileName(dst);
                using (var srcFs = new FileStream(srcL, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var dstFs = new FileStream(dstL, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var tmpFs = new FileStream(tmpL, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    tmpFs.SetLength(srcSize);
                    foreach (var seg in segs)
                    {
                        if (_cancel || _ct.IsCancellationRequested) throw new OperationCanceledException("块级增量已取消");
                        var from = seg.FromDst ? dstFs : srcFs;
                        from.Position = seg.Read;
                        tmpFs.Position = seg.Write;
                        long remain = seg.Len;
                        while (remain > 0)
                        {
                            var n = from.Read(buf, 0, (int)Math.Min(buf.Length, remain));
                            if (n <= 0) throw new EndOfStreamException($"增量读越界 {rel}");
                            tmpFs.Write(buf, 0, n);
                            remain -= n;
                            written += n;
                            Interlocked.Add(ref _doneBytes, n);
                        }
                        ReportDeltaProgress(dstName, written, needWrite, srcSize);
                    }
                }

                // ---- 第二道闸：终检（tmp 全文 sha 必须等于源；不过=基线脏/位腐，删基线降级） ----
                var tmpSha = Sha256OfFile(tmpL);
                if (!string.Equals(tmpSha, srcSha, StringComparison.OrdinalIgnoreCase))
                {
                    BaselineStore.RemoveEntry(sideRoot, rel);
                    return false;
                }

                // ---- 收尾：tmp 终检已过 → 补时间戳 → 归档旧目标（与 rename 仅隔语句，消中断空窗）→ 原子替换 ----
                var srcMtime = File.GetLastWriteTimeUtc(srcL);
                FileOps.CopyTimestamps(srcL, tmpL);
                if (_job.VersionKeepCount > 0 && File.Exists(dstL))
                    ArchiveFileSafe(dst);
                // 目标只读时 Move(overwrite) 拒绝访问（与整文件路径同坑）：覆盖前先清
                try { File.SetAttributes(dstL, FileAttributes.Normal); } catch { }
                File.Move(tmpL, dstL, overwrite: true);

                // ---- 基线更新（下次增量直接对比，免现场切目标） ----
                BaselineStore.SaveFile(sideRoot, rel, new BaselineFile
                {
                    Size = srcSize,
                    MtimeUtc = srcMtime,
                    FileSha = srcSha,
                    ChunkCount = srcChunks.Count
                }, srcChunks.Select(c => new BaselineChunk { Offset = c.Offset, Len = c.Len, Hash = c.Hash }).ToList());

                Interlocked.Add(ref _deltaSaved, srcSize - needWrite);
                return true;
            }
            catch (OperationCanceledException) { throw; }   // 取消沿现有语义上抛
            catch (Win32Exception) { throw; }               // 磁盘满中止链（CopyFileEx 同款）继续生效
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 112 or 395)
            {
                throw new Win32Exception(ex.HResult & 0xFFFF, $"目标磁盘空间不足（块级增量）: {rel}");
            }
            catch { return false; }   // 任何异常降级整文件：基线库故障绝不阻断同步
            finally
            {
                try { if (File.Exists(tmpL)) { File.SetAttributes(tmpL, FileAttributes.Normal); File.Delete(tmpL); } }
                catch { /* 半截 tmp 尽力而为：锁死时留给 SweepTmpResidue 兜底 */ }
            }
        }

        /// <summary>段追加：与上一段同源且读写位置都衔接 → 合并（碎片随机读变整段顺序读）。</summary>
        private static void AppendSeg(List<DeltaSeg> segs, DeltaSeg seg)
        {
            if (segs.Count > 0)
            {
                var last = segs[^1];
                if (last.FromDst == seg.FromDst && last.Read + last.Len == seg.Read && last.Write + last.Len == seg.Write)
                {
                    segs[^1] = last with { Len = last.Len + seg.Len };
                    return;
                }
            }
            segs.Add(seg);
        }

        /// <summary>切块 + 全文 sha256 累计。块级哈希与全文哈希独立累计（每次遍历一遍流）；缓冲跨文件复用。</summary>
        private (List<DeltaChunk> chunks, string sha, long size) ChunkFileForDelta(string pathL)
        {
            var chunks = new List<DeltaChunk>();
            using var fs = new FileStream(pathL, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sha = SHA256.Create();
            var buf = _chunkBufTl.Value ??= new byte[CdcChunker.MaxChunkSize];
            var rb = _chunkReadBufTl.Value ??= new byte[80 * 1024];
            long off = 0;
            foreach (var chunk in CdcChunker.Chunk(fs, buf, rb))
            {
                if (_cancel || _ct.IsCancellationRequested) throw new OperationCanceledException("块级增量已取消");
                sha.TransformBlock(chunk, 0, chunk.Length, null, 0);
                chunks.Add(new DeltaChunk(off, chunk.Length,
                    Convert.ToHexString(SHA256.HashData(chunk)).ToLowerInvariant()));
                off += chunk.Length;
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return (chunks, Convert.ToHexString(sha.Hash!).ToLowerInvariant(), off);
        }

        /// <summary>全文 sha256（长路径直读；深度校验/复核共用）。</summary>
        internal static string Sha256OfFile(string pathL)
        {
            using var fs = new FileStream(pathL, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }

        private void ReportDeltaProgress(string name, long written, long need, long total)
        {
            var pi = _pi;
            var progress = _progress;
            pi.DoneBytes = Math.Min(Interlocked.Read(ref _doneBytes), pi.TotalBytes);
            pi.CurrentItem = $"[增量] {name}  已传 {FormatSize(written)}/{FormatSize(need)}（共 {FormatSize(total)}）";
            if ((DateTime.Now - _lastUi).TotalMilliseconds > 250)
            {
                progress?.Report(pi.Snapshot());
                _lastUi = DateTime.Now;
            }
        }

        #endregion

        /// <summary>ERROR_DISK_FULL(112) / ERROR_DISK_QUOTA_EXCEEDED(395)：Win32Exception 看
        /// NativeErrorCode，IOException（File.Move/流写入）看 HResult 低 16 位。</summary>
        private static bool IsDiskFullEx(Exception ex) =>
            ex is Win32Exception w && (w.NativeErrorCode == 112 || w.NativeErrorCode == 395)
            || ex is IOException io && (io.HResult & 0xFFFF) is 112 or 395;

        /// <summary>D1：本 Update 是否按冲突副本处理败者。双向任务的 Update 全部源自冲突裁决
        /// （ComputeTwoWay 仅 ResolveConflict 产 Update），策略=ConflictCopy 即败者改名保留；
        /// 人工裁决（ManuallyResolved）同样按此策略——败者去向三选一全局一致。</summary>
        private bool NeedConflictCopy() =>
            _job.Direction == SyncDirection.TwoWay && _job.ConflictPolicy == ConflictPolicy.ConflictCopy;

        /// <summary>败者改名 原名.sync-conflict-YYYYMMDD-HHMMSS.扩展 留在原目录（同目录 rename，瞬时）。
        /// 副本视作普通新文件参与后续同步（Syncthing 语义）。基线条目键随改名失效即删。</summary>
        private void MakeConflictCopy(string loserPath)
        {
            var dir = Path.GetDirectoryName(loserPath) ?? "";
            var name = Path.GetFileNameWithoutExtension(loserPath);
            var ext = Path.GetExtension(loserPath);
            var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var copyPath = Path.Combine(dir, $"{name}.sync-conflict-{ts}{ext}");
            if (File.Exists(LongPath(copyPath)))   // 同秒同目录多冲突（罕见）：序列号防御
                copyPath = Path.Combine(dir, $"{name}.sync-conflict-{ts}-2{ext}");
            File.Move(LongPath(loserPath), LongPath(copyPath));
            var root = RootOf(loserPath);
            BaselineStore.RemoveEntry(root,
                loserPath[root.TrimEnd('\\').Length..].TrimStart('\\', '/').Replace('\\', '/'));
        }

        /// <summary>删除单个条目。返回 true = 已删除（回收站文件为"已入队，刷盘核对"）；false = 本来就不存在。
        /// origAction：该删除的原始计划动作——回收站攒批失败重试时要还原成可执行的动作（不再写死 "RecycleDelete"）。</summary>
        private bool DeleteOne(string path, bool isDirectory, SyncAction origAction, bool immediate = false)
        {
            // SHFileOperation 不支持 \\?\ 前缀，回收站删除用原始路径
            if (isDirectory)
            {
                if (!Directory.Exists(LongPath(path))) return false;
                if (UseRecycleBin(path))
                    RecycleBinDelete(path, isDirectory: true);
                else
                {
                    ClearReadOnlyTree(LongPath(path));
                    Directory.Delete(LongPath(path), recursive: true);
                }
                return true;
            }
            else
            {
                if (!File.Exists(LongPath(path))) return false;
                // 基线条目随文件删除即失效（尽力而为：删除万一失败只是丢缓存，下轮重建）
                BaselineStore.RemoveEntry(RootOf(path),
                    path[RootOf(path).TrimEnd('\\').Length..].TrimStart('\\', '/').Replace('\\', '/'));
                // 版本保留(N>0) > 回收站 > 永久删：文件优先入版本库（入库即保护，不再叠回收站）
                if (_job.VersionKeepCount > 0 && ArchiveFileSafe(path)) return true;
                try { File.SetAttributes(LongPath(path), FileAttributes.Normal); } catch { }
                if (UseRecycleBin(path))
                {
                    if (immediate)
                    {
                        // 大小写旧写法清理等「本轮稍后要在同路径落地新文件」的场景不能攒批：
                        // 延迟删除会与 rename/新建竞争（不敏感卷上攒批条目轮末核对时命中新文件），立即删
                        RecycleBinDelete(path, isDirectory: false);
                        return true;
                    }
                    // SHFileOperation 逐文件调用极慢（shell 初始化），攒批一次删；
                    // 计数乐观 +1，删失败在 FlushRecycle 核对时回退。
                    // 双闸分批：条目数 500 或 pForm 累计字符 24K（32K 上限留余量）任一触顶即刷。
                    // D2：复制趟的类型冲突/D1 副本路径也会并发攒批——锁内入队，满批锁内刷
                    lock (_recycleGate)
                    {
                        _recyclePending.Add((path, origAction));
                        _recycleChars += path.Length + 1;
                        if (_recyclePending.Count >= 500 || _recycleChars >= 24_000) FlushRecycleLocked();
                    }
                    return true;
                }
                File.Delete(LongPath(path));
                return true;
            }
        }

        /// <summary>刷盘：攒批的回收站文件一次交给平台实现批量删（Win=拼一次 SHFileOperation；
        /// Unix=逐条 mv 进 XDG Trash），随后逐个核对存在性（攒批路径的 ok 是乐观计数，
        /// 核对失败必须同步回退，否则 ok+skipped+failed &gt; 计划数、runs 落库虚高）。</summary>
        private void FlushRecycle()
        {
            lock (_recycleGate) FlushRecycleLocked();
        }

        private void FlushRecycleLocked()
        {
            if (_recyclePending.Count == 0) return;
            var batch = _recyclePending.ToArray();
            _recyclePending.Clear();
            _recycleChars = 0;
            FileOps.RecycleDeleteBatch(batch.Select(p => p.path).ToList());
            foreach (var (p, origAction) in batch)
            {
                if (!File.Exists(LongPath(p))) continue;   // 已删掉
                try { FileOps.RecycleDelete(p, isDirectory: false); } catch { }
                if (File.Exists(LongPath(p)))
                {
                    Interlocked.Decrement(ref _deletedCount);       // 入队时的乐观计数回退
                    Interlocked.Increment(ref _deleteFailed);
                    LastError = $"{p}: 回收站删除失败";
                    var root = RootOf(p);
                    var relOf = p[root.TrimEnd('\\', '/').Length..];
                    RecordFailure(relOf.TrimStart('\\', '/').Replace('\\', '/'),
                        origAction.ToString(), "回收站删除失败");
                }
            }
        }

        /// <summary>单文件/目录回收站删除（版本归档跳过路径与目录直删用）。失败且条目仍在时抛 IOException。</summary>
        private void RecycleBinDelete(string path, bool isDirectory)
            => FileOps.RecycleDelete(path, isDirectory);

        /// <summary>网络路径判定（Win=UNC+映射盘符 WNet 解析；Unix 一期恒 false）。委托平台实现。
        /// 映射盘没有回收站，回收站删除会静默永久删除，必须按网络路径同等处理。</summary>
        private static bool IsNetworkPath(string path) => FileOps.IsNetworkPath(path);

        /// <summary>本条删除是否走回收站。网络路径（含映射盘符）无回收站，降级永久删并写警告——
        /// 用户开着「删除进回收站」却在网络侧无处找回，必须至少留下提示。</summary>
        private bool UseRecycleBin(string path)
        {
            if (!_job.DeleteToRecycleBin) return false;
            if (!IsNetworkPath(path)) return true;
            LastWarning = $"网络路径无回收站，本次为直接永久删除: {path}";
            return false;
        }

        /// <summary>长路径统一入口（Win=\\?\ 设备前缀，Unix=透传）。转发 FsPath；保留本名是既有调用面
        /// （引擎/测试大量引用），三处历史重复实现已收口。</summary>
        internal static string LongPath(string path) => FsPath.LongPath(path);

        public static string FormatSize(double bytes) => bytes switch
        {
            >= 1L << 30 => $"{bytes / (1L << 30):F2} GB",
            >= 1L << 20 => $"{bytes / (1L << 20):F1} MB",
            >= 1L << 10 => $"{bytes / (1L << 10):F1} KB",
            _ => $"{bytes:F0} B"
        };
    }
}
