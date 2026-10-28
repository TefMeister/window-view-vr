// KeysUi.cs -- Window View's own keyboard (1.7, 2026-10-28, Tefa: "something i can use to type stuff into the windows that
// window view puts inside the headset, so it could be a window view specific, has to be toggleable on and off like any
// other window"). A plain window of keys that is always one of the rows under IN VR; clicked with the controller pointer
// (or the mouse on the windows), each key goes to the window last clicked through InputRouter.TypeKey, the same way the
// real keyboard's keys go while MOUSE & KEYBOARD is on. Clicking a key is not a click into a window, so the target stays.
// The Windows on-screen keyboard cannot do this: it types into the focused window, and the game keeps the focus.
// Shift and Ctrl are one-shot: pressed, they hold for the next key only. A still window: nothing moves, nothing animates.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

class KeysUi : Form {
    public const string TITLE = "Keyboard (Window View)";
    const int KEY_W = 46, KEY_H = 40, GAP = 4, PAD = 8;
    const uint VK_SHIFT = 0x10, VK_CONTROL = 0x11;
    static readonly Color Back = Color.FromArgb(32, 34, 40), KeyBack = Color.FromArgb(52, 56, 70), OnBack = Color.FromArgb(60, 160, 80);
    readonly InputRouter input;
    readonly List<KeyValuePair<Button, string[]>> letters = new List<KeyValuePair<Button, string[]>>();   // labels: plain, shifted
    Button shiftKey, ctrlKey;
    bool shift, ctrl;
    public bool AllowClose;

    // one key: its label(s), its virtual-key code, and how many key widths it takes
    struct K { public string Plain, Shifted; public uint Vk; public float W; public K(string p, string s, uint vk, float w = 1f) { Plain = p; Shifted = s; Vk = vk; W = w; } }

    static readonly K[][] KeyRows = {
        new[] { new K("Esc", null, 0x1B), new K("1", "!", '1'), new K("2", "@", '2'), new K("3", "#", '3'), new K("4", "$", '4'), new K("5", "%", '5'),
                new K("6", "^", '6'), new K("7", "&", '7'), new K("8", "*", '8'), new K("9", "(", '9'), new K("0", ")", '0'),
                new K("-", "_", 0xBD), new K("=", "+", 0xBB), new K("Back", null, 0x08, 1.8f) },
        new[] { new K("Tab", null, 0x09, 1.4f), new K("q", "Q", 'Q'), new K("w", "W", 'W'), new K("e", "E", 'E'), new K("r", "R", 'R'), new K("t", "T", 'T'),
                new K("y", "Y", 'Y'), new K("u", "U", 'U'), new K("i", "I", 'I'), new K("o", "O", 'O'), new K("p", "P", 'P'),
                new K("[", "{", 0xDB), new K("]", "}", 0xDD), new K("\\", "|", 0xDC, 1.4f) },
        new[] { new K("Ctrl", null, VK_CONTROL, 1.7f), new K("a", "A", 'A'), new K("s", "S", 'S'), new K("d", "D", 'D'), new K("f", "F", 'F'), new K("g", "G", 'G'),
                new K("h", "H", 'H'), new K("j", "J", 'J'), new K("k", "K", 'K'), new K("l", "L", 'L'), new K(";", ":", 0xBA),
                new K("'", "\"", 0xDE), new K("Enter", null, 0x0D, 2.1f) },
        new[] { new K("Shift", null, VK_SHIFT, 2.2f), new K("z", "Z", 'Z'), new K("x", "X", 'X'), new K("c", "C", 'C'), new K("v", "V", 'V'), new K("b", "B", 'B'),
                new K("n", "N", 'N'), new K("m", "M", 'M'), new K(",", "<", 0xBC), new K(".", ">", 0xBE), new K("/", "?", 0xBF),
                new K("Up", null, 0x26), new K("Del", null, 0x2E, 1.6f) },
        new[] { new K("`", "~", 0xC0), new K("Home", null, 0x24, 1.3f), new K("End", null, 0x23, 1.3f), new K("Space", null, 0x20, 6.6f),
                new K("Left", null, 0x25, 1.3f), new K("Down", null, 0x28, 1.3f), new K("Right", null, 0x27, 1.3f) },
    };

    public KeysUi(InputRouter input) {
        this.input = input;
        Text = TITLE;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Back;
        ForeColor = Color.White;
        Font = new Font("Segoe UI Semibold", 10f);
        StartPosition = FormStartPosition.Manual;
        int y = PAD, widest = 0;
        foreach (var row in KeyRows) {
            int x = PAD;
            foreach (var k in row) {
                int w = (int)Math.Round(KEY_W * k.W + GAP * (k.W - 1));
                var b = new Button();
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderColor = Color.FromArgb(90, 96, 118);
                b.FlatAppearance.MouseOverBackColor = KeyBack;   // still: no hover colour
                b.FlatAppearance.MouseDownBackColor = KeyBack;
                b.BackColor = KeyBack;
                b.ForeColor = Color.White;
                b.TabStop = false;
                b.SetBounds(x, y, w, KEY_H);
                b.Text = k.Shifted != null && k.Plain.Length == 1 && !char.IsLetter(k.Plain[0]) ? k.Plain + "  " + k.Shifted : k.Plain;
                if (k.Shifted != null && char.IsLetter(k.Plain[0])) letters.Add(new KeyValuePair<Button, string[]>(b, new[] { k.Plain, k.Shifted }));
                var key = k;
                if (k.Vk == VK_SHIFT) { shiftKey = b; b.Click += delegate { shift = !shift; ShowMods(); }; }
                else if (k.Vk == VK_CONTROL) { ctrlKey = b; b.Click += delegate { ctrl = !ctrl; ShowMods(); }; }
                else b.Click += delegate { Press(key.Vk); };
                Controls.Add(b);
                x += w + GAP;
            }
            widest = Math.Max(widest, x - GAP + PAD);
            y += KEY_H + GAP;
        }
        ClientSize = new Size(widest, y - GAP + PAD);
        var screen = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(screen.Right - Width - 20, screen.Bottom - Height - 20);
    }

    void Press(uint vk) {
        input.TypeKey(vk, shift, ctrl);
        if (shift || ctrl) { shift = ctrl = false; ShowMods(); }   // one-shot
    }

    void ShowMods() {
        shiftKey.BackColor = shift ? OnBack : KeyBack;
        shiftKey.FlatAppearance.MouseOverBackColor = shiftKey.FlatAppearance.MouseDownBackColor = shiftKey.BackColor;
        ctrlKey.BackColor = ctrl ? OnBack : KeyBack;
        ctrlKey.FlatAppearance.MouseOverBackColor = ctrlKey.FlatAppearance.MouseDownBackColor = ctrlKey.BackColor;
        foreach (var kv in letters) kv.Key.Text = kv.Value[shift ? 1 : 0];
    }

    // it never takes the focus from the game, also when clicked on the desktop
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000; return cp; } }   // WS_EX_NOACTIVATE
    // closed only with Window View; the cross just hides it from the desktop row of windows (its row's Show box hides it in VR)
    protected override void OnFormClosing(FormClosingEventArgs e) {
        if (!AllowClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; WindowState = FormWindowState.Minimized; }
        base.OnFormClosing(e);
    }
}
