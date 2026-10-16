// ViewUi.cs -- the Window View window: the layer ON/OFF switch, the big MOUSE & KEYBOARD button, which game is connected,
// the list of open windows to add, and one row per added window (see-through, size, show, remove). A background loop
// captures every added window about 30 times a second and hands the pictures to the layer through Shared.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

class OpenWindow { public IntPtr Hwnd; public string Title, Exe; public override string ToString() { return Title + "   (" + Exe + ")"; } }

class ViewUi : Form {
    const int CAPTURE_MS = 33;                    // about 30 pictures a second per window
    static readonly Color OnColor = Color.FromArgb(60, 160, 80), OffColor = Color.FromArgb(70, 70, 70);
    readonly Shared shared = new Shared();
    readonly Desk desk = new Desk();
    readonly InputRouter input;
    readonly Button layerBtn = new Button(), inputBtn = new Button(), refreshBtn = new Button(), addBtn = new Button();
    readonly ListBox openList = new ListBox();
    readonly FlowLayoutPanel rows = new FlowLayoutPanel();
    readonly Label status = new Label(), hint = new Label();
    readonly System.Windows.Forms.Timer uiTimer = new System.Windows.Forms.Timer();
    Thread captureThread;
    volatile bool running = true;
    int lastRelink;

    delegate bool EnumProc(IntPtr hwnd, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int GetWindowText(IntPtr hwnd, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder s, ref int n);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;

    public ViewUi() {
        input = new InputRouter(desk);
        Text = "Window View";
        ClientSize = new Size(560, 640);
        MinimumSize = new Size(480, 400);
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.FromArgb(32, 34, 40);
        ForeColor = Color.White;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

        var top = new TableLayoutPanel();
        top.Dock = DockStyle.Top;
        top.Height = 112;
        top.ColumnCount = 2;
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        top.Padding = new Padding(8);
        Style(layerBtn, "VR LAYER: OFF");
        layerBtn.Height = 44;
        layerBtn.Click += delegate { if (Layer.IsOn()) Layer.TurnOff(); else Layer.TurnOn(); ShowLayer(); };
        Style(inputBtn, "MOUSE && KEYBOARD: OFF   (Page Up = on, Page Down = off)");
        inputBtn.Height = 44;
        inputBtn.Click += delegate { input.SetOn(!input.On); };
        top.Controls.Add(layerBtn, 0, 0);
        top.Controls.Add(inputBtn, 1, 0);
        top.SetColumnSpan(inputBtn, 1);
        hint.Dock = DockStyle.Fill;
        hint.Text = "Switch the layer ON before the game starts. Windows follow your head; drag one by its dark header, resize by its corner.";
        hint.ForeColor = Color.Gainsboro;
        top.Controls.Add(hint, 0, 1);
        top.SetColumnSpan(hint, 2);
        Controls.Add(top);

        var mid = new Panel();
        mid.Dock = DockStyle.Top;
        mid.Height = 190;
        mid.Padding = new Padding(8, 0, 8, 0);
        var openLabel = new Label();
        openLabel.Text = "Open windows";
        openLabel.Dock = DockStyle.Top;
        openLabel.Height = 20;
        openList.Dock = DockStyle.Fill;
        openList.BackColor = Color.FromArgb(24, 26, 30);
        openList.ForeColor = Color.White;
        openList.DoubleClick += delegate { AddSelected(); };
        var btns = new FlowLayoutPanel();
        btns.Dock = DockStyle.Bottom;
        btns.Height = 36;
        btns.FlowDirection = FlowDirection.RightToLeft;
        Style(addBtn, "ADD TO VR");
        addBtn.Width = 140;
        addBtn.Click += delegate { AddSelected(); };
        Style(refreshBtn, "REFRESH");
        refreshBtn.Width = 110;
        refreshBtn.Click += delegate { RefreshOpen(); };
        btns.Controls.Add(addBtn);
        btns.Controls.Add(refreshBtn);
        mid.Controls.Add(openList);
        mid.Controls.Add(btns);
        mid.Controls.Add(openLabel);
        Controls.Add(mid);
        mid.BringToFront();

        status.Dock = DockStyle.Bottom;
        status.Height = 44;
        status.Padding = new Padding(8, 4, 8, 4);
        status.ForeColor = Color.Gainsboro;
        Controls.Add(status);

        var rowsLabel = new Label();
        rowsLabel.Text = "In VR";
        rowsLabel.Dock = DockStyle.Top;
        rowsLabel.Height = 24;
        rowsLabel.Padding = new Padding(8, 4, 0, 0);
        rows.Dock = DockStyle.Fill;
        rows.FlowDirection = FlowDirection.TopDown;
        rows.WrapContents = false;
        rows.AutoScroll = true;
        rows.Padding = new Padding(8, 0, 8, 0);
        Controls.Add(rows);
        Controls.Add(rowsLabel);
        rows.BringToFront();
        rowsLabel.BringToFront();
        mid.BringToFront();
        top.BringToFront();

        desk.Load();
        input.OnChanged += delegate(bool on) { try { BeginInvoke(new Action(ShowInput)); } catch (Exception) { } };
        input.Start();
        ShowLayer();
        ShowInput();
        RefreshOpen();
        Relink();
        RebuildRows();
        captureThread = new Thread(CaptureLoop);
        captureThread.IsBackground = true;
        captureThread.Start();
        uiTimer.Interval = 500;
        uiTimer.Tick += delegate { Tick(); };
        uiTimer.Start();
    }

    static void Style(Button b, string text) {
        b.Text = text;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = Color.DimGray;
        b.BackColor = OffColor;
        b.ForeColor = Color.White;
        b.Dock = DockStyle.Fill;
        b.Margin = new Padding(4);
    }
    void ShowLayer() { bool on = Layer.IsOn(); layerBtn.Text = on ? "VR LAYER: ON" : "VR LAYER: OFF"; layerBtn.BackColor = on ? OnColor : OffColor; }
    void ShowInput() {
        inputBtn.Text = input.On ? "MOUSE && KEYBOARD: TO THE WINDOWS   (Page Down = back)" : "MOUSE && KEYBOARD: NORMAL   (Page Up = to the windows)";
        inputBtn.BackColor = input.On ? OnColor : OffColor;
    }

    protected override void OnFormClosing(FormClosingEventArgs e) {
        running = false;
        uiTimer.Stop();
        input.Dispose();          // the mouse and keyboard always come back
        if (captureThread != null) captureThread.Join(1000);
        lock (desk.Lock) { desk.Save(); }
        shared.Dispose();         // hides every slot: the layer shows nothing within a frame
        base.OnFormClosing(e);
    }

    // ---- the open-window list ------------------------------------------------------------------------------------
    List<OpenWindow> ListOpen() {
        var list = new List<OpenWindow>();
        uint me = (uint)Process.GetCurrentProcess().Id;
        EnumWindows(delegate(IntPtr hwnd, IntPtr l) {
            if (!IsWindowVisible(hwnd) || GetWindowTextLength(hwnd) == 0 || (GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid == me) return true;
            var sb = new StringBuilder(256);
            GetWindowText(hwnd, sb, 256);
            string title = sb.ToString();
            if (title == "Program Manager" || title == "Windows Input Experience" || title == "Settings") return true;
            var w = new OpenWindow();
            w.Hwnd = hwnd;
            w.Title = title;
            w.Exe = ExeOf(pid);
            list.Add(w);
            return true;
        }, IntPtr.Zero);
        return list;
    }
    static string ExeOf(uint pid) {
        IntPtr h = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) return "?";
        var sb = new StringBuilder(1024);
        int n = sb.Capacity;
        string path = QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : "?";
        CloseHandle(h);
        return System.IO.Path.GetFileName(path);
    }
    void RefreshOpen() {
        var list = ListOpen();
        openList.BeginUpdate();
        openList.Items.Clear();
        foreach (var w in list) openList.Items.Add(w);
        openList.EndUpdate();
    }
    void AddSelected() {
        var w = openList.SelectedItem as OpenWindow;
        if (w == null) return;
        lock (desk.Lock) {
            foreach (var p in desk.Windows) if (p.Hwnd == w.Hwnd) { status.Text = w.Title + " is already in VR."; return; }
            if (desk.FreeSlot() < 0) { status.Text = "All " + Shared.SLOTS + " places are taken; remove one first."; return; }
            desk.Add(w.Hwnd, w.Exe, w.Title);
            desk.Save();
        }
        RebuildRows();
    }
    // a window from the layout file (or one that was closed) is linked to a live window again by exe name and title
    void Relink() {
        List<OpenWindow> open = null;
        lock (desk.Lock) {
            foreach (var p in desk.Windows) {
                if (p.Live && Native.IsWindow(p.Hwnd)) continue;
                p.Hwnd = IntPtr.Zero;
                if (open == null) open = ListOpen();
                foreach (var w in open) {
                    bool sameExe = string.Equals(w.Exe, p.Exe, StringComparison.OrdinalIgnoreCase);
                    if (sameExe && (w.Title == p.Title || w.Title.StartsWith(p.Title) || p.Title.StartsWith(w.Title))) { p.Hwnd = w.Hwnd; break; }
                }
                if (!p.Live) shared.Hide(p.Slot);
            }
        }
    }

    // ---- the rows ------------------------------------------------------------------------------------------------
    void RebuildRows() {
        rows.SuspendLayout();
        rows.Controls.Clear();
        List<Placement> list;
        lock (desk.Lock) { list = new List<Placement>(desk.Windows); }
        foreach (var p in list) rows.Controls.Add(Row(p));
        rows.ResumeLayout();
    }
    Control Row(Placement p) {
        var panel = new TableLayoutPanel();
        panel.Width = rows.ClientSize.Width - 24;
        panel.Height = 58;
        panel.ColumnCount = 5;
        panel.RowCount = 2;
        panel.BackColor = Color.FromArgb(44, 47, 54);
        panel.Margin = new Padding(0, 0, 0, 6);
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        var title = new Label();
        title.Text = p.Title + "  (" + p.Exe + ")";
        title.AutoEllipsis = true;
        title.Dock = DockStyle.Fill;
        title.TextAlign = ContentAlignment.MiddleLeft;
        panel.Controls.Add(title, 0, 0);
        panel.SetColumnSpan(title, 2);
        var opacityLabel = new Label(); opacityLabel.Text = "See-through"; opacityLabel.Dock = DockStyle.Fill; opacityLabel.TextAlign = ContentAlignment.MiddleLeft;
        var opacity = new TrackBar();
        opacity.Minimum = 10; opacity.Maximum = 100; opacity.TickFrequency = 10; opacity.AutoSize = false; opacity.Height = 24; opacity.Dock = DockStyle.Fill;
        opacity.Value = (int)Math.Round(Math.Max(0.1f, Math.Min(1f, p.Opacity)) * 100);
        opacity.ValueChanged += delegate { lock (desk.Lock) { p.Opacity = opacity.Value / 100f; } };
        opacity.MouseUp += delegate { lock (desk.Lock) { desk.Save(); } };
        panel.Controls.Add(opacityLabel, 0, 1);
        panel.Controls.Add(opacity, 1, 1);
        var size = new TrackBar();
        size.Minimum = 30; size.Maximum = 300; size.TickFrequency = 30; size.AutoSize = false; size.Height = 24; size.Dock = DockStyle.Fill;
        size.Value = Math.Max(30, Math.Min(300, (int)Math.Round(p.Scale / Desk.DEFAULT_SCALE * 100)));
        size.ValueChanged += delegate { lock (desk.Lock) { p.Scale = Desk.DEFAULT_SCALE * size.Value / 100f; } };
        size.MouseUp += delegate { lock (desk.Lock) { desk.Save(); } };
        var sizeLabel = new Label(); sizeLabel.Text = "Size"; sizeLabel.Dock = DockStyle.Fill; sizeLabel.TextAlign = ContentAlignment.MiddleCenter;
        panel.Controls.Add(sizeLabel, 2, 0);
        panel.Controls.Add(size, 2, 1);
        panel.SetColumnSpan(size, 2);
        var shown = new CheckBox();
        shown.Text = "Show"; shown.Checked = p.Shown; shown.Dock = DockStyle.Fill;
        shown.CheckedChanged += delegate { lock (desk.Lock) { p.Shown = shown.Checked; if (!p.Shown) shared.Hide(p.Slot); desk.Save(); } };
        panel.Controls.Add(shown, 3, 0);
        var remove = new Button();
        Style(remove, "REMOVE");
        remove.Click += delegate { lock (desk.Lock) { desk.Remove(p); shared.Hide(p.Slot); desk.Save(); } RebuildRows(); };
        panel.Controls.Add(remove, 4, 0);
        panel.SetRowSpan(remove, 2);
        return panel;
    }

    // ---- every half second ---------------------------------------------------------------------------------------
    void Tick() {
        if (Environment.TickCount - lastRelink > 2000) { lastRelink = Environment.TickCount; Relink(); }
        string game = shared.GameConnected ? "Connected: " + shared.GameName + " (" + Api.Name(shared.GameApi) + "), " + shared.Uploads + " pictures sent" : "No game connected";
        string err = shared.LastError != 0 ? "   last error " + shared.LastError : "";
        string where = "";
        if (input.On) {
            lock (desk.Lock) {
                var hit = desk.HitTest(desk.CursorX, desk.CursorY);
                where = string.Format(System.Globalization.CultureInfo.InvariantCulture, "   cursor {0:0.000} {1:0.000} {2}", desk.CursorX, desk.CursorY,
                    hit.Kind == HitKind.None ? "over nothing" : hit.Kind + " of " + hit.P.Title + " at " + hit.PicX + "," + hit.PicY);
            }
        }
        status.Text = game + err + "\n" + input.LastAction + where;
        ShowLayer();
    }

    // ---- the capture loop (its own thread) -----------------------------------------------------------------------
    void CaptureLoop() {
        var caps = new Dictionary<Placement, Capture>();
        while (running) {
            int t0 = Environment.TickCount;
            shared.Heartbeat();
            List<Placement> list;
            Hit under = new Hit();
            lock (desk.Lock) {
                list = new List<Placement>(desk.Windows);
                if (input.On) under = desk.HitTest(desk.CursorX, desk.CursorY);
            }
            foreach (var p in list) {
                if (p.Slot < 0) continue;
                if (!p.Shown || !p.Live) { shared.Hide(p.Slot); continue; }
                Capture cap;
                if (!caps.TryGetValue(p, out cap)) caps[p] = cap = new Capture();
                Point? cursor = under.P == p ? new Point(under.PicX, under.PicY) : (Point?)null;
                float x, y, scale, opacity;
                lock (desk.Lock) { x = p.X; y = p.Y; scale = p.Scale; opacity = p.Opacity; }
                if (cap.Grab(p.Hwnd, p.Title, opacity, cursor)) {
                    lock (desk.Lock) { p.PicW = cap.Width; p.PicH = cap.Height; }
                    shared.Publish(p.Slot, cap.Pixels, cap.Width, cap.Height, x, y, scale);
                }
            }
            foreach (var kv in new List<KeyValuePair<Placement, Capture>>(caps)) if (!list.Contains(kv.Key)) { kv.Value.Dispose(); caps.Remove(kv.Key); }
            int spent = Environment.TickCount - t0;
            Thread.Sleep(Math.Max(1, CAPTURE_MS - spent));
        }
        foreach (var c in caps.Values) c.Dispose();
    }
}
