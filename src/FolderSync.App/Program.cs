using System;
#if !NO_GUI
using Avalonia;
#endif
using FolderSync.Core.Platform;

namespace FolderSync.App;

internal static class Program
{
    public static void Main(string[] args)
    {
        // 平台装配 + 数据目录（含 Windows 老数据一次性迁移）必须先于一切数据访问。
        // CLI 无头模式抢占判定（GUI 变体在 App.OnFrameworkInitializationCompleted 之前，不建任何窗口）。
        Platform.Init(FolderSync.Platforms.PlatformImpl.SelectForCurrentOS());
        AppPaths.Init(ResolveDataRoot());

        if (Cli.TryRun(args, out var cliExit))
            Environment.Exit(cliExit);

#if !NO_GUI
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
#else
        // CLI-only 变体（linux-x86）：无 GUI 参数时提示用法退出
        Console.WriteLine("FolderSync CLI（此构建不含图形界面）");
        Console.WriteLine("用法: FolderSync --run <任务名> | --run-all | --analyze <任务名> | --list");
#endif
    }

    /// <summary>数据根解析：Windows 含旧数据迁移；Unix 无迁移概念（FOLDERSYNC_DATA 优先）。</summary>
    private static string ResolveDataRoot() =>
        OperatingSystem.IsWindows()
            ? FolderSync.Platforms.Windows.DataMigrator.Resolve()
            : Environment.GetEnvironmentVariable("FOLDERSYNC_DATA") is { Length: > 0 } env
                ? env
                : AppPaths.Root;   // 惰性解析（平台规范目录）

#if !NO_GUI
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
#endif
}
