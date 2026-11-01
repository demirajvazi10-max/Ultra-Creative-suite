using System;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace UltraVideoEditor
{
    /// <summary>
    /// v2.0 — power windows: a clip's colour correction (and sharpening, vignette, blur) only
    /// inside an oval or rectangle, or in a soft graduated part of the picture (the sky at the
    /// top, the ground at the bottom, one side) — or everywhere except there. The mask is drawn
    /// by FFmpeg from a formula, so it needs no extra files and works in the export, in live
    /// playback, in the preview and in the spoken colour analysis.
    /// </summary>
    public static class PowerWindow
    {
        public static readonly string[] Shapes = { "Oval", "Rectangle", "Top", "Bottom", "Left", "Right" };

        private static int _n;
        private static string N(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        /// <summary>The mask's brightness (0 … 255) for a pixel at X, Y of a W × H picture, as a geq expression.</summary>
        public static string Expr(VideoEffectData e)
        {
            double cx = Math.Clamp(e.PwX, 0, 100) / 100, cy = Math.Clamp(e.PwY, 0, 100) / 100;
            double w = Math.Clamp(e.PwW, 1, 200) / 100, h = Math.Clamp(e.PwH, 1, 200) / 100;
            double f = Math.Clamp(e.PwFeather, 0, 100) / 100;
            f = Math.Max(0.01, f);
            // d: 0 in the middle of the area, 1 on its edge; the edge is soft over ±f/2
            string d = e.PwShape switch
            {
                "Oval" => $"hypot((X/W-{N(cx)})/{N(w / 2)},(Y/H-{N(cy)})/{N(h / 2)})",
                "Rectangle" => $"max(abs(X/W-{N(cx)})/{N(w / 2)},abs(Y/H-{N(cy)})/{N(h / 2)})",
                "Top" => $"(Y/H)/{N(h)}",
                "Bottom" => $"(1-Y/H)/{N(h)}",
                "Left" => $"(X/W)/{N(w)}",
                "Right" => $"(1-X/W)/{N(w)}",
                _ => "0"
            };
            string inside = $"clip((1-{d})/{N(f)}+0.5,0,1)";
            return $"255*{(e.PwInvert ? $"(1-{inside})" : inside)}";
        }

        /// <summary>
        /// One input, one output: <paramref name="fx"/> applied only through the window. The
        /// labels are unique, so the result can sit in any filter chain or filter graph.
        /// </summary>
        public static string Wrap(VideoEffectData e, string fx)
        {
            int n = Interlocked.Increment(ref _n);
            string a = $"[pwa{n}]", b = $"[pwb{n}]", m = $"[pwm{n}]", k = $"[pwk{n}]", c = $"[pwc{n}]", o = $"[pwo{n}]";
            return $"split=3{a}{b}{m};" +
                   $"{m}trim=end_frame=1,format=gray,geq=lum='{Expr(e)}'{k};" +   // drawn once, then held for every frame
                   $"{b}{fx},format=yuva420p{c};" +
                   $"{c}{k}alphamerge{o};" +
                   $"{a}{o}overlay=format=auto:eof_action=pass,format=yuv420p";
        }

        public static string Describe(VideoEffectData e)
        {
            string T(string k) => LanguageManager.GetText(k, LanguageManager.CurrentLanguage ?? "en");
            string where = e.PwShape is "Oval" or "Rectangle"
                ? string.Format(T("pw_desc_box"), T("pw_shape_" + e.PwShape), Math.Round(e.PwX), Math.Round(e.PwY), Math.Round(e.PwW), Math.Round(e.PwH))
                : string.Format(T("pw_desc_side"), T("pw_shape_" + e.PwShape), Math.Round(e.PwShape is "Top" or "Bottom" ? e.PwH : e.PwW));
            return string.Format(T(e.PwInvert ? "pw_desc_outside" : "pw_desc_inside"), where);
        }
    }
}
