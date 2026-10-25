"""input_test.py -- the whole chain with a real window: Notepad is put in VR through the layout file, the app is started
with --test-input (so software-made keys and mouse moves count as real), the stand-in game runs on the simulator, and
then: Page Up turns the mouse and keyboard over to the windows, a click lands in Notepad, "hello" is typed, the window
is dragged by its header, Page Down hands everything back. PASS when Notepad really holds "hello" and the window really
moved on the desk. A photo of the preview is taken while the cursor is in VR.

    python input_test.py [--shot <png>] [--keep]
Needs the build (build.bat) and Pillow (for the photo). The simulator runtime is used; the registry is not touched.
"""
import argparse
import ctypes
import ctypes.wintypes as wt
import json
import mmap
import os
import struct
import subprocess
import sys
import tempfile
import time

from grab_preview import pids_of, windows_of, grab

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, ".."))
SIM_RUNTIME = r"D:\claude video game stuff\tools\OpenXR-Simulator\openxr_simulator.json"
LAYER_NAME = "XR_APILAYER_FLATTOVR_window_view"
NAME, HEADER, SLOTS_OFF = "Local\\FlatToVR_WindowView_v2", 628, 180
u32, k32 = ctypes.windll.user32, ctypes.windll.kernel32
VK_PRIOR, VK_NEXT = 0x21, 0x22


# ---- SendInput --------------------------------------------------------------------------------------------------
class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", wt.LONG), ("dy", wt.LONG), ("mouseData", wt.DWORD), ("dwFlags", wt.DWORD), ("time", wt.DWORD), ("extra", ctypes.c_size_t)]


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wt.WORD), ("wScan", wt.WORD), ("dwFlags", wt.DWORD), ("time", wt.DWORD), ("extra", ctypes.c_size_t)]


class _U(ctypes.Union):
    _fields_ = [("mi", MOUSEINPUT), ("ki", KEYBDINPUT)]


class INPUT(ctypes.Structure):
    _fields_ = [("type", wt.DWORD), ("u", _U)]


def key(vk, down):
    i = INPUT(type=1)
    i.u.ki = KEYBDINPUT(vk, u32.MapVirtualKeyW(vk, 0), 0 if down else 2, 0, 0)
    u32.SendInput(1, ctypes.byref(i), ctypes.sizeof(INPUT))


def tap(vk):
    key(vk, True)
    time.sleep(0.03)
    key(vk, False)
    time.sleep(0.03)


def mouse(dx=0, dy=0, flags=0x0001):
    i = INPUT(type=0)
    i.u.mi = MOUSEINPUT(dx, dy, 0, flags, 0, 0)
    u32.SendInput(1, ctypes.byref(i), ctypes.sizeof(INPUT))


def move(dx, dy):
    """small steps (4 px) so Windows' pointer acceleration does not stretch them; the app sees 1 mouse px = 1 picture px"""
    sx, sy = (1 if dx >= 0 else -1), (1 if dy >= 0 else -1)
    dx, dy = abs(dx), abs(dy)
    while dx > 0 or dy > 0:
        mx, my = min(4, dx), min(4, dy)
        mouse(sx * mx, sy * my)
        dx, dy = dx - mx, dy - my
        time.sleep(0.004)


def cursor():
    p = wt.POINT()
    u32.GetCursorPos(ctypes.byref(p))
    return (p.x, p.y)


def click():
    mouse(flags=0x0002)
    time.sleep(0.05)
    mouse(flags=0x0004)
    time.sleep(0.05)


# ---- shared header ----------------------------------------------------------------------------------------------
def slot0(m):
    vis, w, h, x, y, scale, frame, buf, shown = struct.unpack_from("<iiifffiii", m, SLOTS_OFF)
    return dict(visible=vis, w=w, h=h, x=x, y=y, scale=scale, frame=frame, shown=shown)


def app_status(app_pid):
    """the status line of the Window View window (its second line says where the desk cursor is)"""
    texts = []
    for top in windows_of({app_pid}):
        @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
        def cb(h, _):
            n = u32.SendMessageW(h, 0x000E, 0, 0) + 1
            buf = ctypes.create_unicode_buffer(n)
            u32.SendMessageW(h, 0x000D, n, buf)
            if "cursor" in buf.value or buf.value.startswith("Connected") or buf.value.startswith("No game"):
                texts.append(buf.value.replace("\n", " | "))
            return True
        u32.EnumChildWindows(top, cb, 0)
    return texts[0] if texts else "(no status)"


def notepad_text(hwnd):
    edit = u32.FindWindowExW(hwnd, None, "Edit", None)
    if not edit:
        return None
    n = u32.SendMessageW(edit, 0x000E, 0, 0) + 1   # WM_GETTEXTLENGTH
    buf = ctypes.create_unicode_buffer(n)
    u32.SendMessageW(edit, 0x000D, n, buf)          # WM_GETTEXT
    return buf.value


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--shot", default=os.path.join(os.environ.get("TEMP", "."), "input_test.png"))
    ap.add_argument("--keep", action="store_true", help="leave the app, Notepad and the game running")
    a = ap.parse_args()
    ok = {}

    # Notepad with a known title, and a layout that puts it in VR at the middle of the desk
    scratch = os.path.join(tempfile.gettempdir(), "wv_test.txt")
    open(scratch, "w").close()
    np = subprocess.Popen(["notepad.exe", scratch])
    hw = []
    t0 = time.time()
    while time.time() - t0 < 10 and not hw:
        hw = windows_of({np.pid})
        time.sleep(0.2)
    if not hw:
        sys.exit("Notepad did not open")
    layout_dir = os.path.join(os.environ["APPDATA"], "WindowViewVR")
    os.makedirs(layout_dir, exist_ok=True)
    layout = os.path.join(layout_dir, "layout.txt")
    backup = open(layout).read() if os.path.exists(layout) else None
    with open(layout, "w") as f:
        f.write("notepad.exe|wv_test|0.0|0.0|0.0006|0.85|1\n")

    app = subprocess.Popen([os.path.join(ROOT, "dist", "WindowView.exe"), "--test-input"])
    layer_dir = tempfile.mkdtemp(prefix="windowview-test-")
    with open(os.path.join(layer_dir, "window_view_layer.json"), "w") as f:
        json.dump({"file_format_version": "1.0.0", "api_layer": {"name": LAYER_NAME, "library_path": os.path.join(ROOT, "dist", "window_view_layer.dll"),
                                                                 "api_version": "1.0", "implementation_version": "1", "description": "Window View (test)"}}, f)
    env = dict(os.environ, XR_RUNTIME_JSON=SIM_RUNTIME, XR_API_LAYER_PATH=layer_dir, XR_ENABLE_API_LAYERS=LAYER_NAME,
               DISABLE_WINDOW_VIEW_LAYER="1")   # the installed copy (registered with Layer ON) stays out of the test
    import xr
    loader = os.path.join(os.path.dirname(xr.__file__), "library", "windows_x86_64", "openxr_loader.dll")
    game = subprocess.Popen([os.path.join(ROOT, "build", "xr_test_app.exe"), loader, "d3d11", "40"], env=env, stdout=subprocess.PIPE, text=True)
    m = mmap.mmap(-1, HEADER, tagname=NAME)
    try:
        time.sleep(4)
        s = slot0(m)
        print("slot0 before:", s)
        ok["notepad captured"] = s["visible"] == 1 and s["w"] > 100 and s["h"] > 100
        ok["layer showed it"] = s["shown"] > 0

        tap(VK_PRIOR)                      # mouse and keyboard to the windows
        time.sleep(0.5)
        parked = cursor()
        move(60, 40)                       # the real cursor must stay parked while the desk cursor moves
        time.sleep(0.2)
        ok["real cursor stayed parked"] = cursor() == parked
        print("real cursor parked at", parked, "now", cursor())
        move(-60, -40)
        time.sleep(0.2)
        click()                            # the desk cursor is back at (0,0): the middle of Notepad's picture = the text area
        time.sleep(0.3)
        for vk in (0x48, 0x45, 0x4C, 0x4C, 0x4F):   # h e l l o
            tap(vk)
        time.sleep(0.6)
        text = notepad_text(hw[0])
        print("notepad text:", repr(text))
        ok["typed into notepad"] = text == "hello"

        shot_ok, img = grab(windows_of(pids_of("xr_test_app.exe"))[0])
        img.save(a.shot)
        print("SAVED", a.shot)

        # Windows scales mouse movement by the pointer-speed setting, so first measure how far 100 px really goes
        move(0, -100)
        time.sleep(0.7)
        st = app_status(app.pid)
        print("status after 100 px up:", st)
        cy = float(st.split("cursor ")[1].split()[1])
        ratio = max(0.1, cy / (100 * 0.0006))
        print("movement ratio %.2f" % ratio)
        # drag by the header: up to the strip, then 150 px right
        move(0, -int((s["h"] // 2 - 14 - 100 * ratio) / ratio))
        time.sleep(0.7)
        print("status at header:", app_status(app.pid))
        preview = windows_of(pids_of("xr_test_app.exe"))[0]
        u32.MoveWindow(preview, 0, 0, 1700, 1000, True)   # a bigger preview photographs better
        time.sleep(0.8)
        grab(preview)[1].save(a.shot.replace(".png", "_header.png"))
        mouse(flags=0x0002)
        time.sleep(0.1)
        move(150, 0)
        time.sleep(0.1)
        mouse(flags=0x0004)
        time.sleep(0.5)
        s2 = slot0(m)
        print("slot0 after drag:", s2)
        time.sleep(0.6)
        print("status after drag:", app_status(app.pid))
        expected = 150 * ratio * 0.0006
        ok["window moved right by the drag"] = abs(s2["x"] - expected) < expected * 0.35 and abs(s2["y"]) < 0.002

        tap(VK_NEXT)                       # back to normal
        time.sleep(0.3)
        before = cursor()
        move(40, 0)
        time.sleep(0.2)
        ok["real cursor moves again after Page Down"] = cursor() != before
        for k, v in ok.items():
            print(("  ok   " if v else "  FAIL ") + k)
        print("RESULT:", "PASS" if all(ok.values()) else "FAIL")
    finally:
        tap(VK_NEXT)
        if not a.keep:
            app.terminate()
            game.terminate()
            k32.TerminateProcess(int(np._handle), 0)
            if backup is None:
                os.remove(layout)
            else:
                open(layout, "w").write(backup)
    sys.exit(0 if all(ok.values()) else 1)


if __name__ == "__main__":
    main()
