using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace UltraVideoEditor
{
    /// <summary>
    /// v1.4 — how clips get their place on the timeline. Shared by the editor (MainWindow)
    /// and Batch Export so a project renders the same way everywhere.
    ///
    /// Tracks run in parallel (Video 1, Video 2, Audio 1, Audio 2 each start at 0).
    /// Within a track clips follow each other in list order; a clip with a fixed position
    /// stays exactly there and the others flow around it.
    /// </summary>
    public static class TimelineLayout
    {
        public static void Recalculate(IList<TimelineItem> allItems)
        {
            if (allItems == null) return;
            foreach (var track in allItems.GroupBy(i => i.TrackIndex))
            {
                var items = track.ToList();
                var fixedItems = items.Where(i => i.UseFixedPosition).ToList();
                foreach (var f in fixedItems)
                {
                    f.Start = Math.Max(0, f.FixedPosition);
                    f.End = f.Start + f.Duration;
                }

                double cursor = 0;
                foreach (var it in items)
                {
                    if (it.UseFixedPosition)
                    {
                        cursor = Math.Max(cursor, it.End);
                        continue;
                    }
                    bool moved = true;
                    while (moved)
                    {
                        moved = false;
                        foreach (var f in fixedItems)
                        {
                            if (f.Start < cursor + it.Duration - 0.001 && f.End > cursor + 0.001)
                            {
                                cursor = f.End;
                                moved = true;
                            }
                        }
                    }
                    it.Start = cursor;
                    it.End = cursor + it.Duration;
                    cursor = it.End;
                }
            }
        }
    }

    /// <summary>
    /// v1.4 — small export helpers used by Final Render and Batch Export:
    ///  • audio-only export (the "Export audio only" option and batch MP3 never worked —
    ///    the renderer was asked for a video in resolution "audio" and crashed);
    ///  • WebM / AVI output (the renderer always writes H.264 + AAC, which a .webm file
    ///    cannot hold, so WebM renders failed at the very end).
    /// </summary>
    public static class ExportHelpers
    {
        private static string FfmpegPath => AppPaths.FFmpeg;

        public static async Task<bool> RunFfmpegAsync(string args, CancellationToken ct)
        {
            if (!File.Exists(FfmpegPath)) return false;
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo(FfmpegPath, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                }
            };
            p.Start();
            p.DrainAll();
            await p.WaitForExitOrKillAsync(ct);
            return p.ExitCode == 0;
        }

        /// <summary>
        /// Writes the project's main audio (the longest audio clip, from its in-point) to an
        /// MP3 or WAV file, depending on the extension of <paramref name="outPath"/>.
        /// </summary>
        public static async Task<bool> ExportAudioOnlyAsync(IEnumerable<TimelineItem> items, string outPath, CancellationToken ct)
        {
            var audio = items?
                .Where(i => (i.Type == "Audio" || i.IsAudio) && !string.IsNullOrEmpty(i.Path) && File.Exists(i.Path))
                .OrderByDescending(i => i.Duration)
                .FirstOrDefault();
            if (audio == null) return false;

            var inv = CultureInfo.InvariantCulture;
            string seek = audio.SourceOffset > 0.01 ? $"-ss {audio.SourceOffset.ToString("F3", inv)} " : "";
            bool wav = outPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
            string codec = wav ? "-c:a pcm_s16le" : "-c:a libmp3lame -b:a 192k";
            string args = $"-nostdin -y {seek}-i \"{audio.Path}\" -vn {codec} \"{outPath}\"";
            return await RunFfmpegAsync(args, ct) && File.Exists(outPath);
        }

        /// <summary>
        /// v1.4 — cuts the highlight segments out of their source videos and joins them into one
        /// 1080p MP4 that keeps the clips' own sound (silence for sources without sound).
        /// Phase 3 used to export the whole SOURCE video instead of the highlight.
        /// </summary>
        public static async Task<bool> CutSegmentsAsync(IList<HighlightSegment> segments, string outPath, CancellationToken ct)
        {
            if (segments == null || segments.Count == 0) return false;
            var inv = CultureInfo.InvariantCulture;
            var inputs = new System.Text.StringBuilder();
            var graph = new System.Text.StringBuilder();
            var concatIn = new System.Text.StringBuilder();
            int i = 0;
            foreach (var seg in segments.OrderBy(s => s.Order))
            {
                if (string.IsNullOrEmpty(seg.SourcePath) || !File.Exists(seg.SourcePath) || seg.Duration <= 0.05) continue;
                string dur = seg.Duration.ToString("F3", inv);
                inputs.Append($"-ss {Math.Max(0, seg.SourceStart).ToString("F3", inv)} -t {dur} -i \"{seg.SourcePath}\" ");
                graph.Append($"[{i}:v]scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30,format=yuv420p[v{i}];");
                graph.Append(await HasAudioAsync(seg.SourcePath, ct)
                    ? $"[{i}:a]aformat=sample_rates=48000:channel_layouts=stereo[a{i}];"
                    : $"anullsrc=r=48000:cl=stereo,atrim=duration={dur}[a{i}];");
                concatIn.Append($"[v{i}][a{i}]");
                i++;
            }
            if (i == 0) return false;
            graph.Append($"{concatIn}concat=n={i}:v=1:a=1[v][a]");
            string args = $"-nostdin -y {inputs}-filter_complex \"{graph}\" -map \"[v]\" -map \"[a]\" " +
                          $"-c:v libx264 -preset veryfast -crf 18 -c:a aac -b:a 192k -movflags +faststart \"{outPath}\"";
            return await RunFfmpegAsync(args, ct) && File.Exists(outPath) && new FileInfo(outPath).Length > 1000;
        }

        /// <summary>True when the file has at least one audio stream (ffprobe next to ffmpeg.exe).</summary>
        public static async Task<bool> HasAudioAsync(string path, CancellationToken ct)
        {
            string probe = Path.Combine(Path.GetDirectoryName(FfmpegPath) ?? "", "ffprobe.exe");
            if (!File.Exists(probe)) return true;   // can't tell — assume the usual case
            try
            {
                using var p = new Process
                {
                    StartInfo = new ProcessStartInfo(probe,
                        $"-v error -select_streams a -show_entries stream=index -of csv=p=0 \"{path}\"")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                p.Start();
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                await p.WaitForExitOrKillAsync(ct);
                string output = await outTask;
                await errTask;
                return !string.IsNullOrWhiteSpace(output);
            }
            catch (OperationCanceledException) { throw; }
            catch { return true; }
        }

        /// <summary>
        /// Converts a finished MP4 render into WebM (VP9 + Opus) or AVI (MPEG-4 + MP3).
        /// Returns true when <paramref name="outPath"/> was written.
        /// </summary>
        public static async Task<bool> ConvertContainerAsync(string mp4Path, string outPath, CancellationToken ct)
        {
            string ext = Path.GetExtension(outPath).ToLowerInvariant();
            string codecs = ext switch
            {
                ".webm" => "-c:v libvpx-vp9 -crf 32 -b:v 0 -row-mt 1 -c:a libopus -b:a 160k",
                ".avi" => "-c:v mpeg4 -q:v 3 -c:a libmp3lame -b:a 192k",
                _ => "-c copy"
            };
            string args = $"-nostdin -y -i \"{mp4Path}\" {codecs} \"{outPath}\"";
            return await RunFfmpegAsync(args, ct) && File.Exists(outPath) && new FileInfo(outPath).Length > 1000;
        }
    }
}
