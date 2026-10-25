// Skin.cs -- the look shared by the helper apps (2026-10-25). The same file in three places, keep them the same:
// hand-poser-vr/app/Skin.cs (the one to edit), window-view-vr/app/Skin.cs, visceral-re8-vr/dev-archive/native/zones-app/app/Skin.cs.
//
//   FancySlider  a slider drawn by us. The stock Windows TrackBar paints itself white for a moment before its dark colours, and
//                Window View photographs the window many times a second (PrintWindow), so the white moment showed in the
//                headset and on the desktop as random flicker (Tefa: "a quick white flicker, that's on the hand poser as well").
//                This one paints in one go into a buffer: a rounded track, a glowing gradient fill (from zero on a signed
//                slider), quarter dots, and a round thumb that swells and glows while held. Click anywhere to jump and drag,
//                the wheel and the arrow keys step, Page Up / Down take big steps, Home / End go to the ends.
//   Backdrop     the window's background: the app's own icon, finely pixelated and faint, STILL (Tefa: "make the window
//                background have a pixelated image of the ... icon"; the scan lines and rolling light came off on 2026-10-25
//                for the headset's sake: "we need to lose the animations").
//   SkinPanel    a panel that paints the backdrop at its own place in the window, then its Tint (a see-through colour) over it.
//                Children with a Transparent BackColor show it through.
//   SkinButton, SkinCheckBox, SkinRadioButton: take the clicks Window View sends from the headset (see below).
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

// 2026-10-25 (Tefa: "please make these windows as little performance hungry as they can, we need to lose the animations ...
// same with the scan lines for other apps, they do affect performance in vr quite a bit"): a STILL picture now. The icon, finely
// pixelated and faint, with darker corners, worked out once per window size and never again. No timer, no scan lines, no
// rolling light: the window changes only when its own controls do, so Window View (which copies a window only when it
// changed) has nothing to copy while nobody touches it. Before, the band of light changed the window 20 times a second, and
// each change was a fresh picture for the headset.
static class Backdrop {
    const int BLOCK = 6;                 // screen pixels per icon pixel
    const float FILL = 0.86f;            // the picture's size as a share of the window's smaller side
    const float PIC_STRENGTH = 0.26f;    // how faint the picture is
    const float VIGNETTE = 1.0f;         // corner darkening

    static Bitmap icon;                  // the icon's biggest picture
    static Form owner;
    static Color back;
    static int W, H;
    static Bitmap page;

    public static void Init(Form form, string iconPath, Color background) {
        owner = form; back = background;
        try { icon = LargestPicture(iconPath); } catch (Exception) { icon = null; }
        form.Resize += (s, e) => { if (Rebuild()) form.Invalidate(true); };
        Rebuild();
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

    // the still picture for this window size; false = nothing to do (same size, or minimised)
    static bool Rebuild() {
        if (owner == null) return false;
        Size sz = owner.ClientSize;
        if (sz.Width < 1 || sz.Height < 1 || (page != null && sz.Width == W && sz.Height == H)) return false;
        W = sz.Width; H = sz.Height;
        int side = (int)(Math.Min(W, H) * FILL), n = Math.Max(1, side / BLOCK);
        side = n * BLOCK;
        int left = (W - side) / 2, top = (H - side) / 2;
        int[] ip = null;
        if (icon != null) using (var tiny = new Bitmap(n, n, PixelFormat.Format32bppArgb)) {
            using (var g = Graphics.FromImage(tiny)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(icon, new Rectangle(0, 0, n, n)); }
            ip = Pixels(tiny);
        }
        var px = new int[W * H];
        float br = back.R, bg = back.G, bb = back.B;
        for (int y = 0; y < H; y++) {
            float dy = (float)y / H - 0.5f;
            int iy = y - top;
            for (int x = 0; x < W; x++) {
                float dx = (float)x / W - 0.5f;
                float vig = Math.Max(0f, Math.Min(1f, 1f - (dx * dx + dy * dy) * VIGNETTE));
                float pr = 0, pg = 0, pb = 0;
                int ix = x - left;
                if (ip != null && ix >= 0 && iy >= 0 && ix < side && iy < side) {
                    int c = ip[(iy / BLOCK) * n + ix / BLOCK];
                    float a = ((c >> 24) & 255) / 255f;
                    pr = ((c >> 16) & 255) * a; pg = ((c >> 8) & 255) * a; pb = (c & 255) * a;
                }
                px[y * W + x] = Pack((br + pr * PIC_STRENGTH) * vig, (bg + pg * PIC_STRENGTH) * vig, (bb + pb * PIC_STRENGTH) * vig);
            }
        }
        var old = page;
        page = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
        var data = page.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        for (int y = 0; y < H; y++) System.Runtime.InteropServices.Marshal.Copy(px, y * W, data.Scan0 + y * data.Stride, W);
        page.UnlockBits(data);
        if (old != null) old.Dispose();
        return true;
    }

    static int Pack(float r, float g, float b) {
        int ir = r > 255 ? 255 : (int)r, ig = g > 255 ? 255 : (int)g, ib = b > 255 ? 255 : (int)b;
        return unchecked((int)0xFF000000) | (ir << 16) | (ig << 8) | ib;
    }

    // paint the part of the background that lies under control c; only the clip rectangle is copied
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

    // (until 2026-10-25 this switched on WS_EX_COMPOSITED, so the moving band never flickered; that repaints the WHOLE window
    // whenever any control in it changes, e.g. the Hand Poser's live numbers ten times a second. With nothing moving it is
    // not needed: every control here paints double-buffered by itself. Kept as a pass-through so the apps need no change.)
    public static CreateParams Composited(CreateParams cp) { return cp; }
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
