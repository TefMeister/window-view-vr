"""fake_window.py -- stands in for the Window View app: fills slot 0 with a known picture (red left half, blue right
half) and keeps its frame counter and heartbeat going, so the layer can be tested without the app and without a real
window. At the end it prints what the layer reported into the header (LAYER: ...), which view_test.py reads.

    python fake_window.py [--seconds 8] [--width 400] [--height 200] [--resize]
--resize: after 3 s the picture becomes 300 x 300, green left / yellow right (the layer must remake its swapchain).
--room: the room mode on (1.4), with a recenter asked; --depth D: the window pushed back to D times the distance.
--ctrl: CONTROL mode switched on from the start (1.5), as the left stick click would.
Layout: shared.h (header 628 bytes, slots at 180 + 40 * n; pixel mapping = two 2560x1600 BGRA buffers).
"""
import argparse
import ctypes
import mmap
import struct
import time

NAME = "Local\\FlatToVR_WindowView_v2"
SLOT_NAME = "Local\\FlatToVR_WindowView_v2_slot%d"
MAGIC, VERSION = 0x57564956, 2
HEADER, SLOTS_OFF, SLOT = 628, 180, 40
SLOT_BYTES = 2560 * 1600 * 4
APP_HEARTBEAT, LAYER_HEARTBEAT, LAYER_PID, API, GAME_NAME = 8, 16, 24, 28, 36
DISTANCE, QUADS, UPLOADS, LAST_ERROR = 164, 168, 172, 176
ROOM_MODE, RECENTER_SEQ, ANCHOR_SEEN = 500, 504, 508
CTRL_MODE, POINTER_READY, RAY_VALID, RAY_SEQ = 536, 540, 544, 588
RED, BLUE, GREEN, YELLOW = (0, 0, 255, 255), (255, 0, 0, 255), (0, 255, 0, 255), (0, 255, 255, 255)   # BGRA

k32 = ctypes.windll.kernel32
k32.GetTickCount64.restype = ctypes.c_ulonglong


def pattern(w, h, left, right):
    row = bytes(left) * (w // 2) + bytes(right) * (w - w // 2)
    return row * h


def open_header():
    m = mmap.mmap(-1, HEADER, tagname=NAME)
    if struct.unpack_from("<I", m, 0)[0] != MAGIC:
        struct.pack_into("<II", m, 0, MAGIC, VERSION)
    return m


def layer_report(m):
    api = struct.unpack_from("<i", m, API)[0]
    name = m[GAME_NAME:GAME_NAME + 128].decode("utf-16-le").split("\0")[0]
    quads, uploads, err = struct.unpack_from("<iii", m, QUADS)
    shown = struct.unpack_from("<i", m, SLOTS_OFF + 32)[0]
    alive = k32.GetTickCount64() - struct.unpack_from("<Q", m, LAYER_HEARTBEAT)[0] < 3000
    room, want, seen = struct.unpack_from("<iii", m, ROOM_MODE)
    ctrl, ready, ray = struct.unpack_from("<iii", m, CTRL_MODE)
    rays = struct.unpack_from("<i", m, RAY_SEQ)[0]
    return "LAYER: api=%d game=%s alive=%d quads=%d uploads=%d shown_frame=%d last_error=%d room=%d anchor_set=%d pointer_ready=%d ctrl=%d ray_frames=%d" % (
        api, name, alive, quads, uploads, shown, err, room, int(room == 1 and seen == want), ready, ctrl, rays)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seconds", type=float, default=8.0)
    ap.add_argument("--width", type=int, default=400)
    ap.add_argument("--height", type=int, default=200)
    ap.add_argument("--resize", action="store_true")
    ap.add_argument("--room", action="store_true")
    ap.add_argument("--depth", type=float, default=1.0)
    ap.add_argument("--ctrl", action="store_true")
    a = ap.parse_args()
    m = open_header()
    struct.pack_into("<iiii", m, QUADS, 0, 0, 0, 0)   # fresh counters for this run (quads, uploads, last_error, slot0.visible)
    slot = mmap.mmap(-1, 2 * SLOT_BYTES, tagname=SLOT_NAME % 0)
    struct.pack_into("<f", m, DISTANCE, 1.0)
    struct.pack_into("<i", m, ROOM_MODE, 1 if a.room else 0)
    if a.room:
        struct.pack_into("<i", m, RECENTER_SEQ, struct.unpack_from("<i", m, RECENTER_SEQ)[0] + 1)
    struct.pack_into("<f", m, SLOTS_OFF + 36, a.depth)
    struct.pack_into("<iii", m, CTRL_MODE, 1 if a.ctrl else 0, 0, 0)
    struct.pack_into("<i", m, RAY_SEQ, 0)
    w, h, pic = a.width, a.height, pattern(a.width, a.height, RED, BLUE)
    frame, buf, t0, resized = 0, 0, time.time(), False
    try:
        while time.time() - t0 < a.seconds:
            if a.resize and not resized and time.time() - t0 > 3.0:
                w, h, pic, resized = 300, 300, pattern(300, 300, GREEN, YELLOW), True
            buf ^= 1
            slot[buf * SLOT_BYTES:buf * SLOT_BYTES + len(pic)] = pic
            frame += 1
            # slot 0: visible, width, height, x, y, scale, frame, buffer
            struct.pack_into("<iiifffii", m, SLOTS_OFF, 1, w, h, 0.0, 0.0, 0.001, frame, buf)
            struct.pack_into("<Q", m, APP_HEARTBEAT, k32.GetTickCount64())
            time.sleep(1 / 30)
    finally:
        print(layer_report(m))
        struct.pack_into("<i", m, SLOTS_OFF, 0)   # hidden again
        struct.pack_into("<i", m, ROOM_MODE, 0)
        struct.pack_into("<i", m, CTRL_MODE, 0)
        struct.pack_into("<Q", m, APP_HEARTBEAT, 0)


if __name__ == "__main__":
    main()
