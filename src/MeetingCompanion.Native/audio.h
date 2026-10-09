#pragma once
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <deque>
#include <functional>
#include <limits>
#include <mutex>
#include <optional>
#include <stdexcept>
#include <string>
#include <vector>

namespace cintra {
struct Format {
    uint32_t rate{};
    uint16_t channels{}, bits{}, validBits{}, align{};
    bool floating{};
    void validate() const {
        if (rate < 8000 || rate > 192000 || channels == 0 || channels > 32 ||
            (bits != 16 && bits != 24 && bits != 32) || validBits == 0 || validBits > bits ||
            align != channels * (bits / 8) || (floating && (bits != 32 || validBits != 32)))
            throw std::runtime_error("unsupported_sample_format");
    }
};
struct Pcm {
    std::vector<int16_t> samples;
    double peak{};
    uint64_t clipped{};
};
// Streaming rational resampler: symmetric 32-tap windowed sinc; 16 input-frame
// lookahead, retained between packets. Downmix before filtering, then quantize.
// Output timestamps describe the input signal, compensating filter lookahead.
class Converter {
    Format format_;
    std::deque<double> input_;
    int64_t base_{-16}, total_{};
    uint64_t output_{};
public:
    explicit Converter(Format format) : format_(format), input_(16, 0.) { format.validate(); }
    uint64_t output_count() const { return output_; }
    Pcm convert(const uint8_t* data, uint32_t frames, bool silent) {
        if (frames > format_.rate * 2 || (!silent && data == nullptr))
            throw std::runtime_error("invalid_packet_size");
        for (uint32_t i = 0; i < frames; ++i) {
            double mono = 0;
            for (uint16_t c = 0; c < format_.channels && !silent; ++c) {
                const auto p = data + static_cast<size_t>(i) * format_.align + c * (format_.bits / 8);
                double value;
                if (format_.floating) {
                    float f; std::memcpy(&f, p, 4); value = std::isfinite(f) ? f : 0.;
                } else {
                    int32_t sample{};
                    if (format_.bits == 16) { int16_t s; std::memcpy(&s, p, 2); sample = s; }
                    else if (format_.bits == 24) {
                        sample = p[0] | (p[1] << 8) | (p[2] << 16);
                        if (sample & 0x800000) sample |= static_cast<int32_t>(0xff000000u);
                    } else std::memcpy(&sample, p, 4);
                    // Extensible PCM valid bits are left-aligned in their container.
                    value = std::ldexp(static_cast<double>(sample), 1 - format_.bits);
                }
                mono += value / format_.channels;
            }
            input_.push_back(mono);
        }
        total_ += frames;
        Pcm result;
        const double cutoff = std::min(1., 24000. / format_.rate) * .94;
        while (true) {
            const double position = static_cast<double>(output_) * format_.rate / 24000.;
            const auto center = static_cast<int64_t>(std::floor(position));
            if (center + 16 >= total_) break;
            double sum = 0, weight = 0;
            for (int tap = -15; tap <= 16; ++tap) {
                const double distance = position - static_cast<double>(center + tap);
                const double x = distance * cutoff;
                const double sinc = std::abs(x) < 1e-12 ? 1. : std::sin(3.141592653589793 * x) / (3.141592653589793 * x);
                const double window = .5 + .5 * std::cos(3.141592653589793 * distance / 17.);
                const double w = sinc * window * cutoff;
                sum += input_.at(static_cast<size_t>(center + tap - base_)) * w;
                weight += w;
            }
            const double value = sum / weight;
            result.peak = std::max(result.peak, std::min(1., std::abs(value)));
            if (std::abs(value) > 1.) ++result.clipped;
            const auto quantized = std::lround(std::clamp(value, -1., 1.) * 32768.);
            result.samples.push_back(static_cast<int16_t>(std::clamp(quantized, -32768L, 32767L)));
            ++output_;
        }
        const auto retain = static_cast<int64_t>(static_cast<double>(output_) * format_.rate / 24000.) - 16;
        while (base_ < retain && !input_.empty()) { input_.pop_front(); ++base_; }
        return result;
    }
};

struct QueuedMessage {
    std::string json;
    std::string stream;
    int64_t start{}, end{};
    uint32_t samples{};
};
class RecoveryBudget {
    unsigned failures_{};
public:
    std::optional<unsigned> next_delay(bool retryable) {
        if (!retryable || ++failures_ >= 3) return std::nullopt;
        return 250u << (failures_ - 1);
    }
    void reset() { failures_ = 0; }
};
// Audio never blocks a capture thread. Control metadata is separately bounded;
// metadata overflow fails the transport instead of silently losing diagnostics.
class Outbox {
    std::mutex mutex_;
    std::deque<QueuedMessage> messages_;
    uint32_t micSamples_{}, remoteSamples_{};
public:
    bool push(QueuedMessage message, std::vector<QueuedMessage>& dropped) {
        std::lock_guard lock(mutex_);
        auto& count = message.stream == "local_mic" ? micSamples_ : remoteSamples_;
        if (message.samples > 48000) throw std::runtime_error("oversized_audio_frame");
        if (message.samples != 0) {
            for (auto i = messages_.begin(); count + message.samples > 48000 && i != messages_.end();) {
                if (i->samples && i->stream == message.stream) {
                    count -= i->samples; dropped.push_back(std::move(*i)); i = messages_.erase(i);
                } else ++i;
            }
        }
        if (messages_.size() >= 512) return false;
        count += message.samples;
        messages_.push_back(std::move(message)); return true;
    }
    bool pop(QueuedMessage& result) {
        std::lock_guard lock(mutex_);
        if (messages_.empty()) return false;
        result = std::move(messages_.front()); messages_.pop_front();
        (result.stream == "local_mic" ? micSamples_ : remoteSamples_) -= result.samples;
        return true;
    }
    void clear() {
        std::lock_guard lock(mutex_); messages_.clear(); micSamples_ = remoteSamples_ = 0;
    }
    std::optional<int64_t> oldest_start(const std::string& stream) {
        std::lock_guard lock(mutex_);
        for (const auto& message : messages_) if (message.samples && message.stream == stream) return message.start;
        return std::nullopt;
    }
};
inline int64_t sample_ms(uint64_t samples) { return static_cast<int64_t>(samples / 24); }
}
