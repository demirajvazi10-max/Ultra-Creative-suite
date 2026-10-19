using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace UltraVideoEditor
{
    /// <summary>v1.4 (R5) — one point of a clip's volume curve.</summary>
    public class VolumePoint
    {
        /// <summary>Seconds from the clip's start on the timeline.</summary>
        public double Time { get; set; }
        /// <summary>Volume in percent, on the same scale as the clip volume (100 = unchanged).</summary>
        public double Volume { get; set; } = 100;
    }

    /// <summary>
    /// v1.4 (R5) — volume that changes over time ("louder here, quieter there") and lowering the
    /// music under speech in one step. A clip without points simply uses its volume; with points,
    /// the volume moves in straight lines from point to point (before the first point it holds
    /// the first value, after the last point the last value). The final render, the montage mix
    /// (Ctrl+Shift+Space) and the clip preview all use the same curve.
    /// </summary>
    public static class AudioAutomation
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static bool HasCurve(TimelineItem item) => item?.VolumePoints != null && item.VolumePoints.Count > 0;

        /// <summary>Volume in percent at a position inside the clip.</summary>
        public static double VolumeAt(TimelineItem item, double posInClip)
        {
            if (!HasCurve(item)) return item?.Volume ?? 100;
            var pts = item.VolumePoints.OrderBy(p => p.Time).ToList();
            if (posInClip <= pts[0].Time) return pts[0].Volume;
            for (int i = 1; i < pts.Count; i++)
            {
                if (posInClip <= pts[i].Time)
                {
                    var a = pts[i - 1]; var b = pts[i];
                    double span = Math.Max(0.001, b.Time - a.Time);
                    return a.Volume + (b.Volume - a.Volume) * (posInClip - a.Time) / span;
                }
            }
            return pts[^1].Volume;
        }

        /// <summary>
        /// FFmpeg volume filter for the clip, for a chain in which t is TIMELINE time (after adelay):
        /// "volume=0.8", or "volume='if(lt(t,…),…)':eval=frame" when the clip has a curve.
        /// <paramref name="clipStartOnTimeline"/> is where the clip starts in that chain's time.
        /// </summary>
        public static string VolumeFilter(TimelineItem item, double clipStartOnTimeline, double maxGain = 4.0)
        {
            if (!HasCurve(item))
                return "volume=" + Math.Clamp((item?.Volume ?? 100) / 100.0, 0.0, maxGain).ToString("0.###", Inv);

            var pts = item.VolumePoints.OrderBy(p => p.Time).ToList();
            string G(VolumePoint p) => Math.Clamp(p.Volume / 100.0, 0.0, maxGain).ToString("0.###", Inv);
            string T(VolumePoint p) => (clipStartOnTimeline + p.Time).ToString("0.###", Inv);

            // nested if(): before point 0 → g0; between points → straight line; after the last → gN
            var sb = new StringBuilder();
            sb.Append($"if(lt(t,{T(pts[0])}),{G(pts[0])},");
            int open = 1;
            for (int i = 1; i < pts.Count; i++)
            {
                var a = pts[i - 1]; var b = pts[i];
                double span = Math.Max(0.001, b.Time - a.Time);
                sb.Append($"if(lt(t,{T(b)}),{G(a)}+({G(b)}-{G(a)})*(t-{T(a)})/{span.ToString("0.###", Inv)},");
                open++;
            }
            sb.Append(G(pts[^1]));
            sb.Append(')', open);
            return $"volume='{sb}':eval=frame";
        }

        /// <summary>Adds a point, or changes the one within 0.05 s of the same time.</summary>
        public static void SetPoint(TimelineItem item, double time, double volume)
        {
            item.VolumePoints ??= new List<VolumePoint>();
            var near = item.VolumePoints.FirstOrDefault(p => Math.Abs(p.Time - time) < 0.05);
            if (near != null) near.Volume = volume;
            else item.VolumePoints.Add(new VolumePoint { Time = time, Volume = volume });
            item.VolumePoints = item.VolumePoints.OrderBy(p => p.Time).ToList();
        }

        /// <summary>
        /// Lowers <paramref name="music"/> to <paramref name="duckPercent"/> % of its volume wherever one
        /// of the <paramref name="voices"/> sounds, with a short fade down before and up after.
        /// Replaces the music's existing curve. Returns the number of places lowered.
        /// </summary>
        public static int DuckUnder(TimelineItem music, IEnumerable<TimelineItem> voices, double duckPercent = 30, double fade = 0.4)
        {
            double baseVol = music.Volume;
            double low = baseVol * duckPercent / 100.0;
            double mStart = music.Start, mEnd = music.Start + music.Duration;

            // the voice intervals that overlap the music, merged when they nearly touch
            var spans = voices.Where(v => v != null && !ReferenceEquals(v, music) && v.Duration > 0.05)
                              .Select(v => (A: Math.Max(mStart, v.Start), B: Math.Min(mEnd, v.Start + v.Duration)))
                              .Where(s => s.B - s.A > 0.05)
                              .OrderBy(s => s.A).ToList();
            var merged = new List<(double A, double B)>();
            foreach (var s in spans)
            {
                if (merged.Count > 0 && s.A - merged[^1].B < fade * 2 + 0.3)
                    merged[^1] = (merged[^1].A, Math.Max(merged[^1].B, s.B));
                else merged.Add(s);
            }
            if (merged.Count == 0) return 0;

            var pts = new List<VolumePoint>();
            foreach (var (a, b) in merged)
            {
                double ca = a - mStart, cb = b - mStart;   // clip-relative
                pts.Add(new VolumePoint { Time = Math.Max(0, ca - fade), Volume = baseVol });
                pts.Add(new VolumePoint { Time = Math.Max(0, ca), Volume = low });
                pts.Add(new VolumePoint { Time = Math.Min(music.Duration, cb), Volume = low });
                pts.Add(new VolumePoint { Time = Math.Min(music.Duration, cb + fade), Volume = baseVol });
            }
            // a voice right at the very start: the music starts low
            music.VolumePoints = pts.GroupBy(p => Math.Round(p.Time, 2)).Select(g => g.Last()).OrderBy(p => p.Time).ToList();
            return merged.Count;
        }

        /// <summary>Spoken summary of a clip's curve, e.g. "00:04.0 100 %, 00:04.4 30 %".</summary>
        public static string Describe(TimelineItem item)
        {
            if (!HasCurve(item)) return null;
            return string.Join(", ", item.VolumePoints.OrderBy(p => p.Time)
                .Select(p => $"{TimeSpan.FromSeconds(Math.Max(0, p.Time)):mm\\:ss\\.f} {Math.Round(p.Volume)} %"));
        }
    }
}
