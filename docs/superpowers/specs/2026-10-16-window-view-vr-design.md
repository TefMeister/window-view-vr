# Window View VR: design (2026-10-16)

## Why

Tefa tunes hand and weapon poses in VR mods with the controllers frozen (Hand Freeze), dragging sliders with the mouse
on the flat monitor while peeking under the headset. They asked (2026-10-16):

> "a tool that lets me see any window from my pc in vr - so that i can choose a specific window i want to be present
> in vr, set it's opacity and freely use mouse to click things in that window, without the mouse and keyboard
> affecting the game, so the game can only be operated by motion controllers or the gamepad" ... "but it has to be
> openxr"

First uses: Hand Freeze's window, and later the stand-alone hand poser (`hand-poser-vr`).

## What Tefa decided

- **OpenXR**, any OpenXR game.
- Windows **move with the head**, in front of the eyes "like an in-game menu window little further from my eyes";
  no 3D room placement needed.
- **Move a window by grabbing its header** with the mouse, like a normal window. **Resize** them too.
- **As many windows as needed**, each with its own see-through amount (opacity).
- **Mouse and keyboard to the windows can be switched on and off**: a button at the top of the tool's window,
  **Page Up** turns it on, **Page Down** turns it off. While on, the game gets no mouse and no keyboard, so it is
  played with the controllers or the gamepad.
- Public repo; the description says it is untested.

## Shape (builder's calls, marked)

Two parts, the same split as Hand Freeze (`hand-freeze-vr`), which this copies wherever it can:

1. **The app** (`WindowView.exe`, C# WinForms on .NET Framework 4, built with the `csc.exe` that ships with Windows).
   It does all the thinking: the window list, capture, the desk layout, the cursor, input capture and forwarding, and
   saving the layout. *(call: keeps the layer simple and the logic testable without a game)*
2. **The OpenXR layer** (`window_view_layer.dll`, C++, 64-bit and a 32-bit copy, registered per user like Hand
   Freeze's). It only shows what the app hands it: one head-locked quad layer per window, appended after the game's
   own layers in `xrEndFrame`.

### Hand-over between them (call)

Shared memory: one header block (`Local\FlatToVR_WindowView_v1`) with up to 8 slots (visible, position, size in
metres, pixel size, frame counter, which of two pixel buffers is current), and one pixel mapping per slot holding two
BGRA buffers (double buffered). The layer copies the current buffer only when the frame counter changed, and skips a
copy whose counter moved while it was reading. The layer writes a heartbeat, the game's exe name and its graphics API
back into the header, so the app can say "Connected: re2.exe (D3D12)".

*Why CPU pixels and not shared GPU textures:* works the same for D3D11 and D3D12 games, needs no shared handles, and a
1280 x 800 window at 30 frames a second is about 120 MB/s of copying, which is fine.

### Showing (layer)

- On `xrCreateSession`, note the graphics binding: **D3D11 and D3D12** are supported; Vulkan and OpenGL show nothing
  and report "not supported yet" *(call: RE Engine games are D3D11/D3D12)*.
- One swapchain per slot (sRGB BGRA preferred, RGBA with a byte swap otherwise), made lazily, remade when the pixel
  size changes. Upload: D3D11 `UpdateSubresource` on the immediate context; D3D12 an upload buffer, copy on the
  game's queue with barriers RENDER_TARGET -> COPY_DEST -> RENDER_TARGET and a fence.
- Quads live in a VIEW reference space the layer makes per session (head-locked), at the slot's x/y and 1 m in front
  *(call)*, with source-alpha blending (unpremultiplied); opacity is baked into the pixels' alpha by the app.

### The desk (app)

- An imaginary flat desk 1 m in front of the eyes. Each window sits on it at a position (metres) with a scale
  (metres per pixel, default 0.0006 *(call)*), so a 600-pixel-wide window is 36 cm wide at 1 m.
- The app draws a **header strip** (28 px *(call)*) above each captured window, with its title, and a **resize grip**
  in the bottom-right corner. Resizing changes the scale, not the real window *(call: keeps the real window intact)*.
- The **cursor** is drawn by the app into whichever window it is over, only while mouse and keyboard are on.
- Layout saved to `%APPDATA%\WindowViewVR\layout.txt` and put back next time; windows are found again by exe name
  and title *(call; Tefa agreed to "windows come back where you left them")*.

### Capture (app)

`PrintWindow(hwnd, PW_RENDERFULLCONTENT)` into a 32-bit bitmap, about 30 times a second per window *(call)*. Works for
windows covered by the game; a minimised window shows its last picture.

### Mouse and keyboard (app)

- A low-level keyboard hook (own thread) always watches **Page Up / Page Down** and swallows them, so the game never
  gets them *(call; explained to Tefa: the tool still hears them)*.
- **While on:** a low-level mouse hook swallows every mouse event (so the cursor and the game do not move) and turns
  the would-be movement into cursor movement on the desk. Header drag moves a window, grip drag resizes it, anything
  else is posted to the real window under the cursor as mouse messages (the child control under the point, with
  client coordinates; a button-down keeps sending to the same control until button-up, so slider drags work).
  The keyboard hook swallows keys and posts them to the control last clicked: `WM_CHAR` for characters,
  `WM_KEYDOWN/UP` for the rest.
- The game keeps focus the whole time, so it keeps reading controllers and gamepad.
- Safety: closing the app always turns input back on for the game. *(call)*

### The app window

Like Hand Freeze in spirit, but plain to start *(call: looks get tweaked later with Tefa)*: the layer ON/OFF switch,
a big **MOUSE & KEYBOARD: ON / OFF** button at the top, the connected game, a list of open windows to **Add**, and
one row per added window with an opacity slider, a size slider, show/hide and **Remove**.

## Not now

Placing windows in the room, OpenVR games, Vulkan/OpenGL games, laser-pointer clicking with the controllers.

## Known risks `[hypothesis]` until tested

- Some programs ignore posted mouse messages (they check the real mouse state). Then that program needs a different
  route.
- A game reading the mouse through a route the low-level hook does not cover would still see it.
- Anti-cheat games may refuse OpenXR layers.

## Testing

On the dev PC with the OpenXR Simulator (it composites quad layers for D3D11 and D3D12 and can screenshot them): a
small D3D11 and D3D12 OpenXR test program with the layer enabled for that process only, a test window, and a check
that the quad shows the window's pixels. Then Tefa in the headset with Hand Freeze.
