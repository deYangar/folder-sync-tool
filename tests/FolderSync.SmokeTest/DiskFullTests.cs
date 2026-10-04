using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Core;

namespace FolderSync.SmokeTest
{
    /// <summary>
    /// 必修2 回归（2026-09-15 v1.7 审查）：并行模式磁盘满必须落 error（曾误报 cancelled——
    /// 磁盘满只置 _cancel+_diskFull 不触发 token，在途 worker 的进度回调抛 OCE 置 _cancelOce，
    /// ForEach 后补抛把「熔断副作用」当成了用户取消，DiskFull→error 收尾分支永远到不了）。
    /// 用 64MB 固定 VHD 构造真实磁盘满（提权 + diskpart），断言 runs 落 error、非 cancelled。
    /// 跑法（需提权）：fstest diskfull
    /// </summary>
    internal static class DiskFullTests
    {
        private static int _fail;

        private static void Check(bool cond, string name, string extra = "")
        {
            Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}" + (extra.Length > 0 ? $" ({extra})" : ""));
            if (!cond) _fail++;
        }

        private const string Letter = "V:";   // 测试专用盘符（先查未被占用）
        private static string _vhd = "";

        public static async Task<int> RunAll()
        {
            var elevated = System.Security.Principal.WindowsIdentity.GetCurrent()
                .Owner?.IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid) == true;
            if (!elevated)
            {
                Console.WriteLine("SKIP  diskfull：需要提权（VHD 挂载，本机 run_elevated.py 链；CI 非提权 runner）");
                return 0;
            }
            if (Directory.Exists(Letter + "\\"))
            {
                Console.WriteLine($"FAIL  {Letter} 已被占用，换一个空闲盘符再跑");
                return 1;
            }
            try
            {
                await TestParallelDiskFull();
            }
            finally { CleanupVhd(); }
            Console.WriteLine(_fail == 0 ? "diskfull: ALL PASS" : $"diskfull: {_fail} FAILED");
            return _fail;
        }

        private static async Task TestParallelDiskFull()
        {
            var root = Path.Combine(Path.GetTempPath(), "fsdiskfull_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(root);
            if (!CreateVhd(root)) return;
            try
            {
                // 源总量 6×12MB=72MB > 64MB VHD 的 NTFS 可用（~50MB）：并行复制必然触发磁盘满
                var left = Path.Combine(root, "L");
                Directory.CreateDirectory(left);
                var right = Letter + "\\dst";
                var rng = new Random(99);
                for (int i = 0; i < 6; i++)
                {
                    var buf = new byte[12 * 1024 * 1024];
                    rng.NextBytes(buf);
                    File.WriteAllBytes(Path.Combine(left, $"f{i}.bin"), buf);
                }

                var job = new SyncJob
                {
                    Name = "diskfull", LeftPath = left, RightPath = right,
                    Direction = SyncDirection.MirrorLeftToRight, CopyWorkers = 4
                };
                using var db = new Db(Path.Combine(root, "t.db"));
                job.Id = db.InsertJob(job);
                var engine = new SyncEngine(job, db);
                var (rec, _) = await engine.RunAsync("manual");

                Check(rec.Status == "error", "磁盘满: runs 落 error（曾误报 cancelled）", rec.Status);
                Check(rec.Status != "cancelled", "磁盘满: 不是 cancelled", rec.Status);
                var errMsg = engine.LastError ?? rec.ErrorMessage ?? "";
                Check(errMsg.Contains("空间不足"), "磁盘满: LastError 说明空间不足", errMsg);
                Check(rec.ErrorMessage != null, "磁盘满: runs.error_message 已回填", rec.ErrorMessage ?? "(null)");
                Check(engine.Status == JobStatus.Idle, "磁盘满: 引擎回 Idle", engine.Status.ToString());
                // 落库核对（历史窗口语义）：runs 表最近一条 = error
                var row = db.GetRecentRuns(job.Id, 1).FirstOrDefault();
                Check(row != null && row.Status == "error", "磁盘满: db runs.status=error", row?.Status ?? "(none)");
                // 磁盘真实状态佐证：目标盘确实写满过（可用空间远小于源总量）
                var free = new DriveInfo(Letter).AvailableFreeSpace;
                Check(free < 6L * 12 * 1024 * 1024, "磁盘满: VHD 确实已写满", $"{free / 1024.0 / 1024.0:F1}MB 可用");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>64MB 固定 VHD → 挂载 → NTFS 快速格式化 → 分配盘符。失败打 FAIL 返回 false。</summary>
        private static bool CreateVhd(string root)
        {
            _vhd = Path.Combine(root, "fsfull.vhdx");
            var script = Path.Combine(root, "dp.txt");
            File.WriteAllText(script, string.Join("\r\n",
                $"create vdisk file=\"{_vhd}\" maximum=64 type=fixed",
                $"select vdisk file=\"{_vhd}\"",
                "attach vdisk",
                "create partition primary",
                "format fs=ntfs label=FSFULL quick",
                $"assign letter={Letter.TrimEnd(':')}",
                "exit"), System.Text.Encoding.ASCII);
            var ok = RunDiskpart(script);
            if (!ok) { Console.WriteLine("FAIL  VHD 创建/挂载失败（见上行 diskpart 输出）"); _fail++; return false; }
            // 等卷就绪（格式化/分配盘符有短暂延迟）
            for (int i = 0; i < 100; i++)
            {
                try { if (new DriveInfo(Letter).IsReady) return true; } catch { }
                Thread.Sleep(100);
            }
            Console.WriteLine("FAIL  VHD 卷 10s 内未就绪");
            _fail++;
            return false;
        }

        private static void CleanupVhd()
        {
            if (_vhd.Length == 0 || !File.Exists(_vhd)) return;
            var script = _vhd + ".cleanup.txt";
            try
            {
                File.WriteAllText(script, string.Join("\r\n",
                    $"select vdisk file=\"{_vhd}\"",
                    "detach vdisk",
                    $"delete vdisk file=\"{_vhd}\" noerr",
                    "exit"), System.Text.Encoding.ASCII);
                RunDiskpart(script, quiet: true);
                try { File.Delete(script); } catch { }
                try { if (File.Exists(_vhd)) File.Delete(_vhd); } catch { }
            }
            catch { }
        }

        private static bool RunDiskpart(string script, bool quiet = false)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo("diskpart.exe", $"/s \"{script}\"")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                })!;
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(120_000);
                if (!quiet) Console.WriteLine(output
                    .Split('\n').Where(l => l.Trim().Length > 0).Select(l => "    dp| " + l.Trim())
                    .Aggregate((a, b) => a + "\n" + b));
                return p.ExitCode == 0;
            }
            catch (Exception ex)
            {
                if (!quiet) Console.WriteLine("    diskpart 启动失败: " + ex.Message);
                return false;
            }
        }
    }
}
