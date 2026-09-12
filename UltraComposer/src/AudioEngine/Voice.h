#pragma once

// A single synth voice: one playing note, with a simple linear ADSR-style
// envelope (attack + release only, for now) so notes don't click on/off.
//
// IMPORTANT: Render() runs on the real-time audio thread. It must never
// allocate memory, lock a mutex, or call anything that could block.

enum class EnvelopeStage
{
    Idle,
    Attack,
    Sustain,
    Release
};

struct Voice
{
    bool active = false;
    int note = -1;
    double phase = 0.0;
    double phaseIncrement = 0.0;
    float velocity = 0.0f;
    float envelopeLevel = 0.0f;
    EnvelopeStage stage = EnvelopeStage::Idle;

    // Starts (or restarts) this voice playing the given MIDI note.
    void NoteOn(int midiNote, float velocity, double sampleRate);

    // Begins the release phase; the voice goes inactive once it fades out.
    void NoteOff();

    // Adds up to 'frames' samples of this voice's output into 'output'
    // (mono, additive mix - caller is responsible for clearing the buffer
    // first). Real-time safe: no allocations, no locks.
    void Render(float* output, int frames, double sampleRate);
};
