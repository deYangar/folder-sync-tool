# -*- coding: utf-8 -*-
"""最小化复现：GUI 进程（publish exe）+ 预置 realtime 任务 + python 跨进程写源文件。
不带 UIA/大树——纯磁盘观察。5 轮统计丢率；失败轮抓托管栈。
用法（提权链内）：python repro_rt_loss.py <轮数>
"""
import os, sys, time, shutil, sqlite3, subprocess, tempfile

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
PROJECT = r"C:\Users\Yang\.zcode\workspace\default\projects\folder-sync-tool"
SRC_EXE = os.path.join(PROJECT, "publish", "FolderSync.App.exe")
ROUNDS = int(sys.argv[1]) if len(sys.argv) > 1 else 5

def sha_ok(a, b):
    return open(a, encoding="utf-8").read() == open(b, encoding="utf-8").read()

def one_round(n):
    root = os.path.join(tempfile.gettempdir(), f"fs_rtloss_{n}_" + os.urandom(3).hex())
    appdir = os.path.join(root, "app"); os.makedirs(appdir)
    l2 = os.path.join(root, "L2"); r2 = os.path.join(root, "R2")
    os.makedirs(l2); os.makedirs(r2)
    with open(os.path.join(l2, "live.txt"), "w") as f:
        f.write("v1")
    exe = os.path.join(appdir, "FolderSync.App.exe")
    shutil.copy2(SRC_EXE, exe)
    db = os.path.join(appdir, "foldersync.db")
    conn = sqlite3.connect(db)
    conn.executescript("""
CREATE TABLE jobs(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL,
  left_path TEXT NOT NULL, right_path TEXT NOT NULL, direction INTEGER NOT NULL DEFAULT 0,
  trigger_type INTEGER NOT NULL DEFAULT 0, interval_seconds INTEGER NOT NULL DEFAULT 7200,
  debounce_seconds INTEGER NOT NULL DEFAULT 10, exclude_patterns TEXT NOT NULL DEFAULT '',
  auto_start INTEGER NOT NULL DEFAULT 1, enabled INTEGER NOT NULL DEFAULT 1,
  delete_to_recycle_bin INTEGER NOT NULL DEFAULT 1, mirror_delete INTEGER NOT NULL DEFAULT 0,
  strict_mirror INTEGER NOT NULL DEFAULT 0, delta_sync INTEGER NOT NULL DEFAULT 1,
  created_at TEXT NOT NULL);
CREATE TABLE runs(id INTEGER PRIMARY KEY AUTOINCREMENT, job_id INTEGER NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
  started_at TEXT NOT NULL, finished_at TEXT, trigger TEXT NOT NULL DEFAULT 'manual',
  status TEXT NOT NULL DEFAULT 'running', scanned_files INTEGER NOT NULL DEFAULT 0,
  copied_files INTEGER NOT NULL DEFAULT 0, deleted_files INTEGER NOT NULL DEFAULT 0,
  skipped_files INTEGER NOT NULL DEFAULT 0, failed_files INTEGER NOT NULL DEFAULT 0,
  bytes_copied INTEGER NOT NULL DEFAULT 0, delta_saved_bytes INTEGER NOT NULL DEFAULT 0,
  avg_speed REAL, error_message TEXT);
""")
    conn.execute("INSERT INTO jobs(name,left_path,right_path,direction,trigger_type,auto_start,enabled,debounce_seconds,created_at)"
                 " VALUES('rt',?,?,0,1,1,1,2,?)", (l2, r2, "2026-09-12"))
    conn.commit(); conn.close()

    env = dict(os.environ); env["FS_E2E_SILENT"] = "1"
    proc = subprocess.Popen([exe, "--e2e", "--minimized"], env=env)
    try:
        time.sleep(6)   # 启动+startup 轮+watcher 就绪
        # 洪流变量（GUI 测试 B/C 的等价物）：watcher 挂载后、同卷别目录批量写 5400 文件——
        # 让 PollLoop 消化万条无关记录（洪流建早了没用：watcher 从挂载时刻的 NextUsn 开始读）
        flood = os.path.join(root, "FLOOD")
        os.makedirs(flood)
        for i in range(270):
            d = os.path.join(flood, f"d{i:03d}")
            os.makedirs(d)
            for j in range(20):
                with open(os.path.join(d, f"f{j:02d}.txt"), "w") as f:
                    f.write("flood " * 10)
        time.sleep(3)   # 让洪流流过 PollLoop
        t0 = time.time()
        with open(os.path.join(l2, "live.txt"), "w") as f:
            f.write(f"v2-round{n}")
        dst = os.path.join(r2, "live.txt")
        landed = False
        while time.time() - t0 < 60:
            if os.path.exists(dst):
                try:
                    if open(dst, encoding="utf-8").read() == f"v2-round{n}":
                        landed = True
                        break
                except Exception:
                    pass
            time.sleep(1)
        el = round(time.time() - t0, 1)
        print(f"round {n}: {'OK' if landed else 'LOST'} elapsed={el}s", flush=True)
        if not landed:
            # 抓栈 + state.txt + runs
            try:
                ds = os.path.join(os.environ.get("USERPROFILE", ""), ".dotnet", "tools", "dotnet-stack.exe")
                out = subprocess.run([ds, "report", "-p", str(proc.pid)], capture_output=True, text=True,
                                     encoding="utf-8", errors="replace", timeout=90)
                open(os.path.join(root, "stacks.txt"), "w", encoding="utf-8").write((out.stdout or "") + (out.stderr or ""))
                print(f"  stacks saved: {root}\\stacks.txt", flush=True)
            except Exception as ex:
                print(f"  stack fail: {ex}", flush=True)
            st = os.path.join(appdir, "logs", "job1.state.txt")
            if os.path.exists(st):
                print(f"  state: {open(st, encoding='utf-8', errors='replace').read()[:200]}", flush=True)
            print(f"  sandbox kept: {root}", flush=True)
            return False
        shutil.rmtree(root, ignore_errors=True)
        return True
    finally:
        subprocess.run(["taskkill", "/f", "/t", "/pid", str(proc.pid)], capture_output=True)
        time.sleep(1)

ok = 0
for i in range(1, ROUNDS + 1):
    if one_round(i):
        ok += 1
print(f"\n=== {ok}/{ROUNDS} 轮触发成功 ===", flush=True)
sys.exit(0 if ok == ROUNDS else 1)
