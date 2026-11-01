using System;
using System.Linq;
using System.Text;
using System.Windows;
using WControls = System.Windows.Controls;
using WInput = System.Windows.Input;

namespace UltraVideoEditor
{
    /// <summary>
    /// v2.0 — unlimited tracks. Video and audio tracks can be added without limit (up to 60
    /// each), named, locked against changes, and layer tracks can be hidden from the picture.
    /// The "current track" is the one shown in the clip list (the track selector), or, when
    /// all tracks are shown, the track of the selected clip.
    ///
    /// Keys: Ctrl+Alt+Page Up / Page Down — previous / next track (the clip list shows it);
    /// Ctrl+Alt+N — new video track, Ctrl+Alt+Shift+N — new audio track; Ctrl+Alt+R — name the
    /// track; Ctrl+Alt+K — lock or unlock it; Ctrl+Alt+H — hide or show a layer track's picture;
    /// Ctrl+Alt+I — read all tracks. Ctrl+Up / Ctrl+Down move the selected clip to the track
    /// above / below (a new track is made past the last one).
    /// </summary>
    public partial class MainWindow
    {
        private bool _rebuildingTrackCombo;
        private string _trackComboSignature;

        // ── the track selector above the clip list ──────────────────────────────────

        /// <summary>Fills the track selector with every track (and "All tracks") when the tracks changed.</summary>
        private void RebuildTrackCombo()
        {
            if (cmbTrackSelector == null) return;
            string sig = string.Join("|", TrackSet.All().Select(t => TrackSet.Name(t) + (TrackSet.IsLocked(t) ? "L" : "") + (TrackSet.IsHidden(t) ? "H" : "")))
                         + "#" + currentTrackFilter + "#" + _currentLanguage;
            if (sig == _trackComboSignature) return;
            _trackComboSignature = sig;
            _rebuildingTrackCombo = true;
            try
            {
                cmbTrackSelector.Items.Clear();
                foreach (int t in TrackSet.All())
                    cmbTrackSelector.Items.Add(new WControls.ComboBoxItem { Content = TrackLabel(t), Tag = t.ToString() });
                cmbTrackSelector.Items.Add(new WControls.ComboBoxItem { Content = "📋 " + L("trk_all_tracks"), Tag = "-1" });
                SelectTrackInCombo(currentTrackFilter);
            }
            finally { _rebuildingTrackCombo = false; }
        }

        private static string TrackLabel(int t) =>
            (TrackSet.IsAudio(t) ? "🎵 " : "🎬 ") + TrackSet.Name(t) +
            (TrackSet.IsLocked(t) ? " 🔒" : "") + (TrackSet.IsHidden(t) ? " 🚫" : "");

        private void SelectTrackInCombo(int track)
        {
            if (cmbTrackSelector == null) return;
            bool was = _rebuildingTrackCombo;
            _rebuildingTrackCombo = true;
            try
            {
                foreach (var o in cmbTrackSelector.Items)
                    if (o is WControls.ComboBoxItem ci && ci.Tag?.ToString() == track.ToString()) { cmbTrackSelector.SelectedItem = ci; return; }
            }
            finally { _rebuildingTrackCombo = was; }
        }

        // ── the current track ────────────────────────────────────────────────────────

        private int CurrentTrack =>
            currentTrackFilter >= 0 ? currentTrackFilter : (SelectedClip()?.TrackIndex ?? 0);

        /// <summary>Where new files go: the shown track when it is of the right kind and not locked.</summary>
        private int TargetTrackFor(bool audio)
        {
            int t = currentTrackFilter;
            bool fits = t >= 0 && (audio ? TrackSet.IsAudio(t) : TrackSet.IsVideo(t));
            if (fits && !TrackSet.IsLocked(t)) return t;
            int fallback = audio ? 2 : 0;
            if (fits) LogMessage(LF("trk_locked_added_to", TrackSet.Name(t), TrackSet.Name(fallback)), true);
            return fallback;
        }

        /// <summary>True (and says why) when the clip is on a locked track.</summary>
        private bool RefuseLocked(TimelineItem item)
        {
            if (item == null || !TrackSet.IsLocked(item.TrackIndex)) return false;
            LogMessage(LF("trk_locked_refuse", TrackSet.Name(item.TrackIndex)), true);
            PlayBeep();
            return true;
        }

        /// <summary>Shows one track in the clip list and says what is on it.</summary>
        private void ShowTrack(int track, bool announce = true)
        {
            currentTrackFilter = track;
            UpdateTimelineDisplay();
            SelectTrackInCombo(track);
            if (track >= 0) ScrollGraphicTimelineTo(track);
            if (announce) LogMessage(DescribeTrack(track), true);
        }

        private string DescribeTrack(int t)
        {
            if (t < 0) return L("msg_showing_all_tracks");
            var clips = timelineItems.Where(i => i.TrackIndex == t).ToList();
            var sb = new StringBuilder(TrackSet.Name(t)).Append(". ");
            sb.Append(clips.Count == 0 ? L("trk_empty") : LF("trk_clips", clips.Count, FormatTime(clips.Max(i => i.End))));
            if (TrackSet.IsLocked(t)) sb.Append(' ').Append(L("trk_is_locked"));
            if (TrackSet.IsHidden(t)) sb.Append(' ').Append(L("trk_is_hidden"));
            if (TrackMixer.Current.Any(m => m.Track == t && !m.IsDefault))
                sb.Append(' ').Append(TrackMixer.Describe(t)).Append('.');
            return sb.ToString();
        }

        private void ScrollGraphicTimelineTo(int track)
        {
            try
            {
                if (graphicTimeline?.Parent is WControls.ScrollViewer sv)
                {
                    double top = graphicTimeline.TopOfTrack(track);
                    if (top < sv.VerticalOffset || top + GraphicTimeline.LaneHeight > sv.VerticalOffset + sv.ViewportHeight)
                        sv.ScrollToVerticalOffset(Math.Max(0, top - GraphicTimeline.RulerHeight - 4));
                }
            }
            catch { }
        }

        /// <summary>Ctrl+Alt+Page Up / Page Down: the previous / next track, after the last one "All tracks".</summary>
        private void StepTrack(int dir)
        {
            var all = TrackSet.All();
            all.Add(-1);
            int i = all.IndexOf(currentTrackFilter);
            if (i < 0) i = all.Count - 1;
            int n = Math.Clamp(i + dir, 0, all.Count - 1);
            if (n == i) { PlayBeep(); LogMessage(DescribeTrack(all[i]), true); return; }
            ShowTrack(all[n]);
        }

        // ── adding and removing tracks ──────────────────────────────────────────────

        private void AddTrack(bool audio)
        {
            int t = TrackSet.Add(audio);
            if (t < 0) { LogMessage(LF("trk_limit", audio ? TrackSet.MaxAudioTracks : TrackSet.MaxVideoTracks), true); PlayBeep(); return; }
            currentTrackFilter = t;
            UpdateTimelineDisplay();
            SelectTrackInCombo(t);
            ScrollGraphicTimelineTo(t);
            LogMessage(LF(audio ? "trk_added_audio" : "trk_added_video", TrackSet.Name(t)), true);
            PlayBeep();
        }

        private void RemoveTrack_Click(object sender, RoutedEventArgs e)
        {
            int t = CurrentTrack;
            string name = TrackSet.Name(t);
            if (TrackSet.Number(t) <= 2) { LogMessage(LF("trk_cannot_remove_base", name), true); PlayBeep(); return; }
            int count = timelineItems.Count(i => i.TrackIndex == t);
            if (count > 0) { LogMessage(LF("trk_cannot_remove_full", name, count), true); PlayBeep(); return; }
            SaveState();
            if (!TrackSet.RemoveEmpty(t, timelineItems)) { DiscardLastUndo(); LogMessage(LF("trk_cannot_remove_base", name), true); return; }
            currentTrackFilter = -1;
            UpdateTimelineDisplay();
            SelectTrackInCombo(-1);
            LogMessage(LF("trk_removed", name), true);
            PlayBeep();
        }

        // ── name, lock, hide ─────────────────────────────────────────────────────────

        private void RenameTrack_Click(object sender, RoutedEventArgs e)
        {
            int t = CurrentTrack;
            string now = TrackSet.OwnName(t);
            string answer = AskTrackName(TrackSet.BaseName(t), now);
            if (answer == null) { LogMessage(L("trk_rename_cancelled"), true); return; }
            TrackSet.Rename(t, answer);
            UpdateTimelineDisplay();
            LogMessage(string.IsNullOrWhiteSpace(answer) ? LF("trk_name_cleared", TrackSet.BaseName(t)) : LF("trk_renamed", TrackSet.Name(t)), true);
        }

        /// <summary>A small accessible window with one text field; null when cancelled.</summary>
        private string AskTrackName(string baseName, string current)
        {
            var w = new Window
            {
                Owner = this,
                Title = LF("trk_rename_title", baseName),
                Width = 460,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.NoResize
            };
            w.SetResourceReference(BackgroundProperty, "ControlBg");
            var panel = new WControls.StackPanel { Margin = new Thickness(18) };
            var box = new WControls.TextBox { Text = current ?? "", MaxLength = 40, Margin = new Thickness(0, 4, 0, 0) };
            box.SetResourceReference(WControls.Control.BackgroundProperty, "ControlBg");
            box.SetResourceReference(WControls.Control.ForegroundProperty, "TextPrimary");
            panel.Children.Add(UiKit.Labelled(LF("trk_rename_label", baseName), box));
            panel.Children.Add(UiKit.Text(L("trk_rename_hint"), 6));
            var buttons = new WControls.StackPanel { Orientation = WControls.Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            var ok = UiKit.Btn(L("mix_btn_ok"));
            ok.IsDefault = true;
            ok.Click += (s, e) => { w.DialogResult = true; };
            var cancel = UiKit.Btn(L("mix_btn_cancel"));
            cancel.IsCancel = true;
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            w.Content = panel;
            w.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
            return w.ShowDialog() == true ? box.Text : null;
        }

        private void ToggleTrackLock_Click(object sender, RoutedEventArgs e)
        {
            int t = CurrentTrack;
            bool locked = TrackSet.ToggleLock(t);
            UpdateTimelineDisplay();
            LogMessage(LF(locked ? "trk_locked" : "trk_unlocked", TrackSet.Name(t)), true);
            PlayBeep();
        }

        private void ToggleTrackHidden_Click(object sender, RoutedEventArgs e)
        {
            int t = CurrentTrack;
            if (!TrackSet.IsLayer(t))
            {
                LogMessage(LF(TrackSet.IsAudio(t) ? "trk_hide_audio" : "trk_hide_main", TrackSet.Name(t)), true);
                PlayBeep();
                return;
            }
            bool hidden = TrackSet.ToggleHidden(t);
            UpdateTimelineDisplay();
            LogMessage(LF(hidden ? "trk_hidden" : "trk_shown", TrackSet.Name(t)), true);
            PlayBeep();
        }

        private void ListTracks_Click(object sender, RoutedEventArgs e)
        {
            var sb = new StringBuilder(LF("trk_list_head", TrackSet.VideoCount, TrackSet.AudioCount));
            foreach (int t in TrackSet.All()) sb.Append(' ').Append(DescribeTrack(t));
            LogMessage(sb.ToString(), true);
        }

        private void PrevTrack_Click(object sender, RoutedEventArgs e) => StepTrack(-1);
        private void NextTrack_Click(object sender, RoutedEventArgs e) => StepTrack(+1);

        // ── moving a clip between tracks ─────────────────────────────────────────────

        /// <summary>
        /// Ctrl+Up / Ctrl+Down: the selected clip goes to the track above / below of the same
        /// kind (picture to video tracks, sound to audio tracks). "Below" past the last track
        /// makes a new one. Locked tracks are skipped over.
        /// </summary>
        private void MoveClipToNeighbourTrack(int dir)
        {
            var item = SelectedClip();
            if (item == null) { LogMessage(L("msg_select_a_clip_first"), true); return; }
            if (RefuseLocked(item)) return;
            bool audio = item.Type == "Audio" || item.IsAudio;
            var list = (audio ? TrackSet.AudioTracks() : TrackSet.VideoTracks()).ToList();
            int i = list.IndexOf(item.TrackIndex);
            if (i < 0) i = 0;
            int target = -1;
            for (int k = i + dir; k >= 0 && k < list.Count; k += dir)
                if (!TrackSet.IsLocked(list[k])) { target = list[k]; break; }
            bool created = false;
            if (target < 0 && dir > 0)
            {
                target = TrackSet.Add(audio);
                created = target >= 0;
            }
            if (target < 0)
            {
                LogMessage(L(dir < 0 ? "cant_move_higher" : "cant_move_lower"), true);
                PlayBeep();
                return;
            }
            SaveState();
            item.TrackIndex = target;
            if (currentTrackFilter >= 0) currentTrackFilter = target;   // the list follows the clip
            UpdateTimelineDisplay();
            SelectClipInList(item);
            ScrollGraphicTimelineTo(target);
            string msg = LF("trk_clip_moved", item.Name, TrackSet.Name(target));
            if (created) msg = LF("trk_new_track_made", TrackSet.Name(target)) + " " + msg;
            if (!audio && TrackSet.IsLayer(target) && TrackSet.IsHidden(target)) msg += " " + L("trk_is_hidden");
            LogMessage(msg, true);
            PlayBeep();
        }

        // ── keys ─────────────────────────────────────────────────────────────────────

        private static bool IsTrackKey(WInput.Key key, WInput.ModifierKeys mods)
        {
            const WInput.ModifierKeys C = WInput.ModifierKeys.Control, A = WInput.ModifierKeys.Alt, S = WInput.ModifierKeys.Shift;
            if (mods == (C | A))
                return key == WInput.Key.PageUp || key == WInput.Key.PageDown || key == WInput.Key.N || key == WInput.Key.R ||
                       key == WInput.Key.K || key == WInput.Key.H || key == WInput.Key.I;
            return mods == (C | A | S) && key == WInput.Key.N;
        }

        private bool HandleTrackKey(WInput.Key key, WInput.ModifierKeys mods)
        {
            const WInput.ModifierKeys C = WInput.ModifierKeys.Control, A = WInput.ModifierKeys.Alt, S = WInput.ModifierKeys.Shift;
            if (mods == (C | A | S) && key == WInput.Key.N) { AddTrack(audio: true); return true; }
            if (mods != (C | A)) return false;
            switch (key)
            {
                case WInput.Key.PageUp: StepTrack(-1); return true;
                case WInput.Key.PageDown: StepTrack(+1); return true;
                case WInput.Key.N: AddTrack(audio: false); return true;
                case WInput.Key.R: RenameTrack_Click(null, null); return true;
                case WInput.Key.K: ToggleTrackLock_Click(null, null); return true;
                case WInput.Key.H: ToggleTrackHidden_Click(null, null); return true;
                case WInput.Key.I: ListTracks_Click(null, null); return true;
            }
            return false;
        }
    }
}
