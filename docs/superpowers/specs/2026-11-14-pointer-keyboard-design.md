# Window View 1.12: a pointer keyboard that feels instant (design)

Date: 2026-11-14 (Developer PC, Opus). Status: **waiting for Tefa's yes.**

## Why

Tefa types to Claude Code sessions (Windows Terminal) from inside the headset, through Window View's own keyboard
(the "Keyboard" row, `app/KeysUi.cs`, since 1.7). Tefa, 2026-11-14: *"the claude window typing is only useful if the
keyboard used is instant, even with all your speed ups, it is still too slow to react to presses sometimes - what would
be amazing is an actual keyboard i can use my motion controller pointers in to type - like the one VD uses"*. Asked what
"slow" looks like: *"some presses don't register at all, others show up late"*.

The model to match is Virtual Desktop's keyboard: the key under the pointer lights up at once, a press is felt in the
controller at once, and no press is ever lost.

## Goals

1. **No lost presses.** Every trigger pull (or A press) over a key types that key, once.
2. **No late presses.** A key reaches the terminal within a frame or two of the press, also right after a pause.
3. **Instant feedback, drawn by the headset side every frame:** the key under the right pointer is lit; a pressed key
   flashes; the right controller gives a short buzz on every press that counted.
4. A press log, so a press that is ever lost again can be traced to where it vanished.

## Non-goals

- No new key layout, no new keys, no word suggestions, no voice typing. The keys stay exactly as 1.11 has them.
- No change to how the other windows are clicked, dragged, scrolled or carried.
- No change for typing with the real keyboard (MOUSE & KEYBOARD on).

## Anti-goals (a failure even if it works)

- The keyboard taking the Windows focus from the game, or the game's controllers stopping.
- Anything that makes the captured windows repaint more (Tefa's rule: helper windows stay still, since every repaint
  costs VR performance). The light-up and flash are drawn by the layer only, never into the keyboard window.
- A key typed twice from one pull.

## What is wrong today (read from the code, 2026-11-14; not yet measured)

- **Lost:** a press counts only past 60 % trigger (`Pointer.cs` TRIGGER_DOWN 0.6), so a quick light pull can fall
  short. A press travels: pointer loop → a posted mouse-down to the WinForms button → its UI thread → `TypeKey` →
  `ConsoleInput.Type`; when that last step cannot attach to the terminal program's console it returns false and the key
  is silently dropped (only `LastAction` says so). Nothing on screen shows whether a press counted.
- **Late:** the program behind the terminal window is cached for only 2 s (`ConsoleInput` CACHE_MS); the first key after
  that looks it up again by attaching to the console of every program the terminal runs, one by one, on the keyboard's
  UI thread, so presses queue behind it and then land in a burst.
- **Slow feedback:** the keyboard and the terminal both reach the headset as pictures (about 30 a second), and the keys
  never light up, so the only sign of a press is the letter arriving in the terminal.

## Approaches

- **A. Mend the existing keyboard only** (threshold, warm lookup, typing thread, log). Small; no instant light-up.
- **B. A new keyboard drawn entirely by the layer**, keys and all, like VD's. Most like VD; biggest job; the keyboard's
  look moves into the C++ layer where it is slow to change.
- **C. Keep the keyboard window, let the layer draw the instant parts on top, plus A's fixes.** Nearly B's feel for about
  half the work; the layout stays in C#.

**Chosen: C** (Tefa 2026-11-14: *"c please yes it sounds good"*).

**Both hands type** (Tefa 2026-11-14: *"both hands please, like VD"*): on the keyboard, the LEFT controller is a second
pointer with its own light-up, press and buzz, exactly like the right. Everything below that says "the pointer" means
each hand's own, independently; two keys can be lit at once, and presses from both hands go into the one typing queue
in the order they happened. Off the keyboard the left hand keeps doing what it does in 1.11 (its grip carries a window,
its stick pushes and pulls it); its ray points at nothing there and draws no arrow.

## Design

### 1. Press detection moves out of WinForms (app, `Pointer.cs` + `KeysUi.cs`)

- `KeysUi` publishes its key table: each key's rectangle in the window's client pixels plus its key code (it already has
  them when it lays the buttons out). The pointer loop (250 a second) turns the right ray's spot on the keyboard's picture
  into "the key under the pointer" by that table, without asking WinForms.
- While a pointer is on the keyboard, a press is detected right there: that hand's trigger past **0.35** (let go below
  **0.2**), or A for the right hand. One press = one key, typed on the press, never on release. A left grip that starts
  while the left pointer is on the keyboard still carries the keyboard window, as today. The 1.8 rule stays: from the first touch of the trigger
  the pointer holds still, so the tilt of a pull does not slide it onto the next key.
- The press is put on a **typing queue** (below) and its key is reported to the layer (section 3). Shift and Ctrl stay
  one-shot toggles exactly as today, kept in `KeysUi`, shown in the window as today (a one-time repaint when they change).
- On every other window the trigger works exactly as in 1.11 (0.6 / 0.4, posted mouse messages).

### 2. Typing on its own thread, with the program kept warm (app, `InputRouter.cs` + `ConsoleInput.cs`)

- A **typing thread** takes keys from the queue in order. Keys that piled up go into the console in one
  `WriteConsoleInput` call, so a burst cannot reorder or drop.
- The terminal program behind the target window is **looked up in the background** (every 2 s and whenever the target
  window or its title changes), never on the typing path. A press uses the last good answer.
- If a write fails, the program is looked up again at once and the key is retried **once**; if it still fails, the press
  is logged as lost with the reason, and the key's flash turns **red** instead of green (section 3).
- Windows that are not terminals still get posted messages as today, from the typing thread.

### 3. The instant parts, drawn by the layer (`shared.h`, `layer/window_view_layer.cpp`, `layer/controllers.inl`)

New fields, in a **second, separate shared block** `Local\FlatToVR_WindowView_keys_v1` (`WindowViewKeys` in
`shared.h`). Changed while planning, 2026-11-14: the main block `Local\FlatToVR_WindowView_v2` is also opened by name
and size by Hand Poser, Grip Zones, Gun Poser, Animation & Timing (`Nudge.cs`) and the Village mod (`sleephands.cpp`), so
renaming or growing it would break all of them. The main block stays exactly as it is. The new block holds:

- `ltrigger`: the LEFT trigger 0..1, written by the layer (today only the left grip and stick are shared; the layer's
  action set gains a left trigger action).
- per hand (left and right): `key_slot`, `key_rect[4]` (u0, v0, u1, v1 on that window's picture), `key_lit`: the key
  under that hand's pointer, written by the app's pointer loop; and `lcursor_visible` / `lcursor_p[3]` so the layer draws
  the left hand's arrow while it is on the keyboard.
- per hand: `press_seq`, `press_rect[4]`, `press_ok`: +1 per press, where, and whether it reached the program (0 = lost).

The layer, every frame, for each hand:

- draws a translucent light rectangle over that hand's `key_rect` on the keyboard's quad (one more small quad, same
  space and depth as the window, a hair in front, like the pointer arrow);
- when that hand's `press_seq` moves: a short bright flash over `press_rect` (green, about 120 ms; red when `press_ok`
  is 0) and one short **haptic pulse on THAT hand's controller** through new output actions in Window View's own action
  set (`/user/hand/left/output/haptic`, `/user/hand/right/output/haptic`, `xrApplyHapticFeedback`, about 20 ms, medium
  strength).

These quads are tiny and not captured, so no window repaints because of them.

### 4. The press log (app)

`%APPDATA%\WindowViewVR\keys.log`: one line per press: time of the trigger crossing, key, target program, time it was
written, result (typed / retried / lost + why). Capped (the newest 2,000 lines). Read when Tefa reports a lost press.

## Constraints

- Windows Terminal ignores posted keys: terminals are typed through their program's console (1.7.1), kept.
- The game keeps the Windows focus all the time (controllers keep working); the keyboard stays `WS_EX_NOACTIVATE`.
- Both the 64-bit and the 32-bit layer change together; the app mends the layer registration on start as today.
- The haptic action must not disturb the game's own haptics: it lives only in Window View's action set, used only while
  the controllers work the windows (CONTROL mode).
- Village on Virtual Desktop is the first real target; the OpenXR Simulator plus VR Bench for the automated tests.

## How it is tested

- **On the simulator, no person:** `test/view_test.py` (D3D11, D3D12) still passes; a new `test/keys_test.py`: VR Bench
  aims the right controller at chosen keys and pulls the trigger (light pulls at 0.4 included, and fast runs), typing into
  a **throwaway** Windows Terminal window running a small echo script (never a real Claude session, never a real file);
  both hands, one after the other and interleaved (left, right, left, ...).
  Pass = every key arrived, once, in order; the log shows each press; the time from pull to arrival is printed (goal: under
  50 ms each, also for the first key after a 5 s pause). The layer's counters say a highlight and a flash were drawn and the
  haptic call returned success (the simulator cannot be felt).
- **In the headset, Tefa:** type a few sentences into a Claude window; say whether any press was lost or late, and whether
  the light-up and the buzz feel right.

## Left to the builder

- The exact highlight and flash colours and see-through amounts (start: light white at 25 %, flash green / red at 60 %).
- Where in `KeysUi` the key table is built and how it is shared with the pointer thread (a snapshot array is enough).
- The haptic pulse's exact length and strength within "short and medium" (start 20 ms, amplitude 0.5).
