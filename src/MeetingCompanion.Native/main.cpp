#include "capture.h"
#include <iostream>
#include <charconv>

namespace cintra {
namespace {
std::string text(const JsonObject& value, const wchar_t* key) { return winrt::to_string(value.GetNamedString(key)); }
int64_t integer(const JsonObject& value, const wchar_t* key, int64_t minimum, int64_t maximum) {
    const auto number = value.GetNamedNumber(key);
    if (!std::isfinite(number) || std::floor(number) != number || number < static_cast<double>(minimum) || number > static_cast<double>(maximum))
        throw std::runtime_error("invalid_integer");
    return static_cast<int64_t>(number);
}
void guid(const std::string& id) {
    GUID parsed{};
    if (id.size() != 36 || FAILED(CLSIDFromString(winrt::to_hstring("{" + id + "}").c_str(), &parsed)) || parsed == GUID_NULL)
        throw std::runtime_error("invalid_uuid");
}
Selection selection(const JsonObject& value, const std::string& expected) {
    Selection result; result.stream = text(value, L"stream_id"); result.display = text(value, L"display_name"); result.wire = value;
    if (result.stream != expected || result.display.empty() || result.display.size() > 1024)
        throw std::runtime_error("invalid_source");
    if (expected == "remote_app") {
        result.pid = static_cast<DWORD>(integer(value, L"process_id", 1, UINT32_MAX));
        if (!value.GetNamedBoolean(L"include_process_tree") || value.GetNamedValue(L"device_id").ValueType() != winrt::Windows::Data::Json::JsonValueType::Null)
            throw std::runtime_error("process_tree_required");
    } else {
        result.endpoint = value.GetNamedString(L"device_id");
        if (result.endpoint.empty() || result.endpoint.size() > 1024 || value.GetNamedBoolean(L"include_process_tree") ||
            value.GetNamedValue(L"process_id").ValueType() != winrt::Windows::Data::Json::JsonValueType::Null)
            throw std::runtime_error("pinned_microphone_required");
    }
    (void)value.GetNamedBoolean(L"unisolated_acknowledged");
    return result;
}
void transfer(HANDLE pipe, void* buffer, DWORD bytes, bool writing, std::atomic<bool>& abort, bool idle = false) {
    auto data = static_cast<uint8_t*>(buffer);
    while (bytes) {
        Handle event{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
        OVERLAPPED operation{}; operation.hEvent = event.value;
        DWORD done{};
        const BOOL completed = writing ? WriteFile(pipe, data, bytes, &done, &operation) : ReadFile(pipe, data, bytes, &done, &operation);
        if (!completed) {
            if (GetLastError() != ERROR_IO_PENDING) throw std::runtime_error("pipe_io_failed");
            const auto started = GetTickCount64();
            while (WaitForSingleObject(event.value, 10) == WAIT_TIMEOUT) {
                // Idle command reads are allowed; partial frames and writes have a deadline.
                if (abort || (!idle && GetTickCount64() - started > 2000)) {
                    CancelIoEx(pipe, &operation);
                    WaitForSingleObject(event.value, INFINITE);
                    throw std::runtime_error("pipe_io_cancelled_or_timeout");
                }
            }
            if (!GetOverlappedResult(pipe, &operation, &done, FALSE)) throw std::runtime_error("pipe_disconnected");
        }
        if (!done) throw std::runtime_error("pipe_eof");
        bytes -= done; data += done;
    }
}
JsonObject read(HANDLE pipe, std::atomic<bool>& abort) {
    uint32_t size{}; transfer(pipe, &size, 1, false, abort, true);
    transfer(pipe, reinterpret_cast<uint8_t*>(&size) + 1, 3, false, abort);
    if (size == 0 || size > 1048576) throw std::runtime_error("invalid_envelope_length");
    std::string json(size, '\0'); transfer(pipe, json.data(), size, false, abort);
    auto result = JsonObject::Parse(winrt::to_hstring(json));
    if (integer(result, L"schema_version", 1, 1) != 1) throw std::runtime_error("unsupported_version");
    guid(text(result, L"message_id")); guid(text(result, L"session_id")); return result;
}
std::string envelope(JsonObject payload, const std::string& session) {
    GUID id; winrt::check_hresult(CoCreateGuid(&id));
    wchar_t formatted[40]{}; StringFromGUID2(id, formatted, 40);
    std::wstring uuid(formatted + 1, 36);
    JsonObject result; num(result, L"schema_version", 1); str(result, L"message_id", winrt::to_string(uuid));
    str(result, L"session_id", session); result.Insert(L"payload", payload); return stringify(result);
}
}
int serve(const wchar_t* name, DWORD parent) {
    if (std::wstring(name).find(L"cintra-audio-") != 0 || !parent) throw std::runtime_error("invalid_pipe_arguments");
    const std::wstring path = L"\\\\.\\pipe\\" + std::wstring(name);
    Handle pipe{CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr)};
    if (pipe.value == INVALID_HANDLE_VALUE) throw std::runtime_error("pipe_connect_failed");
    ULONG server{};
    if (!GetNamedPipeServerProcessId(pipe.value, &server) || server != parent) throw std::runtime_error("pipe_server_identity_mismatch");
    Handle owner{OpenProcess(SYNCHRONIZE, FALSE, parent)};
    if (!owner.value) throw std::runtime_error("host_unavailable");
    std::atomic<bool> abort{false};
    const auto command = read(pipe.value, abort);
    const auto session = text(command, L"session_id");
    const auto payload = command.GetNamedObject(L"payload");
    if (text(payload, L"message_type") != "start_capture") throw std::runtime_error("start_required");
    auto sources = payload.GetNamedObject(L"selection");
    auto mic = selection(sources.GetNamedObject(L"microphone"), "local_mic");
    auto remote = selection(sources.GetNamedObject(L"incoming"), "remote_app");
    auto mapping = payload.GetNamedObject(L"clock");
    Clock clock{integer(mapping, L"monotonic_origin_ticks", 0, 9007199254740991LL), integer(mapping, L"ticks_per_second", 1, 1000000000)};
    LARGE_INTEGER frequency; QueryPerformanceFrequency(&frequency);
    if (clock.frequency != frequency.QuadPart || clock.now() > 60000) throw std::runtime_error("incompatible_qpc_mapping");
    (void)mapping.GetNamedString(L"utc_origin");
    Outbox outbox;
    std::atomic<uint64_t> micDropped{}, remoteDropped{};
    auto emit = [&](JsonObject message, const std::string& stream, int64_t start, int64_t end, uint32_t samples) {
        auto& totalDropped = stream == "local_mic" ? micDropped : remoteDropped;
        if (text(message, L"message_type") == "capture_health") {
            num(message, L"dropped_frames", message.GetNamedNumber(L"dropped_frames") + static_cast<double>(totalDropped.load()));
            const auto oldest = outbox.oldest_start(stream);
            num(message, L"oldest_frame_age_ms", oldest ? static_cast<double>(std::max<int64_t>(0, clock.now() - *oldest)) : 0.);
        }
        std::vector<QueuedMessage> dropped;
        if (!outbox.push({envelope(message, session), stream, start, end, samples}, dropped)) { abort = true; return; }
        if (!dropped.empty()) {
            uint64_t count{}; int64_t first = dropped.front().start, last = dropped.back().end;
            for (const auto& item : dropped) count += item.samples;
            totalDropped += count;
            JsonObject gap; str(gap, L"message_type", "capture_gap"); str(gap, L"stream_id", stream);
            str(gap, L"reason", "queue_overflow"); num(gap, L"captured_start_ms", static_cast<double>(first));
            num(gap, L"captured_end_ms", static_cast<double>(last)); num(gap, L"dropped_frames", static_cast<double>(count));
            std::vector<QueuedMessage> unused;
            if (!outbox.push({envelope(gap, session), stream, 0, 0, 0}, unused)) abort = true;
        }
    };
    Capture microphone(std::move(mic), clock, emit), incoming(std::move(remote), clock, emit);
    std::atomic<bool> stopping{false};
    std::jthread reader([&](std::stop_token) {
        try {
            winrt::init_apartment(winrt::apartment_type::multi_threaded);
            struct Apartment { ~Apartment() { winrt::uninit_apartment(); } } apartment;
            while (!abort && !stopping) {
                auto control = read(pipe.value, abort);
                if (text(control, L"session_id") != session) throw std::runtime_error("session_mismatch");
                auto body = control.GetNamedObject(L"payload");
                if (text(body, L"message_type") != "capture_control") throw std::runtime_error("unexpected_command");
                const auto action = text(body, L"action");
                if (action == "stop") { stopping = true; break; }
                if (action != "pause" && action != "resume") throw std::runtime_error("invalid_control");
                microphone.pause(action == "pause"); incoming.pause(action == "pause");
            }
        } catch (...) { abort = true; }
    });
    try {
        while (!abort && !stopping) {
            if (WaitForSingleObject(owner.value, 0) != WAIT_TIMEOUT) { abort = true; break; }
            QueuedMessage message;
            if (outbox.pop(message)) {
                if (message.samples && clock.now() - message.start > 2000) {
                    (message.stream == "local_mic" ? microphone : incoming).drop(message.samples);
                    (message.stream == "local_mic" ? microphone : incoming).gap(message.start, message.end, "queue_overflow", message.samples);
                    continue;
                }
                auto size = static_cast<uint32_t>(message.json.size());
                transfer(pipe.value, &size, 4, true, abort); transfer(pipe.value, message.json.data(), size, true, abort);
            } else Sleep(2);
        }
        // Flush old queued audio before stopping workers. Final acknowledgements are metadata only.
        outbox.clear(); microphone.stop(); incoming.stop();
        QueuedMessage message;
        while (!abort && outbox.pop(message)) {
            if (message.samples) continue;
            auto size = static_cast<uint32_t>(message.json.size());
            transfer(pipe.value, &size, 4, true, abort); transfer(pipe.value, message.json.data(), size, true, abort);
        }
    } catch (...) { abort = true; }
    abort = true; reader.join(); microphone.stop(); incoming.stop();
    return stopping ? 0 : 2;
}
}
int wmain(int argc, wchar_t** argv) {
    try {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
        struct Apartment { ~Apartment() { winrt::uninit_apartment(); } } apartment;
        if (argc == 2 && std::wstring(argv[1]) == L"--inventory") { std::cout << cintra::stringify(cintra::source_inventory()) << '\n'; return 0; }
        if (argc != 4 || std::wstring(argv[1]) != L"--pipe") throw std::runtime_error("usage: --inventory | --pipe name host_pid");
        wchar_t* end{}; const auto pid = wcstoul(argv[3], &end, 10);
        if (!end || *end || !pid) throw std::runtime_error("invalid_host_pid");
        return cintra::serve(argv[2], pid);
    } catch (const winrt::hresult_error& failure) { std::cerr << "native_error HRESULT=0x" << std::hex << static_cast<uint32_t>(failure.code()) << '\n'; return 2; }
    catch (const std::exception& failure) { std::cerr << failure.what() << '\n'; return 2; }
}
