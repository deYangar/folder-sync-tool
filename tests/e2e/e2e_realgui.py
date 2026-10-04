# -*- coding: utf-8 -*-
"""真机 GUI 端到端测试（2026-09-12，咩咩验收要求）：
测的是 publish 单文件 exe 在真实窗口里的表现——UIA 读真实控件文本/进度条数值，磁盘拿真数据证据。
上次教训：Core 测试 317 全绿但 UI 假卡死漏网（显示层零覆盖）；本脚本把显示层纳入发版门槛。

场景 A：启动自动同步（无差异任务）→ UI 必须到达终态（上次翻车点精确复刻）
场景 B：大目录分析 → 进度条出现 (0,100) 开区间真值且递增（咩咩的进度条诉求）
场景 C：点同步 → 目标侧文件 sha256 与源一致（正向真数据验证）
场景 D：实时任务改文件 → 自动同步落盘（H-1 补跑 + M-5 自写抑制实机链路）

跑法（提权，软件 manifest=requireAdministrator）：
  python tests/e2e/run_elevated.py <本脚本绝对路径> <日志绝对路径> FS_E2E_SILENT=1
被测 exe：publish 单文件拷到独立临时目录（不动 publish 里的试用 db/logs）。
"""
import os, sys, time, hashlib, shutil, sqlite3, subprocess, tempfile, traceback

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

PROJECT = r"C:\Users\Yang\.zcode\workspace\default\projects\folder-sync-tool"
SRC_EXE = os.path.join(PROJECT, "publish", "FolderSync.App.exe")

FAILS = []
def check(cond, name, extra=""):
    line = ("PASS  " if cond else "FAIL  ") + name + (f" ({extra})" if extra else "")
    print(line, flush=True)
    if not cond:
        FAILS.append(name)

# ---------- pywinauto UIA（定位方式沿用 e2e_gui 验证过的 EnumWindows + UIAWrapper） ----------
import ctypes
from ctypes import wintypes
from pywinauto.uia_element_info import UIAElementInfo
from pywinauto.controls.uiawrapper import UIAWrapper

user32 = ctypes.WinDLL("user32.dll")

def find_hwnds(pred):
    result = []
    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def cb(hwnd, lp):
        buf = ctypes.create_unicode_buffer(256)
        user32.GetWindowTextW(hwnd, buf, 256)
        if pred(buf.value):
            result.append(hwnd)
        return True
    user32.EnumWindows(cb, 0)
    return result

def wrapper(hwnd):
    return UIAWrapper(UIAElementInfo(hwnd))

def read_log(win):
    """TxtLog（TextBox）全文：ValuePattern 优先，window_text 兜底。"""
    el = find_ctrl(win, "TxtLog")
    if el is None:
        return "<TxtLog not found>"
    for m in ("get_value", "window_text"):
        try:
            v = getattr(el, m)()
            if v:
                return v
        except Exception:
            pass
    try:
        return el.element_info.name or ""
    except Exception:
        return "<unreadable>"

_CTRL_CACHE = {}

def find_ctrl(win, auto_id, control_type=None, no_cache=False):
    """按 automation_id 找控件。带缓存：WPF 控件实例稳定，分析出大树（几千行 DataGrid）后
    全树 descendants 一次要秒级——轮询场景必须走缓存（B2 进度采样曾因此只采到终值）。"""
    if not no_cache and auto_id in _CTRL_CACHE:
        return _CTRL_CACHE[auto_id]
    crit = {"control_type": control_type} if control_type else {}
    try:
        els = win.descendants(**crit)
    except Exception:
        els = win.descendants()
    for el in els:
        try:
            if el.element_info.automation_id == auto_id:
                _CTRL_CACHE[auto_id] = el
                return el
        except Exception:
            pass
    return None

def read_text(win, auto_id):
    el = find_ctrl(win, auto_id)
    if el is None:
        return None
    try:
        return el.window_text()
    except Exception:
        try:
            return el.element_info.name
        except Exception:
            return None

def read_progress(win, auto_id="PbProgress"):
    """进度条当前值：返回 (value, found)。ProgressBar 没有 ValuePattern——COM RangeValue 直读优先。"""
    el = find_ctrl(win, auto_id)
    if el is None:
        return None, False
    try:
        return float(el.element_info._element.CurrentRangeValue), True   # UIA RangeValue
    except Exception:
        pass
    try:
        return float(el.get_value()), True
    except Exception:
        pass
    try:
        return float(el.legacy_properties()['Value']), True
    except Exception:
        pass
    return None, False

def wait_terminal(win, timeout=60, keywords=("两边一致", "完成", "已同步", "已加载", "无需同步")):
    """轮询 TxtProgress/TxtStatus 直到终态关键词；返回 (是否终态, 样本列表)。"""
    seen = []
    sw = time.time()
    while time.time() - sw < timeout:
        t = read_text(win, "TxtProgress") or ""
        s = read_text(win, "TxtStatus") or ""
        seen.append((round(time.time() - sw, 1), t[:80], s[:40]))
        if any(k in t for k in keywords) or any(k in s for k in keywords):
            return True, seen
        time.sleep(0.2)
    return False, seen

def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()

# ---------- 准备被测环境 ----------
def setup_env():
    root = os.path.join(tempfile.gettempdir(), "fs_realgui_" + os.urandom(4).hex())
    # exe 与任务目录必须分离：H-4 程序目录守卫会拒绝"任务侧与程序目录重叠"的任务
    # （首跑 8 项失败全是这个：守卫正常工作，沙箱布局错了 + UI 文案误导）
    appdir = os.path.join(root, "app")
    datadir = os.path.join(root, "data")
    os.makedirs(appdir); os.makedirs(datadir)
    exe = os.path.join(appdir, "FolderSync.App.exe")
    shutil.copy2(SRC_EXE, exe)
    # 任务目录：job1 手动单向 l2r（无差异预置）；job2 实时单向 l2r
    l1 = os.path.join(datadir, "L1"); r1 = os.path.join(datadir, "R1")
    l2 = os.path.join(datadir, "L2"); r2 = os.path.join(datadir, "R2")
    for d in (l1, r1, l2, r2):
        os.makedirs(os.path.join(d, "sub"))
    for p in (os.path.join(l1, "a.txt"), os.path.join(l1, "sub", "b.txt"),
              os.path.join(r1, "a.txt"), os.path.join(r1, "sub", "b.txt")):
        with open(p, "w") as f:
            f.write("same content")
    with open(os.path.join(l2, "live.txt"), "w") as f:
        f.write("live v1")
    # 预置 db（DDL 按 Db.cs；缺省列由应用启动 Migrate 补齐——只填核心列）。
    # db 必须放 exe 目录（Db 默认 AppContext.BaseDirectory）
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
CREATE TABLE sync_snapshot(job_id INTEGER NOT NULL, rel_path TEXT NOT NULL, size INTEGER NOT NULL,
  mtime_utc TEXT NOT NULL, is_dir INTEGER NOT NULL, PRIMARY KEY(job_id, rel_path));
""")
    conn.execute("INSERT INTO jobs(name,left_path,right_path,direction,trigger_type,auto_start,enabled,debounce_seconds,created_at)"
                 " VALUES('手动任务',?,?,0,0,1,1,10,?)", (l1, r1, "2026-09-12 00:00:00"))
    conn.execute("INSERT INTO jobs(name,left_path,right_path,direction,trigger_type,auto_start,enabled,debounce_seconds,created_at)"
                 " VALUES('实时任务',?,?,0,1,1,1,2,?)", (l2, r2, "2026-09-12 00:00:00"))
    conn.commit(); conn.close()
    return root, exe, (l1, r1, l2, r2)

def start_app(exe):
    env = dict(os.environ)
    env["FS_E2E_SILENT"] = "1"   # 双因子之一：环境变量（命令行 --e2e 由 Popen 参数带）
    p = subprocess.Popen([exe, "--e2e"], env=env)
    # 等 UIA 树就绪（EnumWindows 找主窗口：pywinauto 顶层枚举会漏/连错 owned 窗口，e2e_gui 前车之鉴）
    for _ in range(50):
        time.sleep(0.4)
        hwnds = find_hwnds(lambda t: "FolderSync" in t and "本地文件夹同步" in t)
        if hwnds:
            win = wrapper(hwnds[0])
            print(f"[env] main window: {hwnds[0]:#x}", flush=True)
            return p, win
    raise RuntimeError("主窗口 20s 未出现")

def main():
    print(f"[env] exe={SRC_EXE}")
    print(f"[env] mtime={time.strftime('%Y-%m-%d %H:%M:%S', time.localtime(os.path.getmtime(SRC_EXE)))}")
    root, exe, (l1, r1, l2, r2) = setup_env()
    print(f"[env] sandbox={root}")
    proc = None
    try:
        proc, win = start_app(exe)
        print("[env] 主窗口已出现", flush=True)

        # ===== 场景 A：启动自动同步（无差异）→ UI 终态（上次翻车点） =====
        print("--- A: 启动自动同步，UI 必须到达终态 ---", flush=True)
        ok, seen = wait_terminal(win, timeout=45)
        txt = seen[-1][1] if seen else "<no sample>"
        check(ok, "A1 无差异轮次 UI 到达终态（不再卡在②扫描）", txt)
        if not ok:
            print("[diag] A1 失败——TxtLog 现场如下：", flush=True)
            print(read_log(win), flush=True)
            print("[diag] 最近进度样本：", seen[-6:], flush=True)
        else:
            # 终态后 2s 再采样：不得退回"扫描中"（晚到事件覆盖检测）
            time.sleep(2)
            after = read_text(win, "TxtProgress") or ""
            check("正在扫描" not in after and "正在对比" not in after,
                  "A2 终态保持 2s 不回退（无晚到覆盖）", after[:60])

        # ===== 场景 B：大目录分析 → 进度条真值 =====
        print("--- B: 大目录分析，进度条 (0,100) 开区间真值 ---", flush=True)
        # 造 4000 目录 × 6 文件 = 24000 文件。三版教训：120/600/1200 目录在 NVMe 热缓存下
        # 分析 <0.3s，UIA 采样（0.06s/次）追不上——扫描窗口必须 ≥2s 才能稳定采到进度真值
        for i in range(4000):
            d = os.path.join(l1, f"dir{i:03d}")
            os.makedirs(d, exist_ok=True)
            for j in range(6):
                with open(os.path.join(d, f"f{j:02d}.txt"), "w") as f:
                    f.write(f"content {i}-{j} " * 20)
        btn = find_ctrl(win, "BtnAnalyze", "Button")
        check(btn is not None and btn.is_enabled(), "B1 「分析」按钮可用")
        if btn:
            # 预热控件缓存：首次 find_ctrl 全树扫描 ~0.5s，会把 <0.5s 的进度窗口整个吃掉
            read_progress(win)
            read_text(win, "TxtProgress")
            baseline = read_text(win, "TxtProgress") or ""
            btn.invoke()
            samples = []
            t0 = time.time()
            n_read = 0
            while time.time() - t0 < 60:
                ts = time.time()
                v, found = read_progress(win)
                t = read_text(win, "TxtProgress") or ""
                n_read += 1
                samples.append((round(time.time() - t0, 1), v, t[:60]))
                if n_read <= 5:
                    print(f"[diag:B] sample#{n_read} readcost={round(time.time()-ts, 2)}s v={v} t={t[:40]}", flush=True)
                # 终态判定必须对比基线：上一场景残留的"已加载/两边已一致"会让循环首轮就 break（采不到中间值）
                if t != baseline and any(k in t for k in ("分析完成", "已加载", "两边已一致", "就绪")):
                    print(f"[diag:B] break at sample#{n_read}", flush=True)
                    break
                time.sleep(0.1)
            mids = [v for _, v, _ in samples if v is not None and 0.01 < v < 99.99]
            vals = [v for _, v, _ in samples if v is not None]
            print(f"[diag] B 采样 {len(samples)} 个，进度值样本: {sorted(set(round(v,1) for v in vals))[:15]}", flush=True)
            check(len(mids) >= 1, "B2 进度条出现 (0,100) 开区间真值（按进度走，非纯动画）",
                  f"mid_values={sorted(set(round(m,1) for m in mids))[:10]}")
            # B3：分析终态进度条复位为 0（扫描 100% 瞬态由 Core SafetyTests 的 done 事件覆盖——
            # UI 采样 0.17s/个追不上 <0.5s 的扫描窗口尾部，断言满值是设计错误）
            v_last, _ = read_progress(win)
            check(v_last == 0.0 or v_last is None, "B3 分析终态进度条复位干净", f"final={v_last}")
            final = samples[-1][2]
            check(any(k in final for k in ("分析完成", "已加载", "两边已一致")), "B4 分析终态文本", final)

        # ===== 场景 C：点同步 → 磁盘 sha 真数据验证 =====
        print("--- C: 同步落盘，目标侧 sha256 与源一致 ---", flush=True)
        btn_sync = find_ctrl(win, "BtnSync", "Button")
        check(btn_sync is not None and btn_sync.is_enabled(), "C1 「同步」按钮可用")
        if btn_sync:
            btn_sync.invoke()
            ok, seen = wait_terminal(win, timeout=90, keywords=("已同步", "同步完成", "完成", "两边已一致", "已加载"))
            check(ok, "C2 同步 UI 到达终态", seen[-1][1] if seen else "<none>")
            src = os.path.join(l1, "dir000", "f00.txt")
            dst = os.path.join(r1, "dir000", "f00.txt")
            check(os.path.exists(dst), "C3 目标侧文件已落盘", dst)
            if os.path.exists(dst):
                check(sha256(src) == sha256(dst), "C4 目标侧 sha256 与源一致（真数据验证）")

        # ===== 场景 D：实时任务自动同步（H-1/M-5 链路） =====
        print("--- D: 实时任务改文件自动同步 ---", flush=True)
        # 选到实时任务（LstJobs 第二项，按 Job.Id 排序 job2=实时）
        lst = find_ctrl(win, "LstJobs")
        sel_ok = False
        card_text = ""
        if lst is not None:
            items = [el for el in lst.descendants() if el.element_info.control_type == "ListItem"]
            if len(items) >= 2:
                items[1].select()
                sel_ok = True
                time.sleep(1)
                items = [el for el in lst.descendants() if el.element_info.control_type == "ListItem"]
                try:
                    card_text = items[1].window_text()
                except Exception:
                    card_text = ""
        check(sel_ok, "D1 选中实时任务")
        check("实时监听" in card_text, "D2 实时监听在跑（watcher 存活）", card_text[:60])
        # 等 AutoPreview 完（切任务的"已加载"）再改文件——避免把变更混进 preview 轮的 IsRunning 窗口
        ok, seen = wait_terminal(win, timeout=30, keywords=("已加载", "两边已一致", "就绪"))
        time.sleep(3)   # watcher 稳定期 + 补偿窗尾巴
        with open(os.path.join(l2, "live.txt"), "w") as f:
            f.write("live v2 changed")
        print(f"[diag] live.txt 已改为 v2，等实时轮（debounce 2s + 轮次）…", flush=True)
        # 终态信号 = 磁盘内容（最硬证据）。"已同步"文案只在任务卡/引擎状态，TxtProgress 不显示
        dst = os.path.join(r2, "live.txt")
        landed = False
        t0 = time.time()
        while time.time() - t0 < 45:
            if os.path.exists(dst) and open(dst, encoding="utf-8").read() == "live v2 changed":
                landed = True
                break
            time.sleep(1)
        check(landed, "D3 实时变更自动落盘（watcher→debounce→轮次→复制全链路）",
              f"耗时 {round(time.time()-t0,1)}s")
        if not landed and proc is not None:
            # 现场抓托管栈：看 watcher 线程卡在哪（批处理消化/DeviceIoControl/其他）
            try:
                ds = os.path.join(os.environ.get("USERPROFILE", ""), ".dotnet", "tools", "dotnet-stack.exe")
                out = subprocess.run([ds, "report", "-p", str(proc.pid)], capture_output=True, text=True,
                                     encoding="utf-8", errors="replace", timeout=90)
                stacks = (out.stdout or "") + (out.stderr or "")
                open(os.path.join(root, "stacks.txt"), "w", encoding="utf-8").write(stacks)
                print(f"[diag] 托管栈已存 {os.path.join(root, 'stacks.txt')}（{len(stacks)} 字节）", flush=True)
                for seg in stacks.split("Thread ("):
                    if "UsnWatcher" in seg or "FolderSync.Core" in seg:
                        print("[diag][stack] Thread (" + seg[:600], flush=True)
                        break
            except Exception as ex:
                print(f"[diag] dotnet-stack 失败: {ex}", flush=True)
        if landed:
            log = read_log(win)
            check("实时任务: realtime" in log and "ok" in log.split("实时任务: realtime")[-1][:80],
                  "D4 实时轮 runs 正常（realtime · ok）", log.splitlines()[-1][:60] if log else "")
        content = open(dst, encoding="utf-8").read() if os.path.exists(dst) else ""
        check(content == "live v2 changed", "D5 落盘内容与源一致", repr(content))

        print(f"\n{'=== 真机 GUI 全部通过 ===' if not FAILS else f'=== {len(FAILS)} 项失败 ==='}", flush=True)
        return 0 if not FAILS else 1
    finally:
        try:
            if proc is not None:
                # 离场前把应用日志全量带走（成功也留诊断材料）
                print("[diag] TxtLog tail:", flush=True)
                tail = read_log(win).splitlines()[-25:] if win else ["<no win>"]
                print(os.linesep.join(tail), flush=True)
                subprocess.run(["taskkill", "/f", "/t", "/pid", str(proc.pid)], capture_output=True)
        except Exception:
            pass
        time.sleep(1)
        if not FAILS:
            try:
                shutil.rmtree(root, ignore_errors=True)
            except Exception:
                pass
        else:
            print(f"[diag] 有失败项，sandbox 保留供排查: {root}", flush=True)

if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception:
        traceback.print_exc()
        sys.exit(2)
