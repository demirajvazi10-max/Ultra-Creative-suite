using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace UltraVideoEditor
{
    /// <summary>v2.0 — the mixer settings of one track (saved with the project).</summary>
    public class TrackMix
    {
        public int Track { get; set; }
        /// <summary>Percent, 0 … 200.</summary>
        public double Volume { get; set; } = 100;
        public bool Mute { get; set; }
        public bool Solo { get; set; }
        /// <summary>Three-band equaliser in dB, -12 … 12.</summary>
        public double Low { get; set; }
        public double Mid { get; set; }
        public double High { get; set; }
        public bool Compressor { get; set; }

        // v2.0 — more sound per track
        /// <summary>Left / right, -100 (left) … 100 (right).</summary>
        public double Pan { get; set; }
        /// <summary>Cuts rumble and wind below 80 Hz.</summary>
        public bool LowCut { get; set; }
        /// <summary>Softens sharp "s" sounds of a voice.</summary>
        public bool DeEsser { get; set; }
        /// <summary>Takes away steady background noise (hiss, hum, fans).</summary>
        public bool NoiseReduction { get; set; }
        /// <summary>Room echo, 0 … 100.</summary>
        public double Reverb { get; set; }
        /// <summary>Keeps the track from ever getting too loud (no clipping).</summary>
        public bool Limiter { get; set; }

        /// <summary>
        /// v2.0 — mixer automation: the track's volume over time (seconds on the timeline,
        /// percent). Between points it changes smoothly; null / empty = the steady volume.
        /// </summary>
        public List<VolumePoint> Points { get; set; }

        public bool HasPoints => Points != null && Points.Count > 0;

        public bool IsDefault => Math.Abs(Volume - 100) < 0.01 && !Mute && !Solo && Low == 0 && Mid == 0 && High == 0 && !Compressor &&
                                 Pan == 0 && !LowCut && !DeEsser && !NoiseReduction && Reverb == 0 && !Limiter && !HasPoints;
        public TrackMix Clone()
        {
            var c = (TrackMix)MemberwiseClone();
            c.Points = Points?.Select(p => new VolumePoint { Time = p.Time, Volume = p.Volume }).ToList();
            return c;
        }
    }

    /// <summary>
    /// v2.0 — the track mixer: volume, mute, solo, equaliser and compressor per track,
    /// on top of each clip's own settings. Used by the export, the whole-video preview,
    /// the sound-only montage mix and the layer tracks.
    /// </summary>
    public static class TrackMixer
    {
        /// <summary>The settings of the open project (empty = every track as it is).</summary>
        public static List<TrackMix> Current { get; set; } = new List<TrackMix>();

        private static TrackMix Get(int track) => Current?.FirstOrDefault(m => m.Track == track);

        private static TrackMix GetOrAdd(int track)
        {
            Current ??= new List<TrackMix>();
            var m = Get(track);
            if (m == null) { m = new TrackMix { Track = track }; Current.Add(m); }
            return m;
        }

        /// <summary>The track's volume as a factor (0 when it is muted, or when another track is soloed).</summary>
        public static double Gain(int track)
        {
            if (Current == null || Current.Count == 0) return 1.0;
            if (Current.Any(m => m.Solo) && !(Get(track)?.Solo ?? false)) return 0.0;
            var m = Get(track);
            if (m == null) return 1.0;
            if (m.Mute) return 0.0;
            return Math.Clamp(m.Volume / 100.0, 0, 2);
        }

        /// <summary>
        /// The filters to add after a clip's own chain for its track ("" = none): equaliser,
        /// compressor and volume.
        /// </summary>
        public static string Filter(int track)
        {
            var m = Get(track);
            var f = new List<string>();
            string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
            if (m != null)
            {
                if (m.LowCut) f.Add("highpass=f=80");
                if (m.NoiseReduction) f.Add("afftdn=nf=-25");
                if (m.Low != 0) f.Add($"equalizer=frequency=120:width_type=o:width=2:gain={N(Math.Clamp(m.Low, -12, 12))}");
                if (m.Mid != 0) f.Add($"equalizer=frequency=1000:width_type=o:width=2:gain={N(Math.Clamp(m.Mid, -12, 12))}");
                if (m.High != 0) f.Add($"equalizer=frequency=8000:width_type=o:width=2:gain={N(Math.Clamp(m.High, -12, 12))}");
                if (m.DeEsser) f.Add("deesser=i=0.4");
                if (m.Compressor) f.Add("acompressor=threshold=0.125:ratio=3:attack=10:release=150:makeup=1.5");
                if (m.Reverb > 0.5)
                {
                    double r = Math.Clamp(m.Reverb, 0, 100) / 100;
                    f.Add($"aecho=0.8:0.88:40|70|110:{N(0.35 * r)}|{N(0.25 * r)}|{N(0.15 * r)}");
                }
                if (Math.Abs(m.Pan) > 0.5) f.Add($"stereotools=balance_out={N(Math.Clamp(m.Pan, -100, 100) / 100)}");
            }
            double g = Gain(track);
            if (m != null && m.HasPoints && g > 0)
                // v2.0: mixer automation — t is timeline time here (after the clip was moved to its place)
                f.Add($"volume='{N(g / Math.Clamp(m.Volume / 100.0, 0.0001, 2))}*({PointsExpr(m.Points)})':eval=frame");
            else if (Math.Abs(g - 1.0) > 0.001) f.Add($"volume={N(g)}");
            if (m != null && m.Limiter) f.Add("alimiter=limit=0.89:level=false");
            return string.Join(",", f);
        }

        /// <summary>v2.0 — the automation as a factor of t (timeline seconds): straight lines between the points.</summary>
        internal static string PointsExpr(List<VolumePoint> points)
        {
            string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
            var p = points.OrderBy(x => x.Time).ToList();
            string G(VolumePoint q) => N(Math.Clamp(q.Volume / 100.0, 0, 4));
            string e = G(p[^1]);
            for (int i = p.Count - 2; i >= 0; i--)
            {
                double span = Math.Max(0.001, p[i + 1].Time - p[i].Time);
                e = $"if(lt(t,{N(p[i + 1].Time)}),{G(p[i])}+({G(p[i + 1])}-{G(p[i])})*(t-({N(p[i].Time)}))/{N(span)},{e})";
            }
            return $"if(lt(t,{N(p[0].Time)}),{G(p[0])},{e})";
        }

        /// <summary>v2.0 — the track's own volume (percent) at a timeline time: its automation, or its steady volume.</summary>
        public static double VolumeAt(int track, double t)
        {
            var m = Get(track);
            if (m == null) return 100;
            if (!m.HasPoints) return m.Volume;
            var p = m.Points.OrderBy(x => x.Time).ToList();
            if (t <= p[0].Time) return p[0].Volume;
            if (t >= p[^1].Time) return p[^1].Volume;
            int i = p.FindIndex(x => x.Time > t);
            double k = (t - p[i - 1].Time) / Math.Max(0.001, p[i].Time - p[i - 1].Time);
            return p[i - 1].Volume + (p[i].Volume - p[i - 1].Volume) * k;
        }

        /// <summary>v2.0 — adds an automation point, or changes the one within 0.05 s; returns the number of points.</summary>
        public static int SetPoint(int track, double t, double volume)
        {
            var m = GetOrAdd(track);
            m.Points ??= new List<VolumePoint>();
            var near = m.Points.FirstOrDefault(p => Math.Abs(p.Time - t) < 0.05);
            if (near != null) near.Volume = volume;
            else m.Points.Add(new VolumePoint { Time = Math.Max(0, t), Volume = volume });
            m.Points = m.Points.OrderBy(p => p.Time).ToList();
            return m.Points.Count;
        }

        /// <summary>v2.0 — removes the automation point nearest to t (within 1 s); false when there is none.</summary>
        public static bool RemovePointNear(int track, double t)
        {
            var m = Get(track);
            if (m?.Points == null || m.Points.Count == 0) return false;
            var near = m.Points.OrderBy(p => Math.Abs(p.Time - t)).First();
            if (Math.Abs(near.Time - t) > 1.0) return false;
            m.Points.Remove(near);
            return true;
        }

        public static void ClearPoints(int track)
        {
            var m = Get(track);
            if (m != null) m.Points = null;
        }

        public static List<VolumePoint> PointsOf(int track) => Get(track)?.Points?.OrderBy(p => p.Time).ToList() ?? new List<VolumePoint>();

        /// <summary>v2.0 — a copy of all mixer settings (undo).</summary>
        public static List<TrackMix> Snapshot() => (Current ?? new List<TrackMix>()).Select(m => m.Clone()).ToList();

        /// <summary>A spoken summary of one track's settings.</summary>
        public static string Describe(int track)
        {
            string T(string k) => LanguageManager.GetText(k, LanguageManager.CurrentLanguage ?? "en");
            var m = Get(track);
            if (m == null || m.IsDefault) return T("mix_d_default");
            var parts = new List<string>();
            if (m.Mute) parts.Add(T("mix_d_muted"));
            if (m.Solo) parts.Add(T("mix_d_solo"));
            if (Math.Abs(m.Volume - 100) > 0.01) parts.Add(string.Format(T("mix_d_volume"), Math.Round(m.Volume)));
            if (m.Low != 0 || m.Mid != 0 || m.High != 0) parts.Add(string.Format(T("mix_d_eq"), Math.Round(m.Low), Math.Round(m.Mid), Math.Round(m.High)));
            if (m.Compressor) parts.Add(T("mix_d_comp"));
            if (Math.Abs(m.Pan) > 0.5) parts.Add(string.Format(T(m.Pan < 0 ? "mix_d_pan_left" : "mix_d_pan_right"), Math.Round(Math.Abs(m.Pan))));
            if (m.LowCut) parts.Add(T("mix_d_lowcut"));
            if (m.NoiseReduction) parts.Add(T("mix_d_noise"));
            if (m.DeEsser) parts.Add(T("mix_d_deess"));
            if (m.Reverb > 0.5) parts.Add(string.Format(T("mix_d_reverb"), Math.Round(m.Reverb)));
            if (m.Limiter) parts.Add(T("mix_d_limiter"));
            if (m.HasPoints) parts.Add(string.Format(T("mix_d_points"), m.Points.Count));
            return string.Join(", ", parts);
        }
    }
}
