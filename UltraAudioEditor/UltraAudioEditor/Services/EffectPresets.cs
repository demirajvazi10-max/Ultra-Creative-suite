using UltraAudioEditor.Models;
using UltraAudioEditor.Views;

namespace UltraAudioEditor.Services
{
    /// <summary>
    /// Gotova podešavanja efekata (presets), u stilu Sound Forge-a: izabereš "Katedrala"
    /// i reverb odmah zvuči kao katedrala. Preset samo postavlja parametre i uključuje
    /// efekat — posle se svaki parametar i dalje može fino podesiti klizačima.
    /// Naziv preseta je ključ iz Lang tabele (pr_...), pa je preveden na oba jezika.
    /// </summary>
    public sealed record EffectPreset(string NameKey, Action<TrackEffects> Apply);

    public static class EffectPresets
    {
        public static IReadOnlyList<EffectPreset> For(EffectType type) => type switch
        {
            // Reverb: mix, veličina prostora, prigušenje (0 = tvrdi zidovi), pre-delay (ms)
            EffectType.Reverb => new List<EffectPreset>
            {
                R("pr_rev_small_room", 0.18f, 0.30f, 0.60f, 5),
                R("pr_rev_bathroom",   0.32f, 0.50f, 0.10f, 3),
                R("pr_rev_room",       0.22f, 0.55f, 0.50f, 10),
                R("pr_rev_studio",     0.20f, 0.62f, 0.35f, 15),
                R("pr_rev_plate",      0.28f, 0.72f, 0.20f, 20),
                R("pr_rev_church",     0.32f, 0.82f, 0.40f, 30),
                R("pr_rev_hall",       0.32f, 0.86f, 0.45f, 35),
                R("pr_rev_large_hall", 0.38f, 0.92f, 0.40f, 50),
                R("pr_rev_cathedral",  0.45f, 0.97f, 0.35f, 75),
                R("pr_rev_arena",      0.42f, 0.95f, 0.55f, 110),
                R("pr_rev_cave",       0.50f, 0.93f, 0.08f, 45),
            },
            // Delay: vreme (s), povratnost, mix
            EffectType.Delay => new List<EffectPreset>
            {
                D("pr_dl_doubling", 0.035f, 0.00f, 0.45f),
                D("pr_dl_slapback", 0.11f,  0.05f, 0.45f),
                D("pr_dl_short",    0.20f,  0.25f, 0.35f),
                D("pr_dl_rhythm",   0.375f, 0.40f, 0.35f),
                D("pr_dl_canyon",   0.60f,  0.45f, 0.40f),
                D("pr_dl_long",     0.85f,  0.60f, 0.35f),
                D("pr_dl_space",    0.50f,  0.80f, 0.40f),
            },
            // Kompresor: prag (dB), odnos, attack (ms), release (ms), pojačanje (dB)
            EffectType.Compressor => new List<EffectPreset>
            {
                C("pr_cp_gentle_vocal", -18, 2.5f, 15, 150, 3),
                C("pr_cp_vocal_front",  -24, 4f,   5,  100, 6),
                C("pr_cp_podcast",      -20, 3f,   10, 120, 5),
                C("pr_cp_drums",        -15, 4f,   30, 80,  3),
                C("pr_cp_bass",         -20, 5f,   20, 150, 4),
                C("pr_cp_glue",         -12, 2f,   30, 200, 2),
                C("pr_cp_limiter",      -6,  20f,  1,  50,  4),
            },
            // Ekvilajzer: bas, sredina, visoki (dB)
            EffectType.Equalizer => new List<EffectPreset>
            {
                E("pr_eq_flat",     0,   0,  0),
                E("pr_eq_warm",     3,   0, -2, midHz: 2500, q: 0.8f),
                E("pr_eq_bright",  -1,   0,  4),
                E("pr_eq_presence",-2,   3,  2, midHz: 3000, q: 1.0f, hp: 80),
                E("pr_eq_loudness", 5,  -2,  4, midHz: 1000, q: 0.6f),
                E("pr_eq_bass",     7,   0,  0, lowHz: 120),
                E("pr_eq_lowcut",   0,   0,  0, hp: 100),
                E("pr_eq_deess",    0,  -6,  0, midHz: 6500, q: 2.5f),
                E("pr_eq_mud",      0,  -4,  0, midHz: 300, q: 1.2f),
                E("pr_eq_radio",   -2,   4, -2, midHz: 1800, q: 0.9f, hp: 150, lp: 6000),
                E("pr_eq_phone",    0,   6,  0, midHz: 1500, q: 0.8f, hp: 300, lp: 3400),
            },
            EffectType.NoiseGate => new List<EffectPreset>
            {
                new("pr_ng_gentle", fx => fx.GateThreshold = -60),
                new("pr_ng_voice",  fx => fx.GateThreshold = -50),
                new("pr_ng_medium", fx => fx.GateThreshold = -45),
                new("pr_ng_strong", fx => fx.GateThreshold = -35),
            },
            EffectType.BassBoost => new List<EffectPreset>
            {
                new("pr_bb_light",   fx => fx.BassGain = 3),
                new("pr_bb_medium",  fx => fx.BassGain = 6),
                new("pr_bb_strong",  fx => fx.BassGain = 10),
                new("pr_bb_extreme", fx => fx.BassGain = 16),
            },
            EffectType.PitchShift => new List<EffectPreset>
            {
                new("pr_ps_octave_down", fx => fx.PitchSemitones = -12),
                new("pr_ps_deep",        fx => fx.PitchSemitones = -5),
                new("pr_ps_lower",       fx => fx.PitchSemitones = -2),
                new("pr_ps_higher",      fx => fx.PitchSemitones = 2),
                new("pr_ps_child",       fx => fx.PitchSemitones = 5),
                new("pr_ps_chipmunk",    fx => fx.PitchSemitones = 8),
                new("pr_ps_octave_up",   fx => fx.PitchSemitones = 12),
            },
            // Chorus: dubina, brzina (Hz), mix
            EffectType.Chorus => new List<EffectPreset>
            {
                Ch("pr_ch_subtle",  0.30f, 0.5f, 0.35f),
                Ch("pr_ch_classic", 0.55f, 0.8f, 0.50f),
                Ch("pr_ch_rich",    0.80f, 0.6f, 0.60f),
                Ch("pr_ch_fast",    0.45f, 3.0f, 0.50f),
                Ch("pr_ch_vibrato", 0.40f, 5.5f, 1.00f),
            },
            _ => new List<EffectPreset>()
        };

        private static EffectPreset R(string k, float mix, float room, float damp, float pre) =>
            new(k, fx => { fx.ReverbMix = mix; fx.ReverbRoom = room; fx.ReverbDamping = damp; fx.ReverbPreDelay = pre; });
        private static EffectPreset D(string k, float time, float fb, float mix) =>
            new(k, fx => { fx.DelayTime = time; fx.DelayFeedback = fb; fx.DelayMix = mix; });
        private static EffectPreset C(string k, float thr, float ratio, float att, float rel, float makeup) =>
            new(k, fx => { fx.CompThreshold = thr; fx.CompRatio = ratio; fx.CompAttack = att; fx.CompRelease = rel; fx.CompMakeup = makeup; });
        private static EffectPreset E(string k, float low, float mid, float high,
            float lowHz = 200, float midHz = 1000, float q = 0.7f, float highHz = 8000, float hp = 0, float lp = 0) =>
            new(k, fx =>
            {
                fx.EqLow = low; fx.EqMid = mid; fx.EqHigh = high;
                fx.EqLowFreq = lowHz; fx.EqMidFreq = midHz; fx.EqMidQ = q; fx.EqHighFreq = highHz;
                fx.HighPassFreq = hp; fx.LowPassFreq = lp;
            });
        private static EffectPreset Ch(string k, float depth, float rate, float mix) =>
            new(k, fx => { fx.ChorusDepth = depth; fx.ChorusRate = rate; fx.ChorusMix = mix; });
    }
}
