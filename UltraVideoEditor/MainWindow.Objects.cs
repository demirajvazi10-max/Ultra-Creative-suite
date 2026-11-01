using System;
using System.Linq;
using System.Windows;
using WInput = System.Windows.Input;

namespace UltraVideoEditor
{
    /// <summary>
    /// v2.0 — Follow an object (Ctrl+Alt+O): find an object in the selected clip (by words for
    /// the picture AI, or a part of the picture), follow it through the clip, and blur,
    /// pixelate, cover or light it; let a layer's mask follow it; or move a layer clip along
    /// with an object in the picture below it.
    /// </summary>
    public partial class MainWindow
    {
        private void FollowObject_Click(object sender, RoutedEventArgs e)
        {
            var item = SelectedClip();
            if (item == null) { LogMessage(L("msg_select_a_clip_first"), true); return; }
            if (item.IsAudio || item.Type == "Audio") { LogMessage(L("obj_video_only"), true); return; }
            if (RefuseLocked(item)) return;
            RecalculatePositions();
            double playhead = _mixPreviewActive ? currentPlaybackPosition : TimelinePosition;
            var below = TrackSet.IsLayer(item.TrackIndex) ? ClipBelow(item) : null;

            SaveState();
            var w = new TrackingWindow(this, item, below, playhead, _selectedResolution);
            if (w.ShowDialog() != true || !w.Changed)
            {
                DiscardLastUndo();
                LogMessage(L("obj_cancelled"), true);
                return;
            }
            UpdateTimelineDisplay();
            SelectClipInList(item);
            LogMessage(w.Summary, true);
            PlayBeep();
        }

        /// <summary>The picture clip under a layer clip: on the highest shown video track below it, at the layer's start (or middle).</summary>
        private TimelineItem ClipBelow(TimelineItem layer)
        {
            int key = TrackSet.SortKey(layer.TrackIndex);
            foreach (double t in new[] { layer.Start + 0.01, layer.Start + layer.Duration / 2 })
            {
                var c = timelineItems
                    .Where(i => !ReferenceEquals(i, layer) && TrackSet.IsVideo(i.TrackIndex) && !TrackSet.IsHidden(i.TrackIndex) &&
                                TrackSet.SortKey(i.TrackIndex) < key && (i.IsVideo || i.IsImage) && t >= i.Start && t < i.Start + i.Duration)
                    .OrderByDescending(i => TrackSet.SortKey(i.TrackIndex))
                    .FirstOrDefault();
                if (c != null) return c;
            }
            return null;
        }

        private static bool IsObjectKey(WInput.Key key, WInput.ModifierKeys mods) =>
            key == WInput.Key.O && mods == (WInput.ModifierKeys.Control | WInput.ModifierKeys.Alt);

        private bool HandleObjectKey(WInput.Key key, WInput.ModifierKeys mods)
        {
            if (!IsObjectKey(key, mods)) return false;
            FollowObject_Click(null, null);
            return true;
        }
    }
}
