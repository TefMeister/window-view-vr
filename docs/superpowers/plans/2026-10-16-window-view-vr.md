# Window View VR Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
> On Tefa's PCs: executed inline (no subagents, standing rule).

**Goal:** Show chosen desktop windows head-locked in any OpenXR game, with opacity, move/resize, and mouse+keyboard that go to the windows instead of the game.

**Architecture:** A C# app captures windows and owns all input and layout; an OpenXR API layer (C++) shows the app's pixels as quad layers. They talk through shared memory (`shared.h`).

**Tech Stack:** C++17 / MSVC (VS 2022 Build Tools), OpenXR headers in `third_party/`, D3D11 + D3D12; C# on .NET Framework 4 (`csc.exe`); Python only for test helpers.

**Spec:** `docs/superpowers/specs/2026-10-16-window-view-vr-design.md`

## Global Constraints

- Mapping names: header `Local\FlatToVR_WindowView_v1`, slot pixels `Local\FlatToVR_WindowView_v1_slot<N>`; magic `0x57564956` ("VIVW"), version 1; 8 slots; max slot size 2560 x 1600.
- Layer name `XR_APILAYER_FLATTOVR_window_view`, registered under `HKCU\Software\Khronos\OpenXR\1\ApiLayers\Implicit`, 64-bit + 32-bit copies, `disable_environment` `DISABLE_WINDOW_VIEW_LAYER`.
- Hotkeys: Page Up = input to windows ON, Page Down = OFF; both always swallowed while the app runs.
- Desk distance 1.0 m, default scale 0.0006 m/px, header 28 px.
- Closing the app hands input back to the game and hides all quads.

## Review Focus

1. Game started before the app is opened: the layer must show nothing and never crash until the app appears (header magic absent).
2. A window that closes while shown: the app frees its slot; the layer drops the quad and destroys the swapchain.
3. A window resized while shown: new pixel size, the layer remakes the swapchain at the next frame.
4. The app closed while input is ON: hooks removed, game gets mouse/keyboard back.
5. The runtime refuses the swapchain format or the quad layer: the layer passes the game's frame through untouched.

---

### Task 1: Shared block + layer showing slot pixels in a D3D11 session

**Files:** Create `shared.h`, `layer/window_view_layer.cpp`, `test/xr_test_app.cpp`, `test/fake_window.py`, `build.bat`.

**Interfaces:** Produces `WindowViewShared` / `WindowViewSlot` (shared.h, offsets commented for C#); test app `xr_test_app.exe <loader.dll> d3d11|d3d12 <seconds>`; `fake_window.py` fills slot 0 with a known pattern (red left half, blue right half) and keeps its frame counter rising.

- [ ] Write `xr_test_app.cpp`: loads the loader dynamically, creates a D3D11 (or D3D12) session on the default adapter, runs frames clearing a grey projection layer, exits after N seconds, prints `FRAMES: n`.
- [ ] Write the layer: negotiation, hooks for `xrCreateSession`, `xrDestroySession`, `xrEndFrame`; D3D11 upload; head-locked quad per visible slot; heartbeat + game name + API into the header.
- [ ] Build; run the test app on the OpenXR Simulator with the layer enabled for the process only (`XR_API_LAYER_PATH` + `XR_ENABLE_API_LAYERS`) while `fake_window.py` runs.
- [ ] Expected: simulator log shows `D3D11 quad drawn` / screenshot shows the red/blue quad; header reports the test app's exe name and `D3D11`.
- [ ] Commit.

### Task 2: D3D12 upload path

- [ ] Add the D3D12 path (upload buffer, command list on the game's queue, barriers RT -> COPY_DEST -> RT, fence wait before reuse).
- [ ] Run `xr_test_app.exe ... d3d12` with `fake_window.py`. Expected: `D3D12 quad drawn`, red/blue quad in the quad screenshot.
- [ ] Review-focus checks 1, 3, 5: test app with no header present (no crash, frames continue); `fake_window.py --resize` mid-run (quad follows); commit.

### Task 3: The app: capture, slots, window list, layer switch

**Files:** Create `app/WindowView.cs` (start-up, Shared, Layer registration), `app/Capture.cs` (PrintWindow into slot buffers), `app/Desk.cs` (window placements, header + grip drawing, hit test), `app/ViewUi.cs` (the window).

- [ ] Shared/Layer classes mirror Hand Freeze's (`HandFreeze.cs`) with the new names.
- [ ] Capture each added window ~30/s with `PrintWindow(hwnd, hdc, 2)`, compose header strip + opacity alpha into the back buffer, flip.
- [ ] UI: ON/OFF switch, MOUSE & KEYBOARD button, connected line, open-window list + Add, rows with opacity, size, show, Remove.
- [ ] Check with the test app + simulator: add Notepad, see it in the quad screenshot; opacity 50 % shows the grey through it.
- [ ] Commit.

### Task 4: Input: hooks, desk cursor, move/resize, forwarding

**Files:** Create `app/InputRouter.cs`.

- [ ] Hook thread with `WH_KEYBOARD_LL` (Page Up/Down always, other keys while ON) and `WH_MOUSE_LL` (while ON); swallow; mouse delta = hook point - current cursor point.
- [ ] Cursor on the desk; header drag moves, grip drag scales, else post `WM_MOUSEMOVE/BUTTON*/MOUSEWHEEL` to the deepest child at the point, sticky to the button-down target.
- [ ] Keys: `WM_CHAR` (via `ToUnicode` with our own modifier state) for characters, `WM_KEYDOWN/UP` otherwise, to the last-clicked control.
- [ ] Check: with input ON, the real cursor does not move; typing into Notepad through the desk works; a click on a Hand Freeze button presses it. Review-focus 4: close the app while ON, the mouse moves again.
- [ ] Commit.

### Task 5: Layout memory, install, README, publish

- [ ] Save/restore `%APPDATA%\WindowViewVR\layout.txt` (exe, title, x, y, scale, opacity, shown).
- [ ] `install.ps1` (like Hand Freeze's, `%LOCALAPPDATA%\Programs\WindowView`, desktop icon), README in Tefa's style with the untested caution and credits.
- [ ] Push to the public `TefMeister/window-view-vr`.
