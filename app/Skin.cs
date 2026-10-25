// Skin.cs -- the look shared by the helper apps (2026-10-25). The same file in three places, keep them the same:
// hand-poser-vr/app/Skin.cs (the one to edit), window-view-vr/app/Skin.cs, visceral-re8-vr/dev-archive/native/zones-app/app/Skin.cs.
//
//   FancySlider  a slider drawn by us. The stock Windows TrackBar paints itself white for a moment before its dark colours, and
//                Window View photographs the window many times a second (PrintWindow), so the white moment showed in the
//                headset and on the desktop as random flicker (Tefa: "a quick white flicker, that's on the hand poser as well").
//                This one paints in one go into a buffer: a rounded track, a glowing gradient fill (from zero on a signed
//                slider), quarter dots, and a round thumb that swells and glows while held. Click anywhere to jump and drag,
//                the wheel and the arrow keys step, Page Up / Down take big steps, Home / End go to the ends.
//   Backdrop     the window's background: the app's own icon, finely pixelated and faint, with the Lanes theme's scan lines
//                and rolling light (Tefa: "make the window background have a pixelated image of the ... icon", then "in the
//                same style as our Lanes plugin background, only dim and coloured, not green").
//   SkinPanel    a panel that paints the backdrop at its own place in the window, then its Tint (a see-through colour) over it.
//                Children with a Transparent BackColor show it through.
//   SkinButton, SkinCheckBox, SkinRadioButton: take the clicks Window View sends from the headset (see below).
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

// In the style of the Lanes terminal theme (lanes-plugin theme/green-monitor-lanes/lanes.hlsl), dim and in the icon's own
// colours instead of green: the icon finely pixelated and faint, scan lines on the picture every 4 pixels, a soft band of light
// rolling from the top to the bottom every 9 seconds (the theme's ROLL_SECONDS, ROLL_HEIGHT and its -0.2 .. 1.2 travel), and
// darker corners. The still part is worked out once per window size; each frame only the rows under the band are redone.
static class Backdrop {
    const int BLOCK = 6;                 // screen pixels per icon pixel (finer than the first version's ~17)
    const float FILL = 0.86f;            // the picture's size as a share of the window's smaller side
    const float PIC_STRENGTH = 0.26f;    // how faint the picture is (lanes.hlsl 0.20, a touch brighter: no glow here)
    const float ROLL_STRENGTH = 0.30f;   // how much the band lights it up (lanes.hlsl 0.25)
    const int STRIPE_PERIOD = 4;         // pixels per scan-line cycle, as lanes.hlsl
    const float STRIPE_DARKEN = 0.55f;   // how dark the dark line is, as lanes.hlsl
    const float ROLL_SECONDS = 9f, ROLL_HEIGHT = 0.10f;   // as lanes.hlsl
    const float VIGNETTE = 1.0f;         // corner darkening (lanes.hlsl 1.35; gentler, the window has text to read)
    const int FRAME_MS = 50;             // the band moves 20 times a second
    const float BAND_REACH = 0.19f;      // rows further than this (share of the height) from the band's middle are left alone (light there < 3 %)

    static Bitmap icon;                  // the icon's biggest picture
    static Form owner;
    static Color back;
    static int W, H;
    static int[] still, lit, now;        // the background without the band, the band's extra light, this frame (0xAARRGGBB)
    static Bitmap page;
    static int bandA = -1, bandB = -1;   // the rows the band covered last frame
    static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    static Timer timer;

    public static void Init(Form form, string iconPath, Color background) {
        owner = form; back = background;
        try { icon = LargestPicture(iconPath); } catch (Exception) { icon = null; }
        form.Resize += (s, e) => { Rebuild(); form.Invalidate(true); };
        Rebuild();
        timer = new Timer { Interval = FRAME_MS };
        timer.Tick += (s, e) => Step();
        timer.Start();
    }

    // The biggest picture inside an .ico. Its large sizes are stored as PNG, which .NET Framework's Icon class draws blank, so
    // the file's directory is read and a PNG entry is opened as an image; an old-style entry falls back to the Icon class.
    public static Bitmap LargestPicture(string path) {
        byte[] f = System.IO.File.ReadAllBytes(path);
        int count = BitConverter.ToUInt16(f, 4), best = -1, bestSide = 0;
        for (int i = 0; i < count; i++) {
            int e = 6 + 16 * i, side = f[e] == 0 ? 256 : f[e];
            if (side > bestSide) { bestSide = side; best = e; }
        }
        if (best < 0) throw new System.IO.InvalidDataException("no pictures in " + path);
        int size = BitConverter.ToInt32(f, best + 8), at = BitConverter.ToInt32(f, best + 12);
        bool png = f[at] == 0x89 && f[at + 1] == 0x50 && f[at + 2] == 0x4E && f[at + 3] == 0x47;
        if (png) using (var ms = new System.IO.MemoryStream(f, at, size)) using (var img = Image.FromStream(ms)) return new Bitmap(img);
        using (var ico = new Icon(path, bestSide, bestSide)) return ico.ToBitmap();
    }

    static int[] Pixels(Bitmap b) {
        var data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new int[b.Width * b.Height];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, px, 0, px.Length);
        b.UnlockBits(data);
        return px;
    }

    static void Rebuild() {
        if (owner == null) return;
        Size sz = owner.ClientSize;
        if (sz.Width < 1 || sz.Height < 1) return;
        W = sz.Width; H = sz.Height;
        int side = (int)(Math.Min(W, H) * FILL), n = Math.Max(1, side / BLOCK);
        side = n * BLOCK;
        int left = (W - side) / 2, top = (H - side) / 2;
        int[] ip = null;
        if (icon != null) using (var tiny = new Bitmap(n, n, PixelFormat.Format32bppArgb)) {
            using (var g = Graphics.FromImage(tiny)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(icon, new Rectangle(0, 0, n, n)); }
            ip = Pixels(tiny);
        }
        still = new int[W * H]; lit = new int[W * H]; now = new int[W * H];
        float br = back.R, bg = back.G, bb = back.B;
        for (int y = 0; y < H; y++) {
            float dy = (float)y / H - 0.5f;
            float strip = (y % STRIPE_PERIOD) < STRIPE_PERIOD / 2 ? 1f - STRIPE_DARKEN : 1f;
            int iy = y - top;
            for (int x = 0; x < W; x++) {
                float dx = (float)x / W - 0.5f;
                float vig = Math.Max(0f, Math.Min(1f, 1f - (dx * dx + dy * dy) * VIGNETTE));
                float pr = 0, pg = 0, pb = 0;
                int ix = x - left;
                if (ip != null && ix >= 0 && iy >= 0 && ix < side && iy < side) {
                    int c = ip[(iy / BLOCK) * n + ix / BLOCK];
                    float a = ((c >> 24) & 255) / 255f * strip;
                    pr = ((c >> 16) & 255) * a; pg = ((c >> 8) & 255) * a; pb = (c & 255) * a;
                }
                int i = y * W + x;
                still[i] = Pack((br + pr * PIC_STRENGTH) * vig, (bg + pg * PIC_STRENGTH) * vig, (bb + pb * PIC_STRENGTH) * vig);
                lit[i] = Pack(pr * ROLL_STRENGTH * vig, pg * ROLL_STRENGTH * vig, pb * ROLL_STRENGTH * vig);
            }
        }
        Array.Copy(still, now, still.Length);
        var old = page;
        page = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
        Write(0, H);
        if (old != null) old.Dispose();
        bandA = bandB = -1;
        Step();
    }

    static int Pack(float r, float g, float b) {
        int ir = r > 255 ? 255 : (int)r, ig = g > 255 ? 255 : (int)g, ib = b > 255 ? 255 : (int)b;
        return unchecked((int)0xFF000000) | (ir << 16) | (ig << 8) | ib;
    }

    static void Write(int y0, int y1) {   // rows y0 .. y1 of `now` into the page bitmap
        if (y1 <= y0) return;
        var data = page.LockBits(new Rectangle(0, y0, W, y1 - y0), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        for (int y = y0; y < y1; y++) System.Runtime.InteropServices.Marshal.Copy(now, y * W, data.Scan0 + (y - y0) * data.Stride, W);
        page.UnlockBits(data);
    }

    static void Step() {
        if (page == null || owner == null || owner.WindowState == FormWindowState.Minimized) return;
        float t = (float)clock.Elapsed.TotalSeconds;
        float rollY = (t / ROLL_SECONDS - (float)Math.Floor(t / ROLL_SECONDS)) * 1.4f - 0.2f;
        int a = Math.Max(0, (int)((rollY - BAND_REACH) * H)), b = Math.Min(H, (int)((rollY + BAND_REACH) * H));
        bool shown = b > a;
        int y0 = shown ? a : bandA, y1 = shown ? b : bandB;
        if (bandA >= 0) { y0 = Math.Min(y0 < 0 ? bandA : y0, bandA); y1 = Math.Max(y1, bandB); }
        if (y0 < 0 || y1 <= y0) { bandA = bandB = -1; return; }
        for (int y = y0; y < y1; y++) {
            int row = y * W;
            float d = ((float)y / H - rollY) / ROLL_HEIGHT;
            float k = (float)Math.Exp(-d * d);
            if (!shown || y < a || y >= b || k < 0.004f) { Array.Copy(still, row, now, row, W); continue; }
            for (int x = 0; x < W; x++) {
                int i = row + x, l = lit[i];
                if ((l & 0xFFFFFF) == 0) { now[i] = still[i]; continue; }
                int s = still[i];
                int r = ((s >> 16) & 255) + (int)(((l >> 16) & 255) * k), g = ((s >> 8) & 255) + (int)(((l >> 8) & 255) * k), bl = (s & 255) + (int)((l & 255) * k);
                now[i] = unchecked((int)0xFF000000) | ((r > 255 ? 255 : r) << 16) | ((g > 255 ? 255 : g) << 8) | (bl > 255 ? 255 : bl);
            }
        }
        Write(y0, y1);
        if (shown) { bandA = a; bandB = b; } else { bandA = bandB = -1; }
        owner.Invalidate(new Rectangle(0, y0, W, y1 - y0), true);
    }

    // paint the part of the background that lies under control c; only the clip rectangle is copied, so a frame that
    // changes a strip of the window costs that strip, not the whole picture once per control
    public static void PaintUnder(Control c, Graphics g, Rectangle clip) {
        if (page == null || owner == null || !owner.IsHandleCreated || !c.IsHandleCreated) { g.Clear(back); return; }
        Point off = owner.PointToClient(c.PointToScreen(Point.Empty));
        var src = Rectangle.Intersect(new Rectangle(clip.X + off.X, clip.Y + off.Y, clip.Width, clip.Height), new Rectangle(0, 0, W, H));
        if (src.Width <= 0 || src.Height <= 0) return;
        var oldMode = g.CompositingMode;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.DrawImage(page, new Rectangle(src.X - off.X, src.Y - off.Y, src.Width, src.Height), src, GraphicsUnit.Pixel);
        g.CompositingMode = oldMode;
    }

    // the whole window painted in one go into a buffer (WS_EX_COMPOSITED), so the moving band never makes anything flicker
    public static CreateParams Composited(CreateParams cp) { cp.ExStyle |= 0x02000000; return cp; }
}

class SkinPanel : Panel {
    Color tint = Color.Transparent;
    public Color Tint { get { return tint; } set { tint = value; Invalidate(true); } }

    public SkinPanel() {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        // a scrolling panel moves its pixels; the backdrop stays put, so repaint after every scroll
        Scroll += (s, e) => Invalidate(true);
        MouseWheel += (s, e) => Invalidate(true);
    }

    protected override void OnPaintBackground(PaintEventArgs e) {
        Backdrop.PaintUnder(this, e.Graphics, e.ClipRectangle);
        if (tint.A > 0) using (var br = new SolidBrush(tint)) e.Graphics.FillRectangle(br, e.ClipRectangle);
    }
}

// a label that never flickers while its text changes over the backdrop
class SkinLabel : Label {
    public SkinLabel() {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }
}

// Buttons and tick boxes that take a click Window View sends. Window View clicks by posting the mouse-down and mouse-up messages
// to the control under its VR cursor; the stock ButtonBase only clicks when the control is ALSO the topmost window at that spot
// on the real desktop (WindowFromPoint), and a window used in the headset is usually behind others there, so nothing happened
// (Tefa 2026-10-25: "clicking on any of these does not bring up the sliders in hand poser"; the drawn sliders never checked).
// These click on a down and an up inside them, the stock way first, and themselves only when the stock way did not.
class SkinButton : Button {
    bool down, clicked;
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) down = true; }
    protected override void OnClick(EventArgs e) { clicked = true; base.OnClick(e); }
    protected override void OnMouseUp(MouseEventArgs e) {
        bool was = down; down = false; clicked = false;
        base.OnMouseUp(e);
        if (was && !clicked && e.Button == MouseButtons.Left && Enabled && ClientRectangle.Contains(e.Location)) OnClick(EventArgs.Empty);
    }
}
class SkinCheckBox : CheckBox {
    bool down, clicked;
    public SkinCheckBox() { BackColor = Color.Transparent; }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) down = true; }
    protected override void OnClick(EventArgs e) { clicked = true; base.OnClick(e); }
    protected override void OnMouseUp(MouseEventArgs e) {
        bool was = down; down = false; clicked = false;
        base.OnMouseUp(e);
        if (was && !clicked && e.Button == MouseButtons.Left && Enabled && ClientRectangle.Contains(e.Location)) OnClick(EventArgs.Empty);
    }
}
class SkinRadioButton : RadioButton {
    bool down, clicked;
    public SkinRadioButton() { BackColor = Color.Transparent; }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) down = true; }
    protected override void OnClick(EventArgs e) { clicked = true; base.OnClick(e); }
    protected override void OnMouseUp(MouseEventArgs e) {
        bool was = down; down = false; clicked = false;
        base.OnMouseUp(e);
        if (was && !clicked && e.Button == MouseButtons.Left && Enabled && ClientRectangle.Contains(e.Location)) OnClick(EventArgs.Empty);
    }
}

class FancySlider : Control {
    int min = 0, max = 100, val = 0;
    bool hot, dragging;
    public int SmallChange = 1, LargeChange = 10;
    public bool Signed;   // the fill runs from zero (the middle) to the thumb, and a notch marks zero

    public static readonly Color TrackBack = Color.FromArgb(18, 20, 27), TrackEdge = Color.FromArgb(78, 84, 106);
    public static readonly Color FillA = Color.FromArgb(56, 196, 232), FillB = Color.FromArgb(150, 108, 245);
    public static readonly Color ThumbFace = Color.FromArgb(244, 246, 252), Off = Color.FromArgb(92, 96, 112);

    public event EventHandler ValueChanged;   // any change, by the user or by code
    public event EventHandler Scroll;         // a change by the user only
    public event Action Pressed, Released;    // the mouse took hold / let go

    public FancySlider() {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Height = 32;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    public int Minimum { get { return min; } set { min = value; if (max < min) max = min; Value = val; Invalidate(); } }
    public int Maximum { get { return max; } set { max = value; if (min > max) min = max; Value = val; Invalidate(); } }
    public int Value {
        get { return val; }
        set { int v = Math.Max(min, Math.Min(max, value)); if (v == val) return; val = v; Invalidate(); if (ValueChanged != null) ValueChanged(this, EventArgs.Empty); }
    }

    void UserSet(int v) {
        int before = val;
        Value = v;
        if (val != before && Scroll != null) Scroll(this, EventArgs.Empty);
    }

    const int PAD = 14, TRACK_H = 8, THUMB_R = 9, THUMB_R_HOT = 11;
    int Left0 { get { return PAD; } }
    int Width0 { get { return Math.Max(1, ClientSize.Width - 2 * PAD); } }
    float XOf(int v) { return Left0 + (max == min ? 0f : (v - min) * (float)Width0 / (max - min)); }
    int ValueAt(int x) { return max == min ? min : min + (int)Math.Round((x - Left0) * (double)(max - min) / Width0); }

    static GraphicsPath Pill(RectangleF r) {
        var p = new GraphicsPath();
        float d = Math.Min(r.Height, r.Width);
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 90, 180);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        p.CloseFigure();
        return p;
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cy = ClientSize.Height / 2f;
        var track = new RectangleF(Left0, cy - TRACK_H / 2f, Width0, TRACK_H);
        bool on = Enabled;

        using (var p = Pill(track)) {
            using (var br = new SolidBrush(TrackBack)) g.FillPath(br, p);
            using (var pen = new Pen(TrackEdge, 1f)) g.DrawPath(pen, p);
        }
        // quarter dots under the track
        using (var dot = new SolidBrush(Color.FromArgb(on ? 110 : 50, TrackEdge)))
            for (int q = 1; q < 4; q++) { float x = Left0 + Width0 * q / 4f; g.FillEllipse(dot, x - 1.5f, cy + TRACK_H / 2f + 3, 3, 3); }

        float tx = XOf(val);
        float from = Signed && min < 0 && max > 0 ? XOf(0) : Left0;
        float a = Math.Min(from, tx), b = Math.Max(from, tx);
        if (b - a >= 1f) {
            var fill = new RectangleF(a, track.Y + 1, b - a, TRACK_H - 2);
            using (var p = Pill(fill)) {
                if (on) {
                    using (var glow = new Pen(Color.FromArgb(hot || dragging ? 90 : 50, FillA), 6f)) g.DrawPath(glow, p);
                    using (var br = new LinearGradientBrush(new RectangleF(Left0 - 1, 0, Width0 + 2, 1), FillA, FillB, LinearGradientMode.Horizontal)) g.FillPath(br, p);
                } else using (var br = new SolidBrush(Off)) g.FillPath(br, p);
            }
        }
        if (Signed && min < 0 && max > 0) {   // the zero notch
            float zx = XOf(0);
            using (var pen = new Pen(Color.FromArgb(on ? 200 : 90, ThumbFace), 2f)) g.DrawLine(pen, zx, track.Y - 4, zx, track.Bottom + 4);
        }

        float r = (hot || dragging) && on ? THUMB_R_HOT : THUMB_R;
        if (on && (hot || dragging || Focused)) {
            using (var halo = new SolidBrush(Color.FromArgb(dragging ? 80 : 45, FillB))) g.FillEllipse(halo, tx - r - 6, cy - r - 6, 2 * r + 12, 2 * r + 12);
        }
        var thumb = new RectangleF(tx - r, cy - r, 2 * r, 2 * r);
        using (var shade = new SolidBrush(Color.FromArgb(90, 0, 0, 0))) g.FillEllipse(shade, thumb.X + 1, thumb.Y + 2, thumb.Width, thumb.Height);
        using (var face = new LinearGradientBrush(thumb, on ? ThumbFace : Off, on ? Color.FromArgb(196, 204, 226) : Color.FromArgb(70, 74, 88), LinearGradientMode.Vertical))
            g.FillEllipse(face, thumb);
        Color ring = on ? (tx - Left0 < Width0 / 2f ? FillA : FillB) : TrackEdge;
        using (var pen = new Pen(ring, 3f)) g.DrawEllipse(pen, thumb.X + 1.5f, thumb.Y + 1.5f, thumb.Width - 3, thumb.Height - 3);
        using (var pip = new SolidBrush(on ? Color.FromArgb(60, 64, 82) : TrackEdge)) g.FillEllipse(pip, tx - 2, cy - 2, 4, 4);
    }

    protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnMouseDown(MouseEventArgs e) {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Enabled) return;
        Focus();
        dragging = true;
        Capture = true;
        if (Pressed != null) Pressed();
        UserSet(ValueAt(e.X));
    }
    protected override void OnMouseMove(MouseEventArgs e) {
        base.OnMouseMove(e);
        if (dragging) UserSet(ValueAt(e.X));
    }
    protected override void OnMouseUp(MouseEventArgs e) {
        base.OnMouseUp(e);
        if (!dragging) return;
        dragging = false;
        Capture = false;
        Invalidate();
        if (Released != null) Released();
    }
    protected override void OnMouseCaptureChanged(EventArgs e) {
        base.OnMouseCaptureChanged(e);
        if (dragging && !Capture) { dragging = false; Invalidate(); if (Released != null) Released(); }
    }
    protected override void OnMouseWheel(MouseEventArgs e) {
        base.OnMouseWheel(e);
        if (!Enabled) return;
        if (Pressed != null) Pressed();
        UserSet(val + Math.Sign(e.Delta) * SmallChange);
        if (Released != null) Released();
    }

    protected override bool IsInputKey(Keys k) {
        switch (k & Keys.KeyCode) {
        case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down: case Keys.PageUp: case Keys.PageDown: case Keys.Home: case Keys.End: return true;
        }
        return base.IsInputKey(k);
    }
    protected override void OnKeyDown(KeyEventArgs e) {
        base.OnKeyDown(e);
        if (!Enabled) return;
        switch (e.KeyCode) {
        case Keys.Left: case Keys.Down: UserSet(val - SmallChange); break;
        case Keys.Right: case Keys.Up: UserSet(val + SmallChange); break;
        case Keys.PageDown: UserSet(val - LargeChange); break;
        case Keys.PageUp: UserSet(val + LargeChange); break;
        case Keys.Home: UserSet(min); break;
        case Keys.End: UserSet(max); break;
        }
    }
}
