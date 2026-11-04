using System;
using System.Linq;
using ImageMagick;
using ImageMagick.Drawing;
using UltraStudio.Models;

namespace UltraStudio.Services
{
    /// <summary>
    /// Sastavlja finalnu sliku: radna fotografija (sa nedestruktivnim
    /// podešavanjima) ili prazno platno + svaki vidljiv sloj preko nje, po
    /// redu (indeks 0 = najniži). Pregled se radi u umanjenoj razmeri i tada
    /// se i SLOJEVI crtaju u istoj razmeri (pozicija, font, debljine), pa
    /// pregled tačno odgovara izvozu.
    /// </summary>
    public static class CanvasEngine
    {
        // Keš umanjene radne slike za pregled — dekodiranje velike fotografije
        // je najsporiji deo, a ona se ne menja dok se pomeraju slajderi.
        private static readonly object _cacheLock = new();
        private static string? _cacheKey;
        private static MagickImage? _cacheImg;

        private static MagickImage LoadBase(string path, uint? maxDim)
        {
            string key = path + "|" + (maxDim?.ToString() ?? "full") + "|" + System.IO.File.GetLastWriteTimeUtc(path).Ticks;
            lock (_cacheLock)
            {
                if (_cacheKey == key && _cacheImg != null) return (MagickImage)_cacheImg.Clone();
            }
            var img = ImageEngine.Load(path);
            if (maxDim.HasValue && (img.Width > maxDim.Value || img.Height > maxDim.Value))
            {
                img.FilterType = FilterType.Triangle;
                img.Resize(maxDim.Value, maxDim.Value);
            }
            if (maxDim.HasValue)
            {
                lock (_cacheLock)
                {
                    _cacheImg?.Dispose();
                    _cacheImg = (MagickImage)img.Clone();
                    _cacheKey = key;
                }
            }
            return img;
        }

        // Keš slika iz slojeva već umanjenih na veličinu sloja — slajderi ne čekaju dekodiranje istog fajla
        private static readonly System.Collections.Generic.Dictionary<string, MagickImage> _layerCache = new();
        private static readonly System.Collections.Generic.Queue<string> _layerCacheOrder = new();

        private static MagickImage LayerSource(string path, uint w, uint h)
        {
            string key = $"{path}|{w}x{h}|{System.IO.File.GetLastWriteTimeUtc(path).Ticks}";
            lock (_cacheLock)
            {
                if (_layerCache.TryGetValue(key, out var hit)) return (MagickImage)hit.Clone();
            }
            var src = ImageEngine.Load(path);
            src.FilterType = FilterType.Triangle;
            src.Resize(new MagickGeometry(w, h) { IgnoreAspectRatio = true });
            if ((long)w * h <= 4_000_000) // velike (izvoz) se ne keširaju
            {
                lock (_cacheLock)
                {
                    _layerCache[key] = (MagickImage)src.Clone();
                    _layerCacheOrder.Enqueue(key);
                    while (_layerCacheOrder.Count > 12)
                    {
                        var old = _layerCacheOrder.Dequeue();
                        if (_layerCache.Remove(old, out var o)) o.Dispose();
                    }
                }
            }
            return src;
        }

        /// <summary>Radna slika + podešavanja, BEZ slojeva (za destruktivne izmene i AI).</summary>
        public static MagickImage RenderBase(ImageProject p, uint? maxDim, out double scale, bool applyAdjustments = true)
        {
            MagickImage canvas;
            if (p.HasImage)
            {
                canvas = LoadBase(p.SourcePath!, maxDim);
                scale = p.CanvasWidth > 0 ? (double)canvas.Width / p.CanvasWidth : 1.0;
                if (applyAdjustments) ImageEngine.ApplyAdjustments(canvas, p, scale);
            }
            else
            {
                scale = 1.0;
                if (maxDim.HasValue) scale = Math.Min(1.0, (double)maxDim.Value / Math.Max(p.CanvasWidth, p.CanvasHeight));
                uint w = (uint)Math.Max(1, Math.Round(p.CanvasWidth * scale)), h = (uint)Math.Max(1, Math.Round(p.CanvasHeight * scale));
                var bg = string.Equals(p.CanvasBackgroundHex, "transparent", StringComparison.OrdinalIgnoreCase)
                    ? MagickColors.Transparent
                    : ImageEngine.Color(p.CanvasBackgroundHex, MagickColors.White);
                canvas = new MagickImage(bg, w, h);
            }
            return canvas;
        }

        public static MagickImage RenderComposite(ImageProject p, uint? maxDim, out double scale, Guid? skipLayer = null)
        {
            var canvas = RenderBase(p, maxDim, out scale);
            if (!canvas.HasAlpha) canvas.Alpha(AlphaOption.Set);
            foreach (var layer in p.Layers)
            {
                if (!layer.Visible || layer.Id == skipLayer) continue;
                using var layerImg = RenderLayer(layer, canvas.Width, canvas.Height, scale);
                if (layerImg == null) continue;
                canvas.Composite(layerImg, 0, 0, ToOperator(layer.Blend));
            }
            return canvas;
        }

        public static MagickImage RenderComposite(ImageProject p, uint? maxDim = null) => RenderComposite(p, maxDim, out _);

        public static CompositeOperator ToOperator(BlendMode b) => b switch
        {
            BlendMode.Multiply => CompositeOperator.Multiply,
            BlendMode.Screen => CompositeOperator.Screen,
            BlendMode.Overlay => CompositeOperator.Overlay,
            BlendMode.SoftLight => CompositeOperator.SoftLight,
            BlendMode.HardLight => CompositeOperator.HardLight,
            BlendMode.Darken => CompositeOperator.Darken,
            BlendMode.Lighten => CompositeOperator.Lighten,
            BlendMode.ColorDodge => CompositeOperator.ColorDodge,
            BlendMode.ColorBurn => CompositeOperator.ColorBurn,
            BlendMode.Difference => CompositeOperator.Difference,
            BlendMode.Exclusion => CompositeOperator.Exclusion,
            BlendMode.Hue => CompositeOperator.Hue,
            BlendMode.Saturation => CompositeOperator.Saturate,
            BlendMode.Color => CompositeOperator.Colorize,
            BlendMode.Luminosity => CompositeOperator.Luminize,
            _ => CompositeOperator.Over
        };

        /// <summary>Jedan sloj na providnom platnu veličine glavnog platna, sa rotacijom, stilovima i providnošću.</summary>
        public static MagickImage? RenderLayer(Layer layer, uint cw, uint ch, double s)
        {
            var img = new MagickImage(MagickColors.Transparent, cw, ch);
            double x = layer.X * s, y = layer.Y * s, w = layer.Width * s, h = layer.Height * s;

            switch (layer)
            {
                case TextLayer t:
                    DrawText(img, t, s);
                    break;

                case ShapeLayer sh:
                {
                    var d = new Drawables();
                    d.FillColor(sh.FillEnabled ? ImageEngine.Color(sh.FillColorHex, MagickColors.Gray) : MagickColors.Transparent);
                    if (sh.StrokeEnabled && sh.StrokeWidth > 0)
                        d.StrokeColor(ImageEngine.Color(sh.StrokeColorHex, MagickColors.White)).StrokeWidth(Math.Max(0.5, sh.StrokeWidth * s));
                    else
                        d.StrokeColor(MagickColors.Transparent);
                    switch (sh.ShapeKind)
                    {
                        case ShapeKind.Rectangle:
                            if (sh.CornerRadius > 0)
                                d.RoundRectangle(x, y, x + w, y + h, sh.CornerRadius * s, sh.CornerRadius * s);
                            else
                                d.Rectangle(x, y, x + w, y + h);
                            break;
                        case ShapeKind.Ellipse:
                            d.Ellipse(x + w / 2, y + h / 2, w / 2, h / 2, 0, 360);
                            break;
                        case ShapeKind.Line:
                            d.StrokeWidth(Math.Max(1, sh.StrokeWidth * s));
                            d.StrokeColor(ImageEngine.Color(sh.StrokeEnabled ? sh.StrokeColorHex : sh.FillColorHex, MagickColors.White));
                            d.Line(x, y, x + w, y + (layer.Height <= 1 ? 0 : h));
                            break;
                    }
                    img.Draw(d);
                    break;
                }

                case ImageLayer il:
                {
                    if (string.IsNullOrWhiteSpace(il.SourcePath) || !System.IO.File.Exists(il.SourcePath)) { img.Dispose(); return null; }
                    using var src = LayerSource(il.SourcePath, (uint)Math.Max(1, Math.Round(w)), (uint)Math.Max(1, Math.Round(h)));
                    img.Composite(src, (int)Math.Round(x), (int)Math.Round(y), CompositeOperator.Over);
                    break;
                }

                case GradientLayer g:
                {
                    int gw = Math.Max(1, (int)Math.Round(w)), gh = Math.Max(1, (int)Math.Round(h));
                    var c1 = ImageEngine.ColorBytes(g.StartColorHex, g.StartOpacity);
                    var c2 = ImageEngine.ColorBytes(g.EndColorHex, g.EndOpacity);
                    var px = PixelOps.Gradient(gw, gh, g.Kind == GradientKind.Radial, c1, c2, g.Angle);
                    using var gi = ImageEngine.FromRgba(px, gw, gh);
                    img.Composite(gi, (int)Math.Round(x), (int)Math.Round(y), CompositeOperator.Over);
                    break;
                }
            }

            if (layer.Rotation % 360 != 0)
            {
                img.VirtualPixelMethod = VirtualPixelMethod.Transparent;
                img.Distort(DistortMethod.ScaleRotateTranslate, (layer.X + layer.Width / 2) * s, (layer.Y + layer.Height / 2) * s, 1, layer.Rotation);
            }

            ApplyStyles(ref img, layer, s);

            if (layer.Opacity < 100)
                img.Evaluate(Channels.Alpha, EvaluateOperator.Multiply, Math.Clamp(layer.Opacity, 0, 100) / 100.0);

            return img;
        }

        private static void DrawText(MagickImage img, TextLayer t, double s)
        {
            var lines = TextLayout.Lines(t);
            int visible = Math.Min(lines.Count, TextLayout.VisibleLineCount(t)); // okvir skriva višak, kao u InDesignu
            double fs = Math.Max(1, t.FontSize * s);
            double lineH = TextLayout.LineHeight(t) * s;
            double x = t.X * s, w = t.Width * s;

            IDrawables<ushort> Base(TextAlignment align) => new Drawables()
                .Font(t.FontFamily, t.Italic ? FontStyleType.Italic : FontStyleType.Normal,
                      t.Bold ? FontWeight.Bold : FontWeight.Normal, FontStretch.Normal)
                .FontPointSize(fs)
                .FillColor(ImageEngine.Color(t.ColorHex, MagickColors.White))
                .StrokeColor(MagickColors.Transparent)
                .TextAlignment(align);

            var d = Base(t.Align switch { TextAlign.Center => TextAlignment.Center, TextAlign.Right => TextAlignment.Right, _ => TextAlignment.Left });
            var dj = Base(TextAlignment.Left);
            bool anyJustified = false;
            double anchorX = t.Align switch { TextAlign.Center => x + w / 2, TextAlign.Right => x + w, _ => x };

            for (int i = 0; i < visible; i++)
            {
                var line = lines[i];
                if (line.Text.Length == 0) continue;
                double baseline = t.Y * s + fs * 0.9 + i * lineH;
                if (t.Align == TextAlign.Justify && t.Wrap && !line.EndsParagraph && line.Words.Count > 1)
                {
                    // Obostrano poravnanje: reči pojedinačno, razmak raspoređen po celoj širini okvira
                    double total = line.Words.Sum(wd => wd.width);
                    double gap = (t.Width - total) / (line.Words.Count - 1);
                    double cx = t.X;
                    foreach (var (word, ww) in line.Words)
                    {
                        dj.Text(cx * s, baseline, word);
                        cx += ww + gap;
                    }
                    anyJustified = true;
                }
                else d.Text(anchorX, baseline, line.Text);
            }
            img.Draw(d);
            if (anyJustified) img.Draw(dj);
        }

        /// <summary>Senka, kontura i sjaj se crtaju ISPOD sloja, iz njegovog oblika (alfa kanala).</summary>
        private static void ApplyStyles(ref MagickImage img, Layer l, double s)
        {
            if (!l.ShadowEnabled && !l.OutlineEnabled && !l.GlowEnabled) return;
            var result = new MagickImage(MagickColors.Transparent, img.Width, img.Height);

            if (l.GlowEnabled && l.GlowSize > 0)
            {
                using var glow = Silhouette(img, ImageEngine.Color(l.GlowColorHex, MagickColors.White));
                glow.Blur(0, Math.Max(0.5, l.GlowSize * s / 2));
                result.Composite(glow, 0, 0, CompositeOperator.Over);
                result.Composite(glow, 0, 0, CompositeOperator.Over); // dvaput — sjaj je inače previše bled
            }
            if (l.ShadowEnabled)
            {
                using var sh = Silhouette(img, ImageEngine.Color(l.ShadowColorHex, MagickColors.Black));
                if (l.ShadowBlur > 0) sh.Blur(0, Math.Max(0.3, l.ShadowBlur * s / 2));
                sh.Evaluate(Channels.Alpha, EvaluateOperator.Multiply, Math.Clamp(l.ShadowOpacity, 0, 100) / 100.0);
                result.Composite(sh, (int)Math.Round(l.ShadowOffsetX * s), (int)Math.Round(l.ShadowOffsetY * s), CompositeOperator.Over);
            }
            if (l.OutlineEnabled && l.OutlineWidth > 0)
            {
                using var ol = Silhouette(img, ImageEngine.Color(l.OutlineColorHex, MagickColors.Black));
                int r = Math.Max(1, (int)Math.Round(l.OutlineWidth * s));
                ol.Morphology(new MorphologySettings
                {
                    Method = MorphologyMethod.Dilate,
                    Kernel = Kernel.Disk,
                    KernelArguments = r.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Channels = Channels.Alpha
                });
                result.Composite(ol, 0, 0, CompositeOperator.Over);
            }
            result.Composite(img, 0, 0, CompositeOperator.Over);
            img.Dispose();
            img = result;
        }

        /// <summary>Jednobojna kopija oblika sloja (ista providnost, zadata boja).</summary>
        private static MagickImage Silhouette(MagickImage src, MagickColor color)
        {
            int w = (int)src.Width, h = (int)src.Height;
            var px = ImageEngine.ToRgba(src);
            byte r = (byte)(color.R >> 8), g = (byte)(color.G >> 8), b = (byte)(color.B >> 8);
            for (int i = 0; i < px.Length; i += 4) { px[i] = r; px[i + 1] = g; px[i + 2] = b; }
            return ImageEngine.FromRgba(px, w, h);
        }

        /// <summary>Stvarna veličina teksta u pikselima platna (za automatski okvir tekst-sloja).</summary>
        public static (double width, double height) MeasureText(TextLayer t)
        {
            var lines = TextLayout.Lines(t);
            double maxW = t.Wrap ? t.Width : lines.Select(l => TextLayout.Measure(t, l.Text)).DefaultIfEmpty(0).Max();
            if (maxW <= 0) maxW = t.FontSize;
            double height = t.FontSize * 1.2 + (lines.Count - 1) * TextLayout.LineHeight(t);
            return (Math.Ceiling(maxW), Math.Ceiling(height));
        }

        // ════════════════════════════════════════════════════════════════
        // PREGLED I IZVOZ
        // ════════════════════════════════════════════════════════════════
        public static byte[] RenderPreviewJpeg(ImageProject p, uint maxDimension = 1600, bool showSelection = true)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var img = RenderComposite(p, maxDimension, out double scale);
            if (showSelection && p.Selection.IsActive) DrawSelectionOverlay(img, p.Selection, scale);
            img.BackgroundColor = MagickColors.White;
            img.Alpha(AlphaOption.Remove);
            img.Format = MagickFormat.Jpeg;
            img.Quality = 88;
            var bytes = img.ToByteArray();
            DebugLog.Write($"CanvasEngine.RenderPreviewJpeg: {sw.ElapsedMilliseconds}ms.");
            return bytes;
        }

        /// <summary>Samo za vizuelni pregled: isprekidana ivica selekcije, ili blago zatamnjenje van maske.</summary>
        private static void DrawSelectionOverlay(MagickImage img, Selection sel, double s)
        {
            try
            {
                if (sel.Kind == SelectionKind.Mask || sel.Inverted || sel.Feather > 0)
                {
                    int w = (int)img.Width, h = (int)img.Height;
                    var mask = ImageEngine.BuildMask(sel, w, h, s);
                    var px = ImageEngine.ToRgba(img);
                    for (int i = 0; i < mask.Length; i++)
                    {
                        if (mask[i] >= 250) continue;
                        double k = 0.45 * (1 - mask[i] / 255.0);
                        px[i * 4] = (byte)(px[i * 4] * (1 - k));
                        px[i * 4 + 1] = (byte)(px[i * 4 + 1] * (1 - k));
                        px[i * 4 + 2] = (byte)Math.Min(255, px[i * 4 + 2] * (1 - k) + 60 * k);
                    }
                    ImageEngine.Assign(img, px, w, h);
                }
                if (sel.Kind is SelectionKind.Rectangle or SelectionKind.Ellipse)
                {
                    double x = sel.X * s, y = sel.Y * s, w = sel.Width * s, h = sel.Height * s;
                    foreach (var (color, dash) in new[] { (MagickColors.Black, 0.0), (MagickColors.White, 6.0) })
                    {
                        var d = new Drawables().FillColor(MagickColors.Transparent).StrokeColor(color).StrokeWidth(1.5);
                        if (dash > 0) d.StrokeDashArray(dash, dash);
                        if (sel.Kind == SelectionKind.Ellipse) d.Ellipse(x + w / 2, y + h / 2, w / 2, h / 2, 0, 360);
                        else d.Rectangle(x, y, x + w, y + h);
                        img.Draw(d);
                    }
                }
            }
            catch (Exception ex) { DebugLog.Write("DrawSelectionOverlay: " + ex.Message); }
        }

        public sealed class ExportOptions
        {
            public string Format = "png";        // png, jpg, webp, tiff, bmp
            public int Quality = 92;              // jpg/webp
            public int TargetWidth;               // 0 = original
            public int TargetHeight;              // 0 = zadrži razmeru
            public bool CoverCrop;                // true = popuni tačnu veličinu i iseci višak (društvene mreže)
        }

        public static void Export(ImageProject p, string outputPath, ExportOptions? opt = null)
        {
            opt ??= new ExportOptions { Format = FormatFromPath(outputPath) };
            using var img = RenderComposite(p, null, out _);
            Finish(img, opt);
            img.Write(outputPath);
        }

        /// <summary>Promena veličine + format + kvalitet — zajedničko za izvoz i grupnu obradu.</summary>
        public static void Finish(MagickImage img, ExportOptions opt)
        {
            if (opt.TargetWidth > 0)
            {
                img.FilterType = FilterType.Lanczos;
                if (opt.TargetHeight > 0 && opt.CoverCrop)
                {
                    img.Resize(new MagickGeometry((uint)opt.TargetWidth, (uint)opt.TargetHeight) { FillArea = true });
                    img.Crop(new MagickGeometry((uint)opt.TargetWidth, (uint)opt.TargetHeight), Gravity.Center);
                    img.ResetPage();
                }
                else if (opt.TargetHeight > 0)
                    img.Resize(new MagickGeometry((uint)opt.TargetWidth, (uint)opt.TargetHeight));
                else
                {
                    // dužu stranu na TargetWidth
                    uint target = (uint)opt.TargetWidth;
                    if (Math.Max(img.Width, img.Height) > target) img.Resize(target, target);
                }
            }

            string f = opt.Format.ToLowerInvariant();
            if (f is "jpg" or "jpeg" or "bmp" && img.HasAlpha)
            {
                img.BackgroundColor = MagickColors.White;
                img.Alpha(AlphaOption.Remove);
            }
            img.Format = f switch
            {
                "jpg" or "jpeg" => MagickFormat.Jpeg,
                "webp" => MagickFormat.WebP,
                "tif" or "tiff" => MagickFormat.Tiff,
                "bmp" => MagickFormat.Bmp,
                _ => MagickFormat.Png
            };
            if (f is "jpg" or "jpeg" or "webp") img.Quality = (uint)Math.Clamp(opt.Quality, 1, 100);
            img.Strip(); // bez EXIF-a (GPS lokacija i sl.) u izvezenoj slici
        }

        public static string FormatFromPath(string path)
        {
            string ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            return ext switch { "jpeg" => "jpg", "tif" => "tiff", "" => "png", _ => ext };
        }
    }
}
