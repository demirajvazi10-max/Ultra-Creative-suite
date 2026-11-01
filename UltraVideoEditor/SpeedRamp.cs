using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace UltraVideoEditor
{
    /// <summary>v2.0 — one point of a speed ramp: at this time of the clip (seconds from its start on the timeline) the speed is this many percent.</summary>
    public class SpeedPoint
    {
        public double Time { get; set; }
        public double Speed { get; set; } = 100;
    }

    /// <summary>
    /// v2.0 — speed ramps: the speed changes smoothly inside one clip (from point to point,
    /// straight lines; before the first point the first speed, after the last the last one).
    /// The file time used at clip time u is S(u) = ∫ speed; FFmpeg's setpts gets the exact
    /// inverse (a square root per part), so the picture runs at exactly that speed.
    /// </summary>
    public static class SpeedRamp
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static string N(double v) => v.ToString("0.######", Inv);

        public static bool Has(TimelineItem i) => i?.VideoEffect?.SpeedPoints != null && i.VideoEffect.SpeedPoints.Count > 0 && !i.IsImage;

        /// <summary>Points as (time, speed factor), at least the clip's start and end, sorted.</summary>
        private static List<(double U, double V)> Knots(List<SpeedPoint> pts, double duration)
        {
            var p = pts.OrderBy(x => x.Time).Select(x => (U: Math.Clamp(x.Time, 0, duration), V: Math.Clamp(x.Speed, 10, 1000) / 100.0)).ToList();
            if (p.Count == 0) return new List<(double, double)> { (0, 1), (duration, 1) };
            if (p[0].U > 0.0001) p.Insert(0, (0, p[0].V));
            if (p[^1].U < duration - 0.0001) p.Add((duration, p[^1].V));
            return p;
        }

        /// <summary>The file seconds used from the clip's start up to clip time <paramref name="u"/> (not counting the in-point).</summary>
        public static double SourceAt(List<SpeedPoint> pts, double duration, double u)
        {
            var k = Knots(pts, duration);
            double s = 0;
            for (int i = 0; i + 1 < k.Count; i++)
            {
                double a = k[i].U, b = Math.Min(k[i + 1].U, u);
                if (b <= a) break;
                double va = k[i].V, slope = (k[i + 1].V - k[i].V) / Math.Max(1e-9, k[i + 1].U - k[i].U);
                double x = b - a;
                s += va * x + slope / 2 * x * x;
            }
            if (u > k[^1].U) s += k[^1].V * (u - k[^1].U);
            return s;
        }

        /// <summary>The average speed factor over the clip (for places that need one number).</summary>
        public static double Average(TimelineItem i) =>
            Has(i) && i.Duration > 0.01 ? SourceAt(i.VideoEffect.SpeedPoints, i.Duration, i.Duration) / i.Duration : (i?.VideoEffect?.Speed ?? 1);

        /// <summary>
        /// An expression (seconds) for setpts: the clip time minus <paramref name="uOffset"/>
        /// for input time T, where the input starts at file second <paramref name="sOffset"/>
        /// of the clip's part (S = sOffset + T).
        /// </summary>
        public static string InverseExpr(List<SpeedPoint> pts, double duration, double sOffset, double uOffset)
        {
            var k = Knots(pts, duration);
            string S = sOffset > 0.000001 ? $"(T+{N(sOffset)})" : "T";
            // after the last knot: straight at the last speed
            double sEnd = SourceAt(pts, duration, k[^1].U);
            string e = $"{N(k[^1].U)}+({S}-{N(sEnd)})/{N(k[^1].V)}";
            for (int i = k.Count - 2; i >= 0; i--)
            {
                double u0 = k[i].U, u1 = k[i + 1].U;
                double s0 = SourceAt(pts, duration, u0), s1 = SourceAt(pts, duration, u1);
                double a = k[i].V, b = (k[i + 1].V - k[i].V) / Math.Max(1e-9, u1 - u0);
                string ds = $"({S}-{N(s0)})";
                string seg = Math.Abs(b) < 1e-9
                    ? $"{N(u0)}+{ds}/{N(a)}"
                    : $"{N(u0)}+(sqrt(max(0,{N(a * a)}+{N(2 * b)}*{ds}))-{N(a)})/{N(b)}";
                e = $"if(lt({S},{N(s1)}),{seg},{e})";
            }
            return $"({e})-{N(uOffset)}";
        }

        /// <summary>A spoken list of the points.</summary>
        public static string Describe(TimelineItem i)
        {
            if (!Has(i)) return null;
            return string.Join(", ", i.VideoEffect.SpeedPoints.OrderBy(p => p.Time)
                                      .Select(p => $"{p.Time.ToString("0.0", CultureInfo.CurrentCulture)} s: {Math.Round(p.Speed)} %"));
        }

        /// <summary>Ready-made ramps for a clip of <paramref name="d"/> seconds.</summary>
        public static List<SpeedPoint> Preset(string key, double d) => key switch
        {
            "SpeedUp" => new List<SpeedPoint> { new SpeedPoint { Time = 0, Speed = 100 }, new SpeedPoint { Time = d, Speed = 300 } },
            "SlowDown" => new List<SpeedPoint> { new SpeedPoint { Time = 0, Speed = 100 }, new SpeedPoint { Time = d, Speed = 30 } },
            "SlowMiddle" => new List<SpeedPoint>
            {
                new SpeedPoint { Time = 0, Speed = 100 }, new SpeedPoint { Time = d * 0.35, Speed = 100 },
                new SpeedPoint { Time = d * 0.45, Speed = 25 }, new SpeedPoint { Time = d * 0.6, Speed = 25 },
                new SpeedPoint { Time = d * 0.7, Speed = 100 }, new SpeedPoint { Time = d, Speed = 100 }
            },
            "FastMiddle" => new List<SpeedPoint>
            {
                new SpeedPoint { Time = 0, Speed = 100 }, new SpeedPoint { Time = d * 0.3, Speed = 100 },
                new SpeedPoint { Time = d * 0.5, Speed = 400 }, new SpeedPoint { Time = d * 0.7, Speed = 100 },
                new SpeedPoint { Time = d, Speed = 100 }
            },
            _ => new List<SpeedPoint>()
        };

        public static readonly string[] Presets = { "SpeedUp", "SlowDown", "SlowMiddle", "FastMiddle" };
    }
}

namespace UltraVideoEditor
{
    using System.Windows;
    using WControls = System.Windows.Controls;

    /// <summary>
    /// v2.0 — "Speed ramp" (Ctrl+Alt+Shift+S) for the selected video clip: ready shapes (speed
    /// up, slow down, slow or fast in the middle) or points set at the playhead; every change
    /// is spoken with how much of the file the clip now uses.
    /// </summary>
    public class SpeedRampWindow : Window
    {
        private static string L(string k) => UiKit.L(k);
        private static string LF(string k, params object[] a) => UiKit.LF(k, a);

        private readonly TimelineItem _item;
        private readonly double _posInClip;
        private readonly List<SpeedPoint> _points;
        private readonly WControls.ComboBox _preset;
        private readonly WControls.Slider _speed;
        private readonly WControls.TextBlock _list, _status;

        public string Summary { get; private set; } = "";

        public SpeedRampWindow(Window owner, TimelineItem item, double positionInClip)
        {
            Owner = owner;
            _item = item;
            _posInClip = Math.Clamp(positionInClip, 0, item.Duration);
            _points = item.VideoEffect?.SpeedPoints?.Select(p => new SpeedPoint { Time = p.Time, Speed = p.Speed }).ToList() ?? new List<SpeedPoint>();
            Title = LF("sr_title", item.Name ?? "");
            Width = 560;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            SetResourceReference(BackgroundProperty, "ControlBg");

            var panel = new WControls.StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(UiKit.Text(L("sr_intro")));
            _list = UiKit.Text("", 8);
            panel.Children.Add(_list);

            _preset = UiKit.Combo(SpeedRamp.Presets.Select(p => (L("sr_preset_" + p), (object)p)).ToArray());
            panel.Children.Add(UiKit.Labelled(L("sr_lbl_preset"), _preset));
            var usePreset = UiKit.Btn(L("sr_btn_preset"));
            usePreset.Click += (s, e) =>
            {
                _points.Clear();
                _points.AddRange(SpeedRamp.Preset(UiKit.Tag(_preset) as string, _item.Duration));
                Refresh(true);
            };
            panel.Children.Add(usePreset);

            double now = 100;
            if (_points.Count > 0)
            {
                // the speed the ramp has at the playhead now
                var k = _points.OrderBy(p => p.Time).ToList();
                now = _posInClip <= k[0].Time ? k[0].Speed : _posInClip >= k[^1].Time ? k[^1].Speed
                    : k.Zip(k.Skip(1)).Where(z => _posInClip >= z.First.Time && _posInClip <= z.Second.Time)
                       .Select(z => z.First.Speed + (z.Second.Speed - z.First.Speed) * (_posInClip - z.First.Time) / Math.Max(1e-6, z.Second.Time - z.First.Time)).First();
            }
            _speed = UiKit.Slider(10, 400, Math.Round(Math.Clamp(now, 10, 400) / 5) * 5, 5);
            panel.Children.Add(UiKit.Labelled(LF("sr_lbl_speed", _posInClip.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture)), _speed));
            var setPoint = UiKit.Btn(L("sr_btn_point"));
            setPoint.Click += (s, e) =>
            {
                var near = _points.FirstOrDefault(p => Math.Abs(p.Time - _posInClip) < 0.05);
                if (near != null) near.Speed = _speed.Value;
                else _points.Add(new SpeedPoint { Time = _posInClip, Speed = _speed.Value });
                Refresh(true);
            };
            panel.Children.Add(setPoint);
            var clear = UiKit.Btn(L("sr_btn_clear"));
            clear.Click += (s, e) => { _points.Clear(); Refresh(true); };
            panel.Children.Add(clear);

            _status = UiKit.Status();
            panel.Children.Add(_status);
            var buttons = new WControls.StackPanel { Orientation = WControls.Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            var ok = UiKit.Btn(L("sr_btn_ok"));
            ok.IsDefault = true;
            ok.Click += (s, e) =>
            {
                _item.VideoEffect ??= new VideoEffectData();
                _item.VideoEffect.SpeedPoints = _points.Count > 0 ? _points.OrderBy(p => p.Time).ToList() : null;
                Summary = _points.Count > 0 ? LF("sr_saved", _points.Count, Used()) : L("sr_removed");
                DialogResult = true;
                Close();
            };
            var cancel = UiKit.Btn(L("sr_btn_cancel"));
            cancel.IsCancel = true;
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);

            Content = new WControls.ScrollViewer { Content = panel, VerticalScrollBarVisibility = WControls.ScrollBarVisibility.Auto, MaxHeight = 860 };
            Refresh(false);
            Loaded += (s, e) => _preset.Focus();
        }

        private string Used() => SpeedRamp.SourceAt(_points, _item.Duration, _item.Duration).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture);

        private void Refresh(bool say)
        {
            string text = _points.Count == 0
                ? L("sr_none")
                : LF("sr_points", string.Join(", ", _points.OrderBy(p => p.Time)
                        .Select(p => LF("sr_point", p.Time.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture), Math.Round(p.Speed)))), Used(),
                     _item.Duration.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture));
            _list.Text = text;
            if (say) UiKit.Announce(_status, text);
        }
    }
}
