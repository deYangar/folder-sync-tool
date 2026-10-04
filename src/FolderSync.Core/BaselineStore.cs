using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;

namespace FolderSync.Core
{
    /// <summary>基线条目（files 表一行）：目标侧文件上次块级同步成功后的画像。</summary>
    public sealed class BaselineFile
    {
        public long Size { get; set; }
        public DateTime MtimeUtc { get; set; }
        public string FileSha { get; set; } = "";
        public int ChunkCount { get; set; }
    }

    /// <summary>基线块记录（chunks 表一行，seq 即列表序）。</summary>
    public sealed class BaselineChunk
    {
        public long Offset { get; set; }
        public int Len { get; set; }
        public string Hash { get; set; } = "";
    }

    /// <summary>
    /// 块级增量同步的基线缓存（v1.4）：<c>&lt;数据目录&gt;\sync-index\&lt;侧根哈希16位&gt;\baseline.db</c>，
    /// 记录目标侧文件上次块级同步成功后的 CDC 块表，供下轮 Update 只传缺块。
    /// 囩「该侧文件现状」的描述，与任务无关——共享侧根的多任务共用一库，无 job_id。
    /// 铁律：基线只是缓存，永远不承担正确性——任何读失败返回 null（调用方降级整文件），
    /// 任何写失败静默吞（丢缓存不丢数据）；块级产物另有全文 sha256 终检数学兜底。
    /// 并发沿用版本库模式：每侧一把进程内互斥锁（同侧串行、异侧并行）+ WAL。
    /// </summary>
    public static class BaselineStore
    {
        /// <summary>格式/切块参数换代号：改 CdcChunker 参数必须同步升 VersionStore.FormatVersion 并换代号。</summary>
        public const int FormatVersion = 1;
        private const string ChunkerName = "buzhash48-v1";
        /// <summary>库根里记录侧根原文的标记文件（排查 hash 目录归属用，同版本库 side.txt 惯例）。</summary>
        private const string SideMarkerFile = "side.txt";

        private static readonly object LocksGate = new();
        private static readonly Dictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);

        private static SemaphoreSlim LockOf(string sideRoot)
        {
            // 键归一化（与库目录同键）：D:\data 与 D:\data\（或盘根 D:\ 与 D:）必须落到同一把锁，
            // 否则共享侧根的两个任务各持一把锁并发读写基线库
            var key = KeyOf(sideRoot);
            lock (LocksGate)
            {
                if (!Locks.TryGetValue(key, out var s)) Locks[key] = s = new SemaphoreSlim(1, 1);
                return s;
            }
        }

        // 侧内连接复用：每操作短连接曾让千文件增量轮开库 2000+ 次（open+PRAGMA+EnsureSchema 逐次重跑）。
        // 侧锁已保证同侧串行，连接按侧缓存即可；失效（库文件被删/损坏后操作抛错）由各调用方
        // catch 里 DiscardConn 丢弃，下次 ConnOf 重建——基线只是缓存，重建无损
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SqliteConnection> Conns =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>取该侧的复用连接（须持侧锁调用）。库文件不存在时仍会建库——调用方入口已有 File.Exists 防护。</summary>
        private static SqliteConnection ConnOf(string sideRoot)
        {
            if (Conns.TryGetValue(sideRoot, out var c) && c.State == System.Data.ConnectionState.Open)
                return c;
            DiscardConn(sideRoot);
            c = Open(sideRoot);
            Conns[sideRoot] = c;
            return c;
        }

        /// <summary>丢弃该侧缓存连接（须持侧锁；操作异常或删库前调用，下次 ConnOf 重建）。</summary>
        private static void DiscardConn(string sideRoot)
        {
            if (Conns.TryRemove(sideRoot, out var old)) old.Dispose();
        }

        /// <summary>测试注入的根（null=数据目录下 sync-index，见 AppPaths）。必须在首次使用 BaselineStore 前设置。</summary>
        internal static string? RootOverride;

        public static string RootBase => RootOverride ?? Platform.AppPaths.SyncIndexRoot;

        /// <summary>侧根 → 基线库键：SHA-256(去尾分隔符+大写不变文化)[..16]，与版本库同规则。</summary>
        private static string KeyOf(string sideRoot)
        {
            var norm = sideRoot.TrimEnd('\\', '/').ToUpperInvariant();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(norm)));
            return hash[..16].ToLowerInvariant();
        }

        public static string RootOf(string sideRoot) => Path.Combine(RootBase, KeyOf(sideRoot));
        private static string DbPathOf(string sideRoot) => Path.Combine(RootOf(sideRoot), "baseline.db");

        private static string LongPath(string p) => Platform.FsPath.LongPath(p);

        /// <summary>建库并开新连接（幂等；仅 ConnOf 内部使用）。库文件损坏（截断/野数据）时删库重建——基线只是缓存，重建无损。</summary>
        private static SqliteConnection Open(string sideRoot)
        {
            var root = RootOf(sideRoot);
            try
            {
                var rootDi = Directory.CreateDirectory(LongPath(root));
                if ((rootDi.Attributes & FileAttributes.Hidden) == 0)
                    rootDi.Attributes |= FileAttributes.Hidden;
                var marker = Path.Combine(root, SideMarkerFile);
                if (!File.Exists(LongPath(marker))) File.WriteAllText(LongPath(marker), sideRoot);
            }
            catch { /* 属性/标记尽力而为（只读卷不阻断：只读卷上建不了库，下面 Open 自会失败降级） */ }
            return OpenDb(sideRoot, allowRebuild: true);
        }

        /// <summary>读文件条目 + 块表（一次锁一次连接；块数与 meta 不符视为脏返回 null）。库不存在/任何异常 → null（调用方降级）。</summary>
        public static (BaselineFile meta, List<BaselineChunk> chunks)? GetEntry(string sideRoot, string relPath)
        {
            var l = LockOf(sideRoot);
            l.Wait();
            try
            {
                if (!File.Exists(LongPath(DbPathOf(sideRoot)))) { DiscardConn(sideRoot); return null; }
                var conn = ConnOf(sideRoot);
                var row = conn.Query<(long size, string mtime, string sha, int cnt)?>(
                    "SELECT size, mtime_utc, file_sha, chunk_count FROM files WHERE rel_path=@r",
                    new { r = relPath }).FirstOrDefault();
                if (row == null) return null;
                var (size, mtime, sha, cnt) = row.Value;
                var rows = conn.Query<(long off, int len, string hash)>(
                    "SELECT offset, len, hash FROM chunks WHERE rel_path=@r ORDER BY seq", new { r = relPath }).AsList();
                if (rows.Count != cnt) return null;   // 索引不完整=脏，降级
                return (new BaselineFile
                {
                    Size = size,
                    MtimeUtc = DateTime.Parse(mtime, null, System.Globalization.DateTimeStyles.RoundtripKind),
                    FileSha = sha,
                    ChunkCount = cnt
                }, rows.Select(x => new BaselineChunk { Offset = x.off, Len = x.len, Hash = x.hash }).ToList());
            }
            catch { DiscardConn(sideRoot); return null; }
            finally { l.Release(); }
        }

        /// <summary>开新连接（ConnOf 专用）：open + PRAGMA + EnsureSchema；损坏时删库重建（基线只是缓存，重建无损）。</summary>
        private static SqliteConnection OpenDb(string sideRoot, bool allowRebuild)
        {
            var db = DbPathOf(sideRoot);
            var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = LongPath(db),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
            try
            {
                conn.Open();
                conn.Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;");
                EnsureSchema(conn);
                return conn;
            }
            catch (Exception ex) when (allowRebuild && (ex is SqliteException || ex is IOException || ex is InvalidDataException))
            {
                conn.Dispose();
                // 库损坏（截断/野数据/参数换代）：删库重建。基线是缓存，重建只丢一轮增量收益，不丢任何数据。
                // 别的进程正开着库时删除会失败（占用），留给下次再试
                try
                {
                    foreach (var suffix in new[] { "", "-wal", "-shm" })
                    {
                        var p = LongPath(db + suffix);
                        if (File.Exists(p)) { File.SetAttributes(p, FileAttributes.Normal); File.Delete(p); }
                    }
                }
                catch { }
                var retry = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = LongPath(db),
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false
                }.ToString());
                try
                {
                    retry.Open();
                    retry.Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;");
                    EnsureSchema(retry);
                    return retry;
                }
                catch
                {
                    retry.Dispose();   // 重建再失败：连接必须释放，泄漏的句柄会让库文件永远删不掉
                    throw;
                }
            }
        }

        private static void EnsureSchema(SqliteConnection conn)
        {
            conn.Execute(@"
CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS files(
    rel_path TEXT PRIMARY KEY,
    size INTEGER NOT NULL,
    mtime_utc TEXT NOT NULL,
    file_sha TEXT NOT NULL,
    chunk_count INTEGER NOT NULL,
    updated_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS chunks(
    rel_path TEXT NOT NULL,
    seq INTEGER NOT NULL,
    offset INTEGER NOT NULL,
    len INTEGER NOT NULL,
    hash TEXT NOT NULL,
    PRIMARY KEY(rel_path, seq));
DROP INDEX IF EXISTS idx_chunks_hash;");   // 死索引：全部查询走 rel_path 前缀（PK），按 hash 无查询，纯写放大
            var chunker = conn.ExecuteScalar<string>("SELECT value FROM meta WHERE key='chunker'");
            if (chunker == null)
            {
                using var tx = conn.BeginTransaction();
                conn.Execute("INSERT OR IGNORE INTO meta(key,value) VALUES('format_version',@v),('chunker',@c),('mask_bits',@m)",
                    new { v = FormatVersion.ToString(), c = ChunkerName, m = CdcChunker.MaskBits.ToString() }, tx);
                tx.Commit();
            }
            else if (chunker != ChunkerName)
                throw new IOException($"基线库切块参数不符（{chunker} ≠ {ChunkerName}），需重建");
            else
            {
                // format_version 同步校验：改参数忘换代号时库被静默接受、增量静默失效，必须炸给重建路径
                var fv = conn.ExecuteScalar<string>("SELECT value FROM meta WHERE key='format_version'");
                if (fv != null && int.TryParse(fv, out var v) && v != FormatVersion)
                    throw new IOException($"基线库格式版本不符（{fv} ≠ {FormatVersion}），需重建");
            }
        }

        /// <summary>写入/替换条目（事务：先删旧行再插新）。尽力而为，失败静默（丢缓存不丢数据）。</summary>
        public static void SaveFile(string sideRoot, string relPath, BaselineFile meta, List<BaselineChunk> chunks)
        {
            var l = LockOf(sideRoot);
            l.Wait();
            try
            {
                var conn = ConnOf(sideRoot);
                using var tx = conn.BeginTransaction();
                conn.Execute("DELETE FROM chunks WHERE rel_path=@r; DELETE FROM files WHERE rel_path=@r;",
                    new { r = relPath }, tx);
                conn.Execute(@"
INSERT INTO files(rel_path,size,mtime_utc,file_sha,chunk_count,updated_at)
VALUES(@r,@sz,@m,@sha,@cnt,@u)",
                    new { r = relPath, sz = meta.Size, m = meta.MtimeUtc.ToString("o"),
                          sha = meta.FileSha, cnt = chunks.Count, u = DateTime.UtcNow.ToString("o") }, tx);
                conn.Execute("INSERT INTO chunks(rel_path,seq,offset,len,hash) VALUES(@r,@q,@Offset,@Len,@Hash)",
                    chunks.Select((c, i) => new { r = relPath, q = i, c.Offset, c.Len, c.Hash }), tx);
                tx.Commit();
            }
            catch
            {
                DiscardConn(sideRoot);   // 库可能已损坏：丢弃连接，下次 ConnOf 删库重建
            }
            finally { l.Release(); }
        }

        /// <summary>删条目（目标文件被删时调用）。尽力而为。</summary>
        public static void RemoveEntry(string sideRoot, string relPath)
        {
            var l = LockOf(sideRoot);
            l.Wait();
            try
            {
                if (!File.Exists(LongPath(DbPathOf(sideRoot)))) { DiscardConn(sideRoot); return; }
                var conn = ConnOf(sideRoot);
                conn.Execute("DELETE FROM chunks WHERE rel_path=@r; DELETE FROM files WHERE rel_path=@r;",
                    new { r = relPath });
            }
            catch { DiscardConn(sideRoot); }
            finally { l.Release(); }
        }

        /// <summary>移动/重命名的块表换键（v1.5）：文件内容未变只改路径 → files/chunks 的 rel_path
        /// 从 from 换到 to，块表零重算。事务内先清 to 防残留，尽力而为（失败=丢缓存，下轮现场重建）。</summary>
        public static void RenameEntry(string sideRoot, string fromRel, string toRel)
        {
            var l = LockOf(sideRoot);
            l.Wait();
            try
            {
                if (!File.Exists(LongPath(DbPathOf(sideRoot)))) { DiscardConn(sideRoot); return; }
                var conn = ConnOf(sideRoot);
                using var tx = conn.BeginTransaction();
                conn.Execute("DELETE FROM chunks WHERE rel_path=@to; DELETE FROM files WHERE rel_path=@to;",
                    new { to = toRel }, tx);
                conn.Execute("UPDATE chunks SET rel_path=@to WHERE rel_path=@from;" +
                             "UPDATE files SET rel_path=@to, updated_at=@u WHERE rel_path=@from;",
                    new { to = toRel, from = fromRel, u = DateTime.UtcNow.ToString("o") }, tx);
                tx.Commit();
            }
            catch { DiscardConn(sideRoot); }
            finally { l.Release(); }
        }

        /// <summary>
        /// 轮末对账：基线里 rel_path 不在现存集内的条目删除（手动绕过工具删文件后的死条目）。
        /// 磁盘确认后才删：存活集是单任务视角——共享侧根的其他任务条目、被排除规则滤掉的文件、
        /// 扫描时 ACL 不可达的文件都还在盘上，绝不能当死条目清掉（增量缓存反复误删）。
        /// 尽力而为，异步调用不阻塞同步。
        /// </summary>
        public static void Reconcile(string sideRoot, HashSet<string> liveRelPaths)
        {
            var l = LockOf(sideRoot);
            l.Wait();
            try
            {
                if (!File.Exists(LongPath(DbPathOf(sideRoot)))) { DiscardConn(sideRoot); return; }
                var conn = ConnOf(sideRoot);
                var dead = conn.Query<string>("SELECT DISTINCT rel_path FROM files")
                    .Where(p => !liveRelPaths.Contains(p))
                    .Where(p => !ExistsOnDisk(sideRoot, p))
                    .AsList();
                if (dead.Count == 0) return;
                // 分批 ~1000：IN @ids 展开超 SQLITE_MAX_VARIABLE_NUMBER(32766) 会抛异常，
                // 被 catch 吞掉后死条目每次同败、对账静默失效（与 VersionStore.PruneAll 同坑）
                for (int i = 0; i < dead.Count; i += 1000)
                {
                    var batch = dead.Skip(i).Take(1000).ToList();
                    conn.Execute("DELETE FROM chunks WHERE rel_path IN @d; DELETE FROM files WHERE rel_path IN @d;",
                        new { d = batch });
                }
            }
            catch { DiscardConn(sideRoot); }
            finally { l.Release(); }
        }

        /// <summary>磁盘确认（保守）：文件/目录任一存在即算存活；查询本身失败按"存在"处理（宁可漏删缓存，不误删）。</summary>
        private static bool ExistsOnDisk(string sideRoot, string relPath)
        {
            try
            {
                var full = Path.Combine(sideRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(LongPath(full)) || Directory.Exists(LongPath(full));
            }
            catch { return true; }
        }

        /// <summary>条目数（UI 统计用）。库不存在返回 0。</summary>
        public static long CountEntries(string sideRoot)
        {
            try
            {
                if (!File.Exists(LongPath(DbPathOf(sideRoot)))) { DiscardConn(sideRoot); return 0; }
                var l = LockOf(sideRoot);
                l.Wait();
                try
                {
                    var conn = ConnOf(sideRoot);
                    return conn.ExecuteScalar<long>("SELECT COUNT(*) FROM files");
                }
                catch { DiscardConn(sideRoot); return 0; }
                finally { l.Release(); }
            }
            catch { return 0; }
        }

        /// <summary>清空基线库（UI 按钮）：关连接（Windows 上句柄开着拒删）后整树删除（baseline.db + wal/shm + side.txt）。返回是否删了东西。</summary>
        public static bool ClearAll(string sideRoot)
        {
            var root = RootOf(sideRoot);
            if (!Directory.Exists(LongPath(root))) return false;
            var l = LockOf(sideRoot);
            l.Wait();
            try
            {
                DiscardConn(sideRoot);
                Directory.Delete(LongPath(root), recursive: true);
                return true;
            }
            finally { l.Release(); }
        }

        /// <summary>
        /// 清空全部站点的基线库（设置页按钮，全局语义）：遍历 sync-index 下所有侧根目录，
        /// 读 side.txt 恢复侧根原文后走各自锁删除（并发安全）；side.txt 缺失的孤儿目录直接删。
        /// 返回删除的站点数。不影响任何同步内容——基线只是缓存，下次同步大文件时自动重建。
        /// </summary>
        public static int ClearAllSites()
        {
            var baseDir = RootBase;
            if (!Directory.Exists(LongPath(baseDir))) return 0;
            int n = 0;
            foreach (var dir in Directory.EnumerateDirectories(LongPath(baseDir)))
            {
                try
                {
                    var marker = Path.Combine(dir, SideMarkerFile);
                    var sideRoot = File.Exists(LongPath(marker))
                        ? File.ReadAllText(LongPath(marker)).Trim()
                        : null;
                    if (!string.IsNullOrWhiteSpace(sideRoot) && ClearAll(sideRoot)) n++;
                    else
                    {
                        Directory.Delete(LongPath(dir), recursive: true);   // 孤儿目录（无标记）：直接删
                        n++;
                    }
                }
                catch { /* 单站点失败不阻断其余 */ }
            }
            return n;
        }

        /// <summary>全部站点的条目总数（设置页统计用）。</summary>
        public static long CountAllEntries()
        {
            var baseDir = RootBase;
            if (!Directory.Exists(LongPath(baseDir))) return 0;
            long total = 0;
            foreach (var dir in Directory.EnumerateDirectories(LongPath(baseDir)))
            {
                try
                {
                    var marker = Path.Combine(dir, SideMarkerFile);
                    if (!File.Exists(LongPath(marker))) continue;
                    var sideRoot = File.ReadAllText(LongPath(marker)).Trim();
                    if (!string.IsNullOrWhiteSpace(sideRoot)) total += CountEntries(sideRoot);
                }
                catch { }
            }
            return total;
        }
    }
}
