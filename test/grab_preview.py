"""grab_preview.py -- photographs the OpenXR Simulator's preview window (it belongs to the stand-in game's process) with
PrintWindow, the same call the Window View app will use to capture windows, and saves it as a PNG.

    python grab_preview.py [--exe xr_test_app.exe] [--out preview.png] [--settle 2]
Waits up to 20 s for a visible top-level window of that exe, lets it draw for --settle seconds, grabs it.
Exit 0 and prints "SAVED <path> <w>x<h>" on success. Needs Pillow.
"""
import argparse
import ctypes
import ctypes.wintypes as wt
import os
import sys
import time

from PIL import Image

u32, g32, k32 = ctypes.windll.user32, ctypes.windll.gdi32, ctypes.windll.kernel32
PW_RENDERFULLCONTENT = 2
TH32CS_SNAPPROCESS = 2


class PROCESSENTRY32W(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD), ("th32ProcessID", wt.DWORD), ("th32DefaultHeapID", ctypes.c_size_t),
                ("th32ModuleID", wt.DWORD), ("cntThreads", wt.DWORD), ("th32ParentProcessID", wt.DWORD), ("pcPriClassBase", wt.LONG),
                ("dwFlags", wt.DWORD), ("szExeFile", wt.WCHAR * 260)]


def pids_of(exe):
    out, snap = set(), k32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    e = PROCESSENTRY32W()
    e.dwSize = ctypes.sizeof(e)
    ok = k32.Process32FirstW(snap, ctypes.byref(e))
    while ok:
        if e.szExeFile.lower() == exe.lower():
            out.add(e.th32ProcessID)
        ok = k32.Process32NextW(snap, ctypes.byref(e))
    k32.CloseHandle(snap)
    return out


def windows_of(pids):
    found = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def cb(hwnd, _):
        pid = wt.DWORD()
        u32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value in pids and u32.IsWindowVisible(hwnd):
            r = wt.RECT()
            u32.GetWindowRect(hwnd, ctypes.byref(r))
            if r.right - r.left > 100 and r.bottom - r.top > 50:
                found.append(hwnd)
        return True

    u32.EnumWindows(cb, 0)
    return found


def grab(hwnd):
    r = wt.RECT()
    u32.GetWindowRect(hwnd, ctypes.byref(r))
    w, h = r.right - r.left, r.bottom - r.top
    hdc = u32.GetDC(0)
    mem = g32.CreateCompatibleDC(hdc)
    bmp = g32.CreateCompatibleBitmap(hdc, w, h)
    g32.SelectObject(mem, bmp)
    ok = u32.PrintWindow(hwnd, mem, PW_RENDERFULLCONTENT)

    class BMI(ctypes.Structure):
        _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG), ("biPlanes", wt.WORD), ("biBitCount", wt.WORD),
                    ("biCompression", wt.DWORD), ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG), ("biYPelsPerMeter", wt.LONG),
                    ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD)]
    bmi = BMI(ctypes.sizeof(BMI), w, -h, 1, 32, 0, 0, 0, 0, 0, 0)
    buf = ctypes.create_string_buffer(w * h * 4)
    g32.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bmi), 0)
    g32.DeleteObject(bmp)
    g32.DeleteDC(mem)
    u32.ReleaseDC(0, hdc)
    return ok, Image.frombuffer("RGBA", (w, h), buf.raw, "raw", "BGRA", 0, 1)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", default="xr_test_app.exe")
    ap.add_argument("--out", default=os.path.join(os.environ.get("TEMP", "."), "preview.png"))
    ap.add_argument("--settle", type=float, default=2.0)
    a = ap.parse_args()
    t0 = time.time()
    hwnds = []
    while time.time() - t0 < 20 and not hwnds:
        hwnds = windows_of(pids_of(a.exe))
        if not hwnds:
            time.sleep(0.25)
    if not hwnds:
        sys.exit("no visible window of %s" % a.exe)
    time.sleep(a.settle)
    ok, img = grab(hwnds[0])
    img.save(a.out)
    print("SAVED %s %dx%d printwindow=%d" % (a.out, img.width, img.height, ok))


if __name__ == "__main__":
    main()
