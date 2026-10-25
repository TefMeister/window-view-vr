// window_view_layer.cpp -- an OpenXR API layer that hangs desktop windows in front of the player's eyes, in any OpenXR
// game that renders with D3D11 or D3D12.
//
// Why (Tefa, 2026-10-16): "a tool that lets me see any window from my pc in vr - so that i can choose a specific window
// i want to be present in vr, set it's opacity and freely use mouse to click things in that window, without the mouse
// and keyboard affecting the game". The windows follow the head "like an in-game menu window little further from my
// eyes", and the mouse moves them by their header.
//
// How: the Window View app captures the chosen windows and writes their pixels into shared memory (shared.h), one slot
// per window, with where it sits on an imaginary desk in front of the eyes. This layer sits between the game and the
// OpenXR runtime (registered as an implicit layer by the app). At every xrEndFrame it copies each slot whose picture
// changed into a swapchain of its own and appends one head-locked quad layer per visible slot after the game's own
// layers. The game never knows. Nothing here reads the mouse or keyboard: that is the app's job.
//
// 1.4 (2026-10-25, Tefa: "can the Window view actually leave the windows in place inside the VR headset, so they are not
// stuck to my view, but look like they are inside the game world essentially"): a room mode, off by default. The quads then
// live in the LOCAL reference space, around an anchor set from where the head looked (level, at the head's height) when the
// mode was switched on or the app asked for a recenter; each faces the anchor, and the runtime holds them still in the room.
// A per-window depth pushes a window back (it shrinks with the distance, as a real object would), in either mode.
#define XR_NO_PROTOTYPES
#define XR_USE_PLATFORM_WIN32
#define XR_USE_GRAPHICS_API_D3D11
#define XR_USE_GRAPHICS_API_D3D12
#include <windows.h>
#include <d3d11.h>
#include <d3d12.h>
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>
#include <openxr/openxr_loader_negotiation.h>

#include <cmath>
#include <cstring>
#include <map>
#include <mutex>
#include <type_traits>
#include <vector>

#include "../shared.h"

namespace {

PFN_xrGetInstanceProcAddr g_next_gipa = nullptr;
PFN_xrCreateSession g_next_create_session = nullptr;
PFN_xrDestroySession g_next_destroy_session = nullptr;
PFN_xrEndFrame g_next_end_frame = nullptr;
PFN_xrCreateReferenceSpace g_next_create_reference_space = nullptr;
PFN_xrLocateSpace g_next_locate_space = nullptr;
PFN_xrCreateSwapchain g_next_create_swapchain = nullptr;
PFN_xrDestroySwapchain g_next_destroy_swapchain = nullptr;
PFN_xrEnumerateSwapchainFormats g_next_enum_formats = nullptr;
PFN_xrEnumerateSwapchainImages g_next_enum_images = nullptr;
PFN_xrAcquireSwapchainImage g_next_acquire = nullptr;
PFN_xrWaitSwapchainImage g_next_wait = nullptr;
PFN_xrReleaseSwapchainImage g_next_release = nullptr;
XrInstance g_instance = XR_NULL_HANDLE;

// settings
constexpr int64_t kFormatPreference[] = {DXGI_FORMAT_B8G8R8A8_UNORM_SRGB, DXGI_FORMAT_B8G8R8A8_UNORM,
                                         DXGI_FORMAT_R8G8B8A8_UNORM_SRGB, DXGI_FORMAT_R8G8B8A8_UNORM};
constexpr ULONGLONG kAppAliveMs = 3000;        // the app counts as gone this long after its last heartbeat
constexpr int kRetryEveryFrames = 60;          // how often to look again for a slot's pixel mapping
constexpr float kDefaultDistance = 1.0f;       // metres in front of the eyes when the app says 0
constexpr XrDuration kImageWaitNs = 100000000; // 100 ms: never hang the game on a swapchain image
constexpr DWORD kFenceWaitMs = 1000;

struct Quad {   // one slot's picture in one session
    XrSwapchain swapchain = XR_NULL_HANDLE;
    LONG width = 0, height = 0;
    int64_t format = 0;
    bool swap_rb = false;            // the swapchain is RGBA, our pixels are BGRA
    std::vector<ID3D11Texture2D*> d11;
    std::vector<ID3D12Resource*> d12;
    ID3D12Resource* upload = nullptr; UINT64 upload_bytes = 0;
    LONG shown_frame = -1;           // -1: nothing uploaded yet, so the quad is not shown
};
struct Session {
    LONG api = WINDOW_VIEW_API_NONE;
    ID3D11Device* d11 = nullptr; ID3D11DeviceContext* ctx = nullptr;
    ID3D12Device* d12 = nullptr; ID3D12CommandQueue* queue = nullptr; ID3D12CommandAllocator* alloc = nullptr;
    ID3D12GraphicsCommandList* list = nullptr; ID3D12Fence* fence = nullptr; HANDLE fence_event = nullptr; UINT64 fence_value = 0;
    XrSpace view_space = XR_NULL_HANDLE;   // head-locked: the quads live here
    XrSpace local_space = XR_NULL_HANDLE;  // the room mode: the quads live here, around the anchor
    bool anchor_ok = false;
    LONG anchor_seq = -1;
    XrVector3f anchor_pos{0, 0, 0};
    XrQuaternionf anchor_rot{0, 0, 0, 1};  // a turn about the vertical only
    Quad quads[WINDOW_VIEW_SLOTS];
    std::vector<uint8_t> staging;
};
struct SlotMap { HANDLE h = nullptr; const uint8_t* p = nullptr; int retry = 0; };

std::recursive_mutex g_mtx;
std::map<XrSession, Session> g_sessions;
SlotMap g_slotmaps[WINDOW_VIEW_SLOTS];

template <class T> void release(T*& p) { if (p != nullptr) { p->Release(); p = nullptr; } }

// ---- the shared blocks -------------------------------------------------------------------------------------------
WindowViewShared* g_shared = nullptr;

WindowViewShared* shared() {
    if (g_shared != nullptr) return g_shared;
    HANDLE h = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(WindowViewShared), WINDOW_VIEW_MAPPING_NAME);
    if (h == nullptr) return nullptr;
    auto* p = static_cast<WindowViewShared*>(MapViewOfFile(h, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(WindowViewShared)));
    if (p == nullptr) { CloseHandle(h); return nullptr; }
    if (p->magic != WINDOW_VIEW_MAGIC) { p->version = WINDOW_VIEW_VERSION; p->magic = WINDOW_VIEW_MAGIC; }   // first one in sets it up
    g_shared = p;   // the handle stays open for the life of the process, on purpose
    return p;
}

// a slot's pixel mapping, opened when the app has made it; nullptr until then (looked for again every so often)
const uint8_t* slot_pixels(int slot) {
    SlotMap& m = g_slotmaps[slot];
    if (m.p != nullptr) return m.p;
    if (m.retry-- > 0) return nullptr;
    m.retry = kRetryEveryFrames;
    wchar_t name[96]{};
    swprintf_s(name, WINDOW_VIEW_SLOT_MAPPING_FMT, static_cast<unsigned>(slot));
    HANDLE h = OpenFileMappingW(FILE_MAP_READ, FALSE, name);
    if (h == nullptr) return nullptr;
    void* p = MapViewOfFile(h, FILE_MAP_READ, 0, 0, WINDOW_VIEW_SLOT_MAPPING_BYTES);
    if (p == nullptr) { CloseHandle(h); return nullptr; }
    m.h = h;
    m.p = static_cast<const uint8_t*>(p);
    return m.p;
}

void note_error(LONG code) { auto* s = shared(); if (s != nullptr && code != 0) s->last_error = code; }

void heartbeat(WindowViewShared* s, LONG api) {
    s->layer_heartbeat_ms = GetTickCount64();
    s->layer_pid = GetCurrentProcessId();
    s->api = api;
    if (s->game_name[0] == 0 || s->game_name_pid != s->layer_pid) {
        wchar_t path[MAX_PATH]{};
        GetModuleFileNameW(nullptr, path, MAX_PATH);
        const wchar_t* name = wcsrchr(path, L'\\');
        name = name ? name + 1 : path;
        wcsncpy_s(s->game_name, name, _TRUNCATE);
        s->game_name_pid = s->layer_pid;
    }
}

// ---- swapchains and uploads --------------------------------------------------------------------------------------
void destroy_quad(Quad& q) {
    for (auto* t : q.d11) release(t);
    q.d11.clear();
    q.d12.clear();   // the runtime owns the images; the vector only borrowed them
    release(q.upload);
    q.upload_bytes = 0;
    if (q.swapchain != XR_NULL_HANDLE && g_next_destroy_swapchain != nullptr) g_next_destroy_swapchain(q.swapchain);
    q.swapchain = XR_NULL_HANDLE;
    q.width = q.height = 0;
    q.shown_frame = -1;
}

bool pick_format(XrSession xs, int64_t& format) {
    uint32_t n = 0;
    if (XR_FAILED(g_next_enum_formats(xs, 0, &n, nullptr)) || n == 0) return false;
    std::vector<int64_t> have(n);
    if (XR_FAILED(g_next_enum_formats(xs, n, &n, have.data()))) return false;
    for (int64_t want : kFormatPreference) {
        for (uint32_t i = 0; i < n; ++i) if (have[i] == want) { format = want; return true; }
    }
    return false;
}

// a swapchain of exactly this size for the quad; remade when the size changes
bool ensure_swapchain(Session& s, XrSession xs, Quad& q, LONG w, LONG h) {
    if (q.swapchain != XR_NULL_HANDLE && q.width == w && q.height == h) return true;
    destroy_quad(q);
    if (g_next_create_swapchain == nullptr || g_next_enum_formats == nullptr || g_next_enum_images == nullptr) return false;
    int64_t format = 0;
    if (!pick_format(xs, format)) { note_error(-1000); return false; }
    XrSwapchainCreateInfo ci{XR_TYPE_SWAPCHAIN_CREATE_INFO};
    ci.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT | XR_SWAPCHAIN_USAGE_SAMPLED_BIT | XR_SWAPCHAIN_USAGE_TRANSFER_DST_BIT;
    ci.format = format;
    ci.sampleCount = 1;
    ci.width = static_cast<uint32_t>(w);
    ci.height = static_cast<uint32_t>(h);
    ci.faceCount = 1;
    ci.arraySize = 1;
    ci.mipCount = 1;
    XrSwapchain sc = XR_NULL_HANDLE;
    XrResult r = g_next_create_swapchain(xs, &ci, &sc);
    if (XR_FAILED(r)) { note_error(r); return false; }
    uint32_t n = 0;
    r = g_next_enum_images(sc, 0, &n, nullptr);
    if (XR_FAILED(r) || n == 0) { note_error(r); g_next_destroy_swapchain(sc); return false; }
    if (s.api == WINDOW_VIEW_API_D3D11) {
        std::vector<XrSwapchainImageD3D11KHR> imgs(n, XrSwapchainImageD3D11KHR{XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR});
        r = g_next_enum_images(sc, n, &n, reinterpret_cast<XrSwapchainImageBaseHeader*>(imgs.data()));
        if (XR_FAILED(r)) { note_error(r); g_next_destroy_swapchain(sc); return false; }
        for (uint32_t i = 0; i < n; ++i) { imgs[i].texture->AddRef(); q.d11.push_back(imgs[i].texture); }
    } else {
        std::vector<XrSwapchainImageD3D12KHR> imgs(n, XrSwapchainImageD3D12KHR{XR_TYPE_SWAPCHAIN_IMAGE_D3D12_KHR});
        r = g_next_enum_images(sc, n, &n, reinterpret_cast<XrSwapchainImageBaseHeader*>(imgs.data()));
        if (XR_FAILED(r)) { note_error(r); g_next_destroy_swapchain(sc); return false; }
        for (uint32_t i = 0; i < n; ++i) q.d12.push_back(imgs[i].texture);
    }
    q.swapchain = sc;
    q.width = w;
    q.height = h;
    q.format = format;
    q.swap_rb = format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB || format == DXGI_FORMAT_R8G8B8A8_UNORM;
    return true;
}

bool upload_d3d11(Session& s, Quad& q, uint32_t index, const uint8_t* px) {
    if (index >= q.d11.size() || s.ctx == nullptr) return false;
    s.ctx->UpdateSubresource(q.d11[index], 0, nullptr, px, static_cast<UINT>(q.width * 4), 0);
    return true;
}

// an upload buffer (rows padded to 256 bytes, as D3D12 wants), a copy on the game's queue, and a wait so the buffer
// can be reused next time; the image goes RENDER_TARGET -> COPY_DEST -> RENDER_TARGET, the state OpenXR hands it over in
bool upload_d3d12(Session& s, Quad& q, uint32_t index, const uint8_t* px) {
    if (index >= q.d12.size() || s.list == nullptr) return false;
    const UINT pitch = (static_cast<UINT>(q.width) * 4 + 255u) & ~255u;
    const UINT64 bytes = static_cast<UINT64>(pitch) * static_cast<UINT64>(q.height);
    if (q.upload == nullptr || q.upload_bytes < bytes) {
        release(q.upload);
        D3D12_HEAP_PROPERTIES hp{};
        hp.Type = D3D12_HEAP_TYPE_UPLOAD;
        D3D12_RESOURCE_DESC rd{};
        rd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        rd.Width = bytes;
        rd.Height = 1;
        rd.DepthOrArraySize = 1;
        rd.MipLevels = 1;
        rd.SampleDesc.Count = 1;
        rd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        HRESULT hr = s.d12->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&q.upload));
        if (FAILED(hr)) { note_error(static_cast<LONG>(hr)); return false; }
        q.upload_bytes = bytes;
    }
    uint8_t* dst = nullptr;
    const D3D12_RANGE none{0, 0};
    if (FAILED(q.upload->Map(0, &none, reinterpret_cast<void**>(&dst)))) return false;
    for (LONG y = 0; y < q.height; ++y) std::memcpy(dst + static_cast<size_t>(y) * pitch, px + static_cast<size_t>(y) * q.width * 4, static_cast<size_t>(q.width) * 4);
    q.upload->Unmap(0, nullptr);
    if (FAILED(s.alloc->Reset()) || FAILED(s.list->Reset(s.alloc, nullptr))) return false;
    ID3D12Resource* tex = q.d12[index];
    D3D12_RESOURCE_BARRIER b{};
    b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    b.Transition.pResource = tex;
    b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    b.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
    b.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_DEST;
    s.list->ResourceBarrier(1, &b);
    D3D12_TEXTURE_COPY_LOCATION to{};
    to.pResource = tex;
    to.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
    to.SubresourceIndex = 0;
    D3D12_TEXTURE_COPY_LOCATION from{};
    from.pResource = q.upload;
    from.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
    from.PlacedFootprint.Offset = 0;
    from.PlacedFootprint.Footprint.Format = static_cast<DXGI_FORMAT>(q.format);
    from.PlacedFootprint.Footprint.Width = static_cast<UINT>(q.width);
    from.PlacedFootprint.Footprint.Height = static_cast<UINT>(q.height);
    from.PlacedFootprint.Footprint.Depth = 1;
    from.PlacedFootprint.Footprint.RowPitch = pitch;
    s.list->CopyTextureRegion(&to, 0, 0, 0, &from, nullptr);
    b.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
    b.Transition.StateAfter = D3D12_RESOURCE_STATE_RENDER_TARGET;
    s.list->ResourceBarrier(1, &b);
    if (FAILED(s.list->Close())) return false;
    ID3D12CommandList* lists[] = {s.list};
    s.queue->ExecuteCommandLists(1, lists);
    const UINT64 v = ++s.fence_value;
    s.queue->Signal(s.fence, v);
    if (s.fence->GetCompletedValue() < v) {
        s.fence->SetEventOnCompletion(v, s.fence_event);
        WaitForSingleObject(s.fence_event, kFenceWaitMs);
    }
    return true;
}

// acquire an image, put the picture in, release it; false = nothing changed in the swapchain
bool upload(Session& s, Quad& q, const uint8_t* px) {
    XrSwapchainImageAcquireInfo ai{XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO};
    uint32_t index = 0;
    XrResult r = g_next_acquire(q.swapchain, &ai, &index);
    if (XR_FAILED(r)) { note_error(r); return false; }
    XrSwapchainImageWaitInfo wi{XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO};
    wi.timeout = kImageWaitNs;
    r = g_next_wait(q.swapchain, &wi);
    if (r != XR_SUCCESS) { note_error(r); return false; }   // a timed-out image is left acquired; the next frame tries again
    const bool ok = s.api == WINDOW_VIEW_API_D3D11 ? upload_d3d11(s, q, index, px) : upload_d3d12(s, q, index, px);
    XrSwapchainImageReleaseInfo ri{XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO};
    g_next_release(q.swapchain, &ri);
    return ok;
}

// copy the slot's current buffer out of shared memory (swizzling if the swapchain is RGBA); false = the app flipped
// buffers while we read, so the copy is a mix of two frames and is thrown away
bool copy_pixels(Session& s, const WindowViewSlot& sl, const Quad& q, const uint8_t* base, LONG frame, LONG buf) {
    const size_t bytes = static_cast<size_t>(q.width) * static_cast<size_t>(q.height) * 4;
    s.staging.resize(bytes);
    const uint8_t* src = base + static_cast<size_t>(buf) * WINDOW_VIEW_SLOT_BYTES;
    if (!q.swap_rb) std::memcpy(s.staging.data(), src, bytes);
    else for (size_t i = 0; i < bytes; i += 4) { s.staging[i] = src[i + 2]; s.staging[i + 1] = src[i + 1]; s.staging[i + 2] = src[i]; s.staging[i + 3] = src[i + 3]; }
    return sl.frame == frame && sl.buffer == buf;
}

// ---- small vector / quaternion helpers for the room mode -------------------------------------------------------------
XrQuaternionf q_mul(const XrQuaternionf& a, const XrQuaternionf& b) {
    return {a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y, a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
            a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w, a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z};
}
XrVector3f q_rot(const XrQuaternionf& q, const XrVector3f& v) {
    const XrQuaternionf p{v.x, v.y, v.z, 0}, c{-q.x, -q.y, -q.z, q.w};
    const XrQuaternionf r = q_mul(q_mul(q, p), c);
    return {r.x, r.y, r.z};
}
XrQuaternionf q_axis(float x, float y, float z, float angle) { const float h = angle * 0.5f, sn = std::sin(h); return {x * sn, y * sn, z * sn, std::cos(h)}; }

bool ensure_space(XrSession xs, XrReferenceSpaceType type, XrSpace& space) {
    if (space != XR_NULL_HANDLE) return true;
    if (g_next_create_reference_space == nullptr) return false;
    XrReferenceSpaceCreateInfo ci{XR_TYPE_REFERENCE_SPACE_CREATE_INFO};
    ci.referenceSpaceType = type;
    ci.poseInReferenceSpace.orientation.w = 1.0f;
    const XrResult r = g_next_create_reference_space(xs, &ci, &space);
    if (XR_FAILED(r)) { note_error(r); space = XR_NULL_HANDLE; return false; }
    return true;
}

// the anchor: where the head is, turned the way it looks but level (only the turn about the vertical is kept)
void update_anchor(Session& s, WindowViewShared* sh, XrTime when) {
    const LONG want = sh->recenter_seq;
    if (s.anchor_ok && want == s.anchor_seq) return;
    if (g_next_locate_space == nullptr || when == 0) return;
    XrSpaceLocation loc{XR_TYPE_SPACE_LOCATION};
    const XrResult r = g_next_locate_space(s.view_space, s.local_space, when, &loc);
    if (XR_FAILED(r)) { note_error(r); return; }
    const XrSpaceLocationFlags need = XR_SPACE_LOCATION_POSITION_VALID_BIT | XR_SPACE_LOCATION_ORIENTATION_VALID_BIT;
    if ((loc.locationFlags & need) != need) return;   // tracking lost this frame: try again next frame
    const XrVector3f fwd = q_rot(loc.pose.orientation, {0, 0, -1});
    const float yaw = std::atan2(-fwd.x, -fwd.z);
    s.anchor_pos = loc.pose.position;
    s.anchor_rot = q_axis(0, 1, 0, yaw);
    s.anchor_ok = true;
    s.anchor_seq = want;
    sh->anchor_seq_seen = want;
}

// everything this frame: refresh changed pictures and list one quad per visible slot (the quads vector is reserved by
// the caller so the pointers taken afterwards stay put)
void build_quads(Session& s, XrSession xs, std::vector<XrCompositionLayerQuad>& out, XrTime when) {
    auto* sh = shared();
    if (sh == nullptr) return;
    heartbeat(sh, s.api);
    if (s.api != WINDOW_VIEW_API_D3D11 && s.api != WINDOW_VIEW_API_D3D12) return;
    const ULONGLONG now = GetTickCount64();
    if (sh->app_heartbeat_ms == 0 || now - sh->app_heartbeat_ms > kAppAliveMs) return;   // the app is gone: show nothing
    if (!ensure_space(xs, XR_REFERENCE_SPACE_TYPE_VIEW, s.view_space)) return;
    const float distance = sh->distance > 0.01f ? sh->distance : kDefaultDistance;
    // the room mode: until the anchor is known (the first tracked frame) the windows stay head-locked
    bool room = sh->room_mode != 0 && ensure_space(xs, XR_REFERENCE_SPACE_TYPE_LOCAL, s.local_space);
    if (room) update_anchor(s, sh, when);
    if (!room) s.anchor_ok = false;   // switched off: the next switch-on takes a fresh anchor
    room = room && s.anchor_ok;
    for (int i = 0; i < WINDOW_VIEW_SLOTS; ++i) {
        WindowViewSlot& sl = sh->slots[i];
        Quad& q = s.quads[i];
        const LONG w = sl.width, h = sl.height;
        if (!sl.visible || w <= 0 || h <= 0 || w > WINDOW_VIEW_MAX_W || h > WINDOW_VIEW_MAX_H) {
            if (q.swapchain != XR_NULL_HANDLE) destroy_quad(q);   // the window went away or was hidden
            continue;
        }
        if (!ensure_swapchain(s, xs, q, w, h)) continue;
        const LONG frame = sl.frame, buf = sl.buffer & 1;
        if (frame != q.shown_frame) {
            const uint8_t* px = slot_pixels(i);
            if (px != nullptr && copy_pixels(s, sl, q, px, frame, buf) && upload(s, q, s.staging.data())) {
                q.shown_frame = frame;
                sl.shown_frame = frame;
                InterlockedIncrement(&sh->uploads);
            }
        }
        if (q.shown_frame < 0) continue;   // never uploaded: an empty swapchain would show rubbish
        XrCompositionLayerQuad l{XR_TYPE_COMPOSITION_LAYER_QUAD};
        l.layerFlags = XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT | XR_COMPOSITION_LAYER_UNPREMULTIPLIED_ALPHA_BIT;
        l.space = room ? s.local_space : s.view_space;
        l.eyeVisibility = XR_EYE_VISIBILITY_BOTH;
        l.subImage.swapchain = q.swapchain;
        l.subImage.imageRect = {{0, 0}, {static_cast<int32_t>(q.width), static_cast<int32_t>(q.height)}};
        l.subImage.imageArrayIndex = 0;
        // on the desk the window's centre is (x, y) at `distance`; pushed back by `depth` along the same line of sight, so it
        // keeps its place in the view and shrinks with the distance (its size in metres stays the same)
        const float depth = sl.depth > 0.05f ? sl.depth : 1.0f;
        const XrVector3f p{sl.x * depth, sl.y * depth, -distance * depth};
        if (!room) {
            l.pose.orientation = {0, 0, 0, 1};
            l.pose.position = p;
        } else {
            // facing the anchor, upright: turn +Z (the quad's front) towards it, first tipped about X, then about Y
            const float len = std::sqrt(p.x * p.x + p.y * p.y + p.z * p.z);
            const XrVector3f n{-p.x / len, -p.y / len, -p.z / len};
            const XrQuaternionf face = q_mul(q_axis(0, 1, 0, std::atan2(n.x, n.z)), q_axis(1, 0, 0, -std::asin(n.y)));
            const XrVector3f off = q_rot(s.anchor_rot, p);
            l.pose.position = {s.anchor_pos.x + off.x, s.anchor_pos.y + off.y, s.anchor_pos.z + off.z};
            l.pose.orientation = q_mul(s.anchor_rot, face);
        }
        l.size = {static_cast<float>(q.width) * sl.scale, static_cast<float>(q.height) * sl.scale};
        out.push_back(l);
    }
    if (!out.empty()) InterlockedIncrement(&sh->quads_submitted);
}

void destroy_session_state(Session& s) {
    for (auto& q : s.quads) destroy_quad(q);
    release(s.ctx);
    release(s.d11);
    release(s.list);
    release(s.alloc);
    release(s.fence);
    if (s.fence_event != nullptr) { CloseHandle(s.fence_event); s.fence_event = nullptr; }
    release(s.queue);
    release(s.d12);
}

// ---- the hooked functions ----------------------------------------------------------------------------------------
XRAPI_ATTR XrResult XRAPI_CALL wv_xrCreateSession(XrInstance instance, const XrSessionCreateInfo* info, XrSession* session) {
    const XrResult r = g_next_create_session(instance, info, session);
    if (XR_FAILED(r) || session == nullptr || info == nullptr) return r;
    Session s{};
    for (auto* n = static_cast<const XrBaseInStructure*>(info->next); n != nullptr; n = n->next) {
        if (n->type == XR_TYPE_GRAPHICS_BINDING_D3D11_KHR) {
            auto* b = reinterpret_cast<const XrGraphicsBindingD3D11KHR*>(n);
            if (b->device == nullptr) continue;
            s.api = WINDOW_VIEW_API_D3D11;
            s.d11 = b->device;
            s.d11->AddRef();
            s.d11->GetImmediateContext(&s.ctx);
        } else if (n->type == XR_TYPE_GRAPHICS_BINDING_D3D12_KHR) {
            auto* b = reinterpret_cast<const XrGraphicsBindingD3D12KHR*>(n);
            if (b->device == nullptr || b->queue == nullptr) continue;
            s.api = WINDOW_VIEW_API_D3D12;
            s.d12 = b->device;
            s.d12->AddRef();
            s.queue = b->queue;
            s.queue->AddRef();
            const D3D12_COMMAND_LIST_TYPE type = s.queue->GetDesc().Type;
            bool ok = SUCCEEDED(s.d12->CreateCommandAllocator(type, IID_PPV_ARGS(&s.alloc)));
            ok = ok && SUCCEEDED(s.d12->CreateCommandList(0, type, s.alloc, nullptr, IID_PPV_ARGS(&s.list)));
            if (ok) s.list->Close();   // made open; kept closed between uploads
            ok = ok && SUCCEEDED(s.d12->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&s.fence)));
            s.fence_event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
            if (!ok || s.fence_event == nullptr) { destroy_session_state(s); s = Session{}; s.api = WINDOW_VIEW_API_OTHER; note_error(-1001); }
        }
    }
    if (s.api == WINDOW_VIEW_API_NONE) s.api = WINDOW_VIEW_API_OTHER;   // Vulkan, OpenGL, headless: nothing to draw with
    std::lock_guard _{g_mtx};
    g_sessions[*session] = s;
    return r;
}

XRAPI_ATTR XrResult XRAPI_CALL wv_xrDestroySession(XrSession session) {
    {
        std::lock_guard _{g_mtx};
        auto it = g_sessions.find(session);
        if (it != g_sessions.end()) { destroy_session_state(it->second); g_sessions.erase(it); }   // the runtime destroys the space with the session
    }
    return g_next_destroy_session(session);
}

XRAPI_ATTR XrResult XRAPI_CALL wv_xrEndFrame(XrSession session, const XrFrameEndInfo* info) {
    std::vector<XrCompositionLayerQuad> quads;
    quads.reserve(WINDOW_VIEW_SLOTS);
    if (info != nullptr) {
        std::lock_guard _{g_mtx};
        auto it = g_sessions.find(session);
        if (it != g_sessions.end()) build_quads(it->second, session, quads, info->displayTime);
    }
    if (quads.empty()) return g_next_end_frame(session, info);
    std::vector<const XrCompositionLayerBaseHeader*> layers(info->layers, info->layers + info->layerCount);
    for (const auto& q : quads) layers.push_back(reinterpret_cast<const XrCompositionLayerBaseHeader*>(&q));
    XrFrameEndInfo copy = *info;   // the game's own layers first, ours on top
    copy.layerCount = static_cast<uint32_t>(layers.size());
    copy.layers = layers.data();
    return g_next_end_frame(session, &copy);
}

XRAPI_ATTR XrResult XRAPI_CALL wv_xrGetInstanceProcAddr(XrInstance instance, const char* name, PFN_xrVoidFunction* function) {
    const XrResult r = g_next_gipa(instance, name, function);
    if (XR_FAILED(r) || function == nullptr || *function == nullptr || name == nullptr) return r;
    struct Hook { const char* name; PFN_xrVoidFunction ours; PFN_xrVoidFunction* next; };
    static const Hook hooks[] = {
        {"xrCreateSession", (PFN_xrVoidFunction)wv_xrCreateSession, (PFN_xrVoidFunction*)&g_next_create_session},
        {"xrDestroySession", (PFN_xrVoidFunction)wv_xrDestroySession, (PFN_xrVoidFunction*)&g_next_destroy_session},
        {"xrEndFrame", (PFN_xrVoidFunction)wv_xrEndFrame, (PFN_xrVoidFunction*)&g_next_end_frame},
    };
    for (const auto& h : hooks) {
        if (std::strcmp(name, h.name) == 0) { *h.next = *function; *function = h.ours; break; }
    }
    return r;
}

// the functions the layer itself calls, fetched once the instance exists
void fetch_next(XrInstance instance) {
    auto get = [&](const char* n, auto& slot) {
        PFN_xrVoidFunction f = nullptr;
        if (slot == nullptr && XR_SUCCEEDED(g_next_gipa(instance, n, &f)) && f != nullptr) slot = reinterpret_cast<std::remove_reference_t<decltype(slot)>>(f);
    };
    get("xrCreateSession", g_next_create_session);
    get("xrDestroySession", g_next_destroy_session);
    get("xrEndFrame", g_next_end_frame);
    get("xrCreateReferenceSpace", g_next_create_reference_space);
    get("xrLocateSpace", g_next_locate_space);
    get("xrCreateSwapchain", g_next_create_swapchain);
    get("xrDestroySwapchain", g_next_destroy_swapchain);
    get("xrEnumerateSwapchainFormats", g_next_enum_formats);
    get("xrEnumerateSwapchainImages", g_next_enum_images);
    get("xrAcquireSwapchainImage", g_next_acquire);
    get("xrWaitSwapchainImage", g_next_wait);
    get("xrReleaseSwapchainImage", g_next_release);
    g_instance = instance;
}

XRAPI_ATTR XrResult XRAPI_CALL wv_xrCreateApiLayerInstance(const XrInstanceCreateInfo* info, const XrApiLayerCreateInfo* layer_info, XrInstance* instance) {
    if (layer_info == nullptr || layer_info->nextInfo == nullptr) return XR_ERROR_INITIALIZATION_FAILED;
    g_next_gipa = layer_info->nextInfo->nextGetInstanceProcAddr;
    XrApiLayerCreateInfo next_info = *layer_info;   // the next layer down gets the chain one step further on
    next_info.nextInfo = layer_info->nextInfo->next;
    const XrResult r = layer_info->nextInfo->nextCreateApiLayerInstance(info, &next_info, instance);
    if (XR_SUCCEEDED(r) && instance != nullptr) fetch_next(*instance);
    return r;
}

} // namespace

// 32-bit build: XRAPI_CALL is __stdcall there, which decorates the export; export the plain name too (as Hand Freeze v3.1)
#if defined(_M_IX86)
#pragma comment(linker, "/EXPORT:xrNegotiateLoaderApiLayerInterface=_xrNegotiateLoaderApiLayerInterface@12")
#endif
extern "C" __declspec(dllexport) XrResult XRAPI_CALL xrNegotiateLoaderApiLayerInterface(const XrNegotiateLoaderInfo* loader_info, const char* /*layer_name*/, XrNegotiateApiLayerRequest* request) {
    if (loader_info == nullptr || request == nullptr || loader_info->structType != XR_LOADER_INTERFACE_STRUCT_LOADER_INFO ||
        request->structType != XR_LOADER_INTERFACE_STRUCT_API_LAYER_REQUEST ||
        loader_info->minInterfaceVersion > XR_CURRENT_LOADER_API_LAYER_VERSION || loader_info->maxInterfaceVersion < XR_CURRENT_LOADER_API_LAYER_VERSION) {
        return XR_ERROR_INITIALIZATION_FAILED;
    }
    request->layerInterfaceVersion = XR_CURRENT_LOADER_API_LAYER_VERSION;
    request->layerApiVersion = loader_info->maxApiVersion < XR_CURRENT_API_VERSION ? loader_info->maxApiVersion : XR_CURRENT_API_VERSION;
    request->getInstanceProcAddr = wv_xrGetInstanceProcAddr;
    request->createApiLayerInstance = wv_xrCreateApiLayerInstance;
    return XR_SUCCESS;
}
