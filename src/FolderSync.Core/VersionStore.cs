using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Dapper;
using Microsoft.Data.Sqlite;

namespace FolderSync.Core
{
    /// <summary>版本库中的文件条目（按 任务+相对路径 聚合）。</summary>
    public sealed class StoredFile
    {
        public long JobId { get; set; }
        public string RelPath { get; set; } = "";
        public int VersionCount { get; set; }
        public long TotalSize { get; set; }
        public string LatestTs { get; set; } = "";
    }

    /// <summary>文件的某一历史版本。</summary>
    public sealed class StoredVersion
    {
        public int Id { get; set; }
        public string Ts { get; set; } = "";
        public long Size { get; set; }
        public DateTime MtimeUtc { get; set; }
        public int ChunkCount { get; set; }
    }

    /// <summary>版本库统计（占用/逻辑量/去重率）。</summary>
    public sealed class RepoStats
    {
        public int VersionCount { get; set; }
        public long LogicalBytes { get; set; }   // Σ各版本原始大小
        public long StoredBytes { get; set; }    // Σ块落盘字节（含 1 字节格式头）
        public int ChunkCount { get; set; }
        public double DedupRatio => LogicalBytes <= 0 ? 0 : 1.0 - (double)StoredBytes / LogicalBytes;
    }

    internal sealed class ChunkRef
    {
        public string Hash { get; set; } = "";
        public int PlainLen { get; set; }
        public int Csize { get; set; }
    }

    /// <summary>
    /// 块级去重版本库（v1.3）：中心位置 <c>&lt;数据目录&gt;\versions\&lt;侧根哈希16位&gt;\repo\</c> 下
    /// objects 内容寻址块（文件名=SHA-256，首字节 0x00 直存 / 0x01 Brotli）+ index.db 元数据。
    /// 每侧根独立一库（双向任务两侧历史不混），共享同侧根的任务共用一库；侧键 = SHA-256(大写侧根)[..16]。
    /// 全静态接口：每操作短连接（Pooling=false）+ 每侧一把进程内互斥锁（同侧串行、异侧并行、跨进程 WAL 兜底）。
    /// 铁律：先数据后索引，索引事务提交成功才删原文件；任何失败原文件完好，调用方降级常规删除。
    /// </summary>
    public static class VersionStore
    {
        /// <summary>切块参数/块格式换代号：改 CdcChunker 参数或块格式必须 +1（老库拒绝写入，防错乱）。</summary>
        public const int FormatVersion = 1;
        private const byte FlagPlain = 0x00;
        private const byte FlagBrotli = 0x01;
        /// <summary>库根里记录侧根原文的标记文件（排查 hash 目录归属用）。</summary>
        private const string SideMarkerFile = "side.txt";

        private static readonly object LocksGate = new();
        private static readonly Dictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);

        private static SemaphoreSlim LockOf(string sideRoot)
        {
            // 键归一化（与库目录同键）：D:\data 与 D:\data\（或盘根 D:\ 与 D:）必须落到同一把锁，
            // 否则共享侧根的两个任务各持一把锁并发归档/GC → 版本悬挂
            var key = KeyOf(sideRoot);
            lock (LocksGate)
            {
                if (!Locks.TryGetValue(key, out var s)) Locks[key] = s = new SemaphoreSlim(1, 1);
                return s;
            }
        }

        /// <summary>测试注入的中心根（null=数据目录下 versions，见 AppPaths）。必须在首次使用 VersionStore 前设置。</summary>
        internal static string? CentralRootOverride;

        // 归档路径的切块缓冲（线程级复用：多侧并行归档互不踩；见 ArchiveFile）
        [ThreadStatic] private static byte[]? _chunkBuf;
        [ThreadStatic] private static byte[]? _readBuf;

        public static string CentralRoot => CentralRootOverride ?? Platform.AppPaths.VersionsRoot;

        /// <summary>侧根 → 中心库键：SHA-256(去尾分隔符+大写不变文化)[..16]，同侧大小写不敏感稳定。</summary>
        private static string KeyOf(string sideRoot)
        {
            var norm = sideRoot.TrimEnd('\\', '/').ToUpperInvariant();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(norm)));
            return hash[..16].ToLowerInvariant();
        }

        internal static string CentralStoreOf(string sideRoot) => Path.Combine(CentralRoot, KeyOf(sideRoot));

        /// <summary>库根固定在中心数据位置（同步文件夹零数据夹）。
        /// 历史注：曾有旧位置 &lt;侧根&gt;\_FolderSync_Versions 与启动迁移逻辑，按需求移除（单机无存量分发包）；
        /// Scanner 对该目录名的排除规则保留，防手工残留/外部回流被误同步。</summary>
        public static string StoreRootOf(string sideRoot) => CentralStoreOf(sideRoot);

        public static string RepoDirOf(string sideRoot) => Path.Combine(StoreRootOf(sideRoot), "repo");
        public static bool Exists(string sideRoot) => Directory.Exists(LongPath(RepoDirOf(sideRoot)));

        private static string LongPath(string p) => Platform.FsPath.LongPath(p);

        #region 索引库

        private static SqliteConnection OpenRepo(string sideRoot)
        {
            var store = StoreRootOf(sideRoot);   // 单次解析：隐藏属性/建库/索引共用同一根，中途不换位
            var repo = Path.Combine(store, "repo");
            // 库根目录设为隐藏：内部数据结构（哈希块/索引库）对用户无直接意义，
            // 资源管理器默认视图不可见防误动；程序自身访问不受 Hidden 影响。幂等（已设则跳过）
            try
            {
                var storeDi = Directory.CreateDirectory(LongPath(store));
                if ((storeDi.Attributes & FileAttributes.Hidden) == 0)
                    storeDi.Attributes |= FileAttributes.Hidden;
                var marker = Path.Combine(store, SideMarkerFile);
                if (!File.Exists(LongPath(marker))) File.WriteAllText(LongPath(marker), sideRoot);
            }
            catch { /* 属性/标记尽力而为（只读卷/权限不足不阻断同步） */ }
            Directory.CreateDirectory(LongPath(Path.Combine(repo, "objects")));
            var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = LongPath(Path.Combine(repo, "index.db")),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
            conn.Open();
            conn.Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;");
            conn.Execute(@"
CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS chunks(
    hash TEXT PRIMARY KEY,
    size INTEGER NOT NULL,
    csize INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS versions(
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    job_id INTEGER NOT NULL,
    rel_path TEXT NOT NULL,
    ts TEXT NOT NULL,
    size INTEGER NOT NULL,
    mtime_utc TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS idx_versions_path ON versions(job_id, rel_path, ts);
CREATE TABLE IF NOT EXISTS version_chunks(
    version_id INTEGER NOT NULL,
    seq INTEGER NOT NULL,
    hash TEXT NOT NULL,
    plain_len INTEGER NOT NULL,
    PRIMARY KEY(version_id, seq));
CREATE INDEX IF NOT EXISTS idx_vc_hash ON version_chunks(hash);");
            var ver = conn.ExecuteScalar<string>("SELECT value FROM meta WHERE key='format_version'");
            if (ver == null)
                conn.Execute("INSERT INTO meta(key,value) VALUES('format_version',@v) ON CONFLICT(key) DO UPDATE SET value=@v",
                    new { v = FormatVersion.ToString() });
            else if (int.Parse(ver) > FormatVersion)
                throw new IOException($"版本库格式过新（{ver} > {FormatVersion}），请升级程序后再写入");
            return conn;
        }

        #endregion

        #region 压缩

        // 魔数嗅探表：命中 → 块已压缩，直存（省 CPU 无收益）。判断依据是块首字节。
        private static readonly (byte[] magic, int offset)[] SkipMagic =
        {
            (new byte[] { 0x50, 0x4B, 0x03, 0x04 }, 0),                  // zip
            (new byte[] { 0x50, 0x4B, 0x05, 0x06 }, 0),                  // zip empty
            (new byte[] { 0x1F, 0x8B }, 0),                              // gzip
            (new byte[] { 0x89, 0x50, 0x4E, 0x47 }, 0),                  // png
            (new byte[] { 0xFF, 0xD8, 0xFF }, 0),                        // jpeg
            (new byte[] { 0x66, 0x74, 0x79, 0x70 }, 4),                  // mp4/mov (ftyp)
            (new byte[] { 0x49, 0x44, 0x33 }, 0),                        // mp3 (ID3)
            (new byte[] { 0x52, 0x61, 0x72, 0x21 }, 0),                  // rar
            (new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }, 0),      // 7z
            (new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 }, 0),      // xz
            (new byte[] { 0x52, 0x49, 0x46, 0x46 }, 0),                  // riff (avi/wav)
            (new byte[] { 0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66 }, 0),      // wmv/asf
            (new byte[] { 0x66, 0x4C, 0x61, 0x43 }, 0),                  // flac
            (new byte[] { 0x4F, 0x67, 0x67, 0x53 }, 0),                  // ogg
            (new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }, 0),                  // mkv/webm
            (new byte[] { 0x47, 0x49, 0x46, 0x38 }, 0),                  // gif
            (new byte[] { 0x25, 0x50, 0x44, 0x46 }, 0),                  // pdf (内部已压缩)
        };

        private static bool LooksCompressed(byte[] chunk)
        {
            foreach (var (magic, off) in SkipMagic)
            {
                if (chunk.Length < off + magic.Length) continue;
                var ok = true;
                for (int i = 0; i < magic.Length; i++)
                    if (chunk[off + i] != magic[i]) { ok = false; break; }
                if (ok) return true;
            }
            return false;
        }

        /// <summary>压缩单块（Brotli Fastest=q1）：不可压/无收益直存。返回 [格式头+载荷]。</summary>
        private static byte[] PackChunk(byte[] chunk, out int csize)
        {
            if (!LooksCompressed(chunk))
            {
                try
                {
                    using var ms = new MemoryStream(chunk.Length / 2 + 16);
                    using (var br = new BrotliStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                        br.Write(chunk, 0, chunk.Length);
                    var payload = ms.ToArray();
                    if (payload.Length < chunk.Length)   // 膨胀保险：无收益直存
                    {
                        csize = payload.Length + 1;
                        var packed = new byte[csize];
                        packed[0] = FlagBrotli;
                        Buffer.BlockCopy(payload, 0, packed, 1, payload.Length);
                        return packed;
                    }
                }
                catch { /* 压缩失败直存 */ }
            }
            csize = chunk.Length + 1;
            var plain = new byte[csize];
            plain[0] = FlagPlain;
            Buffer.BlockCopy(chunk, 0, plain, 1, chunk.Length);
            return plain;
        }

        private static byte[] UnpackChunk(byte[] raw, string hash)
        {
            if (raw.Length == 0) throw new InvalidDataException($"空块 {hash}");
            if (raw[0] == FlagPlain) return raw[1..];
            if (raw[0] == FlagBrotli)
            {
                using var src = new MemoryStream(raw, 1, raw.Length - 1);
                using var br = new BrotliStream(src, CompressionMode.Decompress);
                using var dst = new MemoryStream(raw.Length * 4);
                br.CopyTo(dst);
                return dst.ToArray();
            }
            throw new InvalidDataException($"未知块格式标记 {raw[0]} ({hash})");
        }

        #endregion

        #region 归档

        /// <summary>
        /// 文件归档进版本库：切块 → 去重落块 → 索引事务 → 删原文件。
        /// 返回 false = 归档失败（error 带原因），原文件完好，调用方降级常规删除路径。
        /// cancelled 回调返回 true 时抛 OperationCanceledException（同步取消，不走降级删除）。
        /// </summary>
        public static bool ArchiveFile(string sideRoot, string path, string relPath, long jobId, string ts,
            out string? error, Func<bool>? cancelled = null, Action<long, long>? progress = null)
        {
            error = null;
            var l = LockOf(sideRoot);
            l.Wait();
            try
            {
                var objectsDir = Path.Combine(RepoDirOf(sideRoot), "objects");
                using var conn = OpenRepo(sideRoot);

                // 1. 切块 + 内容寻址落块（已存在的块直接复用 = 去重）
                // 切块缓冲线程级复用（与 Executor 增量路径同一纪律）：免每文件 2MiB+80KiB 新分配
                var chunkRefs = new List<ChunkRef>();
                using (var fs = new FileStream(LongPath(path), FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var sha = SHA256.Create())
                {
                    foreach (var chunk in CdcChunker.Chunk(fs,
                        _chunkBuf ??= new byte[CdcChunker.MaxChunkSize], _readBuf ??= new byte[80 * 1024]))
                    {
                        if (cancelled?.Invoke() == true) throw new OperationCanceledException("版本入库已取消");
                        var hash = Convert.ToHexString(sha.ComputeHash(chunk)).ToLowerInvariant();
                        var objPath = Path.Combine(objectsDir, hash[..2], hash);
                        var csize = 0;
                        if (!File.Exists(LongPath(objPath)))
                        {
                            var packed = PackChunk(chunk, out csize);
                            Directory.CreateDirectory(LongPath(Path.GetDirectoryName(objPath)!));
                            // S-2：tmp 名必须含进程+随机唯一成分——LockOf 只是进程内信号量，CLI 与 GUI
                            // 并存跨进程归档同内容块时，确定的 tmp 路径会被对方的 FileMode.Create 截断，
                            // 半截内容随后被 Move 成哈希命名的正式块（版本数据永久不可还原）。
                            // 后缀仍收尾 .foldersync-tmp：SweepTmpResidue/GcOrphans 的清扫按后缀匹配不受影响
                            var tmp = $"{objPath}.{Environment.ProcessId}.{Guid.NewGuid():N}{Scanner.TmpSuffix}";
                            File.WriteAllBytes(LongPath(tmp), packed);
                            File.Move(LongPath(tmp), LongPath(objPath), overwrite: true);   // 同哈希必同内容，覆盖无损
                        }
                        else
                        {
                            csize = (int)new FileInfo(LongPath(objPath)).Length;
                        }
                        progress?.Invoke(fs.Position, fs.Length);
                        chunkRefs.Add(new ChunkRef { Hash = hash, PlainLen = chunk.Length, Csize = csize });
                    }
                }

                // 2. 索引事务（提交成功前原文件绝不动）
                var mtime = File.GetLastWriteTimeUtc(LongPath(path));
                using (var tx = conn.BeginTransaction())
                {
                    foreach (var g in chunkRefs.GroupBy(c => c.Hash))
                    {
                        var f = g.First();
                        conn.Execute("INSERT OR IGNORE INTO chunks(hash,size,csize) VALUES(@h,@s,@cs)",
                            new { h = f.Hash, s = f.PlainLen, cs = f.Csize }, tx);
                    }
                    var versionId = conn.ExecuteScalar<long>(@"
INSERT INTO versions(job_id,rel_path,ts,size,mtime_utc) VALUES(@j,@r,@t,@sz,@m);
SELECT last_insert_rowid();",
                        new { j = jobId, r = relPath, t = ts, sz = (long)chunkRefs.Sum(c => (long)c.PlainLen), m = mtime.ToString("o") }, tx);
                    foreach (var (c, seq) in chunkRefs.Select((c, i) => (c, i)))
                        conn.Execute("INSERT INTO version_chunks(version_id,seq,hash,plain_len) VALUES(@v,@q,@h,@p)",
                            new { v = versionId, q = seq, h = c.Hash, p = c.PlainLen }, tx);
                    tx.Commit();
                }

                // 3. 索引已落 → 删原文件（入库即保护，不叠回收站）。
                // 删除可能失败（文件被占用）且异常沿外层 catch 降级常规删除路径——
                // 原文件的 Hidden/System 属性不能被 SetAttributes(Normal) 顺手抹掉，失败时恢复
                var lp = LongPath(path);
                FileAttributes origAttr = default;
                try { origAttr = File.GetAttributes(lp); } catch { }
                File.SetAttributes(lp, FileAttributes.Normal);
                try { File.Delete(lp); }
                catch
                {
                    try { if (origAttr != default) File.SetAttributes(lp, origAttr); } catch { }
                    throw;
                }
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { error = ex.Message; return false; }
            finally { l.Release(); }
        }

        #endregion

        #region 查询

        /// <summary>文件列表（按 任务+相对路径 聚合，最近版本时间降序）。</summary>
        public static List<StoredFile> ListFiles(string sideRoot, long? jobId = null)
        {
            using var conn = OpenRepo(sideRoot);
            return conn.Query<StoredFile>(@"
SELECT job_id AS JobId, rel_path AS RelPath, COUNT(*) AS VersionCount,
       SUM(size) AS TotalSize, MAX(ts) AS LatestTs
FROM versions
WHERE (@j IS NULL OR job_id=@j)
GROUP BY job_id, rel_path
ORDER BY LatestTs DESC", new { j = jobId }).ToList();
        }

        /// <summary>某文件全部版本（时间降序）。</summary>
        public static List<StoredVersion> ListVersions(string sideRoot, long jobId, string relPath)
        {
            using var conn = OpenRepo(sideRoot);
            return conn.Query<StoredVersion>(@"
SELECT v.id AS Id, v.ts AS Ts, v.size AS Size, v.mtime_utc AS MtimeUtc, COUNT(vc.seq) AS ChunkCount
FROM versions v LEFT JOIN version_chunks vc ON vc.version_id=v.id
WHERE v.job_id=@j AND v.rel_path=@r
GROUP BY v.id ORDER BY v.ts DESC, v.id DESC", new { j = jobId, r = relPath }).ToList();
        }

        /// <summary>库统计（版本数/逻辑量/落盘量）。库不存在返回 null。</summary>
        public static RepoStats? Stats(string sideRoot)
        {
            if (!Exists(sideRoot)) return null;
            try
            {
                using var conn = OpenRepo(sideRoot);
                var v = conn.QuerySingle<(int cnt, long logical)>("SELECT COUNT(*), COALESCE(SUM(size),0) FROM versions");
                var c = conn.QuerySingle<(int cnt, long stored)>("SELECT COUNT(*), COALESCE(SUM(csize),0) FROM chunks");
                return new RepoStats { VersionCount = v.cnt, LogicalBytes = v.logical, ChunkCount = c.cnt, StoredBytes = c.stored };
            }
            catch { return null; }
        }

        #endregion

        #region 还原

        /// <summary>
        /// 还原版本到目标路径：逐块解压 + SHA-256 校验全部通过 → tmp + rename 原子落地 + 恢复 mtime。
        /// 任一块校验失败中止并清 tmp，绝不落地半截文件。
        /// </summary>
        public static void RestoreVersion(string sideRoot, int versionId, string destPath)
        {
            var l = LockOf(sideRoot);
            l.Wait();
            try
            {
                using var conn = OpenRepo(sideRoot);
                var chunks = conn.Query<ChunkRef>(@"
SELECT vc.hash AS Hash, vc.plain_len AS PlainLen, c.csize AS Csize
FROM version_chunks vc JOIN chunks c ON c.hash=vc.hash
WHERE vc.version_id=@id ORDER BY vc.seq", new { id = versionId }).ToList();
                var meta = conn.QuerySingle<(string ts, string mtime, long size)>(
                    "SELECT ts, mtime_utc, size FROM versions WHERE id=@id", new { id = versionId });
                // 0 块有两种：空文件（切块器对 0 字节产 0 块，属正常）与真库损坏，按 size 区分
                if (chunks.Count == 0 && meta.size != 0)
                    throw new IOException($"版本 {versionId} 无块记录（库损坏）");
                // JOIN 丢行终检：version_chunks 行在而 chunks 行缺（索引库部分损坏/外部改动）时 JOIN 静默丢块，
                // 残缺文件不能靠下面的逐块校验拦住（它们只查「在场」的块）——按行数对账，宁可拒绝不可残缺
                var vcRows = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM version_chunks WHERE version_id=@id",
                    new { id = versionId });
                if (chunks.Count != vcRows)
                    throw new IOException($"版本 {versionId} 块索引不完整（{vcRows} 行只装配出 {chunks.Count} 块，库损坏），拒绝还原");
                // 总长终检：ΣPlainLen == meta.size，任何块表/元数据不一致在此拦下
                if (chunks.Sum(c => (long)c.PlainLen) != meta.size)
                    throw new IOException($"版本 {versionId} 块总长 {chunks.Sum(c => (long)c.PlainLen)} ≠ 记录大小 {meta.size}（库损坏），拒绝还原");
                var mtimeUtc = DateTime.Parse(meta.mtime, null, System.Globalization.DateTimeStyles.RoundtripKind);

                var objectsDir = Path.Combine(RepoDirOf(sideRoot), "objects");
                var tmp = destPath + Scanner.TmpSuffix;
                try
                {
                    using var sha = SHA256.Create();
                    using (var outFs = new FileStream(LongPath(tmp), FileMode.Create, FileAccess.Write))
                    {
                        foreach (var c in chunks)
                        {
                            var objPath = Path.Combine(objectsDir, c.Hash[..2], c.Hash);
                            if (!File.Exists(LongPath(objPath)))
                                throw new FileNotFoundException($"块文件缺失: {c.Hash[..12]}（版本库被外部改动？）");
                            var raw = File.ReadAllBytes(LongPath(objPath));
                            var plain = UnpackChunk(raw, c.Hash);
                            if (plain.Length != c.PlainLen)
                                throw new InvalidDataException($"块长度不符 {c.Hash[..12]}: {plain.Length}≠{c.PlainLen}");
                            var hex = Convert.ToHexString(sha.ComputeHash(plain)).ToLowerInvariant();
                            if (hex != c.Hash)
                                throw new InvalidDataException($"块校验失败 {c.Hash[..12]}（数据损坏，拒绝落地）");
                            outFs.Write(plain, 0, plain.Length);
                        }
                    }
                    File.SetLastWriteTimeUtc(LongPath(tmp), mtimeUtc);
                    File.Move(LongPath(tmp), LongPath(destPath), overwrite: true);
                }
                finally
                {
                    try { if (File.Exists(LongPath(tmp))) File.Delete(LongPath(tmp)); } catch { }
                }
            }
            finally { l.Release(); }
        }

        #endregion

        #region 清理与 GC

        /// <summary>删除单版本（版本浏览器用），随后立即 GC 孤儿块。</summary>
        public static void DeleteVersion(string sideRoot, int versionId)
        {
            var l = LockOf(sideRoot);
            l.Wait();
            try
            {
                using (var conn = OpenRepo(sideRoot))
                using (var tx = conn.BeginTransaction())
                {
                    conn.Execute("DELETE FROM version_chunks WHERE version_id=@id", new { id = versionId }, tx);
                    conn.Execute("DELETE FROM versions WHERE id=@id", new { id = versionId }, tx);
                    tx.Commit();
                }
                GcOrphans(sideRoot, conn: null);
            }
            finally { l.Release(); }
        }

        /// <summary>
        /// 全量清理（SyncEngine 同步成功后异步调用）：新库保 N 代 → GC 孤儿块进 trash → 清上轮 trash。
        /// keepCount≤0（功能关闭）不裁版本行（历史数据留给用户手动清），但孤儿块 GC 与 trash 照跑。
        /// 同侧 3 分钟节流：实时任务高频轮次下每轮裁剪无意义，攒批做。
        /// 尽力而为不抛异常。
        /// </summary>
        private static readonly object PruneGate = new();
        private static readonly Dictionary<string, DateTime> PruneAt = new(StringComparer.OrdinalIgnoreCase);
        internal static TimeSpan PruneThrottle = TimeSpan.FromMinutes(3);   // 测试可调

        public static void PruneAll(string sideRoot, int keepCount)
        {
            lock (PruneGate)
            {
                if (PruneAt.TryGetValue(sideRoot, out var at) && DateTime.UtcNow - at < PruneThrottle) return;
                PruneAt[sideRoot] = DateTime.UtcNow;
            }
            var l = LockOf(sideRoot);
            l.Wait();
            try
            {
                if (Exists(sideRoot))
                {
                    using (var conn = OpenRepo(sideRoot))
                    {
                        if (keepCount > 0)
                        {
                            // 每组（job,rel_path）按 ts,id 降序保留前 N 代（窗口函数，原相关子查询 O(n²)）
                            var doomed = conn.Query<long>(@"
SELECT id FROM (
    SELECT id, ROW_NUMBER() OVER (PARTITION BY job_id, rel_path ORDER BY ts DESC, id DESC) AS rn
    FROM versions
) WHERE rn > @k", new { k = keepCount }).ToList();
                            if (doomed.Count > 0)
                            {
                                using var tx = conn.BeginTransaction();
                                // 分批 ~1000：Dapper 把 IN @ids 展开成 N 个绑定参数，超 SQLite
                                // SQLITE_MAX_VARIABLE_NUMBER(32766) 会抛异常被外层吞掉——
                                // 裁剪每轮同败、版本库无限膨胀且用户无感知（实时任务几个月即超限）
                                for (int i = 0; i < doomed.Count; i += 1000)
                                {
                                    var batch = doomed.Skip(i).Take(1000).ToList();
                                    conn.Execute("DELETE FROM version_chunks WHERE version_id IN @ids", new { ids = batch }, tx);
                                    conn.Execute("DELETE FROM versions WHERE id IN @ids", new { ids = batch }, tx);
                                }
                                tx.Commit();
                            }
                        }
                        GcOrphans(sideRoot, conn);
                    }
                    // 上一轮移入 trash 的孤儿块真删（跨一轮缓冲，防断电竞态）
                    var trash = Path.Combine(RepoDirOf(sideRoot), "trash");
                    if (Directory.Exists(LongPath(trash)))
                        foreach (var f in Directory.EnumerateFiles(LongPath(trash)))
                            try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { }
                }
            }
            catch { /* 清理尽力而为 */ }
            finally { l.Release(); }
        }

        /// <summary>GC：chunks 表零引用行 + objects 目录孤儿文件 → rename 进 trash（同卷瞬时）。</summary>
        /// <remarks>跨进程竞态防御：表外孤儿文件按 mtime 豁免最近 GcFreshFileExempt（双实例同侧归档窗口）。</remarks>
        private static readonly TimeSpan GcFreshFileExempt = TimeSpan.FromMinutes(10);

        private static void GcOrphans(string sideRoot, SqliteConnection? conn)
        {
            var owned = conn == null;
            try
            {
                conn ??= OpenRepo(sideRoot);
                var repo = RepoDirOf(sideRoot);
                var trash = Path.Combine(repo, "trash");
                Directory.CreateDirectory(LongPath(trash));

                // 1. 表内零引用块
                var unreferenced = conn.Query<string>(
                    "SELECT hash FROM chunks c WHERE NOT EXISTS(SELECT 1 FROM version_chunks vc WHERE vc.hash=c.hash)").ToList();
                foreach (var hash in unreferenced)
                {
                    var objPath = Path.Combine(repo, "objects", hash[..2], hash);
                    var lp = LongPath(objPath);
                    if (File.Exists(lp))
                    {
                        try { File.Move(lp, LongPath(Path.Combine(trash, hash)), overwrite: true); }
                        catch { continue; }
                    }
                    conn.Execute("DELETE FROM chunks WHERE hash=@h", new { h = hash });
                }

                // 2. objects 目录里表外孤儿（索引事务前断电残留）
                // mtime 豁免最近 10 分钟：双实例操作同一侧根时，另一进程可能正处在
                // 「块已落盘、索引未提交」窗口（SQLite 锁拦不住对象文件层）——新文件不当孤儿清
                var known = new HashSet<string>(
                    conn.Query<string>("SELECT hash FROM chunks"), StringComparer.OrdinalIgnoreCase);
                var objectsDir = Path.Combine(repo, "objects");
                if (Directory.Exists(LongPath(objectsDir)))
                    foreach (var f in Directory.EnumerateFiles(LongPath(objectsDir), "*", SearchOption.AllDirectories))
                    {
                        var name = Path.GetFileName(f);
                        if (!name.EndsWith(Scanner.TmpSuffix, StringComparison.OrdinalIgnoreCase) && known.Contains(name)) continue;
                        try
                        {
                            if (name.EndsWith(Scanner.TmpSuffix, StringComparison.OrdinalIgnoreCase))
                                File.Delete(f);   // 半截中转件直接清
                            else
                            {
                                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) < GcFreshFileExempt) continue;
                                File.Move(f, LongPath(Path.Combine(trash, name)), overwrite: true);
                            }
                        }
                        catch { }
                    }
            }
            catch { }
            finally { if (owned) conn?.Dispose(); }
        }

        /// <summary>清空版本库（UI 按钮）：关锁后整树删除（repo + side.txt 标记）。返回是否删了东西。</summary>
        public static bool ClearAll(string sideRoot)
        {
            var store = StoreRootOf(sideRoot);
            if (!Directory.Exists(LongPath(store))) return false;
            var l = LockOf(sideRoot);
            l.Wait();
            try
            {
                Directory.Delete(LongPath(store), recursive: true);
                return true;
            }
            finally { l.Release(); }
        }

        #endregion
    }
}
