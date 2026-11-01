using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using WControls = System.Windows.Controls;

namespace UltraVideoEditor
{
    /// <summary>
    /// v2.0 — the track mixer (Ctrl+Alt+M): for every track that holds sound, its volume,
    /// mute, solo, a three-band equaliser and a compressor. One heading per track, the
    /// controls under it in keyboard order.
    /// </summary>
    public class TrackMixerWindow : Window
    {
        private static string L(string k) => UiKit.L(k);
        private static string LF(string k, params object[] a) => UiKit.LF(k, a);

        private sealed class Row
        {
            public int Track;
            public WControls.Slider Volume, Low, Mid, High, Pan, Reverb;
            public WControls.CheckBox Mute, Solo, Comp, LowCut, Noise, DeEss, Limiter, ClearPoints;
            public System.Collections.Generic.List<VolumePoint> Points;
        }

        private readonly List<Row> _rows = new List<Row>();
        private readonly WControls.TextBlock _status;

        public TrackMixerWindow(Window owner, IEnumerable<int> tracks)
        {
            Owner = owner;
            Title = L("mix_title");
            Width = 600;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            SetResourceReference(BackgroundProperty, "ControlBg");

            var panel = new WControls.StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(UiKit.Text(L("mix_intro")));

            foreach (int track in tracks.Distinct().OrderBy(TrackSet.SortKey))
            {
                var m = TrackMixer.Current.FirstOrDefault(x => x.Track == track) ?? new TrackMix { Track = track };
                string name = TimelineExporter.TrackName(track);
                var head = UiKit.Text(name, 16);
                head.FontWeight = FontWeights.Bold;
                head.FontSize = 15;
                System.Windows.Automation.AutomationProperties.SetHeadingLevel(head, System.Windows.Automation.AutomationHeadingLevel.Level2);
                panel.Children.Add(head);

                var r = new Row { Track = track };
                r.Volume = UiKit.Slider(0, 200, Math.Clamp(m.Volume, 0, 200), 5);
                panel.Children.Add(UiKit.Labelled(LF("mix_lbl_volume", name), r.Volume));
                r.Mute = UiKit.Check(LF("mix_chk_mute", name), m.Mute);
                panel.Children.Add(r.Mute);
                r.Solo = UiKit.Check(LF("mix_chk_solo", name), m.Solo);
                panel.Children.Add(r.Solo);
                r.Low = UiKit.Slider(-12, 12, Math.Clamp(m.Low, -12, 12), 1);
                panel.Children.Add(UiKit.Labelled(LF("mix_lbl_low", name), r.Low));
                r.Mid = UiKit.Slider(-12, 12, Math.Clamp(m.Mid, -12, 12), 1);
                panel.Children.Add(UiKit.Labelled(LF("mix_lbl_mid", name), r.Mid));
                r.High = UiKit.Slider(-12, 12, Math.Clamp(m.High, -12, 12), 1);
                panel.Children.Add(UiKit.Labelled(LF("mix_lbl_high", name), r.High));
                r.Comp = UiKit.Check(LF("mix_chk_comp", name), m.Compressor);
                panel.Children.Add(r.Comp);
                // v2.0: more sound per track
                r.Pan = UiKit.Slider(-100, 100, Math.Clamp(m.Pan, -100, 100), 5);
                panel.Children.Add(UiKit.Labelled(LF("mix_lbl_pan", name), r.Pan));
                r.LowCut = UiKit.Check(LF("mix_chk_lowcut", name), m.LowCut);
                panel.Children.Add(r.LowCut);
                r.Noise = UiKit.Check(LF("mix_chk_noise", name), m.NoiseReduction);
                panel.Children.Add(r.Noise);
                r.DeEss = UiKit.Check(LF("mix_chk_deess", name), m.DeEsser);
                panel.Children.Add(r.DeEss);
                r.Reverb = UiKit.Slider(0, 100, Math.Clamp(m.Reverb, 0, 100), 5);
                panel.Children.Add(UiKit.Labelled(LF("mix_lbl_reverb", name), r.Reverb));
                r.Limiter = UiKit.Check(LF("mix_chk_limiter", name), m.Limiter);
                panel.Children.Add(r.Limiter);
                // v2.0: mixer automation (volume points are set from the timeline, Ctrl+Alt+Shift+V)
                r.Points = m.Points?.Select(p => new VolumePoint { Time = p.Time, Volume = p.Volume }).ToList();
                if (m.HasPoints)
                {
                    panel.Children.Add(UiKit.Text(LF("mix_points_info", name, m.Points.Count), 8));
                    r.ClearPoints = UiKit.Check(LF("mix_chk_clear_points", name), false);
                    panel.Children.Add(r.ClearPoints);
                }
                else panel.Children.Add(UiKit.Text(LF("mix_points_none", name), 8));
                _rows.Add(r);
            }

            _status = UiKit.Status();
            panel.Children.Add(_status);

            var buttons = new WControls.StackPanel { Orientation = WControls.Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            var ok = UiKit.Btn(L("mix_btn_ok"));
            ok.IsDefault = true;
            ok.Click += (s, e) => { Apply(); DialogResult = true; Close(); };
            var reset = UiKit.Btn(L("mix_btn_reset"));
            reset.Click += (s, e) =>
            {
                foreach (var r in _rows)
                {
                    r.Volume.Value = 100; r.Low.Value = 0; r.Mid.Value = 0; r.High.Value = 0;
                    r.Mute.IsChecked = false; r.Solo.IsChecked = false; r.Comp.IsChecked = false;
                    r.Pan.Value = 0; r.Reverb.Value = 0; r.LowCut.IsChecked = false; r.Noise.IsChecked = false;
                    r.DeEss.IsChecked = false; r.Limiter.IsChecked = false;
                    if (r.ClearPoints != null) r.ClearPoints.IsChecked = true;
                }
                UiKit.Announce(_status, L("mix_reset_done"));
            };
            var cancel = UiKit.Btn(L("mix_btn_cancel"));
            cancel.IsCancel = true;
            buttons.Children.Add(ok);
            buttons.Children.Add(reset);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);

            Content = new WControls.ScrollViewer { Content = panel, VerticalScrollBarVisibility = WControls.ScrollBarVisibility.Auto, MaxHeight = 860 };
            Loaded += (s, e) => { if (_rows.Count > 0) _rows[0].Volume.Focus(); };
        }

        private void Apply()
        {
            var list = TrackMixer.Current.Where(m => _rows.All(r => r.Track != m.Track)).ToList();
            foreach (var r in _rows)
            {
                var m = new TrackMix
                {
                    Track = r.Track, Volume = r.Volume.Value, Mute = r.Mute.IsChecked == true, Solo = r.Solo.IsChecked == true,
                    Low = r.Low.Value, Mid = r.Mid.Value, High = r.High.Value, Compressor = r.Comp.IsChecked == true,
                    Pan = r.Pan.Value, LowCut = r.LowCut.IsChecked == true, NoiseReduction = r.Noise.IsChecked == true,
                    DeEsser = r.DeEss.IsChecked == true, Reverb = r.Reverb.Value, Limiter = r.Limiter.IsChecked == true,
                    Points = r.ClearPoints?.IsChecked == true ? null : r.Points
                };
                if (!m.IsDefault) list.Add(m);
            }
            TrackMixer.Current = list;
        }
    }
}
