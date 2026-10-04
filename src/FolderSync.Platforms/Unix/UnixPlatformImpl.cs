using System.Runtime.Versioning;
using FolderSync.Core.Platform;

namespace FolderSync.Platforms.Unix
{
    /// <summary>Unix 平台聚合基类（Linux/macOS 共用 FileOps/Watcher/Shell；自启实现由子类分流）。</summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public abstract class UnixPlatformImpl : IPlatformImpl
    {
        protected UnixPlatformImpl(bool isMac) => Icons = new UnixIcons(isMac);

        public IFileOps FileOps { get; } = new UnixFileOps();
        public IRealtimeWatcherFactory Watchers { get; } = new UnixWatcherFactory();
        public IAutostart? Autostart { get; protected init; }
        public IShellOps Shell { get; } = new UnixShellOps();
        public IIconProvider? Icons { get; }   // 二期增强：图片直读 + Linux freedesktop 缩略图缓存
    }

    /// <summary>Linux 聚合：XDG autostart。</summary>
    [SupportedOSPlatform("linux")]
    public sealed class LinuxPlatformImpl : UnixPlatformImpl
    {
        public LinuxPlatformImpl() : base(isMac: false) => Autostart = new XdgAutostart();
    }

    /// <summary>macOS 聚合：LaunchAgent。</summary>
    [SupportedOSPlatform("macos")]
    public sealed class MacPlatformImpl : UnixPlatformImpl
    {
        public MacPlatformImpl() : base(isMac: true) => Autostart = new LaunchAgentAutostart();
    }
}
