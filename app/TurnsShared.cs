// TurnsShared.cs -- 1.15: each window's own tilt and turn (WindowViewTurns in ../shared.h, same offsets). A separate block
// because the main one is also read by other apps by name and size and must never change. This app creates it and writes
// every window's tilt and turn (radians) from the pointer loop; the layer opens it read-only and turns each window about
// its own middle (layer/face.inl), the same turn WindowFrame.Of (Pointer.cs) aims the rays with.
using System;
using System.IO.MemoryMappedFiles;

class TurnsShared : IDisposable {
    public const string NAME = @"Local\FlatToVR_WindowView_turns_v1";
    const uint MAGIC = 0x4E525554u, VERSION = 1;
    public const int SIZE = 72, TILT = 8, TURN = 40;
    readonly MemoryMappedFile file;
    readonly MemoryMappedViewAccessor view;

    public TurnsShared() : this(NAME) { }
    public TurnsShared(string name) {   // another name: the self-tests, never the live block
        file = MemoryMappedFile.CreateOrOpen(name, SIZE);
        view = file.CreateViewAccessor(0, SIZE);
        if (view.ReadUInt32(0) != MAGIC) { view.Write(4, VERSION); view.Write(0, MAGIC); }
    }
    public void Dispose() { view.Dispose(); file.Dispose(); }

    // degrees in, radians out (the layer's unit)
    public void Write(int slot, float tiltDeg, float turnDeg) {
        if (slot < 0 || slot >= Shared.SLOTS) return;
        view.Write(TILT + 4 * slot, tiltDeg * (float)(Math.PI / 180));
        view.Write(TURN + 4 * slot, turnDeg * (float)(Math.PI / 180));
    }
    public void Clear() { for (int i = 0; i < Shared.SLOTS; ++i) Write(i, 0, 0); }
    public int RawInt(int off) { return view.ReadInt32(off); }
    public float RawFloat(int off) { return view.ReadSingle(off); }
}
