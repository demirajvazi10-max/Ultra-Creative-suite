#include "DrumSynth.h"

#include <cmath>
#include <cstring>

namespace
{
    constexpr double kTwoPi = 6.283185307179586;

    // One fixed "recipe" per percussion voice: how long it lasts, whether
    // it has a pitched component (a sine sweep, for the kick's thump) and
    // how much of that to mix against filtered noise, and whether the
    // noise gets a one-pole highpass (which is what makes a hi-hat/clap/
    // crash sound bright and "cymbal-like" instead of a dull thud).
    struct DrumProfile
    {
        double durationSeconds;
        double toneStartHz;  // 0 = no tonal sweep at all (pure noise voices)
        double toneEndHz;
        double toneMix;      // 0..1, tone vs. noise
        double highpass;     // 0 = unfiltered noise, else one-pole highpass coefficient (0..1)
        double decayRate;    // how fast the exponential envelope falls (larger = snappier)
    };

    const DrumProfile& ProfileFor(DrumVoiceType type)
    {
        static constexpr DrumProfile kProfiles[kDrumVoiceTypeCount] = {
            /* Kick      */ {0.18, 150.0, 45.0,  1.00, 0.0,  6.0},
            /* Snare     */ {0.15, 200.0, 150.0, 0.35, 0.0,  7.0},
            /* ClosedHat */ {0.05, 0.0,   0.0,   0.0,  0.90, 10.0},
            /* OpenHat   */ {0.28, 0.0,   0.0,   0.0,  0.90, 5.0},
            /* Clap      */ {0.14, 0.0,   0.0,   0.0,  0.80, 8.0},
            /* Crash     */ {0.90, 0.0,   0.0,   0.0,  0.85, 2.5},
            /* Tom       */ {0.35, 220.0, 90.0,  1.00, 0.0,  4.0},
            /* DumbekDum */ {0.30, 115.0, 55.0,  0.85, 0.30, 4.0},
            /* DumbekTek */ {0.10, 320.0, 260.0, 0.25, 0.75, 9.0},
        };
        return kProfiles[static_cast<int>(type)];
    }
}

void DrumHitVoice::Trigger(DrumVoiceType voiceType, float noteVelocity, double sampleRate)
{
    type = voiceType;
    active = true;
    ageSeconds = 0.0;
    phase = 0.0;
    velocity = noteVelocity;
    noiseFilterPrevIn = 0.0f;
    noiseFilterPrevOut = 0.0f;
    const DrumProfile& profile = ProfileFor(voiceType);
    phaseIncrement = sampleRate > 0.0 ? (kTwoPi * profile.toneStartHz / sampleRate) : 0.0;
    // Re-seed rather than reset to a fixed value, so back-to-back hits of
    // the same voice (e.g. a closed hat on every 8th note) don't all sound
    // identically "clicky" from reusing the exact same noise sequence.
    noiseState = noiseState * 2654435761u + 1u;
    if (noiseState == 0)
    {
        noiseState = 1;
    }
}

float DrumHitVoice::NextNoise()
{
    // xorshift32 - fast, dependency-free, plenty random-sounding for
    // percussion noise (no cryptographic requirement here).
    noiseState ^= noiseState << 13;
    noiseState ^= noiseState >> 17;
    noiseState ^= noiseState << 5;
    return (static_cast<float>(noiseState) / 2147483648.0f) - 1.0f;
}

void DrumHitVoice::Render(float* output, int frames, double sampleRate)
{
    if (!active || sampleRate <= 0.0)
    {
        return;
    }

    const DrumProfile& profile = ProfileFor(type);

    for (int i = 0; i < frames; ++i)
    {
        if (ageSeconds >= profile.durationSeconds)
        {
            active = false;
            break;
        }

        double t = ageSeconds / profile.durationSeconds; // 0..1 through this hit's life
        float envelope = static_cast<float>(std::exp(-t * profile.decayRate));

        float toneSample = 0.0f;
        if (profile.toneMix > 0.0)
        {
            double freq = profile.toneStartHz + (profile.toneEndHz - profile.toneStartHz) * t;
            phaseIncrement = kTwoPi * freq / sampleRate;
            toneSample = static_cast<float>(std::sin(phase));
            phase += phaseIncrement;
            if (phase >= kTwoPi)
            {
                phase -= kTwoPi;
            }
        }

        float noiseSample = NextNoise();
        if (profile.highpass > 0.0)
        {
            float filtered = noiseSample - noiseFilterPrevIn + static_cast<float>(profile.highpass) * noiseFilterPrevOut;
            noiseFilterPrevIn = noiseSample;
            noiseFilterPrevOut = filtered;
            noiseSample = filtered;
        }

        float mixed = static_cast<float>(profile.toneMix) * toneSample
                    + static_cast<float>(1.0 - profile.toneMix) * noiseSample;

        output[i] += mixed * envelope * velocity * 0.9f;

        ageSeconds += 1.0 / sampleRate;
    }
}

void DrumSynth::Prepare(double sampleRate)
{
    std::lock_guard<std::mutex> lock(mutex_);
    sampleRate_ = sampleRate;
    for (auto& voice : voices_)
    {
        voice = DrumHitVoice{};
    }
}

void DrumSynth::Trigger(DrumVoiceType voiceType, float velocity)
{
    std::lock_guard<std::mutex> lock(mutex_);
    for (auto& voice : voices_)
    {
        if (!voice.active)
        {
            voice.Trigger(voiceType, velocity, sampleRate_);
            return;
        }
    }

    // All voices busy: steal one, round-robin - matches Synth's convention.
    DrumHitVoice& stolen = voices_[nextVoiceIndex_];
    nextVoiceIndex_ = (nextVoiceIndex_ + 1) % kMaxVoices;
    stolen.Trigger(voiceType, velocity, sampleRate_);
}

void DrumSynth::Render(float* monoBuffer, int frames)
{
    std::lock_guard<std::mutex> lock(mutex_);
    std::memset(monoBuffer, 0, sizeof(float) * static_cast<size_t>(frames));
    for (auto& voice : voices_)
    {
        if (voice.active)
        {
            voice.Render(monoBuffer, frames, sampleRate_);
        }
    }
}
