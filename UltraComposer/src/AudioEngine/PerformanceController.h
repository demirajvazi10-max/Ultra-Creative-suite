#pragma once

#include <array>
#include <atomic>
#include <cstdint>
#include <functional>
#include <mutex>
#include <vector>

#include "SoundParams.h"

// Everything a modern keyboard does between "a key went down" and "the
// sound engine plays a note": transpose, sustain pedal, Poly / Mono /
// Legato voice modes, portamento (glide), pitch bend, modulation-wheel
// vibrato and the arpeggiator.
//
// Only LIVE playing goes through here (computer keyboard and MIDI
// hardware). The sequencer, arranger, multi-track and accompaniment play
// their notes directly, unchanged.
//
// It does not make sound itself: it calls back into AudioEngine with the
// notes to start and stop, and once per audio block (Advance) it works out
// the pitch offset that the pitched instruments (Ultra FM and SoundFont)
// should use. VST instruments get pitch bend and the modulation wheel as
// their own controllers, so they use their own vibrato/bend settings.
class PerformanceController
{
public:
    enum Target
    {
        kPitched = 1, // Ultra FM + SoundFont (they follow GetPitchOffset)
        kVst = 2,     // VST instruments (they get real legato notes)
        kAll = 3,
    };
    using NoteOnFn = std::function<void(int note, float velocity, int targets)>;
    using NoteOffFn = std::function<void(int note, int targets)>;

    PerformanceController();
    void SetCallbacks(NoteOnFn noteOn, NoteOffFn noteOff);
    void Prepare(double sampleRate);

    // Live keys, already transposed.
    void KeyDown(int note, float velocity);
    void KeyUp(int note);

    void SetParam(int id, float value);
    float GetParam(int id) const;
    static float DefaultValue(int id);
    static bool IsPerformanceParam(int id) { return id >= 0 && id <= SoundParam::PerformanceLast; }

    // Releases every note this controller started (mode changes, panic).
    void AllNotesOff();

    // Audio thread, once per block before the instruments render: runs the
    // arpeggiator and moves bend/vibrato/glide forward by 'frames' samples.
    void Advance(int frames);

    float GetPitchOffset() const { return pitchOffset_.load(); }   // semitones, for Ultra FM + SoundFont
    float GetBend() const { return bendCurrent_.load(); }           // -1..1, for VST
    float GetModWheel() const { return params_[SoundParam::ModWheel].load(); }
    bool PolyGlideActive() const;                                    // Ultra FM's own per-voice portamento
    float PortamentoSeconds() const { return params_[SoundParam::PortamentoTime].load(); }
    int GetTranspose() const;

private:
    struct HeldNote
    {
        int note;
        float velocity;
    };
    struct ArpSounding
    {
        int note;
        double samplesLeft;
    };

    float Param(int id) const { return params_[static_cast<size_t>(id)].load(); }
    int Mode() const;
    void ReleaseKey(int note);              // key really released (after sustain)
    void VoiceOn(int note, float velocity);
    void VoiceOff(int note);
    void MonoMoveTo(int note, float velocity, bool fromRelease);
    void SendOn(int note, float velocity, int targets);
    void SendOff(int note, int targets);
    void ArpReleaseAll();
    void ArpStep();
    void BuildArpSequence(std::vector<HeldNote>& out);

    NoteOnFn noteOn_;
    NoteOffFn noteOff_;
    double sampleRate_ = 44100.0;
    std::array<std::atomic<float>, SoundParam::PerformanceLast + 1> params_;

    mutable std::mutex mutex_;
    // keys physically down (or held by the sustain pedal)
    std::array<bool, 128> keyDown_{};
    std::array<bool, 128> sustained_{};
    // what we have started, so AllNotesOff can stop exactly that
    std::array<int, 128> soundingPitched_{};
    std::array<int, 128> soundingVst_{};

    // Mono / Legato
    std::vector<HeldNote> monoStack_;
    int monoPitchedKey_ = -1;  // key the pitched instruments are really playing
    int monoVstKey_ = -1;      // key the VST is playing
    int lastMonoNote_ = -1;    // for portamento from the previous note
    float glideCurrent_ = 0.0f;
    float glideTarget_ = 0.0f;

    // Arpeggiator
    std::vector<HeldNote> arpHeld_;
    bool arpLatched_ = false;   // latch: keys stay after release until a new chord starts
    double arpCounter_ = 0.0;   // samples until next step
    int arpIndex_ = 0;
    int arpDirection_ = 1;
    std::vector<ArpSounding> arpSounding_;
    uint32_t random_ = 12345u;

    // Pitch
    std::atomic<float> bendTarget_{0.0f};
    std::atomic<float> bendCurrent_{0.0f};
    std::atomic<bool> bendSmooth_{false};
    double vibratoPhase_ = 0.0;
    std::atomic<float> pitchOffset_{0.0f};
    int lastMode_ = 0;
    bool lastArpOn_ = false;
};
