using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Core.Platform;

namespace FolderSync.SmokeTest;

/// <summary>Unix 平台文件操作套件（方案 §8.1 新增）：流复制+进度/错误映射、XDG Trash、
/// utimensat 保 mtime。Windows 上整体 SKIP（执行矩阵：Unix 套件只在本机/CI unix 跑）。</summary>
internal static class UnixFileOpsTests
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
            Console.WriteLine("SKIP  unixfileops：Unix 专属套件（Windows 跑 XDG Trash 语义不成立）");
            return Task.FromResult(0);
        }
        return Task.FromResult(RunCore());
    }

    private static int RunCore()
    {
        TestStreamCopyWithProgress();
        TestCopyTimestampsMtime();
        TestXdgTrash();
        TestErrnoMapping();
        Console.WriteLine(_fail == 0 ? "=== unixfileops 全部通过 ===" : $"=== {_fail} 项失败 ===");
        return _fail;
    }

    /// <summary>分块流复制：进度回调单调、取消即中止、产物逐字节一致、权限位跟随。</summary>
    private static void TestStreamCopyWithProgress()
    {
        Console.WriteLine("--- 流复制+进度 ---");
        var ops = Platform.FileOps;
        if (ops is not Platforms.Unix.UnixFileOps)
        {
            Check(false, "装配了 UnixFileOps（测试入口未装配平台实现）");
            return;
        }
        var dir = Path.Combine(Path.GetTempPath(), "fsunix_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var src = Path.Combine(dir, "src.bin");
            var dst = Path.Combine(dir, "dst.bin");
            var data = new byte[10 * 1024 * 1024];
            new Random(42).NextBytes(data);
            File.WriteAllBytes(src, data);

            long lastTransferred = 0, totalSeen = 0;
            bool cancelSent = false;
            var ok = ops.CopyFile(src, dst, (total, transferred) =>
            {
                totalSeen = total;
                if (transferred < lastTransferred) throw new Exception("进度回调回退");
                lastTransferred = transferred;
                if (transferred > 8 * 1024 * 1024) { cancelSent = true; return false; }
                return true;
            }, out var err1);
            Check(!ok && cancelSent && err1 == 1235, "进度回调返回 false → 中止且 win32=1235", $"ok={ok} err={err1}");

            var ok2 = ops.CopyFile(src, dst, null, out var err2);
            Check(ok2 && err2 == 0, "全量复制成功");
            Check(File.ReadAllBytes(dst).AsSpan().SequenceEqual(data), "产物逐字节一致");
            Check(totalSeen == data.Length, "进度 total = 文件长度", $"{totalSeen}");

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                var mode = File.GetUnixFileMode(src);
                File.SetUnixFileMode(src, UnixFileMode.UserExecute | UnixFileMode.UserRead | UnixFileMode.UserWrite);
                ops.CopyFile(src, dst + "2", null, out _);
                Check(File.GetUnixFileMode(dst + "2").HasFlag(UnixFileMode.UserExecute), "权限位跟随源（可执行位保留）");
                File.SetUnixFileMode(src, mode);
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>CopyTimestamps：mtime 精确保留（futimens 通道）。</summary>
    private static void TestCopyTimestampsMtime()
    {
        Console.WriteLine("--- utimensat 保 mtime ---");
        var dir = Path.Combine(Path.GetTempPath(), "fsunix2_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var src = Path.Combine(dir, "s.txt");
            var dst = Path.Combine(dir, "d.txt");
            File.WriteAllText(src, "hello");
            File.WriteAllText(dst, "world");
            var past = DateTime.UtcNow.AddHours(-5);
            File.SetLastWriteTimeUtc(src, past);
            Platform.FileOps.CopyTimestamps(src, dst);
            var delta = Math.Abs((File.GetLastWriteTimeUtc(dst) - past).TotalMilliseconds);
            Check(delta < 2000, "mtime 显式保留（2s 容差）", $"Δ={delta:F0}ms");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>XDG Trash（Linux）/ ~/.Trash（macOS）：删除后原位消失、Trash 内出现。</summary>
    private static void TestXdgTrash()
    {
        Console.WriteLine("--- 回收站（XDG Trash / ~/.Trash） ---");
        var dir = Path.Combine(Path.GetTempPath(), "fstrash_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var f1 = Path.Combine(dir, "a.txt");
            File.WriteAllText(f1, "1");
            var f2 = Path.Combine(dir, "sub");
            Directory.CreateDirectory(f2);
            File.WriteAllText(Path.Combine(f2, "b.txt"), "2");

            var ops = Platform.FileOps;
            ops.RecycleDelete(f1, isDirectory: false);
            Check(!File.Exists(f1), "文件回收站删除后原位消失");

            var trashFiles = OperatingSystem.IsMacOS()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash")
                : Path.Combine(TrashHomeLinux(), "files");
            var landed = Directory.Exists(trashFiles)
                && (File.Exists(Path.Combine(trashFiles, "a.txt")) || File.Exists(Path.Combine(trashFiles, "a.2.txt")));
            Check(landed, "回收站内出现被删文件", trashFiles);

            if (!OperatingSystem.IsMacOS())
            {
                // .trashinfo 元数据存在（恢复工具按它找原路径）
                var infoDir = Path.Combine(TrashHomeLinux(), "info");
                Check(Directory.Exists(infoDir)
                    && (File.Exists(Path.Combine(infoDir, "a.txt.trashinfo")) || File.Exists(Path.Combine(infoDir, "a.2.txt.trashinfo"))),
                    ".trashinfo 元数据已写");
            }

            ops.RecycleDelete(f2, isDirectory: true);
            Check(!Directory.Exists(f2), "目录回收站删除成功");

            // 批量接口：不抛 + 原位消失
            var f3 = Path.Combine(dir, "c.txt");
            File.WriteAllText(f3, "3");
            ops.RecycleDeleteBatch(new[] { f3 });
            Check(!File.Exists(f3), "批量删除原位消失");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static string TrashHomeLinux()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "Trash")
            : Path.Combine(xdg, "Trash");
    }

    /// <summary>Errno→Win32 映射：不存在(ENOENT→2)、无权限(EACCES→5)、目录不存在(ENOTDIR→2)。</summary>
    private static void TestErrnoMapping()
    {
        Console.WriteLine("--- errno→Win32 映射 ---");
        var ops = Platform.FileOps;
        ops.CopyFile(Path.Combine(Path.GetTempPath(), "definitely_missing_" + Guid.NewGuid().ToString("N")[..6]), 
            Path.Combine(Path.GetTempPath(), "dst_" + Guid.NewGuid().ToString("N")[..6]), null, out var e1);
        Check(e1 == 2, "源不存在 → ENOENT→2", $"err={e1}");
        Check(Errno.ToWin32(new IOException("x", unchecked((int)0x8007001C))) == 112,
            "ENOSPC(28)→112 磁盘满熔断映射");
        Check(Errno.ToWin32(new IOException("x", unchecked((int)0x8007000B))) == 32,
            "EAGAIN(11)→32 共享冲突（瞬时错误类）", "");
    }
}
