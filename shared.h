// shared.h -- the hand-over between the Window View app and the OpenXR layer: one small header block per Windows session
// plus one pixel block per slot. The app writes everything a window needs (where, how big, which pixel buffer is
// finished); the layer (inside the game) writes a heartbeat, which graphics API the game uses and what it managed to
// show. The C# app reads and writes the header by the byte offsets in the comments, so the layout must never change
// without bumping WINDOW_VIEW_VERSION and the mapping names together.
//
// Pixels: each slot has its own mapping of two BGRA buffers (top-down rows, tightly packed, width*4 bytes per row),
// double buffered: the app fills the buffer the layer is NOT reading, then sets `buffer` and bumps `frame`. The layer
// copies the current buffer when `frame` changed and throws the copy away if `frame` moved while it was reading.
// Only the app creates slot mappings (they are big); the layer opens them and shows nothing while they are missing.
#pragma once
#include <windows.h>
#include <cstdint>

#define WINDOW_VIEW_MAPPING_NAME L"Local\\FlatToVR_WindowView_v1"
#define WINDOW_VIEW_SLOT_MAPPING_FMT L"Local\\FlatToVR_WindowView_v1_slot%u"   // %u = slot index
#define WINDOW_VIEW_MAGIC 0x57564956u   // "VIVW"
#define WINDOW_VIEW_VERSION 1u
#define WINDOW_VIEW_SLOTS 8
#define WINDOW_VIEW_MAX_W 2560
#define WINDOW_VIEW_MAX_H 1600
#define WINDOW_VIEW_SLOT_BYTES (WINDOW_VIEW_MAX_W * WINDOW_VIEW_MAX_H * 4)   // one pixel buffer
#define WINDOW_VIEW_SLOT_MAPPING_BYTES (2 * WINDOW_VIEW_SLOT_BYTES)        // both buffers of one slot
#define WINDOW_VIEW_HEADER_SIZE 500     // bytes; the app maps exactly this much (WindowView.cs SIZE)

// which graphics API the game binds (header `api`)
#define WINDOW_VIEW_API_NONE 0
#define WINDOW_VIEW_API_D3D11 1
#define WINDOW_VIEW_API_D3D12 2
#define WINDOW_VIEW_API_OTHER 3   // Vulkan, OpenGL: nothing is shown

#pragma pack(push, 4)
struct WindowViewSlot {              // 40 bytes, at header offset 180 + 40 * slot
    volatile LONG visible;           //  +0  app: 1 = show this slot
    volatile LONG width;             //  +4  app: pixel size of the picture in the buffers
    volatile LONG height;            //  +8
    float x;                         // +12  app: centre of the quad on the desk, metres right of the eyes' middle
    float y;                         // +16  app: ... metres up
    float scale;                     // +20  app: metres per pixel
    volatile LONG frame;             // +24  app: +1 after every finished buffer
    volatile LONG buffer;            // +28  app: which buffer (0 or 1) holds `frame`
    volatile LONG shown_frame;       // +32  layer: the last frame it uploaded into the game
    LONG reserved;                   // +36
};
struct WindowViewShared {
    uint32_t magic;                  //   0
    uint32_t version;                //   4
    uint64_t app_heartbeat_ms;       //   8  app: GetTickCount64() at its last tick; stale = the app is gone, show nothing
    uint64_t layer_heartbeat_ms;     //  16  layer: GetTickCount64() at its last xrEndFrame
    uint32_t layer_pid;              //  24  layer: the game's process id
    volatile LONG api;               //  28  layer: WINDOW_VIEW_API_* of the game's session
    uint32_t game_name_pid;          //  32  layer: whose name is in game_name
    wchar_t game_name[64];           //  36  layer: the game's exe name
    float distance;                  // 164  app: how far in front of the eyes the desk hangs, metres (0 = 1.0)
    volatile LONG quads_submitted;   // 168  layer: frames in which it added at least one quad
    volatile LONG uploads;           // 172  layer: pictures copied into the game
    volatile LONG last_error;        // 176  layer: the last failing result (XrResult or HRESULT as a LONG), 0 = none
    WindowViewSlot slots[WINDOW_VIEW_SLOTS];   // 180
};                                   // 500 bytes
#pragma pack(pop)
static_assert(sizeof(WindowViewSlot) == 40, "the app reads this layout by offset");
static_assert(sizeof(WindowViewShared) == WINDOW_VIEW_HEADER_SIZE, "the app reads this layout by offset");
