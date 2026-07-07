using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UltraVideoEditor
{
    // ═══════════════════════════════════════════════════════════════
    // COLOR GRADING ENGINE — Faza 4C
    // AI-automatski color grade po ContentTag / VisionResult.
    // Generates FFmpeg vf filter string on a per-clip basis.
    // ═══════════════════════════════════════════════════════════════

    public enum GradePreset
    {
        Auto,           // AI bira po VisionResult
        Cinematic,      // visok kontrast, desaturate highlights, teal-orange
        Warm,           // topli ton, orange/yellow boost
        Cool,           // hladni ton, blue/cyan boost
        Vintage,        // fade, grain, warm shadows
        Vivid,          // boost saturation + kontrast
        Noir,           // crno-belo, visok kontrast
        Golden,         // golden hour — warm, soft
        Morning,        // hladno plavo, visok brightness
        Moody,          // tamno, crush blacks, desat
        Natural,        // samo normalizacija, nema grade-a
    }

    public class GradeResult
    {
        public string        VideoFilter   { get; set; } = "";  // FFmpeg -vf string
        public GradePreset   AppliedPreset { get; set; }
        public string        Description   { get; set; } = "";
        public string        Error         { get; set; }
        public bool          Success       => string.IsNullOrEmpty(Error);
    }

    public class ClipGradeResult
    {
        public TimelineItem  Item          { get; set; }
        public GradeResult   Grade         { get; set; }
        public string        PreviewPath   { get; set; }   // thumbnail sa primenjenim grade-om
        public bool          Selected      { get; set; } = true;
    }

    public static class ColorGradingEngine
    {
        // ── FFmpeg filter definicije po presetima ────────────────────

        private static readonly Dictionary<GradePreset, string> FilterMap =
            new Dictionary<GradePreset, string>
        {
            [GradePreset.Natural]   = "eq=saturation=1.0:brightness=0.0:contrast=1.0",
            [GradePreset.Cinematic] = "eq=saturation=0.88:contrast=1.18:brightness=-0.03," +
                                      "curves=r='0/0 0.3/0.26 0.7/0.72 1/0.96':" +
                                      "g='0/0 0.3/0.28 0.7/0.70 1/0.94':" +
                                      "b='0/0.04 0.3/0.32 0.7/0.68 1/0.92'",
            [GradePreset.Warm]      = "eq=saturation=1.12:brightness=0.02:contrast=1.04," +
                                      "curves=r='0/0 0.5/0.56 1/1.0':" +
                                      "g='0/0 0.5/0.525 1/0.97':" +
                                      "b='0/0 0.5/0.46 1/0.90'",
            [GradePreset.Cool]      = "eq=saturation=1.05:brightness=0.01:contrast=1.05," +
                                      "curves=r='0/0 0.5/0.46 1/0.92':" +
                                      "g='0/0 0.5/0.50 1/0.97':" +
                                      "b='0/0.03 0.5/0.54 1/1.0'",
            [GradePreset.Vintage]   = "eq=saturation=0.80:contrast=1.08:brightness=0.02," +
                                      "curves=r='0/0.05 0.5/0.54 1/0.96':" +
                                      "g='0/0.03 0.5/0.50 1/0.90':" +
                                      "b='0/0.08 0.5/0.46 1/0.82'," +
                                      "noise=alls=4:allf=t",
            [GradePreset.Vivid]     = "eq=saturation=1.35:contrast=1.15:brightness=0.01," +
                                      "curves=r='0/0 0.5/0.52 1/1.0':" +
                                      "g='0/0 0.5/0.52 1/1.0':" +
                                      "b='0/0 0.5/0.52 1/1.0'",
            [GradePreset.Noir]      = "hue=s=0," +
                                      "eq=contrast=1.35:brightness=-0.04," +
                                      "curves=all='0/0 0.25/0.18 0.75/0.82 1/1'",
            [GradePreset.Golden]    = "eq=saturation=1.08:brightness=0.03:contrast=1.02," +
                                      "curves=r='0/0 0.4/0.46 0.8/0.84 1/1.0':" +
                                      "g='0/0 0.4/0.42 0.8/0.80 1/0.96':" +
                                      "b='0/0 0.4/0.36 0.8/0.70 1/0.86'",
            [GradePreset.Morning]   = "eq=saturation=0.95:brightness=0.05:contrast=1.06," +
                                      "curves=r='0/0 0.5/0.48 1/0.94':" +
                                      "g='0/0 0.5/0.50 1/0.97':" +
                                      "b='0/0.04 0.5/0.55 1/1.0'",
            [GradePreset.Moody]     = "eq=saturation=0.78:contrast=1.22:brightness=-0.06," +
                                      "curves=r='0/0 0.2/0.14 0.7/0.66 1/0.94':" +
                                      "g='0/0 0.2/0.13 0.7/0.64 1/0.90':" +
                                      "b='0/0 0.2/0.16 0.7/0.68 1/0.96'",
        };

        public static readonly Dictionary<GradePreset, string> PresetDescriptions =
            new Dictionary<GradePreset, string>
        {
            [GradePreset.Auto]      = "AI automatically selects based on clip content",
            [GradePreset.Cinematic] = "Cinematic — high contrast, teal-orange",
            [GradePreset.Warm]      = "Warm tone — orange/yellow boost",
            [GradePreset.Cool]      = "Cool tone — blue/cyan boost",
            [GradePreset.Vintage]   = "Vintage — fade, grain, warm tones",
            [GradePreset.Vivid]     = "Vivid — color and contrast boost",
            [GradePreset.Noir]      = "Noir — black&white, high contrast",
            [GradePreset.Golden]    = "Golden hour — warm, soft light",
            [GradePreset.Morning]   = "Morning — cool blue, brightness",
            [GradePreset.Moody]     = "Moody — crush blacks, desaturate",
            [GradePreset.Natural]   = "Prirodno — samo normalizacija",
        };

        // ── Javni API ────────────────────────────────────────────────

        /// <summary>
        /// Analyzes clips and generates a color grade for each.
        /// Ako je preset == Auto, AI bira na osnovu VisionResult.
        /// </summary>
        public static async Task<List<ClipGradeResult>> AnalyzeAndGradeAsync(
            List<TimelineItem> items,
            GradePreset preset,
            IProgress<(int Percent, string Message)> progress = null,
            CancellationToken ct = default)
        {
            string ffmpegPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");

            var results = new List<ClipGradeResult>();
            int total   = items.Count;
            int done    = 0;

            var sem = new SemaphoreSlim(3);
            var tasks = items.Select(async item =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    GradePreset chosen = preset;
                    VisionResult vision = null;

                    if (preset == GradePreset.Auto && File.Exists(item.Path))
                    {
                        try { vision = await VisionAnalyzer.AnalyzeClipAsync(item.Path, ffmpegPath, ct); }
                        catch { }
                        chosen = AutoChoosePreset(vision, item);
                    }

                    var grade = BuildGrade(chosen, vision);

                    // Preview thumbnail sa grade-om
                    string previewPath = null;
                    if (File.Exists(item.Path))
                    {
                        previewPath = await GenerateGradePreviewAsync(
                            item, grade.VideoFilter, ffmpegPath, ct);
                    }

                    int pct = 10 + (int)(Interlocked.Increment(ref done) * 85.0 / total);
                    progress?.Report((pct, $"Grade: {item.Name ?? System.IO.Path.GetFileName(item.Path)}  [{chosen}]"));

                    lock (results)
                    {
                        results.Add(new ClipGradeResult
                        {
                            Item        = item,
                            Grade       = grade,
                            PreviewPath = previewPath,
                            Selected    = true,
                        });
                    }
                }
                catch (Exception ex)
                {
                    lock (results)
                    {
                        results.Add(new ClipGradeResult
                        {
                            Item  = item,
                            Grade = new GradeResult { Error = ex.Message },
                        });
                    }
                }
                finally { sem.Release(); }
            });

            progress?.Report((5, "Starting clip analysis…"));
            await Task.WhenAll(tasks);
            progress?.Report((98, "Sortiranje…"));

            // Vrati u originalnom redosledu
            var ordered = items
                .Select(i => results.FirstOrDefault(r => r.Item == i))
                .Where(r => r != null)
                .ToList();

            progress?.Report((100, "Gotovo!"));
            return ordered;
        }

        /// <summary>
        /// Primenjuje grade na klipoive — upisuje filter u AudioDescription tag
        /// which RenderEngine reads at render time.
        /// </summary>
        public static void ApplyGradesToItems(List<ClipGradeResult> grades)
        {
            foreach (var g in grades.Where(g => g.Selected && g.Grade.Success))
            {
                // We store the grade filter in ContentTag with prefix "grade:"
                // RenderEngine reads it and inserts it into the vf pipeline
                string existing = g.Item.ContentTag ?? "";
                // Ukloni stari grade tag ako postoji
                existing = System.Text.RegularExpressions.Regex.Replace(
                    existing, @"\[grade:[^\]]*\]", "").Trim();
                g.Item.ContentTag = string.IsNullOrEmpty(existing)
                    ? $"[grade:{g.Grade.VideoFilter}]"
                    : $"{existing} [grade:{g.Grade.VideoFilter}]";
            }
        }

        /// <summary>
        /// Returns FFmpeg vf filter string for the given preset — used directly by RenderEngine.
        /// </summary>
        public static string GetFilterForPreset(GradePreset preset)
            => FilterMap.TryGetValue(preset, out string f) ? f : "";

        // ── Auto AI izbor preseta ─────────────────────────────────────

        private static GradePreset AutoChoosePreset(VisionResult v, TimelineItem item)
        {
            if (v == null) return GradePreset.Natural;

            string tag = (item.ContentTag ?? "").ToLower();
            string label = (v.TopLabel ?? "").ToLower();

            // Noir: black-and-white content, night scenes
            if (label.Contains("night") || label.Contains("noc") || label.Contains("dark"))
                return GradePreset.Moody;

            // Vintage: retro, stari materijal
            if (tag.Contains("vintage") || tag.Contains("retro") || label.Contains("archive"))
                return GradePreset.Vintage;

            // Portrait / lica → Warm ili Golden
            if (v.HasFaces)
                return v.IsWarm ? GradePreset.Golden : GradePreset.Warm;

            // Eksterijer
            if (v.IsOutdoor)
            {
                if (v.IsWarm && v.Luminance > 0.55) return GradePreset.Golden;
                if (!v.IsWarm && v.Luminance > 0.55) return GradePreset.Morning;
                if (v.Luminance < 0.35) return GradePreset.Moody;
                return GradePreset.Vivid;
            }

            // Interijer
            if (v.IsWarm)  return GradePreset.Warm;
            if (!v.IsWarm && v.Luminance < 0.4) return GradePreset.Cinematic;

            // Action/sport tag
            if (tag.Contains("action") || tag.Contains("sport"))
                return GradePreset.Vivid;

            // Visoka saturacija → Vivid
            if (v.Saturation > 0.65) return GradePreset.Vivid;

            // Niska saturacija + nizak luminance → Cinematic
            if (v.Saturation < 0.35 && v.Luminance < 0.45) return GradePreset.Cinematic;

            return GradePreset.Natural;
        }

        private static GradeResult BuildGrade(GradePreset preset, VisionResult vision)
        {
            string filter = GetFilterForPreset(preset);
            return new GradeResult
            {
                VideoFilter   = filter,
                AppliedPreset = preset,
                Description   = PresetDescriptions.TryGetValue(preset, out string d) ? d : "",
            };
        }

        // ── Preview thumbnail sa grade-om ─────────────────────────────

        private static async Task<string> GenerateGradePreviewAsync(
            TimelineItem item, string vfFilter,
            string ffmpegPath, CancellationToken ct)
        {
            string outPath = Path.Combine(Path.GetTempPath(), $"grade_{Guid.NewGuid():N}.jpg");
            double seek    = item.Start + item.Duration * 0.4;

            // Combine grade filter sa scale
            string vf = string.IsNullOrEmpty(vfFilter)
                ? "scale=200:112:flags=lanczos"
                : $"{vfFilter},scale=200:112:flags=lanczos";

            string args = $"-nostdin -ss {seek.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} " +
                          $"-i \"{item.Path}\" -vframes 1 -vf \"{vf}\" " +
                          $"-q:v 3 -y \"{outPath}\"";

            var psi = new ProcessStartInfo
            {
                FileName               = ffmpegPath,
                Arguments              = args,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardError  = true,
                RedirectStandardOutput = true,
            };

            try
            {
                using var proc = Process.Start(psi);
                if (proc == null) return null;
                using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts2.CancelAfter(10000);
                await proc.WaitForExitAsync(cts2.Token);
                return File.Exists(outPath) ? outPath : null;
            }
            catch { return null; }
        }
    }
}
