using System.Collections.Generic;

namespace UltraVideoEditor
{
    public class ProjectData
    {
        public List<TimelineItem> TimelineItems { get; set; } = new List<TimelineItem>();
        public List<SubtitleItem> Subtitles { get; set; } = new List<SubtitleItem>();
        public List<double> Markers { get; set; } = new List<double>();
        public List<TransitionEffect> Transitions { get; set; } = new List<TransitionEffect>();
        /// <summary>v2.0 — the track mixer (volume, mute, solo, EQ, compressor per track).</summary>
        public List<TrackMix> Tracks { get; set; } = new List<TrackMix>();
        /// <summary>v2.0 — how many tracks there are, their names, locks and hidden layers.</summary>
        public TrackSetData TrackLayout { get; set; }
        /// <summary>v2.0 — multicam clips (their cameras and sync).</summary>
        public List<MulticamData> Multicams { get; set; }
        public int CurrentTrackFilter { get; set; } = -1;
        public double ZoomLevel { get; set; } = 1.0;
        public string ProjectVersion { get; set; } = "4.3";
    }
}