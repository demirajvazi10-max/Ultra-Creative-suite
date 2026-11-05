#pragma once

#include <array>
#include <atomic>
#include <cstdint>
#include <mutex>

// Ultra FM - a six-operator FM synthesizer in the spirit of the DX7 and FM8.
// It replaces the old single-sine built-in synth of the performance keyboard
// ("Sloj 1" when no SoundFont/VST is loaded, and the extra layers set to
// "built-in synth"), with the same NoteOn/NoteOff/Render(mono) interface.
//
// Each operator is a sine oscillator with its own frequency ratio, fine
// detune, output level, ADSR envelope and velocity sensitivity. The
// algorithm decides which operators are heard (carriers) and which ones
// modulate the others; operator 6 can also modulate itself (feedback).
// Edits apply live, also to notes that are already sounding, like FM8.

struct FmOperator
{
    float level = 0.0f;     // 0..1 (fader-like: 0 = silent, 1 = full)
    float ratio = 1.0f;     // frequency multiple of the note, 0.5..16
    float detune = 0.0f;    // cents, -50..50
    float attack = 0.005f;  // seconds
    float decay = 0.5f;     // seconds
    float sustain = 1.0f;   // 0..1
    float release = 0.3f;   // seconds
    float velocity = 0.5f;  // velocity sensitivity 0..1
};

struct FmPatch
{
    int algorithm = 0;      // 0..kAlgorithmCount-1
    float feedback = 0.0f;  // 0..1, operator 6 modulating itself
    float volume = 0.8f;    // 0..1
    float brightness = 1.0f; // 0..2, scales every modulator ("timbre" knob)
    std::array<FmOperator, 6> ops{};
};

class FmSynth
{
public:
    static constexpr int kMaxVoices = 16;
    static constexpr int kOperatorCount = 6;
    static constexpr int kAlgorithmCount = 8;

    FmSynth();

    void Prepare(double sampleRate);
    void NoteOn(int midiNote, float velocity);
    void NoteOff(int midiNote);
    void AllNotesOff();
    // Overwrites 'monoBuffer' (frames samples) with the synth's output.
    void Render(float* monoBuffer, int frames);

    void SetPatch(const FmPatch& patch);
    FmPatch GetPatch() const;

    // Live pitch offset in semitones for every voice (pitch bend + vibrato +
    // mono/legato glide, all computed by PerformanceController). Smoothed
    // over each render block, so per-block updates don't click.
    void SetPitchOffset(float semitones) { pitchOffset_.store(semitones); }

    // Polyphonic portamento: every new note slides from the pitch of the
    // previous note. Only used in Poly mode - in Mono/Legato the glide comes
    // from the pitch offset above instead.
    void SetGlide(bool enabled, float seconds);

    static int PresetCount();
    static const char* PresetName(int index); // English name, for logs/tests
    static FmPatch Preset(int index);

private:
    struct OpState
    {
        double phase = 0.0;
        float env = 0.0f;
        int stage = 0; // 0 idle, 1 attack, 2 decay/sustain, 3 release
        float out = 0.0f;
    };
    struct VoiceState
    {
        bool active = false;
        bool released = false;
        int note = -1;
        float velocity = 0.0f;
        float pitch = 60.0f;       // current (gliding) pitch, in MIDI note units
        float targetPitch = 60.0f;
        float fb1 = 0.0f, fb2 = 0.0f;
        uint64_t age = 0;
        std::array<OpState, 6> ops{};
    };

    void StartVoice(VoiceState& v, int note, float velocity);
    void RenderVoice(VoiceState& v, float* out, int frames, float offsetStart, float offsetEnd);

    std::array<VoiceState, kMaxVoices> voices_{};
    FmPatch patch_{};
    double sampleRate_ = 44100.0;
    uint64_t ageCounter_ = 0;
    float lastPitch_ = -1.0f;
    bool glideOn_ = false;
    float glideSeconds_ = 0.1f;
    float renderedOffset_ = 0.0f;
    std::atomic<float> pitchOffset_{0.0f};
    mutable std::mutex mutex_;
};
