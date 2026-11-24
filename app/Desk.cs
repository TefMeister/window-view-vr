// Desk.cs -- the imaginary flat desk in front of the eyes: which windows sit on it, where, how big and how see-through,
// where the desk cursor is, what is under it, and the layout file that brings it all back next time.
//
// Units: metres on a plane `distance` in front of the eyes (the layer puts it at Shared.Distance, 1 m by default);
// x to the right, y up, (0, 0) straight ahead. A window's picture (header strip + client area, Capture.cs) is
// PicW x PicH pixels drawn at Scale metres per pixel, centred on (X, Y).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

class Placement {
    public IntPtr Hwnd;              // IntPtr.Zero while the window is not open
    public string Exe = "", Title = "";   // Title "" = any window of that exe (an app added by ADD AN APP or dropped on the window)
    public string Path = "";              // the exe's full path: its icon on the row, and how it is started when it is not running
    public float X, Y, Scale = Desk.DEFAULT_SCALE, Opacity = 1f;
    // 1.4: how far back the window sits, as a share of the desk's distance (F9 / F10). On the desk (the plane the cursor moves
    // on) a window pushed back looks smaller: it is drawn at Scale / Depth metres per pixel there, at the same centre.
    public float Depth = 1f;
    // 1.15: the window's own tilt and turn about its middle, degrees (the right stick while a grip holds it): tilt > 0 = the
    // top leans back, turn > 0 = the face turns right. 0 / 0 = facing the eyes, as before
    public float Tilt, Turn;
    public float Seen { get { return Scale / Depth; } }
    public bool Shown = true;
    public int Slot = -1;
    public int PicW, PicH;           // the last picture's size, set by the capture loop
    public bool Live { get { return Hwnd != IntPtr.Zero; } }
    public float Left { get { return X - PicW * Seen / 2; } }
    public float Top { get { return Y + PicH * Seen / 2; } }
}

enum HitKind { None, Header, Grip, Body }
struct Hit {
    public Placement P; public HitKind Kind;
    public int PicX, PicY;           // pixel in the picture (header included)
    public int ClientX { get { return PicX; } }
    public int ClientY { get { return PicY - Capture.HEADER; } }
}

class Desk {
    public const float DEFAULT_SCALE = 0.0006f, MIN_SCALE = 0.0002f, MAX_SCALE = 0.003f;   // metres per pixel
    public const float MAX_TURN = 75f;   // 1.15: degrees either way, for the tilt and the turn (past this a window is hard to aim at)
    public const float MIN_DEPTH = 0.4f, MAX_DEPTH = 4f, DEPTH_STEP = 1.15f;   // 1.4: F9 / F10 push a window back / pull it in by this
    public const float CURSOR_SPEED = 0.0006f;   // metres of desk per mouse pixel (1 mouse px = 1 window px at the default size)
    public const float REACH_X = 0.9f, REACH_Y = 0.6f;   // how far the cursor may go, metres
    public const float CURSOR_MARGIN = 0.03f;   // v1.2: how far past the windows' outline the cursor may go, metres
    public readonly object Lock = new object();
    public readonly List<Placement> Windows = new List<Placement>();   // last = on top
    public float CursorX, CursorY;
    internal static string File = Path.Combine(   // 1.15: the self-tests point it at a temp file
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowViewVR", "layout.txt");

    // 1.5: where windows overlap, the one the layer draws on top wins: the nearer (it paints farthest first), at the same
    // distance the later slot; so the mouse and the controllers always take the window that is seen
    public Hit HitTest(float cx, float cy) {
        var hit = new Hit();
        var order = new List<Placement>(Windows);
        order.Sort((a, b) => a.Depth != b.Depth ? a.Depth.CompareTo(b.Depth) : b.Slot.CompareTo(a.Slot));
        for (int i = 0; i < order.Count; ++i) {
            var p = order[i];
            if (!p.Shown || !p.Live || p.PicW == 0) continue;
            int px = (int)Math.Floor((cx - p.Left) / p.Seen), py = (int)Math.Floor((p.Top - cy) / p.Seen);
            if (px < 0 || py < 0 || px >= p.PicW || py >= p.PicH) continue;
            hit.P = p;
            hit.PicX = px;
            hit.PicY = py;
            hit.Kind = py < Capture.HEADER ? HitKind.Header : px >= p.PicW - Capture.GRIP && py >= p.PicH - Capture.GRIP ? HitKind.Grip : HitKind.Body;
            return hit;
        }
        return hit;
    }
    // v1.2: the cursor stays within CURSOR_MARGIN of the windows' joint outline (Tefa 2026-10-24: "i need to somehow be
    // able to know where my mouse is in vr"); before, it could wander up to REACH metres off into empty space, unseen
    public void MoveCursor(int dx, int dy) {
        float minX = -REACH_X, maxX = REACH_X, minY = -REACH_Y, maxY = REACH_Y;
        bool any = false;
        foreach (var p in Windows) {
            if (!p.Shown || !p.Live || p.PicW == 0) continue;
            float l = p.Left - CURSOR_MARGIN, r = p.Left + p.PicW * p.Seen + CURSOR_MARGIN;
            float t = p.Top + CURSOR_MARGIN, b = p.Top - p.PicH * p.Seen - CURSOR_MARGIN;
            if (!any) { minX = l; maxX = r; minY = b; maxY = t; any = true; }
            else { minX = Math.Min(minX, l); maxX = Math.Max(maxX, r); minY = Math.Min(minY, b); maxY = Math.Max(maxY, t); }
        }
        CursorX = Math.Max(minX, Math.Min(maxX, CursorX + dx * CURSOR_SPEED));
        CursorY = Math.Max(minY, Math.Min(maxY, CursorY - dy * CURSOR_SPEED));
    }
    // v1.2: the cursor to the middle of the top window (MOUSE & KEYBOARD switched on), so it always starts in sight
    public void CursorToTop() {
        for (int i = Windows.Count - 1; i >= 0; --i) {
            var p = Windows[i];
            if (!p.Shown || !p.Live || p.PicW == 0) continue;
            CursorX = p.X;
            CursorY = p.Y;
            return;
        }
    }
    // v1.2: the window nearest the cursor when it is over none, and the point on its edge closest to the cursor: the
    // cursor is drawn there in its "outside" colour, so it never disappears between windows
    public Hit NearestEdge(float cx, float cy) {
        var best = new Hit();
        float bestD = float.MaxValue;
        foreach (var p in Windows) {
            if (!p.Shown || !p.Live || p.PicW == 0) continue;
            float fx = (cx - p.Left) / p.Seen, fy = (p.Top - cy) / p.Seen;
            float qx = Math.Max(0, Math.Min(p.PicW - 1, fx)), qy = Math.Max(0, Math.Min(p.PicH - 1, fy));
            float d = ((fx - qx) * (fx - qx) + (fy - qy) * (fy - qy)) * p.Seen * p.Seen;
            if (d >= bestD) continue;
            bestD = d;
            best.P = p;
            best.PicX = (int)qx;
            best.PicY = (int)qy;
        }
        return best;
    }
    public Placement Add(IntPtr hwnd, string exe, string title, string path = "") {
        var p = new Placement();
        p.Path = path;
        p.Hwnd = hwnd;
        p.Exe = exe;
        p.Title = title;
        p.Slot = FreeSlot();
        p.X = 0.04f * Windows.Count;    // each new one a little right and down of the last, so they do not stack exactly
        p.Y = -0.03f * Windows.Count;
        Windows.Add(p);
        return p;
    }
    public void Remove(Placement p) { Windows.Remove(p); }
    // 1.4: the window under the cursor (or the nearest one) pushed back (steps > 0) or pulled in (steps < 0)
    public Placement Push(int steps) {
        var hit = HitTest(CursorX, CursorY);
        if (hit.P == null) hit = NearestEdge(CursorX, CursorY);
        if (hit.P == null) return null;
        hit.P.Depth = Math.Max(MIN_DEPTH, Math.Min(MAX_DEPTH, hit.P.Depth * (float)Math.Pow(DEPTH_STEP, steps)));
        return hit.P;
    }
    // 1.17: no window farther than this from the eyes. X runs along the desk's curve, a full circle round the eyes, so it is wrapped to
    // half a turn either way (the same place, a smaller number); Y is held to MAX_Y up or down (about 56 degrees at the
    // desk's 1 m), and Depth already stops at MAX_DEPTH. Rubbish (NaN) goes back to the middle. Load() brings a window that
    // is past these limits back straight ahead
    public const float MAX_Y = 1.5f, LOST_X = 100f;   // LOST_X: a layout line this far round (16 turns) was never put there by hand
    public static void KeepNear(Placement p, float distance) {
        if (!(distance > 0.01f)) distance = 1f;
        float turn = 2 * (float)Math.PI * distance;   // once round the eyes: the same place again
        p.X = float.IsNaN(p.X) || Math.Abs(p.X) > LOST_X ? 0 : p.X - turn * (float)Math.Round(p.X / turn);
        p.Y = float.IsNaN(p.Y) ? 0 : Math.Max(-MAX_Y, Math.Min(MAX_Y, p.Y));
    }
    public static float ClampTurn(float deg) { return float.IsNaN(deg) ? 0 : Math.Max(-MAX_TURN, Math.Min(MAX_TURN, deg)); }
    public void Raise(Placement p) { if (Windows.Remove(p)) Windows.Add(p); }
    public int FreeSlot() {
        for (int s = 0; s < Shared.SLOTS; ++s) {
            bool used = false;
            foreach (var p in Windows) used |= p.Slot == s;
            if (!used) return s;
        }
        return -1;
    }
    public void Save() {
        var lines = new List<string>();
        var ci = CultureInfo.InvariantCulture;
        foreach (var p in Windows)
            lines.Add(string.Join("|", new[] { p.Exe, p.Title.Replace("|", " "), p.X.ToString(ci), p.Y.ToString(ci), p.Scale.ToString(ci), p.Opacity.ToString(ci), p.Shown ? "1" : "0", p.Path, p.Depth.ToString(ci), p.Tilt.ToString(ci), p.Turn.ToString(ci) }));
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(File));
            System.IO.File.WriteAllLines(File, lines.ToArray());
        } catch (Exception) { }
    }
    // windows come back where they were, not yet linked to a live window (ViewUi relinks them by exe + title)
    public void Load() {
        if (!System.IO.File.Exists(File)) return;
        var ci = CultureInfo.InvariantCulture;
        foreach (var line in System.IO.File.ReadAllLines(File)) {
            var f = line.Split('|');
            if (f.Length < 7) continue;
            var p = new Placement();
            p.Exe = f[0];
            p.Title = f[1];
            float.TryParse(f[2], NumberStyles.Float, ci, out p.X);
            float.TryParse(f[3], NumberStyles.Float, ci, out p.Y);
            float.TryParse(f[4], NumberStyles.Float, ci, out p.Scale);
            float.TryParse(f[5], NumberStyles.Float, ci, out p.Opacity);
            p.Shown = f[6] == "1";
            if (f.Length > 7) p.Path = f[7];   // v1.3: the exe's path (older layout files have none)
            if (f.Length > 8) float.TryParse(f[8], NumberStyles.Float, ci, out p.Depth);   // 1.4
            if (f.Length > 10) { float.TryParse(f[9], NumberStyles.Float, ci, out p.Tilt); float.TryParse(f[10], NumberStyles.Float, ci, out p.Turn); }   // 1.15
            p.Tilt = ClampTurn(p.Tilt); p.Turn = ClampTurn(p.Turn);
            bool lost = float.IsNaN(p.X) || float.IsNaN(p.Y) || Math.Abs(p.X) > LOST_X || Math.Abs(p.Y) > MAX_Y + 0.01f;
            if (lost) { p.X = 0; p.Y = 0; p.Tilt = 0; p.Turn = 0; }   // 1.17: a window that got away comes back straight ahead, facing the eyes
            p.Depth = Math.Max(MIN_DEPTH, Math.Min(MAX_DEPTH, p.Depth == 0 ? 1f : p.Depth));
            p.Scale = Math.Max(MIN_SCALE, Math.Min(MAX_SCALE, p.Scale == 0 ? DEFAULT_SCALE : p.Scale));
            p.Opacity = Math.Max(0.1f, Math.Min(1f, p.Opacity == 0 ? 1f : p.Opacity));
            p.Slot = FreeSlot();
            if (p.Slot >= 0) Windows.Add(p);
        }
    }
}
