using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;

namespace UltraVideoEditor
{
    /// <summary>
    /// v2.0, second part — closing the gap to the big editors:
    ///   Ctrl+Alt+Space  Preview the whole video: picture, layers, titles, effects and sound,
    ///                   rendered small and fast, then played in the player
    ///   Ctrl+Alt+E      Clip effects: colour, stabilisation, speed and smooth slow motion, sound
    ///   Ctrl+Alt+T      Title on the selected clip
    ///   Alt+[ / Alt+]   Slip: the clip shows an earlier / later part of its file (same place, same length)
    ///   Ctrl+Alt+[ / ]  Roll: the cut between this clip and the next one moves half a second earlier / later
    /// </summary>
    public partial class MainWindow
    {
        // ── Clip effects ─────────────────────────────────────────────────────────────

        private void OpenClipEffects_Click(object sender, RoutedEventArgs e)
        {
            var item = SelectedClip();
            if (item == null) { LogMessage(L("msg_select_a_clip_first"), true); return; }
            SaveState();
            // v2.0: the window can look at the frame at the playhead and compare with other clips
            RecalculatePositions();
            double ph = _mixPreviewActive ? currentPlaybackPosition : TimelinePosition;
            double sp = item.IsImage ? 1 : Math.Clamp(item.VideoEffect?.Speed is double s0 && s0 > 0.01 ? s0 : 1, 0.1, 10);
            double fileTime = ph >= item.Start && ph < item.Start + item.Duration ? item.SourceOffset + (ph - item.Start) * sp : -1;
            var w = new ClipEffectsWindow(this, item, fileTime, timelineItems);
            if (w.ShowDialog() != true) { LogMessage(L("cfx_cancelled"), true); return; }

            int copied = 0;
            if (w.ApplyColourToTrack && item.VideoEffect != null)
            {
                foreach (var other in timelineItems.Where(i => !ReferenceEquals(i, item) && i.TrackIndex == item.TrackIndex &&
                                                               !(i.Type == "Audio" || i.IsAudio)))
                {
                    var v = other.VideoEffect ??= new VideoEffectData();
                    var s = item.VideoEffect;
                    v.Brightness = s.Brightness; v.Contrast = s.Contrast; v.Saturation = s.Saturation;
                    v.Temperature = s.Temperature; v.Tint = s.Tint; v.Gamma = s.Gamma;
                    v.Shadows = s.Shadows; v.Highlights = s.Highlights; v.Vignette = s.Vignette; v.Sharpen = s.Sharpen;
                    v.ShadowR = s.ShadowR; v.ShadowG = s.ShadowG; v.ShadowB = s.ShadowB;
                    v.MidR = s.MidR; v.MidG = s.MidG; v.MidB = s.MidB;
                    v.HighR = s.HighR; v.HighG = s.HighG; v.HighB = s.HighB;
                    v.SelColor = s.SelColor; v.SelHue = s.SelHue; v.SelSaturation = s.SelSaturation;
                    v.SelBrightness = s.SelBrightness; v.SelKeepOnly = s.SelKeepOnly;
                    v.SelPicked = s.SelPicked; v.SelSimilarity = s.SelSimilarity;   // v2.0
                    v.CurveAll = s.CurveAll; v.CurveR = s.CurveR; v.CurveG = s.CurveG; v.CurveB = s.CurveB;
                    copied++;
                }
            }
            UpdateTimelineDisplay();
            string msg = LF("cfx_saved", item.Name, ClipEffectsWindow.Describe(item));
            if (copied > 0) msg += " " + LF("cfx_copied", copied);
            LogMessage(msg, true);
            PlayBeep();
        }

        // ── Title ────────────────────────────────────────────────────────────────────

        private void OpenTitle_Click(object sender, RoutedEventArgs e)
        {
            var item = SelectedClip();
            if (item == null) { LogMessage(L("msg_select_a_clip_first"), true); return; }
            if (item.Type == "Audio" || item.IsAudio) { LogMessage(L("ttl_need_picture"), true); return; }
            SaveState();
            var w = new TitleWindow(this, item);
            if (w.ShowDialog() != true) { LogMessage(L("ttl_cancelled"), true); return; }
            UpdateTimelineDisplay();
            LogMessage(w.Removed ? LF("ttl_removed", item.Name) : LF("ttl_saved", item.Name, TitleWindow.Describe(item)), true);
            PlayBeep();
        }

        // ── Slip and roll ────────────────────────────────────────────────────────────

        /// <summary>Alt+[ / Alt+]: the clip stays where it is and as long as it is, but shows an earlier / later part of its file.</summary>
        private void SlipClip(bool later)
        {
            var item = SelectedClip();
            if (item == null) { LogMessage(L("msg_select_a_clip_first"), true); return; }
            if (RefuseLocked(item)) return;   // v2.0
            if (item.IsImage) { LogMessage(L("slip_image"), true); return; }
            const double step = 0.5;
            double speed = ClipSpeed(item);
            double delta = later ? step : -Math.Min(step, item.SourceOffset / Math.Max(0.01, speed));
            if (Math.Abs(delta) < 0.01) { LogMessage(L("slip_at_start"), true); PlayBeep(); return; }
            SaveState();
            item.SourceOffset = Math.Max(0, item.SourceOffset + delta * speed);
            ShiftClipContent(item, delta);
            UpdateTimelineDisplay();
            LogMessage(LF("slip_done", item.Name, FormatTimeDetailed(item.SourceOffset), FormatTimeDetailed(item.SourceOffset + item.Duration * speed)), true);
        }

        /// <summary>Ctrl+Alt+[ / ]: the cut between the selected clip and the next one on its track moves half a second earlier / later.</summary>
        private void RollCut(bool later)
        {
            var a = SelectedClip();
            if (a == null) { LogMessage(L("msg_select_a_clip_first"), true); return; }
            if (RefuseLocked(a)) return;   // v2.0
            RecalculatePositions();
            var b = timelineItems.Where(i => !ReferenceEquals(i, a) && i.TrackIndex == a.TrackIndex && Math.Abs(i.Start - a.End) < 0.05)
                                 .OrderBy(i => i.Start).FirstOrDefault();
            if (b == null) { LogMessage(L("roll_no_next"), true); PlayBeep(); return; }
            const double step = 0.5;
            double speedB = ClipSpeed(b);
            double d;
            if (later)
            {
                d = Math.Min(step, b.Duration - 0.2);
                if (d < 0.01) { LogMessage(L("edge_too_short"), true); PlayBeep(); return; }
            }
            else
            {
                double roomB = b.IsImage ? step : b.SourceOffset / Math.Max(0.01, speedB);
                d = -Math.Min(step, Math.Min(a.Duration - 0.2, roomB));
                if (Math.Abs(d) < 0.01) { LogMessage(L("edge_no_room"), true); PlayBeep(); return; }
            }
            SaveState();
            a.Duration += d;
            b.Duration -= d;
            if (!b.IsImage) b.SourceOffset = Math.Max(0, b.SourceOffset + d * speedB);
            if (b.UseFixedPosition) b.FixedPosition += d;
            ShiftClipContent(b, d);
            UpdateTimelineDisplay();
            RecalculatePositions();
            LogMessage(LF("roll_done", a.Name, b.Name, FormatTimeDetailed(a.End)), true);
        }

        /// <summary>
        /// Shift+Alt+[ / ]: slide — the clip moves half a second earlier / later between its
        /// neighbours; it keeps its content and length, the clip before it gets shorter or
        /// longer and the clip after it the other way round.
        /// </summary>
        private void SlideClip(bool later)
        {
            var c = SelectedClip();
            if (c == null) { LogMessage(L("msg_select_a_clip_first"), true); return; }
            if (RefuseLocked(c)) return;   // v2.0
            RecalculatePositions();
            var prev = timelineItems.Where(i => !ReferenceEquals(i, c) && i.TrackIndex == c.TrackIndex && Math.Abs(i.End - c.Start) < 0.05)
                                    .OrderByDescending(i => i.Start).FirstOrDefault();
            var next = timelineItems.Where(i => !ReferenceEquals(i, c) && i.TrackIndex == c.TrackIndex && Math.Abs(i.Start - c.End) < 0.05)
                                    .OrderBy(i => i.Start).FirstOrDefault();
            if (prev == null || next == null) { LogMessage(L("slide_need_neighbours"), true); PlayBeep(); return; }
            const double step = 0.5;
            double speedN = ClipSpeed(next);
            double d;
            if (later)
            {
                d = Math.Min(step, next.Duration - 0.2);
            }
            else
            {
                double roomN = next.IsImage ? step : next.SourceOffset / Math.Max(0.01, speedN);
                d = -Math.Min(step, Math.Min(prev.Duration - 0.2, roomN));
            }
            if (Math.Abs(d) < 0.01) { LogMessage(L("edge_no_room"), true); PlayBeep(); return; }
            SaveState();
            prev.Duration += d;
            if (c.UseFixedPosition) c.FixedPosition += d;
            next.Duration -= d;
            if (!next.IsImage) next.SourceOffset = Math.Max(0, next.SourceOffset + d * speedN);
            if (next.UseFixedPosition) next.FixedPosition += d;
            ShiftClipContent(next, d);
            UpdateTimelineDisplay();
            RecalculatePositions();
            LogMessage(LF("slide_done", c.Name, FormatTimeDetailed(c.Start), FormatTimeDetailed(c.End)), true);
        }

        // ── Track mixer ─────────────────────────────────────────────────────────────

        private void OpenTrackMixer_Click(object sender, RoutedEventArgs e)
        {
            // tracks with sound: video tracks (the sound of the clips) and audio tracks that hold clips
            var tracks = timelineItems.Where(i => i.Type == "Audio" || i.IsAudio || i.IsVideo).Select(i => i.TrackIndex)
                                      .Concat(TrackSet.AudioTracks()).Distinct().ToList();   // v2.0: every audio track, also an empty one
            if (tracks.Count == 0) { LogMessage(L("mix_no_tracks"), true); return; }
            var w = new TrackMixerWindow(this, tracks);
            if (w.ShowDialog() != true) { LogMessage(L("mix_cancelled"), true); return; }
            LogMessage(L("mix_saved") + " " + string.Join(" ", tracks.OrderBy(TrackSet.SortKey).Select(t => TimelineExporter.TrackName(t) + ": " + TrackMixer.Describe(t) + ".")), true);
            PlayBeep();
        }

        private void SlideEarlier_Click(object sender, RoutedEventArgs e) => SlideClip(later: false);
        private void SlideLater_Click(object sender, RoutedEventArgs e) => SlideClip(later: true);

        // ── Preview of the whole video ──────────────────────────────────────────────

        private bool _videoPreviewBuilding;
        private CancellationTokenSource _videoPreviewCts;

        private async void PreviewVideo_Click(object sender, RoutedEventArgs e)
        {
            if (_videoPreviewBuilding)
            {
                _videoPreviewCts?.Cancel();
                LogMessage(L("pv_stopping"), true);
                return;
            }
            if (_renderCancellation != null) { LogMessage(L("pv_render_running"), true); return; }
            if (!timelineItems.Any(i => !(i.Type == "Audio" || i.IsAudio))) { LogMessage(L("pv_nothing"), true); return; }

            RecalculatePositions();
            double total = TimelineMixPreview.TotalLength(timelineItems);
            double from = 0;
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem sel) from = sel.Start;
            if (from >= total - 0.5) from = 0;

            // vertical and square projects keep their shape in the preview
            string res = "854x480";
            try
            {
                var parts = (_selectedResolution ?? "1920x1080").Split('x');
                int w = int.Parse(parts[0]), h = int.Parse(parts[1]);
                if (h > w) res = "480x854";
                else if (h == w) res = "540x540";
            }
            catch { }

            bool subs = chkEnableSubtitles?.IsChecked == true;
            string key;
            try
            {
                key = JsonConvert.SerializeObject(new { timelineItems, subtitles, subs, res, currentExportSettings?.FadeInOut, currentExportSettings?.WarmLook, TrackMixer.Current, hidden = TrackSet.Save().Hidden });
            }
            catch { key = Guid.NewGuid().ToString(); }
            string hash;
            using (var sha = SHA1.Create())
                hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "").Substring(0, 16);
            string dir = Path.Combine(Path.GetTempPath(), "UVE_Preview");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"preview_{hash}.mp4");

            if (File.Exists(path) && new FileInfo(path).Length > 10_000)
            {
                PlayVideoPreview(path, from, total, cached: true);
                return;
            }
            foreach (var old in Directory.GetFiles(dir, "preview_*.mp4"))
                try { File.Delete(old); } catch { }

            _videoPreviewBuilding = true;
            _videoPreviewCts = new CancellationTokenSource();
            LogMessage(L("pv_building"), true);
            int lastQuarter = 0;
            var progress = new Progress<int>(p =>
            {
                int q = Math.Clamp(p / 25, 0, 3);
                if (q > lastQuarter) { lastQuarter = q; LogMessage(LF("pv_progress", q * 25), true); }
                if (prgRender != null) prgRender.Value = p;
            });
            if (prgRender != null) { prgRender.Visibility = Visibility.Visible; prgRender.Value = 0; }
            try
            {
                var settings = new ExportSettingsData
                {
                    Format = "MP4", VideoCodec = "H264", Quality = "Low", FrameRate = "25",
                    FadeInOut = currentExportSettings?.FadeInOut ?? true, WarmLook = currentExportSettings?.WarmLook
                };
                _renderEngine.PreviewMode = true;
                await _renderEngine.RenderSimpleAsync(timelineItems.ToList(), path, "mp4", progress, subtitles.ToList(), settings,
                                                      _videoPreviewCts.Token, useGPUAcceleration, res, AIVideoCreator.FastRenderMode, subs, null);
                if (File.Exists(path) && new FileInfo(path).Length > 10_000)
                {
                    PlayBeep();
                    PlayVideoPreview(path, from, total, cached: false);
                }
                else LogMessage(L("pv_failed_nofile"), true);
            }
            catch (OperationCanceledException) { LogMessage(L("pv_stopped"), true); try { File.Delete(path); } catch { } }
            catch (Exception ex)
            {
                AppLog.Error("Video preview", ex);
                LogMessage(LF("pv_failed", ex.Message), true);
                try { File.Delete(path); } catch { }
            }
            finally
            {
                _renderEngine.PreviewMode = false;
                _videoPreviewBuilding = false;
                _videoPreviewCts = null;
                if (prgRender != null) prgRender.Visibility = Visibility.Collapsed;
            }
        }

        private void PlayVideoPreview(string path, double from, double total, bool cached)
        {
            LoadMix(path, from, play: true);
            currentVideoDuration = total;
            UpdatePositionDisplay();
            LogMessage(LF(cached ? "pv_playing_cached" : "pv_playing", FormatTime(from), FormatTime(total)), true);
        }

        // ── keys ─────────────────────────────────────────────────────────────────────

        private static bool IsV2bKey(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys mods)
        {
            const System.Windows.Input.ModifierKeys C = System.Windows.Input.ModifierKeys.Control,
                                                    A = System.Windows.Input.ModifierKeys.Alt;
            bool bracket = key == System.Windows.Input.Key.OemOpenBrackets || key == System.Windows.Input.Key.OemCloseBrackets;
            const System.Windows.Input.ModifierKeys S = System.Windows.Input.ModifierKeys.Shift;
            return (mods == (C | A) && (key == System.Windows.Input.Key.E || key == System.Windows.Input.Key.T || key == System.Windows.Input.Key.Space ||
                                        key == System.Windows.Input.Key.M || bracket)) ||
                   (mods == A && bracket) || (mods == (A | S) && bracket);
        }

        private bool HandleV2bKey(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys mods)
        {
            const System.Windows.Input.ModifierKeys C = System.Windows.Input.ModifierKeys.Control,
                                                    A = System.Windows.Input.ModifierKeys.Alt;
            if (mods == (C | A))
            {
                switch (key)
                {
                    case System.Windows.Input.Key.E: OpenClipEffects_Click(null, null); return true;
                    case System.Windows.Input.Key.T: OpenTitle_Click(null, null); return true;
                    case System.Windows.Input.Key.Space: PreviewVideo_Click(null, null); return true;
                    case System.Windows.Input.Key.M: OpenTrackMixer_Click(null, null); return true;
                    case System.Windows.Input.Key.OemOpenBrackets: RollCut(later: false); return true;
                    case System.Windows.Input.Key.OemCloseBrackets: RollCut(later: true); return true;
                }
            }
            if (mods == (A | System.Windows.Input.ModifierKeys.Shift))
            {
                if (key == System.Windows.Input.Key.OemOpenBrackets) { SlideClip(later: false); return true; }
                if (key == System.Windows.Input.Key.OemCloseBrackets) { SlideClip(later: true); return true; }
            }
            if (mods == A)
            {
                if (key == System.Windows.Input.Key.OemOpenBrackets) { SlipClip(later: false); return true; }
                if (key == System.Windows.Input.Key.OemCloseBrackets) { SlipClip(later: true); return true; }
            }
            return false;
        }

        private void SlipEarlier_Click(object sender, RoutedEventArgs e) => SlipClip(later: false);
        private void SlipLater_Click(object sender, RoutedEventArgs e) => SlipClip(later: true);
        private void RollEarlier_Click(object sender, RoutedEventArgs e) => RollCut(later: false);
        private void RollLater_Click(object sender, RoutedEventArgs e) => RollCut(later: true);
    }
}
