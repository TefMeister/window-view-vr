"""view_test.py -- does the Window View layer really put the stand-in app's picture into the stand-in game as a quad?

Runs xr_test_app.exe (a stand-in game: D3D11 or D3D12, both eyes grey) on the OpenXR Simulator with the layer enabled
for THAT process only (XR_API_LAYER_PATH + XR_ENABLE_API_LAYERS; the registry is not touched), beside fake_window.py
(a stand-in app filling slot 0 with red/blue). PASS when the stand-in game ended frames, the layer reported the right
graphics API and its exe name, uploaded at least one picture and submitted quads, with no error noted. What the quad
LOOKS like is checked by eye with the simulator's screenshot (the MCP capture_screenshot tool) during a run.

    python view_test.py [d3d11|d3d12|both] [--runtime <runtime json>] [--seconds 6] [--resize] [--room]
--room (1.4): the room mode on; also PASS only when the layer set its anchor in the room.
--actions (1.5): the stand-in game uses controllers; also PASS only when the game's own actions all worked and the layer's
controller actions were attached beside them. --ctrl: CONTROL mode on from the start (the game's hands held).
Exit 0 = every requested API passed.
"""
import argparse
import json
import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
LAYER_NAME = "XR_APILAYER_FLATTOVR_window_view"
SIM_RUNTIME = r"D:\claude video game stuff\tools\OpenXR-Simulator\openxr_simulator.json"
API_CODE = {"d3d11": 1, "d3d12": 2}


def find_loader():
    try:
        import xr   # pyopenxr ships a 64-bit loader
        d = os.path.join(os.path.dirname(xr.__file__), "library")
        for sub in ("windows_x86_64", ""):
            p = os.path.join(d, sub, "openxr_loader.dll")
            if os.path.exists(p):
                return p
    except ImportError:
        pass
    return None


def run_one(api, args, env):
    fake = [sys.executable, "-I", os.path.join(HERE, "fake_window.py"), "--seconds", str(args.seconds + 2)]
    if args.resize:
        fake.append("--resize")
    if args.room:
        fake += ["--room", "--depth", "1.5"]
    if args.ctrl:
        fake.append("--ctrl")
    fw = subprocess.Popen(fake, stdout=subprocess.PIPE, text=True)
    game = subprocess.run([args.app, args.loader, api, str(args.seconds)] + (["actions"] if args.actions else []), env=env, capture_output=True, text=True)
    report = fw.communicate()[0].strip()
    print(game.stdout.strip())
    print(report)
    frames = int(next((l.split(":")[1] for l in game.stdout.splitlines() if l.startswith("FRAMES:")), "0"))
    got = dict(kv.split("=", 1) for kv in report.replace("LAYER: ", "").split())
    checks = {
        "game ended frames": frames > 0,
        "layer saw the right API": int(got.get("api", -1)) == API_CODE[api],
        "layer named the game": got.get("game", "").lower() == "xr_test_app.exe",
        "pictures uploaded": int(got.get("uploads", 0)) >= 1,
        "quads submitted": int(got.get("quads", 0)) >= 1,
        "no error noted": int(got.get("last_error", 1)) == 0,
    }
    if args.room:
        checks["room mode on and the anchor set in the room"] = int(got.get("room", 0)) == 1 and int(got.get("anchor_set", 0)) == 1
    if args.actions:
        acts = next((l for l in game.stdout.splitlines() if l.startswith("ACTIONS:")), "ACTIONS: none")
        checks["the game's own controller actions all worked"] = acts.startswith("ACTIONS: ok") and " syncs=0" not in acts
        checks["the layer's controller actions attached beside them"] = int(got.get("pointer_ready", 0)) == 1
    for name, ok in checks.items():
        print(("  ok   " if ok else "  FAIL ") + name)
    return all(checks.values())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("api", nargs="?", default="both", choices=["d3d11", "d3d12", "both"])
    ap.add_argument("--runtime", default=SIM_RUNTIME)
    ap.add_argument("--loader", default=find_loader())
    ap.add_argument("--app", default=os.path.join(HERE, "..", "build", "xr_test_app.exe"))
    ap.add_argument("--dll", default=os.path.join(HERE, "..", "dist", "window_view_layer.dll"))
    ap.add_argument("--seconds", type=float, default=6.0)
    ap.add_argument("--resize", action="store_true")
    ap.add_argument("--room", action="store_true")
    ap.add_argument("--actions", action="store_true")
    ap.add_argument("--ctrl", action="store_true")
    args = ap.parse_args()
    if not args.loader:
        sys.exit("no openxr_loader.dll found; pass --loader")
    layer_dir = tempfile.mkdtemp(prefix="windowview-test-")
    with open(os.path.join(layer_dir, "window_view_layer.json"), "w") as f:
        json.dump({"file_format_version": "1.0.0", "api_layer": {
            "name": LAYER_NAME, "library_path": os.path.abspath(args.dll), "api_version": "1.0",
            "implementation_version": "1", "description": "Window View (test)"}}, f)
    env = dict(os.environ, XR_RUNTIME_JSON=args.runtime, XR_API_LAYER_PATH=layer_dir, XR_ENABLE_API_LAYERS=LAYER_NAME,
               DISABLE_WINDOW_VIEW_LAYER="1")   # the installed copy (registered with Layer ON) stays out of the test
    apis = ["d3d11", "d3d12"] if args.api == "both" else [args.api]
    results = {api: run_one(api, args, env) for api in apis}
    print("RESULT: " + "  ".join("%s %s" % (k, "PASS" if v else "FAIL") for k, v in results.items()))
    sys.exit(0 if all(results.values()) else 1)


if __name__ == "__main__":
    main()
