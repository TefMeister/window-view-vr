// SelfTest.cs -- 1.12: checks of the keyboard's parts with no game, no headset and no window, for the build plan's
// tests: `WindowView.exe --selftest-keys [group]` prints one PASS / FAIL line per check and exits 0 only when all passed.
// Groups: keys-block, keymap, handkeys, typing, wiring (none = all). Nothing here touches the live shared blocks, real
// windows or real files: the keys block is opened under a test name, the log goes to a temp file.
using System;
using System.Collections.Generic;
using System.IO;

static partial class SelfTest {
    static int fails, passes;

    static void Check(string name, bool ok, string why = "") {
        if (ok) { passes++; Console.WriteLine("PASS " + name); }
        else { fails++; Console.WriteLine("FAIL " + name + (why != "" ? ": " + why : "")); }
    }

    public static int Run(string what) {
        var groups = new Dictionary<string, Action> {
            {"keys-block", KeysBlock},
            {"keymap", KeyMapChecks},
            {"handkeys", HandKeysChecks},
        };
        foreach (var g in groups) {
            if (what != "all" && what != g.Key) continue;
            try { g.Value(); } catch (Exception e) { Check(g.Key + " (no exception)", false, e.GetType().Name + ": " + e.Message); }
        }
        Console.WriteLine(fails == 0 ? "ALL PASS (" + passes + ")" : fails + " FAILED, " + passes + " passed");
        Console.Out.Flush();
        return fails == 0 && passes > 0 ? 0 : 1;
    }

    // ---- Task 1: the keys block, read back at the C++ offsets of shared.h ---------------------------------------------
    static void KeysBlock() {
        using (var k = new KeysShared(@"Local\FlatToVR_WindowView_keys_selftest")) {
            Check("keys block is 192 bytes", KeysShared.SIZE == 192);
            Check("magic and version written", k.RawInt(0) == unchecked((int)0x5359454Bu) && k.RawInt(4) == 1);
            k.WriteLit(1, true, 3, new V3(0.1f, 0.2f, -1f), 0.04f, 0.03f);
            int h1 = KeysShared.HAND0 + KeysShared.HAND_SIZE;
            Check("lit written for the right hand at its offsets",
                k.RawInt(h1 + KeysShared.H_LIT_VISIBLE) == 1 && k.RawInt(h1 + KeysShared.H_LIT_SLOT) == 3 &&
                k.RawFloat(h1 + KeysShared.H_LIT_C + 8) == -1f && k.RawFloat(h1 + KeysShared.H_LIT_W) == 0.04f && k.RawFloat(h1 + KeysShared.H_LIT_H) == 0.03f);
            Check("the left hand untouched", k.RawInt(KeysShared.HAND0 + KeysShared.H_LIT_VISIBLE) == 0);
            int s0 = k.RawInt(h1 + KeysShared.H_PRESS_SEQ);
            k.WritePress(1, 2, new V3(0, 0, -1), 0.04f, 0.03f);
            int s1 = k.RawInt(h1 + KeysShared.H_PRESS_SEQ);
            k.WritePress(1, 2, new V3(0, 0, -1), 0.04f, 0.03f);
            int s2 = k.RawInt(h1 + KeysShared.H_PRESS_SEQ);
            Check("press_seq counts each press", s1 == s0 + 1 && s2 == s0 + 2);
            Check("a press is ok until marked lost", k.RawInt(h1 + KeysShared.H_PRESS_OK) == 1 && k.RawInt(h1 + KeysShared.H_PRESS_SLOT) == 2);
            k.MarkLost(1);
            Check("MarkLost clears ok and keeps the seq", k.RawInt(h1 + KeysShared.H_PRESS_OK) == 0 && k.RawInt(h1 + KeysShared.H_PRESS_SEQ) == s2);
            k.RawWriteFloat(KeysShared.LTRIGGER, 0.42f);
            Check("left trigger read from its offset", k.LTrigger == 0.42f);
            k.WriteLeftCursor(true, new V3(1, 2, 3));
            Check("left cursor written", k.RawInt(KeysShared.HAND0 + KeysShared.H_CURSOR_VISIBLE) == 1 && k.RawFloat(KeysShared.HAND0 + KeysShared.H_CURSOR_P + 4) == 2f);
        }
    }

    // ---- Task 2: the key table and the hit test ------------------------------------------------------------------------
    static void KeyMapChecks() {
        var t = new[] { new KeyRect { X = 8, Y = 8, W = 46, H = 40, Vk = 'Q' }, new KeyRect { X = 58, Y = 8, W = 46, H = 40, Vk = 'W' } };
        KeyRect k;
        Check("a point on Q finds Q", KeyMap.Find(t, 30, 8 + Capture.HEADER + 20, out k) && k.Vk == 'Q');
        Check("a point on W finds W", KeyMap.Find(t, 80, 56, out k) && k.Vk == 'W');
        Check("the gap between keys finds nothing", !KeyMap.Find(t, 56, 56, out k));
        Check("the header strip finds nothing", !KeyMap.Find(t, 30, 20, out k));
        using (var ui = new KeysUi(null)) {
            var table = ui.KeyTable;
            Check("the keyboard's table has all 61 keys", table.Length == 61, "got " + table.Length);
            bool q = false; foreach (var r in table) if (r.Vk == 'Q' && r.W == 46 && r.H == 40) q = true;
            Check("Q is in the table at key size", q);
            bool s, c;
            ui.ToggleShift(); ui.TakeMods(out s, out c);
            bool s2, c2; ui.TakeMods(out s2, out c2);
            Check("Shift is one-shot", s && !c && !s2 && !c2);
            ui.ToggleCtrl(); ui.ToggleCtrl(); ui.TakeMods(out s, out c);
            Check("Ctrl pressed twice is off again", !c);
        }
    }

    // ---- Task 3: one hand's press detector -------------------------------------------------------------------------------
    static readonly KeyRect[] QW = { new KeyRect { X = 8, Y = 8, W = 46, H = 40, Vk = 'Q' }, new KeyRect { X = 58, Y = 8, W = 46, H = 40, Vk = 'W' } };
    const float QX = 31, WX = 81, KY = 8 + Capture.HEADER + 20;
    // steps one hand through (trigger, A, x) samples on the keyboard; returns the keys pressed, as a string
    static string Pulls(HandKeys h, params object[] samples) {
        string got = ""; int t = 0;
        for (int i = 0; i < samples.Length; i += 3) {
            var e = h.Step((float)(double)samples[i], (bool)samples[i + 1], true, (float)(double)samples[i + 2], KY, QW, t += 11);
            if (e.Pressed) got += (char)e.Key.Vk;
        }
        return got;
    }
    static void HandKeysChecks() {
        Check("(a) a light pull to 0.4 presses the key", Pulls(new HandKeys(1), 0.0, false, (double)QX, 0.4, false, (double)QX) == "Q");
        Check("(b) a pull to 0.3 presses nothing", Pulls(new HandKeys(1), 0.0, false, (double)QX, 0.3, false, (double)QX, 0.0, false, (double)QX) == "");
        Check("(c) wobbling above 0.2 presses once", Pulls(new HandKeys(1), 0.0, false, (double)QX, 0.5, false, (double)QX, 0.3, false, (double)QX,
              0.5, false, (double)QX, 0.36, false, (double)QX) == "Q");
        Check("(d) the key touched first is the one pressed", Pulls(new HandKeys(1), 0.0, false, (double)QX, 0.15, false, (double)QX,
              0.15, false, (double)WX, 0.5, false, (double)WX) == "Q");
        Check("(e) A twice is two presses", Pulls(new HandKeys(1), 0.0, true, (double)WX, 0.0, false, (double)WX, 0.0, true, (double)WX, 0.0, false, (double)WX) == "WW");
        var l = new HandKeys(0); var r = new HandKeys(1);
        l.Step(0, false, true, QX, KY, QW, 0); r.Step(0, false, true, QX, KY, QW, 0);
        var el = l.Step(0.4f, false, true, QX, KY, QW, 11); var er = r.Step(0.4f, false, true, QX, KY, QW, 11);
        Check("(f) both hands on Q at once: two presses, hands 0 and 1", el.Pressed && er.Pressed && el.Hand == 0 && er.Hand == 1 && el.Key.Vk == 'Q' && er.Key.Vk == 'Q');
        var g = new HandKeys(1);
        g.Step(0, false, true, QX, KY, QW, 0); g.Step(0.5f, false, true, QX, KY, QW, 11);
        g.Reset();
        bool litAfterReset = g.Lit;
        var eg = g.Step(0.5f, false, true, QX, KY, QW, 22);
        var eg2 = g.Step(0.6f, false, true, QX, KY, QW, 33);
        Check("(g) Reset clears the light, and a trigger still held presses nothing", !litAfterReset && !eg.Pressed && !eg2.Pressed);
        g.Step(0.1f, false, true, QX, KY, QW, 44);
        Check("(g) once let go, the next pull presses again", g.Step(0.5f, false, true, QX, KY, QW, 55).Pressed);
        var lit = new HandKeys(1); lit.Step(0, false, true, WX, KY, QW, 0);
        Check("the key under the pointer is lit", lit.Lit && lit.LitKey.Vk == 'W');
        var off = new HandKeys(1); var eo = off.Step(0.9f, false, false, QX, KY, QW, 0);
        Check("off the keyboard nothing is lit or pressed", !eo.Pressed && !off.Lit);
        Check("a resting finger at 0.34-0.36 presses once", Pulls(new HandKeys(1), 0.0, false, (double)QX, 0.36, false, (double)QX, 0.34, false, (double)QX,
              0.36, false, (double)QX, 0.34, false, (double)QX, 0.36, false, (double)QX) == "Q");
    }
}
