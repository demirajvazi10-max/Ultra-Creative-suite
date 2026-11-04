using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ImageMagick;
using ImageMagick.Drawing;
using UltraStudio.Models;

namespace UltraStudio.Services
{
    /// <summary>Grupna obrada foldera: podešavanja/izgled, veličina, format, vodeni žig.</summary>
    public static class BatchProcessor
    {
        public static readonly string[] InputExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".gif" };

        public sealed class Options
        {
            public string InputFolder = "";
            public string OutputFolder = "";
            public bool IncludeSubfolders;
            public ImageProject? Adjustments;     // null = bez podešavanja
            public CanvasEngine.ExportOptions Export = new();
            public string WatermarkText = "";
            public int WatermarkPosition = 8;     // 0..8 kao CanvasSize anchor, 8 = dole desno
            public double WatermarkOpacity = 60;
            public double WatermarkSizePercent = 4; // visina slova kao % visine slike
            public string Suffix = "";
        }

        public static List<string> FindImages(Options o) =>
            Directory.EnumerateFiles(o.InputFolder, "*.*", o.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Where(f => InputExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

        /// <summary>Vraća (uspešno, greške). progress(index, total, fileName).</summary>
        public static (int ok, List<string> errors) Run(Options o, Action<int, int, string> progress, CancellationToken ct)
        {
            var files = FindImages(o);
            var errors = new List<string>();
            int ok = 0;
            Directory.CreateDirectory(o.OutputFolder);
            string ext = o.Export.Format.ToLowerInvariant() switch { "jpeg" => "jpg", "tif" => "tiff", var e => e };

            for (int i = 0; i < files.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                string f = files[i];
                progress(i + 1, files.Count, Path.GetFileName(f));
                try
                {
                    using var img = ImageEngine.Load(f);
                    if (o.Adjustments != null) ImageEngine.ApplyAdjustments(img, o.Adjustments, 1.0);
                    CanvasEngine.Finish(img, o.Export);   // veličina pre vodenog žiga, da žig bude uvek iste relativne veličine
                    if (!string.IsNullOrWhiteSpace(o.WatermarkText)) Watermark(img, o);

                    string rel = o.IncludeSubfolders ? Path.GetRelativePath(o.InputFolder, Path.GetDirectoryName(f)!) : "";
                    string dir = Path.Combine(o.OutputFolder, rel);
                    Directory.CreateDirectory(dir);
                    string outPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(f) + o.Suffix + "." + ext);
                    if (string.Equals(Path.GetFullPath(outPath), Path.GetFullPath(f), StringComparison.OrdinalIgnoreCase))
                        outPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(f) + "_edited." + ext); // nikad ne prepisuj original
                    img.Write(outPath);
                    ok++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(f)}: {ex.Message}");
                    DebugLog.Write($"Batch: {f} — {ex}");
                }
            }
            return (ok, errors);
        }

        private static void Watermark(MagickImage img, Options o)
        {
            double fs = Math.Max(10, img.Height * Math.Clamp(o.WatermarkSizePercent, 1, 30) / 100.0);
            double margin = Math.Max(8, Math.Min(img.Width, img.Height) * 0.025);
            int ax = o.WatermarkPosition % 3, ay = o.WatermarkPosition / 3;
            var gravity = (ay, ax) switch
            {
                (0, 0) => Gravity.Northwest, (0, 1) => Gravity.North, (0, 2) => Gravity.Northeast,
                (1, 0) => Gravity.West, (1, 1) => Gravity.Center, (1, 2) => Gravity.East,
                (2, 0) => Gravity.Southwest, (2, 1) => Gravity.South, _ => Gravity.Southeast
            };
            byte alpha = (byte)Math.Round(255 * Math.Clamp(o.WatermarkOpacity, 5, 100) / 100.0);
            using var layer = new MagickImage(MagickColors.Transparent, img.Width, img.Height);
            var shadow = new Drawables().FontPointSize(fs).Gravity(gravity)
                .FillColor(new MagickColor(0, 0, 0, (ushort)(alpha * 257 * 0.6)))
                .Text(margin - fs * 0.04, margin - fs * 0.04, o.WatermarkText);
            var text = new Drawables().FontPointSize(fs).Gravity(gravity)
                .FillColor(new MagickColor(65535, 65535, 65535, (ushort)(alpha * 257)))
                .Text(margin, margin, o.WatermarkText);
            layer.Draw(shadow);
            layer.Draw(text);
            img.Composite(layer, 0, 0, CompositeOperator.Over);
        }
    }
}
