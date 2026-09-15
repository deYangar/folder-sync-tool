using System;
using System.Collections.Generic;
using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;

namespace FolderSync.Core
{
    /// <summary>SQLite 持久层：配置 / 运行历史。WAL 模式，随 exe 目录走。</summary>
    public class Db : IDisposable
    {
        private readonly SqliteConnection _conn;
        // SqliteConnection 实例非线程安全（多引擎并行跑时 InsertRun/SaveSnapshot 会撞，
        // 事务重叠直接抛 "connection does not support parallel transactions"）——全方法串行化
        private readonly object _sync = new();

        public Db(string? dbPath = null)
        {
            dbPath ??= Path.Combine(AppContext.BaseDirectory, "foldersync.db");
            var dir = Path.GetDirectoryName(dbPath)!;
            Directory.CreateDirectory(dir);
            _conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
            _conn.Open();
            _conn.Execute("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
            Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true; // left_path → LeftPath 等列名映射
            Migrate();
        }

        private void Migrate()
        {
            lock (_sync)
            {
                _conn.Execute(@"
CREATE TABLE IF NOT EXISTS jobs(
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    left_path TEXT NOT NULL,
    right_path TEXT NOT NULL,
    direction INTEGER NOT NULL DEFAULT 0,
    trigger_type INTEGER NOT NULL DEFAULT 0,
    interval_seconds INTEGER NOT NULL DEFAULT 7200,
    debounce_seconds INTEGER NOT NULL DEFAULT 10,
    exclude_patterns TEXT NOT NULL DEFAULT '',
    auto_start INTEGER NOT NULL DEFAULT 1,
    enabled INTEGER NOT NULL DEFAULT 1,
    delete_to_recycle_bin INTEGER NOT NULL DEFAULT 1,
    mirror_delete INTEGER NOT NULL DEFAULT 0,
    strict_mirror INTEGER NOT NULL DEFAULT 0,
    conflict_policy INTEGER NOT NULL DEFAULT 0,
    version_keep_count INTEGER NOT NULL DEFAULT 0,
    delta_sync INTEGER NOT NULL DEFAULT 1,
    auto_retry INTEGER NOT NULL DEFAULT 1,
    move_detect INTEGER NOT NULL DEFAULT 1,
    copy_verify INTEGER NOT NULL DEFAULT 0,
    deep_verify INTEGER NOT NULL DEFAULT 0,
    schedule_spec TEXT,
    copy_workers INTEGER NOT NULL DEFAULT 2,
    created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS runs(
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    job_id INTEGER NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
    started_at TEXT NOT NULL,
    finished_at TEXT,
    trigger TEXT NOT NULL DEFAULT 'manual',
    status TEXT NOT NULL DEFAULT 'running',
    scanned_files INTEGER NOT NULL DEFAULT 0,
    copied_files INTEGER NOT NULL DEFAULT 0,
    deleted_files INTEGER NOT NULL DEFAULT 0,
    skipped_files INTEGER NOT NULL DEFAULT 0,
    failed_files INTEGER NOT NULL DEFAULT 0,
    bytes_copied INTEGER NOT NULL DEFAULT 0,
    delta_saved_bytes INTEGER NOT NULL DEFAULT 0,
    retried_ok INTEGER NOT NULL DEFAULT 0,
    moved_files INTEGER NOT NULL DEFAULT 0,
    avg_speed REAL,
    error_message TEXT
);
CREATE INDEX IF NOT EXISTS ix_runs_job ON runs(job_id, started_at DESC);
CREATE TABLE IF NOT EXISTS sync_snapshot(
    job_id INTEGER NOT NULL,
    rel_path TEXT NOT NULL,
    size INTEGER NOT NULL,
    mtime_utc TEXT NOT NULL,
    is_dir INTEGER NOT NULL,
    PRIMARY KEY(job_id, rel_path)
);
");

                // 旧库升级：新增列（conflict_policy / strict_mirror / version_keep_count / delta_sync / runs.delta_saved_bytes）
                var cols = _conn.Query<string>("SELECT name FROM pragma_table_info('jobs')").AsList();
                if (!cols.Contains("conflict_policy"))
                    _conn.Execute("ALTER TABLE jobs ADD COLUMN conflict_policy INTEGER NOT NULL DEFAULT 0");
                if (!cols.Contains("strict_mirror"))
                    _conn.Execute("ALTER TABLE jobs ADD COLUMN strict_mirror INTEGER NOT NULL DEFAULT 0");
                if (!cols.Contains("version_keep_count"))
                    _conn.Execute("ALTER TABLE jobs ADD COLUMN version_keep_count INTEGER NOT NULL DEFAULT 0");
                if (!cols.Contains("delta_sync"))
                    _conn.Execute("ALTER TABLE jobs ADD COLUMN delta_sync INTEGER NOT NULL DEFAULT 1");
                if (!cols.Contains("auto_retry"))
                    _conn.Execute("ALTER TABLE jobs ADD COLUMN auto_retry INTEGER NOT NULL DEFAULT 1");
                if (!cols.Contains("move_detect"))
                    _conn.Execute("ALTER TABLE jobs ADD COLUMN move_detect INTEGER NOT NULL DEFAULT 1");
                if (!cols.Contains("copy_verify"))
                    _conn.Execute("ALTER TABLE jobs ADD COLUMN copy_verify INTEGER NOT NULL DEFAULT 0");
                if (!cols.Contains("deep_verify"))
                    _conn.Execute("ALTER TABLE jobs ADD COLUMN deep_verify INTEGER NOT NULL DEFAULT 0");
                if (!cols.Contains("schedule_spec"))
                    _conn.Execute("ALTER TABLE jobs ADD COLUMN schedule_spec TEXT");
                if (!cols.Contains("copy_workers"))
                    _conn.Execute("ALTER TABLE jobs ADD COLUMN copy_workers INTEGER NOT NULL DEFAULT 2");
                var runCols = _conn.Query<string>("SELECT name FROM pragma_table_info('runs')").AsList();
                if (!runCols.Contains("delta_saved_bytes"))
                    _conn.Execute("ALTER TABLE runs ADD COLUMN delta_saved_bytes INTEGER NOT NULL DEFAULT 0");
                if (!runCols.Contains("retried_ok"))
                    _conn.Execute("ALTER TABLE runs ADD COLUMN retried_ok INTEGER NOT NULL DEFAULT 0");
                if (!runCols.Contains("moved_files"))
                    _conn.Execute("ALTER TABLE runs ADD COLUMN moved_files INTEGER NOT NULL DEFAULT 0");
            }
        }

        // ---------- jobs ----------

        public List<SyncJob> GetJobs()
        {
            lock (_sync)
            {
                // trigger_type 必须 AS Trigger：MatchNamesWithUnderscores 只去下划线（triggertype），
                // 与属性名 Trigger 对不上 → 曾致重启后实时/定时任务全部回落成 Manual（存库值本身正确）
                return _conn.Query<SyncJob>(
                    "SELECT *, trigger_type AS Trigger FROM jobs ORDER BY id").AsList();
            }
        }

        public long InsertJob(SyncJob j)
        {
            lock (_sync)
            {
                return _conn.ExecuteScalar<long>(@"
INSERT INTO jobs(name,left_path,right_path,direction,trigger_type,interval_seconds,
                 debounce_seconds,exclude_patterns,auto_start,enabled,delete_to_recycle_bin,mirror_delete,strict_mirror,conflict_policy,version_keep_count,delta_sync,auto_retry,move_detect,copy_verify,deep_verify,schedule_spec,copy_workers,created_at)
VALUES(@Name,@LeftPath,@RightPath,@Direction,@Trigger,@IntervalSeconds,
       @DebounceSeconds,@ExcludePatterns,@AutoStart,@Enabled,@DeleteToRecycleBin,@MirrorDelete,@StrictMirror,@ConflictPolicy,@VersionKeepCount,@DeltaSync,@AutoRetry,@MoveDetect,@CopyVerify,@DeepVerify,@ScheduleSpec,@CopyWorkers,@CreatedAt);
SELECT last_insert_rowid();", j);
            }
        }

        public void UpdateJob(SyncJob j)
        {
            lock (_sync)
            {
                _conn.Execute(@"
UPDATE jobs SET name=@Name, left_path=@LeftPath, right_path=@RightPath, direction=@Direction,
    trigger_type=@Trigger, interval_seconds=@IntervalSeconds, debounce_seconds=@DebounceSeconds,
    exclude_patterns=@ExcludePatterns, auto_start=@AutoStart, enabled=@Enabled,
    delete_to_recycle_bin=@DeleteToRecycleBin, mirror_delete=@MirrorDelete,
    strict_mirror=@StrictMirror, conflict_policy=@ConflictPolicy, version_keep_count=@VersionKeepCount,
    delta_sync=@DeltaSync, auto_retry=@AutoRetry, move_detect=@MoveDetect,
    copy_verify=@CopyVerify, deep_verify=@DeepVerify, schedule_spec=@ScheduleSpec,
    copy_workers=@CopyWorkers
WHERE id=@Id", j);
            }
        }

        public void DeleteJob(long id)
        {
            lock (_sync)
            {
                _conn.Execute("DELETE FROM jobs WHERE id=@id", new { id });
                _conn.Execute("DELETE FROM sync_snapshot WHERE job_id=@id", new { id }); // 快照随任务级联清理
                _conn.Execute("DELETE FROM runs WHERE job_id=@id", new { id });
                // 历史随任务级联：运行中删任务时在跑轮次收尾还会 FinishRun，job 已不在会留孤儿 runs 行；
                // 删任务即删历史，一步到位（那之后 FinishRun 更新的行已不存在，UPDATE 零行无害）
            }
        }

        public SyncJob? GetJob(long id)
        {
            lock (_sync)
            {
                return _conn.QueryFirstOrDefault<SyncJob>(
                    "SELECT *, trigger_type AS Trigger FROM jobs WHERE id=@id", new { id });
            }
        }

        // ---------- runs ----------

        public long InsertRun(RunRecord r)
        {
            lock (_sync)
            {
                return _conn.ExecuteScalar<long>(@"
INSERT INTO runs(job_id,started_at,trigger,status) VALUES(@JobId,@StartedAt,@Trigger,@Status);
SELECT last_insert_rowid();", r);
            }
        }

        public void FinishRun(RunRecord r)
        {
            lock (_sync)
            {
                _conn.Execute(@"
UPDATE runs SET finished_at=@FinishedAt, status=@Status, scanned_files=@ScannedFiles,
    copied_files=@CopiedFiles, deleted_files=@DeletedFiles, skipped_files=@SkippedFiles,
    failed_files=@FailedFiles, bytes_copied=@BytesCopied, delta_saved_bytes=@DeltaSavedBytes,
    retried_ok=@RetriedOk, moved_files=@MovedFiles, avg_speed=@AvgSpeedBytesPerSec, error_message=@ErrorMessage
WHERE id=@Id", r);
            }
        }

        /// <summary>进程退出/崩溃时遗留的 running 记录，启动时统一标为 interrupted</summary>
        public void MarkInterruptedRuns()
        {
            lock (_sync)
            {
                _conn.Execute(@"
UPDATE runs SET status='interrupted', finished_at=@now
WHERE status='running'", new { now = DateTime.Now });
            }
        }

        public List<RunRecord> GetRecentRuns(long jobId, int limit = 20)
        {
            lock (_sync)
            {
                // avg_speed 同理：avgspeed 与 AvgSpeedBytesPerSec 对不上，不 AS 则历史速度统计恒为空
                return _conn.Query<RunRecord>(
                    "SELECT *, avg_speed AS AvgSpeedBytesPerSec FROM runs WHERE job_id=@jobId ORDER BY id DESC LIMIT @limit",
                    new { jobId, limit }).AsList();
            }
        }

        public void CleanupOldRuns(int keepPerJob = 100)
        {
            lock (_sync)
            {
                _conn.Execute(@"
DELETE FROM runs WHERE id IN (
    SELECT id FROM (
        SELECT id, ROW_NUMBER() OVER (PARTITION BY job_id ORDER BY id DESC) rn FROM runs
    ) WHERE rn > @keep)", new { keep = keepPerJob });
            }
        }

        // ---------- sync_snapshot（双向同步状态基线） ----------

        private class SnapRow
        {
            public string rel_path { get; set; } = "";
            public long size { get; set; }
            public string mtime_utc { get; set; } = "";
            public int is_dir { get; set; }
        }

        public Dictionary<string, FileEntry> GetSnapshot(long jobId)
        {
            lock (_sync)
            {
                var rows = _conn.Query<SnapRow>(
                    "SELECT rel_path, size, mtime_utc, is_dir FROM sync_snapshot WHERE job_id=@jobId",
                    new { jobId }).AsList();
                var result = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in rows)
                {
                    if (!DateTime.TryParse(r.mtime_utc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var mtime))
                        mtime = DateTime.MinValue;
                    result[r.rel_path.Replace('\\', '/')] = new FileEntry
                    {
                        RelativePath = r.rel_path.Replace('\\', '/'),
                        Size = r.size,
                        MtimeUtc = DateTime.SpecifyKind(mtime, DateTimeKind.Utc),
                        IsDirectory = r.is_dir == 1
                    };
                }
                return result;
            }
        }

        /// <summary>原子替换任务快照（事务：先清后插，失败回滚保留旧基线）</summary>
        public void SaveSnapshot(long jobId, IEnumerable<FileEntry> entries)
        {
            lock (_sync)
            {
                using var tx = _conn.BeginTransaction();
                _conn.Execute("DELETE FROM sync_snapshot WHERE job_id=@jobId", new { jobId }, tx);
                _conn.Execute(@"
INSERT INTO sync_snapshot(job_id,rel_path,size,mtime_utc,is_dir)
VALUES(@JobId,@RelativePath,@Size,@Mtime,@IsDir)",
                    entries.Select(e => new
                    {
                        JobId = jobId,
                        e.RelativePath,
                        e.Size,
                        Mtime = e.MtimeUtc.ToString("O"),
                        IsDir = e.IsDirectory ? 1 : 0
                    }), tx);
                tx.Commit();
            }
        }

        public void Dispose() => _conn.Dispose();
    }
}
