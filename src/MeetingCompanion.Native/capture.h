#pragma once
#include "audio.h"
#include <windows.h>
#include <unknwn.h>
#include <objbase.h>
#include <winrt/base.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Data.Json.h>
#include <atomic>
#include <memory>
#include <thread>

namespace cintra {
using winrt::Windows::Data::Json::JsonObject;
using winrt::Windows::Data::Json::JsonValue;
inline std::string stringify(const JsonObject& value) { return winrt::to_string(value.Stringify()); }
inline void str(JsonObject& object, const wchar_t* key, const std::string& value) { object.Insert(key, JsonValue::CreateStringValue(winrt::to_hstring(value))); }
inline void num(JsonObject& object, const wchar_t* key, double value) { object.Insert(key, JsonValue::CreateNumberValue(value)); }
struct Handle {
    HANDLE value{};
    explicit Handle(HANDLE handle = nullptr) : value(handle) {}
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
};
struct Selection { std::string stream, display; std::wstring endpoint; DWORD pid{}; JsonObject wire{nullptr}; };
struct Clock { int64_t origin{}, frequency{}; int64_t now() const; int64_t packet_ms(uint64_t qpc100ns) const; };
using Emit = std::function<void(JsonObject, const std::string&, int64_t, int64_t, uint32_t)>;
class Capture {
    Selection selection_;
    Clock clock_;
    Emit emit_;
    std::jthread worker_;
    std::atomic<bool> paused_{false};
    std::atomic<uint64_t> dropped_{0};
    Handle process_;
    uint64_t sequence_{};
    DWORD processError_{};
    void run(std::stop_token stop);
    void attempt(std::stop_token stop);
public:
    Capture(Selection selection, Clock clock, Emit emit);
    ~Capture();
    void stop();
    void pause(bool pause);
    void drop(uint64_t count) { dropped_ += count; }
    void health(const std::string& status, const std::string& diagnostic, double peak = 0);
    void gap(int64_t start, int64_t end, const std::string& reason, uint64_t dropped = 0);
};
JsonObject source_inventory();
}
