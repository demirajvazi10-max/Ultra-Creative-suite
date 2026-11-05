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
            Closing += (_, __) => { try { VM.Dispose(); } catch { } };
        }

        /// <summary>Naslov prozora i statusna traka sa verzijom iz csproj-a (jedno mesto za verziju).</summary>
        private void ApplyVersionTexts()
        {
            Title = $"{Lang.T("app_title")}  v{AppInfo.Version}";
            TxtVersionInfo.Text = string.Format(Lang.T("statusbar_ready"), AppInfo.Version);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplyVersionTexts();
            VM.OnShowLog = () => new LogWindow(this).Show();
            VM.OnOpenEffect = (track, type) =>
            {
                // Naslov sa nazivom trake — čitač ekrana odmah kaže na koju traku se efekat odnosi.
                var dlg = new EffectDialog($"{EffectDialog.TitleFor(type)} — {track.Name}", track.Effects, type,
                    () => VM.PlayPauseCommand.Execute(null)) { Owner = this };
                dlg.ShowDialog();
                AccessibleTrackList.Rebuild();
            };
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

                // AutomationProperties.LiveSetting="Polite" u XAML-u SAMO OZNAČAVA element
                // kao live region — ne garantuje da će JAWS/NVDA stvarno pročitati promenu.
                // Mora se eksplicitno dići UIA LiveRegionChanged event svaki put kad se
                // tekst promeni, inače promene (npr. napredak Demucs-a) prolaze nečujno.
                if (args.PropertyName == nameof(VM.StatusMessage))
                    Dispatcher.Invoke(() =>
                    {
                        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer
                            .FromElement(TxtStatusMessage)
                            ?? System.Windows.Automation.Peers.UIElementAutomationPeer
                                .CreatePeerForElement(TxtStatusMessage);
                        peer?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
                    });
            };
            // PreviewKeyDown hvata Space/S/R prije nego List kontrole progutaju event
            this.PreviewKeyDown += MainWindow_PreviewKeyDown;
            this.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            UpdateLanguageChecks();

            // Prepoznavanje čitača ekrana (isti ScreenReaderDetector kao Ultra Studio i Video
            // Editor): sa JAWS/NVDA/Narratorom program se otvara u pristupačnom modu (lista),
            // bez njega u vizuelnom modu (talasni oblik). Alt+W i dalje ručno menja mod.
            bool sr = Services.ScreenReaderDetector.IsScreenReaderRunning();
            Services.DebugLog.Write("Screen reader detected: " + sr);
            if (sr) SetJawsMode(); else SetVisualMode();
            // Najava tek kad je prozor prikazan i čitač ekrana ga je "uhvatio".
            Dispatcher.BeginInvoke(new Action(() =>
                VM.Announce(sr ? Lang.T("sr_detected") : Lang.T("sr_not_detected"))),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
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

        private async void WaveformArea_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                foreach (var file in files)
                {
                    var ext = System.IO.Path.GetExtension(file).ToLower();
                    if (ext is ".wav" or ".mp3" or ".ogg" or ".flac" or ".m4a" or ".aiff" or ".aif" or ".opus" or ".wma" or ".aac")
                    {
                        var readable = await VM.EnsureReadableAsync(file);
                        if (readable == null) continue;
                        var track = VM.AddTrackInternal(System.IO.Path.GetFileNameWithoutExtension(file));
                        var clip = VM.CreateClipFromFile(readable, 0);
                        track.Clips.Add(clip);
                        VM.NotifyEdited();
                        VM.Announce(string.Format(Lang.T("dropped_file"), clip.Name, clip.Duration));
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

            // Polja za unos teksta (API ključ, AI rezultati...) i otvoreni meniji zadržavaju
            // svoje tastere (M, P, Ctrl+C...). Samo F6/Shift+F6 rade svuda.
            var focused = Keyboard.FocusedElement;
            bool inText = focused is TextBox || focused is PasswordBox || focused is MenuItem
                          || (focused is ComboBox cb && cb.IsEditable);
            var key = e.Key == Key.System ? e.SystemKey : e.Key;   // Alt+strelice stižu kao Key.System
            if ((!inText || key == Key.F6) && VM.HandleKey(key, mods, fromList: false))
            {
                e.Handled = true;
                return;
            }

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
        }

        private void ShowAbout_Click(object sender, RoutedEventArgs e)
        {
            new AboutWindow(this).ShowDialog();
        }

        private void ShowSupport_Click(object sender, RoutedEventArgs e)
            => UltraSupport.SupportInfo.Show(this, "Ultra Audio Editor", Lang.Current == "sr");
        // ─── Odabir jezika ────────────────────────────────────────────────
        private void LangEn_Click(object sender, RoutedEventArgs e) => SwitchLanguage("en");
        private void LangSr_Click(object sender, RoutedEventArgs e) => SwitchLanguage("sr");

        private void SwitchLanguage(string code)
        {
            Lang.SetLanguage(code);
            UpdateLanguageChecks();
            ApplyVersionTexts();
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
