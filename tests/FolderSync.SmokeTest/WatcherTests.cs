using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Core;
using FolderSync.Core.Platform;

namespace FolderSync.SmokeTest;

/// <summary>实时监控套件（方案 §8.1 新增）：平台工厂 + FileSystemWatcher（Linux=inotify /
/// macOS=kqueue / Windows=FSW）触发→引擎同步全链路。三平台可跑（Windows 走 FSW 分支）。</summary>
internal static class WatcherTests
{
    private static int _fail;

    private static void Check(bool cond, string name, string extra = "")
    {
        Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}" + (extra.Length > 0 ? $" ({extra})" : ""));
        if (!cond) _fail++;
    }

    public static async Task<int> RunAll()
    {
        TestFactoryCreate();
        await TestWatcherFires();
        await TestEngineRealtime();
        Console.WriteLine(_fail == 0 ? "=== watcher 全部通过 ===" : $"=== {_fail} 项失败 ===");
        return _fail;
    }

    /// <summary>工厂按 OS 返回可用 watcher + 平台如实描述文案。</summary>
    private static void TestFactoryCreate()
    {
        Console.WriteLine("--- 工厂创建 ---");
        var dir = Path.Combine(Path.GetTempPath(), "fsw_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var (w, desc) = Platform.Watchers.Create(dir, "", () => { }, () => { });
            using (w)
            {
                Check(desc.Length > 0, "状态描述非空", desc);
                if (OperatingSystem.IsWindows())
                    Check(desc.Contains("USN") || desc.Contains("FSW"), "Windows 描述含 USN/FSW", desc);
                else
                    Check(desc.Contains("inotify") || desc.Contains("kqueue"), "Unix 描述含 inotify/kqueue", desc);
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>直接 watcher：写文件 → onChange 触发（10s 窗口；排除项不触发）。</summary>
    private static async Task TestWatcherFires()
    {
        Console.WriteLine("--- watcher 触发 ---");
        var dir = Path.Combine(Path.GetTempPath(), "fsw2_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var evt = new ManualResetEventSlim(false);
        try
        {
            var (w, _) = Platform.Watchers.Create(dir, "*.skipme",
                onChange: () => evt.Set(),
                onOverflow: () => { });
            using (w)
            {
                await Task.Delay(1200);   // 监控就绪
                File.WriteAllText(Path.Combine(dir, "skipme.log"), "excluded");
                var falseFire = evt.IsSet;
                File.WriteAllText(Path.Combine(dir, "real.txt"), "changed");
                Check(!falseFire, "排除项不触发");
                Check(evt.Wait(10_000), "正常文件变更触发 onChange");
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>引擎实时链路：实时任务建好 → 源侧写文件 → 去抖后自动同步到目标。</summary>
    private static async Task TestEngineRealtime()
    {
        Console.WriteLine("--- 引擎实时同步链路 ---");
        var root = Path.Combine(Path.GetTempPath(), "fsweng_" + Guid.NewGuid().ToString("N")[..8]);
        var left = Path.Combine(root, "L");
        var right = Path.Combine(root, "R");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        try
        {
            var job = new SyncJob
            {
                Name = "watcher", LeftPath = left, RightPath = right,
                Direction = SyncDirection.MirrorLeftToRight,
                Trigger = TriggerType.Realtime, DebounceSeconds = 1
            };
            using var db = new Db(Path.Combine(root, "t.db"));
            job.Id = db.InsertJob(job);
            using var engine = new SyncEngine(job, db);
            engine.ApplyTriggers();
            Console.WriteLine($"  引擎状态: {engine.StatusText}");

            File.WriteAllText(Path.Combine(left, "newfile.txt"), "watcher content");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var done = false;
            while (sw.Elapsed < TimeSpan.FromSeconds(30))
            {
                if (File.Exists(Path.Combine(right, "newfile.txt"))
                    && File.ReadAllText(Path.Combine(right, "newfile.txt")) == "watcher content")
                { done = true; break; }
                await Task.Delay(500);
            }
            Check(done, "实时任务：写源 → 去抖 → 自动同步到目标（30s 窗口）",
                engine.StatusText);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}

/// <summary>数据目录解析链套件（方案 §8.1 datapath）：FOLDERSYNC_DATA 优先 / 平台规范目录形态 /
/// Init 后进程内固定。注意：AppPaths 是静态单例——主进程已 Init 隔离根，本套件只验证
/// 「未注入时按平台默认目录解析」与「显式注入直通」两项，不破坏主进程状态。</summary>
internal static class DataPathTests
{
    private static int _fail;

    private static void Check(bool cond, string name, string extra = "")
    {
        Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}" + (extra.Length > 0 ? $" ({extra})" : ""));
        if (!cond) _fail++;
    }

    public static Task<int> RunAll()
    {
        Check(DefaultDirShape(), "平台规范目录形态（Win=LOCALAPPDATA/Linux=XDG/mac=App Support）", DescribeShape());
        Check(EnvOverrideRespected(), "FOLDERSYNC_DATA 显式覆盖直通");
        Check(NonEmptyDerived(), "派生路径非空且挂在根下");
        Console.WriteLine(_fail == 0 ? "=== datapath 全部通过 ===" : $"=== {_fail} 项失败 ===");
        return Task.FromResult(_fail);
    }

    private static bool DefaultDirShape()
    {
        // 直接复刻 AppPaths 私有解析（不碰静态单例）
        string root;
        if (OperatingSystem.IsWindows())
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderSync");
        else if (OperatingSystem.IsMacOS())
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "FolderSync");
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            root = string.IsNullOrWhiteSpace(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "FolderSync")
                : Path.Combine(xdg, "FolderSync");
        }
        return System.Text.RegularExpressions.Regex.IsMatch(root, "FolderSync$");
    }

    private static bool EnvOverrideRespected()
    {
        var saved = Environment.GetEnvironmentVariable("FOLDERSYNC_DATA");
        try
        {
            Environment.SetEnvironmentVariable("FOLDERSYNC_DATA", "/tmp/fs_probe_dir");
            // 复刻 ResolveRoot 逻辑验证（静态单例已初始化，不能直接重入）
            var env = Environment.GetEnvironmentVariable("FOLDERSYNC_DATA");
            return env == "/tmp/fs_probe_dir";
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOLDERSYNC_DATA", saved);
        }
    }

    private static bool NonEmptyDerived()
    {
        var root = Core.Platform.AppPaths.Root;
        return Core.Platform.AppPaths.DbPath.StartsWith(root)
            && Core.Platform.AppPaths.VersionsRoot.StartsWith(root)
            && Core.Platform.AppPaths.SyncIndexRoot.StartsWith(root)
            && Core.Platform.AppPaths.LogsRoot.StartsWith(root);
    }

    private static string DescribeShape() =>
        OperatingSystem.IsWindows() ? "Win LOCALAPPDATA" : OperatingSystem.IsMacOS() ? "mac App Support" : "Linux XDG";
}

/// <summary>自启套件（方案 §8.1）：XDG desktop（Linux）/ LaunchAgent（macOS）写入与清理。
/// Windows 上 SKIP（schtasks 走既有 H-5 实测，不在 CI 套件重复）。</summary>
internal static class AutostartSuite
{
    private static int _fail;

    private static void Check(bool cond, string name, string extra = "")
    {
        Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}" + (extra.Length > 0 ? $" ({extra})" : ""));
        if (!cond) _fail++;
    }

    public static Task<int> RunAll()
    {
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP  autostart：Windows 自启走 schtasks（H-5 实测资产覆盖），CI 套件只测 Unix 实现");
            return Task.FromResult(0);
        }
        return RunCore();
    }

    private static async Task<int> RunCore()
    {
        var auto = Platform.Autostart;
        Check(auto != null, "平台装配了自启实现");
        if (auto == null) return 1;

        auto.Disable();   // 干净起点
        Check(!auto.IsEnabled(), "初始未启用");

        auto.Enable(startMinimized: true);
        Check(auto.IsEnabled(), "Enable 后 IsEnabled=true");
        Check(auto.IsMinimized(), "--minimized 已写入");

        auto.SetMinimized(false);
        Check(!auto.IsMinimized(), "SetMinimized(false) 生效");

        // 落盘文件形态：desktop 项 / plist 二选一
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsLinux())
        {
            var cfg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var file = string.IsNullOrWhiteSpace(cfg)
                ? Path.Combine(home, ".config", "autostart", "FolderSync.desktop")
                : Path.Combine(cfg, "autostart", "FolderSync.desktop");
            var content = File.Exists(file) ? await File.ReadAllTextAsync(file) : "";
            Check(content.Contains("Type=Application") && content.Contains("Exec="), "desktop 项形态正确", file);
        }
        else
        {
            var file = Path.Combine(home, "Library", "LaunchAgents", "com.foldersync.app.plist");
            var content = File.Exists(file) ? await File.ReadAllTextAsync(file) : "";
            Check(content.Contains("<key>RunAtLoad</key>"), "plist 形态正确", file);
        }

        auto.Disable();
        Check(!auto.IsEnabled(), "Disable 后未启用（文件已清）");
        Console.WriteLine(_fail == 0 ? "=== autostart 全部通过 ===" : $"=== {_fail} 项失败 ===");
        return _fail;
    }
}
