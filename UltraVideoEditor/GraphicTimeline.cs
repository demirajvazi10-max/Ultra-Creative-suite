using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WPoint = System.Windows.Point;
using WSize = System.Windows.Size;
using WInput = System.Windows.Input;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;

namespace UltraVideoEditor
{
    /// <summary>
    /// v1.4 (R7) — the graphical timeline for sighted editors: tracks as lanes, clips as blocks
    /// with a picture (video and photos) or a waveform (sound), a time ruler, markers and the
    /// playhead. It is a second VIEW of the same montage as the accessible clip list: every
    /// change made here goes through the same clip data, the same undo and the same list, and
    /// every change made from the keyboard is drawn here at once.
    ///
    /// Mouse: click a clip to select it and put the playhead there, double-click to play from
    /// there, click the ruler to move the playhead, drag a clip to move it (also to the other
    /// video or audio track), drag its left or right edge to trim it, Ctrl+wheel to zoom.
    /// Snapping to clip edges, markers and the playhead; hold Shift to drag without snapping;
    /// a right click during a drag cancels it.
    ///
    /// For screen readers it is one labelled, non-focusable element: the clip list stays the
    /// keyboard way to edit, so nothing here gets in the way of JAWS or NVDA.
    /// </summary>
    public class GraphicTimeline : System.Windows.FrameworkElement
    {
        // ── layout ────────────────────────────────────────────────────────────
        public const double LabelWidth = 86, RulerHeight = 24, LaneHeight = 46, LaneGap = 4;
        private static readonly int[] LaneOrder = { 1, 0, 2, 3 };   // top to bottom: Video 2, Video 1, Audio 1, Audio 2
        private const double EdgeGrip = 7, SnapPixels = 8;

        // ── data, supplied by the main window ─────────────────────────────────
        public Func<IList<TimelineItem>> Items { get; set; }
        public Func<double> PlayheadTime { get; set; }
        public Func<IEnumerable<double>> Markers { get; set; }
        public TimelineItem Selected { get; set; }
        public double PixelsPerSecond { get; set; } = 20;
        /// <summary>Horizontal scroll offset of the surrounding ScrollViewer (keeps the track names visible).</summary>
        public double ScrollOffset { get; set; }

        // ── events ────────────────────────────────────────────────────────────
        /// <summary>A clip was clicked: the clip, the position inside it, and whether it was a double click.</summary>
        public event Action<TimelineItem, double, bool> ClipClicked;
        /// <summary>The ruler or an empty lane was clicked at a timeline time.</summary>
        public event Action<double> TimeClicked;
        /// <summary>A clip was dropped at a new start on a track.</summary>
        public event Action<TimelineItem, double, int> ClipMoved;
        /// <summary>A clip edge was dropped: clip, new start, new in-point, new duration.</summary>
        public event Action<TimelineItem, double, double, double> ClipTrimmed;
        /// <summary>Ctrl+wheel: +1 zoom in, -1 zoom out.</summary>
        public event Action<int> ZoomRequested;
        /// <summary>Text for the info line under the timeline (hovered clip, drag result).</summary>
        public event Action<string> InfoChanged;

        // ── drag state ────────────────────────────────────────────────────────
        private enum DragKind { None, Move, TrimLeft, TrimRight }
        private DragKind _drag = DragKind.None;
        private TimelineItem _dragItem;
        private WPoint _dragFrom;
        private bool _dragStarted;
        private double _origStart, _origDuration, _origOffset;
        private int _origTrack;
        private double _pvStart, _pvDuration, _pvOffset;
        private int _pvTrack;

        public GraphicTimeline()
        {
            Focusable = false;
            ClipToBounds = true;
            System.Windows.Automation.AutomationProperties.SetName(this,
                "Graphic timeline for the mouse. With the keyboard, use the clip list above.");
            WaveformCache.Ready += () => Dispatcher.BeginInvoke(new Action(InvalidateVisual));
            ThumbnailCache.Ready += () => Dispatcher.BeginInvoke(new Action(InvalidateVisual));
        }

        public double ContentHeight => RulerHeight + LaneOrder.Length * (LaneHeight + LaneGap) + 6;

        /// <summary>Recomputes the width from the montage length and redraws.</summary>
        public void Refresh()
        {
            var items = Items?.Invoke();
            double total = items == null || items.Count == 0 ? 0 : items.Max(i => i.Start + i.Duration);
            Width = Math.Max(400, LabelWidth + (total + 10) * PixelsPerSecond);
            Height = ContentHeight;
            InvalidateVisual();
        }

        private double X(double t) => LabelWidth + t * PixelsPerSecond;
        private double T(double x) => Math.Max(0, (x - LabelWidth) / PixelsPerSecond);
        private static int LaneOf(int track) { int i = Array.IndexOf(LaneOrder, track); return i < 0 ? 0 : i; }
        private double LaneTop(int lane) => RulerHeight + lane * (LaneHeight + LaneGap) + 3;
        private int TrackAtY(double y)
        {
            int lane = (int)Math.Floor((y - RulerHeight - 3) / (LaneHeight + LaneGap));
            return lane < 0 || lane >= LaneOrder.Length ? -1 : LaneOrder[lane];
        }

        // ── drawing ───────────────────────────────────────────────────────────
        private static Media.SolidColorBrush B(string hex)
        {
            var b = new Media.SolidColorBrush((Media.Color)Media.ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }
        private static readonly Media.Brush BgBrush = B("#161616"), LaneBrush = B("#222226"), RulerBrush = B("#101010"),
            VideoBrush = B("#2D5F9A"), ImageBrush = B("#3E7D4F"), AudioBrush = B("#7A4E9C"), TextBrush = B("#E8E8E8"),
            DimText = B("#9A9A9A"), PlayheadBrush = B("#FF3B30"), MarkerBrush = B("#FFC107"), WaveBrush = B("#D9C6F0"),
            SelBrush = B("#FFFFFF"), GhostBrush = B("#66FFFFFF"), LabelBg = B("#1C1C1C");
        private static readonly Media.Pen TickPen = Frozen(new Media.Pen(B("#555555"), 1));
        private static readonly Media.Pen SelPen = Frozen(new Media.Pen(SelBrush, 2));
        private static readonly Media.Pen ClipPen = Frozen(new Media.Pen(B("#0D0D0D"), 1));
        private static readonly Media.Pen PlayPen = Frozen(new Media.Pen(PlayheadBrush, 2));
        private static readonly Media.Pen MarkerPen = Frozen(new Media.Pen(MarkerBrush, 1) { DashStyle = Media.DashStyles.Dash });
        private static readonly Media.Pen WavePen = Frozen(new Media.Pen(WaveBrush, 1));
        private static Media.Pen Frozen(Media.Pen p) { p.Freeze(); return p; }

        private Media.FormattedText Text(string s, double size, Media.Brush brush) =>
            new Media.FormattedText(s ?? "", CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
                new Media.Typeface("Segoe UI"), size, brush, Media.VisualTreeHelper.GetDpi(this).PixelsPerDip);

        protected override void OnRender(Media.DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRectangle(BgBrush, null, new System.Windows.Rect(0, 0, w, h));
            var items = Items?.Invoke() ?? new List<TimelineItem>();

            // lanes
            for (int lane = 0; lane < LaneOrder.Length; lane++)
                dc.DrawRectangle(LaneBrush, null, new System.Windows.Rect(LabelWidth, LaneTop(lane), Math.Max(0, w - LabelWidth), LaneHeight));

            DrawRuler(dc, w);

            // markers
            foreach (double m in Markers?.Invoke() ?? Enumerable.Empty<double>())
                dc.DrawLine(MarkerPen, new WPoint(X(m), RulerHeight), new WPoint(X(m), h));

            // clips (the one being dragged is drawn at its new place)
            foreach (var it in items)
            {
                bool dragged = ReferenceEquals(it, _dragItem) && _dragStarted;
                double start = dragged ? _pvStart : it.Start;
                double dur = dragged ? _pvDuration : it.Duration;
                int track = dragged ? _pvTrack : it.TrackIndex;
                if (dragged)
                {
                    // where it was, faintly
                    var was = ClipRect(it.Start, it.Duration, it.TrackIndex);
                    dc.DrawRectangle(null, new Media.Pen(GhostBrush, 1) { DashStyle = Media.DashStyles.Dot }, was);
                }
                DrawClip(dc, it, ClipRect(start, dur, track), dragged ? _pvOffset : it.SourceOffset, dur);
            }

            // playhead
            double ph = PlayheadTime?.Invoke() ?? 0;
            double px = X(ph);
            dc.DrawLine(PlayPen, new WPoint(px, 0), new WPoint(px, h));
            var tri = new Media.StreamGeometry();
            using (var g = tri.Open())
            {
                g.BeginFigure(new WPoint(px - 6, 0), true, true);
                g.LineTo(new WPoint(px + 6, 0), true, false);
                g.LineTo(new WPoint(px, 9), true, false);
            }
            tri.Freeze();
            dc.DrawGeometry(PlayheadBrush, null, tri);

            // track names, pinned to the left edge of the visible area
            double lx = ScrollOffset;
            dc.DrawRectangle(LabelBg, null, new System.Windows.Rect(lx, 0, LabelWidth, h));
            string[] names = { "Video 2", "Video 1", "Audio 1", "Audio 2" };
            for (int lane = 0; lane < names.Length; lane++)
                dc.DrawText(Text(names[lane], 12, TextBrush), new WPoint(lx + 8, LaneTop(lane) + LaneHeight / 2 - 8));
        }

        private System.Windows.Rect ClipRect(double start, double dur, int track) =>
            new System.Windows.Rect(X(start), LaneTop(LaneOf(track)), Math.Max(2, dur * PixelsPerSecond), LaneHeight);

        private void DrawRuler(Media.DrawingContext dc, double w)
        {
            dc.DrawRectangle(RulerBrush, null, new System.Windows.Rect(0, 0, w, RulerHeight));
            // a label roughly every 80 pixels, on a "round" step
            double[] steps = { 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };
            double step = steps.FirstOrDefault(s => s * PixelsPerSecond >= 80);
            if (step == 0) step = 600;
            double tEnd = T(w);
            for (double t = 0; t <= tEnd; t += step / 5)
            {
                bool major = Math.Abs(t / step - Math.Round(t / step)) < 1e-6;
                double x = X(t);
                dc.DrawLine(TickPen, new WPoint(x, major ? 10 : 17), new WPoint(x, RulerHeight));
                if (major)
                    dc.DrawText(Text(TimeSpan.FromSeconds(t).ToString(step < 1 ? @"m\:ss\.f" : @"m\:ss"), 10, DimText), new WPoint(x + 3, 1));
            }
        }

        private void DrawClip(Media.DrawingContext dc, TimelineItem it, System.Windows.Rect r, double offset, double dur)
        {
            Media.Brush fill = it.IsAudio ? AudioBrush : it.IsImage ? ImageBrush : VideoBrush;
            dc.DrawRoundedRectangle(fill, ClipPen, r, 3, 3);
            dc.PushClip(new Media.RectangleGeometry(r));

            double textLeft = r.Left + 4;
            if (it.IsAudio || (it.IsVideo && it.Volume > 0 && !RenderEngine.IsAiCreatorClip(it)))
                DrawWave(dc, it, r, offset, dur, it.IsAudio);

            if (!it.IsAudio)
            {
                var thumb = ThumbnailCache.Get(it.Path, offset);
                if (thumb != null && r.Width > 30)
                {
                    double th = r.Height - 4;
                    double tw = Math.Min(r.Width - 4, th * thumb.PixelWidth / Math.Max(1.0, thumb.PixelHeight));
                    dc.DrawImage(thumb, new System.Windows.Rect(r.Left + 2, r.Top + 2, tw, th));
                    textLeft = r.Left + tw + 6;
                }
            }

            if (r.Right - textLeft > 24)
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(it.Name ?? "");
                if (string.IsNullOrWhiteSpace(name)) name = it.Name ?? "";
                dc.DrawText(Text(name, 11, TextBrush), new WPoint(textLeft, r.Top + 3));
                string sub = TimeSpan.FromSeconds(Math.Max(0, dur)).ToString(@"m\:ss\.f");
                if (AudioAutomation.HasCurve(it)) sub += "  ∿";
                if (it.FrameDescriptions != null && it.FrameDescriptions.Count > 0) sub += "  👁";
                dc.DrawText(Text(sub, 9.5, DimText), new WPoint(textLeft, r.Bottom - 15));
            }
            dc.Pop();
            if (ReferenceEquals(it, Selected))
                dc.DrawRoundedRectangle(null, SelPen, new System.Windows.Rect(r.Left + 1, r.Top + 1, Math.Max(1, r.Width - 2), r.Height - 2), 3, 3);
        }

        private void DrawWave(Media.DrawingContext dc, TimelineItem it, System.Windows.Rect r, double offset, double dur, bool full)
        {
            var peaks = WaveformCache.Get(it.Path);
            if (peaks == null || peaks.Length == 0) return;
            double speed = it.VideoEffect?.Speed is double s && s > 0.05 ? s : 1.0;
            double mid = full ? r.Top + r.Height / 2 : r.Bottom - 8;
            double amp = full ? r.Height / 2 - 3 : 7;
            double left = Math.Max(r.Left, ScrollOffset), right = Math.Min(r.Right, ScrollOffset + Math.Max(ActualWidth, 1));
            var geo = new Media.StreamGeometry();
            using (var g = geo.Open())
            {
                for (double x = left; x < right; x += 2)
                {
                    double fileT = offset + (x - r.Left) / PixelsPerSecond * speed;
                    int i = (int)(fileT * WaveformCache.PeaksPerSecond);
                    if (i < 0 || i >= peaks.Length) continue;
                    double v = peaks[i] * amp;
                    g.BeginFigure(new WPoint(x, mid - v), false, false);
                    g.LineTo(new WPoint(x, mid + v), true, false);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, WavePen, geo);
        }

        // ── mouse ─────────────────────────────────────────────────────────────
        private (TimelineItem item, DragKind kind) HitTest(WPoint p)
        {
            var items = Items?.Invoke();
            if (items == null || p.Y < RulerHeight) return (null, DragKind.None);
            int track = TrackAtY(p.Y);
            if (track < 0) return (null, DragKind.None);
            foreach (var it in items.Where(i => i.TrackIndex == track).Reverse())
            {
                var r = ClipRect(it.Start, it.Duration, it.TrackIndex);
                if (p.X < r.Left - 1 || p.X > r.Right + 1) continue;
                if (r.Width > EdgeGrip * 3)
                {
                    if (p.X - r.Left <= EdgeGrip) return (it, DragKind.TrimLeft);
                    if (r.Right - p.X <= EdgeGrip) return (it, DragKind.TrimRight);
                }
                return (it, DragKind.Move);
            }
            return (null, DragKind.None);
        }

        protected override void OnMouseLeftButtonDown(WInput.MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            var p = e.GetPosition(this);
            if (p.X < ScrollOffset + LabelWidth) return;   // the track names
            var (item, kind) = HitTest(p);
            if (item == null)
            {
                TimeClicked?.Invoke(T(p.X));
                e.Handled = true;
                return;
            }
            if (e.ClickCount >= 2)
            {
                ClipClicked?.Invoke(item, Math.Clamp(T(p.X) - item.Start, 0, item.Duration), true);
                e.Handled = true;
                return;
            }
            _drag = kind;
            _dragItem = item;
            _dragFrom = p;
            _dragStarted = false;
            _origStart = item.Start; _origDuration = item.Duration; _origOffset = item.SourceOffset; _origTrack = item.TrackIndex;
            _pvStart = _origStart; _pvDuration = _origDuration; _pvOffset = _origOffset; _pvTrack = _origTrack;
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(WInput.MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var p = e.GetPosition(this);
            if (_drag == DragKind.None)
            {
                var (item, kind) = HitTest(p);
                Cursor = kind == DragKind.TrimLeft || kind == DragKind.TrimRight ? WInput.Cursors.SizeWE
                       : item != null ? WInput.Cursors.Hand : WInput.Cursors.Arrow;
                if (item != null)
                    InfoChanged?.Invoke($"{item.Name}  —  {Fmt(item.Start)} → {Fmt(item.Start + item.Duration)}  ({Fmt(item.Duration)})");
                return;
            }
            if (!_dragStarted && Math.Abs(p.X - _dragFrom.X) < 4 && Math.Abs(p.Y - _dragFrom.Y) < 4) return;
            _dragStarted = true;

            double dt = (p.X - _dragFrom.X) / PixelsPerSecond;
            bool snap = !WInput.Keyboard.Modifiers.HasFlag(WInput.ModifierKeys.Shift);
            double speed = _dragItem.VideoEffect?.Speed is double s && s > 0.05 ? s : 1.0;
            switch (_drag)
            {
                case DragKind.Move:
                {
                    double ns = Math.Max(0, _origStart + dt);
                    if (snap)
                    {
                        double a = Snap(ns), b = Snap(ns + _origDuration) - _origDuration;
                        ns = Math.Abs(a - ns) <= Math.Abs(b - ns) ? a : Math.Max(0, b);
                    }
                    _pvStart = ns;
                    int tr = TrackAtY(p.Y);
                    // video stays on video tracks, sound on audio tracks
                    bool audio = _dragItem.IsAudio;
                    _pvTrack = tr >= 0 && (audio ? tr >= 2 : tr <= 1) ? tr : _origTrack;
                    InfoChanged?.Invoke($"{_dragItem.Name}  →  {Fmt(_pvStart)}  ({TrackName(_pvTrack)})");
                    break;
                }
                case DragKind.TrimLeft:
                {
                    double ns = _origStart + dt;
                    if (snap) ns = Snap(ns);
                    double maxShift = _origDuration - 0.1;
                    double minShift = _dragItem.IsImage ? -_origStart : -Math.Min(_origStart, _origOffset / speed);
                    double shift = Math.Clamp(ns - _origStart, minShift, maxShift);
                    _pvStart = _origStart + shift;
                    _pvDuration = _origDuration - shift;
                    _pvOffset = _dragItem.IsImage ? 0 : Math.Max(0, _origOffset + shift * speed);
                    InfoChanged?.Invoke($"{_dragItem.Name}  ⟵ {Fmt(_pvStart)}  ({Fmt(_pvDuration)})");
                    break;
                }
                case DragKind.TrimRight:
                {
                    double ne = _origStart + _origDuration + dt;
                    if (snap) ne = Snap(ne);
                    _pvDuration = Math.Max(0.1, ne - _origStart);
                    InfoChanged?.Invoke($"{_dragItem.Name}  ⟶ {Fmt(_origStart + _pvDuration)}  ({Fmt(_pvDuration)})");
                    break;
                }
            }
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonUp(WInput.MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (_drag == DragKind.None) return;
            var item = _dragItem;
            var kind = _drag;
            bool started = _dragStarted;
            EndDrag();
            if (item == null) return;
            if (!started)
            {
                var p = e.GetPosition(this);
                ClipClicked?.Invoke(item, Math.Clamp(T(p.X) - item.Start, 0, item.Duration), false);
                return;
            }
            if (kind == DragKind.Move)
            {
                if (Math.Abs(_pvStart - _origStart) > 0.001 || _pvTrack != _origTrack)
                    ClipMoved?.Invoke(item, _pvStart, _pvTrack);
            }
            else if (Math.Abs(_pvDuration - _origDuration) > 0.001 || Math.Abs(_pvStart - _origStart) > 0.001)
                ClipTrimmed?.Invoke(item, _pvStart, _pvOffset, _pvDuration);
        }

        protected override void OnMouseRightButtonDown(WInput.MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonDown(e);
            if (_drag != DragKind.None) { EndDrag(); e.Handled = true; }   // cancel the drag
        }

        protected override void OnLostMouseCapture(WInput.MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            if (_drag != DragKind.None && _dragStarted) { _drag = DragKind.None; _dragItem = null; _dragStarted = false; InvalidateVisual(); }
        }

        private void EndDrag()
        {
            _drag = DragKind.None;
            if (IsMouseCaptured) ReleaseMouseCapture();
            _dragStarted = false;
            _dragItem = null;
            InvalidateVisual();
        }

        protected override void OnMouseWheel(WInput.MouseWheelEventArgs e)
        {
            if (WInput.Keyboard.Modifiers.HasFlag(WInput.ModifierKeys.Control))
            {
                ZoomRequested?.Invoke(e.Delta > 0 ? 1 : -1);
                e.Handled = true;
                return;
            }
            base.OnMouseWheel(e);
        }

        /// <summary>Snaps a time to clip edges, markers, the playhead and 0 within a few pixels.</summary>
        private double Snap(double t)
        {
            var targets = new List<double> { 0, PlayheadTime?.Invoke() ?? 0 };
            foreach (var it in Items?.Invoke() ?? new List<TimelineItem>())
            {
                if (ReferenceEquals(it, _dragItem)) continue;
                targets.Add(it.Start);
                targets.Add(it.Start + it.Duration);
            }
            targets.AddRange(Markers?.Invoke() ?? Enumerable.Empty<double>());
            double best = t, bestDist = SnapPixels / PixelsPerSecond;
            foreach (double c in targets)
                if (Math.Abs(c - t) < bestDist) { best = c; bestDist = Math.Abs(c - t); }
            return Math.Max(0, best);
        }

        private static string Fmt(double s) => TimeSpan.FromSeconds(Math.Max(0, s)).ToString(@"m\:ss\.f");
        private static string TrackName(int t) => t switch { 0 => "Video 1", 1 => "Video 2", 2 => "Audio 1", 3 => "Audio 2", _ => "?" };
    }

    /// <summary>v1.4 (R7) — sound peaks of a file (20 per second), read once with FFmpeg in the background.</summary>
    public static class WaveformCache
    {
        public const int PeaksPerSecond = 20;
        private const int SampleRate = 2000;
        private static readonly ConcurrentDictionary<string, float[]> _done = new ConcurrentDictionary<string, float[]>();
        private static readonly ConcurrentDictionary<string, bool> _busy = new ConcurrentDictionary<string, bool>();
        private static readonly System.Threading.SemaphoreSlim _gate = new System.Threading.SemaphoreSlim(2);
        public static event Action Ready;

        public static float[] Get(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (_done.TryGetValue(path, out var p)) return p;
            if (_busy.TryAdd(path, true)) _ = Task.Run(() => Load(path));
            return null;
        }

        private static async Task Load(string path)
        {
            await _gate.WaitAsync();
            try
            {
                if (!File.Exists(path) || !File.Exists(AppPaths.FFmpeg)) { _done[path] = Array.Empty<float>(); return; }
                var psi = new ProcessStartInfo(AppPaths.FFmpeg,
                    $"-nostdin -v error -i \"{path}\" -vn -ac 1 -ar {SampleRate} -f s16le -")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                proc.DrainStderr();
                var peaks = new List<float>();
                int per = SampleRate / PeaksPerSecond, count = 0, max = 0;
                var buf = new byte[65536];
                var stream = proc.StandardOutput.BaseStream;
                int carry = -1, read;
                while ((read = await stream.ReadAsync(buf, 0, buf.Length)) > 0)
                {
                    int i = 0;
                    if (carry >= 0 && read > 0) { Add((short)(carry | (buf[0] << 8))); i = 1; carry = -1; }
                    for (; i + 1 < read; i += 2) Add((short)(buf[i] | (buf[i + 1] << 8)));
                    if (i < read) carry = buf[i];
                }
                await proc.WaitForExitAsync();
                if (count > 0) peaks.Add(max / 32768f);
                // normalise so quiet recordings are still visible
                float top = peaks.Count > 0 ? Math.Max(0.05f, peaks.Max()) : 1;
                _done[path] = peaks.Select(v => Math.Min(1f, v / top)).ToArray();

                void Add(short v)
                {
                    int a = Math.Abs((int)v);
                    if (a > max) max = a;
                    if (++count >= per) { peaks.Add(max / 32768f); count = 0; max = 0; }
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Reading the waveform of " + path, ex);
                _done[path] = Array.Empty<float>();
            }
            finally
            {
                _gate.Release();
                Ready?.Invoke();
            }
        }
    }

    /// <summary>v1.4 (R7) — one small picture per clip (its first frame, or the photo), loaded in the background.</summary>
    public static class ThumbnailCache
    {
        private static readonly ConcurrentDictionary<string, Imaging.BitmapSource> _done = new ConcurrentDictionary<string, Imaging.BitmapSource>();
        private static readonly ConcurrentDictionary<string, bool> _busy = new ConcurrentDictionary<string, bool>();
        private static readonly System.Threading.SemaphoreSlim _gate = new System.Threading.SemaphoreSlim(2);
        public static event Action Ready;

        public static Imaging.BitmapSource Get(string path, double offset)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string key = path + "|" + Math.Round(offset, 1).ToString(CultureInfo.InvariantCulture);
            if (_done.TryGetValue(key, out var b)) return b;
            if (_busy.TryAdd(key, true)) _ = Task.Run(() => Load(path, offset, key));
            return null;
        }

        private static async Task Load(string path, double offset, string key)
        {
            await _gate.WaitAsync();
            try
            {
                if (!File.Exists(path)) return;
                byte[] bytes;
                string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp")
                    bytes = await File.ReadAllBytesAsync(path);
                else
                {
                    if (!File.Exists(AppPaths.FFmpeg)) return;
                    var kind = await FrameDescriber.ProbePictureAsync(path, System.Threading.CancellationToken.None);
                    if (kind == FrameDescriber.PictureKind.None) return;
                    string ss = kind == FrameDescriber.PictureKind.Video
                        ? $"-ss {(offset + 0.3).ToString("0.###", CultureInfo.InvariantCulture)} " : "";
                    var psi = new ProcessStartInfo(AppPaths.FFmpeg,
                        $"-nostdin -v error {ss}-i \"{path}\" -map 0:v:0 -frames:v 1 -vf \"scale=-2:72\" -f image2pipe -c:v png -")
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true
                    };
                    using var proc = Process.Start(psi);
                    proc.DrainStderr();
                    using var ms = new MemoryStream();
                    await proc.StandardOutput.BaseStream.CopyToAsync(ms);
                    await proc.WaitForExitAsync();
                    bytes = ms.ToArray();
                }
                if (bytes == null || bytes.Length < 100) return;
                var img = new Imaging.BitmapImage();
                img.BeginInit();
                img.CacheOption = Imaging.BitmapCacheOption.OnLoad;
                img.DecodePixelHeight = 72;
                img.StreamSource = new MemoryStream(bytes);
                img.EndInit();
                img.Freeze();
                _done[key] = img;
            }
            catch (Exception ex)
            {
                AppLog.Error("Making the timeline picture of " + path, ex);
            }
            finally
            {
                _gate.Release();
                Ready?.Invoke();
            }
        }
    }
}
