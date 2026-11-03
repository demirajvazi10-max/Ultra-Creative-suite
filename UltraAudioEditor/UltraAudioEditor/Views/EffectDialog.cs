using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UltraAudioEditor.Models;

using UltraAudioEditor.Localization;

namespace UltraAudioEditor.Views
{
    // Definicija jednog parametra efekta
    public record EffectParam(
        string   Label,
        double   Min,
        double   Max,
        double   Step,
        string   Unit,
        Func<double>   Get,
        Action<double> Set);

    // EffectType je premešten u Models (AudioModels.cs) — koristi ga i audio engine za redosled efekata.

    /// <summary>
    /// Pristupačan dijalog za podešavanje jednog efekta.
    /// Svaki parametar = Slider + TextBox. JAWS čita normalno.
    /// Efekat se mijenja LIVE dok muzika svira (DSP lanac čita live vrijednosti).
    /// </summary>
    public class EffectDialog : Window
    {
        /// <summary>Naziv efekta za naslov dijaloga — isti u meniju Efekti i u meniju trake.</summary>
        public static string TitleFor(EffectType t) => t switch
        {
            EffectType.Equalizer  => "Equalizer (EQ)",
            EffectType.Reverb     => "Reverb",
            EffectType.Delay      => "Delay / Echo",
            EffectType.Compressor => Lang.T("fx_compressor"),
            EffectType.NoiseGate  => "Noise Gate",
            EffectType.BassBoost  => "Bass Boost",
            EffectType.PitchShift => "Pitch Shift",
            EffectType.Chorus     => "Chorus",
            _ => t.ToString()
        };

        private readonly List<(Slider Slider, TextBox Box, EffectParam Param)> _rows = new();

        public EffectDialog(string title, TrackEffects fx, EffectType effectType, Action? togglePlay = null)
        {
            Title  = title;
            Width  = 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode   = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Background   = (Brush)Application.Current.Resources["BrBgDark"];

            var (isOn, setOn, paramList) = BuildParams(fx, effectType);

            // Dinamična visina (+ red za presete)
            Height = Math.Min(720, 200 + paramList.Length * 52);

            var root = new Grid { Margin = new Thickness(20, 16, 20, 16) };
            for (int i = 0; i < 7; i++)
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions[4] = new RowDefinition { Height = new GridLength(1, GridUnitType.Star) };   // parametri (sa skrolom)

            // ── Naslov ───────────────────────────────────────────────────
            var hdr = new TextBlock
            {
                Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["BrText"],
                Margin = new Thickness(0, 0, 0, 12)
            };
            hdr.SetValue(AutomationProperties.HeadingLevelProperty, AutomationHeadingLevel.Level1);
            Grid.SetRow(hdr, 0); root.Children.Add(hdr);

            // ── Enable checkbox ───────────────────────────────────────────
            var cb = new CheckBox
            {
                Content   = Lang.T("effect_enabled"),
                IsChecked = isOn(),
                FontSize  = 13,
                Foreground = (Brush)Application.Current.Resources["BrText"],
                Margin = new Thickness(0, 0, 0, 4)
            };
            UpdateCbAria(cb, title, isOn());
            cb.Checked   += (_, __) => { setOn(true);  UpdateCbAria(cb, title, true); };
            cb.Unchecked += (_, __) => { setOn(false); UpdateCbAria(cb, title, false); };
            Grid.SetRow(cb, 2); root.Children.Add(cb);

            // ── Preseti (u stilu Sound Forge-a) ──────────────────────────
            // Strelicama gore/dole kroz listu: svaki preset se primeni ODMAH (i uključi
            // efekat), pa se dok muzika svira čuje razlika između npr. sobe i katedrale.
            var presets = Services.EffectPresets.For(effectType);
            if (presets.Count > 0)
            {
                var presetPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
                var presetLbl = new TextBlock
                {
                    Text = Lang.T("fx_preset_label"), FontSize = 12,
                    Foreground = (Brush)Application.Current.Resources["BrTextSec"],
                    Margin = new Thickness(0, 0, 0, 4)
                };
                var combo = new ComboBox { FontSize = 13, Height = 30 };
                combo.Items.Add(Lang.T("fx_preset_choose"));
                foreach (var pr in presets) combo.Items.Add(Lang.T(pr.NameKey));
                combo.SelectedIndex = 0;
                combo.SetValue(AutomationProperties.NameProperty, Lang.T("fx_preset_label"));
                combo.SetValue(AutomationProperties.HelpTextProperty, Lang.T("fx_preset_help"));
                combo.SelectionChanged += (_, __) =>
                {
                    int idx = combo.SelectedIndex - 1;
                    if (idx < 0 || idx >= presets.Count) return;
                    presets[idx].Apply(fx);
                    if (cb.IsChecked != true) cb.IsChecked = true;   // Checked handler uključuje efekat
                    else setOn(true);
                    RefreshRows();
                };
                presetPanel.Children.Add(presetLbl);
                presetPanel.Children.Add(combo);
                Grid.SetRow(presetPanel, 1); root.Children.Add(presetPanel);
                Loaded += (_, __) => combo.Focus();
            }

            // ── Spacer ────────────────────────────────────────────────────
            var spacer = new Border { Height = 8 };
            Grid.SetRow(spacer, 3); root.Children.Add(spacer);

            // ── Parametri ─────────────────────────────────────────────────
            var paramGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            paramGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            paramGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            paramGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });

            for (int i = 0; i < paramList.Length; i++)
            {
                paramGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
                var param = paramList[i];

                var lbl = new TextBlock
                {
                    Text = param.Label, FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)Application.Current.Resources["BrTextSec"],
                    Margin = new Thickness(0, 0, 10, 0)
                };

                var slider = new Slider
                {
                    Minimum = param.Min, Maximum = param.Max,
                    Value   = param.Get(),
                    SmallChange = param.Step,
                    LargeChange = param.Step * 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsTabStop = true
                };
                UpdateSliderAria(slider, param);

                var box = new TextBox
                {
                    Text = Fmt(param.Get(), param.Unit),
                    Width = 68, Height = 28, FontSize = 12,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Right
                };
                box.SetValue(AutomationProperties.NameProperty,
                    string.Format(Lang.T("fx_enter_number"), param.Label, Fmt(param.Min, param.Unit), Fmt(param.Max, param.Unit)));

                // Slider → model + box
                bool busy = false;
                slider.ValueChanged += (_, ev) =>
                {
                    if (busy) return; busy = true;
                    param.Set(ev.NewValue);
                    box.Text = Fmt(ev.NewValue, param.Unit);
                    UpdateSliderAria(slider, param);
                    busy = false;
                };

                // Box → model + slider  (na Enter/Tab/LostFocus)
                void ApplyBox()
                {
                    if (busy) return; busy = true;
                    if (double.TryParse(box.Text.Replace(',', '.'),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double v))
                    {
                        v = Math.Clamp(v, param.Min, param.Max);
                        slider.Value = v;
                        param.Set(v);
                        box.Text = Fmt(v, param.Unit);
                    }
                    else box.Text = Fmt(param.Get(), param.Unit);
                    busy = false;
                }
                box.KeyDown   += (_, ke) => { if (ke.Key is Key.Return or Key.Tab) ApplyBox(); };
                box.LostFocus += (_, __) => ApplyBox();

                Grid.SetRow(lbl,    i); Grid.SetColumn(lbl,    0); paramGrid.Children.Add(lbl);
                Grid.SetRow(slider, i); Grid.SetColumn(slider, 1); paramGrid.Children.Add(slider);
                Grid.SetRow(box,    i); Grid.SetColumn(box,    2); paramGrid.Children.Add(box);

                _rows.Add((slider, box, param));
            }
            var paramScroll = new ScrollViewer { Content = paramGrid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
            Grid.SetRow(paramScroll, 4); root.Children.Add(paramScroll);

            // ── Dugmad ────────────────────────────────────────────────────
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var btnReset = new Button
            {
                Content = Lang.T("fx_reset"),
                Style   = (Style)Application.Current.Resources["StdButton"],
                Height  = 30, Padding = new Thickness(10, 0, 10, 0),
                Margin  = new Thickness(0, 0, 8, 0)
            };
            btnReset.SetValue(AutomationProperties.NameProperty,
                Lang.T("fx_reset_help"));
            btnReset.Click += (_, __) => ResetDefaults(fx, effectType);

            var btnClose = new Button
            {
                Content = Lang.T("btn_close"),
                Style   = (Style)Application.Current.Resources["AIButton"],
                Height  = 30, Padding = new Thickness(10, 0, 10, 0),
                IsDefault = true
            };
            btnClose.SetValue(AutomationProperties.NameProperty,
                Lang.T("fx_close_hint"));
            btnClose.Click += (_, __) => Close();

            // Pusti/pauziraj iz samog dijaloga — dijalog je modalan, pa bez ovoga ne bi
            // moglo da se pusti zvuk i da se promena efekta čuje odmah dok se pomera klizač.
            if (togglePlay != null)
            {
                var btnPlay = new Button
                {
                    Content = Lang.T("fx_play_btn"),
                    Style   = (Style)Application.Current.Resources["StdButton"],
                    Height  = 30, Padding = new Thickness(10, 0, 10, 0),
                    Margin  = new Thickness(0, 0, 8, 0)
                };
                btnPlay.SetValue(AutomationProperties.NameProperty, Lang.T("fx_play_btn"));
                btnPlay.Click += (_, __) => togglePlay();
                btnPanel.Children.Add(btnPlay);
                PreviewKeyDown += (_, ke) =>
                {
                    if (ke.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
                    { togglePlay(); ke.Handled = true; }
                };
            }
            btnPanel.Children.Add(btnReset);
            btnPanel.Children.Add(btnClose);
            Grid.SetRow(btnPanel, 6); root.Children.Add(btnPanel);

            Content = root;
            if (presets.Count == 0) Loaded += (_, __) => cb.Focus();
            KeyDown += (_, ke) => { if (ke.Key == Key.Escape) Close(); };
        }

        // ── Helpers ───────────────────────────────────────────────────────
        private static void UpdateCbAria(CheckBox cb, string title, bool on) =>
            cb.SetValue(AutomationProperties.NameProperty,
                string.Format(Lang.T("fx_toggle_state"), title, on ? Lang.T("state_on") : Lang.T("state_off")));

        private static void UpdateSliderAria(Slider s, EffectParam p) =>
            s.SetValue(AutomationProperties.NameProperty,
                string.Format(Lang.T("fx_param_state"), p.Label, Fmt(p.Get(), p.Unit), Fmt(p.Min, p.Unit), Fmt(p.Max, p.Unit)));

        public static string Fmt(double v, string unit) => unit switch
        {
            "dB" => $"{v:F1} dB",
            "s"  => $"{v:F2} s",
            "st" => $"{v:F1} st",
            "ms" => $"{v:F0} ms",
            "Hz" => v >= 100 ? $"{v:F0} Hz" : $"{v:F1} Hz",
            "%"  => $"{v * 100:F0}%",
            _    => $"{v:F2}"
        };

        // ── Reset na defaultne vrijednosti ────────────────────────────────
        private void ResetDefaults(TrackEffects fx, EffectType t)
        {
            switch (t)
            {
                case EffectType.Equalizer:
                    fx.EqLow = 0; fx.EqMid = 0; fx.EqHigh = 0;
                    fx.EqLowFreq = 200; fx.EqMidFreq = 1000; fx.EqMidQ = 0.7f; fx.EqHighFreq = 8000;
                    fx.HighPassFreq = 0; fx.LowPassFreq = 0; break;
                case EffectType.Reverb:     fx.ReverbMix = 0.3f; fx.ReverbRoom = 0.5f; fx.ReverbDamping = 0.5f; fx.ReverbPreDelay = 15f; break;
                case EffectType.Delay:      fx.DelayTime = 0.25f; fx.DelayFeedback = 0.3f; fx.DelayMix = 0.4f; break;
                case EffectType.Compressor: fx.CompThreshold = -20; fx.CompRatio = 4; fx.CompAttack = 10; fx.CompRelease = 100; fx.CompMakeup = 0; break;
                case EffectType.NoiseGate:  fx.GateThreshold = -40; break;
                case EffectType.BassBoost:  fx.BassGain = 6; break;
                case EffectType.PitchShift: fx.PitchSemitones = 0; break;
                case EffectType.Chorus:     fx.ChorusDepth = 0.5f; fx.ChorusRate = 0.8f; fx.ChorusMix = 0.5f; break;
            }
            RefreshRows();
        }

        /// <summary>Klizači i polja prate trenutne vrednosti modela (posle preseta ili reseta).</summary>
        private void RefreshRows()
        {
            foreach (var (slider, box, param) in _rows)
            {
                slider.Value = param.Get();
                box.Text     = Fmt(param.Get(), param.Unit);
                UpdateSliderAria(slider, param);
            }
        }

        // ── Definicije parametara po tipu efekta ──────────────────────────
        private static (Func<bool> IsOn, Action<bool> SetOn, EffectParam[] Params)
            BuildParams(TrackEffects fx, EffectType t) => t switch
        {
            EffectType.Equalizer => (
                () => fx.EqEnabled,
                v  => fx.EqEnabled = v,
                new EffectParam[]
                {
                    new(Lang.T("fx_eq_low"),    -12, 12, 0.5,  "dB", () => fx.EqLow,  v => fx.EqLow  = (float)v),
                    new(Lang.T("fx_eq_mid"), -12, 12, 0.5,  "dB", () => fx.EqMid,  v => fx.EqMid  = (float)v),
                    new(Lang.T("fx_eq_high"),  -12, 12, 0.5,  "dB", () => fx.EqHigh, v => fx.EqHigh = (float)v),
                    new(Lang.T("fx_eq_low_freq"),  40, 500, 5, "Hz", () => fx.EqLowFreq, v => fx.EqLowFreq = (float)v),
                    new(Lang.T("fx_eq_mid_freq"),  150, 10000, 10, "Hz", () => fx.EqMidFreq, v => fx.EqMidFreq = (float)v),
                    new(Lang.T("fx_eq_mid_q"),     0.2, 8, 0.1, "", () => fx.EqMidQ, v => fx.EqMidQ = (float)v),
                    new(Lang.T("fx_eq_high_freq"), 2000, 16000, 100, "Hz", () => fx.EqHighFreq, v => fx.EqHighFreq = (float)v),
                    new(Lang.T("fx_highpass"),     0, 1000, 5, "Hz", () => fx.HighPassFreq, v => fx.HighPassFreq = (float)v),
                    new(Lang.T("fx_lowpass"),      0, 20000, 100, "Hz", () => fx.LowPassFreq, v => fx.LowPassFreq = (float)v),
                }),

            EffectType.Reverb => (
                () => fx.ReverbEnabled,
                v  => fx.ReverbEnabled = v,
                new EffectParam[]
                {
                    new(Lang.T("fx_reverb_mix"), 0, 1, 0.01, "%", () => fx.ReverbMix,  v => fx.ReverbMix  = (float)v),
                    new(Lang.T("fx_room_size"),    0, 1, 0.01, "%", () => fx.ReverbRoom, v => fx.ReverbRoom = (float)v),
                    new(Lang.T("fx_damping"),      0, 1, 0.01, "%", () => fx.ReverbDamping, v => fx.ReverbDamping = (float)v),
                    new(Lang.T("fx_predelay"),     0, 200, 1,  "ms", () => fx.ReverbPreDelay, v => fx.ReverbPreDelay = (float)v),
                }),

            EffectType.Delay => (
                () => fx.DelayEnabled,
                v  => fx.DelayEnabled = v,
                new EffectParam[]
                {
                    new(Lang.T("fx_delay"),  0, 1,   0.01, "s", () => fx.DelayTime,     v => fx.DelayTime     = (float)v),
                    new(Lang.T("fx_feedback"), 0, 0.9, 0.01, "%", () => fx.DelayFeedback, v => fx.DelayFeedback = (float)v),
                    new(Lang.T("fx_mix"),      0, 1,   0.01, "%", () => fx.DelayMix,      v => fx.DelayMix      = (float)v),
                }),

            EffectType.Compressor => (
                () => fx.CompressorEnabled,
                v  => fx.CompressorEnabled = v,
                new EffectParam[]
                {
                    new(Lang.T("fx_threshold"), -60, 0,   1,   "dB", () => fx.CompThreshold, v => fx.CompThreshold = (float)v),
                    new(Lang.T("fx_ratio"),      1, 20,  0.5, "",   () => fx.CompRatio,     v => fx.CompRatio     = (float)v),
                    new(Lang.T("fx_attack"),  1, 200, 1,   "",   () => fx.CompAttack,    v => fx.CompAttack    = (float)v),
                    new(Lang.T("fx_release"),10, 500, 10, "",   () => fx.CompRelease,   v => fx.CompRelease   = (float)v),
                    new(Lang.T("fx_makeup"),  0, 18,  0.5, "dB", () => fx.CompMakeup,   v => fx.CompMakeup    = (float)v),
                }),

            EffectType.NoiseGate => (
                () => fx.NoiseGateEnabled,
                v  => fx.NoiseGateEnabled = v,
                new EffectParam[]
                {
                    new(Lang.T("fx_threshold"), -80, 0, 1, "dB", () => fx.GateThreshold, v => fx.GateThreshold = (float)v),
                }),

            EffectType.BassBoost => (
                () => fx.BassBostEnabled,
                v  => fx.BassBostEnabled = v,
                new EffectParam[]
                {
                    new(Lang.T("fx_gain"), 0, 24, 0.5, "dB", () => fx.BassGain, v => fx.BassGain = (float)v),
                }),

            EffectType.PitchShift => (
                () => fx.PitchEnabled,
                v  => fx.PitchEnabled = v,
                new EffectParam[]
                {
                    new(Lang.T("fx_semitones"), -12, 12, 0.5, "st", () => fx.PitchSemitones, v => fx.PitchSemitones = (float)v),
                }),

            EffectType.Chorus => (
                () => fx.ChorusEnabled,
                v  => fx.ChorusEnabled = v,
                new EffectParam[]
                {
                    new(Lang.T("fx_depth"), 0, 1, 0.01, "%", () => fx.ChorusDepth, v => fx.ChorusDepth = (float)v),
                    new(Lang.T("fx_rate"),  0.1, 8, 0.1, "Hz", () => fx.ChorusRate, v => fx.ChorusRate = (float)v),
                    new(Lang.T("fx_mix"),   0, 1, 0.01, "%", () => fx.ChorusMix, v => fx.ChorusMix = (float)v),
                }),

            _ => throw new ArgumentOutOfRangeException(nameof(t), t, null)
        };
    }
}
