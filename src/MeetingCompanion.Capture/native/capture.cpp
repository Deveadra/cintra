#include <windows.h>
#include <d3d11.h>
#include <dxgi1_6.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <atomic>
#include <cstring>
#include <memory>

using namespace winrt;
using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::Graphics::DirectX::Direct3D11;

// All resources belong to this one manual request. No capture session survives return.
extern "C" __declspec(dllexport) HRESULT __cdecl SnapshotCapture(
    void* target, int monitor, HANDLE cancelled, unsigned char** pixels,
    int* width, int* height, long long* qpc) noexcept {
    *pixels = nullptr; *width = 0; *height = 0; *qpc = 0;
    try {
        init_apartment(apartment_type::multi_threaded);
        struct apartment_guard { ~apartment_guard() { uninit_apartment(); } } apartment;
        if (!GraphicsCaptureSession::IsSupported()) return E_NOTIMPL;
        if (!monitor && (!IsWindow(static_cast<HWND>(target)) || IsIconic(static_cast<HWND>(target)))) return E_INVALIDARG;
        if (!monitor) {
            DWORD affinity = 0;
            if (GetWindowDisplayAffinity(static_cast<HWND>(target), &affinity) && affinity != WDA_NONE) return E_ACCESSDENIED;
        }
        auto factory = get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        GraphicsCaptureItem item{ nullptr };
        if (monitor) check_hresult(factory->CreateForMonitor(static_cast<HMONITOR>(target), guid_of<GraphicsCaptureItem>(), put_abi(item)));
        else check_hresult(factory->CreateForWindow(static_cast<HWND>(target), guid_of<GraphicsCaptureItem>(), put_abi(item)));
        auto size = item.Size();
        if (size.Width <= 0 || size.Height <= 0 || size.Width > 16384 || size.Height > 16384 || static_cast<long long>(size.Width) * size.Height > 33554432) return E_INVALIDARG;
        com_ptr<ID3D11Device> device;
        com_ptr<ID3D11DeviceContext> context;
        check_hresult(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            nullptr, 0, D3D11_SDK_VERSION, device.put(), nullptr, context.put()));
        // Reject HDR rather than silently clip diagnostic text/colors into an SDR buffer.
        com_ptr<IDXGIDevice> dxgi = device.as<IDXGIDevice>();
        auto targetMonitor = monitor ? static_cast<HMONITOR>(target) : MonitorFromWindow(static_cast<HWND>(target), MONITOR_DEFAULTTONEAREST);
        com_ptr<IDXGIFactory1> displayFactory; check_hresult(CreateDXGIFactory1(__uuidof(IDXGIFactory1), displayFactory.put_void()));
        for (UINT a = 0;; ++a) {
            com_ptr<IDXGIAdapter1> adapter;
            auto adapterResult = displayFactory->EnumAdapters1(a, adapter.put());
            if (adapterResult == DXGI_ERROR_NOT_FOUND) break;
            check_hresult(adapterResult);
            for (UINT i = 0;; ++i) {
                com_ptr<IDXGIOutput> output;
                auto outputResult = adapter->EnumOutputs(i, output.put());
                if (outputResult == DXGI_ERROR_NOT_FOUND) break;
                check_hresult(outputResult);
                auto output6 = output.try_as<IDXGIOutput6>();
                if (output6) {
                    DXGI_OUTPUT_DESC1 description{};
                    if (SUCCEEDED(output6->GetDesc1(&description)) && description.Monitor == targetMonitor &&
                        description.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020) return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
                }
            }
        }
        com_ptr<IInspectable> inspectable;
        check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgi.get(), inspectable.put()));
        auto pool = Direct3D11CaptureFramePool::CreateFreeThreaded(inspectable.as<IDirect3DDevice>(), DirectXPixelFormat::B8G8R8A8UIntNormalized, 1, size);
        auto session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled(false);
        auto closed = std::make_shared<std::atomic_bool>(false);
        auto closedToken = item.Closed([closed](auto const&, auto const&) { *closed = true; });
        struct capture_guard {
            GraphicsCaptureItem item; event_token token; GraphicsCaptureSession session; Direct3D11CaptureFramePool pool;
            ~capture_guard() noexcept { try { item.Closed(token); session.Close(); pool.Close(); } catch (...) {} }
        } guard{ item, closedToken, session, pool };
        session.StartCapture();
        const auto deadline = GetTickCount64() + 3000;
        Direct3D11CaptureFrame frame{ nullptr };
        while (!(frame = pool.TryGetNextFrame())) {
            if (WaitForSingleObject(cancelled, 10) == WAIT_OBJECT_0) return HRESULT_FROM_WIN32(ERROR_CANCELLED);
            if (*closed) return HRESULT_FROM_WIN32(ERROR_INVALID_WINDOW_HANDLE);
            if (GetTickCount64() >= deadline) return HRESULT_FROM_WIN32(WAIT_TIMEOUT);
        }
        struct frame_guard { Direct3D11CaptureFrame frame; ~frame_guard() noexcept { try { frame.Close(); } catch (...) {} } } frameGuard{ frame };
        if (WaitForSingleObject(cancelled, 0) == WAIT_OBJECT_0) return HRESULT_FROM_WIN32(ERROR_CANCELLED);
        const auto actual = frame.ContentSize();
        if (actual.Width != size.Width || actual.Height != size.Height) return HRESULT_FROM_WIN32(ERROR_RETRY);
        auto access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
        com_ptr<ID3D11Texture2D> texture;
        check_hresult(access->GetInterface(__uuidof(ID3D11Texture2D), texture.put_void()));
        D3D11_TEXTURE2D_DESC desc{}; texture->GetDesc(&desc);
        desc.Usage = D3D11_USAGE_STAGING; desc.BindFlags = 0; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ; desc.MiscFlags = 0;
        com_ptr<ID3D11Texture2D> staging; check_hresult(device->CreateTexture2D(&desc, nullptr, staging.put()));
        context->CopyResource(staging.get(), texture.get());
        D3D11_MAPPED_SUBRESOURCE mapped{};
        for (;;) {
            auto result = context->Map(staging.get(), 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped);
            if (result != DXGI_ERROR_WAS_STILL_DRAWING) { check_hresult(result); break; }
            if (WaitForSingleObject(cancelled, 10) == WAIT_OBJECT_0) return HRESULT_FROM_WIN32(ERROR_CANCELLED);
            if (GetTickCount64() >= deadline) return HRESULT_FROM_WIN32(WAIT_TIMEOUT);
        }
        struct map_guard { ID3D11DeviceContext* context; ID3D11Resource* resource; ~map_guard() { context->Unmap(resource, 0); } } mapGuard{ context.get(), staging.get() };
        const size_t stride = static_cast<size_t>(actual.Width) * 4;
        const size_t bytes = stride * actual.Height;
        auto buffer = static_cast<unsigned char*>(CoTaskMemAlloc(bytes));
        if (!buffer) return E_OUTOFMEMORY;
        for (int row = 0; row < actual.Height; ++row) std::memcpy(buffer + row * stride, static_cast<unsigned char*>(mapped.pData) + row * mapped.RowPitch, stride);
        *pixels = buffer; *width = actual.Width; *height = actual.Height;
        LARGE_INTEGER frequency{}; QueryPerformanceFrequency(&frequency);
        *qpc = static_cast<long long>(frame.SystemRelativeTime().count() * static_cast<long double>(frequency.QuadPart) / 10000000.0L);
        return S_OK;
    } catch (...) { return to_hresult(); }
}
