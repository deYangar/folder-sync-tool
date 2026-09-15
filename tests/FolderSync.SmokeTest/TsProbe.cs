using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using FolderSync.Core;

namespace FolderSync.SmokeTest;

/// <summary>时间戳保留专项：CopyOne 的 CopyFileEx → CopyTimestamps → Move 序列原位验证（ctime/mtime 与源一致）。</summary>
public static class TsProbe
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CopyFileEx(string a, string b, IntPtr cb, IntPtr d, ref bool cancel, uint flags);

    public static int Run()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tsdotnet_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        int fail = 0;
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
                bool cancel = false;
                var ok = CopyFileEx(srcL, tmpL, IntPtr.Zero, IntPtr.Zero, ref cancel, 0);
                var mi = typeof(Executor).GetMethod("CopyTimestamps", BindingFlags.NonPublic | BindingFlags.Static)!;
                mi.Invoke(null, new object[] { srcL, tmpL });
                File.Move(tmpL, dstL, true);
                var (dCT, dMT) = (File.GetCreationTimeUtc(src) - File.GetCreationTimeUtc(dst),
                                  File.GetLastWriteTimeUtc(src) - File.GetLastWriteTimeUtc(dst));
                var pass = ok && Math.Abs(dCT.TotalSeconds) < 1 && Math.Abs(dMT.TotalSeconds) < 1;
                Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  时间戳保留[{name}]: CopyFileEx={ok} dCT={dCT.TotalSeconds:F1}s dMT={dMT.TotalSeconds:F1}s");
                if (!pass) fail++;
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
        Console.WriteLine(fail == 0 ? "=== 时间戳专项全部通过 ===" : $"=== {fail} 项失败 ===");
        return fail;
    }
}
