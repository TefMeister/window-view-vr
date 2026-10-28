// ViewUi.cs -- the Window View window: the layer ON/OFF switch, the big MOUSE & KEYBOARD button, which game is connected,
// ADD AN APP (or drop an app or its shortcut on the window), and one row per added app (its icon and name, see-through,
// size, show, remove). A background loop captures every added window about 30 times a second and hands the pictures to
// the layer through Shared.
//
// v1.3 (2026-10-25, Tefa: "instead of having a list of open apps to choose from, can we have an add window button instead?
// so that i would have to navigate to the app folder and choose the exe. or simply drag the shortcut to the app window and
// it would show up?"): the list of every open window is gone. An app is added by its exe (picked, or dropped as an exe or a
// shortcut); if it is not running it is started; its window joins the headset as soon as it opens, and again whenever the
// app is opened later. Each row shows the app's own icon ("could these have the actual app icon added to them"). The
// sliders are drawn by the app (Skin.cs), and the background is the Window View icon pixelated in the Lanes theme's style.
//
// 1.7 (2026-10-28, Tefa: "can you make the in-vr keyboard, window view itself and the Lanes window show up in VR headset,
// while the window view is on. hold Lthumb for 2 seconds to make window view window in the headset to appear/dissapear"):
// three rows are always there, with a Show box and no REMOVE: this window, its own keyboard (KeysUi.cs) and Lanes (the
// Windows Terminal window; its desktop shortcut starts wt.exe, whose window belongs to WindowsTerminal.exe, so an app
// added by that shortcut never found its window). Holding the left stick in for 2 s shows or hides this window in VR.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

class OpenWindow { public IntPtr Hwnd; public string Title, Exe, Path; }

class ViewUi : Form {
    const int CAPTURE_MS = 11;                    // 1.5: up to about 90 pictures a second per window (Graphics Capture costs next to nothing)
    const int MESSAGE_HOLD_MS = 6000;             // a message about adding an app stays this long before the status line returns
    static readonly Color Back = Color.FromArgb(32, 34, 40);
    static readonly Color OnColor = Color.FromArgb(60, 160, 80), OffColor = Color.FromArgb(62, 67, 86);
    static readonly Color RowTint = Color.FromArgb(165, 44, 47, 54), Ink = Color.White, Dim = Color.FromArgb(200, 204, 214);
    readonly Shared shared = new Shared();
    readonly Desk desk = new Desk();
    readonly InputRouter input;
    readonly Button layerBtn = new Button(), inputBtn = new Button(), addBtn = new Button(), roomBtn = new Button(), ctrlBtn = new Button();
    static readonly string SettingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowViewVR", "settings.txt");
    readonly FlowLayoutPanel rows = new FlowLayoutPanel();
    readonly Label status = new SkinLabel(), hint = new SkinLabel(), dropHint = new SkinLabel();
    readonly System.Windows.Forms.Timer uiTimer = new System.Windows.Forms.Timer();
    readonly Dictionary<string, Bitmap> icons = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, string> names = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    Thread captureThread;
    Pointer pointer;   // 1.5: the smooth pointer and the controllers (Pointer.cs)
    KeysUi keys;       // 1.7: Window View's own keyboard
    int lastSelfToggles = -1;
    const string SELF_TITLE = "Window View", LANES_EXE = "WindowsTerminal.exe";
    volatile bool running = true;
    int lastRelink, messageAt = -MESSAGE_HOLD_MS;

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
        Text = SELF_TITLE;
        ClientSize = new Size(580, 640);
        MinimumSize = new Size(500, 420);
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Back;
        ForeColor = Ink;
        DoubleBuffered = true;
        string ico = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "window_view.ico");
        try { Icon = File.Exists(ico) ? new Icon(ico) : Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
        Backdrop.Init(this, ico, Back);
        DropTarget(this);

        var top = new TableLayoutPanel();
        top.Dock = DockStyle.Top;
        top.Height = 160;
        top.ColumnCount = 2;
        top.BackColor = Color.Transparent;
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
        // 1.4 (Tefa: "can the Window view actually leave the windows in place inside the VR headset ... with the on/off switch
        // and leave it off for now please"): where the windows live, saved, OFF (following the head) until switched
        Style(roomBtn, "");
        roomBtn.Height = 40;
        roomBtn.Click += delegate { SetRoom(!shared.RoomMode); };
        top.Controls.Add(roomBtn, 0, 1);
        // 1.6 (Tefa: "with the window view on, my controller is not working"): the controllers can be left to the game alone
        Style(ctrlBtn, "");
        ctrlBtn.Height = 40;
        ctrlBtn.Click += delegate { SetControllers(ReadSetting("controllers") == "0"); };
        top.Controls.Add(ctrlBtn, 1, 1);
        hint.Dock = DockStyle.Fill;
        hint.Text = "Switch the layer ON before the game starts. Drag a window by its dark header, resize it by its corner. " +
                    "With the mouse on the windows: F9 pushes the window under the cursor back, F10 pulls it in, F11 brings all of them in front of you. " +
                    "Left stick click: the controllers work the windows (trigger = click, grip = carry, stick = scroll, Y = bring them in front); click it again for the game. " +
                    "Hold the left stick in for 2 seconds: this window appears or disappears in the headset.";
        hint.ForeColor = Dim;
        top.Controls.Add(hint, 0, 2);
        top.SetColumnSpan(hint, 2);

        var add = new FlowLayoutPanel();
        add.Dock = DockStyle.Top;
        add.Height = 50;
        add.BackColor = Color.Transparent;
        add.Padding = new Padding(8, 4, 8, 0);
        add.WrapContents = false;
        Style(addBtn, "ADD AN APP...");
        addBtn.Dock = DockStyle.None;
        addBtn.Size = new Size(150, 36);
        addBtn.Font = new Font("Segoe UI Semibold", 10f);
        addBtn.Click += delegate { PickApp(); };
        dropHint.Text = "or drag an app, or its shortcut, onto this window";
        dropHint.ForeColor = Dim;
        dropHint.AutoSize = true;
        dropHint.Padding = new Padding(6, 11, 0, 0);
        add.Controls.Add(addBtn);
        add.Controls.Add(dropHint);

        status.Dock = DockStyle.Bottom;
        status.Height = 62;
        status.Padding = new Padding(8, 4, 8, 4);
        status.ForeColor = Dim;

        var rowsLabel = new SkinLabel();
        rowsLabel.Text = "IN VR";
        rowsLabel.Font = new Font("Segoe UI Semibold", 10f);
        rowsLabel.Dock = DockStyle.Top;
        rowsLabel.Height = 26;
        rowsLabel.Padding = new Padding(8, 6, 0, 0);
        rows.Dock = DockStyle.Fill;
        rows.FlowDirection = FlowDirection.TopDown;
        rows.WrapContents = false;
        rows.AutoScroll = true;
        rows.BackColor = Color.Transparent;
        rows.Padding = new Padding(8, 0, 8, 0);
        rows.Scroll += (s, e) => rows.Invalidate(true);       // the background stays put while the rows scroll
        rows.MouseWheel += (s, e) => rows.Invalidate(true);
        rows.Resize += (s, e) => { foreach (Control c in rows.Controls) c.Width = rows.ClientSize.Width - 24; };
        // WinForms lays docked controls out from the END of this list to the front, so the order here is: top strip first,
        // then ADD AN APP, then the "IN VR" heading, the status line at the bottom, the rows filling the rest
        Controls.Clear();
        Controls.Add(rows);
        Controls.Add(status);
        Controls.Add(rowsLabel);
        Controls.Add(add);
        Controls.Add(top);
        foreach (Control c in Controls) DropTargets(c);   // a drop lands wherever the mouse is, on a button or a row too

        desk.Load();
        EnsureBuiltins();
        FindMissingPaths();
        shared.RoomMode = ReadSetting("room") == "1";   // off unless it was switched on and saved
        ShowRoom();
        ShowControllers();
        input.OnRecenter += delegate { shared.Recenter(); };
        shared.CtrlMode = false;   // the controllers start on the game
        pointer = new Pointer(shared, desk, input);
        input.OnChanged += delegate(bool on) { try { BeginInvoke(new Action(ShowInput)); } catch (Exception) { } };
        input.Start();
        keys = new KeysUi(input);
        Shown += delegate { keys.Show(); InputRouter.KeyboardWindow = keys.Handle; Relink(); };
        Layer.Mend();   // v1.1: a registration that no longer matches the files beside the exe is re-done here
        ShowLayer();
        ShowInput();
        Relink();
        RebuildRows();
        captureThread = new Thread(CaptureLoop);
        captureThread.IsBackground = true;
        captureThread.Start();
        uiTimer.Interval = 500;
        uiTimer.Tick += delegate { Tick(); };
        uiTimer.Start();
    }

    protected override void OnPaintBackground(PaintEventArgs e) { Backdrop.PaintUnder(this, e.Graphics, e.ClipRectangle); }
    protected override CreateParams CreateParams { get { return Backdrop.Composited(base.CreateParams); } }

    static void Style(Button b, string text) {
        b.Text = text;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = Color.FromArgb(90, 96, 118);
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
    void Say(string text) { status.Text = text; messageAt = Environment.TickCount; }

    // ---- 1.4: the room mode -------------------------------------------------------------------------------------------
    void SetRoom(bool on) {
        shared.RoomMode = on;
        if (on) shared.Recenter();   // they start where you look now
        WriteSetting("room", on ? "1" : "0");
        ShowRoom();
        Say(on ? "Windows now stay in the room. F11 (with the mouse on the windows) brings them back in front of you." : "Windows follow your head again.");
    }
    // ---- 1.6: the controllers left to the game ----------------------------------------------------------------------
    void SetControllers(bool on) {
        WriteSetting("controllers", on ? "1" : "0");
        ShowControllers();
        Say(on ? "The controllers can work the windows again (left stick click). Restart the game for it to take effect."
               : "The controllers are left to the game; the windows and the mouse still work. Restart the game for it to take effect.");
    }
    void ShowControllers() {
        bool on = ReadSetting("controllers") != "0";
        ctrlBtn.Text = on ? "CONTROLLERS: GAME + WINDOWS" : "CONTROLLERS: GAME ONLY";
        ctrlBtn.BackColor = on ? OnColor : OffColor;
    }
    void ShowRoom() {
        bool on = shared.RoomMode;
        roomBtn.Text = on ? "WINDOWS: STAY IN THE ROOM" : "WINDOWS: FOLLOW MY HEAD";
        roomBtn.BackColor = on ? OnColor : OffColor;
    }
    static string ReadSetting(string key) {
        try { foreach (var line in File.ReadAllLines(SettingsFile)) if (line.StartsWith(key + "=")) return line.Substring(key.Length + 1); } catch (Exception) { }
        return "";
    }
    static void WriteSetting(string key, string value) {
        var lines = new List<string>();
        try { foreach (var line in File.ReadAllLines(SettingsFile)) if (!line.StartsWith(key + "=")) lines.Add(line); } catch (Exception) { }
        lines.Add(key + "=" + value);
        try { Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)); File.WriteAllLines(SettingsFile, lines.ToArray()); } catch (Exception) { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e) {
        running = false;
        uiTimer.Stop();
        if (pointer != null) pointer.Dispose();
        if (keys != null) { keys.AllowClose = true; keys.Close(); InputRouter.KeyboardWindow = IntPtr.Zero; }
        shared.CtrlMode = false;  // the game gets its controllers back
        input.Dispose();          // the mouse and keyboard always come back
        if (captureThread != null) captureThread.Join(1000);
        lock (desk.Lock) { desk.Save(); }
        shared.Dispose();         // hides every slot: the layer shows nothing within a frame
        base.OnFormClosing(e);
    }

    // ---- adding an app ---------------------------------------------------------------------------------------------
    void PickApp() {
        using (var d = new OpenFileDialog()) {
            d.Title = "Pick the app to show in VR (its .exe, or a shortcut to it)";
            d.Filter = "Apps and shortcuts (*.exe;*.lnk)|*.exe;*.lnk";
            d.DereferenceLinks = true;
            if (d.ShowDialog(this) == DialogResult.OK) AddApp(d.FileName);
        }
    }

    // a dropped or picked .exe or .lnk: the app's window goes into the headset, and the app is started if it is not running
    internal void AddApp(string file) {
        string exe = file;
        if (file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) exe = ShortcutTarget(file);
        if (string.IsNullOrEmpty(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exe)) {
            Say(Path.GetFileName(file) + " does not lead to an app (.exe), so it cannot be added.");
            return;
        }
        if (string.Equals(Path.GetFileName(exe), Path.GetFileName(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase)) {
            Say("Window View and its keyboard are always in the list (their Show boxes put them in VR).");
            return;
        }
        string name = Path.GetFileName(exe);
        lock (desk.Lock) {
            foreach (var p in desk.Windows)
                if (string.Equals(p.Exe, name, StringComparison.OrdinalIgnoreCase)) { Say(AppName(p) + " is already in VR."); return; }
            if (desk.FreeSlot() < 0) { Say("All " + Shared.SLOTS + " places are taken; remove one first."); return; }
            desk.Add(IntPtr.Zero, name, "", exe);
            desk.Save();
        }
        bool open = false;
        foreach (var w in ListOpen()) if (string.Equals(w.Exe, name, StringComparison.OrdinalIgnoreCase)) { open = true; break; }
        if (!open) {
            try {
                Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = true });
                Say("Starting " + FriendlyName(exe) + "; its window joins VR as soon as it opens.");
            } catch (Exception ex) { Say("Could not start " + FriendlyName(exe) + ": " + ex.Message); }
        } else Say(FriendlyName(exe) + " added.");
        Relink();
        RebuildRows();
    }

    // every control takes a dropped app (a control that does not accept the drop would show the "no" cursor over itself)
    void DropTarget(Control c) {
        c.AllowDrop = true;
        c.DragEnter += (s, e) => { e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None; };
        c.DragDrop += (s, e) => { var files = e.Data.GetData(DataFormats.FileDrop) as string[]; if (files != null) BeginInvoke(new Action(() => { foreach (var f in files) AddApp(f); })); };
    }
    void DropTargets(Control c) { DropTarget(c); foreach (Control ch in c.Controls) DropTargets(ch); }

    // the target of a Windows shortcut, read through the Windows Script Host (no extra library needed)
    static string ShortcutTarget(string lnk) {
        object shell = null, link = null;
        try {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            shell = Activator.CreateInstance(t);
            link = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            return (string)link.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null);
        } catch (Exception) { return null; }
        finally {
            if (link != null) Marshal.ReleaseComObject(link);
            if (shell != null) Marshal.ReleaseComObject(shell);
        }
    }

    // the app's own name (its file description), worked out once per exe
    static string FriendlyName(string exe) {
        return names.GetOrAdd(exe, e => {
            try {
                var v = FileVersionInfo.GetVersionInfo(e);
                if (!string.IsNullOrWhiteSpace(v.FileDescription)) return v.FileDescription.Trim();
                if (!string.IsNullOrWhiteSpace(v.ProductName)) return v.ProductName.Trim();
            } catch (Exception) { }
            return Spaced(Path.GetFileNameWithoutExtension(e));
        });
    }
    // "GrabZones" -> "Grab Zones": an exe with no name inside it still reads as words
    static string Spaced(string s) { return System.Text.RegularExpressions.Regex.Replace(s, "(?<=[a-z])(?=[A-Z])", " "); }
    static string AppName(Placement p) {
        if (IsLanes(p)) return "Lanes (Windows Terminal)";
        if (p.Title != "") return p.Title;                              // a row from before v1.3 keeps the name it was given
        if (p.Path != "" && File.Exists(p.Path)) return FriendlyName(p.Path);
        return Spaced(Path.GetFileNameWithoutExtension(p.Exe));
    }

    Bitmap IconOf(string path) {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        Bitmap b;
        if (icons.TryGetValue(path, out b)) return b;
        try { using (var ic = Icon.ExtractAssociatedIcon(path)) using (var raw = ic.ToBitmap()) b = new Bitmap(raw, 32, 32); } catch (Exception) { b = null; }
        icons[path] = b;
        return b;
    }

    // rows from before v1.3 kept no path: look for the exe where our installers put apps (%LOCALAPPDATA%\Programs\<app>\),
    // so their icons show and they can be started, even before the app is opened once
    void FindMissingPaths() {
        string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        bool found = false;
        lock (desk.Lock) {
            foreach (var p in desk.Windows) {
                if (p.Path != "" || !Directory.Exists(programs)) continue;
                try {
                    foreach (var dir in Directory.GetDirectories(programs)) {
                        string f = Path.Combine(dir, p.Exe);
                        if (File.Exists(f)) { p.Path = f; found = true; break; }
                    }
                } catch (Exception) { }
            }
            if (found) desk.Save();
        }
    }

    // ---- 1.7: the rows that are always there ---------------------------------------------------------------------
    static string MyExe { get { return Path.GetFileName(Application.ExecutablePath); } }
    static bool IsLanes(Placement p) { return string.Equals(p.Exe, LANES_EXE, StringComparison.OrdinalIgnoreCase) && p.Title == ""; }
    static bool IsBuiltin(Placement p) {
        return IsLanes(p) || (string.Equals(p.Exe, MyExe, StringComparison.OrdinalIgnoreCase) && (p.Title == SELF_TITLE || p.Title == KeysUi.TITLE));
    }
    void EnsureBuiltins() {
        lock (desk.Lock) {
            bool added = Ensure(MyExe, SELF_TITLE, Application.ExecutablePath) | Ensure(MyExe, KeysUi.TITLE, Application.ExecutablePath) | Ensure(LANES_EXE, "", "");
            if (added) desk.Save();
        }
    }
    bool Ensure(string exe, string title, string path) {
        foreach (var p in desk.Windows) if (string.Equals(p.Exe, exe, StringComparison.OrdinalIgnoreCase) && p.Title == title) return false;
        if (desk.FreeSlot() < 0) return false;
        desk.Add(IntPtr.Zero, exe, title, path);
        return true;
    }
    // the left stick held in for 2 s (counted by the layer): this window in or out of the headset
    void WatchSelfToggle() {
        int t = shared.SelfToggles;
        if (lastSelfToggles < 0 || !shared.GameConnected) { lastSelfToggles = t; return; }
        if (t == lastSelfToggles) return;
        lastSelfToggles = t;
        Placement self = null;
        lock (desk.Lock) {
            foreach (var p in desk.Windows) if (string.Equals(p.Exe, MyExe, StringComparison.OrdinalIgnoreCase) && p.Title == SELF_TITLE) self = p;
            if (self == null) return;
            self.Shown = !self.Shown;
            if (!self.Shown) shared.Hide(self.Slot);
            desk.Save();
        }
        RebuildRows();
        Say(self.Shown ? "Window View is in the headset (left stick held 2 s)." : "Window View is out of the headset (left stick held 2 s).");
    }

    // ---- the open windows, to link the added apps to ----------------------------------------------------------------
    List<OpenWindow> ListOpen() {
        var list = new List<OpenWindow>();
        uint me = (uint)Process.GetCurrentProcess().Id;
        EnumWindows(delegate(IntPtr hwnd, IntPtr l) {
            if (!IsWindowVisible(hwnd) || GetWindowTextLength(hwnd) == 0 || (GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            var sb = new StringBuilder(256);
            GetWindowText(hwnd, sb, 256);
            string title = sb.ToString();
            if (pid == me && title != SELF_TITLE && title != KeysUi.TITLE) return true;   // 1.7: of our own, only this window and the keyboard
            if (title == "Program Manager" || title == "Windows Input Experience" || title == "Settings") return true;
            var w = new OpenWindow();
            w.Hwnd = hwnd;
            w.Title = title;
            w.Path = PathOf(pid);
            w.Exe = Path.GetFileName(w.Path);
            list.Add(w);
            return true;
        }, IntPtr.Zero);
        return list;
    }
    static string PathOf(uint pid) {
        IntPtr h = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) return "?";
        var sb = new StringBuilder(1024);
        int n = sb.Capacity;
        string path = QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : "?";
        CloseHandle(h);
        return path;
    }

    // an added app (or one from the layout file, or one that was closed) is linked to a live window again by exe name, and
    // by title when one was kept (layouts from before v1.3); a window already shown by another row is never taken twice
    void Relink() {
        List<OpenWindow> open = null;
        bool changed = false;
        lock (desk.Lock) {
            foreach (var p in desk.Windows) {
                if (p.Live && Native.IsWindow(p.Hwnd)) continue;
                if (p.Live) changed = true;
                p.Hwnd = IntPtr.Zero;
                if (open == null) open = ListOpen();
                foreach (var w in open) {
                    bool sameExe = string.Equals(w.Exe, p.Exe, StringComparison.OrdinalIgnoreCase);
                    bool sameTitle = p.Title == "" || w.Title == p.Title || w.Title.StartsWith(p.Title) || p.Title.StartsWith(w.Title);
                    bool taken = false;
                    foreach (var q in desk.Windows) taken |= q != p && q.Hwnd == w.Hwnd;
                    if (sameExe && sameTitle && !taken) {
                        p.Hwnd = w.Hwnd;
                        if (p.Path == "" && File.Exists(w.Path)) { p.Path = w.Path; desk.Save(); }
                        changed = true;
                        break;
                    }
                }
                if (!p.Live) shared.Hide(p.Slot);
            }
        }
        if (changed && IsHandleCreated) BeginInvoke(new Action(ShowRowStates));
    }

    // ---- the rows ------------------------------------------------------------------------------------------------
    readonly Dictionary<Placement, Label> rowState = new Dictionary<Placement, Label>();
    readonly Dictionary<Placement, PictureBox> rowIcon = new Dictionary<Placement, PictureBox>();

    void RebuildRows() {
        rows.SuspendLayout();
        foreach (Control c in rows.Controls) c.Dispose();
        rows.Controls.Clear();
        rowState.Clear();
        rowIcon.Clear();
        List<Placement> list;
        lock (desk.Lock) { list = new List<Placement>(desk.Windows); }
        foreach (var p in list) { var row = Row(p); DropTargets(row); rows.Controls.Add(row); }
        rows.ResumeLayout();
        ShowRowStates();
        rows.Invalidate(true);
    }
    void ShowRowStates() {
        lock (desk.Lock) {
            foreach (var kv in rowState) kv.Value.Text = kv.Key.Live ? "" : "waiting for its window: open the app, it joins by itself";
            foreach (var kv in rowIcon) if (kv.Value.Image == null) kv.Value.Image = IconOf(kv.Key.Path);   // an old row learns its path once linked
        }
    }

    Control Row(Placement p) {
        var panel = new TableLayoutPanel();
        panel.Width = rows.ClientSize.Width - 24;
        panel.Height = 70;
        panel.ColumnCount = 6;
        panel.RowCount = 2;
        panel.BackColor = RowTint;
        panel.Margin = new Padding(0, 0, 0, 6);
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        var pic = new PictureBox();
        pic.Size = new Size(32, 32);
        pic.SizeMode = PictureBoxSizeMode.Zoom;
        pic.BackColor = Color.Transparent;
        pic.Anchor = AnchorStyles.None;
        pic.Image = IconOf(p.Path);
        panel.Controls.Add(pic, 0, 0);
        panel.SetRowSpan(pic, 2);
        rowIcon[p] = pic;
        var title = new SkinLabel();
        title.Text = AppName(p);
        title.Font = new Font("Segoe UI Semibold", 10f);
        title.AutoEllipsis = true;
        title.Dock = DockStyle.Fill;
        title.TextAlign = ContentAlignment.MiddleLeft;
        panel.Controls.Add(title, 1, 0);
        var state = new SkinLabel();
        state.ForeColor = Color.FromArgb(230, 190, 90);
        state.AutoEllipsis = true;
        state.Dock = DockStyle.Fill;
        state.TextAlign = ContentAlignment.MiddleLeft;
        panel.Controls.Add(state, 2, 0);
        rowState[p] = state;
        var opacityLabel = new SkinLabel(); opacityLabel.Text = "See-through"; opacityLabel.ForeColor = Dim; opacityLabel.Dock = DockStyle.Fill; opacityLabel.TextAlign = ContentAlignment.MiddleLeft;
        var opacity = new FancySlider();
        opacity.Minimum = 10; opacity.Maximum = 100; opacity.SmallChange = 5; opacity.Height = 30; opacity.Dock = DockStyle.Fill;
        opacity.Value = (int)Math.Round(Math.Max(0.1f, Math.Min(1f, p.Opacity)) * 100);
        opacity.ValueChanged += delegate { lock (desk.Lock) { p.Opacity = opacity.Value / 100f; } };
        opacity.Released += delegate { lock (desk.Lock) { desk.Save(); } };
        panel.Controls.Add(opacityLabel, 1, 1);
        panel.Controls.Add(opacity, 2, 1);
        var size = new FancySlider();
        size.Minimum = 30; size.Maximum = 300; size.SmallChange = 10; size.Height = 30; size.Dock = DockStyle.Fill;
        size.Value = Math.Max(30, Math.Min(300, (int)Math.Round(p.Scale / Desk.DEFAULT_SCALE * 100)));
        size.ValueChanged += delegate { lock (desk.Lock) { p.Scale = Desk.DEFAULT_SCALE * size.Value / 100f; } };
        size.Released += delegate { lock (desk.Lock) { desk.Save(); } };
        var sizeLabel = new SkinLabel(); sizeLabel.Text = "Size"; sizeLabel.ForeColor = Dim; sizeLabel.Dock = DockStyle.Fill; sizeLabel.TextAlign = ContentAlignment.MiddleCenter;
        panel.Controls.Add(sizeLabel, 3, 0);
        panel.Controls.Add(size, 3, 1);
        panel.SetColumnSpan(size, 2);
        var shown = new CheckBox();
        shown.Text = "Show"; shown.Checked = p.Shown; shown.Dock = DockStyle.Fill; shown.BackColor = Color.Transparent;
        shown.CheckedChanged += delegate { lock (desk.Lock) { p.Shown = shown.Checked; if (!p.Shown) shared.Hide(p.Slot); desk.Save(); } };
        panel.Controls.Add(shown, 4, 0);
        if (!IsBuiltin(p)) {   // 1.7: the three built-in rows stay; their Show box takes them out of VR
            var remove = new Button();
            Style(remove, "REMOVE");
            remove.Click += delegate { lock (desk.Lock) { desk.Remove(p); shared.Hide(p.Slot); desk.Save(); } RebuildRows(); };
            panel.Controls.Add(remove, 5, 0);
            panel.SetRowSpan(remove, 2);
        }
        return panel;
    }

    // ---- every half second ---------------------------------------------------------------------------------------
    void Tick() {
        if (Environment.TickCount - lastRelink > 1000) { lastRelink = Environment.TickCount; Relink(); }
        WatchSelfToggle();
        string game = shared.GameConnected ? "Connected: " + shared.GameName + " (" + Api.Name(shared.GameApi) + "), " + shared.Uploads + " pictures sent" : "No game connected";
        if (shared.GameConnected && shared.RoomMode) game += shared.AnchorSet ? ", windows in the room" : ", placing the windows...";
        if (shared.GameConnected) game += shared.PointerReady == 1 ? (shared.CtrlMode ? "\nCONTROLLERS ON THE WINDOWS: trigger clicks, grip carries, left stick click = back to the game" : "\nLeft stick click: the controllers work the windows")
                                         : shared.PointerReady == -2 ? "\nControllers: left to the game"
                                         : shared.PointerReady < 0 ? "\n(the controllers could not be set up in this game)" : "";
        string err = shared.LastError != 0 ? "   last error " + shared.LastError : "";
        string where = "";
        if (input.On) {
            lock (desk.Lock) {
                var hit = desk.HitTest(desk.CursorX, desk.CursorY);
                where = string.Format(System.Globalization.CultureInfo.InvariantCulture, "   cursor {0:0.000} {1:0.000} {2}", desk.CursorX, desk.CursorY,
                    hit.Kind == HitKind.None ? "over nothing" : hit.Kind + " of " + AppName(hit.P) + " at " + hit.PicX + "," + hit.PicY);
            }
        }
        if (Environment.TickCount - messageAt > MESSAGE_HOLD_MS) {   // a message about adding an app is left up for a while
            string text = game + err + "\n" + input.LastAction + where;
            if (status.Text != text) status.Text = text;
        }
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
            bool outside = false;
            lock (desk.Lock) {
                list = new List<Placement>(desk.Windows);
                if (input.On) {
                    under = desk.HitTest(desk.CursorX, desk.CursorY);
                    if (under.P == null) { under = desk.NearestEdge(desk.CursorX, desk.CursorY); outside = true; }   // v1.2
                }
            }
            foreach (var p in list) {
                if (p.Slot < 0) continue;
                if (!p.Shown || !p.Live) { shared.Hide(p.Slot); continue; }
                Capture cap;
                if (!caps.TryGetValue(p, out cap)) caps[p] = cap = new Capture();
                Point? cursor = null;   // 1.5: the layer draws the pointer itself, every headset frame (Pointer.cs)
                float x, y, scale, opacity, depth;
                lock (desk.Lock) { x = p.X; y = p.Y; scale = p.Scale; opacity = p.Opacity; depth = p.Depth; }
                if (cap.Grab(p.Hwnd, AppName(p), opacity, cursor, outside)) {
                    lock (desk.Lock) { p.PicW = cap.Width; p.PicH = cap.Height; }
                    shared.Publish(p.Slot, cap.Pixels, cap.Width, cap.Height, x, y, scale, depth);
                }
            }
            foreach (var kv in new List<KeyValuePair<Placement, Capture>>(caps)) if (!list.Contains(kv.Key)) { kv.Value.Dispose(); caps.Remove(kv.Key); }
            int spent = Environment.TickCount - t0;
            Thread.Sleep(Math.Max(1, CAPTURE_MS - spent));
        }
        foreach (var c in caps.Values) c.Dispose();
    }
}
