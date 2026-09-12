#include "Voice.h"

#include <cmath>

namespace
{
    constexpr float kAttackTimeSeconds = 0.005f;
    constexpr float kReleaseTimeSeconds = 0.15f;
    constexpr double kTwoPi = 6.283185307179586;

    double MidiNoteToFrequencyHz(int note)
    {
        return 440.0 * std::pow(2.0, (note - 69) / 12.0);
    }
}

void Voice::NoteOn(int midiNote, float noteVelocity, double sampleRate)
{
    note = midiNote;
    velocity = noteVelocity;
    phase = 0.0;
    phaseIncrement = kTwoPi * MidiNoteToFrequencyHz(midiNote) / sampleRate;
    envelopeLevel = 0.0f;
    stage = EnvelopeStage::Attack;
    active = true;
}

void Voice::NoteOff()
{
    if (active)
    {
        stage = EnvelopeStage::Release;
    }
}

void Voice::Render(float* output, int frames, double sampleRate)
{
    if (!active)
    {
        return;
    }

    const float attackStep = 1.0f / (kAttackTimeSeconds * static_cast<float>(sampleRate));
    const float releaseStep = 1.0f / (kReleaseTimeSeconds * static_cast<float>(sampleRate));

    for (int i = 0; i < frames; ++i)
    {
        switch (stage)
        {
        case EnvelopeStage::Attack:
            envelopeLevel += attackStep;
            if (envelopeLevel >= 1.0f)
            {
                envelopeLevel = 1.0f;
                stage = EnvelopeStage::Sustain;
            }
            break;
        case EnvelopeStage::Sustain:
            break;
        case EnvelopeStage::Release:
            envelopeLevel -= releaseStep;
            if (envelopeLevel <= 0.0f)
            {
                envelopeLevel = 0.0f;
                stage = EnvelopeStage::Idle;
                active = false;
            }
            break;
        case EnvelopeStage::Idle:
        default:
            break;
        }

        const float sample = static_cast<float>(std::sin(phase)) * envelopeLevel * velocity;
        output[i] += sample;

        phase += phaseIncrement;
        if (phase >= kTwoPi)
        {
            phase -= kTwoPi;
        }

        if (!active)
        {
            break;
        }
    }
}
