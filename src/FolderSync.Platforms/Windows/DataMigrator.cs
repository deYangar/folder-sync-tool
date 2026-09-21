using System;
using System.IO;
using System.Runtime.Versioning;
using FolderSync.Core.Platform;

namespace FolderSync.Platforms.Windows
{
    /// <summary>Windows 老用户数据一次性迁移（方案 §4.2）：现版数据随 exe 目录走（便携式），
    /// 重写后落平台规范目录。GUI/CLI 启动最早期（任何 Db/VersionStore 访问之前）调用 Resolve()：
    /// 1. FOLDERSYNC_DATA 显式设置 → 直接用，不迁移（用户自己指路，尊重原样）
    /// 2. 程序目录无旧数据（foldersync.db/versions/sync-index 任一都不存在）→ 规范目录
    /// 3. 有旧数据且规范目录尚无数据 → 整树搬移（db 含 -wal/-shm 伴生、versions、sync-index）：
    ///    同卷 rename 优先（瞬时），跨卷 copy+长度校验+删源；logs 不搬（可再生）
    /// 4. 迁移任何一步失败 → 回退到 db 所在侧启动 + 写迁移日志，下版启动幂等重试
    /// 幂等性（S-3）：迁移全部完成才落 .foldersync-migrated 完成标记；部分完成（中断/失败）无标记，
    /// 下次启动重试——已搬项按「同卷原子=完整 / 跨卷长度相等=完整」识别跳过，跨卷半份先清再拷。</summary>
    [SupportedOSPlatform("windows")]
    public static class DataMigrator
    {
    /// <summary>解析数据根（含必要的迁移）。必须在 AppPaths.Init 之前调用，返回值交给 Init。</summary>
    public static string Resolve()
    {
        var env = Environment.GetEnvironmentVariable("FOLDERSYNC_DATA");
        if (!string.IsNullOrWhiteSpace(env)) return env;

        var progDir = AppContext.BaseDirectory;
        var target = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderSync");

        if (!HasLegacyData(progDir)) return target;
        // S-3：只认完成标记，不看 target 是否已有数据——迁移是多步非原子操作，db 搬成、versions
        // 搬失败被 catch 回退后，旧逻辑的 HasAnyData(target) 因 db 已在而永真 → versions/sync-index
        // 永久滞留程序目录成孤儿，引擎在 target 按无历史新建空库，数据劈成两半
        if (File.Exists(Path.Combine(target, MarkerFile))) return target;
        try
        {
            Migrate(progDir, target);   // 幂等：已搬项跳过/重拷（部分迁移中断后可安全重试）
            File.WriteAllText(Path.Combine(target, MarkerFile),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} migrated from {progDir}");
            return target;
        }
        catch (Exception ex)
        {
            // 回退启动位置：db 在哪边就用哪边——部分迁移后 progDir 已无 db，退回程序目录会
            // 按无历史新建空库（第二重劈裂数据）
            var fallback = File.Exists(Path.Combine(target, "foldersync.db")) ? target : progDir;
            try
            {
                Directory.CreateDirectory(fallback);
                File.AppendAllText(Path.Combine(fallback, "migrate-error.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 数据目录迁移失败，本次沿用 {fallback}（下次启动重试）: {ex}\n");
            }
            catch { }
            return fallback;
        }
    }

    /// <summary>迁移全部完成标记（S-3）：存在=迁移闭环，不存在=未开始或部分完成需重试。</summary>
    private const string MarkerFile = ".foldersync-migrated";

    private static bool HasLegacyData(string dir) =>
        File.Exists(Path.Combine(dir, "foldersync.db"))
        || Directory.Exists(Path.Combine(dir, "versions"))
        || Directory.Exists(Path.Combine(dir, "sync-index"));

    private static void Migrate(string progDir, string target)
    {
        Directory.CreateDirectory(target);
        bool sameVolume = string.Equals(
            Path.GetPathRoot(Path.GetFullPath(progDir)),
            Path.GetPathRoot(Path.GetFullPath(target)),
            StringComparison.OrdinalIgnoreCase);

        // 跨卷中断的半份 db 清理（重试幂等的前提）：src/dst 并存且长度不等 = copy 中断残留，
        // 不清掉会把损坏库当已迁项跳过（MoveFileChecked 的长度校验只对本次拷贝负责）
        if (!sameVolume)
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var s = Path.Combine(progDir, "foldersync.db" + suffix);
                var d = Path.Combine(target, "foldersync.db" + suffix);
                if (File.Exists(s) && File.Exists(d) && new FileInfo(d).Length != new FileInfo(s).Length)
                    File.Delete(d);
            }

        // db 三件套（主库 + WAL 伴生，SQLite 未打开时随迁安全，重放由打开方完成）
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var src = Path.Combine(progDir, "foldersync.db" + suffix);
            if (!File.Exists(src)) continue;
            var dst = Path.Combine(target, "foldersync.db" + suffix);
            if (File.Exists(dst)) continue;   // 部分迁移重试：同卷 rename 原子、跨卷长度相等（半份已清）= 上轮已完整
            MoveFileChecked(src, dst, sameVolume);
        }
        foreach (var name in new[] { "versions", "sync-index" })
        {
            var src = Path.Combine(progDir, name);
            if (!Directory.Exists(src)) continue;
            var dst = Path.Combine(target, name);
            if (Directory.Exists(dst) && sameVolume) continue;   // 同卷 Directory.Move 原子：dst 已在=上轮搬完
            MoveDirChecked(src, dst, sameVolume);   // 跨卷 dst 半份无妨：CopyDir 逐文件 overwrite 重拷幂等
        }
    }

        private static void MoveFileChecked(string src, string dst, bool sameVolume)
        {
            if (sameVolume)
            {
                File.Move(src, dst);   // rename：瞬时
                return;
            }
            // 跨卷：copy → 长度校验 → 删源（sha256 全量校验对几十 GB 版本库不现实，长度+mtime 双核对）
            File.Copy(src, dst, overwrite: true);
            if (new FileInfo(dst).Length != new FileInfo(src).Length)
                throw new IOException($"跨卷复制长度不一致: {src}");
            File.SetLastWriteTimeUtc(dst, File.GetLastWriteTimeUtc(src));
            File.Delete(src);
        }

        private static void MoveDirChecked(string src, string dst, bool sameVolume)
        {
            if (sameVolume)
            {
                Directory.Move(src, dst);
                return;
            }
            // 跨卷递归复制（目录 mtime 尽力保留），全量完成才删源——中断时旧位置仍完整，下版重试
            CopyDir(src, dst);
            Directory.Delete(src, recursive: true);
        }

        private static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
                MoveFileChecked(f, Path.Combine(dst, Path.GetFileName(f)), sameVolume: false);
            foreach (var d in Directory.GetDirectories(src))
                CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
            try { Directory.SetLastWriteTimeUtc(dst, Directory.GetLastWriteTimeUtc(src)); } catch { }
        }
    }
}
