// KeyMap.cs -- 1.12: which keyboard key lies under a point of the keyboard's picture, found by geometry from the key table
// (KeysUi.KeyTable), so a controller press is caught in the pointer loop without asking WinForms (Tefa 2026-11-14: "some
// presses don't register at all, others show up late").

// one key of Window View's keyboard: its button's rectangle in the keyboard window's client pixels, and its key code
struct KeyRect {
    public int X, Y, W, H;
    public uint Vk;
}

static class KeyMap {
    // picX / picY: pixels on the captured picture, which carries Capture.HEADER rows of our header strip above the client
    // area. A point in the gap between two keys, or on the header, finds nothing.
    public static bool Find(KeyRect[] table, float picX, float picY, out KeyRect key) {
        key = default(KeyRect);
        if (table == null) return false;
        float x = picX, y = picY - Capture.HEADER;
        foreach (var k in table) {
            if (x >= k.X && x < k.X + k.W && y >= k.Y && y < k.Y + k.H) { key = k; return true; }
        }
        return false;
    }
}
