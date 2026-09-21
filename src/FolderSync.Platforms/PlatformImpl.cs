using System;
using FolderSync.Core.Platform;

namespace FolderSync.Platforms
{
    /// <summary>宿主装配入口：App/SmokeTest 启动时
    /// Platform.Init(PlatformImpl.SelectForCurrentOS()) 一句完成按 OS 装配。</summary>
    public static class PlatformImpl
    {
        public static IPlatformImpl SelectForCurrentOS()
        {
            // 运行时按 OS 分流枢纽：静态平台可达性分析（CA1416）在这里必然误报，
            // 各分支实现类自身已标 SupportedOSPlatform 防业务侧误用
#pragma warning disable CA1416
            if (OperatingSystem.IsWindows()) return new Windows.WindowsPlatformImpl();
            if (OperatingSystem.IsMacOS()) return new Unix.MacPlatformImpl();
            return new Unix.LinuxPlatformImpl();
#pragma warning restore CA1416
        }
    }
}
