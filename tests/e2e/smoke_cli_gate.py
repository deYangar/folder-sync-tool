# -*- coding: utf-8 -*-
"""必修1 回归冒烟：CLI 门控只接管已知命令——--minimized/--e2e 必须正常启动 GUI（曾 ExitUsage(2) 闪退），
--list 等 CLI 命令照常工作。跑法：python smoke_cli_gate.py（exe 为 requireAdmin，经 ShellExecute 提权启动）。"""
import subprocess, sys, os, time, glob

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
base = r"C:\Users\Yang\.zcode\workspace\default\projects\folder-sync-tool"
exe = os.path.join(base, "src", "FolderSync.App", "bin", "Release", "net8.0-windows", "FolderSync.App.exe")
if not os.path.isfile(exe):
    print("FAIL  exe 不存在（先编译 App Release）:", exe); sys.exit(1)

fail = 0
def check(cond, name, extra=""):
    global fail
    print(("PASS  " if cond else "FAIL  ") + name + (f" ({extra})" if extra else ""))
    if not cond: fail += 1

def run_elevated(args, wait_s):
    """ShellExecute 启动（requireAdmin 经 UAC 静默提升），返回输出串（ALIVE:/EXITED:）"""
    p = subprocess.run(
        ["powershell", "-NoProfile", "-Command",
         f"$p = Start-Process -FilePath '{exe}' -ArgumentList '{args}' -PassThru; Start-Sleep -Seconds {wait_s};"
         f"if ($p.HasExited) {{ Write-Output EXITED:$($p.ExitCode) }} else {{ Write-Output ALIVE:$($p.Id) }};"
         f"if (-not $p.HasExited) {{ Stop-Process -Id $p.Id -Force }}"],
        capture_output=True, text=True, timeout=180)
    return (p.stdout or "").strip()

# 前置：确认没有正在跑的 GUI 实例（单例互斥会干扰判定）
running = subprocess.run(["powershell", "-NoProfile", "-Command",
                          "Get-Process FolderSync.App -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Id"],
                         capture_output=True, text=True).stdout.strip()
if running:
    print(f"FAIL  已有 FolderSync.App 实例在跑 (pid={running})，先退出再冒烟"); sys.exit(1)

# 记录冒烟前 cli 日志数（修复前 --minimized 会写「未知参数」日志并退出码 2）
cli_dir = os.path.join(os.path.dirname(exe), "logs", "cli")
before = len(glob.glob(os.path.join(cli_dir, "*.log"))) if os.path.isdir(cli_dir) else 0

out = run_elevated("--minimized", 8)
check(out.startswith("ALIVE"), "--minimized: GUI 进程 8s 存活（曾闪退 ExitUsage=2）", out)

out = run_elevated("--e2e", 8)
check(out.startswith("ALIVE"), "--e2e: GUI 进程 8s 存活", out)

# --list 走 CLI 路径：跑完即退，退出码 0
p = subprocess.run(
    ["powershell", "-NoProfile", "-Command",
     f"$p = Start-Process -FilePath '{exe}' -ArgumentList '--list' -PassThru -Wait; Write-Output RC:$($p.ExitCode)"],
    capture_output=True, text=True, timeout=180)
rc_out = (p.stdout or "").strip()
check(rc_out == "RC:0", "--list: CLI 模式照常工作（退出码 0）", rc_out)

after = len(glob.glob(os.path.join(cli_dir, "*.log"))) if os.path.isdir(cli_dir) else 0
new_logs = sorted(glob.glob(os.path.join(cli_dir, "*.log")))[before:]
unknown = [f for f in new_logs if "未知参数" in open(f, encoding="utf-8", errors="replace").read()]
check(len(unknown) == 0, "无「未知参数」CLI 日志产生（--minimized/--e2e 不再落入 CLI 未知分支）",
      f"{len(unknown)} 条")

print("smoke_cli_gate: " + ("ALL PASS" if fail == 0 else f"{fail} FAILED"))
sys.exit(fail)
