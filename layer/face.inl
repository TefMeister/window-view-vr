// face.inl -- where a window hangs and which way it faces, in the desk's frame: the small quaternion helpers and the one
// sum the layer draws every window with (build_quads), kept apart so test/turn_test.cpp compiles the very same code and
// checks it against the app's hit test (app/Pointer.cs WindowFrame.Of). Included inside the layer's unnamed namespace.
//
// 1.15 (Tefa 2026-11-24: "while LG or RG holds a window, the RIGHT stick tilts it (up/down = back/forward) and turns it
// (left/right)"): each window also has its own tilt and turn (the turns block, shared.h WindowViewTurns). They are turns
// of the window about its OWN middle, on top of the way it already faces the eyes: first the turn about its own upright
// line, then the tilt about its own level line. tilt > 0 = the top leans back (away), turn > 0 = the face turns to the
// right (the right edge goes back). Both 0 = exactly as before 1.15.

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

// one window: its centre `p` and its facing `face` in the desk's frame (the head's in follow mode, the anchor's in room
// mode). x, y on the curved desk (metres), depth as a share of `distance`, tilt / turn in radians.
void window_pose(float x, float y, float depth, float distance, bool room, float tilt, float turn, XrVector3f& p, XrQuaternionf& face) {
    // on the desk the window's centre is (x, y) at `distance`; pushed back by `depth` along the same line of sight, so it
    // keeps its place in the view and shrinks with the distance (its size in metres stays the same). 1.9 (Tefa 2026-10-30:
    // "windows move further and closer when i want to move them from side to side"): the desk is CURVED around the eyes,
    // x is the distance along the curve, so a window moved sideways stays as far away and turns to face the eyes
    const float yaw = x / distance;   // radians round the curve
    p = {std::sin(yaw) * distance * depth, y * depth, -std::cos(yaw) * distance * depth};
    if (!room) {
        face = q_axis(0, 1, 0, -yaw);
    } else {
        // facing the anchor, upright: turn +Z (the quad's front) towards it, first tipped about X, then about Y
        const float len = std::sqrt(p.x * p.x + p.y * p.y + p.z * p.z);
        const XrVector3f n{-p.x / len, -p.y / len, -p.z / len};
        face = q_mul(q_axis(0, 1, 0, std::atan2(n.x, n.z)), q_axis(1, 0, 0, -std::asin(n.y)));
    }
    if (tilt != 0 || turn != 0) face = q_mul(face, q_mul(q_axis(0, 1, 0, turn), q_axis(1, 0, 0, -tilt)));
}
