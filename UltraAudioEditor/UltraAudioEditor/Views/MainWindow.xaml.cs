using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UltraAudioEditor.ViewModels;
using UltraAudioEditor.Controls;
using UltraAudioEditor.Views.Controls;
using UltraAudioEditor.Localization;

namespace UltraAudioEditor.Views
{
    public partial class MainWindow : Window
    {
        private MainViewModel VM => (MainViewModel)DataContext;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            VM.OnToggleWorkspaceMode = () =>
            {
                if (VM.IsJawsMode) SetVisualMode(); else SetJawsMode();
            };
            VM.OnRebuildTrackList = () =>
            {
                Dispatcher.Invoke(() =>
                {
                    AccessibleTrackList.DataContext = VM;
                    AccessibleTrackList.Rebuild();
                });
            };
            AccessibleTrackList.DataContext = VM;
            // PropertyChanged na traci trigguje rebuild
            VM.Project.Tracks.CollectionChanged += (_, __) =>
                Dispatcher.Invoke(() => AccessibleTrackList.Rebuild());
            VM.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(VM.PlayheadPosition) or nameof(VM.TimeDisplay)
                    or nameof(VM.SelectedClip) or nameof(VM.SelectedTrack))
                    Dispatcher.Invoke(() => AccessibleTrackList.UpdateStatus());
            };
            // PreviewKeyDown hvata Space/S/R prije nego List kontrole progutaju event
            this.PreviewKeyDown += MainWindow_PreviewKeyDown;
            this.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            UpdateLanguageChecks();
            VM.Announce(Lang.T("app_loaded"));
        }

        private void MenuItem_Exit(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(Lang.T("exit_confirm"), Lang.T("exit_title"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                Application.Current.Shutdown();
        }

        private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            VM.AiApiKey = ((PasswordBox)sender).Password;
        }

        private void WaveformArea_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                foreach (var file in files)
                {
                    var ext = System.IO.Path.GetExtension(file).ToLower();
                    if (ext is ".wav" or ".mp3" or ".ogg" or ".flac" or ".m4a" or ".aiff" or ".aif")
                    {
                        var track = VM.AddTrackInternal(System.IO.Path.GetFileNameWithoutExtension(file));
                        double dur = 5;
                        try { using var r = new NAudio.Wave.AudioFileReader(file); dur = r.TotalTime.TotalSeconds; } catch { }
                        track.Clips.Add(new Models.AudioClip
                        {
                            Name = System.IO.Path.GetFileName(file),
                            FilePath = file,
                            StartTime = 0,
                            Duration = dur,
                            WaveformData = Services.AudioEngine.LoadWaveformData(file)
                        });
                        VM.Announce(string.Format(Lang.T("dropped_file"), System.IO.Path.GetFileName(file), dur));
                    }
                }
            }
        }

        private void WaveformArea_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is WaveformControl wc)
            {
                var track = wc.DataContext as Models.AudioTrack;
                if (track == null) return;

                VM.SelectedTrack = track;
                double x = e.GetPosition(wc).X;
                double duration = Math.Max(1, VM.Project.Duration);
                double clickTime = (x / wc.ActualWidth) * duration / VM.ZoomLevel;

                // Provjeri da li je klik na neki klip
                Models.AudioClip? clickedClip = null;
                foreach (var clip in track.Clips)
                {
                    double clipX     = clip.StartTime / duration * wc.ActualWidth * VM.ZoomLevel;
                    double clipEndX  = (clip.StartTime + clip.Duration) / duration * wc.ActualWidth * VM.ZoomLevel;
                    if (x >= clipX && x <= clipEndX)
                    {
                        clickedClip = clip;
                        break;
                    }
                }

                if (clickedClip != null)
                {
                    // Selektuj klip
                    VM.SelectClip(clickedClip, track);
                    // Dvostruki klik otvara dialog
                    if (e.ClickCount == 2)
                        VM.OpenSetClipPositionDialog();
                }
                else
                {
                    // Klik na prazno — postavi playhead
                    VM.SelectedClip = null;
                    VM.PlayheadPosition = Math.Max(0, clickTime);
                    VM.Announce(string.Format(Lang.T("position_is"), VM.TimeDisplay));
                }
            }
        }

        private void ShowShortcuts_Click(object sender, RoutedEventArgs e)
        {
            var msg = Lang.T("shortcuts_text");

            MessageBox.Show(msg, Lang.T("shortcuts_title"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OpenApiKeyLink_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string url = VM.ApiKeyLink;
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }

                // ─── Dual-Mode Workspace ───────────────────────────────────────────

        private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            var mods = e.KeyboardDevice.Modifiers;
            bool noMods = mods == System.Windows.Input.ModifierKeys.None;

            // Ako je fokus UNUTAR JAWS panela (AccessibleTrackList),
            // ne palimo globalne transport prečice — kontrola sama hendla šta treba.
            bool focusInJaws = VM.IsJawsMode && AccessibleTrackList.IsKeyboardFocusWithin;

            // Space — Play/Pause SAMO kad fokus nije u JAWS panelu
            if (e.Key == System.Windows.Input.Key.Space && noMods && !focusInJaws)
            {
                VM.PlayPauseCommand.Execute(null);
                e.Handled = true;
                return;
            }
            // S — Stop (ne u textboxu, ne u JAWS panelu)
            if (e.Key == System.Windows.Input.Key.S && noMods
                && !focusInJaws
                && !(e.OriginalSource is System.Windows.Controls.TextBox))
            {
                VM.StopCommand.Execute(null);
                e.Handled = true;
                return;
            }
            // Home/End — transport SAMO kad fokus nije u JAWS panelu
            if ((e.Key == System.Windows.Input.Key.Home || e.Key == System.Windows.Input.Key.End)
                && noMods && focusInJaws)
            {
                // Ne radimo ništa — JAWS panel sam hendla Home/End
                return;
            }
            // F6 — Status (uvijek OK)
            if (e.Key == System.Windows.Input.Key.F6 && noMods)
            {
                VM.AnnounceProjectStatus();
                e.Handled = true;
                return;
            }
        }

        private static string FormatDuration(double seconds)
        {
            var ts = TimeSpan.FromSeconds(seconds);
            return string.Format("{0:D2}:{1:D2}.{2:D2}", (int)ts.TotalMinutes, ts.Seconds, ts.Milliseconds / 10);
        }

        private void BtnVisualMode_Click(object sender, RoutedEventArgs e) => SetVisualMode();
        private void BtnJawsMode_Click(object sender, RoutedEventArgs e) => SetJawsMode();

        private void SetVisualMode()
        {
            VisualWorkspace.Visibility     = System.Windows.Visibility.Visible;
            AccessibleTrackList.Visibility = System.Windows.Visibility.Collapsed;
            CurrentModeLabel.Text      = Lang.T("visual_mode_indicator");
            BtnVisualMode.Style        = (System.Windows.Style)FindResource("AIButton");
            BtnJawsMode.Style          = (System.Windows.Style)FindResource("StdButton");
            VM.IsJawsMode = false;
            VM.Announce(Lang.T("visual_mode_on"));
        }

        private void SetJawsMode()
        {
            VisualWorkspace.Visibility      = System.Windows.Visibility.Collapsed;
            AccessibleTrackList.Visibility  = System.Windows.Visibility.Visible;
            CurrentModeLabel.Text           = Lang.T("jaws_mode_indicator");
            BtnJawsMode.Style               = (System.Windows.Style)FindResource("AIButton");
            BtnVisualMode.Style             = (System.Windows.Style)FindResource("StdButton");
            VM.IsJawsMode = true;
            AccessibleTrackList.Rebuild();
            AccessibleTrackList.FocusFirstTrack();
            VM.Announce(Lang.T("jaws_mode_on"));
        }

        private void RefreshJawsSummary()
        {
            // AccessibleTrackList.UpdateStatus() preuzeo ovu ulogu
            if (VM.IsJawsMode)
                AccessibleTrackList.UpdateStatus();
        }

        protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
        {
            base.OnKeyDown(e);
            // Alt+W — prebaci mod
            if (e.Key == System.Windows.Input.Key.W &&
                e.KeyboardDevice.Modifiers == System.Windows.Input.ModifierKeys.Alt)
            {
                if (VM.IsJawsMode) SetVisualMode(); else SetJawsMode();
                e.Handled = true;
                return;
            }
            // F6 — čitaj status projekta
            if (e.Key == System.Windows.Input.Key.F6)
            {
                VM.AnnounceProjectStatus();
                e.Handled = true;
                return;
            }
        }

        private void ShowAbout_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(Lang.T("about_text"), Lang.T("about_title"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        // ─── Odabir jezika ────────────────────────────────────────────────
        private void LangEn_Click(object sender, RoutedEventArgs e) => SwitchLanguage("en");
        private void LangSr_Click(object sender, RoutedEventArgs e) => SwitchLanguage("sr");

        private void SwitchLanguage(string code)
        {
            Lang.SetLanguage(code);
            UpdateLanguageChecks();
            // Osvezi tekstove koji se postavljaju iz koda
            CurrentModeLabel.Text = VM.IsJawsMode ? Lang.T("jaws_mode_indicator") : Lang.T("visual_mode_indicator");
            AccessibleTrackList.Rebuild();
            AccessibleTrackList.UpdateStatus();
            VM.Announce(Lang.T("language_changed"));
        }

        private void UpdateLanguageChecks()
        {
            MenuLangEn.IsChecked = Lang.Current == "en";
            MenuLangSr.IsChecked = Lang.Current == "sr";
        }
    }
}
