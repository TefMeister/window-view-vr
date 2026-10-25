// Capture.cs -- one window's picture for the headset: the window's client area under our header strip (the drag handle), a
// resize grip in the corner, and the opacity baked into every pixel's alpha. Pixels come out BGRA, top-down, tightly packed,
// which is what the layer expects (shared.h).
// 1.5: the client area comes from Windows Graphics Capture (window_view_capture.dll), which copies the picture the desktop
// already has instead of making the window draw itself again (PrintWindow cost 29-40 ms a picture for the slider apps, so
// sliders trailed behind in the headset: Tefa, "the sliders to somehow move smoothly not trail behind"); PrintWindow stays as
// the fall-back when the capture cannot start. The desk cursor is drawn by the layer now (Pointer.cs).
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
[StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

static class Wgc {
    [DllImport("window_view_capture.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int wvc_supported();
    [DllImport("window_view_capture.dll", CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr wvc_open(IntPtr hwnd);
    [DllImport("window_view_capture.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int wvc_grab(IntPtr c, IntPtr outBuf, int maxBytes, out int w, out int h);
    [DllImport("window_view_capture.dll", CallingConvention = CallingConvention.Cdecl)] public static extern void wvc_close(IntPtr c);
    static int ok = -1;
    public static bool Available {
        get {
            if (ok < 0) { try { ok = wvc_supported() == 1 ? 1 : 0; } catch (Exception) { ok = 0; } }   // DLL missing or Windows too old: PrintWindow
            return ok == 1;
        }
    }
}

static class Native {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hwnd, ref POINT p);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    public const uint SRCCOPY = 0x00CC0020, PW_RENDERFULLCONTENT = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; public uint colors;
    }
}

// a 32-bit top-down DIB with its own memory DC, remade when the size changes
class Dib : IDisposable {
    public IntPtr Dc = IntPtr.Zero, Bits = IntPtr.Zero;
    public int W, H;
    IntPtr bmp = IntPtr.Zero, old = IntPtr.Zero;

    public bool Ensure(int w, int h) {
        if (Dc != IntPtr.Zero && w == W && h == H) return true;
        Free();
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        Dc = Native.CreateCompatibleDC(screen);
        var bmi = new Native.BITMAPINFO();
        bmi.biSize = 40;
        bmi.biWidth = w;
        bmi.biHeight = -h;   // top-down
        bmi.biPlanes = 1;
        bmi.biBitCount = 32;
        bmp = Native.CreateDIBSection(screen, ref bmi, 0, out Bits, IntPtr.Zero, 0);
        Native.ReleaseDC(IntPtr.Zero, screen);
        if (bmp == IntPtr.Zero) { Free(); return false; }
        old = Native.SelectObject(Dc, bmp);
        W = w;
        H = h;
        return true;
    }
    void Free() {
        if (Dc != IntPtr.Zero) { if (old != IntPtr.Zero) Native.SelectObject(Dc, old); Native.DeleteDC(Dc); }
        if (bmp != IntPtr.Zero) Native.DeleteObject(bmp);
        Dc = bmp = old = Bits = IntPtr.Zero;
        W = H = 0;
    }
    public void Dispose() { Free(); }
}

class Capture : IDisposable {
    public const int HEADER = 28, GRIP = 18;   // pixels; the layout maths in Desk.cs uses the same numbers
    static readonly Font TitleFont = new Font("Segoe UI", 10f, FontStyle.Bold);
    static readonly Brush HeaderBrush = new SolidBrush(Color.FromArgb(40, 44, 52));
    static readonly Pen GripPen = new Pen(Color.FromArgb(160, 160, 160), 2f);
    // v1.2: twice the size it was, with a thick dark outline, and yellow while the cursor is between windows (drawn on the
    // nearest edge then): a 19-pixel arrow was about a centimetre across at arm's length and easy to lose
    static readonly Point[] Arrow = { new Point(0, 0), new Point(0, 32), new Point(8, 24), new Point(14, 38), new Point(18, 36), new Point(12, 22), new Point(22, 22) };
    static readonly Pen ArrowPen = new Pen(Color.Black, 3f);
    static readonly Brush OutsideBrush = new SolidBrush(Color.FromArgb(255, 210, 40));
    readonly Dib full = new Dib(), pic = new Dib();
    public byte[] Pixels = new byte[0];
    public int Width, Height, ClientW, ClientH;
    // 1.5: Graphics Capture state
    IntPtr wgc = IntPtr.Zero, wgcHwnd = IntPtr.Zero;
    IntPtr client = IntPtr.Zero;                 // the newest client-area picture (BGRA), unmanaged, MAX_W x MAX_H at most
    int clientW, clientH;
    bool haveClient;
    float lastOpacity = -1;
    string lastTitle;
    const int CLIENT_BYTES = Shared.MAX_W * Shared.MAX_H * 4;

    // false = nothing new (the window is minimised, gone, or could not be drawn); the last picture should stay up
    public bool Grab(IntPtr hwnd, string title, float opacity, Point? cursor, bool outside) {
        if (!Native.IsWindow(hwnd) || Native.IsIconic(hwnd)) return false;
        if (Wgc.Available) {
            if (wgc != IntPtr.Zero && wgcHwnd != hwnd) { Wgc.wvc_close(wgc); wgc = IntPtr.Zero; haveClient = false; }
            if (wgc == IntPtr.Zero) { wgc = Wgc.wvc_open(hwnd); wgcHwnd = hwnd; }
            if (wgc != IntPtr.Zero) {
                if (client == IntPtr.Zero) client = Marshal.AllocHGlobal(CLIENT_BYTES);
                int w, h;
                int r = Wgc.wvc_grab(wgc, client, CLIENT_BYTES, out w, out h);
                if (r == 1) { clientW = w; clientH = h; haveClient = true; }
                else if (r < 0) { Wgc.wvc_close(wgc); wgc = IntPtr.Zero; haveClient = false; }
                if (wgc != IntPtr.Zero) {
                    if (!haveClient || (r != 1 && opacity == lastOpacity && title == lastTitle)) return false;   // nothing changed
                    return Compose(title, opacity);
                }
            }
        }
        return GrabPrintWindow(hwnd, title, opacity, cursor, outside);
    }

    // the Graphics Capture picture under the header strip
    bool Compose(string title, float opacity) {
        int cw = Math.Min(clientW, Shared.MAX_W), ch = Math.Min(clientH, Shared.MAX_H - HEADER);
        int w = cw, h = ch + HEADER;
        if (!pic.Ensure(w, h)) return false;
        unsafe {
            byte* src = (byte*)client, dst = (byte*)pic.Bits + HEADER * w * 4;
            for (int y = 0; y < ch; ++y) Buffer.MemoryCopy(src + (long)y * clientW * 4, dst + (long)y * w * 4, (long)w * 4, (long)cw * 4);
        }
        return Finish(w, h, cw, ch, title, opacity, null, false);
    }

    bool GrabPrintWindow(IntPtr hwnd, string title, float opacity, Point? cursor, bool outside) {
        RECT wr, cr;
        if (!Native.IsWindow(hwnd) || Native.IsIconic(hwnd) || !Native.GetWindowRect(hwnd, out wr) || !Native.GetClientRect(hwnd, out cr)) return false;
        int fw = wr.Right - wr.Left, fh = wr.Bottom - wr.Top, cw = cr.Right, ch = cr.Bottom;
        if (fw < 8 || fh < 8 || cw < 8 || ch < 8) return false;
        var org = new POINT(0, 0);
        Native.ClientToScreen(hwnd, ref org);
        int ox = org.X - wr.Left, oy = org.Y - wr.Top;   // where the client area sits inside the whole window
        if (!full.Ensure(fw, fh) || !Native.PrintWindow(hwnd, full.Dc, Native.PW_RENDERFULLCONTENT)) return false;
        cw = Math.Min(cw, Shared.MAX_W);
        ch = Math.Min(ch, Shared.MAX_H - HEADER);
        int w = cw, h = ch + HEADER;
        if (!pic.Ensure(w, h)) return false;
        Native.BitBlt(pic.Dc, 0, HEADER, cw, ch, full.Dc, ox, oy, Native.SRCCOPY);
        return Finish(w, h, cw, ch, title, opacity, cursor, outside);
    }

    // the header strip, the grip, (the old in-picture cursor), the opacity, and out into Pixels
    bool Finish(int w, int h, int cw, int ch, string title, float opacity, Point? cursor, bool outside) {
        lastOpacity = opacity;
        lastTitle = title;
        using (var bmp = new Bitmap(w, h, w * 4, PixelFormat.Format32bppArgb, pic.Bits))
        using (var g = Graphics.FromImage(bmp)) {
            g.FillRectangle(HeaderBrush, 0, 0, w, HEADER);
            g.DrawString(title, TitleFont, Brushes.White, 8, 5);
            for (int i = 1; i <= 3; ++i) g.DrawLine(GripPen, w - 2, h - 2 - i * 5, w - 2 - i * 5, h - 2);   // the grip: three diagonal lines
            if (cursor.HasValue) {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TranslateTransform(cursor.Value.X, cursor.Value.Y);
                g.DrawPolygon(ArrowPen, Arrow);
                g.FillPolygon(outside ? OutsideBrush : Brushes.White, Arrow);
            }
            g.Flush();
        }
        int n = w * h;
        if (Pixels.Length < n * 4) Pixels = new byte[n * 4];
        uint a = (uint)Math.Round(255 * Math.Max(0.05f, Math.Min(1f, opacity))) << 24;   // the window is opaque; the opacity is ours
        unsafe {
            uint* s = (uint*)pic.Bits;
            fixed (byte* d0 = Pixels) {
                uint* d = (uint*)d0;
                for (int i = 0; i < n; ++i) d[i] = (s[i] & 0x00FFFFFFu) | a;
            }
        }
        Width = w;
        Height = h;
        ClientW = cw;
        ClientH = ch;
        return true;
    }
    public void Dispose() {
        if (wgc != IntPtr.Zero) { Wgc.wvc_close(wgc); wgc = IntPtr.Zero; }
        if (client != IntPtr.Zero) { Marshal.FreeHGlobal(client); client = IntPtr.Zero; }
        full.Dispose(); pic.Dispose();
    }
}
