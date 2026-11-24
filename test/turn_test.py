"""turn_test.py -- 1.15: does the app aim at exactly the window the layer draws, for every tilt and turn?

Runs build/turn_test.exe (the layer's own sums, layer/face.inl, compiled) and `dist/WindowView.exe --dump-frames` (the app's
hit test, Pointer.cs WindowFrame.Of) over the same grid of cases (follow and room mode, two desk distances, three places
across, two heights, two depths, four tilts, three turns: 576 cases) and compares each window's middle and its three axes.
Then `WindowView.exe --selftest-keys turns` (the signs, the stick, the layout file). No game, no headset, no real files.

    python turn_test.py          exit 0 = every case agrees within 1e-4 m and every self-test passed
"""
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
TOL = 1e-4


def cases(lines):
    out = {}
    for line in lines:
        line = line.strip()
        if not line:
            continue
        parts = [p.split() for p in line.split("|")]
        key = tuple(round(float(v), 4) for v in parts[0])
        out[key] = [[float(v) for v in p] for p in parts[1:]]
    return out


def main():
    layer = subprocess.run([os.path.join(ROOT, "build", "turn_test.exe")], capture_output=True, text=True, check=True).stdout.splitlines()
    app_exe = os.path.join(ROOT, "dist", "WindowView.exe")
    app = subprocess.run([app_exe, "--dump-frames"], capture_output=True, text=True, check=True).stdout.splitlines()
    a, b = cases(layer), cases(app)
    fails = 0
    if len(a) != 576 or set(a) != set(b):
        print("FAIL the two grids differ: layer %d cases, app %d" % (len(a), len(b)))
        fails += 1
    worst = 0.0
    names = ("middle", "right", "up", "normal")
    for key in sorted(set(a) & set(b)):
        for name, va, vb in zip(names, a[key], b[key]):
            d = max(abs(x - y) for x, y in zip(va, vb))
            worst = max(worst, d)
            if d > TOL:
                fails += 1
                if fails <= 10:
                    print("FAIL %s %s: layer %s app %s" % (key, name, va, vb))
    print("%s layer and app agree on %d cases x 4 vectors (worst difference %.2e)" % ("PASS" if fails == 0 else "FAIL", len(a), worst))
    st = subprocess.run([app_exe, "--selftest-keys", "turns"], capture_output=True, text=True)
    print(st.stdout.strip())
    ok = fails == 0 and st.returncode == 0
    print("TURN TEST: " + ("ALL PASS" if ok else "FAILED"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
