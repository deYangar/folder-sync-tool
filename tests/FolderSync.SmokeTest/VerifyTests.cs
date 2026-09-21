using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderSync.Core;

namespace FolderSync.SmokeTest
{
    /// <summary>
    /// B1 深度校验/复制后复核专项。
    /// copy_verify 失败（写坏盘）物理不可注入——复核失败分支为单行 throw，由正常路径测试
    /// （复核不误报）+ 代码审查覆盖，如实记录此限制。
    /// </summary>
    internal static class VerifyTests
    {
        private static int _fail;

        private static void Check(bool cond, string name, string extra = "")
        {
            Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}" + (extra.Length > 0 ? $" ({extra})" : ""));
            if (!cond) _fail++;
        }

        private static (string root, string left, string right, Db db, SyncEngine engine, SyncJob job)
            Setup(string tag, SyncDirection dir, int deepVerify = 0, bool copyVerify = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "fsverify_" + tag + "_" + Guid.NewGuid().ToString("N")[..8]);
            var left = Path.Combine(root, "L");
            var right = Path.Combine(root, "R");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            var job = new SyncJob
            {
                Name = "verify-" + tag, LeftPath = left, RightPath = right,
                Direction = dir, DeepVerify = deepVerify, CopyVerify = copyVerify
            };
            var db = new Db(Path.Combine(root, "test.db"));
            job.Id = db.InsertJob(job);
            return (root, left, right, db, new SyncEngine(job, db), job);
        }

        private static void Cleanup(string root, Db db)
        {
            db.Dispose();
            try { Directory.Delete(root, true); } catch { }
        }

        public static async Task<int> RunAll()
        {
            await TestBitRotDetected();            // 1 篡改内容+回拨 mtime → deep_verify=2 检出位腐
            await TestDeepVerifyTiers();           // 2 三档：0 关 / 1 仅≥50MB / 2 全量
            await TestBitRotOneWayAutoFix();       // 3 单向位腐：Conflict 执行=按源覆盖修复
            await TestVerifyAsyncButton();         // 4 工具栏校验（全量，不受三档限制）
            await TestCopyVerifyNormalPass();      // 5 copy_verify=on 正常路径复核通过不误报
            TestDapperRoundTrip();                 // 6 copy_verify/deep_verify 往返
            Console.WriteLine(_fail == 0 ? "verify: ALL PASS" : $"verify: {_fail} FAILED");
            return _fail;
        }

        private static async Task TestBitRotDetected()
        {
            var (root, left, right, db, engine, job) = Setup("rot", SyncDirection.TwoWay, deepVerify: 2);
            try
            {
                File.WriteAllText(Path.Combine(left, "a.txt"), "原始内容");
                File.WriteAllText(Path.Combine(left, "b.txt"), "另一个文件");
                await engine.RunAsync("manual");   // 首轮同步（deep_verify 全量但两侧一致无位腐）

                // 模拟位腐：右侧 a.txt 内容被静默改坏（字节数严格相同），mtime 回拨到与左侧一致
                var mtime = File.GetLastWriteTimeUtc(Path.Combine(left, "a.txt"));
                var raw = File.ReadAllBytes(Path.Combine(right, "a.txt"));
                raw[^1] ^= 0xFF;   // 末字节翻转：等长不同内容
                File.WriteAllBytes(Path.Combine(right, "a.txt"), raw);
                File.SetLastWriteTimeUtc(Path.Combine(right, "a.txt"), mtime);

                var (rec, plan) = await engine.RunAsync("manual");
                var row = plan.FirstOrDefault(p => p.RelativePath == "a.txt");
                Check(row != null && row.Action == SyncAction.Conflict && row.Note.Contains("位腐差异"),
                    "rot: 位腐产出冲突行（标人工）", $"{row?.Action} {row?.Note}");
                Check(plan.Count(p => p.Note.Contains("位腐差异")) == 1, "rot: 恰 1 处位腐（b.txt 一致不报）");
                // 双向不自动覆盖：右侧仍是篡改后内容（末字节翻转），未被左侧覆盖
                Check(!File.ReadAllBytes(Path.Combine(right, "a.txt")).SequenceEqual(File.ReadAllBytes(Path.Combine(left, "a.txt"))),
                    "rot: 双向不自动覆盖（两侧内容未动）");
            }
            finally { Cleanup(root, db); }
        }

        private static async Task TestDeepVerifyTiers()
        {
            // 档 0：关——位腐不检出
            var (root0, l0, r0, db0, e0, j0) = Setup("t0", SyncDirection.TwoWay, deepVerify: 0);
            try
            {
                File.WriteAllText(Path.Combine(l0, "x.txt"), "good");
                await e0.RunAsync("manual");
                var mtime = File.GetLastWriteTimeUtc(Path.Combine(l0, "x.txt"));
                File.WriteAllText(Path.Combine(r0, "x.txt"), "bad!");
                File.SetLastWriteTimeUtc(Path.Combine(r0, "x.txt"), mtime);
                var (_, plan0) = await e0.RunAsync("manual");
                Check(plan0.Count(p => p.Note.Contains("位腐差异")) == 0, "t0: 档 0 不校验");
            }
            finally { Cleanup(root0, db0); }

            // 档 1：仅 ≥50MB——小文件位腐不检出、大文件检出
            var (root1, l1, r1, db1, e1, j1) = Setup("t1", SyncDirection.TwoWay, deepVerify: 1);
            try
            {
                File.WriteAllText(Path.Combine(l1, "small.txt"), "good");
                var big = new byte[51 * 1024 * 1024];
                new Random(9).NextBytes(big);
                File.WriteAllBytes(Path.Combine(l1, "big.bin"), big);
                await e1.RunAsync("manual");

                var mt = File.GetLastWriteTimeUtc(Path.Combine(l1, "big.bin"));
                big[100] ^= 0xFF;   // 单字节位腐
                File.WriteAllBytes(Path.Combine(r1, "big.bin"), big);
                File.SetLastWriteTimeUtc(Path.Combine(r1, "big.bin"), mt);
                var mtS = File.GetLastWriteTimeUtc(Path.Combine(l1, "small.txt"));
                File.WriteAllText(Path.Combine(r1, "small.txt"), "bad!");
                File.SetLastWriteTimeUtc(Path.Combine(r1, "small.txt"), mtS);

                var (_, plan1) = await e1.RunAsync("manual");
                Check(plan1.Count(p => p.Note.Contains("位腐差异") && p.RelativePath == "big.bin") == 1,
                    "t1: 大文件位腐检出（≥50MB 档）");
                Check(plan1.Count(p => p.Note.Contains("位腐差异") && p.RelativePath == "small.txt") == 0,
                    "t1: 小文件不校（档 1 阈值）");
            }
            finally { Cleanup(root1, db1); }
        }

        /// <summary>3) 单向位腐（2026-09-21 review 参考#1 起）：好坏未知不抛硬币——
        /// 源未必是好方，按源覆盖可能毁掉唯一好副本；人工确认后删坏侧重同步即修复。</summary>
        private static async Task TestBitRotOneWayAutoFix()
        {
            var (root, left, right, db, engine, job) = Setup("fix", SyncDirection.MirrorLeftToRight, deepVerify: 2);
            try
            {
                File.WriteAllText(Path.Combine(left, "f.txt"), "权威内容");
                await engine.RunAsync("manual");
                var mtime = File.GetLastWriteTimeUtc(Path.Combine(left, "f.txt"));
                File.WriteAllText(Path.Combine(right, "f.txt"), "目标位腐");
                File.SetLastWriteTimeUtc(Path.Combine(right, "f.txt"), mtime);

                var (rec, plan) = await engine.RunAsync("manual");   // 执行轮：位腐行标冲突不动
                Check(rec.Status == "ok", "fix: 轮次 ok", rec.Status);
                Check(plan.Any(p => p.RelativePath == "f.txt" && p.Action == SyncAction.Conflict && p.BitRot),
                    "fix: 单向位腐产出冲突行（好坏未知）");
                Check(File.ReadAllText(Path.Combine(right, "f.txt")) == "目标位腐",
                    "fix: 单向不再自动按源覆盖（不抛硬币，人工裁决）");
                // 人工修复路径：确认左侧是好的 → 删掉坏的目标副本 → 重分析即正常复制
                File.Delete(Path.Combine(right, "f.txt"));
                var (rec2, plan2) = await engine.RunAsync("manual");
                Check(plan2.Any(p => p.Action == SyncAction.CreateRight && p.RelativePath == "f.txt"),
                    "fix: 删坏侧后重同步按单侧新建修复");
                Check(File.ReadAllText(Path.Combine(right, "f.txt")) == "权威内容", "fix: 修复后内容为源版本");
                var (_, plan3) = await engine.RunAsync("manual", execute: false);
                Check(plan3.Count == 0, "fix: 收敛", plan3.Count.ToString());
            }
            finally { Cleanup(root, db); }
        }

        private static async Task TestVerifyAsyncButton()
        {
            var (root, left, right, db, engine, job) = Setup("btn", SyncDirection.TwoWay, deepVerify: 0);
            try
            {
                // 任务设置档 0（关），但按钮全量校验不受限制
                File.WriteAllText(Path.Combine(left, "c.txt"), "内容 C");
                await engine.RunAsync("manual");
                var mtime = File.GetLastWriteTimeUtc(Path.Combine(left, "c.txt"));
                File.WriteAllText(Path.Combine(right, "c.txt"), "内容 D");
                File.SetLastWriteTimeUtc(Path.Combine(right, "c.txt"), mtime);

                var (rec, rot) = await engine.VerifyAsync();
                Check(rec.Trigger == "verify" && rec.Status == "ok", "btn: verify 轮落库", $"{rec.Trigger}/{rec.Status}");
                Check(rot.Count(p => p.Note.Contains("位腐差异")) == 1, "btn: 全量校验检出（不受三档限制）");
                Check(engine.LastConflictCount == 1, "btn: 冲突计数联动");
                // 只读：两侧内容原样
                Check(File.ReadAllText(Path.Combine(left, "c.txt")) == "内容 C"
                    && File.ReadAllText(Path.Combine(right, "c.txt")) == "内容 D", "btn: 只读未动文件");
                var runs = db.GetRecentRuns(job.Id);
                Check(runs.Any(r => r.Trigger == "verify"), "btn: runs 历史可见校验轮");
            }
            finally { Cleanup(root, db); }
        }

        private static async Task TestCopyVerifyNormalPass()
        {
            var (root, left, right, db, engine, job) = Setup("copyv", SyncDirection.MirrorLeftToRight, copyVerify: true);
            try
            {
                var buf = new byte[5 * 1024 * 1024];
                new Random(21).NextBytes(buf);
                File.WriteAllBytes(Path.Combine(left, "big.bin"), buf);
                File.WriteAllText(Path.Combine(left, "small.txt"), "小文件也复核");
                var (rec, _) = await engine.RunAsync("manual");
                Check(rec.Status == "ok" && rec.FailedFiles == 0, "copyv: 复核开启正常路径不误报", rec.Status);
                Check(File.ReadAllBytes(Path.Combine(right, "big.bin")).SequenceEqual(buf), "copyv: 大文件字节一致");
            }
            finally { Cleanup(root, db); }
        }

        private static void TestDapperRoundTrip()
        {
            var root = Path.Combine(Path.GetTempPath(), "fsverify_db_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(root);
            var dbPath = Path.Combine(root, "t.db");
            try
            {
                long id;
                using (var db = new Db(dbPath))
                    id = db.InsertJob(new SyncJob
                    {
                        Name = "vv", LeftPath = Path.Combine(root, "L"), RightPath = Path.Combine(root, "R"),
                        CopyVerify = true, DeepVerify = 2   // 双非默认值
                    });
                using (var db2 = new Db(dbPath))
                {
                    var j = db2.GetJob(id);
                    Check(j?.CopyVerify == true, "db: copy_verify 往返", j?.CopyVerify.ToString() ?? "(null)");
                    Check(j?.DeepVerify == 2, "db: deep_verify 往返", j?.DeepVerify.ToString() ?? "(null)");
                }
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
