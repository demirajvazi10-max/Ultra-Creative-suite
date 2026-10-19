using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using Microsoft.Win32;
using SkiaSharp;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfSaveFileDialog = Microsoft.Win32.SaveFileDialog;
using WpfMessageBox = System.Windows.MessageBox;
using WpfApp = System.Windows.Application;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace UltraVideoEditor
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public partial class AIVideoCreator : Window
    {
        private OllamaClient _ollama;
        private bool _ollamaRunning = false;
        private List<string> _lyricLines;
        private Dictionary<int, double> _lyricTimestamps = new Dictionary<int, double>();
        private List<TimelineSegment> _segments;
        private string _pixabayApiKey;
        private bool _showLyrics = false;

        private double _timelineCursorOffset = 0;

        public static bool FastRenderMode = true;
        private bool _enableTransitionSounds = true;
        private bool _enableAmbientSounds = true;
        private bool _enableVoiceNarration = false;
        private string _selectedTheme = "fun";
        private string _tempVideoFolder;
        private string _ambientAudioPath;
        private string _audioPath;
        private double _totalDuration;
        private BeatInfo _beatInfo;
        private double _audioStartSeconds = 0.0;  // Seconds of silence at the start of the audio file
        private double _cutAdvanceMs = 120.0;      // ms ranije od beat peaka (default 120ms)
        private List<AITranscription.WordTiming> _wordTimings = new();  // Whisper word timestamps

        // ── v1.4 lyric alignment ─────────────────────────────────────────────
        // Raw words Whisper heard in the song (cached per audio file), so changing the
        // lyrics text only needs a fast re-alignment, not a new Whisper run.
        private List<AITranscription.WordTiming> _heardWords = null;
        private string _heardWordsAudioPath = null;
        // The exact lyrics text the current _lyricTimestamps belong to. If the text in
        // the box differs (user edited/re-split lines), the timing is redone before generating.
        private string _timedLyricsText = null;
        // Per lyric line: the user's words with times (karaoke). Index = lyric line.
        private List<List<AITranscription.WordTiming>> _lineWordTimings = null;
        private bool _karaoke = true;
        private bool _reviewScenes = true;
        private Dictionary<int, double> _lyricEndTimestamps = new Dictionary<int, double>(); // Whisper end times
        private readonly Dictionary<string, string> _lyricQueryCache
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // Refren cache

        /// <summary>Exposes BeatInfo za RenderEngine beat-sync rezove.</summary>
        public BeatInfo GetBeatInfo() => _beatInfo;

        private MotionResult _lastClipMotion;
        private MotionResult _lastClipEndMotion;   // EndDirection prethodnog klipa za precizni jump-cut matching
        private double _lastDownloadedVisionScore = 6.0;
        private string _lastDownloadedDescription = null;   // v1.4: Qwen's description of the chosen clip
        private bool _lastDownloadedIsStatic = false;
        private bool _lastDownloadedHasChildren = false;
        private bool _lastDownloadedIsOutdoor = false;
        private string _detectedMood = "neutral";
        private string _detectedContext = "";
        private SongContext _songContext = new SongContext(); // Iskra AI-First: kontekst za StrictQueryEngine

        // ── Auto-Render: pokreni render automatski posle generisanja ─────────
        // User sets path, checks checkbox, goes to sleep :)
        public string AutoRenderOutputPath { get; set; } = "";
        public bool AutoRenderEnabled { get; set; } = false;
        // Setuju se iz scene loopa da bi bili dostupni unutar SearchAndDownloadMedia
        private string _currentLyric = null;
        private string _currentSeason = null;
        private string _detectedSeason = "";

        /// <summary>
        /// Cached resolution — read once on the UI thread at the start of generation.
        /// Koristi se sa background threadova bez Dispatcher problema.
        /// </summary>
        private string _cachedResolution = "1920x1080";
        private string _cachedLang = "sr";

        private string _targetResolution => _cachedResolution;
        private string _lang => LanguageManager.CurrentLanguage;   // v1.4: always the current UI language

        private void CacheResolutionFromUIThread()
        {
            string res = "1920x1080";
            string lang = "sr";
            try
            {
                Dispatcher.Invoke(() =>
                {
                    var mw = System.Windows.Application.Current?.MainWindow as MainWindow;
                    res = mw?.GetExportResolution?.Invoke() ?? "1920x1080";
                    lang = mw?._currentLanguage ?? "en";
                });
            }
            catch { }
            _cachedResolution = string.IsNullOrWhiteSpace(res) ? "1920x1080" : res;
            _cachedLang = string.IsNullOrWhiteSpace(lang) ? "sr" : lang;
        }

        /// <summary>Width in pixels from _targetResolution.</summary>
        private int _targetWidth
        {
            get
            {
                var parts = _targetResolution.Split('x');
                return parts.Length == 2 && int.TryParse(parts[0], out int w) ? w : 1920;
            }
        }

        /// <summary>Visina u pikselima iz _targetResolution.</summary>
        private int _targetHeight
        {
            get
            {
                var parts = _targetResolution.Split('x');
                return parts.Length == 2 && int.TryParse(parts[1], out int h) ? h : 1080;
            }
        }

        /// <summary>
        /// min_height for Pixabay API search: for 4K we request 2160, for 1080 we request 1080, etc.
        /// </summary>
        private int _pixabayMinHeight => _targetHeight;

        private string L(string key) => LanguageManager.GetText(key, _lang);
        private string LF(string key, params object[] args) => string.Format(LanguageManager.GetText(key, _lang), args);

        private readonly Dictionary<string, string> _transitionSoundCache = new Dictionary<string, string>();

        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private static readonly HttpClient _dlHttpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        private CancellationTokenSource _cts;
        private bool _isRunning = false;
        private readonly SemaphoreSlim _apiRateLimiter = new SemaphoreSlim(1, 1);

        private readonly HashSet<string> _usedMediaUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // FIX-COOLDOWN: Tracks when a specific query theme type was last used
        // Prevents visual repetition of similar shots (stream, forest) in a short period
        // Key = normalized query tema (npr "stream water nature"), Value = zadnji timestamp scene
        private readonly Dictionary<string, int> _queryThemeCooldown =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private int _currentSceneIndex = 0;
        private const int QUERY_COOLDOWN_SCENES = 4; // Ne ponavljaj istu temu unutar 4 scene (~12-16s)
        private readonly HashSet<string> _seenPixabayIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _seenPexelsIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _queryUseCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Page rotation — when the first page (30 results) is exhausted, we take the next
        private readonly Dictionary<string, int> _queryPageMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private List<string> _universalKeywords = new List<string>
        {
            "children playing in park soft light warm colors",
            "kids running and laughing happy atmosphere",
            "children on swings warm colors",
            "happy kids playing outdoors soft light",
            "children having fun warm colors",
            "kids playing together happy atmosphere",
            "eastern european children playing park",
            "european kids outdoor warm light",
            "slavic children happy playing"
        };

        private static readonly Dictionary<string, List<string>> _contextKeywords = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["lullaby"] = new List<string> { "baby sleeping peaceful soft light", "toddler sleeping cozy bedroom night", "mother rocking baby to sleep warm light" },
            ["party"] = new List<string> { "children birthday party colorful balloons", "kids celebrating happy confetti", "birthday cake candles children excited" },
            ["love"] = new List<string> { "couple holding hands sunset romantic", "romantic walk park golden hour", "love heart flowers warm colors" },
            ["sad"] = new List<string> { "child alone window rain melancholy", "person looking out rainy window", "melancholy empty bench autumn" },
            ["adventure"] = new List<string> { "child exploring forest adventure", "kids hiking mountains adventure", "children running through field adventure" },
            ["nature"] = new List<string> { "beautiful nature flowers soft light", "meadow flowers butterflies sunlight", "spring flowers garden warm light" },
            ["dance"] = new List<string> { "children dancing joyful colorful", "kids dance class happy movement", "children spinning dancing colorful clothes" },
            ["school"] = new List<string> { "children learning classroom bright", "kids school backpack happy", "children reading books learning" },
            ["animal"] = new List<string> { "cute animals children playing warm", "child playing with dog happy", "kids petting animals farm" },
            ["christmas"] = new List<string> { "christmas tree lights cozy warm", "children christmas gifts excited", "winter holiday family warm lights" },
            ["music"] = new List<string> {
                "children singing together joyful music",
                "child playing piano keys happy",
                "kids dancing music colorful fun",
                "children with instruments school music class",
                "child headphones listening music happy bedroom"
            },
            ["fun"] = new List<string> { "children playing in park soft light warm colors", "kids running and laughing happy atmosphere", "children on swings warm colors" },
            ["outdoor"] = new List<string> {
                "children running park playground sunny",
                "kids playing park green grass happy",
                "child walking park family sunny day",
                "children playground swings slide happy",
                "kids outdoor park nature sunny playing"
            },
            ["health"] = new List<string> {
                "children running active outdoor park",
                "kids healthy active outdoor sunny",
                "child jumping playing park happy healthy",
                "children outdoor exercise active fun",
                "kids sports active outdoor healthy sunny"
            }
        };

        public AIVideoCreator()
        {
            InitializeComponent();
            UiScaling.Register(this);
            Loaded += AIVideoCreator_Loaded;
            _pixabayApiKey = GetPixabayApiKey();
        }

        private async void AIVideoCreator_Loaded(object sender, RoutedEventArgs e)
        {
            AutomationProperties.SetName(this, "AI Video Creator dialog");
            txtOllamaStatus.Text = L("ol_checking");
            txtOllamaStatus.Foreground = System.Windows.Media.Brushes.Orange;

            _ollama = new OllamaClient();
            _ollamaRunning = await _ollama.IsOllamaRunning();

            if (_ollamaRunning)
            {
                txtOllamaStatus.Text = L("ollama_running");
                txtOllamaStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
                btnGenerate.IsEnabled = true;
            }
            else
            {
                txtOllamaStatus.Text = L("ollama_not_running");
                txtOllamaStatus.Foreground = System.Windows.Media.Brushes.Red;
                btnGenerate.IsEnabled = false;
            }

            InitLocalSounds();
            AutoPopulateSongInfo();
            txtLyrics.Focus();
        }

        private void AutoPopulateSongInfo()
        {
            var mainWindow = System.Windows.Application.Current.MainWindow as MainWindow;
            if (mainWindow == null) return;

            var audioItem = mainWindow.timelineItems.FirstOrDefault(i => i.Type == "Audio");
            if (audioItem == null || string.IsNullOrEmpty(audioItem.Path)) return;

            string fileName = Path.GetFileNameWithoutExtension(audioItem.Path);
            if (string.IsNullOrWhiteSpace(fileName)) return;

            if (txtIntroText != null)
                txtIntroText.Text = fileName;

            if (txtOutroText != null)
            {
                if (fileName.Contains(" - "))
                {
                    var parts = fileName.Split(new[] { " - " }, 2, StringSplitOptions.None);
                    string izvodjac = parts[0].Trim();
                    string naziv = parts[1].Trim();
                    txtOutroText.Text =
                        naziv + " | " + LF("aic_outro_author", izvodjac) +
                        L("outro_subscribe");
                }
                else
                {
                    // v1.4: the outro is the closing credits, not the song title again
                    txtOutroText.Text = L("aic_default_outro");
                }
            }

            LogToMainWindow($"🎵 Auto-filled from audio file: '{fileName}'");
        }

        private void InitLocalSounds()
        {
            int count = LocalSoundLibrary.GetSoundCount();
            if (count > 0)
            {
                LogToMainWindow($"🔊 Local sound library: {count} files — ambient sounds active.");
            }
            else
            {
                LogToMainWindow("⚠️ Assets/Sounds/ is empty or does not exist — ambient sounds will not be available.");
                LogToMainWindow("   Place MP3/WAV files in Assets/Sounds/ to activate.");
                _enableAmbientSounds = false;
                if (chkAmbientSounds != null) chkAmbientSounds.IsChecked = false;
                if (chkAmbientSounds != null) chkAmbientSounds.IsEnabled = false;
            }
        }

        private void btnBrowseLogo_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new WpfOpenFileDialog { Filter = L("filter_images") };
            if (dialog.ShowDialog() == true)
                txtLogoPath.Text = dialog.FileName;
        }

        private void txtLyrics_TextChanged(object sender, TextChangedEventArgs e)
        {
            _lyricLines = txtLyrics.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                         .Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

            lstKeywords.Items.Clear();
            for (int i = 0; i < _lyricLines.Count; i++)
            {
                var stackPanel = new StackPanel { Orientation = WpfOrientation.Horizontal, Margin = new Thickness(5) };
                stackPanel.Children.Add(new TextBlock { Text = $"{i + 1}. ", Width = 40, Foreground = System.Windows.Media.Brushes.White, VerticalAlignment = VerticalAlignment.Center });
                stackPanel.Children.Add(new TextBlock { Text = _lyricLines[i], Width = 350, Foreground = System.Windows.Media.Brushes.LightGray, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                stackPanel.Children.Add(new TextBlock { Text = L("ai_keywords_label"), Width = 300, Foreground = System.Windows.Media.Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
                lstKeywords.Items.Add(stackPanel);
            }
            btnGenerate.IsEnabled = _lyricLines.Count > 0;
        }

        private async void btnAutoTranscribe_Click(object sender, RoutedEventArgs e)
        {
            string audioPath = null;

            var mainWindow = System.Windows.Application.Current.MainWindow as MainWindow;
            if (mainWindow != null)
            {
                var audioItem = mainWindow.timelineItems
                    .FirstOrDefault(t => t.IsAudio && File.Exists(t.Path));
                if (audioItem != null)
                    audioPath = audioItem.Path;
            }

            if (string.IsNullOrEmpty(audioPath))
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select audio file for transcription",
                    Filter = L("filter_audio_video")
                };
                if (dlg.ShowDialog() != true) return;
                audioPath = dlg.FileName;
            }

            if (!AITranscription.IsWhisperAvailable())
            {
                var msg = L("whisper_not_found_msg") + Environment.NewLine + Environment.NewLine +
                          "Install it in one of these two ways:" + Environment.NewLine + Environment.NewLine +
                          "OPTION A - Python (recommended):" + Environment.NewLine +
                          "  pip install openai-whisper" + Environment.NewLine + Environment.NewLine +
                          "OPTION B - Standalone (no Python required):" + Environment.NewLine +
                          "  Download faster-whisper-xxl.exe" + Environment.NewLine +
                          "  and place it next to UltraVideoEditor.exe" + Environment.NewLine + Environment.NewLine +
                          "After installation, restart the application.";
                WpfMessageBox.Show(msg, L("whisper_not_found_title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            btnAutoTranscribe.IsEnabled = false;
            btnAutoTranscribe.Content = "⏳ Transcribing...";
            if (txtTranscribeStatus != null)
                txtTranscribeStatus.Text = "Starting Whisper...";

            var progress = new Progress<string>(msg =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (txtTranscribeStatus != null)
                        txtTranscribeStatus.Text = msg;
                    AnnounceToUser(msg, 0);
                });
            });

            string ffmpegPath = AppPaths.FFmpeg;

            try
            {
                var result = await AITranscription.TranscribeAsync(
                    audioPath,
                    language: "sr",
                    ffmpegPath: ffmpegPath,
                    modelSize: "large-v3",
                    progress: progress,
                    ct: _cts?.Token ?? CancellationToken.None);

                if (!result.Success)
                {
                    WpfMessageBox.Show(result.ErrorMessage, L("transcription_error_title"),
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                txtLyrics.Text = AITranscription.FormatLyricsForTextBox(result.Lines);

                _lyricTimestamps.Clear();
                for (int i = 0; i < result.Lines.Count; i++)
                    _lyricTimestamps[i] = result.Lines[i].StartSeconds;

                // v1.4: keep what Whisper heard (word level). If the user now corrects the
                // text, EnsureLyricTimingAsync re-aligns the corrected lines to these words.
                _heardWords = result.WordTimings;
                _heardWordsAudioPath = audioPath;
                _wordTimings = result.WordTimings ?? new List<AITranscription.WordTiming>();
                _lineWordTimings = null;
                _timedLyricsText = null; // forces a word-level alignment at generation (fast, cached words)

                if (txtTranscribeStatus != null)
                    txtTranscribeStatus.Text = $"✅ {result.Lines.Count} linija prepoznato";

                AnnounceToUser(LF("transcription_done", result.Lines.Count), 0);
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(LF("generic_error", ex.Message), L("error_title"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnAutoTranscribe.IsEnabled = true;
                btnAutoTranscribe.Content = "🎤 Transcribe from audio";
            }
        }

        // ── v1.4: lyric timing ────────────────────────────────────────────────
        /// <summary>
        /// Gives every lyric line in the box a real start time before the video is built.
        ///  • Timing already belongs to exactly this text → nothing to do.
        ///  • Whisper words for this song are cached (from "Transcribe" or an earlier run)
        ///    → re-align the current lines to them (instant, no Whisper run).
        ///  • Otherwise → Whisper forced alignment with the user's text as the prompt, then
        ///    word-level alignment (LyricAligner).
        /// If nothing works, stale timestamps that no longer match the lines are dropped,
        /// so scenes are never synced to the wrong lines.
        /// </summary>
        private async Task EnsureLyricTimingAsync(string audioPath, CancellationToken ct)
        {
            var lines = _lyricLines ?? new List<string>();
            if (lines.Count == 0 || string.IsNullOrEmpty(audioPath) || !File.Exists(audioPath)) return;

            string textKey = string.Join("\n", lines);
            if (_timedLyricsText == textKey && _lyricTimestamps.Count == lines.Count)
                return;

            // 1. Cached Whisper words for this exact audio file
            if (_heardWords != null && _heardWords.Count > 0 &&
                string.Equals(_heardWordsAudioPath, audioPath, StringComparison.OrdinalIgnoreCase))
            {
                var cached = new AITranscription.TranscriptionResult();
                if (AITranscription.TryApplyWordAlignment(lines, _heardWords, cached))
                {
                    ApplyLyricTiming(cached, textKey);
                    return;
                }
                LogToMainWindow($"🔤 Cached words matched only {cached.AlignmentConfidence:P0} of the lyrics — running Whisper alignment");
            }

            // 2. Whisper forced alignment
            if (AITranscription.IsWhisperAvailable())
            {
                AnnounceToUser(L("aligning_lyrics"), 2);
                var progress = new Progress<string>(msg => LogToMainWindow("   🔤 " + msg));
                string ffmpegPath = AppPaths.FFmpeg;
                var res = await AITranscription.ForcedAlignAsync(audioPath, lines, "sr", ffmpegPath, "large-v3", progress, ct);
                ct.ThrowIfCancellationRequested();

                if (res.HeardWords != null && res.HeardWords.Count > 0)
                {
                    _heardWords = res.HeardWords;
                    _heardWordsAudioPath = audioPath;
                }

                if (res.Success && res.Lines.Count == lines.Count)
                {
                    ApplyLyricTiming(res, textKey);
                    return;
                }
                LogToMainWindow($"⚠️ Lyric alignment failed: {res.ErrorMessage}");
            }
            else
            {
                LogToMainWindow("ℹ️ Whisper is not installed — lyric lines cannot be aligned to the song");
            }

            // 3. Nothing worked: never keep timestamps that belong to a different text
            if (_lyricTimestamps.Count != lines.Count)
            {
                _lyricTimestamps.Clear();
                _lineWordTimings = null;
                _wordTimings = new List<AITranscription.WordTiming>();
            }
            AnnounceToUser(L("lyrics_not_aligned"));
        }

        private void ApplyLyricTiming(AITranscription.TranscriptionResult res, string textKey)
        {
            _lyricTimestamps.Clear();
            for (int i = 0; i < res.Lines.Count; i++)
                _lyricTimestamps[i] = res.Lines[i].StartSeconds;

            _lineWordTimings = res.LineWords != null && res.LineWords.Count == res.Lines.Count ? res.LineWords : null;
            _wordTimings = res.WordTimings ?? new List<AITranscription.WordTiming>();
            _timedLyricsText = textKey;

            string confidence = res.AlignmentConfidence >= 0 ? $"{res.AlignmentConfidence:P0}" : "—";
            LogToMainWindow($"🔤 Lyrics aligned: {res.Lines.Count} lines, {_wordTimings.Count} words, matched {confidence}");
            for (int i = 0; i < res.Lines.Count; i++)
                LogToMainWindow($"   {FormatTime(res.Lines[i].StartSeconds)}  {res.Lines[i].Text}");
            AnnounceToUser(LF("lyrics_aligned", res.Lines.Count, confidence));
        }

        private async void btnGenerate_Click(object sender, RoutedEventArgs e)
        {
            var mainWindow = WpfApp.Current.MainWindow as MainWindow;
            if (mainWindow == null) { WpfMessageBox.Show(L("mainwindow_not_available"), L("error_title"), MessageBoxButton.OK, MessageBoxImage.Error); return; }

            CacheResolutionFromUIThread(); // read resolution while still on UI thread

            _enableTransitionSounds = chkTransitionSounds.IsChecked == true;
            _enableAmbientSounds = chkAmbientSounds.IsChecked == true;
            FastRenderMode = chkFastRender.IsChecked == true;
            _enableVoiceNarration = chkVoiceNarration.IsChecked == true;
            _selectedTheme = (cmbVideoTheme.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "fun";

            LogToMainWindow($"🎬 Postavke: TransitionSounds={_enableTransitionSounds}, AmbientSounds={_enableAmbientSounds}, VoiceNarration={_enableVoiceNarration}, Theme={_selectedTheme}");

            var audioItem = mainWindow.timelineItems.FirstOrDefault(i => i.Type == "Audio");

            double totalDuration;
            bool hasAudio = audioItem != null && audioItem.Duration > 0;

            if (!hasAudio)
            {
                var result = WpfMessageBox.Show(L("no_audio_on_timeline"),
                    "🎵 AI Muzika", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                {
                    WpfMessageBox.Show(L("add_audio_manually"),
                        L("warning"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                AnnounceToUser(L("ai_analyzing_song"), 0);
                var tempStoryBoard = await GenerateStoryBoard(_lyricLines, _cts?.Token ?? CancellationToken.None);

                totalDuration = 180;

                AnnounceToUser(L("ai_searching_music"), 5);
                string mood = _detectedMood ?? tempStoryBoard?.OverallTheme ?? "happy";
                string aiMusicPath = await DownloadBackgroundMusic(mood, totalDuration, _tempVideoFolder, _cts.Token);

                if (!string.IsNullOrEmpty(aiMusicPath))
                {
                    var newAudioItem = new TimelineItem
                    {
                        Path = aiMusicPath,
                        Duration = 0,
                        Start = 0,
                        End = 0,
                        Name = "🎵 AI-generated music",
                        Type = "Audio",
                        Volume = 100,
                        TrackIndex = 2
                    };
                    await Dispatcher.InvokeAsync(() => mainWindow.timelineItems.Add(newAudioItem));

                    totalDuration = await GetAudioDuration(aiMusicPath);
                    newAudioItem.Duration = totalDuration;
                    newAudioItem.End = totalDuration;

                    LogToMainWindow($"🎵 AI-generated music added, duration: {FormatTime(totalDuration)}");
                    audioItem = newAudioItem;
                    hasAudio = true;
                }
                else
                {
                    WpfMessageBox.Show(L("ai_music_download_failed"),
                        L("error_title"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            else
            {
                totalDuration = audioItem.Duration;
                if (totalDuration <= 0)
                {
                    WpfMessageBox.Show(L("audio_unknown_duration"), L("warning"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            _lyricLines = txtLyrics.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

            if (_lyricLines.Count == 0)
            {
                int autoSceneCount = Math.Max(4, (int)(totalDuration / 7.0));
                _lyricLines = GenerateInstrumentalSceneDescriptions(autoSceneCount, totalDuration, _detectedContext, _detectedMood);
                AnnounceToUser(LF("no_lyrics_generating_auto", autoSceneCount), 0);
            }

            _showLyrics = chkShowLyrics.IsChecked == true;
            _karaoke = chkKaraoke.IsChecked == true;
            _reviewScenes = chkReviewScenes.IsChecked == true && chkAutoRender.IsChecked != true && !AutoRenderEnabled;
            bool userTypedLyrics = !string.IsNullOrWhiteSpace(txtLyrics.Text);
            _cts = new CancellationTokenSource();
            _isRunning = true;
            btnGenerate.IsEnabled = false;
            btnCancel.Content = L("stop_button");
            btnGenerate.Content = L("ai_creating_story");
            prgProgress.Visibility = Visibility.Visible;
            AnnounceToUser(LF("audio_duration_analyzing", FormatTime(totalDuration), totalDuration, _lyricLines.Count), 0);

            try
            {
                // v1.4: make sure every lyric line has a real start time (word-level alignment
                // of the user's own text), not an even spread over the song.
                if (userTypedLyrics)
                    await EnsureLyricTimingAsync(audioItem.Path, _cts.Token);

                await ProcessVideoCreation(audioItem.Path, totalDuration);
            }
            catch (OperationCanceledException) { AnnounceToUser(L("operation_cancelled")); }
            catch (Exception ex) { WpfMessageBox.Show(LF("generic_error", ex.Message), L("generic_error_title"), MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { _isRunning = false; _cts?.Dispose(); _cts = null; btnGenerate.IsEnabled = true; btnGenerate.Content = L("create_video_button"); btnCancel.Content = L("cancel_button"); btnCancel.IsEnabled = true; prgProgress.Visibility = Visibility.Collapsed; txtProgress.Text = ""; }
        }

        #region AI Pipeline - 3 Step Architecture

        private async Task<SongAnalysis> AnalyseSongWithAI(List<string> lyrics, CancellationToken ct)
        {
            string lyricsText = string.Join("\n", lyrics.Take(12)); // prvih 12 stihova je dovoljno

            string prompt =
                "You are a music video director. Analyze this children's song and respond ONLY with a JSON object. " +
                "No explanation, no markdown, no extra text — ONLY the JSON.\n\n" +
                "SONG LYRICS:\n" + lyricsText + "\n\n" +
                "Respond with exactly this JSON structure:\n" +
                "{\"context\":\"fun\",\"mood\":\"happy\",\"theme\":\"children playing in the park\",\"visual_style\":\"bright colorful outdoor kids playground\",\"main_subject\":\"children\",\"season\":\"spring\",\"setting\":\"park\"}\n\n" +
                "Rules:\n" +
                "- context: one of: fun, lullaby, party, love, sad, adventure, nature, dance, school, animal, christmas, outdoor, seasons, health\n" +
                "- mood: one of: happy, calm, melancholy, romantic, excited, energetic, upbeat, curious, playful, joyful, peaceful\n" +
                "- theme: one sentence in Serbian describing the song\n" +
                "- visual_style: 5-7 English words describing the visual style\n" +
                "- main_subject: children, baby, family, nature, or animal\n" +
                "- season: spring, summer, autumn, winter, all, or none\n" +
                "- setting: park, bedroom, school, forest, beach, city, home, outdoors, or mixed\n" +
                "ONLY JSON. Start your response with {";

            try
            {
                LogToMainWindow(L("ai_song_analyzing"));
                string response = await _ollama.GenerateAsync(prompt, model: OllamaClient.QueryModel, ct: ct);
                string jsonStr = ExtractJson(response);
                if (!string.IsNullOrEmpty(jsonStr))
                {
                    var analysis = Newtonsoft.Json.JsonConvert.DeserializeObject<SongAnalysis>(jsonStr);
                    bool validAnalysis = analysis != null
                        && !string.IsNullOrWhiteSpace(analysis.Context)
                        && !string.IsNullOrWhiteSpace(analysis.Mood);
                    if (validAnalysis)
                    {
                        LogToMainWindow($"🎭 AI analysis: context='{analysis.Context}', mood='{analysis.Mood}', theme='{analysis.Theme}'");
                        return analysis;
                    }
                    LogToMainWindow(L("ai_empty_values"));
                }
                else
                {
                    LogToMainWindow($"⚠️ AI did not return JSON. Response: '{response?.Substring(0, Math.Min(100, response?.Length ?? 0))}'");
                }
            }
            catch (Exception ex)
            {
                LogToMainWindow(LF("ai_analysis_failed", ex.Message));
            }

            return FallbackSongAnalysis(lyrics);
        }

        private SongAnalysis FallbackSongAnalysis(List<string> lyrics)
        {
            string allText = string.Join(" ", lyrics).ToLower();
            var scores = new Dictionary<string, int> { ["music"] = 0, ["lullaby"] = 0, ["party"] = 0, ["love"] = 0, ["sad"] = 0, ["adventure"] = 0, ["nature"] = 0, ["dance"] = 0, ["school"] = 0, ["animal"] = 0, ["christmas"] = 0, ["outdoor"] = 0, ["seasons"] = 0, ["health"] = 0, ["fun"] = 1 };

            string[] musicW = { "muzik", "melodij", "pesm", "pjesm", "svira", "instrument", "nota", "ritam", "zvuk", "gitara", "klavir", "bubanj", "violina", "flauta", "pevaj", "pjevaj", "muzicar", "koncert", "slusaj muzik", "blago muzik" };
            string[] lullabyW = { "spavaj", "usni", "sni", "laku noc", "uspavanka", "sleep", "lullaby", "goodnight", "moonlight" };
            string[] partyW = { "sretan", "srecan", "rodjendan", "baloni", "torta", "birthday", "party", "balloon", "cake", "celebrate" };
            string[] loveW = { "volim", "ljubav", "srce", "draga", "dragi", "love", "heart", "kiss", "hug", "romance" };
            string[] sadW = { "placem", "suze", "tuzan", "tuga", "cry", "tears", "sad", "alone", "lonely", "goodbye" };
            string[] adventW = { "istrazi", "avantura", "planina", "suma", "adventure", "explore", "mountain", "forest", "discover" };
            string[] natureW = { "cvijet", "proljece", "jesen", "zima", "ljeto", "priroda", "flower", "spring", "autumn", "winter", "summer", "nature" };
            string[] danceW = { "plesi", "igraj", "ples", "ritam", "dance", "dancing", "rhythm", "music", "sing" };
            string[] schoolW = { "skola", "ucenje", "knjiga", "ucitelj", "school", "learn", "book", "teacher", "class" };
            string[] animalW = { "pas", "maca", "konj", "zec", "ptica", "dog", "cat", "horse", "bunny", "animal" };
            string[] xmasW = { "bozic", "nova godina", "snijeg", "jelka", "christmas", "santa", "snow", "holiday", "gift" };
            string[] outdoorW = { "setaj", "setnja", "trci", "park", "outdoor", "walk", "run", "playground", "park", "outside" };
            string[] seasonsW = { "prolece", "proljece", "jesen", "zima", "leto", "ljeto", "spring", "autumn", "fall", "winter", "summer", "seasons" };
            string[] healthW = { "zdravlje", "zdravo", "zdravi", "kretanje", "health", "healthy", "exercise", "active", "fit", "movement" };

            foreach (var w in musicW) if (allText.Contains(w)) scores["music"] += 4;
            foreach (var w in lullabyW) if (allText.Contains(w)) scores["lullaby"] += 3;
            foreach (var w in partyW) if (allText.Contains(w)) scores["party"] += 3;
            foreach (var w in loveW) if (allText.Contains(w)) scores["love"] += 3;
            foreach (var w in sadW) if (allText.Contains(w)) scores["sad"] += 3;
            foreach (var w in adventW) if (allText.Contains(w)) scores["adventure"] += 2;
            foreach (var w in natureW) if (allText.Contains(w)) scores["nature"] += 1;
            foreach (var w in danceW) if (allText.Contains(w)) scores["dance"] += 2;
            foreach (var w in schoolW) if (allText.Contains(w)) scores["school"] += 2;
            foreach (var w in animalW) if (allText.Contains(w)) scores["animal"] += 2;
            foreach (var w in xmasW) if (allText.Contains(w)) scores["christmas"] += 4;
            foreach (var w in outdoorW) if (allText.Contains(w)) scores["outdoor"] += 2;
            foreach (var w in seasonsW) if (allText.Contains(w)) scores["seasons"] += 2;
            foreach (var w in healthW) if (allText.Contains(w)) scores["health"] += 2;

            string ctx = scores.OrderByDescending(kv => kv.Value).First().Key;
            string mood = ctx switch { "music" => "upbeat", "lullaby" => "calm", "sad" => "melancholy", "love" => "romantic", "party" => "excited", "adventure" => "energetic", "dance" => "upbeat", "christmas" => "joyful", "animal" => "playful", "school" => "curious", "nature" => "peaceful", "outdoor" => "happy", _ => "happy" };

            string season = "none";
            if (allText.Contains("prolec") || allText.Contains("spring")) season = "spring";
            else if (allText.Contains("jesen") || allText.Contains("autumn")) season = "autumn";
            else if (allText.Contains("zima") || allText.Contains("winter")) season = "winter";
            else if (allText.Contains("leto") || allText.Contains("summer")) season = "summer";
            else if (scores["seasons"] >= 2) season = "all";

            string themeByCtx = ctx switch
            {
                "music" => "Children's song about music",
                "lullaby" => "Lullaby",
                "party" => "Fun celebration",
                "love" => "Love song",
                "sad" => "Sad song",
                "adventure" => "Adventure",
                "nature" => "Nature and animals",
                "dance" => "Dance and rhythm",
                "school" => "School song",
                "animal" => "Animals",
                "christmas" => "Christmas song",
                "outdoor" => "Children's song about walking and playing",
                "health" => "Children's health and activity",
                _ => "Children's song"
            };

            return new SongAnalysis
            {
                Context = ctx,
                Mood = mood,
                Theme = themeByCtx,
                VisualStyle = ctx switch
                {
                    "music" => "colorful bright children joyful",
                    "outdoor" => "children park playground sunny green happy running",
                    "health" => "children active outdoor park sunny healthy",
                    _ => "warm colors soft light natural outdoor"
                },
                MainSubject = ctx switch
                {
                    "music" => "children singing playing music",
                    "outdoor" => "children running playing park playground",
                    "health" => "children active healthy outdoor",
                    _ => "children"
                },
                Season = season,
                Setting = ctx == "outdoor" || ctx == "seasons" || ctx == "health" ? "outdoors" : "mixed"
            };
        }

        // ── v1.4 DIRECTOR PASS ────────────────────────────────────────────────
        // Before: lyrics were sent 4 lines at a time with no shared plan, every scene was
        // planned in isolation (and the result was then mostly discarded by a filter bug and
        // asked for AGAIN, line by line, in the scene loop). Now the model first fixes ONE
        // visual identity for the whole video (main character, setting, palette) and plans all
        // lines against it, 16 lines per call, carrying the identity and the chorus queries
        // from batch to batch. Repeated lines (chorus) reuse the same query, so the video
        // has a recurring visual motif. The scene loop uses these queries directly (no
        // second Ollama call per line), which is also much faster.
        private async Task<List<SceneKeywords>> GenerateKeywordsPerLyric(List<string> lyrics, SongAnalysis analysis, CancellationToken ct)
        {
            var results = new List<SceneKeywords>();
            int total = lyrics.Count;

            LogToMainWindow($"🎬 Director pass: planning {total} lines with one visual identity...");

            int batchSize = 16;
            string identityJson = null;                                     // fixed after the first batch
            var chorusQueries = new Dictionary<string, string>(StringComparer.Ordinal); // normalized line → keywords
            for (int i = 0; i < total; i += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var batch = lyrics.Skip(i).Take(batchSize).ToList();
                int batchStart = i;

                string batchText = string.Join("\n", batch.Select((l, idx) => $"{batchStart + idx + 1}. {l}"));
                string fullSong = string.Join("\n", lyrics.Select((l, idx) => $"{idx + 1}. {l}"));

                string prompt =
                    "You are the DIRECTOR of a music video for a Serbian children's song (audience: children 2-6 years). " +
                    "The video is cut from stock footage (Pixabay/Pexels). Plan ONE video search query per lyric line so the " +
                    "whole video feels like ONE story, not random clips.\n\n" +
                    "Song theme: " + analysis.Theme + " | Mood: " + analysis.Mood + " | Style: " + analysis.VisualStyle +
                    " | Season: " + (analysis.Season ?? "none") + " | Setting: " + (analysis.Setting ?? "outdoors") + "\n\n" +
                    "WHOLE SONG (for context):\n" + fullSong + "\n\n" +
                    (identityJson == null
                        ? "STEP 1 - VISUAL IDENTITY: choose main_character (e.g. \"little girl with pigtails\"), setting (e.g. \"sunny park\") and palette (e.g. \"warm golden\"). Keep it simple and filmable.\n"
                        : "VISUAL IDENTITY (already chosen - keep it exactly): " + identityJson + "\n") +
                    "STEP 2 - for EACH line below write:\n" +
                    "- keywords: 3-6 English words for a stock VIDEO search: subject + action + place, concrete and filmable " +
                    "(e.g. \"girl eating ice cream park\"). Show the ACTION of the line. Use the main character in most lines " +
                    "so clips look like the same story. For feelings use a visual metaphor (\"my heart is smiling\" -> \"child laughing in sunlight\").\n" +
                    "- fallback_keywords: a simpler 2-4 word alternative.\n" +
                    "- ambient: the background sound of the scene in 2-3 English words (\"birds chirping\", \"children playing\", \"rain\", \"sea waves\", \"none\").\n" +
                    "RULES: a repeated line (chorus) gets EXACTLY the same keywords every time. No adults-only content, offices, alcohol, " +
                    "gyms, animation/cartoons, text, dark or scary scenes. Never write Serbian words in keywords.\n" +
                    // v1.4 (Gemini review): keep every shot a children's shot
                    "EVERY line's keywords contain a child (child, kid, girl, boy, toddler) - grandparents, mum or dad only TOGETHER " +
                    "with the child (\"grandmother walking with granddaughter park\", never \"elderly couple\"). " +
                    "When a line names a thing (ice cream, hot chocolate, tea, scooter, ball, dog), show the CHILD WITH THAT THING, " +
                    "not the season or the place (\"zimi topli čaj\" -> \"child drinking hot chocolate mug\", not \"snowy mountain\"). " +
                    "Never: hospitals, wheelchairs, therapy, construction, road works, warning signs, crowds, aerial shots of people, " +
                    "coffee makers, ski lifts.\n\n" +
                    "LINES TO PLAN NOW:\n" + batchText + "\n\n" +
                    "Respond ONLY with JSON, no markdown:\n" +
                    "{\"identity\":{\"main_character\":\"...\",\"setting\":\"...\",\"palette\":\"...\"},\"lines\":[" +
                    "{\"line\":" + (batchStart + 1) + ",\"keywords\":\"...\",\"fallback_keywords\":\"...\",\"ambient\":\"...\"}]}";

                try
                {
                    AnnounceToUser(LF("ai_analyzing_lyrics", i + 1, Math.Min(i + batchSize, total)), 5 + (i * 20 / total));
                    string response = await _ollama.GenerateAsync(prompt, model: OllamaClient.QueryModel, ct: ct);

                    // v1.4: the director answers with {"identity":{...},"lines":[...]};
                    // a bare array (older style answer) is still accepted.
                    List<SceneKeywords> batchResults = null;
                    string objStr = ExtractJson(response);
                    if (!string.IsNullOrEmpty(objStr) && objStr.TrimStart().StartsWith("{"))
                    {
                        try
                        {
                            var obj = JObject.Parse(objStr);
                            if (identityJson == null && obj["identity"] is JObject idObj && idObj.HasValues)
                            {
                                identityJson = idObj.ToString(Newtonsoft.Json.Formatting.None);
                                LogToMainWindow($"🎭 Visual identity: {identityJson}");
                            }
                            if (obj["lines"] is JArray linesArr)
                                batchResults = linesArr.ToObject<List<SceneKeywords>>();
                        }
                        catch (Exception exParse) { LogToMainWindow($"⚠️ Director JSON parse: {exParse.Message}"); }
                    }
                    if (batchResults == null)
                    {
                        string arrStr = ExtractJson(response, isArray: true);
                        if (!string.IsNullOrEmpty(arrStr))
                            batchResults = Newtonsoft.Json.JsonConvert.DeserializeObject<List<SceneKeywords>>(arrStr);
                    }

                    {
                        if (batchResults != null && batchResults.Count > 0)
                        {
                            foreach (var r in batchResults)
                            {
                                bool isTemplate = string.IsNullOrEmpty(r.Keywords) ||
                                    r.Keywords.Contains("konkretna akcija") ||
                                    r.Keywords.Contains("subjekt + detalj") ||
                                    r.Keywords == "..." ||
                                    r.Keywords.Length < 5;

                                bool hasCyrillic = r.Keywords?.Any(c => c > 0x400 && c < 0x500) ?? false;
                                // FIX (v1.4): this used to test for the plain letters s, d, j, c, z —
                                // almost every English query contains one of them, so Ollama's
                                // keywords were thrown away and replaced by the fallback nearly always.
                                // The intent is to catch Serbian words, i.e. letters with diacritics.
                                bool hasLatin = r.Keywords?.Any(c => "šđčćžŠĐČĆŽ".Contains(c)) ?? false;

                                var genericPhrases = new[] { "happy child", "child playing", "children playing", "warm colors", "soft light" };
                                bool isTooGeneric = genericPhrases.Any(p => r.Keywords?.ToLower() == p);

                                var adultKeywords = new[] { "coffee", "beer", "wine", "whiskey", "alcohol", "office",
                                    "business", "suit", "tie", "laptop", "computer", "meeting", "corporate",
                                    "cigarette", "smoking", "bar ", "cocktail", "nightclub", "adult",
                                    "toad", "frog", "frog closeup", "reptile", "insect closeup",
                                    "rush hour", "busy street", "commute", "crowd city",
                                    "stock market", "finance", "real estate",
                                    "marble floor", "corridor", "hallway", "atrium", "lobby",
                                    "indoor walk", "shopping mall", "airport terminal",
                                    "black and white", "monochrome", "silhouette", "animation",
                                    "cartoon", "sketch", "drawing", "illustrated",
                                    "dark alley", "abandoned", "horror", "scary",
                                    "cemetery", "graveyard", "funeral" };
                                bool hasAdultContent = adultKeywords.Any(w => r.Keywords?.ToLower().Contains(w) ?? false);

                                // Contextual bans — prevents fireplace/bacon in health/outdoor songs
                                var contextForbidden = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) {
                                    { "health",   new[] { "fireplace", "campfire indoor", "cooking", "frying", "kitchen", "bacon", "candle warm", "chimney", "living room cozy", "hot drink" } },
                                    { "outdoor",  new[] { "fireplace", "indoor cozy", "kitchen", "cooking", "living room" } },
                                    { "nature",   new[] { "fireplace", "kitchen", "cooking", "indoor cozy", "living room" } },
                                    { "music",    new[] { "fireplace", "kitchen", "cooking" } },
                                    { "dance",    new[] { "fireplace", "sleeping", "bedroom cozy" } },
                                    { "lullaby",  new[] { "running fast", "jumping crowd", "party crowd", "loud energetic crowd" } },
                                    { "sad",      new[] { "party", "celebration confetti", "birthday crowd", "crowd cheering" } },
                                };
                                bool hasContextForbidden = false;
                                if (analysis?.Context != null && contextForbidden.TryGetValue(analysis.Context, out var ctxBad))
                                    hasContextForbidden = ctxBad.Any(w => r.Keywords?.ToLower().Contains(w) ?? false);

                                bool rejected = isTemplate || hasCyrillic || hasLatin || isTooGeneric || hasAdultContent || hasContextForbidden;
                                r.FromAI = !rejected;
                                if (rejected)
                                {
                                    if (hasAdultContent)
                                        LogToMainWindow(LF("ai_bad_keywords", r.Line, r.Keywords));
                                    if (hasContextForbidden)
                                        LogToMainWindow($"🚫 Contextually forbidden keyword (ctx={analysis?.Context}): {r.Keywords}");
                                    int lyricIdx = r.Line - batchStart - 1;
                                    if (lyricIdx >= 0 && lyricIdx < batch.Count)
                                    {
                                        r.Keywords = GenerateKeywordsFromLyric(batch[lyricIdx], analysis);
                                        r.Ambient = InferAmbientFromLyric(batch[lyricIdx], analysis.Context);
                                        LogToMainWindow(LF("ai_unusable_keywords", r.Line));
                                    }
                                }
                            }
                            // Chorus consistency: the same lyric line always gets the same query
                            foreach (var r in batchResults)
                            {
                                int li = r.Line - 1;
                                if (li < 0 || li >= lyrics.Count || string.IsNullOrWhiteSpace(r.Keywords)) continue;
                                string key = SerbianText.Normalize(lyrics[li]);
                                if (chorusQueries.TryGetValue(key, out string firstQuery))
                                    r.Keywords = firstQuery;
                                else if (r.FromAI)
                                    chorusQueries[key] = r.Keywords;
                            }
                            // Keep only lines of this batch, once each (the model sometimes repeats or invents lines)
                            var accepted = batchResults
                                .Where(r => r.Line >= batchStart + 1 && r.Line <= batchStart + batch.Count)
                                .GroupBy(r => r.Line).Select(g => g.First())
                                .ToList();
                            results.AddRange(accepted);
                            foreach (var r in accepted)
                                LogToMainWindow($"   Line {r.Line}: action='{r.Keywords}', sound='{r.Ambient}'{(r.FromAI ? "" : " (fallback)")}");
                            // Lines the model skipped get the rule-based fallback
                            for (int idx = 0; idx < batch.Count; idx++)
                            {
                                int lineNo = batchStart + idx + 1;
                                if (accepted.Any(r => r.Line == lineNo)) continue;
                                results.Add(new SceneKeywords
                                {
                                    Line = lineNo,
                                    Keywords = GenerateKeywordsFromLyric(batch[idx], analysis),
                                    Ambient = InferAmbientFromLyric(batch[idx], analysis.Context)
                                });
                            }
                            continue;
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogToMainWindow(LF("ai_keywords_error", ex.Message));
                }

                foreach (var (lyric, idx) in batch.Select((l, idx) => (l, idx)))
                {
                    results.Add(new SceneKeywords
                    {
                        Line = batchStart + idx + 1,
                        Keywords = GenerateKeywordsFromLyric(lyric, analysis),
                        Ambient = InferAmbientFromLyric(lyric, analysis.Context)
                    });
                }
            }

            while (results.Count < total)
            {
                int missing = results.Count;
                results.Add(new SceneKeywords
                {
                    Line = missing + 1,
                    Keywords = GenerateKeywordsFromLyric(lyrics[missing], analysis),
                    Ambient = InferAmbientFromLyric(lyrics[missing], analysis.Context)
                });
            }

            return results;
        }

        private List<string> GenerateInstrumentalSceneDescriptions(int count, double totalDuration, string context = "fun", string mood = "happy", string visualStyle = "")
        {
            string[] templates;

            switch (context)
            {
                case "music":
                    templates = new[]
                    {
                        "children singing together choir joyful colorful",
                        "child playing piano happy lesson bright",
                        "kids clapping hands singing music fun",
                        "children dancing music classroom colorful",
                        "girl boy playing guitar together happy",
                        "child headphones listening music happy bedroom",
                        "children instruments school band performance",
                        "kids music class teacher colorful fun",
                        "child microphone singing stage joyful",
                        "children choir singing colorful concert",
                        "boy girl dancing music living room fun",
                        "children music notes colorful animation singing",
                    };
                    break;

                case "lullaby":
                    templates = new[]
                    {
                        "moonlight soft glow peaceful night",
                        "baby sleeping peaceful soft light",
                        "stars twinkling night sky gentle",
                        "clouds floating slow soft colors",
                        "mother rocking baby gentle warm",
                        "soft toys bedroom cozy night light",
                        "candle flickering soft warm glow",
                        "teddy bear child sleeping peaceful",
                    };
                    break;

                case "party":
                    templates = new[]
                    {
                        "confetti colorful falling celebration",
                        "children dancing party colorful clothes",
                        "balloons colorful floating celebration",
                        "birthday cake candles children cheering",
                        "children jumping happy excited party",
                        "colorful lights bokeh celebration",
                        "kids laughing party fun together",
                        "streamers colorful festive celebration",
                    };
                    break;

                case "love":
                    templates = new[]
                    {
                        "heart shapes colorful romantic warm",
                        "family hugging together warm golden light",
                        "children holding hands friendship",
                        "flowers blooming colorful garden",
                        "family together park sunset warm golden light",
                        "mother child hugging tender moment",
                        "golden hour sunlight warm glow",
                    };
                    break;

                case "nature":
                    templates = new[]
                    {
                        "forest sunlight through trees peaceful",
                        "butterfly flower garden colorful",
                        "river flowing peaceful nature sounds",
                        "birds flying sky freedom",
                        "flowers blooming spring garden",
                        "children exploring nature curious",
                        "rainbow after rain colorful sky",
                        "meadow green sunny peaceful",
                    };
                    break;

                case "animal":
                    templates = new[]
                    {
                        "cute animals farm happy sunny",
                        "dog playing child happy outdoor",
                        "cat curious playful sunny window",
                        "birds colorful singing branch",
                        "rabbit hopping green meadow",
                        "children petting animals farm",
                        "ducks pond water splashing",
                        "horse running field beautiful",
                    };
                    break;

                case "dance":
                    templates = new[]
                    {
                        "children dancing joyful colorful",
                        "feet dancing floor rhythmic",
                        "colorful dance performance stage",
                        "kids moving dancing happy energetic",
                        "dance studio children learning",
                        "spinning twirling colorful dress",
                        "group dance children smiling",
                    };
                    break;

                case "adventure":
                    templates = new[]
                    {
                        "children exploring forest adventure",
                        "mountain landscape sunrise dramatic",
                        "hiking trail nature adventure",
                        "children running open field free",
                        "map compass adventure exploring",
                        "treehouse children playing adventure",
                        "bicycle riding path outdoor adventure",
                    };
                    break;

                case "sad":
                    templates = new[]
                    {
                        "rain window drops melancholy",
                        "autumn leaves falling slow motion",
                        "foggy morning misty peaceful",
                        "candle flame flickering quiet",
                        "empty swing park gentle wind",
                        "cloudy sky peaceful grey tones",
                    };
                    break;

                case "school":
                    templates = new[]
                    {
                        "children classroom learning happy",
                        "books colorful school supplies",
                        "children raising hands class eager",
                        "school playground children playing",
                        "pencils crayons colorful drawing",
                        "children reading books curious",
                        "teacher children classroom warm",
                    };
                    break;

                case "christmas":
                    templates = new[]
                    {
                        "christmas tree lights colorful glowing",
                        "snow falling winter peaceful",
                        "children opening gifts excited christmas",
                        "fireplace warm cozy winter",
                        "snowflakes falling slow motion",
                        "family christmas dinner warm lights",
                        "santa claus gifts children happy",
                    };
                    break;

                default:
                    templates = new[]
                    {
                        "children running park sunny happy",
                        "family walking nature together",
                        "child laughing playing outdoor",
                        "friends sitting together outside",
                        "children jumping dancing joyful",
                        "sunlight through leaves warm",
                        "child riding bicycle street",
                        "family picnic grass sunny",
                        "children playground happy",
                        "grandparents child park walking",
                        "child chasing butterfly garden",
                        "friends playing ball together",
                        "child eating ice cream summer",
                        "family watching sunset together",
                        "children building sand castle beach",
                        "laughing children running camera",
                        "child feeding ducks river",
                        "family picking fruit garden",
                        "children playing autumn leaves",
                        "child painting drawing colorful",
                    };
                    break;
            }

            var result = new List<string>();
            for (int i = 0; i < count; i++)
                result.Add(templates[i % templates.Length]);

            LogToMainWindow($"🎵 Instrumental scene ({count} frames) — context: '{context}', mood: '{mood}'");
            return result;
        }

        // ============================================================
        //  BuildLiteralSearchQuery — ISKRA PATCH v2.0
        //  1. AGGRESSION LOCK   — wolf, growling, angry, scary, fear, predator, aggressive
        //  2. SEASONAL LOCK     — snow, winter, ice, frost, cold
        //  3. DOMESTIC ANIMAL FILTER — cow, livestock, moose, deer, wildlife
        //  4. CHILD PLAY BOOST  — prefix: "happy children playing ... playground kids fun"
        //  5. EMPTY SCENERY BAN — empty, lonely, static, mountain peak, desert
        // ============================================================
        private string BuildLiteralSearchQuery(string keywords, string action, string energyBoost, string style)
        {
            var srToEn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                {"automobil","car"}, {"auto","car"}, {"kola","car"}, {"vozilo","car"},
                {"kamion","truck"}, {"bus","bus"}, {"autobus","bus"}, {"voz","train"},
                {"bicikl","bicycle"}, {"motor","motorcycle"}, {"avion","airplane"},

                {"pas","dog"}, {"macka","cat"}, {"patka","duck"},
                {"veverica","squirrel"}, {"zec","rabbit"}, {"ptica","bird"},
                {"riba","fish"}, {"konj","horse"},

                // PATCH 3 — Domestic Animal Override: wild/farm animals → dog
                {"krava","dog"}, {"ovca","dog"}, {"koza","dog"},
                {"vuk","dog"}, {"medved","dog"}, {"lisica","dog"},
                {"jelen","dog"}, {"los","dog"},

                {"park","park"}, {"parkic","park"}, {"suma","forest"},
                {"plaza","beach"}, {"more","sea"}, {"reka","river"}, {"rijeka","river"},
                // VIBE CHECK: mountain → garden, meadow → garden, yard → playground
                {"planina","garden"}, {"livada","garden"}, {"basta","garden"},
                {"dvoriste","playground"}, {"ulica","street"}, {"grad","city"},
                {"skola","school"}, {"kuca","house"}, {"dom","home"},

                {"sladoled","ice cream"}, {"torta","cake"}, {"cokolada","chocolate"},
                {"jabuka","apple"}, {"banana","banana"}, {"hleb","bread"},
                {"caj","tea"}, {"sok","juice"}, {"mleko","milk"},

                // ACTION BIAS: smeje → smiling (toplije od laughing)
                {"trci","running"}, {"trcanje","running"}, {"seta","walking"},
                {"setaj","walking"}, {"setnja","walking"}, {"skace","jumping"},
                {"igra","playing"}, {"plese","dancing"}, {"peva","singing"},
                {"cita","reading"}, {"crta","drawing"}, {"slika","painting"},
                {"spava","sleeping"}, {"jede","eating"}, {"pije","drinking"},
                {"pliva","swimming"}, {"vozi","riding"}, {"nosi","carrying"},
                {"grli","hugging"}, {"smeje","smiling"}, {"place","crying"},

                // v1.4: "prolece" was listed twice. A duplicate key in this initializer throws
                // "An item with the same key has already been added" the first time a scene
                // needs a literal query (e.g. winter lines with clothing words), which stopped
                // the whole video. Winter words now mean winter (see the season lock below).
                {"prolece","spring"}, {"proljece","spring"},
                {"leto","summer"}, {"ljeto","summer"}, {"jesen","autumn"},
                {"zima","winter"}, {"sneg","snow"}, {"snijeg","snow"},
                {"kisa","rain"}, {"sunce","sun"}, {"vetar","wind"}, {"vjetar","wind"},

                {"lopta","ball"}, {"igracka","toy"}, {"lutka","doll"},
                {"knjiga","book"}, {"olovka","pencil"}, {"torba","bag"},
                {"kapa","hat"}, {"cizme","boots"}, {"rukavice","gloves"},
                {"sal","scarf"}, {"kaput","coat"}, {"haljina","dress"},

                {"sretan","happy"}, {"srecan","happy"}, {"vesel","joyful"},
                {"tuzan","sad"}, {"ljut","angry"}, {"uplasen","scared"},
                {"zdravo","healthy"}, {"jako","strong"}, {"malo","little"},
                {"veliko","big"}, {"lepo","beautiful"}, {"lijepo","beautiful"},
            };

            // STRICT NEGATIVE CONSTRAINTS
            var excludedTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // === ORIGINAL ===
                "cow", "livestock", "farm", "wilderness",
                "barren", "remote", "uninhabited", "desolate",

                // === PATCH 1: Aggression & Fear Lock ===
                "wolf", "wolves", "growling", "growl", "angry", "anger",
                "scary", "scare", "fear", "fearful", "predator",
                "aggressive", "aggression", "attack", "threat", "wild",
                "roaring", "roar", "fierce", "vicious", "danger", "dangerous",

                // === PATCH 2: Seasonal Lock ===
                "snow", "snowy", "winter", "ice", "icy", "frost", "frosty",
                "cold", "freeze", "frozen", "blizzard", "snowfall", "sleet",
                "hail", "snowflake", "snowflakes",

                // === PATCH 3: Domestic Animal Filter ===
                "moose", "deer", "elk", "bison", "buffalo", "wildlife",

                // === PATCH 5: Empty Scenery Ban ===
                "peak", "mountaintop", "summit", "mountain",
                "desert", "static", "empty", "lonely",
                "abandoned", "ghost", "wasteland", "void",

                // === PATCH 6: Gym/Fitness Ban (surgical — bodybuilder only) ===
                "gym", "fitness", "workout", "kettlebell", "bodybuilding",
                "weightlifting", "dumbbell", "barbell", "crossfit",

                // === PATCH 8: Kids Edition — Adult Content Vibe Filter ===
                // Pregnancy / medical anatomical shots
                "pregnant", "pregnancy", "maternity", "prenatal", "belly", "bump",
                "ultrasound", "embryo", "fetus", "womb", "obstetric",
                // Teretana — dodatni izrazi
                "treadmill", "pushup", "situp", "plank", "squat", "lunge",
                "deadlift", "pullup", "chinup", "burpee",
                "hiit", "cardio", "spinning", "aerobic",
                // Traffic / urban crowds
                "traffic", "highway", "freeway", "motorway", "intersection",
                "commute", "rush", "crowded", "subway", "metro",
                // Clinical / medical
                "hospital", "clinic", "surgery", "medical", "doctor", "nurse",
                "injection", "syringe", "pill", "medicine", "pharmacy",
                // Too adult / abstract
                "corporate", "business", "meeting", "conference",
                "dating", "romance", "intimate", "adult",

                // === PATCH 9: Anomaly-Fix "Kindergarten Precision" ===
                // ANOMALIJA 2: Tech/Audio oprema — gramofon, mikset, studio
                "gramophone", "turntable", "vinyl", "record player", "vinyl record",
                "audio mixer", "mixing board", "sound board", "mixing console",
                "studio equipment", "audio equipment", "music studio", "recording studio",
                "dj equipment", "dj console", "dj mixer", "dj booth",
                "headphones professional", "microphone studio", "speaker system",
                "amplifier", "synthesizer", "equalizer", "audio interface",

                // === PATCH 11: Extended gym blacklist — catching all Pixabay tag variants ===
                // Kettlebell specific (Pixabay uses various variants)
                "girja", "girje", "tegovi", "teg", "bucica", "bucice",
                "kettlebell swing", "kettlebell workout", "kettlebell training",
                "dumbbell workout", "dumbbell exercise", "weight training",
                "strength training", "muscle training", "gym training",
                "fitness training", "exercise", "exercising", "lifting weights",
                "sports equipment", "workout equipment", "gym equipment",
                "athletic training", "physical training", "body workout",
                "sport exercise", "fitness exercise", "health exercise",
                // Anything that sounds like "exercise" but is not children's play
                "jogging adult", "running adult", "cycling adult", "swimming adult",
                "sport adult", "athlete", "athletics", "sportsman", "sportswoman",
            };

            // VIBE CHECK: replace generic "nature" with "park" in input strings
            keywords = System.Text.RegularExpressions.Regex.Replace(keywords ?? "", @"\bnature\b", "park", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            style = System.Text.RegularExpressions.Regex.Replace(style ?? "", @"\bnature\b", "garden", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // PATCH 5: zameni "mountain" sa "park" pre tokenizacije
            keywords = System.Text.RegularExpressions.Regex.Replace(keywords, @"\bmountain\b", "park", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var parts = new[] { keywords, action, energyBoost, style }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .SelectMany(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .ToList();

            var englishWords = new List<string>();
            foreach (var word in parts)
            {
                string clean = System.Text.RegularExpressions.Regex.Replace(word, @"[^a-zA-Z\-]", "");
                if (string.IsNullOrEmpty(clean)) continue;

                if (srToEn.TryGetValue(clean, out string translated))
                {
                    if (!englishWords.Contains(translated))
                        englishWords.Add(translated);
                }
                else
                {
                    bool isEnglish = clean.All(c => c < 128 && char.IsLetter(c));
                    if (isEnglish && !englishWords.Contains(clean.ToLower()))
                        englishWords.Add(clean.ToLower());
                }
            }

            // v1.4: the old "seasonal lock" removed snow/winter/cold from EVERY literal query, so a
            // line like "Kad je zima, nosi čizme... na velikom snegu uživaj" got summer clips.
            // When the current line is a winter line, winter words are allowed again.
            if (string.Equals(_currentSeason, "winter", StringComparison.OrdinalIgnoreCase))
                foreach (string w in new[] { "snow", "snowy", "winter", "ice", "icy", "frost", "frosty", "cold", "snowfall", "snowflake", "snowflakes" })
                    excludedTerms.Remove(w);

            // STRICT NEGATIVE FILTERING: ukloni excluded termine iz query-ja
            englishWords.RemoveAll(w =>
                excludedTerms.Any(ex => string.Equals(w, ex, StringComparison.OrdinalIgnoreCase)));

            var styleNoise = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "warm","colors","colour","soft","light","happy","atmosphere","child","friendly",
                "bright","colorful","colourful","beautiful","nice","good","calm","peaceful",
                "cheerful","uplifting","gentle","cozy","cosy"
            };

            var coreWords = englishWords.Where(w => !styleNoise.Contains(w)).Take(4).ToList();
            var styleWords = englishWords.Where(w => styleNoise.Contains(w)).Take(1).ToList();

            if (coreWords.Count == 0)
                coreWords = englishWords.Take(4).ToList();

            var finalWords = coreWords.Count < 3
                ? coreWords.Concat(styleWords).ToList()
                : coreWords;

            // MANDATORY HUMAN ELEMENT: ako nema ljudskog elementa, dodaj ga
            bool hasHuman = finalWords.Any(w => w is "child" or "children" or "person" or "people"
                or "family" or "baby" or "mother" or "father" or "girl" or "boy"
                or "kids" or "toddler" or "woman" or "man" or "grandmother" or "grandfather");

            // ACTION BIAS: ako nema glagola pokreta, dodaj "playing"
            bool hasAction = finalWords.Any(w => w is "playing" or "running" or "walking" or "smiling"
                or "laughing" or "dancing" or "jumping" or "singing" or "hugging");

            // GEO TAG: slavic/balkan/european kontekst
            bool hasGeo = finalWords.Any(w => w is "slavic" or "balkan" or "european" or "eastern");

            var append = new List<string>();
            if (!hasHuman) append.Add("children");
            if (!hasAction) append.Add("playing");
            if (!hasGeo) append.Add("european");

            if (append.Count > 0)
                finalWords.AddRange(append);

            // ─────────────────────────────────────────────────────────────
            // PATCH 4 — CHILD PLAY BOOST (niece's feedback)
            //   Svaki query: "happy children playing <kontekst> playground kids fun"
            // ─────────────────────────────────────────────────────────────
            const string CHILD_PLAY_PREFIX = "happy children playing";
            const string CHILD_PLAY_BOOST = "playground kids fun";

            string baseQuery;

            if (finalWords.Count == 0)
            {
                baseQuery = $"{CHILD_PLAY_PREFIX} {CHILD_PLAY_BOOST}";
            }
            else
            {
                var prefixWords = new HashSet<string>(
                    CHILD_PLAY_PREFIX.Split(' '), StringComparer.OrdinalIgnoreCase);
                var uniqueCore = finalWords
                    .Where(w => !prefixWords.Contains(w))
                    .Take(3)
                    .ToList();

                baseQuery = uniqueCore.Count > 0
                    ? $"{CHILD_PLAY_PREFIX} {string.Join(" ", uniqueCore)} {CHILD_PLAY_BOOST}"
                    : $"{CHILD_PLAY_PREFIX} {CHILD_PLAY_BOOST}";
            }

            string contextSuffix = _detectedContext switch
            {
                "wedding" or "love" or "romantic" => " bokeh shallow depth",
                "documentary" or "news" => " professional cinematic",
                "sad" or "melancholy" => " cinematic dramatic",
                "adventure" or "sport" or "action" => " action dynamic",
                "children" or "lullaby" or "fun" => "",
                _ => ""
            };

            return baseQuery + contextSuffix;
        }


        private string GenerateKeywordsFromLyric(string lyric, SongAnalysis analysis)
        {
            string lower = StrictQueryEngine.Fold(lyric);   // v1.4: "čaj" and "caj" alike

            // ── KORAK 0: Subject Hierarchy — simbolika vs. bukvalnost ─────────
            // Detection of phrases where the noun is a DETAIL of clothing/object, not the primary subject
            bool isClothingDetail = (lower.Contains("na majici") || lower.Contains("na dresu") ||
                                     lower.Contains("na jakni") || lower.Contains("na kaputu") ||
                                     lower.Contains("na sesiru") || lower.Contains("on shirt") ||
                                     lower.Contains("on jacket") || lower.Contains("on hat"));
            if (isClothingDetail)
                return "child wearing colorful outfit graphic tee clothing detail candid";

            // Vizuelna metafora — apstraktne emotivne fraze → vizuelni ekvivalent emocije
            bool isAbstractEmotion = (lower.Contains("srce mi se") || lower.Contains("dusa mi") ||
                                       lower.Contains("heart is") || lower.Contains("soul is") ||
                                       lower.Contains("lako mi je") || lower.Contains("tesko mi") ||
                                       lower.Contains("sve je lijepo") || lower.Contains("sve je lepo") ||
                                       lower.Contains("svijetlim") || lower.Contains("svetlim") ||
                                       lower.Contains("sjajim") || lower.Contains("lebdim"));
            if (isAbstractEmotion)
            {
                string mood = analysis?.Mood ?? "happy";
                return mood switch
                {
                    "happy" or "joyful" or "playful" => "child laughing arms open sunlight freedom joy candid",
                    "calm" or "peaceful" => "child peaceful meadow soft light serene candid",
                    "excited" or "energetic" => "child jumping spinning outdoor sunny candid",
                    _ => "child smiling golden hour park warm candid"
                };
            }

            // Action Priority — glagol radnje kao dominantni element
            // "gledam u nebo" → osoba koja gleda, ne samo nebo
            if ((lower.Contains("gledam") || lower.Contains("gledas") || lower.Contains("looking")) &&
                (lower.Contains("nebo") || lower.Contains("sky") || lower.Contains("zvijezde") || lower.Contains("stars")))
                return "child looking up sky wonder outdoor candid";
            if ((lower.Contains("trcim") || lower.Contains("trcis") || lower.Contains("trce") || lower.Contains("running")) &&
                !lower.Contains("auto") && !lower.Contains("bicikl"))
                return "child running outdoor meadow candid natural";
            if (lower.Contains("skacem") || lower.Contains("skaci") || lower.Contains("skacemo") || lower.Contains("jumping"))
                return "child jumping outdoor joyful candid natural";
            if (lower.Contains("plasem") || lower.Contains("plesati") || lower.Contains("plasemo") || lower.Contains("dancing"))
                return "child dancing outdoor joyful movement candid";
            if (lower.Contains("pjevam") || lower.Contains("pjevamo") || lower.Contains("singing"))
                return "child singing joyful outdoor candid natural";
            // ─────────────────────────────────────────────────────────────────

            // ── Seasonal logic — if the lyric mentions a season, we force seasonal shots
            if (lower.Contains("zima") || lower.Contains("zimska") || lower.Contains("zimski") ||
                lower.Contains("winter") || lower.Contains("sneg") || lower.Contains("snijeg") ||
                lower.Contains("mraz") || lower.Contains("led "))
                return "children winter snow playing outdoor cozy warm";

            if (lower.Contains("prolece") || lower.Contains("prolece") || lower.Contains("proljece") ||
                lower.Contains("spring") || lower.Contains("cvet") || lower.Contains("cvijet") ||
                lower.Contains("cveta") || lower.Contains("cvjeta"))
                return "children spring park flowers blooming sunny";

            if (lower.Contains("leto") || lower.Contains("ljeto") || lower.Contains("ljetna") ||
                lower.Contains("letnja") || lower.Contains("summer") || lower.Contains("sunce") ||
                lower.Contains("plaza"))
                return "children summer sunny outdoor playing beach";

            if (lower.Contains("jesen") || lower.Contains("jesenja") || lower.Contains("jesenji") ||
                lower.Contains("autumn") || lower.Contains("fall ") || lower.Contains("lisce") ||
                lower.Contains("lisce"))
                return "children autumn leaves park golden colors";
            if (lower.Contains("automobil") || lower.Contains(" auto ") || lower.Contains("kolima") ||
                lower.Contains("autic") || lower.Contains("kola ") || lower.Contains(" kola"))
                return "car driving street";
            if (lower.Contains("bicikl") || lower.Contains("biciklo") || lower.Contains("biciklu"))
                return "child riding bicycle park";
            if (lower.Contains("trotinet") || lower.Contains("romobil"))
                return "child riding scooter street";
            if (lower.Contains("kamion") || lower.Contains("kamionic"))
                return "truck driving road";
            if (lower.Contains("avion") || lower.Contains("avionic") || lower.Contains("helikopter"))
                return "airplane flying sky clouds";
            if (lower.Contains("voz") || lower.Contains("vozic") || lower.Contains("lokomot"))
                return "train railway station";
            if (lower.Contains("brod") || lower.Contains("camac") || lower.Contains("jedrilica") || lower.Contains("barka"))
                return "boat sailing water";
            if (lower.Contains("traktor") || lower.Contains("kombajn"))
                return "tractor farm field";
            if (lower.Contains("motor") || lower.Contains("motocikl"))
                return "motorcycle driving road";
            if (lower.Contains("autobus") || lower.Contains("trollej"))
                return "bus city street children";
            if (lower.Contains("raketa") || lower.Contains("svemirsk") || lower.Contains("svemir"))
                return "rocket space stars universe";
            if (lower.Contains("tenkic") || lower.Contains("tenk"))
                return "toy tank children playing";
            if (lower.Contains("vatrogasn") || lower.Contains("vatrogasac"))
                return "fire truck firefighter action";
            if (lower.Contains("policij") || lower.Contains("policajac"))
                return "police car city";
            if (lower.Contains("ambulant") || lower.Contains("hitna"))
                return "ambulance medical help";
            if (lower.Contains("skuter"))
                return "scooter riding street";

            if (lower.Contains(" pas ") || lower.Contains("psa ") || lower.Contains("psic") ||
                lower.Contains("stenad") || lower.Contains("stene") || lower.Contains("psu "))
                return "dog playing happy";
            if (lower.Contains("macka") || lower.Contains("maca") || lower.Contains("macic") ||
                lower.Contains("mace") || lower.Contains("macu"))
                return "cat cute kitten";
            if (lower.Contains("konj") || lower.Contains("kobila") || lower.Contains("zdreb"))
                return "horse running field";
            if (lower.Contains("krava") || lower.Contains("telic") || lower.Contains("tele"))
                return "cow farm meadow";
            if (lower.Contains("ovca") || lower.Contains("ovce") || lower.Contains("jagnje"))
                return "sheep meadow farm";
            if (lower.Contains("svinja") || lower.Contains("prasic") || lower.Contains("prase"))
                return "pig farm cute";
            if (lower.Contains("kokos") || lower.Contains("pilic") || lower.Contains("pile"))
                return "chicken farm chick cute";
            if (lower.Contains("patka") || lower.Contains("pacic"))
                return "duck pond water cute";
            if (lower.Contains("zec") || lower.Contains("kunic") || lower.Contains("zecic"))
                return "rabbit cute bunny";
            if (lower.Contains("mis") || lower.Contains("hrcak"))
                return "mouse hamster cute small animal";
            if (lower.Contains("papagaj") || lower.Contains("papigica"))
                return "parrot colorful bird";

            if (lower.Contains("lav") || lower.Contains("lavic") || lower.Contains("lavica"))
                return "lion savanna wild";
            if (lower.Contains("slon") || lower.Contains("slonic"))
                return "elephant nature wild";
            if (lower.Contains("medved") || lower.Contains("medvjed") || lower.Contains("medvedic"))
                return "bear forest nature";
            if (lower.Contains("leptir") || lower.Contains("leptiric"))
                return "butterfly flower garden";
            if (lower.Contains("pcela") || lower.Contains("bumbara") || lower.Contains("bumbar"))
                return "bee flower honey garden";
            if (lower.Contains("riba") || lower.Contains("ribica") || lower.Contains("ribe"))
                return "fish swimming water aquarium";
            if (lower.Contains("delfin"))
                return "dolphin ocean jumping";
            if (lower.Contains("kit"))
                return "whale ocean water";
            if (lower.Contains("kornjaca"))
                return "turtle slow nature";
            if (lower.Contains("zmija"))
                return "snake nature grass";
            if (lower.Contains("tigar") || lower.Contains("tigric"))
                return "tiger wild jungle";
            if (lower.Contains("gepard") || lower.Contains("leopard"))
                return "cheetah running fast wild";
            if (lower.Contains("majmun") || lower.Contains("majmunica"))
                return "monkey jungle climbing";
            if (lower.Contains("zirafa"))
                return "giraffe savanna tall";
            if (lower.Contains("kengur"))
                return "kangaroo australia jumping";
            if (lower.Contains("pingvin"))
                return "penguin ice cute";
            if (lower.Contains("polarni medved") || lower.Contains("polarni"))
                return "polar bear arctic snow";
            if (lower.Contains("lisica") || lower.Contains("lisicica"))
                return "fox forest cute";
            if (lower.Contains("vuk") || lower.Contains("vuce"))
                return "wolf forest nature";
            if (lower.Contains("jelen") || lower.Contains("srna"))
                return "deer forest nature";
            if (lower.Contains("jazavac") || lower.Contains("vjeverica") || lower.Contains("veverica"))
                return "squirrel forest tree";
            if (lower.Contains("krokodil") || lower.Contains("aligator"))
                return "crocodile water wild";
            if (lower.Contains("nilski konj") || lower.Contains("nilskog"))
                return "hippo water wildlife";
            if (lower.Contains("zebra"))
                return "zebra savanna africa";
            if (lower.Contains("nosorog"))
                return "rhino savanna wildlife";

            if (lower.Contains("ptic") || lower.Contains("vrabac") || lower.Contains("lastavic") ||
                lower.Contains("golub") || lower.Contains("sova") || lower.Contains("orao") ||
                lower.Contains("soko") || lower.Contains("kos ") || lower.Contains("slavuj") ||
                lower.Contains("cvorak") || lower.Contains("roda") || lower.Contains("caplja"))
                return "birds flying sky nature";
            if (lower.Contains("papagaj"))
                return "parrot colorful tropical";

            if (lower.Contains("mama") && lower.Contains("tata")) return "family parents child walking";
            if (lower.Contains("mama") || lower.Contains("majka") || lower.Contains("majko"))
                return "mother child hug love";
            if (lower.Contains("tata") || lower.Contains("otac") || lower.Contains("oce"))
                return "father child playing outdoor";
            if (lower.Contains("baka") || lower.Contains("nana") || lower.Contains("baba"))
                return "grandmother grandchild hugging park warm tender elderly child";
            if (lower.Contains("deka") || lower.Contains("deda") || lower.Contains("djed"))
                return "grandfather grandchild playing park outdoor elderly child happy";
            if (lower.Contains("brat ") || lower.Contains("brate") || lower.Contains("bratic"))
                return "siblings brothers playing";
            if (lower.Contains("sestra") || lower.Contains("sestrica"))
                return "sisters siblings playing";
            if (lower.Contains("drugar") || lower.Contains("prijatelj") || lower.Contains("drug "))
                return "children friends playing";
            if (lower.Contains("porodic") || lower.Contains("familij"))
                return "family outdoor together";
            if (lower.Contains("beba") || lower.Contains("bebac") || lower.Contains("novoroden"))
                return "baby cute toddler smiling happy";
            if (lower.Contains("dete") || lower.Contains("dijete") || lower.Contains("djecak") ||
                lower.Contains("devojcica") || lower.Contains("djevojcica"))
                return "child playing outdoor happy";
            if (lower.Contains("uciteljic") || lower.Contains("nastavnic") || lower.Contains("profesor"))
                return "teacher classroom children learning";
            if (lower.Contains("doktor") || lower.Contains("lekar") || lower.Contains("ljekar"))
                return "doctor children hospital care";
            if (lower.Contains("heroj") || lower.Contains("superhero"))
                return "superhero children playing";
            if (lower.Contains("princeza") || lower.Contains("princ ") || lower.Contains("kralj") || lower.Contains("kraljica"))
                return "princess prince fairy tale children";
            if (lower.Contains("vila") || lower.Contains("vilenjak") || lower.Contains("bajk"))
                return "fairy tale magical children";

            if (lower.Contains("sladoled") && (lower.Contains("caj") || lower.Contains("zima") || lower.Contains("zimi")))
                return "child eating ice cream summer park happy";
            if (lower.Contains("sladoled")) return "child eating ice cream summer park happy";
            if (lower.Contains("cokolada"))
                return "child chocolate happy";
            if (lower.Contains("torta") || lower.Contains("kolac") || lower.Contains("cupcake"))
                return "birthday cake children celebrating";
            if (lower.Contains("jabuka")) return "child eating apple healthy";
            if (lower.Contains("banana") || lower.Contains("banane"))
                return "child eating banana fruit";
            if (lower.Contains("jagoda") || lower.Contains("jagode") || lower.Contains("malina"))
                return "child eating strawberry fruit summer";
            if (lower.Contains("lubenica"))
                return "child eating watermelon summer";
            if (lower.Contains("pomorandza") || lower.Contains("narandza") || lower.Contains("limun"))
                return "citrus fruit colorful fresh";
            if (lower.Contains("caj")) return "child drinking hot chocolate warm cozy";
            if (lower.Contains("sok")) return "child drinking juice fresh";
            if (lower.Contains("mleko") || lower.Contains("mlijeko"))
                return "child drinking milk healthy";
            if (lower.Contains("pizza"))
                return "children eating pizza happy";
            if (lower.Contains("palacink") || lower.Contains("crepe"))
                return "child eating pancakes breakfast";
            if (lower.Contains("med ") || lower.Contains("medu"))
                return "honey jar sweet";
            if (lower.Contains("kokice") || lower.Contains("popcorn"))
                return "children eating popcorn movie";
            if (lower.Contains("bombon") || lower.Contains("slatkis") || lower.Contains("gumeni"))
                return "child candy sweets colorful";
            if (lower.Contains("hleb") || lower.Contains("hljeb") || lower.Contains("sendvic"))
                return "child eating sandwich lunch";
            if (lower.Contains("vecera") || lower.Contains("rucak") || lower.Contains("dorucak"))
                return "family eating meal together";
            if (lower.Contains("kafa") || lower.Contains("espresso"))
                return "children drinking hot chocolate cozy winter";
            if (lower.Contains("voda ") || lower.Contains("vodu") || lower.Contains("pijem"))
                return "child drinking water healthy";

            if (lower.Contains("trci") || lower.Contains("trcanje") || lower.Contains("juris") ||
                lower.Contains("trcati") || lower.Contains("trceci"))
                return "children running park playground sunny happy";
            if (lower.Contains("setaj") || lower.Contains("seta ") || lower.Contains("setnja") ||
                lower.Contains("sece") || lower.Contains("setati") || lower.Contains("setnjicu"))
                return "children walking park sunny green happy";
            if (lower.Contains("skoci") || lower.Contains("skaci") || lower.Contains("skakanje") ||
                lower.Contains("skakutanje") || lower.Contains("skokovit"))
                return "child jumping happy";
            if (lower.Contains("pliva") || lower.Contains("kupanje") || lower.Contains("plivanje"))
                return "child swimming pool water";
            if (lower.Contains("rolanje") || lower.Contains("klizanje") || lower.Contains("klizaliste"))
                return "child ice skating winter";
            if (lower.Contains("skijanje") || lower.Contains("skija") || lower.Contains("ski"))
                return "child skiing winter snow mountain";
            if (lower.Contains("sankanje") || lower.Contains("sanke") || lower.Contains("saonice"))
                return "child sledding snow winter fun";
            if (lower.Contains("fudbal") || lower.Contains("nogomet") || lower.Contains("loptu") ||
                lower.Contains("lopta "))
                return "children playing football soccer";
            if (lower.Contains("kosarka") || lower.Contains("kos "))
                return "basketball children playing";
            if (lower.Contains("tenis") || lower.Contains("reket"))
                return "tennis children sport";
            if (lower.Contains("penjanje") || lower.Contains("penj") || lower.Contains("penje"))
                return "child climbing tree playground";
            if (lower.Contains("plivanje") || lower.Contains("surf") || lower.Contains("surfanje"))
                return "surfing waves ocean sport";
            if (lower.Contains("yoga") || lower.Contains("meditacij"))
                return "yoga meditation peaceful";
            if (lower.Contains("gimnastik") || lower.Contains("akrobat"))
                return "gymnastics child flexible sport";
            if (lower.Contains("karate") || lower.Contains("dzudo") || lower.Contains("borilack"))
                return "martial arts children sport";
            if (lower.Contains("biciklizm") || lower.Contains("vozi bicikl"))
                return "child riding bicycle outdoor";
            if (lower.Contains("planinar") || lower.Contains("pohod") || lower.Contains("trekking"))
                return "hiking mountain trail nature";
            if (lower.Contains("ribolov") || lower.Contains("pecanje"))
                return "fishing lake child outdoor";
            if (lower.Contains("jedrilica") || lower.Contains("jedricarenje") || lower.Contains("vesla"))
                return "sailing boat water outdoor";
            if (lower.Contains("padobran") || lower.Contains("skakanje padobranom"))
                return "parachute sky adventure";

            if (lower.Contains("plesi") || lower.Contains("pleses") || lower.Contains("ples") ||
                lower.Contains("tancuj") || lower.Contains("zaigra") || lower.Contains("igra kolo"))
                return "children dancing joyful";
            if (lower.Contains("peva") || lower.Contains("pjeva") || lower.Contains("pevaj") ||
                lower.Contains("pjevaj") || lower.Contains("pevanje"))
                return "child singing music";
            if (lower.Contains("crta") || lower.Contains("risanje") || lower.Contains("boji") ||
                lower.Contains("bojanka") || lower.Contains("akvarelom") || lower.Contains("kistom"))
                return "child drawing painting art colorful";
            if (lower.Contains("cita") || lower.Contains("knjig") || lower.Contains("prica") ||
                lower.Contains("bajka") || lower.Contains("lektir"))
                return "child reading book library";
            if (lower.Contains("igraj") || lower.Contains("igra ") || lower.Contains("igraju") ||
                lower.Contains("igrajmo") || lower.Contains("igrajte"))
                return "children playing joyful";
            if (lower.Contains("smej") || lower.Contains("smije") || lower.Contains("blistaj") ||
                lower.Contains("kesi") || lower.Contains("kikose"))
                return "children laughing happy faces";
            if (lower.Contains("spava") || lower.Contains("tones u san") || lower.Contains("zaspi") ||
                lower.Contains("drijema") || lower.Contains("drijemat"))
                return "child sleeping peaceful";
            if (lower.Contains("sanja") || lower.Contains("snovi") || lower.Contains("snovid"))
                return "child dreaming stars night sky";
            if (lower.Contains("grli") || lower.Contains("zagrli") || lower.Contains("zagrliti") ||
                lower.Contains("mazi") || lower.Contains("mazi"))
                return "children hugging friendship love";
            if (lower.Contains("ljuljaska") || lower.Contains("ljulja") || lower.Contains("tobogan"))
                return "child playground swing slide";
            if (lower.Contains("pesak") || lower.Contains("pjescanik") || lower.Contains("sanduk"))
                return "child playing sand sandbox";
            if (lower.Contains("lopta") || lower.Contains("balon") || lower.Contains("balonom"))
                return "child playing ball balloon colorful";
            if (lower.Contains("zmaj") || lower.Contains("zmajevima") || lower.Contains("zmajic"))
                return "child flying kite wind outdoor";
            if (lower.Contains("puzzle") || lower.Contains("slagalica"))
                return "child playing puzzle indoor";
            if (lower.Contains("lego") || lower.Contains("kocke") || lower.Contains("graditi") ||
                lower.Contains("gradi kulu"))
                return "child building blocks lego";
            if (lower.Contains("skrivac") || lower.Contains("zmure") || lower.Contains("sakriven"))
                return "children hide seek playing";
            if (lower.Contains("karnevar") || lower.Contains("karneval") || lower.Contains("maskaradu"))
                return "carnival costume children celebration";
            if (lower.Contains("pozoriset") || lower.Contains("kazaliste") || lower.Contains("pozornica"))
                return "children theater stage performance";
            if (lower.Contains("lutak") || lower.Contains("lutke") || lower.Contains("marioneta"))
                return "puppet show children theater";
            if (lower.Contains("cirkus"))
                return "circus performance children";
            if (lower.Contains("video igric") || lower.Contains("kompjuter") || lower.Contains("tablet"))
                return "child playing video game";
            if (lower.Contains("bazen") || lower.Contains("vodeni park"))
                return "children water park swimming fun";
            if (lower.Contains("piknik"))
                return "family picnic outdoor nature";
            if (lower.Contains("kampovanje") || lower.Contains("kamp") || lower.Contains("sator"))
                return "camping tent nature outdoor";
            if (lower.Contains("vatromet") || lower.Contains("svecanos") || lower.Contains("proslava"))
                return "fireworks celebration night colorful";

            if (lower.Contains("uci") || lower.Contains("uciti") || lower.Contains("nauci") ||
                lower.Contains("uciti") || lower.Contains("ucis"))
                return "child learning school studying";
            if (lower.Contains("skola") || lower.Contains("razred") || lower.Contains("ucionica"))
                return "school children classroom learning";
            if (lower.Contains("domaci zadatak") || lower.Contains("zadatak") || lower.Contains("domaci"))
                return "child doing homework studying";
            if (lower.Contains("pismo") || lower.Contains("pisanje") || lower.Contains("pise"))
                return "child writing letter paper";
            if (lower.Contains("muzicka skola") || lower.Contains("hora") || lower.Contains("hor "))
                return "children choir singing school";
            if (lower.Contains("kuvanje") || lower.Contains("kuva") || lower.Contains("pravi kolac"))
                return "child cooking baking kitchen";
            if (lower.Contains("fotografij") || lower.Contains("fotoaparat") || lower.Contains("slika prirodu"))
                return "child photography camera nature";
            if (lower.Contains("pravi") || lower.Contains("izraduj") || lower.Contains("kreacij"))
                return "child crafts making creative";
            if (lower.Contains("origami") || lower.Contains("papir"))
                return "child paper crafts origami";
            if (lower.Contains("botanik") || lower.Contains("biljke") || lower.Contains("cvecara"))
                return "child gardening plants flowers";
            if (lower.Contains("sadi") || lower.Contains("zaliva") || lower.Contains("basta"))
                return "child planting garden watering";

            if (lower.Contains("parkic") || lower.Contains(" park ") || lower.Contains("parku"))
                return "children playing park playground sunny happy";
            if (lower.Contains("plaza") || lower.Contains("more ") || lower.Contains("mora") ||
                lower.Contains("obala") || lower.Contains("pesak mora"))
                return "child beach sea summer";
            if (lower.Contains("suma") || lower.Contains("sumi") || lower.Contains("sumarak"))
                return "children forest nature trees";
            if (lower.Contains("planina") || lower.Contains("vrh") || lower.Contains("planinski"))
                return "mountain nature hiking landscape";
            if (lower.Contains("reka") || lower.Contains("potok") || lower.Contains("reci") ||
                lower.Contains("rjecica"))
                return "river stream water nature";
            if (lower.Contains("jezero") || lower.Contains("jezerc"))
                return "lake water nature reflection";
            if (lower.Contains("livada") || lower.Contains("polje") || lower.Contains("njiva"))
                return "meadow flowers field nature";
            if (lower.Contains("basta") || lower.Contains("vrt ") || lower.Contains("vrtu"))
                return "garden flowers colorful outdoor";
            if (lower.Contains("pecina") || lower.Contains("spilja"))
                return "cave nature adventure";
            if (lower.Contains("vodopad") || lower.Contains("kaskad"))
                return "waterfall nature beautiful";
            if (lower.Contains("desert") || lower.Contains("pustinja"))
                return "desert sand dunes landscape";
            if (lower.Contains("dzungla") || lower.Contains("prasuma"))
                return "jungle tropical nature";
            if (lower.Contains("arktik") || lower.Contains("antarktik") || lower.Contains("led ") ||
                lower.Contains("ledenjak"))
                return "arctic ice polar landscape";
            if (lower.Contains("nebo ") || lower.Contains("nebom") || lower.Contains("oblaci") ||
                lower.Contains("oblak"))
                return "sky clouds blue beautiful";
            if (lower.Contains("zvezdano nebo") || lower.Contains("zvezde") || lower.Contains("zvijezde"))
                return "night sky stars milky way";
            if (lower.Contains("mesec ") || lower.Contains("mjesec ") || lower.Contains("punog meseca"))
                return "moon night sky stars";
            if (lower.Contains("sunce") || lower.Contains("suncano") || lower.Contains("suncani"))
                return "sunshine bright sunny outdoor";
            if (lower.Contains("duga") || lower.Contains("dugom"))
                return "rainbow colorful sky nature";
            if (lower.Contains("kisa") || lower.Contains("kisica") || lower.Contains("kaplje"))
                return "rain drops children playing puddle";
            if (lower.Contains("oluja") || lower.Contains("grmljavina") || lower.Contains("munja"))
                return "storm lightning dramatic sky";
            if (lower.Contains("magla") || lower.Contains("izmaglica"))
                return "fog misty morning nature";
            if (lower.Contains("vetar") || lower.Contains("vjetar") || lower.Contains("povjetarac"))
                return "wind blowing leaves nature";

            if (lower.Contains("grad ") || lower.Contains("gradu") || lower.Contains("gradic"))
                return "city street urban children";
            if (lower.Contains("ulica") || lower.Contains("ulici") || lower.Contains("trotoar"))
                return "street sidewalk city";
            if (lower.Contains("kuca") || lower.Contains("kuci") || lower.Contains(" dom ") ||
                lower.Contains("doma") || lower.Contains("domov"))
                return "home family house cozy";
            if (lower.Contains("dvorist"))
                return "children backyard playing";
            if (lower.Contains("zoo") || lower.Contains("zoloski") || lower.Contains("zoovrt"))
                return "zoo animals children visiting";
            if (lower.Contains("muzej") || lower.Contains("galerij"))
                return "museum children visit art";
            if (lower.Contains("bioskop") || lower.Contains("kino") || lower.Contains("film"))
                return "cinema movie children popcorn";
            if (lower.Contains("bibliotek") || lower.Contains("knjiznica"))
                return "library books children reading";
            if (lower.Contains("trzni centar") || lower.Contains("prodavnica") || lower.Contains("prodavaonica"))
                return "shopping mall children family";
            if (lower.Contains("crkva") || lower.Contains("dzamija") || lower.Contains("hram"))
                return "church architecture peaceful";
            if (lower.Contains("bolnica"))
                return "hospital doctor care";
            if (lower.Contains("aerodrom"))
                return "airport airplane travel";
            if (lower.Contains("kolodvor") || lower.Contains("zeleznicka stanica") || lower.Contains("stanica"))
                return "train station travel";
            if (lower.Contains("luka") || lower.Contains("pristaniste"))
                return "harbor boats port";
            if (lower.Contains("trznica") || lower.Contains("pijaca") || lower.Contains("pijaci"))
                return "market colorful food outdoor";
            if (lower.Contains("kafic") || lower.Contains("restoran"))
                return "cafe restaurant family";
            if (lower.Contains("hotel") || lower.Contains("odmor") || lower.Contains("ljetovanje") ||
                lower.Contains("letovanje"))
                return "hotel vacation family travel";

            if (lower.Contains("prolec") || lower.Contains("proljec") || lower.Contains("cvet") ||
                lower.Contains("cvijet") || lower.Contains("tresnja cveta") || lower.Contains("latica"))
                return "spring flowers blooming child";
            if (lower.Contains("jesen") || lower.Contains("opada") || lower.Contains("jesenj") ||
                lower.Contains("zuto lisce") || lower.Contains("zlatno lisce"))
                return "autumn leaves falling colorful";
            if (lower.Contains("zima ") || lower.Contains("zimska") || lower.Contains("zimski") ||
                lower.Contains("sneg ") || lower.Contains("snijeg") || lower.Contains("mraz"))
                return "winter snow child playing outdoor";
            if (lower.Contains("leto ") || lower.Contains("ljeto") || lower.Contains("letnji") ||
                lower.Contains("ljetni") || lower.Contains("toplo") || lower.Contains("vrelo"))
                return "summer sunny outdoor children";

            if (lower.Contains("cizme") || lower.Contains("gumene cizme"))
                return "child winter boots snow";
            if (lower.Contains("rukavice"))
                return "child winter gloves snow";
            if (lower.Contains("skafander") || lower.Contains("kombinezon"))
                return "child winter suit snow outdoor";
            if (lower.Contains("kapa ") || lower.Contains("kapu") || lower.Contains("sesir"))
                return "child hat winter colorful";
            if (lower.Contains("sal ") || lower.Contains("salom"))
                return "child scarf winter cozy";
            if (lower.Contains("kaput") || lower.Contains("jakna") || lower.Contains("mantil"))
                return "child coat winter dressed";
            if (lower.Contains("kupaci") || lower.Contains("plivacke naocale"))
                return "child swimsuit pool summer";
            if (lower.Contains("carapa") || lower.Contains("carape"))
                return "colorful socks child cute";
            if (lower.Contains("haljina") || lower.Contains("suknjica"))
                return "girl dress colorful beautiful";
            if (lower.Contains("pantalone") || lower.Contains("traperice"))
                return "child casual clothes";
            if (lower.Contains("pidzama") || lower.Contains("pidzama"))
                return "child pajamas bedtime cute";

            if (lower.Contains("gitara") || lower.Contains("gitaru") || lower.Contains("gitarom"))
                return "child playing guitar music";
            if (lower.Contains("klavir") || lower.Contains("piano") || lower.Contains("pijanino"))
                return "child playing piano keyboard";
            if (lower.Contains("bubanj") || lower.Contains("bubnjevi") || lower.Contains("bubi"))
                return "child playing drums percussion";
            if (lower.Contains("violina") || lower.Contains("violinu"))
                return "child playing violin music";
            if (lower.Contains("flaut") || lower.Contains("flauta"))
                return "child playing flute music";
            if (lower.Contains("truba") || lower.Contains("trombon") || lower.Contains("saksofon"))
                return "child playing trumpet brass music";
            if (lower.Contains("harmonika") || lower.Contains("harmoniku"))
                return "child playing accordion music";
            if (lower.Contains("ukulele") || lower.Contains("mandolina"))
                return "child playing ukulele music";
            if (lower.Contains("svira") || lower.Contains("instrument") || lower.Contains("orkestar"))
                return "child playing musical instrument";
            if (lower.Contains("pesm") || lower.Contains("pjesm") || lower.Contains("pjesmica"))
                return "child singing microphone stage";
            if (lower.Contains("slusaj") || lower.Contains("slusa ") || lower.Contains("slusati") ||
                lower.Contains("slusamo") || lower.Contains("slus"))
                return "child listening headphones music";
            if (lower.Contains("melodija") || lower.Contains("nota") || lower.Contains("note "))
                return "music notes flying colorful";
            // PATCH 9: Re-Contextualization — zamena za anomalije iz Test2.mp4
            // Anomaly 1 replacement: health/movement/activity → children's play, NOT gym
            if (lower.Contains("zdrav") || lower.Contains("jak") || lower.Contains("snazan") ||
                lower.Contains("health") || lower.Contains("strong") || lower.Contains("active"))
                return "children jumping playing active outdoor park sandbox butterfly fun";
            // Anomaly 2 replacement: music/rhythm/note → children dancing in circle, NOT gramophone
            if (lower.Contains("nota") || lower.Contains("melodij") || lower.Contains("pesma") ||
                lower.Contains("muzik") || lower.Contains("song") || lower.Contains("melody") ||
                lower.Contains("music") || lower.Contains("beat"))
                return "children dancing circle clapping hands singing joyful colorful";

            if (lower.Contains("ritam") || lower.Contains("takt") || lower.Contains("tempo"))
                return "music beat rhythm children dancing";
            if (lower.Contains("koncert") || lower.Contains("nastup") || lower.Contains("pozornica"))
                return "music concert stage performance";
            if (lower.Contains("muzik") || lower.Contains("glazb"))
                return "music colorful notes children";
            if (lower.Contains("radio") || lower.Contains("zvucnik"))
                return "music radio listening colorful";

            if (lower.Contains("srecan") || lower.Contains("sretna") || lower.Contains("sretan") ||
                lower.Contains("sreca") || lower.Contains("radost") ||
                lower.Contains("veseo") || lower.Contains("vesela"))
                return "happy child joyful smiling";
            if (lower.Contains("tuzan") || lower.Contains("tuzna") || lower.Contains("plac") ||
                lower.Contains("suze") || lower.Contains("placem"))
                return "child sad crying emotional";
            if (lower.Contains("ljut") || lower.Contains("besn") || lower.Contains("srdit"))
                return "child angry frustrated emotional";
            if (lower.Contains("uplasen") || lower.Contains("strah") || lower.Contains("bojim"))
                return "child scared surprised";
            if (lower.Contains("iznenadjen") || lower.Contains("iznenadjenje") || lower.Contains("wow"))
                return "child surprised amazed face";
            if (lower.Contains("ponosan") || lower.Contains("ponosna") || lower.Contains("ponos"))
                return "child proud achievement success";
            if (lower.Contains("umoran") || lower.Contains("umorna") || lower.Contains("pospanost"))
                return "child tired sleepy yawning";
            if (lower.Contains("bolestan") || lower.Contains("bolesna") || lower.Contains("boli me"))
                return "child sick bed rest";
            if (lower.Contains("zdrav") || lower.Contains("zdravlje") || lower.Contains("fit"))
                return "child healthy active outdoor";
            if (lower.Contains("zaljubljen") || lower.Contains("voli") || lower.Contains("ljubav") ||
                lower.Contains("srce") || lower.Contains("dragi") || lower.Contains("draga"))
                return "love heart children friendship";
            if (lower.Contains("osecanj") || lower.Contains("osjjecanj") || lower.Contains("emocij"))
                return "child expressive emotional face";
            if (lower.Contains("smireno") || lower.Contains("mirno") || lower.Contains("spokojno"))
                return "child calm peaceful serene";
            if (lower.Contains("uzbudjen") || lower.Contains("uzbudjean") || lower.Contains("euforican"))
                return "child excited happy energetic";

            if (lower.Contains("rodjendan"))
                return "birthday party children celebration cake";
            if (lower.Contains("bozic") || lower.Contains("jelka") ||
                lower.Contains("deda mraz") || lower.Contains("santa"))
                return "christmas tree gifts children";
            if (lower.Contains("nova godina") || lower.Contains("silvest") || lower.Contains("docek"))
                return "new year celebration fireworks children";
            if (lower.Contains("uskrs") || lower.Contains("vaskrs") || lower.Contains("jaje") ||
                lower.Contains("jaja") || lower.Contains("zec uskrs"))
                return "easter eggs colorful spring children";
            if (lower.Contains("halloween") || lower.Contains("noc vjest") || lower.Contains("bundeva"))
                return "halloween pumpkin children costumes";
            if (lower.Contains("valentinovo") || lower.Contains("srce poklanjam"))
                return "valentines day heart love flowers";
            if (lower.Contains("dan majki") || lower.Contains("majcin dan"))
                return "mothers day flowers love family";
            if (lower.Contains("dan oceva") || lower.Contains("ocev dan"))
                return "fathers day family love outdoor";
            if (lower.Contains("skolski praznici") || lower.Contains("raspust"))
                return "school holidays children vacation";
            if (lower.Contains("festival") || lower.Contains("svecanos"))
                return "festival celebration colorful people";
            if (lower.Contains("vjencanje") || lower.Contains("vencanje") || lower.Contains("svadba"))
                return "wedding celebration flowers";
            if (lower.Contains("penzij") || lower.Contains("odlazak u penziju"))
                return "retirement celebration family";

            if (lower.Contains("crvena") || lower.Contains("crveno") || lower.Contains("crven"))
                return "red color bright vibrant";
            if (lower.Contains("plava") || lower.Contains("plavo") || lower.Contains("plav"))
                return "blue sky water bright";
            if (lower.Contains("zelena") || lower.Contains("zeleno") || lower.Contains("zelen"))
                return "green nature grass outdoor";
            if (lower.Contains("zuta") || lower.Contains("zuto") || lower.Contains("zut") ||
                lower.Contains("suncano zut"))
                return "yellow sunshine bright cheerful";
            if (lower.Contains("narancasta") || lower.Contains("narandzasta") || lower.Contains("oranz"))
                return "orange colorful warm sunset";
            if (lower.Contains("ljubicasta") || lower.Contains("violetna") || lower.Contains("lila"))
                return "purple violet colorful flowers";
            if (lower.Contains("ruzicasta") || lower.Contains("roze") || lower.Contains("pink"))
                return "pink flowers cute colorful";
            if (lower.Contains("zlatna") || lower.Contains("zlatno") || lower.Contains("zlato"))
                return "golden sunlight treasure bright";
            if (lower.Contains("bela") || lower.Contains("bijela") || lower.Contains("snezno bel"))
                return "white pure clean snow";
            if (lower.Contains("sarena") || lower.Contains("sareno") || lower.Contains("raznobojn"))
                return "colorful rainbow bright children";
            if (lower.Contains("duga") || lower.Contains("duginih boja"))
                return "rainbow colors bright beautiful";

            if (lower.Contains("oci") || lower.Contains("okice") || lower.Contains("oko ") ||
                lower.Contains("okom") || lower.Contains("pogled") || lower.Contains("gleda") ||
                lower.Contains("vidi") || lower.Contains("otvori oci") || lower.Contains("utvori"))
                return "child eyes open looking curious";
            if (lower.Contains("ruke") || lower.Contains("ruku") || lower.Contains("rukom") ||
                lower.Contains("rukica") || lower.Contains("rukice") || lower.Contains("saka") ||
                lower.Contains("prsti") || lower.Contains("drzi se za ruku"))
                return "child hands holding together";
            if (lower.Contains("noge") || lower.Contains("nogama") || lower.Contains("nogice") ||
                lower.Contains("stopala") || lower.Contains("stopalo") || lower.Contains("nozice"))
                return "child feet walking barefoot grass";
            if (lower.Contains("glava") || lower.Contains("glavica") || lower.Contains("kosa") ||
                lower.Contains("kosom") || lower.Contains("pletenice") || lower.Contains("frizura"))
                return "child hair cute portrait";
            if (lower.Contains("usi") || lower.Contains("uho") || lower.Contains("usice") ||
                lower.Contains("cuje") || lower.Contains("slusa") || lower.Contains("cuti"))
                return "child listening ears music";
            if (lower.Contains("nos") || lower.Contains("nosic") || lower.Contains("mirise") ||
                lower.Contains("miris") || lower.Contains("vonj"))
                return "child smelling flowers nature";
            if (lower.Contains("usta") || lower.Contains("usne") || lower.Contains("osmeh") ||
                lower.Contains("osmijeh") || lower.Contains("smjesak") || lower.Contains("zubi") ||
                lower.Contains("zub ") || lower.Contains("jezik"))
                return "child smile teeth happy portrait";
            if (lower.Contains("lice") || lower.Contains("lica") || lower.Contains("obrazi") ||
                lower.Contains("obraz") || lower.Contains("crvenila"))
                return "child face portrait expression";
            if (lower.Contains("srce ") || lower.Contains("srcem") || lower.Contains("kuca srce") ||
                lower.Contains("srceko"))
                return "heart love warm feeling";
            if (lower.Contains("stomak") || lower.Contains("trbusic") || lower.Contains("stomacic"))
                return "child belly laughing cute";
            if (lower.Contains("ledja") || lower.Contains("ramena") || lower.Contains("rame"))
                return "child back shoulder outdoor";
            if (lower.Contains("koljena") || lower.Contains("kolena") || lower.Contains("koljenice"))
                return "child kneeling sitting outdoor";
            if (lower.Contains("telo") || lower.Contains("tijelo") || lower.Contains("celo telo") ||
                lower.Contains("cijelo tijelo"))
                return "child body healthy active outdoor";
            if (lower.Contains("koza") || lower.Contains("put") || lower.Contains("ten"))
                return "child skin healthy outdoor";
            if (lower.Contains("misic") || lower.Contains("jak") || lower.Contains("snazan") ||
                lower.Contains("snazna"))
                return "child strong muscles active sport";

            if (lower.Contains("jutro") || lower.Contains("zora") || lower.Contains("osvanu") ||
                lower.Contains("pocni dan") || lower.Contains("probudi"))
                return "child morning waking up sunrise";
            if (lower.Contains("podne") || lower.Contains("podnev"))
                return "sunny midday outdoor children";
            if (lower.Contains("vece") || lower.Contains("vecer") || lower.Contains("sumrak") ||
                lower.Contains("zalazak"))
                return "sunset evening colorful sky";
            if (lower.Contains("noc ") || lower.Contains("nocu") || lower.Contains("laku noc") ||
                lower.Contains("zavrsi dan"))
                return "child evening bedtime stars";
            if (lower.Contains("ponoc") || lower.Contains("u ponoc"))
                return "midnight stars moon night";

            if (lower.Contains("sanja") || lower.Contains("snovi") || lower.Contains("mastam") ||
                lower.Contains("sanjar"))
                return "child dreaming stars imagination";
            if (lower.Contains("masta") || lower.Contains("fantazij") || lower.Contains("imaginacij"))
                return "children imagination magical fantasy";
            if (lower.Contains("sloboda") || lower.Contains("slobodan") || lower.Contains("leti slobodn"))
                return "freedom outdoor running open";
            if (lower.Contains("prijateljs") || lower.Contains("drugarst"))
                return "children friendship together happy";
            if (lower.Contains("ljubav") || lower.Contains("volim") || lower.Contains("voljet") ||
                lower.Contains("ljubi"))
                return "love children heart warm";
            if (lower.Contains("nada") || lower.Contains("nadaj") || lower.Contains("nadamo"))
                return "hope bright future children";
            if (lower.Contains("mir ") || lower.Contains("miru") || lower.Contains("miran"))
                return "peace calm nature serene";
            if (lower.Contains("hrabrost") || lower.Contains("hrabar") || lower.Contains("odvazcan"))
                return "brave child confident strong";
            if (lower.Contains("blago") || lower.Contains("dragocen") || lower.Contains("najvec"))
                return "treasure gift golden child";
            if (lower.Contains("cudo") || lower.Contains("cudesno") || lower.Contains("magicn"))
                return "magic wonder children amazed";
            if (lower.Contains("rast") || lower.Contains("sazrev") || lower.Contains("odrastanj"))
                return "child growing learning achievement";
            if (lower.Contains("zajedno") || lower.Contains("svi zajed") || lower.Contains("svi smo"))
                return "children group together teamwork";
            if (lower.Contains("priroda") || lower.Contains("sve oko nas") || lower.Contains("okol"))
                return "nature outdoor children exploring";
            if (lower.Contains("simbol") || lower.Contains("znak") || lower.Contains("mocan"))
                return "child happiness joy celebration";
            if (lower.Contains("kraj") || lower.Contains("zavrsetak") || lower.Contains("finali"))
                return "children celebration finish happy";
            if (lower.Contains("pocetak") || lower.Contains("novi poce") || lower.Contains("polazak"))
                return "child new beginning adventure";
            if (lower.Contains("put ") || lower.Contains("putovan") || lower.Contains("avantum"))
                return "journey adventure children travel";
            if (lower.Contains("zvuk") || lower.Contains("buka") || lower.Contains("tisina"))
                return "sound waves music colorful";
            if (lower.Contains("svetlost") || lower.Contains("svjetlost") || lower.Contains("sjaj") ||
                lower.Contains("sija") || lower.Contains("blista"))
                return "light bright shining beautiful";
            if (lower.Contains("tama") || lower.Contains("mrak") || lower.Contains("nocna"))
                return "night stars moon dark sky";
            if (lower.Contains("toplina") || lower.Contains("toplota") || lower.Contains("greje"))
                return "warm cozy home family";
            if (lower.Contains("hladnoc") || lower.Contains("hladno") || lower.Contains("smrzava"))
                return "cold winter snow outdoor";

            if (!string.IsNullOrEmpty(analysis.Context) && !string.IsNullOrEmpty(analysis.Mood))
            {
                string moodVisual = analysis.Mood switch
                {
                    "happy" => "happy joyful bright",
                    "calm" => "peaceful calm serene",
                    "excited" => "energetic vibrant colorful",
                    "playful" => "playful fun children",
                    "joyful" => "joyful celebration bright",
                    "energetic" => "active dynamic movement",
                    "upbeat" => "cheerful bright uplifting",
                    _ => "happy bright colorful"
                };
                string contextVisual = analysis.Context switch
                {
                    "dance" => "children dancing",
                    "lullaby" => "child peaceful quiet",
                    "party" => "children celebrating",
                    "nature" => "nature outdoor green",
                    "school" => "children learning",
                    "health" => "child active outdoor",
                    "animal" => "animals cute",
                    _ => "child playing"
                };
                return $"{contextVisual} {moodVisual}";
            }

            return analysis.Context switch
            {
                "outdoor" => "child outdoor activity playing",
                "dance" => "children dancing joyful",
                "lullaby" => "child sleeping peaceful bedroom soft light",
                "party" => "child celebrating birthday party",
                "nature" => "children nature outdoor exploring",
                "health" => "child active healthy running outdoor",
                _ => "child playing outdoor happy"
            };
        }
        // ── Ambient sound from lyric ─────────────────────────────────────────
        // AMBIENT FIX (v1.4): the old version used lower.Contains("...") on the raw
        // lyric. Two problems made the ambient track feel random:
        //  • diacritics — "šuma", "kiša", "čizme", "rođendan" never matched;
        //  • substrings — "led" matched "gledaj" (blizzard under a summer line),
        //    "pas" matched "opasno"/"spasi" (dog bark), "grad" matched "nagrada"
        //    (city traffic), "pjev" sent every "sing" line to bird chirps.
        // Now the lyric is normalized (Cyrillic→Latin, no diacritics) and every rule
        // matches only at the start of a word ('=' = whole word only).
        // Rules are checked top to bottom; the first match wins.
        private static readonly (string[] Stems, string Joyful, string Calm)[] _ambientRules =
        {
            (new[] { "ptic", "ptica", "cvrkut", "lastavic", "vrab", "slavuj", "golub" }, "animal bird chirp", "animal bird chirp"),
            (new[] { "=reka", "=reke", "=reci", "=reku", "rijek", "potok", "bujic", "izvor", "voda tec", "fontan", "cesm" }, "ambience creek stream", "ambience creek stream"),
            (new[] { "=more", "=moru", "=morem", "plaz", "talas", "obal", "brod", "ocean" }, "ambience ocean shore", "ambience ocean shore"),
            (new[] { "prska", "pljas", "=bara", "=bari", "=baru", "lokv" }, "liquid water splash", "liquid water splash"),
            (new[] { "=kisa", "=kise", "=kisi", "=kisu", "kisic", "kisob", "pada kis", "kaplj", "mokr" }, "weather ambience rain drips", "weather ambience rain drips"),
            (new[] { "grmljavin", "oluj", "munj", "=grom", "gromov" }, "weather ambience thunderstorm", "weather ambience thunderstorm"),
            (new[] { "vetar", "vetr", "vjetar", "vjetr", "povetar", "povjetar", "duva" }, "ambience nature field windy", "weather ambience hurricane wind"),
            (new[] { "zim", "sneg", "snez", "snijeg", "snjez", "mraz", "=led", "=leda", "=ledu", "leden", "mecav", "pahulj" }, "weather snow boots jumping", "weather ambience blizzard"),
            (new[] { "cizm", "rukavic", "skafander", "=kapa", "=kapu", "kapic", "=sal", "=salom", "kaput" }, "weather snow footstep", "weather ambience blizzard"),
            (new[] { "=suma", "=sume", "=sumi", "=sumu", "sumic", "sumsk", "drvec", "drvo", "grana", "grancic", "lisc", "lisk", "susti" }, "ambience nature trail", "ambience nature trail"),
            (new[] { "prolec", "proljec", "cvet", "cvijet", "cvec", "cvijec", "procvat", "pupolj" }, "animal bird chirp", "animal bird chirp"),
            (new[] { "=leto", "=leti", "=letu", "letnj", "=ljeto", "=ljeti", "ljetnj", "sunc", "suns", "topl", "vrucin", "cvrc" }, "animal ambience crickets", "animal ambience crickets"),
            (new[] { "jesen", "lisce pada", "opada", "zlatn", "zuto lisc" }, "ambience dirt road woods", "ambience dirt road woods"),
            (new[] { "trc", "trk", "skoc", "skac", "skak", "juri" }, "ambience children group playground", "ambience children group playground"),
            (new[] { "smej", "smeh", "smij", "vesel", "haha", "raduj", "radost" }, "ambience children group playground", "ambience children group playground"),
            (new[] { "setaj", "setnj", "setam", "hodaj", "hodam", "korak", "proset" }, "ambience nature trail", "ambience nature trail"),
            (new[] { "igraj", "igram", "igrac", "igric", "=igra", "=igre", "=igri", "=igru", "zabav" }, "ambience children group playground", "ambience children group playground"),
            (new[] { "tusir", "kupan", "kupam" }, "ambience children sprinkler", "ambience children sprinkler"),
            (new[] { "=park", "=parku", "=parka", "parkic", "klackalic", "ljuljas", "ljuljaj", "tobogan", "peskovnik", "pjeskovnik" }, "ambience children group playground distant", "ambience children group playground distant"),
            (new[] { "dvorist", "=basta", "=basti", "=bastu", "=vrt", "=vrtu", "vrtic" }, "ambience backyard road", "ambience backyard road"),
            (new[] { "=grad", "=grada", "=gradu", "=gradom", "ulic", "sokak", "centar" }, "ambience downtown area", "ambience downtown area"),
            (new[] { "=kuca", "=kuci", "=kucu", "=kucom", "=dom", "=domu", "=doma", "=soba", "=sobi", "=sobu", "unutra" }, "ambience backyard road", "ambience nature 180"),
            (new[] { "planin", "=vrh", "=vrhu", "klisur", "pecin" }, "ambience nature field windy", "ambience nature field windy"),
            (new[] { "movar", "mocvar", "ritov" }, "ambience nature near swamp", "ambience nature near swamp"),
            (new[] { "sladoled" }, "ambience children group playground", "ambience children group playground"),
            (new[] { "=caj", "=caja", "=caju", "kakao", "cokolad", "topli napit" }, "ambience backyard road", "ambience nature 180"),
            (new[] { "tort", "kolac", "slatkis", "rodjendan", "rodendan" }, "ambience children group playground", "ambience children group playground"),
            (new[] { "=pas", "=psa", "=psu", "=psi", "=pse", "psic", "kucic", "=kuce", "sten", "kuza" }, "animal dog bark", "animal dog bark"),
            (new[] { "=maca", "=mace", "=maci", "=macu", "macak", "mack", "macic", "mjau", "mijau" }, "animal mammal cat domestic meow", "animal mammal cat domestic meow"),
            (new[] { "konj", "konjic", "ponij" }, "animal horse canters", "animal horse canters"),
            (new[] { "pcel", "leptir", "bubamar", "=buba", "=bube" }, "animal ambience crickets", "animal ambience crickets"),
            (new[] { "=zaba", "=zabe", "zabic", "kvak", "krekec", "kreket" }, "animal frog chirp", "animal frog chirp"),
            (new[] { "=zec", "=zeca", "zek", "zecic", "vever", "vjever", "jelen" }, "ambience nature trail", "ambience nature trail"),
            (new[] { "livad", "=polje", "=polja", "=polju", "poljan" }, "ambience nature field windy", "ambience nature field windy"),
            (new[] { "majmun", "dzungl" }, "animal ambience jungle", "animal ambience jungle"),
            (new[] { "=mama", "=mamu", "=mami", "=mamom", "mamic", "majk", "=tata", "=tatu", "=tati", "=tatom", "tatic", "otac", "=oca" }, "ambience backyard road", "ambience backyard road"),
            (new[] { "=baka", "=baku", "=baki", "bakic", "=deka", "=deda", "=dedu", "=deki", "dekic", "porodic" }, "ambience nature trail", "ambience nature trail"),
            (new[] { "drugar", "prijatelj", "zajedno", "=svi", "=sve" }, "ambience children group playground", "ambience children group playground"),
            (new[] { "spava", "zaspi", "laku noc", "=usni", "=san", "=snu", "sanja" }, "animal bird chirp", "ambience nature 180"),
            (new[] { "=noc", "=noci", "=nocu", "nocas", "zvezd", "zvijezd", "mesec", "mjesec", "tisin" }, "animal bird chirp", "ambience night crickets"),
            (new[] { "zdrav", "=jako", "snazn", "sport", "trening", "vezba", "vjezba" }, "ambience children group playground", "ambience children group playground"),
        };

        /// <summary>True when the lyric itself names something with a sound (a rule matched).</summary>
        private bool TryInferAmbientFromLyric(string lyric, string context, out string ambient)
        {
            ambient = null;
            string norm = SerbianText.Normalize(lyric);
            bool isJoyfulContext = context is "outdoor" or "health" or "fun" or "party"
                                             or "dance" or "seasons" or "animal" or "school";

            foreach (var rule in _ambientRules)
            {
                if (SerbianText.HasAny(norm, rule.Stems))
                {
                    ambient = isJoyfulContext ? rule.Joyful : rule.Calm;
                    return true;
                }
            }
            return false;
        }

        private string InferAmbientFromLyric(string lyric, string context)
        {
            if (TryInferAmbientFromLyric(lyric, context, out string fromLyric))
                return fromLyric;

            return context switch
            {
                "lullaby" => "ambience nature 180",
                "outdoor" => "ambience children group playground",
                "health" => "ambience nature trail",
                "nature" => "ambience creek stream",
                "adventure" => "ambience nature trail",
                "sad" => "weather ambience rain drips",
                "party" => "ambience children group playground",
                "christmas" => "weather snow boots jumping",
                "animal" => "animal bird chirp",
                "seasons" => "animal bird chirp",
                "dance" => "ambience children group playground",
                "school" => "ambience children group playground distant",
                "love" => "ambience nature trail",
                "fun" => "ambience children group playground",
                _ => "animal bird chirp"
            };
        }

        private string ExtractJson(string text, bool isArray = false)
        {
            if (string.IsNullOrEmpty(text)) return null;
            text = System.Text.RegularExpressions.Regex.Replace(text, @"```json|```", "").Trim();
            try
            {
                if (isArray)
                {
                    int start = text.IndexOf('[');
                    int end = text.LastIndexOf(']');
                    if (start >= 0 && end > start) return text.Substring(start, end - start + 1);
                }
                else
                {
                    int start = text.IndexOf('{');
                    int end = text.LastIndexOf('}');
                    if (start >= 0 && end > start) return text.Substring(start, end - start + 1);
                }
            }
            catch (Exception swallowed) { AppLog.Error("Extracting JSON from the AI answer", swallowed); }   // v1.4: was silent
            return null;
        }

        private async Task<StoryBoard> GenerateStoryBoard(List<string> lyrics, CancellationToken ct)
        {
            var analysis = await AnalyseSongWithAI(lyrics, ct);
            _detectedContext = string.IsNullOrWhiteSpace(analysis.Context) ? "fun" : analysis.Context;
            _detectedMood = string.IsNullOrWhiteSpace(analysis.Mood) ? "happy" : analysis.Mood;
            _detectedSeason = string.IsNullOrWhiteSpace(analysis.Season) ? "" : analysis.Season;

            // Iskra AI-First: build SongContext za Ollama + StrictQueryEngine
            _songContext = new SongContext
            {
                AgeGroup = StrictQueryEngine.DetectAgeGroup(string.Join(" ", lyrics), _detectedContext),
                Context = _detectedContext,
                Mood = _detectedMood,
                Season = _detectedSeason,
                Setting = analysis.Setting ?? "outdoor",
                Theme = analysis.Theme ?? _detectedContext,
                VisualStyle = analysis.VisualStyle ?? "bright colorful outdoor children"
            };
            LogToMainWindow($"🎯 SongContext: age={_songContext.AgeGroup}, context={_songContext.Context}, season={_songContext.Season}");

            if (_contextKeywords.TryGetValue(_detectedContext, out var ctxList))
                _universalKeywords = new List<string>(ctxList);

            var perLyricKeywords = await GenerateKeywordsPerLyric(lyrics, analysis, ct);

            var scenes = new List<StoryScene>();
            for (int i = 0; i < lyrics.Count; i++)
            {
                var kw = perLyricKeywords.FirstOrDefault(k => k.Line == i + 1);
                string keywords = kw?.Keywords ?? GenerateKeywordsFromLyric(lyrics[i], analysis);
                string fallbackKw = kw?.FallbackKeywords; // asocijativni fallback iz AI interpretacije
                string ambient = kw?.Ambient ?? InferAmbientFromLyric(lyrics[i], analysis.Context);
                ambient = NormalizeAmbientFromAI(ambient, lyrics[i], analysis.Context ?? "outdoor");
                int energy = CalculateEnergy(i, lyrics.Count, analysis.Context);

                scenes.Add(new StoryScene
                {
                    SceneNumber = i + 1,
                    Description = lyrics[i].Length > 60 ? lyrics[i].Substring(0, 57) + "..." : lyrics[i],
                    FullLyric = lyrics[i],  // ZERO-FALLBACK: originalni stih za StrictQueryEngine
                    Emotion = analysis.Mood ?? "happy",
                    Energy = energy,
                    Characters = analysis.MainSubject ?? "children",
                    Action = ExtractActionFromLyric(lyrics[i]),
                    Location = analysis.Setting ?? "outdoor",
                    Keywords = keywords,
                    FallbackKeywords = fallbackKw,
                    KeywordsFromDirector = kw?.FromAI == true,
                    AmbientSound = ambient
                });
            }

            string overallTheme = analysis.Theme ?? (_detectedContext switch
            {
                "lullaby" => "Mirna uspavanka",
                "party" => "Vesela proslava",
                "love" => "Love Story",
                "sad" => "Emotivno putovanje",
                "adventure" => "Uzbudljiva avantura",
                "dance" => "Veseli ples",
                "christmas" => "Magical Christmas",
                "outdoor" => "Active Outdoor Life",
                "seasons" => "Beauty of the Seasons",
                "health" => "Zdravo i aktivno dijete",
                _ => "Cheerful Children's Song"
            });

            LogToMainWindow($"✅ Story board created: {scenes.Count} scenes for theme: '{overallTheme}'");
            return new StoryBoard { Scenes = scenes, MainCharacter = analysis.MainSubject ?? "Happy child", OverallTheme = overallTheme };
        }

        private int CalculateEnergy(int index, int total, string context)
        {
            if (total <= 0) return 3;
            double pos = (double)index / Math.Max(1, total - 1); // 0.0 → 1.0

            // Tihi konteksti: ravniji luk, max energija 2-3
            if (context == "lullaby")
            {
                if (pos < 0.1) return 1;
                if (pos < 0.5) return 2;
                if (pos < 0.85) return 2;
                return 1; // outro uvijek miran
            }
            if (context == "sad")
            {
                if (pos < 0.15) return 1;
                if (pos < 0.6) return 2;
                if (pos < 0.85) return 3;
                return 2;
            }

            // Normalni narativni luk: intro → buildup → peak → wind_down → outro
            // intro    0-12%  : uspostavljanje atmosfere, sporiji rezovi
            // buildup  12-35% : energija raste, raznovrsnost lokacija
            // peak     35-70% : maximum energy, faster cuts
            // wind_down 70-88%: smiruje se
            // outro    88-100%: memorabilna finalna slika
            if (pos < 0.12) return 2; // intro
            if (pos < 0.35) return 3; // buildup
            if (pos < 0.70) return 5; // peak
            if (pos < 0.88) return 3; // wind_down
            return 2;                  // outro
        }

        private string GetNarrativePhase(int index, int total)
        {
            if (total <= 0) return "middle";
            double pos = (double)index / Math.Max(1, total - 1);
            if (pos < 0.12) return "intro";
            if (pos < 0.35) return "buildup";
            if (pos < 0.70) return "peak";
            if (pos < 0.88) return "wind_down";
            return "outro";
        }

        private string ExtractActionFromLyric(string lyric)
        {
            string lower = StrictQueryEngine.Fold(lyric);

            if (lower.Contains("trci") || lower.Contains("trcanje") || lower.Contains("juri")) return "running";
            if (lower.Contains("setaj") || lower.Contains("seta") || lower.Contains("setnja") || lower.Contains("sece") || lower.Contains("hoda")) return "walking";
            if (lower.Contains("skoci") || lower.Contains("skaci") || lower.Contains("skakanje") || lower.Contains("poskakuje")) return "jumping";
            if (lower.Contains("leti") || lower.Contains("leteci") || lower.Contains("lebdi")) return "flying";
            if (lower.Contains("pliva") || lower.Contains("kupanje") || lower.Contains("ronjen")) return "swimming";
            if (lower.Contains("vozi") || lower.Contains("bicikl")) return "riding bicycle";
            if (lower.Contains("plesi") || lower.Contains("pleses") || lower.Contains("zaigraj") || lower.Contains("zaigra") || lower.Contains("brza") || lower.Contains("tancuj")) return "dancing";
            if (lower.Contains("penje") || lower.Contains("penjanje") || lower.Contains("penj")) return "climbing";

            if (lower.Contains("peva") || lower.Contains("pjeva") || lower.Contains("pevaj") || lower.Contains("pjevaj") || lower.Contains("zapeva")) return "singing";
            if (lower.Contains("svira") || lower.Contains("sviranje")) return "playing instrument";
            if (lower.Contains("crta") || lower.Contains("slika") || lower.Contains("boji") || lower.Contains("pravi")) return "drawing painting";
            if (lower.Contains("cita") || lower.Contains("knjig")) return "reading book";
            if (lower.Contains("igraj") || lower.Contains("igra") || lower.Contains("igraju")) return "playing";
            if (lower.Contains("gradi") || lower.Contains("pravi") || lower.Contains("kocke")) return "building blocks";
            if (lower.Contains("lopta") || lower.Contains("sutiraj") || lower.Contains("baca")) return "playing ball";

            if (lower.Contains("smej") || lower.Contains("smije") || lower.Contains("smeh") || lower.Contains("smijeh") || lower.Contains("blistaj")) return "laughing";
            if (lower.Contains("plac") || lower.Contains("suza")) return "crying";
            if (lower.Contains("grli") || lower.Contains("zagrli") || lower.Contains("mazi")) return "hugging";
            if (lower.Contains("ljubi") || lower.Contains("polj")) return "kissing cheek";

            if (lower.Contains("spava") || lower.Contains("sanja") || lower.Contains("zaspi") || lower.Contains("zadrema") || lower.Contains("lagana")) return "sleeping";
            if (lower.Contains("odmara") || lower.Contains("sedi") || lower.Contains("sjedi") || lower.Contains("lezi")) return "relaxing";
            if (lower.Contains("slusaj") || lower.Contains("slusa")) return "listening";

            if (lower.Contains("jede") || lower.Contains("jedi") || lower.Contains("sladoled") || lower.Contains("torta")) return "eating";
            if (lower.Contains("pije") || lower.Contains("pij") || lower.Contains("sok") || lower.Contains("caj")) return "drinking";

            if (lower.Contains("upoznaj") || lower.Contains("otkriva") || lower.Contains("istrazu")) return "exploring";
            if (lower.Contains("gleda") || lower.Contains("posmatra") || lower.Contains("vidi") || lower.Contains("viri")) return "watching";

            return "enjoying";
        }

        private bool ValidateStoryBoard(StoryBoard storyBoard)
        {
            if (storyBoard?.Scenes == null || storyBoard.Scenes.Count < 3)
            {
                LogToMainWindow("❌ Validation failed: Not enough scenes");
                return false;
            }

            var scenes = storyBoard.Scenes;
            bool valid = true;

            bool requiresPeak = _detectedContext != "lullaby" && _detectedContext != "sad";

            if (requiresPeak)
            {
                bool hasPeak = scenes.Any(s => s.Energy >= 4);
                if (!hasPeak)
                {
                    LogToMainWindow("⚠️ No scene with high energy (peak)");
                    valid = false;
                }
            }

            if (_detectedContext == "lullaby")
            {
                bool allCalm = scenes.All(s => s.Energy <= 2);
                if (!allCalm)
                {
                    LogToMainWindow("⚠️ Lullaby has a scene with too much energy");
                    valid = false;
                }
            }

            if (valid)
                LogToMainWindow("✅ Story board validation successful!");
            else
                LogToMainWindow("❌ Story board validation failed");

            return valid;
        }

        private StoryBoard ParseStoryBoard(string response)
        {
            try
            {
                int start = response.IndexOf('[');
                int end = response.LastIndexOf(']');
                if (start >= 0 && end > start)
                {
                    string json = response.Substring(start, end - start + 1);
                    json = System.Text.RegularExpressions.Regex.Replace(json, @"\]\s*,\s*\[", "],[");

                    if (!json.TrimStart().StartsWith("[") && json.Contains("{"))
                    {
                        json = $"[{json}]";
                    }

                    var scenes = Newtonsoft.Json.JsonConvert.DeserializeObject<List<StoryScene>>(json);

                    if (scenes != null && scenes.Count > 0)
                    {
                        LogToMainWindow($"✅ Successfully parsed {scenes.Count} scenes");

                        string overallTheme = _detectedContext switch
                        {
                            "lullaby" => "Peaceful bedtime story",
                            "party" => "Joyful birthday celebration",
                            "love" => "Romantic love journey",
                            "sad" => "Emotional reflection",
                            "adventure" => "Exciting exploration",
                            "dance" => "Joyful dance story",
                            "christmas" => "Magical Christmas story",
                            "animal" => "Animal friends adventure",
                            "school" => "Learning and discovery",
                            "nature" => "Beautiful nature journey",
                            _ => _selectedTheme == "calm" ? "Peaceful adventure" :
                                 _selectedTheme == "educational" ? "Learning adventure" :
                                 _selectedTheme == "action" ? "Exciting adventure" : "Fun adventure"
                        };

                        return new StoryBoard
                        {
                            Scenes = scenes,
                            MainCharacter = scenes[0]?.Characters ?? "Happy child",
                            OverallTheme = overallTheme
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                LogToMainWindow($"❌ Parse error: {ex.Message}");
            }

            return CreateFallbackStoryBoard();
        }

        private StoryBoard CreateFallbackStoryBoard()
        {
            LogToMainWindow("🔄 Using fallback story board");
            var scenes = new List<StoryScene>();

            string mainCharacter = _detectedContext switch
            {
                "lullaby" => "Sleeping baby in cozy crib",
                "party" => "Happy child at birthday party",
                "love" => "Couple walking in golden sunset",
                "sad" => "Child looking out rainy window",
                "adventure" => "Boy with backpack exploring forest",
                "dance" => "Girl spinning and dancing joyfully",
                "christmas" => "Children opening Christmas gifts",
                "animal" => "Child playing with cute puppy",
                "school" => "Children learning in colorful classroom",
                "nature" => "Child running through flower meadow",
                _ => "Happy child playing in sunny park"
            };

            string[] emotions = _detectedContext switch
            {
                "lullaby" => new[] { "sleepy", "peaceful", "calm", "dreamy", "gentle" },
                "party" => new[] { "excited", "joyful", "happy", "funny", "surprised" },
                "love" => new[] { "loving", "romantic", "happy", "nostalgic", "peaceful" },
                "sad" => new[] { "sad", "nostalgic", "calm", "peaceful", "hopeful" },
                "adventure" => new[] { "curious", "excited", "brave", "happy", "triumphant" },
                "dance" => new[] { "joyful", "excited", "playful", "happy", "energetic" },
                _ => new[] { "happy", "playful", "excited", "calm", "curious" }
            };

            string[] actions = _detectedContext switch
            {
                "lullaby" => new[] { "sleeping", "yawning", "snuggling", "dreaming", "resting" },
                "party" => new[] { "blowing candles", "opening gifts", "dancing", "laughing", "celebrating" },
                "love" => new[] { "walking together", "holding hands", "smiling", "hugging", "looking at sunset" },
                "sad" => new[] { "sitting quietly", "looking out window", "walking alone", "reflecting", "watching rain" },
                "adventure" => new[] { "exploring", "climbing", "running", "discovering", "hiking" },
                "dance" => new[] { "dancing", "spinning", "jumping", "moving", "swaying" },
                _ => new[] { "playing", "running", "dancing", "walking", "jumping" }
            };

            string[] locations = _detectedContext switch
            {
                "lullaby" => new[] { "cozy bedroom", "moonlit nursery", "starry night room", "soft lamp lit bedroom" },
                "party" => new[] { "birthday party room", "decorated garden", "festive hall", "colorful backyard" },
                "love" => new[] { "sunset beach", "flower garden", "park at golden hour", "romantic forest path" },
                "sad" => new[] { "rainy window", "empty park", "autumn forest", "grey cloudy day" },
                "adventure" => new[] { "forest clearing", "mountain path", "meadow", "hidden trail", "river bank" },
                "dance" => new[] { "dance floor", "sunny park", "colorful stage", "outdoor festival" },
                _ => new[] { "park", "garden", "playground", "sunny field" }
            };

            string[] keywordsList = _universalKeywords.ToArray();
            string[] ambientSounds = _detectedContext switch
            {
                "lullaby" => new[] { "soft lullaby music", "none", "gentle wind", "none", "soft music box" },
                "party" => new[] { "children laughing", "party noise", "children laughing", "joyful celebration sounds" },
                "sad" => new[] { "rain", "wind", "rain", "none", "distant birds" },
                "nature" => new[] { "birds", "leaves", "forest", "water", "wind" },
                "christmas" => new[] { "none", "fireplace", "children laughing", "none", "wind" },
                _ => new[] { "birds", "children laughing", "wind", "leaves", "playground" }
            };

            for (int i = 0; i < _lyricLines.Count; i++)
            {
                int energy = 3;
                if (_detectedContext == "lullaby")
                {
                    energy = 1;
                }
                else if (_detectedContext == "sad")
                {
                    energy = i < 2 ? 1 : 2;
                }
                else if (_detectedContext == "party" || _detectedContext == "dance")
                {
                    if (i < 1) energy = 2;
                    else if (i >= _lyricLines.Count - 1) energy = 2;
                    else energy = 4;
                }
                else
                {
                    if (i < 2) energy = 2;
                    else if (i > _lyricLines.Count - 3) energy = 2;
                    else if (i > 2 && i < _lyricLines.Count - 3) energy = 4;
                    if (_lyricLines.Count >= 10 && i == (int)(_lyricLines.Count * 0.7)) energy = 5;
                }

                scenes.Add(new StoryScene
                {
                    SceneNumber = i + 1,
                    Description = _lyricLines[i].Length > 50 ? _lyricLines[i].Substring(0, 47) + "..." : _lyricLines[i],
                    FullLyric = _lyricLines[i],  // ZERO-FALLBACK: originalni stih za StrictQueryEngine
                    Emotion = emotions[i % emotions.Length],
                    Energy = energy,
                    Characters = mainCharacter,
                    Action = actions[i % actions.Length],
                    Location = locations[i % locations.Length],
                    Keywords = keywordsList[i % keywordsList.Length],
                    AmbientSound = ambientSounds[i % ambientSounds.Length]
                });
            }

            string overallTheme = _detectedContext switch
            {
                "lullaby" => "Peaceful bedtime",
                "party" => "Joyful celebration",
                "love" => "Romantic love story",
                "sad" => "Emotional journey",
                "adventure" => "Exciting adventure",
                "dance" => "Joyful dance",
                "christmas" => "Magical Christmas",
                "animal" => "Animal friends",
                "school" => "Learning adventure",
                "nature" => "Nature beauty",
                _ => _selectedTheme == "calm" ? "Peaceful adventure" :
                       _selectedTheme == "educational" ? "Learning adventure" :
                       _selectedTheme == "action" ? "Exciting adventure" : "Fun adventure"
            };

            return new StoryBoard
            {
                Scenes = scenes,
                MainCharacter = mainCharacter,
                OverallTheme = overallTheme
            };
        }

        #endregion

        #region Pixabay Music API (samo za muziku, NE za zvukove)

        private async Task<string> DownloadBackgroundMusic(string mood, double targetDuration, string tempDir, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_pixabayApiKey))
                return null;

            string searchQuery = _detectedContext switch
            {
                "lullaby" => "lullaby soft piano baby sleep",
                "party" => "happy upbeat birthday kids celebration",
                "love" => "romantic piano soft acoustic love",
                "sad" => "melancholy piano emotional soft",
                "adventure" => "adventure orchestral energetic exploration",
                "dance" => "upbeat dance kids fun energetic",
                "christmas" => "christmas joyful holiday bells warm",
                "animal" => "playful cute whimsical animals fun",
                "school" => "playful educational kids learning fun",
                "nature" => "nature acoustic peaceful gentle outdoor",
                _ => mood?.ToLower() switch
                {
                    "happy" or "joyful" => "happy children upbeat",
                    "calm" or "peaceful" => "calm relaxing piano",
                    "excited" or "energetic" => "upbeat energetic fun",
                    "playful" => "playful funny kids",
                    "melancholy" => "melancholy emotional soft piano",
                    "romantic" => "romantic acoustic soft piano",
                    _ => "children happy uplifting"
                }
            };

            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await _apiRateLimiter.WaitAsync(ct);

                    try
                    {
                        string url = $"https://pixabay.com/api/music/?key={_pixabayApiKey}&q={Uri.EscapeDataString(searchQuery)}&duration=30-300";
                        string response = await _httpClient.GetStringAsync(url, ct);
                        var json = JObject.Parse(response);
                        var hits = json["hits"] as JArray;

                        if (hits == null || hits.Count == 0)
                        {
                            url = $"https://pixabay.com/api/music/?key={_pixabayApiKey}&q=happy%20kids&duration=30-300";
                            response = await _httpClient.GetStringAsync(url, ct);
                            json = JObject.Parse(response);
                            hits = json["hits"] as JArray;
                        }

                        if (hits != null && hits.Count > 0)
                        {
                            var rng = new Random(Guid.NewGuid().GetHashCode());
                            var hit = hits[rng.Next(Math.Min(5, hits.Count))];
                            string audioUrl = hit["audio"]?["url"]?.ToString();

                            if (!string.IsNullOrEmpty(audioUrl))
                            {
                                string fileName = $"background_{Guid.NewGuid().ToString().Substring(0, 8)}.mp3";
                                string outputPath = Path.Combine(tempDir, fileName);

                                LogToMainWindow($"🎵 Downloading background music: {hit["title"]?.ToString() ?? searchQuery}");

                                await DownloadToFileAsync(audioUrl, outputPath, ct);   // v1.4: the file is closed before anyone reads it

                                double duration = hit["duration"]?.Value<double>() ?? 0;
                                if (duration < targetDuration && duration > 0)
                                {
                                    return await LoopAudio(outputPath, targetDuration, tempDir, ct);
                                }

                                return outputPath;
                            }
                        }
                    }
                    finally
                    {
                        _apiRateLimiter.Release();
                    }
                }
                catch (Exception ex)
                {
                    LogToMainWindow($"❌ Error downloading music: {ex.Message}");
                }
            }

            return null;
        }

        private async Task<string> LoopAudio(string audioPath, double targetDuration, string tempDir, CancellationToken ct)
        {
            string outputPath = Path.Combine(tempDir, $"looped_{Guid.NewGuid().ToString().Substring(0, 8)}.mp3");
            string ffmpegPath = AppPaths.FFmpeg;

            if (!File.Exists(ffmpegPath))
                return audioPath;

            string args = $"-nostdin -stream_loop -1 -i \"{audioPath}\" -t {targetDuration.ToString(CultureInfo.InvariantCulture)} -c copy -y \"{outputPath}\"";
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    RedirectStandardInput = true
                }
            };
            process.Start();
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            await process.WaitForExitAsync(ct);

            return (process.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                ? outputPath : audioPath;
        }

        private async Task<double> GetAudioDuration(string audioPath)
        {
            try
            {
                string ffmpegPath = AppPaths.FFmpeg;
                if (!File.Exists(ffmpegPath)) return 180.0;

                string args = $"-nostdin -i \"{audioPath}\" -f null -";
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = args,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        RedirectStandardInput = true
                    }
                };
                process.Start();
                process.StandardInput.Close();
                var _so1 = process.StandardOutput.ReadToEndAsync();
                var _se1 = process.StandardError.ReadToEndAsync();
                await Task.WhenAll(_so1, _se1);
                await process.WaitForExitAsync();
                string output = _se1.Result;

                var match = System.Text.RegularExpressions.Regex.Match(output, @"Duration: (\d{2}):(\d{2}):(\d{2}\.\d{2})");
                if (match.Success)
                {
                    int hours = int.Parse(match.Groups[1].Value);
                    int minutes = int.Parse(match.Groups[2].Value);
                    double seconds = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                    return hours * 3600 + minutes * 60 + seconds;
                }
                return 180.0;
            }
            catch { return 180.0; }
        }

        #endregion

        #region Audio Processing (Lokalni zvukovi — bez Freesound)

        // AMBIENT FIX (v1.4): one sound file per ambient type for the whole video.
        // Before, every scene got its own temp copy, so MixAmbientWithMusic could not
        // tell that two neighbouring scenes use the SAME sound — it restarted the
        // recording from second 0 with a fade-out/fade-in on every cut (2-4 s), which
        // was heard as a stuttering, "pumping" background. With one path per type,
        // neighbouring scenes are merged into one continuous ambient region.
        private readonly Dictionary<string, string> _ambientFileByType =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private async Task<string> GetAmbientSoundPath(string soundType, string tempDir, CancellationToken ct)
        {
            string requested = soundType;
            soundType = MapToAmbientBed(soundType, _detectedContext);
            if (!string.IsNullOrEmpty(requested) && !string.Equals(requested, soundType, StringComparison.OrdinalIgnoreCase))
                LogToMainWindow($"🔊 Ambient '{requested}' → '{soundType}' (a continuous background recording instead of a single sound effect)");
            if (!string.IsNullOrEmpty(soundType) &&
                _ambientFileByType.TryGetValue(soundType, out string cachedPath) &&
                File.Exists(cachedPath))
                return cachedPath;

            LogToMainWindow($"🔊 GetAmbientSoundPath: soundType='{soundType}', context='{_detectedContext}', AmbientSoundsEnabled={_enableAmbientSounds}");

            if (!_enableAmbientSounds || string.IsNullOrEmpty(soundType) || soundType == "none")
            {
                LogToMainWindow($"🔊 Ambient sounds disabled or soundType empty");
                return null;
            }

            string localPath = await GetLocalAmbientSound(soundType, tempDir);
            if (localPath != null)
            {
                _ambientFileByType[soundType] = localPath;
                LogToMainWindow($"✅ Local ambient sound: {localPath}");
                return localPath;
            }

            LogToMainWindow($"⚠️ No ambient sound found for '{soundType}' in the local library.");
            return null;
        }

        // AMBIENT FIX (v1.4): maps the AI's free-text ambient description to one of the
        // known sound categories. Word-start matching instead of Contains(): before,
        // "freedom"/"random" matched "dom" (indoor), "train"/"brain" matched "rain",
        // "location"/"education" matched "cat", and the English word "more" matched
        // the Serbian "more" (sea) — so the AI's suggestion was often turned into
        // something unrelated.
        private static readonly (string[] Stems, string Result)[] _aiAmbientRules =
        {
            (new[] { "ptic", "bird", "cvrkut", "chirp", "=suma", "=sume", "forest", "drvec", "tree", "priro", "grana" }, "animal bird chirp"),
            (new[] { "=park", "setnj", "staz", "trail", "pjescan", "pescan", "korak", "footstep", "hodanj", "walk" }, "ambience nature trail"),
            (new[] { "djec", "dec", "dete", "djet", "child", "kid", "=igre", "playground", "play", "kretanj", "trcanj" }, "ambience children group playground"),
            (new[] { "zim", "snijeg", "sneg", "snow", "winter", "hladn", "mraz", "frost" }, "weather snow boots jumping"),
            (new[] { "=kisa", "=kise", "rain", "kaplj", "drip", "mokr" }, "weather ambience rain drips"),
            (new[] { "grmlj", "thunder", "oluj", "storm", "munj" }, "weather ambience thunderstorm"),
            (new[] { "vetar", "vetr", "vjetar", "vjetr", "wind", "breez", "povetar", "povjetar" }, "ambience nature field windy"),
            (new[] { "=voda", "=vode", "water", "potok", "rijek", "=reka", "stream", "creek", "river" }, "ambience creek stream"),
            (new[] { "=more", "=moru", "ocean", "=sea", "plaz", "beach", "talas", "wave", "shore" }, "ambience ocean shore"),
            (new[] { "=kuca", "=kuci", "restoran", "indoor", "=dom", "=domu", "=soba", "=sobi", "room", "home" }, "ambience backyard road"),
            (new[] { "=ljeto", "=leto", "summer", "sunc", "sunny", "topl", "warm", "cvrc", "cricket" }, "animal ambience crickets"),
            (new[] { "=noc", "=noci", "night", "zvezd", "zvijezd", "star", "mesec", "mjesec", "moon" }, "ambience night crickets"),
            (new[] { "dzungl", "jungle", "tropsk", "tropic", "majmun", "monkey" }, "animal ambience jungle"),
            (new[] { "=pas", "=psa", "dog", "puppy", "kucic", "bark" }, "animal dog bark"),
            (new[] { "konj", "horse", "kopit", "hoof", "hooves" }, "animal horse canters"),
            (new[] { "=macka", "=maca", "macak", "=cat", "=cats", "kitten", "meow" }, "animal mammal cat domestic meow"),
            (new[] { "=zaba", "frog" }, "animal frog chirp"),
        };

        private string NormalizeAmbientFromAI(string aiAmbient, string lyric, string context)
        {
            if (string.IsNullOrWhiteSpace(aiAmbient))
                return InferAmbientFromLyric(lyric, context);

            // The lyric itself is the most reliable signal: if it clearly names
            // something with a sound (rain, dog, sea...), trust it over the AI.
            if (TryInferAmbientFromLyric(lyric, context, out string fromLyric))
                return fromLyric;

            string lower = aiAmbient.ToLowerInvariant().Trim();

            var knownTypes = new[] {
                "animal bird", "ambience creek", "ambience stream", "ambience ocean",
                "ambience nature", "ambience children", "ambience backyard",
                "ambience downtown", "ambience dirt road", "ambience night",
                "animal dog", "animal horse", "animal frog", "animal mammal cat",
                "animal ambience crickets", "animal ambience jungle",
                "weather ambience rain", "weather ambience thunderstorm",
                "weather ambience hurricane", "weather ambience blizzard",
                "weather snow", "liquid water"
            };
            if (knownTypes.Any(k => lower.StartsWith(k)))
                return aiAmbient;

            string norm = SerbianText.Normalize(aiAmbient);
            foreach (var rule in _aiAmbientRules)
            {
                if (SerbianText.HasAny(norm, rule.Stems))
                    return rule.Result;
            }

            return InferAmbientFromLyric(lyric, context);
        }

        private static Dictionary<string, List<string>> _soundIndex = null;
        private static Dictionary<string, List<string>> _sfxIndex = null;
        private static readonly object _soundIndexLock = new object();

        private static Dictionary<string, List<string>> BuildSoundIndex(string folder)
        {
            var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(folder)) return index;

            var supportedExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".mp3", ".wav", ".flac", ".ogg", ".aiff", ".aif" };

            foreach (string file in Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories))
            {
                if (!supportedExt.Contains(Path.GetExtension(file))) continue;

                string name = Path.GetFileNameWithoutExtension(file).ToLower();

                var rawTokens = System.Text.RegularExpressions.Regex.Split(name, @"[_\-\s]+");
                var tags = new List<string>();
                foreach (string token in rawTokens)
                {
                    var camel = System.Text.RegularExpressions.Regex.Replace(
                        token, @"([a-z])([A-Z])", "$1 $2").ToLower().Split(' ');
                    tags.AddRange(camel.Where(t => t.Length > 1));
                }

                tags.Add(name.Replace("_", " ").Replace("-", " "));

                index[file] = tags;
            }

            return index;
        }

        private static readonly HashSet<string> _genericSoundWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "ambience", "ambient", "animal", "weather", "liquid", "area", "distant", "light", "heavy",
            "single", "small", "large", "medium", "loop", "the", "and", "with", "from", "mammal", "carnivore"
        };

        private static string FindBestMatch(Dictionary<string, List<string>> index,
                                            IEnumerable<string> queryTags,
                                            HashSet<string> usedFiles = null)
        {
            if (index == null || index.Count == 0) return null;

            var queryList = queryTags
                .Select(t => t.ToLower().Trim())
                .Where(t => t.Length > 1)
                .ToList();

            if (queryList.Count == 0) return null;

            string bestFile = null;
            double bestScore = 0;

            // v1.4: words that are in hundreds of file names ("Ambience ...", "Animal ...",
            // "Weather ...") count very little, and a file needs at least one specific word.
            // Before, "ambience" alone scored as much as "crickets", every "Ambience ..." file
            // tied, and the first one in the folder won — a lullaby got an air conditioner or
            // an auto repair shop as its background sound.
            foreach (var (file, tags) in index)
            {
                if (usedFiles != null && usedFiles.Contains(file)) continue;

                int score = 0;
                bool specific = false;
                foreach (string q in queryList)
                {
                    bool generic = _genericSoundWords.Contains(q);
                    if (tags.Any(t => t == q))
                    {
                        score += generic ? 1 : 6;
                        specific |= !generic;
                    }
                    else if (!generic && q.Length >= 4 && tags.Any(t => t.Length >= 4 && (t.StartsWith(q) || q.StartsWith(t))))
                    {
                        score += 2;
                        specific = true;
                    }
                }

                // ties go to the file with fewer extra words ("Ambience Nature 180" before
                // "Ambience Nature Trail 180" when the request is "ambience nature 180")
                double ranked = score - tags.Count * 0.01;
                if (specific && ranked > bestScore)
                {
                    bestScore = ranked;
                    bestFile = file;
                }
            }

            return bestScore > 0 ? bestFile : null;
        }

        // v1.4 — the background of a scene must be a CONTINUOUS recording (a field, a creek,
        // crickets at night). The library has hundreds of one-shot effects too (one bird chirp
        // of half a second, one dog bark, one footstep in snow). The mixer loops the chosen file
        // for the whole scene, so a one-shot became a machine-gun of the same chirp — that is
        // why the ambient sounds felt "pointless". Every category is mapped here to a bed that
        // exists in the library, and calm songs only get calm beds.
        private static readonly string[] _calmBeds =
        {
            "ambience nature 180", "ambience wind with crickets", "ambience night crickets",
            "ambience creek", "ambience stream", "ambience nature trail", "ambience wind blowing through trees",
            "weather ambience rain drips", "ambience nature near water", "ambience nature near swamp",
            "ambience ocean shore", "ambience nature field windy", "animal ambience crickets near a stream"
        };

        internal static string MapToAmbientBed(string soundType, string context)
        {
            if (string.IsNullOrWhiteSpace(soundType) || soundType == "none") return soundType;
            string s = soundType.ToLowerInvariant();
            bool calm = context is "lullaby" or "sad" or "love" or "calm";
            string bed;

            if (s.Contains("frog") || s.Contains("toad") || s.Contains("swamp")) bed = "ambience nature near swamp";
            else if (s.Contains("bird") || s.Contains("spring") || s.Contains("morning") ||
                     (s.Contains("chirp") && !s.Contains("cricket"))) bed = "ambience nature 180";
            else if (s.Contains("dog") || s.Contains("bark") || s.Contains("cat") || s.Contains("meow") ||
                     s.Contains("horse") || s.Contains("mammal") || s.Contains("canter")) bed = "ambience nature trail";
            else if (s.Contains("blizzard") || s.Contains("hurricane"))
                bed = calm ? "ambience wind blowing through trees" : "weather ambience wind blizzard snow";
            else if (s.Contains("snow") || s.Contains("winter") || s.Contains("boots") || s.Contains("footstep"))
                bed = "ambience wind blowing through trees";
            else if (s.Contains("splash") || s.Contains("liquid")) bed = "ambience nature near water";
            else if (s.Contains("creek") || s.Contains("stream") || s.Contains("river")) bed = "ambience creek";
            else if (s.Contains("thunder") || s.Contains("storm"))
                bed = calm ? "weather ambience rain drips" : "weather ambience rain thunderstorm";
            else if (s.Contains("backyard")) bed = "ambience nature 180";   // the only backyard bed is next to a road
            else if (s.Contains("night") || s.Contains("cricket"))
                bed = calm ? "ambience wind with crickets" : "ambience night crickets";
            else if (s.Contains("windy") || s.Contains("wind")) bed = calm ? "ambience wind blowing through trees" : "ambience nature field windy";
            else bed = soundType;

            if (context == "lullaby" && !_calmBeds.Any(b => bed.StartsWith(b, StringComparison.OrdinalIgnoreCase)))
                bed = "ambience nature 180";
            return bed;
        }

        /// <summary>v1.4 — length of a sound file in seconds, from its header (WAV) or its size (others).</summary>
        private static double EstimateSoundSeconds(string file)
        {
            try
            {
                var fi = new FileInfo(file);
                if (fi.Extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) && fi.Length > 44)
                {
                    using var fs = File.OpenRead(file);
                    var hdr = new byte[36];
                    if (fs.Read(hdr, 0, 36) == 36)
                    {
                        int byteRate = BitConverter.ToInt32(hdr, 28);
                        if (byteRate > 0) return (fi.Length - 44) / (double)byteRate;
                    }
                }
                return fi.Length / 24000.0;   // about 192 kbps for compressed files
            }
            catch { return 0; }
        }

        private static Dictionary<string, List<string>> _bedIndex = null;

        private async Task<string> GetLocalAmbientSound(string soundType, string tempDir)
        {
            string soundsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Sounds");

            lock (_soundIndexLock)
            {
                if (_soundIndex == null)
                {
                    _soundIndex = BuildSoundIndex(soundsDir);
                    LogToMainWindow($"🎵 Sound index: {_soundIndex.Count} files in Assets/Sounds/");
                }
            }

            if (_soundIndex.Count == 0)
            {
                LogToMainWindow($"⚠️ Assets/Sounds/ is empty or missing");
                return null;
            }

            // v1.4: machines, shops and vehicles are never right for a children's song — the
            // filter now applies to every song (before, a lullaby or a sad song skipped it),
            // unless the AI explicitly asked for a city sound.
            bool isOutdoorContext = !soundType.Contains("downtown", StringComparison.OrdinalIgnoreCase) &&
                                    !soundType.Contains("city", StringComparison.OrdinalIgnoreCase) &&
                                    !soundType.Contains("traffic", StringComparison.OrdinalIgnoreCase);

            var outdoorBlacklistTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "boiler", "factory", "transmission", "collision", "workshop", "repair",
                "construction", "wood chipper", "turbine", "generator", "compressor",
                "parking", "garage", "stairway", "restroom", "bathroom", "toilet",
                "sewer", "drain", "lockdown", "police", "dispatch", "scanner",
                "theater", "concession", "popcorn", "bowling", "hockey", "ice rink",
                "helicopter", "airplane", "plane", "train yard", "welding",
                "shipping", "stamp", "bottle return", "grocery", "vending",
                "interior", "inside", "indoor",
                "air conditioner", "ceiling fan", "platform fans", "appliance",
                "car drive", "car interior", "car parked", "car wash", "car exterior",
                "driving", "van driving", "road after rain from van",
                "machine", "equipment", "electrical", "lockdown",
            };

            var queryTags = soundType.ToLower()
                .Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length > 2)
                .ToList();

            var synonymMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["bird"] = new[] { "animal", "chirp", "goldfinch", "cowbird", "vireo", "tanager", "pewee", "stilt" },
                ["chirp"] = new[] { "bird", "animal", "goldfinch", "chirp" },
                ["animal"] = new[] { "ambience", "bird", "chirp", "bark", "frog" },

                ["creek"] = new[] { "ambience", "stream", "bridge", "woods", "water" },
                ["stream"] = new[] { "ambience", "creek", "water", "river", "flowing" },
                ["water"] = new[] { "liquid", "creek", "stream", "river", "ambience" },
                ["river"] = new[] { "liquid", "water", "flowing", "creek" },
                ["ocean"] = new[] { "ambience", "shore", "sea", "waves" },
                ["shore"] = new[] { "ambience", "ocean", "sea" },
                ["splash"] = new[] { "liquid", "water", "splashing", "hands" },
                ["liquid"] = new[] { "water", "splash", "pour", "creek" },

                ["children"] = new[] { "ambience", "group", "playground", "distant", "sprinkler" },
                ["playground"] = new[] { "ambience", "children", "group", "distant" },
                ["group"] = new[] { "ambience", "children", "playground" },
                ["sprinkler"] = new[] { "ambience", "children", "sprinkler" },

                ["nature"] = new[] { "ambience", "trail", "field", "windy", "wilderness", "swamp" },
                ["trail"] = new[] { "ambience", "nature", "trail" },
                ["wilderness"] = new[] { "ambience", "wilderness", "nature" },
                ["forest"] = new[] { "ambience", "nature", "trail", "woods", "creek" },
                ["woods"] = new[] { "ambience", "creek", "dirt", "road", "nature" },
                ["dirt"] = new[] { "ambience", "road", "woods", "dirt" },

                ["backyard"] = new[] { "ambience", "backyard", "road" },
                ["park"] = new[] { "ambience", "backyard", "children", "group" },
                ["garden"] = new[] { "ambience", "backyard", "nature" },

                ["downtown"] = new[] { "ambience", "downtown", "area", "city" },
                ["city"] = new[] { "ambience", "downtown", "road", "traffic" },
                ["urban"] = new[] { "ambience", "downtown", "traffic" },

                ["rain"] = new[] { "weather", "ambience", "drips", "drizzle", "light" },
                ["drizzle"] = new[] { "weather", "ambience", "rain", "drips" },
                ["drips"] = new[] { "weather", "ambience", "rain" },

                ["thunder"] = new[] { "weather", "ambience", "thunderstorm", "heavy", "rain" },
                ["storm"] = new[] { "weather", "ambience", "heavy", "rain", "thunder" },
                ["thunderstorm"] = new[] { "weather", "ambience", "thunder", "heavy" },

                ["wind"] = new[] { "weather", "ambience", "hurricane", "windy", "field" },
                ["windy"] = new[] { "ambience", "nature", "field", "windy" },
                ["breeze"] = new[] { "ambience", "nature", "field", "windy" },
                ["hurricane"] = new[] { "weather", "ambience", "hurricane", "wind", "gusts" },

                ["snow"] = new[] { "weather", "snow", "boots", "footstep", "jumping" },
                ["winter"] = new[] { "weather", "snow", "boots", "blizzard" },
                ["boots"] = new[] { "weather", "snow", "boots", "jumping" },
                ["blizzard"] = new[] { "weather", "ambience", "wind", "blizzard", "snow" },
                ["jumping"] = new[] { "weather", "snow", "boots", "jumping" },
                ["footstep"] = new[] { "weather", "snow", "footstep", "single" },

                ["night"] = new[] { "ambience", "night", "crickets", "bullfrog" },
                ["crickets"] = new[] { "ambience", "night", "crickets", "animal" },
                ["bullfrog"] = new[] { "ambience", "night", "bullfrog" },

                ["cricket"] = new[] { "animal", "ambience", "crickets", "stream" },
                ["insects"] = new[] { "animal", "ambience", "crickets", "jungle" },
                ["summer"] = new[] { "animal", "ambience", "crickets" },

                ["jungle"] = new[] { "animal", "ambience", "jungle", "rain", "forest", "insects" },
                ["tropical"] = new[] { "animal", "ambience", "jungle", "insects" },

                ["dog"] = new[] { "animal", "dog", "bark", "labrador", "maltese" },
                ["bark"] = new[] { "animal", "dog", "bark" },
                ["puppy"] = new[] { "animal", "dog", "bark" },

                ["horse"] = new[] { "animal", "horse", "canters", "gravel", "trots" },
                ["canters"] = new[] { "animal", "horse", "canters" },
                ["hooves"] = new[] { "animal", "horse", "canters", "trot" },

                ["cat"] = new[] { "animal", "mammal", "carnivore", "domestic", "meow" },
                ["meow"] = new[] { "animal", "mammal", "cat", "domestic", "meow" },
                ["domestic"] = new[] { "animal", "mammal", "cat", "domestic" },

                ["frog"] = new[] { "animal", "frog", "chirp", "california", "tree" },
                ["quack"] = new[] { "animal", "frog", "toad", "spadefoot" },

                ["outdoor"] = new[] { "ambience", "nature", "children", "backyard", "trail" },
                ["outside"] = new[] { "ambience", "nature", "backyard", "outdoor" },
                ["morning"] = new[] { "animal", "bird", "chirp", "ambience", "nature" },
                ["spring"] = new[] { "animal", "bird", "chirp" },
                ["gentle"] = new[] { "ambience", "nature", "trail", "bird" },
                ["quiet"] = new[] { "ambience", "nature", "trail" },
                ["calm"] = new[] { "ambience", "nature", "trail", "bird" },
            };

            var expandedTags = new HashSet<string>(queryTags, StringComparer.OrdinalIgnoreCase);
            foreach (string tag in queryTags.ToList())
            {
                if (synonymMap.TryGetValue(tag, out string[] syns))
                    foreach (string s in syns) expandedTags.Add(s);
            }

            // v1.4: only continuous recordings of at least 15 seconds can be a scene's background
            lock (_soundIndexLock)
            {
                if (_bedIndex == null)
                {
                    _bedIndex = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (file, tags) in _soundIndex)
                        if (EstimateSoundSeconds(file) >= 15) _bedIndex[file] = tags;
                    LogToMainWindow($"🔊 Background recordings (15 s or longer): {_bedIndex.Count} of {_soundIndex.Count} sound files");
                }
            }

            Dictionary<string, List<string>> filteredIndex = _bedIndex;
            if (isOutdoorContext && outdoorBlacklistTags.Count > 0)
            {
                filteredIndex = new Dictionary<string, List<string>>(_bedIndex.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var (file, tags) in _bedIndex)
                {
                    string fileName = Path.GetFileNameWithoutExtension(file).ToLower();
                    bool isBlacklisted = outdoorBlacklistTags.Any(bt =>
                        fileName.Contains(bt.ToLower()));
                    if (!isBlacklisted)
                        filteredIndex[file] = tags;
                }
                LogToMainWindow($"🔊 Filter active: {filteredIndex.Count}/{_bedIndex.Count} background recordings in search");
            }

            // v1.4: a file whose name starts with the requested words wins outright
            // ("ambience nature 180" → "Ambience Nature 180 01.wav"); the synonym search
            // below is only for requests that name no file directly.
            string wanted = soundType.Trim().ToLowerInvariant();
            string found = filteredIndex.Keys
                .Where(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant().StartsWith(wanted))
                .OrderBy(f => Path.GetFileName(f).Length)
                .FirstOrDefault()
                ?? FindBestMatch(filteredIndex, expandedTags);

            // v1.4: no fallback to the unfiltered library — no sound is better than a machine hum.

            if (found != null)
            {
                string ext = Path.GetExtension(found);
                string outputPath = Path.Combine(tempDir, $"ambient_{Guid.NewGuid().ToString().Substring(0, 8)}{ext}");
                await Task.Run(() => File.Copy(found, outputPath, true));
                LogToMainWindow($"🔊 Local sound: '{Path.GetFileName(found)}' for '{soundType}'");
                return outputPath;
            }

            LogToMainWindow($"🔊 No local sound found for '{soundType}' in Assets/Sounds/");
            return null;
        }

        private async Task<string> GetLocalSFX(string sfxType, string tempDir)
        {
            string sfxDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "SFX");

            lock (_soundIndexLock)
            {
                if (_sfxIndex == null)
                {
                    _sfxIndex = BuildSoundIndex(sfxDir);
                    if (_sfxIndex.Count > 0)
                        LogToMainWindow($"🎵 SFX index: {_sfxIndex.Count} files in Assets/SFX/");
                }
            }

            if (_sfxIndex == null || _sfxIndex.Count == 0) return null;

            var queryTags = sfxType.ToLower()
                .Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length > 1)
                .ToList();

            string found = FindBestMatch(_sfxIndex, queryTags);
            if (found == null) return null;

            string ext = Path.GetExtension(found);
            string outputPath = Path.Combine(tempDir, $"sfx_{Guid.NewGuid().ToString().Substring(0, 8)}{ext}");
            await Task.Run(() => File.Copy(found, outputPath, true));
            LogToMainWindow($"🎵 SFX: '{Path.GetFileName(found)}' for '{sfxType}'");
            return outputPath;
        }

        public static string GetSoundLibraryReport()
        {
            var sb = new System.Text.StringBuilder();
            string soundsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Sounds");
            string sfxDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "SFX");

            var allDirs = new[] { ("Sounds", soundsDir), ("SFX", sfxDir) };
            foreach (var (label, dir) in allDirs)
            {
                if (!Directory.Exists(dir)) continue;
                var idx = BuildSoundIndex(dir);
                sb.AppendLine($"\n═══ {label} ({idx.Count} files) ═══");
                foreach (var (file, tags) in idx.OrderBy(x => Path.GetFileName(x.Key)))
                    sb.AppendLine($"  {Path.GetFileName(file)}\n    Tags: {string.Join(", ", tags.Distinct().Take(8))}");
            }
            return sb.ToString();
        }

        // ── Ambient mix (rewritten in v1.4) ───────────────────────────────────
        // What was wrong before, and what this version does instead:
        //  1. Every scene (2-4 s) restarted its ambient file from second 0 with its own
        //     fade-in/out, even when the next scene used the same sound → audible
        //     stutter / "pumping". Now neighbouring scenes with the same sound become ONE
        //     continuous region, started at a random point inside the recording.
        //  2. Ambient was placed at TIMELINE time (logo + title + scene time) inside the
        //     song file, so every sound came in later than its picture by the logo+title
        //     length. Positions are now the scene's time in the song.
        //  3. The mix was cut with -t <effective duration>, then the render cut the start
        //     again with -ss → the last seconds of the SONG were lost whenever ambient
        //     sounds were on. The mix now keeps the full song length.
        //  4. The mix was encoded to MP3 and the render encoded it again (AAC) — two lossy
        //     passes over the song. The mix is now lossless WAV.
        //  5. Ambient now ducks under the music (sidechain on the vocal band), so it
        //     fills pauses and the instrumental parts instead of competing with the voice.
        //  6. aloop with a 2-billion-sample buffer per scene was slow and memory-hungry and
        //     often hit the 120 s timeout (silent fallback to music without ambient).
        //     Inputs now loop with -stream_loop, and a failure is announced, not silent.
        private sealed class AmbientRegion
        {
            public string Path;
            public double Start;   // seconds in the RAW music file
            public double End;
        }

        /// <summary>v1.4 — resampling to 48 kHz with a long (high-quality) filter instead of the default one.</summary>
        private const string HqResample = "aresample=48000:filter_size=64:phase_shift=10:cutoff=0.97";

        /// <summary>v1.4 — average loudness of a sound file in dB (FFmpeg volumedetect); NaN when unknown.</summary>
        private async Task<double> MeasureMeanVolumeDb(string path)
        {
            try
            {
                string ffmpeg = AppPaths.FFmpeg;
                if (!File.Exists(ffmpeg) || !File.Exists(path)) return double.NaN;
                var psi = new ProcessStartInfo(ffmpeg, $"-nostdin -i \"{path}\" -vn -af volumedetect -f null -")
                {
                    CreateNoWindow = true, UseShellExecute = false,
                    RedirectStandardError = true, RedirectStandardOutput = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return double.NaN;
                var errTask = proc.StandardError.ReadToEndAsync();
                var outTask = proc.StandardOutput.ReadToEndAsync();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                try { await proc.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException) { try { proc.Kill(true); } catch { } return double.NaN; }
                string err = await errTask;
                var m = System.Text.RegularExpressions.Regex.Match(err, @"mean_volume:\s*(-?[\d.]+)\s*dB");
                return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double db)
                    ? db : double.NaN;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Measuring loudness of {Path.GetFileName(path)}: {ex.Message}");
                return double.NaN;
            }
        }

        private async Task<string> MixAmbientWithMusic(string musicPath, List<StoryScene> scenes, double totalDuration, string tempDir)
        {
            if (!_enableAmbientSounds || scenes.All(s => string.IsNullOrEmpty(s.AmbientPath)))
            {
                LogToMainWindow("🔊 Ambient sounds disabled or no sounds to mix");
                return musicPath;
            }

            string ffmpegPath = AppPaths.FFmpeg;
            if (!File.Exists(ffmpegPath)) return musicPath;

            // v1.4: how far BELOW the song the background sits, in dB of average loudness.
            // Before, a fixed gain (0.14-0.26) was applied to recordings that are quiet to begin
            // with (a field, wind, crickets), and the result was ducked further under the music —
            // the background was practically inaudible. Now every recording is measured and set
            // to this distance from the song, whatever its own level.
            double ambientBelowSongDb = _detectedContext switch
            {
                "lullaby" => 20,
                "sad" => 19,
                "love" => 19,
                "nature" => 14,
                "outdoor" => 15,
                "health" => 15,
                "adventure" => 14,
                "christmas" => 15,
                "party" => 15,
                "dance" => 16,
                "fun" => 15,
                _ => 16
            };
            double musicMeanDb = await MeasureMeanVolumeDb(musicPath);
            var fileMeanDb = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            double RegionGain(string path)
            {
                if (!fileMeanDb.TryGetValue(path, out double own) || double.IsNaN(own) || double.IsNaN(musicMeanDb))
                    return 0.35;   // could not measure — a safe middle value
                double targetDb = musicMeanDb - ambientBelowSongDb;
                // v1.4: up to ×16 (+24 dB) — several field recordings are very quiet and hit the old ×8 limit
                return Math.Clamp(Math.Pow(10, (targetDb - own) / 20.0), 0.05, 16.0);
            }

            // The mix is the song file with ambience added, and the song is placed on the
            // timeline at the cursor — so a scene's ambience goes exactly at the scene's time
            // IN THE SONG (scene.StartTime), no extra offset.
            double ssOffset = 0.0;

            // ── 0. v1.4: one background per part of the song (about 15-20 s) ────
            // A different background every few seconds sounded random, and "keep the previous
            // sound" let the FIRST line decide for a minute (one "winter" word → wind over the
            // spring and summer lines). Now the scenes are grouped into parts of at least
            // 15 seconds, and in each part the sound that fits most of its lines (by time) wins.
            {
                var withSound = scenes
                    .Where(s => !string.IsNullOrEmpty(s.AmbientPath) && File.Exists(s.AmbientPath))
                    .OrderBy(s => s.StartTime)
                    .ToList();
                const double PartSeconds = 15.0;
                int idx = 0;
                while (idx < withSound.Count)
                {
                    var part = new List<StoryScene>();
                    double partStart = withSound[idx].StartTime, partEnd = partStart;
                    while (idx < withSound.Count && (part.Count == 0 || partEnd - partStart < PartSeconds))
                    {
                        part.Add(withSound[idx]);
                        partEnd = withSound[idx].StartTime + withSound[idx].Duration;
                        idx++;
                    }
                    string winner = part
                        .GroupBy(s => s.AmbientPath, StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(g => g.Sum(s => s.Duration))
                        .ThenBy(g => part.FindIndex(s => string.Equals(s.AmbientPath, g.Key, StringComparison.OrdinalIgnoreCase)))
                        .First().Key;
                    foreach (var s in part) s.AmbientPath = winner;
                }
            }

            // ── 1. Merge neighbouring scenes that use the same sound ─────────────
            const double MergeGap = 1.0;     // scenes closer than this share one region
            var regions = new List<AmbientRegion>();
            foreach (var sc in scenes
                .Where(s => !string.IsNullOrEmpty(s.AmbientPath) && File.Exists(s.AmbientPath) && s.Duration > 0.05)
                .OrderBy(s => s.StartTime))
            {
                double start = sc.StartTime + ssOffset;
                double end = start + sc.Duration;
                var last = regions.Count > 0 ? regions[regions.Count - 1] : null;
                if (last != null &&
                    string.Equals(last.Path, sc.AmbientPath, StringComparison.OrdinalIgnoreCase) &&
                    start - last.End <= MergeGap)
                {
                    last.End = Math.Max(last.End, end);
                }
                else
                {
                    regions.Add(new AmbientRegion { Path = sc.AmbientPath, Start = start, End = end });
                }
            }

            if (regions.Count == 0) return musicPath;

            int sceneCount = scenes.Count(s => !string.IsNullOrEmpty(s.AmbientPath));
            LogToMainWindow($"🔊 Mixing ambient sounds: {sceneCount} scenes → {regions.Count} continuous regions, {ambientBelowSongDb:F0} dB below the song (song average {musicMeanDb:F1} dB)");

            // ── 2. Build the filter graph ────────────────────────────────────────
            var inv = CultureInfo.InvariantCulture;
            var inputs = new List<string> { $"-i \"{musicPath}\"" };
            var filterParts = new List<string>
            {
                // Music: one copy for the mix, one (vocal band) as the ducking key
                // v1.4: 48 kHz with a high-quality resampler (the old plain 44.1 kHz step resampled
                // a 48 kHz song twice and dulled it), float all the way.
                $"[0:a]{HqResample},aformat=sample_fmts=fltp:channel_layouts=stereo,asplit=2[mus][key0]",
                "[key0]highpass=f=250,lowpass=f=3500[key]"
            };

            var rng = new Random();
            var fileDurations = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            const double Overlap = 0.4;      // neighbouring regions overlap → natural crossfade

            for (int r = 0; r < regions.Count; r++)
            {
                var reg = regions[r];
                bool hasPrev = r > 0 && regions[r - 1].End >= reg.Start - MergeGap;
                bool hasNext = r < regions.Count - 1 && regions[r + 1].Start <= reg.End + MergeGap;

                double start = Math.Max(0, reg.Start - (hasPrev ? Overlap : 0));
                double end = reg.End + (hasNext ? Overlap : 0);
                double dur = Math.Max(0.2, end - start);

                if (!fileDurations.TryGetValue(reg.Path, out double fileDur))
                {
                    fileDur = await GetAudioDuration(reg.Path);
                    fileDurations[reg.Path] = fileDur;
                }
                // Start somewhere inside the recording (not always at second 0), but
                // leave room so the region does not need to loop if the file is long enough.
                // GetAudioDuration returns exactly 180.0 when it cannot read the file — then don't seek.
                bool durationKnown = fileDur > 0 && Math.Abs(fileDur - 180.0) > 0.001;
                double maxOffset = durationKnown ? Math.Max(0, fileDur - dur - 0.5) : 0;
                double seek = maxOffset > 0.5 ? Math.Round(rng.NextDouble() * maxOffset, 2) : 0;

                double fadeIn = Math.Min(hasPrev ? Overlap * 2 : 0.8, dur / 3);
                double fadeOut = Math.Min(hasNext ? Overlap * 2 : 1.2, dur / 3);
                long delayMs = (long)Math.Round(start * 1000);

                if (!fileMeanDb.ContainsKey(reg.Path))
                    fileMeanDb[reg.Path] = await MeasureMeanVolumeDb(reg.Path);
                inputs.Add($"-stream_loop -1 -ss {seek.ToString("F2", inv)} -i \"{reg.Path}\"");
                int k = r + 1;
                filterParts.Add(
                    $"[{k}:a]{HqResample},aformat=sample_fmts=fltp:channel_layouts=stereo," +
                    $"atrim=duration={dur.ToString("F3", inv)},asetpts=PTS-STARTPTS," +
                    $"afade=t=in:st=0:d={fadeIn.ToString("F2", inv)}," +
                    $"afade=t=out:st={Math.Max(0, dur - fadeOut).ToString("F3", inv)}:d={fadeOut.ToString("F2", inv)}," +
                    $"volume={RegionGain(reg.Path).ToString("F3", inv)}," +
                    $"adelay={delayMs}:all=1[amb{k}]");

                LogToMainWindow($"🔊 Region {r + 1}: {Path.GetFileName(reg.Path)} {start:F1}s–{end:F1}s (from {seek:F1}s in file), gain ×{RegionGain(reg.Path):F2}");
            }

            // All ambient regions → one bed, padded with silence to the full song length so the
            // ducking filter gets two inputs of the same length (both end together).
            double musicDur = await GetAudioDuration(musicPath);
            string padToSong = musicDur > 1 && Math.Abs(musicDur - 180.0) > 0.001
                ? $",apad=whole_dur={musicDur.ToString("F3", inv)}"
                : "";
            if (regions.Count == 1)
                filterParts.Add($"[amb1]anull{padToSong}[bed]");
            else
                filterParts.Add(string.Concat(Enumerable.Range(1, regions.Count).Select(k => $"[amb{k}]")) +
                                $"amix=inputs={regions.Count}:duration=longest:normalize=0{padToSong}[bed]");

            // Duck the bed when the voice/music is loud, then mix with the song
            // v1.4: gentle ducking (at most about 6 dB, and only when the singing band is loud).
            // ratio=5 at a 0.04 threshold kept the bed pushed down for the whole song.
            filterParts.Add("[bed][key]sidechaincompress=threshold=0.12:ratio=2.5:attack=40:release=600:makeup=1[bedduck]");
            // v1.4: no limiter here any more. alimiter's default auto-level pumped the whole song
            // up and down; the float WAV below cannot clip, and the final render has a gentle
            // peak limiter (level=disabled) at the very end.
            filterParts.Add("[mus][bedduck]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[out]");

            string outputPath = Path.Combine(tempDir, $"mixed_audio_{Guid.NewGuid().ToString().Substring(0, 8)}.wav");
            string args = $"-nostdin -y {string.Join(" ", inputs)} " +
                          $"-filter_complex \"{string.Join(";", filterParts)}\" " +
                          $"-map \"[out]\" -c:a pcm_f32le -ar 48000 \"{outputPath}\"";

            var stderr = new StringBuilder();
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                }
            };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
            process.OutputDataReceived += (s, e) => { };

            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                LogToMainWindow("⚠️ Audio mixing exceeded timeout (300s) - using original music");
                AnnounceToUser(L("ambient_mix_failed"));
                return musicPath;
            }

            if (process.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 1000)
            {
                LogToMainWindow($"✅ Ambient sounds mixed: {regions.Count} regions, ducking under the vocals, lossless WAV");
                return outputPath;
            }

            string tail;
            lock (stderr) tail = stderr.ToString();
            if (tail.Length > 600) tail = tail.Substring(tail.Length - 600);
            LogToMainWindow($"⚠️ Ambient mixing failed (exit {process.ExitCode}) - using original music. FFmpeg: {tail}");
            AnnounceToUser(L("ambient_mix_failed"));
            return musicPath;
        }

        #endregion

        #region Transition Sounds

        private async Task<string> GetTransitionSound(string tempDir, string type = "pop")
        {
            if (!_enableTransitionSounds) return null;

            if (_transitionSoundCache.TryGetValue(type, out string cachedSource) && File.Exists(cachedSource))
            {
                string copyPath = Path.Combine(tempDir, $"transition_{type}_{Guid.NewGuid().ToString().Substring(0, 8)}.mp3");
                File.Copy(cachedSource, copyPath, true);
                return copyPath;
            }

            string soundFile = type == "pop" ? "transition-pop.mp3" : "transition-whoosh.mp3";
            string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Sounds", soundFile);
            if (File.Exists(localPath))
            {
                string outputPath = Path.Combine(tempDir, $"transition_{type}_{Guid.NewGuid().ToString().Substring(0, 8)}.mp3");
                File.Copy(localPath, outputPath, true);
                _transitionSoundCache[type] = outputPath;
                return outputPath;
            }

            try
            {
                string genPath = Path.Combine(tempDir, $"transition_{type}_{Guid.NewGuid().ToString().Substring(0, 8)}.wav");
                bool ok = type == "pop"
                    ? GeneratePopSound(genPath)
                    : GenerateWhooshSound(genPath);

                if (ok && File.Exists(genPath))
                {
                    _transitionSoundCache[type] = genPath;
                    LogToMainWindow($"🔊 Transition sound generated: {type}");
                    string copyPath = Path.Combine(tempDir, $"transition_{type}_{Guid.NewGuid().ToString().Substring(0, 8)}.wav");
                    File.Copy(genPath, copyPath, true);
                    return copyPath;
                }
            }
            catch (Exception ex)
            {
                LogToMainWindow($"⚠️ Sound generation error: {ex.Message}");
            }

            return null;
        }

        private bool GeneratePopSound(string outputPath)
        {
            const int sampleRate = 44100;
            const int channels = 2;
            double totalSeconds = 0.18;
            int totalSamples = (int)(sampleRate * totalSeconds);

            var samples = new float[totalSamples * channels];
            var rng = new Random(42);

            for (int i = 0; i < totalSamples; i++)
            {
                double t = (double)i / sampleRate;
                double tNorm = t / totalSeconds;

                double envelope;
                if (t < 0.005)
                    envelope = t / 0.005;
                else
                    envelope = Math.Exp(-18.0 * (t - 0.005));

                double fundamental = Math.Sin(2 * Math.PI * 80 * t) * 0.6;
                double harmonic = Math.Sin(2 * Math.PI * 160 * t) * 0.25;
                double click = (rng.NextDouble() * 2 - 1) * Math.Exp(-120 * t) * 0.3;

                double signal = (fundamental + harmonic + click) * envelope * 0.15;

                float sample = (float)Math.Clamp(signal, -1.0, 1.0);
                samples[i * channels] = sample;
                samples[i * channels + 1] = sample;
            }

            return WriteWav(outputPath, samples, sampleRate, channels);
        }

        private bool GenerateWhooshSound(string outputPath)
        {
            const int sampleRate = 44100;
            const int channels = 2;
            double totalSeconds = 0.45;
            int totalSamples = (int)(sampleRate * totalSeconds);

            var samples = new float[totalSamples * channels];
            var rng = new Random(42);

            double filterState = 0;
            const double filterCoeff = 0.12;

            for (int i = 0; i < totalSamples; i++)
            {
                double t = (double)i / sampleRate;
                double tNorm = t / totalSeconds;

                double envelope;
                if (tNorm < 0.18)
                    envelope = tNorm / 0.18;
                else if (tNorm < 0.60)
                    envelope = 1.0;
                else
                    envelope = 1.0 - (tNorm - 0.60) / 0.40;

                double noise = rng.NextDouble() * 2 - 1;
                filterState = filterState * (1 - filterCoeff) + noise * filterCoeff;

                double signal = filterState * envelope * 0.12;

                float sample = (float)Math.Clamp(signal, -1.0, 1.0);
                samples[i * channels] = sample;
                samples[i * channels + 1] = sample;
            }

            return WriteWav(outputPath, samples, sampleRate, channels);
        }

        private bool WriteWav(string path, float[] samples, int sampleRate, int channels)
        {
            try
            {
                using var writer = new NAudio.Wave.WaveFileWriter(
                    path,
                    NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels));
                writer.WriteSamples(samples, 0, samples.Length);
                return true;
            }
            catch (Exception ex)
            {
                LogToMainWindow($"⚠️ WriteWav error: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Video Processing sa B-roll za instrumentalne dijelove

        // ── RHYTHMIC PACING VARIATION ─────────────────────────────────────────────
        // Distribucija: 30% fast (1.5-2s), 50% standard (2.8-3.2s), 20% slow (5-6s)
        // Not random — follows Energy: high=fast, medium=standard, low=slow
        // Poziva se POSLE beat-snap korekcije da ne remeti audio sync,
        // ali SAMO ako nema lyric timestamps (instrumentalna muzika).
        // Ako postoje timestamps, trajanje je vezano za stihove — ne diramo ga.
        // ─────────────────────────────────────────────────────────────────────────
        private void ApplyRhythmicPacing(List<StoryScene> scenes, bool hasLyricTimestamps)
        {
            if (scenes == null || scenes.Count < 3) return;
            // When lyric timestamps exist, duration is determined by audio positions — do not change
            if (hasLyricTimestamps) return;

            var rng = new Random(42); // seed=42 → reproducibility between renders
            int total = scenes.Count;

            var byEnergy = scenes
                .Select((sc, idx) => new { sc, idx })
                .OrderByDescending(x => x.sc.Energy)
                .ToList();

            int fastCount = Math.Max(1, (int)Math.Round(total * 0.30));
            int slowCount = Math.Max(1, (int)Math.Round(total * 0.20));

            var fastIdxs = byEnergy.Take(fastCount).Select(x => x.idx).ToHashSet();
            var slowIdxs = byEnergy.TakeLast(slowCount).Select(x => x.idx).ToHashSet();

            for (int i = 0; i < scenes.Count; i++)
            {
                var sc = scenes[i];
                double prev = sc.Duration;

                if (fastIdxs.Contains(i))
                {
                    sc.PacingCategory = "fast";
                    sc.Duration = Math.Round(1.5 + rng.NextDouble() * 0.5, 2); // 1.5–2.0s
                }
                else if (slowIdxs.Contains(i))
                {
                    sc.PacingCategory = "slow";
                    sc.Duration = Math.Round(5.0 + rng.NextDouble() * 1.0, 2); // 5.0–6.0s
                }
                else
                {
                    sc.PacingCategory = "standard";
                    sc.Duration = Math.Round(2.8 + rng.NextDouble() * 0.4, 2); // 2.8–3.2s
                }

                LogToMainWindow($"🎬 Pacing [{sc.PacingCategory}] Scene {sc.SceneNumber} E={sc.Energy}: {prev:F1}s→{sc.Duration:F1}s");
            }
        }

        private async Task ProcessVideoCreation(string audioPath, double totalDuration)
        {
            _seenPixabayIds.Clear();
            _seenPexelsIds.Clear();
            _queryPageMap.Clear();
            _usedMediaUrls.Clear();
            _audioPath = audioPath;
            _totalDuration = totalDuration;

            btnGenerate.Content = "🎬 AI is creating story...";
            AnnounceToUser(L("ai_analyzing_song_story"), 5);

            var storyBoard = await GenerateStoryBoard(_lyricLines, _cts?.Token ?? CancellationToken.None);

            if (storyBoard?.Scenes == null || storyBoard.Scenes.Count == 0)
            {
                LogToMainWindow("❌ Story board was not generated, using fallback");
                storyBoard = CreateFallbackStoryBoard();
            }

            LogToMainWindow($"📖 Story created: {storyBoard.Scenes.Count} scenes");
            LogToMainWindow($"👤 Glavni lik: {storyBoard.MainCharacter}");
            LogToMainWindow($"🎬 Tema: {storyBoard.OverallTheme}");

            string ffmpegForBeat = AppPaths.FFmpeg;
            _beatInfo = await BeatDetection.AnalyzeAudio(audioPath, ffmpegForBeat, _cts?.Token ?? CancellationToken.None);
            if (_beatInfo.IsValid)
                LogToMainWindow($"🥁 Beat detection: {_beatInfo.BPM:F0} BPM, {_beatInfo.BeatTimes.Count} udaraca, confidence={_beatInfo.Confidence:F2}");
            else
                LogToMainWindow("🥁 Beat detection: no rhythm found, using Whisper durations");

            // Piano Mode log
            if (_beatInfo.PianoMode)
                LogToMainWindow($"🎹 Piano mode active: {_beatInfo.PhraseBeats.Count} melodic phrases, note density={_beatInfo.NoteDensity:F2} " +
                    $"({(_beatInfo.NoteDensity < 0.3 ? "tiha/spora" : _beatInfo.NoteDensity > 0.7 ? "brza/gusta" : "umjerena")}) " +
                    $"→ dynamic shot pacing {(4.5 - _beatInfo.NoteDensity * 2.7):F1}s average");

            _audioStartSeconds = _beatInfo.AudioStartSeconds;
            // ── LOOK-AHEAD: dynamic cut-advance based on BPM ─────────────────
            // 80BPM→80ms, 120BPM→100ms, 160BPM→120ms — skalira sa tempom pesme
            double bpmBasedAdvance = 60.0 + (_beatInfo.BPM / 120.0) * 50.0;
            _cutAdvanceMs = Math.Max(60, Math.Min(150, bpmBasedAdvance));

            if (_audioStartSeconds > 0.05)
                LogToMainWindow($"🔇 Audio start offset: {_audioStartSeconds:F2}s silence at start → video starts exactly when music does");
            LogToMainWindow($"✂️ Cut-advance: {_cutAdvanceMs:F0}ms look-ahead (BPM={_beatInfo.BPM:F0}) — rez ide ispred beata");

            // DURATION FIX: totalDuration mora biti efektivno trajanje audio streama,
            // ne raw trajanje fajla. RenderEngine koristi -ss AudioStartSeconds na audio inputu
            // which shortens the audio stream by exactly that value. If segments cover 177s
            // but audio plays only 169s (177-8), -shortest cuts output to 169s = 2:49.
            // Fix: planiramo segmente na effectiveDuration = rawDuration - AudioStartSeconds.
            if (_audioStartSeconds > 0.05)
            {
                double rawTotalDuration = totalDuration;
                totalDuration = Math.Max(10.0, totalDuration - _audioStartSeconds);
                _totalDuration = totalDuration;
                LogToMainWindow($"⏱ Duration correction: {rawTotalDuration:F1}s → {totalDuration:F1}s (subtracted {_audioStartSeconds:F2}s Beat-Lock offset)");
            }

            _lyricQueryCache.Clear();  // reset refrain cache za svaki novi video
            _ambientFileByType.Clear(); // AMBIENT FIX: new video → new temp folder → fresh sound choices

            bool visionOk = await VisionAnalyzer.InitializeAsync(LogToMainWindow, _cts?.Token ?? CancellationToken.None);
            LogToMainWindow(visionOk ? "🧠 VisionAnalyzer: ONNX active" : "🧠 VisionAnalyzer: FFmpeg mod");

            // Try Qwen2-VL — if successful, used as priority 1 ahead of ONNX
            bool qwenOk = await VisionAnalyzer.InitializeQwenAsync(LogToMainWindow, _cts?.Token ?? CancellationToken.None);
            if (qwenOk)
                LogToMainWindow("🤖 VisionAnalyzer: Qwen2-VL active — using AI image analysis");

            MotionAnalyzer.ClearCache();
            _lastClipMotion = null;
            _lastClipEndMotion = null;
            _queryThemeCooldown.Clear();
            _currentSceneIndex = 0;
            _lastSeasonTag = "none";
            _lastClipWarm = true;
            _lastClipWasOutdoor = true; // PATCH 10: reset
            _recentLuminance.Clear(); // PATCH 10: reset rolling luminance history

            _tempVideoFolder = Path.Combine(Path.GetTempPath(), $"UVE_Story_{Guid.NewGuid()}");
            Directory.CreateDirectory(_tempVideoFolder);

            // CLEANUP: Silently delete old UVE_* temp folders older than 12h (previous sessions).
            string _activeTempFolder = _tempVideoFolder;
            _ = Task.Run(() =>
            {
                try
                {
                    string tempRoot = Path.GetTempPath();
                    foreach (var prefix in new[] { "UVE_Story_", "UVE_MixAudio_", "UVE_Render_" })
                    {
                        foreach (var dir in Directory.GetDirectories(tempRoot, prefix + "*"))
                        {
                            try
                            {
                                if (dir.Equals(_activeTempFolder, StringComparison.OrdinalIgnoreCase)) continue;
                                if ((DateTime.Now - new DirectoryInfo(dir).LastWriteTime).TotalHours > 12)
                                    Directory.Delete(dir, true);
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            });

            _segments = new List<TimelineSegment>();

            _usedMediaUrls.Clear();
            _queryUseCount.Clear();
            _queryPageMap.Clear();

            int sceneCount = storyBoard.Scenes.Count;
            double estimatedSegDur = sceneCount > 0
                ? Math.Round(totalDuration / sceneCount, 2)
                : 0;

            // ── OutputProfile: koliko udaraca po kadru ────────────────────────────
            // YouTube Kids → 4 udarca (sporiji rez, manje stresno za decu)
            // Instagram/TikTok → 2 udarca (brzi rez)
            // Autorski → 6 udaraca (slobodniji ritam)
            // Lullaby uvek → 8 udaraca (maksimalno spoро)
            int beatsPerCut = _detectedContext switch
            {
                "lullaby" => 8,
                "sad" => 6,
                "adventure" or "dance" => 2,
                _ => 4  // YouTube Kids default
            };

            double MAX_LYRIC_SCENE_DURATION;
            if (_beatInfo != null && _beatInfo.IsValid && _beatInfo.BPM > 0)
            {
                // Formula: (60s / BPM) * beatsPerCut = sekunde po kadru
                double bpmBasedMax = Math.Round((60.0 / _beatInfo.BPM) * beatsPerCut, 2);
                // Clamp: never less than 1.5s (not enough time to see), never more than 8s
                MAX_LYRIC_SCENE_DURATION = Math.Max(1.5, Math.Min(8.0, bpmBasedMax));
                LogToMainWindow($"🥁 BPM {_beatInfo.BPM:F0} × {beatsPerCut} udarca = max {MAX_LYRIC_SCENE_DURATION:F1}s po kadru");
            }
            else
            {
                // Nema BeatInfo → context-based default
                MAX_LYRIC_SCENE_DURATION = _detectedContext switch
                {
                    "lullaby" => 6.0,
                    "sad" => 5.0,
                    "adventure" or "dance" => 2.5,
                    _ => 3.5   // YouTube Kids default bez BPM-a
                };
                LogToMainWindow($"🎬 No BPM, context '{_detectedContext}' → max {MAX_LYRIC_SCENE_DURATION}s per frame");
            }

            for (int si = 0; si < storyBoard.Scenes.Count; si++)
            {
                var sc = storyBoard.Scenes[si];
                if (_lyricTimestamps.Count > 0 && _lyricTimestamps.ContainsKey(si))
                {
                    // CUT-ADVANCE: rez se pravi _cutAdvanceMs ranije od audio peaka
                    // so the eye experiences the image change in the same millisecond the ear hears the vocal
                    double cutAdvanceSec = _cutAdvanceMs / 1000.0;
                    sc.StartTime = Math.Max(0, _lyricTimestamps[si] - cutAdvanceSec);

                    // WORD-SYNC: ako imamo word-level timestamps iz Whispera,
                    // try to find the scene keyword and snap StartTime to it.
                    // This way e.g. "sladoled" → clip starts exactly when that word is spoken.
                    // Works in ±2s window around lyric-level timestamps to avoid wrong matches.
                    if (_wordTimings != null && _wordTimings.Count > 0)
                    {
                        // Collect candidate keywords: Keywords, Action, first words of Description
                        var candidateWords = new List<string>();
                        if (!string.IsNullOrWhiteSpace(sc.Keywords))
                            candidateWords.AddRange(sc.Keywords.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries));
                        if (!string.IsNullOrWhiteSpace(sc.Action))
                            candidateWords.AddRange(sc.Action.Split(new[] { ' ', '_' }, StringSplitOptions.RemoveEmptyEntries));
                        if (!string.IsNullOrWhiteSpace(sc.FullLyric))
                            candidateWords.AddRange(sc.FullLyric.Split(' ').Take(4)); // prve 4 rijeci stiha

                        // Prozor pretrage: od stih-start do stih-start + MAX_LYRIC_SCENE_DURATION
                        double windowStart = _lyricTimestamps[si] - 0.5;
                        double windowEnd = _lyricTimestamps.ContainsKey(si + 1)
                            ? _lyricTimestamps[si + 1]
                            : _lyricTimestamps[si] + MAX_LYRIC_SCENE_DURATION;

                        AITranscription.WordTiming bestMatch = null;
                        foreach (var cw in candidateWords)
                        {
                            string cwNorm = cw.ToLowerInvariant().Trim('.', ',', '!', '?', ';', ':');
                            if (cwNorm.Length < 3) continue; // ignore short words (i, je, na...)
                            var match = _wordTimings.FirstOrDefault(wt =>
                                wt.StartSecond >= windowStart &&
                                wt.StartSecond <= windowEnd &&
                                wt.Word.ToLowerInvariant().Trim('.', ',', '!', '?') == cwNorm);
                            if (match != null)
                            {
                                bestMatch = match;
                                break;
                            }
                        }

                        if (bestMatch != null)
                        {
                            double wordSync = Math.Max(0, bestMatch.StartSecond - cutAdvanceSec);
                            LogToMainWindow($"   🔤 WordSync scene {si + 1}: '{bestMatch.Word}' @ {bestMatch.StartSecond:F2}s → StartTime {sc.StartTime:F2}s → {wordSync:F2}s");
                            sc.StartTime = wordSync;
                        }
                    }

                    double nxt = _lyricTimestamps.ContainsKey(si + 1)
                        ? _lyricTimestamps[si + 1]
                        : sc.StartTime + MAX_LYRIC_SCENE_DURATION;
                    double rawDur = Math.Max(1.0, Math.Round(nxt - sc.StartTime, 2));
                    // v1.4: the scene lasts until the next line starts. MAX_LYRIC_SCENE_DURATION is
                    // the longest single SHOT, not the longest scene: a long line (slow lullaby,
                    // instrumental after it) is filled with several clips (montage) below.
                    // Before, the scene was cut to one shot and the render then had to loop that
                    // one 6-second clip for up to 40 seconds.
                    sc.Duration = Math.Min(60.0, rawDur);

                    if (_beatInfo != null && _beatInfo.IsValid && _beatInfo.BeatTimes?.Count > 4)
                    {
                        double snapRadius = 0.150;
                        double nearest = _beatInfo.BeatTimes
                            .OrderBy(b => Math.Abs(b - sc.StartTime))
                            .FirstOrDefault();
                        double diff = Math.Abs(nearest - sc.StartTime);
                        if (diff > 0.02 && diff <= snapRadius)
                        {
                            sc.StartTime = Math.Round(nearest, 3);
                            if (_lyricTimestamps.ContainsKey(si + 1))
                            {
                                double maxDur = _lyricTimestamps[si + 1] - sc.StartTime;
                                sc.Duration = Math.Min(sc.Duration, Math.Max(1.0, maxDur));
                            }
                        }
                    }
                }
                else
                {
                    sc.StartTime = Math.Round(si * estimatedSegDur, 2);
                    sc.Duration = (si == storyBoard.Scenes.Count - 1)
                        ? Math.Min(MAX_LYRIC_SCENE_DURATION,
                            Math.Max(1.0, Math.Round(totalDuration - sc.StartTime, 2)))
                        : Math.Round(estimatedSegDur, 2);
                }
            }

            double actualLyricsEnd;
            if (_lyricTimestamps.Count > 0 && storyBoard.Scenes.Count > 0)
            {
                var lastScene = storyBoard.Scenes[storyBoard.Scenes.Count - 1];
                int lastIdx = storyBoard.Scenes.Count - 1;
                if (_lyricTimestamps.ContainsKey(lastIdx))
                {
                    double lastStart = _lyricTimestamps[lastIdx];
                    double lastEnd = _lyricTimestamps.ContainsKey(lastIdx + 1)
                        ? _lyricTimestamps[lastIdx + 1]
                        : lastScene.StartTime + lastScene.Duration;
                    actualLyricsEnd = lastEnd;
                }
                else
                {
                    actualLyricsEnd = lastScene.StartTime + lastScene.Duration;
                }
            }
            else
            {
                double cappedSegDur = Math.Min(estimatedSegDur, MAX_LYRIC_SCENE_DURATION);
                actualLyricsEnd = cappedSegDur * sceneCount;
            }

            double lyricsTotalDuration = Math.Round(actualLyricsEnd, 2);
            double remainingDuration = Math.Round(totalDuration - lyricsTotalDuration, 2);

            // ── Rhythmic Pacing: varirajemo trajanje scena po 30/50/20 distribuciji ──
            // Called AFTER computing StartTime/Duration, but ONLY for instrumental music
            ApplyRhythmicPacing(storyBoard.Scenes, _lyricTimestamps.Count > 0);

            LogToMainWindow($"📊 Audio duration: {FormatTime(totalDuration)} | Lyrics: {FormatTime(lyricsTotalDuration)} | Instrumental remainder: {FormatTime(Math.Max(0, remainingDuration))}");

            if (remainingDuration > 1.5)
            {
                LogToMainWindow($"🎵 Detected instrumental section: {FormatTime(remainingDuration)} (total duration: {FormatTime(totalDuration)}, lyrics: {FormatTime(lyricsTotalDuration)})");

                List<string> bRollKeywords = new List<string>();

                switch (_detectedContext)
                {
                    case "lullaby":
                        bRollKeywords.AddRange(new[] {
                            "peaceful sleeping baby soft light cozy bedroom",
                            "moonlight night stars quiet gentle atmosphere",
                            "soft clouds floating gentle time lapse sky",
                            "slow motion nature peaceful waterfall calm",
                            "baby crib mobile turning soft light",
                            "mother kissing baby forehead goodnight warm",
                            "starry night sky twinkling stars peaceful"
                        });
                        break;
                    case "party":
                        bRollKeywords.AddRange(new[] {
                            "colorful abstract shapes celebration joyful background",
                            "happy kids dancing colorful clothes energetic",
                            "confetti colorful festive celebration falling",
                            "celebration lights bokeh happy joyful",
                            "birthday cake candles children excited cheering",
                            "balloons floating up colorful celebration",
                            "children jumping happy celebration fun"
                        });
                        break;
                    case "sad":
                        bRollKeywords.AddRange(new[] {
                            "rain on window melancholy quiet indoor",
                            "autumn leaves falling slow motion lonely",
                            "empty park bench cloudy day grey",
                            "gentle water ripples calm peaceful reflective",
                            "person sitting alone window thinking",
                            "foggy morning misty forest quiet",
                            "candle flickering alone dark room"
                        });
                        break;
                    case "adventure":
                        bRollKeywords.AddRange(new[] {
                            "children exploring forest adventure excited",
                            "nature landscape cinematic mountains sunrise",
                            "adventure travel scenic journey hiking",
                            "mountains clouds time lapse dramatic",
                            "kids running through field grass happy",
                            "forest path sunlight dappled exploring",
                            "river crossing bridge adventure outdoors"
                        });
                        break;
                    case "dance":
                        bRollKeywords.AddRange(new[] {
                            "colorful abstract dance background movement",
                            "children dancing joyful colorful clothes spinning",
                            "children dancing party colorful balloons happy",
                            "happy kids dancing background energetic",
                            "dance floor colorful lights movement",
                            "spinning colorful skirts dancing joyful",
                            "children group dance happy celebration"
                        });
                        break;
                    case "christmas":
                        bRollKeywords.AddRange(new[] {
                            "christmas lights cozy background warm",
                            "snow falling slow motion winter wonderland",
                            "fireplace crackling warm glow cozy indoor",
                            "holiday decorations magical sparkle",
                            "children opening christmas gifts excited happy",
                            "snowflakes gently falling winter landscape",
                            "christmas tree ornaments twinkling lights"
                        });
                        break;
                    case "nature":
                        bRollKeywords.AddRange(new[] {
                            "nature landscape cinematic beautiful mountains",
                            "flowers blooming time lapse colorful garden",
                            "sunset clouds peaceful orange sky",
                            "forest river flowing gentle water nature",
                            "butterflies flying flowers meadow spring",
                            "waterfall cascade nature peaceful green",
                            "meadow flowers wind gentle breeze"
                        });
                        break;
                    case "outdoor":
                    case "health":
                        bRollKeywords.AddRange(new[] {
                            "children walking park path sunny morning cinematic",
                            "birds singing trees nature spring peaceful bokeh",
                            "stream flowing forest nature gentle peaceful",
                            "family walking together park golden hour warm",
                            "child running grass meadow joyful slow motion",
                            "flowers blooming spring garden colorful nature",
                            "sunrise morning park mist golden light cinematic",
                            "park bench children playing background sunny day",
                            "autumn leaves park path children walking colorful",
                            "winter children snow playing cozy warm colors",
                        });
                        break;
                    case "seasons":
                        bRollKeywords.AddRange(new[] {
                            "spring flowers blooming children garden colorful",
                            "summer beach children playing sunshine warm",
                            "autumn leaves falling park path golden colors",
                            "winter snow children playing cozy warm bokeh",
                            "rain puddles children boots splashing happy",
                            "children park golden hour sunset warm light",
                            "morning dew nature close-up cinematic beautiful",
                            "rainbow sky colorful nature children wonder",
                        });
                        break;
                    case "music":
                        bRollKeywords.AddRange(new[] {
                            "children singing choir joyful colorful",
                            "child playing piano happy lesson",
                            "kids dancing music classroom joyful",
                            "children instruments school band music",
                            "child headphones bedroom happy music listening",
                            "children singing together choir joyful classroom",
                            "girl boy guitar playing happy smiling",
                            "children clapping hands singing together fun",
                            "kids music class teacher colorful instruments",
                            "child microphone singing stage joyful performance"
                        });
                        break;
                    case "school":
                        bRollKeywords.AddRange(new[] {
                            "children classroom learning happy school",
                            "school desk pencil drawing children art",
                            "playground children running school recess",
                            "books colorful backpack school education",
                            "teacher children reading story classroom"
                        });
                        break;
                    case "animal":
                        bRollKeywords.AddRange(new[] {
                            "cute animals farm children petting",
                            "puppy dog playing children happy outdoor",
                            "baby animals cute nature soft",
                            "children zoo animals watching excited",
                            "kitten cat playing children home"
                        });
                        break;
                    default:
                        if (!string.IsNullOrEmpty(storyBoard?.OverallTheme))
                        {
                            string themeLower = storyBoard.OverallTheme.ToLower();
                            if (themeLower.Contains("muzik") || themeLower.Contains("pesm"))
                                bRollKeywords.AddRange(new[] { "music concert children stage", "musical notes colorful animation", "children singing together group" });
                            else if (themeLower.Contains("prirod") || themeLower.Contains("sum"))
                                bRollKeywords.AddRange(new[] { "nature outdoor children exploring", "forest path sunlight children", "meadow flowers wind children" });
                            else if (themeLower.Contains("porodic") || themeLower.Contains("ljubav"))
                                bRollKeywords.AddRange(new[] { "family together outdoor happy warm", "parents children hugging love", "home family cozy warm happy" });
                        }
                        if (bRollKeywords.Count == 0)
                            // FIX-BROLL: Extended query list for default/fun context
                            // Prioritet: djeca i emocije prvo, priroda kao fill-in
                            // SmartOutroPool rotira posljednjih 8 klipova → raznovrsniji outro
                            bRollKeywords.AddRange(new[] {
                                // Djeca i emocije — glavna vizuelna tema outra
                                "child closeup smiling face happy warm light",
                                "children laughing together outdoor playground",
                                "children friends playing together sunny park",
                                "child closeup eyes wonder curious nature",
                                "family walking park holding hands warm golden",
                                "parent child hands holding walking outdoor",
                                "family outdoor together golden hour warm light",
                                "child running meadow happy slow motion",
                                // Nature as visual breathing room between children's shots
                                "children playing park nature sunny day cinematic",
                                "kids jumping playing outdoor joyful bright",
                                "nature path sunlight dappled morning forest",
                                "child exploring curiosity outdoor nature bokeh",
                                "birds chirping morning trees nature peaceful",
                                "flowers garden colorful bokeh soft light nature",
                                "nature landscape beautiful sky cinematic aerial",
                                "stream brook water peaceful outdoor nature sounds",
                            });
                        break;
                }

                double bRollDurationPerClip = 4.0;
                int bRollCount = Math.Max(1, (int)Math.Ceiling(remainingDuration / bRollDurationPerClip));
                double actualDurationPerClip = remainingDuration / bRollCount;

                LogToMainWindow($"🎬 Creating {bRollCount} B-roll shots for the instrumental part ({actualDurationPerClip:F1}s each)");
                _currentLyric = null;          // v1.4: instrumental — the vision AI judges mood, not a lyric
                _lyricNamesSeason = false;

                double introPortion = Math.Min(remainingDuration * 0.4, 10.0);
                int introClips = Math.Max(1, (int)Math.Ceiling(introPortion / actualDurationPerClip));
                double introClipDuration = introPortion / introClips;

                double bRollStartTime = 0;
                string bRollMediaType = (cmbMediaType.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "video";

                var introSegments = new List<TimelineSegment>();
                for (int i = 0; i < introClips; i++)
                {
                    string kw = bRollKeywords[i % bRollKeywords.Count];
                    // FIX-CINEMATIC: Added cinematic quality terms for premium stock feel
                    // "soft cinematic lighting" → warmer, softer lighting
                    // "shallow depth of field" → bokeh efekat, ne flat stock look
                    // "candid moment" → authentic, not posed
                    string styleEnhance = _detectedContext switch
                    {
                        "lullaby" => "soft cinematic lighting gentle bokeh calm",
                        "party" => "colorful joyful candid moment bright",
                        "sad" => "soft cinematic lighting melancholy gentle mood",
                        "adventure" => "cinematic dynamic outdoor natural light",
                        "dance" => "rhythmic colorful soft cinematic lighting",
                        "christmas" => "warm cinematic cozy magical soft light",
                        "nature" => "cinematic natural light shallow depth of field",
                        _ => "soft cinematic lighting warm candid moment"
                    };
                    string enhancedQuery = $"{kw} {styleEnhance}";

                    AnnounceToUser(LF("b_roll_intro", i + 1, introClips), 5);
                    string bMediaPath = await SearchAndDownloadMedia(enhancedQuery, _pixabayMinHeight, bRollMediaType, _cts?.Token ?? CancellationToken.None, strictSeasonFilter: false);

                    if (string.IsNullOrEmpty(bMediaPath))
                    {
                        string altKw = bRollKeywords[(i + 1) % bRollKeywords.Count];
                        bMediaPath = await SearchAndDownloadMedia(altKw, _pixabayMinHeight, bRollMediaType, _cts?.Token ?? CancellationToken.None, strictSeasonFilter: false);
                    }

                    // Third attempt: ultra-safe generic query
                    if (string.IsNullOrEmpty(bMediaPath))
                    {
                        var introSafe = new[] { "children playing outdoor", "nature landscape sunny", "kids park happy", "flowers garden nature" };
                        bMediaPath = await SearchAndDownloadMedia(introSafe[i % introSafe.Length], _pixabayMinHeight, bRollMediaType, _cts?.Token ?? CancellationToken.None, strictSeasonFilter: false);
                    }

                    if (!string.IsNullOrEmpty(bMediaPath) && bRollMediaType == "video")
                    {
                        double bVidDur = await GetVideoDuration(bMediaPath);
                        if (Math.Abs(bVidDur - introClipDuration) > 0.5)
                            bMediaPath = await TrimVideoToDuration(bMediaPath, introClipDuration, _tempVideoFolder);
                    }

                    introSegments.Add(new TimelineSegment
                    {
                        Path = bMediaPath ?? "",
                        Duration = introClipDuration,
                        StartTime = bRollStartTime + i * introClipDuration,
                        Description = i == 0 ? "🎵 Uvod - instrumental" : "🎵 Uvod - nastavak",
                        LyricText = "",
                        AmbientSoundPath = null,
                        Energy = 2,
                        Emotion = _detectedContext == "lullaby" ? "calm" : "happy"
                    });
                    LogToMainWindow($"   B-roll uvod {i + 1}: {introClipDuration:F1}s - '{kw}' → {(string.IsNullOrEmpty(bMediaPath) ? "❌ nema medija" : "✅ OK")}");
                }

                for (int i = introSegments.Count - 1; i >= 0; i--)
                    _segments.Insert(0, introSegments[i]);

                double firstLyricTimestamp = _lyricTimestamps.Count > 0
                    ? _lyricTimestamps[0]
                    : 0.0;
                double shift = introPortion - firstLyricTimestamp;
                if (shift < 0) shift = 0;

                foreach (var scene in storyBoard.Scenes)
                    scene.StartTime += shift;

                double outroPortion = remainingDuration - introPortion;
                if (outroPortion > 0.5)
                {
                    // OUTRO-FIX: Koristimo fiksni max 3.5s po kadru (ne actualDurationPerClip koji
                    // can be ~64s when search fails for all shots except the first).
                    // Gemini analysis: last 74s video "locked" onto same stream shot.
                    // Cause: when SearchAndDownloadMedia returns "" for 16+ shots, all are skipped
                    // u sortedSegments petlji (File.Exists("") = false → continue).
                    // Fix: (1) max 3.5s/shot, (2) recycle pool of successful intro shots,
                    //      (3) fade-out tag na zadnjim 3 kadra outro sekcije.
                    const double OUTRO_MAX_CLIP_DURATION = 3.5;
                    double outroEffectiveDurPerClip = Math.Min(actualDurationPerClip, OUTRO_MAX_CLIP_DURATION);
                    int outroClips = Math.Max(1, (int)Math.Ceiling(outroPortion / outroEffectiveDurPerClip));
                    double outroClipDuration = outroPortion / outroClips;
                    double outroStartTime = totalDuration - outroPortion;

                    // REPETITION FIX: Recycle pool koristi SAMO lyrics segment putanje (sredina videa),
                    // NE intro klipove — to je uzrokovalo da se isti kadrovi pojavljuju u prvoj I drugoj polovini.
                    // Intro putanje se svjesno izostavljaju da izbjegnemo vizuelno ponavljanje.
                    var introPathSet = new HashSet<string>(
                        introSegments
                            .Where(s => !string.IsNullOrEmpty(s.Path))
                            .Select(s => s.Path),
                        StringComparer.OrdinalIgnoreCase);

                    var outroRecyclePool = _segments
                        .Where(s => !string.IsNullOrEmpty(s.Path) && File.Exists(s.Path)
                                    && !introPathSet.Contains(s.Path))
                        .Select(s => s.Path)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    // Ako lyrics pool prazan (nema dovoljno scena), dodaj zadnjih 50% intro segmenata
                    // (not all — only the second half, visually further from the start)
                    if (outroRecyclePool.Count < 3)
                    {
                        var introFallback = introSegments
                            .Where(s => !string.IsNullOrEmpty(s.Path) && File.Exists(s.Path))
                            .Select(s => s.Path)
                            .Skip(introSegments.Count / 2)  // samo druga polovina intro segmenata
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        foreach (var p in introFallback)
                            if (!outroRecyclePool.Contains(p))
                                outroRecyclePool.Add(p);
                    }

                    int recycleIdx = 0;

                    // Guaranteed ultra-safe fallback queries that always find results on Pixabay
                    var ultraSafeFallbacks = new[]
                    {
                        "children playing outdoor sunny day",
                        "nature landscape beautiful sky",
                        "kids running park happy",
                        "green meadow flowers sunshine",
                        "children laughing together",
                        "outdoor park trees sunlight",
                        "happy family outdoor",
                        "nature forest peaceful morning"
                    };

                    _currentLyric = null;      // v1.4: the outro is instrumental, not the last lyric line
                    _lyricNamesSeason = false;
                    for (int i = 0; i < outroClips; i++)
                    {
                        string kw = bRollKeywords[(introClips + i) % bRollKeywords.Count];
                        string styleEnhance = _detectedContext switch
                        {
                            "lullaby" => "soft cinematic lighting peaceful calm bokeh",
                            "party" => "colorful joyful candid moment warm fade",
                            "sad" => "soft cinematic melancholy gentle mood ending",
                            "adventure" => "cinematic dynamic outdoor natural conclusion",
                            "dance" => "rhythmic colorful soft cinematic fade",
                            "christmas" => "warm cinematic cozy magical bokeh ending",
                            "nature" => "cinematic shallow depth of field natural soft",
                            _ => "soft cinematic lighting warm candid moment"
                        };
                        string enhancedQuery = $"{kw} {styleEnhance}";

                        AnnounceToUser(LF("b_roll_outro", i + 1, outroClips), 92);
                        string bMediaPath = await SearchAndDownloadMedia(enhancedQuery, _pixabayMinHeight, bRollMediaType, _cts?.Token ?? CancellationToken.None, strictSeasonFilter: false);

                        if (string.IsNullOrEmpty(bMediaPath))
                        {
                            string altKw = bRollKeywords[(introClips + i + 1) % bRollKeywords.Count];
                            bMediaPath = await SearchAndDownloadMedia(altKw, _pixabayMinHeight, bRollMediaType, _cts?.Token ?? CancellationToken.None, strictSeasonFilter: false);
                        }

                        // Third attempt: ultra-safe generic query that always finds something
                        if (string.IsNullOrEmpty(bMediaPath))
                        {
                            string safeQuery = ultraSafeFallbacks[i % ultraSafeFallbacks.Length];
                            bMediaPath = await SearchAndDownloadMedia(safeQuery, _pixabayMinHeight, bRollMediaType, _cts?.Token ?? CancellationToken.None, strictSeasonFilter: false);
                            if (!string.IsNullOrEmpty(bMediaPath))
                                LogToMainWindow($"   ✅ Outro {i + 1}: ultra-safe fallback successful ('{safeQuery}')");
                        }

                        // OUTRO-FIX: If all attempts fail, recycle clip from pool
                        // umjesto da ostavimo prazan Path koji uzrokuje rupe u trajanju videa.
                        if (string.IsNullOrEmpty(bMediaPath) && outroRecyclePool.Count > 0)
                        {
                            bMediaPath = outroRecyclePool[recycleIdx % outroRecyclePool.Count];
                            recycleIdx++;
                            LogToMainWindow($"   ♻️ Outro {i + 1}: reciklirani kadar (pool={outroRecyclePool.Count})");
                        }

                        if (!string.IsNullOrEmpty(bMediaPath) && bRollMediaType == "video")
                        {
                            double bVidDur = await GetVideoDuration(bMediaPath);
                            if (Math.Abs(bVidDur - outroClipDuration) > 0.5)
                                bMediaPath = await TrimVideoToDuration(bMediaPath, outroClipDuration, _tempVideoFolder);
                        }

                        // Last 3 shots get fade-out tag for gradual ending
                        bool isFadeOutClip = (i >= outroClips - 3);
                        string outroDesc = isFadeOutClip
                            ? (i == outroClips - 1 ? "🎵 Outro - end|fadeout=1" : "🎵 Outro - fadeout|fadeout=1")
                            : "🎵 Outro - instrumental";

                        _segments.Add(new TimelineSegment
                        {
                            Path = bMediaPath ?? "",
                            Duration = outroClipDuration,
                            StartTime = outroStartTime + (i * outroClipDuration),
                            Description = outroDesc,
                            LyricText = "",
                            AmbientSoundPath = null,
                            Energy = 2,
                            Emotion = _detectedContext == "lullaby" ? "calm" : "happy"
                        });
                        LogToMainWindow($"   B-roll outro {i + 1}/{outroClips}: {outroClipDuration:F1}s - '{kw}' → {(string.IsNullOrEmpty(bMediaPath) ? "❌ nema medija" : "✅ OK")}{(isFadeOutClip ? " [fade-out]" : "")}");
                    }
                }

                totalDuration = introPortion + lyricsTotalDuration + outroPortion;
                LogToMainWindow($"📊 Nakon dodavanja B-roll: uvod={introPortion:F1}s, stihovi={lyricsTotalDuration:F1}s, outro={outroPortion:F1}s, UKUPNO={totalDuration:F1}s");
            }

            try
            {
                string mediaType = (cmbMediaType.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "video";
                int count = storyBoard.Scenes.Count;
                _currentSeason = string.IsNullOrEmpty(_detectedSeason) ? null : _detectedSeason; // start from the song's season

                for (int i = 0; i < count; i++)
                {
                    _cts?.Token.ThrowIfCancellationRequested();
                    var scene = storyBoard.Scenes[i];

                    // "candid natural authentic" se dodaje svuda — suprotno od stock-commercial look
                    string styleConsistency = _detectedContext switch
                    {
                        "lullaby" => "candid natural soft light cozy warm peaceful night children",
                        "party" => "candid natural colorful bright happy festive vibrant children",
                        "love" => "candid natural warm golden soft glow family children",
                        "sad" => "candid natural grey moody soft desaturated melancholy",
                        "adventure" => "candid natural outdoor bright energetic children lifestyle",
                        "dance" => "candid natural colorful joyful movement bright children",
                        "christmas" => "candid natural warm holiday lights cozy festive children",
                        "animal" => "candid natural cute nature soft light warm children",
                        "school" => "candid natural bright colorful educational warm children",
                        "nature" => "candid natural sunlight green peaceful children lifestyle",
                        "music" => "children joyful colorful bright fun",
                        "outdoor" => "children park playground sunny green happy",
                        "health" => "children running active park outdoor healthy sunny",
                        _ => "children soft cinematic lighting warm candid moment"
                    };
                    string energyBoost = scene.Energy >= 4 ? " action fast dynamic exciting" : scene.Energy <= 2 ? " calm peaceful slow gentle" : "";
                    string emotionBoost = scene.Emotion;

                    // ── ZERO-FALLBACK: StrictQueryEngine je jedini izvor query-a ─────────
                    // GetHardCodedQuery performs EXACT match on _actionMap.
                    // Ako nema mape → NULL → GenerateBlackFrame (vidljiv debug signal).
                    // BuildLiteralSearchQuery se NE koristi kao fallback.
                    // ─────────────────────────────────────────────────────────────────────
                    // ── ISKRA AI-FIRST: 3 sloja, nikad null ──────────────────────────────
                    // SLOJ 1: Ollama — glavni, dobija stih + kontekst pesme
                    // SLOJ 2: StrictQueryEngine._actionMap — fallback
                    // SLOJ 3: SmartFallback — nikad null, nikad crni ekran
                    // ─────────────────────────────────────────────────────────────────────
                    string lyricForQuery = !string.IsNullOrEmpty(scene.FullLyric) ? scene.FullLyric : scene.Description;
                    string primaryQuery = null;

                    // ── DYNAMIC SEASON PER LYRIC ─────────────────────────────────────────
                    // Each lyric can mention a different season — we do not hardcode a global one
                    // "Leti kupite sladoled" → summer, "Kad je zima nosi cizme" → winter
                    string lyricSeason = StrictQueryEngine.DetectSeasonFromLyric(lyricForQuery);
                    _lyricNamesSeason = lyricSeason != null;   // v1.4: strict season for this line's clips
                    LogToMainWindow($"   🗓 Season: song={_songContext.Season}, this line={lyricSeason ?? "none"}");

                    // FIX-SEASON: Uvijek sinkronizuj _currentSeason sa lyric sezonom
                    // Ranije: mijenjalo se samo ako lyricSeason != globalna sezona
                    // Problem: _currentSeason ostajao na staroj vrijednosti, StrictSeason filtrirao krive klipove
                    // Fix: if lyric has a season, it is authoritative; if not, keep previous lyric season
                    if (lyricSeason != null)
                    {
                        // Stih eksplicitno pominje sezonu — koristi je za ovaj kadar
                        _currentSeason = lyricSeason;
                        if (lyricSeason != _songContext.Season)
                        {
                            _songContext = new SongContext
                            {
                                AgeGroup = _songContext.AgeGroup,
                                Context = _songContext.Context,
                                Mood = _songContext.Mood,
                                Season = lyricSeason,
                                Setting = _songContext.Setting,
                                Theme = _songContext.Theme,
                                VisualStyle = _songContext.VisualStyle
                            };
                            LogToMainWindow($"   🗓 ✅ Season changed to: {lyricSeason}");
                        }
                    }
                    // Ako lyricSeason == null → _currentSeason ostaje isti kao prethodni stih (ispravno)

                    // LITERAL SYNC FIX — SLOJ 0: ActionMap direktni match (prije Ollame)
                    // Problem: Ollama overriduje actionMap pa "sladoled" → generic child query
                    // instead of exact "child eating ice cream".
                    // Solution: if actionMap has a direct lexical hit on a specific noun in the lyric,
                    // that query goes as PRIORITY and Ollama can only extend it, not replace it.
                    string actionMapDirectHit = null;
                    {
                        string directQuery = StrictQueryEngine.GetHardCodedQuery(lyricForQuery);
                        if (!string.IsNullOrEmpty(directQuery))
                        {
                            // Check if match is on a specific noun (object/action, not generic context)
                            // GetHardCodedQuery returns only if there is a direct keyword match in the lyric
                            actionMapDirectHit = StrictQueryEngine.ValidateAndFilter(directQuery, _songContext);
                            if (!string.IsNullOrEmpty(actionMapDirectHit))
                                LogToMainWindow($"   🎯 ActionMap direktni hit (prioritet): '{actionMapDirectHit}'");
                        }
                    }

                    // v1.4 LAYER 0: the director pass already planned this line against the
                    // whole song (one visual identity, chorus = same query). Use it directly —
                    // no second Ollama call per line. The literal ActionMap hit is kept as the
                    // next attempt below instead of overriding the director.
                    if (scene.KeywordsFromDirector && !string.IsNullOrWhiteSpace(scene.Keywords))
                    {
                        string directed = StrictQueryEngine.FilterOllamaQuery(scene.Keywords, _songContext);
                        if (!string.IsNullOrEmpty(directed))
                            primaryQuery = StrictQueryEngine.ValidateAndFilter(directed, _songContext);
                        if (!string.IsNullOrEmpty(primaryQuery))
                            LogToMainWindow($"   🎬 Director query: '{primaryQuery}'");
                    }

                    // v1.4 LITERAL FIRST: when the line names a concrete thing a child can see (ice cream,
                    // hot chocolate, a scooter, a ball, a dog...), that thing is shown — the director's
                    // mood shot is not. Gemini's review: "leti kupite sladoled, zimi topli čaj, može i
                    // topla čokolada" showed a snowy mountain with a ski lift and a French press.
                    // Deterministic, so a repeated chorus still gets the same picture.
                    if (!string.IsNullOrEmpty(actionMapDirectHit) &&
                        SerbianText.HasAny(SerbianText.Normalize(lyricForQuery), _concreteThingStems) &&
                        !string.Equals(primaryQuery, actionMapDirectHit, StringComparison.OrdinalIgnoreCase))
                    {
                        LogToMainWindow($"   🎯 The line names a concrete thing — '{actionMapDirectHit}' instead of '{primaryQuery}'");
                        primaryQuery = actionMapDirectHit;
                    }

                    // v1.4 CHILD IN EVERY SHOT: a director query without a child ("elderly couple
                    // walking", "snowy mountain") brought therapy, crowds and ski lifts. The literal
                    // query is used instead when there is one, otherwise a child is added to the search.
                    if (!string.IsNullOrEmpty(primaryQuery) && !MentionsChild(primaryQuery))
                    {
                        string before = primaryQuery;
                        primaryQuery = !string.IsNullOrEmpty(actionMapDirectHit) && MentionsChild(actionMapDirectHit)
                            ? actionMapDirectHit
                            : primaryQuery + " with child";
                        LogToMainWindow($"   👧 No child in '{before}' → '{primaryQuery}'");
                    }

                    // LAYER 1: Ollama — with semantic classification of the lyric (FIX Tag-Semantic Gap)
                    if (_ollamaRunning && string.IsNullOrEmpty(primaryQuery))
                    {
                        try
                        {
                            var lyricTag = StrictQueryEngine.ClassifyLyric(lyricForQuery);
                            var lyricSentiment = StrictQueryEngine.ClassifySentiment(lyricForQuery);

                            // FIX-CLOSEUP: Detekcija close-up stihova
                            // Kada stih pominje dete/lice/ruku/osmeh → forsiraj close-up hint u promptu
                            // Close-up shots hold the attention of children aged 3-7 much better than wide shots
                            bool needsCloseUp = false;
                            {
                                string lyricLow = lyricForQuery.ToLower();
                                var closeUpTriggers = new[] {
                                    "dete","dijete","decu","djece","mali","mala","malo","klinac","klinka",
                                    "ruku","rukicu","rukom","ruke","rucica","rucice",
                                    "oci","oce","ocima","lice","licem","lica",
                                    "osmeh","osmijeh","smeska","smesak","smije","smeje","smeje",
                                    "mama","tata","baka","deka","mamu","tatu"
                                };
                                needsCloseUp = closeUpTriggers.Any(t => lyricLow.Contains(t));
                            }
                            LogToMainWindow($"   🏷 LyricTag: {lyricTag} | Sentiment: {lyricSentiment} | CloseUp: {needsCloseUp}");
                            string ollamaPrompt = StrictQueryEngine.BuildOllamaPrompt(lyricForQuery, _songContext, lyricTag, lyricSentiment, needsCloseUp);
                            string ollamaRaw = await _ollama.GenerateAsync(ollamaPrompt, model: OllamaClient.QueryModel, ct: _cts?.Token ?? CancellationToken.None);
                            string ollamaFiltered = StrictQueryEngine.FilterOllamaQuery(ollamaRaw, _songContext);
                            if (!string.IsNullOrEmpty(ollamaFiltered))
                            {
                                primaryQuery = StrictQueryEngine.ValidateAndFilter(ollamaFiltered, _songContext);
                                if (!string.IsNullOrEmpty(primaryQuery))
                                    LogToMainWindow($"   🤖 Ollama query: '{primaryQuery}'");
                                else
                                    LogToMainWindow($"   ⚠️ Ollama query odbijen filterom: '{ollamaFiltered}'");
                            }
                            else
                            {
                                // FIX-OLLAMA-DEBUG: Log raw output to see why it is being rejected
                                string rawPreview = string.IsNullOrWhiteSpace(ollamaRaw)
                                    ? "(empty answer)"
                                    : ollamaRaw.Substring(0, Math.Min(80, ollamaRaw.Length)).Replace("\n", "↵").Replace("\r", "");
                                LogToMainWindow($"   ⚠️ Ollama did not return a usable query | Raw: '{rawPreview}'");
                            }

                            // LITERAL SYNC FIX: ActionMap direct hit beats Ollama
                            // "sladoled" → actionMap = "child eating ice cream" (konkretan)
                            // Ollama = "child running park joy" (generic) → must lose
                            if (!string.IsNullOrEmpty(actionMapDirectHit))
                            {
                                if (primaryQuery != actionMapDirectHit)
                                    LogToMainWindow($"   🎯 ActionMap override: '{primaryQuery}' → '{actionMapDirectHit}' (literal sync)");
                                primaryQuery = actionMapDirectHit;
                            }
                        }
                        catch (Exception ollamaEx)
                        {
                            LogToMainWindow($"   ⚠️ Ollama error: {ollamaEx.Message}");
                        }
                    }

                    // SLOJ 2: StrictQueryEngine fallback
                    if (string.IsNullOrEmpty(primaryQuery))
                    {
                        string mapQuery = StrictQueryEngine.GetHardCodedQuery(lyricForQuery);
                        if (!string.IsNullOrEmpty(mapQuery))
                        {
                            primaryQuery = StrictQueryEngine.ValidateAndFilter(mapQuery, _songContext);
                            if (!string.IsNullOrEmpty(primaryQuery))
                                LogToMainWindow($"   📖 Mapa fallback: '{primaryQuery}'");
                        }
                    }

                    // SLOJ 3: SmartFallback — nikad crni ekran
                    if (string.IsNullOrEmpty(primaryQuery))
                    {
                        primaryQuery = StrictQueryEngine.GetSmartFallback(_songContext);
                        LogToMainWindow($"   🔄 SmartFallback: '{primaryQuery}'");
                    }

                    // FIX-COOLDOWN: Anti-ponavljanje vizuelnih tema unutar 4 scene (~12-16s)
                    // Gemini: "certain visual motifs repeat (stream/forest shots)"
                    // Solution: normalize query to 2-3 keywords, track cooldown per theme
                    // If the same theme was used within QUERY_COOLDOWN_SCENES scenes → vary query
                    _currentSceneIndex++;
                    if (!string.IsNullOrEmpty(primaryQuery))
                    {
                        // Normalize query to first 2-3 words = "theme" (e.g. "children stream water" → "children stream")
                        var queryWords = primaryQuery.ToLower()
                            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                            .Where(w => w.Length > 3) // filtriraj kratke function words
                            .Take(2)
                            .ToArray();
                        string queryTheme = string.Join(" ", queryWords);

                        if (!string.IsNullOrEmpty(queryTheme) &&
                            _queryThemeCooldown.TryGetValue(queryTheme, out int lastUsed) &&
                            _currentSceneIndex - lastUsed < QUERY_COOLDOWN_SCENES)
                        {
                            // Ista tema se ponovila prebrzo — varira query sa energy/season kontekstom
                            // v1.4: the variant no longer adds season words — "child drinking hot chocolate"
                            // + "snow frost winter" found ski slopes and cable cars. Already used clips are
                            // skipped anyway, so the same search simply gives the next clip.
                            string variant = primaryQuery.Contains("child", StringComparison.OrdinalIgnoreCase) ||
                                             primaryQuery.Contains("kid", StringComparison.OrdinalIgnoreCase)
                                ? primaryQuery
                                : $"{primaryQuery} children";
                            LogToMainWindow($"   🔄 Cooldown variant (theme '{queryTheme}' was scene {lastUsed}): '{variant}'");
                            primaryQuery = variant;
                        }

                        // Update cooldown for this theme
                        _queryThemeCooldown[queryTheme] = _currentSceneIndex;
                    }

                    // Multi-anchor za duge scene
                    var multiMatches = StrictQueryEngine.GetAllHardCodedMatches(lyricForQuery);

                    // Setujemo instance fields da budu dostupni unutar SearchAndDownloadMedia
                    _currentLyric = scene.Description;
                    // FIX (v1.4): this line used to reset _currentSeason to the song's global
                    // season right here — after the per-lyric season had been detected above
                    // and just before the search. So "Kad je zima..." still searched and
                    // filtered for spring clips. _currentSeason now keeps the lyric's season
                    // (or the previous lyric's, if this line names none).

                    LogToMainWindow($"🎬 Scene {i + 1}: Energy={scene.Energy}, Action={scene.Action}, Duration={scene.Duration:F1}s");
                    LogToMainWindow($"   📝 Lyric: '{(lyricForQuery.Length > 50 ? lyricForQuery.Substring(0, 50) + "..." : lyricForQuery)}'");
                    LogToMainWindow($"   🎯 Query: '{primaryQuery}'" + (multiMatches.Count > 1 ? $" (+ {multiMatches.Count - 1} multi-anchor)" : ""));

                    // ── HYBRID CONTENT SELECTOR ────────────────────────────────────────────────
                    scene.ContentTag = DetermineContentTag(scene);
                    string effectiveMediaType;
                    if (scene.ContentTag == "Emotional" || scene.ContentTag == "Portrait")
                        effectiveMediaType = "photo";          // fotografija + ZoomPan
                    else if (scene.ContentTag == "Action" || scene.ContentTag == "Nature")
                        effectiveMediaType = "video";          // video snimak
                    else
                        effectiveMediaType = mediaType;        // default iz UI

                    LogToMainWindow($"⁠   🎞 ContentTag={scene.ContentTag ?? "UI-default"} → mediaType={effectiveMediaType}");
                    // ──────────────────────────────────────────────────────────────────

                    AnnounceToUser(LF("scene_progress", i + 1, count, scene.Description), 10 + (i * 70 / count));

                    string mediaPath = null;

                    // v1.4: an unexpected error while finding media for ONE scene used to end the
                    // whole video. Now it is logged, and the scene falls back to the safe query.
                    try
                    {
                        var subSceneQueries = DetectSubSceneQueries(scene.Description, styleConsistency);
                        if (subSceneQueries != null && subSceneQueries.Count >= 2 && mediaType == "video")
                        {
                            LogToMainWindow($"   🎬 Sub-scene detection: {subSceneQueries.Count} visual objects → clip montage");
                            mediaPath = await BuildSubSceneVideo(subSceneQueries, scene.Duration, _tempVideoFolder, _cts?.Token ?? CancellationToken.None);
                            if (mediaPath != null)
                                LogToMainWindow($"   ✅ Sub-scene video created: {subSceneQueries.Count} clips");
                            else
                                LogToMainWindow($"   ⚠ Sub-scene video failed, using the standard path");
                        }

                        // v1.4: a scene longer than two shots becomes a montage of video clips (a single
                        // photo or clip would otherwise be stretched or looped across the whole line)
                        bool longScene = scene.Duration > MAX_LYRIC_SCENE_DURATION * 2;
                        if (longScene && effectiveMediaType == "photo")
                        {
                            effectiveMediaType = "video";
                            LogToMainWindow($"   🎞 Scene {i + 1}: {scene.Duration:F1}s long → several video clips instead of one photo");
                        }

                        // ── ZERO-FALLBACK: Multi-anchor za duge scene ─────────────────────────
                        // If there are multiple hard-coded maps for this lyric → montage.
                        // Ako postoji samo jedna → jedini API poziv.
                        // If there is no map (primaryQuery from BuildLiteral) → one attempt then black screen.
                        // v1.4: only when the sub-scene montage above did not already make the scene —
                        // before, a finished montage was thrown away and a second one was downloaded.
                        if (string.IsNullOrEmpty(mediaPath) &&
                            multiMatches.Count >= 2 && effectiveMediaType == "video" && scene.Duration > 4.0)
                        {
                            LogToMainWindow($"   🎬 Multi-anchor: {multiMatches.Count} hard-coded queries → clip montage");
                            mediaPath = await BuildSubSceneVideo(multiMatches, scene.Duration, _tempVideoFolder, _cts?.Token ?? CancellationToken.None);
                            if (mediaPath != null)
                                LogToMainWindow($"   ✅ Multi-anchor video created");
                        }

                        if (string.IsNullOrEmpty(mediaPath) && effectiveMediaType == "video" &&
                            scene.Duration > Math.Max(6.0, MAX_LYRIC_SCENE_DURATION * 1.5) && !string.IsNullOrEmpty(primaryQuery))
                        {
                            // Long scene → try multi-clip with same query
                            mediaPath = await SearchAndDownloadMultipleMedia(
                                primaryQuery, primaryQuery, effectiveMediaType,
                                scene.Duration, _tempVideoFolder, _cts?.Token ?? CancellationToken.None);
                        }

                        // ── 3-ATTEMPT SYSTEM ─────────────────────────────────────────────────
                        // Attempt 1: primaryQuery (Ollama/map)
                        // Attempt 2: SmartFallback (contextual)
                        // Attempt 3: Absolute safe query
                        // Qwen screening is built into SearchAndDownloadMedia —
                        // it rejects bad clips and looks for better ones within the same search.
                        // This system changes THE QUERY ITSELF between attempts.
                        // Never black screen — at least one attempt always gives a result.
                        // ─────────────────────────────────────────────────────────────────────
                        var queryAttempts = new List<string>();
                        if (!string.IsNullOrEmpty(primaryQuery))
                            queryAttempts.Add(primaryQuery);
                        // v1.4: the literal ActionMap hit and the director's own fallback come before
                        // the generic SmartFallback (FallbackKeywords was planned but never used).
                        if (!string.IsNullOrEmpty(actionMapDirectHit) && !queryAttempts.Contains(actionMapDirectHit))
                            queryAttempts.Add(actionMapDirectHit);
                        string directorFallback = StrictQueryEngine.ValidateAndFilter(
                            StrictQueryEngine.FilterOllamaQuery(scene.FallbackKeywords, _songContext), _songContext);
                        if (!string.IsNullOrEmpty(directorFallback) && !queryAttempts.Contains(directorFallback))
                            queryAttempts.Add(directorFallback);
                        queryAttempts.Add(StrictQueryEngine.GetSmartFallback(_songContext));
                        // v1.4: the last, safe attempt keeps the line's season — a winter line that found
                        // nothing ended with no picture at all ("children playing outdoor sunny" is never
                        // a winter clip, so the strict season check refused it too)
                        queryAttempts.Add(_currentSeason switch
                        {
                            "winter" => "children playing in snow",
                            "autumn" => "children playing autumn leaves",
                            _ => "children playing outdoor sunny"
                        });

                        for (int attempt = 0; attempt < queryAttempts.Count && string.IsNullOrEmpty(mediaPath); attempt++)
                        {
                            string attemptQuery = queryAttempts[attempt];
                            string attemptLabel = attempt == 0 ? $"Attempt 1/{queryAttempts.Count} (primary)" :
                                                  attempt == queryAttempts.Count - 1 ? $"Attempt {attempt + 1}/{queryAttempts.Count} (safe)" :
                                                                $"Attempt {attempt + 1}/{queryAttempts.Count} (fallback)";
                            LogToMainWindow($"   🔍 {attemptLabel}: '{attemptQuery}'");

                            // FIX-SEASON-OBJECT: Some objects exist in all seasons (ice cream, tea, playing)
                            // ali Pixabay klipovi za njih mogu imati winter/summer tag koji se mimoilazi.
                            // For object queries → disable strictSeason filter so it doesn't reject valid clips.
                            bool seasonNeutralQuery = new[] {
                                "ice cream", "eating", "drinking", "tea", "hot chocolate",
                                "playing indoor", "birthday", "cake", "food"
                            }.Any(k => attemptQuery.ToLower().Contains(k));

                            mediaPath = await SearchAndDownloadMedia(
                                attemptQuery, _pixabayMinHeight, effectiveMediaType,
                                _cts?.Token ?? CancellationToken.None, scene.Duration,
                                strictSeasonFilter: !seasonNeutralQuery);
                            if (!string.IsNullOrEmpty(mediaPath))
                                LogToMainWindow($"   ✅ {attemptLabel} successful");
                            else
                                LogToMainWindow($"   ⚠️ {attemptLabel} did not return any media");
                        }
                    }
                    catch (Exception sceneEx) when (sceneEx is not OperationCanceledException)
                    {
                        AppLog.Error($"AI creator: finding media for scene {i + 1}", sceneEx);
                        LogToMainWindow($"   ⚠️ Scene {i + 1}: error while searching ({sceneEx.Message}) — trying the safe query");
                        mediaPath = null;
                        try
                        {
                            mediaPath = await SearchAndDownloadMedia(
                                "children playing outdoor sunny", _pixabayMinHeight, effectiveMediaType,
                                _cts?.Token ?? CancellationToken.None, scene.Duration, strictSeasonFilter: false);
                        }
                        catch (Exception safeEx) when (safeEx is not OperationCanceledException)
                        {
                            AppLog.Error($"AI creator: safe query for scene {i + 1}", safeEx);
                        }
                    }

                    if (!string.IsNullOrEmpty(mediaPath))
                    {
                        // _lastShotType je setovan unutar SearchAndDownloadMedia — prepisujemo na scenu
                        if (!string.IsNullOrEmpty(_lastShotType))
                            scene.ShotType = _lastShotType;

                        string finalPath = mediaPath;
                        if (effectiveMediaType == "video")
                        {
                            double videoDuration = await GetVideoDuration(mediaPath);
                            LogToMainWindow($"📹 Video duration: {videoDuration:F1}s, needed: {scene.Duration:F1}s");

                            if (videoDuration > scene.Duration + 0.5)
                            {
                                finalPath = await TrimVideoToDuration(mediaPath, scene.Duration, _tempVideoFolder);
                            }
                            else if (videoDuration < scene.Duration - 0.5)
                            {
                                LogToMainWindow($"   🔁 Short video ({videoDuration:F1}s < {scene.Duration:F1}s) — RenderEngine will loop");
                                finalPath = mediaPath;
                            }
                        }

                        string ambientPath = await GetAmbientSoundPath(scene.AmbientSound, _tempVideoFolder, _cts?.Token ?? CancellationToken.None);
                        if (ambientPath != null)
                        {
                            scene.AmbientPath = ambientPath;
                            LogToMainWindow($"🔊 Ambient sound for scene {i + 1}: {scene.AmbientSound}");
                        }

                        scene.VisionScore = _lastDownloadedVisionScore;
                        scene.IsStaticClip = _lastDownloadedIsStatic;
                        scene.HasChildren = _lastDownloadedHasChildren;
                        scene.IsOutdoor = _lastDownloadedIsOutdoor;

                        if (scene.IsStaticClip)
                            LogToMainWindow($"   🎬 Static clip — Ken Burns will be applied in render");

                        _segments.Add(new TimelineSegment
                        {
                            Path = finalPath,
                            Duration = scene.Duration,
                            StartTime = scene.StartTime,
                            Description = scene.Description,
                            LyricText = _showLyrics ? _lyricLines[i] : "",
                            LyricIndex = i,
                            Query = primaryQuery,
                            VisionDescription = _lastDownloadedDescription,
                            AmbientSoundPath = ambientPath,
                            Energy = scene.Energy,
                            Emotion = scene.Emotion,
                            MoodTag = $"mood:{scene.Emotion ?? _detectedMood}|context:{_detectedContext}|pacing:{scene.PacingCategory}|shottype:{scene.ShotType}|season:{_currentSeason ?? _detectedSeason ?? _songContext.Season ?? "none"}",
                            VisionScore = scene.VisionScore,
                            IsStaticClip = scene.IsStaticClip,
                            HasChildren = scene.HasChildren,
                            IsOutdoor = scene.IsOutdoor,
                            ContentTag = scene.ContentTag,        // Hybrid Content Selector
                            Sentiment = StrictQueryEngine.ClassifySentiment(
                                i < _lyricLines?.Count ? _lyricLines[i] : "").ToString()
                        });
                        LogToMainWindow($"✅ Scene {i + 1}: media downloaded");
                    }
                    else
                    {
                        LogToMainWindow($"❌ Scene {i + 1}: no media - skipping!");
                    }

                    await Task.Delay(300, _cts?.Token ?? CancellationToken.None);
                }

                // AMBIENT FIX (v1.4): the old block here copied _segments[idx].AmbientSoundPath
                // into storyBoard.Scenes[idx]. But _segments also contains the intro and
                // instrumental b-roll segments (added before the lyric loop) and skips scenes
                // with no media, so the indexes did not line up: lyric scenes received the
                // ambient sound of a DIFFERENT scene (e.g. a blizzard under a summer line).
                // scene.AmbientPath is already set directly in the loop above, so nothing
                // needs to be copied here.
                if (_enableAmbientSounds && storyBoard.Scenes.Any(sc => !string.IsNullOrEmpty(sc.AmbientPath)))
                    LogToMainWindow("🔊 Ambient sounds prepared — mixing with the video timeline in progress...");

                if (_segments.Count >= 8)
                {
                    // v1.4: only instrumental b-roll may be dropped. Removing a LYRIC scene left a line
                    // of the song without its own picture (the previous shot just ran longer) — clips
                    // below 4 can no longer get in anyway, so a 4-5 lyric clip stays.
                    var lowQuality = _segments
                        .Where(s => s.VisionScore < 5.5 && (s.Description ?? "").StartsWith("🎵") &&
                                    !string.IsNullOrEmpty(s.Path) && File.Exists(s.Path))
                        .OrderBy(s => s.VisionScore)
                        .Take(Math.Min(2, _segments.Count - 6))
                        .ToList();

                    foreach (var bad in lowQuality)
                    {
                        LogToMainWindow($"   🗑 Quality Gate: removing scene '{bad.Description}' (Score={bad.VisionScore:F1}/10)");
                        _segments.Remove(bad);
                    }

                    if (lowQuality.Count > 0)
                        LogToMainWindow($"✂️ Quality Gate: removed {lowQuality.Count} weak frame(s) — {_segments.Count} scenes remaining");
                }

                AnnounceToUser(L("generation_done_reviewing"), 95);
                LogToMainWindow("═══════════════════════════════════════");
                LogToMainWindow($"📋 SCENE OVERVIEW — {_segments.Count} scenes ready");
                LogToMainWindow("═══════════════════════════════════════");

                const double DISPLAY_CURSOR = 4.0;
                for (int si = 0; si < _segments.Count; si++)
                {
                    var seg = _segments[si];
                    string hasMedia = string.IsNullOrEmpty(seg.Path) || !File.Exists(seg.Path)
                        ? "❌ NO MEDIA" : "✅";
                    string lyric = !string.IsNullOrEmpty(seg.LyricText) ? $" | Lyric: \"{seg.LyricText}\"" : "";
                    double displayStart = DISPLAY_CURSOR + seg.StartTime;
                    LogToMainWindow($"  [{si + 1:D2}] {hasMedia} {FormatTime(displayStart)}-{FormatTime(displayStart + seg.Duration)} " +
                                   $"({seg.Duration:F1}s) — {seg.Description}{lyric}");
                }
                LogToMainWindow("═══════════════════════════════════════");

                int missingCount = _segments.Count(s => string.IsNullOrEmpty(s.Path) || !File.Exists(s.Path));
                if (missingCount > 0)
                    LogToMainWindow($"⚠ {missingCount} scenes have no media — will be skipped in render");
                else
                    LogToMainWindow($"✅ All scenes have media. Starting render...");

                // v1.4 SCENE REVIEW: hear what every clip shows and replace any of them
                // before the video is assembled (skipped for auto-render / unattended runs).
                if (_reviewScenes && _segments.Any(s => s.LyricIndex >= 0 && !string.IsNullOrEmpty(s.Path)))
                {
                    AnnounceToUser(L("review_scenes_open"), 96);
                    bool proceed = ShowSceneReview();
                    if (!proceed)
                        throw new OperationCanceledException();
                }

                GenerateValidationReport();

                AnnounceToUser(LF("review_done_creating", _segments.Count), 97);

                await CreateVideo(storyBoard);

                // ── AUTO-RENDER: pokreni odmah ako je korisnik postavio putanju ────
                if (AutoRenderEnabled)
                {
                    // Uzmi putanju direktno iz TextBox (korisnik je mogao da je promeni)
                    string autoPath = !string.IsNullOrEmpty(txtAutoRenderPath?.Text)
                        ? txtAutoRenderPath.Text.Trim()
                        : AutoRenderOutputPath;

                    if (string.IsNullOrEmpty(autoPath))
                    {
                        LogToMainWindow("⚠️ Auto-Render: save path not set");
                    }
                    else
                    {
                        AutoRenderOutputPath = autoPath;
                        LogToMainWindow($"🚀 Auto-Render: starting render → {autoPath}");
                        await TriggerAutoRender(autoPath);
                    }
                }
            }
            catch (Exception ex) { WpfMessageBox.Show(LF("generic_error", ex.Message), L("error_title"), MessageBoxButton.OK, MessageBoxImage.Error); }
            finally
            {
                btnGenerate.IsEnabled = true;
                btnGenerate.Content = "🎬 CREATE VIDEO";

                // v1.4 CLEANUP: only what the timeline does NOT use is deleted. Before, the whole
                // temporary folder was deleted after the auto-render — the clips the timeline
                // still pointed to went with it, so the project could not be rendered or saved
                // again — while rejected downloads in the project folder were never deleted.
                DeleteUnusedMedia(_tempVideoFolder);
            }
        }

        #endregion

        #region TrimVideoToDuration

        // ══════════════════════════════════════════════════════════════════════
        //  TriggerAutoRender — automatski render posle generisanja
        //  Poziva FinalRender_Click sa predefinisanom putanjom, bez dijaloga
        // ══════════════════════════════════════════════════════════════════════
        // ── Auto-Render UI event handlers ────────────────────────────────────
        private void chkAutoRender_Checked(object sender, System.Windows.RoutedEventArgs e)
        {
            if (pnlAutoRenderPath == null) return;
            bool isChecked = chkAutoRender.IsChecked == true;
            pnlAutoRenderPath.Visibility = isChecked
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
            AutoRenderEnabled = isChecked;
            if (isChecked && !string.IsNullOrEmpty(txtAutoRenderPath.Text))
                AutoRenderOutputPath = txtAutoRenderPath.Text;
        }

        private void btnAutoRenderBrowse_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var dlg = new WpfSaveFileDialog
            {
                Filter = "MP4 video|*.mp4",
                DefaultExt = "mp4",
                FileName = "iskra_video.mp4",
                Title = "Select where to save video"
            };
            if (dlg.ShowDialog() == true)
            {
                txtAutoRenderPath.Text = dlg.FileName;
                AutoRenderOutputPath = dlg.FileName;
            }
        }

        private async Task TriggerAutoRender(string outputPath)
        {
            try
            {
                var mainWindow = System.Windows.Application.Current?.MainWindow as MainWindow;
                if (mainWindow == null)
                {
                    LogToMainWindow("⚠️ Auto-Render: MainWindow is not available");
                    return;
                }

                // Osiguraj da folder postoji
                string folder = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                    Directory.CreateDirectory(folder);

                // Postavi currentProjectFolder ako nije postavljen
                if (string.IsNullOrEmpty(mainWindow.currentProjectFolder))
                    mainWindow.currentProjectFolder = folder ?? string.Empty;

                LogToMainWindow($"🎬 Auto-Render: output → {outputPath}");
                AnnounceToUser("Starting render...", 99);

                // Direktno pokretanje rendera sa output putanjom
                await mainWindow.StartRenderToPath(outputPath);
            }
            catch (Exception ex)
            {
                LogToMainWindow($"⚠️ Auto-Render error: {ex.Message}");
            }
        }

        private void GenerateValidationReport()
        {
            var segs = _segments.Where(s => !string.IsNullOrEmpty(s.Path) && File.Exists(s.Path)).ToList();
            if (segs.Count == 0) return;

            int totalSegs = segs.Count;
            double avgScore = segs.Average(s => s.VisionScore);
            int staticCount = segs.Count(s => s.IsStaticClip);
            // FIX-VALIDATION: Koristi IsOutdoor flag koji se setuje pri downloadu (TagBoost + ONNX)
            int outdoorCount = segs.Count(s => s.IsOutdoor);
            // FIX-VALIDATION: Koristi HasChildren flag koji se setuje pri downloadu (TagBoost + ONNX)
            // Previous code was reading s.Description (lyric text) — that always returned ~0%
            int childrenCount = segs.Count(s => s.HasChildren);
            // BUG-4 FIX: Subtract crossfade overlap so display is accurate
            // Crossfade oduzima (N-1) * avgFade od ukupnog trajanja
            double avgFadeEst = 0.346; // average fade from pacing logic
            double crossfadeOverlapEst = segs.Count > 1 ? (segs.Count - 1) * avgFadeEst : 0.0;
            double totalDur = Math.Max(0, segs.Sum(s => s.Duration) - crossfadeOverlapEst);
            int highEnergy = segs.Count(s => s.Energy >= 4);
            int lowEnergy = segs.Count(s => s.Energy <= 2);

            LogToMainWindow("═══════════════════════════════════════");
            LogToMainWindow("📊 VALIDATION REPORT — Automatska provjera kvaliteta");
            LogToMainWindow("═══════════════════════════════════════");
            LogToMainWindow($"  ✅ Total scenes: {totalSegs}");
            LogToMainWindow($"  ⏱ Total duration: {FormatTime(totalDur)}");
            LogToMainWindow($"  ⭐ Average Vision Score: {avgScore:F1}/10" +
                            (avgScore >= 6.5 ? " — Dobar" : avgScore >= 5.0 ? " — Prihvatljiv" : " — ⚠ Slab"));
            LogToMainWindow($"  🌿 Priroda/Outdoor kadrovi: {outdoorCount}/{totalSegs}" +
                            (outdoorCount >= totalSegs / 2 ? " ✅" : " ⚠ Malo outdoor"));
            double humanPresenceRatio = (double)childrenCount / totalSegs;
            LogToMainWindow($"  👧 Children/Family shots: {childrenCount}/{totalSegs}" +
                (childrenCount == 0 ? " ⚠️ No children in the footage!" :
                 humanPresenceRatio < 0.70 ? $" ⚠️ Only {humanPresenceRatio:P0} — the goal is at least 70%!" :
                 $" ✅ ({humanPresenceRatio:P0})"));
            LogToMainWindow($"  🎬 Static shots (Ken Burns applied): {staticCount}/{totalSegs}");
            LogToMainWindow($"  ⚡ High energy (scene 4-5): {highEnergy} scenes");
            LogToMainWindow($"  🌊 Low energy (scene 1-2): {lowEnergy} scenes");

            bool hasWarnings = false;
            if (avgScore < 5.0)
            { LogToMainWindow("  ⚠ WARNING: Average score is low. Consider regenerating."); hasWarnings = true; }
            if (outdoorCount < totalSegs / 3)
            { LogToMainWindow("  ⚠ WARNING: Few outdoor scenes — possible indoor/dark shots."); hasWarnings = true; }
            if (staticCount > totalSegs / 2)
            { LogToMainWindow($"  ⚠ INFO: More than half of scenes are static ({staticCount}) — Ken Burns applied to all."); }

            if (!hasWarnings)
                LogToMainWindow("  🎉 All checks passed — video should be free of 'nonsense'!");

            LogToMainWindow($"  🎵 Context: {_detectedContext} | Mood: {_detectedMood}");
            LogToMainWindow("  📝 Description: Shots are predominantly " +
                (outdoorCount > childrenCount ? "priroda i vanjski prostori" : "djeca i porodica") +
                $", ukupno {FormatTime(totalDur)} materijala.");
            LogToMainWindow("═══════════════════════════════════════");

            AnnounceToUser($"Validation Report: {totalSegs} scenes, average score {avgScore:F1}, " +
                $"{outdoorCount} outdoor, {childrenCount} children's shots.", 0);
        }

        private string _lastShotType = "";
        private string _lastSeasonTag = "none"; // prati sezonu prethodnog klipa za grupisanje
        private bool _lastClipWarm = true;   // prati temperaturu boja prethodnog klipa
        private bool _lastClipWasOutdoor = true; // PATCH 10: prati indoor/outdoor kontinuitet
        private readonly System.Collections.Generic.Queue<double> _recentLuminance
            = new System.Collections.Generic.Queue<double>(4); // PATCH 10: rolling warm avg (3 klipa)

        private async Task<bool> CheckBrightnessAndTint(string videoPath, string ffmpegPath, CancellationToken ct)
        {
            if (!File.Exists(ffmpegPath) || !File.Exists(videoPath)) return true;

            try
            {
                string args = $"-nostdin -i \"{videoPath}\" " +
                              // v1.4: metadata=print — signalstats prints nothing on its own, so this
                              // check never saw a number and let every dark clip through
                              $"-vf \"select=eq(n\\,5),signalstats,metadata=mode=print\" -frames:v 1 " +
                              $"-f null -";

                var psi = new ProcessStartInfo(ffmpegPath, args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                using var proc = Process.Start(psi);
                var soTask = proc.StandardOutput.ReadToEndAsync();
                var seTask = proc.StandardError.ReadToEndAsync();
                await Task.WhenAll(soTask, seTask);
                await proc.WaitForExitAsync(ct);
                string output = seTask.Result + soTask.Result;

                var yavgMatch = System.Text.RegularExpressions.Regex.Match(output, @"YAVG[=:](\d+\.?\d*)");
                if (yavgMatch.Success && double.TryParse(yavgMatch.Groups[1].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double yavg))
                {
                    // v1.4: 60 instead of 40 — silhouettes against windows and dim rooms passed before
                    if (yavg < 60)
                    {
                        LogToMainWindow($"   📊 Brightness {yavg:F0}/255 — too dark (min=60)");
                        return false;
                    }
                    if (yavg > 235)
                    {
                        LogToMainWindow($"   📊 Brightness {yavg:F0}/255 — preekspozirano (max=235)");
                        return false;
                    }
                }

                // v1.4: signalstats has no RAVG/GAVG, so the pink check never ran. A pink cast is a
                // high red-difference (V) together with a not-low blue-difference (U); orange (V high,
                // U low) is warm light and stays allowed.
                var uavgMatch = System.Text.RegularExpressions.Regex.Match(output, @"UAVG[=:](\d+\.?\d*)");
                var vavgMatch = System.Text.RegularExpressions.Regex.Match(output, @"VAVG[=:](\d+\.?\d*)");
                if (uavgMatch.Success && vavgMatch.Success)
                {
                    double uavg = double.Parse(uavgMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    double vavg = double.Parse(vavgMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    double pinkDiff = vavg - 128;

                    if (pinkDiff > 14 && uavg >= 126)
                    {
                        LogToMainWindow($"   📊 Pink colour cast (V={vavg:F0}, U={uavg:F0})");
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                return true;
            }
        }

        private async Task<string> TrimVideoToDuration(string inputPath, double targetDuration, string tempDir)
        {
            var sw = Stopwatch.StartNew();
            LogToMainWindow($"✂️ Trim/Loop: {Path.GetFileName(inputPath)} → {targetDuration:F1}s");

            try
            {
                string outputPath = Path.Combine(tempDir, $"trimmed_{Guid.NewGuid().ToString().Substring(0, 8)}.mp4");
                string ffmpegPath = AppPaths.FFmpeg;
                if (!File.Exists(ffmpegPath)) return inputPath;

                double actualDuration = await GetVideoDuration(inputPath);

                string args;
                if (actualDuration >= targetDuration - 0.5)
                {
                    // Re-enkodujemo umjesto -c copy da normalizujemo FPS na 25 i izbjegnemo
                    // stuttering at joins when the original clip has a different framerate.
                    bool nvencOk = false;
                    try
                    {
                        string nvencTestArgs = $"-f lavfi -i color=c=black:s=64x64:d=0.1 -c:v h264_nvenc -f null -";
                        var nvencTestProc = new System.Diagnostics.Process
                        {
                            StartInfo = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = ffmpegPath,
                                Arguments = nvencTestArgs,
                                CreateNoWindow = true,
                                UseShellExecute = false,
                                RedirectStandardError = true,
                                RedirectStandardOutput = true
                            }
                        };
                        nvencTestProc.Start();
                        var nvecSo = nvencTestProc.StandardOutput.ReadToEndAsync();
                        var nvecSe = nvencTestProc.StandardError.ReadToEndAsync();
                        await Task.WhenAll(nvecSo, nvecSe);
                        await nvencTestProc.WaitForExitAsync();
                        string nvecOut = nvecSe.Result;
                        nvencOk = !nvecOut.Contains("No NVENC") && !nvecOut.Contains("Cannot load") && !nvecOut.Contains("Unknown encoder");
                    }
                    catch { nvencOk = false; }

                    string encArgs = nvencOk
                        ? "-c:v h264_nvenc -preset p2 -rc vbr -cq 23 -b:v 0 -profile:v high -level 4.1"
                        : "-c:v libx264 -preset veryfast -crf 23 -profile:v high -level 4.1";

                    args = $"-ss 0 -i \"{inputPath}\" -t {targetDuration.ToString(CultureInfo.InvariantCulture)} " +
                           $"-vf \"scale={_targetWidth}:{_targetHeight}:flags=lanczos,fps=25,format=yuv420p\" " +
                           $"{encArgs} -fps_mode cfr -pix_fmt yuv420p -an -avoid_negative_ts make_zero -y \"{outputPath}\"";
                    LogToMainWindow($"   ✂ Trim+normalizacija FPS: {actualDuration:F1}s → {targetDuration:F1}s @ 25fps [{_targetResolution}]");
                }
                else
                {
                    LogToMainWindow($"   🔁 Short video ({actualDuration:F1}s < {targetDuration:F1}s) — RenderEngine will loop");
                    return inputPath;
                }

                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = args,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    }
                };
                process.Start();
                var trimSo = process.StandardOutput.ReadToEndAsync();
                var trimSe = process.StandardError.ReadToEndAsync();
                await Task.WhenAll(trimSo, trimSe);
                await process.WaitForExitAsync();

                if (process.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                {
                    LogToMainWindow($"✅ Trim/Loop successful in {sw.ElapsedMilliseconds}ms");
                    return outputPath;
                }

                LogToMainWindow($"⚠️ Trim/Loop failed — using original");
                return inputPath;
            }
            catch (Exception ex)
            {
                LogToMainWindow($"❌ Trim error: {ex.Message}");
                return inputPath;
            }
        }

        #endregion

        private async Task<double> GetVideoDuration(string videoPath)
        {
            try
            {
                string ffmpegPath = AppPaths.FFmpeg;
                if (!File.Exists(ffmpegPath)) return 5.0;

                string args = $"-nostdin -i \"{videoPath}\" -f null -";
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = args,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        RedirectStandardInput = true
                    }
                };
                process.Start();
                process.StandardInput.Close();
                var _so1 = process.StandardOutput.ReadToEndAsync();
                var _se1 = process.StandardError.ReadToEndAsync();
                await Task.WhenAll(_so1, _se1);
                await process.WaitForExitAsync();
                string output = _se1.Result;

                var match = System.Text.RegularExpressions.Regex.Match(output, @"Duration: (\d{2}):(\d{2}):(\d{2}\.\d{2})");
                if (match.Success)
                {
                    int hours = int.Parse(match.Groups[1].Value);
                    int minutes = int.Parse(match.Groups[2].Value);
                    double seconds = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                    return hours * 3600 + minutes * 60 + seconds;
                }
                return 5.0;
            }
            catch { return 5.0; }
        }

        private async Task<string> SearchAndDownloadMedia(string keywords, int minWidth, string mediaType, CancellationToken ct, double minDurationSeconds = 0, bool strictSeasonFilter = true)
        {
            _lastDownloadedDescription = null; // v1.4: never show the previous clip's description
            // ZERO-FALLBACK guard: null keywords → immediately null (caller will GenerateBlackFrame)
            if (string.IsNullOrWhiteSpace(keywords)) return null;
            LogToMainWindow($"🔍 SearchAndDownloadMedia: '{keywords.Substring(0, Math.Min(60, keywords.Length))}...', type={mediaType}");

            // v1.4: one shared Pixabay key (the same one the Media providers window edits)
            string apiKey = MediaProviderSettings.LoadPixabayKey();

            if (string.IsNullOrEmpty(apiKey))
            {
                string newKey = null;
                await Dispatcher.InvokeAsync(() =>
                {
                    var dlg = new ApiKeyDialog("Pixabay",
                        L("pixabay_key_not_found_msg"));
                    if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.ApiKey))
                    {
                        newKey = dlg.ApiKey;
                        if (!MediaProviderSettings.SavePixabayKey(newKey))
                            LogToMainWindow("❌ The Pixabay key could not be saved.");
                        apiKey = newKey;
                    }
                });

                if (string.IsNullOrEmpty(apiKey))
                {
                    AnnounceToUser(L("pixabay_key_not_entered"));
                    return null;
                }
            }

            _pixabayApiKey = apiKey;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await _apiRateLimiter.WaitAsync(ct);

                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        // FALLBACK FIX: attempt==1 stays semantically tied to the query
                        // (does not pull random from _universalKeywords unrelated to the lyric)
                        string searchQ;
                        if (attempt == 0)
                        {
                            searchQ = keywords;
                        }
                        else
                        {
                            // v1.4: the second try keeps the CONCRETE words of the search and drops the
                            // generic ones ("family buying ice cream" → "buying ice cream child").
                            // Before it kept only the first word + "children outdoor", so "family buying
                            // ice cream" became "family children outdoor" — and a montage part about ice
                            // cream could end up with a train or a railway.
                            var generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                            { "child", "children", "kid", "kids", "girl", "boy", "family", "happy", "outdoor", "outdoors",
                              "park", "playing", "cute", "little", "with", "the", "and", "together", "sunny", "warm",
                              "soft", "cinematic", "lighting", "light", "candid", "moment", "closeup", "close" };
                            var concrete = keywords.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                                .Where(t => t.Length > 2 && !generic.Contains(t)).Take(3).ToList();
                            searchQ = concrete.Count > 0 ? string.Join(" ", concrete) + " child" : "children outdoor";
                        }

                        // Page rotation — when all results have been seen, we take the next page
                        string pageKey = searchQ.ToLowerInvariant().Trim();
                        if (!_queryPageMap.ContainsKey(pageKey)) _queryPageMap[pageKey] = 1;
                        int currentPage = _queryPageMap[pageKey];

                        string url = mediaType == "video"
                            ? (minDurationSeconds > 20
                                ? $"https://pixabay.com/api/videos/?key={apiKey}&q={Uri.EscapeDataString(searchQ)}&safesearch=true&per_page=30&video_type=film&min_duration=20&max_duration=60&page={currentPage}"
                                : minDurationSeconds > 4
                                    ? $"https://pixabay.com/api/videos/?key={apiKey}&q={Uri.EscapeDataString(searchQ)}&safesearch=true&per_page=30&video_type=film&min_duration=4&max_duration=60&page={currentPage}"
                                    : $"https://pixabay.com/api/videos/?key={apiKey}&q={Uri.EscapeDataString(searchQ)}&safesearch=true&per_page=30&video_type=film&page={currentPage}")
                            : $"https://pixabay.com/api/?key={apiKey}&q={Uri.EscapeDataString(searchQ)}&image_type=photo&safesearch=true&per_page=30&min_width={minWidth}&page={currentPage}";

                        string response = await _httpClient.GetStringAsync(url, ct);
                        var json = JObject.Parse(response);
                        var hits = json["hits"] as JArray;

                        if (hits == null || hits.Count == 0) continue;

                        if (mediaType == "video")
                        {
                            var whitelistTags = new[] { "nature","park","family","kid","children","child",
    "smiling","bright","daytime","outdoor","sunshine","happy","playing",
    "playground","flowers","grass","blue sky",
    "boy","girl","toddler","baby","kids","siblings",
    "mother","father","parent","teacher" };

                            // LITERAL SYNC FIX: Extract query words for direct comparison with tags.
                            // This solves the problem where "sladoled" returns generic children's shots instead of
                            // specific ones (child eating ice cream) — because sorting was not looking at the query itself.
                            // queryMatchScore * 3 gives 3x higher priority to direct match than whitelist.
                            var queryWords = searchQ.ToLowerInvariant()
                                .Split(new[] { ' ', ',', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                                .Where(w => w.Length > 2) // ignore short stop-words
                                .ToArray();

                            var sortedHits = hits.Cast<JToken>().OrderByDescending(h => {
                                string tags = h["tags"]?.ToString()?.ToLower() ?? "";
                                string title = h["user"]?.ToString()?.ToLower() ?? "";

                                // Query match score — each query word found in tags = +1
                                int queryMatchScore = queryWords.Count(w => tags.Contains(w));

                                // Whitelist score — generic bonus for children's content
                                int whitelistScore = whitelistTags.Count(w => tags.Contains(w));

                                // PATCH 7: Bonus za HD/4K snimke
                                int width = h["videos"]?["large"]?["width"]?.Value<int>() ?? h["width"]?.Value<int>() ?? 0;
                                int height = h["videos"]?["large"]?["height"]?.Value<int>() ?? h["height"]?.Value<int>() ?? 0;
                                int qualityBonus = (width >= 3840 || height >= 2160) ? 5   // 4K
                                                 : (width >= 1920 || height >= 1080) ? 3   // 1080p
                                                 : (width >= 1280 || height >= 720) ? 1   // 720p
                                                 : 0;

                                // queryMatchScore carries 3x weight — specificity beats popularity
                                return (queryMatchScore * 3) + whitelistScore + qualityBonus;
                            }).ToList();

                            string queryKey = searchQ.ToLowerInvariant().Trim();
                            if (!_queryUseCount.ContainsKey(queryKey))
                                _queryUseCount[queryKey] = 0;

                            int startIdx = _queryUseCount[queryKey] % sortedHits.Count;
                            _queryUseCount[queryKey]++;

                            int analyzedHere = 0;
                            for (int hitOffset = 0; hitOffset < sortedHits.Count; hitOffset++)
                            {
                                int hitIdx = (startIdx + hitOffset) % sortedHits.Count;
                                var hit = sortedHits[hitIdx];
                                // Asset Deduplication — skip shots already used in this video
                                string hitId = hit["id"]?.ToString() ?? "";
                                if (!string.IsNullOrEmpty(hitId) && !_seenPixabayIds.Add(hitId))
                                {
                                    LogToMainWindow($"⏭ Deduplication: skipping shot ID {hitId} (already used)");
                                    continue;
                                }
                                var videos = hit["videos"] as JObject;
                                if (videos == null) continue;

                                // v1.4: ONE download per clip, in Full HD. Before, a clip rejected after
                                // download (shot type, vision, brightness) was downloaded again in the next
                                // size and rejected again for the same reason — up to three times, starting
                                // with the 4K "large" file. One test video downloaded 1061 files (30 GB).
                                string[] formats = new[] { "medium", "large", "small" }
                                    .Where(f => !string.IsNullOrEmpty(videos[f]?["url"]?.ToString()))
                                    .Take(1)
                                    .ToArray();
                                foreach (var fmt in formats)
                                {
                                    var videoObj = videos[fmt];
                                    if (videoObj == null) continue;

                                    string dlUrl = videoObj["url"]?.ToString();
                                    if (string.IsNullOrEmpty(dlUrl)) continue;

                                    if (_usedMediaUrls.Contains(dlUrl))
                                    {
                                        LogToMainWindow($"   ⏭ Skipping already used video URL (hit {hitIdx + 1})");
                                        continue;
                                    }

                                    string hitTags = hit["tags"]?.ToString()?.ToLower() ?? "";
                                    // v1.4: a line about tea or hot chocolate may show a cosy home (a mug,
                                    // the family, a fireplace) even in an "outdoor" song
                                    bool warmDrinkLine = IsWarmDrinkLyric(_currentLyric);

                                    string[] contextBadTags;
                                    switch (_detectedContext)
                                    {
                                        case "children":
                                        case "lullaby":
                                        case "fun":
                                        case "school":
                                            contextBadTags = new[] {
            "coffee", "tea cup", "beer", "wine", "whiskey", "alcohol",
            "cigarette", "smoking", "office", "corporate", "business",
            "meeting", "suit", "formal", "briefcase", "laptop work",
            "nightclub", "bar", "pub", "casino", "violence",
            "cemetery", "funeral", "weapon", "gun", "knife",
            "woman alone", "solo woman", "woman portrait", "woman closeup",
            "adult woman sitting", "model", "stock woman",
            "bull", "cow", "cattle", "livestock", "ox",
            "dry branch", "bare branch", "dead tree", "twig closeup",
            // FIX-BADTAGS: Vizuelno agresivni kadrovi (Gemini @ 01:25 tornado, @ 00:49 vatra)
            "tornado", "twister", "hurricane", "cyclone", "typhoon",
            "storm", "thunderstorm", "lightning strike", "dark storm",
            "fire", "flame", "flames", "fireplace", "campfire",
            "burning", "wildfire", "bonfire", "blaze", "inferno",
            "explosion", "blast", "smoke dark", "disaster",
            "flood", "tsunami", "earthquake", "volcano", "eruption"
        };
                                            break;
                                        case "health":
                                        case "outdoor":
                                        case "nature":
                                            contextBadTags = new[] {
            "fireplace", "campfire indoor", "cooking", "frying",
            "kitchen", "bacon", "candle warm indoor", "chimney",
            "cozy interior", "hot drink indoor", "food preparation",
            "living room", "fireplace crackling", "indoor cozy",
            "bull", "cow", "cattle", "livestock", "ox",
            "dry branch", "bare branch", "dead tree", "twig closeup"
        };
                                            break;
                                        case "music":
                                        case "dance":
                                            contextBadTags = new[] {
            "fireplace", "kitchen", "cooking", "sleeping",
            "bedroom cozy", "cemetery", "funeral", "violence"
        };
                                            break;
                                        case "wedding":
                                        case "love":
                                        case "romantic":
                                            contextBadTags = new[] {
            "violence", "weapon", "gun", "knife", "horror",
            "cemetery", "funeral", "accident", "crash"
        };
                                            break;
                                        case "adventure":
                                        case "sport":
                                            contextBadTags = new[] {
            "hospital", "sick", "death", "violence", "gun",
            "office", "corporate", "boring", "slow"
        };
                                            break;

                                        default:
                                            contextBadTags = new[] {
                                                "violence", "weapon", "gun", "knife", "horror",
                                                "cemetery", "funeral", "accident", "crash"
                                            };
                                            break;
                                    }

                                    var universalBadTags = new[] {
    "corridor", "hallway", "lobby", "atrium", "marble",
    "frog", "toad", "reptile", "snake", "spider",
    "black and white", "monochrome", "silhouette",
    "animation", "cartoon", "cartoons", "animated", "3d", "render", "rendered",
    "illustration", "drawing", "painting", "sketch", "art",
    "ai generated", "ai-generated", "generative", "abstract",
    "background", "texture", "pattern", "graphics", "vector",
    "fantasy", "surreal", "watercolor", "cg", "vfx", "particles",
    "solo woman", "solo adult", "woman alone", "woman sitting",
    "woman smiling", "woman portrait", "woman closeup",
    "bull", "cow snow", "cattle snow", "livestock",
    "dry branch", "bare tree branch", "dead branch", "bare tree",
    "bark tree closeup", "twig closeup",
    "stock photo woman", "model smiling", "model portrait",
    // Lifestyle / teenagers
    "teenager", "teen", "teens", "teenage", "adolescent",
    "young adult", "young woman", "young man",
    "lifestyle", "influencer", "selfie", "ice cream woman",
    "ice cream girl", "ice cream lifestyle", "fashion", "beauty",
    "dating", "romance adult", "couple", "boyfriend", "girlfriend",
    // PATCH 7: Maske i pandemija
    "mask", "face mask", "medical mask", "surgical mask", "masked",
    "covid", "pandemic", "quarantine", "lockdown", "protective mask",
    // PATCH 7: Sports incompatible with family walk
    "hockey", "ice hockey", "hockey stick", "hockey player",
    "rugby", "american football", "boxing", "wrestling", "martial arts",
    "extreme sport", "skateboard trick", "bmx trick",
    // PATCH 9: Tech/Audio oprema — Anomalija 2 fix (tag-level)
    "gramophone", "turntable", "vinyl", "record player", "vinyl record",
    "dj mixer", "mixing console", "mixing board", "audio mixer", "sound board",
    "studio equipment", "recording studio", "music studio", "audio equipment",
    "synthesizer", "amplifier", "equalizer", "audio interface",
    "gramofon", "mixeta", "mikset",
    // PATCH 8: Urban/city elimination — nature, parks, forests only
    "city", "cities", "urban", "downtown", "traffic", "highway", "freeway",
    "intersection", "skyscraper", "skyline", "building exterior", "office building",
    "apartment building", "street traffic", "road traffic", "busy street",
    "rush hour", "commute", "subway", "metro", "bus stop", "taxi", "cars traffic",
    "parking lot", "parking garage", "shopping mall", "mall interior",
    "construction site", "industrial", "factory", "warehouse", "concrete",
    "asphalt", "overpass", "bridge city", "tunnel city"
};
                                    // Odrasla osoba je OK samo ako je uz dijete
                                    bool hasAdult = hitTags.Contains("woman") || hitTags.Contains("man")
                                                 || hitTags.Contains("adult") || hitTags.Contains("person");
                                    bool hasChildContext = hitTags.Contains("child") || hitTags.Contains("kid")
                                                        || hitTags.Contains("baby") || hitTags.Contains("toddler")
                                                        || hitTags.Contains("parent") || hitTags.Contains("mother")
                                                        || hitTags.Contains("father")
                                                        || hitTags.Contains("teacher") || hitTags.Contains("family");
                                    // v1.4: "grandparent" no longer counts as a child — old people
                                    // without a child in the picture are filtered below.
                                    bool adultWithoutChild = hasAdult && !hasChildContext;
                                    // v1.4: the lyric's own season is checked from the tags BEFORE downloading
                                    // (it used to be checked only after the download and the vision analysis)
                                    // Winter and autumn must be visible (snow, leaves); for spring and summer
                                    // an ordinary green, sunny clip is fine, only the cold seasons are refused.
                                    string tagSeason = SeasonFromTags(hitTags);
                                    bool coldLine = _currentSeason is "winter" or "autumn";
                                    if (_lyricNamesSeason && strictSeasonFilter &&
                                        !string.IsNullOrEmpty(_currentSeason) && _currentSeason != "none" &&
                                        (coldLine ? tagSeason != _currentSeason : tagSeason is "winter" or "autumn") &&
                                        hitOffset < sortedHits.Count - 2)
                                    {
                                        LogToMainWindow($"   🗓 Not a {_currentSeason} clip (tags) — skipped without downloading");
                                        continue;
                                    }

                                    bool notForKids = IskraKidsSafeQuery.IsUnsuitableForKids(hitTags, _currentLyric, out string notForKidsReason);
                                    if (notForKids)
                                        LogToMainWindow($"   ⏭ Not for a children's video ({notForKidsReason})");
                                    bool hasBadTag = universalBadTags.Any(t => hitTags.Contains(t))
                                                  || contextBadTags.Where(t => !(warmDrinkLine && IsCozyTag(t))).Any(t => hitTags.Contains(t))
                                                  || adultWithoutChild || notForKids;
                                    if (hasBadTag)
                                    {
                                        LogToMainWindow($"   ⏭ Skipping inappropriate content (tags: {hitTags.Substring(0, Math.Min(60, hitTags.Length))})");
                                        continue;
                                    }

                                    _usedMediaUrls.Add(dlUrl);

                                    string fileName = $"AI_{Guid.NewGuid().ToString().Substring(0, 8)}.mp4";
                                    string fullPath = NewDownloadPath(fileName);

                                    LogToMainWindow($"   ⬇ Downloading video hit {hitIdx + 1}/{sortedHits.Count} (format: {fmt}): {dlUrl.Substring(dlUrl.LastIndexOf('/') + 1)}");
                                    await DownloadToFileAsync(dlUrl, fullPath, ct);   // v1.4: the file is closed before anyone reads it

                                    string ffmpegForBright = AppPaths.FFmpeg;
                                    var brightnessOk = await CheckBrightnessAndTint(fullPath, ffmpegForBright, ct);
                                    if (!brightnessOk && hitOffset < sortedHits.Count - 2)
                                    {
                                        LogToMainWindow($"   ⏭ Rejected due to brightness/tint (too dark/bright or pink tint)");
                                        try { File.Delete(fullPath); } catch { }
                                        _usedMediaUrls.Remove(dlUrl);
                                        continue;
                                    }

                                    string ffmpegForVision = AppPaths.FFmpeg;

                                    // Prosljedjujemo stih i kontekst — Qwen koristi Dynamic Mood Alignment
                                    // _currentLyric i _currentSeason se setuju u scene loopu (AIVideoCreator)
                                    // v1.4 CANDIDATE BUDGET: every analysed clip costs a download and about half a
                                    // minute of the vision AI. Style rules (shot sizes, camera motion, indoor/outdoor,
                                    // brightness jumps, the AI's "could fit better") may reject the first three clips;
                                    // from the fourth on they are only preferences. Content rules (unsafe content, the
                                    // vision AI's reject, a season the lyric names) always apply. Before, one 3-second
                                    // intro shot took 13 minutes, and 3.5 hours were not enough for 8 scenes.
                                    analyzedHere++;
                                    bool relax = analyzedHere > 3;
                                    // v1.4: at most 6 vision analyses per search — after that the next query layer
                                    // (simpler search words) has a better chance than more results of this one
                                    if (analyzedHere > 6)
                                    {
                                        LogToMainWindow("   ⏭ Six clips analysed for this search without a good one — trying a simpler search");
                                        try { File.Delete(fullPath); } catch { }
                                        _usedMediaUrls.Remove(dlUrl);
                                        break;
                                    }
                                    var visionClock = System.Diagnostics.Stopwatch.StartNew();
                                    var vision = await VisionAnalyzer.AnalyzeClipAsync(
                                        fullPath, ffmpegForVision, ct,
                                        lyricLine: VisionLyricContext(),
                                        season: _currentSeason,
                                        mood: _detectedMood,
                                        context: _detectedContext);

                                    // TAG BOOST: Ako ONNX nije prepoznao dijete ali Pixabay tagovi jasno govore
                                    // da je kadar sa djecom — override HasChildren=true.
                                    // ONNX MobileNetV2 has poor labels for children; tags are a more reliable signal.
                                    if (!vision.HasChildren)
                                    {
                                        string[] childTagKeywords = {
                                            "child", "children", "kid", "kids", "baby", "toddler",
                                            "boy", "girl", "infant", "preschool", "kindergarten",
                                            "playground", "nursery", "newborn"
                                        };
                                        bool tagsHaveChild = childTagKeywords.Any(k => hitTags.Contains(k));
                                        if (tagsHaveChild)
                                        {
                                            vision.HasChildren = true;
                                            LogToMainWindow($"   👶 TagBoost: Pixabay tags contain child keyword — HasChildren override=True");
                                        }
                                    }

                                    // OUTDOOR TAG BOOST: Isti princip kao HasChildren
                                    // ONNX rijetko detektuje outdoor — tagovi su pouzdaniji
                                    if (!vision.IsOutdoor)
                                    {
                                        string[] outdoorTagKeywords = {
                                            "outdoor", "outside", "nature", "park", "garden", "field",
                                            "forest", "meadow", "grass", "sky", "sunshine", "sunlight",
                                            "playground", "street", "beach", "mountain", "river", "lake",
                                            "path", "trail", "yard", "backyard", "exterior"
                                        };
                                        bool tagsHaveOutdoor = outdoorTagKeywords.Any(k => hitTags.Contains(k));
                                        if (tagsHaveOutdoor)
                                            vision.IsOutdoor = true;
                                    }

                                    if (vision.RetryNeeded && hitOffset < sortedHits.Count - 2 && !relax)
                                    {
                                        string lyricPreview = _currentLyric?.Length > 40 ? _currentLyric.Substring(0, 40) + "..." : _currentLyric;
                                        LogToMainWindow($"   🔄 Qwen: scene doesn't match the lyric '{lyricPreview}' — looking for a better clip...");
                                        try { File.Delete(fullPath); } catch { }
                                        _usedMediaUrls.Remove(dlUrl);
                                        continue;
                                    }

                                    // v1.4: a clip the vision AI scores below 4 (or rejects) is NEVER used. Before,
                                    // the last two results of a search were taken even when the AI had rejected them
                                    // (11 clips with score 1.0 got into one video, two of them were then cut out again
                                    // by the quality gate, leaving holes). Now the search moves on to the next, simpler
                                    // query instead.
                                    if (vision.Score < 4.0)
                                    {
                                        LogToMainWindow($"   ⚠ VisionScore {vision.Score:F1}/10 — odbacujem ({vision.RejectReason}), trazim bolji...");
                                        try { File.Delete(fullPath); } catch { }
                                        _usedMediaUrls.Remove(dlUrl);
                                        break;
                                    }

                                    var clipMotion = await MotionAnalyzer.AnalyzeAsync(fullPath, ffmpegForVision, ct);

                                    // PATCH 8 — Safe Motion Filter (Kids Edition)
                                    // Odbacujemo klipove sa prevelikim kretanjem kamere — vizuelni zamor kod dece 2-6g
                                    // HasStrongMotion = Magnitude > 30 (threshold je u MotionResult)
                                    // v1.4: off for now. The motion analysis works since this version, but its
                                    // scale is not calibrated yet (most ordinary clips measured 100 of 100), and this
                                    // rule threw away 37 clips in one test — each after a full vision analysis.
                                    if (false && clipMotion.HasStrongMotion)
                                    {
                                        LogToMainWindow($"   🚫 KidsFilter: Camera motion too aggressive (mag:{clipMotion.Magnitude:F0}) — rejecting for children's video...");
                                        try { File.Delete(fullPath); } catch { }
                                        _usedMediaUrls.Remove(dlUrl);
                                        continue;
                                    }

                                    // FIX-FROZEN: Filter for "frozen" animal shots
                                    // AI stock clips of birds, flamingos etc. often have Magnitude≈0 (static scene)
                                    // Iskusno oko to odmah primijeti (Gemini: 00:21 flamingo, 00:32 ptica).
                                    // If the shot is an animal AND nearly static → we look for a more dynamic clip.
                                    bool isAnimalClip = hitTags.Contains("bird") || hitTags.Contains("flamingo") ||
                                                        hitTags.Contains("animal") || hitTags.Contains("wildlife") ||
                                                        hitTags.Contains("butterfly") || hitTags.Contains("cat") ||
                                                        hitTags.Contains("dog") || hitTags.Contains("horse") ||
                                                        hitTags.Contains("duck") || hitTags.Contains("rabbit") ||
                                                        hitTags.Contains("fish") || hitTags.Contains("deer") ||
                                                        (vision.Labels != null && vision.Labels.Any(l =>
                                                            l.ToLower().Contains("bird") || l.ToLower().Contains("animal") ||
                                                            l.ToLower().Contains("flamingo") || l.ToLower().Contains("wildlife")));
                                    bool isFrozenClip = clipMotion.Direction == MotionDirection.Static &&
                                                        clipMotion.Magnitude < 3.0;
                                    if (isAnimalClip && isFrozenClip && hitOffset < sortedHits.Count - 3 && !relax)
                                    {
                                        LogToMainWindow($"   🐦 FrozenAnimal: animal shot with no motion (mag:{clipMotion.Magnitude:F1}) — looking for animated clip...");
                                        try { File.Delete(fullPath); } catch { }
                                        _usedMediaUrls.Remove(dlUrl);
                                        continue;
                                    }

                                    // FIX-MOTION: Koristimo EndDirection prethodnog klipa (ne Direction)
                                    // _lastClipEndMotion stores motion at the END of the previous clip
                                    // This fixes jump-cut: end-start must be compatible, not start-start
                                    var prevForMatch = _lastClipEndMotion ?? _lastClipMotion;
                                    bool motionOk = MotionResult.IsCompatible(prevForMatch, clipMotion);

                                    if (!motionOk && hitOffset < sortedHits.Count - 2 && !relax)
                                    {
                                        LogToMainWindow("   Motion mismatch (end:" + (prevForMatch?.EndDirection.ToString() ?? "null") + " -> start:" + clipMotion.Direction + ") -- trazim bolji...");
                                        try { File.Delete(fullPath); } catch { }
                                        _usedMediaUrls.Remove(dlUrl);
                                        break;
                                    }

                                    string hitTagsForShot = hit["tags"]?.ToString()?.ToLower() ?? "";
                                    string shotType = "medium";
                                    if (hitTagsForShot.Contains("aerial") || hitTagsForShot.Contains("drone") ||
                                        hitTagsForShot.Contains("landscape") || hitTagsForShot.Contains("panorama") ||
                                        hitTagsForShot.Contains("wide") || vision.IsOutdoor && !vision.HasChildren)
                                        shotType = "wide";

                                    // PATCH 9: 30% Composition Rule — blokiraj wide landscape bez dece
                                    // Ako je "wide" shot I nema djece u kadru → odbaci (djeca su premala/nevidljiva)
                                    bool isTooWideWithoutChild = shotType == "wide" && !vision.HasChildren &&
                                                                 !hitTagsForShot.Contains("nature") && // pure nature OK as B-roll
                                                                 !hitTagsForShot.Contains("forest") &&
                                                                 !hitTagsForShot.Contains("flower") &&
                                                                 !hitTagsForShot.Contains("garden") &&
                                                                 !hitTagsForShot.Contains("park");
                                    if (isTooWideWithoutChild && hitOffset < sortedHits.Count - 2 && !relax)
                                    {
                                        LogToMainWindow($"   📐 PATCH9 30%Rule: wide shot without children — too static for ages 2-6, looking for medium/close shot...");
                                        try { File.Delete(fullPath); } catch { }
                                        _usedMediaUrls.Remove(dlUrl);
                                        continue;
                                    }
                                    else if (hitTagsForShot.Contains("close") || hitTagsForShot.Contains("portrait") ||
                                             hitTagsForShot.Contains("face") || vision.HasChildren)
                                        shotType = "close";

                                    // v1.4 NOTE: the two shot-size rules below are switched off. A clip with a child
                                    // counts as "close", and every lyric clip must show a child, so "no two close shots in
                                    // a row" rejected almost every clip (101 rejections and 89 "bridge" rejections in one
                                    // test, each after a full download and vision analysis). The director's plan and the
                                    // vision AI already vary the shots.
                                    // Bridge logika: wide→close ili close→wide je preskok — treba medium
                                    bool needsBridge = (_lastShotType == "wide" && shotType == "close") ||
                                                       (_lastShotType == "close" && shotType == "wide");
                                    if (false && needsBridge) // v1.4: off — see the note above
                                    {
                                        LogToMainWindow($"   🌉 Bridge: {_lastShotType}→{shotType} is too big a jump — looking for a medium shot...");
                                        try { File.Delete(fullPath); } catch { }
                                        _usedMediaUrls.Remove(dlUrl);
                                        continue;
                                    }

                                    if (false && shotType == _lastShotType) // v1.4: off — see the note above
                                    {
                                        LogToMainWindow($"   🎬 Shot composition: dva uzastopna '{shotType}' — trazim drugi tip...");
                                        try { File.Delete(fullPath); } catch { }
                                        _usedMediaUrls.Remove(dlUrl);
                                        continue;
                                    }

                                    // ── Temporal Seasonal Grouping ────────────────────────────────
                                    // ── STRICT SEASONAL MATCHING ──────────────────────────────────
                                    // Detect clip season based on tags (extended set for better detection)
                                    string clipSeasonTag = SeasonFromTags(hitTagsForShot);   // v1.4: whole words, ice cream is not winter

                                    // STRICT FILTER: odbaci klip koji nije "none" i nije ciljana sezona pesme
                                    // IZNIMKA 1: B-roll klipovi (strictSeasonFilter=false) — ne filtriramo
                                    // IZNIMKA 2: Ako scena ima eksplicitnu sezonu (po stihu), prihvati klipove te sezone
                                    //            e.g. lyric "kad je zima" → _currentSeason may be "winter" even if global season is "spring"
                                    bool strictSeasonReject = false;
                                    if (strictSeasonFilter &&
                                        !string.IsNullOrEmpty(_currentSeason) && _currentSeason != "none" &&
                                        clipSeasonTag != "none" && clipSeasonTag != _currentSeason &&
                                        hitOffset < sortedHits.Count - 2)
                                    {
                                        // Dodatna tolerancija: spring ↔ summer su kompatibilni (toplo doba)
                                        // winter ↔ autumn su kompatibilni (hladno doba)
                                        bool compatibleSeasons =
                                            (_currentSeason == "spring" && clipSeasonTag == "summer") ||
                                            (_currentSeason == "summer" && clipSeasonTag == "spring") ||
                                            (_currentSeason == "winter" && clipSeasonTag == "autumn") ||
                                            (_currentSeason == "autumn" && clipSeasonTag == "winter");

                                        if (!compatibleSeasons)
                                            strictSeasonReject = true;
                                    }

                                    // v1.4: when THIS line names its season ("Kad je zima nosi čizme",
                                    // "na velikom snegu"), every clip of the line must clearly show that
                                    // season — no "close enough" autumn leaves and no clips without any
                                    // season (ants, lemons, green leaves under a winter line). The last
                                    // two results stay allowed so the scene never ends up empty.
                                    if (!strictSeasonReject && _lyricNamesSeason && strictSeasonFilter &&
                                        _currentSeason is "winter" or "autumn" &&
                                        clipSeasonTag != _currentSeason && hitOffset < sortedHits.Count - 2)
                                        strictSeasonReject = true;

                                    if (strictSeasonReject)
                                    {
                                        LogToMainWindow($"   🗓 StrictSeason: clip={clipSeasonTag}, song={_currentSeason} — not compatible, looking for consistent clip...");
                                        try { File.Delete(fullPath); } catch { }
                                        _usedMediaUrls.Remove(dlUrl);
                                        continue;
                                    }

                                    // Sekundarni guard: zabrani i nagle oscilacije (winter→summer na istoj sceni)
                                    // Ovo ostaje kao fallback za pesme bez definirane sezone ("none")
                                    bool seasonClash = false;
                                    if (!strictSeasonReject && _lastSeasonTag != "none" &&
                                        clipSeasonTag != "none" && clipSeasonTag != _lastSeasonTag)
                                    {
                                        var opposites = new Dictionary<string, string>
                                        {
                                            ["winter"] = "summer",
                                            ["summer"] = "winter",
                                            ["spring"] = "autumn",
                                            ["autumn"] = "spring"
                                        };
                                        if (opposites.TryGetValue(_lastSeasonTag, out string opposite) && opposite == clipSeasonTag)
                                            seasonClash = true;
                                    }

                                    if (seasonClash && hitOffset < sortedHits.Count - 2 && !relax)
                                    {
                                        LogToMainWindow($"   🗓 Seasonal clash ({_lastSeasonTag} → {clipSeasonTag}) — looking for consistent clip...");
                                        try { File.Delete(fullPath); } catch { }
                                        _usedMediaUrls.Remove(dlUrl);
                                        continue;
                                    }
                                    // ─────────────────────────────────────────────────────────────

                                    // ── PATCH 10: Candid Only — strogi filter za "Stock Look" ──
                                    // Rejects adults looking directly into the lens (classic stock feel)
                                    if (vision.Labels != null && vision.Labels.Length > 0)
                                    {
                                        bool adultLookingAtCamera = vision.Labels.Any(l =>
                                            l.Contains("looking at camera") ||
                                            l.Contains("smile camera") ||
                                            l.Contains("posing") ||
                                            l.Contains("stock photo") ||
                                            l.Contains("direct gaze") ||
                                            l.Contains("group pose") ||
                                            l.Contains("commercial smile"));
                                        bool isPortraitStock = vision.Labels.Any(l => l.Contains("portrait")) &&
                                                               !vision.HasChildren; // portret bez djece = stock odrasli
                                        bool rejectStock = (adultLookingAtCamera || isPortraitStock)
                                                           && hitOffset < sortedHits.Count - 2 && !relax;
                                        if (rejectStock)
                                        {
                                            LogToMainWindow($"   📸 PATCH10 Candid: rejecting 'commercial/stock' shot (adult posing) — looking for authentic moment...");
                                            try { File.Delete(fullPath); } catch { }
                                            _usedMediaUrls.Remove(dlUrl);
                                            continue;
                                        }
                                    }
                                    // ─────────────────────────────────────────────────────────────

                                    // Color temperature continuity check
                                    // Ako prethodni kadar bio topao (warm), sledeci ne sme biti hladan (osim promene sezone)
                                    bool clipIsWarm = vision.IsWarm || hitTagsForShot.Contains("warm") ||
                                                      hitTagsForShot.Contains("golden") || hitTagsForShot.Contains("sunset") ||
                                                      hitTagsForShot.Contains("sunny");
                                    bool clipIsCold = hitTagsForShot.Contains("blue") || hitTagsForShot.Contains("cold") ||
                                                      hitTagsForShot.Contains("overcast") || hitTagsForShot.Contains("grey") ||
                                                      hitTagsForShot.Contains("gray");
                                    // PATCH 11: Gym/Fitness Vision Label Check — third layer of protection
                                    // Kettlebell and similar pass if Pixabay tag doesn't contain "gym" but clip is fitness
                                    // Here we catch this at Qwen label level — direct check of what Qwen SEES in the shot
                                    if (vision.Labels != null && vision.Labels.Length > 0)
                                    {
                                        var gymLabels = new[] {
                                            "kettlebell", "dumbbell", "barbell", "weight", "weights",
                                            "gym", "fitness", "workout", "exercise machine", "treadmill",
                                            "bench press", "deadlift", "squat rack", "pull-up bar",
                                            "resistance band", "weightlifting", "bodybuilding",
                                            "crossfit", "hiit", "plank", "pushup", "situp",
                                            "gramophone", "turntable", "vinyl", "dj mixer",
                                            "mixing console", "audio mixer", "synthesizer",
                                            "pregnant belly", "maternity", "prenatal", "pregnant"
                                        };
                                        bool hasGymOrTech = vision.Labels.Any(l =>
                                            gymLabels.Any(g => l.ToLower().Contains(g)));
                                        if (hasGymOrTech && hitOffset < sortedHits.Count - 2)
                                        {
                                            string matched = vision.Labels.FirstOrDefault(l =>
                                                gymLabels.Any(g => l.ToLower().Contains(g))) ?? "";
                                            LogToMainWindow($"   🚫 PATCH11 GymTechVisionBlock: Qwen detektovao '{matched}' — odbacujem momentalno...");
                                            try { File.Delete(fullPath); } catch { }
                                            _usedMediaUrls.Remove(dlUrl);
                                            continue;
                                        }
                                    }

                                    // PATCH 10 / FIX-D: Indoor/Outdoor Context Continuity
                                    // If the previous clip was outdoor AND the current scene is an outdoor song,
                                    // ne dozvoljavamo indoor (kuhinja, soba) klipove osim ako tekst ne pominje to
                                    bool thisClipIsIndoor = hitTagsForShot.Contains("indoor") ||
                                                            hitTagsForShot.Contains("kitchen") ||
                                                            hitTagsForShot.Contains("living room") ||
                                                            hitTagsForShot.Contains("interior") ||
                                                            hitTagsForShot.Contains("inside") ||
                                                            hitTagsForShot.Contains("room") ||
                                                            (vision.Labels != null && vision.Labels.Any(l =>
                                                                l.Contains("indoor") || l.Contains("kitchen") ||
                                                                l.Contains("interior") || l.Contains("inside")));
                                    bool outdoorContext = _detectedContext is "fun" or "children" or "nature" or "adventure" or "sport";
                                    bool lyricAllowsIndoor = !string.IsNullOrEmpty(_currentLyric) && (
                                        _currentLyric.ToLower().Contains("ruca") || _currentLyric.ToLower().Contains("vecer") ||
                                        _currentLyric.ToLower().Contains("uzin") || _currentLyric.ToLower().Contains("kuci") ||
                                        _currentLyric.ToLower().Contains("kuca") || _currentLyric.ToLower().Contains("soba") ||
                                        _currentLyric.ToLower().Contains("lunch") || _currentLyric.ToLower().Contains("home") ||
                                        _currentLyric.ToLower().Contains("dinner") || _currentLyric.ToLower().Contains("snack") ||
                                        IsWarmDrinkLyric(_currentLyric));   // v1.4: tea / hot chocolate at home
                                    bool indoorClash = thisClipIsIndoor && outdoorContext &&
                                                       _lastClipWasOutdoor && !lyricAllowsIndoor &&
                                                       hitOffset < sortedHits.Count - 2 && !relax;
                                    if (indoorClash)
                                    {
                                        LogToMainWindow($"   🏠 PATCH10 Indoor/Outdoor: outdoor song, indoor shot ({hitTagsForShot.Substring(0, Math.Min(30, hitTagsForShot.Length))}) — lookim outdoor kadar...");
                                        try { File.Delete(fullPath); } catch { }
                                        _usedMediaUrls.Remove(dlUrl);
                                        continue;
                                    }

                                    // PATCH 9: Enhanced Warm Continuity check
                                    // We also block cold clips even if previous was not warm — children's video must be warm
                                    bool tempClash = clipIsCold && !clipIsWarm &&
                                                     !hitTagsForShot.Contains("snow") && !hitTagsForShot.Contains("winter") &&
                                                     !(_detectedContext == "winter" || _detectedSeason == "winter");
                                    if (tempClash && hitOffset < sortedHits.Count - 2 && !relax)
                                    {
                                        LogToMainWindow($"   🌡 PATCH9 Warm Filter: cold/blue palette — not OK for children's video, looking for warmer shot...");
                                        try { File.Delete(fullPath); } catch { }
                                        _usedMediaUrls.Remove(dlUrl);
                                        continue;
                                    }

                                    // PATCH 10 / FIX-G: Tone Matching — rolling luminance check
                                    // If the clip is significantly darker than the average of the previous 3, reject it
                                    if (_recentLuminance.Count >= 3 && vision.Luminance > 0)
                                    {
                                        double avgLum = _recentLuminance.Average();
                                        double lumDrop = avgLum - vision.Luminance;
                                        // Reject if more than 0.20 darker than average (significant drop)
                                        if (lumDrop > 0.20 && hitOffset < sortedHits.Count - 2 && !relax)
                                        {
                                            LogToMainWindow($"   ☀ PATCH10 ToneMatch: shot too dark (lum:{vision.Luminance:F2} vs avg:{avgLum:F2}) — looking for warmer shot...");
                                            try { File.Delete(fullPath); } catch { }
                                            _usedMediaUrls.Remove(dlUrl);
                                            continue;
                                        }
                                    }
                                    // Update rolling luminance history
                                    if (vision.Luminance > 0)
                                    {
                                        _recentLuminance.Enqueue(vision.Luminance);
                                        while (_recentLuminance.Count > 3) _recentLuminance.Dequeue();
                                    }

                                    _lastClipMotion = clipMotion;

                                    // FIX-MOTION: Analiza KRAJA klipa za precizni jump-cut matching
                                    // Asinhrono — ne blokira, fallback na clipMotion ako ne uspe
                                    try
                                    {
                                        double clipDurEst = 3.0; // konzervativna procena; precizno vreme nije bitno za ovu analizu
                                        _lastClipEndMotion = await MotionAnalyzer.AnalyzeEndAsync(
                                            fullPath, ffmpegForVision, clipDurEst, 1.5, ct);
                                    }
                                    catch { _lastClipEndMotion = clipMotion; }
                                    _lastShotType = shotType;
                                    _lastSeasonTag = clipSeasonTag != "none" ? clipSeasonTag : _lastSeasonTag;
                                    _lastClipWarm = clipIsWarm || (!clipIsCold);
                                    _lastClipWasOutdoor = vision.IsOutdoor || !thisClipIsIndoor;

                                    // FIX-SMILE: Bonus score za osmeh kada je stih pozitivan
                                    // Gemini preporuka: "ako je muzika vesela, dozvoljeni su samo kadrovi gdje se vide osmesi"
                                    // Implementation: score bonus (not rejection), so system prefers funny shots
                                    double finalScore = vision.Score;
                                    if (vision.HasSmile)
                                    {
                                        var currentSentiment = StrictQueryEngine.ClassifySentiment(_currentLyric ?? "");
                                        if (currentSentiment == SentimentPolarity.Positive)
                                        {
                                            finalScore = Math.Min(10.0, vision.Score + 1.5);
                                            LogToMainWindow($"   😊 Smile bonus +1.5 (sentiment=Positive): {vision.Score:F1} → {finalScore:F1}");
                                        }
                                    }

                                    string visionTag = vision.QwenUsed ? "Qwen" : vision.OnnxUsed ? $"ONNX:{vision.TopLabel}" : ("FFmpeg" + (string.IsNullOrEmpty(vision.RejectReason) ? "" : $": {vision.RejectReason}"));
                                    LogToMainWindow($"   ✅ Score {finalScore:F1}/10 [{visionTag}, {visionClock.Elapsed.TotalSeconds:F0}s, candidate {analyzedHere}] | Motion:{clipMotion.Direction} | Shot:{shotType} | Season:{clipSeasonTag} | " +
                                        $"Children:{vision.HasChildren} Outdoor:{vision.IsOutdoor} Smile:{vision.HasSmile}");

                                    _lastDownloadedVisionScore = finalScore;
                                    _lastDownloadedDescription = vision.Description;
                                    _lastDownloadedIsStatic = clipMotion.Direction == MotionDirection.Unknown
                                                              || clipMotion.Direction == MotionDirection.Static;
                                    _lastDownloadedHasChildren = vision.HasChildren;
                                    _lastDownloadedIsOutdoor = vision.IsOutdoor;
                                    return fullPath;
                                }
                            }

                            LogToMainWindow($"   ⚠ All {sortedHits.Count} video results for '{searchQ.Substring(0, Math.Min(40, searchQ.Length))}' already used");
                            // Page rotation — increment page for the next call of the same query
                            if (_queryPageMap.ContainsKey(pageKey) && _queryPageMap[pageKey] < 5)
                                _queryPageMap[pageKey]++;
                        }
                        else
                        {
                            string queryKey = searchQ.ToLowerInvariant().Trim() + "_img";
                            if (!_queryUseCount.ContainsKey(queryKey))
                                _queryUseCount[queryKey] = 0;

                            int startIdx = _queryUseCount[queryKey] % hits.Count;
                            _queryUseCount[queryKey]++;

                            for (int hitOffset = 0; hitOffset < hits.Count; hitOffset++)
                            {
                                int hitIdx = (startIdx + hitOffset) % hits.Count;
                                var hit = hits[hitIdx];
                                string dlUrl = hit["largeImageURL"]?.ToString() ?? hit["webformatURL"]?.ToString();

                                if (string.IsNullOrEmpty(dlUrl)) continue;
                                if (_usedMediaUrls.Contains(dlUrl))
                                {
                                    LogToMainWindow($"   ⏭ Skipping already used image (hit {hitIdx + 1})");
                                    continue;
                                }
                                // v1.4: pictures were not checked at all (only videos were) — the
                                // wheelchair/therapy shot in the grandparents line came this way.
                                string imgTags = hit["tags"]?.ToString()?.ToLower() ?? "";
                                if (!IskraKidsSafeQuery.IsHitSafe(imgTags) ||
                                    IskraKidsSafeQuery.IsUnsuitableForKids(imgTags, _currentLyric, out _))
                                {
                                    IskraKidsSafeQuery.IsUnsuitableForKids(imgTags, _currentLyric, out string imgReason);
                                    LogToMainWindow($"   ⏭ Picture not for a children's video ({imgReason ?? imgTags.Substring(0, Math.Min(50, imgTags.Length))})");
                                    continue;
                                }

                                _usedMediaUrls.Add(dlUrl);
                                string fileName = $"AI_{Guid.NewGuid().ToString().Substring(0, 8)}.jpg";
                                string fullPath = NewDownloadPath(fileName);

                                await DownloadToFileAsync(dlUrl, fullPath, ct);   // v1.4: the file is closed before anyone reads it
                                return fullPath;
                            }
                            LogToMainWindow($"   ⚠ All images for '{searchQ.Substring(0, Math.Min(40, searchQ.Length))}' already used");
                        }
                    }
                    finally
                    {
                        _apiRateLimiter.Release();
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (HttpRequestException ex) when (ex.Message.Contains("429"))
                {
                    await Task.Delay(2000, ct);
                }
                catch (Exception ex)
                {
                    LogToMainWindow($"❌ Error in attempt {attempt + 1}: {ex.Message}");
                }
            }

            // ══════════════════════════════════════════════════════════════════
            // PEXELS FALLBACK — activated when Pixabay finds nothing
            // Koristi MediaProviderRegistry waterfall (Pexels → Coverr → ...)
            // Pexels: 200 req/hour, high HD quality, lifestyle/children content
            // ══════════════════════════════════════════════════════════════════
            if (mediaType == "video")
            {
                try
                {
                    LogToMainWindow($"   🔄 Pixabay exhausted — trying Pexels fallback for '{keywords.Substring(0, Math.Min(50, keywords.Length))}'...");

                    var registry = MediaProviderRegistry.Instance;
                    var fallbackProviders = registry.Configured
                        .Where(p => !string.Equals(p.Name, "Pixabay", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (fallbackProviders.Count == 0)
                    {
                        LogToMainWindow("   ℹ No fallback providers configured (Pexels/Coverr). Add API key in Settings → Providers.");
                    }

                    foreach (var provider in fallbackProviders)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            LogToMainWindow($"   🌐 [{provider.Name}] Searching: '{keywords.Substring(0, Math.Min(50, keywords.Length))}'...");

                            double minDur = minDurationSeconds > 0 ? minDurationSeconds : 4.0;
                            var results = await provider.SearchAsync(
                                keywords, true, minWidth,
                                minDur, 60.0, 30, ct);

                            // Kids-safe filter
                            var safeResults = results
                                .Where(r => IskraKidsSafeQuery.IsHitSafe(r.Tags?.ToLower() ?? ""))
                                .Where(r => !_usedMediaUrls.Contains(r.DownloadUrl))
                                .ToList();

                            if (safeResults.Count == 0)
                            {
                                LogToMainWindow($"   ⏭ [{provider.Name}] 0 kid-safe results for this query");
                                continue;
                            }

                            // LITERAL SYNC FIX: Isti query-match scoring kao Pixabay —
                            // specific query words carry 3x higher bonus than the generic children tag.
                            var pexelsQueryWords = keywords.ToLowerInvariant()
                                .Split(new[] { ' ', ',', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                                .Where(w => w.Length > 2)
                                .ToArray();

                            // Sortiraj po query match (3x) + rezolucija + children tag bonus
                            var sorted = safeResults.OrderByDescending(r =>
                            {
                                string tagsLower = r.Tags?.ToLower() ?? "";
                                int queryMatchScore = pexelsQueryWords.Count(w => tagsLower.Contains(w));
                                int qualityBonus = (r.Width >= 1920 || r.Height >= 1080) ? 10 : (r.Width >= 1280) ? 5 : 0;
                                int childBonus = (tagsLower.Contains("child") || tagsLower.Contains("kid") ||
                                                  tagsLower.Contains("family") || tagsLower.Contains("boy") ||
                                                  tagsLower.Contains("girl") || tagsLower.Contains("baby")) ? 8 : 0;
                                return (queryMatchScore * 3) + qualityBonus + childBonus;
                            }).ToList();

                            foreach (var result in sorted)
                            {
                                ct.ThrowIfCancellationRequested();
                                if (_usedMediaUrls.Contains(result.DownloadUrl)) continue;

                                // FIX-PEXELS-DEDUP: same video can arrive under different URLs (1080p vs 4K).
                                // We extract the numeric ID from filename and store it as an additional dedup key.
                                string pexelsDlFile = result.DownloadUrl.Substring(result.DownloadUrl.LastIndexOf('/') + 1);
                                string pexelsVideoIdMatch = System.Text.RegularExpressions.Regex.Match(pexelsDlFile, @"^(\d+)").Value;
                                string pexelsDedupeKey = !string.IsNullOrEmpty(pexelsVideoIdMatch) ? $"pexels_{pexelsVideoIdMatch}" : null;
                                if (pexelsDedupeKey != null && _usedMediaUrls.Contains(pexelsDedupeKey))
                                {
                                    LogToMainWindow($"   ⏭ [Pexels] Skipping ID {pexelsVideoIdMatch} (already used)");
                                    continue;
                                }

                                // TagBoost za HasChildren (isti mehanizam kao Pixabay)
                                string resultTagsLower = result.Tags?.ToLower() ?? "";
                                bool pexelsHasChildTag = new[] {
                                    "child","children","kid","kids","baby","toddler",
                                    "boy","girl","infant","playground","preschool"
                                }.Any(k => resultTagsLower.Contains(k));

                                string fileName = $"AI_px_{Guid.NewGuid().ToString().Substring(0, 8)}.mp4";
                                string fullPath = NewDownloadPath(fileName);

                                try
                                {
                                    LogToMainWindow($"   ⬇ [{provider.Name}] Downloading: {pexelsDlFile.Substring(0, Math.Min(40, pexelsDlFile.Length))}");
                                    _usedMediaUrls.Add(result.DownloadUrl);
                                    if (pexelsDedupeKey != null) _usedMediaUrls.Add(pexelsDedupeKey);

                                    await DownloadToFileAsync(result.DownloadUrl, fullPath, ct);   // v1.4: the file is closed before anyone reads it

                                    if (!File.Exists(fullPath) || new FileInfo(fullPath).Length < 1000)
                                    {
                                        _usedMediaUrls.Remove(result.DownloadUrl);
                                        if (pexelsDedupeKey != null) _usedMediaUrls.Remove(pexelsDedupeKey);
                                        try { File.Delete(fullPath); } catch { }
                                        continue;
                                    }

                                    string ffmpegPath = AppPaths.FFmpeg;

                                    // Brightness check
                                    var brightnessOk = await CheckBrightnessAndTint(fullPath, ffmpegPath, ct);
                                    if (!brightnessOk)
                                    {
                                        LogToMainWindow($"   ⏭ [{provider.Name}] Rejected — brightness/tint problem");
                                        _usedMediaUrls.Remove(result.DownloadUrl);
                                        if (pexelsDedupeKey != null) _usedMediaUrls.Remove(pexelsDedupeKey);
                                        try { File.Delete(fullPath); } catch { }
                                        continue;
                                    }

                                    // Vision analiza
                                    var vision = await VisionAnalyzer.AnalyzeClipAsync(
                                        fullPath, ffmpegPath, ct,
                                        lyricLine: VisionLyricContext(),
                                        season: _currentSeason,
                                        mood: _detectedMood,
                                        context: _detectedContext);

                                    // TagBoost za Pexels (isti princip - Pexels nema bogat tag sistem)
                                    if (!vision.HasChildren && pexelsHasChildTag)
                                    {
                                        vision.HasChildren = true;
                                        LogToMainWindow($"   👶 [{provider.Name}] TagBoost: HasChildren override=True");
                                    }

                                    if (vision.Score < 3.5)
                                    {
                                        LogToMainWindow($"   ⚠ [{provider.Name}] VisionScore {vision.Score:F1}/10 — looking for better...");
                                        _usedMediaUrls.Remove(result.DownloadUrl);
                                        if (pexelsDedupeKey != null) _usedMediaUrls.Remove(pexelsDedupeKey);
                                        try { File.Delete(fullPath); } catch { }
                                        continue;
                                    }

                                    // Motion check
                                    var clipMotion = await MotionAnalyzer.AnalyzeAsync(fullPath, ffmpegPath, ct);
                                    if (clipMotion.HasStrongMotion)
                                    {
                                        LogToMainWindow($"   🚫 [{provider.Name}] Preagresivan pokret — odbacujem");
                                        _usedMediaUrls.Remove(result.DownloadUrl);
                                        if (pexelsDedupeKey != null) _usedMediaUrls.Remove(pexelsDedupeKey);
                                        try { File.Delete(fullPath); } catch { }
                                        continue;
                                    }

                                    // Frozen animal filter
                                    bool isAnimal = resultTagsLower.Contains("bird") || resultTagsLower.Contains("animal") ||
                                                    resultTagsLower.Contains("wildlife") || resultTagsLower.Contains("flamingo");
                                    if (isAnimal && clipMotion.Direction == MotionDirection.Static && clipMotion.Magnitude < 3.0)
                                    {
                                        LogToMainWindow($"   🐦 [{provider.Name}] Frozen animal — looking for animated...");
                                        _usedMediaUrls.Remove(result.DownloadUrl);
                                        if (pexelsDedupeKey != null) _usedMediaUrls.Remove(pexelsDedupeKey);
                                        try { File.Delete(fullPath); } catch { }
                                        continue;
                                    }

                                    double finalScore = vision.Score;
                                    if (vision.HasSmile)
                                    {
                                        var sentiment = StrictQueryEngine.ClassifySentiment(_currentLyric ?? "");
                                        if (sentiment == SentimentPolarity.Positive)
                                            finalScore = Math.Min(10.0, vision.Score + 1.5);
                                    }

                                    string visionTag = vision.QwenUsed ? "Qwen" : vision.OnnxUsed ? $"ONNX:{vision.TopLabel}" : ("FFmpeg" + (string.IsNullOrEmpty(vision.RejectReason) ? "" : $": {vision.RejectReason}"));
                                    LogToMainWindow($"   ✅ [{provider.Name}] Score {finalScore:F1}/10 [{visionTag}] | Motion:{clipMotion.Direction} | Children:{vision.HasChildren}");

                                    _lastDownloadedVisionScore = finalScore;
                                    _lastDownloadedDescription = vision.Description;
                                    _lastDownloadedIsStatic = clipMotion.Direction == MotionDirection.Unknown
                                                              || clipMotion.Direction == MotionDirection.Static;
                                    _lastDownloadedHasChildren = vision.HasChildren;
                                    return fullPath;
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception ex)
                                {
                                    LogToMainWindow($"   ❌ [{provider.Name}] Download error: {ex.Message.Substring(0, Math.Min(60, ex.Message.Length))}");
                                    _usedMediaUrls.Remove(result.DownloadUrl);
                                    if (pexelsDedupeKey != null) _usedMediaUrls.Remove(pexelsDedupeKey);
                                    try { File.Delete(fullPath); } catch { }
                                }
                            }

                            LogToMainWindow($"   ⚠ [{provider.Name}] All results rejected or used");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            LogToMainWindow($"   ❌ [{provider.Name}] Search error: {ex.Message.Substring(0, Math.Min(80, ex.Message.Length))}");
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    LogToMainWindow($"   ❌ Pexels fallback error: {ex.Message.Substring(0, Math.Min(80, ex.Message.Length))}");
                }
            }
            // ══════════════════════════════════════════════════════════════════

            return null;
        }

        private async Task CreateVideo(StoryBoard storyBoard = null)
        {
            var mainWindow = WpfApp.Current.MainWindow as MainWindow;
            if (mainWindow == null) return;

            await Dispatcher.InvokeAsync(() =>
            {
                mainWindow.SaveState();

                var itemsToRemove = mainWindow.timelineItems
                    .Where(i => i.Type == "Image" || i.Type == "Video" ||
                                (i.Type == "Audio" && i.Name.StartsWith("🔊")))
                    .ToList();
                foreach (var item in itemsToRemove)
                    mainWindow.timelineItems.Remove(item);
            });

            double cursor = 0;
            var addedItems = new List<TimelineItem>();

            if (chkShowLogo.IsChecked == true && !string.IsNullOrEmpty(txtLogoPath.Text) && File.Exists(txtLogoPath.Text))
            {
                double logoDuration;
                if (double.TryParse(txtLogoDuration.Text.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double ld))
                {
                    logoDuration = ld;
                }
                else
                {
                    logoDuration = 5;
                    LogToMainWindow($"⚠️ Invalid logo duration '{txtLogoDuration.Text}' — using default of 5 seconds.");
                }
                var logoItem = new TimelineItem
                {
                    Path = txtLogoPath.Text,
                    Duration = logoDuration,
                    Start = cursor,
                    End = cursor + logoDuration,
                    Name = "Channel logo - Growing with Iskra",
                    Type = "Image",
                    Volume = 100,
                    TrackIndex = 0,
                    VideoEffect = new VideoEffectData()
                };
                await Dispatcher.InvokeAsync(() => mainWindow.timelineItems.Add(logoItem));
                addedItems.Add(logoItem);
                cursor += logoDuration;
            }

            string songTitle = string.IsNullOrEmpty(txtIntroText.Text) ? "🎵 Nova pjesmica" : txtIntroText.Text;
            // DEAD-ZONE FIX: Naslov traje koliko instrumentalni uvod (max 4s).
            // If music starts immediately → only 0.5s so it doesn't "hang" while audio plays.
            double introDuration;
            if (_beatInfo != null && _beatInfo.AudioStartSeconds > 0.5)
                introDuration = Math.Min(4.0, Math.Round(_beatInfo.AudioStartSeconds, 1));
            else if (_beatInfo != null && _beatInfo.AudioStartSeconds >= 0.0 && _beatInfo.AudioStartSeconds <= 0.5)
                introDuration = 0.5;
            else
                introDuration = 4.0;
            LogToMainWindow($"🎬 Intro duration: {introDuration:F1}s (AudioStart={_beatInfo?.AudioStartSeconds:F2}s)");
            string introImagePath = await CreateTextImage(songTitle, introDuration, true);
            var introItem = new TimelineItem
            {
                Path = introImagePath,
                Duration = introDuration,
                Start = cursor,
                End = cursor + introDuration,
                Name = $"Title: {songTitle}",
                Type = "Image",
                Volume = 100,
                TrackIndex = 0,
                VideoEffect = new VideoEffectData()
            };
            await Dispatcher.InvokeAsync(() => mainWindow.timelineItems.Add(introItem));
            addedItems.Add(introItem);
            cursor += introDuration;

            _timelineCursorOffset = cursor;

            if (_enableAmbientSounds && storyBoard?.Scenes != null &&
                storyBoard.Scenes.Any(s => !string.IsNullOrEmpty(s.AmbientPath)))
            {
                AnnounceToUser(L("mixing_ambience"), 90);
                _ambientAudioPath = await MixAmbientWithMusic(
                    _audioPath, storyBoard.Scenes, _totalDuration, _tempVideoFolder);
            }

            var sortedSegments = _segments.OrderBy(s => s.StartTime).ToList();
            double maxEnd = cursor;

            double anchorBrightnessValue = 0;
            if (sortedSegments.Count > 2)
            {
                var anchorSegment = sortedSegments
                    .Where(s => !string.IsNullOrEmpty(s.Path) && File.Exists(s.Path))
                    .OrderByDescending(s => s.VisionScore)
                    .FirstOrDefault();
                if (anchorSegment != null)
                {
                    anchorBrightnessValue = 0.3 + (anchorSegment.VisionScore / 10.0) * 0.4;
                    LogToMainWindow($"🎨 Color Match anchor: '{anchorSegment.Description}' (Score={anchorSegment.VisionScore:F1}, Brightness={anchorBrightnessValue:F3})");
                }
            }

            foreach (var segment in sortedSegments)
            {
                if (!File.Exists(segment.Path)) continue;

                string ext = Path.GetExtension(segment.Path).ToLower();
                string type = (ext == ".mp4" || ext == ".avi" || ext == ".mov" || ext == ".mkv") ? "Video" : "Image";

                var newItem = new TimelineItem
                {
                    Path = segment.Path,
                    Duration = segment.Duration,
                    Start = cursor + segment.StartTime,
                    End = cursor + segment.StartTime + segment.Duration,
                    Name = segment.Description,
                    Type = type,
                    Volume = 100,
                    TrackIndex = 0,
                    VideoEffect = new VideoEffectData(),
                    ContentTag = segment.ContentTag,   // Hybrid Content Selector
                    AudioDescription = !string.IsNullOrEmpty(segment.MoodTag)
                        ? $"{segment.MoodTag}|energy={segment.Energy}" +
                          (segment.IsStaticClip ? "|static=1" : "") +
                          (segment.HasChildren ? "|haschildren=1" : "") +
                          (anchorBrightnessValue > 0 ? $"|anchor_brightness={anchorBrightnessValue.ToString("F3", CultureInfo.InvariantCulture)}" : "") +
                          // Warm White Balance tag — signal za RenderEngine da primijeni topliji balans bijele
                          "|wwb=1|wwb_filter=" + GetMoodColorFilter(segment.Emotion, null) + "," + WARM_WHITE_BALANCE_FILTER +
                          // OUTRO-FIX: propagiraj fadeout tag u AudioDescription
                          ((segment.Description ?? "").Contains("fadeout=1") ? "|fadeout=1" : "")
                        : $"{segment.Description}|energy={segment.Energy}" +
                          (segment.IsStaticClip ? "|static=1" : "") +
                          (segment.HasChildren ? "|haschildren=1" : "") +
                          "|wwb=1|wwb_filter=" + WARM_WHITE_BALANCE_FILTER +
                          // OUTRO-FIX: propagiraj fadeout tag u AudioDescription
                          ((segment.Description ?? "").Contains("fadeout=1") ? "|fadeout=1" : "")
                };
                await Dispatcher.InvokeAsync(() => mainWindow.timelineItems.Add(newItem));

                // ── HYBRID: ZoomPan za fotografije (Emotional/Portrait), Ken Burns za ostalo ──
                bool isHybridPhoto = (segment.ContentTag == "Emotional" || segment.ContentTag == "Portrait")
                                     && newItem.Type == "Image";

                float zoomEnd = 1.03f;
                float xEnd = 5f;
                float yEnd = 3f;

                if (segment.Energy >= 4)
                {
                    zoomEnd = 1.08f;
                    xEnd = 15f;
                    yEnd = 8f;
                }
                else if (segment.Energy <= 2)
                {
                    zoomEnd = 1.02f;
                    xEnd = 3f;
                    yEnd = 2f;
                }

                if (isHybridPhoto)
                {
                    // ZoomPan: polagani zoom in + horizontalni pomak — kinematski efekt za portrete i emotivne scene
                    newItem.Keyframes.Add(new AnimationKeyframe { Time = 0, Zoom = 1.0f, X = 0, Y = 0 });
                    newItem.Keyframes.Add(new AnimationKeyframe { Time = segment.Duration * 0.5f, Zoom = 1.04f, X = xEnd * 0.4f, Y = yEnd * 0.3f });
                    newItem.Keyframes.Add(new AnimationKeyframe { Time = segment.Duration, Zoom = 1.08f, X = xEnd, Y = yEnd });
                    LogToMainWindow($"   📷 ZoomPan efekt primijenjen (ContentTag={segment.ContentTag})");
                }
                else
                {
                    // Standardni Ken Burns keyframeovi
                    newItem.Keyframes.Add(new AnimationKeyframe { Time = 0, Zoom = 1.0f, X = 0, Y = 0 });
                    newItem.Keyframes.Add(new AnimationKeyframe { Time = segment.Duration * 0.3f, Zoom = 1.02f, X = 3f, Y = 2f });
                    newItem.Keyframes.Add(new AnimationKeyframe { Time = segment.Duration * 0.7f, Zoom = zoomEnd - 0.02f, X = xEnd - 5f, Y = yEnd - 3f });
                    newItem.Keyframes.Add(new AnimationKeyframe { Time = segment.Duration, Zoom = zoomEnd, X = xEnd, Y = yEnd });
                }

                if (_showLyrics && !string.IsNullOrEmpty(segment.LyricText))
                {
                    var karaokeWords = BuildKaraokeWords(segment);
                    await Dispatcher.InvokeAsync(() => mainWindow.AddSubtitle(segment.LyricText, newItem.Start, newItem.End, karaokeWords));
                }

                if (newItem.End > maxEnd) maxEnd = newItem.End;
            }

            double totalDuration = maxEnd;

            // v1.4: the default outro follows the program language
            string outroText = string.IsNullOrWhiteSpace(txtOutroText.Text) ?
                LanguageManager.T("aic_default_outro") :
                txtOutroText.Text.Trim();

            double outroDuration = 7;
            double outroStart = maxEnd;
            totalDuration = maxEnd + outroDuration;
            string outroImagePath = await CreateTextImage(outroText, outroDuration, false);
            var outroItem = new TimelineItem
            {
                Path = outroImagePath,
                Duration = outroDuration,
                Start = outroStart,
                End = outroStart + outroDuration,
                Name = $"Outro text: {outroText}",
                Type = "Image",
                Volume = 100,
                TrackIndex = 0,
                VideoEffect = new VideoEffectData()
            };
            await Dispatcher.InvokeAsync(() => mainWindow.timelineItems.Add(outroItem));
            addedItems.Add(outroItem);

            await Dispatcher.InvokeAsync(() =>
            {
                var audioItem = mainWindow.timelineItems.FirstOrDefault(i => i.Type == "Audio");
                if (audioItem != null)
                {
                    if (!string.IsNullOrEmpty(_ambientAudioPath) && File.Exists(_ambientAudioPath))
                    {
                        audioItem.Path = _ambientAudioPath;
                        LogToMainWindow("🎵 Using mixed audio with ambient sounds");
                    }
                    // v1.4 TIMELINE SYNC: every scene sits at cursor + (its time in the song), so the
                    // song itself starts at the cursor (after logo and title). It was placed at 0,
                    // which made the pictures run late by the logo+title length — and the old
                    // timeline refresh then moved the song after the last picture anyway.
                    audioItem.TrackIndex = 2;
                    audioItem.UseFixedPosition = true;
                    audioItem.FixedPosition = _timelineCursorOffset;
                    audioItem.Start = _timelineCursorOffset;
                    audioItem.Duration = Math.Max(1.0, totalDuration - _timelineCursorOffset);
                    audioItem.End = audioItem.Start + audioItem.Duration;
                }
            });

            var videoImageItems = addedItems
                .Where(i => i.Type == "Video" || i.Type == "Image")
                .OrderBy(i => i.Start)
                .ToList();

            double _lastTransitionSoundAt = double.MinValue;
            for (int i = 0; i < videoImageItems.Count - 1; i++)
            {
                var currentItem = videoImageItems[i];
                var nextItem = videoImageItems[i + 1];
                double gap = nextItem.Start - currentItem.End;

                int currentEnergy = i < _segments.Count ? _segments[i]?.Energy ?? 3 : 3;
                int nextEnergy = i + 1 < _segments.Count ? _segments[i + 1]?.Energy ?? 3 : 3;

                // FIX-ENERGY-RAMP: If energy jumps/drops by more than 2 levels,
                // limit transEnergy to ±2 from previous — prevents sudden tempo punch
                // that disrupts flow fluidity (observed @ 00:49-00:53 in Gemini analysis).
                int energyDelta = Math.Abs(nextEnergy - currentEnergy);
                int transEnergy;
                if (energyDelta > 2)
                {
                    // Ramp: koristimo srednji korak umjesto punog skoka
                    transEnergy = currentEnergy + Math.Sign(nextEnergy - currentEnergy) * 1;
                    transEnergy = Math.Max(1, Math.Min(5, transEnergy));
                    LogToMainWindow($"   ⚡ EnergyRamp: {currentEnergy}→{nextEnergy} (delta={energyDelta}) → ramp transEnergy={transEnergy}");
                }
                else
                {
                    transEnergy = (currentEnergy + nextEnergy) / 2;
                }

                // ── HYBRID CROSSFADE: 1000ms between different media types ──────────
                string currentContentTag = i < _segments.Count ? _segments[i]?.ContentTag : null;
                string nextContentTag = i + 1 < _segments.Count ? _segments[i + 1]?.ContentTag : null;
                bool isPhotoTag(string t) => t == "Emotional" || t == "Portrait";
                bool isVideoTag(string t) => t == "Action" || t == "Nature";
                bool mediaTypeSwitches = (isPhotoTag(currentContentTag) && (isVideoTag(nextContentTag) || nextContentTag == null))
                                      || (isVideoTag(currentContentTag) && (isPhotoTag(nextContentTag) || nextContentTag == null))
                                      || (isPhotoTag(nextContentTag) && (isVideoTag(currentContentTag) || currentContentTag == null))
                                      || (currentItem.Type != nextItem.Type);  // Video↔Image and in generic case
                double fadeDuration = mediaTypeSwitches ? 1.0 : (transEnergy >= 4 ? 0.3 : 0.4);
                if (mediaTypeSwitches)
                    LogToMainWindow($"   🔄 Hybrid crossfade 1000ms: {currentItem.Type}({currentContentTag ?? "?"}) → {nextItem.Type}({nextContentTag ?? "?"})");
                // ─────────────────────────────────────────────────────────────────────

                // v1.4: no pops or whooshes in calm songs (a lullaby full of pops sounded broken),
                // at most one every 8 seconds elsewhere, and quieter than before (they were
                // 55-85 %, louder than the singing).
                bool calmSong = _detectedContext is "lullaby" or "sad" or "love" or "calm" or "nature";
                bool addTransitionSound = _enableTransitionSounds && !calmSong && transEnergy >= 3 &&
                                          currentItem.End - _lastTransitionSoundAt >= 8.0;
                if (addTransitionSound) _lastTransitionSoundAt = currentItem.End;
                string soundType = transEnergy >= 4 ? "whoosh" : "pop";
                int transitionVolume = transEnergy switch
                {
                    5 => 40,
                    4 => 35,
                    3 => 30,
                    _ => 25
                };
                double transitionDuration = transEnergy >= 4 ? 0.55 : 0.4;

                if (gap > 0.1)
                {
                    AddSmoothFadeOut(currentItem, fadeDuration);
                    AddSmoothFadeIn(nextItem, fadeDuration);

                    if (addTransitionSound)
                    {
                        string transSound = await GetTransitionSound(_tempVideoFolder, soundType);
                        if (transSound != null && File.Exists(transSound))
                        {
                            await Dispatcher.InvokeAsync(() => mainWindow.timelineItems.Add(new TimelineItem
                            {
                                Path = transSound,
                                Duration = transitionDuration,
                                Start = currentItem.End - 0.1,
                                End = currentItem.End - 0.1 + transitionDuration,
                                Name = $"🔊 {(soundType == "whoosh" ? "Whoosh" : "Pop")}",
                                Type = "Audio",
                                Volume = transitionVolume,
                                TrackIndex = 3
                            }));
                        }
                    }
                }
                else
                {
                    // Bira tip tranzicije po energiji i sezoni — deterministic ali raznovrstan
                    var transType = TransitionEffect.PickForEnergy(transEnergy, _detectedSeason, i);
                    AddCrossfadeWithEnergy(currentItem, nextItem, fadeDuration, transEnergy, transType);

                    if (addTransitionSound)
                    {
                        string popSound = await GetTransitionSound(_tempVideoFolder, "pop");
                        if (popSound != null && File.Exists(popSound))
                        {
                            await Dispatcher.InvokeAsync(() => mainWindow.timelineItems.Add(new TimelineItem
                            {
                                Path = popSound,
                                Duration = transitionDuration,
                                Start = currentItem.End - 0.1,
                                End = currentItem.End - 0.1 + transitionDuration,
                                Name = "🔊 Pop",
                                Type = "Audio",
                                Volume = transitionVolume,
                                TrackIndex = 3
                            }));
                        }
                    }
                }
            }

            // v1.4: pin every generated clip to its computed time. Without this the timeline
            // refresh re-laid them end to end and all lyric timing (and the gaps between
            // lines) was lost before the render even started.
            await Dispatcher.InvokeAsync(() =>
            {
                foreach (var it in mainWindow.timelineItems)
                {
                    bool generated = it.Type == "Video" || it.Type == "Image" || (it.Name ?? "").StartsWith("🔊");
                    if (!generated || it.UseFixedPosition) continue;
                    it.UseFixedPosition = true;
                    it.FixedPosition = Math.Max(0, it.Start);
                    if ((it.Name ?? "").StartsWith("🔊")) it.TrackIndex = 3;
                }
            });

            LogToMainWindow("🎬 Updating timeline view...");
            await Dispatcher.InvokeAsync(() => mainWindow.UpdateTimelineDisplay());
            LogToMainWindow("✅ Timeline view updated");

            int segmentsCount = _segments?.Count ?? 0;
            double segmentsTotal = _segments?.Sum(s => s.Duration) ?? 0;

            WpfMessageBox.Show(
                LF("creator_finished", segmentsCount, FormatTime(segmentsTotal), segmentsTotal, FormatTime(totalDuration), totalDuration),
                L("creator_finished_title"), MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }

        /// <summary>v1.4 — opens the scene review; true = build the video, false = cancel.</summary>
        private bool ShowSceneReview()
        {
            if (!Dispatcher.CheckAccess())
                return Dispatcher.Invoke(new Func<bool>(ShowSceneReview));

            var lyricScenes = _segments
                .Where(s => s.LyricIndex >= 0 && !string.IsNullOrEmpty(s.Path))
                .OrderBy(s => s.StartTime)
                .ToList();

            var dlg = new SceneReviewDialog(lyricScenes, _lyricLines, RerollSceneAsync, FormatTime)
            {
                Owner = this
            };
            return dlg.ShowDialog() == true;
        }

        /// <summary>
        /// v1.4 — finds a NEW clip for one scene (the current one is already marked as used,
        /// so the search never returns it again), trims it to the scene length and updates the
        /// segment in place. Returns false when nothing suitable was found (old clip is kept).
        /// </summary>
        private async Task<bool> RerollSceneAsync(TimelineSegment seg, string query)
        {
            var ct = _cts?.Token ?? CancellationToken.None;
            string q = StrictQueryEngine.ValidateAndFilter(query, _songContext);
            if (string.IsNullOrWhiteSpace(q))
            {
                LogToMainWindow($"🚫 Scene review: query '{query}' rejected by the safety filter");
                return false;
            }

            string ext = Path.GetExtension(seg.Path ?? "").ToLowerInvariant();
            string mediaType = ext is ".jpg" or ".jpeg" or ".png" or ".bmp" ? "photo" : "video";

            if (seg.LyricIndex >= 0 && seg.LyricIndex < _lyricLines.Count)
                _currentLyric = _lyricLines[seg.LyricIndex];

            LogToMainWindow($"🔁 Scene review: new clip for line {seg.LyricIndex + 1} with '{q}'");
            string path = await SearchAndDownloadMedia(q, _pixabayMinHeight, mediaType, ct, seg.Duration, strictSeasonFilter: false);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;

            string finalPath = path;
            if (mediaType == "video")
            {
                double videoDuration = await GetVideoDuration(path);
                if (videoDuration > seg.Duration + 0.5)
                    finalPath = await TrimVideoToDuration(path, seg.Duration, _tempVideoFolder);
            }

            seg.Path = finalPath;
            seg.Query = q;
            seg.VisionDescription = _lastDownloadedDescription;
            seg.VisionScore = _lastDownloadedVisionScore;
            seg.IsStaticClip = _lastDownloadedIsStatic;
            seg.HasChildren = _lastDownloadedHasChildren;
            seg.IsOutdoor = _lastDownloadedIsOutdoor;
            LogToMainWindow($"   ✅ Replaced: {Path.GetFileName(finalPath)} (score {seg.VisionScore:F1}) — {seg.VisionDescription}");
            return true;
        }

        /// <summary>
        /// v1.4 — karaoke timing for a lyric segment: the line's words with times relative to
        /// the subtitle start. The subtitle starts at the segment's StartTime, so a word's
        /// offset is simply (word time − segment StartTime). Null when karaoke is off or the
        /// line has no word timing (then a plain subtitle is drawn).
        /// </summary>
        private List<KaraokeWord> BuildKaraokeWords(TimelineSegment segment)
        {
            if (!_karaoke || _lineWordTimings == null) return null;
            if (segment.LyricIndex < 0 || segment.LyricIndex >= _lineWordTimings.Count) return null;
            var words = _lineWordTimings[segment.LyricIndex];
            if (words == null || words.Count == 0) return null;

            return words.Select(w => new KaraokeWord
            {
                Text = w.Word,
                Offset = Math.Max(0, w.StartSecond - segment.StartTime),
                Duration = Math.Max(0.05, w.EndSecond - w.StartSecond)
            }).ToList();
        }

        private void AddSmoothFadeOut(TimelineItem item, double duration)
        {
            double startTime = Math.Max(0, item.Duration - duration);
            item.Keyframes.Add(new AnimationKeyframe { Time = startTime, Opacity = 1 });
            item.Keyframes.Add(new AnimationKeyframe { Time = item.Duration, Opacity = 0 });
        }

        private void AddSmoothFadeIn(TimelineItem item, double duration)
        {
            item.Keyframes.Add(new AnimationKeyframe { Time = 0, Opacity = 0 });
            item.Keyframes.Add(new AnimationKeyframe { Time = duration, Opacity = 1 });
        }

        private void AddCrossfadeWithEnergy(TimelineItem currentItem, TimelineItem nextItem,
            double duration, int nextEnergy,
            TransitionType transType = TransitionType.Fade)
        {
            // Spremi odabrani tip tranzicije na item kako bi ga RenderEngine mogao pokupit
            // via the AudioDescription field (already used for metadata transport)
            // Format: existing string + "|xfade=<type>"
            string xfadeTag = $"|xfade={TransitionTypeToFFmpeg(transType)}";
            if (!string.IsNullOrEmpty(currentItem.AudioDescription))
                currentItem.AudioDescription += xfadeTag;
            else
                currentItem.AudioDescription = xfadeTag.TrimStart('|');

            double startFade = Math.Max(0, currentItem.Duration - duration);
            currentItem.Keyframes.Add(new AnimationKeyframe { Time = startFade, Opacity = 1 });
            currentItem.Keyframes.Add(new AnimationKeyframe { Time = currentItem.Duration, Opacity = 0 });

            double fadeInDuration = nextEnergy >= 4 ? duration * 0.6 : duration;
            nextItem.Keyframes.Add(new AnimationKeyframe { Time = 0, Opacity = 0 });
            nextItem.Keyframes.Add(new AnimationKeyframe { Time = fadeInDuration, Opacity = 1 });
        }

        private static string TransitionTypeToFFmpeg(TransitionType t) => t switch
        {
            TransitionType.SlideLeft => "slideleft",
            TransitionType.SlideRight => "slideright",
            TransitionType.SlideUp => "slideup",
            TransitionType.SlideDown => "slidedown",
            TransitionType.WipeLeft => "wipeleft",
            TransitionType.WipeRight => "wiperight",
            TransitionType.ZoomIn => "zoomin",   // v1.4: "zoom" is not a valid xfade name
            TransitionType.Crossfade => "fade",
            TransitionType.Fade => "fade",
            _ => "fade"
        };

        private async Task<string> CreateTextImage(string text, double duration, bool isIntro = false)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), $"text_{Guid.NewGuid()}.png");

            await Task.Run(() =>
            {
                using (var surface = SKSurface.Create(new SKImageInfo(_targetWidth, _targetHeight)))
                {
                    var canvas = surface.Canvas;
                    canvas.Clear(isIntro ? new SKColor(46, 125, 50) : SKColors.Black);

                    using (var paint = new SKPaint())
                    {
                        paint.Color = SKColors.White;
                        paint.TextSize = isIntro ? 96 : 56;
                        paint.IsAntialias = true;
                        paint.TextAlign = SKTextAlign.Center;

                        if (isIntro)
                            paint.Typeface = SKTypeface.FromFamilyName("Segoe Script", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                                          ?? SKTypeface.FromFamilyName("Lucida Handwriting", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                                          ?? SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);

                        var words = text.Split(' ');
                        var lines = new List<string>();
                        var currentLine = "";
                        int maxCharsPerLine = isIntro ? 20 : 40;

                        foreach (var word in words)
                        {
                            var testLine = string.IsNullOrEmpty(currentLine) ? word : currentLine + " " + word;
                            if (testLine.Length > maxCharsPerLine)
                            {
                                lines.Add(currentLine);
                                currentLine = word;
                            }
                            else
                            {
                                currentLine = testLine;
                            }
                        }
                        if (!string.IsNullOrEmpty(currentLine)) lines.Add(currentLine);

                        float startY = 540 - (lines.Count - 1) * (isIntro ? 50 : 35);
                        float y = startY;
                        foreach (var line in lines)
                        {
                            canvas.DrawText(line, 960, y, paint);
                            y += isIntro ? 80 : 60;
                        }
                    }

                    using (var image = surface.Snapshot())
                    using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
                    using (var stream = File.OpenWrite(tempPath))
                    {
                        data.SaveTo(stream);
                    }
                }
            });

            return tempPath;
        }

        private string FormatTime(double seconds) => TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss");

        // v1.4: one shared Pixabay key store (see MediaProviderSettings.LoadPixabayKey)
        private string GetPixabayApiKey() => MediaProviderSettings.LoadPixabayKey();

        private void SavePixabayApiKey(string key)
        {
            if (!MediaProviderSettings.SavePixabayKey(key))
                AnnounceToUser(LF("pixabay_key_save_error", "—"));
        }

        #region Mood Color Grading i Gradient Fallback

        private async Task<string> GenerateMoodGradient(string mood, double duration, string tempDir)
        {
            string ffmpegPath = AppPaths.FFmpeg;
            if (!File.Exists(ffmpegPath)) return null;

            string color = mood?.ToLower() switch
            {
                "sad" or "melancholy" => "0x3a5f8a",
                "happy" or "joyful" => "0xffd700",
                "calm" or "peaceful" => "0x4a9e6b",
                "excited" or "upbeat" => "0xe03060",
                "romantic" or "love" => "0xc04070",
                "christmas" => "0xb01010",
                "lullaby" => "0x6a5acd",
                _ => "0xffb347"
            };

            string outputPath = Path.Combine(tempDir, $"gradient_{Guid.NewGuid().ToString().Substring(0, 8)}.mp4");
            string dStr = duration.ToString(System.Globalization.CultureInfo.InvariantCulture);

            double fadeOutStart = Math.Max(0, duration - 1);
            string fadeOutStr = fadeOutStart.ToString(System.Globalization.CultureInfo.InvariantCulture);

            string args = $"-f lavfi -i \"color=c={color}:s=1280x720:d={dStr},format=yuv420p\" " +
                          $"-vf \"fade=in:0:25,fade=out:st={fadeOutStr}:d=1\" " +
                          $"-c:v libx264 -preset veryfast -crf 23 -profile:v high -level 4.1 -pix_fmt yuv420p -an -y \"{outputPath}\"";

            var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                }
            };
            proc.Start();
            var _bgSo = proc.StandardOutput.ReadToEndAsync();
            var _bgSe = proc.StandardError.ReadToEndAsync();
            await Task.WhenAll(_bgSo, _bgSe);
            await proc.WaitForExitAsync();

            return (proc.ExitCode == 0 && File.Exists(outputPath)) ? outputPath : null;
        }

        /// <summary>
        /// ZERO-FALLBACK: Generates a black screen instead of a wrong shot.
        /// Svaki crni ekran u finalnom videu = jasna debug poruka:
        /// "dodaj ovu mapu u StrictQueryEngine".
        /// </summary>
        private async Task<string> GenerateBlackFrame(double duration, string tempDir)
        {
            string ffmpegPath = AppPaths.FFmpeg;
            if (!File.Exists(ffmpegPath)) return null;

            string outputPath = Path.Combine(tempDir, $"black_{Guid.NewGuid().ToString().Substring(0, 8)}.mp4");
            string dStr = duration.ToString(System.Globalization.CultureInfo.InvariantCulture);

            string args = $"-f lavfi -i \"color=c=black:s={_targetWidth}x{_targetHeight}:d={dStr},format=yuv420p\" " +
                          $"-c:v libx264 -preset veryfast -crf 23 -profile:v high -level 4.1 -pix_fmt yuv420p -an -y \"{outputPath}\"";

            var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                }
            };
            proc.Start();
            await Task.WhenAll(proc.StandardOutput.ReadToEndAsync(), proc.StandardError.ReadToEndAsync());
            await proc.WaitForExitAsync();

            return (proc.ExitCode == 0 && File.Exists(outputPath)) ? outputPath : null;
        }

        public static string GetMoodColorFilter(string mood, string context)
        {
            string m = (mood ?? "").ToLower();
            string c = (context ?? "").ToLower();

            if (c == "lullaby")
                return "eq=brightness=0.04:saturation=0.85:contrast=0.95,vignette=PI/4";
            if (c == "christmas")
                return "eq=saturation=1.25:contrast=1.08," +
                       "curves=r='0/0 0.5/0.56 1/1':g='0/0 0.5/0.52 1/1'";
            if (c == "party")
                return "eq=saturation=1.35:brightness=0.04:contrast=1.08," +
                       "curves=r='0/0 0.5/0.54 1/1':g='0/0 0.5/0.53 1/1'";
            if (c == "nature")
                return "eq=saturation=1.12:contrast=1.04:brightness=0.02," +
                       "curves=g='0/0 0.5/0.53 1/1'";
            if (c == "outdoor" || c == "health")
                return "eq=saturation=1.18:brightness=0.03:contrast=1.04," +
                       "curves=r='0/0 0.5/0.53 1/1':g='0/0 0.5/0.525 1/1'";
            if (c == "music" || c == "dance")
                return "eq=saturation=1.25:brightness=0.03:contrast=1.06," +
                       "curves=r='0/0 0.5/0.535 1/1':g='0/0 0.5/0.525 1/1'";
            if (c == "school")
                return "eq=saturation=1.10:brightness=0.02:contrast=1.03," +
                       "curves=r='0/0 0.5/0.52 1/1':g='0/0 0.5/0.515 1/1'";
            if (c == "animal")
                return "eq=saturation=1.08:brightness=0.02:contrast=1.03," +
                       "curves=g='0/0 0.5/0.515 1/1'";
            if (c == "sad")
                return "eq=saturation=0.88:contrast=0.96:brightness=-0.01";

            return m switch
            {
                "sad" or "melancholy" =>
                    "eq=saturation=0.90:contrast=0.97",
                "happy" or "joyful" =>
                    "eq=saturation=1.20:brightness=0.03:contrast=1.05," +
                    "curves=r='0/0 0.5/0.53 1/1':g='0/0 0.5/0.52 1/1'",
                "calm" or "peaceful" =>
                    "eq=saturation=1.02:contrast=0.98:brightness=0.01",
                "excited" or "upbeat" =>
                    "eq=saturation=1.30:contrast=1.08:brightness=0.04," +
                    "curves=r='0/0 0.5/0.54 1/1':g='0/0 0.5/0.53 1/1'",
                "romantic" or "love" =>
                    "eq=saturation=1.12:brightness=0.02," +
                    "curves=r='0/0 0.5/0.535 1/1':g='0/0 0.5/0.52 1/1'",
                "playful" =>
                    "eq=saturation=1.25:brightness=0.03:contrast=1.06," +
                    "curves=r='0/0 0.5/0.535 1/1':g='0/0 0.5/0.525 1/1'",
                _ =>
                    "eq=saturation=1.10:brightness=0.02:contrast=1.03," +
                    "curves=r='0/0 0.5/0.52 1/1':g='0/0 0.5/0.515 1/1'"
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        // HYBRID CONTENT SELECTOR — Warm White Balance + ZoomPan za fotografije
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Globalni Warm White Balance filter koji se primjenjuje na SVE medije
        /// (i video i fotografije) kako bi se vizuelno stopili.
        /// Boosts the red channel and slightly lowers blue — "golden hour" effect.
        /// </summary>
        public const string WARM_WHITE_BALANCE_FILTER =
            "curves=r='0/0 0.5/0.57 1/1':b='0/0 0.5/0.44 1/0.92'," +
            "eq=saturation=1.12:brightness=0.02:contrast=1.03";

        /// <summary>
        /// Determines the ContentTag of a scene based on Emotion, Keywords and Energy.
        /// "Emotional" | "Portrait" → fotografija + ZoomPan
        /// "Action"    | "Nature"   → video
        /// Null/ostalo → mediaType iz UI
        /// </summary>
        private static string DetermineContentTag(StoryScene scene)
        {
            string emotion = (scene.Emotion ?? "").ToLowerInvariant();
            string keywords = (scene.Keywords ?? "").ToLowerInvariant();
            string action = (scene.Action ?? "").ToLowerInvariant();

            // Emotional / Portrait → fotografija
            if (emotion is "sad" or "melancholy" or "romantic" or "love" or "tender" or "nostalgic")
                return "Emotional";
            if (keywords.Contains("portrait") || keywords.Contains("face") ||
                keywords.Contains("closeup") || keywords.Contains("close-up"))
                return "Portrait";

            // Action / Nature → video
            if (scene.Energy >= 4 ||
                action.Contains("run") || action.Contains("jump") || action.Contains("dance") ||
                emotion is "excited" or "energetic" or "upbeat")
                return "Action";

            if (keywords.Contains("nature") || keywords.Contains("forest") ||
                keywords.Contains("beach") || keywords.Contains("mountain") ||
                keywords.Contains("river") || keywords.Contains("flowers") ||
                (scene.Energy <= 2 && keywords.Contains("outdoor")))
                return "Nature";

            return null; // koristi UI mediaType
        }

        #endregion

        #region Sub-scene detekcija — Literal Visual Sync

        private List<string> DetectSubSceneQueries(string lyric, string style)
        {
            if (string.IsNullOrWhiteSpace(lyric)) return null;
            string lower = StrictQueryEngine.Fold(lyric);

            bool hasWinterClothing = (lower.Contains("cizme") || lower.Contains("izme")) &&
                                     (lower.Contains("rukavice") || lower.Contains("skafander") ||
                                      lower.Contains("kapu") || lower.Contains("sal") || lower.Contains("kaput"));
            if (hasWinterClothing)
            {
                var q = new List<string>();
                if (lower.Contains("cizme") || lower.Contains("izme"))
                    q.Add(BuildLiteralSearchQuery("child winter boots snow outdoor", "", "", style));
                if (lower.Contains("rukavice"))
                    q.Add(BuildLiteralSearchQuery("child winter gloves snow hands", "", "", style));
                if (lower.Contains("skafander") || lower.Contains("kombinezon"))
                    q.Add(BuildLiteralSearchQuery("child winter snowsuit playing snow", "", "", style));
                if (lower.Contains("kapu") || lower.Contains("kapi") || lower.Contains("sesir"))
                    q.Add(BuildLiteralSearchQuery("child wearing winter hat snow smiling", "", "", style));
                if (lower.Contains("sal") || lower.Contains("salom"))
                    q.Add(BuildLiteralSearchQuery("child scarf winter cozy warm", "", "", style));
                if (lower.Contains("kaput") || lower.Contains("jakna"))
                    q.Add(BuildLiteralSearchQuery("child winter coat dressed warm outdoor", "", "", style));
                if (q.Count >= 2) return q;
            }

            int seasonCount = 0;
            if (lower.Contains("prolec") || lower.Contains("proljec") || lower.Contains("spring")) seasonCount++;
            if (lower.Contains("jesen") || lower.Contains("autumn") || lower.Contains("fall")) seasonCount++;
            if (lower.Contains("zima") || lower.Contains("winter")) seasonCount++;
            if (lower.Contains("leto") || lower.Contains("ljeto") || lower.Contains("summer")) seasonCount++;

            if (seasonCount >= 2)
            {
                var q = new List<string>();
                if (lower.Contains("prolec") || lower.Contains("proljec"))
                    q.Add(BuildLiteralSearchQuery("child spring flowers park playing", "", "", style));
                if (lower.Contains("jesen") || lower.Contains("autumn") || lower.Contains("fall"))
                    q.Add(BuildLiteralSearchQuery("child autumn leaves park playing", "", "", style));
                if (lower.Contains("zima") || lower.Contains("winter"))
                    q.Add(BuildLiteralSearchQuery("child winter snow playing outdoor", "", "", style));
                if (lower.Contains("leto") || lower.Contains("ljeto") || lower.Contains("summer"))
                    q.Add(BuildLiteralSearchQuery("child summer sunny park playing", "", "", style));
                if (q.Count >= 2) return q;
            }

            int instCount = 0;
            if (lower.Contains("gitara") || lower.Contains("guitar")) instCount++;
            if (lower.Contains("klavir") || lower.Contains("piano")) instCount++;
            if (lower.Contains("bubanj") || lower.Contains("drum")) instCount++;
            if (lower.Contains("violina") || lower.Contains("violin")) instCount++;
            if (lower.Contains("flauta") || lower.Contains("flute")) instCount++;

            if (instCount >= 2)
            {
                var q = new List<string>();
                if (lower.Contains("gitara") || lower.Contains("guitar"))
                    q.Add(BuildLiteralSearchQuery("child playing guitar music happy", "", "", style));
                if (lower.Contains("klavir") || lower.Contains("piano"))
                    q.Add(BuildLiteralSearchQuery("child playing piano keyboard music", "", "", style));
                if (lower.Contains("bubanj") || lower.Contains("drum"))
                    q.Add(BuildLiteralSearchQuery("child playing drums music fun", "", "", style));
                if (lower.Contains("violina") || lower.Contains("violin"))
                    q.Add(BuildLiteralSearchQuery("child playing violin music", "", "", style));
                if (lower.Contains("flauta") || lower.Contains("flute"))
                    q.Add(BuildLiteralSearchQuery("child playing flute music", "", "", style));
                if (q.Count >= 2) return q;
            }

            int actionCount = 0;
            bool hasRun = lower.Contains("trci") || lower.Contains("trcanje");
            bool hasJump = lower.Contains("skace") || lower.Contains("skoci");
            bool hasSing = lower.Contains("peva") || lower.Contains("pjeva");
            bool hasDance = lower.Contains("plese") || lower.Contains("pleses") || lower.Contains("ples");
            bool hasSmile = lower.Contains("smej") || lower.Contains("smije") || lower.Contains("blistaj");
            bool hasWalk = lower.Contains("seta") || lower.Contains("setaj") || lower.Contains("hoda");

            if (hasRun) actionCount++;
            if (hasJump) actionCount++;
            if (hasSing) actionCount++;
            if (hasDance) actionCount++;
            if (hasSmile) actionCount++;

            if (actionCount >= 2)
            {
                var q = new List<string>();
                if (hasRun) q.Add(BuildLiteralSearchQuery("children running fast outdoor happy", "", "", style));
                if (hasJump) q.Add(BuildLiteralSearchQuery("child jumping happy excited outdoor", "", "", style));
                if (hasSing) q.Add(BuildLiteralSearchQuery("child singing joyful music", "", "", style));
                if (hasDance) q.Add(BuildLiteralSearchQuery("children dancing joyful colorful", "", "", style));
                if (hasSmile) q.Add(BuildLiteralSearchQuery("children laughing happy smiling faces", "", "", style));
                if (q.Count >= 2) return q;
            }

            var animalMap = new Dictionary<string, string>
            {
                {"pas ",  "dog playing happy"}, {"psa ", "dog playing happy"}, {"psic", "puppy cute"},
                {"macka", "cat cute kitten"},   {"mace", "cat cute kitten"},
                {"zec",   "rabbit cute bunny"}, {"kunic", "rabbit cute"},
                {"ptica", "bird flying colorful"}, {"ptice", "birds flying"},
                {"riba",  "fish aquarium colorful"}, {"ribica", "fish colorful water"},
                {"konj",  "horse running field"}, {"medved", "bear forest nature"},
                {"slon",  "elephant nature wild"}, {"leptir", "butterfly flower garden"}
            };
            var foundAnimals = animalMap.Where(kv => lower.Contains(kv.Key)).ToList();
            if (foundAnimals.Count >= 2)
            {
                return foundAnimals.Take(4)
                    .Select(kv => BuildLiteralSearchQuery("child " + kv.Value, "", "", style))
                    .ToList();
            }

            return null;
        }

        private async Task<string> BuildSubSceneVideo(
            List<string> queries, double totalDuration, string tempDir, CancellationToken ct)
        {
            double clipDur = Math.Max(1.5, Math.Round(totalDuration / queries.Count, 1));
            var clipPaths = new List<string>();

            foreach (var rawQuery in queries)
            {
                ct.ThrowIfCancellationRequested();
                // v1.4: every montage clip is a children's shot too (the montage searches skipped the
                // "child in every shot" rule, which is where the steaming pot, the parking lot and
                // the meerkat came from)
                string query = MentionsChild(rawQuery) ? rawQuery : rawQuery + " child";
                // v1.4: the vision AI judges this montage part by what IT must show, not by the
                // whole line (an ice-cream clip scored 1/10 because the line also names tea)
                _montageFocus = rawQuery;
                string path;
                try { path = await SearchAndDownloadMedia(query, _pixabayMinHeight, "video", ct, clipDur); }
                finally { _montageFocus = null; }
                if (path == null) continue;

                double actualDur = await GetVideoDuration(path);
                if (actualDur > clipDur + 0.3)
                    path = await TrimVideoToDuration(path, clipDur, tempDir);
                clipPaths.Add(path);
            }

            if (clipPaths.Count == 0) return null;
            if (clipPaths.Count == 1) return clipPaths[0];

            string ffmpegPath = AppPaths.FFmpeg;
            if (!File.Exists(ffmpegPath)) return clipPaths[0];

            string concatFile = Path.Combine(tempDir, $"subscene_{Guid.NewGuid().ToString().Substring(0, 8)}.txt");
            string outputPath = Path.Combine(tempDir, $"subscene_out_{Guid.NewGuid().ToString().Substring(0, 8)}.mp4");

            using (var sw = new StreamWriter(concatFile, false, new System.Text.UTF8Encoding(false)))
                foreach (var p in clipPaths)
                    sw.WriteLine("file '" + p.Replace("\\", "/") + "'");

            string args = $"-nostdin -f concat -safe 0 -i \"{concatFile}\" " +
                          $"-vf \"scale={_targetWidth}:{_targetHeight}:flags=lanczos,fps=25,format=yuv420p\" " +
                          $"-c:v h264_nvenc -preset p2 -rc vbr -cq 23 -b:v 0 -profile:v high -level 4.1 " +
                          $"-fps_mode cfr -r 25 -an -y \"{outputPath}\"";
            var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                }
            };
            proc.Start();
            var soTask = proc.StandardOutput.ReadToEndAsync();
            var seTask = proc.StandardError.ReadToEndAsync();
            await Task.WhenAll(soTask, seTask);
            await proc.WaitForExitAsync(ct);
            bool ok = proc.ExitCode == 0;

            // v1.4: the montage was NVENC-only; without an NVIDIA card (or when NVENC refuses) it
            // fell back to the first clip alone. Retry once on the CPU encoder.
            if (!ok || !File.Exists(outputPath) || new FileInfo(outputPath).Length <= 1000)
            {
                string cpuArgs = args.Replace("-c:v h264_nvenc -preset p2 -rc vbr -cq 23 -b:v 0", "-c:v libx264 -preset veryfast -crf 21");
                using var proc2 = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpegPath, Arguments = cpuArgs, CreateNoWindow = true, UseShellExecute = false,
                    RedirectStandardError = true, RedirectStandardOutput = true
                });
                if (proc2 != null)
                {
                    var so2 = proc2.StandardOutput.ReadToEndAsync();
                    var se2 = proc2.StandardError.ReadToEndAsync();
                    await Task.WhenAll(so2, se2);
                    await proc2.WaitForExitAsync(ct);
                    ok = proc2.ExitCode == 0;
                    if (!ok) AppLog.Warn("Montage (concat) failed on GPU and CPU: " + LastLines(se2.Result));
                }
            }

            if (ok && File.Exists(outputPath) && new FileInfo(outputPath).Length > 1000)
                return outputPath;

            return clipPaths[0];
        }

        #endregion

        #region MultiClip — multiple videos per scene
        private async Task<string> SearchAndDownloadMultipleMedia(
            string keywords, string fallbackKeywords, string mediaType,
            double targetDuration, string tempDir, CancellationToken ct)
        {
            // ZERO-FALLBACK guard: null keywords → odmah null
            if (string.IsNullOrWhiteSpace(keywords)) return null;
            int clipCount = Math.Max(2, (int)Math.Ceiling(targetDuration / 5.0));
            // v1.4: up to 8 clips (was 4) — scenes now last the whole line, up to 60 s
            clipCount = Math.Min(clipCount, 8);

            double clipDuration = targetDuration / clipCount;
            var clipPaths = new List<string>();

            LogToMainWindow($"🎬 MultiClip: {clipCount} clips × {clipDuration:F1}s = {targetDuration:F1}s total");

            for (int c = 0; c < clipCount; c++)
            {
                ct.ThrowIfCancellationRequested();
                string q = (c % 2 == 0) ? keywords : (fallbackKeywords ?? keywords);
                string path = await SearchAndDownloadMedia(q, _pixabayMinHeight, mediaType, ct, clipDuration);
                if (path == null && fallbackKeywords != null)
                    path = await SearchAndDownloadMedia(fallbackKeywords, _pixabayMinHeight, mediaType, ct, clipDuration);
                if (path != null)
                    clipPaths.Add(path);
            }

            if (clipPaths.Count == 0) return null;
            if (clipPaths.Count == 1) return clipPaths[0];

            string ffmpegPath = AppPaths.FFmpeg;
            if (!File.Exists(ffmpegPath)) return clipPaths[0];

            string concatList = Path.Combine(tempDir, $"multiclip_{Guid.NewGuid().ToString().Substring(0, 8)}.txt");
            string outputPath = Path.Combine(tempDir, $"multi_{Guid.NewGuid().ToString().Substring(0, 8)}.mp4");

            var preparedPaths = new List<string>();
            foreach (var cp in clipPaths)
            {
                double dur = await GetVideoDuration(cp);
                string prepared = cp;
                if (dur > clipDuration + 0.5)
                    prepared = await TrimVideoToDuration(cp, clipDuration, tempDir);
                preparedPaths.Add(prepared);
            }

            using (var sw = new System.IO.StreamWriter(concatList, false, new System.Text.UTF8Encoding(false)))
                foreach (var p in preparedPaths)
                    sw.WriteLine("file '" + p.Replace("\\", "/") + "'");

            string args = $"-nostdin -f concat -safe 0 -i \"{concatList}\" " +
                          $"-vf \"scale={_targetWidth}:{_targetHeight}:flags=lanczos,fps=25,format=yuv420p\" " +
                          $"-c:v h264_nvenc -preset p2 -rc vbr -cq 23 -b:v 0 -profile:v high -level 4.1 " +
                          $"-fps_mode cfr -r 25 -an -y \"{outputPath}\"";

            var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                }
            };
            proc.Start();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdoutTask, stderrTask);
            await proc.WaitForExitAsync(ct);
            string ffmpegErr = stderrTask.Result;

            if ((proc.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length < 1000)
                && (ffmpegErr.Contains("nvenc") || ffmpegErr.Contains("No NVENC") || ffmpegErr.Contains("Cannot load")))
            {
                LogToMainWindow("⚠ MultiClip: NVENC unavailable, using libx264...");
                string argsCpu = $"-nostdin -f concat -safe 0 -i \"{concatList}\" " +
                                 $"-vf \"scale={_targetWidth}:{_targetHeight}:flags=lanczos,fps=25,format=yuv420p\" " +
                                 $"-c:v libx264 -preset fast -crf 23 -profile:v high -level 4.1 " +
                                 $"-fps_mode cfr -r 25 -an -y \"{outputPath}\"";
                var proc2 = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = argsCpu,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    }
                };
                proc2.Start();
                var so2 = proc2.StandardOutput.ReadToEndAsync();
                var se2 = proc2.StandardError.ReadToEndAsync();
                await Task.WhenAll(so2, se2);
                await proc2.WaitForExitAsync(ct);
                ffmpegErr = se2.Result;
                if (proc2.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 1000)
                {
                    LogToMainWindow($"✅ MultiClip merged (libx264): {preparedPaths.Count} clips → {targetDuration:F1}s");
                    return outputPath;
                }
            }

            if (proc.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 1000)
            {
                LogToMainWindow($"✅ MultiClip merged: {preparedPaths.Count} clips → {targetDuration:F1}s");
                return outputPath;
            }

            if (!string.IsNullOrEmpty(ffmpegErr))
            {
                var errLines = ffmpegErr.Split('\n');
                var realError = string.Join("\n", errLines
                    .Where(l => l.Contains("Error") || l.Contains("Invalid") || l.Contains("failed") || l.Contains("error") || l.Contains("moov"))
                    .Take(5));
                if (!string.IsNullOrEmpty(realError))
                    LogToMainWindow($"⚠ MultiClip FFmpeg error: {realError}");
                else
                    LogToMainWindow($"⚠ MultiClip FFmpeg exit={proc.ExitCode}");
            }

            LogToMainWindow("⚠ MultiClip concat failed — using the first clip");
            return clipPaths[0];
        }
        #endregion
        // v1.4 — every candidate clip or picture downloaded in this run. Rejected candidates
        // (wrong content, too dark, not chosen) used to stay in the project folder for ever:
        // one test project collected more than 1000 clips (27 GB). At the end of a run every
        // download the timeline does not use is deleted.
        private readonly List<string> _downloadsThisRun = new();

        // v1.4 — what the current montage part must show (null outside montages)
        private string _montageFocus;

        /// <summary>v1.4 — the lyric text the vision AI judges a clip against (plus the montage part, if any).</summary>
        private string VisionLyricContext()
        {
            if (string.IsNullOrEmpty(_montageFocus)) return _currentLyric;
            return (string.IsNullOrEmpty(_currentLyric) ? "" : _currentLyric + " — ") +
                   $"this clip is ONE part of a montage for this line and only needs to show: {_montageFocus}";
        }

        // v1.4 — the current lyric line names its own season (see the strict season check)
        private bool _lyricNamesSeason;

        // v1.4 — word starts of things a child can see in a picture (see LITERAL FIRST)
        private static readonly string[] _concreteThingStems =
        {
            "sladoled", "cokolad", "=caj", "=caja", "=caju", "=cajem", "kakao", "tort", "kolac", "jabuk", "banan",
            "jagod", "lubenic", "=sok", "=soka", "mlek", "mlijek", "=pica", "=picu", "pizz", "sendvic",
            "romobil", "bicikl", "rolsul", "trotinet", "lopt", "balon", "zmaj", "igrack", "autic",
            "ljuljas", "tobogan", "klackalic", "peskar", "pjeskar", "kisobran", "cizm", "rukavic",
            "skafander", "=kapu", "=kapa", "=sal", "jakn",
            "=pas", "=psa", "psic", "=maca", "mack", "macic", "=zec", "zecic", "patk", "ptic",
            "leptir", "konj", "=riba", "ribic", "pcel", "bubamar"
        };

        /// <summary>v1.4 — the season a stock clip shows, from its tags (same words as the strict season check).</summary>
        private static string SeasonFromTags(string t)
        {
            // whole words only, and "ice cream" is not ice: before, an ice-cream clip counted as
            // winter and "waterfall" as autumn
            t = System.Text.RegularExpressions.Regex.Replace((t ?? "").ToLowerInvariant(), @"ice[\s-]?creams?|iced\b", " ");
            bool Has(params string[] words) => words.Any(w =>
                System.Text.RegularExpressions.Regex.IsMatch(t, $@"\b{System.Text.RegularExpressions.Regex.Escape(w)}\b"));
            if (Has("snow", "winter", "frost", "ice", "frozen", "cold", "blizzard", "snowflake", "snowflakes")) return "winter";
            if (Has("autumn", "fall", "leaves", "foliage", "maple")) return "autumn";
            if (Has("spring", "blossom", "blossoms", "bloom", "tulip", "tulips", "daffodil")) return "spring";
            if (Has("summer", "beach", "tropical", "heat", "swimming pool", "sunbath")) return "summer";
            return "none";
        }

        private static bool IsWarmDrinkLyric(string lyric) =>
            !string.IsNullOrEmpty(lyric) &&
            SerbianText.HasAny(SerbianText.Normalize(lyric), "=caj", "=caja", "=caju", "=cajem", "cokolad", "kakao", "topli napit", "toplo mleko", "toplo mlijeko");

        private static bool IsCozyTag(string tag) =>
            tag.Contains("fireplace") || tag.Contains("cozy") || tag.Contains("hot drink") || tag.Contains("tea") ||
            tag.Contains("candle") || tag.Contains("living room") || tag.Contains("indoor");

        private static bool MentionsChild(string query) =>
            System.Text.RegularExpressions.Regex.IsMatch(query ?? "",
                @"\b(child|children|kid|kids|toddler|baby|babies|boy|girl|family|grandchild|grandchildren|preschool|kindergarten)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>
        /// v1.4 — downloads to a file and CLOSES it before returning. Before, the file was opened with
        /// "using var" and FileShare.None, so it stayed locked (and partly unflushed) until the end of the
        /// loop iteration. FFmpeg could not open the clip for the brightness check, the vision AI (Qwen)
        /// and the motion analysis — every clip got the fallback score 6.0/10 and "Motion: Unknown", and
        /// the AI never actually looked at a single clip.
        /// </summary>
        private async Task DownloadToFileAsync(string url, string path, CancellationToken ct)
        {
            using (var dlStream = await _dlHttpClient.GetStreamAsync(url, ct))
            using (var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, true))
            {
                await dlStream.CopyToAsync(fileStream, ct);
                await fileStream.FlushAsync(ct);
            }
        }

        private static string LastLines(string text, int n = 3) =>
            string.Join(" | ", (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(n));

        private string NewDownloadPath(string fileName)
        {
            string path = Path.Combine(GetCurrentProjectFolder(), fileName);
            lock (_downloadsThisRun) _downloadsThisRun.Add(path);
            return path;
        }

        /// <summary>
        /// v1.4 — deletes the downloads of this run and the temporary files of this run that no
        /// timeline item uses. What the timeline uses stays (the project keeps working, and
        /// saving copies the temporary files into the project's media folder).
        /// </summary>
        private void DeleteUnusedMedia(string tempFolder)
        {
            try
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Dispatcher.Invoke(() =>
                {
                    if (WpfApp.Current.MainWindow is MainWindow mw)
                        foreach (var it in mw.timelineItems)
                        {
                            if (!string.IsNullOrEmpty(it.Path)) used.Add(Path.GetFullPath(it.Path));
                            if (!string.IsNullOrEmpty(it.AmbientSoundPath)) used.Add(Path.GetFullPath(it.AmbientSoundPath));
                        }
                });

                var candidates = new List<string>();
                lock (_downloadsThisRun) { candidates.AddRange(_downloadsThisRun); _downloadsThisRun.Clear(); }
                if (!string.IsNullOrEmpty(tempFolder) && Directory.Exists(tempFolder))
                    candidates.AddRange(Directory.GetFiles(tempFolder, "*", SearchOption.AllDirectories));

                int deleted = 0;
                long bytes = 0;
                foreach (string f in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (!File.Exists(f) || used.Contains(Path.GetFullPath(f))) continue;
                        long len = new FileInfo(f).Length;
                        File.Delete(f);
                        deleted++;
                        bytes += len;
                    }
                    catch (Exception ex) { AppLog.Warn($"Could not delete the unused file {f}: {ex.Message}"); }
                }
                if (deleted > 0)
                    LogToMainWindow($"🧹 Removed {deleted} unused downloads and temporary files ({bytes / (1024.0 * 1024.0):F0} MB)");
            }
            catch (Exception ex)
            {
                AppLog.Error("Removing unused downloads", ex);
            }
        }

        private string GetCurrentProjectFolder()
        {
            string folder = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (WpfApp.Current.MainWindow is MainWindow mainWin)
                        folder = mainWin.GetCurrentProjectFolder();
                });
            }
            catch { }
            // v1.4: with a project that was never saved the main window has no folder (""), so
            // clips were downloaded to "AI_xxxx.mp4" — a RELATIVE path in whatever the current
            // directory was. The render (which runs FFmpeg from the TEMP folder) then could not
            // find them ("Error opening input files"). Use this run's temp folder instead; it is
            // copied into the project folder when the project is saved.
            if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathRooted(folder))
            {
                folder = !string.IsNullOrEmpty(_tempVideoFolder) ? _tempVideoFolder : Path.GetTempPath();
                Directory.CreateDirectory(folder);
            }
            return folder;
        }

        private void LogToMainWindow(string message)
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (WpfApp.Current.MainWindow is MainWindow mainWin)
                        mainWin.LogMessage(message, true);
                });
            }
            catch { }
        }

        private void AnnounceToUser(string message, int progressPercent = -1)
        {
            Dispatcher.Invoke(() =>
            {
                txtOllamaStatus.Text = message;
                if (progressPercent >= 0)
                {
                    prgProgress.Value = progressPercent;
                    txtProgress.Text = $"{progressPercent}% - {message}";
                }
                // CreatePeerForElement umjesto FromElement — vidi napomenu u MainWindow.LogMessage
                var peer = UIElementAutomationPeer.CreatePeerForElement(txtOllamaStatus) ?? UIElementAutomationPeer.FromElement(txtOllamaStatus);
                peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            });
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning) { _cts?.Cancel(); AnnounceToUser(L("cancelling_msg")); btnCancel.Content = L("cancelling_button"); btnCancel.IsEnabled = false; }
            else { DialogResult = false; Close(); }
        }

        /// <summary>
        /// Postavlja Whisper word-level timestamps koji se koriste za preciznu sinhronizaciju
        /// text and video at the level of individual words.
        /// </summary>
        public void SetWordTimings(List<AITranscription.WordTiming> wordTimings)
        {
            if (wordTimings == null) return;
            _wordTimings = wordTimings;
            LogToMainWindow($"🔤 Word timings set: {_wordTimings.Count} words");
        }

        /// <summary>
        /// Saves API key for the given media provider (encrypted on disk).
        /// </summary>
        public void SaveMediaProviderKey(string providerName, string key)
        {
            if (string.IsNullOrWhiteSpace(providerName) || string.IsNullOrWhiteSpace(key)) return;
            try
            {
                MediaProviderSettings.SaveKey(providerName, key);
                if (string.Equals(providerName, "Pixabay", StringComparison.OrdinalIgnoreCase))
                    _pixabayApiKey = key;
                LogToMainWindow($"✅ API key for {providerName} saved.");
            }
            catch (Exception ex)
            {
                LogToMainWindow($"❌ Error saving key for {providerName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Deletes API key for the given media provider.
        /// </summary>
        public void DeleteMediaProviderKey(string providerName)
        {
            if (string.IsNullOrWhiteSpace(providerName)) return;
            try
            {
                MediaProviderSettings.DeleteKey(providerName);
                if (string.Equals(providerName, "Pixabay", StringComparison.OrdinalIgnoreCase))
                    _pixabayApiKey = null;
                LogToMainWindow($"🗑 API key for {providerName} deleted.");
            }
            catch (Exception ex)
            {
                LogToMainWindow($"❌ Error deleting key for {providerName}: {ex.Message}");
            }
        }
    }

    #region Helper Classes

    public class StoryScene
    {
        public int SceneNumber { get; set; }
        public string Description { get; set; }
        /// <summary>Original, untruncated lyric text — used for StrictQueryEngine matching.</summary>
        public string FullLyric { get; set; }
        public string Emotion { get; set; }
        public int Energy { get; set; }
        public string Characters { get; set; }
        public string Action { get; set; }
        public string Location { get; set; }
        public string Keywords { get; set; }
        /// <summary>Asocijativni fallback query ako primarni nema rezultata (iz AI interpretacije).</summary>
        public string FallbackKeywords { get; set; }
        /// <summary>v1.4 — Keywords came from the director pass (whole-song plan) and passed validation.</summary>
        public bool KeywordsFromDirector { get; set; }
        public string AmbientSound { get; set; }
        public double StartTime { get; set; }
        public double Duration { get; set; }
        public string AmbientPath { get; set; }
        public double VisionScore { get; set; } = 6.0;
        public bool IsStaticClip { get; set; } = false;
        public bool HasChildren { get; set; } = false;
        public bool IsOutdoor { get; set; } = false;
        public string ContentTag { get; set; }

        /// <summary>Wide / Medium / Close — shot type, affects pacing and crossfade length.</summary>
        public string ShotType { get; set; } = "medium";

        /// <summary>Fast(1.5-2s) / Standard(2.8-3.2s) / Slow(5-6s) — Rhythmic Variation kategorija.</summary>
        public string PacingCategory { get; set; } = "standard";
    }

    public class StoryBoard
    {
        public List<StoryScene> Scenes { get; set; }
        public string MainCharacter { get; set; }
        public string OverallTheme { get; set; }
    }

    public class TimelineSegment
    {
        public string Path { get; set; }
        public double Duration { get; set; }
        public double StartTime { get; set; }
        public string Description { get; set; }
        public string LyricText { get; set; }
        public string AmbientSoundPath { get; set; }
        public string VoiceNarrationPath { get; set; }
        public int Energy { get; set; }
        public string Emotion { get; set; }
        public string MoodTag { get; set; }
        public double VisionScore { get; set; } = 6.0;
        public bool IsStaticClip { get; set; } = false;
        public bool HasChildren { get; set; } = false;
        public bool IsOutdoor { get; set; } = false;
        /// <summary>
        /// Hybrid Content Selector tag propagiran iz StoryScene.
        /// </summary>
        public string ContentTag { get; set; }
        /// <summary>Per-stih sentiment polaritet — Positive/Negative/Neutral.</summary>
        public string Sentiment { get; set; } = "Neutral";

        // ── v1.4 ──
        /// <summary>Index of the lyric line this segment shows, or -1 for intro/instrumental b-roll.</summary>
        public int LyricIndex { get; set; } = -1;
        /// <summary>The search query that found this clip (shown and editable in scene review).</summary>
        public string Query { get; set; }
        /// <summary>What the clip shows, as described by the vision AI (for the blind user).</summary>
        public string VisionDescription { get; set; }
    }

    public class SongAnalysis
    {
        [Newtonsoft.Json.JsonProperty("context")]
        public string Context { get; set; }
        [Newtonsoft.Json.JsonProperty("mood")]
        public string Mood { get; set; }
        [Newtonsoft.Json.JsonProperty("theme")]
        public string Theme { get; set; }
        [Newtonsoft.Json.JsonProperty("visual_style")]
        public string VisualStyle { get; set; }
        [Newtonsoft.Json.JsonProperty("main_subject")]
        public string MainSubject { get; set; }
        [Newtonsoft.Json.JsonProperty("season")]
        public string Season { get; set; }
        [Newtonsoft.Json.JsonProperty("setting")]
        public string Setting { get; set; }
    }

    public class SceneKeywords
    {
        [Newtonsoft.Json.JsonProperty("line")]
        public int Line { get; set; }
        [Newtonsoft.Json.JsonProperty("keywords")]
        public string Keywords { get; set; }
        /// <summary>Asocijativni fallback query — koristi se ako primaryQuery ne daje rezultate.</summary>
        [Newtonsoft.Json.JsonProperty("fallback_keywords")]
        public string FallbackKeywords { get; set; }
        [Newtonsoft.Json.JsonProperty("ambient")]
        public string Ambient { get; set; }
        /// <summary>v1.4 — true when the keywords came from the AI director and passed validation.</summary>
        [Newtonsoft.Json.JsonIgnore]
        public bool FromAI { get; set; }
    }

    #endregion

}