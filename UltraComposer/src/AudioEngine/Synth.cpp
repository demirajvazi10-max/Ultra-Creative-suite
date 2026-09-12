#include "Synth.h"

#include <cstring>

void Synth::Prepare(double sampleRate)
{
    std::lock_guard<std::mutex> lock(mutex_);
    sampleRate_ = sampleRate;
    for (auto& voice : voices_)
    {
        voice = Voice{};
    }
}

void Synth::NoteOn(int midiNote, float velocity)
{
    std::lock_guard<std::mutex> lock(mutex_);

    // Retrigger if this exact note is already sounding.
    for (auto& voice : voices_)
    {
        if (voice.active && voice.note == midiNote)
        {
            voice.NoteOn(midiNote, velocity, sampleRate_);
            return;
        }
    }

    // Otherwise take the first free voice.
    for (auto& voice : voices_)
    {
        if (!voice.active)
        {
            voice.NoteOn(midiNote, velocity, sampleRate_);
            return;
        }
    }

    // All voices busy: steal one, round-robin.
    Voice& stolen = voices_[nextVoiceIndex_];
    nextVoiceIndex_ = (nextVoiceIndex_ + 1) % kMaxVoices;
    stolen.NoteOn(midiNote, velocity, sampleRate_);
}

void Synth::NoteOff(int midiNote)
{
    std::lock_guard<std::mutex> lock(mutex_);
    for (auto& voice : voices_)
    {
        if (voice.active && voice.note == midiNote)
        {
            voice.NoteOff();
        }
    }
}

void Synth::Render(float* monoBuffer, int frames)
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
