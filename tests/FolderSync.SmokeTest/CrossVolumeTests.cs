using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FolderSync.Core;

namespace FolderSync.SmokeTest
{
    /// <summary>
    /// 跨卷专项（exFAT 真卷 + 中心根在系统盘）：侧在可移动卷、版本库中心在另一卷时，
    /// 归档/还原跨卷读写（块读自可移动卷、写/校验走系统盘中心库）。
    /// 经 exfat_vhd_test.py 提权调用（TEMP 不重定向 → 中心根落系统盘临时目录）。
    /// </summary>
    internal static class CrossVolumeTests
    {
        private static int _fail;

        private static void Check(bool cond, string name)
        {
            Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}");
            if (!cond) _fail++;
        }

        public static async Task<int> RunAll(string sidesRoot)
        {
            if (string.IsNullOrEmpty(sidesRoot))
            {
                Console.WriteLine("FAIL  用法: xvolume <侧根目录（可移动卷上）>");
                return 1;
            }
            Directory.CreateDirectory(sidesRoot);
            var root = Path.Combine(sidesRoot, "xvol_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(root);
            try
            {
                static string Vol(string p) =>
                    Path.GetPathRoot(Path.GetFullPath(p))!.TrimEnd('\\').ToUpperInvariant();
                Check(Vol(root) != Vol(VersionStore.CentralRoot),
                    $"前置: 侧与中心根异卷（侧 {Vol(root)} / 中心 {Vol(VersionStore.CentralRoot)}）");

                // 1) 跨卷归档/还原：侧文件读自可移动卷，块落系统盘中心库
                var side = Path.Combine(root, "side");
                Directory.CreateDirectory(side);
                var f = Path.Combine(side, "doc.bin");
                var payload = new byte[700 * 1024];
                new Random(1).NextBytes(payload);
                await File.WriteAllBytesAsync(f, payload);
                var ok = VersionStore.ArchiveFile(side, f, "doc.bin", 1, "t1", out var err);
                Check(ok, $"跨卷: 归档成功（{err}）");
                Check(VersionStore.StoreRootOf(side).StartsWith(VersionStore.CentralRoot, StringComparison.OrdinalIgnoreCase),
                    "跨卷: 库落中心（系统盘）");
                var dest = Path.Combine(root, "restored.bin");
                VersionStore.RestoreVersion(side, 1, dest);
                Check(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(dest))
                      .SequenceEqual(System.Security.Cryptography.SHA256.HashData(payload)),
                    "跨卷: 还原逐字节一致");
            }
            finally { try { Directory.Delete(root, true); } catch { } }

            Console.WriteLine(_fail == 0 ? "\n=== 跨卷专项全部通过 ===" : $"\n=== {_fail} 项失败 ===");
            return _fail;
        }
    }
}
