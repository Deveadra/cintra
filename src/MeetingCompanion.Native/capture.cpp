#include "capture.h"
#include "wasapi_support.h"
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <audioclientactivationparams.h>
#include <ksmedia.h>
#include <avrt.h>
#include <sstream>
#include <iomanip>

namespace cintra {
using detail::Activation;
using detail::Notifications;
namespace {
struct Failure : std::runtime_error {
    HRESULT code;
    Failure(const char* stage, HRESULT hr) : std::runtime_error(stage), code(hr) {}
};
void check(HRESULT hr, const char* stage) { if (FAILED(hr)) throw Failure(stage, hr); }
std::string diagnostic(const Failure& failure) {
    std::ostringstream out; out << failure.what() << ":0x" << std::hex << std::setw(8) << std::setfill('0') << static_cast<uint32_t>(failure.code);
    return out.str();
}
struct Subscription {
    IMMDeviceEnumerator* enumerator;
    IMMNotificationClient* notifications;
    ~Subscription() { enumerator->UnregisterEndpointNotificationCallback(notifications); }
};
struct TaskMemory {
    WAVEFORMATEX* value{};
    ~TaskMemory() { CoTaskMemFree(value); }
};
struct BufferLease {
    IAudioCaptureClient* capture;
    UINT32 frames;
    bool released{};
    ~BufferLease() { if (!released) capture->ReleaseBuffer(frames); }
    void release() { released = true; check(capture->ReleaseBuffer(frames), "release_buffer"); }
};
struct StopClient {
    IAudioClient* client;
    ~StopClient() { client->Stop(); }
};
std::string base64(const std::vector<int16_t>& samples) {
    constexpr char alphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    const auto data = reinterpret_cast<const uint8_t*>(samples.data());
    const size_t count = samples.size() * 2;
    std::string result; result.reserve((count + 2) / 3 * 4);
    for (size_t i = 0; i < count; i += 3) {
        const uint32_t value = (static_cast<uint32_t>(data[i]) << 16) |
            (i + 1 < count ? static_cast<uint32_t>(data[i + 1]) << 8 : 0) |
            (i + 2 < count ? data[i + 2] : 0);
        result += alphabet[(value >> 18) & 63]; result += alphabet[(value >> 12) & 63];
        result += i + 1 < count ? alphabet[(value >> 6) & 63] : '=';
        result += i + 2 < count ? alphabet[value & 63] : '=';
    }
    return result;
}
Format sample_format(const WAVEFORMATEX* wave) {
    Format result{wave->nSamplesPerSec, wave->nChannels, wave->wBitsPerSample,
        wave->wBitsPerSample, wave->nBlockAlign, wave->wFormatTag == WAVE_FORMAT_IEEE_FLOAT};
    if (wave->wFormatTag == WAVE_FORMAT_EXTENSIBLE && wave->cbSize >= 22) {
        const auto ext = reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(wave);
        result.validBits = ext->Samples.wValidBitsPerSample;
        result.floating = ext->SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
        if (!result.floating && ext->SubFormat != KSDATAFORMAT_SUBTYPE_PCM)
            throw std::runtime_error("unsupported_subformat");
    } else if (wave->wFormatTag != WAVE_FORMAT_PCM && wave->wFormatTag != WAVE_FORMAT_IEEE_FLOAT)
        throw std::runtime_error("unsupported_subformat");
    result.validate(); return result;
}
}
int64_t Clock::now() const {
    LARGE_INTEGER counter; QueryPerformanceCounter(&counter);
    return std::max<int64_t>(0, static_cast<int64_t>((static_cast<long double>(counter.QuadPart) - origin) * 1000 / frequency));
}
int64_t Clock::packet_ms(uint64_t qpc100ns) const {
    return static_cast<int64_t>(static_cast<long double>(qpc100ns) / 10000 - static_cast<long double>(origin) * 1000 / frequency);
}
Capture::Capture(Selection selection, Clock clock, Emit emit) : selection_(std::move(selection)), clock_(clock), emit_(std::move(emit)) {
    if (selection_.pid) {
        process_.value = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, selection_.pid);
        if (!process_.value) processError_ = GetLastError();
    }
    worker_ = std::jthread([this](std::stop_token stop) { run(stop); });
}
Capture::~Capture() { stop(); }
void Capture::stop() { if (worker_.joinable()) { worker_.request_stop(); worker_.join(); } }
void Capture::pause(bool pause) { paused_ = pause; }
void Capture::health(const std::string& status, const std::string& code, double peak) {
    JsonObject state; str(state, L"component", selection_.stream == "local_mic" ? "local_capture" : "remote_capture");
    str(state, L"status", status); num(state, L"observed_at_ms", static_cast<double>(clock_.now())); str(state, L"diagnostic_code", code);
    JsonObject payload; str(payload, L"message_type", "capture_health"); str(payload, L"stream_id", selection_.stream);
    payload.Insert(L"health", state); num(payload, L"peak", peak);
    num(payload, L"dropped_frames", static_cast<double>(dropped_.load())); num(payload, L"oldest_frame_age_ms", 0);
    emit_(payload, selection_.stream, 0, 0, 0);
}
void Capture::gap(int64_t start, int64_t end, const std::string& reason, uint64_t dropped) {
    JsonObject payload; str(payload, L"message_type", "capture_gap"); str(payload, L"stream_id", selection_.stream);
    num(payload, L"captured_start_ms", static_cast<double>(std::max<int64_t>(0, start)));
    num(payload, L"captured_end_ms", static_cast<double>(std::max({int64_t{0}, start, end})));
    str(payload, L"reason", reason); num(payload, L"dropped_frames", static_cast<double>(dropped));
    emit_(payload, selection_.stream, 0, 0, 0);
}
void Capture::run(std::stop_token stop) {
    try {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
        struct Apartment { ~Apartment() { winrt::uninit_apartment(); } } apartment;
        if (selection_.pid && (!process_.value || WaitForSingleObject(process_.value, 0) != WAIT_TIMEOUT)) {
            health(processError_ == ERROR_ACCESS_DENIED ? "permission_denied" : "wrong_process",
                processError_ == ERROR_ACCESS_DENIED ? "selected_process_access_denied" : "selected_process_unavailable");
            gap(clock_.now(), clock_.now(), "source_changed");
        } else {
            RecoveryBudget recovery;
            while (!stop.stop_requested()) {
                if (paused_) { health("stopped", "paused_resources_released"); for (int i = 0; i < 25 && paused_ && !stop.stop_requested(); ++i) Sleep(10); continue; }
                try { attempt(stop); if (stop.stop_requested()) break; health("stopped", "paused_resources_released"); recovery.reset(); }
                catch (const Failure& failure) {
                    if (stop.stop_requested()) break;
                    const auto now = clock_.now(); gap(now, now, failure.code == HRESULT_FROM_WIN32(ERROR_PROCESS_ABORTED) ? "source_changed" : "device_lost");
                    const bool terminal = failure.code == E_ACCESSDENIED || failure.code == E_INVALIDARG || failure.code == AUDCLNT_E_UNSUPPORTED_FORMAT || failure.code == HRESULT_FROM_WIN32(ERROR_PROCESS_ABORTED);
                    health(terminal ? (failure.code == E_ACCESSDENIED ? "permission_denied" : "failed") :
                        (failure.code == E_NOTFOUND || failure.code == HRESULT_FROM_WIN32(ERROR_NOT_FOUND) ? "no_device" : "reconnecting"), diagnostic(failure));
                    const auto delay = recovery.next_delay(!terminal);
                    if (!delay) { health("failed", terminal ? "nonrecoverable_source_error" : "recovery_budget_exhausted"); break; }
                    const auto start = clock_.now();
                    for (unsigned wait = 0; wait < *delay && !stop.stop_requested() && !paused_; wait += 10) Sleep(10);
                    gap(start, clock_.now(), "device_lost");
                }
                catch (const std::exception& failure) { health("failed", failure.what()); break; }
            }
        }
        health("stopped", "capture_resources_released");
    } catch (...) {
        // No audio/log contents. Main transport watchdog handles absent heartbeat.
    }
}
void Capture::attempt(std::stop_token stop) {
    health("starting", "wasapi_initializing");
    winrt::com_ptr<IMMDeviceEnumerator> enumerator;
    check(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, __uuidof(IMMDeviceEnumerator), enumerator.put_void()), "create_device_enumerator");
    auto notification = winrt::make_self<Notifications>(selection_.endpoint);
    check(enumerator->RegisterEndpointNotificationCallback(notification.get()), "subscribe_devices");
    Subscription subscription{enumerator.get(), notification.get()};
    winrt::com_ptr<IAudioClient> client;
    TaskMemory mix;
    WAVEFORMATEX processFormat{WAVE_FORMAT_PCM, 2, 48000, 192000, 4, 16, 0};
    WAVEFORMATEX* wave;
    if (selection_.pid) {
        if (WaitForSingleObject(process_.value, 0) != WAIT_TIMEOUT) throw Failure("source_terminated", HRESULT_FROM_WIN32(ERROR_PROCESS_ABORTED));
        AUDIOCLIENT_ACTIVATION_PARAMS parameters{};
        parameters.ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK;
        parameters.ProcessLoopbackParams.TargetProcessId = selection_.pid;
        parameters.ProcessLoopbackParams.ProcessLoopbackMode = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE;
        PROPVARIANT variant{}; variant.vt = VT_BLOB; variant.blob.cbSize = sizeof(parameters);
        variant.blob.pBlobData = reinterpret_cast<BYTE*>(&parameters);
        auto completion = winrt::make_self<Activation>();
        winrt::com_ptr<IActivateAudioInterfaceAsyncOperation> operation;
        check(ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, __uuidof(IAudioClient), &variant, completion.get(), operation.put()), "activate_process_loopback");
        const auto began = clock_.now();
        auto lastActivationHealth = began;
        while (WaitForSingleObject(completion->ready.value, 10) == WAIT_TIMEOUT) {
            if (stop.stop_requested() || paused_) return;
            if (clock_.now() - began > 5000) throw Failure("activation_timeout", HRESULT_FROM_WIN32(WAIT_TIMEOUT));
            if (clock_.now() - lastActivationHealth >= 250) { health("starting", "process_activation_pending"); lastActivationHealth = clock_.now(); }
        }
        check(completion->result, "activate_result"); client = completion->client; wave = &processFormat;
        if (WaitForSingleObject(process_.value, 0) != WAIT_TIMEOUT) throw Failure("source_terminated", HRESULT_FROM_WIN32(ERROR_PROCESS_ABORTED));
    } else {
        winrt::com_ptr<IMMDevice> device;
        check(enumerator->GetDevice(selection_.endpoint.c_str(), device.put()), "selected_endpoint");
        check(device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, client.put_void()), "activate_microphone");
        check(client->GetMixFormat(&mix.value), "mix_format"); wave = mix.value;
    }
    const auto format = sample_format(wave);
    DWORD flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK;
    if (selection_.pid) flags |= AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
    check(client->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 200000, 0, wave, nullptr), "initialize_audio");
    Handle event{CreateEventW(nullptr, FALSE, FALSE, nullptr)};
    if (!event.value) throw Failure("create_audio_event", HRESULT_FROM_WIN32(GetLastError()));
    check(client->SetEventHandle(event.value), "set_audio_event");
    winrt::com_ptr<IAudioCaptureClient> capture;
    check(client->GetService(__uuidof(IAudioCaptureClient), capture.put_void()), "capture_service");
    check(client->Start(), "start_audio"); StopClient stopClient{client.get()};
    JsonObject changed; str(changed, L"message_type", "capture_source_changed"); changed.Insert(L"source", selection_.wire);
    num(changed, L"effective_at_ms", static_cast<double>(clock_.now())); emit_(changed, selection_.stream, 0, 0, 0);
    Converter converter(format);
    std::vector<int16_t> assembly;
    int64_t anchor{-1}, emitted{}, lastPacket = clock_.now(), lastHealth{}, lastEnd{};
    uint64_t sourceFrames{};
    double peak{}; bool silent{}, audibleObserved{};
    while (!stop.stop_requested()) {
        if (selection_.pid && WaitForSingleObject(process_.value, 0) != WAIT_TIMEOUT) {
            gap(lastEnd, clock_.now(), "source_changed"); throw Failure("source_terminated", HRESULT_FROM_WIN32(ERROR_PROCESS_ABORTED));
        }
        if (paused_) {
            check(client->Stop(), "pause_audio"); gap(lastEnd, clock_.now(), "paused"); return;
        }
        if (notification->changed.exchange(false)) {
            gap(lastEnd, clock_.now(), "source_changed"); throw Failure("device_topology_changed", AUDCLNT_E_DEVICE_INVALIDATED);
        }
        WaitForSingleObject(event.value, 20);
        UINT32 size{}; check(capture->GetNextPacketSize(&size), "next_packet");
        while (size && !stop.stop_requested() && !paused_) {
            BYTE* data{}; UINT32 frames{}; DWORD packetFlags{}; UINT64 position{}, qpc{};
            check(capture->GetBuffer(&data, &frames, &packetFlags, &position, &qpc), "get_buffer");
            BufferLease lease{capture.get(), frames};
            if (!frames) { lease.release(); check(capture->GetNextPacketSize(&size), "next_packet"); continue; }
            const auto packetStart = clock_.packet_ms(qpc);
            lastPacket = clock_.now();
            if ((packetFlags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) || packetStart < 0 || packetStart < lastEnd - 1 || packetStart > lastPacket + 100 || lastPacket - packetStart > 2000) {
                const auto lost = static_cast<uint64_t>(frames) * 24000 / format.rate + assembly.size();
                gap(lastEnd, lastPacket, "unknown", lost); dropped_ += lost; anchor = -1; assembly.clear(); converter = Converter(format);
                health("disconnected", "invalid_or_stale_packet_timestamp");
            } else {
                const bool drift = anchor >= 0 && std::abs(packetStart - anchor - static_cast<int64_t>(sourceFrames * 1000 / format.rate)) > 20;
                if (anchor < 0 || drift || (packetFlags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY)) {
                    if (anchor >= 0) { gap(lastEnd, packetStart, "unknown", assembly.size()); dropped_ += assembly.size(); }
                    anchor = packetStart; emitted = 0; sourceFrames = 0; assembly.clear(); converter = Converter(format);
                }
                silent = (packetFlags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                auto pcm = converter.convert(data, frames, silent); peak = std::max(peak, pcm.peak);
                sourceFrames += frames;
                if (pcm.clipped) health("healthy", "samples_clipped", peak);
                assembly.insert(assembly.end(), pcm.samples.begin(), pcm.samples.end());
                while (assembly.size() >= 480) {
                    if (selection_.pid && WaitForSingleObject(process_.value, 0) != WAIT_TIMEOUT) throw Failure("source_terminated", HRESULT_FROM_WIN32(ERROR_PROCESS_ABORTED));
                    std::vector<int16_t> block(assembly.begin(), assembly.begin() + 480);
                    assembly.erase(assembly.begin(), assembly.begin() + 480);
                    const auto start = anchor + emitted; const auto end = start + 20;
                    JsonObject payload; str(payload, L"message_type", "audio_frame"); str(payload, L"stream_id", selection_.stream);
                    num(payload, L"sequence", static_cast<double>(sequence_++)); num(payload, L"captured_start_ms", static_cast<double>(start)); num(payload, L"captured_end_ms", static_cast<double>(end));
                    JsonObject normalized; num(normalized, L"sample_rate", 24000); num(normalized, L"channels", 1); num(normalized, L"bits_per_sample", 16);
                    payload.Insert(L"format", normalized); num(payload, L"source_sample_rate", format.rate); str(payload, L"pcm", base64(block));
                    emit_(payload, selection_.stream, start, end, 480); emitted += 20; lastEnd = end;
                }
            }
            lease.release(); check(capture->GetNextPacketSize(&size), "next_packet");
        }
        const auto now = clock_.now();
        if (now - lastPacket > 2000) { gap(lastEnd, now, "unknown"); throw Failure("capture_stalled", AUDCLNT_E_DEVICE_INVALIDATED); }
        if (now - lastHealth >= 250) {
            if (peak >= .0001) audibleObserved = true;
            health(peak >= .0001 ? "healthy" : (silent && audibleObserved ? "silence" : "zero_level"),
                peak >= .0001 ? "audio_detected" : "no_audible_signal_identity_unverified", peak);
            lastHealth = now; peak = 0;
        }
    }
    if (!assembly.empty()) gap(lastEnd, clock_.now(), "unknown", assembly.size());
}
JsonObject source_inventory() {
    auto enumerator = winrt::create_instance<IMMDeviceEnumerator>(__uuidof(MMDeviceEnumerator));
    winrt::com_ptr<IMMDeviceCollection> devices;
    check(enumerator->EnumAudioEndpoints(eCapture, DEVICE_STATE_ACTIVE, devices.put()), "enumerate_mics");
    UINT count{}; check(devices->GetCount(&count), "mic_count");
    winrt::Windows::Data::Json::JsonArray result;
    for (UINT i = 0; i < count; ++i) {
        winrt::com_ptr<IMMDevice> device; check(devices->Item(i, device.put()), "mic_item");
        LPWSTR id{}; check(device->GetId(&id), "mic_id");
        JsonObject item; item.Insert(L"device_id", JsonValue::CreateStringValue(id)); CoTaskMemFree(id);
        result.Append(item);
    }
    JsonObject output; output.Insert(L"microphones", result); return output;
}
}
