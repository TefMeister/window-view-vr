// keys.inl -- 1.12: the pointer keyboard's instant parts, drawn by the layer every headset frame (Tefa 2026-11-14: "an
// actual keyboard i can use my motion controller pointers in to type - like the one VD uses", "both hands please"). The app
// says, in the keys block (shared.h WindowViewKeys), which key lies under each hand's pointer and when a hand pressed one;
// here, every frame: a faint light over each lit key, a short bright flash over a pressed key (green, red when the press
// was lost), one short buzz in the pressing hand, and the left hand's arrow while it points at the keyboard. These are
// small quads of their own: nothing is drawn into the keyboard's picture, so no window repaints because of them.
// Part of window_view_layer.cpp (included after add_arrow); controllers.inl gives buzz() and writes the left trigger.

constexpr int kSolidPx = 4;
constexpr ULONGLONG kFlashMs = 120;
enum { SOLID_LIGHT, SOLID_OK, SOLID_LOST };
// BGRA, not premultiplied: white at 25 % (64), green and red at 60 % (153)
constexpr uint8_t kSolid[3][4] = {{255, 255, 255, 64}, {80, 200, 60, 153}, {60, 60, 230, 153}};

bool ensure_solid(Session& s, XrSession xs, int kind) {
    Quad& q = s.solid[kind];
    if (!ensure_swapchain(s, xs, q, kSolidPx, kSolidPx)) return false;
    if (q.shown_frame >= 0) return true;
    s.staging.assign(kSolidPx * kSolidPx * 4, 0);
    for (int i = 0; i < kSolidPx * kSolidPx; ++i) {
        uint8_t* p = &s.staging[static_cast<size_t>(i) * 4];
        p[0] = kSolid[kind][0]; p[1] = kSolid[kind][1]; p[2] = kSolid[kind][2]; p[3] = kSolid[kind][3];
        if (q.swap_rb) std::swap(p[0], p[2]);
    }
    if (!upload(s, q, s.staging.data())) return false;
    q.shown_frame = 0;
    return true;
}

// a w x h rectangle centred on `c` (the desk's frame), lying on window `slot`
void add_rect(Session& s, XrSession xs, LONG slot, const float c[3], float w, float h, int kind, bool room, std::vector<XrCompositionLayerQuad>& out) {
    if (slot < 0 || slot >= WINDOW_VIEW_SLOTS || w <= 0 || h <= 0 || s.quads[slot].swapchain == XR_NULL_HANDLE) return;
    if (!ensure_solid(s, xs, kind)) return;
    XrVector3f p{c[0], c[1], c[2]};
    XrQuaternionf o = s.slot_face[slot];
    if (room) { const XrVector3f wv = q_rot(s.anchor_rot, p); p = {s.anchor_pos.x + wv.x, s.anchor_pos.y + wv.y, s.anchor_pos.z + wv.z}; o = q_mul(s.anchor_rot, o); }
    XrCompositionLayerQuad l{XR_TYPE_COMPOSITION_LAYER_QUAD};
    l.layerFlags = XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT | XR_COMPOSITION_LAYER_UNPREMULTIPLIED_ALPHA_BIT;
    l.space = room ? s.local_space : s.view_space;
    l.eyeVisibility = XR_EYE_VISIBILITY_BOTH;
    l.subImage.swapchain = s.solid[kind].swapchain;
    l.subImage.imageRect = {{0, 0}, {kSolidPx, kSolidPx}};
    l.pose = {o, p};
    l.size = {w, h};
    out.push_back(l);
}

void keys_frame(Session& s, XrSession xs, WindowViewShared* sh, bool room, std::vector<XrCompositionLayerQuad>& out) {
    WindowViewKeys* k = keys_block();
    if (k == nullptr) return;
    const ULONGLONG now = GetTickCount64();
    bool lit = false, flashed = false;
    for (int hand = 0; hand < 2; ++hand) {
        WindowViewKeysHand& h = k->hand[hand];
        const LONG seq = h.press_seq;
        if (!s.press_known[hand]) { s.press_known[hand] = true; s.press_seen[hand] = seq; }   // no flash for presses before we looked
        else if (seq != s.press_seen[hand]) {
            s.press_seen[hand] = seq;
            s.flash_until[hand] = now + kFlashMs;
            if (sh->ctrl_mode != 0 && buzz(s, xs, hand)) InterlockedIncrement(&k->haptic_ok);
            else if (sh->ctrl_mode != 0) InterlockedIncrement(&k->haptic_fail);
        }
        if (h.lit_visible) { const size_t n = out.size(); add_rect(s, xs, h.lit_slot, h.lit_c, h.lit_w, h.lit_h, SOLID_LIGHT, room, out); lit = lit || out.size() > n; }
        if (now < s.flash_until[hand]) {
            const size_t n = out.size();
            add_rect(s, xs, h.press_slot, h.press_c, h.press_w, h.press_h, h.press_ok ? SOLID_OK : SOLID_LOST, room, out);
            flashed = flashed || out.size() > n;
        }
    }
    WindowViewKeysHand& left = k->hand[0];
    if (!lasers_on(sh) && left.cursor_visible && left.lit_slot >= 0) add_arrow(s, xs, left.lit_slot, XrVector3f{left.cursor_p[0], left.cursor_p[1], left.cursor_p[2]}, 0, room, out);
    if (lit) InterlockedIncrement(&k->lit_drawn);
    if (flashed) InterlockedIncrement(&k->flash_drawn);
}
