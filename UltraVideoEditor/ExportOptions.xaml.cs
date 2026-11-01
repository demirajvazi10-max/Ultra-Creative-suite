using System;
using System.IO;
using System.Windows;
using Newtonsoft.Json;
using WControls = System.Windows.Controls;

namespace UltraVideoEditor
{
    /// <summary>
    /// v2.0 — Export options: container and codec (H.264, H.265, ProRes, WebM, AVI), quality,
    /// frame rate, the fade at the start and end and the warm colour look. Built in code so
    /// every control is labelled in the interface language; the choice is remembered.
    /// </summary>
    public partial class ExportOptions : Window
    {
        private static string L(string k) => UiKit.L(k);

        public ExportSettingsData Settings { get; private set; }

        private readonly WControls.ComboBox _format, _quality, _fps, _warm;
        private readonly WControls.CheckBox _fades, _audioOnly;

        private static string SavedPath => Path.Combine(AppPaths.DataDir, "export_settings.json");

        /// <summary>The settings chosen last time (or the defaults).</summary>
        public static ExportSettingsData LoadSaved()
        {
            try
            {
                if (File.Exists(SavedPath))
                    return JsonConvert.DeserializeObject<ExportSettingsData>(File.ReadAllText(SavedPath)) ?? new ExportSettingsData();
            }
            catch (Exception ex) { AppLog.Error("Reading the export settings", ex); }
            return new ExportSettingsData();
        }

        public ExportOptions(ExportSettingsData defaultSettings = null)
        {
            InitializeComponent();
            UiScaling.Register(this);
            Settings = defaultSettings ?? new ExportSettingsData();
            Title = L("exp_title");

            var panel = new WControls.StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(UiKit.Text(L("exp_intro")));

            _format = UiKit.Combo(
                (L("exp_fmt_h264"), "MP4|H264"),
                (L("exp_fmt_h265"), "MP4|H265"),
                (L("exp_fmt_h265_10"), "MP4|H265_10"),
                (L("exp_fmt_prores"), "MOV|ProRes"),
                (L("exp_fmt_dnxhr"), "MOV|DNxHR"),
                (L("exp_fmt_dnxhr10"), "MOV|DNxHR10"),
                (L("exp_fmt_ffv1"), "MKV|FFV1"),
                (L("exp_fmt_webm"), "WebM|H264"),
                (L("exp_fmt_avi"), "AVI|H264"));
            panel.Children.Add(UiKit.Labelled(L("exp_lbl_format"), _format));

            _quality = UiKit.Combo(
                (L("exp_q_low"), "Low"), (L("exp_q_medium"), "Medium"),
                (L("exp_q_high"), "High"), (L("exp_q_max"), "Maximum"));
            panel.Children.Add(UiKit.Labelled(L("exp_lbl_quality"), _quality));

            _fps = UiKit.Combo(
                (L("exp_fps_auto"), "Auto"), ("23.976", "24000/1001"), ("24", "24"), ("25", "25"),
                ("29.97", "30000/1001"), ("30", "30"), ("50", "50"), ("59.94", "60000/1001"), ("60", "60"));
            panel.Children.Add(UiKit.Labelled(L("exp_lbl_fps"), _fps));

            _fades = UiKit.Check(L("exp_chk_fades"), Settings.FadeInOut);
            panel.Children.Add(_fades);

            _warm = UiKit.Combo((L("exp_warm_auto"), "auto"), (L("exp_warm_on"), "on"), (L("exp_warm_off"), "off"));
            panel.Children.Add(UiKit.Labelled(L("exp_lbl_warm"), _warm));

            _audioOnly = UiKit.Check(L("exp_chk_audio_only"), Settings.ExportAudioOnly);
            panel.Children.Add(_audioOnly);

            var buttons = new WControls.StackPanel { Orientation = WControls.Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
            var ok = UiKit.Btn(L("exp_btn_ok"));
            ok.IsDefault = true;
            ok.Click += (s, e) => { SaveSettings(); DialogResult = true; Close(); };
            var cancel = UiKit.Btn(L("exp_btn_cancel"));
            cancel.IsCancel = true;
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);

            host.Content = panel;
            LoadSettings();
            Loaded += (s, e) => _format.Focus();
        }

        private void LoadSettings()
        {
            string codec = string.IsNullOrEmpty(Settings.VideoCodec) ? "H264" : Settings.VideoCodec;
            string fmt = Settings.Format switch { "WebM" => "WebM", "AVI" => "AVI", "MOV" => "MOV", "MKV" => "MKV", _ => "MP4" };
            if (fmt == "MOV" && codec != "DNxHR" && codec != "DNxHR10") codec = "ProRes";
            if (fmt == "MKV") codec = "FFV1";
            if (fmt == "MP4" && codec != "H264" && codec != "H265" && codec != "H265_10") codec = "H264";
            UiKit.Select(_format, $"{fmt}|{(fmt == "WebM" || fmt == "AVI" ? "H264" : codec)}");
            UiKit.Select(_quality, Settings.Quality ?? "Medium");
            UiKit.Select(_fps, string.IsNullOrEmpty(Settings.FrameRate) ? "Auto" : Settings.FrameRate);
            UiKit.Select(_warm, Settings.WarmLook == null ? "auto" : Settings.WarmLook == true ? "on" : "off");
        }

        private void SaveSettings()
        {
            var parts = ((UiKit.Tag(_format) as string) ?? "MP4|H264").Split('|');
            Settings.Format = parts[0];
            Settings.VideoCodec = parts.Length > 1 ? parts[1] : "H264";
            Settings.Quality = UiKit.Tag(_quality) as string ?? "Medium";
            Settings.FrameRate = UiKit.Tag(_fps) as string ?? "Auto";
            Settings.FadeInOut = _fades.IsChecked == true;
            Settings.WarmLook = (UiKit.Tag(_warm) as string) switch { "on" => true, "off" => false, _ => (bool?)null };
            Settings.ExportAudioOnly = _audioOnly.IsChecked == true;
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.WriteAllText(SavedPath, JsonConvert.SerializeObject(Settings, Formatting.Indented));
            }
            catch (Exception ex) { AppLog.Error("Saving the export settings", ex); }
        }
    }
}
