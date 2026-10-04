using System;
using System.Collections.Generic;
using System.IO;

namespace FolderSync.Core.Platform.Bcl
{
    /// <summary>纯 BCL 兜底平台实现（未装配 Platforms 时的下限）：
    /// 托管流复制 + FSW watcher（DirWatcher）+ UseShellExecute 打开；无自启、无回收站。
    /// 正式宿主（App/SmokeTest）启动即装配 Platforms 的实现，本类只在异常路径出现。</summary>
    internal sealed class BclPlatformImpl : IPlatformImpl
    {
        public IFileOps FileOps { get; } = new BclFileOps();
        public IRealtimeWatcherFactory Watchers { get; } = new FswWatcherFactory();
        public IAutostart? Autostart => null;
        public IShellOps Shell { get; } = new BclShellOps();
        public IIconProvider? Icons => null;
    }

    /// <summary>托管流复制兜底：1MB 分块 + 进度回调；mtime 用 BCL 设置（Unix=futimens），
    /// ctime/birthtime 不保（平台最优实现才保）。回收站明确抛不支持——兜底实现宁可拒绝删除
    /// 也不把「进回收站」静默降级成永久删除。</summary>
    internal sealed class BclFileOps : IFileOps
    {
        public bool CopyFile(string src, string dst, Func<long, long, bool>? onProgress, out int win32Error)
        {
            try
            {
                using var srcFs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var dstFs = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None);
                var total = srcFs.Length;
                var buf = new byte[1024 * 1024];
                long done = 0;
                int n;
                while ((n = srcFs.Read(buf, 0, buf.Length)) > 0)
                {
                    dstFs.Write(buf, 0, n);
                    done += n;
                    if (onProgress != null && !onProgress(total, done))
                    {
                        win32Error = 1235;   // ERROR_REQUEST_ABORTED
                        return false;
                    }
                }
                win32Error = 0;
                return true;
            }
            catch (UnauthorizedAccessException) { win32Error = 5; return false; }
            catch (FileNotFoundException) { win32Error = 2; return false; }
            catch (DirectoryNotFoundException) { win32Error = 3; return false; }
            catch (IOException io) { win32Error = Errno.ToWin32(io); return false; }   // C-4：单表委托（ENOSPC→112 熔断语义与平台实现一致）
            catch (Exception) { win32Error = 1; return false; }
        }

        public void CopyTimestamps(string src, string dst)
        {
            try { File.SetLastWriteTimeUtc(dst, File.GetLastWriteTimeUtc(src)); }
            catch { /* 尽力而为 */ }
        }

        public void RecycleDeleteBatch(IReadOnlyList<string> paths)
            => throw new RecycleBinUnsupportedException("当前平台装配缺少回收站实现，拒绝删除（宁可不删不错删）");

        public void RecycleDelete(string path, bool isDirectory)
            => throw new RecycleBinUnsupportedException("当前平台装配缺少回收站实现，拒绝删除（宁可不删不错删）");

        public bool IsNetworkPath(string path) => false;
    }

    /// <summary>FSW 兜底工厂：包装 Core 现有 DirWatcher（FileSystemWatcher 在
    /// Linux=inotify、macOS=kqueue，本来就是跨平台的）。</summary>
    internal sealed class FswWatcherFactory : IRealtimeWatcherFactory
    {
        public (IRealtimeWatcher watcher, string describe) Create(
            string watchPath, string excludePatterns,
            Action onChange, Action onOverflow, Action? onDead = null)
        {
            var w = new DirWatcher(watchPath, excludePatterns, onChange, onOverflow);
            return (w, "实时监听中（FSW）");
        }
    }

    /// <summary>UseShellExecute 打开路径：Windows 走 shell 关联（explorer/默认程序）；
    /// Unix 上 BCL 会经 /bin/sh 解释（正式 Unix 实现改显式 xdg-open/open）。</summary>
    internal sealed class BclShellOps : IShellOps
    {
        public void OpenPath(string path)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Unix 打开路径请装配 Platforms 的 Shell 实现");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
    }
}
