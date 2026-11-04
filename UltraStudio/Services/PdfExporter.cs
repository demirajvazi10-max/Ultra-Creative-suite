using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImageMagick;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;
using UltraStudio.Models;

namespace UltraStudio.Services
{
    /// <summary>
    /// Pronalazi .ttf fajl fonta po imenu porodice (Windows registar fontova, za sve
    /// korisnike i za trenutnog korisnika). Za PDF sa pravim tekstom.
    /// </summary>
    public static class FontLocator
    {
        private static List<(string name, string file)>? _fonts;

        private static List<(string name, string file)> Fonts()
        {
            if (_fonts != null) return _fonts;
            var list = new List<(string, string)>();
            string winFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (OperatingSystem.IsWindows())
            {
                foreach (var root in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
                {
                    try
                    {
                        using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
                        if (key == null) continue;
                        foreach (var n in key.GetValueNames())
                        {
                            if (key.GetValue(n) is not string f) continue;
                            string path = Path.IsPathRooted(f) ? f : Path.Combine(winFonts, f);
                            list.Add((n, path));
                        }
                    }
                    catch { }
                }
            }
            _fonts = list;
            return list;
        }

        /// <summary>Putanja do .ttf fajla ili null. Bold/italic se traže u imenu ("Arial Bold Italic (TrueType)").</summary>
        public static string? Find(string family, bool bold, bool italic)
        {
            string fam = family.Trim();
            string Clean(string n) => n.Replace("(TrueType)", "").Replace("(OpenType)", "").Trim();
            var candidates = Fonts()
                .Where(f => f.file.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) && File.Exists(f.file))
                .Select(f => (name: Clean(f.name), f.file))
                .Where(f => f.name.StartsWith(fam, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (candidates.Count == 0) return null;

            int Score((string name, string file) f)
            {
                string rest = f.name[fam.Length..].Trim().ToLowerInvariant();
                bool hasBold = rest.Contains("bold"), hasItalic = rest.Contains("italic") || rest.Contains("oblique");
                int score = 0;
                if (hasBold == bold) score += 2;
                if (hasItalic == italic) score += 2;
                // "Arial Black", "Arial Narrow"... su druge porodice — kazni dodatne reči
                string extra = rest.Replace("bold", "").Replace("italic", "").Replace("oblique", "").Replace("regular", "").Trim();
                if (extra.Length > 0) score -= 3;
                return score;
            }
            return candidates.OrderByDescending(Score).First().file;
        }
    }

    /// <summary>
    /// PDF dokument od svih strana. Fotografije, oblici i stilizovan tekst idu kao slika
    /// strane, a običan tekst kao PRAVI tekst (može da se označi, pretražuje i čita
    /// čitačem ekrana), u redosledu čitanja odozgo nadole.
    /// </summary>
    public static class PdfExporter
    {
        public sealed class Options
        {
            public bool RealText = true;
            public int JpegQuality = 88;
            public double MaxImageDpi = 300;   // manje = manji fajl
            public string Title = "";
            public Func<string, bool, bool, string?>? FontResolver; // za testove; inače FontLocator.Find
        }

        public sealed class Result { public int Pages; public int RealTextLayers; public int RasterTextLayers; }

        public static Result Export(List<ImageProject> pages, string path, Options o, Action<int, int>? progress = null)
        {
            var result = new Result();
            var builder = new PdfDocumentBuilder();
            if (!string.IsNullOrWhiteSpace(o.Title)) builder.DocumentInformation.Title = o.Title;
            builder.DocumentInformation.Producer = "Ultra Studio " + AppVersion();
            var fontCache = new Dictionary<string, PdfDocumentBuilder.AddedFont?>(StringComparer.OrdinalIgnoreCase);
            var resolve = o.FontResolver ?? FontLocator.Find;

            PdfDocumentBuilder.AddedFont? FontFor(TextLayer t)
            {
                string? file = resolve(t.FontFamily, t.Bold, t.Italic);
                if (file == null) return null;
                if (fontCache.TryGetValue(file, out var f)) return f;
                try { f = builder.AddTrueTypeFont(File.ReadAllBytes(file)); }
                catch (Exception ex) { DebugLog.Write($"PdfExporter: font {file} — {ex.Message}"); f = null; }
                fontCache[file] = f;
                return f;
            }

            for (int pi = 0; pi < pages.Count; pi++)
            {
                progress?.Invoke(pi + 1, pages.Count);
                var page = pages[pi];
                double dpi = page.Dpi > 0 ? page.Dpi : 300;
                double k = 72.0 / dpi;
                double wPt = page.CanvasWidth * k, hPt = page.CanvasHeight * k;
                var pdfPage = builder.AddPage(wPt, hPt);

                // Koji tekst ide kao pravi tekst: običan (bez rotacije, stilova, stapanja, providnosti) i font postoji kao .ttf
                var real = new List<(TextLayer t, PdfDocumentBuilder.AddedFont font)>();
                if (o.RealText)
                {
                    foreach (var t in page.Layers.OfType<TextLayer>().Where(t => t.Visible))
                    {
                        bool plain = t.Rotation % 360 == 0 && !t.ShadowEnabled && !t.OutlineEnabled && !t.GlowEnabled &&
                                     t.Blend == BlendMode.Normal && t.Opacity >= 99.5;
                        var font = plain ? FontFor(t) : null;
                        if (font != null && CanEncode(pdfPage, t, font)) real.Add((t, font));
                        else result.RasterTextLayers++;
                    }
                }
                var skip = new HashSet<Guid>(real.Select(r => r.t.Id));

                // Slika strane (sve osim pravog teksta)
                using (var img = RenderWithout(page, skip))
                {
                    double maxDpi = Math.Min(dpi, o.MaxImageDpi);
                    if (maxDpi < dpi)
                    {
                        img.FilterType = FilterType.Lanczos;
                        img.Resize((uint)Math.Max(1, page.CanvasWidth * maxDpi / dpi), (uint)Math.Max(1, page.CanvasHeight * maxDpi / dpi));
                    }
                    img.BackgroundColor = MagickColors.White;
                    img.Alpha(AlphaOption.Remove);
                    img.Format = MagickFormat.Jpeg;
                    img.Quality = (uint)Math.Clamp(o.JpegQuality, 30, 100);
                    pdfPage.AddJpeg(img.ToByteArray(), new PdfRectangle(0, 0, wPt, hPt));
                }

                // Pravi tekst, redosledom čitanja (odozgo nadole, pa sleva nadesno)
                foreach (var (t, font) in real.OrderBy(r => Math.Round(r.t.Y / 10)).ThenBy(r => r.t.X))
                {
                    var c = ImageEngine.ColorBytes(t.ColorHex);
                    pdfPage.SetTextAndFillColor(c.r, c.g, c.b);
                    double fsPt = t.FontSize * k;
                    var lines = TextLayout.Lines(t);
                    int visible = Math.Min(lines.Count, TextLayout.VisibleLineCount(t));
                    for (int i = 0; i < visible; i++)
                    {
                        var line = lines[i];
                        if (line.Text.Length == 0) continue;
                        double baselinePx = t.Y + t.FontSize * 0.9 + i * TextLayout.LineHeight(t);
                        double y = hPt - baselinePx * k;
                        if (t.Align == TextAlign.Justify && t.Wrap && !line.EndsParagraph && line.Words.Count > 1)
                        {
                            var widths = line.Words.Select(w => Width(pdfPage, w.word, fsPt, font)).ToList();
                            double gap = (t.Width * k - widths.Sum()) / (line.Words.Count - 1);
                            double x = t.X * k;
                            for (int wi = 0; wi < line.Words.Count; wi++)
                            {
                                pdfPage.AddText(line.Words[wi].word, fsPt, new PdfPoint(x, y), font);
                                x += widths[wi] + gap;
                            }
                        }
                        else
                        {
                            double lw = Width(pdfPage, line.Text, fsPt, font);
                            double x = t.Align switch
                            {
                                TextAlign.Center => (t.X + t.Width / 2) * k - lw / 2,
                                TextAlign.Right => (t.X + t.Width) * k - lw,
                                _ => t.X * k
                            };
                            pdfPage.AddText(line.Text, fsPt, new PdfPoint(x, y), font);
                        }
                    }
                    pdfPage.ResetColor();
                    result.RealTextLayers++;
                }
                result.Pages++;
            }

            File.WriteAllBytes(path, builder.Build());
            return result;
        }

        private static MagickImage RenderWithout(ImageProject page, HashSet<Guid> skip)
        {
            if (skip.Count == 0) return CanvasEngine.RenderComposite(page, null, out _);
            var copy = ProjectJson.Clone(page);
            foreach (var l in copy.Layers) if (skip.Contains(l.Id)) l.Visible = false;
            return CanvasEngine.RenderComposite(copy, null, out _);
        }

        private static double Width(PdfPageBuilder page, string text, double size, PdfDocumentBuilder.AddedFont font)
        {
            var letters = page.MeasureText(text, size, new PdfPoint(0, 0), font);
            if (letters.Count == 0) return 0;
            return letters[letters.Count - 1].EndBaseLine.X - letters[0].StartBaseLine.X;
        }

        /// <summary>Proba da li font ima sva slova teksta (inače bi PDF imao rupe) — tada ide kao slika.</summary>
        private static bool CanEncode(PdfPageBuilder page, TextLayer t, PdfDocumentBuilder.AddedFont font)
        {
            try
            {
                foreach (var line in (t.Text ?? "").Replace("\r", "").Split('\n'))
                    if (line.Length > 0) page.MeasureText(line, 12, new PdfPoint(0, 0), font);
                return true;
            }
            catch { return false; }
        }

        private static string AppVersion()
        {
            try { return AppInfo.Version; } catch { return ""; }
        }
    }
}
