// controllers.inl -- 1.5: the motion controllers work the windows (included into window_view_layer.cpp: one translation unit).
//
// Tefa 2026-10-25: "can you make it so that i can actually use the motion controllers to do all the tweaking and slider
// tuning, instead of the mouse and keyboard? ... a motion controller combination that enables/disables control over the
// game/sliders and windows - that way i could actually walk around in my room and basically physically tune things".
//
// How: the layer adds an action set of its own to the game's. The game's bindings for the Touch and Index controllers get
// ours appended (xrSuggestInteractionProfileBindings), our set is attached beside the game's (xrAttachSessionActionSets)
// and synced with it (xrSyncActions); a profile the game never suggests is never touched, so its own bindings stay whole.
// Clicking the LEFT thumbstick in switches CONTROL mode on and off (shared `ctrl_mode`; Tefa: "the motion controller activation
// button could be left stick click, as there is no need to start sprinting while posing anything"). While on:
//   - the game's controllers are held still in the room (Hand Freeze's method, for every controller space) and its analog
//     triggers and grips keep what they were, so the in-game hands and fingers keep their pose while you walk round them;
//     its buttons read as released and its sticks as centred, so nothing sprints, walks or fires meanwhile;
//   - the right controller's aim ray is written into the shared block in the desk's frame (the head's in follow mode, the
//     anchor's in room mode) with the trigger, grip, stick and buttons; the app finds the window it points at and clicks.
// Our own calls go straight to the next layer down, so they never pass through these hooks.

PFN_xrStringToPath g_next_string_to_path = nullptr;
PFN_xrPathToString g_next_path_to_string = nullptr;
PFN_xrCreateActionSet g_next_create_action_set = nullptr;
PFN_xrCreateAction g_next_create_action = nullptr;
PFN_xrSuggestInteractionProfileBindings g_next_suggest = nullptr;
PFN_xrAttachSessionActionSets g_next_attach = nullptr;
PFN_xrSyncActions g_next_sync = nullptr;
PFN_xrGetActionStateBoolean g_next_state_bool = nullptr;
PFN_xrGetActionStateFloat g_next_state_float = nullptr;
PFN_xrGetActionStateVector2f g_next_state_vec2 = nullptr;
PFN_xrCreateActionSpace g_next_create_action_space = nullptr;
PFN_xrDestroySpace g_next_destroy_space = nullptr;
PFN_xrLocateSpaces g_next_locate_spaces = nullptr;
PFN_xrLocateSpacesKHR g_next_locate_spaces_khr = nullptr;

constexpr float kTriggerDown = 0.6f, kTriggerUp = 0.4f;   // the app uses the same hysteresis; the layer only reports values

XrActionSet g_set = XR_NULL_HANDLE;
bool g_actions_failed = false;
bool g_actions_off = false;   // the app's CONTROLLERS switch says leave them to the game: no set, no bindings of ours

// 1.6 (2026-10-27, Tefa in Village on Virtual Desktop: "with the window view on, my controller is not working"): the game
// saw no controllers while ours were bound to the same buttons, so the app can leave them to the game. Read once, at the
// game's first binding call, from the app's settings.txt ("controllers=0"); a change needs the game restarted.
bool controllers_allowed() {
    static int allowed = -1;
    if (allowed >= 0) return allowed != 0;
    allowed = 1;
    wchar_t* appdata = nullptr; size_t n = 0;
    if (_wdupenv_s(&appdata, &n, L"APPDATA") != 0 || appdata == nullptr) return true;
    std::wstring path = std::wstring(appdata) + L"\\WindowViewVR\\settings.txt";
    free(appdata);
    HANDLE f = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr);
    if (f == INVALID_HANDLE_VALUE) return true;
    char buf[4096]{}; DWORD got = 0;
    ReadFile(f, buf, sizeof(buf) - 1, &got, nullptr);
    CloseHandle(f);
    const char* at = strstr(buf, "controllers=");
    if (at != nullptr && (at == buf || at[-1] == '\n') && at[12] == '0') allowed = 0;
    return allowed != 0;
}
enum { A_LCLICK, A_RCLICK, A_TRIGGER, A_SQUEEZE, A_STICK, A_AIM, A_A, A_B, A_Y, A_LSQUEEZE, A_LSTICK, A_LAIM, A_X, A_COUNT };
XrAction g_act[A_COUNT]{};

// the game's controller spaces and inputs, held while CONTROL mode is on (both hands, everything the game asks for)
struct HeldSpace { XrSession session; bool kept = false; LONG gen = 0; XrPosef in_room{}; };
struct HeldInput { bool kept = false; LONG gen = 0; XrActionStateBoolean b{}; XrActionStateFloat f{}; XrActionStateVector2f v{}; };
std::map<XrSpace, HeldSpace> g_game_spaces;
std::map<std::pair<XrAction, XrPath>, HeldInput> g_game_inputs;
volatile LONG g_ctrl_gen = 0;   // +1 each time CONTROL mode comes on: fresh poses and states are kept
bool g_ctrl_was = false;

bool ctrl_on() { auto* sh = shared(); return sh != nullptr && sh->ctrl_mode != 0; }

XrPath to_path(const char* s) {
    XrPath p = XR_NULL_PATH;
    if (g_next_string_to_path != nullptr) g_next_string_to_path(g_instance, s, &p);
    return p;
}

bool make_actions() {
    if (g_set != XR_NULL_HANDLE) return true;
    if (!controllers_allowed()) { g_actions_off = true; return false; }
    if (g_actions_failed || g_instance == XR_NULL_HANDLE || g_next_create_action_set == nullptr || g_next_create_action == nullptr) return false;
    XrActionSetCreateInfo si{XR_TYPE_ACTION_SET_CREATE_INFO};
    strcpy_s(si.actionSetName, "flattovr_window_view");
    strcpy_s(si.localizedActionSetName, "Window View pointer");
    si.priority = 0;
    XrResult r = g_next_create_action_set(g_instance, &si, &g_set);
    if (XR_FAILED(r)) { note_error(r); g_actions_failed = true; g_set = XR_NULL_HANDLE; return false; }
    struct Def { const char* name; const char* text; XrActionType type; };
    static const Def defs[A_COUNT] = {
        {"wv_left_stick_click", "WV left stick click", XR_ACTION_TYPE_BOOLEAN_INPUT},
        {"wv_right_stick_click", "WV right stick click", XR_ACTION_TYPE_BOOLEAN_INPUT},
        {"wv_trigger", "WV trigger", XR_ACTION_TYPE_FLOAT_INPUT},
        {"wv_grip", "WV grip", XR_ACTION_TYPE_FLOAT_INPUT},
        {"wv_stick", "WV stick", XR_ACTION_TYPE_VECTOR2F_INPUT},
        {"wv_aim", "WV aim", XR_ACTION_TYPE_POSE_INPUT},
        {"wv_a", "WV A", XR_ACTION_TYPE_BOOLEAN_INPUT},
        {"wv_b", "WV B", XR_ACTION_TYPE_BOOLEAN_INPUT},
        {"wv_y", "WV Y", XR_ACTION_TYPE_BOOLEAN_INPUT},
        {"wv_left_grip", "WV left grip", XR_ACTION_TYPE_FLOAT_INPUT},
        {"wv_left_stick", "WV left stick", XR_ACTION_TYPE_VECTOR2F_INPUT},
        {"wv_left_aim", "WV left aim", XR_ACTION_TYPE_POSE_INPUT},
        {"wv_x", "WV X", XR_ACTION_TYPE_BOOLEAN_INPUT},
    };
    for (int i = 0; i < A_COUNT; ++i) {
        XrActionCreateInfo ai{XR_TYPE_ACTION_CREATE_INFO};
        strcpy_s(ai.actionName, defs[i].name);
        strcpy_s(ai.localizedActionName, defs[i].text);
        ai.actionType = defs[i].type;
        r = g_next_create_action(g_set, &ai, &g_act[i]);
        if (XR_FAILED(r)) { note_error(r); g_actions_failed = true; return false; }
    }
    return true;
}

// our bindings for a profile the game itself suggested; empty = not a profile we know (nothing is added)
std::vector<XrActionSuggestedBinding> our_bindings(XrPath profile) {
    std::vector<XrActionSuggestedBinding> out;
    const bool touch = profile == to_path("/interaction_profiles/oculus/touch_controller");
    const bool index = profile == to_path("/interaction_profiles/valve/index_controller");
    if (!touch && !index) return out;
    auto add = [&](int a, const char* p) { out.push_back({g_act[a], to_path(p)}); };
    add(A_LCLICK, "/user/hand/left/input/thumbstick/click");
    add(A_RCLICK, "/user/hand/right/input/thumbstick/click");
    add(A_TRIGGER, "/user/hand/right/input/trigger/value");
    add(A_SQUEEZE, "/user/hand/right/input/squeeze/value");
    add(A_STICK, "/user/hand/right/input/thumbstick");
    add(A_AIM, "/user/hand/right/input/aim/pose");
    add(A_A, "/user/hand/right/input/a/click");
    add(A_B, "/user/hand/right/input/b/click");
    add(A_Y, touch ? "/user/hand/left/input/y/click" : "/user/hand/left/input/b/click");
    add(A_LSQUEEZE, "/user/hand/left/input/squeeze/value");
    add(A_LSTICK, "/user/hand/left/input/thumbstick");
    add(A_LAIM, "/user/hand/left/input/aim/pose");
    add(A_X, touch ? "/user/hand/left/input/x/click" : "/user/hand/left/input/a/click");
    return out;
}

// ---- the hooks: our set alongside the game's ---------------------------------------------------------------------------
XRAPI_ATTR XrResult XRAPI_CALL wv_xrSuggestInteractionProfileBindings(XrInstance instance, const XrInteractionProfileSuggestedBinding* sb) {
    if (sb == nullptr || !make_actions()) return g_next_suggest(instance, sb);
    auto ours = our_bindings(sb->interactionProfile);
    if (ours.empty()) return g_next_suggest(instance, sb);
    std::vector<XrActionSuggestedBinding> all(sb->suggestedBindings, sb->suggestedBindings + sb->countSuggestedBindings);
    all.insert(all.end(), ours.begin(), ours.end());
    XrInteractionProfileSuggestedBinding copy = *sb;
    copy.countSuggestedBindings = static_cast<uint32_t>(all.size());
    copy.suggestedBindings = all.data();
    const XrResult r = g_next_suggest(instance, &copy);
    if (XR_SUCCEEDED(r)) return r;
    note_error(r);
    return g_next_suggest(instance, sb);   // never at the game's cost: its own bindings alone, as it asked
}

XRAPI_ATTR XrResult XRAPI_CALL wv_xrAttachSessionActionSets(XrSession session, const XrSessionActionSetsAttachInfo* info) {
    if (info == nullptr || g_set == XR_NULL_HANDLE) return g_next_attach(session, info);
    std::vector<XrActionSet> sets(info->actionSets, info->actionSets + info->countActionSets);
    sets.push_back(g_set);
    XrSessionActionSetsAttachInfo copy = *info;
    copy.countActionSets = static_cast<uint32_t>(sets.size());
    copy.actionSets = sets.data();
    XrResult r = g_next_attach(session, &copy);
    const bool ours = XR_SUCCEEDED(r);
    if (!ours) { note_error(r); r = g_next_attach(session, info); }
    std::lock_guard _{g_mtx};
    auto it = g_sessions.find(session);
    if (it != g_sessions.end()) it->second.ours_attached = ours;
    return r;
}

XRAPI_ATTR XrResult XRAPI_CALL wv_xrSyncActions(XrSession session, const XrActionsSyncInfo* info) {
    bool ours = false;
    {
        std::lock_guard _{g_mtx};
        auto it = g_sessions.find(session);
        ours = it != g_sessions.end() && it->second.ours_attached;
    }
    if (!ours || info == nullptr) return g_next_sync(session, info);
    std::vector<XrActiveActionSet> sets(info->activeActionSets, info->activeActionSets + info->countActiveActionSets);
    sets.push_back({g_set, XR_NULL_PATH});
    XrActionsSyncInfo copy = *info;
    copy.countActiveActionSets = static_cast<uint32_t>(sets.size());
    copy.activeActionSets = sets.data();
    const XrResult r = g_next_sync(session, &copy);
    if (XR_SUCCEEDED(r)) return r;
    note_error(r);
    return g_next_sync(session, info);
}

// ---- the hooks: the game's controllers held while CONTROL mode is on -----------------------------------------------------
XRAPI_ATTR XrResult XRAPI_CALL wv_xrCreateActionSpace(XrSession session, const XrActionSpaceCreateInfo* info, XrSpace* space) {
    const XrResult r = g_next_create_action_space(session, info, space);
    if (XR_SUCCEEDED(r) && space != nullptr) { std::lock_guard _{g_mtx}; g_game_spaces[*space] = HeldSpace{session}; }
    return r;
}
XRAPI_ATTR XrResult XRAPI_CALL wv_xrDestroySpace(XrSpace space) {
    { std::lock_guard _{g_mtx}; g_game_spaces.erase(space); }
    return g_next_destroy_space(space);
}

// in CONTROL mode the game's buttons are let go and its sticks centred; triggers and grips (floats) keep the finger pose
void neutral(XrActionStateBoolean& st) { st.currentState = XR_FALSE; }
void neutral(XrActionStateVector2f& st) { st.currentState = {0, 0}; }
void neutral(XrActionStateFloat&) {}

template <class T>
void held_input(const XrActionStateGetInfo* info, T& live, T HeldInput::*slot) {
    if (info == nullptr || !ctrl_on()) return;
    std::lock_guard _{g_mtx};
    auto& in = g_game_inputs[{info->action, info->subactionPath}];
    const LONG gen = g_ctrl_gen;
    if (in.kept && in.gen != gen) in.kept = false;
    if (!in.kept) {
        if (!live.isActive) return;
        in.*slot = live;
        in.kept = true;
        in.gen = gen;
        neutral(live);
        return;
    }
    T out = in.*slot;
    out.type = live.type;
    out.next = live.next;
    out.changedSinceLastSync = XR_FALSE;
    neutral(out);
    live = out;
}
XRAPI_ATTR XrResult XRAPI_CALL wv_xrGetActionStateBoolean(XrSession session, const XrActionStateGetInfo* info, XrActionStateBoolean* state) {
    const XrResult r = g_next_state_bool(session, info, state);
    if (XR_SUCCEEDED(r) && state != nullptr) held_input(info, *state, &HeldInput::b);
    return r;
}
XRAPI_ATTR XrResult XRAPI_CALL wv_xrGetActionStateFloat(XrSession session, const XrActionStateGetInfo* info, XrActionStateFloat* state) {
    const XrResult r = g_next_state_float(session, info, state);
    if (XR_SUCCEEDED(r) && state != nullptr) held_input(info, *state, &HeldInput::f);
    return r;
}
XRAPI_ATTR XrResult XRAPI_CALL wv_xrGetActionStateVector2f(XrSession session, const XrActionStateGetInfo* info, XrActionStateVector2f* state) {
    const XrResult r = g_next_state_vec2(session, info, state);
    if (XR_SUCCEEDED(r) && state != nullptr) held_input(info, *state, &HeldInput::v);
    return r;
}

constexpr XrSpaceLocationFlags kValid = XR_SPACE_LOCATION_POSITION_VALID_BIT | XR_SPACE_LOCATION_ORIENTATION_VALID_BIT;
XrPosef relative(const XrPosef& a, const XrPosef& b) {   // pose of B in A's frame, both given in the room
    const XrQuaternionf ia{-a.orientation.x, -a.orientation.y, -a.orientation.z, a.orientation.w};
    const XrVector3f d{b.position.x - a.position.x, b.position.y - a.position.y, b.position.z - a.position.z};
    return {q_mul(ia, b.orientation), q_rot(ia, d)};
}
// the held answer for "game controller `space` relative to `base`"; false = answer live
bool held_pose(XrSpace space, XrSpace base, XrTime time, XrPosef& out) {
    std::lock_guard _{g_mtx};
    auto c = g_game_spaces.find(space);
    if (c == g_game_spaces.end() || g_game_spaces.count(base) != 0) return false;
    auto si = g_sessions.find(c->second.session);
    if (si == g_sessions.end() || !ensure_space(c->second.session, XR_REFERENCE_SPACE_TYPE_LOCAL, si->second.local_space)) return false;
    const XrSpace room = si->second.local_space;
    const LONG gen = g_ctrl_gen;
    if (c->second.kept && c->second.gen != gen) c->second.kept = false;
    if (!c->second.kept) {
        XrSpaceLocation now{XR_TYPE_SPACE_LOCATION};
        if (XR_FAILED(g_next_locate_space(space, room, time, &now)) || (now.locationFlags & kValid) != kValid) return false;
        c->second.in_room = now.pose;
        c->second.kept = true;
        c->second.gen = gen;
    }
    XrSpaceLocation b{XR_TYPE_SPACE_LOCATION};
    if (XR_FAILED(g_next_locate_space(base, room, time, &b)) || (b.locationFlags & kValid) != kValid) return false;
    out = relative(b.pose, c->second.in_room);
    return true;
}
XRAPI_ATTR XrResult XRAPI_CALL wv_xrLocateSpace(XrSpace space, XrSpace base, XrTime time, XrSpaceLocation* location) {
    const XrResult r = g_next_locate_space(space, base, time, location);
    XrPosef held{};
    if (XR_FAILED(r) || location == nullptr || !ctrl_on() || !held_pose(space, base, time, held)) return r;
    location->pose = held;
    location->locationFlags |= kValid | XR_SPACE_LOCATION_POSITION_TRACKED_BIT | XR_SPACE_LOCATION_ORIENTATION_TRACKED_BIT;
    for (auto* n = static_cast<XrBaseOutStructure*>(location->next); n != nullptr; n = n->next) {
        if (n->type == XR_TYPE_SPACE_VELOCITY) { auto* v = reinterpret_cast<XrSpaceVelocity*>(n); v->linearVelocity = {0, 0, 0}; v->angularVelocity = {0, 0, 0}; }
    }
    return r;
}
XrResult locate_spaces(PFN_xrLocateSpaces next, XrSession session, const XrSpacesLocateInfo* info, XrSpaceLocations* out) {
    const XrResult r = next(session, info, out);
    if (XR_FAILED(r) || info == nullptr || out == nullptr || out->locations == nullptr || !ctrl_on()) return r;
    XrSpaceVelocities* vel = nullptr;
    for (auto* n = static_cast<XrBaseOutStructure*>(out->next); n != nullptr; n = n->next) if (n->type == XR_TYPE_SPACE_VELOCITIES) vel = reinterpret_cast<XrSpaceVelocities*>(n);
    const uint32_t count = info->spaceCount < out->locationCount ? info->spaceCount : out->locationCount;
    for (uint32_t i = 0; i < count; ++i) {
        XrPosef held{};
        if (!held_pose(info->spaces[i], info->baseSpace, info->time, held)) continue;
        out->locations[i].pose = held;
        out->locations[i].locationFlags |= kValid | XR_SPACE_LOCATION_POSITION_TRACKED_BIT | XR_SPACE_LOCATION_ORIENTATION_TRACKED_BIT;
        if (vel != nullptr && vel->velocities != nullptr && i < vel->velocityCount) { vel->velocities[i].linearVelocity = {0, 0, 0}; vel->velocities[i].angularVelocity = {0, 0, 0}; }
    }
    return r;
}
XRAPI_ATTR XrResult XRAPI_CALL wv_xrLocateSpaces(XrSession s, const XrSpacesLocateInfo* i, XrSpaceLocations* o) { return locate_spaces(g_next_locate_spaces, s, i, o); }
XRAPI_ATTR XrResult XRAPI_CALL wv_xrLocateSpacesKHR(XrSession s, const XrSpacesLocateInfo* i, XrSpaceLocations* o) { return locate_spaces(g_next_locate_spaces_khr, s, i, o); }

// ---- every frame: our controller state into the shared block -----------------------------------------------------------
bool read_bool(XrSession xs, int a) {
    XrActionStateGetInfo gi{XR_TYPE_ACTION_STATE_GET_INFO};
    gi.action = g_act[a];
    XrActionStateBoolean st{XR_TYPE_ACTION_STATE_BOOLEAN};
    return XR_SUCCEEDED(g_next_state_bool(xs, &gi, &st)) && st.isActive && st.currentState;
}
float read_float(XrSession xs, int a) {
    XrActionStateGetInfo gi{XR_TYPE_ACTION_STATE_GET_INFO};
    gi.action = g_act[a];
    XrActionStateFloat st{XR_TYPE_ACTION_STATE_FLOAT};
    return XR_SUCCEEDED(g_next_state_float(xs, &gi, &st)) && st.isActive ? st.currentState : 0.0f;
}
XrVector2f read_vec2(XrSession xs, int a) {
    XrActionStateGetInfo gi{XR_TYPE_ACTION_STATE_GET_INFO};
    gi.action = g_act[a];
    XrActionStateVector2f st{XR_TYPE_ACTION_STATE_VECTOR2F};
    return XR_SUCCEEDED(g_next_state_vec2(xs, &gi, &st)) && st.isActive ? st.currentState : XrVector2f{0, 0};
}

// a controller's aim ray in the desk's frame (the head's in follow mode, the anchor's in room mode); its action space is made
// straight through the next layer, so it is never one of the game's held spaces
bool aim_ray(Session& s, XrSession xs, XrAction aim, XrSpace& space, XrTime when, bool room, XrVector3f& o, XrVector3f& d) {
    if (space == XR_NULL_HANDLE) {
        XrActionSpaceCreateInfo ci{XR_TYPE_ACTION_SPACE_CREATE_INFO};
        ci.action = aim;
        ci.poseInActionSpace.orientation.w = 1.0f;
        const XrResult r = g_next_create_action_space(xs, &ci, &space);
        if (XR_FAILED(r)) { note_error(r); space = XR_NULL_HANDLE; return false; }
    }
    XrSpaceLocation loc{XR_TYPE_SPACE_LOCATION};
    const XrSpace base = room ? s.local_space : s.view_space;
    if (XR_FAILED(g_next_locate_space(space, base, when, &loc)) || (loc.locationFlags & kValid) != kValid) return false;
    o = loc.pose.position;
    d = q_rot(loc.pose.orientation, {0, 0, -1});
    if (room) {   // into the anchor's frame, where the desk is defined
        const XrQuaternionf ia{-s.anchor_rot.x, -s.anchor_rot.y, -s.anchor_rot.z, s.anchor_rot.w};
        o = q_rot(ia, {o.x - s.anchor_pos.x, o.y - s.anchor_pos.y, o.z - s.anchor_pos.z});
        d = q_rot(ia, d);
    }
    return true;
}

// `room`: the windows are in the room around the anchor this frame (else they follow the head)
void controller_frame(Session& s, XrSession xs, WindowViewShared* sh, XrTime when, bool room) {
    sh->pointer_ready = s.ours_attached ? 1 : g_actions_failed ? -1 : g_actions_off ? -2 : 0;
    const bool now_on = sh->ctrl_mode != 0;
    if (now_on && !g_ctrl_was) InterlockedIncrement(&g_ctrl_gen);   // a fresh hold of the game's hands and buttons
    g_ctrl_was = now_on;
    if (!s.ours_attached) { sh->ray_valid = 0; return; }
    // the left stick clicked in: CONTROL mode on / off (once per press)
    const bool click = read_bool(xs, A_LCLICK);
    if (click && !s.both_was) sh->ctrl_mode = sh->ctrl_mode ? 0 : 1;
    s.both_was = click;
    if (sh->ctrl_mode == 0) { sh->ray_valid = 0; sh->lray_valid = 0; return; }
    XrVector3f o{}, d{};
    if (!aim_ray(s, xs, g_act[A_AIM], s.aim_space, when, room, o, d)) { sh->ray_valid = 0; return; }
    sh->ray_o[0] = o.x; sh->ray_o[1] = o.y; sh->ray_o[2] = o.z;
    sh->ray_d[0] = d.x; sh->ray_d[1] = d.y; sh->ray_d[2] = d.z;
    if (aim_ray(s, xs, g_act[A_LAIM], s.left_aim_space, when, room, o, d)) {   // the left hand: it carries windows too
        sh->lray_o[0] = o.x; sh->lray_o[1] = o.y; sh->lray_o[2] = o.z;
        sh->lray_d[0] = d.x; sh->lray_d[1] = d.y; sh->lray_d[2] = d.z;
        sh->lsqueeze = read_float(xs, A_LSQUEEZE);
        sh->lstick_y = read_vec2(xs, A_LSTICK).y;
        sh->lray_valid = 1;
    } else sh->lray_valid = 0;
    sh->trigger = read_float(xs, A_TRIGGER);
    sh->squeeze = read_float(xs, A_SQUEEZE);
    sh->stick_y = read_vec2(xs, A_STICK).y;
    sh->buttons = (read_bool(xs, A_A) ? 1 : 0) | (read_bool(xs, A_Y) ? 2 : 0) | (read_bool(xs, A_B) ? 4 : 0) | (read_bool(xs, A_X) ? 8 : 0);
    sh->ray_valid = 1;
    InterlockedIncrement(&sh->ray_seq);
}
