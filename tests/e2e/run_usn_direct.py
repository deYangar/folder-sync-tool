# -*- coding: utf-8 -*-
"""USN 提权套件·直跑已编译 exe（流式输出 + 硬超时，不经 dotnet run 重建）。经 run_elevated.py 调用。"""
import subprocess, sys, os, threading
sys.stdout.reconfigure(encoding="utf-8", errors="replace")
base = r"C:\Users\Yang\.zcode\workspace\default\projects\folder-sync-tool"
exe = os.path.join(base, "tests", "FolderSync.SmokeTest", "bin",
                   "Release", "net8.0-windows", "FolderSync.SmokeTest.exe")
if not os.path.isfile(exe):
    print("FAIL  exe 不存在（先编译 SmokeTest Release）:", exe)
    sys.exit(1)

TIMEOUT = 420  # 7 分钟硬超时；正常全程 <3 分钟
p = subprocess.Popen([exe, "usn"], stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                     text=True, encoding="utf-8", errors="replace", bufsize=1)
killed = {"hit": False}
def watchdog():
    import time
    time.sleep(TIMEOUT)
    if p.poll() is None:
        killed["hit"] = True
        p.kill()
threading.Thread(target=watchdog, daemon=True).start()
for line in p.stdout:
    print(line, end="", flush=True)
rc = p.wait()
print(f"\n[usn-direct] rc={rc}" + ("（看门狗超时强杀：进程僵死）" if killed["hit"] else ""))
sys.exit(rc)
