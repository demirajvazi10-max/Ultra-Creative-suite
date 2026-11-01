using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using WpfMessageBox = System.Windows.MessageBox;

namespace UltraVideoEditor
{
    public partial class VideoEngineDialog : Window
    {
        // Language helper
        private string _LangCode => LanguageManager.CurrentLanguage ?? "en";
        private string L(string key) => LanguageManager.GetText(key, _LangCode);
        private string LF(string key, params object[] args) => string.Format(LanguageManager.GetText(key, _LangCode), args);

        // ── Stanje ──────────────────────────────────────────────────
        private List<LyricShot>          _shots;
        private OllamaClient             _ollama;
        private bool                     _ollamaAvailable;
        private bool                     _isRunning;
        private CancellationTokenSource  _cts;
        private string                   _pixabayApiKey;

        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        // ── ViewModel za ListBox ─────────────────────────────────────
        private class ShotViewModel
        {
            public string    Timestamp   { get; set; }
            public string    ChorusLabel { get; set; }
            public string    VibeLabel   { get; set; }
            public ShotData  Data        { get; set; }
            public LyricShot Shot        { get; set; }

            // v1.4: list items use ToString() as their screen-reader name (otherwise the class name is read)
            public override string ToString() =>
                string.Join(", ", new[] { Timestamp, ChorusLabel, Shot?.Lyric, VibeLabel }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        // ── Konstruktor ──────────────────────────────────────────────
        public VideoEngineDialog()
        {
            InitializeComponent();
            UiScaling.Register(this);
            _pixabayApiKey = ReadPixabayKey();
            Loaded += OnLoaded;
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Announce(LanguageManager.T("lx_checking_ollama_d9c55"));
            try
            {
                _ollama         = new OllamaClient();
                _ollamaAvailable = await _ollama.IsOllamaRunning();
            }
            catch { _ollamaAvailable = false; }

            if (_ollamaAvailable)
            {
                txtOllamaInfo.Text          = LanguageManager.T("loc_ollama_active");
                txtOllamaInfo.Foreground    = System.Windows.Media.Brushes.LightGreen;
                chkUseOllama.IsChecked      = true;
                chkUseOllama.IsEnabled      = true;
            }
            else
            {
                txtOllamaInfo.Text          = LanguageManager.T("lx_ollama_is_not_available_the_498a1");
                txtOllamaInfo.Foreground    = System.Windows.Media.Brushes.Orange;
                chkUseOllama.IsChecked      = false;
                chkUseOllama.IsEnabled      = false;
            }

            // Check the local sound library
            int soundCount = LocalSoundLibrary.GetSoundCount();
            if (soundCount > 0)
            {
                if (txtFreesoundInfo != null)
                {
                    txtFreesoundInfo.Text       = LanguageManager.F("lx_local_library_sounds_ambient_sounds_a6d2a", soundCount);
                    txtFreesoundInfo.Foreground = System.Windows.Media.Brushes.LightGreen;
                }
            }
            else
            {
                if (txtFreesoundInfo != null)
                {
                    txtFreesoundInfo.Text       = LanguageManager.T("lx_assets_sounds_is_empty_add_8a2d3");
                    txtFreesoundInfo.Foreground = System.Windows.Media.Brushes.Orange;
                }
                if (chkAmbient != null) chkAmbient.IsEnabled = false;
            }

            UpdateStatus(LanguageManager.T("lx_ready_enter_the_song_lyrics_eb57b"));
            txtLyrics.Focus();
        }

        // ── Analiza stihova ──────────────────────────────────────────
        private void btnAnalyze_Click(object sender, RoutedEventArgs e)
        {
            var lyrics = ParseLyrics();
            if (lyrics.Count == 0)
            {
                Announce(LanguageManager.T("lx_enter_the_song_lyrics_one_a1c5c"));
                return;
            }

            var audio = GetAudioItem();
            double dur = audio?.Duration ?? EstimateDuration(lyrics.Count);

            var intent = GetIntent();
            _shots = VideoEngine.GenerateFromLyrics(lyrics, dur, intent);

            RefreshShotList();
            Announce(LanguageManager.F("lx_analysed_lines_chorus_lines_found_3b186", _shots.Count, _shots.Count(s => s.IsChorus), FormatTime(dur)));
        }

        private void RefreshShotList()
        {
            if (_shots == null) return;

            lstShots.ItemsSource = _shots.Select(s => new ShotViewModel
            {
                Timestamp   = s.Timestamp,
                ChorusLabel = s.IsChorus ? "REFREN" : "",
                VibeLabel   = $"v{s.Data.VibeScore}",
                Data        = s.Data,
                Shot        = s
            }).ToList();

            int choruses = _shots.Count(s => s.IsChorus);
            txtShotCount.Text = LanguageManager.F("lx_shots_chorus_lines_average_mood_0b6b8", _shots.Count, choruses, _shots.Average(s => s.Data.VibeScore));
        }

        private void lstShots_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstShots.SelectedItem is ShotViewModel vm)
            {
                var s = vm.Shot;
                Announce(LanguageManager.F("lx_shot_mood_search_movement_link_cd20d", s.LyricIndex + 1, s.Lyric, s.Data.ShotType, s.Data.VibeScore, s.Data.SearchQuery, s.Data.MotionIntent, s.Data.VisualBridge));
            }
        }

        // ── Kreiranje videa ──────────────────────────────────────────
        private async void btnGenerate_Click(object sender, RoutedEventArgs e)
        {
            var mainWin = System.Windows.Application.Current.MainWindow as MainWindow;
            if (mainWin == null) return;

            var audio = GetAudioItem();
            if (audio == null)
            {
                WpfMessageBox.Show(
                    "No audio file on the timeline.\nAdd a song first (Ctrl+Shift+I).",
                    "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var lyrics = ParseLyrics();
            if (lyrics.Count == 0)
            {
                WpfMessageBox.Show(LanguageManager.T("lx_enter_lyrics"),
                    LanguageManager.T("lx_warning"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Provjeri API key
            if (string.IsNullOrEmpty(_pixabayApiKey))
            {
                _pixabayApiKey = ReadPixabayKey();
                if (string.IsNullOrEmpty(_pixabayApiKey))
                {
                    var dlg = new ApiKeyDialog("pixabay",
                        "Pixabay API key not found.\n" +
                        "Sign up for free at pixabay.com/api.");
                    if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.ApiKey))
                    {
                        SavePixabayKey(dlg.ApiKey);
                        _pixabayApiKey = dlg.ApiKey;
                    }
                    else return;
                }
            }

            _cts       = new CancellationTokenSource();
            _isRunning = true;
            SetUIState(running: true);

            try
            {
                var    intent    = GetIntent();
                double duration  = audio.Duration;
                string mediaType = (cmbMedia.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "video";
                bool   showTitles = chkTitles.IsChecked == true;
                bool   useOllama  = chkUseOllama.IsChecked == true && _ollamaAvailable;

                // ── Step 1: Generate shot list ──────────────────────
                Announce(LanguageManager.T("lx_making_the_shot_list_e4c30"), 5);

                if (useOllama)
                {
                    Announce(LanguageManager.T("lx_ollama_is_reading_the_lyrics_b36ef"), 8);
                    string prompt    = VideoEngine.BuildOllamaPrompt(lyrics, duration, intent);
                    string aiOutput  = await _ollama.GenerateAsync(prompt, ct: _cts.Token);
                    _shots           = VideoEngine.MergeWithAIOutput(lyrics, duration, aiOutput);
                    Announce(LanguageManager.F("lx_ollama_made_shots_c58b6", _shots.Count), 15);
                }
                else
                {
                    _shots = VideoEngine.GenerateFromLyrics(lyrics, duration, intent);
                    Announce(LanguageManager.F("lx_the_local_engine_made_shots_a19d3", _shots.Count), 15);
                }

                RefreshShotList();

                // ── Korak 2: Preuzimanje medija ───────────────────────
                var downloaded = new List<(string path, LyricShot shot)>();
                int total      = _shots.Count;

                for (int i = 0; i < _shots.Count; i++)
                {
                    _cts.Token.ThrowIfCancellationRequested();

                    var shot = _shots[i];
                    int pct  = 15 + (i * 80 / total);
                    Announce(LanguageManager.F("loc_shot_downloading", i + 1, total, shot.Data.SearchQuery,
                             shot.Data.ShotType, shot.Data.VibeScore), pct);

                    string path = await DownloadMedia(
                        shot.Data.SearchQuery,
                        shot.Data.ShotType,
                        mediaType,
                        _cts.Token);

                    // Fallback: try only shot type + vibe descriptor
                    if (string.IsNullOrEmpty(path))
                    {
                        string fallback = shot.IsChorus
                            ? "happy children playing slow motion cinematic"
                            : "children nature park cinematic bokeh";
                        path = await DownloadMedia(fallback, shot.Data.ShotType,
                            mediaType, _cts.Token);
                    }

                    if (!string.IsNullOrEmpty(path))
                        downloaded.Add((path, shot));
                    else
                        Announce(LanguageManager.F("lx_warning_shot_was_not_downloaded_723bc", i+1));
                }

                // ── Korak 2b: Lokalni ambijentalni zvukovi ────────────
                var ambientFiles = new Dictionary<int, string>(); // shotIndex -> ambientPath
                if (downloaded.Count > 0 && chkAmbient?.IsChecked == true)
                {
                    Announce(LanguageManager.T("lx_choosing_ambient_sounds_from_the_58f3d"), 93);

                    var processedScenes = new HashSet<string>();
                    for (int i = 0; i < downloaded.Count; i++)
                    {
                        var shot = downloaded[i].shot;
                        string sceneKey = shot.IsChorus ? "chorus" : $"verse_{i / Math.Max(1, downloaded.Count / 4)}";

                        if (processedScenes.Contains(sceneKey)) continue;
                        processedScenes.Add(sceneKey);

                        _cts.Token.ThrowIfCancellationRequested();
                        string context = shot.IsChorus ? "joy playground" :
                            ExtractAmbientContext(shot.Data?.SearchQuery ?? "");
                        string ambPath = LocalSoundLibrary.GetAmbientSound(context);
                        if (!string.IsNullOrEmpty(ambPath))
                            ambientFiles[i] = ambPath;
                    }
                    Announce(LanguageManager.F("lx_ambient_sounds_chosen_6fbb7", ambientFiles.Count), 96);
                }

                // ── Korak 3: Dodaj na timeline ────────────────────────
                Announce(LanguageManager.T("lx_adding_to_the_timeline_6368d"), 97);
                mainWin.SaveState();

                // Remove existing video/image clips
                var existing = mainWin.timelineItems
                    // v1.4: only the main video track (overlays on Video 2 stay), plus the
                    // ambient clips a previous Video Engine run added.
                    .Where(i => ((i.IsImage || i.IsVideo) && !i.IsAudio && i.TrackIndex == 0) ||
                                (i.TrackIndex == 3 && (i.Name ?? "").StartsWith("🔊 Video Engine")))
                    .ToList();
                foreach (var item in existing)
                    mainWin.timelineItems.Remove(item);

                double cursor = 0;
                foreach (var (path, shot) in downloaded)
                {
                    string ext  = Path.GetExtension(path).ToLower();
                    string type = new[]{ ".mp4",".avi",".mov",".mkv" }.Contains(ext)
                        ? "Video" : "Image";

                    // Provjeri da li postoji ambient za ovaj kadar
                    int dlIdx = downloaded.IndexOf((path, shot));
                    string ambientForShot = ambientFiles.TryGetValue(dlIdx, out var amb) ? amb : null;

                    mainWin.timelineItems.Add(new TimelineItem
                    {
                        Path              = path,
                        Duration          = shot.Duration,
                        Start             = cursor,
                        End               = cursor + shot.Duration,
                        Name              = $"{(shot.IsChorus ? "REFREN" : $"Stih {shot.LyricIndex+1}")} [{shot.Data.ShotType}]",
                        Type              = type,
                        Volume            = 100,
                        TrackIndex        = 0,
                        VideoEffect       = new VideoEffectData(),
                        AudioDescription  = $"{shot.Data.ShotType}, vibe {shot.Data.VibeScore}/10: {shot.Lyric}",
                        AmbientSoundPath  = ambientForShot
                    });

                    // v1.4: the chosen ambient sound was only stored on the clip
                    // (AmbientSoundPath), which the renderer never reads — so it was never
                    // heard. Put it on Audio 2 under the shot instead, quietly.
                    if (!string.IsNullOrEmpty(ambientForShot) && File.Exists(ambientForShot))
                    {
                        mainWin.timelineItems.Add(new TimelineItem
                        {
                            Path             = ambientForShot,
                            Duration         = shot.Duration,
                            Start            = cursor,
                            End              = cursor + shot.Duration,
                            Name             = "🔊 Video Engine: " + Path.GetFileNameWithoutExtension(ambientForShot),
                            Type             = "Audio",
                            Volume           = 25,
                            TrackIndex       = 3,
                            UseFixedPosition = true,
                            FixedPosition    = cursor,
                            VideoEffect      = new VideoEffectData()
                        });
                    }

                    if (showTitles && !string.IsNullOrEmpty(shot.Lyric))
                        mainWin.AddSubtitle(shot.Lyric, cursor, cursor + shot.Duration);

                    cursor += shot.Duration;
                }

                mainWin.UpdateTimelineDisplay();
                Announce(LanguageManager.T("lx_done_55823"), 100);

                int choruses = downloaded.Count(d => d.shot.IsChorus);
                WpfMessageBox.Show(
                    $"Video Engine finished!\n\n" +
                    $"Downloaded: {downloaded.Count}/{total} shots\n" +
                    $"Of which chorus: {choruses}\n" +
                    $"Clip duration: {FormatTime(cursor)}\n" +
                    $"Audio duration: {FormatTime(duration)}\n\n" +
                    $"Render video: Ctrl+R",
                    "Done", MessageBoxButton.OK, MessageBoxImage.Information);

                DialogResult = true;
                Close();
            }
            catch (OperationCanceledException)
            {
                Announce(LanguageManager.T("lx_stopped_8fcc7"));
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(LanguageManager.F("generic_error", ex.Message), LanguageManager.T("generic_error_title"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isRunning = false;
                _cts?.Dispose(); _cts = null;
                SetUIState(running: false);
            }
        }

        // ── Export JSON ──────────────────────────────────────────────
        private void btnExportJson_Click(object sender, RoutedEventArgs e)
        {
            if (_shots == null || _shots.Count == 0)
            {
                Announce(LanguageManager.T("lx_there_is_no_shot_list_708d5"));
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title      = L("ved_save_shotlist"),
                Filter     = "JSON file|*.json",
                FileName   = "shot_list.json"
            };
            if (dlg.ShowDialog() == true)
            {
                File.WriteAllText(dlg.FileName,
                    VideoEngine.ToJson(_shots),
                    System.Text.Encoding.UTF8);
                Announce(LanguageManager.F("lx_shot_list_saved_b658a", Path.GetFileName(dlg.FileName)));
            }
        }

        // ── Download medija ──────────────────────────────────────────
        private async Task<string> DownloadMedia(
            string query, string shotType, string mediaType,
            CancellationToken ct)
        {
            // Enrichuj query sa shot type deskriptorom
            string enriched = shotType switch
            {
                "Close Up"  => $"{query} closeup portrait",
                "Wide Shot" => $"{query} landscape wide",
                _           => $"{query} medium"
            };

            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    string q   = attempt == 0 ? enriched : "children nature cinematic";
                    string url = mediaType == "video"
                        ? $"https://pixabay.com/api/videos/?key={_pixabayApiKey}" +
                          $"&q={Uri.EscapeDataString(q)}&safesearch=true&per_page=5"
                        : $"https://pixabay.com/api/?key={_pixabayApiKey}" +
                          $"&q={Uri.EscapeDataString(q)}&image_type=photo" +
                          $"&safesearch=true&per_page=5&min_width=1080";

                    string resp = await _http.GetStringAsync(url, ct);
                    var    json = JObject.Parse(resp);
                    var    hits = json["hits"] as JArray;
                    if (hits == null || hits.Count == 0) continue;

                    var    rng  = new Random(Guid.NewGuid().GetHashCode());
                    var    hit  = hits[rng.Next(hits.Count)];
                    string dlUrl = mediaType == "video"
                        ? (hit["videos"] as JObject)?["large"]?["url"]?.ToString()
                          ?? (hit["videos"] as JObject)?["medium"]?["url"]?.ToString()
                        : hit["largeImageURL"]?.ToString()
                          ?? hit["webformatURL"]?.ToString();

                    if (string.IsNullOrEmpty(dlUrl)) continue;

                    string ext  = mediaType == "video" ? ".mp4" : ".jpg";
                    string dest = Path.Combine(GetProjectFolder(),
                        $"VE_{Guid.NewGuid().ToString().Substring(0, 8)}{ext}");

                    byte[] data = await _http.GetByteArrayAsync(dlUrl, ct);
                    await File.WriteAllBytesAsync(dest, data, ct);
                    return dest;
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
            return null;
        }

        // Izvuci ambient kontekst iz Pixabay query-ja
        private static string ExtractAmbientContext(string query)
        {
            string lower = query.ToLower();
            if (lower.Contains("snow") || lower.Contains("winter")) return "snow";
            if (lower.Contains("forest") || lower.Contains("nature")) return "forest";
            if (lower.Contains("park") || lower.Contains("walk")) return "park";
            if (lower.Contains("playground") || lower.Contains("children play")) return "playground";
            if (lower.Contains("morning") || lower.Contains("sunrise")) return "morning";
            if (lower.Contains("cafe") || lower.Contains("chocolate")) return "cafe";
            if (lower.Contains("summer") || lower.Contains("sun")) return "summer";
            if (lower.Contains("autumn") || lower.Contains("fall")) return "autumn";
            if (lower.Contains("family") || lower.Contains("home")) return "home";
            return "park";
        }

        private List<string> ParseLyrics()
            => txtLyrics.Text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => l.Trim())
                .ToList();

        private VideoIntent GetIntent()
        {
            string type = (cmbIntent.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "emotional";
            return new VideoIntent { Type = type, Style = "cinematic", Pace = "auto" };
        }

        private TimelineItem GetAudioItem()
            => (System.Windows.Application.Current.MainWindow as MainWindow)
               ?.timelineItems.FirstOrDefault(i => i.IsAudio);

        private double EstimateDuration(int lyricCount)
            => lyricCount * 5.0; // default 5s po stihu

        private string GetProjectFolder()
        {
            if (System.Windows.Application.Current.MainWindow is MainWindow mw)
                return mw.GetCurrentProjectFolder();
            return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        // v1.4: one shared Pixabay key store (see MediaProviderSettings.LoadPixabayKey)
        private string ReadPixabayKey() => MediaProviderSettings.LoadPixabayKey();

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private void SavePixabayKey(string key) => MediaProviderSettings.SavePixabayKey(key);

        private static string FormatTime(double s)
            => TimeSpan.FromSeconds(s).ToString(@"m\:ss");

        // ── UI ───────────────────────────────────────────────────────
        private void UpdateStatus(string msg)
        {
            txtStatus.Text = msg;
            txtStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
        }

        private void Announce(string msg, int pct = -1)
        {
            Dispatcher.Invoke(() =>
            {
                txtStatus.Text = msg;
                if (pct >= 0)
                {
                    prgProgress.Value      = pct;
                    prgProgress.Visibility = Visibility.Visible;
                    txtProgress.Text       = $"{pct}% — {msg}";
                }
                // CreatePeerForElement umjesto FromElement — vidi napomenu u MainWindow.LogMessage
                var peer = System.Windows.Automation.Peers
                    .UIElementAutomationPeer.CreatePeerForElement(txtStatus)
                    ?? System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(txtStatus);
                peer?.RaiseAutomationEvent(
                    System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
            });
        }

        private void SetUIState(bool running)
        {
            btnGenerate.IsEnabled  = !running;
            btnAnalyze.IsEnabled   = !running;
            btnStop.IsEnabled      = running;
            prgProgress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            if (!running) { prgProgress.Value = 0; txtProgress.Text = ""; }
        }

        // ── Dugmad ───────────────────────────────────────────────────
        private void btnStop_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            btnStop.IsEnabled = false;
            Announce(LanguageManager.T("lx_stopping_746be"));
        }

        private void btnClose_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning) _cts?.Cancel();
            DialogResult = false;
            Close();
        }
    }
}
