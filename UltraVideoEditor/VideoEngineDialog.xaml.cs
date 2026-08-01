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
        private string _LangCode => (System.Windows.Application.Current?.MainWindow as MainWindow)?._currentLanguage ?? "en";
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
            Announce("Provjeram Ollama...");
            try
            {
                _ollama         = new OllamaClient();
                _ollamaAvailable = await _ollama.IsOllamaRunning();
            }
            catch { _ollamaAvailable = false; }

            if (_ollamaAvailable)
            {
                txtOllamaInfo.Text          = "Ollama aktivna — AI ce generisati query-je";
                txtOllamaInfo.Foreground    = System.Windows.Media.Brushes.LightGreen;
                chkUseOllama.IsChecked      = true;
                chkUseOllama.IsEnabled      = true;
            }
            else
            {
                txtOllamaInfo.Text          = "Ollama is not available — using the local engine";
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
                    txtFreesoundInfo.Text       = $"🔊 Local library: {soundCount} sounds — ambient sounds active";
                    txtFreesoundInfo.Foreground = System.Windows.Media.Brushes.LightGreen;
                }
            }
            else
            {
                if (txtFreesoundInfo != null)
                {
                    txtFreesoundInfo.Text       = "⚠️ Assets/Sounds/ is empty — add MP3/WAV files for ambient sounds";
                    txtFreesoundInfo.Foreground = System.Windows.Media.Brushes.Orange;
                }
                if (chkAmbient != null) chkAmbient.IsEnabled = false;
            }

            UpdateStatus("Ready. Enter the song lyrics and press ANALYZE.");
            txtLyrics.Focus();
        }

        // ── Analiza stihova ──────────────────────────────────────────
        private void btnAnalyze_Click(object sender, RoutedEventArgs e)
        {
            var lyrics = ParseLyrics();
            if (lyrics.Count == 0)
            {
                Announce("Enter the song lyrics, one per line.");
                return;
            }

            var audio = GetAudioItem();
            double dur = audio?.Duration ?? EstimateDuration(lyrics.Count);

            var intent = GetIntent();
            _shots = VideoEngine.GenerateFromLyrics(lyrics, dur, intent);

            RefreshShotList();
            Announce($"Analyzed: {_shots.Count} lines, {_shots.Count(s => s.IsChorus)} chorus lines detected. " +
                     $"Duration: {FormatTime(dur)}. Shot list preview is ready.");
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
            txtShotCount.Text = $"({_shots.Count} kadrova | {choruses} refrena | " +
                                $"prosj. vibe {_shots.Average(s => s.Data.VibeScore):F1}/10)";
        }

        private void lstShots_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstShots.SelectedItem is ShotViewModel vm)
            {
                var s = vm.Shot;
                Announce($"Kadar {s.LyricIndex + 1}: {s.Lyric}. " +
                         $"{s.Data.ShotType}, vibe {s.Data.VibeScore}/10. " +
                         $"Query: {s.Data.SearchQuery}. " +
                         $"Pokret: {s.Data.MotionIntent}. " +
                         $"Most: {s.Data.VisualBridge}.");
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
                WpfMessageBox.Show("Enter the song lyrics.",
                    "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                Announce("Generating the shot list...", 5);

                if (useOllama)
                {
                    Announce("Ollama is analyzing the lyrics and generating professional queries...", 8);
                    string prompt    = VideoEngine.BuildOllamaPrompt(lyrics, duration, intent);
                    string aiOutput  = await _ollama.GenerateAsync(prompt, ct: _cts.Token);
                    _shots           = VideoEngine.MergeWithAIOutput(lyrics, duration, aiOutput);
                    Announce($"Ollama generated {_shots.Count} shots.", 15);
                }
                else
                {
                    _shots = VideoEngine.GenerateFromLyrics(lyrics, duration, intent);
                    Announce($"Lokalni engine generisao {_shots.Count} kadrova.", 15);
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
                    Announce($"Kadar {i+1}/{total}: preuzimam '{shot.Data.SearchQuery}' " +
                             $"[{shot.Data.ShotType}, v{shot.Data.VibeScore}]...", pct);

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
                        Announce($"Warning: shot {i+1} was not downloaded.");
                }

                // ── Korak 2b: Lokalni ambijentalni zvukovi ────────────
                var ambientFiles = new Dictionary<int, string>(); // shotIndex -> ambientPath
                if (downloaded.Count > 0 && chkAmbient?.IsChecked == true)
                {
                    Announce("Selecting ambient sounds from the local library...", 93);

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
                    Announce($"Selected {ambientFiles.Count} ambient sounds.", 96);
                }

                // ── Korak 3: Dodaj na timeline ────────────────────────
                Announce("Adding to timeline...", 97);
                mainWin.SaveState();

                // Remove existing video/image clips
                var existing = mainWin.timelineItems
                    .Where(i => (i.IsImage || i.IsVideo) && !i.IsAudio)
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

                    if (showTitles && !string.IsNullOrEmpty(shot.Lyric))
                        mainWin.AddSubtitle(shot.Lyric, cursor, cursor + shot.Duration);

                    cursor += shot.Duration;
                }

                mainWin.UpdateTimelineDisplay();
                Announce("Done!", 100);

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
                Announce("Stopped.");
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show($"Error: {ex.Message}", "Error",
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
                Announce("No shot list. Press ANALYZE first.");
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
                Announce($"Shot lista sacuvana: {Path.GetFileName(dlg.FileName)}");
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
        private string ReadPixabayKey()
        {
            try
            {
                string dir  = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "UltraVideoEditor");
                string bin  = Path.Combine(dir, "pixabay_key.bin");
                if (File.Exists(bin))
                {
                    byte[] enc = File.ReadAllBytes(bin);
                    byte[] dec = System.Security.Cryptography.ProtectedData.Unprotect(
                        enc, null,
                        System.Security.Cryptography.DataProtectionScope.CurrentUser);
                    return System.Text.Encoding.UTF8.GetString(dec).Trim();
                }
                string txt = Path.Combine(dir, "pixabay_key.txt");
                if (File.Exists(txt)) return File.ReadAllText(txt).Trim();
            }
            catch { }
            return null;
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private void SavePixabayKey(string key)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "UltraVideoEditor");
                Directory.CreateDirectory(dir);
                byte[] data = System.Text.Encoding.UTF8.GetBytes(key);
                byte[] enc  = System.Security.Cryptography.ProtectedData.Protect(
                    data, null,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser);
                File.WriteAllBytes(Path.Combine(dir, "pixabay_key.bin"), enc);
            }
            catch { }
        }

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
                var peer = System.Windows.Automation.Peers
                    .UIElementAutomationPeer.FromElement(txtStatus);
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
            Announce("Zaustavljanje...");
        }

        private void btnClose_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning) _cts?.Cancel();
            DialogResult = false;
            Close();
        }
    }
}
