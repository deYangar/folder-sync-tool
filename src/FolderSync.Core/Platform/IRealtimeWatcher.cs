using System;

namespace FolderSync.Core.Platform
{
    /// <summary>实时监控句柄（ Dispose 即停）。</summary>
    public interface IRealtimeWatcher : IDisposable
    {
    }

    /// <summary>实时监控工厂：按平台选择最优 watcher。
    /// Windows: USN Journal 优先，构造失败降级 FileSystemWatcher（inotify/FSEvents）；
    /// Unix: FileSystemWatcher 直接转正（无持久 journal，溢出→全扫兜底由引擎侧处理）。</summary>
    public interface IRealtimeWatcherFactory
    {
        /// <summary>创建 watcher，返回 (实例, 状态描述文案)——描述直接进 StatusText
        /// （「实时监听中（USN）」/「实时监听中（FSW 兜底）」等，平台各自如实）。
        /// 全部实现不可用时抛异常（引擎侧按「监听失败」展示）。</summary>
        (IRealtimeWatcher watcher, string describe) Create(
            string watchPath, string excludePatterns,
            Action onChange, Action onOverflow, Action? onDead = null);
    }
}
