using System;
using System.Collections.Generic;

namespace UltraStudio.Services
{
    /// <summary>
    /// Obrada piksela u čistom C#-u, nad RGBA bajtovima (4 bajta po pikselu)
    /// i sivim maskama (1 bajt po pikselu, 255 = izabrano). Nema zavisnosti
    /// od Magick.NET-a ni od Windowsa, pa je sve ovde testirano automatski.
    /// </summary>
    public static class PixelOps
    {
        // ════════════════════════════════════════════════════════════════
        // TON I BOJA
        // ════════════════════════════════════════════════════════════════
        public sealed class ToneParams
        {
            public double Exposure;     // EV, -3..3
            public double Gamma;        // -100..100
            public double Highlights;   // -100..100
            public double Shadows;      // -100..100
            public double Temperature;  // -100..100
            public double Tint;         // -100..100
            public double Vibrance;     // -100..100
            public double Fade;         // 0..100

            public bool IsIdentity =>
                Exposure == 0 && Gamma == 0 && Highlights == 0 && Shadows == 0 &&
                Temperature == 0 && Tint == 0 && Vibrance == 0 && Fade == 0;
        }

        private static readonly double[] SrgbToLinear = BuildSrgbToLinear();
        private static double[] BuildSrgbToLinear()
        {
            var t = new double[256];
            for (int i = 0; i < 256; i++)
            {
                double c = i / 255.0;
                t[i] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            return t;
        }

        private static double LinearToSrgb(double c)
        {
            if (c <= 0) return 0;
            if (c >= 1) return 1;
            return c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
        }

        private static byte ToByte(double v) => (byte)Math.Clamp((int)Math.Round(v * 255.0), 0, 255);

        public static void ApplyTone(byte[] rgba, int w, int h, ToneParams p)
        {
            if (p.IsIdentity) return;
            double expGain = Math.Pow(2, p.Exposure);
            double gammaExp = 1.0 / Math.Pow(2, p.Gamma / 100.0);   // +100 -> svetliji srednji tonovi
            double sh = p.Shadows / 100.0, hi = p.Highlights / 100.0;
            double temp = p.Temperature / 100.0, tint = p.Tint / 100.0;
            double vib = p.Vibrance / 100.0, fade = p.Fade / 100.0;

            // Ekspozicija u linearnom prostoru (kao pravi foto-aparat), pa nazad u sRGB — LUT po kanalu
            var expLut = new double[256];
            for (int i = 0; i < 256; i++)
            {
                double v = LinearToSrgb(SrgbToLinear[i] * expGain);
                if (p.Gamma != 0) v = Math.Pow(v, gammaExp);
                expLut[i] = v;
            }

            int n = w * h;
            System.Threading.Tasks.Parallel.For(0, h, y =>
            {
                int row = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    double r = expLut[rgba[i]], g = expLut[rgba[i + 1]], b = expLut[rgba[i + 2]];

                    if (sh != 0 || hi != 0)
                    {
                        double L = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                        double ws = Math.Clamp(1 - L / 0.55, 0, 1); ws *= ws;
                        double wh = Math.Clamp((L - 0.45) / 0.55, 0, 1); wh *= wh;
                        double nl = Math.Clamp(L + sh * ws * 0.45 + hi * wh * 0.45, 0, 1);
                        if (L > 0.002)
                        {
                            double f = nl / L;
                            r *= f; g *= f; b *= f;
                        }
                        else { r = g = b = nl; }
                    }

                    if (temp != 0) { r *= 1 + 0.16 * temp; b *= 1 - 0.16 * temp; g *= 1 + 0.03 * temp; }
                    if (tint != 0) { g *= 1 - 0.12 * tint; r *= 1 + 0.04 * tint; b *= 1 + 0.04 * tint; }

                    if (vib != 0)
                    {
                        double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
                        double sat = mx <= 0 ? 0 : (mx - mn) / mx;
                        double lum = 0.299 * r + 0.587 * g + 0.114 * b;
                        double amt = vib > 0 ? 1 + vib * (1 - sat) * 1.2 : 1 + vib;
                        r = lum + (r - lum) * amt; g = lum + (g - lum) * amt; b = lum + (b - lum) * amt;
                    }

                    if (fade != 0)
                    {
                        double lift = 0.14 * fade, top = 1 - 0.10 * fade;
                        r = lift + r * (top - lift); g = lift + g * (top - lift); b = lift + b * (top - lift);
                    }

                    rgba[i] = ToByte(r); rgba[i + 1] = ToByte(g); rgba[i + 2] = ToByte(b);
                }
            });
        }

        /// <summary>Zatamnjenje ivica. amount 0..100.</summary>
        public static void ApplyVignette(byte[] rgba, int w, int h, double amount)
        {
            if (amount <= 0) return;
            double a = Math.Clamp(amount / 100.0, 0, 1) * 0.85;
            double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0;
            System.Threading.Tasks.Parallel.For(0, h, y =>
            {
                double dy = (y - cy) / Math.Max(1, cy);
                int row = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    double dx = (x - cx) / Math.Max(1, cx);
                    double d = Math.Sqrt(dx * dx + dy * dy) / Math.Sqrt(2);  // 0 u centru, 1 u uglu
                    double t = Math.Clamp((d - 0.35) / 0.65, 0, 1);
                    double f = 1 - a * t * t * (3 - 2 * t);
                    int i = row + x * 4;
                    rgba[i] = (byte)(rgba[i] * f); rgba[i + 1] = (byte)(rgba[i + 1] * f); rgba[i + 2] = (byte)(rgba[i + 2] * f);
                }
            });
        }

        // ════════════════════════════════════════════════════════════════
        // GRADIJENT
        // ════════════════════════════════════════════════════════════════
        public static byte[] Gradient(int w, int h, bool radial, (byte r, byte g, byte b, byte a) c1,
            (byte r, byte g, byte b, byte a) c2, double angleDeg)
        {
            var px = new byte[w * h * 4];
            double rad = angleDeg * Math.PI / 180;
            double ux = Math.Cos(rad), uy = Math.Sin(rad);
            // projekcije uglova na pravac — da prelaz ide tačno od ivice do ivice
            double[] proj = { 0, (w - 1) * ux, (h - 1) * uy, (w - 1) * ux + (h - 1) * uy };
            double pmin = Math.Min(Math.Min(proj[0], proj[1]), Math.Min(proj[2], proj[3]));
            double pmax = Math.Max(Math.Max(proj[0], proj[1]), Math.Max(proj[2], proj[3]));
            double span = Math.Max(1e-6, pmax - pmin);
            double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0, rmax = Math.Max(1e-6, Math.Sqrt(cx * cx + cy * cy));

            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double t = radial
                        ? Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / rmax
                        : (x * ux + y * uy - pmin) / span;
                    t = Math.Clamp(t, 0, 1);
                    int i = (y * w + x) * 4;
                    px[i] = (byte)Math.Round(c1.r + (c2.r - c1.r) * t);
                    px[i + 1] = (byte)Math.Round(c1.g + (c2.g - c1.g) * t);
                    px[i + 2] = (byte)Math.Round(c1.b + (c2.b - c1.b) * t);
                    px[i + 3] = (byte)Math.Round(c1.a + (c2.a - c1.a) * t);
                }
            return px;
        }

        // ════════════════════════════════════════════════════════════════
        // OPSEG BOJA (Color Range) — isti izbor kao Photoshop: crvene, žute,
        // zelene, cijan, plave, magenta, svetli/srednji/tamni tonovi, koža.
        // ════════════════════════════════════════════════════════════════
        public enum ColorRange { Reds, Oranges, Yellows, Greens, Cyans, Blues, Purples, Magentas, Highlights, Midtones, Shadows, Skin, Custom }

        public static void RgbToHsv(double r, double g, double b, out double hDeg, out double s, out double v)
        {
            double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
            v = mx; double d = mx - mn;
            s = mx <= 0 ? 0 : d / mx;
            if (d <= 1e-9) { hDeg = 0; return; }
            if (mx == r) hDeg = 60 * (((g - b) / d) % 6);
            else if (mx == g) hDeg = 60 * ((b - r) / d + 2);
            else hDeg = 60 * ((r - g) / d + 4);
            if (hDeg < 0) hDeg += 360;
        }

        private static double HueDistance(double a, double b)
        {
            double d = Math.Abs(a - b) % 360;
            return d > 180 ? 360 - d : d;
        }

        /// <summary>fuzziness 0..100 — koliko meko se širi izbor oko zadatog opsega.</summary>
        public static byte[] ColorRangeMask(byte[] rgba, int w, int h, ColorRange range, double fuzziness,
            (byte r, byte g, byte b) custom = default)
        {
            var mask = new byte[w * h];
            double fz = Math.Clamp(fuzziness, 0, 100) / 100.0;
            (double center, double half) hue = range switch
            {
                ColorRange.Reds => (0, 18), ColorRange.Oranges => (30, 14), ColorRange.Yellows => (58, 16),
                ColorRange.Greens => (120, 42), ColorRange.Cyans => (180, 18), ColorRange.Blues => (225, 30),
                ColorRange.Purples => (272, 16), ColorRange.Magentas => (310, 26), _ => (0, 0)
            };

            for (int p = 0, i = 0; p < w * h; p++, i += 4)
            {
                double r = rgba[i] / 255.0, g = rgba[i + 1] / 255.0, b = rgba[i + 2] / 255.0;
                double m;
                switch (range)
                {
                    case ColorRange.Highlights:
                    case ColorRange.Midtones:
                    case ColorRange.Shadows:
                    {
                        double L = 0.299 * r + 0.587 * g + 0.114 * b;
                        double target = range == ColorRange.Highlights ? 0.85 : range == ColorRange.Shadows ? 0.15 : 0.5;
                        double width = 0.18 + 0.25 * fz;
                        m = 1 - Math.Clamp((Math.Abs(L - target) - width * 0.5) / (width * 0.5), 0, 1);
                        if (range == ColorRange.Highlights && L > target) m = 1;
                        if (range == ColorRange.Shadows && L < target) m = 1;
                        break;
                    }
                    case ColorRange.Skin:
                    {
                        RgbToHsv(r, g, b, out double hh, out double s, out double v);
                        bool skin = rgba[i] > 60 && rgba[i] > rgba[i + 1] && rgba[i] > rgba[i + 2] &&
                                    (rgba[i] - Math.Min(rgba[i + 1], rgba[i + 2])) > 12;
                        double hueOk = 1 - Math.Clamp((HueDistance(hh, 22) - 18) / (10 + 20 * fz), 0, 1);
                        double satOk = s > 0.12 && s < 0.7 ? 1 : 0;
                        m = skin && v > 0.2 ? hueOk * satOk : 0;
                        break;
                    }
                    case ColorRange.Custom:
                    {
                        double dr = r - custom.r / 255.0, dg = g - custom.g / 255.0, db = b - custom.b / 255.0;
                        double dist = Math.Sqrt(dr * dr + dg * dg + db * db) / Math.Sqrt(3);
                        double tol = 0.04 + 0.4 * fz;
                        m = 1 - Math.Clamp((dist - tol * 0.6) / (tol * 0.4 + 1e-6), 0, 1);
                        break;
                    }
                    default:
                    {
                        RgbToHsv(r, g, b, out double hh, out double s, out double v);
                        double hd = HueDistance(hh, hue.center);
                        double soft = 6 + 30 * fz;
                        double hm = 1 - Math.Clamp((hd - hue.half) / soft, 0, 1);
                        double sm = Math.Clamp((s - 0.12) / 0.15, 0, 1);
                        double vm = Math.Clamp((v - 0.08) / 0.12, 0, 1);
                        m = hm * sm * vm;
                        break;
                    }
                }
                mask[p] = (byte)Math.Round(Math.Clamp(m, 0, 1) * 255);
            }
            return mask;
        }

        // ════════════════════════════════════════════════════════════════
        // MASKE
        // ════════════════════════════════════════════════════════════════
        /// <summary>Okvir izabranog dela maske; null ako ništa nije izabrano.</summary>
        public static (int x, int y, int w, int h)? MaskBounds(byte[] mask, int w, int h, byte threshold = 127)
        {
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    if (mask[row + x] <= threshold) continue;
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
            }
            if (maxX < 0) return null;
            return (minX, minY, maxX - minX + 1, maxY - minY + 1);
        }

        /// <summary>Udeo izabranog (0..1), sa težinom meke ivice.</summary>
        public static double Coverage(byte[] mask)
        {
            if (mask.Length == 0) return 0;
            long sum = 0;
            foreach (var m in mask) sum += m;
            return sum / (255.0 * mask.Length);
        }

        /// <summary>Širi masku za r piksela (kvadratno jezgro, dva prolaza) — da uklanjanje uhvati i ivicu objekta.</summary>
        public static byte[] Dilate(byte[] mask, int w, int h, int r)
        {
            if (r <= 0) return (byte[])mask.Clone();
            var tmp = new byte[mask.Length];
            var outp = new byte[mask.Length];
            // Radi samo oko izabranog dela — na fotografiji od 24 MP to je razlika između sekunde i trenutka
            var b = MaskBounds(mask, w, h, 0);
            if (b == null) return outp;
            int bx0 = Math.Max(0, b.Value.x - r), by0 = Math.Max(0, b.Value.y - r);
            int bx1 = Math.Min(w - 1, b.Value.x + b.Value.w + r), by1 = Math.Min(h - 1, b.Value.y + b.Value.h + r);
            for (int y = by0; y <= by1; y++)
            {
                int row = y * w;
                for (int x = bx0; x <= bx1; x++)
                {
                    byte m = 0;
                    int x0 = Math.Max(0, x - r), x1 = Math.Min(w - 1, x + r);
                    for (int k = x0; k <= x1 && m < 255; k++) if (mask[row + k] > m) m = mask[row + k];
                    tmp[row + x] = m;
                }
            }
            for (int x = bx0; x <= bx1; x++)
                for (int y = by0; y <= by1; y++)
                {
                    byte m = 0;
                    int y0 = Math.Max(0, y - r), y1 = Math.Min(h - 1, y + r);
                    for (int k = y0; k <= y1 && m < 255; k++) if (tmp[k * w + x] > m) m = tmp[k * w + x];
                    outp[y * w + x] = m;
                }
            return outp;
        }

        public static byte[] CircleMask(int w, int h, double cx, double cy, double radius)
        {
            var m = new byte[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    double v = Math.Clamp(radius + 0.5 - d, 0, 1);
                    m[y * w + x] = (byte)(v * 255);
                }
            return m;
        }

        // ════════════════════════════════════════════════════════════════
        // RETUŠ — popunjavanje (inpainting) metodom "push-pull" piramide +
        // difuzija. Glatko popuni rupu bojama iz okoline; najbolje radi za
        // manje objekte (žica, fleka, mala osoba u daljini, tekst na zidu).
        // ════════════════════════════════════════════════════════════════
        public static void Inpaint(byte[] rgba, int w, int h, byte[] mask, int diffusionIterations = 40)
        {
            var bounds = MaskBounds(mask, w, h, 0);
            if (bounds == null) return;
            var (bx, by, bw, bh) = bounds.Value;
            int margin = Math.Max(8, Math.Max(bw, bh) / 4);
            int x0 = Math.Max(0, bx - margin), y0 = Math.Max(0, by - margin);
            int x1 = Math.Min(w, bx + bw + margin), y1 = Math.Min(h, by + bh + margin);
            int rw = x1 - x0, rh = y1 - y0;

            // Nivo 0: poznati pikseli imaju težinu 1, rupa 0
            var levels = new List<(float[] c, float[] wt, int w, int h)>();
            var c0 = new float[rw * rh * 3];
            var w0 = new float[rw * rh];
            for (int y = 0; y < rh; y++)
                for (int x = 0; x < rw; x++)
                {
                    int src = ((y + y0) * w + (x + x0));
                    int dst = y * rw + x;
                    float known = mask[src] >= 8 ? 0f : 1f;
                    w0[dst] = known;
                    c0[dst * 3] = rgba[src * 4] * known;
                    c0[dst * 3 + 1] = rgba[src * 4 + 1] * known;
                    c0[dst * 3 + 2] = rgba[src * 4 + 2] * known;
                }
            levels.Add((c0, w0, rw, rh));

            // PUSH: smanjuj 2x, sabiraj obojenu težinu
            while (levels[^1].w > 1 || levels[^1].h > 1)
            {
                var (pc, pw, lw, lh) = levels[^1];
                int nw = Math.Max(1, (lw + 1) / 2), nh = Math.Max(1, (lh + 1) / 2);
                var nc = new float[nw * nh * 3];
                var nwt = new float[nw * nh];
                for (int y = 0; y < lh; y++)
                    for (int x = 0; x < lw; x++)
                    {
                        int s = y * lw + x, d = (y / 2) * nw + (x / 2);
                        nwt[d] += pw[s];
                        nc[d * 3] += pc[s * 3]; nc[d * 3 + 1] += pc[s * 3 + 1]; nc[d * 3 + 2] += pc[s * 3 + 2];
                    }
                // normalizuj na težinu <= 1 (zadržava "koliko je poznato")
                for (int i = 0; i < nwt.Length; i++)
                {
                    if (nwt[i] > 1)
                    {
                        float f = 1f / nwt[i];
                        nc[i * 3] *= f; nc[i * 3 + 1] *= f; nc[i * 3 + 2] *= f; nwt[i] = 1;
                    }
                }
                levels.Add((nc, nwt, nw, nh));
                if (levels.Count > 24) break;
            }

            // PULL: od najgrubljeg ka finom, rupe dobijaju vrednost odozgo (bilinearno)
            for (int li = levels.Count - 2; li >= 0; li--)
            {
                var (cc, cw, lw, lh) = levels[li];
                var (uc, uw, uwid, uhei) = levels[li + 1];
                for (int y = 0; y < lh; y++)
                    for (int x = 0; x < lw; x++)
                    {
                        int i = y * lw + x;
                        if (cw[i] >= 1) continue;
                        double fx = Math.Clamp((x - 0.5) / 2.0, 0, uwid - 1), fy = Math.Clamp((y - 0.5) / 2.0, 0, uhei - 1);
                        int ax = (int)fx, ay = (int)fy, bx2 = Math.Min(uwid - 1, ax + 1), by2 = Math.Min(uhei - 1, ay + 1);
                        double tx = fx - ax, ty = fy - ay;
                        float missing = 1 - cw[i];
                        for (int ch = 0; ch < 3; ch++)
                        {
                            double up = Lerp(Lerp(Val(uc, uw, ay * uwid + ax, ch), Val(uc, uw, ay * uwid + bx2, ch), tx),
                                             Lerp(Val(uc, uw, by2 * uwid + ax, ch), Val(uc, uw, by2 * uwid + bx2, ch), tx), ty);
                            cc[i * 3 + ch] += (float)(up * missing);
                        }
                        cw[i] = 1;
                    }
            }

            // Difuzija: zagladi samo popunjene piksele (poznati ostaju netaknuti)
            var (fc, _, _, _) = levels[0];
            var hole = new bool[rw * rh];
            for (int y = 0; y < rh; y++)
                for (int x = 0; x < rw; x++)
                    hole[y * rw + x] = mask[(y + y0) * w + (x + x0)] >= 8;
            var buf = new float[fc.Length];
            for (int it = 0; it < diffusionIterations; it++)
            {
                Array.Copy(fc, buf, fc.Length);
                for (int y = 1; y < rh - 1; y++)
                    for (int x = 1; x < rw - 1; x++)
                    {
                        int i = y * rw + x;
                        if (!hole[i]) continue;
                        for (int ch = 0; ch < 3; ch++)
                            fc[i * 3 + ch] = 0.25f * (buf[(i - 1) * 3 + ch] + buf[(i + 1) * 3 + ch] + buf[(i - rw) * 3 + ch] + buf[(i + rw) * 3 + ch]);
                    }
            }

            // Malo zrna (šuma) da popunjen deo ne izgleda "plastično" u odnosu na fotografiju
            double grain = EstimateNoise(rgba, w, x0, y0, rw, rh, mask);
            var rnd = new Random(12345);

            for (int y = 0; y < rh; y++)
                for (int x = 0; x < rw; x++)
                {
                    int src = (y + y0) * w + (x + x0);
                    byte m = mask[src];
                    if (m == 0) continue;
                    double a = m / 255.0;
                    double n = grain > 0 ? (rnd.NextDouble() - 0.5) * 2 * grain : 0;
                    for (int ch = 0; ch < 3; ch++)
                    {
                        double filled = fc[(y * rw + x) * 3 + ch] + n;
                        rgba[src * 4 + ch] = (byte)Math.Clamp(Math.Round(rgba[src * 4 + ch] * (1 - a) + filled * a), 0, 255);
                    }
                    if (rgba[src * 4 + 3] < 255) rgba[src * 4 + 3] = (byte)Math.Max(rgba[src * 4 + 3], (int)(255 * a));
                }
        }

        private static double Val(float[] c, float[] wt, int i, int ch) => wt[i] > 0 ? c[i * 3 + ch] / wt[i] : 0;
        private static double Lerp(double a, double b, double t) => a + (b - a) * t;

        private static double EstimateNoise(byte[] rgba, int w, int x0, int y0, int rw, int rh, byte[] mask)
        {
            double sum = 0; int count = 0;
            for (int y = y0 + 1; y < y0 + rh - 1; y += 2)
                for (int x = x0 + 1; x < x0 + rw - 1; x += 2)
                {
                    int i = y * w + x;
                    if (mask[i] != 0 || mask[i - 1] != 0 || mask[i + 1] != 0) continue;
                    double l = rgba[i * 4 + 1], l1 = rgba[(i - 1) * 4 + 1], l2 = rgba[(i + 1) * 4 + 1];
                    sum += Math.Abs(l - (l1 + l2) / 2); count++;
                }
            return count == 0 ? 0 : Math.Min(6, sum / count * 0.8);
        }

        /// <summary>Uklanja crvene oči u okviru maske.</summary>
        public static int RemoveRedEye(byte[] rgba, int w, int h, byte[] mask)
        {
            int changed = 0;
            for (int p = 0; p < w * h; p++)
            {
                if (mask[p] == 0) continue;
                int i = p * 4;
                int r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
                int gb = Math.Max(g, b);
                if (r > 50 && r > gb * 1.45)
                {
                    double a = mask[p] / 255.0;
                    double target = (g + b) / 2.0;
                    rgba[i] = (byte)Math.Round(r * (1 - a) + target * a);
                    changed++;
                }
            }
            return changed;
        }

        // ════════════════════════════════════════════════════════════════
        // KONTRAST TEKSTA (WCAG 2.x) — za proveru dizajna
        // ════════════════════════════════════════════════════════════════
        public static double RelativeLuminance(byte r, byte g, byte b) =>
            0.2126 * SrgbToLinear[r] + 0.7152 * SrgbToLinear[g] + 0.0722 * SrgbToLinear[b];

        public static double ContrastRatio((byte r, byte g, byte b) a, (byte r, byte g, byte b) b)
        {
            double la = RelativeLuminance(a.r, a.g, a.b), lb = RelativeLuminance(b.r, b.g, b.b);
            if (la < lb) (la, lb) = (lb, la);
            return (la + 0.05) / (lb + 0.05);
        }

        /// <summary>Prosečna boja pravougaonika (alfa se mesa sa belom, kao na izvozu).</summary>
        public static (byte r, byte g, byte b) AverageColor(byte[] rgba, int w, int h, int rx, int ry, int rw, int rh)
        {
            int x0 = Math.Clamp(rx, 0, w - 1), y0 = Math.Clamp(ry, 0, h - 1);
            int x1 = Math.Clamp(rx + rw, x0 + 1, w), y1 = Math.Clamp(ry + rh, y0 + 1, h);
            double sr = 0, sg = 0, sb = 0; long n = 0;
            int step = Math.Max(1, (int)Math.Sqrt((double)(x1 - x0) * (y1 - y0) / 20000));
            for (int y = y0; y < y1; y += step)
                for (int x = x0; x < x1; x += step)
                {
                    int i = (y * w + x) * 4;
                    double a = rgba[i + 3] / 255.0;
                    sr += rgba[i] * a + 255 * (1 - a); sg += rgba[i + 1] * a + 255 * (1 - a); sb += rgba[i + 2] * a + 255 * (1 - a);
                    n++;
                }
            if (n == 0) return (255, 255, 255);
            return ((byte)(sr / n), (byte)(sg / n), (byte)(sb / n));
        }

        /// <summary>Najsvetliji i najtamniji (5./95. percentil luminanse) deo regiona — tekst mora da se čita i na najgorem mestu.</summary>
        public static ((byte r, byte g, byte b) dark, (byte r, byte g, byte b) light) ExtremeColors(byte[] rgba, int w, int h, int rx, int ry, int rw, int rh)
        {
            int x0 = Math.Clamp(rx, 0, w - 1), y0 = Math.Clamp(ry, 0, h - 1);
            int x1 = Math.Clamp(rx + rw, x0 + 1, w), y1 = Math.Clamp(ry + rh, y0 + 1, h);
            var samples = new List<(double l, int i)>();
            int step = Math.Max(1, (int)Math.Sqrt((double)(x1 - x0) * (y1 - y0) / 20000));
            for (int y = y0; y < y1; y += step)
                for (int x = x0; x < x1; x += step)
                {
                    int i = (y * w + x) * 4;
                    samples.Add((RelativeLuminance(rgba[i], rgba[i + 1], rgba[i + 2]), i));
                }
            if (samples.Count == 0) return ((255, 255, 255), (255, 255, 255));
            samples.Sort((a, b) => a.l.CompareTo(b.l));
            int lo = samples[(int)(samples.Count * 0.05)].i, hi = samples[Math.Min(samples.Count - 1, (int)(samples.Count * 0.95))].i;
            return ((rgba[lo], rgba[lo + 1], rgba[lo + 2]), (rgba[hi], rgba[hi + 1], rgba[hi + 2]));
        }
    }
}
