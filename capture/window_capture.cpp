// window_capture.cpp -- 1.5: window pictures through Windows Graphics Capture, for the Window View app (window_view_capture.dll).
//
// Why (Tefa 2026-10-25: "the sliders to somehow move smoothly not trail behind, if at all possible"): PrintWindow makes the
// window draw itself again for every picture. Measured on the home PC: Hand Poser 29-34 ms, Grab Zones 40 ms a picture, and
// the app took them one after another, so each window got a new picture about 12 times a second and a dragged slider trailed
// behind the pointer. Graphics Capture copies the picture the desktop compositor already has, on the graphics card: nothing is
// redrawn, and new frames arrive as fast as the window changes. Each capture keeps its own D3D11 device, so every window can
// be read from its own thread.
//
//   int   wvc_supported()                           1 = this Windows can do it
//   void* wvc_open(HWND)                            nullptr = could not start (the app falls back to PrintWindow)
//   int   wvc_grab(void*, uint8_t* out, int max_bytes, int* w, int* h)
//         the window's CLIENT area (no title bar or borders), BGRA, top-down, tightly packed; 1 = a new picture is in `out`,
//         0 = nothing new since the last call, -1 = the capture ended (window closed) or the picture would not fit
//   void  wvc_close(void*)
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <dwmapi.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Metadata.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>

#include <cstring>
#include <mutex>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "dwmapi.lib")
#pragma comment(lib, "windowsapp.lib")
#pragma comment(lib, "user32.lib")

namespace wgc = winrt::Windows::Graphics::Capture;
namespace wd3d = winrt::Windows::Graphics::DirectX::Direct3D11;

namespace {

void apartment() {   // WinRT on this thread (the app calls from its own capture threads); a thread already set up is fine
    static thread_local bool done = false;
    if (done) return;
    try { winrt::init_apartment(winrt::apartment_type::multi_threaded); } catch (...) {}
    done = true;
}

struct Cap {
    HWND hwnd = nullptr;
    winrt::com_ptr<ID3D11Device> dev;
    winrt::com_ptr<ID3D11DeviceContext> ctx;
    wd3d::IDirect3DDevice rtdev{nullptr};
    wgc::GraphicsCaptureItem item{nullptr};
    wgc::Direct3D11CaptureFramePool pool{nullptr};
    wgc::GraphicsCaptureSession session{nullptr};
    winrt::com_ptr<ID3D11Texture2D> staging;
    UINT sw = 0, sh = 0;
    winrt::Windows::Graphics::SizeInt32 size{0, 0};
    bool closed = false;
    std::mutex m;
};

// where the client area sits inside the captured picture (the window's visible frame), in physical pixels
bool client_in_frame(HWND hwnd, RECT& out) {
    DPI_AWARENESS_CONTEXT old = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    RECT frame{}, client{};
    POINT org{0, 0};
    bool ok = SUCCEEDED(DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &frame, sizeof(frame))) && GetClientRect(hwnd, &client) && ClientToScreen(hwnd, &org);
    if (old != nullptr) SetThreadDpiAwarenessContext(old);
    if (!ok) return false;
    out = {org.x - frame.left, org.y - frame.top, org.x - frame.left + client.right, org.y - frame.top + client.bottom};
    return true;
}

}  // namespace

extern "C" __declspec(dllexport) int wvc_supported() {
    apartment();
    try { return wgc::GraphicsCaptureSession::IsSupported() ? 1 : 0; } catch (...) { return 0; }
}

extern "C" __declspec(dllexport) void* wvc_open(HWND hwnd) {
    apartment();
    auto* c = new Cap();
    try {
        c->hwnd = hwnd;
        D3D_FEATURE_LEVEL fl;
        if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION,
                                     c->dev.put(), &fl, c->ctx.put()))) { delete c; return nullptr; }
        auto dxgi = c->dev.as<IDXGIDevice>();
        winrt::com_ptr<::IInspectable> insp;
        if (FAILED(CreateDirect3D11DeviceFromDXGIDevice(dxgi.get(), insp.put()))) { delete c; return nullptr; }
        c->rtdev = insp.as<wd3d::IDirect3DDevice>();
        auto interop = winrt::get_activation_factory<wgc::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        winrt::check_hresult(interop->CreateForWindow(hwnd, winrt::guid_of<wgc::GraphicsCaptureItem>(), winrt::put_abi(c->item)));
        c->size = c->item.Size();
        c->pool = wgc::Direct3D11CaptureFramePool::CreateFreeThreaded(c->rtdev, winrt::Windows::Graphics::DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, c->size);
        c->session = c->pool.CreateCaptureSession(c->item);
        try { c->session.IsCursorCaptureEnabled(false); } catch (...) {}   // the layer draws our own pointer
        try {
            if (winrt::Windows::Foundation::Metadata::ApiInformation::IsPropertyPresent(L"Windows.Graphics.Capture.GraphicsCaptureSession", L"IsBorderRequired"))
                c->session.IsBorderRequired(false);                            // no yellow frame around the window on the desktop (Windows 11)
        } catch (...) {}
        c->item.Closed([c](auto&&, auto&&) { c->closed = true; });
        c->session.StartCapture();
        return c;
    } catch (...) {
        delete c;
        return nullptr;
    }
}

extern "C" __declspec(dllexport) int wvc_grab(void* h, uint8_t* out, int max_bytes, int* w, int* hgt) {
    apartment();
    auto* c = static_cast<Cap*>(h);
    if (c == nullptr || c->closed) return -1;
    std::lock_guard<std::mutex> lock(c->m);
    try {
        wgc::Direct3D11CaptureFrame frame{nullptr};
        for (;;) {   // only the newest frame matters
            auto next = c->pool.TryGetNextFrame();
            if (!next) break;
            if (frame) frame.Close();
            frame = next;
        }
        if (!frame) return 0;
        const auto size = frame.ContentSize();
        if (size.Width != c->size.Width || size.Height != c->size.Height) {   // the window was resized: a pool of the new size
            c->size = size;
            c->pool.Recreate(c->rtdev, winrt::Windows::Graphics::DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size);
            frame.Close();
            return 0;
        }
        auto access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
        winrt::com_ptr<ID3D11Texture2D> tex;
        winrt::check_hresult(access->GetInterface(IID_PPV_ARGS(tex.put())));
        D3D11_TEXTURE2D_DESC td{};
        tex->GetDesc(&td);
        RECT cr{};
        if (!client_in_frame(c->hwnd, cr)) { frame.Close(); return 0; }
        cr.left = (cr.left > 0 ? cr.left : 0); cr.top = (cr.top > 0 ? cr.top : 0);
        cr.right = (cr.right < static_cast<LONG>(td.Width) ? cr.right : static_cast<LONG>(td.Width)); cr.bottom = (cr.bottom < static_cast<LONG>(td.Height) ? cr.bottom : static_cast<LONG>(td.Height));
        const int cw = cr.right - cr.left, ch = cr.bottom - cr.top;
        if (cw < 8 || ch < 8) { frame.Close(); return 0; }
        if (cw * ch * 4 > max_bytes) { frame.Close(); return -1; }
        if (!c->staging || c->sw != static_cast<UINT>(cw) || c->sh != static_cast<UINT>(ch)) {
            D3D11_TEXTURE2D_DESC sd{};
            sd.Width = cw; sd.Height = ch; sd.MipLevels = 1; sd.ArraySize = 1; sd.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            sd.SampleDesc.Count = 1; sd.Usage = D3D11_USAGE_STAGING; sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            c->staging = nullptr;
            winrt::check_hresult(c->dev->CreateTexture2D(&sd, nullptr, c->staging.put()));
            c->sw = cw; c->sh = ch;
        }
        D3D11_BOX box{static_cast<UINT>(cr.left), static_cast<UINT>(cr.top), 0, static_cast<UINT>(cr.right), static_cast<UINT>(cr.bottom), 1};
        c->ctx->CopySubresourceRegion(c->staging.get(), 0, 0, 0, 0, tex.get(), 0, &box);
        frame.Close();
        D3D11_MAPPED_SUBRESOURCE ms{};
        winrt::check_hresult(c->ctx->Map(c->staging.get(), 0, D3D11_MAP_READ, 0, &ms));
        for (int y = 0; y < ch; ++y) std::memcpy(out + static_cast<size_t>(y) * cw * 4, static_cast<const uint8_t*>(ms.pData) + static_cast<size_t>(y) * ms.RowPitch, static_cast<size_t>(cw) * 4);
        c->ctx->Unmap(c->staging.get(), 0);
        *w = cw;
        *hgt = ch;
        return 1;
    } catch (...) {
        return -1;
    }
}

extern "C" __declspec(dllexport) void wvc_close(void* h) {
    auto* c = static_cast<Cap*>(h);
    if (c == nullptr) return;
    try { if (c->session) c->session.Close(); if (c->pool) c->pool.Close(); } catch (...) {}
    delete c;
}
