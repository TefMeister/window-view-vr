// xr_test_app.cpp -- a tiny OpenXR program standing in for a game: a D3D11 or D3D12 session that clears both eyes grey
// for a few seconds, so the Window View layer can be tested on the OpenXR Simulator with no headset and no game.
//
//   xr_test_app.exe <openxr_loader.dll> d3d11|d3d12 [seconds=5]
// Prints "API: ..." and "FRAMES: n" (frames ended). Exit 0 when at least one frame was ended.
// 1.5: a 5th argument "actions" gives it controllers like a real game (an action set with a trigger, a grip and a hand pose,
// Touch bindings, synced every frame, the hand located every frame) and prints "ACTIONS: ok syncs=N states=N poses=N" or
// "ACTIONS: FAIL <step> <code>", so a layer that adds its own controller actions can be checked to leave the game's whole.
#define XR_NO_PROTOTYPES
#define XR_USE_PLATFORM_WIN32
#define XR_USE_GRAPHICS_API_D3D11
#define XR_USE_GRAPHICS_API_D3D12
#include <windows.h>
#include <d3d11.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <vector>
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "d3d12.lib")
#pragma comment(lib, "dxgi.lib")

static PFN_xrGetInstanceProcAddr gipa = nullptr;
template <class T> static T fn(XrInstance inst, const char* name) { PFN_xrVoidFunction f = nullptr; gipa(inst, name, &f); return reinterpret_cast<T>(f); }
#define XR(x) do { XrResult r_ = (x); if (XR_FAILED(r_)) { printf("FAIL %s -> %d\n", #x, (int)r_); return 1; } } while (0)
#define HR(x) do { HRESULT h_ = (x); if (FAILED(h_)) { printf("FAIL %s -> 0x%08lX\n", #x, (unsigned long)h_); return 1; } } while (0)

static IDXGIAdapter1* adapter_by_luid(LUID luid) {
    IDXGIFactory1* f = nullptr;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&f)))) return nullptr;
    IDXGIAdapter1* a = nullptr;
    for (UINT i = 0; f->EnumAdapters1(i, &a) != DXGI_ERROR_NOT_FOUND; ++i) {
        DXGI_ADAPTER_DESC1 d{};
        a->GetDesc1(&d);
        if (d.AdapterLuid.LowPart == luid.LowPart && d.AdapterLuid.HighPart == luid.HighPart) break;
        a->Release();
        a = nullptr;
    }
    f->Release();
    return a;
}

struct Eye { XrSwapchain sc = XR_NULL_HANDLE; uint32_t w = 0, h = 0; std::vector<ID3D11RenderTargetView*> rtv11; UINT rtv_base = 0; };

int main(int argc, char** argv) {
    if (argc < 3) { puts("usage: xr_test_app <openxr_loader.dll> d3d11|d3d12 [seconds]"); return 2; }
    const bool d12 = _stricmp(argv[2], "d3d12") == 0;
    const double seconds = argc > 3 ? atof(argv[3]) : 5.0;
    HMODULE m = LoadLibraryA(argv[1]);
    if (m == nullptr) { printf("FAIL LoadLibrary %s\n", argv[1]); return 1; }
    gipa = reinterpret_cast<PFN_xrGetInstanceProcAddr>(GetProcAddress(m, "xrGetInstanceProcAddr"));
    if (gipa == nullptr) { puts("FAIL no xrGetInstanceProcAddr"); return 1; }

    const char* ext = d12 ? "XR_KHR_D3D12_enable" : "XR_KHR_D3D11_enable";
    XrInstanceCreateInfo ici{XR_TYPE_INSTANCE_CREATE_INFO};
    strcpy_s(ici.applicationInfo.applicationName, "window-view-test");
    ici.applicationInfo.applicationVersion = 1;
    strcpy_s(ici.applicationInfo.engineName, "none");
    ici.applicationInfo.apiVersion = XR_API_VERSION_1_0;
    ici.enabledExtensionCount = 1;
    ici.enabledExtensionNames = &ext;
    XrInstance inst = XR_NULL_HANDLE;
    XR(fn<PFN_xrCreateInstance>(XR_NULL_HANDLE, "xrCreateInstance")(&ici, &inst));
    XrSystemGetInfo sgi{XR_TYPE_SYSTEM_GET_INFO};
    sgi.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
    XrSystemId sys = XR_NULL_SYSTEM_ID;
    XR(fn<PFN_xrGetSystem>(inst, "xrGetSystem")(inst, &sgi, &sys));

    ID3D11Device* dev11 = nullptr; ID3D11DeviceContext* ctx11 = nullptr;
    ID3D12Device* dev12 = nullptr; ID3D12CommandQueue* queue = nullptr; ID3D12CommandAllocator* alloc = nullptr;
    ID3D12GraphicsCommandList* list = nullptr; ID3D12Fence* fence = nullptr; HANDLE fence_event = nullptr; UINT64 fence_value = 0;
    ID3D12DescriptorHeap* rtv_heap = nullptr; UINT rtv_inc = 0;
    XrGraphicsBindingD3D11KHR b11{XR_TYPE_GRAPHICS_BINDING_D3D11_KHR};
    XrGraphicsBindingD3D12KHR b12{XR_TYPE_GRAPHICS_BINDING_D3D12_KHR};
    if (!d12) {
        XrGraphicsRequirementsD3D11KHR req{XR_TYPE_GRAPHICS_REQUIREMENTS_D3D11_KHR};
        XR(fn<PFN_xrGetD3D11GraphicsRequirementsKHR>(inst, "xrGetD3D11GraphicsRequirementsKHR")(inst, sys, &req));
        IDXGIAdapter1* a = adapter_by_luid(req.adapterLuid);
        D3D_FEATURE_LEVEL fl = req.minFeatureLevel;
        HR(D3D11CreateDevice(a, a ? D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, &fl, 1, D3D11_SDK_VERSION, &dev11, nullptr, &ctx11));
        b11.device = dev11;
    } else {
        XrGraphicsRequirementsD3D12KHR req{XR_TYPE_GRAPHICS_REQUIREMENTS_D3D12_KHR};
        XR(fn<PFN_xrGetD3D12GraphicsRequirementsKHR>(inst, "xrGetD3D12GraphicsRequirementsKHR")(inst, sys, &req));
        IDXGIAdapter1* a = adapter_by_luid(req.adapterLuid);
        HR(D3D12CreateDevice(a, req.minFeatureLevel, IID_PPV_ARGS(&dev12)));
        D3D12_COMMAND_QUEUE_DESC qd{};
        qd.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        HR(dev12->CreateCommandQueue(&qd, IID_PPV_ARGS(&queue)));
        HR(dev12->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&alloc)));
        HR(dev12->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, alloc, nullptr, IID_PPV_ARGS(&list)));
        list->Close();
        HR(dev12->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence)));
        fence_event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        D3D12_DESCRIPTOR_HEAP_DESC hd{};
        hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
        hd.NumDescriptors = 64;
        HR(dev12->CreateDescriptorHeap(&hd, IID_PPV_ARGS(&rtv_heap)));
        rtv_inc = dev12->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
        b12.device = dev12;
        b12.queue = queue;
    }

    XrSessionCreateInfo sci{XR_TYPE_SESSION_CREATE_INFO};
    sci.next = d12 ? static_cast<const void*>(&b12) : static_cast<const void*>(&b11);
    sci.systemId = sys;
    XrSession session = XR_NULL_HANDLE;
    XR(fn<PFN_xrCreateSession>(inst, "xrCreateSession")(inst, &sci, &session));
    XrReferenceSpaceCreateInfo rci{XR_TYPE_REFERENCE_SPACE_CREATE_INFO};
    rci.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_LOCAL;
    rci.poseInReferenceSpace.orientation.w = 1.0f;
    XrSpace space = XR_NULL_HANDLE;
    XR(fn<PFN_xrCreateReferenceSpace>(inst, "xrCreateReferenceSpace")(session, &rci, &space));

    // 1.5: controllers, when asked for
    const bool actions = argc > 4 && _stricmp(argv[4], "actions") == 0;
    XrActionSet aset = XR_NULL_HANDLE;
    XrAction a_trigger = XR_NULL_HANDLE, a_grip = XR_NULL_HANDLE, a_hand = XR_NULL_HANDLE;
    XrSpace hand_space = XR_NULL_HANDLE;
    const char* act_fail = nullptr;
    XrResult act_code = XR_SUCCESS;
    int syncs = 0, states = 0, poses = 0;
    if (actions) {
        auto to_path = [&](const char* s) { XrPath p = XR_NULL_PATH; fn<PFN_xrStringToPath>(inst, "xrStringToPath")(inst, s, &p); return p; };
        XrActionSetCreateInfo si{XR_TYPE_ACTION_SET_CREATE_INFO};
        strcpy_s(si.actionSetName, "game");
        strcpy_s(si.localizedActionSetName, "Game");
        act_code = fn<PFN_xrCreateActionSet>(inst, "xrCreateActionSet")(inst, &si, &aset);
        if (XR_FAILED(act_code)) act_fail = "create set";
        auto make = [&](const char* name, XrActionType type, XrAction& out) {
            if (act_fail) return;
            XrActionCreateInfo ai{XR_TYPE_ACTION_CREATE_INFO};
            strcpy_s(ai.actionName, name);
            strcpy_s(ai.localizedActionName, name);
            ai.actionType = type;
            act_code = fn<PFN_xrCreateAction>(inst, "xrCreateAction")(aset, &ai, &out);
            if (XR_FAILED(act_code)) act_fail = "create action";
        };
        make("fire", XR_ACTION_TYPE_FLOAT_INPUT, a_trigger);
        make("grab", XR_ACTION_TYPE_FLOAT_INPUT, a_grip);
        make("hand", XR_ACTION_TYPE_POSE_INPUT, a_hand);
        if (!act_fail) {
            XrActionSuggestedBinding b[] = {{a_trigger, to_path("/user/hand/right/input/trigger/value")},
                                            {a_grip, to_path("/user/hand/left/input/squeeze/value")},
                                            {a_hand, to_path("/user/hand/right/input/grip/pose")}};
            XrInteractionProfileSuggestedBinding sb{XR_TYPE_INTERACTION_PROFILE_SUGGESTED_BINDING};
            sb.interactionProfile = to_path("/interaction_profiles/oculus/touch_controller");
            sb.countSuggestedBindings = 3;
            sb.suggestedBindings = b;
            act_code = fn<PFN_xrSuggestInteractionProfileBindings>(inst, "xrSuggestInteractionProfileBindings")(inst, &sb);
            if (XR_FAILED(act_code)) act_fail = "suggest";
        }
        if (!act_fail) {
            XrSessionActionSetsAttachInfo at{XR_TYPE_SESSION_ACTION_SETS_ATTACH_INFO};
            at.countActionSets = 1;
            at.actionSets = &aset;
            act_code = fn<PFN_xrAttachSessionActionSets>(inst, "xrAttachSessionActionSets")(session, &at);
            if (XR_FAILED(act_code)) act_fail = "attach";
        }
        if (!act_fail) {
            XrActionSpaceCreateInfo sc{XR_TYPE_ACTION_SPACE_CREATE_INFO};
            sc.action = a_hand;
            sc.poseInActionSpace.orientation.w = 1.0f;
            act_code = fn<PFN_xrCreateActionSpace>(inst, "xrCreateActionSpace")(session, &sc, &hand_space);
            if (XR_FAILED(act_code)) act_fail = "action space";
        }
    }
    auto sync_actions = fn<PFN_xrSyncActions>(inst, "xrSyncActions");
    auto state_float = fn<PFN_xrGetActionStateFloat>(inst, "xrGetActionStateFloat");
    auto locate_space = fn<PFN_xrLocateSpace>(inst, "xrLocateSpace");

    const XrViewConfigurationType stereo = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
    auto enum_views = fn<PFN_xrEnumerateViewConfigurationViews>(inst, "xrEnumerateViewConfigurationViews");
    uint32_t nviews = 0;
    XR(enum_views(inst, sys, stereo, 0, &nviews, nullptr));
    const XrViewConfigurationView vcv0{XR_TYPE_VIEW_CONFIGURATION_VIEW};
    std::vector<XrViewConfigurationView> vcv(nviews, vcv0);
    XR(enum_views(inst, sys, stereo, nviews, &nviews, vcv.data()));

    auto enum_formats = fn<PFN_xrEnumerateSwapchainFormats>(inst, "xrEnumerateSwapchainFormats");
    uint32_t nf = 0;
    XR(enum_formats(session, 0, &nf, nullptr));
    std::vector<int64_t> fmts(nf);
    XR(enum_formats(session, nf, &nf, fmts.data()));
    int64_t format = fmts.empty() ? DXGI_FORMAT_R8G8B8A8_UNORM : fmts[0];
    for (int64_t want : {DXGI_FORMAT_R8G8B8A8_UNORM_SRGB, DXGI_FORMAT_B8G8R8A8_UNORM_SRGB, DXGI_FORMAT_R8G8B8A8_UNORM, DXGI_FORMAT_B8G8R8A8_UNORM}) {
        bool has = false;
        for (int64_t f : fmts) has = has || f == want;
        if (has) { format = want; break; }
    }

    auto enum_images = fn<PFN_xrEnumerateSwapchainImages>(inst, "xrEnumerateSwapchainImages");
    std::vector<Eye> eyes(nviews);
    UINT rtv_used = 0;
    for (uint32_t i = 0; i < nviews; ++i) {
        XrSwapchainCreateInfo ci{XR_TYPE_SWAPCHAIN_CREATE_INFO};
        ci.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT | XR_SWAPCHAIN_USAGE_SAMPLED_BIT;
        ci.format = format;
        ci.sampleCount = 1;
        ci.width = vcv[i].recommendedImageRectWidth;
        ci.height = vcv[i].recommendedImageRectHeight;
        ci.faceCount = 1;
        ci.arraySize = 1;
        ci.mipCount = 1;
        XR(fn<PFN_xrCreateSwapchain>(inst, "xrCreateSwapchain")(session, &ci, &eyes[i].sc));
        eyes[i].w = ci.width;
        eyes[i].h = ci.height;
        uint32_t ni = 0;
        XR(enum_images(eyes[i].sc, 0, &ni, nullptr));
        if (!d12) {
            const XrSwapchainImageD3D11KHR im0{XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR};
            std::vector<XrSwapchainImageD3D11KHR> imgs(ni, im0);
            XR(enum_images(eyes[i].sc, ni, &ni, reinterpret_cast<XrSwapchainImageBaseHeader*>(imgs.data())));
            for (auto& im : imgs) {
                D3D11_RENDER_TARGET_VIEW_DESC vd{};
                vd.Format = static_cast<DXGI_FORMAT>(format);
                vd.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
                ID3D11RenderTargetView* v = nullptr;
                HR(dev11->CreateRenderTargetView(im.texture, &vd, &v));
                eyes[i].rtv11.push_back(v);
            }
        } else {
            const XrSwapchainImageD3D12KHR im0{XR_TYPE_SWAPCHAIN_IMAGE_D3D12_KHR};
            std::vector<XrSwapchainImageD3D12KHR> imgs(ni, im0);
            XR(enum_images(eyes[i].sc, ni, &ni, reinterpret_cast<XrSwapchainImageBaseHeader*>(imgs.data())));
            eyes[i].rtv_base = rtv_used;
            for (auto& im : imgs) {
                D3D12_RENDER_TARGET_VIEW_DESC vd{};
                vd.Format = static_cast<DXGI_FORMAT>(format);
                vd.ViewDimension = D3D12_RTV_DIMENSION_TEXTURE2D;
                D3D12_CPU_DESCRIPTOR_HANDLE h = rtv_heap->GetCPUDescriptorHandleForHeapStart();
                h.ptr += static_cast<SIZE_T>(rtv_used) * rtv_inc;
                dev12->CreateRenderTargetView(im.texture, &vd, h);
                ++rtv_used;
            }
        }
    }

    auto poll = fn<PFN_xrPollEvent>(inst, "xrPollEvent");
    auto begin_session = fn<PFN_xrBeginSession>(inst, "xrBeginSession");
    auto end_session = fn<PFN_xrEndSession>(inst, "xrEndSession");
    auto wait_frame = fn<PFN_xrWaitFrame>(inst, "xrWaitFrame");
    auto begin_frame = fn<PFN_xrBeginFrame>(inst, "xrBeginFrame");
    auto end_frame = fn<PFN_xrEndFrame>(inst, "xrEndFrame");
    auto locate_views = fn<PFN_xrLocateViews>(inst, "xrLocateViews");
    auto acquire = fn<PFN_xrAcquireSwapchainImage>(inst, "xrAcquireSwapchainImage");
    auto wait_img = fn<PFN_xrWaitSwapchainImage>(inst, "xrWaitSwapchainImage");
    auto release = fn<PFN_xrReleaseSwapchainImage>(inst, "xrReleaseSwapchainImage");

    bool running = false, quit = false;
    int frames = 0;
    const ULONGLONG t0 = GetTickCount64();
    const float grey[4] = {0.3f, 0.3f, 0.3f, 1.0f};
    const XrView view0{XR_TYPE_VIEW};
    const XrCompositionLayerProjectionView pv0{XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW};
    while (!quit && GetTickCount64() - t0 < static_cast<ULONGLONG>(seconds * 1000)) {
        XrEventDataBuffer ev{XR_TYPE_EVENT_DATA_BUFFER};
        while (poll(inst, &ev) == XR_SUCCESS) {
            if (ev.type == XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED) {
                const XrSessionState st = reinterpret_cast<XrEventDataSessionStateChanged*>(&ev)->state;
                if (st == XR_SESSION_STATE_READY) {
                    XrSessionBeginInfo bi{XR_TYPE_SESSION_BEGIN_INFO};
                    bi.primaryViewConfigurationType = stereo;
                    XR(begin_session(session, &bi));
                    running = true;
                } else if (st == XR_SESSION_STATE_STOPPING) {
                    end_session(session);
                    running = false;
                } else if (st == XR_SESSION_STATE_EXITING || st == XR_SESSION_STATE_LOSS_PENDING) {
                    quit = true;
                }
            }
            ev = XrEventDataBuffer{XR_TYPE_EVENT_DATA_BUFFER};
        }
        if (!running) { Sleep(10); continue; }
        XrFrameState fs{XR_TYPE_FRAME_STATE};
        XrFrameWaitInfo fwi{XR_TYPE_FRAME_WAIT_INFO};
        XR(wait_frame(session, &fwi, &fs));
        XrFrameBeginInfo fbi{XR_TYPE_FRAME_BEGIN_INFO};
        XR(begin_frame(session, &fbi));
        std::vector<XrCompositionLayerProjectionView> pv(nviews, pv0);
        XrCompositionLayerProjection proj{XR_TYPE_COMPOSITION_LAYER_PROJECTION};
        const XrCompositionLayerBaseHeader* layers[1] = {};
        uint32_t nlayers = 0;
        if (fs.shouldRender) {
            XrViewLocateInfo vli{XR_TYPE_VIEW_LOCATE_INFO};
            vli.viewConfigurationType = stereo;
            vli.displayTime = fs.predictedDisplayTime;
            vli.space = space;
            XrViewState vs{XR_TYPE_VIEW_STATE};
            std::vector<XrView> views(nviews, view0);
            uint32_t nv = nviews;
            XR(locate_views(session, &vli, &vs, nviews, &nv, views.data()));
            for (uint32_t i = 0; i < nviews; ++i) {
                uint32_t idx = 0;
                XrSwapchainImageAcquireInfo ai{XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO};
                XR(acquire(eyes[i].sc, &ai, &idx));
                XrSwapchainImageWaitInfo wi{XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO};
                wi.timeout = XR_INFINITE_DURATION;
                XR(wait_img(eyes[i].sc, &wi));
                if (!d12) {
                    ctx11->ClearRenderTargetView(eyes[i].rtv11[idx], grey);
                } else {
                    alloc->Reset();
                    list->Reset(alloc, nullptr);
                    D3D12_CPU_DESCRIPTOR_HANDLE h = rtv_heap->GetCPUDescriptorHandleForHeapStart();
                    h.ptr += static_cast<SIZE_T>(eyes[i].rtv_base + idx) * rtv_inc;
                    list->ClearRenderTargetView(h, grey, 0, nullptr);
                    list->Close();
                    ID3D12CommandList* ls[] = {list};
                    queue->ExecuteCommandLists(1, ls);
                    const UINT64 v = ++fence_value;
                    queue->Signal(fence, v);
                    if (fence->GetCompletedValue() < v) { fence->SetEventOnCompletion(v, fence_event); WaitForSingleObject(fence_event, 1000); }
                }
                XrSwapchainImageReleaseInfo ri{XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO};
                XR(release(eyes[i].sc, &ri));
                pv[i].pose = views[i].pose;
                pv[i].fov = views[i].fov;
                pv[i].subImage.swapchain = eyes[i].sc;
                pv[i].subImage.imageRect = {{0, 0}, {static_cast<int32_t>(eyes[i].w), static_cast<int32_t>(eyes[i].h)}};
                pv[i].subImage.imageArrayIndex = 0;
            }
            proj.space = space;
            proj.viewCount = nviews;
            proj.views = pv.data();
            layers[0] = reinterpret_cast<const XrCompositionLayerBaseHeader*>(&proj);
            nlayers = 1;
        }
        if (actions && !act_fail) {   // like a game: sync, read the trigger, find the hand
            XrActiveActionSet active{aset, XR_NULL_PATH};
            XrActionsSyncInfo sy{XR_TYPE_ACTIONS_SYNC_INFO};
            sy.countActiveActionSets = 1;
            sy.activeActionSets = &active;
            XrResult r = sync_actions(session, &sy);
            if (XR_FAILED(r)) { act_fail = "sync"; act_code = r; } else ++syncs;
            XrActionStateGetInfo gi{XR_TYPE_ACTION_STATE_GET_INFO};
            gi.action = a_trigger;
            XrActionStateFloat st{XR_TYPE_ACTION_STATE_FLOAT};
            r = state_float(session, &gi, &st);
            if (XR_FAILED(r)) { act_fail = "state"; act_code = r; } else ++states;
            XrSpaceLocation loc{XR_TYPE_SPACE_LOCATION};
            r = locate_space(hand_space, space, fs.predictedDisplayTime, &loc);
            if (XR_FAILED(r)) { act_fail = "locate"; act_code = r; } else ++poses;
        }
        XrFrameEndInfo fei{XR_TYPE_FRAME_END_INFO};
        fei.displayTime = fs.predictedDisplayTime;
        fei.environmentBlendMode = XR_ENVIRONMENT_BLEND_MODE_OPAQUE;
        fei.layerCount = nlayers;
        fei.layers = layers;
        XR(end_frame(session, &fei));
        ++frames;
    }
    printf("API: %s\nFRAMES: %d\n", d12 ? "D3D12" : "D3D11", frames);
    if (actions) {
        if (act_fail) printf("ACTIONS: FAIL %s %d\n", act_fail, static_cast<int>(act_code));
        else printf("ACTIONS: ok syncs=%d states=%d poses=%d\n", syncs, states, poses);
    }
    fflush(stdout);
    fn<PFN_xrDestroySession>(inst, "xrDestroySession")(session);
    fn<PFN_xrDestroyInstance>(inst, "xrDestroyInstance")(inst);
    return frames > 0 ? 0 : 1;
}
