#include "KeyboardFx.h"

#include <algorithm>
#include <cmath>

using namespace SoundParam;

namespace
{
    constexpr double kTwoPi = 6.283185307179586;
    constexpr float kPi = 3.14159265f;

    inline float Clamp(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }
    inline float DbToGain(float db) { return std::pow(10.0f, db / 20.0f); }
    inline float Coef(float seconds, double sampleRate)
    {
        return static_cast<float>(1.0 - std::exp(-1.0 / (std::max(0.0001, static_cast<double>(seconds)) * sampleRate)));
    }
    inline void Advance(double& phase, double inc)
    {
        phase += inc;
        if (phase >= 1.0) phase -= std::floor(phase);
    }
    inline float Lfo(double phase) { return static_cast<float>(std::sin(kTwoPi * phase)); }

    // RBJ cookbook filters
    void Shelf(float* c, bool high, float freq, float gainDb, double sr)
    {
        double A = std::pow(10.0, gainDb / 40.0);
        double w0 = kTwoPi * freq / sr;
        double cs = std::cos(w0), sn = std::sin(w0);
        double alpha = sn / 2.0 * std::sqrt(2.0); // shelf slope 1
        double sqA = 2.0 * std::sqrt(A) * alpha;
        double b0, b1, b2, a0, a1, a2;
        if (!high)
        {
            b0 = A * ((A + 1) - (A - 1) * cs + sqA);
            b1 = 2 * A * ((A - 1) - (A + 1) * cs);
            b2 = A * ((A + 1) - (A - 1) * cs - sqA);
            a0 = (A + 1) + (A - 1) * cs + sqA;
            a1 = -2 * ((A - 1) + (A + 1) * cs);
            a2 = (A + 1) + (A - 1) * cs - sqA;
        }
        else
        {
            b0 = A * ((A + 1) + (A - 1) * cs + sqA);
            b1 = -2 * A * ((A - 1) + (A + 1) * cs);
            b2 = A * ((A + 1) + (A - 1) * cs - sqA);
            a0 = (A + 1) - (A - 1) * cs + sqA;
            a1 = 2 * ((A - 1) - (A + 1) * cs);
            a2 = (A + 1) - (A - 1) * cs - sqA;
        }
        c[0] = static_cast<float>(b0 / a0); c[1] = static_cast<float>(b1 / a0); c[2] = static_cast<float>(b2 / a0);
        c[3] = static_cast<float>(a1 / a0); c[4] = static_cast<float>(a2 / a0);
    }
    void Peak(float* c, float freq, float gainDb, float q, double sr)
    {
        double A = std::pow(10.0, gainDb / 40.0);
        double w0 = kTwoPi * freq / sr;
        double alpha = std::sin(w0) / (2.0 * q);
        double cs = std::cos(w0);
        double a0 = 1 + alpha / A;
        c[0] = static_cast<float>((1 + alpha * A) / a0);
        c[1] = static_cast<float>((-2 * cs) / a0);
        c[2] = static_cast<float>((1 - alpha * A) / a0);
        c[3] = static_cast<float>((-2 * cs) / a0);
        c[4] = static_cast<float>((1 - alpha / A) / a0);
    }

    const int kCombTuning[8] = {1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617};
    const int kAllpassTuning[4] = {556, 441, 341, 225};
    constexpr int kStereoSpread = 23;

    // index of each effect's "on" switch in wasOn_
    inline int Slot(int onId) { return (onId - FxFirst) / 10; }
}

float KeyboardFx::DefaultValue(int id)
{
    switch (id)
    {
    case FxOn: return 1.0f;
    case DriveOn: return 0.0f; case DriveAmount: return 0.4f; case DriveTone: return 0.6f; case DriveLevel: return 0.5f;
    case FilterOn: return 0.0f; case FilterType: return 0.0f; case FilterCutoff: return 0.7f; case FilterResonance: return 0.3f;
    case FilterLfoRate: return 2.0f; case FilterLfoDepth: return 0.0f; case FilterEnvDepth: return 0.0f;
    case EqOn: return 0.0f; case EqLow: return 0.0f; case EqMid: return 0.0f; case EqMidFreq: return 1000.0f; case EqHigh: return 0.0f;
    case CompOn: return 0.0f; case CompThreshold: return -18.0f; case CompRatio: return 4.0f; case CompAttack: return 10.0f;
    case CompRelease: return 150.0f; case CompMakeup: return 4.0f;
    case ChorusOn: return 0.0f; case ChorusRate: return 0.8f; case ChorusDepth: return 0.5f; case ChorusMix: return 0.5f;
    case FlangerOn: return 0.0f; case FlangerRate: return 0.25f; case FlangerDepth: return 0.7f; case FlangerFeedback: return 0.5f; case FlangerMix: return 0.5f;
    case PhaserOn: return 0.0f; case PhaserRate: return 0.5f; case PhaserDepth: return 0.7f; case PhaserFeedback: return 0.4f; case PhaserMix: return 0.5f;
    case TremoloOn: return 0.0f; case TremoloRate: return 5.0f; case TremoloDepth: return 0.5f;
    case AutoPanOn: return 0.0f; case AutoPanRate: return 1.0f; case AutoPanDepth: return 0.7f;
    case RotaryOn: return 0.0f; case RotaryFast: return 0.0f; case RotaryMix: return 1.0f;
    case DelayOn: return 0.0f; case DelayTime: return 375.0f; case DelayFeedback: return 0.35f; case DelayMix: return 0.3f;
    case DelayPingPong: return 1.0f; case DelayTone: return 0.4f;
    case ReverbOn: return 1.0f; case ReverbSize: return 0.5f; case ReverbDamping: return 0.5f; case ReverbMix: return 0.18f; case ReverbWidth: return 1.0f;
    default: return 0.0f;
    }
}

KeyboardFx::KeyboardFx()
{
    for (int id = FxFirst; id <= FxLast; ++id)
    {
        params_[id - FxFirst].store(DefaultValue(id));
        seen_[id - FxFirst] = -12345.0f;
    }
}

void KeyboardFx::Prepare(double sampleRate)
{
    sampleRate_ = sampleRate;
    double scale = sampleRate / 44100.0;
    for (int ch = 0; ch < 2; ++ch)
    {
        chorus_[ch].Init(static_cast<int>(0.05 * sampleRate));
        flanger_[ch].Init(static_cast<int>(0.02 * sampleRate));
        delay_[ch].Init(static_cast<int>(2.1 * sampleRate));
    }
    rotHorn_.Init(static_cast<int>(0.01 * sampleRate));
    rotDrum_.Init(static_cast<int>(0.01 * sampleRate));
    for (int i = 0; i < 8; ++i)
    {
        combL_[i].buffer.assign(static_cast<size_t>(kCombTuning[i] * scale), 0.0f);
        combR_[i].buffer.assign(static_cast<size_t>((kCombTuning[i] + kStereoSpread) * scale), 0.0f);
    }
    for (int i = 0; i < 4; ++i)
    {
        apL_[i].buffer.assign(static_cast<size_t>(kAllpassTuning[i] * scale), 0.0f);
        apR_[i].buffer.assign(static_cast<size_t>((kAllpassTuning[i] + kStereoSpread) * scale), 0.0f);
    }
    ClearState();
    for (auto& s : seen_) s = -12345.0f;
    wasOn_.fill(false);
}

void KeyboardFx::ClearState()
{
    driveTone_[0] = driveTone_[1] = 0;
    svf1_[0] = svf1_[1] = svf2_[0] = svf2_[1] = 0;
    filterEnv_ = 0;
    eqLow_.Clear(); eqMid_.Clear(); eqHigh_.Clear();
    compEnv_ = 0; compGain_ = 1;
    for (int ch = 0; ch < 2; ++ch)
    {
        chorus_[ch].Clear(); flanger_[ch].Clear(); delay_[ch].Clear();
        flangerFb_[ch] = 0; phaserFb_[ch] = 0; delayDamp_[ch] = 0;
        for (float& s : phaserState_[ch]) s = 0;
    }
    rotHorn_.Clear(); rotDrum_.Clear(); rotLow_ = 0;
    for (auto& c : combL_) { std::fill(c.buffer.begin(), c.buffer.end(), 0.0f); c.store = 0; }
    for (auto& c : combR_) { std::fill(c.buffer.begin(), c.buffer.end(), 0.0f); c.store = 0; }
    for (auto& a : apL_) std::fill(a.buffer.begin(), a.buffer.end(), 0.0f);
    for (auto& a : apR_) std::fill(a.buffer.begin(), a.buffer.end(), 0.0f);
}

void KeyboardFx::SetParam(int id, float value)
{
    if (!IsFxParam(id) || !std::isfinite(value)) return;
    params_[id - FxFirst].store(value);
}

float KeyboardFx::GetParam(int id) const
{
    if (!IsFxParam(id)) return 0.0f;
    return params_[id - FxFirst].load();
}

void KeyboardFx::UpdateEq()
{
    float c[5];
    Shelf(c, false, 200.0f, Clamp(P(EqLow), -15, 15), sampleRate_);
    eqLow_.b0 = c[0]; eqLow_.b1 = c[1]; eqLow_.b2 = c[2]; eqLow_.a1 = c[3]; eqLow_.a2 = c[4];
    Peak(c, Clamp(P(EqMidFreq), 200, 5000), Clamp(P(EqMid), -15, 15), 0.8f, sampleRate_);
    eqMid_.b0 = c[0]; eqMid_.b1 = c[1]; eqMid_.b2 = c[2]; eqMid_.a1 = c[3]; eqMid_.a2 = c[4];
    Shelf(c, true, 4000.0f, Clamp(P(EqHigh), -15, 15), sampleRate_);
    eqHigh_.b0 = c[0]; eqHigh_.b1 = c[1]; eqHigh_.b2 = c[2]; eqHigh_.a1 = c[3]; eqHigh_.a2 = c[4];
}

void KeyboardFx::Process(float* io, int frames)
{
    if (frames <= 0 || P(FxOn) < 0.5f) return;
    const double sr = sampleRate_;
    const float fsr = static_cast<float>(sr);

    // Effects that were just switched on start from silence (no stale echoes).
    auto justOn = [this](int onId) {
        bool on = P(onId) >= 0.5f;
        bool was = wasOn_[static_cast<size_t>(Slot(onId))];
        wasOn_[static_cast<size_t>(Slot(onId))] = on;
        return on && !was;
    };
    if (justOn(DriveOn)) { driveTone_[0] = driveTone_[1] = 0; }
    if (justOn(FilterOn)) { svf1_[0] = svf1_[1] = svf2_[0] = svf2_[1] = 0; filterEnv_ = 0; }
    if (justOn(EqOn)) { eqLow_.Clear(); eqMid_.Clear(); eqHigh_.Clear(); }
    if (justOn(CompOn)) { compEnv_ = 0; compGain_ = 1; }
    if (justOn(ChorusOn)) { chorus_[0].Clear(); chorus_[1].Clear(); }
    if (justOn(FlangerOn)) { flanger_[0].Clear(); flanger_[1].Clear(); flangerFb_[0] = flangerFb_[1] = 0; }
    if (justOn(PhaserOn)) { for (auto& ch : phaserState_) for (float& s : ch) s = 0; phaserFb_[0] = phaserFb_[1] = 0; }
    justOn(TremoloOn);
    justOn(AutoPanOn);
    if (justOn(RotaryOn)) { rotHorn_.Clear(); rotDrum_.Clear(); rotLow_ = 0; }
    if (justOn(DelayOn)) { delay_[0].Clear(); delay_[1].Clear(); delayDamp_[0] = delayDamp_[1] = 0; delayTimeSmooth_ = -1; }
    if (justOn(ReverbOn))
    {
        for (auto& c : combL_) { std::fill(c.buffer.begin(), c.buffer.end(), 0.0f); c.store = 0; }
        for (auto& c : combR_) { std::fill(c.buffer.begin(), c.buffer.end(), 0.0f); c.store = 0; }
        for (auto& a : apL_) std::fill(a.buffer.begin(), a.buffer.end(), 0.0f);
        for (auto& a : apR_) std::fill(a.buffer.begin(), a.buffer.end(), 0.0f);
    }

    // ---- Overdrive ----
    if (P(DriveOn) >= 0.5f)
    {
        float amount = Clamp(P(DriveAmount), 0, 1);
        float g = 1.0f + amount * amount * 40.0f;
        float norm = 1.0f / std::tanh(std::min(g, 3.0f) * 0.7f + 0.3f);
        float level = Clamp(P(DriveLevel), 0, 1) * 1.2f;
        float toneHz = 1000.0f * std::pow(16.0f, Clamp(P(DriveTone), 0, 1));
        float tc = 1.0f - std::exp(-2.0f * kPi * std::min(toneHz, fsr * 0.45f) / fsr);
        for (int i = 0; i < frames; ++i)
            for (int ch = 0; ch < 2; ++ch)
            {
                float x = io[i * 2 + ch];
                float y = std::tanh(g * x + 0.15f * amount * x * x) * norm; // a little asymmetry = warmer
                Lowpass1(driveTone_[ch], y, tc);
                io[i * 2 + ch] = driveTone_[ch] * level;
            }
    }

    // ---- Filter / auto-wah ----
    if (P(FilterOn) >= 0.5f)
    {
        int type = static_cast<int>(P(FilterType) + 0.5f);
        float base = Clamp(P(FilterCutoff), 0, 1);
        float res = Clamp(P(FilterResonance), 0, 1);
        float k = 2.0f - 1.94f * res;
        double lfoInc = Clamp(P(FilterLfoRate), 0.05f, 20.0f) / sr;
        float lfoDepth = Clamp(P(FilterLfoDepth), 0, 1);
        float envDepth = Clamp(P(FilterEnvDepth), 0, 1);
        float envAtt = Coef(0.004f, sr), envRel = Coef(0.15f, sr);
        float a1 = 1, a2 = 0, a3 = 0;
        for (int i = 0; i < frames; ++i)
        {
            float mono = 0.5f * (std::fabs(io[i * 2]) + std::fabs(io[i * 2 + 1]));
            filterEnv_ += (mono - filterEnv_) * (mono > filterEnv_ ? envAtt : envRel);
            if ((i & 15) == 0)
            {
                float c = base + lfoDepth * 0.35f * Lfo(filterLfoPhase_) + envDepth * 0.7f * std::min(1.0f, filterEnv_ * 3.0f);
                c = Clamp(c, 0, 1);
                float fc = std::min(20.0f * std::pow(1000.0f, c), fsr * 0.45f);
                float g = std::tan(kPi * fc / fsr);
                a1 = 1.0f / (1.0f + g * (g + k));
                a2 = g * a1;
                a3 = g * a2;
            }
            Advance(filterLfoPhase_, lfoInc);
            for (int ch = 0; ch < 2; ++ch)
            {
                float x = io[i * 2 + ch];
                float v3 = x - svf2_[ch];
                float v1 = a1 * svf1_[ch] + a2 * v3;
                float v2 = svf2_[ch] + a2 * svf1_[ch] + a3 * v3;
                svf1_[ch] = 2 * v1 - svf1_[ch];
                svf2_[ch] = 2 * v2 - svf2_[ch];
                float y = type == 1 ? (x - k * v1 - v2) : type == 2 ? (k * v1) : v2;
                io[i * 2 + ch] = y;
            }
        }
    }

    // ---- 3-band EQ ----
    if (P(EqOn) >= 0.5f)
    {
        if (seen_[EqLow - FxFirst] != P(EqLow) || seen_[EqMid - FxFirst] != P(EqMid) ||
            seen_[EqMidFreq - FxFirst] != P(EqMidFreq) || seen_[EqHigh - FxFirst] != P(EqHigh))
        {
            seen_[EqLow - FxFirst] = P(EqLow); seen_[EqMid - FxFirst] = P(EqMid);
            seen_[EqMidFreq - FxFirst] = P(EqMidFreq); seen_[EqHigh - FxFirst] = P(EqHigh);
            UpdateEq();
        }
        for (int i = 0; i < frames; ++i)
            for (int ch = 0; ch < 2; ++ch)
            {
                float x = io[i * 2 + ch];
                x = eqLow_.Run(x, ch);
                x = eqMid_.Run(x, ch);
                io[i * 2 + ch] = eqHigh_.Run(x, ch);
            }
    }

    // ---- Compressor ----
    if (P(CompOn) >= 0.5f)
    {
        float thr = Clamp(P(CompThreshold), -60, 0);
        float ratio = Clamp(P(CompRatio), 1, 20);
        float att = Coef(Clamp(P(CompAttack), 0.1f, 200) / 1000.0f, sr);
        float rel = Coef(Clamp(P(CompRelease), 10, 2000) / 1000.0f, sr);
        float makeup = DbToGain(Clamp(P(CompMakeup), 0, 24));
        for (int i = 0; i < frames; ++i)
        {
            float peak = std::max(std::fabs(io[i * 2]), std::fabs(io[i * 2 + 1]));
            compEnv_ += (peak - compEnv_) * (peak > compEnv_ ? att : rel);
            float levelDb = 20.0f * std::log10(std::max(compEnv_, 1e-6f));
            float over = levelDb - thr;
            float target = over > 0 ? DbToGain(-over * (1.0f - 1.0f / ratio)) : 1.0f;
            compGain_ += (target - compGain_) * 0.01f;
            io[i * 2] *= compGain_ * makeup;
            io[i * 2 + 1] *= compGain_ * makeup;
        }
    }

    // ---- Chorus ----
    if (P(ChorusOn) >= 0.5f)
    {
        double inc = Clamp(P(ChorusRate), 0.05f, 10) / sr;
        float depth = Clamp(P(ChorusDepth), 0, 1) * 0.006f * fsr;
        float base = 0.012f * fsr;
        float mix = Clamp(P(ChorusMix), 0, 1);
        for (int i = 0; i < frames; ++i)
        {
            for (int ch = 0; ch < 2; ++ch)
            {
                float x = io[i * 2 + ch];
                chorus_[ch].Push(x);
                float lfo = 0.5f + 0.5f * Lfo(chorusPhase_ + (ch ? 0.25 : 0.0));
                float wet = chorus_[ch].Read(base + depth * lfo);
                io[i * 2 + ch] = x * (1.0f - mix * 0.5f) + wet * mix * 0.7f;
            }
            Advance(chorusPhase_, inc);
        }
    }

    // ---- Flanger ----
    if (P(FlangerOn) >= 0.5f)
    {
        double inc = Clamp(P(FlangerRate), 0.02f, 5) / sr;
        float depth = Clamp(P(FlangerDepth), 0, 1) * 0.004f * fsr;
        float base = 0.0008f * fsr;
        float fb = Clamp(P(FlangerFeedback), 0, 1) * 0.9f;
        float mix = Clamp(P(FlangerMix), 0, 1);
        for (int i = 0; i < frames; ++i)
        {
            for (int ch = 0; ch < 2; ++ch)
            {
                float x = io[i * 2 + ch];
                flanger_[ch].Push(x + flangerFb_[ch] * fb);
                float lfo = 0.5f + 0.5f * Lfo(flangerPhase_ + (ch ? 0.25 : 0.0));
                float wet = flanger_[ch].Read(base + depth * lfo);
                flangerFb_[ch] = wet;
                io[i * 2 + ch] = x * (1.0f - mix * 0.5f) + wet * mix * 0.6f;
            }
            Advance(flangerPhase_, inc);
        }
    }

    // ---- Phaser ----
    if (P(PhaserOn) >= 0.5f)
    {
        double inc = Clamp(P(PhaserRate), 0.02f, 8) / sr;
        float depth = Clamp(P(PhaserDepth), 0, 1);
        float fb = Clamp(P(PhaserFeedback), 0, 1) * 0.85f;
        float mix = Clamp(P(PhaserMix), 0, 1);
        float coef[2] = {0, 0};
        for (int i = 0; i < frames; ++i)
        {
            if ((i & 15) == 0)
            {
                for (int ch = 0; ch < 2; ++ch)
                {
                    float lfo = 0.5f + 0.5f * Lfo(phaserPhase_ + (ch ? 0.25 : 0.0));
                    float f = 250.0f * std::pow(2.0f, depth * 4.0f * lfo);
                    float t = std::tan(kPi * std::min(f, fsr * 0.45f) / fsr);
                    coef[ch] = (t - 1.0f) / (t + 1.0f);
                }
            }
            Advance(phaserPhase_, inc);
            for (int ch = 0; ch < 2; ++ch)
            {
                float x = io[i * 2 + ch];
                float y = x + phaserFb_[ch] * fb;
                for (float& z : phaserState_[ch])
                {
                    float out = coef[ch] * y + z;
                    z = y - coef[ch] * out;
                    y = out;
                }
                phaserFb_[ch] = y;
                io[i * 2 + ch] = x * (1.0f - mix * 0.5f) + y * mix * 0.6f;
            }
        }
    }

    // ---- Tremolo ----
    if (P(TremoloOn) >= 0.5f)
    {
        double inc = Clamp(P(TremoloRate), 0.1f, 20) / sr;
        float depth = Clamp(P(TremoloDepth), 0, 1);
        for (int i = 0; i < frames; ++i)
        {
            float g = 1.0f - depth * (0.5f - 0.5f * static_cast<float>(std::cos(kTwoPi * tremoloPhase_)));
            io[i * 2] *= g;
            io[i * 2 + 1] *= g;
            Advance(tremoloPhase_, inc);
        }
    }

    // ---- Auto-pan ----
    if (P(AutoPanOn) >= 0.5f)
    {
        double inc = Clamp(P(AutoPanRate), 0.05f, 20) / sr;
        float depth = Clamp(P(AutoPanDepth), 0, 1);
        for (int i = 0; i < frames; ++i)
        {
            float pos = depth * Lfo(panPhase_);
            io[i * 2] *= std::sqrt(1.0f - pos);
            io[i * 2 + 1] *= std::sqrt(1.0f + pos);
            Advance(panPhase_, inc);
        }
    }

    // ---- Rotary speaker (Leslie) ----
    if (P(RotaryOn) >= 0.5f)
    {
        bool fast = P(RotaryFast) >= 0.5f;
        float mix = Clamp(P(RotaryMix), 0, 1);
        float hornTarget = fast ? 6.8f : 0.8f, drumTarget = fast ? 5.9f : 0.67f;
        float hornAcc = Coef(fast ? 0.7f : 0.9f, sr), drumAcc = Coef(fast ? 3.0f : 4.0f, sr);
        float split = 1.0f - std::exp(-2.0f * kPi * 800.0f / fsr);
        float hornBase = 0.0015f * fsr, hornDepth = 0.00035f * fsr;
        float drumBase = 0.002f * fsr, drumDepth = 0.0002f * fsr;
        for (int i = 0; i < frames; ++i)
        {
            hornSpeed_ += (hornTarget - hornSpeed_) * hornAcc;
            drumSpeed_ += (drumTarget - drumSpeed_) * drumAcc;
            float mono = 0.5f * (io[i * 2] + io[i * 2 + 1]);
            Lowpass1(rotLow_, mono, split);
            float high = mono - rotLow_;
            rotHorn_.Push(high);
            rotDrum_.Push(rotLow_);
            float hs = Lfo(hornPhase_), ds = Lfo(drumPhase_);
            float hornL = rotHorn_.Read(hornBase + hornDepth * hs) * (1.0f + 0.45f * hs);
            float hornR = rotHorn_.Read(hornBase - hornDepth * hs) * (1.0f - 0.45f * hs);
            float drumL = rotDrum_.Read(drumBase + drumDepth * ds) * (1.0f + 0.25f * ds);
            float drumR = rotDrum_.Read(drumBase - drumDepth * ds) * (1.0f - 0.25f * ds);
            io[i * 2] = io[i * 2] * (1.0f - mix) + (hornL + drumL) * mix;
            io[i * 2 + 1] = io[i * 2 + 1] * (1.0f - mix) + (hornR + drumR) * mix;
            Advance(hornPhase_, hornSpeed_ / sr);
            Advance(drumPhase_, drumSpeed_ / sr);
        }
    }

    // ---- Delay ----
    if (P(DelayOn) >= 0.5f)
    {
        float target = Clamp(P(DelayTime), 20, 2000) / 1000.0f * fsr;
        if (delayTimeSmooth_ < 0) delayTimeSmooth_ = target;
        float fb = Clamp(P(DelayFeedback), 0, 0.95f);
        float mix = Clamp(P(DelayMix), 0, 1);
        bool ping = P(DelayPingPong) >= 0.5f;
        float damp = 1.0f - Clamp(P(DelayTone), 0, 1) * 0.85f;
        float smooth = Coef(0.08f, sr);
        for (int i = 0; i < frames; ++i)
        {
            delayTimeSmooth_ += (target - delayTimeSmooth_) * smooth;
            float wetL = delay_[0].Read(delayTimeSmooth_);
            float wetR = delay_[1].Read(delayTimeSmooth_);
            Lowpass1(delayDamp_[0], wetL, damp);
            Lowpass1(delayDamp_[1], wetR, damp);
            float l = io[i * 2], r = io[i * 2 + 1];
            if (ping)
            {
                delay_[0].Push(0.5f * (l + r) + delayDamp_[1] * fb);
                delay_[1].Push(delayDamp_[0] * fb);
            }
            else
            {
                delay_[0].Push(l + delayDamp_[0] * fb);
                delay_[1].Push(r + delayDamp_[1] * fb);
            }
            io[i * 2] = l + wetL * mix;
            io[i * 2 + 1] = r + wetR * mix;
        }
    }

    // ---- Reverb (Freeverb) ----
    if (P(ReverbOn) >= 0.5f)
    {
        float room = 0.7f + Clamp(P(ReverbSize), 0, 1) * 0.28f;
        float damp = Clamp(P(ReverbDamping), 0, 1) * 0.4f;
        float mix = Clamp(P(ReverbMix), 0, 1);
        float width = Clamp(P(ReverbWidth), 0, 1);
        float wet = mix * 2.0f;
        float wet1 = wet * (width / 2.0f + 0.5f), wet2 = wet * ((1.0f - width) / 2.0f);
        float dry = 1.0f - mix * 0.5f;
        for (int i = 0; i < frames; ++i)
        {
            float l = io[i * 2], r = io[i * 2 + 1];
            float input = (l + r) * 0.015f;
            float outL = 0, outR = 0;
            for (int c = 0; c < 8; ++c)
            {
                outL += combL_[static_cast<size_t>(c)].Run(input, room, damp);
                outR += combR_[static_cast<size_t>(c)].Run(input, room, damp);
            }
            for (int a = 0; a < 4; ++a)
            {
                outL = apL_[static_cast<size_t>(a)].Run(outL);
                outR = apR_[static_cast<size_t>(a)].Run(outR);
            }
            io[i * 2] = l * dry + outL * wet1 + outR * wet2;
            io[i * 2 + 1] = r * dry + outR * wet1 + outL * wet2;
        }
    }

    // Safety: never pass NaN/inf on to the sound card.
    for (int i = 0; i < frames * 2; ++i)
        if (!std::isfinite(io[i])) io[i] = 0.0f;
}
