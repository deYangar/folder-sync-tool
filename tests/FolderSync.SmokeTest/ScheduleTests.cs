using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Core;

namespace FolderSync.SmokeTest
{
    /// <summary>
    /// B2 指定时刻调度专项：NextDue 计算（当天已过→明天/跨周）、Tick 注入时钟到点触发、
    /// 正在跑跳过、非法规格回退、trigger=3/schedule_spec Dapper 往返。
    /// </summary>
    internal static class ScheduleTests
    {
        private static int _fail;

        private static void Check(bool cond, string name, string extra = "")
        {
            Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}" + (extra.Length > 0 ? $" ({extra})" : ""));
            if (!cond) _fail++;
        }

        private static bool WaitFor(Func<bool> pred, int timeoutMs = 15000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (pred()) return true;
                Thread.Sleep(50);
            }
            return pred();
        }

        public static async Task<int> RunAll()
        {
            TestNextDueMatrix();
            await TestTickFiresAtDuePoint();
            await TestBusySkip();
            await TestInvalidSpecIgnored();
            TestDapperRoundTrip();
            Console.WriteLine(_fail == 0 ? "schedule: ALL PASS" : $"schedule: {_fail} FAILED");
            return _fail;
        }

        private static void TestNextDueMatrix()
        {
            // daily：当天时刻未过 → 今天；已过 → 明天；恰好等于触发点 → 严格大于不重复触发
            var wed10 = new DateTime(2026, 9, 16, 10, 0, 0);   // 周三 10:00（9/16 是周三）
            Check(ScheduleSpec.NextDue("daily 03:00", wed10) == new DateTime(2026, 9, 17, 3, 0, 0),
                "daily 已过 → 明天同一时刻", ScheduleSpec.NextDue("daily 03:00", wed10)?.ToString("yyyy-MM-dd HH:mm"));
            Check(ScheduleSpec.NextDue("daily 23:30", wed10) == new DateTime(2026, 9, 16, 23, 30, 0),
                "daily 未过 → 当天");
            Check(ScheduleSpec.NextDue("daily 10:00", wed10) == new DateTime(2026, 9, 17, 10, 0, 0),
                "daily 恰在触发点 → 下一次（严格大于，不重复触发）",
                ScheduleSpec.NextDue("daily 10:00", wed10)?.ToString("MM-dd HH:mm"));

            // weekly 1,3,5（一/三/五）：周三 10:00 起 → 周三 15:00（当天还有）
            Check(ScheduleSpec.NextDue("weekly 1,3,5 15:00", wed10) == new DateTime(2026, 9, 16, 15, 0, 0),
                "weekly 当天还有 → 当天");
            // 周三 16:00 起 → 下一个是周五 15:00
            Check(ScheduleSpec.NextDue("weekly 1,3,5 15:00", wed10.AddHours(6)) == new DateTime(2026, 9, 18, 15, 0, 0),
                "weekly 当天已过 → 本周下一个指定日",
                ScheduleSpec.NextDue("weekly 1,3,5 15:00", wed10.AddHours(6))?.ToString("MM-dd HH:mm"));
            // 周五 16:00 起 → 下周一 15:00（跨周）
            Check(ScheduleSpec.NextDue("weekly 1,3,5 15:00", new DateTime(2026, 9, 18, 16, 0, 0)) == new DateTime(2026, 9, 21, 15, 0, 0),
                "weekly 全部已过 → 下周第一个指定日（跨周）");
            // 周日（7）边界
            Check(ScheduleSpec.NextDue("weekly 7 08:00", new DateTime(2026, 9, 18, 16, 0, 0)) == new DateTime(2026, 9, 20, 8, 0, 0),
                "weekly 7=周日边界", ScheduleSpec.NextDue("weekly 7 08:00", new DateTime(2026, 9, 18, 16, 0, 0))?.ToString("MM-dd HH:mm"));

            // 非法规格：解析 null → NextDue null（调度器跳过不炸）
            Check(ScheduleSpec.Parse(null) == null && ScheduleSpec.Parse("") == null
                  && ScheduleSpec.Parse("daily") == null && ScheduleSpec.Parse("daily 25:00") == null
                  && ScheduleSpec.Parse("weekly 8 03:00") == null && ScheduleSpec.Parse("weekly 03:00") == null
                  && ScheduleSpec.Parse("haha 03:00") == null,
                "非法规格全部拒收（脏数据回退手动语义）");
            Check(ScheduleSpec.NextDue("bad spec", wed10) == null, "非法规格 NextDue=null");

            // 显示文案
            var desc = ScheduleSpec.DescribeNext("daily 23:30", wed10);
            Check(desc.Contains("23:30") && desc.Contains("今天"), "DescribeNext 今天", desc);
        }

        private static (string root, string left, string right, Db db, JobManager manager, SyncEngine engine, SyncJob job)
            Setup(string tag, string spec)
        {
            var root = Path.Combine(Path.GetTempPath(), "fssched_" + tag + "_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            File.WriteAllText(Path.Combine(left, "a.txt"), "内容");
            var job = new SyncJob
            {
                Name = "sched-" + tag, LeftPath = left, RightPath = right,
                Trigger = TriggerType.Schedule, ScheduleSpec = spec, AutoStart = false
            };
            var db = new Db(Path.Combine(root, "test.db"));
            job.Id = db.InsertJob(job);
            var manager = new JobManager(db) { ScheduleTickSeconds = 3600 };   // 测试直接注入时钟，禁真 timer
            manager.LoadAllAndResumeAsync().Wait();
            var engine = manager.Engines.First();
            return (root, left, right, db, manager, engine, job);
        }

        private static void Cleanup(string root, Db db, JobManager manager)
        {
            manager.Dispose();
            db.Dispose();
            try { Directory.Delete(root, true); } catch { }
        }

        /// <summary>到点触发：anchor 之后有已过的调度点 → Tick(now) 发起 RunAsync(trigger:schedule)。</summary>
        private static async Task TestTickFiresAtDuePoint()
        {
            var (root, left, right, db, manager, engine, job) = Setup("fire", "daily 03:00");
            try
            {
                // 锚点=进程起点（真实约 now）→ due=明天 03:00。注入 now=due+1min 的未来时钟直接命中
                var due = ScheduleSpec.NextDue(job.ScheduleSpec, DateTime.Now.AddSeconds(-5))!.Value;
                manager.Tick(due.AddMinutes(1));
                var fired = WaitFor(() => !engine.IsRunning && db.GetRecentRuns(job.Id).Any(r => r.Trigger == "schedule"));
                Check(fired, "fire: 到点触发 schedule 轮");
                var run = db.GetRecentRuns(job.Id).FirstOrDefault(r => r.Trigger == "schedule");
                Check(run?.Status == "ok" && run.CopiedFiles >= 1, "fire: 轮次完成且文件同步",
                    $"{run?.Status} copied={run?.CopiedFiles}");
                Check(File.Exists(Path.Combine(right, "a.txt")), "fire: 产物在");

                // 同一个调度点不再重复触发（lastFire 已前移）
                manager.Tick(due.AddMinutes(2));
                Thread.Sleep(500);
                Check(db.GetRecentRuns(job.Id).Count(r => r.Trigger == "schedule") == 1, "fire: 同点不重复触发");
            }
            finally { Cleanup(root, db, manager); }
        }

        /// <summary>正在跑 → 跳过本轮（不补触发不堆积），下个调度点正常。
        /// 用独占锁制造稳定的忙窗口（重试趟默认 2s/10s/30s）。</summary>
        private static async Task TestBusySkip()
        {
            var (root, left, right, db, manager, engine, job) = Setup("busy", "daily 03:00");
            try
            {
                var round = Task.Run(() => engine.RunAsync("manual"));
                // 锁住源文件让 manual 轮进入重试趟（忙窗口稳定 ~40s）
                using (var hold = new FileStream(Path.Combine(left, "a.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    WaitFor(() => engine.StatusText.Contains("自动重试") || engine.IsRunning, 10000);
                    Thread.Sleep(300);   // 确保已进入稳定执行段
                    var due = ScheduleSpec.NextDue(job.ScheduleSpec, DateTime.Now.AddSeconds(-5))!.Value;
                    manager.Tick(due.AddMinutes(1));   // 正在跑的窗口内到点
                    Thread.Sleep(500);
                    Check(db.GetRecentRuns(job.Id).All(r => r.Trigger != "schedule"),
                        "busy: 正在跑时到点被跳过（无 schedule 轮）",
                        string.Join(",", db.GetRecentRuns(job.Id).Select(r => r.Trigger)));
                }   // 释放锁 → manual 轮重试成功收尾
                try { await round; } catch { }

                // 跳过的调度点不补触发；锚点=上次 tick 的注入时刻，下个调度点（daily→再下一天 03:00）照常
                var lastFire = ScheduleSpec.NextDue(job.ScheduleSpec, DateTime.Now.AddSeconds(-5))!.Value.AddMinutes(1);
                var nextDue = ScheduleSpec.NextDue(job.ScheduleSpec, lastFire)!.Value;
                manager.Tick(nextDue.AddMinutes(1));
                var fired = WaitFor(() => db.GetRecentRuns(job.Id).Any(r => r.Trigger == "schedule"));
                Check(fired, "busy: 下个调度点正常触发");
            }
            finally { Cleanup(root, db, manager); }
        }

        /// <summary>非法/缺失规格的 Schedule 任务：Tick 静默跳过不炸不触发。</summary>
        private static async Task TestInvalidSpecIgnored()
        {
            var (root, left, right, db, manager, engine, job) = Setup("bad", "not a spec");
            try
            {
                manager.Tick(DateTime.Now.AddDays(10));
                Thread.Sleep(400);
                Check(db.GetRecentRuns(job.Id).All(r => r.Trigger != "schedule"), "bad: 非法规格不触发");
                Check(manager.Engines.Count() == 1, "bad: 调度器未崩");
            }
            finally { Cleanup(root, db, manager); }
        }

        private static void TestDapperRoundTrip()
        {
            var root = Path.Combine(Path.GetTempPath(), "fssched_db_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(root);
            var dbPath = Path.Combine(root, "t.db");
            try
            {
                long id;
                using (var db = new Db(dbPath))
                    id = db.InsertJob(new SyncJob
                    {
                        Name = "sc", LeftPath = Path.Combine(root, "L"), RightPath = Path.Combine(root, "R"),
                        Trigger = TriggerType.Schedule, ScheduleSpec = "weekly 1,3,5 03:00"
                    });
                using (var db2 = new Db(dbPath))
                {
                    var j = db2.GetJob(id);
                    // trigger_type=3 与 schedule_spec 都要读回（trigger_type 缩写列名 AS 既有防线 + 新 TEXT 列）
                    Check(j?.Trigger == TriggerType.Schedule, "db: trigger=3 往返", j?.Trigger.ToString());
                    Check(j?.ScheduleSpec == "weekly 1,3,5 03:00", "db: schedule_spec 往返", j?.ScheduleSpec ?? "(null)");
                }
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
