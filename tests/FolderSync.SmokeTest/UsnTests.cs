using System;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Core;
using FolderSync.Platforms.Windows;

namespace FolderSync.SmokeTest
{
    /// <summary>USN 监控 + 实时引擎 + 定时引擎 测试（含诊断输出）。</summary>
    public static class UsnTests
    {
        private static int _fail;

        private static void Check(bool cond, string name)
        {
            Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}");
            if (!cond) _fail++;
        }

        private static void Check(bool cond, string name, string extra)
        {
            Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name} ({extra})");
            if (!cond) _fail++;
        }

        public static async Task<int> RunAll()
        {
            // CI Windows runner 非提权：USN 卷句柄打不开（win32=5），SKIP——
            // 提权覆盖走本机 run_elevated.py 链（执行矩阵 §8.5）
            if (!OperatingSystem.IsWindows())
            {
                Console.WriteLine("SKIP  usn：非 Windows");
                return 0;
            }
            using var wi = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(wi).IsInRole(WindowsBuiltInRole.Administrator))
            {
                Console.WriteLine("SKIP  usn：需要提权（本机 run_elevated.py 链；CI 非提权 runner）");
                return 0;
            }
            Console.WriteLine($"[env] 用户={wi.Name} 提权={new WindowsPrincipal(wi).IsInRole(WindowsBuiltInRole.Administrator)}");
            TestWatcherStandalone();
            TestRecursiveTreeDelete();   // L-13：删目录树不漏检（PASS 则维持现状，仅留测试）
            TestRealtimeEngine();
            TestRealtimeLongLivedWithNoise();     // GUI 场景复刻：长挂+噪声后不丢事件
            TestIntervalEngine();
            await TestStartupAutoRun();
            Console.WriteLine(_fail == 0 ? "\n=== USN/引擎 全部通过 ===" : $"\n=== {_fail} 项失败 ===");
            return _fail;
        }

        private static string TempDir() =>
            Path.Combine(Path.GetTempPath(), "fsusn_" + Guid.NewGuid().ToString("N")[..8]);

        /// <summary>独立监控器：创建/修改/删除/重命名/排除规则/子树外过滤。负向断言前先静默期排空尾事件。</summary>
        private static void TestWatcherStandalone()
        {
            Console.WriteLine("--- UsnWatcher 独立测试 ---");
            var root = TempDir();
            var other = TempDir(); // 同卷其他目录，验证不误触发
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            Directory.CreateDirectory(other);
            Thread.Sleep(800); // 让目录创建事件排空

            int fired = 0, overflow = 0;
            var evt = new AutoResetEvent(false);
            var w = new UsnWatcher(root, "*.log; skipped", () => { fired++; evt.Set(); },
                () => { overflow++; });
            Thread.Sleep(1200); // 等监控就绪

            try
            {
                File.WriteAllText(Path.Combine(root, "a.txt"), "1");
                Check(evt.WaitOne(8000), $"USN 检测到创建 (fired={fired})");

                evt.Reset();
                File.WriteAllText(Path.Combine(root, "a.txt"), "2");
                Check(evt.WaitOne(8000), "USN 检测到修改");

                evt.Reset();
                File.WriteAllText(Path.Combine(root, "sub", "b.txt"), "x");
                Check(evt.WaitOne(8000), "USN 检测到子目录创建");

                // 负向断言前：静默 6s 排空尾事件，用计数器比较而非 evt（避免尾事件污染）
                evt.Reset();
                Thread.Sleep(6000);
                int before = fired;

                File.WriteAllText(Path.Combine(root, "skip.log"), "no");
                Thread.Sleep(4000);
                Check(fired == before, $"排除规则生效（*.log 不触发，fired={fired}=={before}）");
                before = fired;

                evt.Reset();
                File.Delete(Path.Combine(root, "a.txt"));
                Check(evt.WaitOne(8000), "USN 检测到删除");

                evt.Reset();
                File.WriteAllText(Path.Combine(root, "r1.txt"), "r");
                evt.WaitOne(5000); evt.Reset(); // 吸收创建事件
                Thread.Sleep(2000); evt.Reset(); // 再排空
                File.Move(Path.Combine(root, "r1.txt"), Path.Combine(root, "r2.txt"));
                Check(evt.WaitOne(8000), "USN 检测到重命名");

                evt.Reset();
                Thread.Sleep(6000);
                before = fired;

                File.WriteAllText(Path.Combine(other, "outside.txt"), "no");
                Thread.Sleep(4000);
                Check(fired == before, $"子树外变更不触发 (fired={fired}=={before})");

                Check(overflow == 0, "无溢出事件");
            }
            finally
            {
                w.Dispose();
                try { Directory.Delete(root, true); } catch { }
                try { Directory.Delete(other, true); } catch { }
            }
        }

        /// <summary>复刻 GUI 真机场景（2026-09-12 实测丢事件）：watcher 长挂（10s）+ journal 流过
        /// 大量无关记录（别目录的批量写）后才改源文件——验证实时触发不是间歇性丢失。
        /// 诊断字段全打（probe/engine fire 计数），跑 3 轮看稳定性。</summary>
        private static void TestRealtimeLongLivedWithNoise()
        {
            Console.WriteLine("--- 实时：长挂 watcher + journal 噪声（GUI 场景复刻） ---");
            for (int round = 1; round <= 3; round++)
            {
                var root = TempDir();
                var left = Path.Combine(root, "L");
                var right = Path.Combine(root, "R");
                Directory.CreateDirectory(left);
                Directory.CreateDirectory(right);
                using var db = new Db(Path.Combine(root, "t.db"));
                var job = new SyncJob
                {
                    Name = "rt-noise", LeftPath = left, RightPath = right,
                    Direction = SyncDirection.MirrorLeftToRight,
                    Trigger = TriggerType.Realtime, DebounceSeconds = 2
                };
                job.Id = db.InsertJob(job);
                var engine = new SyncEngine(job, db);
                engine.ApplyTriggers();
                Thread.Sleep(1500);

                int probeFired = 0;
                var probe = new UsnWatcher(left, "", () => probeFired++, () => { });
                try
                {
                    // 噪声：别目录批量写（同卷 journal 洪流，模拟 GUI 场景 B/C 的 5400 文件创建）
                    var noise = Path.Combine(Path.GetTempPath(), "fsnoise_" + Guid.NewGuid().ToString("N")[..8]);
                    Directory.CreateDirectory(noise);
                    for (int i = 0; i < 2000; i++)
                        File.WriteAllText(Path.Combine(noise, $"n{i:000}.txt"), "noise");
                    // 二次噪声目录（模拟 GUI 的 C 场景：目标侧也批量写 5400 文件）
                    var noise2 = Path.Combine(Path.GetTempPath(), "fsnoise2_" + Guid.NewGuid().ToString("N")[..8]);
                    Directory.CreateDirectory(noise2);
                    for (int i = 0; i < 2000; i++)
                        File.WriteAllText(Path.Combine(noise2, $"m{i:000}.txt"), "noise2");
                    Thread.Sleep(8000);   // watcher 长挂到 ~10s（GUI 时序）

                    var t0 = DateTime.UtcNow;
                    File.WriteAllText(Path.Combine(left, "late.txt"), "late v1");
                    var got = WaitFor(() => File.Exists(Path.Combine(right, "late.txt")), 30000, out var elapsedMs);
                    Check(got, $"实时（轮{round}）：噪声+长挂后变更仍触发同步",
                        $"probe={probeFired} engineFired={engine.WatcherFireCount} elapsed={elapsedMs}ms");
                    if (!got)
                        Console.WriteLine($"    [diag] 轮{round} 丢失：probe={probeFired} engineFired={engine.WatcherFireCount} engine状态={engine.StatusText}");
                    try { Directory.Delete(noise, true); } catch { }
                    try { Directory.Delete(noise2, true); } catch { }
                }
                finally
                {
                    probe.Dispose();
                    engine.Dispose();
                    try { Directory.Delete(root, true); } catch { }
                }
            }
        }

        private static bool WaitFor(Func<bool> pred, int timeoutMs, out int elapsedMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (pred()) { elapsedMs = (int)sw.ElapsedMilliseconds; return true; }
                Thread.Sleep(50);
            }
            elapsedMs = (int)sw.ElapsedMilliseconds;
            return pred();
        }

        /// <summary>L-13（review-20260912）：一次性递归删除整棵子树，USN 监听须在 3s 内触发。
        /// NTFS 递归删除产 USN 记录子先父后，顶层记录 parent=root 恒可解析——本用例守住这条结论：
        /// 常见删除形态不得漏检；FAIL 再考虑 FRN 白名单方案（补丁集路线）。</summary>
        private static void TestRecursiveTreeDelete()
        {
            Console.WriteLine("--- USN 递归删除目录树（L-13） ---");
            var root = TempDir();
            Directory.CreateDirectory(Path.Combine(root, "dying", "sub"));
            Directory.CreateDirectory(Path.Combine(root, "dying", "deeper", "more"));
            File.WriteAllText(Path.Combine(root, "dying", "sub", "x.txt"), "x");
            File.WriteAllText(Path.Combine(root, "dying", "deeper", "more", "y.txt"), "y");
            File.WriteAllText(Path.Combine(root, "dying", "z.txt"), "z");
            Thread.Sleep(800); // 让目录创建事件排空

            var evt = new AutoResetEvent(false);
            using (var w = new UsnWatcher(root, "", () => evt.Set(), () => { }))
            {
                Thread.Sleep(1200); // 等监控就绪
                Directory.Delete(Path.Combine(root, "dying"), recursive: true);
                Check(evt.WaitOne(3000), "递归删除目录树 3s 内触发（子先父后记录序 + root FRN 可解析）");
            }
            try { Directory.Delete(root, true); } catch { }
        }

        /// <summary>端到端：实时触发引擎自动同步。带并行探针监视器与引擎事件诊断。</summary>
        private static void TestRealtimeEngine()
        {
            Console.WriteLine("--- 实时引擎端到端 ---");
            var root = TempDir();
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);

            var dbPath = Path.Combine(root, "t.db");
            using var db = new Db(dbPath);
            var job = new SyncJob
            {
                Name = "rt", LeftPath = left, RightPath = right,
                Direction = SyncDirection.MirrorLeftToRight,
                Trigger = TriggerType.Realtime, DebounceSeconds = 2,
                MirrorDelete = true
            };
            job.Id = db.InsertJob(job);
            var engine = new SyncEngine(job, db);
            engine.StateChanged += e => Console.WriteLine($"    [engine] {e.StatusText}");
            engine.RunFinished += (e, r) => Console.WriteLine($"    [run] {r.Trigger} {r.Status} copied={r.CopiedFiles} failed={r.FailedFiles} err={r.ErrorMessage}");
            engine.ApplyTriggers();
            Console.WriteLine($"    [engine] 监听状态: {engine.StatusText}");
            Thread.Sleep(1500); // 监控就绪

            // 并行探针：独立 UsnWatcher 数 left 的事件（验证引擎监听源是否收到事件）
            int probeFired = 0;
            var probe = new UsnWatcher(left, "", () => probeFired++, () => { });

            try
            {
                File.WriteAllText(Path.Combine(left, "rt.txt"), "v1");
                bool created = WaitFor(() => File.Exists(Path.Combine(right, "rt.txt")), 20000);
                Check(created, $"实时：创建后自动同步出现 (probe={probeFired} engineFired={engine.WatcherFireCount})");

                File.WriteAllText(Path.Combine(left, "rt.txt"), "v2");
                bool updated = WaitFor(() => SafeRead(Path.Combine(right, "rt.txt")) == "v2", 20000);
                Check(updated, $"实时：修改后自动更新内容 (probe={probeFired} engineFired={engine.WatcherFireCount})");

                File.WriteAllText(Path.Combine(left, "gone.txt"), "bye");
                WaitFor(() => File.Exists(Path.Combine(right, "gone.txt")), 20000);
                File.Delete(Path.Combine(left, "gone.txt"));
                Check(WaitFor(() => !File.Exists(Path.Combine(right, "gone.txt")), 20000),
                    "实时：源删除后镜像删除目标副本（MirrorDelete=true）");
            }
            finally
            {
                probe.Dispose();
                engine.Dispose();
                try { Directory.Delete(root, true); } catch { }
            }
        }

        /// <summary>定时间隔触发。</summary>
        private static void TestIntervalEngine()
        {
            Console.WriteLine("--- 定时引擎 ---");
            var root = TempDir();
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);

            using var db = new Db(Path.Combine(root, "t.db"));
            var job = new SyncJob
            {
                Name = "itv", LeftPath = left, RightPath = right,
                Trigger = TriggerType.Interval, IntervalSeconds = 2,
                MirrorDelete = false
            };
            job.Id = db.InsertJob(job);
            var engine = new SyncEngine(job, db);
            engine.ApplyTriggers();

            try
            {
                File.WriteAllText(Path.Combine(left, "itv.txt"), "periodic");
                Check(WaitFor(() => File.Exists(Path.Combine(right, "itv.txt")), 12000),
                    "定时：间隔 2s 自动同步出现");
            }
            finally
            {
                engine.Dispose();
                try { Directory.Delete(root, true); } catch { }
            }
        }

        /// <summary>开机自启等价场景：引擎启动时 startup 触发自动补跑。</summary>
        private static async Task TestStartupAutoRun()
        {
            Console.WriteLine("--- 启动自动补跑 ---");
            var root = TempDir();
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            File.WriteAllText(Path.Combine(left, "boot.txt"), "at startup");

            using var db = new Db(Path.Combine(root, "t.db"));
            var job = new SyncJob
            {
                Name = "boot", LeftPath = left, RightPath = right,
                Trigger = TriggerType.Manual, AutoStart = true
            };
            job.Id = db.InsertJob(job);

            var engine = new SyncEngine(job, db);
            await engine.RunAsync("startup");
            engine.Dispose();

            Check(File.ReadAllText(Path.Combine(right, "boot.txt")) == "at startup",
                "启动补跑：任务启动即自动同步");
            try { Directory.Delete(root, true); } catch { }
        }

        private static string? SafeRead(string p)
        {
            try { return File.ReadAllText(p); } catch { return null; }
        }

        private static bool WaitFor(Func<bool> cond, int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (cond()) return true;
                Thread.Sleep(400);
            }
            return cond();
        }
    }
}
