using System;
using System.Collections.Generic;
using UltraStudio.Localization;
using UltraStudio.Models;

namespace UltraStudio.Services
{
    /// <summary>
    /// Gotovi "izgledi" (kao Instagram/Lightroom presetovi). Svaki je samo skup
    /// vrednosti nedestruktivnih podešavanja — posle izbora mogu da se doteruju
    /// ručno, a Undo vraća prethodno stanje. Opis se izgovara pri biranju strelicama.
    /// </summary>
    public static class LookPresets
    {
        public sealed record Look(string Key, Action<ImageProject> Apply);

        public static readonly List<Look> All = new()
        {
            new("look_original", p => { }),
            new("look_natural", p => { p.Contrast = 6; p.Vibrance = 15; p.Clarity = 8; p.Sharpen = 1; }),
            new("look_vivid", p => { p.Contrast = 15; p.Vibrance = 40; p.Saturation = 10; p.Clarity = 15; }),
            new("look_warm", p => { p.Temperature = 35; p.Vibrance = 10; p.Shadows = 8; }),
            new("look_cool", p => { p.Temperature = -35; p.Tint = 5; p.Contrast = 5; }),
            new("look_golden", p => { p.Temperature = 50; p.Tint = 10; p.Exposure = 0.15; p.Highlights = -20; p.Vibrance = 20; p.Vignette = 20; }),
            new("look_matte", p => { p.Fade = 45; p.Contrast = -10; p.Saturation = -15; p.Shadows = 10; }),
            new("look_film", p => { p.Fade = 30; p.Temperature = 15; p.Saturation = -10; p.Contrast = 8; p.Vignette = 25; p.Highlights = -15; }),
            new("look_cinematic", p => { p.Temperature = -12; p.Tint = -6; p.Contrast = 20; p.Highlights = -25; p.Shadows = -10; p.Vibrance = 10; p.Vignette = 30; }),
            new("look_dramatic", p => { p.Contrast = 35; p.Clarity = 40; p.Highlights = -35; p.Shadows = 15; p.Saturation = -15; p.Vignette = 35; }),
            new("look_moody", p => { p.Exposure = -0.3; p.Contrast = 15; p.Saturation = -30; p.Temperature = -10; p.Fade = 15; p.Vignette = 40; }),
            new("look_airy", p => { p.Exposure = 0.4; p.Shadows = 30; p.Contrast = -12; p.Saturation = -10; p.Temperature = 5; p.Highlights = -10; }),
            new("look_portrait", p => { p.Clarity = -20; p.Temperature = 10; p.Vibrance = 10; p.Shadows = 10; p.Highlights = -10; }),
            new("look_bw", p => { p.Grayscale = true; p.Contrast = 10; }),
            new("look_bw_contrast", p => { p.Grayscale = true; p.Contrast = 40; p.Clarity = 30; p.Vignette = 20; }),
            new("look_noir", p => { p.Grayscale = true; p.Contrast = 55; p.Exposure = -0.3; p.Vignette = 50; p.Highlights = -20; }),
            new("look_sepia", p => { p.Sepia = true; p.Fade = 20; p.Vignette = 25; }),
            new("look_vintage", p => { p.Fade = 35; p.Temperature = 25; p.Tint = 8; p.Saturation = -25; p.Vignette = 35; p.Contrast = -5; }),
            new("look_pop", p => { p.Saturation = 45; p.Contrast = 25; p.Clarity = 10; }),
            new("look_night", p => { p.Temperature = -45; p.Exposure = -0.5; p.Contrast = 15; p.Saturation = -20; p.Vignette = 30; }),
        };

        public static string Name(Look l) => Lang.T(l.Key);
        public static string Description(Look l) => Lang.T(l.Key + "_desc");

        /// <summary>Primeni izgled: prvo resetuje podešavanja, pa postavlja vrednosti izgleda.</summary>
        public static void Apply(ImageProject p, Look l)
        {
            p.ResetAdjustments();
            l.Apply(p);
            p.LookName = l.Key == "look_original" ? "" : Name(l);
        }
    }

    /// <summary>Gotove veličine platna za društvene mreže, štampu i video.</summary>
    public static class CanvasPresets
    {
        public sealed record Preset(string Key, int Width, int Height, double Dpi = 96);

        public static readonly List<Preset> All = new()
        {
            new("cp_ig_square", 1080, 1080),
            new("cp_ig_portrait", 1080, 1350),
            new("cp_ig_story", 1080, 1920),
            new("cp_fb_post", 1200, 630),
            new("cp_fb_cover", 1640, 624),
            new("cp_yt_thumb", 1280, 720),
            new("cp_yt_banner", 2560, 1440),
            new("cp_x_post", 1600, 900),
            new("cp_linkedin", 1200, 627),
            new("cp_hd", 1920, 1080),
            new("cp_4k", 3840, 2160),
            new("cp_a4_300", 2480, 3508, 300),
            new("cp_a4_150", 1240, 1754, 150),
            new("cp_a3_300", 3508, 4961, 300),
            new("cp_poster", 3600, 5400, 150),
            new("cp_flyer", 1275, 1650, 150),
            new("cp_card", 1050, 600, 300),
            new("cp_a5_300", 1748, 2480, 300),
            new("cp_b5_300", 2079, 2953, 300),
            new("cp_book_300", 1772, 2598, 300),
            new("cp_custom", 0, 0, 300),
        };

        public static string Label(Preset p) =>
            p.Width > 0 ? $"{Lang.T(p.Key)} — {p.Width} × {p.Height}" : Lang.T(p.Key);

        /// <summary>Ime preseta koji tačno odgovara veličini, ako postoji (za izgovor "Instagram objava").</summary>
        public static string? Match(int w, int h)
        {
            foreach (var p in All) if (p.Width == w && p.Height == h) return Lang.T(p.Key);
            return null;
        }
    }
}
