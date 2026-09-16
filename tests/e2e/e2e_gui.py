# -*- coding: utf-8 -*-
"""E2E：新建任务全流程（须管理员运行）。
所有按钮用 InvokePattern、列表项用 SelectionItemPattern（避免 click_input 的 wait-idle 10s 假延迟）。
窗口定位走 Win32 EnumWindows（pywinauto 的 UIA 顶层枚举会漏 owned dialog）。"""
import os, sys, time, ctypes
from ctypes import wintypes
sys.stdout.reconfigure(encoding="utf-8", errors="replace")

from pywinauto.uia_element_info import UIAElementInfo
from pywinauto.controls.uiawrapper import UIAWrapper

JOB_NAME = "E2E自动化测试任务"
fail = []
user32 = ctypes.WinDLL("user32.dll")

def check(cond, name, extra=""):
    print(("PASS" if cond else "FAIL") + "  " + name + (f" ({extra})" if extra else ""), flush=True)
    if not cond:
        fail.append(name)

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

def ctrl(parent, title=None, ctype=None, index=0):
    kw = {}
    if ctype: kw["control_type"] = ctype
    if title is not None: kw["title"] = title
    els = parent.descendants(**kw)
    if not els:
        raise LookupError(f"control not found: {title} {ctype}")
    return els[index]

def invoke(el):
    el.invoke()   # InvokePattern，不阻塞

def main():
    hwnds = find_hwnds(lambda t: "FolderSync" in t and "本地文件夹同步" in t)
    if not hwnds:
        print("FAIL main window not found")
        return
    main = wrapper(hwnds[0])
    print("PASS  main-window", flush=True)

    # 1) 打开新建任务对话框
    t0 = time.time()
    invoke(ctrl(main, title="新建任务", ctype="Button"))
    dlg = None
    while time.time() - t0 < 10:
        time.sleep(0.2)
        hs = find_hwnds(lambda t: t == "任务设置")
        if hs:
            dlg = wrapper(hs[0])
            break
    check(dlg is not None, "dialog-open", f"{time.time()-t0:.1f}s")
    if not dlg:
        print("E2E-DONE fail-early")
        return

    # 2) 文件夹选择器弹出速度
    t1 = time.time()
    invoke(ctrl(dlg, title="浏览…", ctype="Button"))
    picker = None
    while time.time() - t1 < 5:
        time.sleep(0.1)
        hs = find_hwnds(lambda t: t == "选择文件夹")
        if hs:
            picker = wrapper(hs[0])
            break
    pt = time.time() - t1
    if picker is None:
        print("windows at fail:", [w for w in find_hwnds(lambda t: "浏览" in t or "选择" in t or "任务" in t or "Folder" in t)], flush=True)
    check(picker is not None and pt < 3, "picker-dialog<3s", f"{pt:.1f}s")

    # 2b) 选择器输路径 → 确定 → 回填
    if picker is not None:
        target_dir = r"C:\Users\Yang\AppData\Local\Temp"
        pe = picker.descendants(control_type="Edit")[0]
        pe.set_edit_text(target_dir)
        invoke(ctrl(picker, title="确定", ctype="Button"))
        time.sleep(1)
        left_edit = ctrl(dlg, ctype="Edit", index=1)
        got = left_edit.get_value()
        check(got == target_dir, "picker-returns-path", repr(got))

    # 3) 填表
    edits = dlg.descendants(control_type="Edit")
    print(f"edits found: {len(edits)}", flush=True)
    values = [JOB_NAME,
              r"C:\Users\Yang\AppData\Local\Temp",
              r"C:\Users\Yang\AppData\Local\Temp\fstest_e2e_target",
              ""]
    for e, v in zip(edits, values):
        e.iface_value.SetValue(v)

    # 4) 确定保存
    invoke(ctrl(dlg, title="确定", ctype="Button"))
    time.sleep(2)

    # 5) 验证列表（只看 LstJobs 列表，排除日志 ListBox）
    job_lists = [l for l in main.descendants(control_type="List")
                 if l.element_info.automation_id == "LstJobs"]
    items = job_lists[0].descendants(control_type="ListItem") if job_lists else []
    texts = [it.window_text() for it in items]
    print("job list:", texts, flush=True)
    check(any(JOB_NAME in t for t in texts), "job-in-list", f"items={len(texts)}")

    # 6) 清理本测试任务（含上轮遗留）：循环删除所有 E2E 任务
    for _ in range(6):
        items = job_lists[0].descendants(control_type="ListItem") if job_lists else []
        target_item = next((it for it in items if JOB_NAME in it.window_text()), None)
        if target_item is None:
            break
        target_item.select()
        time.sleep(0.5)
        invoke(ctrl(main, title="删除", ctype="Button"))
        time.sleep(1)
        confirm = find_hwnds(lambda t: t == "确认")
        if confirm:
            cw = wrapper(confirm[0])
            clicked = False
            for b in cw.descendants(control_type="Button"):
                if "是" in b.window_text():
                    b.invoke()
                    clicked = True
                    break
            if not clicked:
                user32.SendMessageW(confirm[0], 0x0010, 0, 0)   # WM_CLOSE 兜底，零焦点
        time.sleep(1)
    texts2 = [it.window_text() for it in job_lists[0].descendants(control_type="ListItem")] if job_lists else []
    check(not any(JOB_NAME in t for t in texts2), "deleted", f"剩余={texts2}")

    print("E2E-DONE", "全部通过" if not fail else f"{len(fail)} 项失败", flush=True)

main()
