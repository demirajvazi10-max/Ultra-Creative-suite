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
    // ACCESSIBILITY REPORT GENERATOR  —  Faza 3 / C
    //
    // Generates a complete audio-description script for all segments.
    // Output is a TXT file readable by a screen reader and TTS engine.
    //
    // Report structure:
    //   1. Zaglavlje (metadata, trajanje, BPM)
    //   2. Per-segment audio description
    //   3. Navigacioni markeri (timestamp per segment)
    //   4. TTS summary (shorter, no ASCII art)
    // ═══════════════════════════════════════════════════════════════

    public class AccessibilityReportOptions
    {
        /// <summary>Ime projekta / videa za zaglavlje.</summary>
        public string ProjectName       { get; set; } = "Highlight Video";

        /// <summary>Report language ("sr" or "en").</summary>
        public string Language          { get; set; } = "sr";

        /// <summary>Whether to include TTS-optimized version at the end.</summary>
        public bool   IncludeTtsSummary { get; set; } = true;

        /// <summary>Whether to include navigation markers (timestamp list).</summary>
        public bool   IncludeNavMarkers { get; set; } = true;

        /// <summary>Whether to include transition details.</summary>
        public bool   IncludeTransitions{ get; set; } = true;
    }

    public class AccessibilityReport
    {
        public string FullText     { get; set; }
        public string TtsSummary   { get; set; }
        public string OutputPath   { get; set; }
        public bool   Success      { get; set; }
        public string Error        { get; set; }
    }

    public static class AccessibilityReportGenerator
    {
        // ── Javni API ────────────────────────────────────────────────

        /// <summary>
        /// Generates an accessibility report and optionally saves it to disk.
        /// </summary>
        public static async Task<AccessibilityReport> GenerateAsync(
            HighlightResult              result,
            List<TransitionDecision>     transitions = null,
            AudioMixSettings             audioSettings = null,
            AccessibilityReportOptions   options = null,
            string                       outputPath = null,
            CancellationToken            ct = default)
        {
            options ??= new AccessibilityReportOptions();

            if (result == null || !result.Success)
                return Fail("HighlightResult is not valid.");

            await Task.Yield(); // async kompatibilnost

            var fullText   = BuildFullReport(result, transitions, audioSettings, options);
            var ttsSummary = options.IncludeTtsSummary
                ? BuildTtsSummary(result, options)
                : "";

            // Save if path was provided
            if (!string.IsNullOrEmpty(outputPath))
            {
                try
                {
                    Directory.CreateDirectory(
                        Path.GetDirectoryName(outputPath) ?? ".");
                    await File.WriteAllTextAsync(
                        outputPath, fullText, Encoding.UTF8, ct);
                }
                catch (Exception ex)
                {
                    return Fail($"Error saving: {ex.Message}");
                }
            }

            return new AccessibilityReport
            {
                FullText   = fullText,
                TtsSummary = ttsSummary,
                OutputPath = outputPath,
                Success    = true,
            };
        }

        // ── Full Report Builder ──────────────────────────────────────

        private static string BuildFullReport(
            HighlightResult            result,
            List<TransitionDecision>   transitions,
            AudioMixSettings           audio,
            AccessibilityReportOptions opts)
        {
            bool sr = opts.Language == "sr";
            var sb = new StringBuilder();

            // ── Zaglavlje ────────────────────────────────────────────
            sb.AppendLine("╔══════════════════════════════════════════════════════╗");
            sb.AppendLine(sr
                ? "║     ULTRA CREATIVE SUITE — ACCESSIBILITY REPORT     ║"
                : "║     ULTRA CREATIVE SUITE — ACCESSIBILITY REPORT     ║");
            sb.AppendLine("╚══════════════════════════════════════════════════════╝");
            sb.AppendLine();
            sb.AppendLine(sr ? $"Projekat:     {opts.ProjectName}"
                             : $"Project:      {opts.ProjectName}");
            sb.AppendLine(sr ? $"Generisan:    {DateTime.Now:dd.MM.yyyy HH:mm:ss}"
                             : $"Generated:    {DateTime.Now:dd.MM.yyyy HH:mm:ss}");
            sb.AppendLine(sr ? $"Ukupno trajanje: {FormatTime(result.TotalDuration)}"
                             : $"Total duration:  {FormatTime(result.TotalDuration)}");
            sb.AppendLine(sr ? $"Broj segmenata:  {result.Segments.Count}"
                             : $"Segments:        {result.Segments.Count}");
            if (result.BPM > 0)
                sb.AppendLine(sr ? $"Ritam muzike: {result.BPM:F1} BPM"
                                 : $"Music BPM:    {result.BPM:F1} BPM");
            sb.AppendLine();

            // ── Audio settings ────────────────────────────────────
            if (audio != null)
            {
                sb.AppendLine(sr ? "🔊 AUDIO MIX" : "🔊 AUDIO MIX");
                sb.AppendLine(sr ? $"   Music volume:         {audio.MusicVolume:F0}%"
                                 : $"   Music volume:         {audio.MusicVolume:F0}%");
                sb.AppendLine(sr ? $"   Original volume:      {audio.ClipVolume:F0}%"
                                 : $"   Original clip volume: {audio.ClipVolume:F0}%");
                sb.AppendLine(sr ? $"   Ducking:              {(audio.EnableDucking ? "Enabled" : "Disabled")}"
                                 : $"   Ducking:              {(audio.EnableDucking ? "Enabled" : "Disabled")}");
                sb.AppendLine(sr ? $"   Normalization (LUFS): {(audio.NormalizeLoudness ? "-14 LUFS (YouTube)" : "Disabled")}"
                                 : $"   Loudness norm (LUFS): {(audio.NormalizeLoudness ? "-14 LUFS (YouTube)" : "Disabled")}");
                sb.AppendLine();
            }

            // ── Segmenti ─────────────────────────────────────────────
            sb.AppendLine(sr ? "🎬 SEGMENTI — AUDIO DESCRIPTION" : "🎬 SEGMENTS — AUDIO DESCRIPTION");
            sb.AppendLine();

            double timelineCursor = 0.0;
            foreach (var seg in result.Segments.OrderBy(s => s.Order))
            {
                sb.AppendLine($"  [{seg.Order:D2}]  " +
                              $"{FormatTime(timelineCursor)} — {FormatTime(timelineCursor + seg.Duration)}  " +
                              $"({seg.Duration:F2}s)");

                // Audio description tekst
                sb.AppendLine($"       {BuildSegmentDescription(seg, sr)}");

                // Technical info
                sb.AppendLine(sr
                    ? $"       Skor: {seg.ImportanceScore:F0}/100  " +
                      $"Izvor: {Path.GetFileName(seg.SourcePath)} " +
                      $"[{FormatTime(seg.SourceStart)}–{FormatTime(seg.SourceEnd)}]"
                    : $"       Score: {seg.ImportanceScore:F0}/100  " +
                      $"Source: {Path.GetFileName(seg.SourcePath)} " +
                      $"[{FormatTime(seg.SourceStart)}–{FormatTime(seg.SourceEnd)}]");

                if (!string.IsNullOrEmpty(seg.ArcDescription) &&
                    seg.ArcDescription != "no distinct arc")
                {
                    sb.AppendLine(sr
                        ? $"       Razvoj kadra: {seg.ArcDescription}"
                        : $"       Frame arc:   {seg.ArcDescription}");
                }

                // Prelaz posle ovog segmenta
                if (opts.IncludeTransitions && transitions != null)
                {
                    var dec = transitions.FirstOrDefault(t => t.AfterSegmentIndex == seg.Order - 1);
                    if (dec != null)
                    {
                        sb.AppendLine(sr
                            ? $"       ↓ Prelaz: {TransitionEngine.XfadeToString(dec.Type)} ({dec.Duration:F2}s)"
                            : $"       ↓ Transition: {TransitionEngine.XfadeToString(dec.Type)} ({dec.Duration:F2}s)");
                    }
                }

                sb.AppendLine();
                timelineCursor += seg.Duration;
            }

            // ── Navigacioni markeri ──────────────────────────────────
            if (opts.IncludeNavMarkers)
            {
                sb.AppendLine(sr ? "🗺️  NAVIGACIONI MARKERI" : "🗺️  NAVIGATION MARKERS");
                sb.AppendLine(sr ? "(For screen reader: use Ctrl+F to search for timestamp)"
                                 : "(For screen reader: use Ctrl+F to search for timestamp)");
                sb.AppendLine();

                double navCursor = 0.0;
                foreach (var seg in result.Segments.OrderBy(s => s.Order))
                {
                    string label = !string.IsNullOrEmpty(seg.ContentDescription)
                        ? seg.ContentDescription
                        : (sr ? "Highlight segment" : "Highlight segment");

                    sb.AppendLine($"  {FormatTime(navCursor)}  →  [{seg.Order:D2}] {label}");
                    navCursor += seg.Duration;
                }
                sb.AppendLine();
            }

            // ── Statistike ───────────────────────────────────────────
            sb.AppendLine(sr ? "📊 STATISTIKE" : "📊 STATISTICS");
            double avgScore = result.Segments.Average(s => s.ImportanceScore);
            int    withArc  = result.Segments.Count(s => s.ArcBonus > 5.0);
            int    dynamic  = result.Segments.Count(s => s.Motion != null && s.Motion.HasStrongMotion);

            sb.AppendLine(sr
                ? $"   Average score:       {avgScore:F1}/100\n" +
                  $"   With dynamic arc:    {withArc}/{result.Segments.Count}\n" +
                  $"   Dynamic frames:      {dynamic}/{result.Segments.Count}\n" +
                  $"   Pokrivenost targeta:  {result.TotalDuration / result.TargetDuration * 100:F1}%"
                : $"   Average score:        {avgScore:F1}/100\n" +
                  $"   With dynamic arc:     {withArc}/{result.Segments.Count}\n" +
                  $"   Dynamic shots:        {dynamic}/{result.Segments.Count}\n" +
                  $"   Target coverage:      {result.TotalDuration / result.TargetDuration * 100:F1}%");

            sb.AppendLine();
            sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            sb.AppendLine("Ultra Creative Suite (Iskra) — AI Highlight Engine Faza 3");

            return sb.ToString();
        }

        // ── TTS Summary ──────────────────────────────────────────────

        private static string BuildTtsSummary(
            HighlightResult result, AccessibilityReportOptions opts)
        {
            bool sr = opts.Language == "sr";
            var sb = new StringBuilder();

            sb.AppendLine(sr
                ? $"Highlight video. {opts.ProjectName}. " +
                  $"Trajanje {FormatTimeSpeech(result.TotalDuration)}. " +
                  $"{result.Segments.Count} segmenata."
                : $"Highlight video. {opts.ProjectName}. " +
                  $"Duration {FormatTimeSpeech(result.TotalDuration)}. " +
                  $"{result.Segments.Count} segments.");

            if (result.BPM > 0)
                sb.AppendLine(sr
                    ? $"Ritam muzike {result.BPM:F0} otkucaja u minuti."
                    : $"Music tempo {result.BPM:F0} beats per minute.");

            sb.AppendLine();

            double cursor = 0.0;
            foreach (var seg in result.Segments.OrderBy(s => s.Order))
            {
                sb.AppendLine(sr
                    ? $"Segment {seg.Order}. {FormatTimeSpeech(cursor)}. " +
                      $"{BuildSegmentDescription(seg, sr)} " +
                      $"Trajanje {seg.Duration:F0} sekundi."
                    : $"Segment {seg.Order}. {FormatTimeSpeech(cursor)}. " +
                      $"{BuildSegmentDescription(seg, sr)} " +
                      $"Duration {seg.Duration:F0} seconds.");
                cursor += seg.Duration;
            }

            return sb.ToString();
        }

        // ── Segment Description Builder ──────────────────────────────

        private static string BuildSegmentDescription(HighlightSegment seg, bool sr)
        {
            var parts = new List<string>();

            // Content
            if (!string.IsNullOrEmpty(seg.ContentDescription))
                parts.Add(sr
                    ? $"Displayed content: {seg.ContentDescription}."
                    : $"Content: {seg.ContentDescription}.");

            // Kretanje kamere
            if (seg.Motion != null)
            {
                if (seg.Motion.IsStatic)
                    parts.Add(sr ? "Static shot." : "Static shot.");
                else if (seg.Motion.HasStrongMotion)
                    parts.Add(sr
                        ? $"Dynamic camera movement {seg.Motion.Direction}."
                        : $"Dynamic camera movement {seg.Motion.Direction}.");
                else
                    parts.Add(sr ? "Blago kretanje kamere." : "Gentle camera movement.");
            }

            // Lica
            bool hasFace = (seg.ContentDescription ?? "").Contains("face",
                StringComparison.OrdinalIgnoreCase) ||
                (seg.ContentDescription ?? "").Contains("person",
                StringComparison.OrdinalIgnoreCase) ||
                (seg.ContentDescription ?? "").Contains("lice",
                StringComparison.OrdinalIgnoreCase);

            if (hasFace)
                parts.Add(sr ? "Prisutne osobe." : "People present.");

            // Ocena kvaliteta
            string quality = seg.ImportanceScore >= 70
                ? (sr ? "Visok vizuelni kvalitet." : "High visual quality.")
                : seg.ImportanceScore >= 40
                    ? (sr ? "Dobar vizuelni kvalitet." : "Good visual quality.")
                    : (sr ? "Prihvatljiv vizuelni kvalitet." : "Acceptable visual quality.");
            parts.Add(quality);

            return string.Join(" ", parts);
        }

        // ── Helpers ──────────────────────────────────────────────────

        private static string FormatTime(double seconds)
        {
            if (seconds < 0) seconds = 0;
            int m = (int)(seconds / 60);
            double s = seconds - m * 60;
            return $"{m:D2}:{s:05.2f}";
        }

        private static string FormatTimeSpeech(double seconds)
        {
            int m = (int)(seconds / 60);
            int s = (int)(seconds % 60);
            if (m == 0) return $"{s} sekundi";
            if (s == 0) return $"{m} {(m == 1 ? "minuta" : "minute")}";
            return $"{m} {(m == 1 ? "minuta" : "minute")} i {s} sekundi";
        }

        private static AccessibilityReport Fail(string error)
            => new AccessibilityReport { Success = false, Error = error };
    }
}
