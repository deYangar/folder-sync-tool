# -*- coding: utf-8 -*-
"""E2E：FS_E2E_SILENT=1 时启动与弹窗全程零焦点抢夺。
断言：主窗口出现前后 GetForegroundWindow 不变（启动不激活）；
UIA 后台读树可用；invoke 打开设置窗口同样不抢焦点；WM_CLOSE 后台关闭。"""
import os, sys, time, ctypes, subprocess
from ctypes import wintypes
sys.stdout.reconfigure(encoding="utf-8", errors="replace")

from pywinauto.uia_element_info import UIAElementInfo
from pywinauto.controls.uiawrapper import UIAWrapper

BASE = os.path.dirname(os.path.abspath(__file__))
EXE = os.path.join(BASE, "..", "..", "src", "FolderSync.App", "bin", "Debug", "net8.0-windows", "FolderSync.App.exe")
fail = []
user32 = ctypes.WinDLL("user32.dll")

def check(cond, name, extra=""):
    print(("PASS" if cond else "FAIL") + "  " + name + (f" ({extra})" if extra else ""), flush=True)
    if not cond: fail.append(name)

def find_hwnds(pred):
    result = []
    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def cb(hwnd, lp):
        buf = ctypes.create_unicode_buffer(256)
        user32.GetWindowTextW(hwnd, buf, 256)
        if pred(buf.value): result.append(hwnd)
        return True
    user32.EnumWindows(cb, 0)
    return result

def fg_desc(hwnd):
    buf = ctypes.create_unicode_buffer(256)
    user32.GetWindowTextW(hwnd, buf, 256)
    return f"{hwnd}:{buf.value}"

def main():
    subprocess.run(["taskkill", "/f", "/im", "FolderSync.App.exe"], capture_output=True)
    time.sleep(1)

    before = user32.GetForegroundWindow()
    print(f"[dbg] 启动前前台 = {fg_desc(before)}", flush=True)

    subprocess.Popen([EXE, "--e2e"], env=dict(os.environ, FS_E2E_SILENT="1"))  # 双因子：环境变量 + 启动参数缺一不可
    t0 = time.time(); main_hwnd = None
    while time.time() - t0 < 20:
        hs = find_hwnds(lambda t: "FolderSync" in t and "本地文件夹同步" in t)
        if hs:
            main_hwnd = hs[0]; break
        time.sleep(0.3)
    check(main_hwnd is not None, "主窗口出现", f"{time.time()-t0:.1f}s")
    if main_hwnd is None:
        print("E2E-DONE fail-early"); return
    check(user32.IsWindowVisible(main_hwnd), "主窗口可见（ShowActivated=false 不影响显示）")
    time.sleep(1.5)   # 给可能迟到的激活留时间窗

    after = user32.GetForegroundWindow()
    print(f"[dbg] 启动后前台 = {fg_desc(after)}", flush=True)
    check(after != main_hwnd, "主窗口未抢前台")
    check(after == before, "前台窗口保持不变（启动零焦点抢夺）")

    # UIA 后台操作：读树 + 打开设置窗口
    main_w = UIAWrapper(UIAElementInfo(main_hwnd))
    btn = next((b for b in main_w.descendants(control_type="Button")
                if b.window_text() == "设置"), None)
    check(btn is not None, "UIA 后台读到「设置」按钮")
    if btn is None:
        subprocess.run(["taskkill", "/f", "/im", "FolderSync.App.exe"], capture_output=True)
        print("E2E-DONE fail-early"); return
    btn.invoke()
    t0 = time.time(); set_hwnd = None
    while time.time() - t0 < 10:
        hs = [h for h in find_hwnds(lambda t: t == "设置")
              if user32.GetWindow(h, 4) == main_hwnd]   # GW_OWNER
        if hs:
            set_hwnd = hs[0]; break
        time.sleep(0.2)
    check(set_hwnd is not None, "设置窗口打开（invoke 后台生效）")
    if set_hwnd:
        time.sleep(1)
        now = user32.GetForegroundWindow()
        print(f"[dbg] 弹设置后前台 = {fg_desc(now)}", flush=True)
        check(now != set_hwnd, "设置窗口未抢前台")
        check(now == before, "前台窗口保持不变（对话框零焦点抢夺）")
        user32.SendMessageW(set_hwnd, 0x0010, 0, 0)   # WM_CLOSE 后台关闭
        time.sleep(1)
        check(not user32.IsWindowVisible(set_hwnd), "WM_CLOSE 后台关闭设置窗口")

    subprocess.run(["taskkill", "/f", "/im", "FolderSync.App.exe"], capture_output=True)
    print("E2E-DONE " + ("all-pass" if not fail else f"fail:{len(fail)}"), flush=True)

if __name__ == "__main__":
    main()
