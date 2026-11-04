using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using UltraStudio.Localization;
using UltraStudio.Models;
using UltraStudio.Services;

namespace UltraStudio.Views
{
    /// <summary>Veličine za izvoz — iste u Izvozu i u Grupnoj obradi.</summary>
    public static class ExportSizes
    {
        public sealed record Size(string Key, int W, int H, bool Cover);

        public static readonly List<Size> All = new()
        {
            new("es_original", 0, 0, false),
            new("es_long_1080", 1080, 0, false),
            new("es_long_1600", 1600, 0, false),
            new("es_long_2048", 2048, 0, false),
            new("es_long_3840", 3840, 0, false),
            new("cp_ig_square", 1080, 1080, true),
            new("cp_ig_portrait", 1080, 1350, true),
            new("cp_ig_story", 1080, 1920, true),
            new("cp_yt_thumb", 1280, 720, true),
            new("cp_fb_post", 1200, 630, true),
            new("cp_x_post", 1600, 900, true),
            new("cp_linkedin", 1200, 627, true),
        };

        public static string Label(Size s) => s.H > 0 ? $"{Lang.T(s.Key)} — {s.W} × {s.H}" : Lang.T(s.Key);
        public static readonly string[] Formats = { "jpg", "png", "webp", "tiff", "bmp" };
    }

    public class BatchWindow : Window
    {
        private readonly TextBox _in, _out, _wmText, _wmOpacity, _quality, _suffix;
        private readonly CheckBox _sub;
        private readonly ComboBox _format, _size, _look, _wmPos;
        private readonly TextBlock _status;
        private readonly ListBox _log;
        private readonly Button _start;
        private CancellationTokenSource? _cts;
        private readonly ImageProject _current;

        public BatchWindow(Window owner, ImageProject current)
        {
            Owner = owner;
            _current = current;
            Ui.Prepare(this, Lang.T("batch_title"), 620, 760);
            var sp = new StackPanel { Margin = new Thickness(16) };
            sp.Children.Add(Ui.Label(Lang.T("batch_hint")));

            TextBox Folder(string label, out Button browse)
            {
                sp.Children.Add(Ui.Label(label, 6));
                var t = Ui.TextBox(label, "");
                sp.Children.Add(t);
                browse = Ui.Button(Lang.T("batch_browse") + " — " + label, () => { });
                browse.HorizontalAlignment = HorizontalAlignment.Left;
                sp.Children.Add(browse);
                return t;
            }

            _in = Folder(Lang.T("batch_input"), out var bIn);
            bIn.Click += (_, __) => Browse(_in);
            _sub = Ui.Check(Lang.T("batch_subfolders"), false);
            sp.Children.Add(_sub);
            _out = Folder(Lang.T("batch_output"), out var bOut);
            bOut.Click += (_, __) => Browse(_out);

            sp.Children.Add(Ui.Label(Lang.T("export_format"), 8));
            _format = Ui.Combo(Lang.T("export_format"), ExportSizes.Formats.Select(f => f.ToUpperInvariant()), 0);
            sp.Children.Add(_format);
            sp.Children.Add(Ui.Label(Lang.T("export_quality")));
            _quality = Ui.TextBox(Lang.T("export_quality"), "90");
            sp.Children.Add(_quality);
            sp.Children.Add(Ui.Label(Lang.T("export_size")));
            _size = Ui.Combo(Lang.T("export_size"), ExportSizes.All.Select(ExportSizes.Label), 0);
            sp.Children.Add(_size);

            sp.Children.Add(Ui.Label(Lang.T("batch_look")));
            var looks = new List<string> { Lang.T("batch_look_none"), Lang.T("batch_look_current") };
            looks.AddRange(LookPresets.All.Skip(1).Select(LookPresets.Name));
            _look = Ui.Combo(Lang.T("batch_look"), looks, current.HasAdjustments ? 1 : 0);
            sp.Children.Add(_look);

            sp.Children.Add(Ui.Label(Lang.T("batch_watermark")));
            _wmText = Ui.TextBox(Lang.T("batch_watermark"), "");
            sp.Children.Add(_wmText);
            sp.Children.Add(Ui.Label(Lang.T("batch_wm_position")));
            _wmPos = Ui.Combo(Lang.T("batch_wm_position"), Enumerable.Range(0, 9).Select(i => Lang.T($"pos_{i / 3}{i % 3}")), 8);
            sp.Children.Add(_wmPos);
            sp.Children.Add(Ui.Label(Lang.T("batch_wm_opacity")));
            _wmOpacity = Ui.TextBox(Lang.T("batch_wm_opacity"), "60");
            sp.Children.Add(_wmOpacity);
            sp.Children.Add(Ui.Label(Lang.T("batch_suffix")));
            _suffix = Ui.TextBox(Lang.T("batch_suffix"), "_edited");
            sp.Children.Add(_suffix);

            _status = new TextBlock { Foreground = Ui.Res("BrText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) };
            _status.SetValue(AutomationProperties.LiveSettingProperty, AutomationLiveSetting.Polite);
            sp.Children.Add(_status);
            _log = Ui.List(Lang.T("batch_log"));
            _log.MinHeight = 80;
            sp.Children.Add(_log);

            _start = Ui.Button(Lang.T("batch_start"), Start, primary: true);
            sp.Children.Add(Ui.Row(_start, Ui.Button(Lang.T("batch_stop"), () => _cts?.Cancel()), Ui.Button(Lang.T("btn_close"), Close, isCancel: true)));

            Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Loaded += (_, __) => _in.Focus();
            Closing += (_, __) => _cts?.Cancel();
        }

        private void Browse(TextBox target)
        {
            var dlg = new Ookii.Dialogs.Wpf.VistaFolderBrowserDialog { Description = Lang.T("batch_browse"), UseDescriptionForTitle = true };
            if (Directory.Exists(target.Text)) dlg.SelectedPath = target.Text;
            if (dlg.ShowDialog(this) == true) { target.Text = dlg.SelectedPath; target.Focus(); }
        }

        private void Say(string text) { _status.Text = text; Ui.Announce(_status, text); }

        private async void Start()
        {
            if (_cts != null) return; // već radi
            if (!Directory.Exists(_in.Text)) { Say(Lang.T("batch_no_input")); _in.Focus(); return; }
            if (string.IsNullOrWhiteSpace(_out.Text)) { Say(Lang.T("batch_no_output")); _out.Focus(); return; }

            var size = ExportSizes.All[Math.Max(0, _size.SelectedIndex)];
            Ui.TryNum(_quality.Text, out double q);
            Ui.TryNum(_wmOpacity.Text, out double wmo);
            ImageProject? adj = null;
            if (_look.SelectedIndex == 1) { adj = new ImageProject(); adj.CopyAdjustmentsFrom(_current); }
            else if (_look.SelectedIndex >= 2) { adj = new ImageProject(); LookPresets.Apply(adj, LookPresets.All[_look.SelectedIndex - 1]); }

            var opt = new BatchProcessor.Options
            {
                InputFolder = _in.Text.Trim(), OutputFolder = _out.Text.Trim(), IncludeSubfolders = _sub.IsChecked == true,
                Adjustments = adj,
                Export = new CanvasEngine.ExportOptions
                {
                    Format = ExportSizes.Formats[Math.Max(0, _format.SelectedIndex)], Quality = q > 0 ? (int)q : 90,
                    TargetWidth = size.W, TargetHeight = size.H, CoverCrop = size.Cover
                },
                WatermarkText = _wmText.Text, WatermarkPosition = Math.Max(0, _wmPos.SelectedIndex),
                WatermarkOpacity = wmo > 0 ? wmo : 60, Suffix = _suffix.Text
            };

            int count = BatchProcessor.FindImages(opt).Count;
            if (count == 0) { Say(Lang.T("batch_none_found")); return; }

            _log.Items.Clear();
            _cts = new CancellationTokenSource();
            // Fokus ostaje gde jeste — dugme Start se NE onemogućava (videti napomenu o fokusu u MainWindow)
            Say(string.Format(Lang.T("batch_started"), count));
            int lastSpoken = 0;
            var result = await Task.Run(() => BatchProcessor.Run(opt, (i, n, name) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _status.Text = string.Format(Lang.T("batch_progress"), i, n, name);
                    // Izgovor na svakih 10% — ne posle svake slike (bez neprekidnog brbljanja)
                    int pct = i * 100 / n;
                    if (pct / 10 > lastSpoken / 10 || i == n) { lastSpoken = pct; Ui.Announce(_status, _status.Text); }
                }));
            }, _cts.Token));

            foreach (var e in result.errors) _log.Items.Add(e);
            Say(_cts.IsCancellationRequested
                ? string.Format(Lang.T("batch_stopped"), result.ok)
                : string.Format(Lang.T("batch_done"), result.ok, result.errors.Count, opt.OutputFolder));
            _cts = null;
        }
    }
}
