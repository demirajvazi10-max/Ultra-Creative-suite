using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using NAudio.Wave;
using UltraAudioEditor.Controls;
using UltraAudioEditor.Models;
using UltraAudioEditor.Services;
using UltraAudioEditor.ViewModels;

using UltraAudioEditor.Localization;

namespace UltraAudioEditor.Views.Controls
{
    public partial class AccessibleTrackList : UserControl
    {
        private MainViewModel? _vm;
        private AudioTrack?    _activeTrack;
        private Slider?        _playheadSlider;
        private TextBlock?     _playheadTimeBlock;

        public AccessibleTrackList()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _vm = DataContext as MainViewModel
               ?? Window.GetWindow(this)?.DataContext as MainViewModel;
            if (_vm == null) return;

            _vm.Project.Tracks.CollectionChanged += (_, __) => RebuildTrackList();
            _vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(MainViewModel.PlayheadPosition)
                                      or nameof(MainViewModel.TimeDisplay))
                { UpdateStatus(); SyncSlider(); }
            };

            PreviewKeyDown += OnPreviewKeyDown;
            BuildPlayheadPanel();
            RebuildTrackList();
        }

        // ════════════════════════════════════════════════════════════════════
        // KEYBOARD — jedan PreviewKeyDown hvata sve
        // ════════════════════════════════════════════════════════════════════
        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var mods  = e.KeyboardDevice.Modifiers;
            bool none = mods == ModifierKeys.None;
            bool ctrl = mods == ModifierKeys.Control;
            bool cs   = mods == (ModifierKeys.Control | ModifierKeys.Shift);
            bool shft = mods == ModifierKeys.Shift;

            // Shift+F10 ili Apps — otvori NATIVE meni
            if ((e.Key == Key.F10 && shft) || e.Key == Key.Apps)
            {
                OpenNativeMenu();
                e.Handled = true;
                return;
            }

            // Space na slideru = play/pause, ne propagiraj dalje
            if (e.Key == Key.Space && none && _playheadSlider?.IsKeyboardFocusWithin == true)
            {
                _vm?.PlayPauseCommand.Execute(null);
                e.Handled = true;
                return;
            }

            // Playhead slider
            if (_playheadSlider?.IsKeyboardFocusWithin == true)
            {
                switch (e.Key)
                {
                    case Key.Right when none: Seek(+0.1); e.Handled = true; return;
                    case Key.Left  when none: Seek(-0.1); e.Handled = true; return;
                    case Key.Right when ctrl: Seek(+1.0); e.Handled = true; return;
                    case Key.Left  when ctrl: Seek(-1.0); e.Handled = true; return;
                    case Key.Home  when none: SeekTo(0); e.Handled = true; return;
                    case Key.End   when none: SeekTo(_vm?.Project.Duration ?? 0); e.Handled = true; return;
                    case Key.Return: case Key.Tab: TrackListBox.Focus(); e.Handled = true; return;
                }
            }

            // ListView klipova
            if (WorkspaceContent.IsKeyboardFocusWithin && _activeTrack != null)
            {
                var lv = FocusedLV();
                if (lv?.SelectedItem is ClipRow cr)
                {
                    var clip = cr.Clip;
                    switch (e.Key)
                    {
                        case Key.F2:                     OpenSetPos(clip); e.Handled = true; break;
                        case Key.Delete when none:       DelClip(clip);    e.Handled = true; break;
                        case Key.Right  when ctrl:       MoveClip(clip, +1.0); e.Handled = true; break;
                        case Key.Left   when ctrl:       MoveClip(clip, -1.0); e.Handled = true; break;
                        case Key.Right  when cs:         MoveClip(clip, +0.1); e.Handled = true; break;
                        case Key.Left   when cs:         MoveClip(clip, -0.1); e.Handled = true; break;
                        case Key.Home   when none:       lv.SelectedIndex = 0; e.Handled = true; break;
                        case Key.End    when none:       lv.SelectedIndex = lv.Items.Count - 1; e.Handled = true; break;
                    }
                }
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // NATIVE WIN32 KONTEKSTNI MENI — JAWS čita savršeno
        // ════════════════════════════════════════════════════════════════════
        private void OpenNativeMenu()
        {
            var win = Window.GetWindow(this);
            if (win == null || _vm == null) return;

            // Odredi koji element ima fokus
            if (TrackListBox.IsKeyboardFocusWithin
                && TrackListBox.SelectedItem is ListBoxItem lbi
                && lbi.Tag is AudioTrack t)
            {
                ShowTrackMenu(t, win);
                return;
            }
            if (_playheadSlider?.IsKeyboardFocusWithin == true && _activeTrack != null)
            {
                ShowTrackMenu(_activeTrack, win);
                return;
            }
            var lv = FocusedLV();
            if (lv != null && _activeTrack != null)
            {
                if (lv.SelectedItem is ClipRow cr)
                    ShowClipMenu(cr.Clip, _activeTrack, win);
                else
                    ShowTrackMenu(_activeTrack, win);
            }
        }

        // Pozicija ispod fokusiranog elementa (za keyboard trigger)
        private System.Drawing.Point GetMenuPosition()
        {
            // Dobijamo poziciju fokusiranog elementa na ekranu
            var focused = Keyboard.FocusedElement as UIElement;
            if (focused != null)
            {
                try
                {
                    var pt = focused.PointToScreen(new Point(0, (focused as FrameworkElement)?.ActualHeight ?? 20));
                    return new System.Drawing.Point((int)pt.X, (int)pt.Y);
                }
                catch { }
            }
            // Fallback — centar prozora
            var win = Window.GetWindow(this);
            if (win != null)
            {
                var center = win.PointToScreen(new Point(win.ActualWidth / 2, win.ActualHeight / 2));
                return new System.Drawing.Point((int)center.X, (int)center.Y);
            }
            return new System.Drawing.Point(200, 200);
        }

        // ── Meni trake ────────────────────────────────────────────────────
        private void ShowTrackMenu(AudioTrack track, Window win)
        {
            double ph = _vm?.PlayheadPosition ?? 0;
            var fx = track.Effects;

            using var m = new NativeContextMenu();

            // Header
            m.AddHeader($"  {track.Name}  [{track.Type}]  Vol: {track.Volume:P0}");
            m.AddHeader($"  Playhead: {FormatSec(ph)}");
            m.AddSeparator();

            // Uvoz
            m.AddItem(Lang.T("trk_import_here"),
                () => { _vm!.SelectedTrack = track; _vm.ImportAudioCommand.Execute(null); });
            m.AddItem(string.Format(Lang.T("trk_import_playhead"), FormatSec(ph)),
                () => ImportAt(track, ph));
            m.AddItem(Lang.T("trk_import_at_pos"),
                () => { double p = AskPos(Lang.T("trk_import_at_pos_title"), ph); if (p >= 0) ImportAt(track, p); });
            m.AddItem(Lang.T("trk_import_new_track"),
                () => { _vm!.AddTrackCommand.Execute(null); _vm!.ImportAudioCommand.Execute(null); });
            m.AddSeparator();

            // Demucs
            m.AddItem(Lang.T("trk_demucs"),
                () => OpenDemucsDialog(track));
            m.AddSeparator();

            // Mute / Solo
            m.AddItem((track.IsMuted ? "[x] " : "[ ] ") + Lang.T("trk_mute"),
                () => { track.IsMuted = !track.IsMuted; _vm!.Announce($"Mute {(track.IsMuted ? "On" : "Off")}"); RefreshActive(); });
            m.AddItem((track.IsSolo ? "[x] " : "[ ] ") + Lang.T("trk_solo"),
                () => { track.IsSolo = !track.IsSolo; _vm!.Announce($"Solo {(track.IsSolo ? "On" : "Off")}"); RefreshActive(); });
            m.AddSeparator();

            // Glasnoća / Panorama
            m.AddItem(string.Format(Lang.T("trk_volume_menu"), track.Volume),
                () => SetVolumeDialog(track));
            m.AddItem(string.Format(Lang.T("trk_pan_menu"), track.Pan),
                () => SetPanDialog(track));
            m.AddSeparator();

            // Efekti — svaki otvara dijalog sa slajderima
            m.AddItem((fx.EqEnabled         ? "[x] " : "[ ] ") + "Equalizer (EQ)...",
                () => OpenFx("Equalizer (EQ)", track, EffectType.Equalizer));
            m.AddItem((fx.ReverbEnabled     ? "[x] " : "[ ] ") + "Reverb...",
                () => OpenFx("Reverb", track, EffectType.Reverb));
            m.AddItem((fx.DelayEnabled      ? "[x] " : "[ ] ") + "Delay / Echo...",
                () => OpenFx("Delay / Echo", track, EffectType.Delay));
            m.AddItem((fx.CompressorEnabled ? "[x] " : "[ ] ") + Lang.T("fx_compressor") + "...",
                () => OpenFx(Lang.T("fx_compressor"), track, EffectType.Compressor));
            m.AddItem((fx.NoiseGateEnabled  ? "[x] " : "[ ] ") + "Noise Gate...",
                () => OpenFx("Noise Gate", track, EffectType.NoiseGate));
            m.AddItem((fx.BassBostEnabled   ? "[x] " : "[ ] ") + "Bass Boost...",
                () => OpenFx("Bass Boost", track, EffectType.BassBoost));
            m.AddItem((fx.PitchEnabled      ? "[x] " : "[ ] ") + "Pitch Shift...",
                () => OpenFx("Pitch Shift", track, EffectType.PitchShift));
            m.AddItem((fx.ChorusEnabled     ? "[x] " : "[ ] ") + "Chorus...",
                () => OpenFx("Chorus", track, EffectType.Chorus));
            m.AddSeparator();

            // Obrada
            m.AddItem(Lang.T("trk_normalize"),
                () => { _vm!.SelectedTrack = track; _vm.NormalizeCommand.Execute(null); });
            m.AddItem(Lang.T("trk_fade_in"),
                () => { _vm!.SelectedTrack = track; _vm.FadeInCommand.Execute(null); });
            m.AddItem(Lang.T("trk_fade_out"),
                () => { _vm!.SelectedTrack = track; _vm.FadeOutCommand.Execute(null); });
            m.AddSeparator();

            // Kombinovanje
            var others = _vm!.Project.Tracks.Where(t => t != track && t.Clips.Any()).ToList();
            foreach (var o in others)
            {
                var oo = o;
                m.AddItem(string.Format(Lang.T("trk_combine_with"), oo.Name, oo.Clips.Count),
                    () => CombineDialog(track, oo));
            }
            if (others.Any()) m.AddSeparator();

            // Organizacija
            m.AddItem(Lang.T("trk_move_up"),
                () => { _vm!.SelectedTrack = track; _vm.MoveTrackUpCommand.Execute(null); });
            m.AddItem(Lang.T("trk_move_down"),
                () => { _vm!.SelectedTrack = track; _vm.MoveTrackDownCommand.Execute(null); });
            m.AddItem(Lang.T("trk_duplicate"),
                () => { _vm!.SelectedTrack = track; _vm.DuplicateTrackCommand.Execute(null); });
            m.AddItem(Lang.T("trk_rename"),
                () => { var d = new SetValueDialog(Lang.T("trk_rename_title"), Lang.T("trk_rename_prompt"), track.Name, ""); d.Owner = win; if (d.ShowDialog()==true && !string.IsNullOrWhiteSpace(d.ResultValue)) { track.Name=d.ResultValue.Trim(); RefreshActive(); } });
            m.AddSeparator();
            m.AddItem(Lang.T("trk_delete"),
                () => { _vm!.SelectedTrack = track; _vm.RemoveTrackCommand.Execute(null); });

            var pos = GetMenuPosition();
            m.ShowAtPosition(win, pos.X, pos.Y);
        }

        // ── Meni klipa ────────────────────────────────────────────────────
        private void ShowClipMenu(AudioClip clip, AudioTrack track, Window win)
        {
            double ph = _vm?.PlayheadPosition ?? 0;

            using var m = new NativeContextMenu();

            m.AddHeader($"  {clip.Name}");
            m.AddHeader(string.Format(Lang.T("clip_header"), FormatSec(clip.StartTime), FormatSec(clip.Duration), FormatSec(clip.StartTime + clip.Duration)));
            m.AddSeparator();

            m.AddItem(Lang.T("clip_set_pos_menu"),
                () => OpenSetPos(clip));
            m.AddItem(string.Format(Lang.T("clip_set_playhead"), FormatSec(ph)),
                () => { clip.StartTime = Math.Max(0, ph); _vm!.Announce(string.Format(Lang.T("clip_at"), FormatSec(clip.StartTime))); UpdateStatus(); BuildFileList(track); });
            m.AddItem(Lang.T("clip_set_at_pos"),
                () => { double p = AskPos(Lang.T("clip_set_title"), clip.StartTime); if (p >= 0) { clip.StartTime = Math.Max(0, p); _vm!.Announce(string.Format(Lang.T("clip_at"), FormatSec(clip.StartTime))); UpdateStatus(); BuildFileList(track); } });
            m.AddSeparator();

            m.AddItem(string.Format(Lang.T("clip_move_fwd_1"), FormatSec(clip.StartTime + 1)),
                () => MoveClip(clip, +1.0));
            m.AddItem(string.Format(Lang.T("clip_move_back_1"), FormatSec(Math.Max(0, clip.StartTime - 1))),
                () => MoveClip(clip, -1.0));
            m.AddItem(string.Format(Lang.T("clip_move_fwd_01"), FormatSec(clip.StartTime + 0.1)),
                () => MoveClip(clip, +0.1));
            m.AddItem(string.Format(Lang.T("clip_move_back_01"), FormatSec(Math.Max(0, clip.StartTime - 0.1))),
                () => MoveClip(clip, -0.1));

            var others = _vm!.Project.Tracks.Where(t => t != track).ToList();
            if (others.Any())
            {
                m.AddSeparator();
                foreach (var o in others)
                {
                    var oo = o;
                    m.AddItem(string.Format(Lang.T("clip_import_on_track"), oo.Name, FormatSec(clip.StartTime)),
                        () => ImportAt(oo, clip.StartTime));
                }
            }

            m.AddSeparator();
            m.AddItem(Lang.T("clip_delete_menu"),
                () => DelClip(clip));

            var pos = GetMenuPosition();
            m.ShowAtPosition(win, pos.X, pos.Y);
        }

        private void OpenFx(string title, AudioTrack track, EffectType type)
        {
            var dlg = new EffectDialog(title, track.Effects, type)
                { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
            RefreshActive();
        }

        // ════════════════════════════════════════════════════════════════════
        // PLAYHEAD PANEL
        // ════════════════════════════════════════════════════════════════════
        private void BuildPlayheadPanel()
        {
            PlayheadPanel.Children.Clear();

            var grid = new Grid { Margin = new Thickness(8, 5, 8, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var phLbl = new TextBlock
            {
                Text = "PLAYHEAD", FontSize = 10, FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 184)),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0)
            };
            Grid.SetColumn(phLbl, 0); grid.Children.Add(phLbl);

            _playheadSlider = new Slider
            {
                Minimum = 0, Maximum = Math.Max(10, _vm?.Project.Duration ?? 60),
                Value = _vm?.PlayheadPosition ?? 0,
                VerticalAlignment = VerticalAlignment.Center,
                LargeChange = 5.0, SmallChange = 0.1, IsTabStop = true
            };
            _playheadSlider.SetValue(AutomationProperties.NameProperty,
                "Playhead pozicija. " +
                "Strelice levo desno za 0.1 sekunde. " +
                "Ctrl plus strelice za 1 sekundu. " +
                "Home za pocetak. End za kraj. " +
                "Space za reprodukciju. " +
                "Enter ili Tab za listu traka. " +
                "Shift F10 za meni trake.");
            _playheadSlider.ValueChanged += (_, ev) =>
            {
                if (_vm != null && Math.Abs(_vm.PlayheadPosition - ev.NewValue) > 0.001)
                    _vm.PlayheadPosition = ev.NewValue;
                if (_playheadTimeBlock != null) _playheadTimeBlock.Text = FormatSec(ev.NewValue);
            };
            Grid.SetColumn(_playheadSlider, 1); grid.Children.Add(_playheadSlider);

            _playheadTimeBlock = new TextBlock
            {
                FontSize = 13, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(78, 207, 160)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 10, 0), MinWidth = 100, Text = "00:00.00"
            };
            _playheadTimeBlock.SetValue(AutomationProperties.NameProperty, Lang.T("acc_playhead_pos"));
            _playheadTimeBlock.SetValue(AutomationProperties.LiveSettingProperty, AutomationLiveSetting.Assertive);
            Grid.SetColumn(_playheadTimeBlock, 2); grid.Children.Add(_playheadTimeBlock);

            var btnGoto = new Button
            {
                Content = Lang.T("goto_btn"), Height = 24, Padding = new Thickness(8, 0, 8, 0),
                Style = (Style)Application.Current.Resources["StdButton"],
                VerticalAlignment = VerticalAlignment.Center
            };
            btnGoto.SetValue(AutomationProperties.NameProperty, Lang.T("clip_goto_pos"));
            btnGoto.Click += (_, __) =>
            {
                if (_vm == null) return;
                double p = AskPos(Lang.T("goto_title"), _vm.PlayheadPosition);
                if (p >= 0) { _vm.PlayheadPosition = Math.Clamp(p, 0, _vm.Project.Duration); SyncSlider(); }
            };
            Grid.SetColumn(btnGoto, 3); grid.Children.Add(btnGoto);

            PlayheadPanel.Children.Add(grid);
            PlayheadPanel.Children.Add(new TextBlock
            {
                Text = Lang.T("playhead_hint"),
                FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 130)),
                Margin = new Thickness(8, 0, 8, 3)
            });
            SyncSlider();
        }

        private void SyncSlider()
        {
            if (_vm == null || _playheadSlider == null) return;
            _playheadSlider.Maximum = Math.Max(10, _vm.Project.Duration);
            if (Math.Abs(_playheadSlider.Value - _vm.PlayheadPosition) > 0.001)
                _playheadSlider.Value = _vm.PlayheadPosition;
            if (_playheadTimeBlock != null) _playheadTimeBlock.Text = FormatSec(_vm.PlayheadPosition);
        }

        // ════════════════════════════════════════════════════════════════════
        // LISTA TRAKA
        // ════════════════════════════════════════════════════════════════════
        public void RebuildTrackList()
        {
            if (_vm == null) return;
            TrackListBox.Items.Clear();
            foreach (var track in _vm.Project.Tracks)
            {
                var item = new ListBoxItem { Tag = track };
                item.SetValue(AutomationProperties.NameProperty, TrackAria(track));
                item.Content = BuildTrackContent(track);
                TrackListBox.Items.Add(item);
                track.PropertyChanged         += (_, __) => Dispatcher.Invoke(() => RefreshItem(item, track));
                track.Clips.CollectionChanged += (_, __) => Dispatcher.Invoke(() => RefreshItem(item, track));
            }
            if (TrackListBox.Items.Count > 0) TrackListBox.SelectedIndex = 0;
            SyncSlider(); UpdateStatus();
        }

        public void Rebuild() => RebuildTrackList();

        private void RefreshItem(ListBoxItem item, AudioTrack track)
        {
            item.Content = BuildTrackContent(track);
            item.SetValue(AutomationProperties.NameProperty, TrackAria(track));
            if (_activeTrack == track) BuildFileList(track);
            SyncSlider();
        }

        private static UIElement BuildTrackContent(AudioTrack track)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var bar = new Border { Background = new SolidColorBrush(track.Color), Width = 4 };
            Grid.SetColumn(bar, 0); g.Children.Add(bar);

            var icon = new TextBlock
            {
                Text = track.Type switch
                {
                    TrackType.Vocal        => Lang.T("tt_vocal") + " ",
                    TrackType.Instrumental => Lang.T("tt_instrumental") + " ",
                    TrackType.Effects      => Lang.T("effects_header") + " ",
                    _                      => track.Clips.Count > 0 ? Lang.T("tt_audio") + " " : Lang.T("tt_empty") + " "
                },
                FontSize = 11, Margin = new Thickness(6, 10, 4, 10),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 200))
            };
            Grid.SetColumn(icon, 1); g.Children.Add(icon);

            var stack = new StackPanel { Margin = new Thickness(0, 8, 8, 8) };
            var name = new TextBlock
            {
                FontSize = 13, FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Color.FromRgb(232, 232, 240)),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            name.SetBinding(TextBlock.TextProperty,
                new System.Windows.Data.Binding("Name") { Source = track, Mode = System.Windows.Data.BindingMode.OneWay });
            stack.Children.Add(name);
            stack.Children.Add(new TextBlock
            {
                Text = string.Format(Lang.T("trk_row_info"), track.Type, track.Volume, track.Clips.Count) +
                       $"{(track.IsMuted ? "  [MUTE]" : "")}{(track.IsSolo ? "  [SOLO]" : "")}",
                FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 184))
            });
            if (track.Clips.Count > 0)
                stack.Children.Add(new TextBlock
                {
                    Text = string.Format(Lang.T("duration_fmt"), FormatSec(track.Clips.Max(c => c.StartTime + c.Duration))),
                    FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(100, 160, 100))
                });
            Grid.SetColumn(stack, 2); g.Children.Add(stack);
            return g;
        }

        private static string TrackAria(AudioTrack t)
        {
            double dur = t.Clips.Count > 0 ? t.Clips.Max(c => c.StartTime + c.Duration) : 0;
            return string.Format(Lang.T("trk_summary"), t.Name, t.Type, t.Volume) +
                   string.Format(Lang.T("trk_files_summary"), t.Clips.Count, FormatSec(dur)) +
                   $"{(t.IsMuted ? Lang.T("suffix_muted") : "")}{(t.IsSolo ? Lang.T("suffix_solo") : "")}. " +
                   Lang.T("press_shift_f10");
        }

        private void TrackListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TrackListBox.SelectedItem is ListBoxItem item && item.Tag is AudioTrack track)
            {
                _vm!.SelectedTrack = track; _vm.SelectedClip = null;
                _activeTrack = track;
                BuildFileList(track);
                UpdateStatus();
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // LISTA FAJLOVA — desni panel, samo fajlovi
        // ════════════════════════════════════════════════════════════════════
        private void BuildFileList(AudioTrack track)
        {
            WorkspaceContent.Children.Clear();
            TxtWorkspaceTitle.Text =
                $"{track.Name}  [{track.Type}]  " +
                $"Vol: {track.Volume:P0}  Pan: {track.Pan:F2}" +
                $"{(track.IsMuted ? "  [MUTE]" : "")}{(track.IsSolo ? "  [SOLO]" : "")}";

            WorkspaceContent.Children.Add(new TextBlock
            {
                Text = Lang.T("filelist_hint"),
                FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 130)),
                Margin = new Thickness(8, 5, 8, 4)
            });

            if (track.Clips.Count == 0)
            {
                WorkspaceContent.Children.Add(new TextBlock
                {
                    Text = Lang.T("no_files_hint"),
                    FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 180)),
                    Margin = new Thickness(16, 20, 16, 8), TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            var lv = new ListView
            {
                Background      = new SolidColorBrush(Color.FromRgb(20, 20, 34)),
                BorderThickness = new Thickness(0, 1, 0, 1),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(58, 58, 82)),
                SelectionMode   = SelectionMode.Single, IsTabStop = true
            };
            lv.SetValue(AutomationProperties.NameProperty,
                string.Format(Lang.T("trk_files_of"), track.Name) + Lang.T("filelist_nav_help"));

            var gv = new GridView();
            gv.Columns.Add(MkCol(Lang.T("col_name"),    "Name",         200));
            gv.Columns.Add(MkCol(Lang.T("col_start"),  "StartTimeFmt",  90));
            gv.Columns.Add(MkCol(Lang.T("col_start_s"),   "StartTimeStr",  65));
            gv.Columns.Add(MkCol(Lang.T("col_duration"), "DurationFmt",   90));
            gv.Columns.Add(MkCol(Lang.T("col_dur_s"),   "DurationStr",   65));
            gv.Columns.Add(MkCol(Lang.T("col_end"),     "EndTimeFmt",    90));
            lv.View = gv;

            foreach (var clip in track.Clips)
                lv.Items.Add(new ClipRow(clip));

            lv.SelectionChanged += (_, __) =>
            {
                if (lv.SelectedItem is ClipRow cr) { _vm!.SelectedClip = cr.Clip; UpdateStatus(); }
            };
            if (_vm!.SelectedClip != null)
            {
                var row = lv.Items.OfType<ClipRow>().FirstOrDefault(r => r.Clip == _vm.SelectedClip);
                if (row != null) lv.SelectedItem = row;
            }
            WorkspaceContent.Children.Add(lv);
        }

        private static GridViewColumn MkCol(string h, string b, double w) =>
            new() { Header = h, Width = w, DisplayMemberBinding = new System.Windows.Data.Binding(b) };

        // ════════════════════════════════════════════════════════════════════
        // DEMUCS
        // ════════════════════════════════════════════════════════════════════
        private async void OpenDemucsDialog(AudioTrack track)
        {
            if (_vm == null || !track.Clips.Any())
            {
                MessageBox.Show(Lang.T("trk_no_audio_files"), Lang.T("trk_no_files"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var svc = new DemucsService();
            bool ok = await svc.CheckAvailableAsync();
            if (!ok)
            {
                MessageBox.Show(
                    string.Format(Lang.T("demucs_not_installed_msg"), svc.StatusMessage),
                    Lang.T("demucs_not_installed_title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var clip = track.Clips.First();
            var folderDlg = new Ookii.Dialogs.Wpf.VistaFolderBrowserDialog
            {
                Description = Lang.T("demucs_pick_folder"),
                UseDescriptionForTitle = true
            };
            if (folderDlg.ShowDialog() != true) return;

            var modeResult = MessageBox.Show(
                Lang.T("demucs_mode_msg"),
                Lang.T("demucs_mode_title"), MessageBoxButton.YesNo, MessageBoxImage.Question);

            var mode = modeResult == MessageBoxResult.Yes
                ? DemucsService.StemMode.TwoStems
                : DemucsService.StemMode.FourStems;

            _vm.Announce(Lang.T("demucs_started"));
            var progress = new Progress<(int Percent, string Status)>(p =>
            {
                if (p.Percent >= 0) _vm.AiProgress = p.Percent;
                _vm.StatusMessage = $"Demucs: {p.Status}";
            });

            try
            {
                var result = await svc.SeparateAsync(clip.FilePath, folderDlg.SelectedPath, mode, "htdemucs", progress);
                _vm.AiProgress = 100;
                int added = 0;
                foreach (var sp in result.AllStems.Where(System.IO.File.Exists))
                {
                    var nt = _vm.AddTrackInternal(System.IO.Path.GetFileNameWithoutExtension(sp));
                    nt.Type = sp.Contains("vocal") ? TrackType.Vocal : TrackType.Instrumental;
                    double dur = 0;
                    try { using var r = new AudioFileReader(sp); dur = r.TotalTime.TotalSeconds; } catch { }
                    nt.Clips.Add(new AudioClip { Name = System.IO.Path.GetFileName(sp), FilePath = sp, StartTime = clip.StartTime, Duration = dur, WaveformData = AudioEngine.LoadWaveformData(sp) });
                    added++;
                }
                RebuildTrackList();
                MessageBox.Show(string.Format(Lang.T("demucs_done"), added), Lang.T("done_title"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _vm.AiProgress = 0;
                MessageBox.Show(string.Format(Lang.T("demucs_error"), ex.Message), Lang.T("error_title"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // STATUS
        // ════════════════════════════════════════════════════════════════════
        public void UpdateStatus()
        {
            if (_vm == null) return;
            int clips = _vm.Project.Tracks.Sum(t => t.Clips.Count);
            double dur = _vm.Project.Tracks.SelectMany(t => t.Clips)
                .Select(c => c.StartTime + c.Duration).DefaultIfEmpty(0).Max();
            TxtProjectStatus.Text = string.Format(Lang.T("status_summary"), _vm.Project.Name, _vm.Project.Tracks.Count, clips, FormatSec(dur));
            TxtPlayhead.Text      = $"Playhead: {_vm.TimeDisplay}  ({_vm.PlayheadPosition:F3}s)";
            if (_vm.SelectedClip != null)
            { var c = _vm.SelectedClip; TxtSelection.Text = string.Format(Lang.T("sel_clip"), c.Name, c.StartTime, c.Duration, c.StartTime + c.Duration); }
            else if (_vm.SelectedTrack != null)
                TxtSelection.Text = string.Format(Lang.T("sel_track"), _vm.SelectedTrack.Name, _vm.SelectedTrack.Clips.Count, _vm.SelectedTrack.Volume);
            else
                TxtSelection.Text = Lang.T("select_track_left_list");
        }

        public void FocusFirstTrack()
        {
            TrackListBox.Focus();
            if (TrackListBox.Items.Count > 0) TrackListBox.SelectedIndex = 0;
        }

        // ════════════════════════════════════════════════════════════════════
        // HELPERS
        // ════════════════════════════════════════════════════════════════════
        private void RefreshActive()
        {
            if (_activeTrack == null) return;
            var item = TrackListBox.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag == _activeTrack);
            if (item != null) RefreshItem(item, _activeTrack);
        }

        private void Seek(double delta) { if (_vm != null) { _vm.PlayheadPosition = Math.Clamp(_vm.PlayheadPosition + delta, 0, _vm.Project.Duration); _vm.Announce($"Playhead {_vm.TimeDisplay}"); } }
        private void SeekTo(double pos) { if (_vm != null) { _vm.PlayheadPosition = Math.Clamp(pos, 0, _vm.Project.Duration); _vm.Announce($"Playhead {_vm.TimeDisplay}"); } }
        private void OpenSetPos(AudioClip clip) { if (_vm != null && _activeTrack != null) { _vm.SelectedTrack = _activeTrack; _vm.SelectedClip = clip; _vm.OpenSetClipPositionDialog(); } }
        private void DelClip(AudioClip clip) { if (_vm != null) { _vm.SelectedClip = clip; _vm.DeleteClipCommand.Execute(null); } }
        private void MoveClip(AudioClip clip, double d) { clip.StartTime = Math.Max(0, clip.StartTime + d); _vm?.Announce(string.Format(Lang.T("clip_at"), FormatSec(clip.StartTime))); if (_activeTrack != null) BuildFileList(_activeTrack); UpdateStatus(); }

        private ListView? FocusedLV()
        {
            foreach (UIElement c in WorkspaceContent.Children)
                if (c is ListView lv && lv.IsKeyboardFocusWithin) return lv;
            return null;
        }

        private void ImportAt(AudioTrack track, double position)
        {
            if (_vm == null) return;
            var dlg = new OpenFileDialog { Title = $"Uvezi audio na {FormatSec(position)}", Filter = "Audio|*.wav;*.mp3;*.ogg;*.flac;*.m4a;*.aiff|Svi|*.*" };
            if (dlg.ShowDialog() != true) return;
            double dur = 5;
            try { using var r = new AudioFileReader(dlg.FileName); dur = r.TotalTime.TotalSeconds; } catch { }
            var clip = new AudioClip { Name = System.IO.Path.GetFileName(dlg.FileName), FilePath = dlg.FileName, StartTime = Math.Max(0, position), Duration = dur, WaveformData = AudioEngine.LoadWaveformData(dlg.FileName) };
            _vm.SelectedTrack = track; track.Clips.Add(clip); _vm.SelectedClip = clip;
            _vm.Announce($"Uvezen {clip.Name}. Pocetak {FormatSec(position)}, kraj {FormatSec(position + dur)}.");
            BuildFileList(track);
        }

        private void CombineDialog(AudioTrack t1, AudioTrack t2)
        {
            if (_vm == null) return;
            var dlg = new SetValueDialog($"Kombinuj: {t1.Name} + {t2.Name}", $"Offset za \"{t2.Name}\" (s):", "0", "s");
            dlg.Owner = Window.GetWindow(this);
            if (dlg.ShowDialog() != true) return;
            if (!double.TryParse(dlg.ResultValue.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double offset)) { MessageBox.Show("Neispravan offset."); return; }
            var saveDlg = new Microsoft.Win32.SaveFileDialog { Filter = "WAV|*.wav", FileName = $"{t1.Name}_plus_{t2.Name}" };
            if (saveDlg.ShowDialog() != true) return;
            _vm.Announce("Kombinujem...");
            var orig = t2.Clips.Select(c => c.StartTime).ToList();
            for (int i = 0; i < t2.Clips.Count; i++) t2.Clips[i].StartTime += offset;
            Task.Run(() =>
            {
                try
                {
                    var tmp = new AudioProject { Name = "tmp", SampleRate = _vm.Project.SampleRate, BitDepth = _vm.Project.BitDepth };
                    tmp.Tracks.Add(t1); tmp.Tracks.Add(t2);
                    AudioEngine.ExportMixdown(tmp, saveDlg.FileName, ExportFormat.WAV, 192, pct => Application.Current?.Dispatcher.Invoke(() => _vm.AiProgress = pct));
                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        for (int i = 0; i < t2.Clips.Count; i++) t2.Clips[i].StartTime = orig[i];
                        _vm.Announce("Sacuvano.");
                        if (MessageBox.Show($"Uvesti kao novu traku?\n{saveDlg.FileName}", "Gotovo", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                        {
                            var nt = _vm.AddTrackInternal($"{t1.Name}+{t2.Name}");
                            double dur = 0; try { using var r = new AudioFileReader(saveDlg.FileName); dur = r.TotalTime.TotalSeconds; } catch { }
                            nt.Clips.Add(new AudioClip { Name = System.IO.Path.GetFileName(saveDlg.FileName), FilePath = saveDlg.FileName, StartTime = 0, Duration = dur, WaveformData = AudioEngine.LoadWaveformData(saveDlg.FileName) });
                            RebuildTrackList();
                        }
                    });
                }
                catch (Exception ex) { Application.Current?.Dispatcher.Invoke(() => { for (int i = 0; i < t2.Clips.Count; i++) t2.Clips[i].StartTime = orig[i]; MessageBox.Show($"Greska: {ex.Message}"); }); }
            });
        }

        private void SetVolumeDialog(AudioTrack track)
        {
            var d = new SetValueDialog($"Glasnoca: {track.Name}", "Glasnoca od 0 do 100:", (track.Volume * 100).ToString("F0"), "%");
            d.Owner = Window.GetWindow(this);
            if (d.ShowDialog() == true && float.TryParse(d.ResultValue, out float v))
            { track.Volume = Math.Clamp(v / 100f, 0f, 1f); _vm?.Announce($"Glasnoca: {track.Volume:P0}"); RefreshActive(); }
        }

        private void SetPanDialog(AudioTrack track)
        {
            var d = new SetValueDialog($"Panorama: {track.Name}", "Levo -100, centar 0, desno 100:", (track.Pan * 100).ToString("F0"), "");
            d.Owner = Window.GetWindow(this);
            if (d.ShowDialog() == true && float.TryParse(d.ResultValue, out float v))
            { track.Pan = Math.Clamp(v / 100f, -1f, 1f); _vm?.Announce($"Pan: {track.Pan:F2}"); RefreshActive(); }
        }

        private double AskPos(string title, double cur)
        {
            var d = new SetValueDialog(title, "Sekunde (15.01) ili MM:SS (1:30):", cur.ToString("F2"), "s");
            d.Owner = Window.GetWindow(this);
            return d.ShowDialog() == true ? ParsePos(d.ResultValue) : -1;
        }

        public static string FormatSec(double sec)
        {
            var ts = TimeSpan.FromSeconds(Math.Max(0, sec));
            return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 10:D2}";
        }

        public static double ParsePos(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return -1;
            text = text.Trim().Replace(',', '.');
            if (text.Contains(':'))
            {
                var p = text.Split(':');
                if (p.Length == 2 && int.TryParse(p[0].Trim(), out int mn)
                    && double.TryParse(p[1].Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double sc))
                    return mn * 60.0 + sc;
                return -1;
            }
            return double.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double s) ? s : -1;
        }
    }

    public class ClipRow
    {
        public AudioClip Clip { get; }
        public ClipRow(AudioClip c) { Clip = c; }
        public string Name         => Clip.Name;
        public string StartTimeStr => $"{Clip.StartTime:F3}";
        public string StartTimeFmt => AccessibleTrackList.FormatSec(Clip.StartTime);
        public string DurationStr  => $"{Clip.Duration:F3}";
        public string DurationFmt  => AccessibleTrackList.FormatSec(Clip.Duration);
        public string EndTimeStr   => $"{(Clip.StartTime + Clip.Duration):F3}";
        public string EndTimeFmt   => AccessibleTrackList.FormatSec(Clip.StartTime + Clip.Duration);
    }
}
