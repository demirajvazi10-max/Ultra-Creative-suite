#pragma once

#include <array>
#include <mutex>

#include "Voice.h"

// Manages a fixed pool of voices (no heap allocation after Prepare()) and
// mixes them down to a mono buffer. This is what the audio callback calls
// on every buffer.
//
// Thread safety: NoteOn/NoteOff can now be called from either the UI thread
// (computer-keyboard playing) or RtMidi's own background thread (a MIDI
// keyboard), while Render() runs on PortAudio's real-time callback thread.
// A short mutex around the voice pool keeps this correct. The critical
// sections are tiny (touching a handful of plain structs, never allocating),
// so in practice the audio thread is never meaningfully delayed by it - a
// lock-free queue would be the next step up if that ever changes.
class Synth
{
public:
    static constexpr int kMaxVoices = 32;

    void Prepare(double sampleRate);
    void NoteOn(int midiNote, float velocity);
    void NoteOff(int midiNote);

    // Fills 'monoBuffer' (length 'frames') with the mixed output of all
    // active voices.
    void Render(float* monoBuffer, int frames);

private:
    std::array<Voice, kMaxVoices> voices_{};
    double sampleRate_ = 44100.0;
    int nextVoiceIndex_ = 0; // round-robin voice stealing when all voices are busy
    mutable std::mutex mutex_;
};
