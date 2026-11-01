using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace UltraVideoEditor
{
    public static class VideoEffects
    {
        public static string GetFFmpegFilter(VideoEffectData effect) => GetFFmpegFilter(effect, includeSpeed: true);

        /// <summary>v1.4 — the render applies speed itself (it also changes how much of the source is read).</summary>
        public static string GetFFmpegFilter(VideoEffectData effect, bool includeSpeed)
        {
            if (effect == null) return "";
            var f = new List<string>();
            string N(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

            // brightness, contrast, saturation and mid-tones in one eq filter
            if (effect.Brightness != 0 || effect.Contrast != 0 || effect.Saturation != 0 || effect.Gamma != 0)
            {
                double brightness = effect.Brightness / 100.0;
                double contrast = effect.Contrast / 100.0 + 1.0;
                double saturation = Math.Clamp(1.0 + effect.Saturation / 100.0, 0, 3);
                // +100 → gamma 2 (lighter mid-tones), -100 → gamma 0.5
                double gamma = Math.Pow(2, effect.Gamma / 100.0);
                f.Add($"eq=brightness={N(brightness)}:contrast={N(contrast)}:saturation={N(saturation)}:gamma={N(gamma)}");
            }
            // v2.0: warmer / cooler (6500 K is neutral; lower Kelvin = warmer picture)
            if (effect.Temperature != 0)
                f.Add($"colortemperature=temperature={N(Math.Clamp(6500 - effect.Temperature * 25, 1000, 40000))}");
            // green ↔ magenta in the mid-tones
            if (effect.Tint != 0)
            {
                double t = Math.Clamp(effect.Tint / 100.0 * 0.3, -1, 1);
                f.Add($"colorbalance=rm={N(t)}:gm={N(-t)}:bm={N(t)}");
            }
            // shadows and highlights: lift or lower the dark and the bright end of the curve
            if (effect.Shadows != 0 || effect.Highlights != 0)
            {
                double sh = Math.Clamp(0.25 + effect.Shadows / 100.0 * 0.15, 0.02, 0.5);
                double hi = Math.Clamp(0.75 + effect.Highlights / 100.0 * 0.15, 0.5, 0.98);
                f.Add($"curves=all='0/0 0.25/{N(sh)} 0.75/{N(hi)} 1/1'");
            }
            // v2.0: colour balance per tonal range (shadows, mid-tones, highlights)
            if (effect.HasBalance)
            {
                double B(double v) => Math.Clamp(v / 100.0 * 0.4, -1, 1);
                f.Add($"colorbalance=rs={N(B(effect.ShadowR))}:gs={N(B(effect.ShadowG))}:bs={N(B(effect.ShadowB))}" +
                      $":rm={N(B(effect.MidR))}:gm={N(B(effect.MidG))}:bm={N(B(effect.MidB))}" +
                      $":rh={N(B(effect.HighR))}:gh={N(B(effect.HighG))}:bh={N(B(effect.HighB))}");
            }
            // v2.0: curves per channel
            if (effect.HasCurves)
            {
                var parts = new List<string>();
                foreach (var (name, curve) in new[] { ("master", effect.CurveAll), ("red", effect.CurveR), ("green", effect.CurveG), ("blue", effect.CurveB) })
                    if (!Curves.IsStraight(curve)) parts.Add($"{name}='{Curves.ToFFmpeg(curve)}'");
                f.Add("curves=" + string.Join(":", parts));
            }
            // v2.0: one colour only — change it, or keep it and turn the rest black and white
            string sel = effect.SelColor ?? "";
            if (sel.Length == 1 && "rygcbm".Contains(sel))
            {
                if (effect.SelKeepOnly && ColorAnalyzer.TryParseHex(effect.SelPicked, out _, out _, out _))
                    // a colour picked from the picture: exactly that colour stays
                    f.Add($"colorhold=color=0x{effect.SelPicked.TrimStart('#')}:similarity={N(Math.Clamp(effect.SelSimilarity, 5, 60) / 100.0)}:blend=0.1");
                else if (effect.SelKeepOnly)
                {
                    string others = string.Join("+", "rygcbm".Where(ch => ch.ToString() != sel).Select(ch => ch.ToString()));
                    f.Add($"huesaturation=colors={others}:saturation=-1:strength=10");
                }
                if (effect.SelHue != 0 || effect.SelSaturation != 0 || effect.SelBrightness != 0)
                    f.Add($"huesaturation=colors={sel}:hue={N(Math.Clamp(effect.SelHue, -180, 180))}" +
                          $":saturation={N(Math.Clamp(effect.SelSaturation / 100.0, -1, 1))}" +
                          $":intensity={N(Math.Clamp(effect.SelBrightness / 100.0, -1, 1))}:strength=5");
            }
            if (effect.Sharpen > 0)
                f.Add($"unsharp=5:5:{N(Math.Clamp(effect.Sharpen / 100.0 * 1.5, 0, 1.5))}:5:5:0");
            if (effect.Vignette > 0)
                f.Add($"vignette=angle={N(Math.Clamp(effect.Vignette / 100.0 * 0.8, 0.05, 1.2))}");
            if (effect.Blur > 0) f.Add($"boxblur={N(effect.Blur)}");
            // v2.0: a power window — everything above only inside (or outside) the area
            if (effect.HasWindow && f.Count > 0)
            {
                string all = PowerWindow.Wrap(effect, string.Join(",", f));
                f.Clear();
                f.Add(all);
            }
            if (includeSpeed && effect.Speed != 1.0) f.Add($"setpts={N(1.0 / effect.Speed)}*PTS");

            return string.Join(",", f);
        }

        /// <summary>v2.0 — a short spoken summary of the colour settings, for the screen reader.</summary>
        public static string Describe(VideoEffectData e)
        {
            if (e == null) return "";
            string T(string k) => LanguageManager.GetText(k, LanguageManager.CurrentLanguage ?? "en");
            var parts = new List<string>();
            void Add(string key, double v) { if (Math.Abs(v) > 0.001) parts.Add($"{T(key)} {(v > 0 ? "+" : "")}{Math.Round(v)}"); }
            Add("fxd_brightness", e.Brightness); Add("fxd_contrast", e.Contrast); Add("fxd_saturation", e.Saturation);
            Add("fxd_temperature", e.Temperature); Add("fxd_tint", e.Tint); Add("fxd_midtones", e.Gamma);
            Add("fxd_shadows", e.Shadows); Add("fxd_highlights", e.Highlights); Add("fxd_vignette", e.Vignette); Add("fxd_sharpen", e.Sharpen);
            if (e.HasBalance) parts.Add(T("fxd_balance"));
            if (e.HasCurves) parts.Add(T("fxd_curves"));
            if (!string.IsNullOrEmpty(e.SelColor)) parts.Add(T(e.SelKeepOnly ? "fxd_sel_keep" : "fxd_sel_change"));
            if (e.HasWindow && parts.Count > 0) parts.Add(PowerWindow.Describe(e));
            return string.Join(", ", parts);
        }
    }

    public static class AudioEffects
    {
        public static string GetFFmpegFilter(AudioSettingsData settings) => GetFFmpegFilter(settings, 0);

        /// <summary>
        /// The clip's sound processing. <paramref name="clipDuration"/> (seconds) is needed for
        /// the fade at the end; 0 leaves the end fade out.
        /// </summary>
        public static string GetFFmpegFilter(AudioSettingsData settings, double clipDuration)
        {
            if (settings == null) return "";
            var f = new List<string>();
            string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

            // v2.0: clearer speech first — rumble out, then the rest of the chain works on clean sound
            if (settings.VoiceEnhance) f.Add("highpass=f=80");
            if (settings.NoiseReduction > 0)
                f.Add(settings.NoiseReduction >= 2 ? "afftdn=nr=24:nf=-30" : "afftdn=nr=12:nf=-40");

            // Bass boost (100Hz) and Treble boost (4000Hz) using equalizer filter
            if (settings.BassBoost > 0)
                f.Add($"equalizer=frequency=100:width_type=o:width=1:gain={N(settings.BassBoost / 5.0)}");
            if (settings.TrebleBoost > 0)
                f.Add($"equalizer=frequency=4000:width_type=o:width=1:gain={N(settings.TrebleBoost / 5.0)}");

            // v2.0: three-band equaliser
            if (settings.LowGain != 0) f.Add($"equalizer=frequency=120:width_type=o:width=2:gain={N(Math.Clamp(settings.LowGain, -12, 12))}");
            if (settings.MidGain != 0) f.Add($"equalizer=frequency=1000:width_type=o:width=2:gain={N(Math.Clamp(settings.MidGain, -12, 12))}");
            if (settings.HighGain != 0) f.Add($"equalizer=frequency=8000:width_type=o:width=2:gain={N(Math.Clamp(settings.HighGain, -12, 12))}");

            if (settings.VoiceEnhance)
            {
                f.Add("equalizer=frequency=3000:width_type=o:width=1.5:gain=3");
                f.Add("acompressor=threshold=0.125:ratio=3:attack=10:release=150:makeup=2");
            }

            // Reverb efekat
            if (settings.ReverbAmount > 0)
            {
                double reverbDelay = 0.3 + (settings.ReverbAmount / 100) * 0.5;
                f.Add($"aecho=0.8:{N(reverbDelay)}:40:0.3");
            }

            // Kompresor
            if (settings.CompressorEnabled && !settings.VoiceEnhance)
                f.Add("acompressor=threshold=0.1:ratio=3:attack=5:release=50");

            // v2.0: fades
            if (settings.FadeIn > 0.01) f.Add($"afade=t=in:st=0:d={N(settings.FadeIn)}");
            if (settings.FadeOut > 0.01 && clipDuration > settings.FadeOut)
                f.Add($"afade=t=out:st={N(clipDuration - settings.FadeOut)}:d={N(settings.FadeOut)}");

            return string.Join(",", f);
        }
    }
}
