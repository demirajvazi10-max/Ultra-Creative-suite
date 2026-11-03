using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UltraAudioEditor.Models;

namespace UltraAudioEditor.Controls
{
    public class WaveformControl : FrameworkElement
    {
        // Svaka izmena projekta (sečenje, pomeranje, undo...) menja svojstva klipova
        // koja ne javljaju PropertyChanged — umesto da svaki model postane "pametan",
        // ViewModel posle svake izmene pozove WaveformControl.RefreshAll() i sve
        // vidljive trake se ponovo iscrtaju.
        private static event Action? RefreshRequested;
        public static void RefreshAll() => RefreshRequested?.Invoke();

        public WaveformControl()
        {
            Loaded += (_, __) => RefreshRequested += OnRefreshRequested;
            Unloaded += (_, __) => RefreshRequested -= OnRefreshRequested;
        }

        private void OnRefreshRequested() => Dispatcher.BeginInvoke(new Action(InvalidateVisual));

        public static readonly DependencyProperty ClipsProperty =
            DependencyProperty.Register(nameof(Clips), typeof(IEnumerable<AudioClip>), typeof(WaveformControl),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnCollectionPropertyChanged));

        public static readonly DependencyProperty MarkersProperty =
            DependencyProperty.Register(nameof(Markers), typeof(IEnumerable<double>), typeof(WaveformControl),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnCollectionPropertyChanged));

        public static readonly DependencyProperty SelectionStartProperty =
            DependencyProperty.Register(nameof(SelectionStart), typeof(double), typeof(WaveformControl),
                new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty SelectionEndProperty =
            DependencyProperty.Register(nameof(SelectionEnd), typeof(double), typeof(WaveformControl),
                new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
        public double SelectionStart { get => (double)GetValue(SelectionStartProperty); set => SetValue(SelectionStartProperty, value); }
        public double SelectionEnd { get => (double)GetValue(SelectionEndProperty); set => SetValue(SelectionEndProperty, value); }

        public static readonly DependencyProperty TrackColorProperty =
            DependencyProperty.Register(nameof(TrackColor), typeof(Color), typeof(WaveformControl),
                new FrameworkPropertyMetadata(Colors.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty PlayheadPositionProperty =
            DependencyProperty.Register(nameof(PlayheadPosition), typeof(double), typeof(WaveformControl),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ProjectDurationProperty =
            DependencyProperty.Register(nameof(ProjectDuration), typeof(double), typeof(WaveformControl),
                new FrameworkPropertyMetadata(30.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ZoomLevelProperty =
            DependencyProperty.Register(nameof(ZoomLevel), typeof(double), typeof(WaveformControl),
                new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public IEnumerable<AudioClip>? Clips { get => (IEnumerable<AudioClip>?)GetValue(ClipsProperty); set => SetValue(ClipsProperty, value); }
        public IEnumerable<double>? Markers { get => (IEnumerable<double>?)GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }
        public Color TrackColor { get => (Color)GetValue(TrackColorProperty); set => SetValue(TrackColorProperty, value); }
        public double PlayheadPosition { get => (double)GetValue(PlayheadPositionProperty); set => SetValue(PlayheadPositionProperty, value); }
        public double ProjectDuration { get => (double)GetValue(ProjectDurationProperty); set => SetValue(ProjectDurationProperty, value); }
        public double ZoomLevel { get => (double)GetValue(ZoomLevelProperty); set => SetValue(ZoomLevelProperty, value); }

        // Dodavanje/brisanje klipa ili markera u ObservableCollection ne menja samu
        // referencu kolekcije — zato se pretplaćujemo na CollectionChanged.
        private static void OnCollectionPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var wc = (WaveformControl)d;
            if (e.OldValue is INotifyCollectionChanged oldC) oldC.CollectionChanged -= wc.OnItemsChanged;
            if (e.NewValue is INotifyCollectionChanged newC) newC.CollectionChanged += wc.OnItemsChanged;
        }

        private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            // Pozadina trake
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(30, 30, 46)), null, new Rect(0, 0, w, h));

            double duration = Math.Max(1, ProjectDuration);
            double pixelsPerSec = w / duration * ZoomLevel;

            // Klipovi
            if (Clips != null)
            {
                foreach (var clip in Clips.ToList())
                {
                    double clipX = clip.StartTime * pixelsPerSec;
                    double clipW = clip.Duration * pixelsPerSec;
                    if (clipX > w || clipX + clipW < 0) continue;

                    // Pozadina klipa
                    var clipColor = Color.FromArgb(60, TrackColor.R, TrackColor.G, TrackColor.B);
                    dc.DrawRectangle(new SolidColorBrush(clipColor),
                        new Pen(new SolidColorBrush(TrackColor), 1),
                        new Rect(clipX, 1, Math.Max(2, clipW), h - 2));

                    // Waveform — WaveformData pokriva CEO izvorni fajl, a klip posle
                    // sečenja/trimovanja prikazuje samo deo [TrimStart, TrimStart+Duration].
                    // Ranije se ceo talas razvlačio preko svakog klipa, pa su oba dela
                    // posle reza izgledala isto.
                    if (clip.WaveformData != null && clip.WaveformData.Length > 0 && clip.Duration > 0)
                    {
                        var data = clip.WaveformData;
                        int pts = data.Length;
                        double srcDur = clip.SourceDuration > 0 ? clip.SourceDuration : clip.TrimStart + clip.Duration;
                        int i0 = (int)Math.Clamp(clip.TrimStart / srcDur * pts, 0, pts - 1);
                        int i1 = (int)Math.Clamp((clip.TrimStart + clip.Duration) / srcDur * pts, i0 + 1, pts);
                        int span = Math.Max(1, i1 - i0);

                        var pen = new Pen(new SolidColorBrush(Color.FromArgb(200, TrackColor.R, TrackColor.G, TrackColor.B)), 1);
                        var geo = new StreamGeometry();
                        using (var ctx = geo.Open())
                        {
                            for (int i = i0; i < i1; i++)
                            {
                                double x = clipX + ((i - i0) / (double)span) * clipW;
                                if (x < 0 || x > w) continue;
                                double amp = Math.Abs(data[i]);
                                double yTop = h / 2 - amp * (h / 2 - 4);
                                double yBot = h / 2 + amp * (h / 2 - 4);
                                ctx.BeginFigure(new Point(x, yTop), false, false);
                                ctx.LineTo(new Point(x, yBot), true, false);
                            }
                        }
                        dc.DrawGeometry(null, pen, geo);
                    }

                    // Naziv klipa
                    if (clipW > 40)
                    {
                        var ft = new FormattedText(clip.Name,
                            System.Globalization.CultureInfo.CurrentCulture,
                            FlowDirection.LeftToRight,
                            new Typeface("Segoe UI"),
                            11, Brushes.White, 96);
                        dc.DrawText(ft, new Point(clipX + 4, 3));
                    }
                }
            }

            // Izbor — svetlo plava providna oblast (I = početak, O = kraj)
            if (!double.IsNaN(SelectionStart) && !double.IsNaN(SelectionEnd) && SelectionEnd > SelectionStart)
            {
                double x1 = SelectionStart * pixelsPerSec, x2 = SelectionEnd * pixelsPerSec;
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(55, 120, 180, 255)),
                    new Pen(new SolidColorBrush(Color.FromArgb(160, 120, 180, 255)), 1),
                    new Rect(Math.Max(0, x1), 0, Math.Max(1, Math.Min(w, x2) - Math.Max(0, x1)), h));
            }

            // Markeri — žute isprekidane linije sa rednim brojem (isti brojevi kao u
            // pristupačnoj listi i u govornim porukama, npr. "Marker 2 od 4").
            if (Markers != null)
            {
                var markerBrush = new SolidColorBrush(Color.FromRgb(239, 191, 39));
                var markerPen = new Pen(markerBrush, 1.5) { DashStyle = DashStyles.Dash };
                int n = 1;
                foreach (var m in Markers.ToList())
                {
                    double mx = m * pixelsPerSec;
                    if (mx >= 0 && mx <= w)
                    {
                        dc.DrawLine(markerPen, new Point(mx, 0), new Point(mx, h));
                        var label = new FormattedText(n.ToString(),
                            System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                            new Typeface("Segoe UI"), 10, markerBrush, 96);
                        dc.DrawText(label, new Point(mx + 3, h - 15));
                    }
                    n++;
                }
            }

            // Playhead
            double phX = PlayheadPosition * pixelsPerSec;
            if (phX >= 0 && phX <= w)
            {
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(226, 75, 74)), 2),
                    new Point(phX, 0), new Point(phX, h));
                // Trokut na vrhu
                var tri = new StreamGeometry();
                using (var tctx = tri.Open())
                {
                    tctx.BeginFigure(new Point(phX - 6, 0), true, true);
                    tctx.LineTo(new Point(phX + 6, 0), true, false);
                    tctx.LineTo(new Point(phX, 10), true, false);
                }
                dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(226, 75, 74)), null, tri);
            }

            // Rešetka (grid lines za sekunde)
            var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 0.5);
            double step = pixelsPerSec > 40 ? 1 : pixelsPerSec > 10 ? 5 : 10;
            for (double t = 0; t <= duration; t += step)
            {
                double x = t * pixelsPerSec;
                if (x > w) break;
                dc.DrawLine(gridPen, new Point(x, 0), new Point(x, h));
            }
        }
    }
}
