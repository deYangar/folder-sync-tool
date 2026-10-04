using System;
using System.Runtime.Versioning;
using FolderSync.Core;
using FolderSync.Core.Platform;

namespace FolderSync.Platforms.Windows
{
    /// <summary>Windows 监控工厂：USN Journal 优先（无缓冲溢出、系统级可靠），
    /// 构造失败（非 NTFS/未提权/卷句柄拒绝）降级 FileSystemWatcher。自 SyncEngine 平移（降级链与文案不变）。</summary>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsWatcherFactory : IRealtimeWatcherFactory
    {
        public (IRealtimeWatcher watcher, string describe) Create(
            string watchPath, string excludePatterns,
            Action onChange, Action onOverflow, Action? onDead = null)
        {
            try
            {
                return (new UsnWatcher(watchPath, excludePatterns, onChange, onOverflow, onDead),
                    "实时监听中（USN）");
            }
            catch
            {
                // FSW 缓冲区溢出 = 丢过事件，onOverflow 由引擎侧接全量校准
                return (new DirWatcher(watchPath, excludePatterns, onChange, onOverflow),
                    "实时监听中（FSW 兜底）");
            }
        }
    }
}
