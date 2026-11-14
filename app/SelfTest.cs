// SelfTest.cs -- 1.12: checks of the keyboard's parts with no game, no headset and no window, for the build plan's
// tests: `WindowView.exe --selftest-keys [group]` prints one PASS / FAIL line per check and exits 0 only when all passed.
// Groups: keys-block, keymap, handkeys, typing, wiring (none = all). Nothing here touches the live shared blocks, real
// windows or real files: the keys block is opened under a test name, the log goes to a temp file.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

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
            {"typing", TypingChecks},
            {"wiring", WiringChecks},
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

    // ---- Task 4: the typing thread, the warm lookup and the press log ------------------------------------------------------
    // a stand-in for the terminal: records every call; can be held busy, or made to fail
    class FakeSink : IKeySink {
        public readonly List<string> Writes = new List<string>();
        public readonly List<IntPtr> Refreshes = new List<IntPtr>();
        public readonly List<KeyStroke> Strokes = new List<KeyStroke>();
        public int FailNext;            // this many writes fail, then they work (int.MaxValue = always)
        public ManualResetEvent Gate = new ManualResetEvent(true);   // reset = the next write waits here
        public ManualResetEvent Entered = new ManualResetEvent(false);
        public bool Write(IntPtr target, KeyStroke[] keys, out string why) {
            Entered.Set();
            Gate.WaitOne(5000);
            var sb = new System.Text.StringBuilder();
            foreach (var k in keys) { sb.Append((char)k.Vk); lock (Strokes) Strokes.Add(k); }
            lock (Writes) Writes.Add(sb.ToString());
            if (FailNext > 0) { FailNext--; why = "the stand-in refused"; return false; }
            why = ""; return true;
        }
        public void Refresh(IntPtr target) { lock (Refreshes) Refreshes.Add(target); }
    }
    static KeyStroke Ks(char c, int hand) { return new KeyStroke { Vk = c, Hand = hand, PressMs = Environment.TickCount }; }
    static string TempLog() { string f = Path.Combine(Path.GetTempPath(), "wv_keys_selftest_" + Guid.NewGuid().ToString("N") + ".log"); return f; }

    static void TypingChecks() {
        // (a) + (b): a burst while the terminal is busy goes in ONE write, in the order pressed (left, right, left)
        var sink = new FakeSink(); var lost = new List<int>(); string log = TempLog();
        using (var q = new TypingQueue(sink, () => new IntPtr(42), h => { lock (lost) lost.Add(h); }, log)) {
            sink.Gate.Reset();
            q.Add(Ks('X', 1));
            sink.Entered.WaitOne(2000);
            q.Add(Ks('A', 0)); q.Add(Ks('B', 1)); q.Add(Ks('C', 0));
            sink.Gate.Set();
            q.Flush(2000);
            string w; lock (sink.Writes) w = string.Join("|", sink.Writes.ToArray());
            Check("(a) a burst while busy goes in one write", w == "X|ABC", "writes: " + w);
            Check("(b) left / right / left keep their order", w.EndsWith("ABC"));
        }
        // (c) a write that fails once: one fresh lookup, then it works; logged as retried
        sink = new FakeSink { FailNext = 1 }; log = TempLog(); lost.Clear();
        using (var q = new TypingQueue(sink, () => new IntPtr(42), h => { lock (lost) lost.Add(h); }, log)) {
            int r0; lock (sink.Refreshes) r0 = sink.Refreshes.Count;
            q.Add(Ks('Q', 1)); q.Flush(2000);
            int r1; lock (sink.Refreshes) r1 = sink.Refreshes.Count;
            Check("(c) a failed write is looked up again and retried once", sink.Writes.Count == 2 && r1 - r0 >= 1 && lost.Count == 0, "writes " + sink.Writes.Count + ", refreshes " + (r1 - r0) + ", lost " + lost.Count);
        }
        Check("(c) the log says retried", File.ReadAllText(log).Contains("result=retried"));
        // (d) a write that always fails: the press is lost, its hand told, the log says why
        sink = new FakeSink { FailNext = int.MaxValue }; log = TempLog(); lost.Clear();
        using (var q = new TypingQueue(sink, () => new IntPtr(42), h => { lock (lost) lost.Add(h); }, log)) { q.Add(Ks('Z', 0)); q.Flush(2000); }
        string lt = File.ReadAllText(log);
        Check("(d) a lost press tells its hand", lost.Count == 1 && lost[0] == 0);
        Check("(d) the log says lost and why", lt.Contains("result=lost") && lt.Contains("why=the stand-in refused") && lt.Contains("hand=L") && lt.Contains("key=Z"));
        // (e) the log keeps the newest 2,000 lines once it passes 2,500
        log = TempLog();
        var old = new string[2600]; for (int i = 0; i < old.Length; ++i) old[i] = "old line " + i;
        File.WriteAllLines(log, old);
        sink = new FakeSink();
        using (var q = new TypingQueue(sink, () => new IntPtr(42), h => { }, log)) { q.Add(Ks('N', 1)); q.Flush(2000); }
        var lines = File.ReadAllLines(log);
        Check("(e) the log is trimmed to the newest 2,000 lines", lines.Length == 2000 && lines[lines.Length - 1].Contains("key=N"), "lines " + lines.Length);
        // (f) a new target is looked up in the background, before any key is typed to it
        sink = new FakeSink(); IntPtr tgt = new IntPtr(42);
        using (var q = new TypingQueue(sink, () => tgt, h => { }, TempLog())) {
            Thread.Sleep(300);
            tgt = new IntPtr(43);
            Thread.Sleep(400);
            bool seen; lock (sink.Refreshes) seen = sink.Refreshes.Contains(new IntPtr(43));
            Check("(f) a changed target is looked up in the background", seen && sink.Writes.Count == 0);
        }
    }

    // ---- Task 5: both hands wired: the keyboard step the pointer loop calls for each hand -----------------------------
    static void WiringChecks() {
        using (var ui = new KeysUi(null))
        using (var keys = new KeysShared(@"Local\FlatToVR_WindowView_keys_selftest_wiring")) {
            var sink = new FakeSink();
            using (var q = new TypingQueue(sink, () => new IntPtr(42), keys.MarkLost, TempLog())) {
                ui.Queue = q;
                var kb = new KeyboardHands(keys, q, ui);
                var p = new Placement { Hwnd = new IntPtr(7), Title = KeysUi.TITLE, PicW = ui.ClientSize.Width, PicH = ui.ClientSize.Height + Capture.HEADER, Slot = 2, Scale = 0.001f };
                float qu, qv, wu, wv, su, sv;
                Uv(ui, p, 'Q', out qu, out qv); Uv(ui, p, 'W', out wu, out wv); Uv(ui, p, KeysUi.ShiftVk, out su, out sv);
                int r = KeysShared.HAND0 + KeysShared.HAND_SIZE, l = KeysShared.HAND0;
                int seq0 = keys.RawInt(r + KeysShared.H_PRESS_SEQ);
                bool on = kb.Step(KeysShared.RIGHT, p, qu, qv, 1f, false, 0f, false, 0);
                kb.Step(KeysShared.RIGHT, p, qu, qv, 1f, false, 0.4f, false, 11);
                q.Flush(2000);
                Check("the right hand on the keyboard says so (no 1.11 click)", on);
                Check("the right hand's pull types Q", Typed(sink) == "Q", Typed(sink));
                Check("the right hand's press is flashed", keys.RawInt(r + KeysShared.H_PRESS_SEQ) == seq0 + 1 && keys.RawInt(r + KeysShared.H_PRESS_SLOT) == 2);
                Check("the right hand's key is lit", keys.RawInt(r + KeysShared.H_LIT_VISIBLE) == 1 && keys.RawFloat(r + KeysShared.H_LIT_W) > 0.04f);
                kb.Step(KeysShared.RIGHT, p, qu, qv, 1f, false, 0f, false, 22);
                kb.Step(KeysShared.LEFT, p, wu, wv, 1f, false, 0f, false, 33);
                kb.Step(KeysShared.LEFT, p, wu, wv, 1f, false, 0.4f, false, 44);
                q.Flush(2000);
                Check("the left hand's pull types W", Typed(sink) == "QW", Typed(sink));
                Check("the left hand's arrow is shown on the keyboard", keys.RawInt(l + KeysShared.H_CURSOR_VISIBLE) == 1);
                kb.Step(KeysShared.LEFT, p, wu, wv, 1f, false, 0f, false, 55);
                kb.Step(KeysShared.RIGHT, p, su, sv, 1f, false, 0.4f, false, 66); kb.Step(KeysShared.RIGHT, p, su, sv, 1f, false, 0f, false, 77);
                kb.Step(KeysShared.RIGHT, p, qu, qv, 1f, false, 0.4f, false, 88); kb.Step(KeysShared.RIGHT, p, qu, qv, 1f, false, 0f, false, 99);
                kb.Step(KeysShared.RIGHT, p, wu, wv, 1f, false, 0.4f, false, 111); kb.Step(KeysShared.RIGHT, p, wu, wv, 1f, false, 0f, false, 122);
                q.Flush(2000);
                KeyStroke[] st; lock (sink.Strokes) st = sink.Strokes.ToArray();
                Check("Shift is not typed itself, it shifts the next key only", Typed(sink) == "QWQW" && st[2].Shift && !st[3].Shift, Typed(sink));
                kb.Step(KeysShared.RIGHT, p, qu, qv, 1f, false, 0.5f, false, 133);
                kb.Release();
                Check("CONTROL off: both lights and the left arrow are off", keys.RawInt(r + KeysShared.H_LIT_VISIBLE) == 0 && keys.RawInt(l + KeysShared.H_LIT_VISIBLE) == 0 && keys.RawInt(l + KeysShared.H_CURSOR_VISIBLE) == 0);
                kb.Step(KeysShared.RIGHT, p, qu, qv, 1f, false, 0.5f, false, 144);
                q.Flush(2000);
                Check("a trigger held through CONTROL off types nothing", Typed(sink) == "QWQWQ", Typed(sink));
                kb.Step(KeysShared.RIGHT, p, qu, qv, 1f, false, 0f, false, 155);
                kb.Step(KeysShared.RIGHT, p, qu, qv, 1f, false, 0f, true, 166);
                q.Flush(2000);
                Check("A types (also while another app claims the buttons, A stays a click)", Typed(sink) == "QWQWQQ", Typed(sink));
                var notKb = new Placement { Hwnd = new IntPtr(8), Title = "Notepad", PicW = 400, PicH = 300, Slot = 1, Scale = 0.001f };
                bool on2 = kb.Step(KeysShared.RIGHT, notKb, 0.5f, 0.5f, 1f, false, 0f, false, 177);
                Check("another window is not the keyboard: no light, 1.11 click path", !on2 && keys.RawInt(r + KeysShared.H_LIT_VISIBLE) == 0);
                p.Shown = false;
                kb.Step(KeysShared.RIGHT, p, qu, qv, 1f, false, 0.9f, false, 188);
                q.Flush(2000);
                Check("a hidden keyboard types nothing", Typed(sink) == "QWQWQQ", Typed(sink));
            }
        }
    }
    static void Uv(KeysUi ui, Placement p, uint vk, out float u, out float v) {
        u = v = 0;
        foreach (var k in ui.KeyTable) if (k.Vk == vk) { u = (k.X + k.W / 2f) / p.PicW; v = (k.Y + Capture.HEADER + k.H / 2f) / p.PicH; return; }
    }
    static string Typed(FakeSink s) { lock (s.Writes) return string.Join("", s.Writes.ToArray()); }
}
