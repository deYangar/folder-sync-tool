using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FolderSync.SmokeTest;

/// <summary>CLI 端到端门禁（方案 §8.2，原 smoke_cli_gate.py 逻辑并入 C# 套件）：
/// Process.Start 自宿主 CLI（FolderSync 可执行文件）跑 --list / --run / --analyze 全流程 +
/// 结果断言 + 磁盘真数据校验。三平台一份代码，CI 免装 Python。
/// 跑法：fstest cli [CLI 可执行文件路径]——缺省在 ../FolderSync.App 的构建输出里找。
/// 数据隔离：CLI 子进程设 FOLDERSYNC_DATA 指向测试临时根，绝不碰真实数据目录。</summary>
internal static class CliGateTests
{
    private static int _fail;

    private static void Check(bool cond, string name, string extra = "")
    {
        Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}" + (extra.Length > 0 ? $" ({extra})" : ""));
        if (!cond) _fail++;
    }

    public static async Task<int> RunAll(string? exeArg)
    {
        var exe = ResolveExe(exeArg);
        if (exe == null)
        {
            Console.WriteLine("SKIP  cli：找不到 FolderSync CLI 可执行文件（先构建 FolderSync.App；或显式传路径 fstest cli <path>）");
            return 0;
        }
        Console.WriteLine($"CLI: {exe}");
        if (OperatingSystem.IsWindows() && !IsElevated())
        {
            Console.WriteLine("SKIP  cli：Windows 的 FolderSync.exe 带 requireAdministrator manifest，非提权进程无法直接启动（用 run_elevated 链跑；Linux/macOS 无此限制）");
            return 0;
        }

        // 数据根与任务目录必须分离：引擎守卫拒绝「任务侧与数据目录重叠」（防自反馈）
        var root = Path.Combine(Path.GetTempPath(), "fscli_" + Guid.NewGuid().ToString("N")[..8]);
        var dataRoot = Path.Combine(root, "data");
        var left = Path.Combine(root, "L");
        var right = Path.Combine(root, "R");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        try
        {
            // 种子数据
            File.WriteAllText(Path.Combine(left, "a.txt"), "cli-gate-v1");
            File.WriteAllText(Path.Combine(left, "b.bin"), string.Concat(Enumerable.Repeat("0123456789", 500)));

            // 1) --list：空库列出 0 任务
            var (rc1, out1) = await RunCli(exe, dataRoot, "--list");
            Check(rc1 == 0 && out1.Contains("共 0 个任务"), "--list 空库退出码 0", $"rc={rc1}");

            // 2) 注入一个任务（直接写库——CLI 不提供建任务命令，GUI/库为准）
            var dbPath = Path.Combine(dataRoot, "foldersync.db");
            var (rcDb, _) = await Task.Run(() =>
            {
                using var db = new Core.Db(dbPath);
                var job = new Core.SyncJob
                {
                    Name = "cli-gate", LeftPath = left, RightPath = right,
                    Direction = Core.SyncDirection.MirrorLeftToRight
                };
                job.Id = db.InsertJob(job);
                return (0, "");
            });
            var (rc2, out2) = await RunCli(exe, dataRoot, "--list");
            Check(rc2 == 0 && out2.Contains("cli-gate"), "--list 显示注入的任务", $"rc={rc2}");

            // 3) --analyze：只分析不执行（磁盘不动）
            var (rc3, out3) = await RunCli(exe, dataRoot, "--analyze", "cli-gate");
            Check(rc3 == 0 && out3.Contains("分析"), "--analyze 退出码 0 且有分析输出", $"rc={rc3}");
            Check(!File.Exists(Path.Combine(right, "a.txt")), "--analyze 不写目标侧");

            // 4) --run：真执行 + 磁盘 SHA-256 证据（真复制铁律：不能只测退出码）
            var (rc4, out4) = await RunCli(exe, dataRoot, "--run", "cli-gate");
            Check(rc4 == 0 && out4.Contains("成功"), "--run 退出码 0", $"rc={rc4}");
            var srcBytes = await File.ReadAllBytesAsync(Path.Combine(left, "a.txt"));
            var dstBytes = await File.ReadAllBytesAsync(Path.Combine(right, "a.txt"));
            Check(srcBytes.SequenceEqual(dstBytes), "磁盘真数据：目标 a.txt 与源逐字节一致");
            Check(Sha256(await File.ReadAllBytesAsync(Path.Combine(right, "b.bin")))
                == Sha256(await File.ReadAllBytesAsync(Path.Combine(left, "b.bin"))), "磁盘真数据：b.bin SHA-256 一致");

            // 5) --run-all 跑全部任务
            var (rc5, out5) = await RunCli(exe, dataRoot, "--run-all");
            Check(rc5 == 0, "--run-all 退出码 0", $"rc={rc5}");

            // 6) 任务不存在：退出码 5
            var (rc6, out6) = await RunCli(exe, dataRoot, "--run", "no-such-job");
            Check(rc6 == 5, "任务不存在退出码 5", $"rc={rc6} {out6.Trim()[..Math.Min(60, out6.Trim().Length)]}");

            // 7) CLI 日志落盘（数据根 logs/cli）
            var cliLogs = Directory.GetFiles(Path.Combine(dataRoot, "logs", "cli"), "cli-*.log");
            Check(cliLogs.Length > 0, "CLI 运行日志已落数据目录", $"{cliLogs.Length} 档");
        }
        finally
        {
            try { Directory.Delete(dataRoot, true); } catch { }
        }
        Console.WriteLine(_fail == 0 ? "=== cli 全部通过 ===" : $"=== {_fail} 项失败 ===");
        return _fail;
    }

    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var wi = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(wi)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static string Sha256(byte[] data) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));

    /// <summary>CLI 可执行文件定位：显式参数 > 常规构建输出（Debug/Release × net8.0[-windows]）。</summary>
    private static string? ResolveExe(string? exeArg)
    {
        if (!string.IsNullOrWhiteSpace(exeArg) && File.Exists(exeArg)) return Path.GetFullPath(exeArg);
        // BaseDirectory = tests/FolderSync.SmokeTest/bin/<cfg>/<tfm>/ → 5 级回到仓库根
        var appDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "FolderSync.App", "bin"));
        var names = OperatingSystem.IsWindows() ? new[] { "FolderSync.exe" } : new[] { "FolderSync", "FolderSync.exe" };
        foreach (var cfg in new[] { "Debug", "Release" })
            foreach (var tfm in new[] { "net8.0", "net8.0-windows" })
                foreach (var n in names)
                {
                    var p = Path.Combine(appDir, cfg, tfm, n);
                    if (File.Exists(p)) return p;
                }
        return null;
    }

    /// <summary>跑一次 CLI：隔离数据根 + 输出聚合（stdout 非零退出码时附 stderr）。</summary>
    private static async Task<(int rc, string output)> RunCli(string exe, string dataRoot, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = dataRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // CLI 侧 StreamWriter 恒 UTF-8：不设则 .NET 默认按控制台代码页（中文系统 GBK）解码，
            // 中文输出变乱码、关键词断言全数失真（2026-09-16 cli 门禁实测）
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["FOLDERSYNC_DATA"] = dataRoot;
        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, stdout + (stderr.Length > 0 ? "\n[stderr] " + stderr : ""));
    }
}
