#include "wasapi_support.h"
#include <iostream>

namespace {
struct FailedOperation : winrt::implements<FailedOperation, IActivateAudioInterfaceAsyncOperation> {
    HRESULT __stdcall GetActivateResult(HRESULT* result, IUnknown** object) noexcept override {
        *result = E_ACCESSDENIED; *object = nullptr; return S_OK;
    }
};
void require(bool condition, const char* message) { if (!condition) throw std::runtime_error(message); }
}
int main() {
    try {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
        using namespace cintra;
        auto mic = winrt::make_self<detail::Notifications>(L"pinned-mic");
        mic->OnDeviceRemoved(L"other-device"); require(!mic->changed, "unrelated endpoint");
        mic->OnDefaultDeviceChanged(eCapture, eConsole, L"new-default"); require(!mic->changed, "no silent mic replacement");
        mic->OnDeviceStateChanged(L"pinned-mic", DEVICE_STATE_UNPLUGGED); require(mic->changed.exchange(false), "selected endpoint invalidation");
        auto remote = winrt::make_self<detail::Notifications>(L"");
        remote->OnDefaultDeviceChanged(eRender, eConsole, L"new-output"); require(remote->changed, "output topology notification");
        DWORD before{}; GetProcessHandleCount(GetCurrentProcess(), &before);
        for (int i = 0; i < 100; ++i) {
            auto activation = winrt::make_self<detail::Activation>();
            const auto event = activation->ready.value;
            require(WaitForSingleObject(event, 0) == WAIT_TIMEOUT, "activation pending");
            // Simulate caller timeout/cancellation. The OS-owned callback reference
            // remains valid after the capture generation releases its owner.
            auto osReference = activation.as<IActivateAudioInterfaceCompletionHandler>();
            activation = nullptr;
            auto operation = winrt::make_self<FailedOperation>();
            require(osReference->ActivateCompleted(operation.get()) == S_OK, "late callback completes");
            require(WaitForSingleObject(event, 0) == WAIT_OBJECT_0, "completion signals");
            auto concrete = winrt::get_self<detail::Activation>(osReference);
            require(concrete->result == E_ACCESSDENIED && !concrete->client, "meaningful activation error");
        }
        DWORD after{}; GetProcessHandleCount(GetCurrentProcess(), &after);
        require(before == after, "100 late activation completions leak no events");
        std::cout << "PASS notifications, pinned device policy, late callback/error, 100 callback cleanup cycles\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << "FAIL " << error.what() << '\n'; return 1; }
}
