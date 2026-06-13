using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UltraVideoEditor
{
    // ═══════════════════════════════════════════════════════════════
    // HIGHLIGHT RENDERER  —  Faza 2 / A
    //
    // Prima List<HighlightSegment> + putanju muzike,
    // i kroz RenderEngine pravi finalni MP4 sa beat-sinhronizovanim rezovima.
    //
    // Workflow:
    //   1. SegmentsToTimelineItems  — konverzija segmenata u TimelineItem listu
    //   2. AddMusicTrack            — muzika kao audio TimelineItem na track 2
    //   3. RenderEngine.RenderSimpleAsync — render sa beatInfo za beat-lock
    //   4. Cleanup                  — briše privremene thumbnail fajlove
    // ═══════════════════════════════════════════════════════════════
    public static class HighlightRenderer
    {
        /// <summary>
        /// Renderuje finalni highlight video.
        /// </summary>
        /// <param name="result">Rezultat AIHighlightEngine.AnalyzeAsync — segmenti + beats.</param>
        /// <param name="musicPath">Putanja do audio fajla pesme.</param>
        /// <param name="outputPath">Gde da sačuva MP4.</param>
        /// <param name="resolution">Rezolucija izlaza, npr. "1920x1080".</param>
        /// <param name="useGPU">Da li da pokuša NVENC hardware encoding.</param>
        /// <param name="progress">Callback za napredak renderovanja (0–100).</param>
        /// <param name="ct">Cancellation token.</param>
        public static async Task RenderAsync(
            HighlightResult  result,
            string           musicPath,
            string           outputPath,
            string           resolution = "1920x1080",
            bool             useGPU    = true,
            IProgress<(int Percent, string Message)> progress = null,
            CancellationToken ct = default)
        {
            if (result == null || !result.Success)
                throw new InvalidOperationException("HighlightResult nije validan.");
            if (result.Segments.Count == 0)
                throw new InvalidOperationException("Nema segmenata za renderovanje.");
            if (!File.Exists(musicPath))
                throw new FileNotFoundException($"Muzički fajl nije pronađen: {musicPath}");

            // Osiguravamo output folder
            string outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            progress?.Report((5, "Pripremam timeline items…"));

            // 1 — Konverzija segmenata u TimelineItem listu
            var items = BuildTimelineItems(result.Segments, musicPath);

            // 2 — Render engine progress wrapper (int → (int, string))
            var innerProgress = new Progress<int>(pct =>
            {
                int mapped = 10 + (int)(pct * 0.88); // 10–98%
                progress?.Report((mapped, $"Renderujem… {pct}%"));
            });

            progress?.Report((8, "Pokrećem RenderEngine…"));

            // 3 — Render
            var engine = new RenderEngine(useHardwareAcceleration: useGPU);
            await engine.RenderSimpleAsync(
                items         : items,
                outputPath    : outputPath,
                format        : "MP4",
                progress      : innerProgress,
                subtitles     : null,
                exportSettings: new ExportSettingsData { Format = "MP4", Quality = "High" },
                cancellationToken: ct,
                useGPU        : useGPU,
                resolution    : resolution,
                fastRender    : false,
                enableSubtitles: false,
                beatInfo      : result.Beats);

            // 4 — Cleanup thumbnailova
            CleanupThumbnails(result.Segments);

            progress?.Report((100, $"Video sačuvan: {Path.GetFileName(outputPath)}"));
        }

        // ── Konverzija ───────────────────────────────────────────────

        /// <summary>
        /// Konvertuje highlight segmente u TimelineItem listu kompatibilnu sa RenderEngine.
        /// Video klipovi idu na track 0, muzika na track 2.
        /// </summary>
        private static List<TimelineItem> BuildTimelineItems(
            List<HighlightSegment> segments, string musicPath)
        {
            var items = new List<TimelineItem>();
            double timelineCursor = 0.0;

            foreach (var seg in segments.OrderBy(s => s.Order))
            {
                // Svaki segment je TimelineItem koji kaže RenderEnginu:
                //   - koji video fajl (Path)
                //   - koji deo da uzme (Start → End u izvornom fajlu)
                //   - gde da ga postavi na timeline (FixedPosition)
                var item = new TimelineItem
                {
                    Path             = seg.SourcePath,
                    Start            = seg.SourceStart,
                    End              = seg.SourceEnd,
                    Duration         = seg.Duration,
                    Name             = BuildItemName(seg),
                    Type             = "Highlight",
                    TrackIndex       = 0,
                    FixedPosition    = timelineCursor,
                    UseFixedPosition = true,
                    Volume           = 0,           // originalnih audio na 0 — koristimo muziku
                    AccessibilityDescription = BuildAccessibilityDescription(seg),
                    ContentTag       = DetermineContentTag(seg),
                };

                // Keyframeovi: ako segment ima arc (dinamičan razvoj), dodajemo blagi zoom
                if (seg.ArcBonus > 8.0)
                    item.Keyframes = BuildArcKeyframes(seg.Duration);

                items.Add(item);
                timelineCursor += seg.Duration;
            }

            // Muzika: jedan audio item koji pokriva ceo timeline
            double totalDuration = segments.Sum(s => s.Duration);
            items.Add(new TimelineItem
            {
                Path             = musicPath,
                Start            = 0.0,
                End              = totalDuration,
                Duration         = totalDuration,
                Name             = $"Muzika — {Path.GetFileNameWithoutExtension(musicPath)}",
                Type             = "Audio",
                TrackIndex       = 2,
                FixedPosition    = 0.0,
                UseFixedPosition = true,
                Volume           = 100,
            });

            return items;
        }

        /// <summary>
        /// Blagi Ken Burns zoom za segmente sa izraženim arc-om.
        /// Ulaz: static → lagani zoom in → static
        /// </summary>
        private static List<AnimationKeyframe> BuildArcKeyframes(double duration)
        {
            return new List<AnimationKeyframe>
            {
                new AnimationKeyframe { Time = 0.0,      Zoom = 1.00, Opacity = 1.0 },
                new AnimationKeyframe { Time = duration * 0.5, Zoom = 1.05, Opacity = 1.0 },
                new AnimationKeyframe { Time = duration,  Zoom = 1.10, Opacity = 1.0 },
            };
        }

        private static string BuildItemName(HighlightSegment seg)
        {
            string base_ = $"HL{seg.Order:D2}";
            if (!string.IsNullOrEmpty(seg.ContentDescription))
                return $"{base_} — {seg.ContentDescription}";
            return base_;
        }

        private static string BuildAccessibilityDescription(HighlightSegment seg)
        {
            var sb = new StringBuilder();
            sb.Append($"Highlight segment {seg.Order}. ");
            sb.Append($"Preuzeto iz videa {AIHighlightEngine.FormatTime(seg.SourceStart)} " +
                      $"do {AIHighlightEngine.FormatTime(seg.SourceEnd)}, " +
                      $"trajanje {seg.Duration:F1} sekundi. ");

            if (!string.IsNullOrEmpty(seg.ContentDescription))
                sb.Append($"Sadržaj: {seg.ContentDescription}. ");

            if (!string.IsNullOrEmpty(seg.ArcDescription) && seg.ArcDescription != "bez izraženog arc-a")
                sb.Append($"Razvoj kadra: {seg.ArcDescription}. ");

            if (seg.Motion != null && seg.Motion.HasStrongMotion)
                sb.Append($"Dinamičan kadar sa pokretom kamere {seg.Motion.Direction}. ");
            else if (seg.Motion != null && seg.Motion.IsStatic)
                sb.Append("Statičan kadar. ");

            sb.Append($"Skor važnosti: {seg.ImportanceScore:F0} od 100.");
            return sb.ToString();
        }

        /// <summary>
        /// Mapira sadržaj segmenta na ContentTag koji RenderEngine koristi
        /// za primenu odgovarajućeg color grading filtera.
        /// </summary>
        private static string DetermineContentTag(HighlightSegment seg)
        {
            if (seg.Motion != null && seg.Motion.HasStrongMotion) return "Action";

            string desc = (seg.ContentDescription ?? "").ToLowerInvariant();
            if (desc.Contains("person") || desc.Contains("face") || desc.Contains("lice"))
                return "Portrait";
            if (desc.Contains("nature") || desc.Contains("outdoor") || desc.Contains("priroda"))
                return "Nature";

            return "Emotional"; // default — topli toni
        }

        // ── Cleanup ──────────────────────────────────────────────────

        private static void CleanupThumbnails(List<HighlightSegment> segments)
        {
            foreach (var seg in segments)
            {
                if (!string.IsNullOrEmpty(seg.ThumbnailPath) &&
                    File.Exists(seg.ThumbnailPath))
                {
                    try { File.Delete(seg.ThumbnailPath); } catch { }
                }
            }
        }
    }
}
