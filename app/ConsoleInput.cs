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
    const uint GA_ROOT = 2, TH32CS_SNAPPROCESS = 2, GENERIC_RW = 0xC0000000, SHARE_RW = 3, OPEN_EXISTING = 3;
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

    // one key, pressed and let go, into the program running in the terminal window `hwnd`; false = no program found
    public static bool Type(IntPtr hwnd, uint vk, uint scan, char ch, bool extended, bool shift, bool ctrl, bool alt) {
        lock (gate) {
            IntPtr root = GetAncestor(hwnd, GA_ROOT);
            var tsb = new StringBuilder(256);
            GetWindowText(root, tsb, 256);
            string title = tsb.ToString();
            uint pid = cachedWindow == root && cachedTitle == title && Environment.TickCount - cachedAt < CACHE_MS ? cachedPid : 0;
            if (pid == 0) {
                pid = FindProgram(root, title);
                if (pid == 0) return false;
                cachedWindow = root; cachedTitle = title; cachedPid = pid; cachedAt = Environment.TickCount;
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

    // the terminal's own children (not its OpenConsole.exe hosts): the one whose console title is the window's, else the newest
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
        uint newest = 0; DateTime newestStart = DateTime.MinValue;
        foreach (var pid in kids) {
            string ct = null;
            if (!WithConsole(pid, h => { var sb = new StringBuilder(512); GetConsoleTitle(sb, 512); ct = sb.ToString(); return true; })) continue;
            if (ct == title || (ct != "" && (title.Contains(ct) || ct.Contains(title)))) return pid;
            try { var st = Process.GetProcessById((int)pid).StartTime; if (st > newestStart) { newestStart = st; newest = pid; } } catch (Exception) { }
        }
        return newest;
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
