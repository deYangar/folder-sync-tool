# -*- coding: utf-8 -*-
"""通用提权运行器：python tests/run_elevated.py <目标脚本绝对路径> <日志绝对路径> [K=V ...]
可选的 K=V 环境变量会写进 bat（提权进程经 ShellExecute 启动，不继承调用方临时环境变量）。"""
import subprocess, sys, os

bat = r"C:\Users\Public\Temp\run_elev_target.bat"
target = sys.argv[1]
log = sys.argv[2]
envs = [a for a in sys.argv[3:] if "=" in a]
os.makedirs(os.path.dirname(log), exist_ok=True)
if os.path.exists(log):
    os.remove(log)

lines = ["@echo off"]
for kv in envs:
    k, _, v = kv.partition("=")
    lines.append(f'set "{k}={v}"')
lines.append(f'python -u "{target}" > "{log}" 2>&1')
with open(bat, "w", encoding="ascii", newline="\r\n") as f:
    f.write("\r\n".join(lines) + "\r\n")

cmd = ("Start-Process -Verb RunAs -FilePath '" + bat + "' -WindowStyle Hidden -Wait")
p = subprocess.run(["powershell", "-NoProfile", "-Command", cmd], capture_output=True, text=True)
print("launcher rc:", p.returncode, p.stderr.strip()[:200])
if os.path.exists(log):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    print(open(log, encoding="utf-8", errors="replace").read())
else:
    print("(no log produced)")
