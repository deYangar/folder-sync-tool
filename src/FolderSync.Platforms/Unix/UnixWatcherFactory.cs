using System;
using System.Runtime.Versioning;
using FolderSync.Core;
using FolderSync.Core.Platform;

namespace FolderSync.Platforms.Unix
{
    /// <summary>Unix 监控工厂：FileSystemWatcher 转正（Linux=inotify，macOS=kqueue/EVFILT_VNODE——
    /// .NET 的 macOS FSW 后端是 kqueue 而非 FSEvents，dotnet/runtime#17290 起如此；kqueue 逐条目
    /// 开 fd，深树可靠性弱于 FSEvents，如实描述防误导）。无持久 journal，
    /// 溢出→全扫补偿由引擎侧 onOverflow 语义承接（DirWatcher 现有行为）。</summary>
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
            // U-5：IRealtimeWatcher 契约「平台各自如实」——macOS 是 kqueue 不是 FSEvents；
            // Linux 之外的 Unix（FreeBSD 等）不冒称 inotify
            var describe = OperatingSystem.IsMacOS() ? "实时监听中（kqueue）"
                         : OperatingSystem.IsLinux() ? "实时监听中（inotify）"
                         : "实时监听中（FSW）";
            return (w, describe);
        }
    }
}
