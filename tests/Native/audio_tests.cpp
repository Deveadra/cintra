#include "audio.h"
#include <iostream>

void require(bool condition, const char* message) { if (!condition) throw std::runtime_error(message); }
int main() {
    try {
        using namespace cintra;
        for (const uint32_t rate : {16000u, 24000u, 44100u, 48000u, 96000u}) {
            Converter converter({rate, 2, 16, 16, 4, false});
            std::vector<int16_t> samples(rate * 2, 8192);
            auto pcm = converter.convert(reinterpret_cast<const uint8_t*>(samples.data()), rate, false);
            require(pcm.samples.size() >= 23975 && pcm.samples.size() <= 24000, "resample duration");
            require(std::abs(pcm.samples[1000] - 8192) < 3, "downmix scaling");
        }
        std::vector<float> floating(4800, .5f);
        floating[1000] = std::numeric_limits<float>::quiet_NaN();
        Converter floatConverter({48000, 1, 32, 32, 4, true});
        auto converted = floatConverter.convert(reinterpret_cast<const uint8_t*>(floating.data()), 4800, false);
        require(std::abs(converted.samples[1500] - 16384) < 3, "float conversion");
        Converter silence({48000, 2, 24, 24, 6, false});
        auto quiet = silence.convert(nullptr, 4800, true);
        require(std::all_of(quiet.samples.begin(), quiet.samples.end(), [](auto x) { return x == 0; }), "silent null buffer");
        std::vector<uint8_t> packed(4800 * 6);
        for (size_t i = 0; i < packed.size(); i += 3) packed[i + 2] = 0xc0;
        auto negative = silence.convert(packed.data(), 4800, false);
        require(std::abs(negative.samples[1000] + 16384) < 5, "packed PCM24");
        // Packet partitioning must not alter phase, retained filter history or output.
        Converter whole({44100, 1, 16, 16, 2, false}), split({44100, 1, 16, 16, 2, false});
        std::vector<int16_t> wave(4410);
        for (size_t i = 0; i < wave.size(); ++i) wave[i] = static_cast<int16_t>(10000 * std::sin(i * .1));
        auto expected = whole.convert(reinterpret_cast<const uint8_t*>(wave.data()), 4410, false);
        std::vector<int16_t> actual;
        for (size_t i = 0; i < wave.size(); i += 147) {
            auto part = split.convert(reinterpret_cast<const uint8_t*>(wave.data() + i), 147, false);
            actual.insert(actual.end(), part.samples.begin(), part.samples.end());
        }
        require(expected.samples == actual, "streaming phase invariant");
        Outbox outbox; std::vector<QueuedMessage> dropped;
        for (int i = 0; i < 101; ++i) {
            require(outbox.push({"mic", "local_mic", i * 20, i * 20 + 20, 480}, dropped), "mic queue");
            require(outbox.push({"remote", "remote_app", i * 20, i * 20 + 20, 480}, dropped), "remote queue");
        }
        require(dropped.size() == 2 && dropped[0].stream != dropped[1].stream, "independent overflow");
        outbox.clear(); QueuedMessage message; require(!outbox.pop(message), "clear releases buffers");
        bool rejected = false; try { Converter bad({48000, 0, 16, 16, 0, false}); } catch (...) { rejected = true; }
        require(rejected, "invalid format");
        require(sample_ms(480) == 20, "normalized clock");
        RecoveryBudget budget;
        require(budget.next_delay(true) == 250u && budget.next_delay(true) == 500u && !budget.next_delay(true), "bounded recovery");
        budget.reset(); require(!budget.next_delay(false), "terminal error recovery");
        Converter antiAlias({48000, 1, 32, 32, 4, true});
        std::vector<float> highTone(4800);
        for (size_t i = 0; i < highTone.size(); ++i) highTone[i] = static_cast<float>(.5 * std::sin(2 * 3.141592653589793 * 20000 * static_cast<double>(i) / 48000));
        auto filtered = antiAlias.convert(reinterpret_cast<const uint8_t*>(highTone.data()), 4800, false);
        double energy = 0;
        for (size_t i = 100; i < filtered.samples.size(); ++i) energy += std::pow(filtered.samples[i] / 32768., 2);
        require(std::sqrt(energy / static_cast<double>(filtered.samples.size() - 100)) < .01, "anti-alias rejection");
        std::cout << "PASS native conversion, partitioning, queue identity/overflow, invalid format, cleanup\n"; return 0;
    } catch (const std::exception& e) { std::cerr << "FAIL " << e.what() << '\n'; return 1; }
}
