#include "DrumSynth.h"

#include <cmath>
#include <cstring>

namespace
{
    constexpr double kTwoPi = 6.283185307179586;

    // One fixed "recipe" per percussion voice: how long it lasts, whether
    // it has a pitched component (a sine sweep, for the kick's thump) and
    // how much of that to mix against filtered noise, whether that noise
    // is plain white noise or the "metallic" six-oscillator mix (see
    // below), how it's band-limited (highpass then lowpass - together a
    // proper bandpass rather than just "bright/thin"), how fast its main
    // envelope decays, and an optional short broadband "click" transient
    // layered at the very onset for the mechanical attack a real drum has
    // (a beater/stick/hand's first contact) that a plain tone-or-noise
    // envelope alone doesn't capture.
    struct DrumProfile
    {
        double durationSeconds;
        double toneStartHz;      // 0 = no tonal sweep at all (pure noise/metallic voices)
        double toneEndHz;
        double toneMix;          // 0..1, tone vs. noise/metallic
        double highpass;         // 0 = unfiltered, else one-pole highpass coefficient (0..1)
        double lowpass;          // 0 = skip this stage, else one-pole lowpass coefficient (0..1) applied after the highpass
        double decayRate;        // how fast the main exponential envelope falls (larger = snappier)
        bool metallic;           // true = six inharmonic square oscillators (classic analog drum-machine cymbal/hi-hat technique) instead of plain white noise, before the same highpass/lowpass filtering
        double clickAmount;      // 0..1, extra broadband burst layered at the very onset, independent of the tone/noise mix above
        double clickDecayPerSec; // how fast that click burst itself fades - tied to real elapsed time, not to durationSeconds, so it stays a short "tick" regardless of the voice's overall length
    };

    // Six inharmonic oscillator frequencies (Hz) approximating the classic
    // analog drum-machine hi-hat/cymbal circuit (several square-wave
    // oscillators at non-integer-related ratios, summed then filtered) -
    // this is what gives a synthesized hat/crash an actual metallic
    // "ring" instead of the flatter hiss plain filtered white noise gives.
    constexpr double kMetallicFreqsHz[6] = {203.0, 304.4, 369.6, 522.7, 540.9, 800.0};

    const DrumProfile& ProfileFor(DrumVoiceType type)
    {
        static constexpr DrumProfile kProfiles[kDrumVoiceTypeCount] = {
            /* Kick      */ {0.20, 150.0, 42.0,  1.00, 0.0,  0.0,  6.0,  false, 0.55, 220.0},
            /* Snare     */ {0.17, 190.0, 130.0, 0.30, 0.55, 0.35, 7.5,  false, 0.35, 260.0},
            /* ClosedHat */ {0.06, 0.0,   0.0,   0.0,  0.85, 0.55, 11.0, true,  0.0,  0.0},
            /* OpenHat   */ {0.30, 0.0,   0.0,   0.0,  0.85, 0.55, 5.5,  true,  0.0,  0.0},
            /* Clap      */ {0.30, 0.0,   0.0,   0.0,  0.75, 0.45, 12.0, false, 0.0,  0.0}, // envelope shaped specially - see Render()
            /* Crash     */ {0.95, 0.0,   0.0,   0.0,  0.80, 0.65, 2.2,  true,  0.0,  0.0},
            /* Tom       */ {0.35, 220.0, 90.0,  1.00, 0.0,  0.0,  4.0,  false, 0.0,  0.0},
            /* DumbekDum */ {0.30, 115.0, 55.0,  0.85, 0.30, 0.0,  4.0,  false, 0.0,  0.0},
            /* DumbekTek */ {0.10, 320.0, 260.0, 0.25, 0.70, 0.30, 9.0,  false, 0.15, 300.0},
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
    lowpassPrevOut = 0.0f;
    for (double& p : metallicPhase)
    {
        p = 0.0;
    }
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

        float envelope;
        if (type == DrumVoiceType::Clap)
        {
            // A real hand-clap is several hands (or fingers) not landing in
            // perfect unison - a handful of very quick noise bursts in the
            // first ~25ms, then one longer decaying "wash" - rather than
            // one plain exponential decay, which is what was reading as a
            // flat, synthetic "plastic" clap before.
            static constexpr double kTapTimesSeconds[3] = {0.0, 0.011, 0.022};
            static constexpr double kTapDecayPerSec = 55.0;
            static constexpr double kTailStartSeconds = 0.03;
            static constexpr double kTailDecayPerSec = 12.0;
            float burstEnvelope = 0.0f;
            for (double tapTime : kTapTimesSeconds)
            {
                double dt = ageSeconds - tapTime;
                if (dt >= 0.0)
                {
                    burstEnvelope += static_cast<float>(std::exp(-dt * kTapDecayPerSec));
                }
            }
            if (ageSeconds >= kTailStartSeconds)
            {
                burstEnvelope += static_cast<float>(std::exp(-(ageSeconds - kTailStartSeconds) * kTailDecayPerSec) * 0.8);
            }
            envelope = burstEnvelope;
        }
        else
        {
            envelope = static_cast<float>(std::exp(-t * profile.decayRate));
        }

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

        float noiseSample;
        if (profile.metallic)
        {
            // Six square-wave oscillators at inharmonic ratios, summed and
            // averaged - a bright metallic mix in place of NextNoise()'s
            // plain hiss, before going through the same highpass/lowpass
            // banding below. This is what actually gives the hats/crash a
            // cymbal-like "ring" instead of sounding like filtered static.
            float metallicSum = 0.0f;
            for (int osc = 0; osc < 6; ++osc)
            {
                metallicPhase[osc] += kTwoPi * kMetallicFreqsHz[osc] / sampleRate;
                if (metallicPhase[osc] >= kTwoPi)
                {
                    metallicPhase[osc] -= kTwoPi;
                }
                metallicSum += (metallicPhase[osc] < 3.14159265358979 ? 1.0f : -1.0f);
            }
            noiseSample = metallicSum / 6.0f;
        }
        else
        {
            noiseSample = NextNoise();
        }

        if (profile.highpass > 0.0)
        {
            float filtered = noiseSample - noiseFilterPrevIn + static_cast<float>(profile.highpass) * noiseFilterPrevOut;
            noiseFilterPrevIn = noiseSample;
            noiseFilterPrevOut = filtered;
            noiseSample = filtered;
        }
        if (profile.lowpass > 0.0)
        {
            // Second, one-pole low-pass stage after the highpass above -
            // together they band-limit the signal (a proper "bandpass")
            // instead of leaving it just bright/thin, which is closer to
            // how a real cymbal/snare-wire/clap noise band actually sits.
            lowpassPrevOut += static_cast<float>(profile.lowpass) * (noiseSample - lowpassPrevOut);
            noiseSample = lowpassPrevOut;
        }

        float mixed = static_cast<float>(profile.toneMix) * toneSample
                    + static_cast<float>(1.0 - profile.toneMix) * noiseSample;

        if (profile.clickAmount > 0.0)
        {
            // A short, unfiltered broadband burst right at the onset - the
            // mechanical "tick" of a beater/stick/hand making contact,
            // which a pitched sweep or filtered noise body alone can't
            // capture and which is a big part of what makes a procedural
            // kick/snare/clap read as "plastic" without it.
            float clickEnvelope = static_cast<float>(std::exp(-ageSeconds * profile.clickDecayPerSec));
            mixed += static_cast<float>(profile.clickAmount) * NextNoise() * clickEnvelope;
        }

        output[i] += mixed * envelope * velocity * 0.85f;

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

    // A gentle soft-saturation across the whole kit, the same idea as
    // AudioEngine::PaCallback's final tanh soft-clip on the overall mix
    // (see its own comment) but applied here as this kit's own "bus glue" -
    // several percussion voices landing on the same beat summing into a
    // few separate, individually-clean transients read as disconnected
    // "each hit on its own" rather than one kit; a light saturation gently
    // rounds off simultaneous peaks together, which is the same reason a
    // real drum bus is almost always compressed/saturated a little.
    for (int i = 0; i < frames; ++i)
    {
        monoBuffer[i] = std::tanh(monoBuffer[i] * 1.15f) * 0.9f;
    }
}
