using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace UltraVideoEditor
{
    /// <summary>
    /// v2.0 — File → Import a timeline (XML, OTIO, EDL): a cut made in Premiere Pro, DaVinci
    /// Resolve, Final Cut or another editor comes in with its tracks, places and in-points.
    /// </summary>
    public partial class MainWindow
    {
        private void ImportTimeline_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = L("imp_filter") + "|*.xml;*.fcpxml7;*.otio;*.edl|Final Cut Pro 7 XML|*.xml|OpenTimelineIO|*.otio|EDL (CMX 3600)|*.edl",
                Title = L("imp_title")
            };
            if (dlg.ShowDialog() != true) return;

            ImportedTimeline t;
            try { t = TimelineImporter.Read(dlg.FileName); }
            catch (Exception ex)
            {
                AppLog.Error("Importing a timeline", ex);
                LogMessage(LF("imp_failed", Path.GetFileName(dlg.FileName), ex.Message), true);
                return;
            }
            var usable = t.Clips.Where(c => !c.Missing).ToList();
            if (usable.Count == 0)
            {
                LogMessage(t.Clips.Count == 0 ? L("imp_empty") : LF("imp_all_missing", string.Join(", ", t.MissingFiles.Take(10))), true);
                return;
            }

            bool replace = false;
            if (timelineItems.Count > 0)
            {
                var answer = System.Windows.MessageBox.Show(this, LF("imp_replace_q", t.Name, usable.Count), L("imp_title"),
                                             MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);
                if (answer == MessageBoxResult.Cancel) { LogMessage(L("imp_cancelled"), true); return; }
                replace = answer == MessageBoxResult.Yes;
            }

            SaveState();
            RecalculatePositions();
            double offset = 0;
            if (replace)
            {
                timelineItems.Clear();
                _multicams.Clear();
                TrackSet.Reset();
            }
            else if (timelineItems.Count > 0) offset = timelineItems.Max(i => i.Start + i.Duration);

            int clamped = 0;
            foreach (var c in usable)
            {
                int n = c.TrackNumber;
                if (n > TrackSet.MaxVideoTracks && !c.IsAudio) { n = TrackSet.MaxVideoTracks; clamped++; }
                if (n > TrackSet.MaxAudioTracks && c.IsAudio) { n = TrackSet.MaxAudioTracks; clamped++; }
                string ext = Path.GetExtension(c.Path).ToLowerInvariant();
                bool image = ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp" or ".tif" or ".tiff" or ".gif";
                var item = new TimelineItem
                {
                    Path = c.Path,
                    Name = string.IsNullOrWhiteSpace(c.Name) ? Path.GetFileName(c.Path) : c.Name,
                    Type = c.IsAudio ? "Audio" : image ? "Image" : "Video",
                    Duration = c.Duration,
                    SourceOffset = image ? 0 : c.SourceOffset,
                    Volume = c.IsAudio || c.WithSound ? 100 : 0,
                    TrackIndex = c.IsAudio ? TrackSet.AudioTrack(n) : TrackSet.VideoTrack(n),
                    UseFixedPosition = true,
                    FixedPosition = offset + c.Start,
                    VideoEffect = new VideoEffectData()
                };
                timelineItems.Add(item);
            }
            TrackSet.Sync(timelineItems);
            UpdateTimelineDisplay();

            string msg = LF("imp_done", t.Name, usable.Count, t.VideoTracks, t.AudioTracks,
                            FormatTimeDetailed(usable.Max(c => c.Start + c.Duration)),
                            replace || offset <= 0 ? L("imp_at_start") : LF("imp_at_end", FormatTimeDetailed(offset)));
            if (t.MissingFiles.Count > 0) msg += " " + LF("imp_missing", t.MissingFiles.Count, string.Join(", ", t.MissingFiles.Take(10)));
            if (t.Transitions > 0) msg += " " + LF("imp_transitions", t.Transitions);
            if (t.Skipped > 0) msg += " " + LF("imp_skipped", t.Skipped);
            if (clamped > 0) msg += " " + LF("imp_clamped", clamped);
            LogMessage(msg, true);
            PlayBeep();
        }
    }
}
