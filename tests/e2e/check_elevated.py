# -*- coding: utf-8 -*-
# 验证 FolderSync.App 进程是否提权运行（TokenElevation=20）
import ctypes
from ctypes import wintypes
import subprocess

# 拿 PID
out = subprocess.run(["powershell", "-NoProfile", "-Command",
                      "(Get-Process FolderSync.App).Id"], capture_output=True, text=True)
pid = int(out.stdout.strip())
print("PID:", pid)

PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
TOKEN_QUERY = 0x0008
TokenElevation = 20

adv = ctypes.WinDLL("advapi32.dll")
k32 = ctypes.WinDLL("kernel32.dll")
k32.OpenProcess.restype = wintypes.HANDLE
k32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]

h = k32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
print("OpenProcess:", h, "err:", k32.GetLastError())

tok = wintypes.HANDLE()
ok = adv.OpenProcessToken(wintypes.HANDLE(h), TOKEN_QUERY, ctypes.byref(tok))
print("OpenProcessToken:", ok, "err:", ctypes.GetLastError())

elev = wintypes.DWORD(0)
rl = wintypes.DWORD(0)
ok2 = adv.GetTokenInformation(tok, TokenElevation, ctypes.byref(elev), 4, ctypes.byref(rl))
print(f"GetTokenInformation: ok={ok2} TokenElevation={elev.value} => {'提权运行 (USN 可用)' if elev.value else '未提权 (将 fallback FSW)'}")
