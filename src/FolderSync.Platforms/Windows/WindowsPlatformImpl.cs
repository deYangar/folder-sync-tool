using System.Runtime.Versioning;
using FolderSync.Core.Platform;

namespace FolderSync.Platforms.Windows
{
    /// <summary>Windows 平台聚合实现。</summary>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsPlatformImpl : IPlatformImpl
    {
        public Core.Platform.IFileOps FileOps { get; } = new WindowsFileOps();
        public IRealtimeWatcherFactory Watchers { get; } = new WindowsWatcherFactory();
        public IAutostart? Autostart { get; } = new WindowsAutostart();
        public IShellOps Shell { get; } = new WindowsShellOps();
        public IIconProvider? Icons { get; } = new ShellIcons();   // 二期增强：Shell 关联图标/内容缩略图
    }
}
