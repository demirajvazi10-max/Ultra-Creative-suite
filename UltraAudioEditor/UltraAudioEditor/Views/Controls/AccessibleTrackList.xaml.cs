using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using NAudio.Wave;
using UltraAudioEditor.Models;
using UltraAudioEditor.Services;
using UltraAudioEditor.ViewModels;
using UltraAudioEditor.Localization;

using WF = System.Windows.Forms;

namespace UltraAudioEditor.Views.Controls
{
    /// <summary>
    /// Jedan red u nativnoj Win32 ListView tabeli. Clip == null znači "traka bez
    /// klipova" — prikazana kao samostalan red da ostane dostupna i selektabilna.
    /// </summary>
    public class TrackRow
    {
        public AudioTrack Track { get; set; } = null!;
        public AudioClip? Clip  { get; set; }
    }

    public partial class AccessibleTrackList : UserControl
    {
        private MainViewModel? _vm;
        private AudioTrack?    _activeTrack;
        private Slider?        _playheadSlider;
        private TextBlock?     _playheadTimeBlock;
        private WF.ContextMenuStrip? _contextMenu;

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

            _vm.Project.Tracks.CollectionChanged += (_, __) => RefreshList();
            _vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(MainViewModel.PlayheadPosition)
                                      or nameof(MainViewModel.TimeDisplay))
                { UpdateStatus(); SyncSlider(); }
            };

            PreviewKeyDown += OnPreviewKeyDown;
            BuildPlayheadPanel();
            SetupNativeListView();
            RefreshList();
        }

        // ════════════════════════════════════════════════════════════════════
        // NATIVE WIN32 LISTVIEW — isti obrazac kao Video Editor (WindowsFormsHost).
        // JAWS/NVDA čitaju pravu Win32 SysListView32 kontrolu nativno, bez
        // custom AutomationProperties koda.
        // ════════════════════════════════════════════════════════════════════
        private void SetupNativeListView()
        {
            nativeListView.Columns.Clear();
            nativeListView.Columns.Add(Lang.T("col_num"),    50,  WF.HorizontalAlignment.Center);
            nativeListView.Columns.Add(Lang.T("col_track"),  160, WF.HorizontalAlignment.Left);
            nativeListView.Columns.Add(Lang.T("col_name"),   220, WF.HorizontalAlignment.Left);
            nativeListView.Columns.Add(Lang.T("col_type"),   90,  WF.HorizontalAlignment.Left);
            nativeListView.Columns.Add(Lang.T("col_start"),  80,  WF.HorizontalAlignment.Center);
            nativeListView.Columns.Add(Lang.T("col_duration"), 80, WF.HorizontalAlignment.Center);
            nativeListView.Columns.Add(Lang.T("col_end"),    80,  WF.HorizontalAlignment.Center);
            nativeListView.Columns.Add(Lang.T("col_status"), 140, WF.HorizontalAlignment.Left);

            nativeListView.BackColor = System.Drawing.Color.FromArgb(20, 20, 34);
            nativeListView.ForeColor = System.Drawing.Color.White;
            nativeListView.Font = new System.Drawing.Font("Segoe UI", 10);

            nativeListView.SelectedIndexChanged += NativeListView_SelectedIndexChanged;
            nativeListView.KeyDown += NativeListView_KeyDown;

            _contextMenu = new WF.ContextMenuStrip();
            _contextMenu.Opening += (s, e) => PopulateContextMenu();
            nativeListView.ContextMenuStrip = _contextMenu;

            nativeListView.AccessibleName = Lang.T("acc_list_help");
        }

        private void NativeListView_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (_vm == null) return;
            var rows = SelectedRows();

            if (rows.Count == 1)
            {
                var r = rows[0];
                _vm.SelectedTrack = r.Track;
                _vm.SelectedClip  = r.Clip;
                _activeTrack      = r.Track;
            }
            else if (rows.Count > 1)
            {
                _activeTrack = rows[0].Track;
            }
            UpdateStatus();
        }

        private void NativeListView_KeyDown(object? sender, WF.KeyEventArgs e)
        {
            var rows = SelectedRows();
            var clipRows = rows.Where(r => r.Clip != null).ToList();

            if (e.KeyCode == WF.Keys.F2 && !e.Control && !e.Shift && clipRows.Count == 1)
            {
                OpenSetPos(clipRows[0].Track, clipRows[0].Clip!);
                e.Handled = true;
            }
            else if (e.KeyCode == WF.Keys.Delete && !e.Control && !e.Shift && clipRows.Count > 0)
            {
                DelClips(clipRows);
                e.Handled = true;
            }
            else if (e.KeyCode == WF.Keys.Right && e.Control && !e.Shift && clipRows.Count > 0)
            {
                MoveClips(clipRows, +1.0); e.Handled = true;
            }
            else if (e.KeyCode == WF.Keys.Left && e.Control && !e.Shift && clipRows.Count > 0)
            {
                MoveClips(clipRows, -1.0); e.Handled = true;
            }
            else if (e.KeyCode == WF.Keys.Right && e.Control && e.Shift && clipRows.Count > 0)
            {
                MoveClips(clipRows, +0.1); e.Handled = true;
            }
            else if (e.KeyCode == WF.Keys.Left && e.Control && e.Shift && clipRows.Count > 0)
            {
                MoveClips(clipRows, -0.1); e.Handled = true;
            }
        }

        private List<TrackRow> SelectedRows() =>
            nativeListView.SelectedItems.Cast<WF.ListViewItem>()
                .Select(i => i.Tag as TrackRow).Where(r => r != null).Select(r => r!).ToList();

        // ── Grupni/kontekstni meni — sadržaj se gradi svaki put pre otvaranja ──
        private void PopulateContextMenu()
        {
            if (_contextMenu == null || _vm == null) return;
            _contextMenu.Items.Clear();

            var rows = SelectedRows();
            if (rows.Count == 0) return;

            if (rows.Count == 1)
            {
                if (rows[0].Clip != null) ShowClipMenu(rows[0].Clip!, rows[0].Track);
                else                      ShowTrackMenu(rows[0].Track);
                return;
            }

            ShowBatchMenu(rows);
        }

        private void MAddHeader(string text) =>
            _contextMenu!.Items.Add(new WF.ToolStripMenuItem(text) { Enabled = false });
        private void MAddSeparator() => _contextMenu!.Items.Add(new WF.ToolStripSeparator());
        private void MAddItem(string text, Action action) =>
            _contextMenu!.Items.Add(text, null, (s, e) => action());

        private void ShowBatchMenu(List<TrackRow> rows)
        {
            var tracks = rows.Select(r => r.Track).Distinct().ToList();
            var clips  = rows.Where(r => r.Clip != null).ToList();

            MAddHeader(string.Format(Lang.T("batch_selected"), rows.Count));
            MAddSeparator();
            if (tracks.Count > 0)
                MAddItem(Lang.T("batch_mute"), () => { foreach (var t in tracks) t.IsMuted = true; RefreshList(); });
            if (clips.Count > 0)
            {
                MAddItem(Lang.T("batch_move_fwd"), () => MoveClips(clips, +1.0));
                MAddItem(Lang.T("batch_move_back"), () => MoveClips(clips, -1.0));
                MAddItem(Lang.T("batch_delete_clips"), () => DelClips(clips));
            }
        }

        // ── Meni trake ────────────────────────────────────────────────────
        private void ShowTrackMenu(AudioTrack track)
        {
            double ph = _vm?.PlayheadPosition ?? 0;
            var fx = track.Effects;
            var win = Window.GetWindow(this);

            MAddHeader($"  {track.Name}  [{track.Type}]  Vol: {track.Volume:P0}");
            MAddHeader($"  Playhead: {FormatSec(ph)}");
            MAddSeparator();

            MAddItem(Lang.T("trk_import_here"),
                () => { _vm!.SelectedTrack = track; _vm.ImportAudioCommand.Execute(null); });
            MAddItem(string.Format(Lang.T("trk_import_playhead"), FormatSec(ph)),
                () => ImportAt(track, ph));
            MAddItem(Lang.T("trk_import_at_pos"),
                () => { double p = AskPos(Lang.T("trk_import_at_pos_title"), ph); if (p >= 0) ImportAt(track, p); });
            MAddItem(Lang.T("trk_import_new_track"),
                () => { _vm!.AddTrackCommand.Execute(null); _vm!.ImportAudioCommand.Execute(null); });
            MAddSeparator();

            MAddItem(Lang.T("trk_demucs"), () => OpenDemucsDialog(track));
            MAddSeparator();

            MAddItem((track.IsMuted ? "[x] " : "[ ] ") + Lang.T("trk_mute"),
                () => { track.IsMuted = !track.IsMuted; _vm!.Announce($"Mute {(track.IsMuted ? "On" : "Off")}"); RefreshList(); });
            MAddItem((track.IsSolo ? "[x] " : "[ ] ") + Lang.T("trk_solo"),
                () => { track.IsSolo = !track.IsSolo; _vm!.Announce($"Solo {(track.IsSolo ? "On" : "Off")}"); RefreshList(); });
            MAddSeparator();

            MAddItem(string.Format(Lang.T("trk_volume_menu"), track.Volume), () => SetVolumeDialog(track));
            MAddItem(string.Format(Lang.T("trk_pan_menu"), track.Pan), () => SetPanDialog(track));
            MAddSeparator();

            MAddItem((fx.EqEnabled         ? "[x] " : "[ ] ") + "Equalizer (EQ)...", () => OpenFx("Equalizer (EQ)", track, EffectType.Equalizer));
            MAddItem((fx.ReverbEnabled     ? "[x] " : "[ ] ") + "Reverb...", () => OpenFx("Reverb", track, EffectType.Reverb));
            MAddItem((fx.DelayEnabled      ? "[x] " : "[ ] ") + "Delay / Echo...", () => OpenFx("Delay / Echo", track, EffectType.Delay));
            MAddItem((fx.CompressorEnabled ? "[x] " : "[ ] ") + Lang.T("fx_compressor") + "...", () => OpenFx(Lang.T("fx_compressor"), track, EffectType.Compressor));
            MAddItem((fx.NoiseGateEnabled  ? "[x] " : "[ ] ") + "Noise Gate...", () => OpenFx("Noise Gate", track, EffectType.NoiseGate));
            MAddItem((fx.BassBostEnabled   ? "[x] " : "[ ] ") + "Bass Boost...", () => OpenFx("Bass Boost", track, EffectType.BassBoost));
            MAddItem((fx.PitchEnabled      ? "[x] " : "[ ] ") + "Pitch Shift...", () => OpenFx("Pitch Shift", track, EffectType.PitchShift));
            MAddItem((fx.ChorusEnabled     ? "[x] " : "[ ] ") + "Chorus...", () => OpenFx("Chorus", track, EffectType.Chorus));
            MAddSeparator();

            MAddItem(Lang.T("trk_normalize"), () => { _vm!.SelectedTrack = track; _vm.NormalizeCommand.Execute(null); });
            MAddItem(Lang.T("trk_fade_in"), () => { _vm!.SelectedTrack = track; _vm.FadeInCommand.Execute(null); });
            MAddItem(Lang.T("trk_fade_out"), () => { _vm!.SelectedTrack = track; _vm.FadeOutCommand.Execute(null); });
            MAddSeparator();

            var others = _vm!.Project.Tracks.Where(t => t != track && t.Clips.Any()).ToList();
            foreach (var o in others)
            {
                var oo = o;
                MAddItem(string.Format(Lang.T("trk_combine_with"), oo.Name, oo.Clips.Count), () => CombineDialog(track, oo));
            }
            if (others.Any()) MAddSeparator();

            MAddItem(Lang.T("trk_move_up"), () => { _vm!.SelectedTrack = track; _vm.MoveTrackUpCommand.Execute(null); });
            MAddItem(Lang.T("trk_move_down"), () => { _vm!.SelectedTrack = track; _vm.MoveTrackDownCommand.Execute(null); });
            MAddItem(Lang.T("trk_duplicate"), () => { _vm!.SelectedTrack = track; _vm.DuplicateTrackCommand.Execute(null); });
            MAddItem(Lang.T("trk_rename"),
                () => { var d = new SetValueDialog(Lang.T("trk_rename_title"), Lang.T("trk_rename_prompt"), track.Name, ""); d.Owner = win; if (d.ShowDialog() == true && !string.IsNullOrWhiteSpace(d.ResultValue)) { track.Name = d.ResultValue.Trim(); RefreshList(); } });
            MAddSeparator();
            MAddItem(Lang.T("trk_delete"), () => { _vm!.SelectedTrack = track; _vm.RemoveTrackCommand.Execute(null); });
        }

        // ── Meni klipa ────────────────────────────────────────────────────
        private void ShowClipMenu(AudioClip clip, AudioTrack track)
        {
            double ph = _vm?.PlayheadPosition ?? 0;

            MAddHeader($"  {clip.Name}");
            MAddHeader(string.Format(Lang.T("clip_header"), FormatSec(clip.StartTime), FormatSec(clip.Duration), FormatSec(clip.StartTime + clip.Duration)));
            MAddSeparator();

            MAddItem(Lang.T("clip_set_pos_menu"), () => OpenSetPos(track, clip));
            MAddItem(string.Format(Lang.T("clip_set_playhead"), FormatSec(ph)),
                () => { clip.StartTime = Math.Max(0, ph); _vm!.Announce(string.Format(Lang.T("clip_at"), FormatSec(clip.StartTime))); RefreshList(); });
            MAddItem(Lang.T("clip_set_at_pos"),
                () => { double p = AskPos(Lang.T("clip_set_title"), clip.StartTime); if (p >= 0) { clip.StartTime = Math.Max(0, p); _vm!.Announce(string.Format(Lang.T("clip_at"), FormatSec(clip.StartTime))); RefreshList(); } });
            MAddSeparator();

            MAddItem(string.Format(Lang.T("clip_move_fwd_1"), FormatSec(clip.StartTime + 1)), () => MoveClips(new() { new TrackRow { Track = track, Clip = clip } }, +1.0));
            MAddItem(string.Format(Lang.T("clip_move_back_1"), FormatSec(Math.Max(0, clip.StartTime - 1))), () => MoveClips(new() { new TrackRow { Track = track, Clip = clip } }, -1.0));
            MAddItem(string.Format(Lang.T("clip_move_fwd_01"), FormatSec(clip.StartTime + 0.1)), () => MoveClips(new() { new TrackRow { Track = track, Clip = clip } }, +0.1));
            MAddItem(string.Format(Lang.T("clip_move_back_01"), FormatSec(Math.Max(0, clip.StartTime - 0.1))), () => MoveClips(new() { new TrackRow { Track = track, Clip = clip } }, -0.1));

            var others = _vm!.Project.Tracks.Where(t => t != track).ToList();
            if (others.Any())
            {
                MAddSeparator();
                foreach (var o in others)
                {
                    var oo = o;
                    MAddItem(string.Format(Lang.T("clip_import_on_track"), oo.Name, FormatSec(clip.StartTime)), () => ImportAt(oo, clip.StartTime));
                }
            }

            MAddSeparator();
            MAddItem(Lang.T("clip_delete_menu"), () => DelClips(new() { new TrackRow { Track = track, Clip = clip } }));
        }

        private void OpenFx(string title, AudioTrack track, EffectType type)
        {
            var dlg = new EffectDialog(title, track.Effects, type) { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
            RefreshList();
        }

        // ════════════════════════════════════════════════════════════════════
        // WPF KEYBOARD — samo za playhead slajder (lista se rukuje kroz WinForms KeyDown)
        // ════════════════════════════════════════════════════════════════════
        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var mods  = e.KeyboardDevice.Modifiers;
            bool none = mods == ModifierKeys.None;
            bool ctrl = mods == ModifierKeys.Control;

            if (e.Key == Key.Space && none && _playheadSlider?.IsKeyboardFocusWithin == true)
            {
                _vm?.PlayPauseCommand.Execute(null);
                e.Handled = true;
                return;
            }

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
                    case Key.Return: case Key.Tab: nativeListView.Focus(); e.Handled = true; return;
                }
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // PLAYHEAD PANEL (nepromenjeno u odnosu na prethodnu verziju)
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
                LargeChange = 5.0, SmallChange = 0.1, IsTabStop = true,
                Height = 28,
                // Podrazumevano WPF Slider PONAŠANJE je da klik na traku samo "pomeri
                // za jedan LargeChange" u tom smeru — ne skoči tačno tamo gde si
                // kliknuo. To je razlog zašto je delovalo "nezgrapno" svakom ko očekuje
                // standardno ponašanje kao YouTube/Spotify (klik = skoči tačno tu).
                // IsMoveToPointEnabled to ispravlja.
                IsMoveToPointEnabled = true
            };
            _playheadSlider.SetValue(AutomationProperties.NameProperty,
                "Playhead position. Left/right arrows for 0.1 second. Ctrl+arrows for 1 second. " +
                "Home for start. End for end. Space to play. Enter or Tab for the track list.");
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
        // LISTA — puni nativeListView: red po klip, ili jedan red za praznu traku
        // ════════════════════════════════════════════════════════════════════
        public void RefreshList()
        {
            if (_vm == null) return;

            var prevTrack = _activeTrack;
            var prevClip  = _vm.SelectedClip;

            nativeListView.BeginUpdate();
            nativeListView.Items.Clear();
            WF.ListViewItem? toReselect = null;
            int n = 1;

            foreach (var track in _vm.Project.Tracks)
            {
                string typeText = track.Type switch
                {
                    TrackType.Vocal        => Lang.T("tt_vocal"),
                    TrackType.Instrumental => Lang.T("tt_instrumental"),
                    TrackType.Effects      => Lang.T("effects_header"),
                    _                      => Lang.T("tt_audio")
                };
                string statusText = $"{track.Volume:P0}{(track.IsMuted ? "  MUTE" : "")}{(track.IsSolo ? "  SOLO" : "")}";

                // Red za SAMU TRAKU — uvek postoji, bez obzira da li ima klipova.
                // Ovde se pristupa Demucs-u, mute/solo, efektima, preimenovanju, brisanju
                // trake itd. (ranije je ovaj red postojao SAMO kad traka nema klipova,
                // pa su sve te akcije bile potpuno nedostupne za bilo koju traku sa audio
                // fajlom — to je bio pravi uzrok "Demucs opcije nema u meniju".)
                string clipSummary = track.Clips.Count == 0
                    ? Lang.T("row_no_clips")
                    : string.Format(Lang.T("row_track_summary"), track.Clips.Count);
                var trackLvi = new WF.ListViewItem(new[]
                {
                    n.ToString(), track.Name, clipSummary, typeText, "", "", "", statusText
                })
                { Tag = new TrackRow { Track = track }, Font = new System.Drawing.Font(nativeListView.Font, System.Drawing.FontStyle.Bold) };
                nativeListView.Items.Add(trackLvi);
                n++;
                if (track == prevTrack && prevClip == null) toReselect = trackLvi;

                if (track.Clips.Count == 0) continue;

                foreach (var clip in track.Clips)
                {
                    var lvi = new WF.ListViewItem(new[]
                    {
                        n.ToString(), track.Name, clip.Name, typeText,
                        FormatSec(clip.StartTime), FormatSec(clip.Duration), FormatSec(clip.StartTime + clip.Duration),
                        statusText
                    })
                    { Tag = new TrackRow { Track = track, Clip = clip } };
                    nativeListView.Items.Add(lvi);
                    n++;
                    if (clip == prevClip) toReselect = lvi;
                }
            }

            nativeListView.EndUpdate();

            if (toReselect != null) { toReselect.Selected = true; toReselect.Focused = true; toReselect.EnsureVisible(); }
            else if (nativeListView.Items.Count > 0) { nativeListView.Items[0].Selected = true; }

            SyncSlider(); UpdateStatus();
        }

        public void Rebuild() => RefreshList();

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
            _vm.Announce(Lang.T("demucs_checking"));
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

            var progressDlg = new DemucsProgressDialog(Lang.T("demucs_dialog_title"))
            {
                Owner = Window.GetWindow(this)
            };

            var progress = new Progress<(int Percent, string Status)>(p =>
                progressDlg.SetProgress(p.Percent, p.Percent < 0 ? p.Status : null));

            // ShowDialog() je modalno (fokus ostaje u prozoru dok traje), ali WPF-ov
            // ugnježdeni message loop i dalje pumpa await nastavke na ovom thread-u,
            // pa async posao ispod normalno napreduje i na kraju zatvara dijalog.
            progressDlg.Show();

            try
            {
                var result = await svc.SeparateAsync(clip.FilePath, folderDlg.SelectedPath, mode, "htdemucs", progress, progressDlg.Cts.Token);

                if (progressDlg.WasCancelled)
                {
                    progressDlg.Finish(Lang.T("demucs_dialog_cancelled"));
                    return;
                }

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
                RefreshList();
                progressDlg.Finish(string.Format(Lang.T("demucs_done"), added));
            }
            catch (OperationCanceledException)
            {
                progressDlg.Finish(Lang.T("demucs_dialog_cancelled"));
            }
            catch (Exception ex)
            {
                progressDlg.Finish(string.Format(Lang.T("demucs_error"), ex.Message));
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

            int selCount = nativeListView.SelectedItems.Count;
            if (selCount > 1)
                TxtSelection.Text = string.Format(Lang.T("batch_selected"), selCount);
            else if (_vm.SelectedClip != null)
            { var c = _vm.SelectedClip; TxtSelection.Text = string.Format(Lang.T("sel_clip"), c.Name, c.StartTime, c.Duration, c.StartTime + c.Duration); }
            else if (_vm.SelectedTrack != null)
                TxtSelection.Text = string.Format(Lang.T("sel_track"), _vm.SelectedTrack.Name, _vm.SelectedTrack.Clips.Count, _vm.SelectedTrack.Volume);
            else
                TxtSelection.Text = Lang.T("select_track_left_list");
        }

        public void FocusFirstTrack()
        {
            nativeListView.Focus();
            if (nativeListView.Items.Count > 0)
            {
                nativeListView.Items[0].Selected = true;
                nativeListView.Items[0].Focused  = true;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // HELPERS
        // ════════════════════════════════════════════════════════════════════
        private void Seek(double delta) { if (_vm != null) { _vm.PlayheadPosition = Math.Clamp(_vm.PlayheadPosition + delta, 0, _vm.Project.Duration); _vm.Announce($"Playhead {_vm.TimeDisplay}"); } }
        private void SeekTo(double pos) { if (_vm != null) { _vm.PlayheadPosition = Math.Clamp(pos, 0, _vm.Project.Duration); _vm.Announce($"Playhead {_vm.TimeDisplay}"); } }
        private void OpenSetPos(AudioTrack track, AudioClip clip) { if (_vm != null) { _vm.SelectedTrack = track; _vm.SelectedClip = clip; _vm.OpenSetClipPositionDialog(); } }

        private void DelClips(List<TrackRow> rows)
        {
            if (_vm == null) return;
            foreach (var r in rows.Where(r => r.Clip != null))
            {
                _vm.SelectedTrack = r.Track;
                _vm.SelectedClip  = r.Clip;
                _vm.DeleteClipCommand.Execute(null);
            }
            RefreshList();
        }

        private void MoveClips(List<TrackRow> rows, double d)
        {
            foreach (var r in rows.Where(r => r.Clip != null))
                r.Clip!.StartTime = Math.Max(0, r.Clip.StartTime + d);
            if (rows.Count == 1 && rows[0].Clip != null)
                _vm?.Announce(string.Format(Lang.T("clip_at"), FormatSec(rows[0].Clip!.StartTime)));
            RefreshList();
        }

        private void ImportAt(AudioTrack track, double position)
        {
            if (_vm == null) return;
            var dlg = new OpenFileDialog { Title = $"{Lang.T("trk_import_at_pos_title")} {FormatSec(position)}", Filter = "Audio|*.wav;*.mp3;*.ogg;*.flac;*.m4a;*.aiff|All|*.*" };
            if (dlg.ShowDialog() != true) return;
            double dur = 5;
            try { using var r = new AudioFileReader(dlg.FileName); dur = r.TotalTime.TotalSeconds; } catch { }
            var clip = new AudioClip { Name = System.IO.Path.GetFileName(dlg.FileName), FilePath = dlg.FileName, StartTime = Math.Max(0, position), Duration = dur, WaveformData = AudioEngine.LoadWaveformData(dlg.FileName) };
            _vm.SelectedTrack = track; track.Clips.Add(clip); _vm.SelectedClip = clip;
            RefreshList();
        }

        private void CombineDialog(AudioTrack t1, AudioTrack t2)
        {
            if (_vm == null) return;
            var dlg = new SetValueDialog($"{t1.Name} + {t2.Name}", $"Offset ({t2.Name}), s:", "0", "s");
            dlg.Owner = Window.GetWindow(this);
            if (dlg.ShowDialog() != true) return;
            if (!double.TryParse(dlg.ResultValue.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double offset)) return;
            var saveDlg = new Microsoft.Win32.SaveFileDialog { Filter = "WAV|*.wav", FileName = $"{t1.Name}_plus_{t2.Name}" };
            if (saveDlg.ShowDialog() != true) return;
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
                        if (MessageBox.Show($"{saveDlg.FileName}", Lang.T("done_title"), MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                        {
                            var nt = _vm.AddTrackInternal($"{t1.Name}+{t2.Name}");
                            double dur = 0; try { using var r = new AudioFileReader(saveDlg.FileName); dur = r.TotalTime.TotalSeconds; } catch { }
                            nt.Clips.Add(new AudioClip { Name = System.IO.Path.GetFileName(saveDlg.FileName), FilePath = saveDlg.FileName, StartTime = 0, Duration = dur, WaveformData = AudioEngine.LoadWaveformData(saveDlg.FileName) });
                            RefreshList();
                        }
                    });
                }
                catch (Exception ex) { Application.Current?.Dispatcher.Invoke(() => { for (int i = 0; i < t2.Clips.Count; i++) t2.Clips[i].StartTime = orig[i]; MessageBox.Show(string.Format(Lang.T("error_prefix"), ex.Message)); }); }
            });
        }

        private void SetVolumeDialog(AudioTrack track)
        {
            var d = new SetValueDialog(track.Name, "0-100:", (track.Volume * 100).ToString("F0"), "%");
            d.Owner = Window.GetWindow(this);
            if (d.ShowDialog() == true && float.TryParse(d.ResultValue, out float v))
            { track.Volume = Math.Clamp(v / 100f, 0f, 1f); RefreshList(); }
        }

        private void SetPanDialog(AudioTrack track)
        {
            var d = new SetValueDialog(track.Name, "-100..100:", (track.Pan * 100).ToString("F0"), "");
            d.Owner = Window.GetWindow(this);
            if (d.ShowDialog() == true && float.TryParse(d.ResultValue, out float v))
            { track.Pan = Math.Clamp(v / 100f, -1f, 1f); RefreshList(); }
        }

        private double AskPos(string title, double cur)
        {
            var d = new SetValueDialog(title, "MM:SS.ms:", cur.ToString("F2"), "s");
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
}
