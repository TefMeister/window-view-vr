"""keys_test.py -- 1.12: the pointer keyboard end to end, nobody at the PC. A throwaway Windows Terminal window (title
WVKEYTEST, running echo_keys.py) and Window View's keyboard are put in VR through the layout file; the app is started with
--dump-keys (where every key hangs, and which window keys would go to); the stand-in game runs on the OpenXR Simulator with
the Window View layer and VR Bench's pretend hands (explicitly, Window View nearest the game); CONTROL mode is switched on
through the shared block. Then VR Bench aims each hand at keys and pulls its trigger:
    "hello world" with the right hand, "vr keys" left / right / left ..., ten keys at a light pull (0.4), a 5 s pause, one key.
PASS when every key arrived once and in order, each within 50 ms of its press (the key after the pause too), the press log
has a line per press and none lost, and the layer lit, flashed and buzzed.
SAFETY: nothing is typed unless the app says the keys would go to the WVKEYTEST window; the real layout.txt is put back.
    python keys_test.py [--keep]
Needs build.bat's output, the OpenXR Simulator, VR Bench (Lanes) and pyopenxr (for its loader)."""
import argparse
import ctypes
import json
import math
import mmap
import os
import struct
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, ".."))
SIM_RUNTIME = r"D:\claude video game stuff\tools\OpenXR-Simulator\openxr_simulator.json"
WV_LAYER = "XR_APILAYER_FLATTOVR_window_view"
VB_LAYER = "XR_APILAYER_LANES_vr_bench"
VB_DIR = os.path.join(os.environ["LOCALAPPDATA"], "Programs", "VRBench")
VB_TOOLS = os.path.expanduser(r"~\github-backups\_lanes-next-wt\plugins\lanes\tools\vrbench")
MAIN_NAME, MAIN_SIZE, CTRL_MODE = r"Local\FlatToVR_WindowView_v2", 644, 536
KEYS_NAME, KEYS_SIZE = r"Local\FlatToVR_WindowView_keys_v1", 192
TITLE = "WVKEYTEST"
HEAD = (0.0, 1.7, 0.0)
LAG_MAX_MS = 50
STEP_S = 0.07          # between pose / pull / let go: several headset frames at 90 Hz
tick = ctypes.windll.kernel32.GetTickCount

sys.path.insert(0, VB_TOOLS)
import vb_input as I      # noqa: E402
import vb_settings as S   # noqa: E402
import vb_layer as L      # noqa: E402   (its on switch; the layer itself is enabled for the stand-in game only)


def wait_for(fn, seconds, what):
    end = time.time() + seconds
    while time.time() < end:
        v = fn()
        if v:
            return v
        time.sleep(0.2)
    raise SystemExit("timed out waiting for " + what)


def read_dump(path):
    try:
        return json.load(open(path, encoding="utf-8"))
    except (OSError, ValueError):
        return None


def head_level():
    os.makedirs(S.SIM_DATA_DIR, exist_ok=True)
    with open(os.path.join(S.SIM_DATA_DIR, S.HEAD_CMD), "w") as f:
        json.dump(dict(x=HEAD[0], y=HEAD[1], z=HEAD[2], yaw=0.0, pitch=0.0, roll=0.0), f)


def aim(centre):
    """a pose at the head, pointing at `centre` (the desk's frame = the head's, the head level and facing -Z)"""
    x, y, z = centre
    n = math.sqrt(x * x + y * y + z * z)
    dx, dy, dz = x / n, y / n, z / n
    return I.pose_from_ypr(HEAD, math.degrees(math.atan2(-dx, -dz)), math.degrees(math.asin(dy)))


class Hands:
    def __init__(self):
        rest = I.pose_from_ypr((HEAD[0], HEAD[1] - 0.5, HEAD[2] - 0.3), 0, -60)   # pointing at the floor, at nothing
        self.pose = [rest, rest]
        self.trig = [0.0, 0.0]
    def send(self):
        inp = I.Input().mode(poses="set", buttons="set")
        for h in (S.HAND_LEFT, S.HAND_RIGHT):
            inp.hand(h, grip=self.pose[h], aim=self.pose[h], trigger=self.trig[h])
        I.write(inp)
    def press(self, hand, centre, pull=1.0):
        self.pose[hand] = aim(centre); self.trig[hand] = 0.0; self.send(); time.sleep(STEP_S)
        t = tick()
        self.trig[hand] = pull; self.send(); time.sleep(STEP_S)
        self.trig[hand] = 0.0; self.send(); time.sleep(STEP_S)
        return t


def close_app(p):
    """Window View's own close: WM_CLOSE to its main window (the keyboard window refuses a close of its own); forced after 15 s"""
    u = ctypes.windll.user32
    found = []
    proc = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    def cb(h, _):
        pid = ctypes.c_ulong()
        u.GetWindowThreadProcessId(ctypes.c_void_p(h), ctypes.byref(pid))
        n = ctypes.create_unicode_buffer(256); u.GetWindowTextW(ctypes.c_void_p(h), n, 256)
        if pid.value == p.pid and n.value == "Window View":
            found.append(h)
        return True
    u.EnumWindows(proc(cb), 0)
    for h in found:
        u.PostMessageW(ctypes.c_void_p(h), 0x0010, 0, 0)   # WM_CLOSE
    try:
        p.wait(15)
    except subprocess.TimeoutExpired:
        p.kill(); p.wait(5)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--keep", action="store_true")
    a = ap.parse_args()
    if subprocess.run(["tasklist", "/FI", "IMAGENAME eq WindowView.exe"], capture_output=True, text=True).stdout.count("WindowView.exe"):
        raise SystemExit("Window View is running: close it first (the test runs its own copy)")
    work = tempfile.mkdtemp(prefix="wv-keys-test-")
    echo_out, dump = os.path.join(work, "echo.txt"), os.path.join(work, "keys.json")
    appdata = os.path.join(os.environ["APPDATA"], "WindowViewVR")
    os.makedirs(appdata, exist_ok=True)
    layout, keylog = os.path.join(appdata, "layout.txt"), os.path.join(appdata, "keys.log")
    backup = open(layout, encoding="utf-8").read() if os.path.exists(layout) else None
    log_start = len(open(keylog, encoding="utf-8").read().splitlines()) if os.path.exists(keylog) else 0
    procs, checks = [], {}
    try:
        # the throwaway target, first in the layout so no other row takes its window
        procs.append(subprocess.Popen(["wt.exe", "-w", "new", "--title", TITLE, "--suppressApplicationTitle",
                                       sys.executable, "-I", os.path.join(HERE, "echo_keys.py"), echo_out]))
        time.sleep(3)
        with open(layout, "w", encoding="utf-8") as f:
            f.write("WindowsTerminal.exe|%s|0.0|0.22|0.0006|1|1\n" % TITLE
                    + "WindowView.exe|Keyboard (Window View)|0.0|-0.12|0.0006|1|1\n"
                    + "WindowView.exe|Window View|0.0|0.0|0.0006|1|0\n"
                    + "WindowsTerminal.exe||0.0|0.0|0.0006|1|0\n")
        procs.append(subprocess.Popen([os.path.join(ROOT, "dist", "WindowView.exe"), "--dump-keys", dump]))
        head_level()
        layer_dir = tempfile.mkdtemp(prefix="windowview-test-")
        with open(os.path.join(layer_dir, "window_view_layer.json"), "w") as f:
            json.dump({"file_format_version": "1.0.0", "api_layer": {"name": WV_LAYER, "library_path": os.path.join(ROOT, "dist", "window_view_layer.dll"),
                                                                     "api_version": "1.0", "implementation_version": "1", "description": "Window View (test)"}}, f)
        env = dict(os.environ, XR_RUNTIME_JSON=SIM_RUNTIME, XR_API_LAYER_PATH=layer_dir + os.pathsep + VB_DIR,
                   XR_ENABLE_API_LAYERS=WV_LAYER + os.pathsep + VB_LAYER, DISABLE_WINDOW_VIEW_LAYER="1")
        import xr
        loader = os.path.join(os.path.dirname(xr.__file__), "library", "windows_x86_64", "openxr_loader.dll")
        L.enable()   # VR Bench reads the pretend hands only while its switch file says on
        hands = Hands(); hands.send()
        game = subprocess.Popen([os.path.join(ROOT, "build", "xr_test_app.exe"), loader, "d3d11", "120", "actions"], env=env,
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        procs.append(game)
        main_m = mmap.mmap(-1, MAIN_SIZE, tagname=MAIN_NAME)
        keys_m = mmap.mmap(-1, KEYS_SIZE, tagname=KEYS_NAME)
        wait_for(lambda: struct.unpack("<i", main_m[540:544])[0] == 1, 30, "the layer's controller actions (pointer_ready)")
        main_m[CTRL_MODE:CTRL_MODE + 4] = struct.pack("<i", 1)
        d = wait_for(lambda: (lambda d: d if d and d.get("keys") else None)(read_dump(dump)), 30, "the keyboard in VR (--dump-keys)")
        d = wait_for(lambda: (lambda d: d if d and d.get("target_title", "").startswith(TITLE) else None)(read_dump(dump)), 15,
                     "the keys to point at the %s window (they point at %r)" % (TITLE, (read_dump(dump) or {}).get("target_title")))
        print("keys go to:", d["target_title"])
        keys = {int(k): v for k, v in d["keys"].items()}
        vk = lambda ch: 0x20 if ch == " " else ord(ch.upper())
        k0 = struct.unpack("<iiii", keys_m[12:28])
        # what the layer sees of the right hand while it aims at "h" with the trigger held (printed, for a failing run)
        hands.pose[S.HAND_RIGHT] = aim(keys[vk("h")]); hands.trig[S.HAND_RIGHT] = 1.0; hands.send(); time.sleep(0.3)
        rv = struct.unpack("<i3f3ff", main_m[544:576]); seq = struct.unpack("<i", main_m[588:592])[0]
        print("aim at h %s: ray_valid=%d o=(%.3f %.3f %.3f) d=(%.3f %.3f %.3f) trigger=%.2f ray_seq=%d; right lit=%d" % (
            keys[vk("h")], rv[0], rv[1], rv[2], rv[3], rv[4], rv[5], rv[6], rv[7], seq, struct.unpack("<i", keys_m[104:108])[0]))
        hands.trig[S.HAND_RIGHT] = 0.0; hands.send(); time.sleep(0.3)
        presses, expect = [], ""
        for ch in "hello world":
            presses.append(hands.press(S.HAND_RIGHT, keys[vk(ch)])); expect += ch
        for i, ch in enumerate("vr keys"):
            presses.append(hands.press(S.HAND_LEFT if i % 2 == 0 else S.HAND_RIGHT, keys[vk(ch)])); expect += ch
        for ch in "abcdefghij":
            presses.append(hands.press(S.HAND_RIGHT, keys[vk(ch)], pull=0.4)); expect += ch
        time.sleep(5)
        presses.append(hands.press(S.HAND_LEFT, keys[vk("z")])); expect += "z"
        time.sleep(1)
        k1 = struct.unpack("<iiii", keys_m[12:28])
        got = [l.split() for l in open(echo_out, encoding="utf-8").read().splitlines() if l.strip()]
        text = "".join(chr(int(c)) for _, c in got)
        print("typed:", repr(text))
        checks["every key arrived once, in order"] = text == expect
        lags = [int(t) - p for (t, _), p in zip(got, presses)] if len(got) == len(presses) else []
        print("lag from pull to arrival (ms):", lags)
        checks["each key within %d ms of its pull" % LAG_MAX_MS] = bool(lags) and max(lags) <= LAG_MAX_MS
        checks["the key after a 5 s pause within %d ms" % LAG_MAX_MS] = bool(lags) and lags[-1] <= LAG_MAX_MS
        log = open(keylog, encoding="utf-8").read().splitlines()[log_start:] if os.path.exists(keylog) else []
        checks["a log line per press, none lost"] = len(log) == len(expect) and not any("result=lost" in l for l in log)
        dl = [b - a_ for a_, b in zip(k0, k1)]
        print("layer: haptic_ok +%d, haptic_fail +%d, lit +%d, flash +%d" % tuple(dl))
        checks["the layer lit the keys and flashed the presses"] = dl[2] > 0 and dl[3] > 0
        checks["the layer buzzed every press, none refused"] = dl[0] >= len(expect) and dl[1] == 0
    finally:
        L.disable()
        if not a.keep:
            if len(procs) > 2:
                subprocess.run(["taskkill", "/F", "/PID", str(procs[2].pid)], capture_output=True)   # the stand-in game
            if len(procs) > 1:
                close_app(procs[1])   # its own close saves the desk, so the layout is put back only after it is gone
            # only the throwaway echo program ends (never WindowsTerminal.exe: one copy of it hosts every terminal window,
            # this test's own session among them); its window then closes or says the program ended
            subprocess.run(["powershell", "-NoProfile", "-Command",
                            "Get-CimInstance Win32_Process -Filter \"Name='python.exe'\" | Where-Object { $_.CommandLine -like '*echo_keys.py*' } | "
                            "ForEach-Object { Stop-Process -Id $_.ProcessId -Force }"], capture_output=True)
            if backup is None:
                if os.path.exists(layout):
                    os.remove(layout)
            else:
                open(layout, "w", encoding="utf-8").write(backup)
    for name, ok in checks.items():
        print(("  ok   " if ok else "  FAIL ") + name)
    print("RESULT: " + ("PASS" if checks and all(checks.values()) else "FAIL"))
    sys.exit(0 if checks and all(checks.values()) else 1)


if __name__ == "__main__":
    main()
