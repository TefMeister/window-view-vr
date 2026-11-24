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
        // 1.17: a carried window never gets away. Hand Freeze's window as it was on 2026-11-25 (turned 63, tilted 18), held at
        // its middle while the hand's ray sweeps off its face and back, as a hand does when placing it
        foreach (bool room in new[] { false, true }) {
            var lostW = TestWindow(-0.81f, 0.47f, 0.78f, 18.4f, 63.4f);
            var hold = new Carry { Window = lostW, U = 0.5f, V = 0.5f };
            var eye = new V3(0.2f, -0.3f, -0.1f);
            var to = WindowFrame.Of(lostW, 1f, room).Centre - eye;
            double yaw0 = Math.Atan2(to.X, -to.Z), pitch0 = Math.Asin(to.Y / to.Length);
            bool near = true, calm = true; string where = "";
            for (int i = 0; i < 4000; ++i) {
                double a = yaw0 + Math.Sin(i * 0.004) * 2.6, e = pitch0 + Math.Sin(i * 0.0027) * 1.2;
                var ray = new V3((float)(Math.Sin(a) * Math.Cos(e)), (float)Math.Sin(e), (float)(-Math.Cos(a) * Math.Cos(e)));
                float x0 = lostW.X, y0 = lostW.Y;
                Pointer.CarryMove(lostW, hold, eye, ray, 1f, room);
                if (float.IsNaN(lostW.X) || float.IsNaN(lostW.Y) || Math.Abs(lostW.X) > Math.PI + 1e-4 || Math.Abs(lostW.Y) > Desk.MAX_Y + 1e-4) { near = false; where = "loop " + i + ": " + lostW.X + ", " + lostW.Y; break; }
                float stepX = Math.Abs(lostW.X - x0); if (stepX > Math.PI) stepX = Math.Abs(stepX - 2 * (float)Math.PI);   // once round is no jump
                if (stepX > Pointer.MAX_CARRY_STEP + 1e-4 || Math.Abs(lostW.Y - y0) > Pointer.MAX_CARRY_STEP + 1e-4) { calm = false; where = "loop " + i; }
            }
            Check("a carried, turned window stays within reach" + (room ? " (room)" : " (follow)"), near, where);
            Check("and never jumps more than a hand's width a loop" + (room ? " (room)" : " (follow)"), calm, where);
        }
        var flatW = TestWindow(0, 0, 1, 0, 0);
        var flatHold = new Carry { Window = flatW, U = 0.5f, V = 0.5f };
        for (int i = 0; i < 50; ++i) Pointer.CarryMove(flatW, flatHold, new V3(0, 0, 0), new V3((float)Math.Sin(0.3), 0, (float)-Math.Cos(0.3)), 1f, false);
        Check("an ordinary carry still follows the ray", Math.Abs(flatW.X - 0.3f) < 0.01f && Math.Abs(flatW.Y) < 0.01f, flatW.X + ", " + flatW.Y);
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
            File.WriteAllText(tmp, "gone.exe|Gone|-15534.58|21713.76|0.0006|0.63|1||0.778|18.4|63.4\r\nfine.exe|Fine|-5.93|1.3|0.0006|1|1||2|7|-5\r\n");
            var gone = new Desk(); gone.Load();
            Check("a window that flew away loads straight ahead, facing the eyes", gone.Windows.Count == 2 && gone.Windows[0].X == 0 && gone.Windows[0].Y == 0 && gone.Windows[0].Tilt == 0 && gone.Windows[0].Turn == 0);
            Check("a window within reach loads where it was", gone.Windows[1].X == -5.93f && gone.Windows[1].Y == 1.3f && gone.Windows[1].Tilt == 7f);
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
