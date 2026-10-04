using System;
using System.IO;
using System.Threading;
using FolderSync.Core;

namespace FolderSync.SmokeTest;

/// <summary>时间戳保留专项（跨平台期望表，方案 §8.1）：平台复制 → CopyTimestamps → Move 序列原位验证。
/// 期望：Windows ctime/mtime 双保；macOS birthtime/mtime；Linux 仅 mtime（crtime 不可移植，方案 §6）。</summary>
public static class TsProbe
{
    public static int Run()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tsdotnet_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        int fail = 0;
        var ops = Core.Platform.Platform.FileOps;
        bool keepCtime = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        try
        {
            var src = Path.Combine(dir, "src.bin");
            File.WriteAllText(src, "hello-world-0123456789");
            Thread.Sleep(2000);   // 拉开源与 tmp 的创建时间差，2s 断言窗口内可分辨
            foreach (var (name, overwrite) in new[] { ("目标不存在", false), ("目标已存在(overwrite)", true) })
            {
                var dst = Path.Combine(dir, $"dst_{overwrite}.bin");
                if (overwrite) File.WriteAllText(dst, "old");
                var tmp = Path.Combine(dir, $"tmp_{overwrite}.bin");
                var (srcL, tmpL, dstL) = (Executor.LongPath(src), Executor.LongPath(tmp), Executor.LongPath(dst));
                var ok = ops.CopyFile(srcL, tmpL, null, out _);
                ops.CopyTimestamps(srcL, tmpL);
                File.Move(tmpL, dstL, true);
                var (dCT, dMT) = (File.GetCreationTimeUtc(src) - File.GetCreationTimeUtc(dst),
                                  File.GetLastWriteTimeUtc(src) - File.GetLastWriteTimeUtc(dst));
                // Linux 的 ctime（inode change time）语义不同且 birthtime 不可移植：只断言 mtime
                var pass = ok && (!keepCtime || Math.Abs(dCT.TotalSeconds) < 1)
                                && Math.Abs(dMT.TotalSeconds) < 1;
                Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  时间戳保留[{name}]: Copy={ok} " +
                    (keepCtime ? $"dCT={dCT.TotalSeconds:F1}s " : "(ctime 按平台期望不保) ") +
                    $"dMT={dMT.TotalSeconds:F1}s");
                if (!pass) fail++;
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
        Console.WriteLine(fail == 0 ? "=== 时间戳专项全部通过 ===" : $"=== {fail} 项失败 ===");
        return fail;
    }
}
