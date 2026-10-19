using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UltraVideoEditor
{
    /// <summary>
    /// v1.4 (R3) — "hear the whole montage without a render".
    ///
    /// Builds ONE audio file with everything the finished video will sound like: every audio
    /// clip (music, ambient sounds, effects, voice) and the original sound of your own video
    /// clips, each at its place on the timeline, from its in-point, for its length and at its
    /// volume — the same rules the final render uses. Mixing takes seconds instead of the
    /// minutes (or hours) a render takes, so a blind editor can check the timing and the
    /// balance of the montage by ear, as a sighted editor in Premiere just presses Space.
    ///
    /// The result is cached: as long as the timeline does not change, the same file is
    /// played again without mixing.
    /// </summary>
    public static class TimelineMixPreview
    {
        /// <summary>One sound placed on the timeline.</summary>
        private sealed class Part
        {
            public string Path;
            public double SourceOffset;
            public double Duration;
            public double Start;
            public double Volume;   // 1.0 = 100 %
            public TimelineItem Item;   // for the volume curve and audio effects (null for partial mixes)
        }

        private const int MaxInputsPerPass = 32;   // keeps the command line short on Windows

        private static string CacheDir => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "UVE_MixPreview");

        /// <summary>Length of the montage: where the last clip ends.</summary>
        public static double TotalLength(IEnumerable<TimelineItem> items) =>
            items?.Where(i => i != null).Select(i => i.Start + Math.Max(0, i.Duration)).DefaultIfEmpty(0).Max() ?? 0;

        /// <summary>
        /// Creates (or reuses) the mix of the whole timeline. Returns the WAV path, or null when
        /// there is nothing to hear or FFmpeg failed. <paramref name="soundCount"/> tells how
        /// many sounds went into the mix.
        /// </summary>
        public static async Task<(string Path, int SoundCount)> BuildAsync(IList<TimelineItem> items, CancellationToken ct)
        {
            if (items == null || items.Count == 0) return (null, 0);
            double total = TotalLength(items);
            if (total < 0.2) return (null, 0);

            var parts = new List<Part>();

            // 1) every audio clip (music, ambience, effects, narration)
            foreach (var a in items.Where(i => i != null && (i.IsAudio || i.Type == "Audio") && !i.IsVideo && !i.IsImage))
            {
                if (string.IsNullOrEmpty(a.Path) || !File.Exists(a.Path) || a.Volume <= 0 || a.Duration <= 0.01) continue;
                parts.Add(ToPart(a));
            }

            // 2) the original sound of the user's own video clips — exactly as the render does it:
            //    not for AI creator stock footage, not for muted clips, not for clips with a changed speed
            var ownVideos = items.Where(v => v != null && v.IsVideo && !v.IsImage &&
                                             !RenderEngine.IsAiCreatorClip(v) && v.Volume > 0 && v.Duration > 0.01 &&
                                             Math.Abs((v.VideoEffect?.Speed ?? 1.0) - 1.0) < 0.001 &&
                                             !string.IsNullOrEmpty(v.Path) && File.Exists(v.Path)).ToList();
            var videoParts = new List<Part>();
            foreach (var v in ownVideos)
            {
                ct.ThrowIfCancellationRequested();
                if (await ExportHelpers.HasAudioAsync(v.Path, ct))
                    videoParts.Add(ToPart(v));
            }
            parts.AddRange(videoParts);

            if (parts.Count == 0) return (null, 0);

            Directory.CreateDirectory(CacheDir);
            string key = CacheKey(parts, total);
            string outPath = System.IO.Path.Combine(CacheDir, $"mix_{key}.wav");
            if (File.Exists(outPath) && new FileInfo(outPath).Length > 1000)
                return (outPath, parts.Count);

            CleanOldMixes(outPath);

            bool ok = await MixAsync(parts, total, outPath, ct);
            if (!ok && videoParts.Count > 0)
            {
                // A video whose sound cannot be read (HasAudio could not tell) breaks the whole
                // graph — try again with the audio clips only rather than giving nothing.
                parts = parts.Except(videoParts).ToList();
                ok = parts.Count > 0 && await MixAsync(parts, total, outPath, ct);
            }
            return ok ? (outPath, parts.Count) : (null, 0);
        }

        private static Part ToPart(TimelineItem i) => new Part
        {
            Path = i.Path,
            SourceOffset = Math.Max(0, i.SourceOffset),
            Duration = i.Duration,
            Start = Math.Max(0, i.Start),
            Volume = Math.Clamp(i.Volume / 100.0, 0.0, 4.0),
            Item = i
        };

        /// <summary>Mixes in passes of at most 32 inputs; the partial mixes are then mixed together.</summary>
        private static async Task<bool> MixAsync(List<Part> parts, double total, string outPath, CancellationToken ct)
        {
            var temps = new List<string>();
            try
            {
                var current = parts;
                while (current.Count > MaxInputsPerPass)
                {
                    var next = new List<Part>();
                    foreach (var group in current.Select((p, i) => (p, i)).GroupBy(x => x.i / MaxInputsPerPass))
                    {
                        string partial = System.IO.Path.Combine(CacheDir, $"partial_{Guid.NewGuid():N}.wav");
                        temps.Add(partial);
                        if (!await MixPassAsync(group.Select(x => x.p).ToList(), total, partial, ct)) return false;
                        next.Add(new Part { Path = partial, SourceOffset = 0, Duration = total, Start = 0, Volume = 1.0 });
                    }
                    current = next;
                }
                return await MixPassAsync(current, total, outPath, ct);
            }
            finally
            {
                foreach (var t in temps) { try { File.Delete(t); } catch { } }
            }
        }

        private static async Task<bool> MixPassAsync(List<Part> parts, double total, string outPath, CancellationToken ct)
        {
            var inv = CultureInfo.InvariantCulture;
            var args = new StringBuilder("-nostdin -y ");
            // a silent bed the length of the montage sets the length of the mix
            args.Append($"-f lavfi -t {total.ToString("0.###", inv)} -i anullsrc=r=48000:cl=stereo ");
            var graph = new StringBuilder();
            for (int k = 0; k < parts.Count; k++)
            {
                var p = parts[k];
                if (p.SourceOffset > 0.001) args.Append($"-ss {p.SourceOffset.ToString("0.###", inv)} ");
                args.Append($"-t {Math.Max(0.05, p.Duration).ToString("0.###", inv)} -i \"{p.Path}\" ");
                long delayMs = (long)Math.Round(p.Start * 1000);
                // v1.4 (R5): the clip's audio effects, then its volume (or volume curve) in timeline time
                string fx = p.Item != null ? AudioEffects.GetFFmpegFilter(p.Item.AudioFx) : "";
                string vol = p.Item != null && AudioAutomation.HasCurve(p.Item)
                    ? AudioAutomation.VolumeFilter(p.Item, p.Start)
                    : $"volume={p.Volume.ToString("0.###", inv)}";
                graph.Append($"[{k + 1}:a]aformat=sample_rates=48000:channel_layouts=stereo," +
                             (fx.Length > 0 ? fx + "," : "") +
                             $"adelay={delayMs}:all=1,{vol}[a{k + 1}];");
            }
            graph.Append("[0:a]");
            for (int k = 1; k <= parts.Count; k++) graph.Append($"[a{k}]");
            graph.Append($"amix=inputs={parts.Count + 1}:duration=first:normalize=0:dropout_transition=0[aout]");
            args.Append($"-filter_complex \"{graph}\" -map \"[aout]\" -c:a pcm_f32le \"{outPath}\"");

            bool ok = await ExportHelpers.RunFfmpegAsync(args.ToString(), ct);
            return ok && File.Exists(outPath) && new FileInfo(outPath).Length > 1000;
        }

        /// <summary>Same timeline (files, places, lengths, volumes, file dates) → same key.</summary>
        private static string CacheKey(List<Part> parts, double total)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(total.ToString("0.###", inv));
            foreach (var p in parts.OrderBy(p => p.Start).ThenBy(p => p.Path, StringComparer.Ordinal))
            {
                long stamp = 0;
                try { stamp = File.GetLastWriteTimeUtc(p.Path).Ticks; } catch { }
                sb.Append('|').Append(p.Path).Append(';').Append(stamp)
                  .Append(';').Append(p.Start.ToString("0.###", inv))
                  .Append(';').Append(p.SourceOffset.ToString("0.###", inv))
                  .Append(';').Append(p.Duration.ToString("0.###", inv))
                  .Append(';').Append(p.Volume.ToString("0.###", inv))
                  .Append(';').Append(p.Item != null ? AudioAutomation.VolumeFilter(p.Item, p.Start) : "")
                  .Append(';').Append(p.Item != null ? AudioEffects.GetFFmpegFilter(p.Item.AudioFx) : "");
            }
            byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }

        /// <summary>Only the newest mix is kept — older ones are removed so the temp folder does not grow.</summary>
        private static void CleanOldMixes(string keep)
        {
            try
            {
                foreach (var f in Directory.GetFiles(CacheDir, "*.wav"))
                    if (!string.Equals(f, keep, StringComparison.OrdinalIgnoreCase))
                        try { File.Delete(f); } catch { }
            }
            catch { }
        }

        /// <summary>The video clip on screen at a timeline time (Video 2 is on top of Video 1).</summary>
        public static TimelineItem VideoAt(IEnumerable<TimelineItem> items, double t) =>
            items?.Where(i => i != null && (i.IsVideo || i.IsImage) && t >= i.Start && t < i.Start + i.Duration)
                  .OrderByDescending(i => i.TrackIndex)
                  .FirstOrDefault();
    }
}
