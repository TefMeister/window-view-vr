// KeysShared.cs -- 1.12: the pointer keyboard's own shared block (WindowViewKeys in ../shared.h, same offsets). A separate
// block because the main one is also read by other apps by name and size and must never change. This app creates it; the
// layer opens it, reads the key lights and presses, draws them and buzzes, and writes the left trigger back.
using System;
using System.IO.MemoryMappedFiles;
using System.Threading;

class KeysShared : IDisposable {
    public const string NAME = @"Local\FlatToVR_WindowView_keys_v1";
    const uint MAGIC = 0x5359454Bu, VERSION = 1;
    public const int SIZE = 192;
    public const int LTRIGGER = 8, HAPTIC_OK = 12, HAPTIC_FAIL = 16, LIT_DRAWN = 20, FLASH_DRAWN = 24, HAND0 = 28, HAND_SIZE = 76;
    public const int H_LIT_VISIBLE = 0, H_LIT_SLOT = 4, H_LIT_C = 8, H_LIT_W = 20, H_LIT_H = 24, H_PRESS_SEQ = 28, H_PRESS_OK = 32,
                     H_PRESS_SLOT = 36, H_PRESS_C = 40, H_PRESS_W = 52, H_PRESS_H = 56, H_CURSOR_VISIBLE = 60, H_CURSOR_P = 64;
    public const int LEFT = 0, RIGHT = 1;
    readonly MemoryMappedFile file;
    readonly MemoryMappedViewAccessor view;
    readonly object gate = new object();

    public KeysShared() : this(NAME) { }
    public KeysShared(string name) {   // another name: the self-tests, never the live block
        file = MemoryMappedFile.CreateOrOpen(name, SIZE);
        view = file.CreateViewAccessor(0, SIZE);
        if (view.ReadUInt32(0) != MAGIC) { view.Write(4, VERSION); view.Write(0, MAGIC); }
    }
    public void Dispose() { view.Dispose(); file.Dispose(); }

    static int H(int hand) { return HAND0 + (hand == LEFT ? 0 : HAND_SIZE); }
    void Vec(int off, V3 v) { view.Write(off, v.X); view.Write(off + 4, v.Y); view.Write(off + 8, v.Z); }

    public float LTrigger { get { return view.ReadSingle(LTRIGGER); } }
    public int HapticOk { get { return view.ReadInt32(HAPTIC_OK); } }
    public int HapticFail { get { return view.ReadInt32(HAPTIC_FAIL); } }
    public int LitDrawn { get { return view.ReadInt32(LIT_DRAWN); } }
    public int FlashDrawn { get { return view.ReadInt32(FLASH_DRAWN); } }

    // the key under a hand's pointer; the visible flag is written last, so the layer never draws a half-written light
    public void WriteLit(int hand, bool visible, int slot, V3 centre, float w, float h) {
        lock (gate) {
            int o = H(hand);
            if (visible) { view.Write(o + H_LIT_SLOT, slot); Vec(o + H_LIT_C, centre); view.Write(o + H_LIT_W, w); view.Write(o + H_LIT_H, h); }
            view.Write(o + H_LIT_VISIBLE, visible ? 1 : 0);
        }
    }
    // a press: where, ok until MarkLost says otherwise; the seq is bumped last, so the layer reads the new place with it
    public void WritePress(int hand, int slot, V3 centre, float w, float h) {
        lock (gate) {
            int o = H(hand);
            view.Write(o + H_PRESS_SLOT, slot); Vec(o + H_PRESS_C, centre); view.Write(o + H_PRESS_W, w); view.Write(o + H_PRESS_H, h);
            view.Write(o + H_PRESS_OK, 1);
            Thread.MemoryBarrier();
            view.Write(o + H_PRESS_SEQ, view.ReadInt32(o + H_PRESS_SEQ) + 1);
        }
    }
    public void MarkLost(int hand) { lock (gate) view.Write(H(hand) + H_PRESS_OK, 0); }
    public void WriteLeftCursor(bool visible, V3 tip) {
        lock (gate) {
            int o = H(LEFT);
            if (visible) Vec(o + H_CURSOR_P, tip);
            view.Write(o + H_CURSOR_VISIBLE, visible ? 1 : 0);
        }
    }

    // for the self-tests and the layer test: raw reads and writes at a byte offset
    public int RawInt(int off) { return view.ReadInt32(off); }
    public float RawFloat(int off) { return view.ReadSingle(off); }
    public void RawWriteFloat(int off, float v) { view.Write(off, v); }
}
