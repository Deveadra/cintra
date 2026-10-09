#pragma once
#include "capture.h"
#include <mmdeviceapi.h>
#include <audioclient.h>

namespace cintra::detail {
struct Activation : winrt::implements<Activation, IActivateAudioInterfaceCompletionHandler> {
    Handle ready{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    winrt::com_ptr<IAudioClient> client;
    HRESULT result{E_PENDING};
    HRESULT __stdcall ActivateCompleted(IActivateAudioInterfaceAsyncOperation* operation) noexcept override {
        winrt::com_ptr<IUnknown> unknown;
        HRESULT activated = E_FAIL;
        result = operation->GetActivateResult(&activated, unknown.put());
        if (SUCCEEDED(result)) result = activated;
        if (SUCCEEDED(result)) result = unknown ? unknown->QueryInterface(__uuidof(IAudioClient), client.put_void()) : E_NOINTERFACE;
        SetEvent(ready.value); return S_OK;
    }
};
struct Notifications : winrt::implements<Notifications, IMMNotificationClient> {
    std::wstring endpoint;
    std::atomic<bool> changed{false};
    explicit Notifications(std::wstring id) : endpoint(std::move(id)) {}
    HRESULT __stdcall OnDeviceStateChanged(LPCWSTR id, DWORD) noexcept override { mark(id); return S_OK; }
    HRESULT __stdcall OnDeviceAdded(LPCWSTR id) noexcept override { mark(id); return S_OK; }
    HRESULT __stdcall OnDeviceRemoved(LPCWSTR id) noexcept override { mark(id); return S_OK; }
    HRESULT __stdcall OnDefaultDeviceChanged(EDataFlow flow, ERole, LPCWSTR) noexcept override {
        if (endpoint.empty() && flow == eRender) changed = true;
        return S_OK;
    }
    HRESULT __stdcall OnPropertyValueChanged(LPCWSTR id, const PROPERTYKEY) noexcept override { mark(id); return S_OK; }
    void mark(LPCWSTR id) { if (id && endpoint == id) changed = true; }
};
}
