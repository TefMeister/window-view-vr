// InputRouter.cs -- the mouse and keyboard while MOUSE & KEYBOARD is on. Two low-level hooks (on their own thread, so
// they stay quick whatever the window is doing) swallow every mouse and keyboard event, so neither the game nor the
// desktop sees them; the mouse movement becomes the desk cursor, a drag on a window's header moves it, a drag on its
// grip resizes it, and everything else is posted straight to the real window under the cursor as ordinary mouse and
// keyboard messages. Page Up (on) and Page Down (off) are always watched and always swallowed, so the switch works from
// inside the headset without the game hearing the key.
//
// The real cursor is parked at the middle of the screen while on: the hook sees where the mouse WANTED to go and the
// difference to the middle is the movement. The game keeps the Windows focus the whole time, so controllers and the
// gamepad keep working.
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

class InputRouter : IDisposable {
    public bool On { get; private set; }
    public static bool AcceptInjected;      // tests only (WindowView.cs --test-input)
    public event Action<bool> OnChanged;    // raised on the hook thread
    readonly Desk desk;
    Thread thread;
    uint threadId;
    IntPtr kbHook = IntPtr.Zero, msHook = IntPtr.Zero;
    HookProc kbProc, msProc;                // kept alive for the hooks' lifetime
    Point centre;
    bool shift, ctrl, alt;
    int buttonsHeld;                        // MK_* bits
    IntPtr target = IntPtr.Zero, lastClicked = IntPtr.Zero;   // the control a button went down on; the last control clicked
    Placement dragging, sizing;
    public volatile string LastAction = "";

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern IntPtr ChildWindowFromPointEx(IntPtr parent, POINT p, uint flags);
    [DllImport("user32.dll")] static extern int ToUnicode(uint vk, uint scan, byte[] state, [Out, MarshalAs(UnmanagedType.LPWStr, SizeConst = 8)] System.Text.StringBuilder buf, int size, uint flags);
    [DllImport("user32.dll")] static extern short GetKeyState(int vk);
    const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    const uint WM_QUIT = 0x12, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_CHAR = 0x102, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    const uint WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205,
               WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208, WM_MOUSEWHEEL = 0x20A, WM_MOUSEHWHEEL = 0x20E;
    const int MK_LBUTTON = 1, MK_RBUTTON = 2, MK_SHIFT = 4, MK_CONTROL = 8, MK_MBUTTON = 0x10;
    const uint VK_PRIOR = 0x21, VK_NEXT = 0x22, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_CAPITAL = 0x14;
    const uint LLKHF_INJECTED = 0x10, LLMHF_INJECTED = 0x01, LLKHF_EXTENDED = 0x01;
    const uint CWP_SKIP = 0x1 | 0x2 | 0x4;   // invisible, disabled, transparent children
    static readonly IntPtr SWALLOW = new IntPtr(1);

    public InputRouter(Desk desk) { this.desk = desk; }

    public void Start() {
        thread = new Thread(Loop);
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
    void Loop() {
        threadId = GetCurrentThreadId();
        kbProc = KbHook;
        msProc = MsHook;
        IntPtr module = GetModuleHandle(null);
        kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, kbProc, module, 0);
        msHook = SetWindowsHookEx(WH_MOUSE_LL, msProc, module, 0);
        Application.Run();
        if (kbHook != IntPtr.Zero) UnhookWindowsHookEx(kbHook);
        if (msHook != IntPtr.Zero) UnhookWindowsHookEx(msHook);
    }
    // closing the app always hands the mouse and keyboard back
    public void Dispose() {
        SetOn(false);
        if (threadId != 0) PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (thread != null) thread.Join(1500);
    }

    public void SetOn(bool on) {
        if (On == on) return;
        On = on;
        if (on) {
            var s = Screen.PrimaryScreen.Bounds;
            centre = new Point(s.Left + s.Width / 2, s.Top + s.Height / 2);
            SetCursorPos(centre.X, centre.Y);
            shift = ctrl = alt = false;
            buttonsHeld = 0;
        } else {
            lock (desk.Lock) { dragging = sizing = null; }
            target = IntPtr.Zero;
            buttonsHeld = 0;
        }
        LastAction = on ? "mouse and keyboard: to the windows" : "mouse and keyboard: back to normal";
        var h = OnChanged;
        if (h != null) h(on);
    }

    // ---- keyboard --------------------------------------------------------------------------------------------------
    IntPtr KbHook(int code, IntPtr wParam, IntPtr lParam) {
        if (code < 0) return CallNextHookEx(kbHook, code, wParam, lParam);
        var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
        uint msg = (uint)wParam.ToInt64();
        bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
        if ((k.flags & LLKHF_INJECTED) == 0 || AcceptInjected) {
            if (k.vkCode == VK_PRIOR) { if (down) SetOn(true); return SWALLOW; }
            if (k.vkCode == VK_NEXT) { if (down) SetOn(false); return SWALLOW; }
            if (On) { ForwardKey(k, down, msg == WM_SYSKEYDOWN || msg == WM_SYSKEYUP); return SWALLOW; }
        }
        return CallNextHookEx(kbHook, code, wParam, lParam);
    }
    void ForwardKey(KBDLLHOOKSTRUCT k, bool down, bool sys) {
        if (k.vkCode == VK_SHIFT || k.vkCode == 0xA0 || k.vkCode == 0xA1) shift = down;
        if (k.vkCode == VK_CONTROL || k.vkCode == 0xA2 || k.vkCode == 0xA3) ctrl = down;
        if (k.vkCode == VK_MENU || k.vkCode == 0xA4 || k.vkCode == 0xA5) alt = down;
        IntPtr t = KeyTarget();
        if (t == IntPtr.Zero) return;
        uint lp = 1u | (k.scanCode << 16) | ((k.flags & LLKHF_EXTENDED) != 0 ? 1u << 24 : 0) | (down ? 0u : 0xC0000000u);
        // A letter goes as ONE WM_CHAR (the window's own message loop would make a second one out of a WM_KEYDOWN, as
        // Notepad did in the first test: "hheelllloo"); everything else (Enter, Backspace, arrows, Tab, Escape, Ctrl and
        // Alt combinations) goes as the key itself, and the window's loop turns it into the character it wants.
        var state = new byte[256];   // our own modifier picture: the real keys never reached Windows
        if (shift) state[VK_SHIFT] = 0x80;
        if (ctrl) state[VK_CONTROL] = 0x80;
        if (alt) state[VK_MENU] = 0x80;
        if ((GetKeyState((int)VK_CAPITAL) & 1) != 0) state[VK_CAPITAL] = 1;
        var buf = new System.Text.StringBuilder(8);
        int n = ToUnicode(k.vkCode, k.scanCode, state, buf, 8, 0);
        bool printable = n >= 1 && buf[0] >= ' ' && !ctrl && !alt;
        if (printable) { if (down) PostMessage(t, WM_CHAR, new IntPtr(buf[0]), new IntPtr(lp)); return; }
        PostMessage(t, down ? (sys ? WM_SYSKEYDOWN : WM_KEYDOWN) : (sys ? WM_SYSKEYUP : WM_KEYUP), new IntPtr(k.vkCode), new IntPtr(lp));
    }
    // keys go to the control last clicked, else to the window under the cursor, else to the top window
    IntPtr KeyTarget() {
        if (lastClicked != IntPtr.Zero && Native.IsWindow(lastClicked)) return lastClicked;
        lock (desk.Lock) {
            var hit = desk.HitTest(desk.CursorX, desk.CursorY);
            if (hit.P != null) return hit.P.Hwnd;
            for (int i = desk.Windows.Count - 1; i >= 0; --i) if (desk.Windows[i].Live && desk.Windows[i].Shown) return desk.Windows[i].Hwnd;
        }
        return IntPtr.Zero;
    }

    // ---- mouse -----------------------------------------------------------------------------------------------------
    IntPtr MsHook(int code, IntPtr wParam, IntPtr lParam) {
        if (code < 0 || !On) return CallNextHookEx(msHook, code, wParam, lParam);
        var m = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
        if ((m.flags & LLMHF_INJECTED) != 0 && !AcceptInjected) return CallNextHookEx(msHook, code, wParam, lParam);   // our own parking move
        uint msg = (uint)wParam.ToInt64();
        try {
            if (msg == WM_MOUSEMOVE) {
                int dx = m.pt.X - centre.X, dy = m.pt.Y - centre.Y;
                if (dx != 0 || dy != 0) Move(dx, dy);
            } else if (msg == WM_MOUSEWHEEL || msg == WM_MOUSEHWHEEL) {
                Wheel(msg, (short)(m.mouseData >> 16));
            } else {
                Button(msg);
            }
        } catch (Exception) { }
        return SWALLOW;
    }
    uint Keys() { return (uint)(buttonsHeld | (shift ? MK_SHIFT : 0) | (ctrl ? MK_CONTROL : 0)); }
    static IntPtr Lp(int x, int y) { return new IntPtr((y << 16) | (x & 0xFFFF)); }
    // the deepest child under a screen point, and that point in its client coordinates
    static IntPtr ChildAt(IntPtr top, POINT screen, out POINT client) {
        IntPtr h = top;
        for (int guard = 0; guard < 32; ++guard) {
            POINT c = screen;
            Native.ScreenToClient(h, ref c);
            IntPtr child = ChildWindowFromPointEx(h, c, CWP_SKIP);
            if (child == IntPtr.Zero || child == h) break;
            h = child;
        }
        client = screen;
        Native.ScreenToClient(h, ref client);
        return h;
    }
    static POINT ScreenPoint(Placement p, int clientX, int clientY) {
        var s = new POINT(clientX, clientY);
        Native.ClientToScreen(p.Hwnd, ref s);
        return s;
    }
    void Move(int dx, int dy) {
        Hit hit;
        lock (desk.Lock) {
            desk.MoveCursor(dx, dy);
            if (dragging != null) {
                dragging.X += dx * Desk.CURSOR_SPEED;
                dragging.Y -= dy * Desk.CURSOR_SPEED;
                return;
            }
            if (sizing != null) {   // the grip follows the cursor: the width in metres from the left edge decides the scale
                float width = desk.CursorX - sizing.Left;
                if (sizing.PicW > 0 && width > 0.02f) {
                    float left = sizing.Left, top = sizing.Top;
                    sizing.Scale = Math.Max(Desk.MIN_SCALE, Math.Min(Desk.MAX_SCALE, width / sizing.PicW));
                    sizing.X = left + sizing.PicW * sizing.Scale / 2;   // keep the top-left corner where it was
                    sizing.Y = top - sizing.PicH * sizing.Scale / 2;
                }
                return;
            }
            hit = desk.HitTest(desk.CursorX, desk.CursorY);
        }
        if (target != IntPtr.Zero && Native.IsWindow(target)) {   // a button is held: the control that got the press keeps the moves
            var held = HeldPlacement();
            if (held != null) {
                var sp = ScreenPoint(held, hit.P == held ? hit.ClientX : HeldClientX(held), hit.P == held ? hit.ClientY : HeldClientY(held));
                POINT c = sp;
                Native.ScreenToClient(target, ref c);
                PostMessage(target, WM_MOUSEMOVE, new IntPtr(Keys()), Lp(c.X, c.Y));
            }
            return;
        }
        if (hit.Kind == HitKind.Body) {
            POINT c;
            IntPtr child = ChildAt(hit.P.Hwnd, ScreenPoint(hit.P, hit.ClientX, hit.ClientY), out c);
            PostMessage(child, WM_MOUSEMOVE, new IntPtr(Keys()), Lp(c.X, c.Y));
        }
    }
    Placement heldPlacement;
    Placement HeldPlacement() { return heldPlacement != null && heldPlacement.Live ? heldPlacement : null; }
    // while a button is held and the cursor has left the window, keep reporting a point just outside it (sliders expect that)
    int HeldClientX(Placement p) { return (int)Math.Floor((desk.CursorX - p.Left) / p.Scale); }
    int HeldClientY(Placement p) { return (int)Math.Floor((p.Top - desk.CursorY) / p.Scale) - Capture.HEADER; }

    void Button(uint msg) {
        bool down = msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN;
        int bit = msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP ? MK_LBUTTON : msg == WM_RBUTTONDOWN || msg == WM_RBUTTONUP ? MK_RBUTTON : MK_MBUTTON;
        Hit hit;
        lock (desk.Lock) { hit = desk.HitTest(desk.CursorX, desk.CursorY); }
        if (down) {
            buttonsHeld |= bit;
            if (target == IntPtr.Zero) {
                if (hit.Kind == HitKind.None) return;
                lock (desk.Lock) { desk.Raise(hit.P); }
                if (bit == MK_LBUTTON && hit.Kind == HitKind.Header) { lock (desk.Lock) { dragging = hit.P; } LastAction = "moving " + hit.P.Title; return; }
                if (bit == MK_LBUTTON && hit.Kind == HitKind.Grip) { lock (desk.Lock) { sizing = hit.P; } LastAction = "resizing " + hit.P.Title; return; }
                if (hit.Kind != HitKind.Body) return;
                POINT c;
                target = ChildAt(hit.P.Hwnd, ScreenPoint(hit.P, hit.ClientX, hit.ClientY), out c);
                heldPlacement = hit.P;
                lastClicked = target;
                PostMessage(target, msg, new IntPtr(Keys()), Lp(c.X, c.Y));
                LastAction = "clicked in " + hit.P.Title;
            } else PostToTarget(msg, hit);
        } else {
            buttonsHeld &= ~bit;
            bool wasDragging;
            lock (desk.Lock) { wasDragging = dragging != null || sizing != null; if (bit == MK_LBUTTON) dragging = sizing = null; }
            if (wasDragging) { desk.Save(); return; }
            if (target != IntPtr.Zero) PostToTarget(msg, hit);
            if (buttonsHeld == 0) target = IntPtr.Zero;
        }
    }
    void PostToTarget(uint msg, Hit hit) {
        var held = HeldPlacement();
        if (held == null || !Native.IsWindow(target)) { target = IntPtr.Zero; return; }
        var sp = ScreenPoint(held, hit.P == held ? hit.ClientX : HeldClientX(held), hit.P == held ? hit.ClientY : HeldClientY(held));
        POINT c = sp;
        Native.ScreenToClient(target, ref c);
        PostMessage(target, msg, new IntPtr(Keys()), Lp(c.X, c.Y));
    }
    void Wheel(uint msg, short delta) {
        Hit hit;
        lock (desk.Lock) { hit = desk.HitTest(desk.CursorX, desk.CursorY); }
        if (hit.Kind != HitKind.Body) return;
        POINT c;
        var sp = ScreenPoint(hit.P, hit.ClientX, hit.ClientY);
        IntPtr child = ChildAt(hit.P.Hwnd, sp, out c);
        PostMessage(child, msg, new IntPtr((delta << 16) | (int)Keys()), Lp(sp.X, sp.Y));   // wheel messages carry screen coordinates
    }
}
