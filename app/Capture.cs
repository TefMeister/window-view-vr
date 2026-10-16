// Capture.cs -- one window's picture for the headset: PrintWindow of the whole window into a bitmap we own, its client
// area copied under our header strip (the drag handle), a resize grip in the corner, the desk cursor on top when it is
// over this window, and the opacity baked into every pixel's alpha. Pixels come out BGRA, top-down, tightly packed,
// which is what the layer expects (shared.h).
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
[StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

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
    static readonly Point[] Arrow = { new Point(0, 0), new Point(0, 16), new Point(4, 12), new Point(7, 19), new Point(9, 18), new Point(6, 11), new Point(11, 11) };
    readonly Dib full = new Dib(), pic = new Dib();
    public byte[] Pixels = new byte[0];
    public int Width, Height, ClientW, ClientH;

    // false = nothing new (the window is minimised, gone, or could not be drawn); the last picture should stay up
    public bool Grab(IntPtr hwnd, string title, float opacity, Point? cursor) {
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
        using (var bmp = new Bitmap(w, h, w * 4, PixelFormat.Format32bppArgb, pic.Bits))
        using (var g = Graphics.FromImage(bmp)) {
            g.FillRectangle(HeaderBrush, 0, 0, w, HEADER);
            g.DrawString(title, TitleFont, Brushes.White, 8, 5);
            for (int i = 1; i <= 3; ++i) g.DrawLine(GripPen, w - 2, h - 2 - i * 5, w - 2 - i * 5, h - 2);   // the grip: three diagonal lines
            if (cursor.HasValue) {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TranslateTransform(cursor.Value.X, cursor.Value.Y);
                g.FillPolygon(Brushes.White, Arrow);
                g.DrawPolygon(Pens.Black, Arrow);
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
    public void Dispose() { full.Dispose(); pic.Dispose(); }
}
