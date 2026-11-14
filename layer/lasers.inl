// lasers.inl -- 1.13: the laser pointers (Tefa 2026-11-14: "add left and right mouse to all and make the pointers dots with a
// laser line behind them. red for right, blue for left"). While the controllers work the windows (CONTROL mode) and the app
// asks for it (keys block `lasers`), each hand gets a thin beam from its controller along its aim, ending in a round dot where
// it meets a window; a ray that meets no window shows a short beam and no dot. Drawn here every headset frame like the
// keyboard's lights (keys.inl), so they move with the controllers at the headset's rate; the arrows are not drawn meanwhile.
// Part of window_view_layer.cpp (included after keys.inl).

constexpr int kDotPx = 32;
constexpr float kDotSize = 0.012f;     // the dot's diameter, metres per metre of distance (the arrow's is 0.03)
constexpr int kBeamPxW = 8, kBeamPxH = 4;
constexpr float kBeamWidth = 0.004f;   // metres
constexpr float kBeamMiss = 0.8f;      // a ray that meets no window: a beam this long
enum { LASER_RED, LASER_BLUE };
constexpr uint8_t kLaser[2][3] = {{50, 50, 240}, {245, 130, 40}};   // BGR: red (right hand), blue (left hand)

void put_pixel(Session& s, int w, int x, int y, const uint8_t bgr[3], float alpha, bool swap_rb) {
    uint8_t* p = &s.staging[(static_cast<size_t>(y) * w + x) * 4];
    p[0] = bgr[0]; p[1] = bgr[1]; p[2] = bgr[2];
    p[3] = static_cast<uint8_t>((std::max)(0.0f, (std::min)(1.0f, alpha)) * 255.0f);
    if (swap_rb) std::swap(p[0], p[2]);
}

// a round dot: the colour inside, a thin dark rim, soft at the very edge
bool ensure_dot(Session& s, XrSession xs, int kind) {
    Quad& q = s.dot[kind];
    if (!ensure_swapchain(s, xs, q, kDotPx, kDotPx)) return false;
    if (q.shown_frame >= 0) return true;
    s.staging.assign(kDotPx * kDotPx * 4, 0);
    const float c = kDotPx * 0.5f, r = c - 1.0f;
    const uint8_t rim[3] = {20, 20, 20};
    for (int y = 0; y < kDotPx; ++y)
        for (int x = 0; x < kDotPx; ++x) {
            const float d = std::sqrt((x + 0.5f - c) * (x + 0.5f - c) + (y + 0.5f - c) * (y + 0.5f - c));
            if (d > r + 0.5f) continue;
            const bool inner = d < r - 3.0f;
            put_pixel(s, kDotPx, x, y, inner ? kLaser[kind] : rim, d > r - 0.5f ? (r + 0.5f - d) : 1.0f, q.swap_rb);
        }
    if (!upload(s, q, s.staging.data())) return false;
    q.shown_frame = 0;
    return true;
}

// the beam: the colour across its width, bright in the middle, fading to the sides
bool ensure_beam(Session& s, XrSession xs, int kind) {
    Quad& q = s.beam[kind];
    if (!ensure_swapchain(s, xs, q, kBeamPxW, kBeamPxH)) return false;
    if (q.shown_frame >= 0) return true;
    s.staging.assign(kBeamPxW * kBeamPxH * 4, 0);
    static const float across[kBeamPxW] = {0.10f, 0.35f, 0.75f, 0.95f, 0.95f, 0.75f, 0.35f, 0.10f};
    for (int y = 0; y < kBeamPxH; ++y)
        for (int x = 0; x < kBeamPxW; ++x) put_pixel(s, kBeamPxW, x, y, kLaser[kind], across[x] * 0.85f, q.swap_rb);
    if (!upload(s, q, s.staging.data())) return false;
    q.shown_frame = 0;
    return true;
}

XrQuaternionf q_from_axes(const XrVector3f& x, const XrVector3f& y, const XrVector3f& z) {   // columns of a rotation
    XrQuaternionf q;
    const float tr = x.x + y.y + z.z;
    if (tr > 0) { const float t = std::sqrt(tr + 1.0f) * 2.0f; q.w = 0.25f * t; q.x = (y.z - z.y) / t; q.y = (z.x - x.z) / t; q.z = (x.y - y.x) / t; }
    else if (x.x > y.y && x.x > z.z) { const float t = std::sqrt(1.0f + x.x - y.y - z.z) * 2.0f; q.w = (y.z - z.y) / t; q.x = 0.25f * t; q.y = (y.x + x.y) / t; q.z = (z.x + x.z) / t; }
    else if (y.y > z.z) { const float t = std::sqrt(1.0f + y.y - x.x - z.z) * 2.0f; q.w = (z.x - x.z) / t; q.x = (y.x + x.y) / t; q.y = 0.25f * t; q.z = (z.y + y.z) / t; }
    else { const float t = std::sqrt(1.0f + z.z - x.x - y.y) * 2.0f; q.w = (x.y - y.x) / t; q.x = (z.x + x.z) / t; q.y = (z.y + y.z) / t; q.z = 0.25f * t; }
    return q;
}

void push_laser_quad(Session& s, XrSwapchain sc, int pw, int ph, XrVector3f p, XrQuaternionf o, float w, float h, bool room, std::vector<XrCompositionLayerQuad>& out) {
    if (room) { const XrVector3f wv = q_rot(s.anchor_rot, p); p = {s.anchor_pos.x + wv.x, s.anchor_pos.y + wv.y, s.anchor_pos.z + wv.z}; o = q_mul(s.anchor_rot, o); }
    XrCompositionLayerQuad l{XR_TYPE_COMPOSITION_LAYER_QUAD};
    l.layerFlags = XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT | XR_COMPOSITION_LAYER_UNPREMULTIPLIED_ALPHA_BIT;
    l.space = room ? s.local_space : s.view_space;
    l.eyeVisibility = XR_EYE_VISIBILITY_BOTH;
    l.subImage.swapchain = sc;
    l.subImage.imageRect = {{0, 0}, {pw, ph}};
    l.pose = {o, p};
    l.size = {w, h};
    out.push_back(l);
}

// the dot lying on window `slot`, centred on `tip` (the desk's frame)
void add_dot(Session& s, XrSession xs, LONG slot, XrVector3f tip, int kind, bool room, std::vector<XrCompositionLayerQuad>& out) {
    if (slot < 0 || slot >= WINDOW_VIEW_SLOTS || !ensure_dot(s, xs, kind)) return;
    const float dist = std::sqrt(tip.x * tip.x + tip.y * tip.y + tip.z * tip.z);
    const float size = kDotSize * (dist > 0.05f ? dist : 1.0f);
    const XrQuaternionf face = s.slot_face[slot];
    const XrVector3f lift = q_rot(face, {0, 0, 0.002f});   // a hair in front of the window
    push_laser_quad(s, s.dot[kind].swapchain, kDotPx, kDotPx, {tip.x + lift.x, tip.y + lift.y, tip.z + lift.z}, face, size, size, room, out);
}

// the beam from `a` (the controller) to `b`, turned about its own line to face the eyes (the desk's origin)
void add_beam(Session& s, XrSession xs, XrVector3f a, XrVector3f b, int kind, bool room, std::vector<XrCompositionLayerQuad>& out) {
    const XrVector3f dir{b.x - a.x, b.y - a.y, b.z - a.z};
    const float len = std::sqrt(dir.x * dir.x + dir.y * dir.y + dir.z * dir.z);
    if (len < 0.01f || !ensure_beam(s, xs, kind)) return;
    const XrVector3f y{dir.x / len, dir.y / len, dir.z / len};
    const XrVector3f mid{(a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f, (a.z + b.z) * 0.5f};
    XrVector3f v{-mid.x, -mid.y, -mid.z};   // toward the eyes
    XrVector3f x{y.y * v.z - y.z * v.y, y.z * v.x - y.x * v.z, y.x * v.y - y.y * v.x};
    const float xl = std::sqrt(x.x * x.x + x.y * x.y + x.z * x.z);
    if (xl < 1e-5f) return;   // looking straight down the beam: nothing to draw
    x = {x.x / xl, x.y / xl, x.z / xl};
    const XrVector3f z{x.y * y.z - x.z * y.y, x.z * y.x - x.x * y.z, x.x * y.y - x.y * y.x};
    push_laser_quad(s, s.beam[kind].swapchain, kBeamPxW, kBeamPxH, mid, q_from_axes(x, y, z), kBeamWidth, len, room, out);
}

// true = the lasers were drawn this frame (the arrows stay away then)
bool lasers_on(WindowViewShared* sh) {
    WindowViewKeys* k = keys_block();
    return k != nullptr && k->lasers != 0 && sh->ctrl_mode != 0;
}

void lasers_frame(Session& s, XrSession xs, WindowViewShared* sh, bool room, std::vector<XrCompositionLayerQuad>& out) {
    if (!lasers_on(sh)) return;
    WindowViewKeys* k = keys_block();
    if (sh->ray_valid) {
        const XrVector3f o{sh->ray_o[0], sh->ray_o[1], sh->ray_o[2]}, d{sh->ray_d[0], sh->ray_d[1], sh->ray_d[2]};
        const LONG slot = sh->cursor_slot;
        const bool hit = k->rhit != 0 && sh->cursor_visible && !sh->cursor_edge && slot >= 0 && slot < WINDOW_VIEW_SLOTS;
        const XrVector3f end = hit ? XrVector3f{sh->cursor_p[0], sh->cursor_p[1], sh->cursor_p[2]}
                                   : XrVector3f{o.x + d.x * kBeamMiss, o.y + d.y * kBeamMiss, o.z + d.z * kBeamMiss};
        add_beam(s, xs, o, end, LASER_RED, room, out);
        if (hit) add_dot(s, xs, slot, end, LASER_RED, room, out);
    }
    if (sh->lray_valid) {
        const XrVector3f o{sh->lray_o[0], sh->lray_o[1], sh->lray_o[2]}, d{sh->lray_d[0], sh->lray_d[1], sh->lray_d[2]};
        const WindowViewKeysHand& h = k->hand[0];
        const LONG slot = k->lcursor_slot;
        const bool hit = h.cursor_visible != 0 && slot >= 0 && slot < WINDOW_VIEW_SLOTS;
        const XrVector3f end = hit ? XrVector3f{h.cursor_p[0], h.cursor_p[1], h.cursor_p[2]}
                                   : XrVector3f{o.x + d.x * kBeamMiss, o.y + d.y * kBeamMiss, o.z + d.z * kBeamMiss};
        add_beam(s, xs, o, end, LASER_BLUE, room, out);
        if (hit) add_dot(s, xs, slot, end, LASER_BLUE, room, out);
    }
}
