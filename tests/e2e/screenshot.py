# -*- coding: utf-8 -*-
# PrintWindow 截图 FolderSync 对话框与主窗口
import ctypes
import os
from ctypes import wintypes

user32 = ctypes.WinDLL("user32.dll")
gdi32 = ctypes.WinDLL("gdi32.dll")

class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]

PW_RENDERFULLCONTENT = 0x2

def shot(hwnd, path):
    rect = RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(rect))
    w, h = rect.right - rect.left, rect.bottom - rect.top
    if w <= 0 or h <= 0: return False
    hdc = user32.GetWindowDC(hwnd)
    mem = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    gdi32.SelectObject(mem, bmp)
    user32.PrintWindow(hwnd, mem, PW_RENDERFULLCONTENT)
    # BITMAPINFOHEADER
    class BMIH(ctypes.Structure):
        _fields_ = [("biSize", ctypes.c_uint32), ("biWidth", ctypes.c_int32),
                    ("biHeight", ctypes.c_int32), ("biPlanes", ctypes.c_uint16),
                    ("biBitCount", ctypes.c_uint16), ("biCompression", ctypes.c_uint32),
                    ("biSizeImage", ctypes.c_uint32), ("biXPelsPerMeter", ctypes.c_int32),
                    ("biYPelsPerMeter", ctypes.c_int32), ("biClrUsed", ctypes.c_uint32),
                    ("biClrImportant", ctypes.c_uint32)]
    bi = BMIH()
    bi.biSize = 40; bi.biWidth = w; bi.biHeight = -h
    bi.biPlanes = 1; bi.biBitCount = 32; bi.biCompression = 0
    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bi), 0)
    # 手工拼 BMP：14 字节文件头 + 40 字节信息头（避免结构体对齐 padding）
    import struct
    pixel_off = 54
    fh = struct.pack("<HIHHI", 0x4D42, pixel_off + w*h*4, 0, 0, pixel_off)
    ih = struct.pack("<IiiHHIIiiII", 40, w, -h, 1, 32, 0, w*h*4, 0, 0, 0, 0)
    with open(path, "wb") as f:
        f.write(fh); f.write(ih); f.write(buf.raw)
    gdi32.DeleteObject(bmp); gdi32.DeleteDC(mem); user32.ReleaseDC(hwnd, hdc)
    return True

targets = {}
@ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
def cb(hwnd, lp):
    pid = wintypes.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    import psutil
    try:
        if psutil.Process(pid.value).name() != "FolderSync.App.exe": return True
    except Exception: return True
    if not user32.IsWindowVisible(hwnd): return True
    buf = ctypes.create_unicode_buffer(256)
    user32.GetWindowTextW(hwnd, buf, 256)
    t = buf.value
    if t: targets[t] = hwnd
    return True

user32.EnumWindows(cb, 0)
out = r"C:\Users\Public\Temp"
for title, hwnd in targets.items():
    fname = "fsdialog.bmp" if "任务设置" in title else "fsmain.bmp"
    p = os.path.join(out, fname)
    if shot(hwnd, p):
        print(f"saved {p}  ({title})")
