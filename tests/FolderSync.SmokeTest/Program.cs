using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Core;

namespace FolderSync.SmokeTest
{
    internal class Program
    {
        private static int _fail;

        private static void Check(bool cond, string name)
        {
            Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}");
            if (!cond) _fail++;
        }

        private static async Task<int> Main(string[] args)
        {
            // 测试数据根隔离：db/logs/报告/失败明细全走测试专属临时目录，绝不落真实用户数据目录
            // （AppPaths 是全静态惰性单例——Main 第一句注入后全进程固定）
            var testRoot = Path.Combine(Path.GetTempPath(),
                "fsappdata_" + Guid.NewGuid().ToString("N")[..8]);
            Core.Platform.AppPaths.Init(testRoot);
            Core.Platform.Platform.Init(FolderSync.Platforms.PlatformImpl.SelectForCurrentOS());
            // 版本库/基线库根隔离到临时目录：测试库不落测试程序目录，各次运行互不残留
            VersionStore.CentralRootOverride = Path.Combine(Path.GetTempPath(),
                "fscentral_" + Guid.NewGuid().ToString("N")[..8]);
            BaselineStore.RootOverride = Path.Combine(Path.GetTempPath(),
                "fsbaseline_" + Guid.NewGuid().ToString("N")[..8]);
            try { return await Route(args); }
            finally
            {
                try { Directory.Delete(VersionStore.CentralRootOverride, true); } catch { }
                try { Directory.Delete(BaselineStore.RootOverride, true); } catch { }
                try { Directory.Delete(testRoot, true); } catch { }
                VersionStore.CentralRootOverride = null;
                BaselineStore.RootOverride = null;
            }
        }

        /// <summary>Windows 专属套件门卫：非 Windows 编译目标/平台打 SKIP（CI 判定认 SKIP 不算挂）。</summary>
        private static Task<int> GateWindowsOnly(string name, Func<Task<int>> run)
        {
#if WINDOWS
            if (OperatingSystem.IsWindows()) return run();
#endif
            Console.WriteLine($"SKIP  {name}：Windows 专属套件");
            return Task.FromResult(0);
        }

        private static async Task<int> Route(string[] args)
        {
            if (args.Length > 0 && args[0] == "ts") return TsProbe.Run();
            if (args.Length > 0 && args[0] == "usn")
#if WINDOWS
                return await GateWindowsOnly("usn", UsnTests.RunAll);
#else
                { Console.WriteLine("SKIP  usn：Windows 专属（USN Journal）"); return 0; }
#endif
            if (args.Length > 0 && args[0] == "twoway") return await TwoWayTests.RunAll();
            if (args.Length > 0 && args[0] == "fixreg") return await FixRegressionTests.RunAll();
            if (args.Length > 0 && args[0] == "safety") return await SafetyTests.RunAll();
            if (args.Length > 0 && args[0] == "versionrepo") return await VersionRepoTests.RunAll();
            if (args.Length > 0 && args[0] == "delta") return await DeltaSyncTests.RunAll();
            if (args.Length > 0 && args[0] == "cancel") return await CancelTests.RunAll();
            if (args.Length > 0 && args[0] == "retry") return await RetryTests.RunAll();
            if (args.Length > 0 && args[0] == "resume" && args.Length > 1)
                return await ResumeSmoke.Run(args[1]);
            if (args.Length > 0 && args[0] == "backup") return await BackupTests.RunAll();
            if (args.Length > 0 && args[0] == "move") return await MoveTests.RunAll();
            if (args.Length > 0 && args[0] == "caseconflict") return await CaseConflictTests.RunAll();
            if (args.Length > 0 && args[0] == "conflictcopy") return await ConflictCopyTests.RunAll();
            if (args.Length > 0 && args[0] == "verify") return await VerifyTests.RunAll();
            if (args.Length > 0 && args[0] == "schedule") return await ScheduleTests.RunAll();
            if (args.Length > 0 && args[0] == "report") return await ReportTests.RunAll();
            if (args.Length > 0 && args[0] == "override") return await OverrideTests.RunAll();
            if (args.Length > 0 && args[0] == "parallel") return await ParallelTests.RunAll();
            if (args.Length > 0 && args[0] == "diskfull")   // 需提权（VHD）
#if WINDOWS
                return await GateWindowsOnly("diskfull", DiskFullTests.RunAll);
#else
                { Console.WriteLine("SKIP  diskfull：Windows 专属（VHD 磁盘满）"); return 0; }
#endif
            if (args.Length > 0 && args[0] == "deltaperf") return DeltaSyncTests.RunPerf(args.Length > 1 ? args[1] : null);
            if (args.Length > 0 && args[0] == "xvolume")
                return await CrossVolumeTests.RunAll(args.Length > 1 ? args[1] : "");
            if (args.Length > 0 && args[0] == "diag")
            {
                // L-6：裸 "fstest diag" 曾经直接 IndexOutOfRange
                if (args.Length < 2)
                { Console.WriteLine("用法: fstest diag <左目录> [右目录]"); return 2; }
                return await DiagTests.Run(args[1], args.Length > 2 ? args[2] : "");
            }
            if (args.Length > 0 && args[0] == "unixfileops") return await UnixFileOpsTests.RunAll();
            if (args.Length > 0 && args[0] == "watcher") return await WatcherTests.RunAll();
            if (args.Length > 0 && args[0] == "datapath") return await DataPathTests.RunAll();
            if (args.Length > 0 && args[0] == "autostart") return await AutostartSuite.RunAll();
            if (args.Length > 0 && args[0] == "cli")
                return await CliGateTests.RunAll(args.Length > 1 ? args[1] : null);
            if (args.Length > 0 && args[0] == "real")
            {
                if (args.Length < 3)
                { Console.WriteLine("用法: fstest real <左目录> <右目录>"); return 2; }
                return await RealDataTests.Run(args[1], args[2]);
            }
            if (args.Length > 0) return await Benchmark(args[0], args.Length > 1 ? args[1] : null);
            return await RunSmoke();
        }

        /// <summary>真实目录基准：fstest <扫描目录> [对比目录]</summary>
        private static async Task<int> Benchmark(string path, string? path2)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var entries = (await Scanner.ScanTreeAsync(path, "", null, CancellationToken.None)).Entries;
            Console.WriteLine($"扫描 {path}");
            Console.WriteLine($"  条目 {entries.Count}，耗时 {sw.Elapsed.TotalSeconds:F2}s");
            if (path2 != null)
            {
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                var e2 = (await Scanner.ScanTreeAsync(path2, "", null, CancellationToken.None)).Entries;
                Console.WriteLine($"  第二棵 {e2.Count} 条目，耗时 {sw2.Elapsed.TotalSeconds:F2}s");
                var sw3 = System.Diagnostics.Stopwatch.StartNew();
                var plan = Differ.Compute(new SyncJob { LeftPath = path, RightPath = path2 }, entries, e2);
                Console.WriteLine($"  差异计算 {plan.Count} 项，耗时 {sw3.Elapsed.TotalSeconds:F2}s");
            }
            return 0;
        }

        private static async Task<int> RunSmoke()
        {
            var root = Path.Combine(Path.GetTempPath(), "fstest_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(Path.Combine(left, "sub", "deep"));
            Directory.CreateDirectory(right);

            // 场景数据：左有右无的文件+目录、两边都有但左更新的文件
            File.WriteAllText(Path.Combine(left, "a.txt"), "hello v1");
            File.WriteAllText(Path.Combine(left, "sub", "b.txt"), "sub file");
            File.WriteAllText(Path.Combine(left, "sub", "deep", "c.txt"), "deep file");
            File.WriteAllText(Path.Combine(left, "skipme.log"), "should be excluded");
            File.WriteAllText(Path.Combine(right, "orphan.txt"), "orphan in target");
            Thread.Sleep(300);
            File.WriteAllText(Path.Combine(right, "a.txt"), "hello v1 old");  // 右侧旧版本
            Thread.Sleep(300);
            File.WriteAllText(Path.Combine(left, "a.txt"), "hello v2");        // 左侧最新（大小不同 → 必定检出差异）
            Thread.Sleep(50);

            var job = new SyncJob
            {
                Name = "smoke", LeftPath = left, RightPath = right,
                Direction = SyncDirection.MirrorLeftToRight,
                ExcludePatterns = "*.log",
                MirrorDelete = false   // 先测安全模式：不删孤儿
            };

            using var db = new Db(Path.Combine(root, "test.db"));
            job.Id = db.InsertJob(job);
            var engine = new SyncEngine(job, db);

            // 1) 分析（不执行）
            var (rec, plan) = await engine.RunAsync("manual", execute: false);
            Console.WriteLine($"计划 {plan.Count} 项: " + string.Join("; ", plan.Select(p => $"{p.Action} {p.RelativePath}")));
            Check(plan.Any(p => p.Action == SyncAction.UpdateRight && p.RelativePath == "a.txt"), "左新文件 → 覆盖右");
            Check(plan.Any(p => p.Action == SyncAction.CreateRight && p.RelativePath == "sub/b.txt"), "新建 sub/b.txt");
            Check(plan.Any(p => p.Action == SyncAction.CreateRight && p.RelativePath == "sub/deep/c.txt"), "新建 deep/c.txt");
            Check(!plan.Any(p => p.RelativePath.Contains("skipme.log")), "排除规则生效");
            Check(!plan.Any(p => p.RelativePath == "orphan.txt"), "安全模式不删孤儿");

            // 2) 执行
            var (rec2, plan2) = await engine.RunAsync("manual", execute: true);
            Console.WriteLine($"执行: ok={rec2.CopiedFiles} failed={rec2.FailedFiles}");
            Check(File.ReadAllText(Path.Combine(right, "a.txt")) == "hello v2", "覆盖后内容正确");
            Check(File.ReadAllText(Path.Combine(right, "sub", "deep", "c.txt")) == "deep file", "深层文件复制成功");
            Check(File.Exists(Path.Combine(right, "orphan.txt")), "孤儿文件保留");
            Check(rec2.FailedFiles == 0, "无失败项");

            // 3) 二次分析应无差异
            var (rec3, plan3) = await engine.RunAsync("manual", execute: false);
            Check(plan3.Count == 0, "二次分析无差异");

            // 4) 开启镜像删除再分析：孤儿应出现在删除计划
            job.MirrorDelete = true;
            var (rec4, plan4) = await engine.RunAsync("manual", execute: true);
            Check(plan4.Any(p => p.Action == SyncAction.DeleteRight && p.RelativePath == "orphan.txt"), "镜像删除计划包含孤儿");
            Check(!File.Exists(Path.Combine(right, "orphan.txt")), "孤儿已被删除");

            // 5) 反向验证：改右文件，右→左镜像
            job.MirrorDelete = false;
            job.Direction = SyncDirection.MirrorRightToLeft;
            File.WriteAllText(Path.Combine(right, "reverse.txt"), "from right");
            var (rec5, plan5) = await engine.RunAsync("manual", execute: true);
            Console.WriteLine($"RTL 计划 {plan5.Count} 项: " + string.Join("; ", plan5.Select(p => $"{p.Action} {p.RelativePath}")));
            Console.WriteLine($"RTL 执行: ok={rec5.CopiedFiles} failed={rec5.FailedFiles} err={rec5.ErrorMessage}");
            Check(File.ReadAllText(Path.Combine(left, "reverse.txt")) == "from right", "右→左方向同步正确");

            // 6) 历史记录
            var runs = db.GetRecentRuns(job.Id);
            Check(runs.Count >= 5, $"运行历史已记录 ({runs.Count} 条)");

            Console.WriteLine(_fail == 0 ? "\n=== 全部通过 ===" : $"\n=== {_fail} 项失败 ===");
            try { Directory.Delete(root, true); } catch { }
            return _fail;
        }
    }
}
