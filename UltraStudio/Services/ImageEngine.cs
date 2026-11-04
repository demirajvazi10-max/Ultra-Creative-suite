using System;
using System.IO;
using ImageMagick;
using ImageMagick.Drawing;
using UltraStudio.Models;

namespace UltraStudio.Services
{
    public enum FilterKind
    {
        GaussianBlur, MotionBlur, SpinBlur, UnsharpMask, AddNoise, ReduceNoise,
        Emboss, FindEdges, OilPaint, Sketch, Charcoal, Pixelate, Solarize, Swirl, Wave, Pinch
    }

    /// <summary>
    /// Magick.NET pomoćne metode: konverzija u/iz RGBA bajtova, podešavanja
    /// (nedestruktivna), filteri i maske selekcije.
    /// </summary>
    public static class ImageEngine
    {
        // ════════════════════════════════════════════════════════════════
        // BAJTOVI
        // ════════════════════════════════════════════════════════════════
        public static byte[] ToRgba(MagickImage img)
        {
            using var c = (MagickImage)img.Clone();
            if (!c.HasAlpha) c.Alpha(AlphaOption.Opaque);
            c.Depth = 8;
            return c.ToByteArray(MagickFormat.Rgba);
        }

        public static MagickImage FromRgba(byte[] rgba, int w, int h) =>
            new MagickImage(rgba, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, PixelMapping.RGBA));

        /// <summary>Zamenjuje sadržaj POSTOJEĆE slike RGBA bajtovima (pozivalac zadržava istu instancu).</summary>
        public static void Assign(MagickImage target, byte[] rgba, int w, int h) =>
            target.ReadPixels(rgba, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, PixelMapping.RGBA));

        public static byte[] ToGray(MagickImage img)
        {
            using var c = (MagickImage)img.Clone();
            c.Alpha(AlphaOption.Off);
            c.Depth = 8;
            return c.ToByteArray(MagickFormat.Gray);
        }

        public static MagickImage FromGray(byte[] gray, int w, int h)
        {
            var rgb = new byte[gray.Length * 3];
            for (int i = 0; i < gray.Length; i++) { rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = gray[i]; }
            return new MagickImage(rgb, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, PixelMapping.RGB));
        }

        public static void SavePng(MagickImage img, string path)
        {
            using var c = (MagickImage)img.Clone();
            c.Format = MagickFormat.Png;
            c.Write(path);
        }

        public static void SaveGrayPng(byte[] gray, int w, int h, string path)
        {
            using var img = FromGray(gray, w, h);
            img.Grayscale();
            img.Format = MagickFormat.Png;
            img.Write(path);
        }

        public static (int width, int height) GetDimensions(string path)
        {
            var info = new MagickImageInfo(path);
            return ((int)info.Width, (int)info.Height);
        }

        public static MagickColor Color(string? hex, MagickColor fallback)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(hex)) return fallback;
                return new MagickColor(hex.Trim());
            }
            catch { return fallback; }
        }

        public static (byte r, byte g, byte b, byte a) ColorBytes(string? hex, double opacityPercent = 100)
        {
            var c = Color(hex, MagickColors.Black);
            // Q16: vrednosti kanala su 0..65535
            byte r = (byte)(c.R >> 8), g = (byte)(c.G >> 8), b = (byte)(c.B >> 8);
            byte a = (byte)Math.Round((c.A >> 8) * Math.Clamp(opacityPercent, 0, 100) / 100.0);
            return (r, g, b, a);
        }

        /// <summary>Učitava sliku i primenjuje EXIF orijentaciju (fotografije sa telefona inače ispadnu položene).</summary>
        public static MagickImage Load(string path)
        {
            var img = new MagickImage(path);
            img.AutoOrient();
            return img;
        }

        // ════════════════════════════════════════════════════════════════
        // NEDESTRUKTIVNA PODEŠAVANJA — redosled kao u Lightroom/Camera Raw:
        // ekspozicija → tonovi → balans belog → živost → svetlina/kontrast
        // → zasićenost/nijansa → jasnoća → oštrina/zamućenje → efekti.
        // scale = koliko je slika umanjena za pregled (da filteri izgledaju isto).
        // ════════════════════════════════════════════════════════════════
        public static void ApplyAdjustments(MagickImage img, ImageProject p, double scale = 1.0)
        {
            if (!p.HasAdjustments) return;
            int w = (int)img.Width, h = (int)img.Height;

            var tone = new PixelOps.ToneParams
            {
                Exposure = p.Exposure, Gamma = p.Gamma, Highlights = p.Highlights, Shadows = p.Shadows,
                Temperature = p.Temperature, Tint = p.Tint, Vibrance = p.Vibrance
            };
            if (!tone.IsIdentity)
                ReplacePixels(img, px => PixelOps.ApplyTone(px, w, h, tone));

            if (p.Brightness != 0 || p.Contrast != 0)
                img.BrightnessContrast(new Percentage(p.Brightness), new Percentage(p.Contrast));

            if (p.Saturation != 0 || p.Hue != 0)
                img.Modulate(new Percentage(100), new Percentage(Math.Max(0, 100 + p.Saturation)),
                    new Percentage(100 + p.Hue * 100.0 / 180.0));

            if (p.Clarity > 0)
                img.UnsharpMask(0, Math.Max(1, 18 * scale), p.Clarity / 100.0 * 0.9, 0.02);
            else if (p.Clarity < 0)
                img.Blur(0, Math.Max(0.3, Math.Abs(p.Clarity) / 100.0 * 3 * scale));

            if (p.Sharpen > 0) img.Sharpen(0, Math.Max(0.3, Math.Min(2.5, p.Sharpen * 0.25) * Math.Max(0.35, scale)));
            if (p.Blur > 0) img.Blur(0, Math.Max(0.3, Math.Min(4.0, p.Blur * 0.4) * scale));

            if (p.Grayscale) img.Modulate(new Percentage(100), new Percentage(0), new Percentage(100));
            if (p.Sepia) img.SepiaTone(new Percentage(80));
            if (p.Posterize >= 2) img.Posterize((int)Math.Clamp(p.Posterize, 2, 32));
            if (p.Threshold > 0)
            {
                img.Modulate(new Percentage(100), new Percentage(0), new Percentage(100));
                img.Threshold(new Percentage(Math.Clamp(p.Threshold, 1, 99)));
            }
            if (p.Invert) img.Negate(Channels.RGB);

            if (p.Fade > 0 || p.Vignette > 0)
            {
                var fx = new PixelOps.ToneParams { Fade = p.Fade };
                ReplacePixels(img, px =>
                {
                    if (p.Fade > 0) PixelOps.ApplyTone(px, w, h, fx);
                    if (p.Vignette > 0) PixelOps.ApplyVignette(px, w, h, p.Vignette);
                });
            }
        }

        /// <summary>RGBA prolaz u C#-u, rezultat se vraća u ISTU sliku (zadržava providnost).</summary>
        public static void ReplacePixels(MagickImage img, Action<byte[]> process)
        {
            int w = (int)img.Width, h = (int)img.Height;
            var px = ToRgba(img);
            process(px);
            Assign(img, px, w, h);
        }

        // ════════════════════════════════════════════════════════════════
        // FILTERI (destruktivni, iz Filter menija). amount 1..100.
        // ════════════════════════════════════════════════════════════════
        public static void ApplyFilter(MagickImage img, FilterKind kind, double amount)
        {
            double a = Math.Clamp(amount, 1, 100) / 100.0;
            double size = Math.Max(img.Width, img.Height) / 1000.0; // da jačina izgleda slično na maloj i velikoj slici
            size = Math.Max(0.5, size);
            switch (kind)
            {
                case FilterKind.GaussianBlur: img.Blur(0, 0.5 + a * 12 * size); break;
                case FilterKind.MotionBlur: img.MotionBlur(0, 1 + a * 25 * size, 0); break;
                case FilterKind.SpinBlur: img.RotationalBlur(1 + a * 20); break;
                case FilterKind.UnsharpMask: img.UnsharpMask(0, 1.2 * size, 0.3 + a * 2.2, 0.01); break;
                case FilterKind.AddNoise: img.AddNoise(NoiseType.Gaussian, 0.1 + a * 1.4); break;
                case FilterKind.ReduceNoise:
                {
                    uint r = (uint)Math.Max(1, Math.Round(a * 4 * size));
                    img.Statistic(StatisticType.Median, r * 2 + 1, r * 2 + 1);
                    break;
                }
                case FilterKind.Emboss: img.Emboss(0, 0.5 + a * 3 * size); break;
                case FilterKind.FindEdges:
                    img.Edge(0.5 + a * 3 * size);
                    img.Negate(Channels.RGB);
                    img.Modulate(new Percentage(100), new Percentage(0), new Percentage(100));
                    break;
                case FilterKind.OilPaint: img.OilPaint(1 + a * 6 * size, 1); break;
                case FilterKind.Sketch: img.Sketch(0, 1 + a * 15 * size, 135); break;
                case FilterKind.Charcoal: img.Charcoal(0, 0.5 + a * 3 * size); break;
                case FilterKind.Pixelate:
                {
                    uint w = img.Width, h = img.Height;
                    double block = 2 + a * 48 * size;
                    uint sw = (uint)Math.Max(1, Math.Round(w / block)), sh = (uint)Math.Max(1, Math.Round(h / block));
                    img.Sample(sw, sh);
                    img.Sample(new MagickGeometry(w, h) { IgnoreAspectRatio = true });
                    break;
                }
                case FilterKind.Solarize: img.Solarize(new Percentage(100 - a * 60)); break;
                case FilterKind.Swirl: img.Swirl(PixelInterpolateMethod.Bilinear, 30 + a * 330); break;
                case FilterKind.Wave:
                    img.BackgroundColor = MagickColors.Transparent;
                    img.Wave(PixelInterpolateMethod.Bilinear, 2 + a * 20 * size, 40 + 160 * size);
                    break;
                case FilterKind.Pinch: img.Implode(0.1 + a * 0.8, PixelInterpolateMethod.Bilinear); break;
            }
        }

        // ════════════════════════════════════════════════════════════════
        // MASKA SELEKCIJE — siva slika veličine w×h (255 = izabrano)
        // ════════════════════════════════════════════════════════════════
        public static byte[] BuildMask(Selection sel, int w, int h, double scale = 1.0)
        {
            if (!sel.IsActive) { var all = new byte[w * h]; Array.Fill(all, (byte)255); return all; }

            using MagickImage m = sel.Kind switch
            {
                SelectionKind.Mask when !string.IsNullOrEmpty(sel.MaskPath) && File.Exists(sel.MaskPath) => LoadMask(sel.MaskPath!, w, h),
                _ => DrawShapeMask(sel, w, h, scale)
            };
            if (sel.Feather > 0) m.Blur(0, Math.Max(0.3, sel.Feather * scale / 2));
            if (sel.Inverted) m.Negate(Channels.RGB);
            var gray = ToGray(m);
            if (gray.Length != w * h) Array.Resize(ref gray, w * h);
            return gray;
        }

        private static MagickImage LoadMask(string path, int w, int h)
        {
            var m = new MagickImage(path);
            m.Alpha(AlphaOption.Off);
            if (m.Width != w || m.Height != h)
                m.Resize(new MagickGeometry((uint)w, (uint)h) { IgnoreAspectRatio = true });
            return m;
        }

        private static MagickImage DrawShapeMask(Selection sel, int w, int h, double scale)
        {
            var m = new MagickImage(MagickColors.Black, (uint)w, (uint)h);
            double x = sel.X * scale, y = sel.Y * scale, sw = sel.Width * scale, sh = sel.Height * scale;
            var d = new Drawables().FillColor(MagickColors.White).StrokeColor(MagickColors.Transparent);
            if (sel.Kind == SelectionKind.Ellipse)
                d.Ellipse(x + sw / 2, y + sh / 2, sw / 2, sh / 2, 0, 360);
            else
                d.Rectangle(x, y, x + sw - 1, y + sh - 1);
            m.Draw(d);
            return m;
        }

        /// <summary>out = original*(1-m) + processed*m, za sve kanale uključujući providnost.</summary>
        public static void BlendThroughMask(MagickImage original, MagickImage processed, byte[] mask)
        {
            int w = (int)original.Width, h = (int)original.Height;
            var o = ToRgba(original);
            byte[] pr;
            if (processed.Width != original.Width || processed.Height != original.Height)
            {
                using var fit = (MagickImage)processed.Clone();
                fit.Resize(new MagickGeometry((uint)w, (uint)h) { IgnoreAspectRatio = true });
                pr = ToRgba(fit);
            }
            else pr = ToRgba(processed);

            for (int p = 0; p < w * h; p++)
            {
                int m = mask[p];
                if (m == 0) continue;
                int i = p * 4;
                if (m == 255) { o[i] = pr[i]; o[i + 1] = pr[i + 1]; o[i + 2] = pr[i + 2]; o[i + 3] = pr[i + 3]; continue; }
                for (int c = 0; c < 4; c++) o[i + c] = (byte)((o[i + c] * (255 - m) + pr[i + c] * m + 127) / 255);
            }
            Assign(original, o, w, h);
        }
    }
}
