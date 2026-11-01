using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace UltraVideoEditor
{
    /// <summary>
    /// v2.0 — multicam editing. Cameras of one scene are put in sync (by their sound or by
    /// their place) and become one multicam clip: the master's sound on an audio track and the
    /// picture as camera parts on a video track. While it plays, the number keys 1 … 9 cut to
    /// that camera at that moment; when it stands still, a number key changes the camera of the
    /// part under the playhead. Every cut is an ordinary clip, so it can be trimmed, rolled or
    /// undone like any other.
    /// </summary>
    public partial class MainWindow
    {
        private List<MulticamData> _multicams = new List<MulticamData>();

        private async void MakeMulticam_Click(object sender, RoutedEventArgs e)
        {
            RecalculatePositions();
            var videos = timelineItems.Where(i => i.IsVideo && i.Type != "Audio" && !i.IsImage && !string.IsNullOrEmpty(i.Path)
                                                  && System.IO.File.Exists(i.Path) && string.IsNullOrEmpty(i.MulticamId))
                                      .OrderBy(i => TrackSet.SortKey(i.TrackIndex)).ThenBy(i => i.Start).ToList();
            if (videos.Count < 2) { LogMessage(L("mc_need_videos"), true); return; }
            var selected = nativeListView?.SelectedItems.Cast<System.Windows.Forms.ListViewItem>().Select(li => li.Tag as TimelineItem)
                               .Where(i => i != null && videos.Contains(i)).ToList() ?? new List<TimelineItem>();
            var w = new MulticamWindow(this, videos, selected);
            if (w.ShowDialog() != true || w.Cameras == null) { LogMessage(L("mc_cancelled"), true); return; }
            if (w.Cameras.Any(c => TrackSet.IsLocked(c.TrackIndex))) { LogMessage(LF("trk_locked_refuse", TrackSet.Name(w.Cameras.First(c => TrackSet.IsLocked(c.TrackIndex)).TrackIndex)), true); return; }

            SaveState();
            var master = w.Master;
            var mc = new MulticamData
            {
                Name = master.Name ?? "Multicam",
                Start = master.Start,
                Length = master.Duration,
                MasterOffset = master.SourceOffset
            };
            for (int k = 0; k < w.Cameras.Count; k++)
            {
                var c = w.Cameras[k];
                double len = await GetMediaDurationWithTimeoutAsync(c.Path, TimeSpan.FromSeconds(20));
                if (len <= 0.1) len = c.SourceOffset + c.Duration;
                mc.Angles.Add(new MulticamAngle { Name = c.Name ?? $"Cam {k + 1}", Path = c.Path, Delay = w.Delays[k], Length = len });
            }

            // the cameras leave the timeline (they live on in the multicam clip)
            foreach (var c in w.Cameras) timelineItems.Remove(c);
            RecalculatePositions();

            // picture: Video 1 when it is free there, otherwise a new video track
            double s = mc.Start, en = mc.Start + mc.Length;
            bool Free(int track) => !timelineItems.Any(i => i.TrackIndex == track && i.Start < en - 0.01 && i.Start + i.Duration > s + 0.01);
            int picTrack = Free(0) ? 0 : TrackSet.VideoTracks().Skip(1).FirstOrDefault(Free);
            if (picTrack <= 0 && !Free(0)) picTrack = TrackSet.Add(audio: false);
            if (picTrack < 0) picTrack = 0;
            mc.Track = picTrack;
            int k0 = w.Cameras.IndexOf(master) + 1;
            timelineItems.Add(MakeSegment(mc, k0, s, mc.Length));

            // sound: the master's, on a free audio track
            int sndTrack = TrackSet.AudioTracks().FirstOrDefault(Free);
            if (sndTrack == 0) sndTrack = TrackSet.Add(audio: true);
            timelineItems.Add(new TimelineItem
            {
                Path = master.Path, Name = LF("mc_sound_name", master.Name ?? ""), Type = "Audio", Volume = 100,
                Duration = mc.Length, SourceOffset = mc.MasterOffset, TrackIndex = sndTrack,
                UseFixedPosition = true, FixedPosition = s, MulticamId = mc.Id, VideoEffect = new VideoEffectData()
            });
            _multicams.Add(mc);
            UpdateTimelineDisplay();
            LogMessage(LF("mc_made", mc.Angles.Count, TrackSet.Name(picTrack), TrackSet.Name(sndTrack), FormatTimeDetailed(mc.Length)) + " " + w.Report + " " + L("mc_how"), true);
            PlayBeep();
        }

        private TimelineItem MakeSegment(MulticamData mc, int k, double start, double length)
        {
            var a = mc.Angles[k - 1];
            double src = mc.SourceAt(k, start);
            return new TimelineItem
            {
                Path = a.Path, Name = LF("mc_cam", k, a.Name), Type = "Video", Volume = 0,
                Duration = length, SourceOffset = double.IsNaN(src) ? 0 : src, TrackIndex = mc.Track,
                UseFixedPosition = true, FixedPosition = start, MulticamId = mc.Id, MulticamAngle = k,
                VideoEffect = new VideoEffectData()
            };
        }

        private MulticamData MulticamAt(double t) =>
            _multicams.FirstOrDefault(m => t >= m.Start - 0.001 && t < m.Start + m.Length &&
                                           timelineItems.Any(i => i.MulticamId == m.Id && i.MulticamAngle > 0));

        private static int DigitOf(Key key) =>
            key >= Key.D1 && key <= Key.D9 ? key - Key.D0 : key >= Key.NumPad1 && key <= Key.NumPad9 ? key - Key.NumPad0 : 0;

        /// <summary>1 … 9 on a multicam clip: cut to that camera (playing) or change the camera of this part (standing still).</summary>
        private bool HandleMulticamDigit(int k)
        {
            double t = _mixPreviewActive ? currentPlaybackPosition : TimelinePosition;
            var mc = MulticamAt(t);
            if (mc == null) return false;
            if (k > mc.Angles.Count) { LogMessage(LF("mc_no_cam", k, mc.Angles.Count), true); PlayBeep(); return true; }
            var seg = timelineItems.Where(i => i.MulticamId == mc.Id && i.MulticamAngle > 0 && t >= i.Start - 0.001 && t < i.Start + i.Duration)
                                   .OrderBy(i => i.Start).FirstOrDefault();
            if (seg == null) return false;
            if (RefuseLocked(seg)) return true;
            bool playing = isPlaying || (LiveActive && _live.Playing);
            double from = playing && t - seg.Start > 0.1 && seg.Start + seg.Duration - t > 0.1 ? t : seg.Start;
            if (double.IsNaN(mc.SourceAt(k, from)) || double.IsNaN(mc.SourceAt(k, Math.Min(seg.Start + seg.Duration, mc.Start + mc.Length) - 0.06)))
            { LogMessage(LF("mc_cam_missing", k, mc.Angles[k - 1].Name), true); PlayBeep(); return true; }
            if (seg.MulticamAngle == k) { LogMessage(LF("mc_already", k), true); return true; }

            SaveState();
            if (from > seg.Start + 0.001)
            {
                double end = seg.Start + seg.Duration;
                seg.Duration = from - seg.Start;
                timelineItems.Insert(timelineItems.IndexOf(seg) + 1, MakeSegment(mc, k, from, end - from));
            }
            else
            {
                var a = mc.Angles[k - 1];
                seg.Path = a.Path; seg.Name = LF("mc_cam", k, a.Name); seg.MulticamAngle = k;
                seg.SourceOffset = mc.SourceAt(k, seg.Start);
            }
            MergeSameCamera(mc);
            UpdateTimelineDisplay();
            LogMessage(LF("mc_cut", k, mc.Angles[k - 1].Name, FormatTimeDetailed(from)), !playing);
            if (playing) PlayBeep();
            return true;
        }

        /// <summary>Neighbouring parts of the same camera become one part again.</summary>
        private void MergeSameCamera(MulticamData mc)
        {
            var segs = timelineItems.Where(i => i.MulticamId == mc.Id && i.MulticamAngle > 0).OrderBy(i => i.Start).ToList();
            for (int i = segs.Count - 1; i > 0; i--)
            {
                var a = segs[i - 1]; var b = segs[i];
                if (a.MulticamAngle == b.MulticamAngle && Math.Abs(a.Start + a.Duration - b.Start) < 0.01)
                {
                    a.Duration += b.Duration;
                    timelineItems.Remove(b);
                }
            }
        }

        private static bool IsMulticamKey(Key key, ModifierKeys mods) =>
            key == Key.M && mods == (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift);

        private bool HandleMulticamKey(Key key, ModifierKeys mods)
        {
            if (!IsMulticamKey(key, mods)) return false;
            MakeMulticam_Click(null, null);
            return true;
        }

        private void ReadMulticamCuts_Click(object sender, RoutedEventArgs e)
        {
            double t = _mixPreviewActive ? currentPlaybackPosition : TimelinePosition;
            var mc = MulticamAt(t) ?? _multicams.FirstOrDefault(m => timelineItems.Any(i => i.MulticamId == m.Id && i.MulticamAngle > 0));
            if (mc == null) { LogMessage(L("mc_none"), true); return; }
            RecalculatePositions();
            var segs = timelineItems.Where(i => i.MulticamId == mc.Id && i.MulticamAngle > 0).OrderBy(i => i.Start).ToList();
            string cams = string.Join(", ", mc.Angles.Select((a, i) => LF("mc_cam", i + 1, a.Name)));
            string cuts = string.Join(", ", segs.Take(40).Select(sg => LF("mc_cut_item", FormatTimeDetailed(sg.Start), sg.MulticamAngle)));
            LogMessage(LF("mc_cuts", mc.Name, segs.Count, cams, cuts), true);
        }
    }
}
