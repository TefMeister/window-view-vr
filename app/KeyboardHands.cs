// KeyboardHands.cs -- 1.12: both hands on Window View's keyboard (Tefa 2026-11-14: "both hands please, like VD"). The
// pointer loop calls Step for each hand on every new controller frame with the window under that hand's ray; when that
// window is the keyboard, this finds the key (KeyMap), catches the press (HandKeys), tells the layer what to light, flash
// and buzz (KeysShared), and puts the key on the typing thread (TypingQueue). Shift and Ctrl toggle the keyboard's one-shot
// modifiers instead of typing. Off the keyboard it lights nothing and leaves the hand to 1.11's ways.
using System;

class KeyboardHands {
    readonly KeysShared keys;
    readonly TypingQueue queue;
    readonly KeysUi ui;
    readonly HandKeys[] hands = { new HandKeys(KeysShared.LEFT), new HandKeys(KeysShared.RIGHT) };
    readonly bool[] wasOn = new bool[2];

    public KeyboardHands(KeysShared keys, TypingQueue queue, KeysUi ui) { this.keys = keys; this.queue = queue; this.ui = ui; }
    public float LTrigger { get { return keys.LTrigger; } }   // written by the layer (the main block has the right trigger only)
    public KeysShared Keys { get { return keys; } }   // 1.13: the pointer loop writes the left dot and the lasers through it

    public static bool IsKeyboard(Placement p) {
        return p != null && p.Title == KeysUi.TITLE && p.Shown && p.Live && p.PicW > 0 && p.PicH > 0 && p.Slot >= 0;
    }

    // one hand, one controller frame: `p` the window under its ray (null = none) and (u, v) on it. True = the pointer is on
    // the keyboard, so the caller leaves this hand's 1.11 click alone (a posted click would type the key a second time).
    public bool Step(int hand, Placement p, float u, float v, float distance, bool room, float trigger, bool a, int nowMs) {
        var hk = hands[hand];
        if (!IsKeyboard(p)) {
            if (wasOn[hand]) hk.Reset();   // slid off, or the keyboard was hidden: a trigger still held types nothing later
            wasOn[hand] = false;
            keys.WriteLit(hand, false, -1, new V3(), 0, 0);
            return false;   // 1.13: the left pointer off the keyboard is the pointer loop's (Pointer.cs) to draw
        }
        wasOn[hand] = true;
        var frame = WindowFrame.Of(p, distance, room);
        var ev = hk.Step(trigger, a, true, u * p.PicW, v * p.PicH, ui.KeyTable, nowMs);
        if (hk.Lit) { V3 c; float w, h; Place(p, frame, hk.LitKey, out c, out w, out h); keys.WriteLit(hand, true, p.Slot, c, w, h); }
        else keys.WriteLit(hand, false, -1, new V3(), 0, 0);
        if (hand == KeysShared.LEFT) keys.WriteLeftCursor(true, frame.PointAt(u, v), p.Slot);
        if (ev.Pressed) {
            V3 c; float w, h; Place(p, frame, ev.Key, out c, out w, out h);
            if (ev.Key.Vk == KeysUi.ShiftVk) ui.ToggleShift();
            else if (ev.Key.Vk == KeysUi.CtrlVk) ui.ToggleCtrl();
            else {
                bool s, ct;
                ui.TakeMods(out s, out ct);
                queue.Add(new KeyStroke { Vk = ev.Key.Vk, Shift = s, Ctrl = ct, Hand = hand, PressMs = ev.AtMs });
            }
            keys.WritePress(hand, p.Slot, c, w, h);
        }
        return true;
    }

    // CONTROL mode went off: nothing stays held or lit
    public void Release() {
        for (int i = 0; i < 2; ++i) { hands[i].Reset(); wasOn[i] = false; keys.WriteLit(i, false, -1, new V3(), 0, 0); }
        keys.WriteLeftCursor(false, new V3());
    }

    // a key's centre on the keyboard's quad (the desk's frame) and its size in metres
    static void Place(Placement p, WindowFrame f, KeyRect k, out V3 centre, out float w, out float h) {
        float cu = (k.X + k.W / 2f) / p.PicW, cv = (k.Y + Capture.HEADER + k.H / 2f) / p.PicH;
        centre = f.PointAt(cu, cv);
        w = f.W * k.W / p.PicW;
        h = f.H * k.H / p.PicH;
    }
}
