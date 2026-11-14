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
}
