#include "FmSynth.h"

#include <algorithm>
#include <cmath>
#include <cstring>

namespace
{
    constexpr int kTableSize = 4096;
    constexpr int kChunk = 16; // pitch, glide and envelope coefficients are refreshed every 16 samples

    struct SineTable
    {
        float values[kTableSize + 1];
        SineTable()
        {
            for (int i = 0; i <= kTableSize; ++i)
            {
                values[i] = static_cast<float>(std::sin(6.283185307179586 * i / kTableSize));
            }
        }
    };
    const SineTable& Table()
    {
        static SineTable table;
        return table;
    }

    // phase in cycles (any real number) -> sin(2*pi*phase)
    inline float FastSin(const float* table, double phase)
    {
        phase -= std::floor(phase);
        double pos = phase * kTableSize;
        int i = static_cast<int>(pos);
        float frac = static_cast<float>(pos - i);
        return table[i] + (table[i + 1] - table[i]) * frac;
    }

    // Fader-style level: 0 = silent, otherwise -48 dB .. 0 dB.
    inline float LevelToAmp(float level)
    {
        if (level <= 0.0005f) return 0.0f;
        return std::pow(10.0f, (std::min(level, 1.0f) - 1.0f) * 2.4f);
    }

    inline float TimeCoef(float seconds, double sampleRate)
    {
        // one-pole coefficient that gets ~99% of the way in 'seconds'
        double tau = std::max(0.0005, static_cast<double>(seconds) / 4.6);
        return static_cast<float>(1.0 - std::exp(-1.0 / (tau * sampleRate)));
    }

    // Which operators modulate operator i (bit j set = operator j modulates i;
    // always j > i, so operators are computed from 6 down to 1) and which
    // operators are heard (carriers).
    struct Algorithm
    {
        unsigned char carriers;
        unsigned char mods[6];
    };
    constexpr unsigned char B(int op) { return static_cast<unsigned char>(1u << op); }
    const Algorithm kAlgorithms[FmSynth::kAlgorithmCount] = {
        // 0: one stack 6>5>4>3>2>1
        {B(0), {B(1), B(2), B(3), B(4), B(5), 0}},
        // 1: two stacks 2>1 and 6>5>4>3
        {static_cast<unsigned char>(B(0) | B(2)), {B(1), 0, B(3), B(4), B(5), 0}},
        // 2: three pairs 2>1, 4>3, 6>5
        {static_cast<unsigned char>(B(0) | B(2) | B(4)), {B(1), 0, B(3), 0, B(5), 0}},
        // 3: two triples 3>2>1 and 6>5>4
        {static_cast<unsigned char>(B(0) | B(3)), {B(1), B(2), 0, B(4), B(5), 0}},
        // 4: branches (2+3)>1 and (5+6)>4
        {static_cast<unsigned char>(B(0) | B(3)), {static_cast<unsigned char>(B(1) | B(2)), 0, 0, static_cast<unsigned char>(B(4) | B(5)), 0, 0}},
        // 5: operator 6 modulates 1-5
        {static_cast<unsigned char>(B(0) | B(1) | B(2) | B(3) | B(4)), {B(5), B(5), B(5), B(5), B(5), 0}},
        // 6: all six heard (additive, organ-like)
        {0x3F, {0, 0, 0, 0, 0, 0}},
        // 7: pair 2>1 plus 3, 4, 5, 6 heard
        {static_cast<unsigned char>(B(0) | B(2) | B(3) | B(4) | B(5)), {B(1), 0, 0, 0, 0, 0}},
    };

    FmOperator Op(float level, float ratio, float attack, float decay, float sustain, float release,
                  float velocity = 0.5f, float detune = 0.0f)
    {
        FmOperator o;
        o.level = level; o.ratio = ratio; o.detune = detune;
        o.attack = attack; o.decay = decay; o.sustain = sustain; o.release = release;
        o.velocity = velocity;
        return o;
    }

    struct PresetDef
    {
        const char* name;
        FmPatch patch;
    };

    FmPatch Make(int algorithm, float feedback, std::array<FmOperator, 6> ops, float volume = 0.8f)
    {
        FmPatch p;
        p.algorithm = algorithm; p.feedback = feedback; p.ops = ops; p.volume = volume; p.brightness = 1.0f;
        return p;
    }

    const PresetDef* Presets(int& count)
    {
        static const PresetDef presets[] = {
            {"Electric Piano", Make(2, 0.15f, {
                Op(1.00f, 1.0f, 0.002f, 3.5f, 0.0f, 0.45f, 0.6f),
                Op(0.55f, 14.0f, 0.001f, 0.25f, 0.0f, 0.2f, 0.9f),
                Op(0.88f, 1.0f, 0.002f, 3.0f, 0.0f, 0.45f, 0.5f, 3.0f),
                Op(0.72f, 1.0f, 0.002f, 1.6f, 0.15f, 0.4f, 0.9f),
                Op(0.55f, 1.0f, 0.002f, 2.5f, 0.0f, 0.4f, 0.5f, -7.0f),
                Op(0.62f, 1.0f, 0.002f, 1.0f, 0.1f, 0.4f, 0.8f)})},
            {"Bell", Make(2, 0.0f, {
                Op(1.00f, 1.0f, 0.001f, 7.0f, 0.0f, 3.0f, 0.5f),
                Op(0.78f, 3.5f, 0.001f, 5.0f, 0.0f, 3.0f, 0.7f),
                Op(0.70f, 2.0f, 0.001f, 5.0f, 0.0f, 2.5f, 0.5f, 4.0f),
                Op(0.62f, 7.0f, 0.001f, 2.5f, 0.0f, 2.0f, 0.7f),
                Op(0.50f, 4.0f, 0.001f, 4.0f, 0.0f, 2.0f, 0.5f, -5.0f),
                Op(0.55f, 1.5f, 0.001f, 3.0f, 0.0f, 2.0f, 0.6f)})},
            {"FM Bass", Make(3, 0.3f, {
                Op(1.00f, 1.0f, 0.001f, 1.2f, 0.6f, 0.12f, 0.4f),
                Op(0.80f, 1.0f, 0.001f, 0.35f, 0.3f, 0.12f, 0.8f),
                Op(0.60f, 1.0f, 0.001f, 0.2f, 0.0f, 0.1f, 0.8f),
                Op(0.80f, 0.5f, 0.001f, 1.5f, 0.7f, 0.12f, 0.3f),
                Op(0.60f, 0.5f, 0.001f, 0.4f, 0.2f, 0.12f, 0.6f),
                Op(0.30f, 1.0f, 0.001f, 0.3f, 0.0f, 0.1f, 0.5f)}, 0.85f)},
            {"Brass", Make(3, 0.4f, {
                Op(1.00f, 1.0f, 0.06f, 0.3f, 0.85f, 0.2f, 0.4f),
                Op(0.82f, 1.0f, 0.08f, 0.4f, 0.7f, 0.2f, 0.6f),
                Op(0.50f, 1.0f, 0.05f, 0.3f, 0.5f, 0.2f, 0.5f),
                Op(0.85f, 1.0f, 0.07f, 0.3f, 0.85f, 0.2f, 0.4f, 5.0f),
                Op(0.76f, 1.0f, 0.1f, 0.5f, 0.65f, 0.2f, 0.6f),
                Op(0.40f, 2.0f, 0.05f, 0.3f, 0.4f, 0.2f, 0.5f)})},
            {"Organ", Make(6, 0.0f, {
                Op(0.82f, 0.5f, 0.004f, 0.1f, 1.0f, 0.05f, 0.0f),
                Op(1.00f, 1.0f, 0.004f, 0.1f, 1.0f, 0.05f, 0.0f),
                Op(0.72f, 1.5f, 0.004f, 0.1f, 1.0f, 0.05f, 0.0f),
                Op(0.80f, 2.0f, 0.004f, 0.1f, 1.0f, 0.05f, 0.0f),
                Op(0.62f, 3.0f, 0.004f, 0.1f, 1.0f, 0.05f, 0.0f),
                Op(0.62f, 4.0f, 0.004f, 0.1f, 1.0f, 0.05f, 0.0f)}, 0.7f)},
            {"Soft Pad", Make(2, 0.1f, {
                Op(1.00f, 1.0f, 1.2f, 2.0f, 1.0f, 1.6f, 0.3f),
                Op(0.55f, 1.0f, 1.6f, 2.0f, 0.9f, 1.6f, 0.4f),
                Op(0.90f, 1.0f, 1.3f, 2.0f, 1.0f, 1.6f, 0.3f, -7.0f),
                Op(0.50f, 2.0f, 2.0f, 2.0f, 0.8f, 1.6f, 0.4f),
                Op(0.70f, 2.0f, 1.4f, 2.0f, 1.0f, 1.6f, 0.3f, 7.0f),
                Op(0.45f, 1.0f, 1.8f, 2.0f, 0.8f, 1.6f, 0.4f)}, 0.75f)},
            {"Synth Lead", Make(1, 0.6f, {
                Op(1.00f, 1.0f, 0.005f, 0.2f, 0.9f, 0.2f, 0.3f),
                Op(0.85f, 1.0f, 0.005f, 0.3f, 0.8f, 0.2f, 0.4f),
                Op(0.85f, 1.0f, 0.005f, 0.2f, 0.9f, 0.2f, 0.3f, 9.0f),
                Op(0.80f, 1.0f, 0.005f, 0.3f, 0.8f, 0.2f, 0.4f),
                Op(0.40f, 2.0f, 0.005f, 0.3f, 0.6f, 0.2f, 0.4f),
                Op(0.50f, 1.0f, 0.005f, 0.3f, 0.8f, 0.2f, 0.4f)})},
            {"Strings", Make(2, 0.25f, {
                Op(1.00f, 1.0f, 0.3f, 1.0f, 1.0f, 0.6f, 0.3f),
                Op(0.62f, 1.0f, 0.35f, 1.0f, 0.9f, 0.6f, 0.4f),
                Op(0.92f, 1.0f, 0.3f, 1.0f, 1.0f, 0.6f, 0.3f, 8.0f),
                Op(0.60f, 1.0f, 0.4f, 1.0f, 0.9f, 0.6f, 0.4f),
                Op(0.80f, 2.0f, 0.32f, 1.0f, 1.0f, 0.6f, 0.3f, -6.0f),
                Op(0.58f, 2.0f, 0.4f, 1.0f, 0.85f, 0.6f, 0.4f)}, 0.75f)},
            {"Marimba", Make(2, 0.0f, {
                Op(1.00f, 1.0f, 0.001f, 0.7f, 0.0f, 0.3f, 0.6f),
                Op(0.62f, 4.0f, 0.001f, 0.08f, 0.0f, 0.08f, 0.8f),
                Op(0.48f, 4.0f, 0.001f, 0.18f, 0.0f, 0.12f, 0.6f),
                Op(0.30f, 1.0f, 0.001f, 0.1f, 0.0f, 0.1f, 0.6f),
                Op(0.00f, 1.0f, 0.001f, 0.5f, 0.0f, 0.3f, 0.5f),
                Op(0.00f, 1.0f, 0.001f, 0.5f, 0.0f, 0.3f, 0.5f)}, 0.85f)},
            {"Clavinet", Make(3, 0.2f, {
                Op(1.00f, 1.0f, 0.001f, 0.9f, 0.15f, 0.08f, 0.6f),
                Op(0.76f, 3.0f, 0.001f, 0.4f, 0.2f, 0.08f, 0.8f),
                Op(0.52f, 1.0f, 0.001f, 0.3f, 0.1f, 0.08f, 0.8f),
                Op(0.62f, 2.0f, 0.001f, 0.6f, 0.1f, 0.08f, 0.6f),
                Op(0.66f, 5.0f, 0.001f, 0.2f, 0.1f, 0.08f, 0.8f),
                Op(0.30f, 1.0f, 0.001f, 0.2f, 0.0f, 0.08f, 0.6f)})},
            {"Flute", Make(1, 0.9f, {
                Op(1.00f, 1.0f, 0.08f, 0.3f, 0.9f, 0.15f, 0.4f),
                Op(0.55f, 1.0f, 0.1f, 0.4f, 0.7f, 0.15f, 0.5f),
                Op(0.30f, 1.0f, 0.03f, 0.2f, 0.5f, 0.1f, 0.4f),
                Op(0.20f, 1.0f, 0.05f, 0.3f, 0.5f, 0.1f, 0.4f),
                Op(0.30f, 1.0f, 0.05f, 0.3f, 0.5f, 0.1f, 0.4f),
                Op(0.35f, 1.0f, 0.05f, 0.3f, 0.6f, 0.1f, 0.4f)}, 0.8f)},
            {"Glass", Make(5, 0.0f, {
                Op(1.00f, 1.0f, 0.001f, 4.0f, 0.0f, 2.0f, 0.5f),
                Op(0.70f, 2.0f, 0.001f, 3.0f, 0.0f, 2.0f, 0.5f, 5.0f),
                Op(0.55f, 3.0f, 0.001f, 2.5f, 0.0f, 1.5f, 0.5f),
                Op(0.45f, 5.0f, 0.001f, 2.0f, 0.0f, 1.5f, 0.5f, -4.0f),
                Op(0.40f, 8.0f, 0.001f, 1.5f, 0.0f, 1.0f, 0.5f),
                Op(0.55f, 7.0f, 0.001f, 1.5f, 0.0f, 1.0f, 0.7f)}, 0.75f)},
            {"Pluck Bass", Make(1, 0.0f, {
                Op(1.00f, 1.0f, 0.001f, 0.45f, 0.0f, 0.1f, 0.5f),
                Op(0.86f, 1.0f, 0.001f, 0.15f, 0.0f, 0.1f, 0.8f),
                Op(0.70f, 0.5f, 0.001f, 0.6f, 0.1f, 0.1f, 0.5f),
                Op(0.70f, 0.5f, 0.001f, 0.2f, 0.0f, 0.1f, 0.8f),
                Op(0.40f, 1.0f, 0.001f, 0.1f, 0.0f, 0.1f, 0.8f),
                Op(0.00f, 1.0f, 0.001f, 0.1f, 0.0f, 0.1f, 0.5f)}, 0.9f)},
            {"Choir", Make(2, 0.05f, {
                Op(1.00f, 1.0f, 0.45f, 1.0f, 1.0f, 0.8f, 0.3f),
                Op(0.45f, 3.0f, 0.5f, 1.0f, 0.9f, 0.8f, 0.3f),
                Op(0.85f, 1.0f, 0.5f, 1.0f, 1.0f, 0.8f, 0.3f, 6.0f),
                Op(0.40f, 5.0f, 0.5f, 1.0f, 0.8f, 0.8f, 0.3f),
                Op(0.70f, 2.0f, 0.5f, 1.0f, 1.0f, 0.8f, 0.3f, -6.0f),
                Op(0.38f, 2.0f, 0.5f, 1.0f, 0.8f, 0.8f, 0.3f)}, 0.75f)},
            {"Steel Drum", Make(2, 0.0f, {
                Op(1.00f, 1.0f, 0.001f, 1.3f, 0.0f, 0.5f, 0.5f),
                Op(0.66f, 2.5f, 0.001f, 0.5f, 0.0f, 0.3f, 0.7f),
                Op(0.45f, 4.0f, 0.001f, 0.6f, 0.0f, 0.3f, 0.5f),
                Op(0.40f, 1.0f, 0.001f, 0.3f, 0.0f, 0.3f, 0.6f),
                Op(0.35f, 2.0f, 0.001f, 0.8f, 0.0f, 0.3f, 0.5f),
                Op(0.45f, 3.5f, 0.001f, 0.3f, 0.0f, 0.3f, 0.6f)}, 0.8f)},
            {"Pure Sine", Make(6, 0.0f, {
                Op(1.00f, 1.0f, 0.005f, 0.5f, 1.0f, 0.15f, 1.0f),
                Op(0.00f, 1.0f, 0.005f, 0.5f, 1.0f, 0.15f, 0.5f),
                Op(0.00f, 2.0f, 0.005f, 0.5f, 1.0f, 0.15f, 0.5f),
                Op(0.00f, 3.0f, 0.005f, 0.5f, 1.0f, 0.15f, 0.5f),
                Op(0.00f, 4.0f, 0.005f, 0.5f, 1.0f, 0.15f, 0.5f),
                Op(0.00f, 5.0f, 0.005f, 0.5f, 1.0f, 0.15f, 0.5f)}, 0.9f)},
        };
        count = static_cast<int>(sizeof(presets) / sizeof(presets[0]));
        return presets;
    }
}

FmSynth::FmSynth()
{
    Table();
    patch_ = Preset(0);
}

int FmSynth::PresetCount()
{
    int count = 0;
    Presets(count);
    return count;
}

const char* FmSynth::PresetName(int index)
{
    int count = 0;
    const PresetDef* p = Presets(count);
    return (index >= 0 && index < count) ? p[index].name : "";
}

FmPatch FmSynth::Preset(int index)
{
    int count = 0;
    const PresetDef* p = Presets(count);
    return (index >= 0 && index < count) ? p[index].patch : p[0].patch;
}

void FmSynth::Prepare(double sampleRate)
{
    std::lock_guard<std::mutex> lock(mutex_);
    sampleRate_ = sampleRate;
    for (auto& v : voices_) v = VoiceState{};
}

void FmSynth::SetPatch(const FmPatch& patch)
{
    std::lock_guard<std::mutex> lock(mutex_);
    patch_ = patch;
    patch_.algorithm = std::clamp(patch_.algorithm, 0, kAlgorithmCount - 1);
}

FmPatch FmSynth::GetPatch() const
{
    std::lock_guard<std::mutex> lock(mutex_);
    return patch_;
}

void FmSynth::SetGlide(bool enabled, float seconds)
{
    std::lock_guard<std::mutex> lock(mutex_);
    glideOn_ = enabled;
    glideSeconds_ = std::clamp(seconds, 0.005f, 5.0f);
}

void FmSynth::StartVoice(VoiceState& v, int note, float velocity)
{
    v.active = true;
    v.released = false;
    v.note = note;
    v.velocity = std::clamp(velocity, 0.0f, 1.0f);
    v.targetPitch = static_cast<float>(note);
    v.pitch = (glideOn_ && lastPitch_ >= 0.0f) ? lastPitch_ : v.targetPitch;
    v.age = ++ageCounter_;
    v.fb1 = v.fb2 = 0.0f;
    for (auto& op : v.ops)
    {
        // Keep the phase running when a voice is retriggered (no click),
        // restart the envelope from where it is (no click either).
        op.stage = 1;
    }
    lastPitch_ = v.targetPitch;
}

void FmSynth::NoteOn(int midiNote, float velocity)
{
    if (midiNote < 0 || midiNote > 127) return;
    std::lock_guard<std::mutex> lock(mutex_);

    // Same note already sounding (not released): retrigger it.
    for (auto& v : voices_)
    {
        if (v.active && v.note == midiNote && !v.released)
        {
            float glideFrom = v.pitch;
            StartVoice(v, midiNote, velocity);
            if (glideOn_) v.pitch = glideFrom;
            return;
        }
    }

    VoiceState* chosen = nullptr;
    for (auto& v : voices_)
    {
        if (!v.active) { chosen = &v; break; }
    }
    if (!chosen)
    {
        // Steal the oldest released voice, otherwise the oldest voice.
        for (auto& v : voices_)
            if (v.released && (!chosen || v.age < chosen->age)) chosen = &v;
        if (!chosen)
            for (auto& v : voices_)
                if (!chosen || v.age < chosen->age) chosen = &v;
        for (auto& op : chosen->ops) { op.env = 0.0f; op.phase = 0.0; }
    }
    else
    {
        for (auto& op : chosen->ops) { op.env = 0.0f; op.phase = 0.0; op.out = 0.0f; }
    }
    StartVoice(*chosen, midiNote, velocity);
}

void FmSynth::NoteOff(int midiNote)
{
    std::lock_guard<std::mutex> lock(mutex_);
    for (auto& v : voices_)
    {
        if (v.active && v.note == midiNote && !v.released)
        {
            v.released = true;
            for (auto& op : v.ops)
                if (op.stage != 0) op.stage = 3;
        }
    }
}

void FmSynth::AllNotesOff()
{
    std::lock_guard<std::mutex> lock(mutex_);
    for (auto& v : voices_)
    {
        if (v.active)
        {
            v.released = true;
            for (auto& op : v.ops)
                if (op.stage != 0) op.stage = 3;
        }
    }
}

void FmSynth::Render(float* monoBuffer, int frames)
{
    std::memset(monoBuffer, 0, sizeof(float) * static_cast<size_t>(frames > 0 ? frames : 0));
    if (frames <= 0) return;

    std::lock_guard<std::mutex> lock(mutex_);
    float offsetStart = renderedOffset_;
    float offsetEnd = pitchOffset_.load();
    if (!std::isfinite(offsetEnd)) offsetEnd = 0.0f;
    for (auto& v : voices_)
    {
        if (v.active) RenderVoice(v, monoBuffer, frames, offsetStart, offsetEnd);
    }
    renderedOffset_ = offsetEnd;
}

void FmSynth::RenderVoice(VoiceState& v, float* out, int frames, float offsetStart, float offsetEnd)
{
    const float* table = Table().values;
    const FmPatch& p = patch_;
    const Algorithm& algo = kAlgorithms[std::clamp(p.algorithm, 0, kAlgorithmCount - 1)];
    const double sr = sampleRate_;

    // Static per-block values.
    float amp[6], attackStep[6], decayCoef[6], releaseCoef[6], sustain[6];
    int carrierCount = 0;
    const float keyScale = v.note > 60 ? std::pow(2.0f, -(v.note - 60) / 48.0f) : 1.0f;
    for (int i = 0; i < 6; ++i)
    {
        const FmOperator& o = p.ops[i];
        float velScale = 1.0f - o.velocity + o.velocity * v.velocity;
        float a = LevelToAmp(o.level) * velScale;
        bool carrier = (algo.carriers >> i) & 1;
        if (carrier) { if (a > 0.0f) ++carrierCount; }
        else a *= p.brightness * keyScale;
        amp[i] = a;
        attackStep[i] = 1.0f / static_cast<float>(std::max(0.0005, static_cast<double>(o.attack)) * sr);
        decayCoef[i] = TimeCoef(o.decay, sr);
        releaseCoef[i] = TimeCoef(o.release, sr);
        sustain[i] = std::clamp(o.sustain, 0.0f, 1.0f);
    }
    const float carrierGain = carrierCount > 0 ? 1.0f / std::sqrt(static_cast<float>(carrierCount)) : 0.0f;
    const float outGain = carrierGain * std::clamp(p.volume, 0.0f, 1.0f) * 0.55f;
    const float fbAmount = std::clamp(p.feedback, 0.0f, 1.0f) * 0.35f;
    const float glideCoef = glideOn_ ? TimeCoef(glideSeconds_, sr / kChunk) : 1.0f;

    for (int start = 0; start < frames; start += kChunk)
    {
        int n = std::min(kChunk, frames - start);
        float t = (static_cast<float>(start) + static_cast<float>(n) * 0.5f) / static_cast<float>(frames);
        float offset = offsetStart + (offsetEnd - offsetStart) * t;

        v.pitch += (v.targetPitch - v.pitch) * glideCoef;
        double freq = 440.0 * std::pow(2.0, (v.pitch + offset - 69.0) / 12.0);
        double inc[6];
        for (int i = 0; i < 6; ++i)
        {
            const FmOperator& o = p.ops[i];
            inc[i] = freq * o.ratio * std::pow(2.0, o.detune / 1200.0) / sr;
            if (inc[i] > 0.49) inc[i] = 0.0; // above Nyquist: silence instead of aliasing
        }

        for (int s = 0; s < n; ++s)
        {
            float sample = 0.0f;
            for (int i = 5; i >= 0; --i)
            {
                OpState& op = v.ops[i];
                switch (op.stage)
                {
                case 1:
                    op.env += attackStep[i];
                    if (op.env >= 1.0f) { op.env = 1.0f; op.stage = 2; }
                    break;
                case 2:
                    op.env += (sustain[i] - op.env) * decayCoef[i];
                    break;
                case 3:
                    op.env -= op.env * releaseCoef[i];
                    if (op.env < 0.0001f) { op.env = 0.0f; op.stage = 0; }
                    break;
                default:
                    break;
                }

                if (amp[i] <= 0.0f || op.stage == 0)
                {
                    op.out = 0.0f;
                }
                else
                {
                    float mod = 0.0f;
                    unsigned char m = algo.mods[i];
                    for (int j = i + 1; m && j < 6; ++j)
                        if ((m >> j) & 1) mod += v.ops[j].out;
                    if (i == 5) mod += fbAmount * (v.fb1 + v.fb2) * 0.5f;
                    op.out = FastSin(table, op.phase + mod) * op.env * amp[i];
                }
                op.phase += inc[i];
                if (op.phase >= 1.0) op.phase -= std::floor(op.phase);

                if ((algo.carriers >> i) & 1) sample += op.out;
            }
            v.fb2 = v.fb1;
            v.fb1 = v.ops[5].out;
            out[start + s] += sample * outGain;
        }
    }

    // Voice is done when every heard operator is silent and can't come back.
    bool alive = false;
    for (int i = 0; i < 6; ++i)
    {
        if (!((algo.carriers >> i) & 1) || amp[i] <= 0.0f) continue;
        const OpState& op = v.ops[i];
        if (op.stage == 1 || (op.stage == 2 && (sustain[i] > 0.0001f || op.env > 0.0002f)) || (op.stage == 3 && op.env > 0.0f))
        {
            alive = true;
            break;
        }
    }
    if (!alive)
    {
        v.active = false;
        v.note = -1;
        for (auto& op : v.ops) { op.stage = 0; op.env = 0.0f; op.out = 0.0f; }
    }
}
