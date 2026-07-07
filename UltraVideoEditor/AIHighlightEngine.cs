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
    // DATA MODELS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// A single highlight segment extracted from the original video.
    /// </summary>
    public class HighlightSegment
    {
        public string SourcePath         { get; set; }
        public double SourceStart        { get; set; }
        public double SourceEnd          { get; set; }
        public double Duration           => SourceEnd - SourceStart;
        public int    Order              { get; set; }
        public double BeatTimestamp      { get; set; }

        /// <summary>Importance score 0–100 (higher = better).</summary>
        public double ImportanceScore    { get; set; }

        /// <summary>Short content description from VisionAnalyzer.</summary>
        public string ContentDescription { get; set; } = "";

        /// <summary>
        /// [Faza 2 / C] Detaljan opis arc-a kadra:
        /// e.g. "Static opening → fast movement to the left → ends in frozen composition"
        /// </summary>
        public string ArcDescription     { get; set; } = "";

        public MotionResult Motion        { get; set; }
        public bool         QualityOk    { get; set; } = true;

        /// <summary>[Faza 2 / B] Putanja do thumbnail slike za preview (temp file).</summary>
        public string ThumbnailPath      { get; set; }

        /// <summary>[Phase 2 / C] Scores per individual frame (for debug/preview).</summary>
        public List<double> FrameScores  { get; set; } = new();

        /// <summary>[Faza 2 / C] Bonus iz arc analize (0–25).</summary>
        public double ArcBonus           { get; set; }
    }

    /// <summary>Kompletan rezultat highlight analize.</summary>
    public class HighlightResult
    {
        public List<HighlightSegment> Segments      { get; set; } = new();
        public double TotalDuration => Segments.Sum(s => s.Duration);
        public double TargetDuration { get; set; }
        public double BPM            { get; set; }
        public BeatInfo Beats        { get; set; }
        public string Report         { get; set; } = "";
        public string Error          { get; set; }
        public bool   Success        => string.IsNullOrEmpty(Error);
    }

    // ═══════════════════════════════════════════════════════════════
    // AI HIGHLIGHT ENGINE  v2
    // Faza 2 / C: Multi-frame scoring + arc detekcija
    // ═══════════════════════════════════════════════════════════════
    public static class AIHighlightEngine
    {
        // ── Tuning ──────────────────────────────────────────────────
        private const double MinSegmentSec       = 2.0;
        private const double MaxSegmentSec       = 8.0;
        private const int    CandidatesPerMinute = 6;   // PERF: smanjeno sa 12 → 6 (dovoljno za selekciju)
        private const double BeatSnapWindow      = 0.25;

        /// <summary>Broj frejmova koji se analiziraju po segmentu za arc scoring.</summary>
        private const int ARC_FRAMES = 3;               // PERF: smanjeno sa 5 → 3 (10%/50%/90%)

        // ── Javni API ────────────────────────────────────────────────

        /// <summary>Video-only analiza — bez muzike. Target = 30% trajanja videa.</summary>
        public static Task<HighlightResult> AnalyzeAsync(
            string videoPath,
            IProgress<(int Percent, string Message)> progress = null,
            CancellationToken ct = default)
            => AnalyzeAsync(videoPath, null, progress, ct);

        public static async Task<HighlightResult> AnalyzeAsync(
            string videoPath,
            string musicPath,
            IProgress<(int Percent, string Message)> progress = null,
            CancellationToken ct = default)
        {
            bool videoOnly = string.IsNullOrEmpty(musicPath);
            string ffmpegPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");

            if (!File.Exists(videoPath))  return Fail($"Video file not found: {videoPath}");
            if (!videoOnly && !File.Exists(musicPath)) return Fail($"Audio file not found: {musicPath}");
            if (!File.Exists(ffmpegPath)) return Fail("FFmpeg not found. Check installation.");

            var result = new HighlightResult();
            try
            {
                // 1 — trajanja
                Report(progress, 5, videoOnly ? "Reading video duration…" : "Reading video and music duration…");
                double videoDuration = await GetDurationAsync(videoPath, ffmpegPath, ct);
                if (videoDuration < 1.0) return Fail("Video is too short for analysis.");

                double musicDuration;
                BeatInfo beats;
                if (videoOnly)
                {
                    musicDuration = videoDuration * 0.30;
                    beats = new BeatInfo(); // BPM=0 => IsValid=false (computed property)
                    Report(progress, 12, $"Video-only mode — target: {FormatTime(musicDuration)} (30% of video)");
                }
                else
                {
                    musicDuration = await GetDurationAsync(musicPath, ffmpegPath, ct);
                    if (musicDuration < 1.0) return Fail("Audio file is too short.");
                    // beat detection
                    Report(progress, 12, $"Analyzing song rhythm ({Path.GetFileName(musicPath)})…");
                    beats = await BeatDetection.AnalyzeAudio(musicPath, ffmpegPath, ct);
                }
                result.TargetDuration = musicDuration;
                result.Beats = beats;
                result.BPM   = beats.BPM;

                // 3 — sampling
                Report(progress, 20, "Sampling candidates from video…");
                var candidates = SampleCandidates(videoPath, videoDuration, musicDuration);

                // 4 — multi-frame scoring (Phase 2 / C)
                Report(progress, 30, $"Analyzing {candidates.Count} candidates — multi-frame arc scoring…");
                await ScoreSegmentsMultiFrameAsync(candidates, ffmpegPath, progress, ct);

                // 5 — selection
                Report(progress, 72, "Choosing the most interesting moments…");
                var selected = SelectSegments(candidates, musicDuration);
                if (selected.Count == 0) return Fail("No usable segment found.");

                // 6 — thumbnail generation (Phase 2 / B)
                Report(progress, 80, "Generating thumbnails for preview…");
                await GenerateThumbnailsAsync(selected, ffmpegPath, ct);

                // 7 — beat alignment (only if there's music)
                if (!videoOnly)
                {
                    Report(progress, 88, "Syncing cuts to the beat…");
                    AlignToBeats(selected, beats);
                }

                for (int i = 0; i < selected.Count; i++)
                    selected[i].Order = i + 1;

                result.Segments = selected;

                // 8 — report
                Report(progress, 95, "Generating report…");
                result.Report = BuildReport(result, videoPath, videoOnly ? null : musicPath);

                Report(progress, 100, "Done!");
                return result;
            }
            catch (OperationCanceledException) { return Fail("Analysis was cancelled."); }
            catch (Exception ex)               { return Fail($"Error: {ex.Message}"); }
        }

        // ── Sampling ─────────────────────────────────────────────

        private static List<HighlightSegment> SampleCandidates(
            string videoPath, double videoDuration, double targetDuration)
        {
            int totalNeeded = (int)Math.Ceiling(targetDuration / MinSegmentSec) * 2; // PERF: *2 umjesto *3
            int byMinute    = (int)(videoDuration / 60.0 * CandidatesPerMinute);
            int count       = Math.Clamp(Math.Max(totalNeeded, byMinute), 10, 60); // PERF: max 60 kandidata
            double step     = videoDuration / count;
            double segLen   = Math.Clamp(step * 0.7, MinSegmentSec, MaxSegmentSec);

            var list = new List<HighlightSegment>(count);
            for (int i = 0; i < count; i++)
            {
                double start = i * step;
                double end   = Math.Min(start + segLen, videoDuration - 0.1);
                if (end - start < MinSegmentSec) continue;
                list.Add(new HighlightSegment
                {
                    SourcePath  = videoPath,
                    SourceStart = Math.Round(start, 3),
                    SourceEnd   = Math.Round(end,   3),
                });
            }
            return list;
        }

        // ── Faza 2 / C: Multi-frame arc scoring ─────────────────────

        /// <summary>
        /// For each candidate we take ARC_FRAMES frames distributed across the segment.
        /// We compute per-frame score, detect arc (static→dynamic, dark→bright, etc.)
        /// and add ArcBonus if the segment has an interesting "story".
        /// </summary>
        private static async Task ScoreSegmentsMultiFrameAsync(
            List<HighlightSegment> candidates,
            string ffmpegPath,
            IProgress<(int, string)> progress,
            CancellationToken ct)
        {
            int done      = 0;
            int total     = candidates.Count;
            var semaphore = new SemaphoreSlim(3); // konzervativno za GPU

            var tasks = candidates.Select(async seg =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    // PERF: timeout per segment — max 30s, otherwise skip
                    using var segCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    segCts.CancelAfter(TimeSpan.FromSeconds(30));
                    try
                    {
                        await ScoreOneSegmentAsync(seg, ffmpegPath, segCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Timeout za ovaj segment — nastavi sa ostalima
                        seg.ImportanceScore = 5.0;
                        seg.QualityOk = true;
                    }

                    int pct = 30 + (int)(Interlocked.Increment(ref done) * 40.0 / total);
                    progress?.Report((pct, $"Scoring {done}/{total}…"));
                }
                catch (OperationCanceledException) { throw; }
                catch { seg.ImportanceScore = 0; seg.QualityOk = false; }
                finally { semaphore.Release(); }
            });

            await Task.WhenAll(tasks);
        }

        private static async Task ScoreOneSegmentAsync(
            HighlightSegment seg, string ffmpegPath, CancellationToken ct)
        {
            double dur = seg.Duration;
            // Pozicije frejmova: 10%, 30%, 50%, 70%, 90% kroz segment
            double[] offsets = Enumerable.Range(0, ARC_FRAMES)
                .Select(i => seg.SourceStart + dur * (i * 0.2 + 0.1))
                .ToArray();

            var frameResults = new VisionResult[ARC_FRAMES];
            var motionResults = new MotionResult[ARC_FRAMES];

            // Ekstrahujemo frejmove i analiziramo ih sekvencijalno (isti temp fajl)
            for (int fi = 0; fi < ARC_FRAMES; fi++)
            {
                ct.ThrowIfCancellationRequested();
                string tempFrame = Path.Combine(Path.GetTempPath(),
                    $"arc_{Guid.NewGuid():N}.jpg");
                try
                {
                    bool ok = await ExtractFrameAtAsync(
                        seg.SourcePath, offsets[fi], tempFrame, ffmpegPath, ct);

                    if (ok)
                    {
                        // We pass the already-extracted frame (not the entire video!)
                        // AnalyzeClipAsync with .jpg → skips extraction, goes directly to analysis
                        frameResults[fi] = await VisionAnalyzer.AnalyzeClipAsync(
                            tempFrame, ffmpegPath, ct);
                    }
                }
                finally
                {
                    try { if (File.Exists(tempFrame)) File.Delete(tempFrame); } catch { }
                }
            }

            // Motion analysis for the exact segment position (seekTo = SourceStart)
            seg.Motion = await MotionAnalyzer.AnalyzeEndAsync(
                seg.SourcePath, ffmpegPath,
                clipDuration: seg.SourceStart + 2.0,    // seekTo = SourceStart
                analyzeLastSeconds: 2.0,
                ct: ct);

            // Per-frame skrovi
            seg.FrameScores = frameResults
                .Select(v => ComputeFrameScore(v, seg.Motion))
                .ToList();

            // Average score
            double avgScore = seg.FrameScores.Any(s => s > 0)
                ? seg.FrameScores.Where(s => s > 0).Average()
                : 0.0;

            // Arc bonus
            (seg.ArcBonus, seg.ArcDescription) = ComputeArcBonus(frameResults, seg.Motion);

            // Finalni skor
            seg.ImportanceScore = Math.Clamp(avgScore + seg.ArcBonus, 0.0, 100.0);
            seg.QualityOk       = seg.ImportanceScore > 5.0
                && (frameResults.Any(v => v != null && string.IsNullOrEmpty(v.RejectReason)));
            seg.ContentDescription = frameResults
                .Where(v => v != null && !string.IsNullOrEmpty(v.TopLabel))
                .Select(v => v.TopLabel)
                .FirstOrDefault() ?? "";
        }

        /// <summary>
        /// Skor jednog frejma 0–75 (bez arc bonusa).
        /// </summary>
        private static double ComputeFrameScore(VisionResult v, MotionResult m)
        {
            if (v == null) return 10.0;
            double score = 0.0;
            score += v.Score * 30.0;
            if (v.HasFaces)            score += 15.0;
            if (v.HasSmile)            score += 8.0;
            if (m != null && m.HasStrongMotion) score += 10.0;
            if (v.IsOutdoor)           score += 5.0;
            if (v.IsWarm)              score += 3.0;
            if (v.Luminance < 0.1 || v.Luminance > 0.95) score -= 15.0;
            if (v.Sharpness < 0.3)     score -= 20.0;
            return Math.Clamp(score, 0.0, 75.0);
        }

        /// <summary>
        /// Detection of segment "arc" — rewards frames that have development:
        /// static→dynamic, dark→bright, no face→face, etc.
        /// Returns bonus (0–25) and a textual arc description.
        /// </summary>
        private static (double bonus, string description) ComputeArcBonus(
            VisionResult[] frames, MotionResult motion)
        {
            var valid = frames.Where(v => v != null).ToArray();
            if (valid.Length < 2) return (0.0, "");

            var arc = new List<string>();
            double bonus = 0.0;

            // Arc 1: Tamno → svetlo (ili obrnuto)
            double firstLum = valid.First().Luminance;
            double lastLum  = valid.Last().Luminance;
            if (Math.Abs(lastLum - firstLum) > 0.25)
            {
                bonus += 8.0;
                arc.Add(lastLum > firstLum ? "tamno → svetlo" : "svetlo → tamno");
            }

            // Arc 2: Static → dynamic (based on sharpness variation)
            double sharpFirst = valid.First().Sharpness;
            double sharpLast  = valid.Last().Sharpness;
            if (motion != null && motion.HasStrongMotion && sharpFirst > 0.5 && sharpLast < 0.4)
            {
                bonus += 7.0;
                arc.Add("sharp → blurred motion");
            }
            else if (motion != null && !motion.IsStatic && sharpFirst < 0.4 && sharpLast > 0.5)
            {
                bonus += 7.0;
                arc.Add("motion → sharp ending");
            }

            // Arc 3: Pojava lica (postaje emotivniji kadar)
            bool faceAtStart = valid.First().HasFaces;
            bool faceAtEnd   = valid.Last().HasFaces;
            if (!faceAtStart && faceAtEnd)
            {
                bonus += 10.0;
                arc.Add("otkrivanje lica");
            }

            // Arc 4: Saturation increases (frame becomes more vivid)
            double satFirst = valid.First().Saturation;
            double satLast  = valid.Last().Saturation;
            if (satLast - satFirst > 0.2)
            {
                bonus += 5.0;
                arc.Add("colors intensify");
            }

            // Arc 5: Konstantno visoki skrovi = pouzdan kadar
            double minScore = valid.Min(v => v.Score);
            if (minScore > 6.0)
            {
                bonus += 5.0;
                arc.Add("consistently high quality");
            }

            string desc = arc.Count > 0
                ? string.Join(" → ", arc)
                : "no distinct arc";

            return (Math.Min(bonus, 25.0), desc);
        }

        // ── Thumbnail generisanje (Faza 2 / B) ──────────────────────

        /// <summary>
        /// Generates a 160×90 thumbnail from the middle of each selected segment.
        /// Thumbnails are saved to %TEMP% and displayed in the preview panel.
        /// </summary>
        private static async Task GenerateThumbnailsAsync(
            List<HighlightSegment> segments, string ffmpegPath, CancellationToken ct)
        {
            var tasks = segments.Select(async seg =>
            {
                string thumbPath = Path.Combine(Path.GetTempPath(),
                    $"thumb_{Guid.NewGuid():N}.jpg");
                double seekTo = seg.SourceStart + seg.Duration * 0.5;

                bool ok = await ExtractFrameAtAsync(
                    seg.SourcePath, seekTo, thumbPath, ffmpegPath, ct,
                    width: 160, height: 90);

                if (ok) seg.ThumbnailPath = thumbPath;
            });
            await Task.WhenAll(tasks);
        }

        // ── Selekcija ─────────────────────────────────────────────────

        private static List<HighlightSegment> SelectSegments(
            List<HighlightSegment> candidates, double targetDuration)
        {
            var usable = candidates
                .Where(s => s.QualityOk && s.ImportanceScore > 5.0)
                .OrderByDescending(s => s.ImportanceScore)
                .ToList();

            var selected  = new List<HighlightSegment>();
            double filled = 0.0;
            double slack  = targetDuration * 0.05;

            foreach (var seg in usable)
            {
                if (filled >= targetDuration - slack) break;
                bool overlaps = selected.Any(s =>
                    s.SourceStart < seg.SourceEnd && s.SourceEnd > seg.SourceStart);
                if (overlaps) continue;

                double remaining = targetDuration - filled;
                if (seg.Duration > remaining + slack)
                {
                    if (remaining >= MinSegmentSec)
                    {
                        var trimmed = new HighlightSegment
                        {
                            SourcePath         = seg.SourcePath,
                            SourceStart        = seg.SourceStart,
                            SourceEnd          = seg.SourceStart + remaining,
                            ImportanceScore    = seg.ImportanceScore,
                            ContentDescription = seg.ContentDescription,
                            ArcDescription     = seg.ArcDescription,
                            ArcBonus           = seg.ArcBonus,
                            FrameScores        = seg.FrameScores,
                            Motion             = seg.Motion,
                            QualityOk          = seg.QualityOk,
                        };
                        selected.Add(trimmed);
                        filled += remaining;
                    }
                    continue;
                }

                selected.Add(seg);
                filled += seg.Duration;
            }

            // Dopunjavanje rezervama ako nismo popunili 80% targeta
            if (filled < targetDuration * 0.8)
            {
                foreach (var seg in candidates.Except(selected)
                    .OrderByDescending(s => s.ImportanceScore))
                {
                    if (filled >= targetDuration - slack) break;
                    bool overlaps = selected.Any(s =>
                        s.SourceStart < seg.SourceEnd && s.SourceEnd > seg.SourceStart);
                    if (overlaps) continue;
                    selected.Add(seg);
                    filled += seg.Duration;
                }
            }

            return selected.OrderBy(s => s.SourceStart).ToList();
        }

        // ── Beat alijacija ───────────────────────────────────────────

        private static void AlignToBeats(List<HighlightSegment> segments, BeatInfo beats)
        {
            if (!beats.IsValid || segments.Count == 0) return;

            var cutPoints = beats.PianoMode && beats.PhraseBeats?.Count >= 2
                ? beats.PhraseBeats
                : beats.BeatTimes;
            if (cutPoints == null || cutPoints.Count == 0) return;

            double advanceSec = beats.CutAdvanceMs / 1000.0;
            double timeline   = beats.AudioStartSeconds;

            for (int i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                double beatTs = FindNearestBeat(cutPoints, timeline);
                seg.BeatTimestamp = beatTs;

                double nextTimeline = timeline + seg.Duration;
                double nextBeat = i < segments.Count - 1
                    ? FindNearestBeat(cutPoints, nextTimeline)
                    : nextTimeline;

                double beatDur = nextBeat - beatTs;
                if (beatDur >= MinSegmentSec && beatDur <= MaxSegmentSec + BeatSnapWindow)
                {
                    seg.SourceEnd = Math.Max(
                        seg.SourceStart + MinSegmentSec,
                        seg.SourceStart + beatDur - advanceSec);
                }
                timeline = nextBeat;
            }
        }

        private static double FindNearestBeat(List<double> beats, double targetTime)
        {
            if (beats == null || beats.Count == 0) return targetTime;
            double best = beats[0], minDst = Math.Abs(beats[0] - targetTime);
            foreach (var b in beats)
            {
                double d = Math.Abs(b - targetTime);
                if (d < minDst) { minDst = d; best = b; }
                if (b > targetTime + BeatSnapWindow) break;
            }
            return minDst <= BeatSnapWindow ? best : targetTime;
        }

        // ── Report ────────────────────────────────────────────────

        private static string BuildReport(
            HighlightResult result, string videoPath, string musicPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("╔══════════════════════════════════════════════════════╗");
            sb.AppendLine("║      AI HIGHLIGHT ENGINE v2 — REPORT                  ║");
            sb.AppendLine("╚══════════════════════════════════════════════════════╝");
            sb.AppendLine();
            sb.AppendLine("📹 ULAZNI PODACI");
            sb.AppendLine($"   Video:  {Path.GetFileName(videoPath)}");
            sb.AppendLine($"   Muzika: {(string.IsNullOrEmpty(musicPath) ? "— (video-only mod)" : Path.GetFileName(musicPath))}");
            sb.AppendLine($"   Target: {FormatTime(result.TargetDuration)}");
            sb.AppendLine();

            sb.AppendLine("🎵 ANALIZA RITMA");
            if (result.Beats?.IsValid == true)
            {
                sb.AppendLine($"   BPM:          {result.BPM:F1}");
                sb.AppendLine($"   Takt:         {result.Beats.TimeSignature}");
                sb.AppendLine($"   Beat interval:{result.Beats.BeatInterval:F3}s");
                sb.AppendLine($"   Mod:          {(result.Beats.PianoMode ? "Piano/melodijski" : "Perkusivni")}");
                sb.AppendLine($"   Cut advance:  {result.Beats.CutAdvanceMs:F0}ms");
            }
            else sb.AppendLine("   Beat detection was not available.");
            sb.AppendLine();

            sb.AppendLine("✂️  SELECTED HIGHLIGHTS");
            sb.AppendLine($"   Segmenata:  {result.Segments.Count}");
            sb.AppendLine($"   Duration:   {FormatTime(result.TotalDuration)} / {FormatTime(result.TargetDuration)}");
            sb.AppendLine();

            foreach (var seg in result.Segments)
            {
                string motionDesc = seg.Motion == null ? "unknown"
                    : seg.Motion.IsStatic ? "static"
                    : $"{seg.Motion.Direction} (mag {seg.Motion.Magnitude:F0})";

                sb.AppendLine($"  [{seg.Order:D2}]  {FormatTime(seg.SourceStart)} → {FormatTime(seg.SourceEnd)}  ({seg.Duration:F2}s)");
                sb.AppendLine($"        Skor:    {seg.ImportanceScore:F0}/100  (arc bonus: +{seg.ArcBonus:F0})");
                sb.AppendLine($"        Content: {seg.ContentDescription}");
                sb.AppendLine($"        Arc:     {seg.ArcDescription}");
                sb.AppendLine($"        Kamera:  {motionDesc}");
                if (seg.FrameScores.Count > 0)
                {
                    string fsStr = string.Join(" | ", seg.FrameScores.Select(s => $"{s:F0}"));
                    sb.AppendLine($"        Frejmovi:{fsStr}");
                }
                sb.AppendLine($"        Beat rez:{FormatTime(seg.BeatTimestamp)}");
            }
            sb.AppendLine();

            sb.AppendLine("📊 STATISTIKE");
            if (result.Segments.Count > 0)
            {
                double avgScore  = result.Segments.Average(s => s.ImportanceScore);
                double avgArc    = result.Segments.Average(s => s.ArcBonus);
                int    withArc   = result.Segments.Count(s => s.ArcBonus > 5.0);
                int    withFaces = result.Segments.Count(s =>
                    s.ContentDescription.Contains("face", StringComparison.OrdinalIgnoreCase) ||
                    s.ContentDescription.Contains("person", StringComparison.OrdinalIgnoreCase));
                double coverage  = result.TotalDuration / result.TargetDuration * 100.0;

                sb.AppendLine($"   Average score:       {avgScore:F1}/100");
                sb.AppendLine($"   Average arc bonus:   +{avgArc:F1}");
                sb.AppendLine($"   Segmenata sa arc-om: {withArc}/{result.Segments.Count}");
                sb.AppendLine($"   Sa licima:           {withFaces}/{result.Segments.Count}");
                sb.AppendLine($"   Pokrivenost targeta: {coverage:F1}%");
            }
            sb.AppendLine();
            sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            sb.AppendLine($"Generisan: {DateTime.Now:dd.MM.yyyy HH:mm:ss}");
            sb.AppendLine("Ultra Creative Suite — AI Highlight Engine v2 (Faza 2)");
            return sb.ToString();
        }

        // ── FFmpeg helpers ───────────────────────────────────────────

        /// <summary>Ekstrahuje jedan frejm iz videa na zadatoj poziciji (sekunde).</summary>
        public static async Task<bool> ExtractFrameAtAsync(
            string videoPath, double seekSeconds, string outputPath,
            string ffmpegPath, CancellationToken ct,
            int width = 224, int height = 224)
        {
            try
            {
                if (File.Exists(outputPath)) File.Delete(outputPath);
                string ssStr = seekSeconds.ToString("F3",
                    System.Globalization.CultureInfo.InvariantCulture);
                string args = $"-nostdin -y -ss {ssStr} -i \"{videoPath}\" " +
                              $"-vframes 1 -vf scale={width}:{height} -q:v 2 \"{outputPath}\"";

                var psi = new ProcessStartInfo
                {
                    FileName              = ffmpegPath,
                    Arguments             = args,
                    CreateNoWindow        = true,
                    UseShellExecute       = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput= true,
                };
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                var seTask = proc.StandardError.ReadToEndAsync();
                var soTask = proc.StandardOutput.ReadToEndAsync();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(15));
                try   { await proc.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    ct.ThrowIfCancellationRequested();
                    return false;
                }
                await Task.WhenAll(seTask, soTask);
                return File.Exists(outputPath);
            }
            catch { return false; }
        }

        public static async Task<double> GetDurationAsync(
            string mediaPath, string ffmpegPath, CancellationToken ct)
        {
            string ffprobePath = ffmpegPath.Replace("ffmpeg.exe", "ffprobe.exe");
            if (File.Exists(ffprobePath))
            {
                var psi = new ProcessStartInfo
                {
                    FileName              = ffprobePath,
                    Arguments             = $"-v error -show_entries format=duration " +
                                            $"-of default=noprint_wrappers=1:nokey=1 \"{mediaPath}\"",
                    RedirectStandardOutput= true,
                    RedirectStandardError = true,
                    UseShellExecute       = false,
                    CreateNoWindow        = true,
                };
                using var proc = Process.Start(psi);
                string output  = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync(ct);
                if (double.TryParse(output.Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double dur))
                    return dur;
            }

            // Fallback: ffmpeg -i
            var psi2 = new ProcessStartInfo
            {
                FileName             = ffmpegPath,
                Arguments            = $"-i \"{mediaPath}\"",
                RedirectStandardError= true,
                UseShellExecute      = false,
                CreateNoWindow       = true,
            };
            using var proc2 = Process.Start(psi2);
            string err = await proc2.StandardError.ReadToEndAsync();
            await proc2.WaitForExitAsync(ct);
            var m = System.Text.RegularExpressions.Regex.Match(
                err, @"Duration:\s*(\d+):(\d+):(\d+\.\d+)");
            if (!m.Success) return 0.0;
            return double.Parse(m.Groups[1].Value) * 3600
                 + double.Parse(m.Groups[2].Value) * 60
                 + double.Parse(m.Groups[3].Value,
                     System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void Report(IProgress<(int, string)> p, int pct, string msg)
            => p?.Report((pct, msg));

        private static HighlightResult Fail(string error)
            => new HighlightResult { Error = error };

        public static string FormatTime(double seconds)
        {
            if (seconds < 0) seconds = 0;
            int m = (int)(seconds / 60);
            double s = seconds - m * 60;
            return $"{m:D2}:{s:05.2f}";
        }
    }
}
