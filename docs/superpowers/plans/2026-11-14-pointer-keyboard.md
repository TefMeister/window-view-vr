# Window View 1.12 Pointer Keyboard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Window View's keyboard loses no presses, types with no delay, and gives instant light-up, flash and buzz on both hands, like Virtual Desktop's.

**Architecture:** The keyboard stays a WinForms window (`KeysUi`), but presses are detected by geometry in the pointer loop for both hands and typed by a dedicated typing thread with the terminal program looked up in the background. The OpenXR layer draws the light-up and flash quads every frame and fires the haptic pulse, fed through a new, separate shared block so the existing block other apps read is untouched.

**Tech Stack:** C# (.NET Framework 4, `csc` from Windows), C++17 OpenXR API layer (VS 2022 Build Tools, 64-bit and 32-bit), Python 3 tests, the OpenXR Simulator and Lanes' VR Bench.

**Spec:** `docs/superpowers/specs/2026-11-14-pointer-keyboard-design.md`

## Global Constraints

- The main shared block `Local\FlatToVR_WindowView_v2` (644 bytes) does not change: name, size, offsets. Five other apps read it.
- New fields live only in `Local\FlatToVR_WindowView_keys_v1` (`WindowViewKeys`, `shared.h`); the app creates it, the layer opens it.
- Keyboard press: trigger past 0.35 counts, below 0.2 lets go; right A also presses; left X never does.
- On every other window the trigger stays 0.6 down / 0.4 up, posted mouse messages, exactly as 1.11.
- Flash: 120 ms, green when typed, red when lost. Light-up: white at 25 %. Flash colours at 60 %. Haptic: 20 ms, amplitude 0.5, the pressing hand only.
- The keyboard window keeps `WS_EX_NOACTIVATE`; the game keeps the Windows focus.
- Light-up and flash are drawn by the layer only; the keyboard window repaints only when Shift / Ctrl change.
- Press log: `%APPDATA%\WindowViewVR\keys.log`, newest 2,000 lines kept.
- Hand-written source files stay under 800 lines; named constants at the top of each class, no bare numbers in code.
- Both layer builds (64-bit, 32-bit) change together; `build.bat` must end with `BUILD OK`.
- Tests never type into a real Claude session or a real file; the end-to-end test types into a throwaway terminal window it starts itself.
- Version: 1.12; README section "Use" item 9 and "Tested" updated.

## Review Focus

1. Both hands on the same key in the same moment: two presses, both typed, in order (test in Task 3).
2. The target terminal's active tab changes or its program exits between presses: the background lookup follows; a failed write retries once with a fresh lookup (test in Task 4).
3. CONTROL mode switched off, or the keyboard's Show box ticked off, while a trigger is held: no stuck press, no light left on (test in Task 5).
4. Hand Poser claiming the buttons while typing: the trigger still types, A still types (1.10 rule kept) (test in Task 5).
5. A trigger hovering around 0.35 (resting finger): one press, never a stream (test in Task 3).

---

### Task 1: The keys block, both sides

**Files:**
- Modify: `shared.h` (append after `WindowViewShared`)
- Create: `app/KeysShared.cs`
- Create: `app/SelfTest.cs`
- Modify: `app/WindowView.cs` (the `--selftest-keys` switch in `Main`)

**Interfaces:**
- Produces (C++ `shared.h`):
  `#define WINDOW_VIEW_KEYS_MAPPING_NAME L"Local\\FlatToVR_WindowView_keys_v1"`, `#define WINDOW_VIEW_KEYS_SIZE 192`,
  `struct WindowViewKeysHand { volatile LONG lit_visible; volatile LONG lit_slot; float lit_c[3]; float lit_w; float lit_h; volatile LONG press_seq; volatile LONG press_ok; volatile LONG press_slot; float press_c[3]; float press_w; float press_h; volatile LONG cursor_visible; float cursor_p[3]; };` (`static_assert` its size; the builder fixes the exact byte count and writes every offset in a comment like `shared.h` does),
  `struct WindowViewKeys { uint32_t magic; uint32_t version; float ltrigger; volatile LONG haptic_ok; volatile LONG haptic_fail; volatile LONG lit_drawn; volatile LONG flash_drawn; WindowViewKeysHand hand[2]; /* 0 = left, 1 = right */ };`, `static_assert(sizeof(WindowViewKeys) == WINDOW_VIEW_KEYS_SIZE)` (pad to it).
- Produces (C#): `class KeysShared : IDisposable` with `float LTrigger { get; }`, `void WriteLit(int hand, bool visible, int slot, V3 centre, float w, float h)`, `void WritePress(int hand, int slot, V3 centre, float w, float h)` (bumps `press_seq`, sets `press_ok = 1`), `void MarkLost(int hand)` (sets `press_ok = 0`), `void WriteLeftCursor(bool visible, V3 tip)`, counters `int HapticOk, HapticFail, LitDrawn, FlashDrawn { get; }`. Offsets as `const int` at the top, mirroring the C++ comments.
- Produces: `static class SelfTest { public static int Run(string what) }` returning 0 = all PASS; prints one `PASS name` / `FAIL name: why` line per check.

- [ ] **Step 1:** Write the `keys-block` checks in `SelfTest.cs`: `KeysShared.SIZE == 192`; write lit/press for hand 1, read the bytes back at the C++ offsets; `press_seq` goes 0 → 1 → 2 over two `WritePress`; `MarkLost` leaves `press_seq` and sets `press_ok` 0.
- [ ] **Step 2:** `build.bat` then `dist\WindowView.exe --selftest-keys keys-block`. Expected: build fails (no `KeysShared`).
- [ ] **Step 3:** Add the struct to `shared.h` and write `KeysShared.cs` (`MemoryMappedFile.CreateOrOpen(NAME, SIZE)`, magic `0x5359454Bu` "KEYS", version 1, written when the magic is missing).
- [ ] **Step 4:** `build.bat` (expect `BUILD OK`, the layer's `static_assert`s hold), then `dist\WindowView.exe --selftest-keys keys-block`. Expected: every line `PASS`, exit 0.
- [ ] **Step 5:** Commit `shared.h app/KeysShared.cs app/SelfTest.cs app/WindowView.cs`: "1.12: a separate keys block (the main block stays as other apps read it)".

### Task 2: The key table and the hit test

**Files:**
- Modify: `app/KeysUi.cs` (build the table while laying out; one-shot Shift / Ctrl made safe to toggle from another thread)
- Create: `app/KeyMap.cs`
- Modify: `app/SelfTest.cs` (checks `keymap`)

**Interfaces:**
- Produces: `struct KeyRect { public int X, Y, W, H; public uint Vk; }` (client pixels); `KeysUi.KeyTable` → `KeyRect[]` (a snapshot array, replaced whole, never edited); `KeysUi.ToggleShift()`, `KeysUi.ToggleCtrl()`, `KeysUi.TakeMods(out bool shift, out bool ctrl)` (returns the one-shot state and clears it, repainting through `BeginInvoke`), all callable from any thread.
- Produces: `static class KeyMap { public static bool Find(KeyRect[] table, float picX, float picY, out KeyRect key) }`: `picX/picY` are pixels on the captured picture, which carries `Capture.HEADER` (28) rows above the client area; a point in a gap between keys finds nothing.

- [ ] **Step 1:** `keymap` checks: a table of two keys `{X=8,Y=8,W=46,H=40,Vk='Q'}`, `{X=58,Y=8,W=46,H=40,Vk='W'}`; `Find(t, 30, 8+28+20)` → Q; `Find(t, 80, 56)` → W; `Find(t, 56, 56)` (the 4 px gap) → false; `Find(t, 30, 20)` (inside the header strip) → false.
- [ ] **Step 2:** Build and run `--selftest-keys keymap`: fails to build.
- [ ] **Step 3:** Implement `KeyMap.Find` and fill `KeysUi.KeyTable` in the constructor's layout loop from each button's bounds and `K.Vk`.
- [ ] **Step 4:** Build; `--selftest-keys keymap`: all PASS.
- [ ] **Step 5:** Commit: "1.12: the keyboard's key table and hit test".

### Task 3: One hand's press detector

**Files:**
- Create: `app/HandKeys.cs`
- Modify: `app/SelfTest.cs` (checks `handkeys`)

**Interfaces:**
- Produces: `class HandKeys { public HandKeys(int hand); public KeyEvent Step(float trigger, bool aButton, bool onKeyboard, float picX, float picY, KeyRect[] table, int nowMs); public bool Lit { get; } public KeyRect LitKey { get; } public void Reset(); }` with `struct KeyEvent { public bool Pressed; public KeyRect Key; public int Hand; public int AtMs; }`.
- Rules it must hold: press when trigger rises past 0.35 (or A goes down) while `onKeyboard` and a key is found; the key is the one under the pointer at the FIRST touch (trigger > 0.12, the 1.8 hold-still rule), so a tilt during the pull cannot move it; no new press until trigger < 0.2 and A up; `Reset()` (CONTROL off, keyboard hidden) drops all state and clears `Lit`.

- [ ] **Step 1:** `handkeys` checks, each a sequence of `Step` calls: (a) trigger 0 → 0.4: one press of the key under the pointer; (b) trigger 0 → 0.3 → 0: no press; (c) 0 → 0.5 → 0.3 → 0.5 → 0.36: ONE press (never fell below 0.2); (d) 0 → 0.15 on Q, pointer slides onto W, → 0.5: the press is Q; (e) A down/up twice with the trigger at 0: two presses; (f) two `HandKeys` (left, right) both reaching 0.4 on Q in the same step: two events, hands 0 and 1; (g) pressed, `Reset()`, trigger still 0.5: no press, `Lit` false.
- [ ] **Step 2:** Build; `--selftest-keys handkeys`: fails to build.
- [ ] **Step 3:** Implement `HandKeys` (constants `PRESS = 0.35f, RELEASE = 0.2f, TOUCH = 0.12f` at the top).
- [ ] **Step 4:** Build; `--selftest-keys handkeys`: all PASS.
- [ ] **Step 5:** Commit: "1.12: the per-hand press detector".

### Task 4: The typing thread, the warm program lookup and the press log

**Files:**
- Create: `app/TypingQueue.cs`
- Modify: `app/ConsoleInput.cs` (a background lookup, a many-keys write)
- Modify: `app/InputRouter.cs` (`TypeKey` becomes the queue's non-terminal sink; `KeyTarget` made public for the queue)
- Modify: `app/SelfTest.cs` (checks `typing`)

**Interfaces:**
- Consumes: `KeyEvent` (Task 3), `KeysShared.MarkLost` (Task 1).
- Produces: `interface IKeySink { bool Write(IntPtr target, KeyStroke[] keys, out string why); void Refresh(IntPtr target); }` with `struct KeyStroke { public uint Vk; public bool Shift, Ctrl; public int Hand; public int PressMs; }`; the real sink routes terminals to `ConsoleInput.TypeMany` and others to `InputRouter.TypeKey`.
- Produces: `class TypingQueue : IDisposable { public TypingQueue(IKeySink sink, Func<IntPtr> target, Action<int> lost, string logPath); public void Add(KeyStroke k); public void Flush(int timeoutMs); }`: one thread; strokes waiting together go out in ONE `Write`; a failed `Write` → `Refresh(target)` and one retry; still failing → `lost(hand)` for each stroke and a `lost` log line with `why`. A background timer calls `Refresh` every 2,000 ms and whenever `target()` or its title changes.
- Produces: `ConsoleInput.TypeMany(IntPtr hwnd, KeyStroke[] keys, out string why) : bool` (one attach, one `WriteConsoleInput` of all records) and `ConsoleInput.Refresh(IntPtr hwnd)` (the existing `FindProgram`, stored with no expiry; `TypeMany` never calls `FindProgram` itself).
- Log line: `HH:mm:ss.fff  hand=L|R  key=<name>  press=<ms>  wrote=<ms>  result=typed|retried|lost  why=<text>`; file trimmed to the newest 2,000 lines when it passes 2,500.

- [ ] **Step 1:** `typing` checks with a fake `IKeySink` that records calls: (a) 3 strokes added in one burst while the sink is held busy → one `Write` with all 3, in order; (b) left, right, left added → written in that order; (c) sink fails once then works → 1 `Refresh`, 2 `Write`s, no `lost`, log `result=retried`; (d) sink always fails → `lost` called for the stroke's hand, log `result=lost why=<sink's text>`; (e) log file of 2,600 lines → after one more press, 2,000 lines remain, the newest last.
- [ ] **Step 2:** Build; `--selftest-keys typing`: fails to build.
- [ ] **Step 3:** Implement `TypingQueue` and the `ConsoleInput` / `InputRouter` changes.
- [ ] **Step 4:** Build; `--selftest-keys typing`: all PASS; `--selftest-keys` (all checks): all PASS.
- [ ] **Step 5:** Commit: "1.12: typing on its own thread, the terminal program looked up in the background, the press log".

### Task 5: Wiring both hands in the app

**Files:**
- Modify: `app/Pointer.cs` (two `HandKeys`; the keyboard checked before the 1.11 click path)
- Modify: `app/KeysUi.cs` (mouse-clicked keys go through the queue too)
- Modify: `app/ViewUi.cs` (create `KeysShared` and `TypingQueue`, dispose them on close)
- Modify: `app/SelfTest.cs` (checks `wiring`, driving `Pointer`'s keyboard step with recorded inputs)

**Interfaces:**
- Consumes: Tasks 1-4.
- Produces: in `Pointer.Controllers`, for each hand with a valid ray: `Under(...)`; if the window under it is the keyboard's `Placement` (title `KeysUi.TITLE`, shown, live), convert (u, v) to picture pixels, `HandKeys.Step(...)` with that hand's trigger (right: `shared.ReadRay` trigger and A; left: `KeysShared.LTrigger`, no button), write `WriteLit` (centre and size from `WindowFrame.Of(p, ...).PointAt` of the key's corners), on a press `WritePress` and `TypingQueue.Add` with `KeysUi.TakeMods`. Shift / Ctrl keys toggle instead of typing. While a hand's pointer is on the keyboard, the right hand's 1.11 click path is skipped for that press; the left ray writes `WriteLeftCursor`. Left grip carrying still starts on the keyboard as today.
- `Release()` (CONTROL off) and a hidden keyboard call `HandKeys.Reset()` on both and clear both lit flags and the left cursor.
- Button claim (Hand Poser): trigger and A still type.

- [ ] **Step 1:** `wiring` checks with a fake keyboard placement and recorded rays: right ray on Q, trigger 0 → 0.4: one stroke Q in the queue, `press_seq` 1 for hand 1, the 1.11 click path NOT taken; left ray on W, `ltrigger` 0.4: stroke W, hand 0; Shift then Q: stroke `Q` with `Shift=true`, then the next key without; CONTROL off while the trigger is at 0.5: no stroke, both lit flags 0; buttons claimed: trigger still gives a stroke.
- [ ] **Step 2:** Build; `--selftest-keys wiring`: fails.
- [ ] **Step 3:** Implement the wiring.
- [ ] **Step 4:** Build; `--selftest-keys` (all): PASS; `python test\view_test.py` and `--actions`: PASS (nothing else broke).
- [ ] **Step 5:** Commit: "1.12: both hands type on the keyboard".

### Task 6: The layer: left trigger, haptics, light-up and flash

**Files:**
- Modify: `layer/controllers.inl` (actions `A_LTRIGGER`, `A_LHAPTIC`, `A_RHAPTIC`; bindings `/user/hand/left/input/trigger/value`, `/user/hand/left/output/haptic`, `/user/hand/right/output/haptic` for Touch and Index; `xrApplyHapticFeedback` fetched in the `get(...)` list; in `controller_frame`: write `ltrigger` to the keys block, and when a hand's `press_seq` moved since the last frame, one pulse of 20 ms (`XrHapticVibration.duration = 20'000'000`, `amplitude = 0.5f`, `frequency = XR_FREQUENCY_UNSPECIFIED`) on that hand, counting `haptic_ok` / `haptic_fail`)
- Modify: `layer/window_view_layer.cpp` (open the keys block like the slot mappings; three 4x4 solid swapchains: white α 64, green α 153, red α 153; after the windows and before the cursor: per hand a lit quad at `lit_c` sized `lit_w`×`lit_h` facing `slot_face[lit_slot]`, 2 mm in front; per hand a flash quad for 120 ms after its `press_seq` changed, red if `press_ok` is 0 when drawn; the left cursor drawn with the existing arrow at `cursor_p`; count `lit_drawn` / `flash_drawn`)
- Modify: `test/view_test.py` (a `--keys` option: the app side is played by the test writing the keys block; PASS when `lit_drawn` and `flash_drawn` rise and, with `--actions`, `haptic_ok` rises and `haptic_fail` stays 0)

**Interfaces:**
- Consumes: `WindowViewKeys` (Task 1); `Session::slot_face`, `ensure_swapchain`, `upload` (existing).

- [ ] **Step 1:** Add the `--keys` checks to `view_test.py`.
- [ ] **Step 2:** `build.bat`, then `python test\view_test.py --actions --keys`: FAIL (counters stay 0).
- [ ] **Step 3:** Implement the layer changes, both `.cpp` units built 64 and 32 bit.
- [ ] **Step 4:** `build.bat` → `BUILD OK`; `python test\view_test.py --keys` (D3D11 and D3D12) and `--actions --keys`: PASS; the plain runs of Task 5 Step 4 still PASS.
- [ ] **Step 5:** Commit: "1.12: the layer lights the key, flashes the press and buzzes the hand".

### Task 7: End to end on the simulator, then release as 1.12

**Files:**
- Create: `test/echo_keys.py` (reads keys with `msvcrt.getwch`, appends `<ms since start> <char code>` per key to a file given on the command line; exits on `~`)
- Create: `test/keys_test.py`
- Modify: `README.md` (Use item 9: both hands, light-up, buzz, the press log; Tested: the new checks), `app/WindowView.cs` version string `1.12`

**Interfaces:**
- `keys_test.py`: starts a throwaway Windows Terminal window running `echo_keys.py` with a temp output file, starts Window View (layout pointing the keyboard and that terminal into VR, as `input_test.py` does), the stand-in game on the OpenXR Simulator with VR Bench on (`vrbench.py start`), CONTROL mode on; clicks the terminal once with the right pointer; then types `hello world` right hand only, `vr keys` alternating left / right, and 10 keys at trigger 0.4; then a 5 s pause and one key. Key centres come from a test-only `--dump-keys <file>` switch of the app (each key's centre in the desk frame); the test aims each controller from its VR Bench position at that point.
- PASS lines: every key arrived once, in order; each arrival under 50 ms after its press (press time from `keys.log`); the key after the pause also under 50 ms; `keys.log` has a line per press, none `lost`; the layer counters as in Task 6.

- [ ] **Step 1:** Write `echo_keys.py` and `keys_test.py` (and the `--dump-keys` switch).
- [ ] **Step 2:** `python test\keys_test.py`: expect PASS on everything; any FAIL is fixed in the task that owns it before going on.
- [ ] **Step 3:** README and version 1.12; `build.bat`; `install.ps1`; the installed app starts and mends the layer registration.
- [ ] **Step 4:** Commit and hand over (`parcel.py send window-view-vr --note "Window View 1.12: the pointer keyboard types with both hands, lights up and buzzes like VD's"`).
- [ ] **Step 5:** Update the owed HOME item with what is left: Tefa types a few sentences into a Claude window in the headset and says whether any press was lost or late, and whether the light-up and buzz feel right.
