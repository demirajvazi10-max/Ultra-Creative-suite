using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WpfSaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace UltraVideoEditor
{
    /// <summary>
    /// v2.0 — the professional editing tools:
    ///   Ctrl+Alt+L      Layer and colour (position, size, opacity, green/blue screen, LUT)
    ///   Shift+Delete    Ripple delete (later fixed clips and their layers move back too)
    ///   Ctrl+Alt+U      Detach the sound of a video clip (for J and L cuts)
    ///   Ctrl+[ / Ctrl+] Start the clip half a second earlier / end it half a second later
    ///                   (with Shift: the other way) — J and L cuts
    ///   F4              Where am I: what is under the playhead on every track, and the next cut
    ///   File menu       Export the timeline for DaVinci Resolve / Premiere (OTIO, EDL)
    ///   View menu       Preview copies (proxies) of large clips
    /// </summary>
    public partial class MainWindow
    {
        private static bool IsLayerTrack(int track) => TrackSet.IsLayer(track);

        // ── Layer and colour ─────────────────────────────────────────────────────────

        private void OpenLayerSettings_Click(object sender, RoutedEventArgs e)
        {
            var item = SelectedClip();
            if (item == null) { LogMessage(L("msg_select_a_clip_first"), true); return; }
            if (item.Type == "Audio" || item.IsAudio) { LogMessage(L("lay_need_picture"), true); return; }

            SaveState();
            var w = new LayerSettingsWindow(this, item);
            if (w.ShowDialog() == true)
            {
                if (w.MoveToLayerTrack && !IsLayerTrack(item.TrackIndex))
                {
                    item.TrackIndex = 1;
                    // a clip moved onto the layer track stays where it was in time
                    item.UseFixedPosition = true;
                    item.FixedPosition = item.Start;
                }
                UpdateTimelineDisplay();
                LogMessage(LF("lay_saved", item.Name, LayerSettingsWindow.Describe(item)), true);
                PlayBeep();
            }
            else LogMessage(L("lay_cancelled"), true);
        }

        // ── Ripple delete ────────────────────────────────────────────────────────────

        private void RippleDelete_Click(object sender, RoutedEventArgs e)
        {
            var item = SelectedClip();
            if (item == null) { LogMessage(L("msg_select_a_clip_first"), true); return; }
            if (RefuseLocked(item)) return;   // v2.0
            SaveState();
            RecalculatePositions();
            double gap = Math.Max(0, item.End - item.Start);
            double end = item.End;
            // clips placed at a fixed time after it on the same track move back; when a clip of
            // Video 1 is removed, the layers above that picture move with it
            var moved = timelineItems.Where(i => !ReferenceEquals(i, item) && i.UseFixedPosition && i.Start >= end - 0.001 &&
                                                 (i.TrackIndex == item.TrackIndex || (item.TrackIndex == 0 && IsLayerTrack(i.TrackIndex))))
                                     .ToList();
            foreach (var m in moved) m.FixedPosition = Math.Max(0, m.FixedPosition - gap);
            timelineItems.Remove(item);
            UpdateTimelineDisplay();
            LogMessage(LF("rip_done", item.Name, FormatTimeDetailed(gap), moved.Count), true);
            PlayBeep();
        }

        // ── Detach the sound (J / L cuts) ───────────────────────────────────────────

        private async void DetachAudio_Click(object sender, RoutedEventArgs e)
        {
            var item = SelectedClip();
            if (item == null) { LogMessage(L("msg_select_a_clip_first"), true); return; }
            if (RefuseLocked(item)) return;   // v2.0
            if (!(item.IsVideo && item.Type != "Audio") || string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
            { LogMessage(L("det_video_only"), true); return; }
            if (item.Volume <= 0) { LogMessage(L("det_already"), true); return; }
            if (!await ExportHelpers.HasAudioAsync(item.Path, CancellationToken.None)) { LogMessage(L("det_no_sound"), true); return; }

            SaveState();
            RecalculatePositions();
            var sound = new TimelineItem
            {
                Path = item.Path,
                RelativePath = item.RelativePath,
                Name = LF("det_name", item.Name),
                Type = "Audio",
                TrackIndex = 3,
                UseFixedPosition = true,
                FixedPosition = item.Start,
                Duration = item.Duration,
                SourceOffset = item.SourceOffset,
                Volume = item.Volume,
                VideoEffect = new VideoEffectData(),
                VolumePoints = item.VolumePoints?.Select(p => new VolumePoint { Time = p.Time, Volume = p.Volume }).ToList(),
                AudioFx = item.AudioFx
            };
            item.Volume = 0;   // the picture is now silent; its sound lives on Audio 2
            int at = timelineItems.IndexOf(item);
            timelineItems.Insert(at + 1, sound);
            UpdateTimelineDisplay();
            LogMessage(LF("det_done", item.Name), true);
            PlayBeep();
        }

        /// <summary>
        /// Ctrl+[ / Ctrl+]: the clip starts half a second earlier / ends half a second later
        /// (from more of its file). With Shift: starts later / ends earlier.
        /// </summary>
        private void NudgeClipEdge(bool startEdge, bool extend)
        {
            var item = SelectedClip();
            if (item == null) { LogMessage(L("msg_select_a_clip_first"), true); return; }
            if (RefuseLocked(item)) return;   // v2.0
            const double step = 0.5;
            double speed = ClipSpeed(item);
            SaveState();
            RecalculatePositions();
            if (startEdge)
            {
                if (extend)
                {
                    double room = item.IsImage ? step : Math.Min(step, item.SourceOffset / Math.Max(0.01, speed));
                    if (item.UseFixedPosition) room = Math.Min(room, item.FixedPosition);
                    if (room < 0.01) { LogMessage(L("edge_no_room"), true); PlayBeep(); return; }
                    if (!item.IsImage) item.SourceOffset = Math.Max(0, item.SourceOffset - room * speed);
                    if (item.UseFixedPosition) item.FixedPosition -= room;
                    item.Duration += room;
                    ShiftClipContent(item, -room);
                }
                else
                {
                    double cut = Math.Min(step, item.Duration - 0.2);
                    if (cut < 0.01) { LogMessage(L("edge_too_short"), true); PlayBeep(); return; }
                    if (!item.IsImage) item.SourceOffset += cut * speed;
                    if (item.UseFixedPosition) item.FixedPosition += cut;
                    item.Duration -= cut;
                    ShiftClipContent(item, cut);
                }
            }
            else
            {
                if (extend) item.Duration += step;
                else
                {
                    double cut = Math.Min(step, item.Duration - 0.2);
                    if (cut < 0.01) { LogMessage(L("edge_too_short"), true); PlayBeep(); return; }
                    item.Duration -= cut;
                }
            }
            UpdateTimelineDisplay();
            RecalculatePositions();
            LogMessage(LF("edge_done", item.Name, FormatTimeDetailed(item.Start), FormatTimeDetailed(item.End), FormatTimeDetailed(item.Duration)), true);
        }

        private void EdgeStartEarlier_Click(object sender, RoutedEventArgs e) => NudgeClipEdge(startEdge: true, extend: true);
        private void EdgeStartLater_Click(object sender, RoutedEventArgs e) => NudgeClipEdge(startEdge: true, extend: false);
        private void EdgeEndLater_Click(object sender, RoutedEventArgs e) => NudgeClipEdge(startEdge: false, extend: true);
        private void EdgeEndEarlier_Click(object sender, RoutedEventArgs e) => NudgeClipEdge(startEdge: false, extend: false);

        // ── Where am I (F4) ──────────────────────────────────────────────────────────

        private void WhereAmI_Click(object sender, RoutedEventArgs e)
        {
            RecalculatePositions();
            double t = _mixPreviewActive ? currentPlaybackPosition : TimelinePosition;
            var sb = new StringBuilder(LF("where_pos", FormatTimeDetailed(t)));
            foreach (int track in timelineItems.Select(i => i.TrackIndex).Distinct().OrderBy(TrackSet.SortKey))
            {
                var here = timelineItems.Where(i => i.TrackIndex == track && t >= i.Start - 0.001 && t < i.End - 0.001)
                                        .OrderByDescending(i => i.Start).FirstOrDefault();
                string trackName = TimelineExporter.TrackName(track);
                if (TrackSet.IsHidden(track)) trackName += " " + L("trk_hidden_note");   // v2.0
                if (TrackMixer.PointsOf(track).Count > 0) trackName += " " + LF("mixa_where", Math.Round(TrackMixer.VolumeAt(track, t)));   // v2.0: automation
                if (here == null)
                {
                    var next = timelineItems.Where(i => i.TrackIndex == track && i.Start > t).OrderBy(i => i.Start).FirstOrDefault();
                    sb.Append(' ').Append(next == null
                        ? LF("where_empty", trackName)
                        : LF("where_empty_next", trackName, next.Name, FormatTimeDetailed(next.Start - t)));
                    continue;
                }
                double into = t - here.Start, left = here.End - t;
                sb.Append(' ').Append(LF("where_clip", trackName, here.Name, FormatTimeDetailed(into), FormatTimeDetailed(left)));
                if (IsLayerTrack(track) && here.Layer != null) sb.Append(" (").Append(LayerSettingsWindow.Describe(here)).Append(')');
                if (here.Tracking?.HasEffect == true)   // v2.0: a followed object
                    sb.Append(' ').Append(LF("obj_where", L("obj_fx_" + here.Tracking.Effect), here.Tracking.Target ?? ""));
                if (track == 0)
                {
                    var d = here.DescriptionAt(into);
                    if (d != null && !string.IsNullOrWhiteSpace(d.Text)) sb.Append(' ').Append(LF("where_picture", d.Text));
                }
            }
            LogMessage(sb.ToString(), true);
        }

        // ── Export for other editors (OTIO / EDL) ───────────────────────────────────

        private List<TlClip> ClipsForExchange()
        {
            RecalculatePositions();
            return timelineItems.Where(i => !string.IsNullOrEmpty(i.Path)).Select(i => new TlClip
            {
                Name = i.Name ?? "",
                Path = i.Path,
                Start = i.Start,
                Duration = i.Duration,
                SourceOffset = i.SourceOffset,
                Track = i.TrackIndex,
                IsAudio = i.Type == "Audio" || i.IsAudio,
                IsImage = i.IsImage,
                WithSound = i.IsVideo && i.Type != "Audio" && i.Volume > 0
            }).ToList();
        }

        private async Task<double> ExchangeFpsAsync()
        {
            string chosen = currentExportSettings?.FrameRate;
            if (!string.IsNullOrEmpty(chosen) && chosen != "Auto") return LayerCompositor.ParseRate(chosen);
            var first = timelineItems.FirstOrDefault(i => i.IsVideo && i.Type != "Audio" && File.Exists(i.Path));
            if (first != null)
            {
                var probe = new LayerCompositor(AppPaths.FFprobe, (a, c) => Task.FromResult(false), null);
                string r = await probe.ProbeFrameRateAsync(first.Path, CancellationToken.None);
                double v = LayerCompositor.ParseRate(r);
                if (v > 1) return v;
            }
            return 30;
        }

        private async void ExportOtio_Click(object sender, RoutedEventArgs e) => await ExportExchangeAsync(edl: false);
        private async void ExportEdl_Click(object sender, RoutedEventArgs e) => await ExportExchangeAsync(edl: true);
        private async void ExportXml_Click(object sender, RoutedEventArgs e) => await ExportExchangeAsync(edl: false, xml: true);   // v2.0

        private async Task ExportExchangeAsync(bool edl, bool xml = false)
        {
            if (timelineItems.Count == 0) { LogMessage(L("msg_no_clips_to_render"), true); return; }
            string name = string.IsNullOrEmpty(_currentProjectPath) ? "Ultra project" : Path.GetFileNameWithoutExtension(_currentProjectPath);
            var dlg = new WpfSaveFileDialog
            {
                Filter = xml ? "Final Cut Pro 7 XML (Premiere, Resolve)|*.xml" : edl ? "EDL (CMX 3600)|*.edl" : "OpenTimelineIO|*.otio",
                DefaultExt = xml ? "xml" : edl ? "edl" : "otio",
                FileName = name + (xml ? ".xml" : edl ? ".edl" : ".otio")
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                double fps = await ExchangeFpsAsync();
                var clips = ClipsForExchange();
                int w = 1920, h = 1080;
                try { var p = (_selectedResolution ?? "1920x1080").Split('x'); w = int.Parse(p[0]); h = int.Parse(p[1]); } catch { }
                string text = xml ? TimelineExporter.ToXml(name, clips, fps, w, h)
                            : edl ? TimelineExporter.ToEdl(name, clips, fps) : TimelineExporter.ToOtio(name, clips, fps);
                File.WriteAllText(dlg.FileName, text, new UTF8Encoding(false));
                LogMessage(LF(xml ? "xch_xml_done" : edl ? "xch_edl_done" : "xch_otio_done", Path.GetFileName(dlg.FileName), clips.Count), true);
                PlayBeep();
            }
            catch (Exception ex)
            {
                AppLog.Error("Exporting the timeline", ex);
                LogMessage(LF("xch_failed", ex.Message), true);
            }
        }

        // ── Preview copies (proxies) ─────────────────────────────────────────────────

        private CancellationTokenSource _proxyCts;

        private async void CreateProxies_Click(object sender, RoutedEventArgs e)
        {
            if (_proxyCts != null) { _proxyCts.Cancel(); LogMessage(L("prx_stopping"), true); return; }
            _proxyCts = new CancellationTokenSource();
            LogMessage(L("prx_start"), true);
            try
            {
                var progress = new Progress<string>(p =>
                {
                    var parts = p.Split('|');
                    LogMessage(LF("prx_progress", parts[0], parts.Length > 1 ? parts[1] : ""), true);
                });
                var (made, existed, failed) = await ProxyManager.CreateAsync(timelineItems.ToList(), progress, _proxyCts.Token);
                if (!ProxyManager.Enabled && (made + existed) > 0) ProxyManager.Enabled = true;
                if (mnuUseProxies != null) mnuUseProxies.IsChecked = ProxyManager.Enabled;
                LogMessage(made + existed == 0 && failed == 0 ? L("prx_none_needed") : LF("prx_done", made, existed, failed), true);
                PlayBeep();
            }
            catch (OperationCanceledException) { LogMessage(L("prx_stopped"), true); }
            catch (Exception ex) { AppLog.Error("Creating preview copies", ex); LogMessage(LF("prx_failed", ex.Message), true); }
            finally { _proxyCts = null; }
        }

        private void ToggleProxies_Click(object sender, RoutedEventArgs e)
        {
            ProxyManager.Enabled = !ProxyManager.Enabled;
            if (mnuUseProxies != null) mnuUseProxies.IsChecked = ProxyManager.Enabled;
            LogMessage(ProxyManager.Enabled ? L("prx_on") : L("prx_off"), true);
        }

        private void DeleteProxies_Click(object sender, RoutedEventArgs e)
        {
            long freed = ProxyManager.DeleteAll();
            LogMessage(LF("prx_deleted", Math.Round(freed / 1024.0 / 1024.0)), true);
        }

        /// <summary>v2.0: called once at start-up — menu check marks of the new options.</summary>
        private void InitV2Menus()
        {
            try { if (mnuUseProxies != null) mnuUseProxies.IsChecked = ProxyManager.Enabled; }
            catch (Exception ex) { AppLog.Error("Proxy menu", ex); }
        }

        /// <summary>v2.0: is this one of the new shortcuts?</summary>
        private static bool IsV2Key(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys mods)
        {
            const System.Windows.Input.ModifierKeys C = System.Windows.Input.ModifierKeys.Control,
                                                    A = System.Windows.Input.ModifierKeys.Alt,
                                                    S = System.Windows.Input.ModifierKeys.Shift;
            return IsV2bKey(key, mods) ||   // v2.0, second part (MainWindow.V2b.cs)
                   IsTrackKey(key, mods) ||   // v2.0, tracks (MainWindow.Tracks.cs)
                   IsObjectKey(key, mods) ||   // v2.0, follow an object (MainWindow.Objects.cs)
                   IsMixerKey(key, mods) ||   // v2.0, mixer automation (MainWindow.Mixer.cs)
                   IsColourKey(key, mods) ||   // v2.0, colour here (MainWindow.Colour.cs)
                   IsMulticamKey(key, mods) ||   // v2.0, multicam (MainWindow.Multicam.cs)
                   IsEditingKey2(key, mods) ||   // v2.0, speed ramp (MainWindow.Editing.cs)
                   (key == System.Windows.Input.Key.L && mods == (C | A)) ||
                   (key == System.Windows.Input.Key.U && mods == (C | A)) ||
                   (key == System.Windows.Input.Key.Delete && mods == S) ||
                   (key == System.Windows.Input.Key.F4 && mods == System.Windows.Input.ModifierKeys.None) ||
                   ((key == System.Windows.Input.Key.OemOpenBrackets || key == System.Windows.Input.Key.OemCloseBrackets) && (mods == C || mods == (C | S)));
        }

        /// <summary>v2.0 keyboard shortcuts; true when the key was handled.</summary>
        private bool HandleV2Key(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys mods)
        {
            const System.Windows.Input.ModifierKeys C = System.Windows.Input.ModifierKeys.Control,
                                                    A = System.Windows.Input.ModifierKeys.Alt,
                                                    S = System.Windows.Input.ModifierKeys.Shift;
            if (HandleV2bKey(key, mods)) return true;
            if (HandleTrackKey(key, mods)) return true;
            if (HandleObjectKey(key, mods)) return true;
            if (HandleMixerKey(key, mods)) return true;
            if (HandleColourKey(key, mods)) return true;
            if (HandleMulticamKey(key, mods)) return true;
            if (HandleEditingKey2(key, mods)) return true;
            if (key == System.Windows.Input.Key.L && mods == (C | A)) { OpenLayerSettings_Click(null, null); return true; }
            if (key == System.Windows.Input.Key.U && mods == (C | A)) { DetachAudio_Click(null, null); return true; }
            if (key == System.Windows.Input.Key.Delete && mods == S) { RippleDelete_Click(null, null); return true; }
            if (key == System.Windows.Input.Key.F4 && mods == System.Windows.Input.ModifierKeys.None) { WhereAmI_Click(null, null); return true; }
            if (key == System.Windows.Input.Key.OemOpenBrackets && (mods == C || mods == (C | S))) { NudgeClipEdge(startEdge: true, extend: mods == C); return true; }
            if (key == System.Windows.Input.Key.OemCloseBrackets && (mods == C || mods == (C | S))) { NudgeClipEdge(startEdge: false, extend: mods == C); return true; }
            return false;
        }
    }
}
