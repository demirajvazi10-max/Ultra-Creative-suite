#pragma once

#include <array>
#include <cstdint>
#include <mutex>

// The fixed set of percussion sounds every built-in style, and the custom
// rhythms built via the UI's "Sopstveni ritam" editor, are written against.
// Deliberately a small, general set rather than a full GM drum kit - enough
// to make a convincing pop/rock/latin/waltz backing pattern (see Style.cpp)
// without the complexity, or the "every loaded SoundFont bank must also
// include a proper drum kit" dependency, a full kit would need. Tom is a
// plain pitched tom-tom hit, close enough to stand in for a timpani-style
// accent when building up a custom rhythm. DumbekDum/DumbekTek are the two
// core darbuka/dumbek strokes used by Balkan/Turkish/Arabic "oriental" hand-
// drum patterns (see the "Orijentalni" built-in style and Maqsum-style
// custom rhythms) - Dum a low, resonant open-palm-center hit, Tek a higher,
// crisp fingertip-edge hit; distinct enough from Kick/Snare/Clap to give
// that style its own voice instead of borrowing the western kit sounds.
enum class DrumVoiceType
{
    Kick = 0,
    Snare = 1,
    ClosedHat = 2,
    OpenHat = 3,
    Clap = 4,
    Crash = 5,
    Tom = 6,
    DumbekDum = 7,
    DumbekTek = 8
};

constexpr int kDrumVoiceTypeCount = 9;

// One playing percussion hit: procedurally synthesized (a pitched sine
// sweep for the kick's "thump", filtered noise for everything else),
// never loaded from a sample - deliberate, matching this project's
// existing preference for self-contained, dependency-free audio (see
// WavWriter's reasoning in README.md), and it sidesteps needing every
// loaded SoundFont bank to also carry a usable drum kit.
//
// IMPORTANT: Render() runs on the real-time audio thread, same rule as
// Voice::Render() - no allocation, no locking, no blocking.
struct DrumHitVoice
{
    bool active = false;
    DrumVoiceType type = DrumVoiceType::Kick;
    double phase = 0.0;
    double phaseIncrement = 0.0;
    double ageSeconds = 0.0;
    float velocity = 0.0f;
    uint32_t noiseState = 1;
    // One-pole highpass filter state for the noise-based voices (hats/
    // clap/crash) - kept as struct members (not Render() locals) so the
    // filter stays continuous across the many separate Render() calls one
    // long hit (e.g. a crash) spans, instead of clicking at every audio
    // callback block boundary.
    float noiseFilterPrevIn = 0.0f;
    float noiseFilterPrevOut = 0.0f;

    // (Re)starts this voice playing the given percussion sound. There is
    // deliberately no NoteOff - real percussion doesn't sustain on a
    // key-up either, so every trigger just plays out its own fixed
    // envelope to silence.
    void Trigger(DrumVoiceType voiceType, float noteVelocity, double sampleRate);

    // Adds up to 'frames' samples of this voice's remaining life into
    // 'output' (mono, additive - caller clears the buffer first).
    void Render(float* output, int frames, double sampleRate);

private:
    float NextNoise();
};

// A small fixed pool of one-shot percussion voices (no heap allocation
// after Prepare()), mirroring Synth's voice-pool design.
class DrumSynth
{
public:
    static constexpr int kMaxVoices = 12;

    void Prepare(double sampleRate);
    void Trigger(DrumVoiceType voiceType, float velocity);

    // Fills 'monoBuffer' (length 'frames') with the mixed output of all
    // active percussion voices.
    void Render(float* monoBuffer, int frames);

private:
    std::array<DrumHitVoice, kMaxVoices> voices_{};
    double sampleRate_ = 44100.0;
    int nextVoiceIndex_ = 0; // round-robin voice stealing when all voices are busy
    mutable std::mutex mutex_;
};
