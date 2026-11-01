using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using WInput = System.Windows.Input;

namespace UltraVideoEditor
{
    /// <summary>
    /// v2.0 — mixer automation: a track's volume over time. Ctrl+Alt+Shift+V sets a volume
    /// point on the current track at the playhead (the volume moves smoothly from point to
    /// point, like a fader moved by hand); the Timeline menu reads them, removes the one at
    /// the playhead or all of them. The track mixer (Ctrl+Alt+M) is undone with Ctrl+Z
    /// together with the clips.
    /// </summary>
    public partial class MainWindow
    {
        private static readonly ConditionalWeakTable<List<TimelineItem>, List<TrackMix>> _mixOf =
            new ConditionalWeakTable<List<TimelineItem>, List<TrackMix>>();

        /// <summary>Keeps the mixer as it is now together with an undo snapshot of the clips.</summary>
        private static void RememberMix(List<TimelineItem> snapshot)
        {
            if (snapshot == null) return;
            _mixOf.AddOrUpdate(snapshot, TrackMixer.Snapshot());
        }

        /// <summary>Brings back the mixer that belongs to an undo snapshot (if one was kept).</summary>
        private static void RestoreMix(List<TimelineItem> snapshot)
        {
            if (snapshot != null && _mixOf.TryGetValue(snapshot, out var mix))
                TrackMixer.Current = mix.Select(m => m.Clone()).ToList();
        }

        private double PlayheadTimeline => _mixPreviewActive ? currentPlaybackPosition : TimelinePosition;

        private void TrackVolumePoint_Click(object sender, RoutedEventArgs e)
        {
            int track = CurrentTrack;
            double t = Math.Max(0, PlayheadTimeline);
            var dlg = new VolumeControl(Math.Round(TrackMixer.VolumeAt(track, t))) { Owner = this };
            if (dlg.ShowDialog() != true) { LogMessage(L("mixa_cancelled"), true); return; }
            SaveState();
            int n = TrackMixer.SetPoint(track, t, dlg.Volume);
            LogMessage(LF("mixa_point_set", TrackSet.Name(track), FormatTimeDetailed(t), Math.Round(dlg.Volume), n), true);
            PlayBeep();
        }

        private void ReadTrackVolume_Click(object sender, RoutedEventArgs e)
        {
            int track = CurrentTrack;
            var pts = TrackMixer.PointsOf(track);
            if (pts.Count == 0) { LogMessage(LF("mixa_none", TrackSet.Name(track), Math.Round(TrackMixer.VolumeAt(track, 0))), true); return; }
            string list = string.Join(", ", pts.Take(30).Select(p => LF("mixa_point", FormatTimeDetailed(p.Time), Math.Round(p.Volume))));
            LogMessage(LF("mixa_list", TrackSet.Name(track), pts.Count, list), true);
        }

        private void RemoveTrackVolumePoint_Click(object sender, RoutedEventArgs e)
        {
            int track = CurrentTrack;
            double t = PlayheadTimeline;
            SaveState();
            if (!TrackMixer.RemovePointNear(track, t)) { DiscardLastUndo(); LogMessage(LF("mixa_no_point_here", TrackSet.Name(track)), true); PlayBeep(); return; }
            LogMessage(LF("mixa_point_removed", TrackSet.Name(track), TrackMixer.PointsOf(track).Count), true);
            PlayBeep();
        }

        private void ClearTrackVolume_Click(object sender, RoutedEventArgs e)
        {
            int track = CurrentTrack;
            if (TrackMixer.PointsOf(track).Count == 0) { LogMessage(LF("mixa_none", TrackSet.Name(track), Math.Round(TrackMixer.VolumeAt(track, 0))), true); return; }
            SaveState();
            TrackMixer.ClearPoints(track);
            LogMessage(LF("mixa_cleared", TrackSet.Name(track)), true);
            PlayBeep();
        }

        private static bool IsMixerKey(WInput.Key key, WInput.ModifierKeys mods) =>
            key == WInput.Key.V && mods == (WInput.ModifierKeys.Control | WInput.ModifierKeys.Alt | WInput.ModifierKeys.Shift);

        private bool HandleMixerKey(WInput.Key key, WInput.ModifierKeys mods)
        {
            if (!IsMixerKey(key, mods)) return false;
            TrackVolumePoint_Click(null, null);
            return true;
        }
    }
}
