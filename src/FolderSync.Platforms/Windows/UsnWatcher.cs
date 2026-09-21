using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using System.Runtime.Versioning;
using FolderSync.Core;

namespace FolderSync.Platforms.Windows
{
    /// <summary>
    /// USN Journal 实时监控：轮询 NTFS 变更日志（每秒）。
    /// 相比 FileSystemWatcher：无缓冲区溢出丢事件问题、系统级可靠。
    /// 非根目录的其他卷变更通过 FRN→路径解析过滤，只触发 watched 子树内的变更。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class UsnWatcher : IDisposable, Core.Platform.IRealtimeWatcher
    {
        #region Win32

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec,
            uint disp, uint flags, IntPtr tmpl);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr h, uint code, IntPtr inBuf, uint inSize,
            byte[] outBuf, uint outSize, out uint bytesReturned, IntPtr overlapped);

        private static IntPtr AllocStructToBytes(READ_USN_JOURNAL_DATA_V1 s)
        {
            var p = Marshal.AllocHGlobal(44);
            Marshal.WriteInt64(p, 0, (long)s.StartUsn);
            Marshal.WriteInt32(p, 8, unchecked((int)s.ReasonMask));
            Marshal.WriteInt32(p, 12, unchecked((int)s.ReturnOnlyOnClose));
            Marshal.WriteInt64(p, 16, (long)s.Timeout);
            Marshal.WriteInt64(p, 24, (long)s.BytesToWaitFor);
            Marshal.WriteInt64(p, 32, (long)s.UsnJournalID);
            Marshal.WriteInt16(p, 40, (short)s.MinMajorVersion);
            Marshal.WriteInt16(p, 42, (short)s.MaxMajorVersion);
            return p;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumePathNameW(string path, StringBuilder sb, uint len);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenFileById(IntPtr volume, IntPtr fileIdDescriptor,
            uint access, uint share, IntPtr secAttrs, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(IntPtr h, StringBuilder sb, uint len, uint flags);

        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        private const uint FSCTL_READ_USN_JOURNAL = 0x000900BB; // CTL_CODE(9,46,METHOD_NEITHER,ANY)
        private const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4; // CTL_CODE(9,61,METHOD_BUFFERED,ANY) → USN_JOURNAL_DATA_V0

        // USN reason bits（Winnt.h）
        private const uint R_DATA_OVERWRITE = 0x00000001;
        private const uint R_DATA_EXTEND = 0x00000002;
        private const uint R_DATA_TRUNCATION = 0x00000004;
        private const uint R_FILE_CREATE = 0x00000100;
        private const uint R_FILE_DELETE = 0x00000200;
        private const uint R_RENAME_OLD = 0x00001000;
        private const uint R_RENAME_NEW = 0x00002000;
        private const uint R_BASIC_INFO_CHANGE = 0x00008000;   // 时间戳/属性等基本信息变更
        private const uint WATCH_MASK = R_DATA_OVERWRITE | R_DATA_EXTEND | R_DATA_TRUNCATION |
            R_FILE_CREATE | R_FILE_DELETE | R_RENAME_OLD | R_RENAME_NEW | R_BASIC_INFO_CHANGE;
        // 不含 BASIC_INFO_CHANGE 时纯改时间戳/属性（touch/copy 保 mtime）不触发同步——FSW 兜底反而能捕

        [StructLayout(LayoutKind.Sequential)]
        private struct READ_USN_JOURNAL_DATA_V1
        {
            public ulong StartUsn;
            public uint ReasonMask;
            public uint ReturnOnlyOnClose;
            public ulong Timeout;
            public ulong BytesToWaitFor;
            public ulong UsnJournalID;
            public ushort MinMajorVersion;
            public ushort MaxMajorVersion;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FILE_ID_DESCRIPTOR
        {
            public uint dwSize;
            public int Type;   // ExtendedFileIdType = 2（实测本机 Type=0 全部 err=87，Type=2 + 24B 可用）
            public long FileId;      // 64 位 FRN 置于 FILE_ID_128 前 8 字节，后 8 字节补零
            public long Reserved;    // FILE_ID_128 高 64 位
        }

        #endregion

        private readonly string _root;
        private readonly string[] _excludes;
        private readonly Action _onChange;
        private readonly Action _onOverflow;
        private readonly Action? _onDead;
        private readonly Dictionary<ulong, string> _frnCache = new();
        private IntPtr _volume = IntPtr.Zero;
        private ulong _journalId;
        private ulong _lastUsn;
        private readonly CancellationTokenSource _cts = new();
        private readonly ManualResetEventSlim _loopExit = new(false);
        // 注：JournalID/NextUsn 由 FSCTL_QUERY_USN_JOURNAL 一次取回，无需 _journalKnown 标记

        public UsnWatcher(string watchPath, string excludePatterns, Action onChange, Action onOverflow,
            Action? onDead = null)
        {
            _root = Path.GetFullPath(watchPath).TrimEnd('\\', '/');
            _excludes = Scanner.SplitPatterns(excludePatterns);
            _onChange = onChange;
            _onOverflow = onOverflow;
            _onDead = onDead;

            if (!Directory.Exists(_root))
                throw new DirectoryNotFoundException($"目录不存在: {_root}");

            // S-4：构造失败路径必须回收已开的卷句柄——ResetJournal 失败（exFAT/FAT32、Journal
            // 被禁用等日常场景）从内部直接抛，旧代码的 CloseHandle 补偿分支永不可达；
            // WindowsWatcherFactory 每次降级重试都先泄一个，长驻进程反复重建监控持续积累
            try
            {
                _volume = OpenVolume(_root);
                if (_volume == IntPtr.Zero)
                    throw new NotSupportedException($"无法打开卷句柄（USN 仅支持本地 NTFS 卷）: {_root}");
                ResetJournal();
            }
            catch
            {
                if (_volume != IntPtr.Zero) { try { CloseHandle(_volume); } catch { } }
                _volume = IntPtr.Zero;
                throw;
            }

            _ = Task.Run(() => PollLoop(_cts.Token));
        }

        private static IntPtr OpenVolume(string path)
        {
            var sb = new StringBuilder(512);
            if (!GetVolumePathNameW(path, sb, 512)) return IntPtr.Zero;
            var vol = sb.ToString();                        // "C:\"
            if (vol.Length < 2 || vol[1] != ':') return IntPtr.Zero; // UNC 等非本地卷
            var h = CreateFileW(@"\\.\" + vol[0] + ":",
                GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
                OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == new IntPtr(-1))  // INVALID_HANDLE_VALUE
            {
                var err = Marshal.GetLastWin32Error();
                throw new NotSupportedException($"无法打开卷句柄 (win32={err}{(err == 5 ? "，需要管理员权限" : "")})");
            }
            return h;
        }

        /// <summary>定位 JournalID + NextUsn（S-4：失败一律抛出，无 false 返回路径——不再假装可返回）。</summary>
        private void ResetJournal()
        {
            // QUERY_USN_JOURNAL：一次拿全 JournalID + NextUsn
            var outBuf = new byte[64];
            if (!DeviceIoControl(_volume, FSCTL_QUERY_USN_JOURNAL, IntPtr.Zero, 0,
                    outBuf, (uint)outBuf.Length, out uint ret, IntPtr.Zero) || ret < 56)
            {
                var err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"USN Journal 查询失败 (win32={err})" +
                    (err == 1178 ? "（USN Journal 未启用）" : ""));
            }
            _journalId = BitConverter.ToUInt64(outBuf, 0);
            _lastUsn = BitConverter.ToUInt64(outBuf, 16); // NextUsn
        }

        private void PollLoop(CancellationToken ct)
        {
            var outBuf = new byte[64 * 1024];
            try
            {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var input = new READ_USN_JOURNAL_DATA_V1
                    {
                        StartUsn = _lastUsn,
                        ReasonMask = WATCH_MASK,
                        ReturnOnlyOnClose = 0,
                        Timeout = 0,
                        BytesToWaitFor = 0,
                        UsnJournalID = _journalId,
                        MinMajorVersion = 2,
                        MaxMajorVersion = 2
                    };
                    var inBuf = AllocStructToBytes(input);
                    uint ret;
                    try
                    {
                        if (!DeviceIoControl(_volume, FSCTL_READ_USN_JOURNAL, inBuf, 44,
                            outBuf, (uint)outBuf.Length, out ret, IntPtr.Zero))
                            throw new IOException($"FSCTL_READ_USN_JOURNAL 失败 (win32={Marshal.GetLastWin32Error()})");
                    }
                    finally { Marshal.FreeHGlobal(inBuf); }
                    if (ret < 8) throw new IOException("USN 返回头异常");

                    ulong nextUsn = BitConverter.ToUInt64(outBuf, 0);
                    bool fired = false;
                    int off = 8;
                    while (off + 60 <= (int)ret)
                    {
                        uint recLen = BitConverter.ToUInt32(outBuf, off);
                        if (recLen == 0 || off + recLen > ret) break;
                        ulong parentFrn = BitConverter.ToUInt64(outBuf, off + 16);
                        uint reason = BitConverter.ToUInt32(outBuf, off + 40);
                        ushort nameLen = BitConverter.ToUInt16(outBuf, off + 56);
                        ushort nameOff = BitConverter.ToUInt16(outBuf, off + 58);
                        string name = nameLen > 0 && off + nameOff + nameLen <= ret
                            ? Encoding.Unicode.GetString(outBuf, off + nameOff, nameLen) : "";

                        if ((reason & WATCH_MASK) != 0 && !fired)
                        {
                            if (ShouldFire(parentFrn, name)) fired = true;
                        }
                        off += (int)recLen;
                    }
                    _lastUsn = nextUsn;
                    if (fired) _onChange();
                    // 批满 = journal 有积压：立即续读（原固定 1s 等待把吞吐限死在 64KB/s，
                    // 大批量复制后的变更要排队几十秒~分钟才被看到——2026-09-12 实测洪流后恒定 20s 延迟）；
                    // 读空了才歇 1s（空闲时 CPU 零负担不变）
                    if (ret >= (uint)(outBuf.Length - 4096)) continue;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception)
                {
                    // 日志被截断/禁用/句柄失效 → 通知全量校准，重新定位 USN
                    try { _onOverflow(); } catch { }
                    Thread.Sleep(2000);
                    if (ct.IsCancellationRequested) break;
                    bool ok = false;
                    try { ResetJournal(); ok = true; } catch { }
                    if (!ok)
                    {
                        Thread.Sleep(5000);
                        try { ResetJournal(); ok = true; } catch { }
                        if (!ok)
                        {
                            // 连续失败，放弃（引擎层下次同步全量兜底）。
                            // 必须通知引擎：否则状态栏永远停在「实时监听中」，变更不再触发无人知晓
                            try { _onDead?.Invoke(); } catch { }
                            break;
                        }
                    }
                    continue;
                }
                ct.WaitHandle.WaitOne(1000);
            }
            }
            catch { /* Dispose 竞态（CTS/MRES 已释放后轮询线程才摸到）：静默退出 */ }
            finally
            {
                // 卷句柄由轮询线程统一关闭（Dispose 超时路径把所有权移交到这里）：
                // Dispose 先关、轮询线程最坏在 catch 里睡 7s 后醒来撞已关句柄，DeviceIoControl 假报溢出
                var vol = Interlocked.Exchange(ref _volume, IntPtr.Zero);
                if (vol != IntPtr.Zero) { try { CloseHandle(vol); } catch { } }
                try { _loopExit.Set(); } catch { /* 同上竞态窗口 */ }
            }
        }

        /// <summary>判断该记录是否属于 watched 子树且未被排除。</summary>
        private bool ShouldFire(ulong parentFrn, string name)
        {
            var parent = GetPathByFrn(parentFrn);
            // 父目录无法解析 → 多半是外部（子树外）的瞬时临时目录被建删；
            // 子树内真实变更必然伴随可解析的祖先记录（root 永远存在），
            // 盲目触发会让去抖定时器被无关事件流永远重置（已踩坑：同步饿死）
            if (parent == null) return false;
            if (!InRoot(parent)) return false;
            var rel = parent[_root.Length..].TrimStart('\\', '/').Replace('\\', '/');
            rel = rel.Length == 0 ? name : rel + "/" + name;
            return !Scanner.IsExcluded(rel, name, _excludes);
        }

        private bool InRoot(string path) =>
            path.Equals(_root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(_root + "\\", StringComparison.OrdinalIgnoreCase);

        /// <summary>FRN → 完整路径（OpenFileById + GetFinalPathNameByHandle，带缓存）。
        /// 缓存设上限：长驻进程无限增长会吃内存，且 FRN 在删除重建后可能被复用（旧路径会误判子树归属）。</summary>
        private string? GetPathByFrn(ulong frn)
        {
            if (_frnCache.TryGetValue(frn, out var cached)) return cached;
            if (_frnCache.Count > 100_000) _frnCache.Clear();   // 超限整表重建（宁可多解析几次，不保留可疑旧值）

            var fd = new FILE_ID_DESCRIPTOR
            {
                dwSize = (uint)Marshal.SizeOf<FILE_ID_DESCRIPTOR>(),
                Type = 2,
                FileId = (long)frn,
                Reserved = 0
            };
            var fdPtr = Marshal.AllocHGlobal(Marshal.SizeOf<FILE_ID_DESCRIPTOR>());
            Marshal.StructureToPtr(fd, fdPtr, false);
            try
            {
                var h = OpenFileById(_volume, fdPtr, GENERIC_READ,
                    FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                    IntPtr.Zero, FILE_FLAG_BACKUP_SEMANTICS);
                if (h == IntPtr.Zero || h == new IntPtr(-1)) return null;
                try
                {
                    var p = FinalPathFromHandle(h);
                    if (p == null) return null;
                    _frnCache[frn] = p;
                    return p;
                }
                finally { CloseHandle(h); }
            }
            finally { Marshal.FreeHGlobal(fdPtr); }
        }

        /// <summary>句柄 → 完整路径（去 \\?\ 前缀）。缓冲不足时 GetFinalPathNameByHandleW 返回
        /// 所需大小（含 null）且缓冲内容未定义——直接 ToString(0,len) 会抛 ArgumentOutOfRange，
        /// 该异常从 ShouldFire 一路上抛打断 PollLoop 的记录循环，_lastUsn 不推进，
        /// 下轮重读同一批记录再次抛 → 监听卡死在同批 USN 上反复全量校准（长路径卷实质失效）。
        /// 此处按返回值扩容重试一次，仍失败按不可解析处理（返回 null）。</summary>
        private static string? FinalPathFromHandle(IntPtr h)
        {
            var cap = 1024;
            var sb = new StringBuilder(cap);
            var len = GetFinalPathNameByHandleW(h, sb, (uint)cap, 0);
            if (len == 0) return null;
            if (len >= cap)
            {
                cap = (int)len + 1;
                sb = new StringBuilder(cap);
                len = GetFinalPathNameByHandleW(h, sb, (uint)cap, 0);
                if (len == 0 || len >= cap) return null;
            }
            var p = sb.ToString(0, (int)len);
            if (p.StartsWith(@"\\?\UNC\")) p = "\\" + p[7..];
            else if (p.StartsWith(@"\\?\")) p = p[4..];
            return p;
        }

        private int _disposed;   // W-2：二次 Dispose 时 _cts.Cancel() 撞已释放对象抛 ObjectDisposedException

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _cts.Cancel();
            // 等轮询线程退出，避免僵尸事件。超时（catch 里最坏睡 7s）不再关句柄/释放 CTS——
            // 所有权已移交 PollLoop 的 finally 自行清理，这里碰它会撞正在用的句柄
            if (!_loopExit.Wait(TimeSpan.FromSeconds(5))) return;
            _cts.Dispose();
            _loopExit.Dispose();
        }
    }
}
