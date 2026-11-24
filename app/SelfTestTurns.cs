// SelfTestTurns.cs -- 1.15: checks of each window's own tilt and turn with no game, no headset and no window
// (`WindowView.exe --selftest-keys turns`), and the frame dump test/turn_test.py compares with the layer's own sums
// (`WindowView.exe --dump-frames`, against build\turn_test.exe, which compiles layer/face.inl). Nothing here touches the
// live shared blocks or the real layout file: the turns block is opened under a test name, the layout goes to a temp file.
using System;
using System.Globalization;
using System.IO;

static partial class SelfTest {
    static bool Near(V3 a, V3 b, float tol = 1e-4f) { return (a - b).Length < tol; }
    static V3 Cross(V3 a, V3 b) { return new V3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X); }
    static Placement TestWindow(float x, float y, float depth, float tilt, float turn) {
        return new Placement { Hwnd = (IntPtr)1, X = x, Y = y, Depth = depth, Tilt = tilt, Turn = turn, PicW = 800, PicH = 500, Scale = 0.0006f };
    }

    static void TurnsChecks() {
        // the block, read back at the C++ offsets of shared.h
        using (var t = new TurnsShared(@"Local\FlatToVR_WindowView_turns_selftest")) {
            Check("turns block is 72 bytes, tilt at 8, turn at 40", TurnsShared.SIZE == 72 && TurnsShared.TILT == 8 && TurnsShared.TURN == 40);
            Check("turns magic and version written", t.RawInt(0) == unchecked((int)0x4E525554u) && t.RawInt(4) == 1);
            t.Write(3, 30, -45);
            Check("slot 3's tilt / turn written in radians", Math.Abs(t.RawFloat(8 + 12) - (float)(Math.PI / 6)) < 1e-6 && Math.Abs(t.RawFloat(40 + 12) + (float)(Math.PI / 4)) < 1e-6);
            Check("the other slots untouched", t.RawFloat(8 + 8) == 0 && t.RawFloat(40 + 16) == 0);
            t.Clear();
            Check("Clear puts every slot back to none", t.RawFloat(8 + 12) == 0 && t.RawFloat(40 + 12) == 0);
        }
        foreach (bool room in new[] { false, true }) {
            string m = room ? " (room)" : " (follow)";
            var flat = WindowFrame.Of(TestWindow(0.2f, 0.1f, 1.3f, 0, 0), 1f, room);
            // a quarter turn right: the face looks where the window's right edge pointed; a quarter tilt back: the top goes away
            var q = WindowFrame.Of(TestWindow(0.2f, 0.1f, 1.3f, 0, 90), 1f, room);
            Check("turn +90: the face looks the old right way" + m, Near(q.Normal, flat.Right) && Near(q.Right, flat.Normal * -1) && Near(q.Up, flat.Up));
            var b = WindowFrame.Of(TestWindow(0.2f, 0.1f, 1.3f, 90, 0), 1f, room);
            Check("tilt +90: the top points away, the face up" + m, Near(b.Up, flat.Normal * -1) && Near(b.Normal, flat.Up) && Near(b.Right, flat.Right));
            Check("a turn never moves the middle" + m, Near(q.Centre, flat.Centre) && Near(b.Centre, flat.Centre));
            // which way is which, as seen from the eyes (0, 0, 0)
            var tb = WindowFrame.Of(TestWindow(0.2f, 0.1f, 1.3f, 25, 0), 1f, room);
            Check("tilt > 0: the top edge farther than the bottom" + m, tb.PointAt(0.5f, 0).Length > tb.PointAt(0.5f, 1).Length + 0.05f);
            var tr = WindowFrame.Of(TestWindow(0.2f, 0.1f, 1.3f, 0, 25), 1f, room);
            Check("turn > 0: the right edge farther than the left" + m, tr.PointAt(1, 0.5f).Length > tr.PointAt(0, 0.5f).Length + 0.05f);
            // every mix stays a proper frame, and a ray from the eyes to a spot on the window meets it at that very spot
            bool ortho = true, round = true; string why = "";
            foreach (float tilt in new[] { -70f, -20f, 0f, 35f, 75f })
                foreach (float turn in new[] { -75f, -30f, 0f, 15f, 60f }) {
                    var f = WindowFrame.Of(TestWindow(-0.3f, -0.15f, 1.6f, tilt, turn), 1.2f, room);
                    if (Math.Abs(f.Right.Length - 1) > 1e-4 || Math.Abs(f.Up.Length - 1) > 1e-4 || Math.Abs(V3.Dot(f.Right, f.Up)) > 1e-4 ||
                        !Near(Cross(f.Right, f.Up), f.Normal)) { ortho = false; why = tilt + "/" + turn; }
                    foreach (var uv in new[] { new[] { 0.5f, 0.5f }, new[] { 0.1f, 0.85f }, new[] { 0.93f, 0.2f } }) {
                        var spot = f.Centre + f.Right * ((uv[0] - 0.5f) * f.W) + f.Up * ((0.5f - uv[1]) * f.H);
                        var o = new V3(0.02f, -0.05f, 0.1f);
                        var d = spot - o; d = d * (1f / d.Length);
                        float u, v, t;
                        if (!f.Meet(o, d, out u, out v, out t) || Math.Abs(u - uv[0]) > 1e-3 || Math.Abs(v - uv[1]) > 1e-3) { round = false; why = tilt + "/" + turn + " at " + uv[0] + "," + uv[1]; }
                    }
                }
            Check("every tilt / turn mix is a right-handed frame" + m, ortho, why);
            Check("a ray to a spot meets the turned window at that spot" + m, round, why);
        }
        // the right stick while carrying
        var w = TestWindow(0, 0, 1, 0, 0);
        Pointer.TiltTurn(1f, 0f, 0.5f, w, null);
        Check("stick up, full, half a second: tilt +30", Math.Abs(w.Tilt - 30) < 1e-3 && w.Turn == 0, "tilt " + w.Tilt);
        Pointer.TiltTurn(0.5f, 0.9f, 0.5f, w, null);
        Check("a mostly-right push turns only", Math.Abs(w.Tilt - 30) < 1e-3 && w.Turn > 0, "tilt " + w.Tilt + " turn " + w.Turn);
        float turnWas = w.Turn;
        Pointer.TiltTurn(0.2f, -0.1f, 0.5f, w, null);
        Check("inside the dead zone nothing moves", Math.Abs(w.Tilt - 30) < 1e-3 && w.Turn == turnWas);
        for (int i = 0; i < 40; ++i) Pointer.TiltTurn(-1f, 0, 0.1f, w, null);
        Check("tilt stops at -75", w.Tilt == -Desk.MAX_TURN, "tilt " + w.Tilt);
        var w2 = TestWindow(0, 0, 1, 0, 0);
        Pointer.TiltTurn(0, -1f, 0.25f, w, w2);
        Check("both held windows turn together", Math.Abs(w2.Turn + 15) < 1e-3, "turn " + w2.Turn);
        // the layout keeps them (a temp file, never the real one)
        string real = Desk.File, tmp = Path.Combine(Path.GetTempPath(), "wv_selftest_layout_" + Environment.TickCount + ".txt");
        try {
            Desk.File = tmp;
            var desk = new Desk();
            var p = desk.Add(IntPtr.Zero, "test.exe", "Turned");
            p.Tilt = -12.5f; p.Turn = 33f;
            desk.Save();
            var back = new Desk(); back.Load();
            Check("the layout keeps tilt and turn", back.Windows.Count == 1 && back.Windows[0].Tilt == -12.5f && back.Windows[0].Turn == 33f);
            File.WriteAllText(tmp, "old.exe|Old|0.1|0|0.0006|1|1|C:\\x.exe|1.2\r\n");
            var old = new Desk(); old.Load();
            Check("an older layout loads flat", old.Windows.Count == 1 && old.Windows[0].Tilt == 0 && old.Windows[0].Turn == 0 && old.Windows[0].Depth == 1.2f);
            File.WriteAllText(tmp, "bad.exe|Bad|0|0|0.0006|1|1||1|400|NaN\r\n");
            var bad = new Desk(); bad.Load();
            Check("a wild tilt is held to 75, rubbish to 0", bad.Windows.Count == 1 && bad.Windows[0].Tilt == Desk.MAX_TURN && bad.Windows[0].Turn == 0);
        } finally { Desk.File = real; try { File.Delete(tmp); } catch (Exception) { } }
    }

    // the grid test/turn_test.py compares with build\turn_test.exe (the layer's face.inl): one line per case
    public static int DumpFrames() {
        var ci = CultureInfo.InvariantCulture;
        foreach (int room in new[] { 0, 1 })
            foreach (float dist in new[] { 1f, 1.3f })
                foreach (float x in new[] { -0.4f, 0f, 0.5f })
                    foreach (float y in new[] { -0.2f, 0.15f })
                        foreach (float depth in new[] { 1f, 1.7f })
                            foreach (float tilt in new[] { -40f, 0f, 25f, 70f })
                                foreach (float turn in new[] { -60f, 0f, 35f }) {
                                    var f = WindowFrame.Of(TestWindow(x, y, depth, tilt, turn), dist, room == 1);
                                    Console.WriteLine(string.Format(ci, "{0} {1} {2} {3} {4} {5} {6} | {7} | {8} | {9} | {10}", room, dist, x, y, depth, tilt, turn,
                                        Fmt(f.Centre), Fmt(f.Right), Fmt(f.Up), Fmt(f.Normal)));
                                }
        Console.Out.Flush();
        return 0;
    }
    static string Fmt(V3 v) { return string.Format(CultureInfo.InvariantCulture, "{0:0.000000} {1:0.000000} {2:0.000000}", v.X, v.Y, v.Z); }
}
