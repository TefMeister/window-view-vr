// Pointer.cs -- 1.5: the pointer, drawn by the layer every frame, and the motion controllers working the windows.
//
// A loop of its own (about 250 times a second) does two things:
//   1. The pointer: the desk cursor (moved by the mouse, or by the controller below) is turned into a point on the window it
//      lies on, in the desk's frame, and written to the shared block; the layer draws the arrow there every headset frame,
//      so it moves smoothly (Tefa: "the mouse in VR right now is very laggy and not smooth at all": it used to be drawn into
//      the window pictures, which arrive about 30 times a second at best).
//   2. CONTROL mode (the left thumbstick clicked in; Tefa: "use the motion controllers to do all the tweaking and slider tuning"):
//      the layer writes both controllers' aim rays; here they are met with every window as it really hangs (flat facing the
//      eyes in follow mode, turned to face the anchor in room mode).
//      The RIGHT ray points: trigger = left click (held = drag, for sliders), A = right click, right stick up / down = the
//      mouse wheel, Y (B on Index) = bring every window back in front of you.
//      Either GRIP picks up the window under that hand's ray at the spot it points at and carries it, one window in each hand
//      at once (Tefa: "the ability to grab these windows with LG and RG, two windows at once as well"); while carried, that
//      hand's stick up / down pushes the window away / pulls it in.
using System;
using System.Threading;

struct V3 {
    public float X, Y, Z;
    public V3(float x, float y, float z) { X = x; Y = y; Z = z; }
    public static V3 operator +(V3 a, V3 b) { return new V3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
    public static V3 operator -(V3 a, V3 b) { return new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
    public static V3 operator *(V3 a, float s) { return new V3(a.X * s, a.Y * s, a.Z * s); }
    public static float Dot(V3 a, V3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }
    public float Length { get { return (float)Math.Sqrt(Dot(this, this)); } }
}

// where a window hangs, in the desk's frame: the same sums as the layer's build_quads (window_view_layer.cpp)
struct WindowFrame {
    public V3 Centre, Right, Up, Normal;   // Normal points at the eyes (the anchor)
    public float W, H;                     // metres
    public static WindowFrame Of(Placement p, float distance, bool room) {
        var f = new WindowFrame();
        float d = p.Depth;
        f.Centre = new V3(p.X * d, p.Y * d, -distance * d);
        f.W = p.PicW * p.Scale; f.H = p.PicH * p.Scale;
        if (!room) { f.Right = new V3(1, 0, 0); f.Up = new V3(0, 1, 0); f.Normal = new V3(0, 0, 1); return f; }
        float len = f.Centre.Length;
        var n = f.Centre * (-1f / len);
        double phi = Math.Atan2(n.X, n.Z), theta = -Math.Asin(n.Y);   // turned about Y by phi after tipping about X by theta
        f.Normal = n;
        f.Right = new V3((float)Math.Cos(phi), 0, (float)-Math.Sin(phi));
        f.Up = new V3((float)(Math.Sin(theta) * Math.Sin(phi)), (float)Math.Cos(theta), (float)(Math.Sin(theta) * Math.Cos(phi)));
        return f;
    }
    // where the ray meets the window's plane: u / v across and down the picture (0..1 inside), and how far along the ray
    public bool Meet(V3 o, V3 dir, out float u, out float v, out float t) {
        u = v = t = 0;
        float den = V3.Dot(dir, Normal);
        if (Math.Abs(den) < 1e-5f) return false;
        t = V3.Dot(Centre - o, Normal) / den;
        if (t <= 0) return false;
        var h = o + dir * t - Centre;
        u = V3.Dot(h, Right) / W + 0.5f;
        v = 0.5f - V3.Dot(h, Up) / H;
        return true;
    }
    public V3 PointAt(float u, float v) { return Centre + Right * ((u - 0.5f) * W) + Up * ((0.5f - v) * H) + Normal * 0.003f; }
}

// one hand's carry: the window it holds, and the spot on it (u, v) that stays under its ray
class Carry {
    public Placement Window;
    public float U, V;
    public bool Grip;
}

class Pointer : IDisposable {
    const int LOOP_MS = 4;
    const float TRIGGER_DOWN = 0.6f, TRIGGER_UP = 0.4f, GRIP_DOWN = 0.6f, GRIP_UP = 0.4f;
    // 1.8 (Tefa 2026-10-30: "the keyboard does not register some presses", the Show ticks "don't work"): pulling the trigger
    // tilts the controller, so the pointer slid off a small key or tick between press and release and the click was lost.
    // From the first touch of the trigger the pointer holds still; it follows again only once the ray has moved more than
    // CLICK_SLOP_PX picture pixels from where it was held (a slider drag), or the trigger is let go.
    const float TRIGGER_TOUCH = 0.12f, CLICK_SLOP_PX = 14f;
    const float STICK_DEAD = 0.25f, WHEEL_NOTCHES_PER_S = 8f, DEPTH_PER_S = 1.6f;   // full stick: 8 wheel notches a second / depth x1.6 a second
    readonly Shared shared;
    readonly Desk desk;
    readonly InputRouter input;
    readonly Thread thread;
    volatile bool running = true;
    bool trigger, a, y;
    bool held; Placement heldOn; float heldX, heldY;   // 1.8: the pointer held still for a click
    float wheel;
    int lastSeq = -1, lastTick = Environment.TickCount;
    readonly Carry right = new Carry(), left = new Carry();   // either hand carries a window, both at once
    public string LastAction = "";

    public Pointer(Shared shared, Desk desk, InputRouter input) {
        this.shared = shared; this.desk = desk; this.input = input;
        thread = new Thread(Loop) { IsBackground = true, Name = "Window View pointer" };
        thread.Start();
    }
    public void Dispose() { running = false; thread.Join(500); shared.WriteCursor(false, -1, new V3(), false); }

    void Loop() {
        while (running) {
            try { Step(); } catch (Exception) { }
            Thread.Sleep(LOOP_MS);
        }
    }

    void Step() {
        int now = Environment.TickCount;
        float dt = Math.Min(0.1f, (now - lastTick) / 1000f);
        lastTick = now;
        bool room = shared.RoomMode;
        float distance = shared.Distance > 0.01f ? shared.Distance : 1f;
        bool ctrl = shared.CtrlMode;
        if (ctrl) Controllers(room, distance, dt); else Release();
        DrawCursor(room, distance, ctrl || input.On);
    }

    // the nearest window under a ray, and (u, v) on it
    Placement Under(V3 o, V3 d, float distance, bool room, Placement skip, out float bu, out float bv) {
        Placement best = null; float bt = float.MaxValue; bu = bv = 0;
        foreach (var p in desk.Windows) {
            if (p == skip || !p.Shown || !p.Live || p.PicW == 0) continue;
            var f = WindowFrame.Of(p, distance, room);
            float u, v, t;
            if (!f.Meet(o, d, out u, out v, out t) || u < 0 || u > 1 || v < 0 || v > 1) continue;
            // nearer wins (the layer paints farthest first); at the same distance the later slot, as the layer's stable order
            bool tie = best != null && Math.Abs(t - bt) < 0.002f;
            if (tie ? p.Slot > best.Slot : t < bt) { best = p; bu = u; bv = v; bt = t; }
        }
        return best;
    }

    // a hand's grip: pressed over a window, the hand takes it at that spot; held, the window follows so the spot stays under the
    // ray (the window moves on the desk by how far the spot drifted from the ray, every loop); the stick pushes it away (up) or
    // pulls it in (down); let go, it stays where it is and the layout is saved. Called under desk.Lock.
    void CarryStep(Carry c, V3 o, V3 d, float squeeze, float stick, float distance, bool room, float dt, Placement other, string hand) {
        bool g = c.Grip ? squeeze > GRIP_UP : squeeze > GRIP_DOWN;
        if (g && !c.Grip) {
            float u, v;
            var p = Under(o, d, distance, room, other, out u, out v);
            c.Window = p; c.U = u; c.V = v;
            if (p != null) { desk.Raise(p); LastAction = hand + " hand carries " + p.Title; }
        } else if (!g && c.Grip) {
            if (c.Window != null) desk.Save();
            c.Window = null;
        }
        c.Grip = g;
        var w = c.Window;
        if (w == null || !w.Live) return;
        var f = WindowFrame.Of(w, distance, room);
        float nu, nv, t;
        if (f.Meet(o, d, out nu, out nv, out t)) {
            w.X += (nu - c.U) * w.PicW * w.Seen;
            w.Y -= (nv - c.V) * w.PicH * w.Seen;
        }
        if (Math.Abs(stick) > STICK_DEAD) {
            float amount = (Math.Abs(stick) - STICK_DEAD) / (1 - STICK_DEAD) * Math.Sign(stick);
            w.Depth = Math.Max(Desk.MIN_DEPTH, Math.Min(Desk.MAX_DEPTH, w.Depth * (float)Math.Pow(DEPTH_PER_S, amount * dt)));
        }
    }

    // the controllers: the right ray points and clicks, both hands carry
    void Controllers(bool room, float distance, float dt) {
        V3 o, d; float trig, sq, stick; int buttons, seq;
        if (!shared.ReadRay(out o, out d, out trig, out sq, out stick, out buttons, out seq) || seq == lastSeq) return;
        lastSeq = seq;
        V3 lo, ld; float lsq, lstick;
        bool leftOk = shared.ReadLeftRay(out lo, out ld, out lsq, out lstick);
        // Hand Poser's nudge borrows A, Y, X, B and both grips (Tefa: "X is -1, Y is -5 and holding LG makes X and Y repeat");
        // then nothing here is carried, right-clicked or recentred, but the trigger still clicks and the stick still scrolls
        bool claimed = shared.ButtonsClaimed;
        if (claimed) { sq = 0; lsq = 0; buttons = 0; }
        Placement hover; float hu = 0, hv = 0;
        lock (desk.Lock) {
            CarryStep(right, o, d, sq, right.Window != null ? stick : 0, distance, room, dt, left.Window, "right");
            if (leftOk) CarryStep(left, lo, ld, lsq, lstick, distance, room, dt, right.Window, "left");
            else if (left.Grip) { left.Grip = false; if (left.Window != null) desk.Save(); left.Window = null; }
            if (right.Window != null) { hover = right.Window; hu = right.U; hv = right.V; }
            else hover = Under(o, d, distance, room, null, out hu, out hv);
        }
        if (hover != null) {
            float cx, cy, seen;
            lock (desk.Lock) { cx = hover.Left + hu * hover.PicW * hover.Seen; cy = hover.Top - hv * hover.PicH * hover.Seen; seen = hover.Seen; }
            bool touch = right.Window == null && trig > TRIGGER_TOUCH;
            if (!touch) { held = false; heldOn = null; }
            else if (!held) { held = true; heldOn = hover; heldX = cx; heldY = cy; }   // the first touch: hold here
            if (held && heldOn == hover && seen > 0 && Math.Sqrt((cx - heldX) * (cx - heldX) + (cy - heldY) * (cy - heldY)) / seen < CLICK_SLOP_PX)
                input.PointerTo(heldX, heldY);
            else { if (held) heldOn = null; input.PointerTo(cx, cy); }   // moved past the slop: follow (a drag), until the trigger is let go
        }
        if (right.Window != null) return;   // while the right hand carries, its trigger and stick neither click nor scroll
        bool tNow = trigger ? trig > TRIGGER_UP : trig > TRIGGER_DOWN;   // trigger: the left button (held = drag a slider)
        if (tNow != trigger) { trigger = tNow; input.PointerLeft(tNow); }
        bool aNow = (buttons & 1) != 0;                                  // A: the right button
        if (aNow != a) { a = aNow; input.PointerRight(aNow); }
        if (Math.Abs(stick) > STICK_DEAD) {                              // the right stick: the mouse wheel
            float amount = (Math.Abs(stick) - STICK_DEAD) / (1 - STICK_DEAD) * Math.Sign(stick);
            wheel += amount * WHEEL_NOTCHES_PER_S * dt;
            while (Math.Abs(wheel) >= 1) { int step = Math.Sign(wheel); wheel -= step; input.PointerWheel(120 * step); }
        } else wheel = 0;
        bool yNow = (buttons & 2) != 0;
        if (yNow && !y) { shared.Recenter(); LastAction = "windows brought back in front of you"; }
        y = yNow;
    }

    // CONTROL mode went off: nothing stays held
    void Release() {
        if (trigger) { trigger = false; input.PointerLeft(false); }
        if (a) { a = false; input.PointerRight(false); }
        lock (desk.Lock) {
            if (right.Window != null || left.Window != null) desk.Save();
            right.Window = left.Window = null;
            right.Grip = left.Grip = false;
        }
        lastSeq = -1;
    }

    // the arrow: on the window under the desk cursor, else yellow on the nearest window's edge
    void DrawCursor(bool room, float distance, bool wanted) {
        if (!wanted) { shared.WriteCursor(false, -1, new V3(), false); return; }
        lock (desk.Lock) {
            var hit = desk.HitTest(desk.CursorX, desk.CursorY);
            bool edge = hit.P == null;
            if (edge) hit = desk.NearestEdge(desk.CursorX, desk.CursorY);
            if (hit.P == null || hit.P.Slot < 0) { shared.WriteCursor(false, -1, new V3(), false); return; }
            var p = hit.P;
            float u = Math.Max(0, Math.Min(1, (desk.CursorX - p.Left) / (p.PicW * p.Seen)));
            float v = Math.Max(0, Math.Min(1, (p.Top - desk.CursorY) / (p.PicH * p.Seen)));
            shared.WriteCursor(true, p.Slot, WindowFrame.Of(p, distance, room).PointAt(u, v), edge);
        }
    }
}
