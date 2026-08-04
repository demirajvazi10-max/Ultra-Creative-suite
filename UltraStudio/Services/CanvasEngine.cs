using System;
using ImageMagick;
using ImageMagick.Drawing;
using UltraStudio.Models;

namespace UltraStudio.Services
{
    /// <summary>
    /// Sastavlja finalnu sliku: pozadinska fotografija (sa postojećim
    /// ImageEngine.ApplyAdjustments podešavanjima, ako je fotografija
    /// otvorena) + svaki Layer preko nje, po redosledu u listi (indeks 0 =
    /// najniže, poslednji = najviše — isti princip kao Photoshop/Canva).
    /// Svaki sloj se prvo iscrta na SVOJE providno platno pa se tek onda
    /// spaja (composite) preko trenutnog rezultata — tako opacity radi
    /// identično za tekst, oblik i sliku, bez posebne logike po tipu.
    /// </summary>
    public static class CanvasEngine
    {
        public static MagickImage RenderComposite(ImageProject p)
        {
            MagickImage canvas;

            if (p.HasImage)
            {
                // Pozadinska fotografija zadržava svoju rezoluciju kao veličinu
                // platna — isto ponašanje kao pre uvođenja slojeva, ništa se
                // ne menja za korisnike koji ne koriste dizajn deo uopšte.
                canvas = ImageEngine.ApplyAdjustments(p.OriginalPath!, p);
            }
            else
            {
                canvas = new MagickImage(MagickColors.White, (uint)Math.Max(1, p.CanvasWidth), (uint)Math.Max(1, p.CanvasHeight));
            }

            foreach (var layer in p.Layers)
            {
                if (!layer.Visible) continue;

                using MagickImage? layerImg = RenderLayer(layer, canvas.Width, canvas.Height);
                if (layerImg == null) continue;

                ApplyOpacity(layerImg, layer.Opacity);
                canvas.Composite(layerImg, CompositeOperator.Over);
            }

            return canvas;
        }

        /// <summary>Iscrtava JEDAN sloj na providno platno iste veličine kao glavno platno.</summary>
        private static MagickImage? RenderLayer(Layer layer, uint canvasWidth, uint canvasHeight)
        {
            var img = new MagickImage(MagickColors.Transparent, canvasWidth, canvasHeight);

            switch (layer)
            {
                case TextLayer t:
                {
                    var drawables = new Drawables()
                        .Font(t.FontFamily,
                            t.Italic ? FontStyleType.Italic : FontStyleType.Normal,
                            t.Bold ? FontWeight.Bold : FontWeight.Normal,
                            FontStretch.Normal)
                        .FontPointSize(t.FontSize)
                        .FillColor(SafeColor(t.ColorHex, MagickColors.White))
                        .TextAlignment(TextAlignment.Left)
                        .Text(t.X, t.Y + t.FontSize, t.Text ?? "");
                    img.Draw(drawables);
                    break;
                }
                case ShapeLayer s:
                {
                    var drawables = new Drawables();
                    drawables.FillColor(s.FillEnabled ? SafeColor(s.FillColorHex, MagickColors.Gray) : MagickColors.Transparent);
                    if (s.StrokeEnabled)
                    {
                        drawables.StrokeColor(SafeColor(s.StrokeColorHex, MagickColors.White));
                        drawables.StrokeWidth(s.StrokeWidth);
                    }
                    else
                    {
                        drawables.StrokeColor(MagickColors.Transparent);
                    }

                    switch (s.ShapeKind)
                    {
                        case ShapeKind.Rectangle:
                            drawables.Rectangle(s.X, s.Y, s.X + s.Width, s.Y + s.Height);
                            break;
                        case ShapeKind.Ellipse:
                            drawables.Ellipse(s.X + s.Width / 2, s.Y + s.Height / 2, s.Width / 2, s.Height / 2, 0, 360);
                            break;
                        case ShapeKind.Line:
                            drawables.StrokeWidth(s.StrokeEnabled ? s.StrokeWidth : Math.Max(1, s.StrokeWidth));
                            drawables.StrokeColor(SafeColor(s.StrokeEnabled ? s.StrokeColorHex : s.FillColorHex, MagickColors.White));
                            drawables.Line(s.X, s.Y, s.X + s.Width, s.Y + s.Height);
                            break;
                    }
                    img.Draw(drawables);
                    break;
                }
                case ImageLayer il:
                {
                    if (string.IsNullOrWhiteSpace(il.SourcePath) || !System.IO.File.Exists(il.SourcePath))
                    {
                        img.Dispose();
                        return null;
                    }
                    using var src = new MagickImage(il.SourcePath);
                    src.Resize((uint)Math.Max(1, il.Width), (uint)Math.Max(1, il.Height));
                    img.Composite(src, (int)il.X, (int)il.Y, CompositeOperator.Over);
                    break;
                }
            }

            return img;
        }

        private static void ApplyOpacity(MagickImage img, double opacityPercent)
        {
            if (opacityPercent >= 100) return;
            if (!img.HasAlpha) img.Alpha(AlphaOption.Opaque);
            img.Evaluate(Channels.Alpha, EvaluateOperator.Multiply, Math.Clamp(opacityPercent, 0, 100) / 100.0);
        }

        private static MagickColor SafeColor(string hex, MagickColor fallback)
        {
            try { return new MagickColor(string.IsNullOrWhiteSpace(hex) ? "#FFFFFF" : hex); }
            catch { return fallback; }
        }

        /// <summary>Vraća JPEG bajtove kompozitne slike za prikaz u WPF Image kontroli (preview).</summary>
        public static byte[] RenderPreviewJpeg(ImageProject p, uint maxDimension = 1600)
        {
            using var img = RenderComposite(p);
            if (img.Width > maxDimension || img.Height > maxDimension)
                img.Resize(maxDimension, maxDimension);
            img.Format = MagickFormat.Jpeg;
            img.Quality = 90;
            // JPEG nema alfa kanal — spljoštavamo na belu podlogu da providni
            // delovi platna (kad nema pozadinske fotografije) ne ispadnu crni.
            if (img.HasAlpha)
            {
                img.BackgroundColor = MagickColors.White;
                img.Alpha(AlphaOption.Remove);
            }
            return img.ToByteArray();
        }

        public static void Export(ImageProject p, string outputPath)
        {
            using var img = RenderComposite(p);
            bool noAlphaFormat = outputPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                  outputPath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                  outputPath.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase);
            if (noAlphaFormat && img.HasAlpha)
            {
                img.BackgroundColor = MagickColors.White;
                img.Alpha(AlphaOption.Remove);
            }
            img.Write(outputPath);
        }
    }
}
