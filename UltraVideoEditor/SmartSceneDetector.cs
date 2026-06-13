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
    // SMART SCENE DETECTOR  — Faza 4A
    // Detektuje scene u videu na osnovu vizuelnih promena između frejmova.
    // Funkcioniše nezavisno — ne treba prethodni HighlightResult.
    // ═══════════════════════════════════════════════════════════════

    public class SceneSegment
    {
        public string SourcePath        { get; set; }
        public double Start             { get; set; }
        public double End               { get; set; }
        public double Duration          => End - Start;
        public int    Index             { get; set; }
        public string Label             { get; set; } = "";
        public string ThumbnailPath     { get; set; }
        public double ChangeScore       { get; set; }   // 0-100: koliko se razlikuje od prethodne scene
        public VisionResult Vision      { get; set; }
        public MotionResult Motion      { get; set; }
    }

    public class SceneDetectionResult
    {
        public List<SceneSegment> Scenes   { get; set; } = new();
        public double TotalDuration        { get; set; }
        public int    SceneCount           => Scenes.Count;
        public string Report               { get; set; } = "";
        public string Error                { get; set; }
        public bool   Success              => string.IsNullOrEmpty(Error);
    }

    public static class SmartSceneDetector
    {
        // ── Tuning ──────────────────────────────────────────────────
        private const double MinSceneSec       = 1.5;
        private const double SampleIntervalSec = 0.5;   // uzorkujemo frejm svakih 0.5s
        private const double ChangeThreshold   = 0.28;  // prag za novu scenu (0-1)

        // ── Javni API ────────────────────────────────────────────────

        public static async Task<SceneDetectionResult> DetectAsync(
            string videoPath,
            IProgress<(int Percent, string Message)> progress = null,
            CancellationToken ct = default)
        {
            string ffmpegPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");

            if (!File.Exists(videoPath))  return Fail($"Video fajl nije pronađen: {videoPath}");
            if (!File.Exists(ffmpegPath)) return Fail("FFmpeg nije pronađen.");

            var result = new SceneDetectionResult();
            try
            {
                Report(progress, 5, "Čitam trajanje videa…");
                double duration = await AIHighlightEngine.GetDurationAsync(videoPath, ffmpegPath, ct);
                if (duration < 2.0) return Fail("Video je previše kratak za detekciju scena.");
                result.TotalDuration = duration;

                // 1 — FFmpeg scene detection filter (brz, ne treba VisionAnalyzer)
                Report(progress, 10, "Pokrećem FFmpeg detekciju promena…");
                var rawCuts = await DetectCutsViaFFmpegAsync(videoPath, ffmpegPath, duration, ct);

                Report(progress, 35, $"Pronađeno {rawCuts.Count} potencijalnih rezova, filtriram…");
                var cutTimes = FilterCuts(rawCuts, duration);

                // 2 — Kreiraj segmente
                Report(progress, 45, "Kreiram segmente…");
                var segments = BuildSegments(videoPath, cutTimes, duration);

                // 3 — Vision analiza (paralelno, max 3)
                Report(progress, 50, $"Analiziram {segments.Count} scena…");
                await AnalyzeScenesAsync(segments, ffmpegPath, progress, ct);

                // 4 — Thumbnailovi
                Report(progress, 82, "Generišem thumbnailove…");
                await GenerateThumbnailsAsync(segments, ffmpegPath, ct);

                for (int i = 0; i < segments.Count; i++)
                    segments[i].Index = i + 1;

                result.Scenes = segments;
                Report(progress, 95, "Generišem izveštaj…");
                result.Report = BuildReport(result, videoPath);
                Report(progress, 100, "Gotovo!");
                return result;
            }
            catch (OperationCanceledException) { return Fail("Detekcija je otkazana."); }
            catch (Exception ex)               { return Fail($"Greška: {ex.Message}"); }
        }

        // ── FFmpeg scene detekcija ───────────────────────────────────

        /// <summary>
        /// Koristi FFmpeg select filter sa scene score-om da detektuje rezove.
        /// Mnogo brže od frame-by-frame VisionAnalyzer analize.
        /// </summary>
        private static async Task<List<(double Time, double Score)>> DetectCutsViaFFmpegAsync(
            string videoPath, string ffmpegPath, double duration, CancellationToken ct)
        {
            var cuts = new List<(double, double)>();

            // FFmpeg scene detection: ispisuje timestamp i score svakog potencijalnog reza
            string args = $"-nostdin -i \"{videoPath}\" " +
                          $"-vf \"select='gt(scene,{ChangeThreshold.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)})'," +
                          $"showinfo\" -f null -an -";

            var psi = new ProcessStartInfo
            {
                FileName               = ffmpegPath,
                Arguments              = args,
                RedirectStandardError  = true,
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return cuts;

            var output = new StringBuilder();
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
            proc.BeginErrorReadLine();

            using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts2.CancelAfter(TimeSpan.FromSeconds(Math.Min(duration * 2, 120)));
            try   { await proc.WaitForExitAsync(cts2.Token); }
            catch { try { proc.Kill(true); } catch { } ct.ThrowIfCancellationRequested(); return cuts; }

            // Parsiraj "pts_time:" iz showinfo outputa
            var lines = output.ToString().Split('\n');
            foreach (var line in lines)
            {
                if (!line.Contains("pts_time:")) continue;
                var m = System.Text.RegularExpressions.Regex.Match(line, @"pts_time:([\d\.]+)");
                if (!m.Success) continue;
                if (double.TryParse(m.Groups[1].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double t))
                {
                    cuts.Add((t, 0.5)); // score placeholder
                }
            }

            // Ako FFmpeg nije dao ništa (neki formati), fallback: uniformni rezovi svakih ~15s
            if (cuts.Count == 0)
            {
                double step = 15.0;
                for (double t = step; t < duration - MinSceneSec; t += step)
                    cuts.Add((t, 0.3));
            }

            return cuts;
        }

        private static List<double> FilterCuts(List<(double Time, double Score)> raw, double duration)
        {
            var times = raw
                .OrderBy(c => c.Time)
                .Select(c => c.Time)
                .ToList();

            // Ukloni previše bliske rezove (min razmak = MinSceneSec)
            var filtered = new List<double> { 0.0 }; // uvek počinjemo od 0
            foreach (var t in times)
            {
                if (t - filtered.Last() >= MinSceneSec && t < duration - MinSceneSec)
                    filtered.Add(Math.Round(t, 2));
            }
            return filtered;
        }

        private static List<SceneSegment> BuildSegments(
            string videoPath, List<double> cutTimes, double duration)
        {
            var segs = new List<SceneSegment>();
            for (int i = 0; i < cutTimes.Count; i++)
            {
                double start = cutTimes[i];
                double end   = i + 1 < cutTimes.Count ? cutTimes[i + 1] : duration;
                if (end - start < MinSceneSec) continue;
                segs.Add(new SceneSegment
                {
                    SourcePath = videoPath,
                    Start      = start,
                    End        = end,
                });
            }
            return segs;
        }

        // ── Analiza scena ─────────────────────────────────────────────

        private static async Task AnalyzeScenesAsync(
            List<SceneSegment> segments, string ffmpegPath,
            IProgress<(int, string)> progress, CancellationToken ct)
        {
            int done = 0, total = segments.Count;
            var sem  = new SemaphoreSlim(3);

            var tasks = segments.Select(async seg =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    double midpoint = seg.Start + seg.Duration * 0.5;
                    // Ekstrahuj frejm na sredini scene, proslijedi kao sliku (ne cio video)
                    string midFrame = Path.Combine(Path.GetTempPath(), $"sd_{Guid.NewGuid():N}.jpg");
                    bool frameOk = await AIHighlightEngine.ExtractFrameAtAsync(
                        seg.SourcePath, midpoint, midFrame, ffmpegPath, ct, 224, 224);
                    seg.Vision = frameOk
                        ? await VisionAnalyzer.AnalyzeClipAsync(midFrame, ffmpegPath, ct)
                        : await VisionAnalyzer.AnalyzeClipAsync(seg.SourcePath, ffmpegPath, ct);
                    if (frameOk) try { File.Delete(midFrame); } catch { }
                    seg.Motion = await MotionAnalyzer.AnalyzeEndAsync(
                        seg.SourcePath, ffmpegPath,
                        clipDuration: seg.Start + 2.0,
                        analyzeLastSeconds: 2.0, ct: ct);
                    seg.Label  = BuildLabel(seg);

                    int pct = 50 + (int)(Interlocked.Increment(ref done) * 30.0 / total);
                    progress?.Report((pct, $"Analiziram scenu {done}/{total}…"));
                }
                catch { seg.Label = "Scena"; }
                finally { sem.Release(); }
            });
            await Task.WhenAll(tasks);
        }

        private static string BuildLabel(SceneSegment seg)
        {
            if (seg.Vision == null) return "Scena";
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(seg.Vision.TopLabel)) parts.Add(seg.Vision.TopLabel);
            if (seg.Vision.HasFaces)  parts.Add("lice");
            if (seg.Vision.IsOutdoor) parts.Add("eksterijer");
            if (seg.Motion != null && seg.Motion.HasStrongMotion) parts.Add("pokret");
            return parts.Count > 0 ? string.Join(", ", parts) : "Scena";
        }

        // ── Thumbnailovi ──────────────────────────────────────────────

        private static async Task GenerateThumbnailsAsync(
            List<SceneSegment> segments, string ffmpegPath, CancellationToken ct)
        {
            var tasks = segments.Select(async seg =>
            {
                string path = Path.Combine(Path.GetTempPath(), $"scene_{Guid.NewGuid():N}.jpg");
                double seek = seg.Start + seg.Duration * 0.4;
                bool ok = await AIHighlightEngine.ExtractFrameAtAsync(
                    seg.SourcePath, seek, path, ffmpegPath, ct, 160, 90);
                if (ok) seg.ThumbnailPath = path;
            });
            await Task.WhenAll(tasks);
        }

        // ── Izveštaj ─────────────────────────────────────────────────

        private static string BuildReport(SceneDetectionResult result, string videoPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("╔══════════════════════════════════════════════════════╗");
            sb.AppendLine("║      SMART SCENE DETECTOR — IZVEŠTAJ  (Faza 4A)      ║");
            sb.AppendLine("╚══════════════════════════════════════════════════════╝");
            sb.AppendLine();
            sb.AppendLine($"   Video:   {Path.GetFileName(videoPath)}");
            sb.AppendLine($"   Trajanje:{AIHighlightEngine.FormatTime(result.TotalDuration)}");
            sb.AppendLine($"   Scena:   {result.SceneCount}");
            sb.AppendLine();
            sb.AppendLine("🎬 DETEKTOVANE SCENE");
            foreach (var s in result.Scenes)
            {
                string motion = s.Motion == null ? "" : s.Motion.IsStatic ? " [statično]" : $" [{s.Motion.Direction}]";
                sb.AppendLine($"  [{s.Index:D3}]  {AIHighlightEngine.FormatTime(s.Start)} → " +
                              $"{AIHighlightEngine.FormatTime(s.End)}  ({s.Duration:F1}s)");
                sb.AppendLine($"         {s.Label}{motion}");
            }
            sb.AppendLine();
            sb.AppendLine($"Generisan: {DateTime.Now:dd.MM.yyyy HH:mm:ss}");
            sb.AppendLine("Ultra Creative Suite — Smart Scene Detector (Faza 4A)");
            return sb.ToString();
        }

        private static void Report(IProgress<(int, string)> p, int pct, string msg)
            => p?.Report((pct, msg));

        private static SceneDetectionResult Fail(string error)
            => new SceneDetectionResult { Error = error };
    }
}
