using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace UltraVideoEditor
{
    // ═══════════════════════════════════════════════════════════════
    // BATCH EXPORT ENGINE
    // Sequential render of multiple .iskra projects.
    // ═══════════════════════════════════════════════════════════════

    public static class BatchExportEngine
    {
        public static async Task RunAsync(
            List<BatchExportJob>                        jobs,
            IProgress<(int JobIndex, BatchExportJob Job, string Message)> progress,
            CancellationToken ct = default)
        {
            string ffmpegPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");

            for (int i = 0; i < jobs.Count; i++)
            {
                var job = jobs[i];
                if (ct.IsCancellationRequested) { job.Status = BatchJobStatus.Skipped; continue; }

                job.Status      = BatchJobStatus.Running;
                job.ProgressPct = 0;
                progress?.Report((i, job, $"Loading project: {job.ProjectName}…"));

                try
                {
                    // 1 — Load project
                    if (!File.Exists(job.ProjectPath))
                        throw new FileNotFoundException($"Project not found: {job.ProjectPath}");

                    string json    = await File.ReadAllTextAsync(job.ProjectPath, ct);
                    var projectData = JsonConvert.DeserializeObject<ProjectData>(json);
                    if (projectData == null)
                        throw new InvalidDataException("Project is unreadable.");

                    var items = projectData.TimelineItems ?? new List<TimelineItem>();
                    if (items.Count == 0)
                        throw new InvalidOperationException("Projekat nema klipoiva.");

                    // Reconstruct timeline positions
                    double t = 0;
                    foreach (var item in items) { item.Start = t; item.End = t + item.Duration; t += item.Duration; }

                    // 2 — Output folder
                    Directory.CreateDirectory(job.OutputFolder);
                    string baseName = Path.GetFileNameWithoutExtension(job.ProjectPath);
                    string outPath  = Path.Combine(job.OutputFolder, $"{baseName}_{job.FormatId}.mp4");

                    // 3 — ExportSettings
                    var exportSettings = BuildExportSettings(job.FormatId);
                    bool verticalCrop  = job.FormatId == "reels";
                    string resolution  = verticalCrop ? "1080x1920" : "1920x1080";

                    // 4 — Render (RenderSimpleAsync prima IProgress<int>)
                    var renderProgress = new Progress<int>(pct =>
                    {
                        job.ProgressPct = pct;
                        progress?.Report((i, job, $"{job.ProjectName}: {pct}%"));
                    });
                    var renderEngine = new RenderEngine();

                    if (job.FormatId == "mp3")
                    {
                        outPath = Path.Combine(job.OutputFolder, $"{baseName}_audio.mp3");
                        await renderEngine.RenderSimpleAsync(
                            items, outPath, "MP3", renderProgress,
                            projectData.Subtitles ?? new List<SubtitleItem>(),
                            exportSettings, ct,
                            useGPU: false, resolution: "audio",
                            fastRender: false, enableSubtitles: false);
                    }
                    else
                    {
                        await renderEngine.RenderSimpleAsync(
                            items, outPath, verticalCrop ? "Reels" : "MP4",
                            renderProgress,
                            projectData.Subtitles ?? new List<SubtitleItem>(),
                            exportSettings, ct,
                            useGPU: false, resolution: resolution,
                            fastRender: false, enableSubtitles: false);
                    }

                    job.OutputFile  = outPath;
                    job.Status      = BatchJobStatus.Done;
                    job.ProgressPct = 100;
                    progress?.Report((i, job, $"✅ {job.ProjectName} — gotovo!"));
                }
                catch (OperationCanceledException)
                {
                    job.Status = BatchJobStatus.Skipped;
                    progress?.Report((i, job, $"⏭ {job.ProjectName} — skipped."));
                }
                catch (Exception ex)
                {
                    job.Status       = BatchJobStatus.Error;
                    job.ErrorMessage = ex.Message;
                    progress?.Report((i, job, $"❌ {job.ProjectName} — error: {ex.Message}"));
                }
            }
        }

        private static ExportSettingsData BuildExportSettings(string formatId)
        {
            return formatId switch
            {
                "reels"   => new ExportSettingsData { Format = "Reels", Quality = "High" },
                "mp3"     => new ExportSettingsData { Format = "MP3",   Quality = "High" },
                "youtube" => new ExportSettingsData { Format = "MP4",   Quality = "High" },
                _         => new ExportSettingsData { Format = "MP4",   Quality = "Medium" },
            };
        }
    }
}
