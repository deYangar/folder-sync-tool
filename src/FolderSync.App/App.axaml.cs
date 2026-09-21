using System;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FolderSync.App.Services;
using FolderSync.App.ViewModels;
using FolderSync.App.Views;

namespace FolderSync.App;

public class App : Application
{
    private static Mutex? _mutex;
    private TrayService? _tray;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // CLI 已在 Program.Main 抢占（不建任何窗口）——走到这里的都是 GUI 模式
            // FS_E2E_SILENT（E2E 隔离模式）跳过互斥：隔离数据目录的测试实例
            // 不该被用户真实实例挡住（数据目录已隔离，互不触碰）
            _mutex = new Mutex(true, "FolderSync_SingleInstance", out var isNew);
            var e2eSilent = Environment.GetEnvironmentVariable("FS_E2E_SILENT") == "1";
            if (!isNew && !e2eSilent)
            {
                // G-3：必须等用户看完「已在运行」再 Shutdown——fire-and-forget 后立即退出，
                // 弹窗一闪而过甚至未及渲染（WPF 阻塞 MessageBox 语义的平移）
                _ = ShowAlreadyRunningAndShutdownAsync(desktop);
                return;
            }

            // 全局异常兜底：写 crash.log（数据目录），绝不无声闪退。
            // Avalonia 没有 DispatcherUnhandledException 等价物：UI 线程异常走
            // Application.Current.ThrowUnhandledException / RxUI 链路，兜底以
            // AppDomain + TaskScheduler 为主（弹窗在进程内尽力而为）
            AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrashLog(e.ExceptionObject);
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                WriteCrashLog(e.Exception);
                e.SetObserved();   // 不让后台任务异常静默升级为进程崩溃
            };

            ThemeService.Init();

            var vm = new MainWindowViewModel();
            var main = new MainWindow
            {
                DataContext = vm,
            };
            desktop.MainWindow = main;
            if (E2eSilent.Enabled)
                main.ShowActivated = false;   // 静默测试：窗口照常显示但不抢焦点
            // G-6：--minimized 依赖托盘常驻；Linux 无 StatusNotifier 环境（GNOME 裸机）托盘
            // 静默不显示，最小化启动会让主窗口永久失联——托盘不可用时降级为正常显示
            if (!HasArg(desktop.Args, "--minimized") || !Services.TrayService.TrayLikelyAvailable)
                main.Show();
            // --minimized：不显示主窗口（托盘常驻，自启最小化路径）

            _tray = new TrayService(main);
            vm.Tray = _tray;   // 注入观察者：轮次状态 → 托盘动画。漏注入则状态机全瘫（2026-09-16）
            desktop.Exit += (_, _) =>
            {
                main.ForceExitCleanup();
                _tray.Dispose();
                _mutex?.Dispose();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static bool HasArg(string?[]? args, string arg) =>
        args != null && Array.Exists(args, a => string.Equals(a, arg, StringComparison.OrdinalIgnoreCase));

    /// <summary>G-3：单实例提示等待用户确认后再退出。</summary>
    private static async Task ShowAlreadyRunningAndShutdownAsync(
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        try { await Services.Dialogs.InfoAsync(null, "FolderSync 已在运行。", "FolderSync"); }
        finally { desktop.Shutdown(); }
    }

    /// <summary>崩溃日志（带轮转：超 1MB 时旧档改 crash.log.old）。落数据目录（AppPaths）。</summary>
    internal static void WriteCrashLog(object exception)
    {
        try
        {
            var log = Core.Platform.AppPaths.CrashLog;
            try
            {
                var fi = new FileInfo(log);
                if (fi.Exists && fi.Length > 1_000_000)
                {
                    var old = log + ".old";
                    try { if (File.Exists(old)) File.Delete(old); } catch { }
                    File.Move(log, old);
                }
            }
            catch { /* 轮转失败不阻断写入 */ }
            File.AppendAllText(log, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}\n\n");
        }
        catch { }
    }
}
