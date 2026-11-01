using System;
using System.Linq;
using System.Windows;
using WControls = System.Windows.Controls;

namespace UltraVideoEditor
{
    /// <summary>
    /// v2.0 — editing, last part: keyframe curves (the way from one keyframe to the next).
    /// </summary>
    public partial class MainWindow
    {
        private string _easingLang;

        // ── compound clips ───────────────────────────────────────────────────────────

        private bool _compoundBusy;

        /// <summary>
        /// Ctrl+Alt+G: the selected clips become one compound clip — rendered once (picture,
        /// layers, titles, colour and sound, no fades) and put in their place; the clips stay
        /// inside it, so Ctrl+Alt+Shift+G gives them back for changes.
        /// </summary>
        private async void MakeCompound_Click(object sender, RoutedEventArgs e)
        {
            if (_compoundBusy) return;
            var sel = nativeListView?.SelectedItems.Cast<System.Windows.Forms.ListViewItem>().Select(li => li.Tag as TimelineItem)
                          .Where(i => i != null && !string.IsNullOrEmpty(i.Path)).ToList() ?? new System.Collections.Generic.List<TimelineItem>();
            if (sel.Count < 2) { LogMessage(L("cmp_select_two"), true); return; }
            var locked = sel.FirstOrDefault(i => TrackSet.IsLocked(i.TrackIndex));
            if (locked != null) { RefuseLocked(locked); return; }
            if (!sel.Any(i => !(i.Type == "Audio" || i.IsAudio))) { LogMessage(L("cmp_need_picture"), true); return; }
            RecalculatePositions();
            double s = sel.Min(i => i.Start), en = sel.Max(i => i.Start + i.Duration);

            string folder = !string.IsNullOrEmpty(currentProjectFolder) ? currentProjectFolder
                          : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Ultra Video Editor");
            folder = System.IO.Path.Combine(folder, "Compound clips");
            System.IO.Directory.CreateDirectory(folder);
            int n = 1;
            string file;
            do file = System.IO.Path.Combine(folder, $"Compound {n++}.mp4"); while (System.IO.File.Exists(file));

            var parts = sel.Select(i =>
            {
                var c = DeepCloneTimelineItem(i);
                c.Start = i.Start - s; c.End = c.Start + c.Duration;
                c.UseFixedPosition = true; c.FixedPosition = c.Start;
                return c;
            }).OrderBy(i => i.Start).ToList();

            _compoundBusy = true;
            LogMessage(LF("cmp_making", sel.Count, FormatTimeDetailed(en - s)), true);
            int lastSaid = -25;
            var progress = new Progress<int>(p => { if (p >= lastSaid + 25) { lastSaid = p - p % 25; LogMessage(LF("cmp_progress", lastSaid), true); } });
            try
            {
                var settings = new ExportSettingsData
                {
                    Format = "MP4", VideoCodec = "H264", Quality = "High",
                    FrameRate = currentExportSettings?.FrameRate ?? "Auto", FadeInOut = false, WarmLook = false
                };
                await _renderEngine.RenderSimpleAsync(parts, file, "mp4", progress, new System.Collections.Generic.List<SubtitleItem>(), settings,
                                                      System.Threading.CancellationToken.None, useGPUAcceleration, _selectedResolution ?? "1920x1080",
                                                      false, false, null);
                if (!System.IO.File.Exists(file)) { LogMessage(L("cmp_failed"), true); return; }

                SaveState();
                int track = sel.Where(i => !(i.Type == "Audio" || i.IsAudio)).Select(i => i.TrackIndex).OrderBy(TrackSet.SortKey).First();
                foreach (var i in sel) timelineItems.Remove(i);
                var compound = new TimelineItem
                {
                    Path = file, Name = LF("cmp_name", sel.Count), Type = "Video", Volume = 100,
                    Duration = en - s, TrackIndex = track, UseFixedPosition = true, FixedPosition = s,
                    VideoEffect = new VideoEffectData(), CompoundItems = parts
                };
                timelineItems.Add(compound);
                UpdateTimelineDisplay();
                SelectClipInList(compound);
                LogMessage(LF("cmp_made", sel.Count, TrackSet.Name(track), FormatTimeDetailed(compound.Duration)), true);
                PlayBeep();
            }
            catch (Exception ex) { AppLog.Error("Compound clip", ex); LogMessage(LF("generic_error", ex.Message), true); }
            finally { _compoundBusy = false; }
        }

        /// <summary>Ctrl+Alt+Shift+G: a compound clip gives back the clips it was made of, at their places.</summary>
        private void BreakCompound_Click(object sender, RoutedEventArgs e)
        {
            var item = SelectedClip();
            if (item?.CompoundItems == null || item.CompoundItems.Count == 0) { LogMessage(L("cmp_not_compound"), true); return; }
            if (RefuseLocked(item)) return;
            RecalculatePositions();
            SaveState();
            double s = item.Start;
            timelineItems.Remove(item);
            foreach (var p in item.CompoundItems)
            {
                var c = DeepCloneTimelineItem(p);
                c.UseFixedPosition = true;
                c.FixedPosition = s + p.FixedPosition;
                timelineItems.Add(c);
                TrackSet.Sync(new[] { c });
            }
            UpdateTimelineDisplay();
            LogMessage(LF("cmp_broken", item.CompoundItems.Count), true);
            PlayBeep();
        }

        // ── speed ramps ──────────────────────────────────────────────────────────────

        private void SpeedRamp_Click(object sender, RoutedEventArgs e)
        {
            var item = SelectedClip();
            if (item == null) { LogMessage(L("msg_select_a_clip_first"), true); return; }
            if (!item.IsVideo || item.Type == "Audio" || item.IsImage) { LogMessage(L("sr_video_only"), true); return; }
            if (RefuseLocked(item)) return;
            RecalculatePositions();
            double t = _mixPreviewActive ? currentPlaybackPosition : TimelinePosition;
            SaveState();
            var w = new SpeedRampWindow(this, item, t - item.Start);
            if (w.ShowDialog() != true) { DiscardLastUndo(); LogMessage(L("sr_cancelled"), true); return; }
            UpdateTimelineDisplay();
            LogMessage(w.Summary, true);
            PlayBeep();
        }

        private const System.Windows.Input.ModifierKeys CA = System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Alt;
        private const System.Windows.Input.ModifierKeys CAS = CA | System.Windows.Input.ModifierKeys.Shift;

        private static bool IsEditingKey2(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys mods) =>
            (key == System.Windows.Input.Key.S && mods == CAS) ||
            (key == System.Windows.Input.Key.I && mods == CAS) ||
            (key == System.Windows.Input.Key.Space && mods == CAS) ||
            (key == System.Windows.Input.Key.G && (mods == CA || mods == CAS));

        private bool HandleEditingKey2(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys mods)
        {
            if (!IsEditingKey2(key, mods)) return false;
            if (key == System.Windows.Input.Key.S) SpeedRamp_Click(null, null);
            else if (key == System.Windows.Input.Key.I) ImportTimeline_Click(null, null);
            else if (key == System.Windows.Input.Key.Space) CycleLiveSize_Click(null, null);
            else if (mods == CA) MakeCompound_Click(null, null);
            else BreakCompound_Click(null, null);
            return true;
        }

        /// <summary>Fills the "way to the next keyframe" list (again after a language change).</summary>
        private void EnsureEasingItems()
        {
            if (cmbKeyEasing == null) return;
            if (_easingLang == _currentLanguage && cmbKeyEasing.Items.Count == AnimationKeyframe.Easings.Length) return;
            string keep = ChosenEasingRaw();
            _easingLang = _currentLanguage;
            cmbKeyEasing.Items.Clear();
            foreach (string e in AnimationKeyframe.Easings)
                cmbKeyEasing.Items.Add(new WControls.ComboBoxItem { Content = L("kf_ease_" + e), Tag = e });
            System.Windows.Automation.AutomationProperties.SetName(cmbKeyEasing, L("kf_lbl_easing"));
            SelectEasing(keep ?? "Linear");
        }

        private string ChosenEasingRaw() => (cmbKeyEasing?.SelectedItem as WControls.ComboBoxItem)?.Tag as string;

        private string ChosenEasing
        {
            get { EnsureEasingItems(); return ChosenEasingRaw() ?? "Linear"; }
        }

        private void SelectEasing(string easing)
        {
            if (cmbKeyEasing == null) return;
            if (cmbKeyEasing.Items.Count == 0) EnsureEasingItems();
            foreach (var o in cmbKeyEasing.Items)
                if (o is WControls.ComboBoxItem ci && (ci.Tag as string) == (easing ?? "Linear")) { cmbKeyEasing.SelectedItem = ci; return; }
            if (cmbKeyEasing.Items.Count > 0) cmbKeyEasing.SelectedIndex = 0;
        }
    }
}
