#pragma once

#include <algorithm>
#include <array>
#include <atomic>
#include <vector>

#include "SoundParams.h"

// The effects rack of the performance keyboard - the chain a modern
// keyboard/workstation has after its sound engine. It processes everything
// the performance keyboard plays (Sloj 1 and Sloj 2/3/4, whatever the
// instrument: Ultra FM, SoundFont or VST), but not the multi-track mixer or
// the auto-accompaniment, which keep their own sound.
//
// Fixed order, like on hardware:
//   Overdrive > Filter (with LFO and envelope = auto-wah) > 3-band EQ >
//   Compressor > Chorus > Flanger > Phaser > Tremolo > Auto-pan >
//   Rotary speaker > Delay > Reverb
//
// Every setting is one SoundParam id (see SoundParams.h). SetParam can be
// called from any thread; the audio thread picks the values up once per
// block, so changes are instant and lock-free.
class KeyboardFx
{
public:
    KeyboardFx();
    void Prepare(double sampleRate);
    void SetParam(int id, float value);
    float GetParam(int id) const;
    static float DefaultValue(int id);
    static bool IsFxParam(int id) { return id >= SoundParam::FxFirst && id <= SoundParam::FxLast; }

    // In place, interleaved stereo.
    void Process(float* interleavedStereo, int frames);

private:
    float P(int id) const { return params_[id - SoundParam::FxFirst].load(std::memory_order_relaxed); }

    struct Biquad
    {
        float b0 = 1, b1 = 0, b2 = 0, a1 = 0, a2 = 0;
        float z1[2] = {0, 0}, z2[2] = {0, 0};
        float Run(float x, int ch)
        {
            float y = b0 * x + z1[ch];
            z1[ch] = b1 * x - a1 * y + z2[ch];
            z2[ch] = b2 * x - a2 * y;
            return y;
        }
        void Clear() { z1[0] = z1[1] = z2[0] = z2[1] = 0; }
    };

    struct DelayLine
    {
        std::vector<float> buffer;
        int write = 0;
        void Init(int length) { buffer.assign(static_cast<size_t>(length > 4 ? length : 4), 0.0f); write = 0; }
        void Clear() { std::fill(buffer.begin(), buffer.end(), 0.0f); }
        void Push(float x)
        {
            buffer[static_cast<size_t>(write)] = x;
            if (++write >= static_cast<int>(buffer.size())) write = 0;
        }
        // Delay in samples (fractional), measured from the last pushed sample.
        float Read(float delay) const
        {
            int size = static_cast<int>(buffer.size());
            if (delay < 1.0f) delay = 1.0f;
            if (delay > size - 2) delay = static_cast<float>(size - 2);
            float pos = static_cast<float>(write) - delay;
            while (pos < 0) pos += static_cast<float>(size);
            int i0 = static_cast<int>(pos);
            float frac = pos - static_cast<float>(i0);
            int i1 = i0 + 1;
            if (i1 >= size) i1 -= size;
            return buffer[static_cast<size_t>(i0)] + (buffer[static_cast<size_t>(i1)] - buffer[static_cast<size_t>(i0)]) * frac;
        }
    };

    struct Comb
    {
        std::vector<float> buffer;
        int index = 0;
        float store = 0;
        float Run(float x, float feedback, float damp)
        {
            float y = buffer[static_cast<size_t>(index)];
            store = y * (1.0f - damp) + store * damp;
            buffer[static_cast<size_t>(index)] = x + store * feedback;
            if (++index >= static_cast<int>(buffer.size())) index = 0;
            return y;
        }
    };
    struct Allpass
    {
        std::vector<float> buffer;
        int index = 0;
        float Run(float x)
        {
            float b = buffer[static_cast<size_t>(index)];
            float y = -x + b;
            buffer[static_cast<size_t>(index)] = x + b * 0.5f;
            if (++index >= static_cast<int>(buffer.size())) index = 0;
            return y;
        }
    };

    void UpdateEq();
    void ClearState();
    static void Lowpass1(float& state, float x, float coef) { state += (x - state) * coef; }

    double sampleRate_ = 44100.0;
    std::array<std::atomic<float>, SoundParam::FxLast - SoundParam::FxFirst + 1> params_;
    std::array<float, SoundParam::FxLast - SoundParam::FxFirst + 1> seen_{}; // last values the audio thread used
    std::array<bool, 16> wasOn_{};

    // Drive
    float driveTone_[2] = {0, 0};
    // Filter (TPT state-variable)
    float svf1_[2] = {0, 0}, svf2_[2] = {0, 0};
    double filterLfoPhase_ = 0;
    float filterEnv_ = 0;
    // EQ
    Biquad eqLow_, eqMid_, eqHigh_;
    // Compressor
    float compEnv_ = 0, compGain_ = 1;
    // Chorus / flanger
    DelayLine chorus_[2], flanger_[2];
    double chorusPhase_ = 0, flangerPhase_ = 0;
    float flangerFb_[2] = {0, 0};
    // Phaser
    float phaserState_[2][6] = {};
    float phaserFb_[2] = {0, 0};
    double phaserPhase_ = 0;
    // Tremolo / auto-pan
    double tremoloPhase_ = 0, panPhase_ = 0;
    // Rotary speaker
    DelayLine rotHorn_, rotDrum_;
    float rotLow_ = 0;
    double hornPhase_ = 0, drumPhase_ = 0;
    float hornSpeed_ = 0.8f, drumSpeed_ = 0.67f;
    // Delay
    DelayLine delay_[2];
    float delayDamp_[2] = {0, 0};
    float delayTimeSmooth_ = -1;
    // Reverb (Freeverb)
    std::array<Comb, 8> combL_, combR_;
    std::array<Allpass, 4> apL_, apR_;
};
