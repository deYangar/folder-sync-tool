using System;

namespace FolderSync.Core.Platform
{
    /// <summary>平台实现聚合：宿主（App/SmokeTest）启动时 Init 装配，引擎层全程只碰 Platform.* 门面。
    /// 未装配时的兜底是纯 BCL 跨平台实现（FSW watcher + 托管流复制 + UseShellExecute 打开 +
    /// 无自启/无回收站）——功能降级但绝不 Native 崩溃，保证 Core 被任意进程复用时的可用下限。</summary>
    public interface IPlatformImpl
    {
        IFileOps FileOps { get; }
        IRealtimeWatcherFactory Watchers { get; }
        IAutostart? Autostart { get; }   // null=平台暂无自启实现
        IShellOps Shell { get; }
        IIconProvider? Icons { get; }    // M2 图标体系；null=用内嵌默认
    }

    /// <summary>装配门面。Init 只允许在进程启动早期调用一次（引擎/文件操作并发起来后再换实现
    /// 会出现半轮旧实现半轮新实现的混合执行）。</summary>
    public static class Platform
    {
        private static IPlatformImpl? _impl;

        public static IFileOps FileOps => Current.FileOps;
        public static IRealtimeWatcherFactory Watchers => Current.Watchers;
        public static IAutostart? Autostart => Current.Autostart;
        public static IShellOps Shell => Current.Shell;
        public static IIconProvider? Icons => Current.Icons;

        public static IPlatformImpl Current => _impl ??= new Bcl.BclPlatformImpl();

        /// <summary>宿主启动时装配平台实现。真实现只允许装配一次（防运行中换实现）；
        /// 惰性 BCL 兜底例外（W-6）——Init 之前任何一处碰 Platform.* 都会把兜底实现固化进
        /// _impl，若不允许覆盖，随后的真 Init 反抛「已装配」，报错误导排查。兜底可被覆盖。</summary>
        public static void Init(IPlatformImpl impl)
        {
            var cur = _impl;
            if (cur != null && !(cur is Bcl.BclPlatformImpl) && !ReferenceEquals(cur, impl))
                throw new InvalidOperationException("Platform 已装配，禁止运行中更换实现");
            _impl = impl;
        }
    }
}
