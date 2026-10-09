#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <winrt/base.h>
#include <cmath>
#include <iostream>

int wmain(int argc, wchar_t** argv) {
    if (argc != 4 || std::wstring(argv[1]) != L"--acknowledge-playback" || std::wstring(argv[2]) != L"--seconds") return 2;
    const int seconds = _wtoi(argv[3]); if (seconds < 1 || seconds > 30) return 2;
    try {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
        auto enumerator = winrt::create_instance<IMMDeviceEnumerator>(__uuidof(MMDeviceEnumerator));
        winrt::com_ptr<IMMDevice> device; winrt::check_hresult(enumerator->GetDefaultAudioEndpoint(eRender, eConsole, device.put()));
        winrt::com_ptr<IAudioClient> client; winrt::check_hresult(device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, client.put_void()));
        WAVEFORMATEX format{WAVE_FORMAT_PCM, 2, 48000, 192000, 4, 16, 0};
        winrt::check_hresult(client->Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY, 1000000, 0, &format, nullptr));
        winrt::com_ptr<IAudioRenderClient> render; winrt::check_hresult(client->GetService(__uuidof(IAudioRenderClient), render.put_void()));
        UINT32 capacity{}; winrt::check_hresult(client->GetBufferSize(&capacity));
        uint64_t position{};
        auto fill = [&](UINT32 frames) {
            BYTE* data{}; winrt::check_hresult(render->GetBuffer(frames, &data));
            auto samples = reinterpret_cast<int16_t*>(data);
            for (UINT32 i = 0; i < frames; ++i) {
                const auto value = static_cast<int16_t>(983 * std::sin(2 * 3.141592653589793 * 440 * static_cast<double>(position++) / 48000));
                samples[i * 2] = samples[i * 2 + 1] = value;
            }
            winrt::check_hresult(render->ReleaseBuffer(frames, 0));
        };
        fill(capacity); winrt::check_hresult(client->Start());
        std::cout << "tone_ready pid=" << GetCurrentProcessId() << std::endl;
        const auto start = GetTickCount64();
        while (GetTickCount64() - start < static_cast<ULONGLONG>(seconds) * 1000) {
            Sleep(10); UINT32 padding{}; winrt::check_hresult(client->GetCurrentPadding(&padding));
            if (capacity > padding) fill(capacity - padding);
        }
        winrt::check_hresult(client->Stop()); return 0;
    } catch (const winrt::hresult_error& error) { std::cerr << "tone_error 0x" << std::hex << static_cast<uint32_t>(error.code()) << '\n'; return 2; }
}
