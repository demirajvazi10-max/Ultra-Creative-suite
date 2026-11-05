#pragma once

// Ultra Composer 2.0 - one numbered list of every "sound" setting of the
// performance keyboard: performance controls (pitch bend, modulation,
// portamento, mono/legato, arpeggiator...), the Ultra FM synthesizer and
// the keyboard effects rack.
//
// The C API exposes them all through just two calls, UC_SetSoundParam(id, value)
// and UC_GetSoundParam(id), so adding a setting never needs new exports.
// The numbers are part of the saved project format and of the C# side
// (src/App/SoundParameters.cs mirrors this list) - never renumber, only add.
namespace SoundParam
{
    // ---- Performance (0-49) ----
    constexpr int PitchBend = 0;          // -1..1, hardware wheel (instant)
    constexpr int ModWheel = 1;           // 0..1
    constexpr int Sustain = 2;            // 0/1 sustain pedal
    constexpr int BendRange = 3;          // 1..24 semitones
    constexpr int VibratoDepth = 4;       // 0..2 semitones at full modulation
    constexpr int VibratoRate = 5;        // 1..12 Hz
    constexpr int PortamentoOn = 6;       // 0/1
    constexpr int PortamentoTime = 7;     // 0.01..2 seconds
    constexpr int VoiceMode = 8;          // 0 Poly, 1 Mono, 2 Legato
    constexpr int Transpose = 9;          // -12..12 semitones
    constexpr int ArpOn = 10;             // 0/1
    constexpr int ArpMode = 11;           // 0 Up, 1 Down, 2 Up-down, 3 Random, 4 As played, 5 Chord
    constexpr int ArpRate = 12;           // 0 1/4, 1 1/8, 2 1/8T, 3 1/16, 4 1/16T, 5 1/32
    constexpr int ArpOctaves = 13;        // 1..4
    constexpr int ArpGate = 14;           // 0.1..1
    constexpr int ArpLatch = 15;          // 0/1
    constexpr int ArpTempo = 16;          // 40..240 BPM
    constexpr int PitchBendKeys = 17;     // -1..1 target from the computer keyboard (moves smoothly, springs back)
    constexpr int PerformanceLast = 17;

    // ---- Ultra FM synthesizer (50-149) ----
    constexpr int FmPreset = 50;          // 0..preset count-1 (loads a whole patch)
    constexpr int FmAlgorithm = 51;       // 0..7
    constexpr int FmFeedback = 52;        // 0..1
    constexpr int FmVolume = 53;          // 0..1
    constexpr int FmBrightness = 54;      // 0..2 (scales every modulator)
    constexpr int FmOpBase = 60;          // operator k: FmOpBase + k*10 + field
    constexpr int FmOpStride = 10;
    constexpr int FmOpLevel = 0;          // 0..1
    constexpr int FmOpRatio = 1;          // 0.5..16
    constexpr int FmOpDetune = 2;         // -50..50 cents
    constexpr int FmOpAttack = 3;         // 0.001..5 s
    constexpr int FmOpDecay = 4;          // 0.01..10 s
    constexpr int FmOpSustain = 5;        // 0..1
    constexpr int FmOpRelease = 6;        // 0.01..10 s
    constexpr int FmOpVelocity = 7;       // 0..1 velocity sensitivity
    constexpr int FmOpCount = 6;
    constexpr int FmLast = FmOpBase + FmOpCount * FmOpStride - 1; // 119

    // ---- Effects rack (150-299) ----
    constexpr int FxOn = 150;             // master: all effects on/off
    constexpr int DriveOn = 160, DriveAmount = 161, DriveTone = 162, DriveLevel = 163;
    constexpr int FilterOn = 170, FilterType = 171, FilterCutoff = 172, FilterResonance = 173,
                  FilterLfoRate = 174, FilterLfoDepth = 175, FilterEnvDepth = 176;
    constexpr int EqOn = 180, EqLow = 181, EqMid = 182, EqMidFreq = 183, EqHigh = 184;
    constexpr int CompOn = 190, CompThreshold = 191, CompRatio = 192, CompAttack = 193, CompRelease = 194, CompMakeup = 195;
    constexpr int ChorusOn = 200, ChorusRate = 201, ChorusDepth = 202, ChorusMix = 203;
    constexpr int FlangerOn = 210, FlangerRate = 211, FlangerDepth = 212, FlangerFeedback = 213, FlangerMix = 214;
    constexpr int PhaserOn = 220, PhaserRate = 221, PhaserDepth = 222, PhaserFeedback = 223, PhaserMix = 224;
    constexpr int TremoloOn = 230, TremoloRate = 231, TremoloDepth = 232;
    constexpr int AutoPanOn = 240, AutoPanRate = 241, AutoPanDepth = 242;
    constexpr int RotaryOn = 250, RotaryFast = 251, RotaryMix = 252;
    constexpr int DelayOn = 260, DelayTime = 261, DelayFeedback = 262, DelayMix = 263, DelayPingPong = 264, DelayTone = 265;
    constexpr int ReverbOn = 270, ReverbSize = 271, ReverbDamping = 272, ReverbMix = 273, ReverbWidth = 274;
    constexpr int FxFirst = 150;
    constexpr int FxLast = 299;

    constexpr int Count = 300;
}
