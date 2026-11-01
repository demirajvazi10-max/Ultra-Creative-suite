using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace UltraVideoEditor
{
    /// <summary>
    /// v2.0 — curves as five points: the output (0 … 100) at 0, 25, 50, 75 and 100 % input.
    /// Stored as text "0 25 50 75 100"; empty means a straight line.
    /// </summary>
    public static class Curves
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public static readonly double[] Straight = { 0, 25, 50, 75, 100 };

        /// <summary>Ready-made shapes: name → points.</summary>
        public static readonly (string Key, double[] Points)[] Presets =
        {
            ("Straight", new double[] { 0, 25, 50, 75, 100 }),
            ("SCurve", new double[] { 0, 20, 50, 80, 100 }),
            ("StrongS", new double[] { 0, 15, 50, 87, 100 }),
            ("Faded", new double[] { 10, 30, 52, 75, 92 }),
            ("LiftShadows", new double[] { 8, 30, 52, 76, 100 }),
            ("Brighter", new double[] { 0, 32, 60, 82, 100 }),
            ("Darker", new double[] { 0, 18, 40, 68, 100 }),
        };

        public static double[] Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return (double[])Straight.Clone();
            var p = s.Split(new[] { ' ', ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                     .Select(x => double.TryParse(x, NumberStyles.Float, Inv, out double v) ? Math.Clamp(v, 0, 100) : double.NaN).ToArray();
            return p.Length == 5 && p.All(v => !double.IsNaN(v)) ? p : (double[])Straight.Clone();
        }

        public static string Format(double[] p) =>
            p == null || IsStraight(p) ? "" : string.Join(" ", p.Select(v => Math.Round(Math.Clamp(v, 0, 100)).ToString(Inv)));

        public static bool IsStraight(string s) => IsStraight(Parse(s));
        public static bool IsStraight(double[] p) => p == null || p.Select((v, i) => Math.Abs(v - Straight[i]) < 0.5).All(b => b);

        /// <summary>FFmpeg's curve points: "0/0 0.25/0.2 …".</summary>
        public static string ToFFmpeg(string s)
        {
            var p = Parse(s);
            return string.Join(" ", p.Select((v, i) => $"{(i * 0.25).ToString("0.##", Inv)}/{(v / 100).ToString("0.###", Inv)}"));
        }
    }

    /// <summary>v2.0 — what a picture looks like, measured.</summary>
    public class ColorStats
    {
        /// <summary>Average brightness 0 … 1.</summary>
        public double Luma;
        /// <summary>Spread of brightness (standard deviation, 0 … 0.5).</summary>
        public double Contrast;
        /// <summary>Share of pure black / pure white pixels, 0 … 1.</summary>
        public double Black, White;
        public double R, G, B;
        /// <summary>Average saturation 0 … 1.</summary>
        public double Saturation;
        /// <summary>Red minus blue (positive = warm).</summary>
        public double Warmth => R - B;
        /// <summary>Green minus the average of red and blue (positive = green, negative = magenta).</summary>
        public double Tint => G - (R + B) / 2;
        /// <summary>The main colours of the coloured part of the picture: name key → share 0 … 1.</summary>
        public List<(string Name, double Share)> Colours = new List<(string, double)>();
        public int Samples;
    }

    /// <summary>
    /// v2.0 — spoken colour analysis instead of scopes (waveform, vectorscope): brightness,
    /// contrast, crushed blacks and burnt-out whites, colour cast, saturation and the main
    /// colours, measured on small frames with FFmpeg. Also picks the colour of a part of the
    /// picture, and matches one clip's colour to another (shot matching) by measuring,
    /// correcting and measuring again.
    /// </summary>
    public static class ColorAnalyzer
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static string N(double v) => v.ToString("0.###", Inv);
        private const int SW = 160, SH = 90;

        private static string T(string k) => LanguageManager.GetText(k, LanguageManager.CurrentLanguage ?? "en");
        private static string TF(string k, params object[] a) => string.Format(T(k), a);

        public static bool TryParseHex(string hex, out int r, out int g, out int b)
        {
            r = g = b = 0;
            hex = (hex ?? "").Trim().TrimStart('#');
            if (hex.Length != 6) return false;
            return int.TryParse(hex.Substring(0, 2), NumberStyles.HexNumber, Inv, out r) &&
                   int.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, Inv, out g) &&
                   int.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, Inv, out b);
        }

        // ── measuring ────────────────────────────────────────────────────────────────

        /// <summary>One small RGB frame of a file at a time, after an optional filter chain; null when none could be read.</summary>
        private static async Task<byte[]> FrameAsync(string path, double fileTime, bool still, string vf, CancellationToken ct)
        {
            string chain = (string.IsNullOrEmpty(vf) ? "" : vf + ",") + $"scale={SW}:{SH}:flags=area,format=rgb24";
            string ss = !still && fileTime > 0.01 ? $"-ss {N(fileTime)} " : "";
            string args = $"-nostdin -hide_banner -loglevel error {ss}-i \"{path}\" -map 0:v:0 -frames:v 1 -vf \"{chain}\" -f rawvideo -";
            var data = await RunRawAsync(args, SW * SH * 3, ct);
            if (data == null && ss.Length > 0)
                data = await RunRawAsync($"-nostdin -hide_banner -loglevel error -i \"{path}\" -map 0:v:0 -frames:v 1 -vf \"{chain}\" -f rawvideo -", SW * SH * 3, ct);
            return data;
        }

        private static async Task<byte[]> RunRawAsync(string args, int size, CancellationToken ct)
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo(AppPaths.FFmpeg, args)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                }
            };
            p.Start();
            _ = p.StandardError.ReadToEndAsync();
            using var ms = new MemoryStream();
            using (ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } }))
            {
                await p.StandardOutput.BaseStream.CopyToAsync(ms, ct);
                await p.WaitForExitAsync(ct);
            }
            return ms.Length >= size ? ms.ToArray().Take(size).ToArray() : null;
        }

        /// <summary>Measures frames (file times) of a clip after <paramref name="vf"/> and averages them.</summary>
        public static async Task<ColorStats> MeasureAsync(string path, IEnumerable<double> fileTimes, bool still, string vf, CancellationToken ct)
        {
            var all = new List<byte[]>();
            foreach (double t in fileTimes)
            {
                var f = await FrameAsync(path, t, still, vf, ct);
                if (f != null) all.Add(f);
                if (still) break;
            }
            return all.Count == 0 ? null : Stats(all);
        }

        /// <summary>A few file times spread over a clip (its middle part).</summary>
        public static List<double> SampleTimes(TimelineItem item, int count = 3)
        {
            double speed = item.IsImage ? 1 : Math.Clamp(item.VideoEffect?.Speed is double sp && sp > 0.01 ? sp : 1, 0.1, 10);
            double len = Math.Max(0.1, item.Duration * speed);
            return Enumerable.Range(0, count).Select(i => item.SourceOffset + len * (i + 1) / (count + 1)).ToList();
        }

        private static ColorStats Stats(List<byte[]> frames)
        {
            double sumY = 0, sumY2 = 0, sr = 0, sg = 0, sb = 0, ss = 0;
            long black = 0, white = 0, n = 0, coloured = 0;
            var bins = new Dictionary<string, long>();
            foreach (var f in frames)
                for (int i = 0; i + 2 < f.Length; i += 3)
                {
                    double r = f[i] / 255.0, g = f[i + 1] / 255.0, b = f[i + 2] / 255.0;
                    double y = 0.299 * r + 0.587 * g + 0.114 * b;
                    sumY += y; sumY2 += y * y; sr += r; sg += g; sb += b; n++;
                    if (y < 0.06) black++;
                    if (y > 0.94) white++;
                    double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
                    double s = mx > 0 ? (mx - mn) / mx : 0;
                    ss += s;
                    if (s > 0.25 && mx > 0.15)
                    {
                        string name = HueName(Hue(r, g, b), s, mx);
                        bins[name] = bins.TryGetValue(name, out long c) ? c + 1 : 1;
                        coloured++;
                    }
                }
            var st = new ColorStats { Samples = frames.Count };
            if (n == 0) return st;
            st.Luma = sumY / n;
            st.Contrast = Math.Sqrt(Math.Max(0, sumY2 / n - st.Luma * st.Luma));
            st.Black = (double)black / n; st.White = (double)white / n;
            st.R = sr / n; st.G = sg / n; st.B = sb / n; st.Saturation = ss / n;
            st.Colours = bins.OrderByDescending(kv => kv.Value).Take(3)
                             .Select(kv => (kv.Key, (double)kv.Value / n)).Where(c => c.Item2 >= 0.03).ToList();
            return st;
        }

        private static double Hue(double r, double g, double b)
        {
            double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b)), d = mx - mn;
            if (d < 1e-6) return 0;
            double h = mx == r ? 60 * (((g - b) / d) % 6) : mx == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
            return h < 0 ? h + 360 : h;
        }

        /// <summary>A colour name key for a hue (skin tones get their own name).</summary>
        private static string HueName(double h, double s, double v)
        {
            if (h >= 10 && h < 45 && s < 0.6 && v > 0.35) return "skin";
            if (h < 15 || h >= 345) return "red";
            if (h < 45) return "orange";
            if (h < 70) return "yellow";
            if (h < 170) return "green";
            if (h < 200) return "cyan";
            if (h < 260) return "blue";
            if (h < 300) return "purple";
            return "pink";
        }

        /// <summary>The nearest of the six colour ranges of "One colour only" (r, y, g, c, b, m).</summary>
        public static string RangeOf(int r, int g, int b)
        {
            int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
            if (mx == 0 || (mx - mn) / (double)mx < 0.12) return "";   // grey, black or white: no colour range
            double h = Hue(r / 255.0, g / 255.0, b / 255.0);
            return h < 30 || h >= 330 ? "r" : h < 90 ? "y" : h < 150 ? "g" : h < 210 ? "c" : h < 270 ? "b" : "m";
        }

        /// <summary>A spoken name for a picked colour: "a dark, strong orange".</summary>
        public static string NameColour(int r, int g, int b)
        {
            double rr = r / 255.0, gg = g / 255.0, bb = b / 255.0;
            double mx = Math.Max(rr, Math.Max(gg, bb)), mn = Math.Min(rr, Math.Min(gg, bb));
            double s = mx > 0 ? (mx - mn) / mx : 0;
            if (s < 0.12) return mx < 0.15 ? T("col_black") : mx > 0.85 ? T("col_white") : TF("col_grey", Math.Round(mx * 100));
            string hue = T("col_" + HueName(Hue(rr, gg, bb), s, mx));
            string light = mx < 0.35 ? T("col_dark") + " " : mx > 0.8 && s < 0.45 ? T("col_light") + " " : "";
            string strength = s > 0.65 ? T("col_strong") + " " : s < 0.3 ? T("col_pale") + " " : "";
            return (light + strength + hue).Trim();
        }

        // ── spoken report ───────────────────────────────────────────────────────────

        public static string Describe(ColorStats s)
        {
            if (s == null) return T("col_no_picture");
            var parts = new List<string>();
            int L = (int)Math.Round(s.Luma * 100);
            string lw = s.Luma < 0.2 ? "col_l_vdark" : s.Luma < 0.35 ? "col_l_dark" : s.Luma < 0.6 ? "col_l_normal" : s.Luma < 0.75 ? "col_l_bright" : "col_l_vbright";
            parts.Add(TF("col_brightness", L, T(lw)));
            string cw = s.Contrast < 0.12 ? "col_c_flat" : s.Contrast < 0.2 ? "col_c_low" : s.Contrast < 0.28 ? "col_c_normal" : "col_c_high";
            parts.Add(T(cw));
            if (s.Black > 0.03) parts.Add(TF("col_crushed", Math.Round(s.Black * 100)));
            if (s.White > 0.03) parts.Add(TF("col_burnt", Math.Round(s.White * 100)));
            if (s.Black <= 0.03 && s.White <= 0.03) parts.Add(T("col_no_clipping"));
            string warm = s.Warmth > 0.1 ? "col_w_vwarm" : s.Warmth > 0.04 ? "col_w_warm" : s.Warmth < -0.1 ? "col_w_vcool" : s.Warmth < -0.04 ? "col_w_cool" : "col_w_neutral";
            parts.Add(T(warm));
            if (s.Tint > 0.03) parts.Add(T("col_t_green"));
            else if (s.Tint < -0.03) parts.Add(T("col_t_magenta"));
            string sw = s.Saturation < 0.15 ? "col_s_bw" : s.Saturation < 0.3 ? "col_s_low" : s.Saturation < 0.5 ? "col_s_medium" : "col_s_strong";
            parts.Add(T(sw));
            string text = string.Join(", ", parts) + ".";
            if (s.Colours.Count > 0)
                text += " " + TF("col_main", string.Join(", ", s.Colours.Select(c => $"{T("col_" + c.Name)} {Math.Round(c.Share * 100)} %"))) + ".";
            return text;
        }

        /// <summary>How two pictures differ, in words (for shot matching).</summary>
        public static string Compare(ColorStats a, ColorStats b)
        {
            if (a == null || b == null) return T("col_no_picture");
            var d = new List<string>();
            if (Math.Abs(a.Luma - b.Luma) > 0.03) d.Add(TF(a.Luma > b.Luma ? "col_cmp_brighter" : "col_cmp_darker", Math.Round(Math.Abs(a.Luma - b.Luma) * 100)));
            if (Math.Abs(a.Contrast - b.Contrast) > 0.02) d.Add(T(a.Contrast > b.Contrast ? "col_cmp_more_contrast" : "col_cmp_less_contrast"));
            if (Math.Abs(a.Warmth - b.Warmth) > 0.02) d.Add(T(a.Warmth > b.Warmth ? "col_cmp_warmer" : "col_cmp_cooler"));
            if (Math.Abs(a.Tint - b.Tint) > 0.015) d.Add(T(a.Tint > b.Tint ? "col_cmp_greener" : "col_cmp_magenta"));
            if (Math.Abs(a.Saturation - b.Saturation) > 0.03) d.Add(T(a.Saturation > b.Saturation ? "col_cmp_more_sat" : "col_cmp_less_sat"));
            return d.Count == 0 ? T("col_cmp_same") : string.Join(", ", d) + ".";
        }

        // ── picking a colour ────────────────────────────────────────────────────────

        /// <summary>The average colour of a part of the picture (3 × 3 grid, a small box in its middle).</summary>
        public static async Task<(int R, int G, int B)?> PickAsync(string path, double fileTime, bool still, string region, CancellationToken ct)
        {
            double cx = region switch { "TopLeft" or "Left" or "BottomLeft" => 1 / 6.0, "TopRight" or "Right" or "BottomRight" => 5 / 6.0, _ => 0.5 };
            double cy = region switch { "TopLeft" or "Top" or "TopRight" => 1 / 6.0, "BottomLeft" or "Bottom" or "BottomRight" => 5 / 6.0, _ => 0.5 };
            string crop = $"crop=w=iw*0.12:h=ih*0.12:x=iw*{N(cx - 0.06)}:y=ih*{N(cy - 0.06)}";
            var f = await FrameAsync(path, fileTime, still, crop, ct);
            if (f == null) return null;
            long r = 0, g = 0, b = 0, n = f.Length / 3;
            for (int i = 0; i + 2 < f.Length; i += 3) { r += f[i]; g += f[i + 1]; b += f[i + 2]; }
            return ((int)(r / n), (int)(g / n), (int)(b / n));
        }

        // ── shot matching ───────────────────────────────────────────────────────────

        /// <summary>
        /// Changes brightness, contrast, saturation, temperature and tint of
        /// <paramref name="effect"/> so that <paramref name="item"/> looks like
        /// <paramref name="target"/>: measure, correct, measure again (up to four rounds).
        /// Returns the final measurement of the clip.
        /// </summary>
        public static async Task<ColorStats> MatchAsync(TimelineItem item, VideoEffectData effect, ColorStats target,
                                                        Action<int> round, CancellationToken ct)
        {
            var times = SampleTimes(item);
            bool still = item.IsImage;
            ColorStats now = null;
            for (int k = 0; k < 6; k++)
            {
                round?.Invoke(k + 1);
                now = await MeasureAsync(item.Path, times, still, VideoEffects.GetFFmpegFilter(effect, includeSpeed: false), ct);
                if (now == null) return null;
                double dL = target.Luma - now.Luma, dW = target.Warmth - now.Warmth, dT = target.Tint - now.Tint;
                double rC = target.Contrast / Math.Max(0.01, now.Contrast), rS = target.Saturation / Math.Max(0.01, now.Saturation);
                bool close = Math.Abs(dL) < 0.01 && Math.Abs(dW) < 0.008 && Math.Abs(dT) < 0.006 && Math.Abs(rC - 1) < 0.03 && Math.Abs(rS - 1) < 0.03;
                if (close) break;
                effect.Brightness = Math.Clamp(effect.Brightness + dL / 0.0105 * 0.8, -100, 100);
                effect.Contrast = Math.Clamp((1 + effect.Contrast / 100) * Math.Pow(rC, 0.9) * 100 - 100, -100, 100);
                effect.Saturation = Math.Clamp((1 + effect.Saturation / 100) * rS * 100 - 100, -100, 100);
                effect.Temperature = Math.Clamp(effect.Temperature + dW / 0.0017 * 0.7, -100, 100);
                effect.Tint = Math.Clamp(effect.Tint - dT / 0.0010 * 0.6, -100, 100);
                effect.Brightness = Math.Round(effect.Brightness); effect.Contrast = Math.Round(effect.Contrast);
                effect.Saturation = Math.Round(effect.Saturation); effect.Temperature = Math.Round(effect.Temperature); effect.Tint = Math.Round(effect.Tint);
            }
            return now;
        }
    }
}
