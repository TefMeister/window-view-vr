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
    public string Exe = "", Title = "";
    public float X, Y, Scale = Desk.DEFAULT_SCALE, Opacity = 1f;
    public bool Shown = true;
    public int Slot = -1;
    public int PicW, PicH;           // the last picture's size, set by the capture loop
    public bool Live { get { return Hwnd != IntPtr.Zero; } }
    public float Left { get { return X - PicW * Scale / 2; } }
    public float Top { get { return Y + PicH * Scale / 2; } }
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
    public const float CURSOR_SPEED = 0.0006f;   // metres of desk per mouse pixel (1 mouse px = 1 window px at the default size)
    public const float REACH_X = 0.9f, REACH_Y = 0.6f;   // how far the cursor may go, metres
    public readonly object Lock = new object();
    public readonly List<Placement> Windows = new List<Placement>();   // last = on top
    public float CursorX, CursorY;
    static readonly string File = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowViewVR", "layout.txt");

    public Hit HitTest(float cx, float cy) {
        var hit = new Hit();
        for (int i = Windows.Count - 1; i >= 0; --i) {
            var p = Windows[i];
            if (!p.Shown || !p.Live || p.PicW == 0) continue;
            int px = (int)Math.Floor((cx - p.Left) / p.Scale), py = (int)Math.Floor((p.Top - cy) / p.Scale);
            if (px < 0 || py < 0 || px >= p.PicW || py >= p.PicH) continue;
            hit.P = p;
            hit.PicX = px;
            hit.PicY = py;
            hit.Kind = py < Capture.HEADER ? HitKind.Header : px >= p.PicW - Capture.GRIP && py >= p.PicH - Capture.GRIP ? HitKind.Grip : HitKind.Body;
            return hit;
        }
        return hit;
    }
    public void MoveCursor(int dx, int dy) {
        CursorX = Math.Max(-REACH_X, Math.Min(REACH_X, CursorX + dx * CURSOR_SPEED));
        CursorY = Math.Max(-REACH_Y, Math.Min(REACH_Y, CursorY - dy * CURSOR_SPEED));
    }
    public Placement Add(IntPtr hwnd, string exe, string title) {
        var p = new Placement();
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
            lines.Add(string.Join("|", new[] { p.Exe, p.Title.Replace("|", " "), p.X.ToString(ci), p.Y.ToString(ci), p.Scale.ToString(ci), p.Opacity.ToString(ci), p.Shown ? "1" : "0" }));
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
            p.Scale = Math.Max(MIN_SCALE, Math.Min(MAX_SCALE, p.Scale == 0 ? DEFAULT_SCALE : p.Scale));
            p.Opacity = Math.Max(0.1f, Math.Min(1f, p.Opacity == 0 ? 1f : p.Opacity));
            p.Slot = FreeSlot();
            if (p.Slot >= 0) Windows.Add(p);
        }
    }
}
