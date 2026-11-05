#include "PerformanceController.h"

#include <algorithm>
#include <cmath>

using namespace SoundParam;

namespace
{
    constexpr double kTwoPi = 6.283185307179586;
    // Arpeggiator note lengths in beats: 1/4, 1/8, 1/8 triplet, 1/16, 1/16 triplet, 1/32
    const double kArpBeats[] = {1.0, 0.5, 1.0 / 3.0, 0.25, 1.0 / 6.0, 0.125};
    constexpr int kArpRateCount = 6;
}

PerformanceController::PerformanceController()
{
    for (int id = 0; id <= PerformanceLast; ++id)
        params_[static_cast<size_t>(id)].store(DefaultValue(id));
    soundingPitched_.fill(0);
    soundingVst_.fill(0);
}

float PerformanceController::DefaultValue(int id)
{
    switch (id)
    {
    case BendRange: return 2.0f;
    case VibratoDepth: return 0.5f;
    case VibratoRate: return 5.5f;
    case PortamentoTime: return 0.15f;
    case ArpRate: return 3.0f;
    case ArpOctaves: return 1.0f;
    case ArpGate: return 0.6f;
    case ArpTempo: return 120.0f;
    default: return 0.0f;
    }
}

void PerformanceController::SetCallbacks(NoteOnFn noteOn, NoteOffFn noteOff)
{
    std::lock_guard<std::mutex> lock(mutex_);
    noteOn_ = std::move(noteOn);
    noteOff_ = std::move(noteOff);
}

void PerformanceController::Prepare(double sampleRate)
{
    std::lock_guard<std::mutex> lock(mutex_);
    sampleRate_ = sampleRate;
}

int PerformanceController::Mode() const
{
    return std::clamp(static_cast<int>(std::lround(Param(VoiceMode))), 0, 2);
}

int PerformanceController::GetTranspose() const
{
    return std::clamp(static_cast<int>(std::lround(Param(Transpose))), -24, 24);
}

bool PerformanceController::PolyGlideActive() const
{
    return Param(PortamentoOn) >= 0.5f && (Mode() == 0 || Param(ArpOn) >= 0.5f);
}

void PerformanceController::SendOn(int note, float velocity, int targets)
{
    if (note < 0 || note > 127 || !noteOn_) return;
    noteOn_(note, velocity, targets);
    if (targets & kPitched) soundingPitched_[static_cast<size_t>(note)] = 1;
    if (targets & kVst) soundingVst_[static_cast<size_t>(note)] = 1;
}

void PerformanceController::SendOff(int note, int targets)
{
    if (note < 0 || note > 127 || !noteOff_) return;
    noteOff_(note, targets);
    if (targets & kPitched) soundingPitched_[static_cast<size_t>(note)] = 0;
    if (targets & kVst) soundingVst_[static_cast<size_t>(note)] = 0;
}

void PerformanceController::SetParam(int id, float value)
{
    if (!IsPerformanceParam(id) || !std::isfinite(value)) return;

    switch (id)
    {
    case PitchBend:
        value = std::clamp(value, -1.0f, 1.0f);
        params_[PitchBend].store(value);
        bendSmooth_.store(false);
        bendTarget_.store(value);
        return;
    case PitchBendKeys:
        value = std::clamp(value, -1.0f, 1.0f);
        params_[PitchBendKeys].store(value);
        bendSmooth_.store(true);
        bendTarget_.store(value);
        return;
    case ModWheel:
        params_[ModWheel].store(std::clamp(value, 0.0f, 1.0f));
        return;
    default:
        break;
    }

    std::lock_guard<std::mutex> lock(mutex_);
    float old = params_[static_cast<size_t>(id)].load();
    params_[static_cast<size_t>(id)].store(value);

    if (id == Sustain)
    {
        bool on = value >= 0.5f;
        if (!on && old >= 0.5f)
        {
            for (int n = 0; n < 128; ++n)
            {
                if (sustained_[static_cast<size_t>(n)] && !keyDown_[static_cast<size_t>(n)])
                {
                    sustained_[static_cast<size_t>(n)] = false;
                    ReleaseKey(n);
                }
                sustained_[static_cast<size_t>(n)] = false;
            }
        }
    }
    else if (id == VoiceMode || id == ArpOn)
    {
        if ((id == VoiceMode && Mode() != lastMode_) || (id == ArpOn && (value >= 0.5f) != lastArpOn_))
        {
            // A clean start in the new mode: stop what this controller is playing.
            for (int n = 0; n < 128; ++n)
            {
                if (soundingPitched_[static_cast<size_t>(n)]) SendOff(n, kPitched);
                if (soundingVst_[static_cast<size_t>(n)]) SendOff(n, kVst);
            }
            monoStack_.clear();
            monoPitchedKey_ = monoVstKey_ = -1;
            glideCurrent_ = glideTarget_ = 0.0f;
            arpHeld_.clear();
            arpSounding_.clear();
            arpLatched_ = false;
            keyDown_.fill(false);
            sustained_.fill(false);
        }
        lastMode_ = Mode();
        lastArpOn_ = Param(ArpOn) >= 0.5f;
    }
    else if (id == ArpLatch && value < 0.5f)
    {
        // Latch off: forget latched keys that are no longer held.
        arpHeld_.erase(std::remove_if(arpHeld_.begin(), arpHeld_.end(), [this](const HeldNote& h) {
                           return !keyDown_[static_cast<size_t>(h.note)] && !sustained_[static_cast<size_t>(h.note)];
                       }),
                       arpHeld_.end());
        arpLatched_ = false;
    }
}

float PerformanceController::GetParam(int id) const
{
    if (!IsPerformanceParam(id)) return 0.0f;
    if (id == PitchBend) return bendCurrent_.load();
    return params_[static_cast<size_t>(id)].load();
}

void PerformanceController::KeyDown(int note, float velocity)
{
    if (note < 0 || note > 127) return;
    std::lock_guard<std::mutex> lock(mutex_);
    const bool arp = Param(ArpOn) >= 0.5f;
    const size_t n = static_cast<size_t>(note);

    if (sustained_[n])
    {
        // Pressed again while the pedal still holds it: play it fresh.
        sustained_[n] = false;
        if (!arp) VoiceOff(note);
    }
    if (keyDown_[n]) return; // key repeat
    keyDown_[n] = true;

    if (arp)
    {
        if (Param(ArpLatch) >= 0.5f)
        {
            bool anyOtherHeld = false;
            for (int k = 0; k < 128; ++k)
                if (k != note && (keyDown_[static_cast<size_t>(k)] || sustained_[static_cast<size_t>(k)])) { anyOtherHeld = true; break; }
            if (!anyOtherHeld) arpHeld_.clear(); // a new chord replaces the latched one
        }
        bool wasEmpty = arpHeld_.empty();
        bool present = std::any_of(arpHeld_.begin(), arpHeld_.end(), [note](const HeldNote& h) { return h.note == note; });
        if (!present) arpHeld_.push_back({note, velocity});
        if (wasEmpty)
        {
            arpCounter_ = 0.0; // first step on the next audio block
            arpIndex_ = 0;
            arpDirection_ = 1;
        }
        return;
    }

    VoiceOn(note, velocity);
}

void PerformanceController::KeyUp(int note)
{
    if (note < 0 || note > 127) return;
    std::lock_guard<std::mutex> lock(mutex_);
    const size_t n = static_cast<size_t>(note);
    if (!keyDown_[n]) return;
    keyDown_[n] = false;
    if (Param(Sustain) >= 0.5f)
    {
        sustained_[n] = true;
        return;
    }
    ReleaseKey(note);
}

void PerformanceController::ReleaseKey(int note)
{
    if (Param(ArpOn) >= 0.5f)
    {
        if (Param(ArpLatch) >= 0.5f)
        {
            arpLatched_ = true;
            return;
        }
        arpHeld_.erase(std::remove_if(arpHeld_.begin(), arpHeld_.end(), [note](const HeldNote& h) { return h.note == note; }),
                       arpHeld_.end());
        return;
    }
    VoiceOff(note);
}

void PerformanceController::VoiceOn(int note, float velocity)
{
    if (Mode() == 0)
    {
        SendOn(note, velocity, kAll);
        return;
    }
    monoStack_.erase(std::remove_if(monoStack_.begin(), monoStack_.end(), [note](const HeldNote& h) { return h.note == note; }),
                     monoStack_.end());
    monoStack_.push_back({note, velocity});
    MonoMoveTo(note, velocity, false);
}

void PerformanceController::VoiceOff(int note)
{
    if (Mode() == 0)
    {
        SendOff(note, kAll);
        return;
    }
    auto it = std::find_if(monoStack_.begin(), monoStack_.end(), [note](const HeldNote& h) { return h.note == note; });
    if (it == monoStack_.end()) return;
    bool wasTop = (it + 1 == monoStack_.end());
    monoStack_.erase(it);

    if (monoStack_.empty())
    {
        if (monoPitchedKey_ >= 0) SendOff(monoPitchedKey_, kPitched);
        if (monoVstKey_ >= 0) SendOff(monoVstKey_, kVst);
        monoPitchedKey_ = monoVstKey_ = -1;
        // glide offset is left where it is, so the release tail keeps its pitch
    }
    else if (wasTop)
    {
        // Back to the previous key that is still held (classic mono-synth "note priority: last").
        MonoMoveTo(monoStack_.back().note, monoStack_.back().velocity, true);
    }
}

void PerformanceController::MonoMoveTo(int note, float velocity, bool /*fromRelease*/)
{
    const bool legato = Mode() == 2;
    const bool portamento = Param(PortamentoOn) >= 0.5f;
    const bool sounding = monoPitchedKey_ >= 0;

    if (sounding && legato && std::abs(note - monoPitchedKey_) <= 24)
    {
        // Legato: no new attack, the sounding note just moves (slides with portamento).
        glideTarget_ = static_cast<float>(note - monoPitchedKey_);
        if (!portamento) glideCurrent_ = glideTarget_;
        if (monoVstKey_ != note)
        {
            int old = monoVstKey_;
            SendOn(note, velocity, kVst);   // new note first, so a mono VST plays it legato
            if (old >= 0) SendOff(old, kVst);
            monoVstKey_ = note;
        }
        lastMonoNote_ = note;
        return;
    }

    // New attack (Mono mode, first note, or a jump of more than two octaves).
    float fromPitch = sounding ? static_cast<float>(monoPitchedKey_) + glideCurrent_
                               : static_cast<float>(lastMonoNote_);
    if (sounding) SendOff(monoPitchedKey_, kPitched);
    monoPitchedKey_ = note;
    glideTarget_ = 0.0f;
    glideCurrent_ = (portamento && (sounding || lastMonoNote_ >= 0)) ? fromPitch - static_cast<float>(note) : 0.0f;
    SendOn(note, velocity, kPitched);

    int oldVst = monoVstKey_;
    if (legato && oldVst >= 0 && oldVst != note)
    {
        SendOn(note, velocity, kVst);
        SendOff(oldVst, kVst);
    }
    else
    {
        if (oldVst >= 0) SendOff(oldVst, kVst);
        SendOn(note, velocity, kVst);
    }
    monoVstKey_ = note;
    lastMonoNote_ = note;
}

void PerformanceController::AllNotesOff()
{
    std::lock_guard<std::mutex> lock(mutex_);
    for (int n = 0; n < 128; ++n)
    {
        if (soundingPitched_[static_cast<size_t>(n)]) SendOff(n, kPitched);
        if (soundingVst_[static_cast<size_t>(n)]) SendOff(n, kVst);
    }
    monoStack_.clear();
    monoPitchedKey_ = monoVstKey_ = -1;
    glideCurrent_ = glideTarget_ = 0.0f;
    arpHeld_.clear();
    arpSounding_.clear();
    arpLatched_ = false;
    keyDown_.fill(false);
    sustained_.fill(false);
    params_[Sustain].store(0.0f);
}

void PerformanceController::BuildArpSequence(std::vector<HeldNote>& out)
{
    out.clear();
    std::vector<HeldNote> base = arpHeld_;
    int mode = std::clamp(static_cast<int>(std::lround(Param(ArpMode))), 0, 5);
    if (mode != 4) // "as played" keeps the order the keys were pressed
        std::sort(base.begin(), base.end(), [](const HeldNote& a, const HeldNote& b) { return a.note < b.note; });
    int octaves = std::clamp(static_cast<int>(std::lround(Param(ArpOctaves))), 1, 4);
    for (int o = 0; o < octaves; ++o)
        for (const auto& h : base)
            if (h.note + o * 12 <= 127) out.push_back({h.note + o * 12, h.velocity});
    if (mode == 1) std::reverse(out.begin(), out.end());
}

void PerformanceController::ArpStep()
{
    int mode = std::clamp(static_cast<int>(std::lround(Param(ArpMode))), 0, 5);
    std::vector<HeldNote> notes;
    if (mode == 5)
    {
        // Chord: the whole chord on every step, moving through the octaves.
        int octaves = std::clamp(static_cast<int>(std::lround(Param(ArpOctaves))), 1, 4);
        int o = arpIndex_ % octaves;
        for (const auto& h : arpHeld_)
            if (h.note + o * 12 <= 127) notes.push_back({h.note + o * 12, h.velocity});
        ++arpIndex_;
    }
    else
    {
        std::vector<HeldNote> seq;
        BuildArpSequence(seq);
        int n = static_cast<int>(seq.size());
        if (n == 0) return;
        int idx = 0;
        if (mode == 2 && n > 1)
        {
            int cycle = 2 * n - 2;
            int pos = arpIndex_ % cycle;
            idx = pos < n ? pos : cycle - pos;
            ++arpIndex_;
        }
        else if (mode == 3)
        {
            random_ = random_ * 1664525u + 1013904223u;
            idx = static_cast<int>((random_ >> 8) % static_cast<uint32_t>(n));
        }
        else
        {
            idx = arpIndex_ % n;
            ++arpIndex_;
        }
        notes.push_back(seq[static_cast<size_t>(idx)]);
    }

    double bpm = std::clamp(static_cast<double>(Param(ArpTempo)), 20.0, 300.0);
    int rate = std::clamp(static_cast<int>(std::lround(Param(ArpRate))), 0, kArpRateCount - 1);
    double stepSamples = 60.0 / bpm * kArpBeats[rate] * sampleRate_;
    double gate = std::clamp(static_cast<double>(Param(ArpGate)), 0.05, 1.0) * stepSamples;
    if (gate > stepSamples - 32) gate = stepSamples - 32; // full gate still re-attacks

    for (const auto& h : notes)
    {
        for (auto it = arpSounding_.begin(); it != arpSounding_.end();)
        {
            if (it->note == h.note) { SendOff(it->note, kAll); it = arpSounding_.erase(it); }
            else ++it;
        }
        SendOn(h.note, h.velocity, kAll);
        arpSounding_.push_back({h.note, gate});
    }
}

void PerformanceController::Advance(int frames)
{
    if (frames <= 0) return;
    std::lock_guard<std::mutex> lock(mutex_);
    const double sr = sampleRate_;
    const bool arpOn = Param(ArpOn) >= 0.5f;

    // ---- Arpeggiator ----
    for (auto it = arpSounding_.begin(); it != arpSounding_.end();)
    {
        it->samplesLeft -= frames;
        if (it->samplesLeft <= 0) { SendOff(it->note, kAll); it = arpSounding_.erase(it); }
        else ++it;
    }
    if (arpOn && !arpHeld_.empty())
    {
        double bpm = std::clamp(static_cast<double>(Param(ArpTempo)), 20.0, 300.0);
        int rate = std::clamp(static_cast<int>(std::lround(Param(ArpRate))), 0, kArpRateCount - 1);
        double stepSamples = 60.0 / bpm * kArpBeats[rate] * sr;
        int guard = 0;
        while (arpCounter_ <= 0.0 && guard++ < 8)
        {
            ArpStep();
            arpCounter_ += stepSamples;
        }
        arpCounter_ -= frames;
    }
    else
    {
        arpCounter_ = 0.0;
    }

    // ---- Pitch bend (keys: moves like a real wheel and springs back) ----
    float target = bendTarget_.load();
    float bend = bendCurrent_.load();
    if (bendSmooth_.load())
    {
        float seconds = std::fabs(target) < 0.001f ? 0.12f : 0.25f;
        float step = static_cast<float>(frames / (sr * seconds));
        if (bend < target) bend = std::min(target, bend + step);
        else if (bend > target) bend = std::max(target, bend - step);
    }
    else
    {
        bend = target;
    }
    bendCurrent_.store(bend);

    // ---- Vibrato from the modulation wheel ----
    float depth = Param(ModWheel) * std::clamp(Param(VibratoDepth), 0.0f, 2.0f);
    vibratoPhase_ += std::clamp(Param(VibratoRate), 0.5f, 15.0f) * frames / sr;
    if (vibratoPhase_ >= 1.0) vibratoPhase_ -= std::floor(vibratoPhase_);
    float vibrato = depth * static_cast<float>(std::sin(kTwoPi * vibratoPhase_));

    // ---- Mono/Legato glide ----
    if (Param(PortamentoOn) >= 0.5f)
    {
        double tau = std::max(0.002, static_cast<double>(Param(PortamentoTime)) / 4.6);
        float coef = static_cast<float>(1.0 - std::exp(-frames / (tau * sr)));
        glideCurrent_ += (glideTarget_ - glideCurrent_) * coef;
        if (std::fabs(glideTarget_ - glideCurrent_) < 0.0005f) glideCurrent_ = glideTarget_;
    }
    else
    {
        glideCurrent_ = glideTarget_;
    }
    float glide = (Mode() != 0 && !arpOn) ? glideCurrent_ : 0.0f;

    float range = std::clamp(Param(BendRange), 0.0f, 24.0f);
    pitchOffset_.store(bend * range + vibrato + glide);
}
