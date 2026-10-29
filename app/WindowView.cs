// WindowView.cs -- the Window View desktop app: start-up, the shared hand-over to the OpenXR layer, and the layer's
// registration. The window itself is ViewUi.cs; one window's picture Capture.cs; the desk and its layout Desk.cs; the
// mouse and keyboard InputRouter.cs.
//
// Tefa, 2026-10-16: "a tool that lets me see any window from my pc in vr - so that i can choose a specific window i
// want to be present in vr, set it's opacity and freely use mouse to click things in that window, without the mouse and
// keyboard affecting the game, so the game can only be operated by motion controllers or the gamepad" - windows that
// move with the head like an in-game menu, dragged by their header with the mouse, as many as needed, each resizable,
// and a MOUSE & KEYBOARD switch (a button here, Page Up on, Page Down off).
//
// ON/OFF registers the OpenXR layer (window_view_layer.dll, next to this exe) for every OpenXR game of this Windows user
// (HKCU, no admin), plus a 32-bit copy beside it so 32-bit games start too; the registration is checked against the
// files and mended each time the app opens (Layer.Mend, v1.1). A game picks the layer up when it starts, so the switch
// has to be on before the game starts. Everything
// else goes through the shared-memory blocks of ../shared.h (offsets below match it): this app writes the pictures
// and where they hang; the layer inside the game shows them and reports back.
using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program {
    [STAThread]
    static void Main(string[] args) {
        // --test-input: the hooks also take mouse and key events made by software (SendInput), so test/input_test.py can
        // drive the desk; never used for real play, where software-made events are the app's own and are let through
        foreach (var a in args) if (a == "--test-input") InputRouter.AcceptInjected = true;
        bool first;
        using (var one = new Mutex(true, "FlatToVR_WindowView_App", out first)) {
            if (!first) { MessageBox.Show("Window View is already open.", "Window View"); return; }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ViewUi());
        }
    }
}

// which graphics API the game binds (header `api`)
static class Api {
    public const int NONE = 0, D3D11 = 1, D3D12 = 2, OTHER = 3;
    public static string Name(int api) { return api == D3D11 ? "D3D11" : api == D3D12 ? "D3D12" : api == OTHER ? "not D3D11/D3D12, nothing shown" : "no session yet"; }
}

class Shared : IDisposable {
    // v2 (1.4): the room mode and per-window depth (shared.h); new names, so an old layer and a new app never mix
    const string NAME = @"Local\FlatToVR_WindowView_v2", SLOT_NAME = @"Local\FlatToVR_WindowView_v2_slot{0}";
    const uint MAGIC = 0x57564956, VERSION = 2;
    public const int SIZE = 636, SLOTS = 8, MAX_W = 2560, MAX_H = 1600;
    public const long SLOT_BYTES = MAX_W * MAX_H * 4L;
    const int APP_HEARTBEAT = 8, LAYER_HEARTBEAT = 16, LAYER_PID = 24, API = 28, GAME_NAME = 36, DISTANCE = 164,
              QUADS = 168, UPLOADS = 172, LAST_ERROR = 176, SLOTS_OFF = 180, SLOT = 40, ROOM_MODE = 500, RECENTER_SEQ = 504, ANCHOR_SEEN = 508,
              CURSOR_VISIBLE = 512, CURSOR_SLOT = 516, CURSOR_P = 520, CURSOR_EDGE = 532, CTRL_MODE = 536, POINTER_READY = 540,
              RAY_VALID = 544, RAY_O = 548, RAY_D = 560, TRIGGER = 572, SQUEEZE = 576, STICK_Y = 580, BUTTONS = 584, RAY_SEQ = 588,
              LRAY_VALID = 592, LRAY_O = 596, LRAY_D = 608, LSQUEEZE = 620, LSTICK_Y = 624, BUTTON_CLAIM = 628;
    const int S_VISIBLE = 0, S_WIDTH = 4, S_HEIGHT = 8, S_X = 12, S_Y = 16, S_SCALE = 20, S_FRAME = 24, S_BUFFER = 28, S_SHOWN = 32, S_DEPTH = 36;
    const ulong CONNECTED_WITHIN_MS = 3000;   // a game counts as connected if the layer was heard from this recently
    readonly MemoryMappedFile file;
    readonly MemoryMappedViewAccessor view;
    readonly MemoryMappedFile[] slotFiles = new MemoryMappedFile[SLOTS];
    readonly MemoryMappedViewAccessor[] slotViews = new MemoryMappedViewAccessor[SLOTS];
    readonly int[] current = new int[SLOTS];   // the buffer the layer reads now; the next picture goes into the other

    [DllImport("kernel32.dll")] static extern ulong GetTickCount64();

    public Shared() {
        file = MemoryMappedFile.CreateOrOpen(NAME, SIZE);
        view = file.CreateViewAccessor(0, SIZE);
        if (view.ReadUInt32(0) != MAGIC) { view.Write(4, VERSION); view.Write(0, MAGIC); }
        if (Distance <= 0.01f) Distance = 1.0f;
    }
    static int Off(int slot) { return SLOTS_OFF + slot * SLOT; }
    public void Heartbeat() { view.Write(APP_HEARTBEAT, GetTickCount64()); }
    // on the way out: the layer shows nothing within a frame or two
    public void Quiet() { for (int i = 0; i < SLOTS; ++i) view.Write(Off(i) + S_VISIBLE, 0); view.Write(APP_HEARTBEAT, 0UL); }
    public float Distance { get { return view.ReadSingle(DISTANCE); } set { view.Write(DISTANCE, value); } }
    public bool GameConnected { get { ulong hb = view.ReadUInt64(LAYER_HEARTBEAT); return hb != 0 && GetTickCount64() - hb < CONNECTED_WITHIN_MS; } }
    public int GameApi { get { return view.ReadInt32(API); } }
    public int Uploads { get { return view.ReadInt32(UPLOADS); } }
    public int LastError { get { return view.ReadInt32(LAST_ERROR); } }
    // the room mode: 1 = the windows stay in the room around an anchor, 0 = they follow the head (the layer reads it each frame)
    public bool RoomMode { get { return view.ReadInt32(ROOM_MODE) != 0; } set { view.Write(ROOM_MODE, value ? 1 : 0); } }
    // put the anchor where the head looks now; AnchorSet says the layer has done it
    public void Recenter() { view.Write(RECENTER_SEQ, view.ReadInt32(RECENTER_SEQ) + 1); }
    public bool AnchorSet { get { return view.ReadInt32(ANCHOR_SEEN) == view.ReadInt32(RECENTER_SEQ); } }
    // 1.5: the pointer the layer draws every frame, and the controllers working the windows (Pointer.cs)
    public void WriteCursor(bool visible, int slot, V3 tip, bool edge) {
        view.Write(CURSOR_SLOT, slot);
        view.Write(CURSOR_P, tip.X); view.Write(CURSOR_P + 4, tip.Y); view.Write(CURSOR_P + 8, tip.Z);
        view.Write(CURSOR_EDGE, edge ? 1 : 0);
        view.Write(CURSOR_VISIBLE, visible ? 1 : 0);
    }
    public bool CtrlMode { get { return view.ReadInt32(CTRL_MODE) != 0; } set { view.Write(CTRL_MODE, value ? 1 : 0); } }
    public int PointerReady { get { return view.ReadInt32(POINTER_READY); } }   // 1 = the controllers can work the windows in this game
    public bool ReadRay(out V3 o, out V3 d, out float trigger, out float squeeze, out float stickY, out int buttons, out int seq) {
        seq = view.ReadInt32(RAY_SEQ);
        o = new V3(view.ReadSingle(RAY_O), view.ReadSingle(RAY_O + 4), view.ReadSingle(RAY_O + 8));
        d = new V3(view.ReadSingle(RAY_D), view.ReadSingle(RAY_D + 4), view.ReadSingle(RAY_D + 8));
        trigger = view.ReadSingle(TRIGGER); squeeze = view.ReadSingle(SQUEEZE); stickY = view.ReadSingle(STICK_Y);
        buttons = view.ReadInt32(BUTTONS);
        return view.ReadInt32(RAY_VALID) != 0;
    }
    // 1.7: how many times the left stick has been held in for the hold time (mod 256; the layer writes it every frame)
    public int SelfToggles { get { return (view.ReadInt32(BUTTONS) >> 8) & 0xFF; } }
    // another app (Hand Poser's nudge) is using X / Y / A / B and the grips right now
    public bool ButtonsClaimed { get { ulong c = view.ReadUInt64(BUTTON_CLAIM); return c != 0 && GetTickCount64() - c < 500; } }
    public bool ReadLeftRay(out V3 o, out V3 d, out float squeeze, out float stickY) {
        o = new V3(view.ReadSingle(LRAY_O), view.ReadSingle(LRAY_O + 4), view.ReadSingle(LRAY_O + 8));
        d = new V3(view.ReadSingle(LRAY_D), view.ReadSingle(LRAY_D + 4), view.ReadSingle(LRAY_D + 8));
        squeeze = view.ReadSingle(LSQUEEZE); stickY = view.ReadSingle(LSTICK_Y);
        return view.ReadInt32(LRAY_VALID) != 0;
    }
    public string GameName {
        get {
            var chars = new char[64];
            view.ReadArray(GAME_NAME, chars, 0, 64);
            int n = Array.IndexOf(chars, '\0');
            return new string(chars, 0, n < 0 ? 64 : n);
        }
    }
    public void Hide(int slot) { if (slot >= 0 && slot < SLOTS) view.Write(Off(slot) + S_VISIBLE, 0); }
    // one finished picture: into the buffer the layer is not reading, then the slot's numbers, the frame counter last
    public void Publish(int slot, byte[] bgra, int w, int h, float x, float y, float scale, float depth) {
        if (slotViews[slot] == null) {
            slotFiles[slot] = MemoryMappedFile.CreateOrOpen(string.Format(SLOT_NAME, slot), 2 * SLOT_BYTES);
            slotViews[slot] = slotFiles[slot].CreateViewAccessor(0, 2 * SLOT_BYTES);
        }
        int next = 1 - current[slot];
        slotViews[slot].WriteArray(next * SLOT_BYTES, bgra, 0, w * h * 4);
        int off = Off(slot);
        view.Write(off + S_WIDTH, w);
        view.Write(off + S_HEIGHT, h);
        view.Write(off + S_X, x);
        view.Write(off + S_Y, y);
        view.Write(off + S_SCALE, scale);
        view.Write(off + S_DEPTH, depth);
        view.Write(off + S_BUFFER, next);
        Thread.MemoryBarrier();
        view.Write(off + S_FRAME, view.ReadInt32(off + S_FRAME) + 1);
        view.Write(off + S_VISIBLE, 1);
        current[slot] = next;
    }
    public void Dispose() {
        Quiet();
        for (int i = 0; i < SLOTS; ++i) { if (slotViews[i] != null) slotViews[i].Dispose(); if (slotFiles[i] != null) slotFiles[i].Dispose(); }
        view.Dispose();
        file.Dispose();
    }
}

static class Layer {
    const string KEY = @"Software\Khronos\OpenXR\1\ApiLayers\Implicit";
    public const string LAYER_NAME = "XR_APILAYER_FLATTOVR_window_view";
    static string Dir { get { return AppDomain.CurrentDomain.BaseDirectory; } }
    public static string Dll { get { return Path.Combine(Dir, "window_view_layer.dll"); } }
    public static string Manifest { get { return Path.Combine(Dir, "window_view_layer.json"); } }
    // a 32-bit copy registered beside the 64-bit one (as Hand Freeze v3.1): the per-user layer list is read by 32-bit and
    // 64-bit games alike, and a game whose loader can load NO layer from the list fails to start
    public static string Dll32 { get { return Path.Combine(Dir, "window_view_layer32.dll"); } }
    public static string Manifest32 { get { return Path.Combine(Dir, "window_view_layer32.json"); } }

    public static bool IsOn() {
        using (var k = Registry.CurrentUser.OpenSubKey(KEY)) {
            object v = k == null ? null : k.GetValue(Manifest);
            return v is int && (int)v == 0;
        }
    }
    static string ManifestText(string name, string dll, string bits) {
        return "{\n  \"file_format_version\": \"1.0.0\",\n  \"api_layer\": {\n    \"name\": \"" + name + "\",\n" +
            "    \"library_path\": \"" + dll.Replace("\\", "\\\\") + "\",\n    \"api_version\": \"1.0\",\n    \"implementation_version\": \"1\",\n" +
            "    \"description\": \"Window View (" + bits + "): hangs desktop windows in front of the eyes, from the Window View app\",\n" +
            "    \"disable_environment\": \"DISABLE_WINDOW_VIEW_LAYER\"\n  }\n}\n";
    }
    public static void TurnOn() {
        File.WriteAllText(Manifest, ManifestText(LAYER_NAME, Dll, "64-bit"));
        using (var k = Registry.CurrentUser.CreateSubKey(KEY)) {
            k.SetValue(Manifest, 0, RegistryValueKind.DWord);
            if (File.Exists(Dll32)) {
                File.WriteAllText(Manifest32, ManifestText(LAYER_NAME + "32", Dll32, "32-bit"));
                k.SetValue(Manifest32, 0, RegistryValueKind.DWord);
            } else k.DeleteValue(Manifest32, false);
        }
    }
    public static void TurnOff() {
        using (var k = Registry.CurrentUser.OpenSubKey(KEY, true)) if (k != null) { k.DeleteValue(Manifest, false); k.DeleteValue(Manifest32, false); }
    }
    // v1.1 (2026-10-16), as Hand Freeze v3.2: the registration is checked against the files each time the app opens, and mended. A PC where ON
    // was pressed with an older install (no 32-bit copy) keeps only the 64-bit copy registered, and its 32-bit OpenXR
    // games then fail to start; pressing OFF and ON again is the cure, and nothing says so. Mend() does that press when
    // the registered set does not match the files present.
    public static bool Mend() {
        if (!IsOn()) return false;
        bool fine;
        using (var k = Registry.CurrentUser.OpenSubKey(KEY)) {
            bool want32 = File.Exists(Dll32), has32 = k != null && k.GetValue(Manifest32) is int && File.Exists(Manifest32);
            fine = want32 == has32 && File.Exists(Manifest);
        }
        if (fine) return false;
        TurnOn();
        return true;
    }
}
