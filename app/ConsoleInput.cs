// ConsoleInput.cs -- typing into a Windows Terminal window (1.7.1, 2026-10-28; Tefa in the headset: "still can't type into
// lanes in vr when it's not in focus, and even when in focus, i don't think the keyboard works"). Windows Terminal ignores
// posted key messages (its text comes through the XAML input stack, not WM_CHAR), so for a terminal window the keys go
// into the console of the program running in it instead: Window View attaches to that program's console for a moment and
// writes key records into its input (WriteConsoleInput). The program reads them as typed keys, whatever has the focus.
// Which program: the terminal's own children (the client of each tab is started by WindowsTerminal.exe itself; its
// OpenConsole.exe is skipped), the one whose console title is the window's title (the active tab), else the newest.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

static class ConsoleInput {
    const string TERMINAL_CLASS = "CASCADIA_HOSTING_WINDOW_CLASS";
    const int CACHE_MS = 2000;   // how long a found program is kept for the same window and title
    const uint GA_ROOT = 2, GA_ROOTOWNER = 3, TH32CS_SNAPPROCESS = 2, GENERIC_RW = 0xC0000000, SHARE_RW = 3, OPEN_EXISTING = 3;
    const ushort KEY_EVENT = 1;
    const uint SHIFT_PRESSED = 0x10, LEFT_CTRL_PRESSED = 0x08, LEFT_ALT_PRESSED = 0x02, ENHANCED_KEY = 0x100;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct KEY_EVENT_RECORD { public int KeyDown; public ushort Repeat, Vk, Scan; public char Char; public uint Control; }
    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
    struct INPUT_RECORD { [FieldOffset(0)] public ushort EventType; [FieldOffset(4)] public KEY_EVENT_RECORD Key; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32 {
        public uint dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID; public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll")] static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll")] static extern bool FreeConsole();
    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")] static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetConsoleTitle(StringBuilder s, uint n);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool WriteConsoleInput(IntPtr h, INPUT_RECORD[] recs, uint n, out uint written);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 e);

    static readonly object gate = new object();
    static IntPtr cachedWindow = IntPtr.Zero;
    static string cachedTitle = "";
    static uint cachedPid;
    static int cachedAt;

    public static bool IsTerminal(IntPtr hwnd) {
        if (hwnd == IntPtr.Zero) return false;
        var sb = new StringBuilder(64);
        GetClassName(GetAncestor(hwnd, GA_ROOT), sb, 64);
        return sb.ToString() == TERMINAL_CLASS;
    }

    // which program's console the keys for terminal window `hwnd` would go into (0 = none); for the tests
    public static uint ProgramFor(IntPtr hwnd) {
        lock (gate) {
            IntPtr root = GetAncestor(hwnd, GA_ROOT);
            var tsb = new StringBuilder(256);
            GetWindowText(root, tsb, 256);
            return FindProgram(root, tsb.ToString());
        }
    }

    // one key, pressed and let go, into the program running in the terminal window `hwnd`; false = no program found
    public static bool Type(IntPtr hwnd, uint vk, uint scan, char ch, bool extended, bool shift, bool ctrl, bool alt) {
        lock (gate) {
            IntPtr root = GetAncestor(hwnd, GA_ROOT);
            var tsb = new StringBuilder(256);
            GetWindowText(root, tsb, 256);
            string title = tsb.ToString();
            uint pid = cachedWindow == root && cachedTitle == Bare(title) && Environment.TickCount - cachedAt < CACHE_MS ? cachedPid : 0;
            if (pid == 0) {
                pid = FindProgram(root, title);
                if (pid == 0) return false;
                cachedWindow = root; cachedTitle = Bare(title); cachedPid = pid; cachedAt = Environment.TickCount;
            }
            if (ctrl && ch >= 'a' && ch <= 'z') ch = (char)(ch - 'a' + 1);         // Ctrl+letter: its control character
            else if (ctrl && ch >= 'A' && ch <= 'Z') ch = (char)(ch - 'A' + 1);
            uint state = (shift ? SHIFT_PRESSED : 0) | (ctrl ? LEFT_CTRL_PRESSED : 0) | (alt ? LEFT_ALT_PRESSED : 0) | (extended ? ENHANCED_KEY : 0);
            var recs = new INPUT_RECORD[2];
            for (int i = 0; i < 2; ++i) {
                recs[i].EventType = KEY_EVENT;
                recs[i].Key = new KEY_EVENT_RECORD { KeyDown = i == 0 ? 1 : 0, Repeat = 1, Vk = (ushort)vk, Scan = (ushort)scan, Char = ch, Control = state };
            }
            return WithConsole(pid, h => { uint n; return WriteConsoleInput(h, recs, 2, out n) && n == 2; });
        }
    }

    // ---- 1.12: the typing thread's way in (TypingQueue.cs) ---------------------------------------------------------------
    // the program behind each terminal window, looked up only by Refresh (when the window or its title changes, or after a
    // failed write) and kept with no time limit: a key never waits for a lookup
    static readonly Dictionary<IntPtr, uint> warm = new Dictionary<IntPtr, uint>();

    public static void Refresh(IntPtr hwnd) {
        lock (gate) {
            IntPtr root = GetAncestor(hwnd, GA_ROOT);
            var tsb = new StringBuilder(256);
            GetWindowText(root, tsb, 256);
            uint pid = FindProgram(root, tsb.ToString());
            if (pid != 0) warm[root] = pid; else warm.Remove(root);
        }
    }

    // every key in one write into the program's console, in order (each pressed and let go)
    public static bool TypeMany(IntPtr hwnd, ConsoleKey[] keys, out string why) {
        lock (gate) {
            IntPtr root = GetAncestor(hwnd, GA_ROOT);
            uint pid;
            if (!warm.TryGetValue(root, out pid)) { why = "the terminal's program is not looked up yet"; return false; }
            var recs = new INPUT_RECORD[keys.Length * 2];
            for (int i = 0; i < keys.Length; ++i) {
                var k = keys[i];
                char ch = k.Ch;
                if (k.Ctrl && ch >= 'a' && ch <= 'z') ch = (char)(ch - 'a' + 1);
                else if (k.Ctrl && ch >= 'A' && ch <= 'Z') ch = (char)(ch - 'A' + 1);
                uint state = (k.Shift ? SHIFT_PRESSED : 0) | (k.Ctrl ? LEFT_CTRL_PRESSED : 0) | (k.Ext ? ENHANCED_KEY : 0);
                for (int j = 0; j < 2; ++j) {
                    recs[i * 2 + j].EventType = KEY_EVENT;
                    recs[i * 2 + j].Key = new KEY_EVENT_RECORD { KeyDown = j == 0 ? 1 : 0, Repeat = 1, Vk = (ushort)k.Vk, Scan = (ushort)k.Scan, Char = ch, Control = state };
                }
            }
            uint n = 0;
            bool ok = WithConsole(pid, h => WriteConsoleInput(h, recs, (uint)recs.Length, out n) && n == recs.Length);
            why = ok ? "" : "could not write into program " + pid + " (" + n + " of " + recs.Length + " records)";
            if (!ok) warm.Remove(root);
            return ok;
        }
    }

    // The terminal's own children (not its OpenConsole.exe hosts) that belong to THIS window, then the active tab among them.
    // 1.11 (2026-11-12, Tefa: typing into a clicked Claude window did not work): one WindowsTerminal.exe hosts EVERY terminal
    // window, so its children are the programs of all of them, and 1.7.1 told them apart by title alone, falling back to the
    // newest. Claude Code's title starts with a status sign that keeps changing while it works, so the window's title and
    // its console's title often differed by that sign for a moment, and the keys went to the newest Claude window instead.
    // Now: a tab's console window is owned by the terminal window it sits in (measured 2026-11-12, Windows Terminal 1.24,
    // two Claude windows: each console's owner was its own window), so only this window's programs are candidates; among
    // several tabs, the title decides with leading signs ignored; a window that owns no console falls back to 1.7.1's way.
    static uint FindProgram(IntPtr root, string title) {
        uint wt;
        GetWindowThreadProcessId(root, out wt);
        var kids = new List<uint>();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return 0;
        try {
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
            for (bool ok = Process32First(snap, ref e); ok; ok = Process32Next(snap, ref e))
                if (e.th32ParentProcessID == wt && !string.Equals(e.szExeFile, "OpenConsole.exe", StringComparison.OrdinalIgnoreCase)) kids.Add(e.th32ProcessID);
        } finally { CloseHandle(snap); }
        var titles = new Dictionary<uint, string>();
        var mine = new List<uint>();
        foreach (var pid in kids) {
            string ct = null; IntPtr owner = IntPtr.Zero;
            if (!WithConsole(pid, h => {
                var sb = new StringBuilder(512); GetConsoleTitle(sb, 512); ct = sb.ToString();
                IntPtr cw = GetConsoleWindow();
                if (cw != IntPtr.Zero) owner = GetAncestor(cw, GA_ROOTOWNER);
                return true; })) continue;
            titles[pid] = ct;
            if (owner == root) mine.Add(pid);
        }
        if (mine.Count == 1) return mine[0];
        return PickByTitle(mine.Count > 1 ? mine : new List<uint>(titles.Keys), titles, title);
    }

    // the candidate whose console title is the window's (status signs at the start ignored), else the newest
    static uint PickByTitle(List<uint> pids, Dictionary<uint, string> titles, string title) {
        string want = Bare(title);
        foreach (var pid in pids) if (Bare(titles[pid]) == want) return pid;
        if (want != "") foreach (var pid in pids) { string ct = Bare(titles[pid]); if (ct != "" && (want.Contains(ct) || ct.Contains(want))) return pid; }
        uint newest = 0; DateTime newestStart = DateTime.MinValue;
        foreach (var pid in pids)
            try { var st = Process.GetProcessById((int)pid).StartTime; if (st > newestStart) { newestStart = st; newest = pid; } } catch (Exception) { }
        return newest;
    }

    // a title without the signs and spaces in front of its first letter or digit (Claude Code's turning status sign)
    public static string Bare(string t) {
        if (t == null) return "";
        int i = 0;
        while (i < t.Length && !char.IsLetterOrDigit(t[i])) ++i;
        return t.Substring(i).Trim();
    }

    // attach to `pid`'s console for the length of `work` (with Ctrl+C ignored, so a typed Ctrl+C never stops Window View)
    static bool WithConsole(uint pid, Func<IntPtr, bool> work) {
        if (!AttachConsole(pid)) return false;
        SetConsoleCtrlHandler(IntPtr.Zero, true);
        IntPtr h = CreateFile("CONIN$", GENERIC_RW, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        try { return h != new IntPtr(-1) && work(h); }
        finally {
            if (h != new IntPtr(-1)) CloseHandle(h);
            FreeConsole();
        }
    }
}

// 1.12: one key as a console key record wants it (InputRouter.Resolve makes them)
struct ConsoleKey {
    public uint Vk, Scan;
    public char Ch;
    public bool Ext, Shift, Ctrl;
}
