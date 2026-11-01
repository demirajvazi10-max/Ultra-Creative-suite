using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace UltraVideoEditor
{
    /// <summary>
    /// v2.0 — plays the WHOLE timeline in real time, without rendering a file first.
    ///
    /// FFmpeg assembles the picture live: the main track clip after clip (from each clip's
    /// in-point, at its speed, with its colour), with the same transitions as the export
    /// (every clip lasts until the next one starts, and the cut is a dissolve, fade, wipe,
    /// slide… of the length the export uses), then over it the layers (position, opacity,
    /// green screen, blend modes, masks), colour LUTs, animation, titles and subtitles — the
    /// same drawing code the export uses. The frames come through a pipe and are shown in
    /// the player; the sound is the montage mix (every clip, the clip effects and the track
    /// mixer), played with NAudio. The sound is the clock: a frame is shown when the sound
    /// reaches its time.
    ///
    /// The picture size can be chosen (small 640 × 360 up to full 1920 × 1080); "automatic"
    /// starts at 960 × 540 and goes one size down when the computer cannot keep up.
    ///
    /// Pause holds the pipe (FFmpeg simply waits), so playing on is instant; seeking starts
    /// FFmpeg again at the new place. Long timelines are played in windows of a few minutes.
    /// Fast forward (2×, 4×) shows the picture without sound; playing BACKWARDS (1×, 2×, 4×)
    /// renders short pieces of a second or two in reverse, the next one always prepared while
    /// the current one plays.
    ///
    /// Not shown live (they need the export): Ken Burns / AI-creator motion, stabilisation and
    /// smooth slow motion.
    /// </summary>
    public sealed class LiveTimelinePlayer : IDisposable
    {
        public int Width { get; private set; } = 640;
        public int Height { get; private set; } = 360;
        public const int Fps = 25;
        private const double WindowSeconds = 240;
        private const int MaxMainInputs = 40, MaxLayerInputs = 16;

        /// <summary>Live picture sizes (the long side): Auto, 640, 960, 1280, 1920.</summary>
        public static readonly string[] Sizes = { "Auto", "640", "960", "1280", "1920" };
        private static string SizeFile => Path.Combine(AppPaths.DataDir, "live_size.txt");

        /// <summary>The chosen live picture size (remembered between sessions).</summary>
        public static string ChosenSize
        {
            get
            {
                try { if (File.Exists(SizeFile)) { string s = File.ReadAllText(SizeFile).Trim(); if (Sizes.Contains(s)) return s; } } catch { }
                return "Auto";
            }
            set
            {
                try { Directory.CreateDirectory(AppPaths.DataDir); File.WriteAllText(SizeFile, Sizes.Contains(value) ? value : "Auto"); } catch { }
            }
        }

        private readonly string _ffmpeg, _ffprobe;
        private readonly Action<string> _log;

        private List<TimelineItem> _items;
        private List<SubtitleItem> _subs;
        private bool _subsOn;
        private double _total;
        private string _audioPath;
        private int _longSide = 640;
        private string _shape = "1920x1080";
        private bool _auto;

        // video: one FFmpeg feeding frames (forward), or a chain of short reversed pieces (backward)
        private sealed class Feed
        {
            public Process Proc;
            public BlockingCollection<(double Pts, byte[] Data)> Frames;
            public ConcurrentQueue<(double Pts, byte[] Data)> Queue;
            public CancellationTokenSource Cts;
            public double Start, End;
            public bool Reverse, Done;
            public readonly StringBuilder Err = new StringBuilder();
        }
        private Feed _feed, _nextFeed;
        private Task<Feed> _nextTask;
        private double _windowStart, _windowEnd;
        private int _generation;
        private readonly List<string> _tempFiles = new List<string>();
        private string _lastError = "";
        private readonly Dictionary<string, (int w, int h)> _sizeCache = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);

        // audio clock
        private WaveOutEvent _out;
        private AudioFileReader _reader2;
        private double _clockBase;
        private long _posBase;
        private readonly Stopwatch _silentClock = new Stopwatch();
        private double _rate = 1.0;

        /// <summary>1 = normal; 2 or 4 = fast forward (the picture only, without sound); -1, -2, -4 = backwards.</summary>
        public double Rate => _rate;
        public bool Reversing => _rate < 0;
        private bool _playing;

        public bool Active { get; private set; }
        public bool Playing => _playing;
        public double Total => _total;
        public int FrameBytes => Width * Height * 4;
        public string LastError => _lastError;
        /// <summary>"Auto" or the long side in pixels, as chosen.</summary>
        public string SizeSetting => _auto ? "Auto" : _longSide.ToString(CultureInfo.InvariantCulture);

        public LiveTimelinePlayer(string ffmpeg, string ffprobe, Action<string> log)
        {
            _ffmpeg = ffmpeg;
            _ffprobe = ffprobe;
            _log = log ?? (_ => { });
        }

        /// <summary>The picture shape follows the project: wide, tall (9:16) or square; the size follows <see cref="ChosenSize"/>.</summary>
        public void SetShape(string resolution)
        {
            _shape = resolution ?? "1920x1080";
            string chosen = ChosenSize;
            _auto = chosen == "Auto";
            _longSide = _auto ? 960 : int.Parse(chosen, CultureInfo.InvariantCulture);
            ApplySize();
        }

        private void ApplySize()
        {
            int w = 1920, h = 1080;
            try { var p = _shape.Split('x'); w = int.Parse(p[0]); h = int.Parse(p[1]); } catch { }
            int L = _longSide, S = (int)Math.Round(L * 9 / 16.0 / 2) * 2;
            if (h > w) { Width = S; Height = L; }
            else if (h == w) { Width = Height = (int)Math.Round(L * 0.75 / 2) * 2; }
            else { Width = L; Height = S; }
        }

        /// <summary>
        /// "Automatic" size: one size smaller after the picture could not keep up. Returns the
        /// new long side, or 0 when it is already the smallest or the size is fixed.
        /// </summary>
        public int StepDown()
        {
            if (!_auto || _longSide <= 640) return 0;
            _longSide = _longSide > 1280 ? 1280 : _longSide > 960 ? 960 : 640;
            ApplySize();
            return _longSide;
        }

        /// <summary>Where the playhead is (timeline seconds).</summary>
        public double Position
        {
            get
            {
                double p;
                if (!_playing) p = _clockBase;
                else if (Math.Abs(_rate - 1) > 0.001) p = _clockBase + _silentClock.Elapsed.TotalSeconds * _rate;
                else
                {
                    p = _clockBase + _silentClock.Elapsed.TotalSeconds;
                    if (_out != null)
                    {
                        try
                        {
                            long pos = _out.GetPosition();
                            double bps = _out.OutputWaveFormat.AverageBytesPerSecond;
                            p = _clockBase + Math.Max(0, pos - _posBase) / bps;
                        }
                        catch { }
                    }
                }
                p = Math.Max(0, p);
                return _total > 0 ? Math.Min(p, _total) : p;
            }
        }

        /// <summary>Starts playing (or shows the first frame paused) at <paramref name="from"/>. False when FFmpeg gave no picture.</summary>
        public async Task<bool> StartAsync(List<TimelineItem> items, List<SubtitleItem> subs, bool subsOn, string audioPath,
                                           double from, bool play, CancellationToken ct)
        {
            _items = items;
            _subs = subs ?? new List<SubtitleItem>();
            _subsOn = subsOn;
            _audioPath = audioPath;
            _total = TimelineMixPreview.TotalLength(items);
            Active = true;
            if (!string.IsNullOrEmpty(audioPath) && File.Exists(audioPath))
            {
                try
                {
                    _reader2 = new AudioFileReader(audioPath);
                    _out = new WaveOutEvent { DesiredLatency = 120 };
                    _out.Init(_reader2);
                }
                catch (Exception ex) { AppLog.Error("Live player: sound", ex); DisposeAudio(); }
            }
            return await SeekAsync(from, play, ct);
        }

        /// <summary>Moves to <paramref name="t"/> (playing forwards); FFmpeg starts again there. Waits for the first frame.</summary>
        public async Task<bool> SeekAsync(double t, bool play, CancellationToken ct = default)
        {
            t = Math.Clamp(t, 0, Math.Max(0, _total - 0.04));
            StopVideo();
            StopAudioClock();
            if (_rate < 0) _rate = 1;
            _clockBase = t;
            var feed = await StartFeedAsync(t, Math.Min(_total, t + WindowSeconds), reverse: false, ct);
            if (feed == null) return false;
            _feed = feed;
            _windowStart = feed.Start;
            _windowEnd = feed.End;
            if (!await FirstFrameAsync(feed, ct)) return false;
            if (play) Resume();
            return true;
        }

        private async Task<bool> FirstFrameAsync(Feed feed, CancellationToken ct)
        {
            // wait for the first frame (FFmpeg opening the files), at most 20 seconds
            var sw = Stopwatch.StartNew();
            while (feed.Frames.Count == 0 && sw.Elapsed.TotalSeconds < 20 && !ct.IsCancellationRequested && !feed.Done)
                await Task.Delay(15, ct).ConfigureAwait(true);
            if (feed.Frames.Count == 0)
            {
                _log("Live player: FFmpeg gave no picture. " + _lastError);
                return false;
            }
            return true;
        }

        /// <summary>
        /// v2.0 — plays backwards from the playhead at 1, 2 or 4 times the speed (the picture
        /// only). Pieces of the timeline are rendered in reverse; the next piece is always
        /// being prepared while the current one plays.
        /// </summary>
        public async Task<bool> PlayReverseAsync(double speed, CancellationToken ct = default)
        {
            speed = speed >= 3 ? 4 : speed >= 1.5 ? 2 : 1;
            double at = Position;
            if (Reversing && _playing && _feed != null)
            {
                // already going backwards: only the speed changes
                Pause();
                _rate = -speed;
                Resume();
                return true;
            }
            if (at <= 0.02) return false;
            StopVideo();
            StopAudioClock();
            _rate = -speed;
            _clockBase = at;
            var feed = await StartFeedAsync(Math.Max(0, at - ReverseChunk), at, reverse: true, ct);
            if (feed == null) { _rate = 1; return false; }
            _feed = feed;
            if (!await FirstFrameAsync(feed, ct)) { _rate = 1; return false; }
            PrepareNextReverse();
            Resume();
            return true;
        }

        /// <summary>Seconds per reversed piece: shorter for big pictures (FFmpeg keeps a whole piece in memory).</summary>
        private double ReverseChunk => _longSide >= 1920 ? 1.2 : _longSide >= 1280 ? 1.6 : 2.0;

        private void PrepareNextReverse()
        {
            var cur = _feed;
            if (cur == null || !cur.Reverse || cur.Start <= 0.01 || _nextTask != null) return;
            double e = cur.Start, s = Math.Max(0, e - ReverseChunk);
            _nextTask = StartFeedAsync(s, e, reverse: true, CancellationToken.None);
        }

        public void Pause()
        {
            if (!_playing) return;
            double now = Position;   // read before the clock stops
            _playing = false;
            _clockBase = now;
            if (_out != null) { try { _out.Stop(); } catch { } _posBase = 0; }
            _silentClock.Reset();
        }

        /// <summary>v2.0 — fast forward (2 or 4) shows the picture faster, without sound; 1 brings the sound back. Not for backwards (see <see cref="PlayReverseAsync"/>).</summary>
        public void SetRate(double rate)
        {
            if (Reversing) return;
            bool wasPlaying = _playing;
            Pause();
            _rate = rate < 1.5 ? 1.0 : rate;
            if (wasPlaying) Resume();
        }

        public void Resume()
        {
            if (_playing || !Active) return;
            _playing = true;
            if (_out != null && _reader2 != null && Math.Abs(_rate - 1) < 0.001)
            {
                try
                {
                    _reader2.CurrentTime = TimeSpan.FromSeconds(Math.Min(_clockBase, _reader2.TotalTime.TotalSeconds));
                    _out.Play();
                    _posBase = _out.GetPosition();
                    return;
                }
                catch (Exception ex) { AppLog.Error("Live player: sound", ex); DisposeAudio(); }
            }
            _silentClock.Restart();
        }

        /// <summary>
        /// The newest frame whose time has come (older ones are dropped), or null. The caller
        /// returns the buffer with <see cref="Return"/>. Backwards, it also moves on to the
        /// next reversed piece and starts preparing the one after it.
        /// </summary>
        public byte[] TakeFrame(out double pts)
        {
            pts = -1;
            if (Reversing) SwitchReverseFeed();
            var feed = _feed;
            if (feed?.Frames == null) return null;
            double now = Position;
            bool back = feed.Reverse;
            // a frame whose time has not come yet stays in the queue (FFmpeg may run ahead of the clock)
            if (feed.Queue.TryPeek(out var first) && (back ? first.Pts < now - 0.5 / Fps : first.Pts > now + 0.5 / Fps)) return null;
            byte[] best = null;
            while (feed.Frames.TryTake(out var f))
            {
                if (best != null) Return(best);
                best = f.Data;
                pts = f.Pts;
                if (!feed.Queue.TryPeek(out var next) || (back ? next.Pts < now : next.Pts > now)) break;
            }
            return best;
        }

        private void SwitchReverseFeed()
        {
            var cur = _feed;
            if (cur == null) return;
            if (Position > cur.Start + 0.5 / Fps) return;   // the current piece still has frames for now
            if (_nextTask == null || !_nextTask.IsCompleted) return;
            var next = _nextTask.Result;
            _nextTask = null;
            if (next == null) return;
            DisposeFeed(cur);
            _feed = next;
            PrepareNextReverse();
        }

        public void Return(byte[] buf) { if (buf != null) ArrayPool<byte>.Shared.Return(buf); }

        /// <summary>True when the window's picture has run out before the end of the timeline (the next window should start).</summary>
        public bool NeedsNextWindow => Active && !Reversing && _windowEnd < _total - 0.05 && Position >= _windowEnd - 0.05;

        public bool AtEnd => Active && !Reversing && Position >= _total - 0.02;

        /// <summary>Backwards, the start of the timeline has been reached.</summary>
        public bool AtStart => Active && Reversing && Position <= 0.02;

        public bool VideoStalled(double lastPts) =>
            _playing && !Reversing && _feed?.Frames != null && _feed.Frames.Count == 0 && lastPts >= 0 && Position - lastPts > 1.5 &&
            Position < _windowEnd - 0.5;

        public void Stop()
        {
            Active = false;
            _playing = false;
            StopVideo();
            DisposeAudio();
            foreach (var f in _tempFiles) try { File.Delete(f); } catch { }
            _tempFiles.Clear();
        }

        public void Dispose() => Stop();

        // ── audio ──────────────────────────────────────────────────────────────────

        private void StopAudioClock()
        {
            _playing = false;
            try { _out?.Stop(); } catch { }
            _posBase = 0;
            _silentClock.Reset();
        }

        private void DisposeAudio()
        {
            try { _out?.Stop(); } catch { }
            try { _out?.Dispose(); } catch { }
            try { _reader2?.Dispose(); } catch { }
            _out = null;
            _reader2 = null;
        }

        // ── video ──────────────────────────────────────────────────────────────────

        private void StopVideo()
        {
            _generation++;
            DisposeFeed(_feed);
            _feed = null;
            var pending = _nextTask;
            _nextTask = null;
            if (pending != null) _ = pending.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) DisposeFeed(t.Result); });
        }

        private void DisposeFeed(Feed f)
        {
            if (f == null) return;
            try { f.Cts?.Cancel(); } catch { }
            try { if (f.Proc != null && !f.Proc.HasExited) f.Proc.Kill(entireProcessTree: true); } catch { }
            try { f.Proc?.Dispose(); } catch { }
            if (f.Frames != null) while (f.Frames.TryTake(out var x)) Return(x.Data);
        }

        /// <summary>Starts FFmpeg for the timeline from <paramref name="t"/> to <paramref name="end"/>, forwards or reversed.</summary>
        private async Task<Feed> StartFeedAsync(double t, double end, bool reverse, CancellationToken ct)
        {
            int gen = _generation;
            if (!reverse)
            {
                foreach (var f in _tempFiles) try { File.Delete(f); } catch { }
                _tempFiles.Clear();
            }
            string prefix = $"g{gen}_{(reverse ? "r" + N(t).Replace('.', '_') : "f")}_";
            var built = await BuildArgsAsync(t, end, reverse, prefix, ct);
            if (built.Args == null) return null;
            if (gen != _generation) return null;   // stopped or moved meanwhile

            var feed = new Feed { Start = t, End = built.End, Reverse = reverse, Cts = new CancellationTokenSource() };
            feed.Queue = new ConcurrentQueue<(double, byte[])>();
            feed.Frames = new BlockingCollection<(double, byte[])>(feed.Queue, reverse ? 160 : 12);
            var psi = new ProcessStartInfo(_ffmpeg, built.Args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, RedirectStandardInput = true
            };
            try { feed.Proc = Process.Start(psi); }
            catch (Exception ex) { _lastError = ex.Message; return null; }
            try { feed.Proc.StandardInput.Close(); } catch { }
            feed.Proc.ErrorDataReceived += (s, e) => { if (e.Data != null && feed.Err.Length < 4000) feed.Err.AppendLine(e.Data); };
            feed.Proc.BeginErrorReadLine();

            int size = FrameBytes;
            double frameLen = 1.0 / Fps;
            var token = feed.Cts.Token;
            var thread = new Thread(() =>
            {
                try
                {
                    var stream = feed.Proc.StandardOutput.BaseStream;
                    long n = 0;
                    while (!token.IsCancellationRequested)
                    {
                        byte[] buf = ArrayPool<byte>.Shared.Rent(size);
                        int got = 0;
                        while (got < size)
                        {
                            int r = stream.Read(buf, got, size - got);
                            if (r <= 0) break;
                            got += r;
                        }
                        if (got < size) { ArrayPool<byte>.Shared.Return(buf); break; }
                        // reversed pieces come last frame first
                        double pts = reverse ? feed.End - (n + 1) * frameLen : feed.Start + n * frameLen;
                        feed.Frames.Add((pts, buf), token);
                        n++;
                    }
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
                catch (Exception ex) { AppLog.Error("Live player: reading frames", ex); }
                finally
                {
                    try { feed.Proc?.WaitForExit(2000); } catch { }
                    feed.Done = true;
                    if (feed.Err.Length > 0) _lastError = feed.Err.ToString().Trim();
                }
            }) { IsBackground = true, Name = "LiveTimelinePlayer" };
            thread.Start();
            return feed;
        }

        private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private async Task<(int w, int h)> SizeOfAsync(LayerCompositor probe, string path, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(path)) return (0, 0);
            if (_sizeCache.TryGetValue(path, out var s)) return s;
            s = await probe.ProbeSizeAsync(path, ct);
            _sizeCache[path] = s;
            return s;
        }

        /// <summary>
        /// The FFmpeg command for the timeline from <paramref name="t"/> to at most
        /// <paramref name="endLimit"/> (fewer inputs may make it end sooner), and where it ends.
        /// </summary>
        private async Task<(string Args, double End)> BuildArgsAsync(double t, double endLimit, bool reverse, string prefix, CancellationToken ct)
        {
            int W = Width, H = Height;
            string fps = Fps.ToString(CultureInfo.InvariantCulture);

            var pictures = _items.Where(i => !(i.Type == "Audio" || i.IsAudio) && !TrackSet.IsHidden(i.TrackIndex)).ToList();   // v2.0: hidden tracks
            var layerItems = pictures.Where(RenderEngine.IsLayerClip).ToList();
            var mainItems = pictures.Except(layerItems).OrderBy(i => i.Start).ToList();
            if (mainItems.Count == 0) { mainItems = layerItems.OrderBy(i => i.Start).ToList(); layerItems.Clear(); }

            // v2.0: as in the export, a main-track clip lasts until the next one starts (its slot);
            // two clips starting together: the later one in the list is left out, as the export does
            var slots = new List<(TimelineItem Item, double Start, double End)>();
            for (int i = 0; i < mainItems.Count; i++)
            {
                var it = mainItems[i];
                if (slots.Count > 0 && Math.Abs(it.Start - slots[^1].Item.Start) < 0.05) continue;
                var next = mainItems.Skip(i + 1).FirstOrDefault(n => n.Start > it.Start + 0.05);
                slots.Add((it, it.Start, next != null ? next.Start : it.Start + it.Duration));
            }

            // the window: as far as the input limits allow
            double end = Math.Min(_total, endLimit);
            var inWindow = slots.Where(s => s.End > t + 0.001 && s.Start < end).ToList();
            if (inWindow.Count > MaxMainInputs) end = Math.Max(t + 1, inWindow[MaxMainInputs].Start);
            var layersIn = layerItems.Where(i => i.Start + i.Duration > t + 0.001 && i.Start < end).OrderBy(i => i.Start).ToList();
            if (layersIn.Count > MaxLayerInputs) { end = Math.Max(t + 1, Math.Min(end, layersIn[MaxLayerInputs].Start)); layersIn = layersIn.Take(MaxLayerInputs).ToList(); }
            double len = Math.Max(0.1, end - t);

            var inputs = new StringBuilder("-nostdin -hide_banner -loglevel error ");
            var g = new List<string>();
            int idx = 0;
            var segs = new List<(string Label, TimelineItem Item, double S, double E)>();
            double cursor = t;
            int sn = 0;

            string Black(double d) => $"color=c=black:s={W}x{H}:r={fps}:d={N(d)},format=yuv420p";
            string flags = W >= 1280 ? "bilinear" : "fast_bilinear";
            string Fit = $"scale={W}:{H}:force_original_aspect_ratio=decrease:flags={flags},pad={W}:{H}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1";

            foreach (var slot in slots.Where(s => s.End > t + 0.001 && s.Start < end))
            {
                var c = slot.Item;
                if (slot.Start > cursor + 0.02)
                {
                    string lb = $"[lvs{sn++}]";
                    g.Add(Black(slot.Start - cursor) + lb);
                    segs.Add((lb, null, cursor, slot.Start));
                    cursor = slot.Start;
                }
                double s = Math.Max(slot.Start, cursor), e = Math.Min(slot.End, end);
                double d = e - s;
                if (d < 0.02) continue;
                double into = s - c.Start;
                string label = $"[lvs{sn++}]";
                string path = string.IsNullOrEmpty(c.Path) ? null : ProxyManager.PathFor(c.Path);
                string fx = VideoEffects.GetFFmpegFilter(c.VideoEffect, includeSpeed: false);
                if (fx.Length > 0) fx += ",";
                bool image = c.IsImage || c.Type == "Image";
                if (path != null && File.Exists(path) && image)
                {
                    inputs.Append($"-loop 1 -framerate {fps} -t {N(d + 0.2)} -i \"{path}\" ");
                    g.Add($"[{idx++}:v]{Fit},fps={fps},{fx}tpad=stop_mode=clone:stop_duration={N(d)},trim=duration={N(d)},setpts=PTS-STARTPTS,format=yuv420p{label}");
                }
                else if (path != null && File.Exists(path) && await HasPictureAsync(path, ct))
                {
                    double speed = Math.Clamp(c.VideoEffect?.Speed ?? 1.0, 0.25, 4.0);
                    double inPoint = Math.Max(0, c.SourceOffset + into * speed);
                    double readLen = d * speed + 0.5;
                    string setpts = $"setpts=(PTS-STARTPTS)/{N(speed)}";
                    if (SpeedRamp.Has(c))
                    {
                        // v2.0: a speed ramp — the window starts "into" seconds into the clip
                        var sp = c.VideoEffect.SpeedPoints;
                        double s0 = SpeedRamp.SourceAt(sp, c.Duration, into);
                        inPoint = Math.Max(0, c.SourceOffset + s0);
                        readLen = SpeedRamp.SourceAt(sp, c.Duration, into + d) - s0 + 0.5;
                        setpts = $"setpts='({SpeedRamp.InverseExpr(sp, c.Duration, s0, into)})/TB'";
                    }
                    inputs.Append($"-hwaccel auto -ss {N(inPoint)} -t {N(readLen)} -i \"{path}\" ");
                    g.Add($"[{idx++}:v]setpts=PTS-STARTPTS,{setpts},{Fit},fps={fps},{fx}" +
                          $"tpad=stop_mode=clone:stop_duration={N(d)},trim=duration={N(d)},setpts=PTS-STARTPTS,format=yuv420p{label}");
                }
                else g.Add(Black(d) + label);
                segs.Add((label, c, s, e));
                cursor = e;
            }
            if (end - cursor > 0.02)
            {
                string lb = $"[lvs{sn++}]";
                g.Add(Black(end - cursor) + lb);
                segs.Add((lb, null, cursor, end));
            }

            // v2.0: the segments joined with the export's transitions: the outgoing picture's last
            // frame is held for the transition's length, and the next clip blends in from its
            // start, so every clip still begins exactly at its place on the timeline
            string cur = segs[0].Label;
            double curLen = segs[0].E - segs[0].S;
            for (int i = 1; i < segs.Count; i++)
            {
                var (lab, item, s, e) = segs[i];
                var prev = segs[i - 1];
                string o = $"[lvj{i}]";
                double fd = 0;
                string type = "fade";
                if (item != null && prev.Item != null && Math.Abs(s - item.Start) < 0.02)
                {
                    int pi = slots.FindIndex(x => ReferenceEquals(x.Item, prev.Item));
                    var before = pi > 0 ? slots[pi - 1].Item : null;
                    (type, fd) = RenderEngine.TransitionAfter(prev.Item, before, slots[pi].End - slots[pi].Start);
                    fd = Math.Min(fd, (e - s) - 0.05);
                }
                if (fd >= 0.08)
                {
                    g.Add($"{cur}tpad=stop_mode=clone:stop_duration={N(fd)}[lvx{i}]");
                    g.Add($"[lvx{i}]{lab}xfade=transition={type}:duration={N(fd)}:offset={N(curLen)}{o}");
                }
                else g.Add($"{cur}{lab}concat=n=2:v=1:a=0{o}");
                cur = o;
                curLen += e - s;
            }

            // layers, colour LUTs, animation and titles — shifted to this window's time
            var mainComposite = mainItems.Where(i => i.Start + i.Duration > t && i.Start < end)
                                         .Select(i => Shift(RenderEngine.ToComposite(i), t))
                                         .Where(c => c != null && (LayerCompositor.HasAnimation(c) || LayerCompositor.HasLut(c) || LayerCompositor.HasTitle(c) || LayerCompositor.HasRegion(c)))
                                         .ToList();
            var layers = layersIn.Select(i => Shift(RenderEngine.ToComposite(i), t)).Where(c => c != null).ToList();
            foreach (var l in layers) if (!string.IsNullOrEmpty(l.Path)) l.Path = ProxyManager.PathFor(l.Path);
            var sizes = new Dictionary<CompositeClip, (int w, int h)>();
            var probe = new LayerCompositor(_ffprobe, (a, c) => Task.FromResult(false), null);
            foreach (var l in layers) sizes[l] = await SizeOfAsync(probe, l.Path, ct);
            foreach (var c in mainComposite.Where(LayerCompositor.HasRegion)) sizes[c] = await SizeOfAsync(probe, c.Path, ct);   // v2.0

            string dir = Path.Combine(Path.GetTempPath(), "UVE_Live");
            Directory.CreateDirectory(dir);
            var temp = new List<string>();
            temp.AddRange(LayerCompositor.WriteTitleFiles(layers.Concat(mainComposite), dir, prefix));
            temp.AddRange(await LayerCompositor.PrepareMasksAsync(layers, sizes, dir, RunFfmpegAsync, ct));
            lock (_tempFiles) _tempFiles.AddRange(temp);
            if (string.IsNullOrEmpty(LayerCompositor.FontSpec)) LayerCompositor.FontSpec = LayerCompositor.DefaultFontSpec();

            if (layers.Count > 0 || mainComposite.Count > 0)
            {
                // the compositor counts inputs from 0 = the picture it draws on; ours starts after the clips
                int inputIndex = idx;
                LayerCompositor.ComposeVideo(inputs, ref inputIndex, ref cur, g, layers, mainComposite, sizes,
                                             new Dictionary<CompositeClip, bool>(), len, W, H, fps, animate: true);
                idx = inputIndex;
            }

            // subtitles
            if (_subsOn && _subs.Count > 0)
            {
                int k = 0;
                foreach (var sub in _subs.Where(x => x.End > t && x.Start < end && !string.IsNullOrWhiteSpace(x.Text)))
                {
                    string file = Path.Combine(dir, $"sub_{prefix}{k++}.txt");
                    File.WriteAllText(file, sub.Text.Trim(), new UTF8Encoding(false));
                    lock (_tempFiles) _tempFiles.Add(file);
                    string o = $"[lvt{k}]";
                    double fs = Math.Round(H * 0.055);
                    g.Add($"{cur}drawtext={LayerCompositor.FontSpec}textfile='{LayerCompositor.EscapeFilterPath(file)}':fontsize={N(fs)}:fontcolor=white" +
                          $":x='(w-text_w)/2':y='h-text_h-{N(H * 0.06)}':box=1:boxcolor=black@0.55:boxborderw={N(Math.Round(fs * 0.3))}" +
                          $":enable='between(t,{N(Math.Max(0, sub.Start - t))},{N(sub.End - t)})'{o}");
                    cur = o;
                }
            }

            // backwards: the whole piece is turned around (it is kept small for that)
            g.Add($"{cur}{(reverse ? $"trim=duration={N(len)},reverse," : "")}format=bgra[lvout]");
            string graph = string.Join(";", g);
            string args = inputs + $"-filter_complex \"{graph}\" -map \"[lvout]\" -an -f rawvideo -pix_fmt bgra pipe:1";
            if (args.Length > 31000)
            {
                _log("Live player: the window is too large for one command; it is made shorter.");
                return (null, end);
            }
            return (args, end);
        }

        private async Task<bool> RunFfmpegAsync(string args, CancellationToken ct)
        {
            try
            {
                using var p = new Process
                {
                    StartInfo = new ProcessStartInfo(_ffmpeg, args)
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true }
                };
                p.Start();
                var e = p.StandardError.ReadToEndAsync();
                var o = p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync(ct);
                await Task.WhenAll(e, o);
                return p.ExitCode == 0;
            }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
        }

        /// <summary>A composite clip moved to the window's time (null when it ends before the window).</summary>
        private static CompositeClip Shift(CompositeClip c, double t)
        {
            double start = c.Start - t;
            if (start + c.Duration <= 0.01) return null;
            if (start < 0)
            {
                double cut = -start;
                c.Duration -= cut;
                c.Cut += cut;   // v2.0: a moving mask keeps its timing
                if (!c.IsImage) c.SourceOffset += cut * (c.Speed > 0.01 ? c.Speed : 1);   // v2.0: file time runs at the clip's speed
                if (c.Keyframes != null)
                    foreach (var k in c.Keyframes) k.Time -= cut;
                if (c.Title != null)
                {
                    c.Title = c.Title.Clone();
                    // a title that was already on screen does not fade in again at the new start
                    if (c.Title.StartIn + 0.5 <= cut) c.Title.Animation = "None";
                    c.Title.StartIn -= cut;
                    if (c.Title.Length > 0.01 && c.Title.StartIn < 0) c.Title.Length += c.Title.StartIn;
                    if (c.Title.StartIn < 0) c.Title.StartIn = 0;
                    if (c.Title.Length < 0) c.Title.Enabled = false;
                }
                start = 0;
            }
            c.Start = start;
            return c;
        }

        private readonly Dictionary<string, bool> _pictureCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private async Task<bool> HasPictureAsync(string path, CancellationToken ct)
        {
            if (_pictureCache.TryGetValue(path, out bool v)) return v;
            bool result = true;
            try
            {
                if (!string.IsNullOrEmpty(_ffprobe) && File.Exists(_ffprobe))
                {
                    using var p = new Process
                    {
                        StartInfo = new ProcessStartInfo(_ffprobe,
                            $"-v error -select_streams v -show_entries stream=codec_name:stream_disposition=attached_pic -of csv=p=0 \"{path}\"")
                        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }
                    };
                    p.Start();
                    var o = p.StandardOutput.ReadToEndAsync();
                    var e = p.StandardError.ReadToEndAsync();
                    await p.WaitForExitAsync(ct);
                    await e;
                    string s = (await o).Trim();
                    if (p.ExitCode == 0)
                        result = s.Split('\n').Select(l => l.Trim()).Any(l => l.Length > 0 && !l.EndsWith(",1"));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            _pictureCache[path] = result;
            return result;
        }
    }
}
