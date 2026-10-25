# Window View — see your desktop windows inside any OpenXR game, and use them with the mouse

> ⚠️ **UNTESTED.** Built on 2026-10-16 and proven only on the OpenXR Simulator with a stand-in game, never yet in a real
> headset or a real game. It may do nothing, show the window in the wrong place, or need a restart to let your mouse go.
> Page Down always hands the mouse and keyboard back; closing the app does too.

<img src="docs/app-window.png" width="420"> <img src="docs/vr-preview-notepad.png" width="420">

A small desktop app plus an OpenXR layer. Add any app (say Hand Freeze, or a browser), and its window hangs in front
of your eyes inside the game like an in-game menu, about a metre away, turning with your head. You can add as many
windows as you like, make each one more or less see-through, drag it aside by its dark header and resize it by its
corner, all with the mouse. A switch, **MOUSE & KEYBOARD**, decides who gets your mouse and keyboard: normally the
game, as always; switched on, the windows in VR get them and the game sees nothing, so you play with the motion
controllers or the gamepad while you click and type in the floating windows. **Page Up** switches it on, **Page Down**
off, from anywhere, without the game hearing the key.

Made for tuning our VR mods from inside the headset (2026-10-16): with Hand Freeze holding the controllers still, the
sliders of the hand poser can now be moved without lifting the headset.

## Get it

Download the zip from [Releases](https://github.com/TefMeister/window-view-vr/releases), unzip it, right-click
`install.ps1` and choose **Run with PowerShell**. A **Window View** icon appears on the desktop; no administrator rights
are needed. Or build it yourself: `build.bat` (Visual Studio 2022 Build Tools for the layer; the app uses the C#
compiler that ships with Windows) then `install.ps1`. After an update, open the app once: it checks that the layer
registration still matches the files beside it (the 32-bit copy too) and mends it (v1.1, 2026-10-16, as Hand Freeze
v3.2; an old registration without the 32-bit copy stops 32-bit OpenXR games from starting).

## Use

1. Double-click **Window View** on the desktop and press **VR LAYER: OFF** so it says **ON**. Leave it on.
2. Start the game. The layer must be on **before** the game starts (OpenXR add-ons load at start); a game that was
   already running needs one restart. The bottom line says *Connected: <game>.exe (D3D11)* once the game draws.
3. Press **ADD AN APP...** and pick the app's .exe (or a shortcut to it), or simply drag the app or its shortcut onto
   the Window View window. If the app is not running, it is started. Its window appears in the headset, straight ahead,
   as soon as it opens, and again every time the app is opened later. Each added app gets a row under **IN VR** with
   its own icon, a **See-through** slider, a **Size** slider, a **Show** box and **REMOVE**.
4. Press **Page Up** (or the big button). Now your mouse moves a small arrow across the floating windows and your
   keyboard types into the one you last clicked. The game gets neither. Drag a window by its dark header to move it,
   drag the three little lines in its bottom-right corner to resize it.
5. Press **Page Down** to give the mouse and keyboard back to the game and the desktop.

Windows come back where you left them next time (matched by program and title), from
`%APPDATA%\WindowViewVR\layout.txt`.

## How it works

- **The layer** (`layer/window_view_layer.cpp`, an OpenXR API layer registered for this Windows user, 64-bit and a
  32-bit copy) sits between the game and the OpenXR runtime. At every `xrEndFrame` it appends one **quad layer** per
  shown window after the game's own layers, in a head-locked (`VIEW`) space, 1 m in front, blended by alpha. The
  pixels come from the app through shared memory; the layer copies a window's picture into a swapchain of its own only
  when it changed. D3D11 (`UpdateSubresource`) and D3D12 (upload buffer + copy on the game's queue) games are supported;
  a Vulkan or OpenGL game shows nothing and the app says so.
- **The app** (`app/*.cs`) does the rest: `Capture.cs` photographs each window about 30 times a second with
  `PrintWindow` (works when the game covers it), puts our header strip above the client area and bakes the opacity
  into the alpha; `Desk.cs` is the imaginary desk in metres with the cursor, the hit test and the layout file;
  `InputRouter.cs` holds two low-level hooks on their own thread: Page Up / Page Down are always swallowed, and while
  on, every mouse and keyboard event is swallowed too. The real cursor is parked at the middle of the screen, the
  hook reads how far the mouse *wanted* to move, and that becomes the desk cursor. Clicks, moves and the wheel are
  posted to the real control under the desk cursor as ordinary window messages; letters go as `WM_CHAR`, other keys
  as `WM_KEYDOWN`/`WM_KEYUP`. The game keeps the Windows focus the whole time, so controllers and the gamepad keep
  working. Closing the app always hands the mouse and keyboard back.
- `shared.h` is the hand-over: one 500-byte header (8 slots: where, how big, which pixel buffer is finished, plus the
  layer's heartbeat, the game's name and graphics API, counters) and one double-buffered BGRA pixel block per slot.

## Tested

On the dev PC with the OpenXR Simulator, 2026-10-16, `[verified-numerically 2026-10-16, n=1 each]`:

- `test/view_test.py` (a stand-in game, `test/xr_test_app.cpp`, and a stand-in app, `test/fake_window.py`): D3D11 and
  D3D12 both PASS: frames ended, the layer named the right API and exe, pictures uploaded, quads submitted, no error;
  a mid-run resize swapped to the new picture. Photos of the preview window show the test picture in both eyes.
- `test/input_test.py` (Notepad in VR through the layout file, the app started with `--test-input`, the real stand-in
  game): Notepad captured and shown, the real cursor stayed parked while on, `hello` typed into Notepad through the
  desk, the window dragged right by its header, the mouse moving again after Page Down. All six PASS.
- A game started with no app running keeps running and shows nothing.

First use in a real headset, home PC, 2026-10-24 (Tefa): two problems, both fixed in v1.2 the same evening, not yet
tried again:

- *"hand poser is not on the list"*: the list of open windows was read only when Window View started, and Hand Poser
  was opened later. It now refreshes by itself every two seconds.
- *"i need to somehow be able to know where my mouse is in vr"*: the cursor was drawn only while it was over a window,
  and could wander almost a metre off into empty space. Now it starts in the middle of the top window, stays within
  3 cm of the windows, is twice the size with a thick outline, and turns yellow on the nearest window's edge while it is
  between windows.

v1.2 worn the same evening, home PC, real headset (Tefa): *"this works really well now!"*

v1.3, 2026-10-25 (Tefa: *"instead of having a list of open apps to choose from, can we have an add window button
instead? ... or simply drag the shortcut to the app window and it would show up?"*): the list of every open window is
gone; apps are added by **ADD AN APP...** or by dropping an app or its shortcut on the window, started if they are not
running, and each row shows the app's own icon. The sliders are drawn by the app itself (the stock Windows slider
flickered white whenever a window was photographed), and the window's background is its new icon, a window with plants
on the sill and a sunny day outside, finely pixelated with the scan lines and rolling light of the Lanes theme. Tested
on the home PC: an app picked with **ADD AN APP...** was started and joined the list, and Hand Poser joined by itself when
it was opened. Dropping a shortcut is not yet tried by hand.

## Limits

- **OpenXR games only**, drawing with **D3D11 or D3D12** (all RE Engine games do). Vulkan and OpenGL come later.
- A game that reads the mouse through raw input may still see the mouse while the switch is on; most VR games do not
  care about the mouse at all.
- A program that checks the real mouse buttons instead of its messages may ignore clicks (none seen so far).
- Keyboard shortcuts with Ctrl or Alt go as key presses without the modifier state the program might check.
- Windows' pointer speed setting also scales the desk cursor.
- Games with anti-cheat may refuse OpenXR layers.

## Credit

Built on the same two-part shape as [Hand Freeze](https://github.com/TefMeister/hand-freeze-vr).
OpenXR headers in `third_party/openxr/` are from the Khronos Group's
[OpenXR-SDK](https://github.com/KhronosGroup/OpenXR-SDK) (release-1.1.43, Apache-2.0). The tests use the
[OpenXR Simulator](https://github.com/webhead2oo9/OpenXR-Simulator) by fholger, extended by elliotttate and
webhead2oo9 (MIT), the loader shipped with [pyopenxr](https://github.com/cmbruns/pyopenxr), and Pillow.
[OpenKneeboard](https://github.com/OpenKneeboard/OpenKneeboard) and Desktop+ showed years ago that windows belong in
the headset; this is a smaller tool for one job.

If you should be credited here and are not, open an issue on this repository and it will be fixed.
