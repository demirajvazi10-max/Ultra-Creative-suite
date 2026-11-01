using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using WControls = System.Windows.Controls;

namespace UltraVideoEditor
{
    /// <summary>
    /// v2.0 — "Clip effects" (Ctrl+Alt+E) for the selected clip, in three parts that follow
    /// the keyboard order:
    ///   • Colour: brightness, contrast, saturation, temperature, tint, mid-tones, shadows,
    ///     highlights, vignette, sharpness;
    ///   • Picture: stabilisation, speed and smooth slow motion (video clips);
    ///   • Sound: three-band equaliser, noise reduction, clearer voice, compressor, fades.
    /// Every slider is labelled and reads its value; nothing here needs a mouse.
    /// </summary>
    public class ClipEffectsWindow : Window
    {
        private static string L(string k) => UiKit.L(k);
        private static string LF(string k, params object[] a) => UiKit.LF(k, a);

        private readonly TimelineItem _item;
        private readonly WControls.Slider _bright, _contrast, _sat, _temp, _tint, _gamma, _shadows, _highlights, _vignette, _sharpen;
        private readonly WControls.Slider _stabStrength, _speed;
        private readonly WControls.Slider _sR, _sG, _sB, _mR, _mG, _mB, _hR, _hG, _hB;
        private readonly WControls.ComboBox _selColor;
        private readonly WControls.Slider _selHue, _selSat, _selBright;
        private readonly WControls.CheckBox _selKeep;
        private readonly WControls.ComboBox _pwShape;                                        // v2.0 power window
        private readonly WControls.Slider _pwX, _pwY, _pwW, _pwH, _pwFeather;
        private readonly WControls.CheckBox _pwInvert;
        private readonly WControls.CheckBox _stabilize, _smooth;
        private readonly WControls.Slider _low, _mid, _high, _fadeIn, _fadeOut;
        private readonly WControls.ComboBox _noise;
        private readonly WControls.CheckBox _voice, _compressor, _toTrack;
        private readonly WControls.TextBlock _status;

        // v2.0: curves, a colour picked from the picture, spoken colour analysis, shot matching
        private readonly double _fileTime;
        private readonly IList<TimelineItem> _others;
        private readonly WControls.ComboBox _curveChannel, _curvePreset, _pickRegion, _matchTo;
        private readonly WControls.Slider[] _curve = new WControls.Slider[5];
        private readonly Dictionary<string, double[]> _curves = new Dictionary<string, double[]>();
        private readonly WControls.Slider _similarity;
        private readonly WControls.Button _pick, _analyse, _match;
        private string _picked = "";
        private bool _loadingCurve;
        private CancellationTokenSource _cts;

        /// <summary>True when the colour settings should also go to every picture clip on the same track.</summary>
        public bool ApplyColourToTrack => _toTrack?.IsChecked == true;

        public ClipEffectsWindow(Window owner, TimelineItem item, double fileTimeAtPlayhead = -1, IList<TimelineItem> others = null)
        {
            Owner = owner;
            _item = item;
            _fileTime = fileTimeAtPlayhead;
            _others = others ?? new List<TimelineItem>();
            Title = LF("cfx_title", item.Name ?? "");
            Width = 620;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            SetResourceReference(BackgroundProperty, "ControlBg");

            var v = item.VideoEffect ?? new VideoEffectData();
            var a = item.AudioFx ?? new AudioSettingsData();
            bool picture = !(item.Type == "Audio" || item.IsAudio);
            bool video = picture && !item.IsImage;
            bool sound = item.IsAudio || item.Type == "Audio" || item.IsVideo;

            var panel = new WControls.StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(UiKit.Text(L("cfx_intro")));

            // ── colour ──
            panel.Children.Add(Heading(L("cfx_head_colour")));
            _bright = Add(panel, "cfx_lbl_brightness", -100, 100, v.Brightness, 1);
            _contrast = Add(panel, "cfx_lbl_contrast", -100, 100, v.Contrast, 1);
            _sat = Add(panel, "cfx_lbl_saturation", -100, 100, v.Saturation, 1);
            _temp = Add(panel, "cfx_lbl_temperature", -100, 100, v.Temperature, 1);
            _tint = Add(panel, "cfx_lbl_tint", -100, 100, v.Tint, 1);
            _gamma = Add(panel, "cfx_lbl_gamma", -100, 100, v.Gamma, 1);
            _shadows = Add(panel, "cfx_lbl_shadows", -100, 100, v.Shadows, 1);
            _highlights = Add(panel, "cfx_lbl_highlights", -100, 100, v.Highlights, 1);
            _vignette = Add(panel, "cfx_lbl_vignette", 0, 100, v.Vignette, 1);
            _sharpen = Add(panel, "cfx_lbl_sharpen", 0, 100, v.Sharpen, 1);
            // v2.0: colour balance per tonal range (what colour wheels do)
            panel.Children.Add(Heading(L("cfx_head_balance")));
            panel.Children.Add(UiKit.Text(L("cfx_balance_help")));
            _sR = Add(panel, "cfx_lbl_sr", -100, 100, v.ShadowR, 1);
            _sG = Add(panel, "cfx_lbl_sg", -100, 100, v.ShadowG, 1);
            _sB = Add(panel, "cfx_lbl_sb", -100, 100, v.ShadowB, 1);
            _mR = Add(panel, "cfx_lbl_mr", -100, 100, v.MidR, 1);
            _mG = Add(panel, "cfx_lbl_mg", -100, 100, v.MidG, 1);
            _mB = Add(panel, "cfx_lbl_mb", -100, 100, v.MidB, 1);
            _hR = Add(panel, "cfx_lbl_hr", -100, 100, v.HighR, 1);
            _hG = Add(panel, "cfx_lbl_hg", -100, 100, v.HighG, 1);
            _hB = Add(panel, "cfx_lbl_hb", -100, 100, v.HighB, 1);

            // v2.0: curves — five points per channel
            panel.Children.Add(Heading(L("cfx_head_curves")));
            panel.Children.Add(UiKit.Text(L("cfx_curves_help")));
            _curves["All"] = Curves.Parse(v.CurveAll); _curves["R"] = Curves.Parse(v.CurveR);
            _curves["G"] = Curves.Parse(v.CurveG); _curves["B"] = Curves.Parse(v.CurveB);
            _curveChannel = UiKit.Combo((L("cfx_curve_all"), "All"), (L("cfx_curve_r"), "R"), (L("cfx_curve_g"), "G"), (L("cfx_curve_b"), "B"));
            panel.Children.Add(UiKit.Labelled(L("cfx_lbl_curve_channel"), _curveChannel));
            _curvePreset = UiKit.Combo(new[] { (L("cfx_curve_preset_choose"), (object)"") }
                .Concat(Curves.Presets.Select(p => (L("cfx_curve_" + p.Key), (object)p.Key))).ToArray());
            panel.Children.Add(UiKit.Labelled(L("cfx_lbl_curve_preset"), _curvePreset));
            string[] pointKeys = { "cfx_lbl_curve_0", "cfx_lbl_curve_25", "cfx_lbl_curve_50", "cfx_lbl_curve_75", "cfx_lbl_curve_100" };
            for (int i = 0; i < 5; i++)
            {
                int k = i;
                _curve[i] = Add(panel, pointKeys[i], 0, 100, _curves["All"][i], 1);
                _curve[i].ValueChanged += (s, e) => { if (!_loadingCurve) _curves[CurveKey][k] = _curve[k].Value; };
            }
            _curveChannel.SelectionChanged += (s, e) => LoadCurve();
            _curvePreset.SelectionChanged += (s, e) =>
            {
                string key = UiKit.Tag(_curvePreset) as string ?? "";
                var preset = Curves.Presets.FirstOrDefault(p => p.Key == key);
                if (preset.Points == null) return;
                _curves[CurveKey] = (double[])preset.Points.Clone();
                LoadCurve();
                UiKit.Announce(_status, LF("cfx_curve_applied", L("cfx_curve_" + key), ((WControls.ComboBoxItem)_curveChannel.SelectedItem).Content));
            };

            // v2.0: one colour only
            panel.Children.Add(Heading(L("cfx_head_onecolour")));
            _selColor = UiKit.Combo((L("cfx_sel_off"), ""), (L("cfx_sel_r"), "r"), (L("cfx_sel_y"), "y"), (L("cfx_sel_g"), "g"),
                                    (L("cfx_sel_c"), "c"), (L("cfx_sel_b"), "b"), (L("cfx_sel_m"), "m"));
            UiKit.Select(_selColor, v.SelColor ?? "");
            panel.Children.Add(UiKit.Labelled(L("cfx_lbl_selcolor"), _selColor));
            _selHue = Add(panel, "cfx_lbl_selhue", -180, 180, v.SelHue, 5);
            _selSat = Add(panel, "cfx_lbl_selsat", -100, 100, v.SelSaturation, 1);
            _selBright = Add(panel, "cfx_lbl_selbright", -100, 100, v.SelBrightness, 1);
            _selKeep = UiKit.Check(L("cfx_chk_selkeep"), v.SelKeepOnly);
            panel.Children.Add(_selKeep);
            _selColor.SelectionChanged += (s, e) => UpdateEnabled();
            // v2.0: pick the colour from the picture (the frame at the playhead, or the clip's middle)
            _picked = v.SelPicked ?? "";
            _pickRegion = UiKit.Combo(ObjectTracker.Regions.Select(r => (L("obj_reg_" + r), (object)r)).ToArray());
            panel.Children.Add(UiKit.Labelled(L("cfx_lbl_pick_region"), _pickRegion));
            _pick = UiKit.Btn(L("cfx_btn_pick"));
            _pick.Click += async (s, e) => await PickAsync();
            panel.Children.Add(_pick);
            _similarity = Add(panel, "cfx_lbl_similarity", 5, 60, v.SelSimilarity, 1);
            if (ColorAnalyzer.TryParseHex(_picked, out int pr, out int pg, out int pb))
                panel.Children.Add(UiKit.Text(LF("cfx_picked_now", ColorAnalyzer.NameColour(pr, pg, pb)), 4));

            // v2.0: power window — the corrections above only inside (or outside) one area
            panel.Children.Add(Heading(L("pw_head")));
            panel.Children.Add(UiKit.Text(L("pw_help")));
            _pwShape = UiKit.Combo(new[] { (L("pw_shape_off"), (object)"") }
                                   .Concat(PowerWindow.Shapes.Select(sh => (L("pw_shape_" + sh), (object)sh))).ToArray());
            UiKit.Select(_pwShape, v.PwShape ?? "");
            panel.Children.Add(UiKit.Labelled(L("pw_lbl_shape"), _pwShape));
            _pwX = Add(panel, "pw_lbl_x", 0, 100, v.PwX, 1);
            _pwY = Add(panel, "pw_lbl_y", 0, 100, v.PwY, 1);
            _pwW = Add(panel, "pw_lbl_w", 1, 200, v.PwW, 1);
            _pwH = Add(panel, "pw_lbl_h", 1, 200, v.PwH, 1);
            _pwFeather = Add(panel, "pw_lbl_feather", 0, 100, v.PwFeather, 1);
            _pwInvert = UiKit.Check(L("pw_chk_invert"), v.PwInvert);
            panel.Children.Add(_pwInvert);
            _pwShape.SelectionChanged += (s, e) => UpdateWindowEnabled(true);

            // v2.0: spoken colour analysis instead of scopes, and shot matching
            panel.Children.Add(Heading(L("cfx_head_analyse")));
            panel.Children.Add(UiKit.Text(L("cfx_analyse_help")));
            _analyse = UiKit.Btn(L("cfx_btn_analyse"));
            _analyse.Click += async (s, e) => await AnalyseAsync();
            panel.Children.Add(_analyse);
            var matchItems = _others.Where(o => !ReferenceEquals(o, item) && !(o.Type == "Audio" || o.IsAudio) && !string.IsNullOrEmpty(o.Path))
                                    .Select(o => ($"{TrackSet.Name(o.TrackIndex)}: {o.Name}", (object)o)).ToArray();
            _matchTo = matchItems.Length > 0 ? UiKit.Combo(matchItems) : UiKit.Combo((L("cfx_match_none"), (object)null));
            panel.Children.Add(UiKit.Labelled(L("cfx_lbl_match_to"), _matchTo));
            _match = UiKit.Btn(L("cfx_btn_match"));
            _match.Click += async (s, e) => await MatchAsync();
            _match.IsEnabled = matchItems.Length > 0;
            panel.Children.Add(_match);
            Closing += (s, e) => _cts?.Cancel();

            _toTrack = UiKit.Check(L("cfx_chk_to_track"), false);
            panel.Children.Add(_toTrack);
            foreach (var s in new[] { _bright, _contrast, _sat, _temp, _tint, _gamma, _shadows, _highlights, _vignette, _sharpen,
                                      _sR, _sG, _sB, _mR, _mG, _mB, _hR, _hG, _hB })
                s.IsEnabled = picture;
            _selColor.IsEnabled = picture;
            _pwShape.IsEnabled = picture;
            UpdateWindowEnabled(false);
            _toTrack.IsEnabled = picture;
            foreach (var s in _curve) s.IsEnabled = picture;
            _curveChannel.IsEnabled = _curvePreset.IsEnabled = _pickRegion.IsEnabled = _pick.IsEnabled = _analyse.IsEnabled = picture;
            _matchTo.IsEnabled = picture;
            if (!picture) _match.IsEnabled = false;

            // ── picture ──
            panel.Children.Add(Heading(L("cfx_head_picture")));
            _stabilize = UiKit.Check(L("cfx_chk_stabilize"), v.Stabilize);
            panel.Children.Add(_stabilize);
            _stabStrength = Add(panel, "cfx_lbl_stab_strength", 1, 10, Math.Clamp(v.StabilizeStrength, 1, 10), 1);
            _speed = Add(panel, "cfx_lbl_speed", 25, 400, Math.Round(Math.Clamp(v.Speed, 0.25, 4) * 100), 5);
            _smooth = UiKit.Check(L("cfx_chk_smooth"), v.SmoothSlowMotion);
            panel.Children.Add(_smooth);
            _stabilize.IsEnabled = _speed.IsEnabled = video;
            _stabilize.Checked += (s, e) => UpdateEnabled();
            _stabilize.Unchecked += (s, e) => UpdateEnabled();
            _speed.ValueChanged += (s, e) => UpdateEnabled();

            // ── sound ──
            panel.Children.Add(Heading(L("cfx_head_sound")));
            _low = Add(panel, "cfx_lbl_low", -12, 12, a.LowGain, 1);
            _mid = Add(panel, "cfx_lbl_mid", -12, 12, a.MidGain, 1);
            _high = Add(panel, "cfx_lbl_high", -12, 12, a.HighGain, 1);
            _noise = UiKit.Combo((L("cfx_noise_off"), 0), (L("cfx_noise_light"), 1), (L("cfx_noise_strong"), 2));
            UiKit.Select(_noise, Math.Clamp(a.NoiseReduction, 0, 2));
            panel.Children.Add(UiKit.Labelled(L("cfx_lbl_noise"), _noise));
            _voice = UiKit.Check(L("cfx_chk_voice"), a.VoiceEnhance);
            panel.Children.Add(_voice);
            _compressor = UiKit.Check(L("cfx_chk_compressor"), a.CompressorEnabled);
            panel.Children.Add(_compressor);
            _fadeIn = Add(panel, "cfx_lbl_fade_in", 0, 10, a.FadeIn, 0.5);
            _fadeOut = Add(panel, "cfx_lbl_fade_out", 0, 10, a.FadeOut, 0.5);
            foreach (var c in new WControls.Control[] { _low, _mid, _high, _noise, _voice, _compressor, _fadeIn, _fadeOut })
                c.IsEnabled = sound;

            _status = UiKit.Status();
            panel.Children.Add(_status);

            var buttons = new WControls.StackPanel { Orientation = WControls.Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            var ok = UiKit.Btn(L("cfx_btn_ok"));
            ok.IsDefault = true;
            ok.Click += (s, e) => { Apply(); DialogResult = true; Close(); };
            var reset = UiKit.Btn(L("cfx_btn_reset"));
            reset.Click += (s, e) => ResetAll();
            var cancel = UiKit.Btn(L("cfx_btn_cancel"));
            cancel.IsCancel = true;
            buttons.Children.Add(ok);
            buttons.Children.Add(reset);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);

            UpdateEnabled();
            Content = new WControls.ScrollViewer { Content = panel, VerticalScrollBarVisibility = WControls.ScrollBarVisibility.Auto, MaxHeight = 860 };
            Loaded += (s, e) => (picture ? (UIElement)_bright : _low).Focus();
        }

        private static WControls.TextBlock Heading(string text)
        {
            var t = UiKit.Text(text, 16);
            t.FontWeight = FontWeights.Bold;
            t.FontSize = 15;
            System.Windows.Automation.AutomationProperties.SetHeadingLevel(t, System.Windows.Automation.AutomationHeadingLevel.Level2);
            return t;
        }

        private static WControls.Slider Add(WControls.Panel panel, string key, double min, double max, double value, double step)
        {
            var s = UiKit.Slider(min, max, Math.Clamp(value, min, max), step);
            panel.Children.Add(UiKit.Labelled(L(key), s));
            return s;
        }

        private string CurveKey => UiKit.Tag(_curveChannel) as string ?? "All";

        private void LoadCurve()
        {
            _loadingCurve = true;
            try { var p = _curves[CurveKey]; for (int i = 0; i < 5; i++) _curve[i].Value = p[i]; }
            finally { _loadingCurve = false; }
        }

        private double ClipSpeed => _item.IsImage ? 1 : Math.Clamp(_item.VideoEffect?.Speed is double sp && sp > 0.01 ? sp : 1, 0.1, 10);

        /// <summary>The file time to look at: the playhead when it is in the clip, else the clip's middle.</summary>
        private double LookTime => _fileTime >= 0 ? _fileTime : _item.SourceOffset + _item.Duration * ClipSpeed / 2;

        private void Busy(bool on)
        {
            _pick.IsEnabled = _analyse.IsEnabled = !on;
            _match.IsEnabled = !on && UiKit.Tag(_matchTo) is TimelineItem;
        }

        private async System.Threading.Tasks.Task PickAsync()
        {
            if (_item.Type == "Audio" || _item.IsAudio || string.IsNullOrEmpty(_item.Path)) return;
            _cts = new CancellationTokenSource();
            Busy(true);
            try
            {
                string region = UiKit.Tag(_pickRegion) as string ?? "Center";
                var c = await ColorAnalyzer.PickAsync(_item.Path, LookTime, _item.IsImage, region, _cts.Token);
                if (c == null) { UiKit.Announce(_status, L("col_no_picture")); return; }
                var (r, g, b) = c.Value;
                _picked = $"#{r:X2}{g:X2}{b:X2}";
                string range = ColorAnalyzer.RangeOf(r, g, b);
                if (range.Length == 0)
                {
                    UiKit.Announce(_status, LF("cfx_picked_grey", L("obj_reg_" + region), ColorAnalyzer.NameColour(r, g, b)));
                    return;
                }
                UiKit.Select(_selColor, range);
                UiKit.Announce(_status, LF("cfx_picked", L("obj_reg_" + region), ColorAnalyzer.NameColour(r, g, b),
                                           ((WControls.ComboBoxItem)_selColor.SelectedItem).Content));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppLog.Error("Picking a colour", ex); UiKit.Announce(_status, ex.Message); }
            finally { Busy(false); _cts?.Dispose(); _cts = null; }
        }

        /// <summary>The colour settings as they are in the window now (not saved yet).</summary>
        private VideoEffectData Current()
        {
            var v = (_item.VideoEffect ?? new VideoEffectData()).Clone();
            WriteColour(v);
            return v;
        }

        private async System.Threading.Tasks.Task AnalyseAsync()
        {
            if (_item.Type == "Audio" || _item.IsAudio || string.IsNullOrEmpty(_item.Path)) return;
            _cts = new CancellationTokenSource();
            Busy(true);
            try
            {
                UiKit.Announce(_status, L("cfx_analysing"));
                string vf = VideoEffects.GetFFmpegFilter(Current(), includeSpeed: false);
                var times = _fileTime >= 0 ? new List<double> { _fileTime } : ColorAnalyzer.SampleTimes(_item);
                var stats = await ColorAnalyzer.MeasureAsync(_item.Path, times, _item.IsImage, vf, _cts.Token);
                UiKit.Announce(_status, (_fileTime >= 0 ? L("cfx_analysis_here") : L("cfx_analysis_clip")) + " " + ColorAnalyzer.Describe(stats));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppLog.Error("Colour analysis", ex); UiKit.Announce(_status, ex.Message); }
            finally { Busy(false); _cts?.Dispose(); _cts = null; }
        }

        private async System.Threading.Tasks.Task MatchAsync()
        {
            if (!(UiKit.Tag(_matchTo) is TimelineItem target)) return;
            _cts = new CancellationTokenSource();
            Busy(true);
            try
            {
                UiKit.Announce(_status, LF("cfx_matching", target.Name ?? ""));
                var want = await ColorAnalyzer.MeasureAsync(target.Path, ColorAnalyzer.SampleTimes(target), target.IsImage,
                                                            VideoEffects.GetFFmpegFilter(target.VideoEffect, includeSpeed: false), _cts.Token);
                var cur = Current();
                var before = await ColorAnalyzer.MeasureAsync(_item.Path, ColorAnalyzer.SampleTimes(_item), _item.IsImage,
                                                              VideoEffects.GetFFmpegFilter(cur, includeSpeed: false), _cts.Token);
                if (want == null || before == null) { UiKit.Announce(_status, L("col_no_picture")); return; }
                string diff = ColorAnalyzer.Compare(before, want);
                var after = await ColorAnalyzer.MatchAsync(_item, cur, want, n => Dispatcher.BeginInvoke(new Action(() =>
                    UiKit.Announce(_status, LF("cfx_match_round", n)))), _cts.Token);
                _bright.Value = cur.Brightness; _contrast.Value = cur.Contrast; _sat.Value = cur.Saturation;
                _temp.Value = cur.Temperature; _tint.Value = cur.Tint;
                UiKit.Announce(_status, LF("cfx_matched", target.Name ?? "", diff, Math.Round(cur.Brightness), Math.Round(cur.Contrast),
                                           Math.Round(cur.Saturation), Math.Round(cur.Temperature), Math.Round(cur.Tint),
                                           ColorAnalyzer.Compare(after, want)));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppLog.Error("Matching colour", ex); UiKit.Announce(_status, ex.Message); }
            finally { Busy(false); _cts?.Dispose(); _cts = null; }
        }

        private void UpdateEnabled()
        {
            bool video = !(_item.Type == "Audio" || _item.IsAudio) && !_item.IsImage;
            _stabStrength.IsEnabled = video && _stabilize.IsChecked == true;
            _smooth.IsEnabled = video && _speed.Value < 99.5;
            bool picture = !(_item.Type == "Audio" || _item.IsAudio);
            bool sel = picture && !string.IsNullOrEmpty(UiKit.Tag(_selColor) as string);
            _selHue.IsEnabled = _selSat.IsEnabled = _selBright.IsEnabled = _selKeep.IsEnabled = sel;
            if (_similarity != null) _similarity.IsEnabled = sel && !string.IsNullOrEmpty(_picked);
        }

        private void ResetAll()
        {
            foreach (var s in new[] { _bright, _contrast, _sat, _temp, _tint, _gamma, _shadows, _highlights, _vignette, _sharpen, _low, _mid, _high, _fadeIn, _fadeOut,
                                      _sR, _sG, _sB, _mR, _mG, _mB, _hR, _hG, _hB, _selHue, _selSat, _selBright })
                s.Value = s.Minimum < 0 ? 0 : s.Minimum;
            UiKit.Select(_selColor, ""); _selKeep.IsChecked = false;
            _picked = ""; _similarity.Value = 25;
            foreach (var k in _curves.Keys.ToList()) _curves[k] = (double[])Curves.Straight.Clone();
            LoadCurve();
            _stabilize.IsChecked = false; _stabStrength.Value = 5; _speed.Value = 100; _smooth.IsChecked = false;
            UiKit.Select(_noise, 0); _voice.IsChecked = false; _compressor.IsChecked = false;
            UiKit.Announce(_status, L("cfx_reset_done"));
        }

        private void Apply()
        {
            var v = _item.VideoEffect ??= new VideoEffectData();
            WriteColour(v);
            if (_stabilize.IsEnabled)
            {
                v.Stabilize = _stabilize.IsChecked == true;
                v.StabilizeStrength = _stabStrength.Value;
                v.Speed = Math.Round(_speed.Value) / 100.0;
                v.SmoothSlowMotion = _smooth.IsChecked == true;
            }
            if (_low.IsEnabled)
            {
                var a = _item.AudioFx ??= new AudioSettingsData();
                a.LowGain = _low.Value; a.MidGain = _mid.Value; a.HighGain = _high.Value;
                a.NoiseReduction = UiKit.Tag(_noise) is int n ? n : 0;
                a.VoiceEnhance = _voice.IsChecked == true;
                a.CompressorEnabled = _compressor.IsChecked == true;
                a.FadeIn = _fadeIn.Value; a.FadeOut = _fadeOut.Value;
            }
        }

        /// <summary>
        /// v2.0 — only the sliders that matter for the chosen window shape are enabled (a band
        /// at the top has a height, not a centre); the change is spoken.
        /// </summary>
        private void UpdateWindowEnabled(bool say)
        {
            string sh = UiKit.Tag(_pwShape) as string ?? "";
            bool on = _pwShape.IsEnabled && sh.Length > 0, box = sh is "Oval" or "Rectangle";
            _pwX.IsEnabled = _pwY.IsEnabled = on && box;
            _pwW.IsEnabled = on && (box || sh is "Left" or "Right");
            _pwH.IsEnabled = on && (box || sh is "Top" or "Bottom");
            _pwFeather.IsEnabled = _pwInvert.IsEnabled = on;
            if (say) UiKit.Announce(_status, on ? L("pw_say_" + (box ? "box" : sh is "Top" or "Bottom" ? "height" : "width")) : L("pw_say_off"));
        }

        /// <summary>v2.0 — the colour part of the window into <paramref name="v"/>.</summary>
        private void WriteColour(VideoEffectData v)
        {
            v.Brightness = _bright.Value; v.Contrast = _contrast.Value; v.Saturation = _sat.Value;
            v.Temperature = _temp.Value; v.Tint = _tint.Value; v.Gamma = _gamma.Value;
            v.Shadows = _shadows.Value; v.Highlights = _highlights.Value; v.Vignette = _vignette.Value; v.Sharpen = _sharpen.Value;
            v.ShadowR = _sR.Value; v.ShadowG = _sG.Value; v.ShadowB = _sB.Value;
            v.MidR = _mR.Value; v.MidG = _mG.Value; v.MidB = _mB.Value;
            v.HighR = _hR.Value; v.HighG = _hG.Value; v.HighB = _hB.Value;
            v.SelColor = UiKit.Tag(_selColor) as string ?? "";
            v.SelHue = _selHue.Value; v.SelSaturation = _selSat.Value; v.SelBrightness = _selBright.Value;
            v.SelKeepOnly = _selKeep.IsChecked == true;
            v.SelPicked = string.IsNullOrEmpty(v.SelColor) ? "" : _picked;
            v.SelSimilarity = _similarity.Value;
            v.PwShape = UiKit.Tag(_pwShape) as string ?? "";
            v.PwX = _pwX.Value; v.PwY = _pwY.Value; v.PwW = _pwW.Value; v.PwH = _pwH.Value;
            v.PwFeather = _pwFeather.Value; v.PwInvert = _pwInvert.IsChecked == true;
            v.CurveAll = Curves.Format(_curves["All"]); v.CurveR = Curves.Format(_curves["R"]);
            v.CurveG = Curves.Format(_curves["G"]); v.CurveB = Curves.Format(_curves["B"]);
        }

        /// <summary>A short spoken summary of what is set on the clip.</summary>
        public static string Describe(TimelineItem item)
        {
            var parts = new System.Collections.Generic.List<string>();
            string colour = VideoEffects.Describe(item.VideoEffect);
            if (colour.Length > 0) parts.Add(colour);
            var v = item.VideoEffect;
            if (v != null)
            {
                if (v.Stabilize) parts.Add(L("cfx_d_stabilized"));
                if (Math.Abs(v.Speed - 1) > 0.001) parts.Add(LF("cfx_d_speed", Math.Round(v.Speed * 100)));
                if (v.SmoothSlowMotion && v.Speed < 0.999) parts.Add(L("cfx_d_smooth"));
            }
            var a = item.AudioFx;
            if (a != null)
            {
                if (a.LowGain != 0 || a.MidGain != 0 || a.HighGain != 0) parts.Add(LF("cfx_d_eq", Math.Round(a.LowGain), Math.Round(a.MidGain), Math.Round(a.HighGain)));
                if (a.NoiseReduction > 0) parts.Add(L(a.NoiseReduction >= 2 ? "cfx_noise_strong" : "cfx_noise_light"));
                if (a.VoiceEnhance) parts.Add(L("cfx_d_voice"));
                if (a.FadeIn > 0 || a.FadeOut > 0) parts.Add(LF("cfx_d_fades", a.FadeIn, a.FadeOut));
            }
            return parts.Count == 0 ? L("cfx_d_none") : string.Join("; ", parts);
        }
    }
}
