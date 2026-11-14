// TypingQueue.cs -- 1.12: the keyboard's presses are typed on a thread of their own, in the order they were pressed (Tefa
// 2026-11-14: "some presses don't register at all, others show up late").
//   - presses waiting together go into the target in ONE write, so a burst can neither reorder nor drop
//   - the program behind a terminal window is looked up when the target window or its title changes, never on the way of
//     a key; a write that fails is looked up again and retried ONCE; still failing, the press is lost: its hand is told
//     (the layer flashes it red) and the log says why
//   - every press gets a line in %APPDATA%\WindowViewVR\keys.log (the newest 2,000 kept), so a lost one can be traced
// The lookup is NOT repeated on a timer: it attaches to every program of the terminal in turn, and a process can be
// attached to one console only, so it would stall typing every time it ran (the lateness this replaces).
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// one key to type: the key code, the one-shot Shift / Ctrl, which hand pressed it and when (Environment.TickCount)
struct KeyStroke {
    public uint Vk;
    public bool Shift, Ctrl;
    public int Hand;
    public int PressMs;
}

// where the keys go: the real one is RouterSink (terminals through their program's console, other windows by message)
interface IKeySink {
    bool Write(IntPtr target, KeyStroke[] keys, out string why);
    void Refresh(IntPtr target);
}

class TypingQueue : IDisposable {
    const int WAKE_MS = 50;                 // how often the thread looks for a changed target when nothing is typed
    const int LOG_KEEP = 2000, LOG_TRIM_AT = 2500;
    readonly IKeySink sink;
    readonly Func<IntPtr> target;
    readonly Action<int> lost;
    readonly string logPath;
    readonly Queue<KeyStroke> pending = new Queue<KeyStroke>();
    readonly object gate = new object();
    readonly Thread thread;
    volatile bool running = true;
    bool busy;
    IntPtr lastTarget = IntPtr.Zero; string lastTitle = null;
    int logLines = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder s, int n);

    public TypingQueue(IKeySink sink, Func<IntPtr> target, Action<int> lost, string logPath) {
        this.sink = sink; this.target = target; this.lost = lost; this.logPath = logPath;
        thread = new Thread(Loop) { IsBackground = true, Name = "Window View typing" };
        thread.Start();
    }
    public void Dispose() { running = false; lock (gate) Monitor.PulseAll(gate); thread.Join(1000); }

    public void Add(KeyStroke k) { lock (gate) { pending.Enqueue(k); Monitor.PulseAll(gate); } }

    // waits until every key added so far is written (or lost); for the tests
    public void Flush(int timeoutMs) {
        int end = Environment.TickCount + timeoutMs;
        lock (gate) while ((pending.Count > 0 || busy) && Environment.TickCount - end < 0) Monitor.Wait(gate, 20);
    }

    void Loop() {
        while (running) {
            KeyStroke[] batch = null;
            lock (gate) {
                if (pending.Count == 0) Monitor.Wait(gate, WAKE_MS);
                if (pending.Count > 0) { batch = pending.ToArray(); pending.Clear(); busy = true; }
            }
            try {
                IntPtr t = target();
                LookUpIfChanged(t);
                if (batch != null) Type(t, batch);
            } catch (Exception e) {
                if (batch != null) foreach (var k in batch) { Log(k, Environment.TickCount, "lost", e.GetType().Name + ": " + e.Message); lost(k.Hand); }
            } finally {
                lock (gate) { busy = false; Monitor.PulseAll(gate); }
            }
        }
    }

    void LookUpIfChanged(IntPtr t) {
        string title = Title(t);
        if (t == lastTarget && title == lastTitle) return;
        lastTarget = t; lastTitle = title;
        if (t != IntPtr.Zero) sink.Refresh(t);
    }
    static string Title(IntPtr t) {
        if (t == IntPtr.Zero) return "";
        var sb = new StringBuilder(256);
        GetWindowText(t, sb, 256);
        return ConsoleInput.Bare(sb.ToString());
    }

    void Type(IntPtr t, KeyStroke[] batch) {
        string why;
        if (t == IntPtr.Zero) { why = "no window to type into (click one first)"; }
        else if (sink.Write(t, batch, out why)) { Done(batch, "typed", ""); return; }
        else {
            sink.Refresh(t);
            string why2;
            if (sink.Write(t, batch, out why2)) { Done(batch, "retried", why); return; }
            why = why2;
        }
        int now = Environment.TickCount;
        foreach (var k in batch) { Log(k, now, "lost", why); lost(k.Hand); }
    }
    void Done(KeyStroke[] batch, string result, string why) { int now = Environment.TickCount; foreach (var k in batch) Log(k, now, result, why); }

    // ---- the press log ---------------------------------------------------------------------------------------------------
    void Log(KeyStroke k, int wroteMs, string result, string why) {
        if (logPath == null) return;
        try {
            string line = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0:HH:mm:ss.fff}  hand={1}  key={2}  press={3}  wrote={4}  lag={5}ms  result={6}  why={7}",
                DateTime.Now, k.Hand == KeysShared.LEFT ? "L" : "R", KeyName(k), k.PressMs, wroteMs, wroteMs - k.PressMs, result, why);
            if (logLines < 0) logLines = File.Exists(logPath) ? File.ReadAllLines(logPath).Length : 0;
            File.AppendAllText(logPath, line + Environment.NewLine);
            if (++logLines > LOG_TRIM_AT) {
                var all = File.ReadAllLines(logPath);
                var keep = new string[Math.Min(LOG_KEEP, all.Length)];
                Array.Copy(all, all.Length - keep.Length, keep, 0, keep.Length);
                File.WriteAllLines(logPath, keep);
                logLines = keep.Length;
            }
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    static string KeyName(KeyStroke k) {
        string mods = (k.Ctrl ? "Ctrl+" : "") + (k.Shift ? "Shift+" : "");
        uint v = k.Vk;
        if ((v >= '0' && v <= '9') || (v >= 'A' && v <= 'Z')) return mods + (char)v;
        switch (v) {
        case 0x08: return mods + "Back"; case 0x09: return mods + "Tab"; case 0x0D: return mods + "Enter"; case 0x1B: return mods + "Esc";
        case 0x20: return mods + "Space"; case 0x25: return mods + "Left"; case 0x26: return mods + "Up"; case 0x27: return mods + "Right";
        case 0x28: return mods + "Down"; case 0x24: return mods + "Home"; case 0x23: return mods + "End"; case 0x2E: return mods + "Del";
        }
        return mods + "0x" + v.ToString("X2");
    }
}

// the real target: a terminal window gets the keys through its program's console (ConsoleInput), any other window as
// window messages (InputRouter, as 1.11 did)
class RouterSink : IKeySink {
    readonly InputRouter input;
    public RouterSink(InputRouter input) { this.input = input; }
    public bool Write(IntPtr target, KeyStroke[] keys, out string why) {
        if (ConsoleInput.IsTerminal(target)) {
            var recs = new ConsoleKey[keys.Length];
            for (int i = 0; i < keys.Length; ++i) recs[i] = input.Resolve(keys[i].Vk, keys[i].Shift, keys[i].Ctrl);
            return ConsoleInput.TypeMany(target, recs, out why);
        }
        foreach (var k in keys) input.TypeKeyTo(target, k.Vk, k.Shift, k.Ctrl);
        why = "";
        return true;
    }
    public void Refresh(IntPtr target) { if (ConsoleInput.IsTerminal(target)) ConsoleInput.Refresh(target); }
}
