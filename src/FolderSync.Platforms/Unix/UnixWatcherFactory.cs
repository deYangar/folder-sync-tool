using System;
using System.Runtime.Versioning;
using FolderSync.Core;
using FolderSync.Core.Platform;

namespace FolderSync.Platforms.Unix
{
    /// <summary>Unix 监控工厂：FileSystemWatcher 转正（Linux=inotify，macOS=FSEvents）。
    /// 无持久 journal，溢出→全扫补偿由引擎侧 onOverflow 语义承接（DirWatcher 现有行为）。</summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("freebsd")]
    public sealed class UnixWatcherFactory : IRealtimeWatcherFactory
    {
        public (IRealtimeWatcher watcher, string describe) Create(
            string watchPath, string excludePatterns,
            Action onChange, Action onOverflow, Action? onDead = null)
        {
            var w = new DirWatcher(watchPath, excludePatterns, onChange, onOverflow);
            var describe = OperatingSystem.IsMacOS() ? "实时监听中（FSEvents）" : "实时监听中（inotify）";
            return (w, describe);
        }
    }
}
