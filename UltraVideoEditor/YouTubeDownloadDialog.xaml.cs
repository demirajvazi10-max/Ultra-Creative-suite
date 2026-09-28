using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using Newtonsoft.Json.Linq;
using WpfMessageBox = System.Windows.MessageBox;
using WinForms = System.Windows.Forms;

namespace UltraVideoEditor
{
    /// <summary>
    /// Dijalog za preuzimanje videa ili audija sa YouTube-a putem yt-dlp.
    /// Integrates directly into the MainWindow timeline.
    /// Accessible for JAWS / NVDA screen readers.
    /// Prati jezik glavne aplikacije (prosleđen kroz konstruktor).
    ///
    /// v1.0.3 — obim preuzimanja je sada deterministički, ne zavisi od toga kako yt-dlp
    /// "tumači" link:
    ///   • "Samo ovaj video"  → skida se kanonski link https://www.youtube.com/watch?v=ID
    ///                          (bez &amp;list=...) uz --no-playlist. Plejlista fizički ne
    ///                          postoji u komandi, pa je nemoguće da se skine cela.
    ///   • "Cela plejlista"   → originalni link uz eksplicitno --yes-playlist.
    ///   • "Određene numere"  → svaka izabrana numera se skida po SVOM video ID-u (batch fajl),
    ///                          ne po poziciji u plejlisti — tačno i za "Mix" plejliste koje
    ///                          YouTube svaki put vrati drugačije.
    /// Spisak preuzetih fajlova se dobija direktno od yt-dlp-a (--print-to-file after_move),
    /// umesto nekadašnjeg "skeniraj folder za fajlove iz poslednjih 5 minuta" koji je na
    /// timeline dodavao i fajlove iz prethodnih preuzimanja (npr. celu raniju plejlistu).
    /// </summary>
    public partial class YouTubeDownloadDialog : Window
    {
        // ─── Javni rezultat ───────────────────────────────────────────────────
        /// <summary>Lista putanja preuzetih fajlova, gotova za dodavanje na timeline.</summary>
        public List<string> DownloadedFiles { get; private set; } = new();

        // ─── Interne promenljive ──────────────────────────────────────────────
        private enum Scope { Single, All, Choose }

        private CancellationTokenSource _cts;        // preuzimanje
        private CancellationTokenSource _fetchCts;   // učitavanje informacija
        private bool _downloadInProgress = false;
        private bool _fetchingInfo = false;
        private bool _bulkSelection = false;
        private readonly string _language;

        private static readonly string AppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraVideoEditor");

        // Naša (ažurirljiva) kopija yt-dlp-a — ide PRVA, da se uvek koristi verzija koju
        // aplikacija sama održava ažurnom (YouTube često menja sajt, stari yt-dlp prestane da radi).
        private static readonly string ManagedYtdlpExe = Path.Combine(AppDataDir, "yt-dlp.exe");

        private static readonly string[] YtdlpSearchPaths = {
            ManagedYtdlpExe,
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yt-dlp.exe"),
        };

        // Deno: JavaScript runtime koji novije verzije yt-dlp-a koriste za YouTube.
        // Preuzima se samo ako preuzimanje pukne upravo zbog toga (pitamo korisnika).
        private static readonly string ManagedDenoDir = Path.Combine(AppDataDir, "deno");
        private static readonly string ManagedDenoExe = Path.Combine(ManagedDenoDir, "deno.exe");

        // FFmpeg koji je već upakovan uz Ultra Video Editor.
        private static readonly string BundledFfmpegExe = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");

        private static readonly string DownloadFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "UltraVideoEditor_Downloads");

        private static readonly string[] MediaExtensions =
            { ".mp4", ".mkv", ".webm", ".mov", ".mp3", ".m4a", ".opus", ".ogg", ".wav", ".flac", ".aac" };

        private string _currentSaveFolder = DownloadFolder;
        private VideoInfo _lastInfo;
        private string _lastFetchedUrl;
        private string _lastVideoId;

        // Stanje najava tokom preuzimanja
        private int _itemIndex, _itemTotal;
        private string _lastAnnouncedFile;
        private int _lastMilestone;
        private readonly List<string> _errorLines = new();
        private bool _needsJsRuntime;
        private Scope _downloadScope;

        // ─── Konstruktor ──────────────────────────────────────────────────────
        public YouTubeDownloadDialog(string language = "en")
        {
            InitializeComponent();
            UiScaling.Register(this);
            _language = string.IsNullOrEmpty(language) ? "en" : language;

            try { Directory.CreateDirectory(DownloadFolder); } catch { }
            txtSaveFolder.Text = _currentSaveFolder;

            ApplyLanguage();
            Loaded += (_, _) => txtUrl.Focus();
            Closing += YouTubeDownloadDialog_Closing;
        }

        private string L(string key) => LanguageManager.GetText(key, _language);

        private void ApplyLanguage()
        {
            Title = L("yt_dialog_title");
            AutomationProperties.SetName(this, L("yt_dialog_aria"));
            txbDialogTitle.Text = L("yt_dialog_title");

            lblUrl.Content = L("yt_url_label");
            AutomationProperties.SetName(txtUrl, L("yt_url_aria"));
            AutomationProperties.SetHelpText(txtUrl, L("yt_url_help"));

            grpFileType.Header = L("yt_filetype_group");
            AutomationProperties.SetName(grpFileType, L("yt_filetype_aria"));
            rbVideo.Content = L("yt_video_option");
            AutomationProperties.SetName(rbVideo, L("yt_video_aria"));
            rbAudio.Content = L("yt_audio_option");
            AutomationProperties.SetName(rbAudio, L("yt_audio_aria"));

            lblQuality.Content = L("yt_quality_label");
            AutomationProperties.SetName(cmbQuality, L("yt_quality_aria"));
            cmbQBest.Content = L("yt_q_best");
            cmbQ1080.Content = L("yt_q_1080");
            cmbQ720.Content = L("yt_q_720");
            cmbQ480.Content = L("yt_q_480");
            cmbQ360.Content = L("yt_q_360");

            lblAudioQuality.Content = L("yt_audioq_label");
            AutomationProperties.SetName(cmbAudioQuality, L("yt_audioq_aria"));
            cmbAQBest.Content = L("yt_aq_best");
            cmbAQ320.Content = L("yt_aq_320");
            cmbAQ192.Content = L("yt_aq_192");
            cmbAQ128.Content = L("yt_aq_128");

            lblSaveFolder.Content = L("yt_savefolder_label");
            AutomationProperties.SetName(txtSaveFolder, L("yt_savefolder_aria"));
            btnBrowseFolder.Content = L("yt_browse_btn");
            AutomationProperties.SetName(btnBrowseFolder, L("yt_browse_aria"));

            chkAddToTimeline.Content = L("yt_addtotimeline");
            AutomationProperties.SetName(chkAddToTimeline, L("yt_addtotimeline_aria"));

            txbPlaylistPrompt.Text = L("yt_playlist_prompt");
            grpScope.Header = L("yt_scope_group");
            AutomationProperties.SetName(grpScope, L("yt_scope_group_aria"));
            rbScopeSingle.Content = L("yt_scope_single");
            AutomationProperties.SetName(rbScopeSingle, L("yt_scope_single_aria"));
            rbScopeAll.Content = L("yt_scope_all");
            AutomationProperties.SetName(rbScopeAll, L("yt_scope_all_aria"));
            rbScopeChoose.Content = L("yt_scope_choose");
            AutomationProperties.SetName(rbScopeChoose, L("yt_scope_choose_aria"));

            txbTracksHint.Text = L("yt_tracks_hint");
            AutomationProperties.SetName(lstPlaylistTracks, L("yt_tracks_aria"));
            AutomationProperties.SetHelpText(lstPlaylistTracks, L("yt_tracks_hint"));
            btnSelectAllTracks.Content = L("yt_select_all");
            btnClearTracks.Content = L("yt_clear_selection");

            AutomationProperties.SetName(prgDownload, L("yt_progress_aria"));
            AutomationProperties.SetName(txbStatus, L("yt_status_aria"));

            btnFetchInfo.Content = _fetchingInfo ? L("yt_btn_loadinfo_loading") : L("yt_btn_loadinfo");
            AutomationProperties.SetName(btnFetchInfo, L("yt_btn_loadinfo_aria"));
            btnDownload.Content = L("yt_btn_download");
            AutomationProperties.SetName(btnDownload, L("yt_btn_download_aria"));
            btnCancel.Content = _downloadInProgress ? L("yt_btn_cancel_downloading") : L("yt_btn_cancel");
            AutomationProperties.SetName(btnCancel, L("yt_btn_cancel_aria"));
        }

        // ═════════════════════════════════════════════════════════════════════
        //  UI EVENT HANDLERS
        // ═════════════════════════════════════════════════════════════════════

        private void TxtUrl_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                BtnFetchInfo_Click(sender, e);
            }
        }

        /// <summary>
        /// Ako se link promeni posle "Učitaj info", stare informacije više ne važe —
        /// ranije je "Preuzmi" ostajalo aktivno i koristilo obim/plejlistu STAROG linka.
        /// </summary>
        private void TxtUrl_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_lastInfo == null || _fetchingInfo || _downloadInProgress) return;
            if (string.Equals(txtUrl.Text.Trim(), _lastFetchedUrl, StringComparison.Ordinal)) return;

            ResetInfo();
            Announce(L("yt_url_changed"));
        }

        private void ResetInfo()
        {
            _lastInfo = null;
            _lastFetchedUrl = null;
            _lastVideoId = null;
            pnlInfo.Visibility = Visibility.Collapsed;
            pnlPlaylistTracks.Visibility = Visibility.Collapsed;
            pnlTrackPicker.Visibility = Visibility.Collapsed;
            lstPlaylistTracks.ItemsSource = null;
            btnDownload.IsEnabled = false;
        }

        private void BtnBrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new WinForms.FolderBrowserDialog
            {
                Description = L("yt_browse_desc"),
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(_currentSaveFolder) ? _currentSaveFolder : DownloadFolder
            };

            if (dlg.ShowDialog() == WinForms.DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
            {
                _currentSaveFolder = dlg.SelectedPath;
                txtSaveFolder.Text = _currentSaveFolder;
                Announce(string.Format(L("yt_folder_set"), _currentSaveFolder));
            }
        }

        private void FormatType_Changed(object sender, RoutedEventArgs e)
        {
            if (pnlQuality == null || pnlAudioQuality == null) return;
            bool isVideo = rbVideo.IsChecked == true;
            pnlQuality.Visibility      = isVideo ? Visibility.Visible : Visibility.Collapsed;
            pnlAudioQuality.Visibility = isVideo ? Visibility.Collapsed : Visibility.Visible;
        }

        private Scope CurrentScope()
        {
            if (_lastInfo == null || !_lastInfo.IsPlaylist) return Scope.Single;
            if (rbScopeChoose.IsChecked == true) return Scope.Choose;
            if (rbScopeSingle.IsChecked == true && !string.IsNullOrEmpty(_lastVideoId)) return Scope.Single;
            return Scope.All;
        }

        private void PlaylistScope_Changed(object sender, RoutedEventArgs e)
        {
            if (pnlTrackPicker == null || _lastInfo == null) return;

            var scope = CurrentScope();
            pnlTrackPicker.Visibility = scope == Scope.Choose ? Visibility.Visible : Visibility.Collapsed;

            switch (scope)
            {
                case Scope.Single:
                    Announce(L("yt_scope_single_chosen"));
                    break;
                case Scope.All:
                    Announce(string.Format(L("yt_scope_all_chosen"), _lastInfo.Entries.Count));
                    break;
                case Scope.Choose:
                    // Ako link pokazuje na konkretan video, a ništa još nije izabrano,
                    // odmah označimo baš taj video — najčešće je to ono što korisnik i želi.
                    var entries = _lastInfo.Entries;
                    if (!entries.Any(x => x.IsSelected) && !string.IsNullOrEmpty(_lastVideoId))
                    {
                        var current = entries.FirstOrDefault(x => x.Id == _lastVideoId);
                        if (current != null)
                        {
                            _bulkSelection = true;
                            current.IsSelected = true;
                            _bulkSelection = false;
                            lstPlaylistTracks.ScrollIntoView(current);
                            UpdateTrackCount();
                            Announce(string.Format(L("yt_preselected_current"), current.Title) + " " + L("yt_tracks_hint"));
                            break;
                        }
                    }
                    UpdateTrackCount();
                    Announce(L("yt_choose_tracks_hint") + " " + L("yt_tracks_hint"));
                    break;
            }
        }

        private void LstPlaylistTracks_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_bulkSelection || _lastInfo == null) return;
            UpdateTrackCount();

            int selected = _lastInfo.Entries.Count(x => x.IsSelected);
            int total = _lastInfo.Entries.Count;

            if (e.AddedItems.Count == 1 && e.AddedItems[0] is PlaylistEntry added)
                Announce(string.Format(L("yt_track_selected"), added.Title, selected, total));
            else if (e.RemovedItems.Count == 1 && e.RemovedItems[0] is PlaylistEntry removed)
                Announce(string.Format(L("yt_track_deselected"), removed.Title, selected, total));
        }

        private void BtnSelectAllTracks_Click(object sender, RoutedEventArgs e)
        {
            if (_lastInfo == null) return;
            _bulkSelection = true;
            foreach (var entry in _lastInfo.Entries) entry.IsSelected = true;
            _bulkSelection = false;
            UpdateTrackCount();
            Announce(string.Format(L("yt_all_selected"), _lastInfo.Entries.Count));
        }

        private void BtnClearTracks_Click(object sender, RoutedEventArgs e)
        {
            if (_lastInfo == null) return;
            _bulkSelection = true;
            foreach (var entry in _lastInfo.Entries) entry.IsSelected = false;
            _bulkSelection = false;
            UpdateTrackCount();
            Announce(L("yt_selection_cleared"));
        }

        private void UpdateTrackCount()
        {
            if (_lastInfo == null) { txbTracksCount.Text = ""; return; }
            txbTracksCount.Text = string.Format(L("yt_tracks_count"),
                _lastInfo.Entries.Count(x => x.IsSelected), _lastInfo.Entries.Count);
        }

        private async void BtnFetchInfo_Click(object sender, RoutedEventArgs e)
        {
            if (_fetchingInfo || _downloadInProgress) return;

            string url = txtUrl.Text.Trim();
            if (string.IsNullOrEmpty(url))
            {
                Announce(L("yt_err_url_empty"));
                txtUrl.Focus();
                return;
            }

            ResetInfo();
            SetFetchingState(true);
            _fetchCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

            try
            {
                string ytdlp = FindYtdlp();
                if (ytdlp == null)
                {
                    bool installed = await OfferYtdlpInstallAsync();
                    if (!installed) return;
                    ytdlp = FindYtdlp();
                    if (ytdlp == null) { ShowError(L("yt_err_ytdlp_not_found_after_install")); return; }
                }
                else
                {
                    await MaybeUpdateYtdlpAsync(ytdlp);
                }

                Announce(L("yt_loading_info"));
                var info = await FetchInfoAsync(ytdlp, url, _fetchCts.Token);
                if (info == null) return;

                _lastInfo = info;
                _lastFetchedUrl = url;
                _lastVideoId = ExtractVideoId(url);

                txbInfoTitle.Text = info.IsPlaylist
                    ? string.Format(L("yt_info_playlist_title"), info.Title, info.Entries.Count)
                    : string.Format(L("yt_info_video_title"), info.Title);

                txbInfoMeta.Text = info.IsPlaylist
                    ? string.Format(L("yt_info_meta_playlist"), info.Channel)
                    : string.Format(L("yt_info_meta_video"), info.Channel, FormatDuration(info.DurationSec));

                bool hasSpecificVideo = !string.IsNullOrEmpty(_lastVideoId);

                if (info.IsPlaylist)
                {
                    rbScopeSingle.Visibility = hasSpecificVideo ? Visibility.Visible : Visibility.Collapsed;
                    rbScopeChoose.Visibility = info.Entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

                    lstPlaylistTracks.ItemsSource = info.Entries;
                    pnlTrackPicker.Visibility = Visibility.Collapsed;
                    UpdateTrackCount();

                    // Bezbedan podrazumevani izbor: ako link ima konkretan video (v=...),
                    // podrazumevano je SAMO taj video. Postavljamo IsChecked pre nego što se
                    // panel prikaže, pa PlaylistScope_Changed ne najavljuje dvaput.
                    var tmp = _lastInfo; _lastInfo = null;
                    rbScopeSingle.IsChecked = false;
                    rbScopeAll.IsChecked = false;
                    rbScopeChoose.IsChecked = false;
                    if (hasSpecificVideo) rbScopeSingle.IsChecked = true;
                    else rbScopeAll.IsChecked = true;
                    _lastInfo = tmp;

                    pnlPlaylistTracks.Visibility = Visibility.Visible;
                }
                else
                {
                    lstPlaylistTracks.ItemsSource = null;
                    pnlPlaylistTracks.Visibility = Visibility.Collapsed;
                }

                pnlInfo.Visibility = Visibility.Visible;
                btnDownload.IsEnabled = true;

                string announcement;
                if (info.IsPlaylist && hasSpecificVideo)
                    announcement = string.Format(L("yt_playlist_found_specific"), info.Title, info.Entries.Count);
                else if (info.IsPlaylist)
                    announcement = string.Format(L("yt_playlist_found_generic"), info.Title, info.Entries.Count);
                else
                    announcement = string.Format(L("yt_video_found"), info.Title, FormatDuration(info.DurationSec));

                Announce(announcement);

                // Kod plejliste fokus ide na izbor obima (da korisnik odmah čuje i vidi izbor),
                // inače direktno na "Preuzmi".
                if (info.IsPlaylist)
                    (hasSpecificVideo ? rbScopeSingle : rbScopeAll).Focus();
                else
                    btnDownload.Focus();
            }
            catch (OperationCanceledException)
            {
                bool timedOut = _fetchCts?.IsCancellationRequested == true && !_userCancelledFetch;
                Announce(timedOut ? L("yt_err_fetch_timeout") : L("yt_fetch_cancelled"));
                if (timedOut) ShowError(L("yt_err_fetch_timeout"));
            }
            catch (Exception ex)
            {
                ShowError(string.Format(L("yt_err_prefix"), ex.Message));
            }
            finally
            {
                _userCancelledFetch = false;
                SetFetchingState(false);
            }
        }

        private bool _userCancelledFetch;

        private async void BtnDownload_Click(object sender, RoutedEventArgs e)
        {
            if (_downloadInProgress || _fetchingInfo) return;

            string url = txtUrl.Text.Trim();
            if (string.IsNullOrEmpty(url)) return;

            if (_lastInfo == null || !string.Equals(url, _lastFetchedUrl, StringComparison.Ordinal))
            {
                ShowError(L("yt_err_load_info_first"));
                btnFetchInfo.Focus();
                return;
            }

            string ytdlp = FindYtdlp();
            if (ytdlp == null) { ShowError(L("yt_err_ytdlp_not_found")); return; }

            bool isAudio = rbAudio.IsChecked == true;

            if (!File.Exists(BundledFfmpegExe))
            {
                ShowError(L("yt_err_ffmpeg_missing"));
                return;
            }

            try { Directory.CreateDirectory(_currentSaveFolder); }
            catch (Exception ex) { ShowError(string.Format(L("yt_err_prefix"), ex.Message)); return; }

            string quality = ((ComboBoxItem)cmbQuality.SelectedItem)?.Tag?.ToString() ?? "best";
            string audioQuality = ((ComboBoxItem)cmbAudioQuality.SelectedItem)?.Tag?.ToString() ?? "192K";

            var scope = CurrentScope();
            List<PlaylistEntry> chosen = null;

            if (scope == Scope.Choose)
            {
                // Izbor se čita iz samih podataka (PlaylistEntry.IsSelected), ne iz
                // ListBox.SelectedItems — tačno i za stavke koje nikad nisu bile prikazane.
                chosen = _lastInfo.Entries.Where(x => x.IsSelected).OrderBy(x => x.Index).ToList();
                if (chosen.Count == 0)
                {
                    string msg = L("yt_err_select_track") +
                        (rbScopeSingle.Visibility == Visibility.Visible ? L("yt_err_select_track_or_single") : L("yt_err_select_track_period"));
                    ShowError(msg);
                    lstPlaylistTracks.Focus();
                    return;
                }
            }

            string scopeInfo = scope switch
            {
                Scope.Choose => string.Format(L("yt_scope_debug_choose"),
                                    string.Join(", ", chosen.Select(x => x.Index))),
                Scope.All    => L("yt_scope_debug_all"),
                _            => _lastInfo.IsPlaylist ? L("yt_scope_debug_single") : L("yt_scope_debug_notplaylist"),
            };

            _cts = new CancellationTokenSource();
            SetDownloadState(true);
            Announce(scopeInfo);

            try
            {
                var files = await RunDownloadAsync(ytdlp, url, isAudio, quality, audioQuality, scope, chosen, _cts.Token);

                // Novija verzija yt-dlp-a traži JavaScript runtime (Deno) za YouTube.
                // Ako je preuzimanje palo baš zbog toga, ponudimo da ga preuzmemo i pokušamo ponovo.
                if (files.Count == 0 && _needsJsRuntime && !File.Exists(ManagedDenoExe))
                {
                    if (await OfferDenoInstallAsync(_cts.Token))
                    {
                        Announce(L("yt_retrying"));
                        files = await RunDownloadAsync(ytdlp, url, isAudio, quality, audioQuality, scope, chosen, _cts.Token);
                    }
                }

                if (files.Count > 0)
                {
                    string savedMsg = files.Count == 1
                        ? string.Format(L("yt_saved_to"), files[0])
                        : string.Format(L("yt_saved_multi"), files.Count, _currentSaveFolder);

                    if (_errorLines.Count > 0)
                        savedMsg += "\n\n" + string.Format(L("yt_partial_success"), _errorLines[0]);

                    if (chkAddToTimeline.IsChecked == true)
                    {
                        DownloadedFiles = files;
                        Announce(string.Format(L("yt_added_to_timeline"), savedMsg));
                    }
                    else
                    {
                        DownloadedFiles = new List<string>();
                        Announce(string.Format(L("yt_not_added_to_timeline"), savedMsg));
                    }

                    WpfMessageBox.Show(this, savedMsg, L("yt_download_complete_title"), MessageBoxButton.OK,
                        _errorLines.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
                    _downloadInProgress = false;
                    DialogResult = true;
                    Close();
                }
                else if (_errorLines.Count > 0)
                {
                    ShowError(string.Format(L("yt_err_exit"), string.Join("\n", _errorLines.Take(3))));
                }
                else
                {
                    ShowError(L("yt_err_download_no_files"));
                }
            }
            catch (OperationCanceledException)
            {
                Announce(L("yt_download_cancelled"));
                SetProgress(0, L("yt_cancelled"));
            }
            catch (Exception ex)
            {
                ShowError(string.Format(L("yt_err_during_download"), ex.Message));
            }
            finally
            {
                if (IsLoaded) SetDownloadState(false);
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (_downloadInProgress)
            {
                _cts?.Cancel();
                return;
            }
            if (_fetchingInfo)
            {
                _userCancelledFetch = true;
                _fetchCts?.Cancel();
                return;
            }
            DialogResult = false;
            Close();
        }

        /// <summary>
        /// Zatvaranje prozora (X, Alt+F4, Escape) tokom preuzimanja ranije je ostavljalo
        /// yt-dlp da radi u pozadini i dalje skida (moguće celu plejlistu). Sada ga gasimo.
        /// </summary>
        private void YouTubeDownloadDialog_Closing(object sender, CancelEventArgs e)
        {
            try { _cts?.Cancel(); } catch { }
            try { _userCancelledFetch = true; _fetchCts?.Cancel(); } catch { }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  YT-DLP LOKACIJA / INSTALACIJA / AŽURIRANJE
        // ═════════════════════════════════════════════════════════════════════

        private string FindYtdlp()
        {
            foreach (var path in YtdlpSearchPaths)
            {
                if (File.Exists(path)) return path;
            }

            // Also try via PATH (npr. instaliran preko winget-a)
            try
            {
                using var p = new Process
                {
                    StartInfo = new ProcessStartInfo("yt-dlp", "--version")
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                p.Start();
                p.DrainStdout();
                p.DrainStderr();
                if (!p.WaitForExit(5000)) { try { p.Kill(true); } catch { } return null; }
                if (p.ExitCode == 0) return "yt-dlp";
            }
            catch { }

            return null;
        }

        /// <summary>
        /// YouTube redovno menja sajt, pa stari yt-dlp posle nekog vremena prestane da radi.
        /// Našu kopiju (u AppData) jednom nedeljno ažuriramo sa "yt-dlp -U". Greške se ignorišu —
        /// ažuriranje nikad ne sme da spreči samo preuzimanje.
        /// </summary>
        private async Task MaybeUpdateYtdlpAsync(string ytdlp)
        {
            if (!string.Equals(ytdlp, ManagedYtdlpExe, StringComparison.OrdinalIgnoreCase)) return;

            string marker = Path.Combine(AppDataDir, "yt-dlp.lastupdate");
            try
            {
                if (File.Exists(marker) && DateTime.Now - File.GetLastWriteTime(marker) < TimeSpan.FromDays(7))
                    return;

                Announce(L("yt_ytdlp_updating"));
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                await RunProcessAsync(ytdlp, new[] { "-U" }, null, cts.Token);
                File.WriteAllText(marker, DateTime.Now.ToString("o"));
            }
            catch { /* bez interneta ili bez dozvole — nastavljamo sa postojećom verzijom */ }
        }

        private async Task<bool> OfferYtdlpInstallAsync()
        {
            var result = WpfMessageBox.Show(this,
                L("yt_ytdlp_missing_msg"),
                L("yt_ytdlp_missing_title"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return false;

            pnlProgress.Visibility = Visibility.Visible;
            Announce(L("yt_ytdlp_downloading"));

            try
            {
                Directory.CreateDirectory(AppDataDir);
                await DownloadFileAsync("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe",
                    ManagedYtdlpExe, "yt-dlp", CancellationToken.None);
                File.WriteAllText(Path.Combine(AppDataDir, "yt-dlp.lastupdate"), DateTime.Now.ToString("o"));

                Announce(L("yt_ytdlp_downloaded"));
                SetProgress(100, L("yt_ytdlp_ready"));
                return true;
            }
            catch (Exception ex)
            {
                ShowError(string.Format(L("yt_ytdlp_install_fail"), ex.Message));
                return false;
            }
        }

        private async Task<bool> OfferDenoInstallAsync(CancellationToken ct)
        {
            var result = WpfMessageBox.Show(this,
                L("yt_deno_offer_msg"),
                L("yt_deno_offer_title"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return false;

            string zipPath = Path.Combine(Path.GetTempPath(), $"deno_{Guid.NewGuid():N}.zip");
            try
            {
                Announce(L("yt_deno_downloading"));
                Directory.CreateDirectory(ManagedDenoDir);
                await DownloadFileAsync(
                    "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip",
                    zipPath, "Deno", ct);

                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    var exe = zip.Entries.FirstOrDefault(x => x.Name.Equals("deno.exe", StringComparison.OrdinalIgnoreCase));
                    if (exe == null) throw new InvalidDataException("deno.exe not found in the archive.");
                    exe.ExtractToFile(ManagedDenoExe, overwrite: true);
                }

                Announce(L("yt_deno_ready"));
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ShowError(string.Format(L("yt_deno_fail"), ex.Message));
                return false;
            }
            finally
            {
                try { File.Delete(zipPath); } catch { }
            }
        }

        /// <summary>Preuzima fajl sa najavom napretka na svakih 25%.</summary>
        private async Task DownloadFileAsync(string url, string dest, string label, CancellationToken ct)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            http.DefaultRequestHeaders.Add("User-Agent", "UltraVideoEditor/1.0");

            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long total = response.Content.Headers.ContentLength ?? -1;
            string tmp = dest + ".download";

            await using (var stream = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = File.Create(tmp))
            {
                var buffer = new byte[65536];
                long downloaded = 0;
                int read, lastQuarter = 0;

                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    downloaded += read;
                    if (total > 0)
                    {
                        double pct = downloaded * 100.0 / total;
                        SetProgress(pct, $"{label}: {pct:F0}%  ({downloaded / 1024.0 / 1024.0:F1} MB)");
                        int quarter = (int)(pct / 25);
                        if (quarter > lastQuarter && quarter < 4)
                        {
                            lastQuarter = quarter;
                            Announce($"{label}: {quarter * 25}%");
                        }
                    }
                }
            }

            File.Move(tmp, dest, overwrite: true);
        }

        // ─── Info ─────────────────────────────────────────────────────────────

        private class VideoInfo
        {
            public string Title   { get; set; } = "";
            public string Channel { get; set; } = "";
            public double DurationSec { get; set; }
            public bool   IsPlaylist  { get; set; }
            public List<PlaylistEntry> Entries { get; set; } = new();
        }

        /// <summary>
        /// Jedna numera iz plejliste. IsSelected je izvor istine za izbor (vezan dvosmerno na
        /// ListBoxItem.IsSelected), a Id služi da se numera skine direktno po svom video ID-u.
        /// </summary>
        public sealed class PlaylistEntry : INotifyPropertyChanged
        {
            public int Index { get; set; }
            public string Id { get; set; }
            public string Url { get; set; }
            public string Title { get; set; }
            public string DisplayName => $"{Index}. {Title}";

            private bool _isSelected;
            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected == value) return;
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
            public override string ToString() => DisplayName;
        }

        /// <summary>
        /// Izvlači YouTube video ID iz linka (watch?v=, youtu.be/, shorts/, live/, embed/).
        /// Vraća null ako link ne pokazuje na konkretan video (npr. čista plejlista).
        /// </summary>
        private static string ExtractVideoId(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (!Regex.IsMatch(url, @"(youtube\.com|youtu\.be|youtube-nocookie\.com)", RegexOptions.IgnoreCase))
                return null;

            var m = Regex.Match(url, @"[?&]v=([A-Za-z0-9_-]{11})");
            if (m.Success) return m.Groups[1].Value;

            m = Regex.Match(url, @"youtu\.be/([A-Za-z0-9_-]{11})", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;

            m = Regex.Match(url, @"/(?:shorts|live|embed)/([A-Za-z0-9_-]{11})", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;

            return null;
        }

        private static string WatchUrl(string id) => $"https://www.youtube.com/watch?v={id}";

        private async Task<VideoInfo> FetchInfoAsync(string ytdlp, string url, CancellationToken ct)
        {
            // --yes-playlist eksplicitno: da prepoznavanje plejliste ne zavisi od eventualnog
            // yt-dlp config fajla na korisnikovom računaru.
            var args = new[] { "--dump-single-json", "--flat-playlist", "--yes-playlist", "--no-warnings", url };

            var (output, _, _) = await RunProcessAsync(ytdlp, args, null, ct);
            if (string.IsNullOrWhiteSpace(output))
            {
                ShowError(L("yt_err_no_response"));
                return null;
            }

            JObject root;
            try { root = JObject.Parse(output); }
            catch (Exception)
            {
                ShowError(L("yt_err_no_response"));
                return null;
            }

            var info = new VideoInfo();
            info.Title = root["title"]?.Value<string>() ?? "";
            info.Channel = root["uploader"]?.Value<string>();
            if (string.IsNullOrEmpty(info.Channel))
                info.Channel = root["channel"]?.Value<string>() ?? "";

            string dtype = root["_type"]?.Value<string>();
            if (dtype == "playlist")
            {
                info.IsPlaylist = true;
                var entries = root["entries"] as JArray ?? new JArray();

                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    string id = entry?["id"]?.Value<string>();
                    string entryUrl = entry?["url"]?.Value<string>();
                    string title = entry?["title"]?.Value<string>();
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        title = !string.IsNullOrEmpty(id)
                            ? string.Format(L("yt_track_unavailable"), id)
                            : L("yt_track_unavailable_generic");
                    }

                    info.Entries.Add(new PlaylistEntry
                    {
                        Index = i + 1,
                        Id = id,
                        Url = !string.IsNullOrEmpty(entryUrl) && entryUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                              ? entryUrl
                              : (!string.IsNullOrEmpty(id) ? WatchUrl(id) : null),
                        Title = title
                    });
                }
            }
            else
            {
                double? dur = root["duration"]?.Value<double?>();
                info.DurationSec = dur ?? 0;
            }

            return info;
        }

        // ─── Download ─────────────────────────────────────────────────────────

        private async Task<List<string>> RunDownloadAsync(
            string ytdlp, string url, bool audio, string quality, string audioQuality,
            Scope scope, List<PlaylistEntry> chosen, CancellationToken ct)
        {
            _itemIndex = 0;
            _itemTotal = scope == Scope.Choose ? chosen.Count : (scope == Scope.All ? _lastInfo.Entries.Count : 1);
            _lastAnnouncedFile = null;
            _lastMilestone = 0;
            _errorLines.Clear();
            _needsJsRuntime = false;
            _downloadScope = scope;

            var args = new List<string>();

            if (audio)
            {
                args.AddRange(new[] { "--format", "bestaudio/best" });
                args.AddRange(new[] { "--extract-audio", "--audio-format", "mp3", "--audio-quality", audioQuality });
            }
            else
            {
                string fmt = quality == "best"
                    ? "bestvideo[ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best"
                    : $"bestvideo[height<={quality}][ext=mp4]+bestaudio[ext=m4a]/best[height<={quality}][ext=mp4]/best[height<={quality}]/best";
                args.AddRange(new[] { "--format", fmt, "--merge-output-format", "mp4" });
            }

            args.AddRange(new[] { "--ffmpeg-location", Path.GetDirectoryName(BundledFfmpegExe) });

            // Tačne putanje gotovih fajlova (posle spajanja/konverzije) yt-dlp upisuje u ovaj
            // fajl (UTF-8) — radi i kad fajl već postoji ("has already been downloaded").
            string listFile = Path.Combine(Path.GetTempPath(), $"uve_yt_{Guid.NewGuid():N}.txt");
            args.AddRange(new[] { "--print-to-file", "after_move:filepath", listFile });

            string batchFile = null;
            switch (scope)
            {
                case Scope.Single:
                {
                    // Kanonski link BEZ "&list=..." — plejlista uopšte ne ulazi u komandu.
                    string id = _lastVideoId ?? ExtractVideoId(url);
                    args.Add("--no-playlist");
                    args.AddRange(new[] { "--output", Path.Combine(_currentSaveFolder, "%(title).150B.%(ext)s") });
                    args.Add("--newline");
                    args.Add("--");
                    args.Add(!string.IsNullOrEmpty(id) ? WatchUrl(id) : url);
                    break;
                }
                case Scope.All:
                    args.Add("--yes-playlist");
                    args.Add("--ignore-errors"); // privatni/obrisani video ne sme da prekine celu plejlistu
                    args.AddRange(new[] { "--output", Path.Combine(_currentSaveFolder, "%(playlist_index)03d - %(title).150B.%(ext)s") });
                    args.Add("--newline");
                    args.Add("--");
                    args.Add(url);
                    break;

                case Scope.Choose:
                {
                    // Svaka izabrana numera po svom ID-u, jedna po liniji u batch fajlu.
                    batchFile = Path.Combine(Path.GetTempPath(), $"uve_yt_batch_{Guid.NewGuid():N}.txt");
                    var urls = chosen.Select(x => x.Url ?? (x.Id != null ? WatchUrl(x.Id) : null))
                                     .Where(x => !string.IsNullOrEmpty(x)).ToList();
                    File.WriteAllLines(batchFile, urls, new UTF8Encoding(false));
                    args.Add("--no-playlist");
                    args.Add("--ignore-errors");
                    args.AddRange(new[] { "--output", Path.Combine(_currentSaveFolder, "%(title).150B.%(ext)s") });
                    args.Add("--newline");
                    args.AddRange(new[] { "--batch-file", batchFile });
                    break;
                }
            }

            var startedAt = DateTime.Now.AddSeconds(-2);
            var downloadedPaths = new List<string>();

            try
            {
                var (_, _, exitCode) = await RunProcessAsync(ytdlp, args, ParseProgressLine, ct);

                if (File.Exists(listFile))
                {
                    foreach (var line in File.ReadAllLines(listFile, Encoding.UTF8))
                    {
                        string path = line.Trim();
                        if (path.Length == 0 || !File.Exists(path)) continue;
                        if (!downloadedPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                            downloadedPaths.Add(path);
                    }
                }

                // Rezerva samo ako yt-dlp nije ništa upisao (vrlo stara verzija bez --print-to-file):
                // samo medijski fajlovi nastali TOKOM ovog preuzimanja.
                if (downloadedPaths.Count == 0 && exitCode == 0)
                {
                    downloadedPaths.AddRange(Directory.GetFiles(_currentSaveFolder)
                        .Where(f => MediaExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                        .Where(f => File.GetLastWriteTime(f) >= startedAt || File.GetCreationTime(f) >= startedAt)
                        .OrderBy(f => File.GetCreationTime(f)));
                }
            }
            finally
            {
                try { File.Delete(listFile); } catch { }
                if (batchFile != null) { try { File.Delete(batchFile); } catch { } }
            }

            return downloadedPaths;
        }

        private void ParseProgressLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            // Greške — skupljamo ih da bismo korisniku rekli ZAŠTO nešto nije uspelo.
            if (line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                string msg = line.Trim();
                lock (_errorLines) { if (_errorLines.Count < 20) _errorLines.Add(msg); }
                if (IsJsRuntimeProblem(line)) _needsJsRuntime = true;
                Dispatcher.InvokeAsync(() => Announce(string.Format(L("yt_ytdlp_error"), msg)));
                return;
            }
            if (line.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase))
            {
                if (IsJsRuntimeProblem(line)) _needsJsRuntime = true;
                return;
            }

            // Cela plejlista: "[download] Downloading item 3 of 12"
            var itemMatch = Regex.Match(line, @"\[download\] Downloading (?:item|video) (\d+) of (\d+)");
            if (itemMatch.Success)
            {
                _itemIndex = int.Parse(itemMatch.Groups[1].Value);
                _itemTotal = int.Parse(itemMatch.Groups[2].Value);
                _lastMilestone = 0;
                return;
            }

            // Izabrane numere (batch fajl): svaka nova numera počinje sa "Extracting URL".
            if (Regex.IsMatch(line, @"^\[[\w:]+\] Extracting URL:") && _downloadScope == Scope.Choose)
            {
                _itemIndex++;
                _lastMilestone = 0;
                return;
            }

            var pctMatch = Regex.Match(line, @"\[download\]\s+([\d.]+)%");
            if (pctMatch.Success &&
                double.TryParse(pctMatch.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double pct))
            {
                string speed = "";
                var speedMatch = Regex.Match(line, @"at\s+([\d.]+\s*\w+/s)");
                if (speedMatch.Success) speed = speedMatch.Groups[1].Value;

                string eta = "";
                var etaMatch = Regex.Match(line, @"ETA\s+([\d:]+)");
                if (etaMatch.Success) eta = etaMatch.Groups[1].Value;

                string label = $"{pct:F1}%";
                if (_itemTotal > 1 && _itemIndex > 0) label = string.Format(L("yt_item_progress"), _itemIndex, _itemTotal) + "  •  " + label;
                if (!string.IsNullOrEmpty(speed)) label += $"  •  {speed}";
                if (!string.IsNullOrEmpty(eta))   label += $"  •  ETA {eta}";

                // Najava samo na 25/50/75% (jednom po numeri) — ne zatrpava screen reader.
                int milestone = (int)(pct / 25) * 25;
                bool announce = milestone > _lastMilestone && milestone > 0 && milestone < 100;
                if (announce) _lastMilestone = milestone;

                Dispatcher.InvokeAsync(() =>
                {
                    SetProgress(pct, label);
                    if (announce) Announce($"{milestone}%");
                });
                return;
            }

            // [download] Destination: path\to\file.f137.mp4  (jednom po numeri, ne po streamu)
            var destMatch = Regex.Match(line, @"\[download\] Destination:\s+(.+)");
            if (destMatch.Success)
            {
                string name = Path.GetFileNameWithoutExtension(destMatch.Groups[1].Value.Trim());
                name = Regex.Replace(name, @"\.f[\w-]+$", "");
                if (name == _lastAnnouncedFile) return;
                _lastAnnouncedFile = name;

                string msg = string.Format(L("yt_downloading_file"), name);
                if (_itemTotal > 1 && _itemIndex > 0)
                    msg = string.Format(L("yt_item_progress"), _itemIndex, _itemTotal) + ": " + msg;
                Dispatcher.InvokeAsync(() =>
                {
                    txbProgress.Text = msg;
                    Announce(msg);
                });
            }
        }

        private static bool IsJsRuntimeProblem(string line) =>
            line.Contains("JavaScript runtime", StringComparison.OrdinalIgnoreCase)
            || line.Contains("js-runtimes", StringComparison.OrdinalIgnoreCase)
            || line.Contains("n challenge", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Signature solving failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Only images are available", StringComparison.OrdinalIgnoreCase);

        // ═════════════════════════════════════════════════════════════════════
        //  PROCESS HELPERS
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Pokreće yt-dlp. Argumenti idu kroz ArgumentList (bez ručnog navođenja navodnika —
        /// nema problema sa razmacima/specijalnim znakovima u putanjama i linkovima).
        /// Otkazivanje stvarno gasi yt-dlp i sve njegove ffmpeg pod-procese (ranije je
        /// "Otkaži" samo menjao tekst, a preuzimanje je nastavljalo u pozadini).
        /// </summary>
        private async Task<(string stdout, string stderr, int exitCode)> RunProcessAsync(
            string exe, IEnumerable<string> args, Action<string> lineCallback, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            // yt-dlp je Python program: bez ovoga na Windowsu piše u lokalnoj kodnoj strani
            // (npr. cp1250), pa naslovi sa č/ć/š/ž/ü dolaze pokvareni.
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONUTF8"] = "1";

            // Ako smo preuzeli Deno, dodajemo ga u PATH samo za yt-dlp (yt-dlp ga tamo sam nađe).
            if (File.Exists(ManagedDenoExe))
            {
                string path = psi.Environment.TryGetValue("PATH", out var p0) ? p0 : Environment.GetEnvironmentVariable("PATH");
                psi.Environment["PATH"] = ManagedDenoDir + ";" + path;
            }

            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var sbOut = new StringBuilder();
            var sbErr = new StringBuilder();

            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (sbOut) sbOut.AppendLine(e.Data);
                lineCallback?.Invoke(e.Data);
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (sbErr) sbErr.AppendLine(e.Data);
                lineCallback?.Invoke(e.Data);
            };

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            try
            {
                await p.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                throw;
            }

            return (sbOut.ToString(), sbErr.ToString(), p.ExitCode);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  UI HELPERS
        // ═════════════════════════════════════════════════════════════════════

        private void SetFetchingState(bool fetching)
        {
            _fetchingInfo = fetching;
            btnFetchInfo.IsEnabled = !fetching;
            btnFetchInfo.Content   = fetching ? L("yt_btn_loadinfo_loading") : L("yt_btn_loadinfo");
            txtUrl.IsEnabled       = !fetching;
            if (!fetching && !txtUrl.IsKeyboardFocusWithin && pnlInfo.Visibility != Visibility.Visible)
                txtUrl.Focus();
        }

        private void SetDownloadState(bool downloading)
        {
            _downloadInProgress    = downloading;
            btnDownload.IsEnabled  = !downloading;
            btnFetchInfo.IsEnabled = !downloading;
            txtUrl.IsEnabled       = !downloading;
            grpScope.IsEnabled     = !downloading;
            pnlTrackPicker.IsEnabled = !downloading;
            btnCancel.Content      = downloading ? L("yt_btn_cancel_downloading") : L("yt_btn_cancel");
            pnlProgress.Visibility = Visibility.Visible;

            if (downloading)
            {
                prgDownload.Value = 0;
                txbProgress.Text  = L("yt_preparing");
                btnCancel.Focus();
            }
        }

        private void SetProgress(double percent, string label)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(() => SetProgress(percent, label)); return; }
            prgDownload.Value = percent;
            txbProgress.Text  = label;
        }

        /// <summary>
        /// Najava za JAWS/NVDA: tekst ide u uvek vidljiv status red, i EKSPLICITNO se diže
        /// LiveRegionChanged (WPF to ne radi sam kad se promeni tekst — ranije su najave
        /// u ovom dijalogu zato uglavnom bile nečujne).
        /// </summary>
        private void Announce(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return;
            if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(() => Announce(msg)); return; }

            txbStatus.Text = msg;
            try
            {
                var peer = UIElementAutomationPeer.CreatePeerForElement(txbStatus)
                           ?? UIElementAutomationPeer.FromElement(txbStatus);
                peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }
            catch { }
        }

        private void ShowError(string msg)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(() => ShowError(msg)); return; }
            // Samo upis u status (bez live najave) — MessageBox ionako čita JAWS/NVDA,
            // pa bi dvostruka najava samo smetala.
            txbStatus.Text = msg;
            WpfMessageBox.Show(this, msg, L("yt_error_title"), MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private static string FormatDuration(double secs)
        {
            if (secs <= 0) return "";
            var ts = TimeSpan.FromSeconds(secs);
            return ts.Hours > 0
                ? $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes}:{ts.Seconds:D2}";
        }
    }
}
