using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WControls = System.Windows.Controls;

namespace UltraVideoEditor
{
    /// <summary>
    /// v2.0 — real-time playback of the whole timeline (Ctrl+Shift+Space), with picture.
    /// The live player (<see cref="LiveTimelinePlayer"/>) assembles the frames; here they are
    /// shown over the player area, and the usual commands — play/pause, seeking, J/K/L,
    /// frame steps, Where am I — work on it. If the live picture cannot start, the editor
    /// falls back to the sound-only montage mix as before.
    /// </summary>
    public partial class MainWindow
    {
        private LiveTimelinePlayer _live;
        private WriteableBitmap _liveBmp;
        private WControls.Image _liveImage;
        private System.Windows.Threading.DispatcherTimer _liveTimer;
        private double _liveLastPts = -1;
        private DateTime _liveLastResync = DateTime.MinValue;
        private DateTime _liveLastDisplayUpdate = DateTime.MinValue;
        private bool _liveBusy;
        private int _liveStalls;

        private bool LiveActive => _live != null && _live.Active;

        /// <summary>Starts live playback at <paramref name="from"/>. False when it could not start (the caller falls back).</summary>
        private async Task<bool> StartLiveAsync(double from, bool play)
        {
            if (_liveBusy) return true;
            _liveBusy = true;
            try
            {
                if (!System.IO.File.Exists(AppPaths.FFmpeg)) return false;
                StopLive(announce: false);
                RecalculatePositions();
                var snapshot = timelineItems.ToList();
                if (!snapshot.Any(i => !(i.Type == "Audio" || i.IsAudio))) return false;   // nothing to see: the sound mix does the job

                LogMessage(L("live_starting"), true);
                string audio = null;
                try { audio = (await TimelineMixPreview.BuildAsync(snapshot, CancellationToken.None)).Path; }
                catch (Exception ex) { AppLog.Error("Live player: sound mix", ex); }

                // the clip player gives the screen to the live picture
                try { if (_mediaPlayer != null && _mediaPlayer.IsPlaying) _mediaPlayer.Stop(); } catch { }
                _rewindTimer?.Stop();
                _shuttle = 0;
                _playingItem = null;
                _mixPath = null;

                _live = new LiveTimelinePlayer(AppPaths.FFmpeg, AppPaths.FFprobe, m => LogMessage(m, false));
                _liveStalls = 0;
                _live.SetShape(_selectedResolution);
                EnsureLiveSurface();

                bool ok = await _live.StartAsync(snapshot, subtitles.ToList(), chkEnableSubtitles?.IsChecked == true, audio, from, play, CancellationToken.None);
                if (!ok)
                {
                    AppLog.Warn("Live player could not start: " + _live.LastError);
                    StopLive(announce: false);
                    LogMessage(L("live_failed"), true);
                    return false;
                }
                _mixPreviewActive = true;
                currentVideoDuration = _live.Total;
                currentPlaybackPosition = from;
                _liveLastPts = -1;
                SetPlayingUi(play);
                ShowLiveFrameNow();
                _liveTimer.Start();
                StartFollowTimer();
                LogMessage(LF("live_playing", FormatTime(from), FormatTime(_live.Total)), true);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error("Live player", ex);
                StopLive(announce: false);
                return false;
            }
            finally { _liveBusy = false; }
        }

        private void EnsureLiveSurface()
        {
            int w = _live.Width, h = _live.Height;
            if (_liveBmp == null || _liveBmp.PixelWidth != w || _liveBmp.PixelHeight != h)
                _liveBmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            if (_liveImage == null)
            {
                _liveImage = new WControls.Image { Stretch = Stretch.Uniform };
                System.Windows.Automation.AutomationProperties.SetName(_liveImage, L("live_surface_name"));
            }
            _liveImage.Source = _liveBmp;
            _liveImage.Visibility = Visibility.Visible;
            try { if (!ReferenceEquals(vlcVideoView.Content, _liveImage)) vlcVideoView.Content = _liveImage; } catch (Exception ex) { AppLog.Error("Live player surface", ex); }
            if (_liveTimer == null)
            {
                _liveTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
                _liveTimer.Tick += (s, e) => LiveTick();
            }
        }

        private void ShowLiveFrameNow()
        {
            for (int i = 0; i < 3; i++) if (DrawLiveFrame()) break;
        }

        private bool DrawLiveFrame()
        {
            if (_live == null) return false;
            var frame = _live.TakeFrame(out double pts);
            if (frame == null) return false;
            try
            {
                _liveBmp.WritePixels(new Int32Rect(0, 0, _live.Width, _live.Height), frame, _live.Width * 4, 0);
                _liveLastPts = pts;
            }
            catch (Exception ex) { AppLog.Error("Live player: drawing", ex); }
            finally { _live.Return(frame); }
            return true;
        }

        private async void LiveTick()
        {
            if (!LiveActive) { _liveTimer?.Stop(); return; }
            if (_liveBusy) return;
            DrawLiveFrame();
            currentPlaybackPosition = _live.Position;
            if ((DateTime.Now - _liveLastDisplayUpdate).TotalMilliseconds > 200)
            {
                _liveLastDisplayUpdate = DateTime.Now;
                UpdatePositionDisplay();
            }

            if (_live.AtStart && _live.Playing)
            {
                // v2.0: playing backwards reached the beginning
                _liveBusy = true;
                try
                {
                    await _live.SeekAsync(0, play: false);
                    _liveLastPts = -1;
                    ShowLiveFrameNow();
                }
                finally { _liveBusy = false; }
                SetPlayingUi(false);
                LogMessage(L("live_rev_start"), true);
                PlayBeep();
                return;
            }
            if (_live.AtEnd && _live.Playing)
            {
                _live.Pause();
                _live.SetRate(1);
                SetPlayingUi(false);
                LogMessage(LF("live_end", FormatTime(_live.Total)), true);
                PlayBeep();
                return;
            }
            if (_live.Playing && (_live.NeedsNextWindow ||
                (_live.VideoStalled(_liveLastPts) && (DateTime.Now - _liveLastResync).TotalSeconds > 4)))
            {
                bool stalled = !_live.NeedsNextWindow;
                _liveLastResync = DateTime.Now;
                _liveBusy = true;
                try
                {
                    double at = _live.Position;
                    if (stalled) LogMessage(L("live_catching_up"), false);
                    // v2.0: automatic size — the computer could not keep up, so a smaller picture
                    if (stalled && ++_liveStalls >= 2 && _live.StepDown() is int side && side > 0)
                    {
                        _liveStalls = 0;
                        EnsureLiveSurface();
                        LogMessage(LF("live_size_down", _live.Width, _live.Height), true);
                    }
                    await _live.SeekAsync(at, play: true);
                    _liveLastPts = -1;
                }
                finally { _liveBusy = false; }
            }
        }

        private void LivePauseResume()
        {
            if (!LiveActive) return;
            if (_live.Reversing)
            {
                // backwards: Space comes back to normal playing, forwards, with sound
                _ = LiveSeekAsync(_live.Position, play: true, announce: false);
                LogMessage(L("shuttle_play"), true);
                return;
            }
            if (_live.Playing && _live.Rate > 1.5)
            {
                // fast forward: Space first comes back to normal speed, with sound
                _live.SetRate(1);
                LogMessage(L("shuttle_play"), true);
                return;
            }
            if (_live.Playing)
            {
                _live.Pause();
                _live.SetRate(1);
                SetPlayingUi(false);
                currentPlaybackPosition = _live.Position;
                LogMessage(LF("mix_paused_at", FormatTimeDetailed(currentPlaybackPosition)), true);
            }
            else
            {
                if (_live.AtEnd) { _ = LiveSeekAsync(0, play: true, announce: false); LogMessage(LF("mix_resumed", FormatTime(0)), true); return; }
                _live.Resume();
                SetPlayingUi(true);
                LogMessage(LF("mix_resumed", FormatTime(_live.Position)), true);
            }
        }

        private async Task LiveSeekAsync(double t, bool play, bool announce = true, bool frames = false)
        {
            if (!LiveActive || _liveBusy) return;
            _liveBusy = true;
            try
            {
                t = Math.Clamp(t, 0, Math.Max(0, _live.Total - 0.04));
                if (!await _live.SeekAsync(t, play))
                {
                    LogMessage(L("live_failed"), true);
                    StopLive(announce: false);
                    return;
                }
                _liveLastPts = -1;
                ShowLiveFrameNow();
                currentPlaybackPosition = t;
                SetPlayingUi(play);
                UpdatePositionDisplay();
                if (announce) LogMessage(frames ? FormatTimeFrames(t) : FormatTimeDetailed(t), true);
            }
            finally { _liveBusy = false; }
        }

        /// <summary>Ends live playback (a single clip is about to play, or the editor closes).</summary>
        private void StopLive(bool announce)
        {
            if (_live == null) return;
            _liveTimer?.Stop();
            try { _live.Stop(); } catch { }
            _live = null;
            if (_liveImage != null) _liveImage.Visibility = Visibility.Collapsed;
            try { vlcVideoView.Content = null; } catch { }
            SetPlayingUi(false);
            if (announce) LogMessage(L("live_stopped"), true);
        }

        // ── the usual commands while the live picture plays ──────────────────────────

        /// <summary>Space / Play: pause or continue.</summary>
        private bool LiveHandlesTogglePlay()
        {
            if (!LiveActive) return false;
            LivePauseResume();
            return true;
        }

        private bool LiveHandlesSeekBy(long ms)
        {
            if (!LiveActive) return false;
            _ = LiveSeekAsync(_live.Position + ms / 1000.0, _live.Playing);
            return true;
        }

        /// <summary>
        /// L plays; pressed again while playing it goes 2 and then 4 times faster (picture only,
        /// without sound). At the end it starts again from the beginning. J plays BACKWARDS
        /// (v2.0): once at normal speed, again 2 and then 4 times faster; L after J plays
        /// forwards again with sound. Speaks only when the speed actually changes.
        /// </summary>
        private bool LiveHandlesShuttle(int direction)
        {
            if (!LiveActive) return false;
            if (direction > 0 && _live.Reversing)
            {
                _ = LiveSeekAsync(_live.Position, play: true, announce: false);
                LogMessage(L("shuttle_play"), true);
                return true;
            }
            if (direction > 0)
            {
                if (_live.AtEnd)
                {
                    _live.SetRate(1);
                    _ = LiveSeekAsync(0, play: true, announce: false);
                    LogMessage(LF("mix_resumed", FormatTime(0)), true);
                }
                else if (!_live.Playing)
                {
                    _live.SetRate(1);
                    _live.Resume(); SetPlayingUi(true); LogMessage(L("shuttle_play"), true);
                }
                else
                {
                    double next = _live.Rate < 1.5 ? 2 : 4;
                    if (next != _live.Rate)
                    {
                        _live.SetRate(next);
                        LogMessage(LF("shuttle_forward", (int)next), true);
                    }
                }
            }
            else
            {
                _ = LiveReverseAsync();
            }
            return true;
        }

        /// <summary>v2.0 — J: backwards at 1×, then 2×, then 4× (the picture only, without sound).</summary>
        private async Task LiveReverseAsync()
        {
            if (!LiveActive || _liveBusy) return;
            double speed = !_live.Reversing || !_live.Playing ? 1 : -_live.Rate < 1.5 ? 2 : 4;
            if (_live.Reversing && _live.Playing && Math.Abs(-_live.Rate - speed) < 0.01) return;   // already at 4×
            if (_live.Position <= 0.05) { LogMessage(L("live_rev_start"), true); return; }
            bool first = !_live.Reversing || !_live.Playing;
            _liveBusy = true;
            try
            {
                if (first) LogMessage(L("live_rev_preparing"), false);
                bool ok = await _live.PlayReverseAsync(speed);
                if (!ok) { LogMessage(L("live_rev_failed"), true); return; }
                _liveLastPts = -1;
                SetPlayingUi(true);
                LogMessage(speed <= 1 ? L("live_rev_play") : LF("live_rev_faster", (int)speed), true);
            }
            finally { _liveBusy = false; }
        }

        /// <summary>v2.0 — Ctrl+Alt+Shift+Space: the next live picture size (automatic, 640 … 1920), remembered.</summary>
        private async void CycleLiveSize_Click(object sender, RoutedEventArgs e)
        {
            var sizes = LiveTimelinePlayer.Sizes;
            string now = LiveTimelinePlayer.ChosenSize;
            string next = sizes[(Array.IndexOf(sizes, now) + 1) % sizes.Length];
            LiveTimelinePlayer.ChosenSize = next;
            string name = next == "Auto" ? L("live_size_auto") : LF("live_size_px", next, (int)Math.Round(int.Parse(next) * 9 / 16.0 / 2) * 2);
            LogMessage(LF("live_size_set", name), true);
            if (!LiveActive || _liveBusy) return;
            _liveBusy = true;
            try
            {
                LiveTimelinePlayer.ChosenSize = next;
                _live.SetShape(_selectedResolution);
                EnsureLiveSurface();
                _liveStalls = 0;
                bool playing = _live.Playing && !_live.Reversing;
                if (!await _live.SeekAsync(_live.Position, playing)) { LogMessage(L("live_failed"), true); StopLive(announce: false); return; }
                _liveLastPts = -1;
                ShowLiveFrameNow();
                SetPlayingUi(playing);
            }
            finally { _liveBusy = false; }
        }

        private bool LiveHandlesShuttleStop()
        {
            if (!LiveActive) return false;
            if (_live.Reversing)
            {
                // K after J: stop where it is, ready to play forwards
                _live.Pause();
                _ = LiveSeekAsync(_live.Position, play: false, announce: false);
                LogMessage(LF("paused_at", FormatTimeDetailed(_live.Position)), true);
                return true;
            }
            if (_live.Playing) { _live.Pause(); SetPlayingUi(false); }
            _live.SetRate(1);
            LogMessage(LF("paused_at", FormatTimeDetailed(_live.Position)), true);
            return true;
        }

        private bool LiveHandlesStep(int direction)
        {
            if (!LiveActive) return false;
            if (_live.Playing) { _live.Pause(); SetPlayingUi(false); }
            _live.SetRate(1);
            _ = LiveSeekAsync(_live.Position + direction / (double)LiveTimelinePlayer.Fps, play: false, frames: true);   // also leaves backwards playing
            return true;
        }

        private bool LiveHandlesSeekTo(double t, bool play)
        {
            if (!LiveActive) return false;
            _ = LiveSeekAsync(t, play);
            return true;
        }
    }
}
