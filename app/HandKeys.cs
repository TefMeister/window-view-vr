// HandKeys.cs -- 1.12: one hand's presses on Window View's keyboard, found by geometry in the pointer loop (Tefa 2026-11-14:
// "some presses don't register at all, others show up late"; both hands, "like VD"). Plain state, no WinForms, no threads:
// the pointer loop feeds it every new controller frame and gets at most one press back.
//   - a light pull counts: past PRESS it presses, and it can press again only after falling below RELEASE (a resting
//     finger hovering around PRESS gives one press, never a stream); A (right hand) presses too, on its way down
//   - the key is the one under the pointer at the FIRST touch of the trigger (1.8's hold-still rule: pulling tilts the
//     controller, so the pointer would slide onto the next key between touch and press)
//   - Reset() (CONTROL mode off, the keyboard hidden) forgets everything; a trigger still held then presses nothing
//     until it has been let go
struct KeyEvent {
    public bool Pressed;
    public KeyRect Key;
    public int Hand;      // KeysShared.LEFT / RIGHT
    public int AtMs;      // Environment.TickCount when the press was seen
}

class HandKeys {
    const float PRESS = 0.35f, RELEASE = 0.2f, TOUCH = 0.12f;
    readonly int hand;
    bool armed = true;          // the next pull may press
    bool aWas;
    bool touching; bool heldFound; KeyRect held;   // the key latched at the first touch
    public bool Lit { get; private set; }
    public KeyRect LitKey { get; private set; }

    public HandKeys(int hand) { this.hand = hand; }

    public void Reset() {
        armed = false; aWas = true;   // a trigger or A still down must be let go first
        touching = false; heldFound = false;
        Lit = false;
    }

    public KeyEvent Step(float trigger, bool aButton, bool onKeyboard, float picX, float picY, KeyRect[] table, int nowMs) {
        var ev = new KeyEvent { Hand = hand, AtMs = nowMs };
        KeyRect under = default(KeyRect);
        bool found = onKeyboard && KeyMap.Find(table, picX, picY, out under);
        bool touchNow = trigger > TOUCH || aButton;
        if (!touchNow) { touching = false; heldFound = false; }
        else if (!touching) { touching = true; heldFound = found; held = under; }
        else if (!heldFound && found) { heldFound = true; held = under; }   // the touch began in a gap: the first key reached
        Lit = onKeyboard && (touching && heldFound || found);
        LitKey = touching && heldFound ? held : under;

        bool aDown = aButton && !aWas;
        bool pull = armed && trigger > PRESS;
        if ((pull || aDown) && onKeyboard && touching && heldFound) {
            ev.Pressed = true; ev.Key = held;
            armed = false;
        }
        if (trigger < RELEASE && !aButton) armed = true;
        aWas = aButton;
        return ev;
    }
}
