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
    /// 4. 迁移任何一步失败 → 回退沿用程序目录启动 + 写迁移日志，下版启动重试
    /// 幂等性：迁移成功后旧位置数据已清空，下次检测不到旧数据自然不再迁移。</summary>
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

            if (!HasLegacyData(progDir) || HasAnyData(target)) return target;
            try
            {
                Migrate(progDir, target);
                return target;
            }
            catch (Exception ex)
            {
                // 回退旧位置启动：迁移失败不能挡住程序可用性
                try
                {
                    Directory.CreateDirectory(progDir);
                    File.AppendAllText(Path.Combine(progDir, "migrate-error.log"),
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 数据目录迁移失败，本次沿用程序目录（下次启动重试）: {ex}\n");
                }
                catch { }
                return progDir;
            }
        }

        private static bool HasLegacyData(string dir) =>
            File.Exists(Path.Combine(dir, "foldersync.db"))
            || Directory.Exists(Path.Combine(dir, "versions"))
            || Directory.Exists(Path.Combine(dir, "sync-index"));

        private static bool HasAnyData(string dir) =>
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

            // db 三件套（主库 + WAL 伴生，SQLite 未打开时随迁安全，重放由打开方完成）
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var src = Path.Combine(progDir, "foldersync.db" + suffix);
                if (!File.Exists(src)) continue;
                MoveFileChecked(src, Path.Combine(target, "foldersync.db" + suffix), sameVolume);
            }
            foreach (var name in new[] { "versions", "sync-index" })
            {
                var src = Path.Combine(progDir, name);
                if (!Directory.Exists(src)) continue;
                MoveDirChecked(src, Path.Combine(target, name), sameVolume);
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
