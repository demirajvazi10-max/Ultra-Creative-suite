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
    // EXPORT PIPELINE  —  Faza 3 / D
    //
    // Final output in multiple formats at once:
    //   - MP4  1920×1080  (YouTube/general use)
    //   - MP4  1080×1920  (Instagram Reels / TikTok, vertical crop)
    //   - MP3             (audio-only, 192kbps)
    //   - TXT             (accessibility report)
    //
    // Sve jednim pozivom ExportAllAsync().
    // ═══════════════════════════════════════════════════════════════

    public class ExportFormat
    {
        public string  Id          { get; set; }   // "youtube", "reels", "mp3", "report"
        public string  Label       { get; set; }
        public bool    Enabled     { get; set; } = true;
        public string  OutputPath  { get; set; }
        public string  Description { get; set; }
    }

    public class ExportJob
    {
        /// <summary>Putanja do renderovanog highlight videa (ulaz).</summary>
        public string SourceVideoPath   { get; set; }

        /// <summary>Putanja do miksovanog audio fajla (ulaz, opciono).</summary>
        public string MixedAudioPath    { get; set; }

        /// <summary>Folder where all exported files are stored.</summary>
        public string OutputFolder      { get; set; }

        /// <summary>Baza za imena fajlova (npr. "highlight_20250608").</summary>
        public string BaseName          { get; set; } = "highlight";

        /// <summary>Lista formata za export.</summary>
        public List<ExportFormat> Formats { get; set; } = DefaultFormats();

        /// <summary>HighlightResult za accessibility report.</summary>
        public HighlightResult HighlightResult { get; set; }

        /// <summary>Odluke o prelazima (za report).</summary>
        public List<TransitionDecision> Transitions { get; set; }

        /// <summary>Audio settings (for report).</summary>
        public AudioMixSettings AudioSettings { get; set; }

        /// <summary>Koristiti GPU encoding.</summary>
        public bool UseGPU { get; set; } = true;

        public static List<ExportFormat> DefaultFormats() => new()
        {
            new ExportFormat
            {
                Id = "youtube", Label = "YouTube / FHD",
                Description = "MP4 1920×1080, H.264, AAC 192kbps",
            },
            new ExportFormat
            {
                Id = "reels", Label = "Instagram Reels / TikTok",
                Description = "MP4 1080×1920 (vertical), H.264, AAC 192kbps",
            },
            new ExportFormat
            {
                Id = "mp3", Label = "Audio MP3",
                Description = "MP3 192kbps, stereo",
            },
            new ExportFormat
            {
                Id = "report", Label = "Accessibility Report",
                Description = "TXT report with audio-description and navigation markers",
            },
        };
    }

    public class ExportResult
    {
        public string FormatId    { get; set; }
        public string OutputPath  { get; set; }
        public bool   Success     { get; set; }
        public string Error       { get; set; }
        public long   FileSizeBytes { get; set; }
        public TimeSpan Duration  { get; set; }
    }

    public class ExportPipelineResult
    {
        public List<ExportResult> Results      { get; set; } = new();
        public bool   AllSucceeded => Results.All(r => r.Success);
        public int    SuccessCount => Results.Count(r => r.Success);
        public string Summary      { get; set; }
    }

    public static class ExportPipeline
    {
        // ── Javni API ────────────────────────────────────────────────

        /// <summary>
        /// Exports all enabled formats from ExportJob.
        /// </summary>
        public static async Task<ExportPipelineResult> ExportAllAsync(
            ExportJob job,
            IProgress<(int Percent, string Message)> progress = null,
            CancellationToken ct = default)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            if (!File.Exists(job.SourceVideoPath))
                throw new FileNotFoundException($"Source video not found: {job.SourceVideoPath}");

            string ffmpegPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");
            if (!File.Exists(ffmpegPath))
                throw new FileNotFoundException("FFmpeg not found.");

            Directory.CreateDirectory(job.OutputFolder);

            var pipeline  = new ExportPipelineResult();
            var enabled   = job.Formats.Where(f => f.Enabled).ToList();
            int total     = enabled.Count;
            int done      = 0;

            foreach (var format in enabled)
            {
                ct.ThrowIfCancellationRequested();

                int pctStart = done * 100 / total;
                int pctEnd   = (done + 1) * 100 / total;
                progress?.Report((pctStart, $"Exportujem: {format.Label}…"));

                var innerProgress = new Progress<(int, string)>(p =>
                {
                    int mapped = pctStart + (p.Item1 * (pctEnd - pctStart) / 100);
                    progress?.Report((mapped, $"{format.Label}: {p.Item2}"));
                });

                var result = await ExportOneAsync(
                    format, job, ffmpegPath, innerProgress, ct);

                pipeline.Results.Add(result);
                done++;
                progress?.Report((pctEnd, $"{format.Label}: {(result.Success ? "✓" : "✗")}"));
            }

            pipeline.Summary = BuildSummary(pipeline, job);
            progress?.Report((100, "Export complete."));
            return pipeline;
        }

        // ── Per-format export ────────────────────────────────────────

        private static async Task<ExportResult> ExportOneAsync(
            ExportFormat format,
            ExportJob    job,
            string       ffmpegPath,
            IProgress<(int, string)> progress,
            CancellationToken ct)
        {
            var sw  = System.Diagnostics.Stopwatch.StartNew();
            string outputPath = Path.Combine(
                job.OutputFolder,
                $"{job.BaseName}_{format.Id}{GetExtension(format.Id)}");
            format.OutputPath = outputPath;

            try
            {
                switch (format.Id)
                {
                    case "youtube":
                        await ExportVideoAsync(
                            job.SourceVideoPath, job.MixedAudioPath,
                            outputPath, "1920x1080", false,
                            job.UseGPU, ffmpegPath, ct);
                        break;

                    case "reels":
                        await ExportVerticalAsync(
                            job.SourceVideoPath, job.MixedAudioPath,
                            outputPath, job.UseGPU, ffmpegPath, ct);
                        break;

                    case "mp3":
                        await ExportAudioAsync(
                            job.MixedAudioPath ?? job.SourceVideoPath,
                            outputPath, ffmpegPath, ct);
                        break;

                    case "report":
                        await ExportReportAsync(
                            job, outputPath, ct);
                        break;

                    default:
                        return Fail(format.Id, outputPath, $"Nepoznat format: {format.Id}");
                }

                sw.Stop();
                long size = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;
                return new ExportResult
                {
                    FormatId      = format.Id,
                    OutputPath    = outputPath,
                    Success       = true,
                    FileSizeBytes = size,
                    Duration      = sw.Elapsed,
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                sw.Stop();
                return Fail(format.Id, outputPath, ex.Message);
            }
        }

        // ── Video export ─────────────────────────────────────────────

        private static async Task ExportVideoAsync(
            string sourcePath, string audioPath, string outputPath,
            string resolution, bool verticalCrop,
            bool useGPU, string ffmpegPath, CancellationToken ct)
        {
            string vcodec  = useGPU ? "h264_nvenc" : "libx264";
            string vfScale = $"scale={resolution}:force_original_aspect_ratio=decrease," +
                             $"pad={resolution}:(ow-iw)/2:(oh-ih)/2";
            string audioInput = File.Exists(audioPath ?? "")
                ? $"-i \"{audioPath}\" " : "";
            string audioMap   = File.Exists(audioPath ?? "")
                ? "-map 0:v -map 1:a" : "-map 0";

            string args = $"-nostdin -y " +
                          $"-i \"{sourcePath}\" {audioInput}" +
                          $"-vf \"{vfScale}\" " +
                          $"{audioMap} " +
                          $"-c:v {vcodec} -preset fast -crf 18 " +
                          $"-c:a aac -b:a 192k " +
                          $"-movflags +faststart " +
                          $"\"{outputPath}\"";

            await RunFfmpegAsync(ffmpegPath, args, ct);
        }

        private static async Task ExportVerticalAsync(
            string sourcePath, string audioPath, string outputPath,
            bool useGPU, string ffmpegPath, CancellationToken ct)
        {
            // Vertical crop: center-crop sa 16:9 → 9:16
            // crop=ih*9/16:ih, zatim scale na 1080x1920
            string vcodec = useGPU ? "h264_nvenc" : "libx264";
            string audioInput = File.Exists(audioPath ?? "")
                ? $"-i \"{audioPath}\" " : "";
            string audioMap   = File.Exists(audioPath ?? "")
                ? "-map 0:v -map 1:a" : "-map 0";

            string vf = "crop=ih*9/16:ih:(iw-ih*9/16)/2:0," +
                        "scale=1080:1920";

            string args = $"-nostdin -y " +
                          $"-i \"{sourcePath}\" {audioInput}" +
                          $"-vf \"{vf}\" " +
                          $"{audioMap} " +
                          $"-c:v {vcodec} -preset fast -crf 18 " +
                          $"-c:a aac -b:a 192k " +
                          $"-movflags +faststart " +
                          $"\"{outputPath}\"";

            await RunFfmpegAsync(ffmpegPath, args, ct);
        }

        private static async Task ExportAudioAsync(
            string sourcePath, string outputPath,
            string ffmpegPath, CancellationToken ct)
        {
            string args = $"-nostdin -y " +
                          $"-i \"{sourcePath}\" " +
                          $"-vn -c:a libmp3lame -b:a 192k -q:a 2 " +
                          $"\"{outputPath}\"";

            await RunFfmpegAsync(ffmpegPath, args, ct);
        }

        private static async Task ExportReportAsync(
            ExportJob job, string outputPath, CancellationToken ct)
        {
            var opts = new AccessibilityReportOptions
            {
                ProjectName        = job.BaseName,
                Language           = "sr",
                IncludeTtsSummary  = true,
                IncludeNavMarkers  = true,
                IncludeTransitions = job.Transitions?.Count > 0,
            };

            await AccessibilityReportGenerator.GenerateAsync(
                result        : job.HighlightResult,
                transitions   : job.Transitions,
                audioSettings : job.AudioSettings,
                options       : opts,
                outputPath    : outputPath,
                ct            : ct);
        }

        // ── Summary ──────────────────────────────────────────────────

        private static string BuildSummary(
            ExportPipelineResult pipeline, ExportJob job)
        {
            var sb = new StringBuilder();
            sb.AppendLine("╔══════════════════════════════════════════════════════╗");
            sb.AppendLine("║           EXPORT PIPELINE — REZULTAT                ║");
            sb.AppendLine("╚══════════════════════════════════════════════════════╝");
            sb.AppendLine();
            sb.AppendLine($"Output folder: {job.OutputFolder}");
            sb.AppendLine($"Successful: {pipeline.SuccessCount}/{pipeline.Results.Count}");
            sb.AppendLine();

            foreach (var r in pipeline.Results)
            {
                string icon   = r.Success ? "✅" : "❌";
                string size   = r.Success ? FormatSize(r.FileSizeBytes) : r.Error;
                string time   = r.Success ? $"{r.Duration.TotalSeconds:F1}s" : "";
                sb.AppendLine($"  {icon}  {r.FormatId,-12} {size,-18} {time}");
                if (r.Success)
                    sb.AppendLine($"       {r.OutputPath}");
            }

            sb.AppendLine();
            sb.AppendLine($"Generisan: {DateTime.Now:dd.MM.yyyy HH:mm:ss}");
            return sb.ToString();
        }

        // ── FFmpeg runner ─────────────────────────────────────────────

        private static async Task RunFfmpegAsync(
            string ffmpegPath, string args, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName               = ffmpegPath,
                Arguments              = args,
                RedirectStandardError  = true,
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("FFmpeg process start failed.");

            var errTask = proc.StandardError.ReadToEndAsync();
            var outTask = proc.StandardOutput.ReadToEndAsync();

            try { await proc.WaitForExitAsync(ct); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                ct.ThrowIfCancellationRequested();
            }

            await Task.WhenAll(errTask, outTask);

            if (proc.ExitCode != 0)
            {
                string log = await errTask;
                throw new InvalidOperationException(
                    $"FFmpeg exit {proc.ExitCode}: {log.Split('\n').LastOrDefault()}");
            }
        }

        // ── Helpers ──────────────────────────────────────────────────

        private static string GetExtension(string formatId) => formatId switch
        {
            "youtube" => ".mp4",
            "reels"   => ".mp4",
            "mp3"     => ".mp3",
            "report"  => ".txt",
            _         => ".bin",
        };

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024)        return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024):F1} MB";
        }

        private static ExportResult Fail(string id, string path, string error)
            => new ExportResult { FormatId = id, OutputPath = path,
                                  Success = false, Error = error };
    }
}
