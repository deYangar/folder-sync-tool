using System;
using System.IO;

namespace FolderSync.Core.Platform
{
    /// <summary>程序数据落位门面：db / versions / sync-index / logs / crash.log 的唯一根。
    /// 解析链（方案 §4.2）：① AppPaths.Init 显式注入（GUI/CLI 启动时先跑平台迁移再注入；
    /// 测试注入隔离目录同走它）→ ② 环境变量 FOLDERSYNC_DATA → ③ 平台规范目录
    /// （Windows %LOCALAPPDATA%\FolderSync；Linux $XDG_DATA_HOME/FolderSync 或 ~/.local/share/FolderSync；
    /// macOS ~/Library/Application Support/FolderSync）。
    /// Core 只做纯解析不做迁移：老数据一次性搬移是宿主启动职责（见 Platforms 的 DataMigrator），
    /// 这样引擎/测试被任意进程复用时不会意外触发跨卷搬移。</summary>
    public static class AppPaths
    {
        private static string? _root;

        /// <summary>数据根（首次访问惰性解析后固定；进程内一致）。</summary>
        public static string Root => _root ??= ResolveRoot();

        /// <summary>显式注入数据根。必须在任何数据访问之前调用（GUI/CLI 启动第一句、测试 Main 第一句）。</summary>
        public static void Init(string explicitRoot) => _root = Path.GetFullPath(explicitRoot);

        public static string DbPath => Path.Combine(Root, "foldersync.db");
        public static string VersionsRoot => Path.Combine(Root, "versions");
        public static string SyncIndexRoot => Path.Combine(Root, "sync-index");
        public static string LogsRoot => Path.Combine(Root, "logs");
        public static string CrashLog => Path.Combine(Root, "crash.log");

        public static string ReportPath(long jobId, long runId) =>
            Path.Combine(LogsRoot, "reports", $"job{jobId}", $"run{runId}.md");

        public static string FailedDir(long jobId) =>
            Path.Combine(LogsRoot, "failed", $"job{jobId}");

        public static string StateFile(long jobId) =>
            Path.Combine(LogsRoot, $"job{jobId}.state.txt");

        public static string CliLogDir => Path.Combine(LogsRoot, "cli");

        /// <summary>Unix 跨进程同任务互斥锁文件（flock；Windows 走 named Mutex 不落文件）。</summary>
        public static string LockFile(long jobId) =>
            Path.Combine(Root, "locks", $"job{jobId}.lock");

        private static string ResolveRoot()
        {
            var env = Environment.GetEnvironmentVariable("FOLDERSYNC_DATA");
            if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
            return Path.Combine(DefaultDataDir(), "FolderSync");
        }

        /// <summary>平台规范数据目录基（不含 FolderSync 尾段）：LOCALAPPDATA / XDG_DATA_HOME / App Support。</summary>
        private static string DefaultDataDir()
        {
            if (OperatingSystem.IsWindows())
                return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (OperatingSystem.IsMacOS())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support");
            // Linux/BSD：XDG 规范
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrWhiteSpace(xdg)) return xdg;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share");
        }
    }
}
