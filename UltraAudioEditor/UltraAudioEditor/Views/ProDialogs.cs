using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UltraAudioEditor.Localization;
using UltraAudioEditor.Models;
using UltraAudioEditor.Services;
using UltraAudioEditor.Services.Dsp;
using UltraAudioEditor.ViewModels;

namespace UltraAudioEditor.Views
{
    /// <summary>Zajednički izgled i pomoćne metode za dijaloge (tamna tema, imena za čitač ekrana).</summary>
    internal static class Ui
    {
        public static Brush Res(string key) => (Brush)Application.Current.Resources[key];
        public static Style GetStyle(string key) => (Style)Application.Current.Resources[key];

        public static void Prepare(Window w, string title, double width, double height)
        {
            w.Title = title; w.Width = width; w.Height = height;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            w.ShowInTaskbar = false;
            w.Background = Res("BrBgDark");
            w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
        }

        public static TextBlock Label(string text, double top = 0) => new TextBlock
        {
            Text = text, FontSize = 12, Foreground = Res("BrTextSec"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, top, 0, 4)
        };

        public static Button Button(string text, Action click, bool primary = false, bool isCancel = false, bool isDefault = false)
        {
            var b = new Button
            {
                Content = text, Height = 30, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 8, 0),
                Style = GetStyle(primary ? "AIButton" : "StdButton"), IsCancel = isCancel, IsDefault = isDefault
            };
            b.SetValue(AutomationProperties.NameProperty, text);
            b.Click += (_, __) => click();
            return b;
        }

        public static TextBox TextBox(string name, string value)
        {
            var t = new TextBox { Text = value, Height = 30, FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
            t.SetValue(AutomationProperties.NameProperty, name);
            return t;
        }

        public static StackPanel Row(params UIElement[] items)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            foreach (var i in items) p.Children.Add(i);
            return p;
        }

        public static ListBox List(string name)
        {
            var l = new ListBox { FontSize = 13, Background = Res("BrBgMid"), Foreground = Res("BrText"), MinHeight = 160 };
            l.SetValue(AutomationProperties.NameProperty, name);
            return l;
        }
    }

    /// <summary>Izbor jedne od ponuđenih opcija (lista + Potvrdi). Strelice biraju, Enter potvrđuje.</summary>
    public class ChoiceDialog : Window
    {
        public int SelectedIndex { get; private set; } = -1;

        public ChoiceDialog(string title, string prompt, string[] options, int def)
        {
            Ui.Prepare(this, title, 520, Math.Min(560, 190 + options.Length * 26));
            ResizeMode = ResizeMode.NoResize;
            var root = new DockPanel { Margin = new Thickness(16) };
            var lbl = Ui.Label(prompt); DockPanel.SetDock(lbl, Dock.Top); root.Children.Add(lbl);
            var list = Ui.List(prompt);
            foreach (var o in options) list.Items.Add(o);
            list.SelectedIndex = Math.Clamp(def, 0, options.Length - 1);
            void Ok() { SelectedIndex = list.SelectedIndex; DialogResult = SelectedIndex >= 0; }
            list.MouseDoubleClick += (_, __) => Ok();
            list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Ok(); e.Handled = true; } };
            var buttons = Ui.Row(Ui.Button(Lang.T("btn_cancel"), () => DialogResult = false, isCancel: true), Ui.Button(Lang.T("btn_confirm"), Ok, primary: true));
            DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
            root.Children.Add(list);
            Content = root;
            Loaded += (_, __) =>
            {
                list.Focus();
                if (list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex) is ListBoxItem it) it.Focus();
            };
        }
    }

    /// <summary>Oznake pesme (naslov, izvođač, album, godina, žanr, komentar, omot) — upisuju se pri izvozu.</summary>
    public class TagsDialog : Window
    {
        public TagsDialog(AudioTags tags)
        {
            Ui.Prepare(this, Lang.T("tags_title"), 520, 600);
            var sp = new StackPanel { Margin = new Thickness(16) };
            sp.Children.Add(Ui.Label(Lang.T("tags_hint")));
            TextBox Field(string key, string v) { sp.Children.Add(Ui.Label(Lang.T(key), 4)); var t = Ui.TextBox(Lang.T(key), v); sp.Children.Add(t); return t; }
            var title = Field("tag_title", tags.Title);
            var artist = Field("tag_artist", tags.Artist);
            var album = Field("tag_album", tags.Album);
            var year = Field("tag_year", tags.Year);
            var genre = Field("tag_genre", tags.Genre);
            var comment = Field("tag_comment", tags.Comment);
            var cover = Field("tag_cover", tags.CoverPath);
            var browse = Ui.Button(Lang.T("tag_cover_browse"), () =>
            {
                var d = new Microsoft.Win32.OpenFileDialog { Filter = "JPG/PNG|*.jpg;*.jpeg;*.png" };
                if (d.ShowDialog() == true) cover.Text = d.FileName;
            });
            browse.HorizontalAlignment = HorizontalAlignment.Left;
            sp.Children.Add(browse);
            sp.Children.Add(Ui.Row(
                Ui.Button(Lang.T("btn_cancel"), Close, isCancel: true),
                Ui.Button(Lang.T("btn_save"), () =>
                {
                    tags.Title = title.Text; tags.Artist = artist.Text; tags.Album = album.Text; tags.Year = year.Text;
                    tags.Genre = genre.Text; tags.Comment = comment.Text; tags.CoverPath = cover.Text;
                    DialogResult = true;
                }, primary: true, isDefault: true)));
            Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Loaded += (_, __) => title.Focus();
        }
    }

    /// <summary>Kriva jačine trake: lista tačaka (vreme, dB). Dodaj na poziciji, izmeni, obriši.</summary>
    public class EnvelopeWindow : Window
    {
        private readonly MainViewModel _vm; private readonly AudioTrack _track; private readonly ListBox _list;

        public EnvelopeWindow(MainViewModel vm, AudioTrack track)
        {
            _vm = vm; _track = track;
            Ui.Prepare(this, string.Format(Lang.T("env_title"), track.Name), 560, 480);
            var root = new DockPanel { Margin = new Thickness(16) };
            var hint = Ui.Label(Lang.T("env_hint")); DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
            _list = Ui.List(string.Format(Lang.T("env_title"), track.Name));
            _list.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Delete) { Delete(); e.Handled = true; }
                if (e.Key == Key.Enter) { Edit(); e.Handled = true; }
            };
            var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            buttons.Children.Add(Ui.Button(string.Format(Lang.T("env_add"), MainViewModel.Fmt(vm.PlayheadPosition)), Add, primary: true));
            buttons.Children.Add(Ui.Button(Lang.T("env_edit"), Edit));
            buttons.Children.Add(Ui.Button(Lang.T("env_delete"), Delete));
            buttons.Children.Add(Ui.Button(Lang.T("env_clear"), () => { _track.Envelope.Clear(); Refresh(); }));
            buttons.Children.Add(Ui.Button(Lang.T("btn_close"), Close, isCancel: true));
            DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
            root.Children.Add(_list);
            Content = root;
            Refresh();
            Loaded += (_, __) => _list.Focus();
        }

        private void Refresh(int select = -1)
        {
            _list.Items.Clear();
            foreach (var p in _track.Envelope) _list.Items.Add(string.Format(Lang.T("env_point"), MainViewModel.Fmt(p.Time), p.Db));
            if (_track.Envelope.Count == 0) _list.Items.Add(Lang.T("env_empty"));
            _list.SelectedIndex = Math.Clamp(select, 0, _list.Items.Count - 1);
        }

        private double? AskDb(double current)
        {
            var d = new SetValueDialog(Lang.T("env_value_title"), Lang.T("env_value_prompt"), current.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture), "dB") { Owner = this };
            if (d.ShowDialog() != true) return null;
            return double.TryParse(d.ResultValue.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v)
                ? Math.Clamp(v, -60, 12) : null;
        }

        private void Add()
        {
            double t = Math.Round(_vm.PlayheadPosition, 3);
            var db = AskDb(_track.EnvelopeGainAt(t) is float g && g > 0 ? 20 * Math.Log10(g) : 0);
            if (db == null) return;
            _track.Envelope.RemoveAll(p => Math.Abs(p.Time - t) < 0.01);
            _track.Envelope.Add(new EnvelopePoint { Time = t, Db = db.Value });
            _track.Envelope.Sort((a, b) => a.Time.CompareTo(b.Time));
            Refresh(_track.Envelope.FindIndex(p => p.Time == t));
        }

        private void Edit()
        {
            int i = _list.SelectedIndex;
            if (i < 0 || i >= _track.Envelope.Count) return;
            var db = AskDb(_track.Envelope[i].Db);
            if (db == null) return;
            _track.Envelope[i].Db = db.Value;
            Refresh(i);
        }

        private void Delete()
        {
            int i = _list.SelectedIndex;
            if (i < 0 || i >= _track.Envelope.Count) return;
            _track.Envelope.RemoveAt(i);
            Refresh(i);
        }
    }

    /// <summary>Redosled efekata u lancu trake: Alt+Gore / Alt+Dole pomeraju izabrani efekat.</summary>
    public class EffectOrderWindow : Window
    {
        public EffectOrderWindow(AudioTrack track)
        {
            Ui.Prepare(this, string.Format(Lang.T("fxorder_title"), track.Name), 480, 460);
            var fx = track.Effects;
            var order = (fx.Order ?? TrackEffects.DefaultOrder()).Distinct().ToList();
            foreach (var t in TrackEffects.DefaultOrder()) if (!order.Contains(t)) order.Add(t);
            var root = new DockPanel { Margin = new Thickness(16) };
            var hint = Ui.Label(Lang.T("fxorder_hint")); DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
            var list = Ui.List(string.Format(Lang.T("fxorder_title"), track.Name));
            bool IsOn(EffectType t) => t switch
            {
                EffectType.Equalizer => fx.EqEnabled, EffectType.Reverb => fx.ReverbEnabled, EffectType.Delay => fx.DelayEnabled,
                EffectType.Compressor => fx.CompressorEnabled, EffectType.NoiseGate => fx.NoiseGateEnabled, EffectType.BassBoost => fx.BassBostEnabled,
                EffectType.PitchShift => fx.PitchEnabled, EffectType.Chorus => fx.ChorusEnabled, _ => false
            };
            void Fill(int sel)
            {
                list.Items.Clear();
                for (int i = 0; i < order.Count; i++)
                    list.Items.Add($"{i + 1}. {EffectDialog.TitleFor(order[i])}  ({(IsOn(order[i]) ? Lang.T("state_on") : Lang.T("state_off"))})");
                list.SelectedIndex = sel;
                if (list.ItemContainerGenerator.ContainerFromIndex(sel) is ListBoxItem it) it.Focus();
                fx.Order = new List<EffectType>(order);
            }
            void Move(int d)
            {
                int i = list.SelectedIndex, j = i + d;
                if (i < 0 || j < 0 || j >= order.Count) return;
                (order[i], order[j]) = (order[j], order[i]);
                Fill(j);
            }
            list.PreviewKeyDown += (_, e) =>
            {
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (Keyboard.Modifiers == ModifierKeys.Alt && key == Key.Up) { Move(-1); e.Handled = true; }
                if (Keyboard.Modifiers == ModifierKeys.Alt && key == Key.Down) { Move(+1); e.Handled = true; }
            };
            var buttons = Ui.Row(
                Ui.Button(Lang.T("fxorder_up"), () => Move(-1)),
                Ui.Button(Lang.T("fxorder_down"), () => Move(+1)),
                Ui.Button(Lang.T("fxorder_reset"), () => { order = TrackEffects.DefaultOrder(); Fill(0); }),
                Ui.Button(Lang.T("btn_close"), Close, primary: true, isCancel: true));
            DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
            root.Children.Add(list);
            Content = root;
            Loaded += (_, __) => { list.Focus(); Fill(0); };
        }
    }

    /// <summary>
    /// Transkript (lokalni Whisper): rečenice sa vremenom. Enter = skoči na to mesto,
    /// "Markeri" = marker na početak svake rečenice, pretraga teksta, čuvanje u TXT/SRT.
    /// </summary>
    public class TranscriptWindow : Window
    {
        public TranscriptWindow(MainViewModel vm, List<TranscriptSegment> segs, string trackName)
        {
            Ui.Prepare(this, string.Format(Lang.T("tr_title"), trackName), 760, 560);
            var root = new DockPanel { Margin = new Thickness(16) };
            var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top);
            top.Children.Add(Ui.Label(Lang.T("tr_hint")));
            var search = Ui.TextBox(Lang.T("tr_search"), "");
            top.Children.Add(Ui.Label(Lang.T("tr_search")));
            top.Children.Add(search);
            root.Children.Add(top);
            var list = Ui.List(string.Format(Lang.T("tr_title"), trackName));
            foreach (var s in segs) list.Items.Add($"{MainViewModel.Fmt(s.Start)}  {s.Text}");
            if (list.Items.Count > 0) list.SelectedIndex = 0;
            void Go() { int i = list.SelectedIndex; if (i >= 0) { vm.GoToMarker(segs[i].Start); vm.Announce(string.Format(Lang.T("tr_jumped"), MainViewModel.Fmt(segs[i].Start))); } }
            list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Go(); e.Handled = true; } };
            list.MouseDoubleClick += (_, __) => Go();
            void Find()
            {
                string q = search.Text.Trim();
                if (q.Length == 0) return;
                int start = list.SelectedIndex + 1;
                for (int k = 0; k < segs.Count; k++)
                {
                    int i = (start + k) % segs.Count;
                    if (segs[i].Text.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                    {
                        list.SelectedIndex = i; list.ScrollIntoView(list.Items[i]);
                        if (list.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem it) it.Focus();
                        return;
                    }
                }
                vm.Announce(Lang.T("tr_not_found"));
            }
            search.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Find(); e.Handled = true; } };
            var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            buttons.Children.Add(Ui.Button(Lang.T("tr_go"), Go, primary: true));
            buttons.Children.Add(Ui.Button(Lang.T("tr_find_next"), Find));
            buttons.Children.Add(Ui.Button(Lang.T("tr_markers"), () =>
            {
                vm.PushUndo();
                foreach (var s in segs) vm.AddMarkerAt(s.Start);
                vm.NotifyEdited();
                vm.Announce(string.Format(Lang.T("tr_markers_done"), segs.Count));
            }));
            buttons.Children.Add(Ui.Button(Lang.T("tr_save_txt"), () => Save(segs, false)));
            buttons.Children.Add(Ui.Button(Lang.T("tr_save_srt"), () => Save(segs, true)));
            buttons.Children.Add(Ui.Button(Lang.T("btn_close"), Close, isCancel: true));
            DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
            root.Children.Add(list);
            Content = root;
            Loaded += (_, __) => { list.Focus(); if (list.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem it) it.Focus(); };
        }

        private void Save(List<TranscriptSegment> segs, bool srt)
        {
            var d = new Microsoft.Win32.SaveFileDialog { Filter = srt ? "SRT|*.srt" : "TXT|*.txt", FileName = srt ? "transkript.srt" : "transkript.txt" };
            if (d.ShowDialog() != true) return;
            File.WriteAllText(d.FileName, srt ? WhisperService.ToSrt(segs) : string.Join(Environment.NewLine, segs.Select(s => s.Text)), System.Text.Encoding.UTF8);
        }
    }

    /// <summary>
    /// Grupna konverzija: svi audio fajlovi iz foldera u izabrani format, uz opcionu
    /// normalizaciju glasnoće (LUFS). Napredak se upisuje u listu (čitljivo čitaču ekrana).
    /// </summary>
    public class BatchConvertWindow : Window
    {
        private static readonly string[] Exts = { ".wav", ".mp3", ".flac", ".ogg", ".opus", ".m4a", ".aac", ".aiff", ".aif", ".wma" };

        public BatchConvertWindow(int sampleRate)
        {
            Ui.Prepare(this, Lang.T("batch_title"), 640, 620);
            var sp = new DockPanel { Margin = new Thickness(16) };
            var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top);
            top.Children.Add(Ui.Label(Lang.T("batch_hint")));
            var inBox = Ui.TextBox(Lang.T("batch_in"), ""); var outBox = Ui.TextBox(Lang.T("batch_out"), "");
            void Pick(TextBox t) { var d = new Ookii.Dialogs.Wpf.VistaFolderBrowserDialog(); if (d.ShowDialog(this) == true) t.Text = d.SelectedPath; }
            top.Children.Add(Ui.Label(Lang.T("batch_in"))); top.Children.Add(inBox);
            var b1 = Ui.Button(Lang.T("batch_browse_in"), () => Pick(inBox)); b1.HorizontalAlignment = HorizontalAlignment.Left; top.Children.Add(b1);
            top.Children.Add(Ui.Label(Lang.T("batch_out"), 8)); top.Children.Add(outBox);
            var b2 = Ui.Button(Lang.T("batch_browse_out"), () => Pick(outBox)); b2.HorizontalAlignment = HorizontalAlignment.Left; top.Children.Add(b2);
            top.Children.Add(Ui.Label(Lang.T("batch_format"), 8));
            var fmt = new ComboBox { Height = 30, FontSize = 13 };
            foreach (var f in new[] { "WAV", "MP3", "M4A", "AIFF", "FLAC", "OGG", "OPUS" }) fmt.Items.Add(f);
            fmt.SelectedIndex = 1;
            fmt.SetValue(AutomationProperties.NameProperty, Lang.T("batch_format"));
            top.Children.Add(fmt);
            var norm = new CheckBox { Content = Lang.T("batch_normalize"), Foreground = Ui.Res("BrText"), Margin = new Thickness(0, 8, 0, 0) };
            top.Children.Add(norm);
            sp.Children.Add(top);

            var log = Ui.List(Lang.T("batch_log"));
            var start = Ui.Button(Lang.T("batch_start"), () => { }, primary: true);
            var buttons = Ui.Row(start, Ui.Button(Lang.T("btn_close"), Close, isCancel: true));
            DockPanel.SetDock(buttons, Dock.Bottom); sp.Children.Add(buttons);
            log.Margin = new Thickness(0, 10, 0, 0);
            sp.Children.Add(log);
            Content = sp;

            bool running = false;
            start.Click += async (_, __) =>
            {
                if (running) return;
                string inDir = inBox.Text.Trim(), outDir = outBox.Text.Trim(), format = (string)fmt.SelectedItem;
                if (!Directory.Exists(inDir)) { log.Items.Add(Lang.T("batch_no_in")); return; }
                if (string.IsNullOrEmpty(outDir)) outDir = Path.Combine(inDir, "Ultra " + format);
                Directory.CreateDirectory(outDir);
                bool needsFfmpeg = format is "FLAC" or "OGG" or "OPUS";
                if (needsFfmpeg && !await EnsureFfmpegAsync(this)) { log.Items.Add(Lang.T("ffmpeg_needed")); return; }
                var files = Directory.GetFiles(inDir).Where(f => Exts.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
                if (files.Count == 0) { log.Items.Add(Lang.T("batch_no_files")); return; }
                running = true;
                log.Focus();                 // fokus PRVO sa dugmeta, pa tek onda isključi dugme
                start.IsEnabled = false;
                int ok = 0, n = 0;
                bool doNorm = norm.IsChecked == true;
                foreach (var f in files)
                {
                    n++;
                    string name = Path.GetFileNameWithoutExtension(f);
                    string target = Path.Combine(outDir, name + FfmpegService.ExtensionFor(format));
                    try
                    {
                        string src = f;
                        float[] buf;
                        try { buf = await Task.Run(() => AudioBuffers.ReadWholeFile(src, sampleRate)); }
                        catch
                        {
                            if (!FfmpegService.IsAvailable && !await EnsureFfmpegAsync(this)) throw;
                            src = await FfmpegService.ConvertToWavAsync(f);
                            buf = await Task.Run(() => AudioBuffers.ReadWholeFile(src, sampleRate));
                        }
                        string extra = "";
                        if (doNorm) { var r = await Task.Run(() => Processing.NormalizeLufs(buf, sampleRate, -14, -1)); extra = $", {r.resultLufs:F1} LUFS"; }
                        string tmp = AudioBuffers.WriteRender(buf, sampleRate, "batch");
                        try
                        {
                            if (format is "WAV" or "MP3" or "M4A" or "AIFF")
                            {
                                var ef = format switch { "MP3" => ExportFormat.MP3, "M4A" => ExportFormat.M4A, "AIFF" => ExportFormat.AIFF, _ => ExportFormat.WAV };
                                await Task.Run(() => AudioEngine.EncodeMix(tmp, target, ef, 1f, 192));
                            }
                            else await FfmpegService.EncodeAsync(tmp, target, format, 192, null);
                        }
                        finally { try { File.Delete(tmp); } catch { } }
                        ok++;
                        log.Items.Add(string.Format(Lang.T("batch_item_ok"), n, files.Count, name) + extra);
                    }
                    catch (Exception ex)
                    {
                        DebugLog.Write($"Batch '{f}' failed: {ex}");
                        log.Items.Add(string.Format(Lang.T("batch_item_fail"), n, files.Count, name, ex.Message));
                    }
                    log.ScrollIntoView(log.Items[^1]);
                }
                log.Items.Add(string.Format(Lang.T("batch_done"), ok, files.Count, outDir));
                log.SelectedIndex = log.Items.Count - 1;
                if (log.ItemContainerGenerator.ContainerFromIndex(log.SelectedIndex) is ListBoxItem it) it.Focus();
                running = false; start.IsEnabled = true;
            };
            Loaded += (_, __) => inBox.Focus();
        }

        /// <summary>Ako FFmpeg nedostaje, ponudi preuzimanje. Vraća true ako je FFmpeg spreman.</summary>
        public static async Task<bool> EnsureFfmpegAsync(Window? owner)
        {
            if (FfmpegService.IsAvailable) return true;
            if (MessageBox.Show(Lang.T("ffmpeg_ask"), "FFmpeg", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
            try
            {
                await FfmpegService.DownloadAsync(null);
                MessageBox.Show(Lang.T("ffmpeg_ready"), "FFmpeg", MessageBoxButton.OK, MessageBoxImage.Information);
                return true;
            }
            catch (Exception ex)
            {
                DebugLog.Write("FFmpeg download failed: " + ex);
                MessageBox.Show(string.Format(Lang.T("ffmpeg_failed"), ex.Message), "FFmpeg", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }
    }
}
