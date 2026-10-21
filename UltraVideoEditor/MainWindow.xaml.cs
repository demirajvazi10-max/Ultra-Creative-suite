#nullable disable
using SkiaSharp;
using System.Globalization;
using Timer = System.Windows.Forms.Timer;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using Microsoft.Win32;
using System.Windows.Forms.Integration;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using System.Windows.Input;
using System.Drawing;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
// === AMBIGUITY RESOLUTIONS ===
using WinForms = System.Windows.Forms;
using WpfDragDrop = System.Windows.DragDrop;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WpfDragDropEffects = System.Windows.DragDropEffects;
using WpfDataFormats = System.Windows.DataFormats;
using WinFormsKeyEventArgs = System.Windows.Forms.KeyEventArgs;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfClipboard = System.Windows.Clipboard;
using WpfMessageBox = System.Windows.MessageBox;
using WpfApplication = System.Windows.Application;
using WinFormsApplication = System.Windows.Forms.Application;
using WpfSaveFileDialog = Microsoft.Win32.SaveFileDialog;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WinFormsSaveFileDialog = System.Windows.Forms.SaveFileDialog;
using WinFormsOpenFileDialog = System.Windows.Forms.OpenFileDialog;
using WpfPoint = System.Windows.Point;
using DrawingPoint = System.Drawing.Point;

namespace UltraVideoEditor
{
    public partial class MainWindow : Window
    {
        // Phase 3 — stores last highlight result
        private HighlightResult _lastHighlightResult     = null;
        private string          _lastHighlightSourceVideo = null;
        private string          _lastHighlightMusicPath   = null;

        // Language helper
        private string _LangCode => LanguageManager.CurrentLanguage ?? "en";
        private string L(string key) => LanguageManager.GetText(key, _LangCode);
        private string LF(string key, params object[] args) => string.Format(LanguageManager.GetText(key, _LangCode), args);

        private string _introText = "🎵 New children's song";
        private string _outroText = "Author: Iskra Ajvazi. Music and lyrics: Iskra Ajvazi. For more wonderful songs, follow our YouTube channel: @Rastimo uz Iskru";

        public string IntroText
        {
            get => _introText;
            set => _introText = value;
        }

        public string OutroText
        {
            get => _outroText;
            set => _outroText = value;
        }
        public List<TimelineItem> timelineItems = new List<TimelineItem>();
        private List<SubtitleItem> subtitles = new List<SubtitleItem>();
        private string _tempVideoFolder = Path.GetTempPath();
        private List<double> markers = new List<double>();
        private List<TransitionEffect> transitions = new List<TransitionEffect>();

        // ── Display mode (Sighted / Accessibility / Low Vision) ──────────────
        // accessibilityMode is kept as a read-only computed property so the
        // ~20 existing "if (accessibilityMode) ..." call sites throughout this
        // file keep working unchanged; the actual state now lives in _uiMode.
        private enum UIMode { Sighted, Accessibility, LowVision }
        private UIMode _uiMode = UIMode.Sighted;
        private bool accessibilityMode => _uiMode == UIMode.Accessibility;

        // ── Low Vision text/UI scale ──────────────────────────────────────
        // Applies to every WPF window via UiScaling (LayoutTransform-based).
        // The native timelineListView (WindowsFormsHost) ignores WPF
        // LayoutTransform, so it's rescaled separately in
        // ApplyNativeListViewScale() using its own base font/column sizes.
        private const double LowVisionScaleMin = 1.0;
        private const double LowVisionScaleMax = 2.5;
        private const double LowVisionScaleStep = 0.25;
        private double _lowVisionScale = 1.5;

        private static readonly int[] TimelineColumnBaseWidths = { 50, 350, 60, 80, 80, 80, 300 };
        private const float TimelineBaseFontSize = 10f;

        private System.Windows.Forms.Timer _selectionDebounceTimer;
        private bool _selectionProcessing = false;
        private TimelineItem _pendingPlaybackItem = null;
        public string currentProjectFolder = string.Empty;  // DODATO POLJE
        public string GetCurrentProjectFolder() => currentProjectFolder;

        /// <summary>
        /// Poslednja rezolucija koju je korisnik odabrao u ResolutionDialog-u.
        /// Stored here so AIVideoCreator can read it before render.
        /// </summary>
        public string _selectedResolution = "1920x1080";

        /// <summary>
        /// Delegate that AIVideoCreator reads via the _targetResolution property.
        /// </summary>
        public Func<string> GetExportResolution => () => _selectedResolution;
        private bool isPlaying = false;
        private double currentPlaybackPosition = 0;
        private double currentVideoDuration = 0;
        private double zoomLevel = 1.0;
        private int previewDurationSeconds = 10;
        private string selectedTranscribeAudioPath = "";
        private double transitionDuration = 1.0;
        private int currentTrackFilter = -1;
        private AnimationKeyframe currentEditingKeyframe = null;
        private bool useGPUAcceleration = true;
        private LibVLC _libVLC;
        private global::LibVLCSharp.Shared.MediaPlayer _mediaPlayer;
        private Media _currentMedia;
        private Equalizer _equalizer;

        private List<AISubtitle> syncedSubtitles = new List<AISubtitle>();
        private string selectedAudioPath = "";
        private WinForms.ListView nativeListView;
        private ExportSettingsData currentExportSettings = new ExportSettingsData();

        private Stack<List<TimelineItem>> undoStack = new Stack<List<TimelineItem>>();
        private Stack<List<TimelineItem>> redoStack = new Stack<List<TimelineItem>>();

        private System.Windows.Threading.DispatcherTimer positionAnnounceTimer;
        private CancellationTokenSource _renderCancellation;
        private LogWindow _logWindow;
        private TimelineItem _copiedClip = null;
        private RenderEngine _renderEngine;
        // v1.4: stored in LanguageManager so every thread can read it safely
        internal string _currentLanguage
        {
            get => LanguageManager.CurrentLanguage;
            set => LanguageManager.CurrentLanguage = string.IsNullOrEmpty(value) ? "en" : value;
        }
        private string _currentTheme = "dark"; // dark | contrast | light

        private List<AnimationScene> _animationScenes = new List<AnimationScene>();

        // v1.4: the clip currently loaded in the preview player. The player plays ONE clip
        // file at a time, so its position is relative to that clip — everything that needs a
        // timeline time (markers, cut, keyframes) converts through this item.
        private TimelineItem _playingItem = null;
        // v1.4: the project file last saved/opened — Ctrl+S saves there without asking again.
        private string _currentProjectPath = null;

        [DllImport("user32.dll")]
        private static extern bool MessageBeep(uint uType);
        private const uint MB_ICONASTERISK = 0x00000040;

        public MainWindow()
        {
            try
            {
                InitializeComponent();
                UiScaling.Register(this);
                _logWindow = new LogWindow();
                _renderEngine = new RenderEngine(useGPUAcceleration);

                sldPreviewDuration.ValueChanged += (s, e) =>
                {
                    previewDurationSeconds = (int)sldPreviewDuration.Value;
                    txtPreviewDurationValue.Text = $"{previewDurationSeconds} s";
                    if (accessibilityMode) LogMessage(string.Format(L("preview_duration_sec"), previewDurationSeconds), true);
                };
                txtPreviewDurationValue.Text = "10 s";

                SetupSliderAnnouncements();

                sldZoom.ValueChanged += AnimValue_Changed;
                sldRotation.ValueChanged += AnimValue_Changed;
                sldX.ValueChanged += AnimValue_Changed;
                sldY.ValueChanged += AnimValue_Changed;
                sldOpacity.ValueChanged += AnimValue_Changed;

                LibVLCSharp.Shared.Core.Initialize();
                _libVLC = new LibVLC();
                _mediaPlayer = new global::LibVLCSharp.Shared.MediaPlayer(_libVLC);

                try { vlcVideoView.MediaPlayer = _mediaPlayer; } catch { }

                _equalizer = new Equalizer();
                _mediaPlayer.SetEqualizer(_equalizer);

                _mediaPlayer.LengthChanged += OnMediaLengthChanged;
                _mediaPlayer.Playing += (s, e) =>
                {
                    isPlaying = true;
                    // v1.4: player events arrive on the player's own thread — queue UI work
                    Dispatcher.BeginInvoke(new Action(() => { if (btnPlay != null) btnPlay.Content = "⏸ " + L("pause"); }));
                    LogMessage("Playback started", false);
                };
                // v1.4: a file opened paused (":start-paused", used after the end) reports Playing
                // first; the Paused event puts the state right again
                _mediaPlayer.Paused += (s, e) =>
                {
                    isPlaying = false;
                    Dispatcher.BeginInvoke(new Action(() => { if (btnPlay != null) btnPlay.Content = "▶ " + L("play"); }));
                };
                _mediaPlayer.Stopped += (s, e) =>
                {
                    isPlaying = false;
                    Dispatcher.BeginInvoke(new Action(() => { if (btnPlay != null) btnPlay.Content = "▶ " + L("play"); }));
                    LogMessage("Playback stopped", false);
                };
                _mediaPlayer.EndReached += (s, e) =>
                {
                    isPlaying = false;
                    Dispatcher.BeginInvoke(new Action(() => { if (btnPlay != null) btnPlay.Content = "▶ " + L("play"); }));
                    LogMessage(L("playback_done"), false);
                };
                _mediaPlayer.TimeChanged += OnTimeChanged;

                SetupKeyboardCommands();
                SetAccessibilityNames();
                InitPositionAnnouncer();
                LoadTransitions();
                SetupDragDrop();
                SetupDragDropFromExplorer();
                Loaded += (s, ev) => SetupAIPromptPaste();
                SetupTransitionContextMenus();

                InitNativeListView();
                InitGraphicTimeline();   // v1.4 (R7)
                _selectionDebounceTimer = new System.Windows.Forms.Timer();
                _selectionDebounceTimer.Interval = 400;
                _selectionDebounceTimer.Tick += (s, e) =>
                {
                    _selectionDebounceTimer.Stop();
                    _selectionProcessing = false;

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_pendingPlaybackItem != null)
                        {
                            // v1.4: a list refresh selects the same clip again — that must not
                            // restart the clip that is already loaded (it jumped back to 0:00
                            // after describing, trimming, changing volume…). Space/Enter still play.
                            if (!(ReferenceEquals(_pendingPlaybackItem, _playingItem) && _currentMedia != null && !_mixPreviewActive))
                                PlaySelectedItem(_pendingPlaybackItem);
                            _pendingPlaybackItem = null;
                        }
                    }));
                };
                // ApplyLanguage MORA biti u Loaded eventu
                // jer u konstruktoru WPF elementi jos nisu sigurno kreirani
                Loaded += (s, ev) =>
                {
                    ApplyLanguage();
                    // v1.4: with a Serbian interface, transcription/lyric sync default to Serbian
                    if (_currentLanguage == "sr" && rbSrb != null) rbSrb.IsChecked = true;
                    LoadSavedTheme();
                    DetectAndApplyInitialMode();
                    LogMessage("System ready...", true);
                    // v1.4: offer to restore work lost in a crash, then keep autosaving
                    OfferAutosaveRecovery();
                    StartAutosave();
                };
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(string.Format(L("startup_error"), ex.Message) + "\n\n" + ex.StackTrace);
                throw;
            }
        }

        private void ApplyLanguage()
        {
            string L(string key) => LanguageManager.GetText(key, _currentLanguage);

            this.Title = string.Format(L("app_title"), GetAppVersion());
            if (txtJawsLog != null) txtJawsLog.Text = L("system_ready");
            if (txtStatus != null) txtStatus.Text = L("status_idle");

            // ── AUTO-SCAN: prevodi sve elemente koji imaju Tag="key" ili Tag="emoji|key" ──
            ApplyLanguageToTree(this);

            // ── TABS (Header is not Content/Text/ToolTip, special case) ──────────
            if (tabTimeline != null) tabTimeline.Header = L("timeline_tab");
            if (tabAI != null) tabAI.Header = L("ai_tab");
            if (tabEffects != null) tabEffects.Header = L("effects_tab");
            if (tabSubtitles != null) tabSubtitles.Header = L("subtitles_tab");
            if (tabTransitions != null) tabTransitions.Header = L("transitions_tab");
            if (tabAnimation != null) tabAnimation.Header = L("animation_tab");

            // ── MENI HEADERS (Tag sistem ne pokriva MenuItem.Header direktno jer
            //    WPF MenuItem.Header can be any object; handled separately) ──
            ApplyLanguageToMenuItems(this);

            // ── JAWS AutomationProperties ─────────────────────────────────────────
            if (txtAIPrompt != null)
                System.Windows.Automation.AutomationProperties.SetName(txtAIPrompt, L("ai_prompt"));
            if (btnGenerate != null)
                System.Windows.Automation.AutomationProperties.SetName(btnGenerate, L("generate_frames"));

            _introText = L("default_intro_text");
            _outroText = L("default_outro_text");

            // ── LISTVIEW KOLONE (WinForms, ne reaguju na Tag sistem) ──────────
            if (nativeListView != null && nativeListView.Columns.Count >= 7)
            {
                nativeListView.Columns[0].Text = L("listview_col_num");
                nativeListView.Columns[1].Text = L("listview_col_name");
                nativeListView.Columns[2].Text = L("listview_col_type");
                nativeListView.Columns[3].Text = L("listview_col_duration");
                nativeListView.Columns[4].Text = L("listview_col_start");
                nativeListView.Columns[5].Text = L("listview_col_end");
                nativeListView.Columns[6].Text = L("listview_col_audio_desc");
            }

            UpdateTimelineDisplay();
        }

        /// <summary>
        /// Rekurzivno prolazi kroz vizuelno stablo i prevodi sve elemente
        /// that have Tag set as a translation key string.
        /// 
        /// Tag format:
        ///   "key"           → samo tekst (npr. "cut")
        ///   "emoji|key"     → emoji + razmak + tekst (npr. "✂|cut")
        ///   "emoji|key|shortcut" → emoji + tekst + shortcut u zagradi (npr. "✂|cut|Ctrl+X")
        ///   "tooltip:key"   → samo ToolTip (ne Content/Text)
        /// </summary>
        private void ApplyLanguageToTree(DependencyObject root)
        {
            string L(string key) => LanguageManager.GetText(key, _currentLanguage);

            int childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < childCount; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is FrameworkElement fe && fe.Tag is string tag && !string.IsNullOrEmpty(tag))
                {
                    ApplyTagToElement(fe, tag, L);
                }
                ApplyLanguageToTree(child);
            }
        }

        private void ApplyTagToElement(FrameworkElement fe, string tag, Func<string, string> L)
        {
            // Format: "tooltip:key" → samo tooltip
            if (tag.StartsWith("tooltip:"))
            {
                string key = tag.Substring(8);
                fe.ToolTip = L(key);
                return;
            }

            // Format: "emoji|key" ili "emoji|key|Ctrl+X"
            string[] parts = tag.Split('|');
            string emoji = parts.Length >= 2 ? parts[0] : "";
            string langKey = parts.Length >= 2 ? parts[1] : parts[0];
            string shortcut = parts.Length >= 3 ? " (" + parts[2] + ")" : "";

            string translated = L(langKey);
            string content = string.IsNullOrEmpty(emoji)
                ? translated + shortcut
                : emoji + " " + translated + shortcut;

            if (fe is System.Windows.Controls.Button btn)
            {
                btn.Content = content;
                // Accessible name: clean text without emoji, so JAWS/NVDA don't announce
                // confusing emoji glyph names (e.g. "page facing up") before the label.
                System.Windows.Automation.AutomationProperties.SetName(btn, translated + shortcut);
                // ToolTip = only translated text without shortcut, so JAWS reads clearly
                if (btn.ToolTip is string || btn.ToolTip == null)
                    btn.ToolTip = translated + shortcut;
            }
            else if (fe is System.Windows.Controls.TextBlock tb)
                tb.Text = content;
            else if (fe is System.Windows.Controls.Label lbl)
            {
                lbl.Content = content;
                System.Windows.Automation.AutomationProperties.SetName(lbl, translated + shortcut);
            }
        }

        /// <summary>
        /// Prolazi kroz MenuItem hijerarhiju i prevodi Header-e.
        /// MenuItem.Header je object, pa koristimo Tag konvenciju:
        ///   Tag="key"          → Header = prevod
        ///   Tag="emoji|key"    → Header = "emoji translation"  (no _ prefix — JAWS reads it!)
        /// </summary>
        private void ApplyLanguageToMenuItems(DependencyObject root)
        {
            string L(string key) => LanguageManager.GetText(key, _currentLanguage);

            // We search for MenuItems through the logical (not visual) tree
            foreach (var mi in FindLogicalChildren<MenuItem>(root))
            {
                if (mi.Tag is string tag && !string.IsNullOrEmpty(tag))
                {
                    string[] parts = tag.Split('|');
                    string emoji = parts.Length >= 2 ? parts[0] : "";
                    string langKey = parts.Length >= 2 ? parts[1] : parts[0];
                    string translated = L(langKey);
                    mi.Header = string.IsNullOrEmpty(emoji) ? translated : emoji + " " + translated;
                    // Accessible name: clean text without emoji, so JAWS/NVDA read the
                    // menu label directly instead of an emoji glyph description first.
                    System.Windows.Automation.AutomationProperties.SetName(mi, translated);
                }
            }
        }

        private static IEnumerable<T> FindLogicalChildren<T>(DependencyObject obj) where T : DependencyObject
        {
            if (obj == null) yield break;
            foreach (object child in LogicalTreeHelper.GetChildren(obj))
            {
                if (child is DependencyObject depChild)
                {
                    if (depChild is T t) yield return t;
                    foreach (T childOfChild in FindLogicalChildren<T>(depChild))
                        yield return childOfChild;
                }
            }
        }

        private void SetLanguageSerbian_Click(object sender, RoutedEventArgs e) { _currentLanguage = "sr"; ApplyLanguage(); LogMessage("Language: Serbian", true); }
        private void SetLanguageEnglish_Click(object sender, RoutedEventArgs e) { _currentLanguage = "en"; ApplyLanguage(); LogMessage("Language: English", true); }
        private void SetLanguageGerman_Click(object sender, RoutedEventArgs e) { _currentLanguage = "de"; ApplyLanguage(); LogMessage("Language: German", true); }

        // ── TEME ────────────────────────────────────────────────────
        private void SetThemeDark_Click(object sender, System.Windows.RoutedEventArgs e)
        { _currentTheme = "dark"; ApplyTheme(); LogMessage("Theme: Dark", true); }
        private void SetThemeContrast_Click(object sender, System.Windows.RoutedEventArgs e)
        { _currentTheme = "contrast"; ApplyTheme(); LogMessage("Theme: High contrast", true); }
        private void SetThemeLight_Click(object sender, System.Windows.RoutedEventArgs e)
        { _currentTheme = "light"; ApplyTheme(); LogMessage("Theme: Light", true); }

        private void ApplyTheme()
        {
            string themeFile = _currentTheme switch
            {
                "contrast" => "Themes/Theme.Contrast.xaml",
                "light"    => "Themes/Theme.Light.xaml",
                _          => "Themes/Theme.Dark.xaml"
            };

            try
            {
                var newDict = new ResourceDictionary
                {
                    Source = new Uri(themeFile, UriKind.Relative)
                };

                // Replace the theme dictionary at the Application level. Every open
                // window (MainWindow and any modeless dialogs like LogWindow or
                // VideoReviewDialog) uses DynamicResource for its colors, so this one
                // swap updates the whole app immediately — no need to touch each
                // window individually.
                var merged = System.Windows.Application.Current.Resources.MergedDictionaries;
                merged.Clear();
                merged.Add(newDict);

                // Snimi temu
                try
                {
                    File.WriteAllText(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "theme.cfg"),
                    _currentTheme);
                }
                catch (Exception swallowed) { AppLog.Error("Saving the theme", swallowed); }   // v1.4: was silent
            }
            catch (Exception ex) { LogMessage("Error applying theme: " + ex.Message, false); }
        }

        private void LoadSavedTheme()
        {
            try
            {
                string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "theme.cfg");
                if (File.Exists(p))
                {
                    string t = File.ReadAllText(p).Trim();
                    if (t == "contrast" || t == "light" || t == "dark")
                    { _currentTheme = t; ApplyTheme(); }
                }
            }
            catch (Exception swallowed) { AppLog.Error("Loading the saved theme", swallowed); }   // v1.4: was silent
        }


        // v1.4: keys and tokens go through the shared KeyStore (same files as before)
        private string GetApiKey(string keyName) => KeyStore.Load(keyName);

        private void SaveApiKey(string keyName, string apiKey)
        {
            if (!KeyStore.Save(keyName, apiKey))
                LogMessage($"The key '{keyName}' could not be saved. Details are in the log file.", true);
        }

        /// <summary>
        /// v1.4 — the Cloudflare ACCOUNT ID (needed in every Workers AI URL). It was hard-coded to
        /// one developer account, so a token from any other account was refused (403) and image
        /// generation, scenario and description never worked for other users. Asked once, saved
        /// encrypted like the token.
        /// </summary>
        private string GetCloudflareAccountId(bool askIfMissing = true)
        {
            string id = GetApiKey("cloudflare_account");
            if (!string.IsNullOrWhiteSpace(id) || !askIfMissing) return id?.Trim();
            var dlg = new TextOverlayDialog("Cloudflare Account ID (Cloudflare dashboard → Workers AI → Account ID)") { Owner = this };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Text))
            {
                id = dlg.Text.Trim();
                SaveApiKey("cloudflare_account", id);
                return id;
            }
            return null;
        }

        private string GetHuggingFaceApiKey() => GetApiKey("huggingface");
        private void SaveHuggingFaceApiKey(string key) => SaveApiKey("huggingface", key);
        private string GetCloudflareApiKey() => GetApiKey("cloudflare");
        private void SaveCloudflareApiKey(string key) => SaveApiKey("cloudflare", key);
        private string GetOpenAiApiKey() => GetApiKey("openai");
        private void SaveOpenAiApiKey(string key) => SaveApiKey("openai", key);
        private string GetPixabayApiKey() => GetApiKey("pixabay");
        private void SavePixabayApiKey(string key) => SaveApiKey("pixabay", key);
        private void SetupSliderAnnouncements()
        {
            var sliders = new[] { sldBrightness, sldContrast, sldBlur, sldBass, sldTreble, sldReverb, sldZoom, sldRotation, sldX, sldY, sldOpacity, sldPreviewDuration, sldTransitionDuration };
            foreach (var slider in sliders)
            {
                if (slider == null) continue;
                slider.ValueChanged += (s, e) =>
                {
                    if (accessibilityMode)
                    {
                        string name = AutomationProperties.GetName(slider);
                        if (string.IsNullOrEmpty(name)) name = "Slider";
                        LogMessage($"{name}: {e.NewValue:F1}", true);
                    }
                };
            }
        }

        public void LogMessage(string message, bool announce = false)
        {
            void Write()
            {
                if (txtJawsLog != null)
                    txtJawsLog.Text = message;
                // Log prozor uvijek prima poruke (bez obzira na accessibility mod)
                _logWindow?.AddMessage(message, announce);
                // Screen reader announcement: samo jednom, ne duplo
                if (announce && accessibilityMode)
                    AnnounceToJaws(message);
            }
            // v1.4: from another thread (player events, background work) the message is queued
            // instead of waiting for the UI thread — waiting could deadlock with the player.
            if (Dispatcher.CheckAccess()) Write();
            else Dispatcher.BeginInvoke(new Action(Write));
        }
        /// <summary>
        /// Dodaje titl na timeline za JAWS i prikaz na ekranu
        /// </summary>
        public void AddSubtitle(string text, double startTime, double endTime, List<KaraokeWord> karaokeWords = null)
        {
            try
            {
                var subtitle = new SubtitleItem
                {
                    Text = text,
                    Start = startTime,
                    End = endTime,
                    Words = karaokeWords   // v1.4: karaoke timing from the AI video creator (null = plain subtitle)
                };

                subtitles.Add(subtitle);

                Dispatcher.Invoke(() =>
                {
                    if (lstSubtitles != null)
                    {
                        lstSubtitles.Items.Add($"{FormatTime(startTime)} -> {FormatTime(endTime)}: {text}");
                    }
                });

                // v1.4: logged, not spoken — the AI creator adds dozens of lines at once
                LogMessage($"📝 Titl: {text} ({FormatTime(startTime)} - {FormatTime(endTime)})", false);
            }
            catch (Exception ex)
            {
                LogMessage(string.Format(L("subtitle_error"), ex.Message), true);
            }
        }
        private void AnnounceToJaws(string message)
        {
            // Status bar je uvijek vidljiv i JAWS/NVDA ga cita automatski (Assertive).
            // Korisnik NIKAD ne mora otvarati Log prozor da bi dobio info.
            Dispatcher.Invoke(() =>
            {
                // v1.4 (R6): NVDA and Narrator read UI Automation notifications reliably, while
                // WPF live regions are read inconsistently by them (sometimes twice because of the
                // ghost region, sometimes not at all). With NVDA or Narrator — and no JAWS — the
                // message goes out as one notification; JAWS keeps the live regions it was tested with.
                var reader = ScreenReaderDetector.CurrentReader();
                if ((reader == ScreenReaderDetector.Reader.Nvda || reader == ScreenReaderDetector.Reader.Narrator) && txtJawsLog != null)
                {
                    txtJawsLog.Text = message;
                    try
                    {
                        var np = UIElementAutomationPeer.CreatePeerForElement(txtJawsLog) ?? UIElementAutomationPeer.FromElement(txtJawsLog);
                        np?.RaiseNotificationEvent(AutomationNotificationKind.Other,
                            AutomationNotificationProcessing.ImportantMostRecent, message, "UltraVideoEditor.Announcement");
                        return;
                    }
                    catch (Exception ex) { AppLog.Error("UIA notification", ex); }   // fall back to live regions
                }

                // ── Primarni live region (uvijek vidljiv status bar) ──────────────
                if (txtJawsLog != null)
                {
                    txtJawsLog.Text = message;
                    // CreatePeerForElement umjesto FromElement: FromElement vraca null
                    // dok neki AT klijent (NVDA/JAWS) sam ne "dodirne" element bar jednom,
                    // pa najava dotad tiho ne radi. CreatePeerForElement kreira peer odmah.
                    var peer = UIElementAutomationPeer.CreatePeerForElement(txtJawsLog) ?? UIElementAutomationPeer.FromElement(txtJawsLog);
                    peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                }
                // ── Ghost live region — JAWS trick: NVDA/JAWS pouzdanije cita
                //    kad se isti tekst pojavi u dva odvojena live regiona naizmjenicno ──
                if (txtJawsGhost != null && accessibilityMode)
                {
                    txtJawsGhost.Text = message;
                    var peer2 = UIElementAutomationPeer.CreatePeerForElement(txtJawsGhost) ?? UIElementAutomationPeer.FromElement(txtJawsGhost);
                    peer2?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                }
            });
        }

        private void SetAccessibilityNames()
        {
            if (nativeListView != null)
            {
                nativeListView.AccessibleName = "List of clips on the timeline. Use arrow keys to navigate. Enter or Space to play. Page Up/Down to scroll quickly. Delete to remove.";
            }
            AutomationProperties.SetName(btnPlay, "Play or pause, Ctrl Space");
            AutomationProperties.SetName(btnRenderTool, "Render project, Ctrl R");
            AutomationProperties.SetName(btnCut, "Cut clip, Ctrl X");
            AutomationProperties.SetName(btnCutAllMarkers, "Cut at all markers, Ctrl Shift X");
            AutomationProperties.SetName(btnMoveLeft, "Move left, Ctrl Left");
            AutomationProperties.SetName(btnMoveRight, "Move right, Ctrl Right");
            AutomationProperties.SetName(btnDuration, "Set duration, Ctrl D");
            AutomationProperties.SetName(btnVolume, L("acc_set_volume"));
            AutomationProperties.SetName(btnZoomIn, L("acc_zoom_in"));
            AutomationProperties.SetName(btnZoomOut, "Zoom out, Ctrl minus");
            AutomationProperties.SetName(btnAddMarker, "Add marker, Ctrl M");
            AutomationProperties.SetName(btnNextMarker, L("acc_next_marker"));
            AutomationProperties.SetName(btnPrevMarker, "Previous marker, Ctrl Shift comma");
            AutomationProperties.SetName(btnSeekBack, "Seek back 5 seconds");
            AutomationProperties.SetName(btnSeekForward, "Seek forward 5 seconds");
            AutomationProperties.SetName(tabTimeline, "Timeline tab, clip list");
            AutomationProperties.SetName(tabAI, "AI functions tab");
            AutomationProperties.SetName(tabEffects, "Effects tab");
            AutomationProperties.SetName(tabSubtitles, "Subtitles tab");
            AutomationProperties.SetName(tabTransitions, "Transitions tab");
            AutomationProperties.SetName(tabAnimation, "Animation tab");
            AutomationProperties.SetName(sldBrightness, "Brightness control");
            AutomationProperties.SetName(sldContrast, "Contrast control");
            AutomationProperties.SetName(sldBlur, "Blur control");
            AutomationProperties.SetName(sldBass, L("acc_bass"));
            AutomationProperties.SetName(sldTreble, L("acc_treble"));
            AutomationProperties.SetName(sldReverb, "Reverb effect");
            AutomationProperties.SetName(txtVoiceText, "Text for AI voice");
            AutomationProperties.SetName(txtAIPrompt, "Prompt for AI images");
            AutomationProperties.SetName(txtLyrics, "Song lyrics for synchronization");
            AutomationProperties.SetName(btnGenerateVoice, L("acc_generate_voice"));
            AutomationProperties.SetName(btnGenerate, L("acc_generate_frames"));
            AutomationProperties.SetName(btnSyncLyrics, "Synchronize lyrics");
            AutomationProperties.SetName(txtSubtitleText, "Subtitle text");
            AutomationProperties.SetName(txtSubStart, L("acc_subtitle_start"));
            AutomationProperties.SetName(txtSubEnd, "End in seconds");
            AutomationProperties.SetName(btnAddSubtitle, "Add subtitle");
            AutomationProperties.SetName(btnClearSubtitles, L("acc_clear_subtitles"));
            AutomationProperties.SetName(sldPreviewDuration, L("acc_preview_duration"));
            AutomationProperties.SetName(cmbTranscribeAudio, "Select audio file for transcription");
            AutomationProperties.SetName(btnTranscribe, "Start AI transcription");
            AutomationProperties.SetName(txtTranscriptionResult, "Transcription result");
            AutomationProperties.SetName(cmbTrackSelector, "Track display selector");
            AutomationProperties.SetName(lstKeyframes, "Keyframe list");
            AutomationProperties.SetName(btnAddKeyframe, "Add keyframe");
            AutomationProperties.SetName(btnRemoveKeyframe, "Remove keyframe");
            AutomationProperties.SetName(btnApplyKeyframe, "Apply keyframe");
            AutomationProperties.SetName(btnPreviewAnimation, "Preview animation");
            AutomationProperties.SetName(sldZoom, "Zoom control");
            AutomationProperties.SetName(sldRotation, "Rotation control");
            AutomationProperties.SetName(sldX, "Horizontal position control");
            AutomationProperties.SetName(sldY, "Vertical position control");
            AutomationProperties.SetName(sldOpacity, "Opacity control");
            AutomationProperties.SetName(sldTransitionDuration, "Transition duration");
            AutomationProperties.SetName(lstTransitions, "Transition list");
            AutomationProperties.SetName(btnRemoveTransition, "Remove transition");
            AutomationProperties.SetName(chkGPUAcceleration, "GPU acceleration");
            // txtJawsLog must be a live region
            if (txtJawsLog != null)
            {
                AutomationProperties.SetName(txtJawsLog, "Status message");
                AutomationProperties.SetLiveSetting(txtJawsLog, AutomationLiveSetting.Assertive);
            }
        }

        private void PlayBeep()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try { MessageBeep(MB_ICONASTERISK); } catch { }
            }
        }

        private string FormatTime(double seconds) => TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss");
        private string FormatTimeDetailed(double seconds) => TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss\.f");
        // v1.4: the project's length is where the last clip ends (tracks run in parallel);
        // the old sum of ALL clip durations counted the song and the pictures one after another.
        private double GetTotalDuration() => timelineItems.Count == 0 ? 0 : timelineItems.Max(t => t.End);

        /// <summary>Timeline time of the preview player's current position.</summary>
        private double TimelinePosition => (_playingItem?.Start ?? 0) + currentPlaybackPosition;

        /// <summary>
        /// Position inside <paramref name="item"/> that corresponds to the player position:
        /// the player position itself if this clip is the one playing, otherwise the timeline
        /// position translated into the clip. Before v1.4 every command subtracted item.Start
        /// from the CLIP-relative player time, so Cut, markers and keyframes only worked on
        /// the very first clip of the timeline ("Cut does nothing").
        /// </summary>
        private double PositionInClip(TimelineItem item)
        {
            if (item == null) return 0;
            if (ReferenceEquals(item, _playingItem)) return currentPlaybackPosition;
            return TimelinePosition - item.Start;
        }

        /// <summary>
        /// v1.4 — gives every clip its Start/End. Tracks run in parallel (Video 1, Video 2,
        /// Audio 1, Audio 2 each start at 0). Within a track clips follow each other in list
        /// order; a clip with a fixed position ("Set position on the timeline", AI music video,
        /// auto-arrange) stays exactly there and the others flow around it.
        /// Before v1.4 every list refresh laid out ALL clips of ALL tracks one after another:
        /// the song ended up after the last picture, fixed positions were overwritten, and
        /// the AI video's lyric timing was destroyed as soon as the list was redrawn.
        /// </summary>
        private void RecalculatePositions()
        {
            RootRelativePaths();
            TimelineLayout.Recalculate(timelineItems);
        }

        /// <summary>
        /// v1.4 — a clip path without a folder ("AI_1234.mp4") only works while the current
        /// directory happens to be right; FFmpeg runs elsewhere and cannot find it. Such paths
        /// are made absolute as long as the file can still be found.
        /// </summary>
        private void RootRelativePaths()
        {
            foreach (var item in timelineItems)
            {
                if (string.IsNullOrEmpty(item.Path) || Path.IsPathRooted(item.Path)) continue;
                try
                {
                    string[] candidates =
                    {
                        Path.GetFullPath(item.Path),
                        string.IsNullOrEmpty(currentProjectFolder) ? null : Path.Combine(currentProjectFolder, item.Path),
                        Path.Combine(AppPaths.BaseDir, item.Path)
                    };
                    string found = candidates.FirstOrDefault(c => c != null && File.Exists(c));
                    if (found != null) item.Path = found;
                }
                catch (Exception ex) { AppLog.Error($"Resolving the clip path '{item.Path}'", ex); }
            }
        }
        private void UpdatePositionDisplay()
        {
            if (txtCurrentPosition != null)
                txtCurrentPosition.Text = $"{FormatTime(currentPlaybackPosition)} / {FormatTime(currentVideoDuration)}";
        }
        private void OnMediaLengthChanged(object sender, LibVLCSharp.Shared.MediaPlayerLengthChangedEventArgs e)
        {
            // v1.4: a cut part plays from the middle of its file — its own length is what counts
            var playing = _playingItem;
            currentVideoDuration = playing != null && !_mixPreviewActive ? playing.Duration : e.Length / 1000.0;
            Dispatcher.BeginInvoke(new Action(UpdatePositionDisplay));   // v1.4: player thread → queue
        }
        private void OnTimeChanged(object sender, LibVLCSharp.Shared.MediaPlayerTimeChangedEventArgs e)
        {
            // v1.4: position relative to the CLIP (the file may start at the clip's in-point)
            double inPoint = _playingItem?.SourceOffset ?? 0;
            currentPlaybackPosition = Math.Max(0, e.Time / 1000.0 - inPoint);
            Dispatcher.BeginInvoke(new Action(UpdatePositionDisplay));   // v1.4: player thread → queue
        }

        // v1.4: the position announcement while playing is OFF by default (it talked every
        // 10 seconds without being asked); when turned on it speaks every 30 seconds.
        // The choice is remembered in position_announce.cfg next to theme.cfg.
        private bool _positionAnnounceOn = false;
        private static string PositionCfgPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "position_announce.cfg");

        private void InitPositionAnnouncer()
        {
            try { _positionAnnounceOn = File.Exists(PositionCfgPath) && File.ReadAllText(PositionCfgPath).Trim() == "on"; }
            catch (Exception ex) { AppLog.Error("Reading the position announcement setting", ex); }
            if (mnuPositionAnnounce != null) mnuPositionAnnounce.IsChecked = _positionAnnounceOn;

            positionAnnounceTimer = new System.Windows.Threading.DispatcherTimer();
            positionAnnounceTimer.Interval = TimeSpan.FromSeconds(30);
            positionAnnounceTimer.Tick += (s, e) =>
            {
                if (isPlaying && accessibilityMode && _positionAnnounceOn)
                {
                    try
                    {
                        LogMessage(string.Format(L("position_of"), FormatTime(currentPlaybackPosition), FormatTime(currentVideoDuration)), true);
                    }
                    catch { }
                }
            };
            // Start timer immediately if accessibility mode is enabled
            if (accessibilityMode && _positionAnnounceOn)
                positionAnnounceTimer.Start();
            StartScreenReaderWatch();
        }

        private void TogglePositionAnnounce_Click(object sender, RoutedEventArgs e)
        {
            _positionAnnounceOn = !_positionAnnounceOn;
            if (mnuPositionAnnounce != null) mnuPositionAnnounce.IsChecked = _positionAnnounceOn;
            try { File.WriteAllText(PositionCfgPath, _positionAnnounceOn ? "on" : "off"); }
            catch (Exception ex) { AppLog.Error("Saving the position announcement setting", ex); }
            if (_positionAnnounceOn && accessibilityMode) positionAnnounceTimer?.Start(); else positionAnnounceTimer?.Stop();
            LogMessage(L(_positionAnnounceOn ? "pos_announce_on" : "pos_announce_off"), true);
        }

        // ── v1.4: a screen reader started AFTER the program ─────────────────────
        // The mode was chosen once at startup, so JAWS/NVDA started later found a silent
        // program until Ctrl+Shift+A. Every 10 seconds the program now looks for a reader and
        // switches to Accessibility mode by itself — unless you chose Sighted or Low vision
        // yourself while a reader was running.
        private bool _modeChosenByUser = false;
        private System.Windows.Threading.DispatcherTimer _readerWatch;

        private void StartScreenReaderWatch()
        {
            _readerWatch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _readerWatch.Tick += (s, e) =>
            {
                if (_uiMode != UIMode.Sighted || _modeChosenByUser) return;
                var reader = ScreenReaderDetector.CurrentReader();
                if (reader == ScreenReaderDetector.Reader.Jaws || reader == ScreenReaderDetector.Reader.Nvda ||
                    reader == ScreenReaderDetector.Reader.Narrator)
                {
                    SetUIMode(UIMode.Accessibility, announce: false, byUser: false);
                    LogMessage(L("reader_detected_switch"), true);
                }
            };
            _readerWatch.Start();
        }
        public void UpdateTimelineDisplay()
        {
            UpdateNativeListView();
        }

        private void UpdateMarkerDisplay()
        {
            graphicTimeline?.InvalidateVisual();
            if (txtMarkerStatus != null)
                txtMarkerStatus.Text = markers.Count > 0 ? $"📍 {markers.Count}" : "";
        }

        private void UpdateZoomDisplay()
        {
            if (txtZoomStatus != null) txtZoomStatus.Text = $"Zoom: {zoomLevel:F1}x";
            UpdateTimelineDisplay();
        }

        // ── v1.4: unsaved changes ────────────────────────────────────────────
        // The saved state is remembered as text; "unsaved" simply means the project now
        // serializes differently. No need to mark every single edit as a change.
        private string _savedSnapshot;

        private string ProjectSnapshot() => JsonConvert.SerializeObject(new ProjectData
        {
            TimelineItems = timelineItems,
            Subtitles = subtitles,
            Markers = markers,
            Transitions = transitions
        });

        private void MarkProjectSaved()
        {
            _savedSnapshot = ProjectSnapshot();
            DeleteAutosave();
        }

        private bool HasUnsavedChanges()
        {
            if (_savedSnapshot == null) return timelineItems.Count > 0 || subtitles.Count > 0;
            return ProjectSnapshot() != _savedSnapshot;
        }

        /// <summary>
        /// Asks "save changes?" when there is something unsaved. Returns false when the user
        /// cancelled (or cancelled the Save as dialog) — the caller must then stop.
        /// </summary>
        private bool ConfirmSaveChanges(string questionKey)
        {
            if (!HasUnsavedChanges()) return true;
            var result = WpfMessageBox.Show(L(questionKey), "Ultra Video Editor",
                                            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (result == MessageBoxResult.Cancel) return false;
            if (result == MessageBoxResult.No) return true;
            return !string.IsNullOrEmpty(_currentProjectPath)
                ? WriteProjectFile(_currentProjectPath)
                : SaveAsInteractive();
        }

        // ── v1.4: autosave and crash recovery ────────────────────────────────
        // Every 2 minutes, if something changed, the project is written to
        // %LOCALAPPDATA%\UltraVideoEditor\Autosave. A normal save or a normal close removes it,
        // so a file that is still there at start-up means the program did not close normally.
        private System.Windows.Threading.DispatcherTimer _autosaveTimer;
        private string _lastAutosavedSnapshot;
        private static string AutosaveFile => Path.Combine(AppPaths.AutosaveDir, "autosave.iskra");
        private static string AutosaveOriginFile => Path.Combine(AppPaths.AutosaveDir, "autosave_origin.txt");

        private void StartAutosave()
        {
            _autosaveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
            _autosaveTimer.Tick += (s, e) => AutosaveNow();
            _autosaveTimer.Start();
        }

        private void AutosaveNow()
        {
            try
            {
                if (!HasUnsavedChanges()) return;
                string snapshot = ProjectSnapshot();
                if (snapshot == _lastAutosavedSnapshot) return;
                string tmp = AutosaveFile + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(new ProjectData
                {
                    TimelineItems = timelineItems,
                    Subtitles = subtitles,
                    Markers = markers,
                    Transitions = transitions,
                    CurrentTrackFilter = currentTrackFilter,
                    ZoomLevel = zoomLevel,
                    ProjectVersion = "5.1"
                }, Formatting.Indented));
                File.Copy(tmp, AutosaveFile, true);
                File.Delete(tmp);
                File.WriteAllText(AutosaveOriginFile, _currentProjectPath ?? "");
                _lastAutosavedSnapshot = snapshot;
                LogMessage(L("autosave_done"), false);
            }
            catch (Exception ex)
            {
                AppLog.Error("Autosave", ex);
            }
        }

        private static void DeleteAutosave()
        {
            try
            {
                if (File.Exists(AutosaveFile)) File.Delete(AutosaveFile);
                if (File.Exists(AutosaveOriginFile)) File.Delete(AutosaveOriginFile);
            }
            catch (Exception ex) { AppLog.Error("Removing the autosave", ex); }
        }

        private async void OfferAutosaveRecovery()
        {
            try
            {
                if (!File.Exists(AutosaveFile)) return;
                string origin = File.Exists(AutosaveOriginFile) ? File.ReadAllText(AutosaveOriginFile).Trim() : "";
                string when = File.GetLastWriteTime(AutosaveFile).ToString("dd.MM.yyyy HH:mm");
                string name = string.IsNullOrEmpty(origin) ? L("autosave_unnamed") : Path.GetFileName(origin);
                var answer = WpfMessageBox.Show(string.Format(L("autosave_restore_question"), name, when),
                    "Ultra Video Editor", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Yes)
                {
                    await LoadProjectFromFileAsync(AutosaveFile, string.IsNullOrEmpty(origin) ? null : origin, recovered: true);
                    LogMessage(L("autosave_restored"), true);
                }
                else
                {
                    DeleteAutosave();
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Restoring the autosave", ex);
            }
        }

        // ── v1.4: projects that can be moved ─────────────────────────────────
        private void StoreRelativePaths(string projectDir)
        {
            if (string.IsNullOrEmpty(projectDir)) return;
            foreach (var item in timelineItems)
            {
                if (string.IsNullOrEmpty(item.Path)) continue;
                try
                {
                    // Different drives give an absolute path back — then there is nothing relative to store.
                    string rel = Path.GetRelativePath(projectDir, item.Path);
                    item.RelativePath = Path.IsPathRooted(rel) ? "" : rel;
                }
                catch { item.RelativePath = ""; }   // malformed path — keep the absolute one only
            }
        }

        /// <summary>
        /// Media that is not where the project says: first tried next to the project (relative
        /// path), then — after asking — in a folder the user picks, by file name.
        /// </summary>
        private void FindMovedMedia(string projectDir)
        {
            int fixedByRelative = 0;
            foreach (var item in timelineItems)
            {
                if (string.IsNullOrEmpty(item.Path) || File.Exists(item.Path)) continue;
                if (string.IsNullOrEmpty(item.RelativePath) || string.IsNullOrEmpty(projectDir)) continue;
                string candidate = Path.GetFullPath(Path.Combine(projectDir, item.RelativePath));
                if (File.Exists(candidate)) { item.Path = candidate; fixedByRelative++; }
            }

            var missing = timelineItems.Where(i => !string.IsNullOrEmpty(i.Path) && !File.Exists(i.Path)).ToList();
            if (fixedByRelative > 0)
                LogMessage(string.Format(L("media_relinked"), fixedByRelative), true);
            if (missing.Count == 0) { if (fixedByRelative > 0) UpdateTimelineDisplay(); return; }

            var answer = WpfMessageBox.Show(string.Format(L("media_missing_question"), missing.Count),
                "Ultra Video Editor", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes)
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = L("media_pick_folder") };
                if (dlg.ShowDialog() == true)
                {
                    var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                        foreach (string f in Directory.EnumerateFiles(dlg.FolderName, "*", options))
                            byName.TryAdd(Path.GetFileName(f), f);
                    }
                    catch (Exception ex) { AppLog.Error("Searching for missing media", ex); }

                    int found = 0;
                    foreach (var item in missing)
                        if (byName.TryGetValue(Path.GetFileName(item.Path), out string f)) { item.Path = f; found++; }
                    LogMessage(string.Format(L("media_found"), found, missing.Count), true);
                }
            }
            UpdateTimelineDisplay();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!e.Cancel && !ConfirmSaveChanges("save_before_close"))
                e.Cancel = true;
            if (!e.Cancel)
            {
                // The user decided (saved or chose not to) — this is not a crash.
                _autosaveTimer?.Stop();
                DeleteAutosave();
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            // v1.4: the log window only hides itself when closed, so it could keep the
            // program running in the background after the main window was closed.
            WpfApplication.Current?.Shutdown();
        }

        private void NewProject_Click(object sender, RoutedEventArgs e)
        {
            if (!ConfirmSaveChanges("save_before_new")) return;
            timelineItems.Clear();
            subtitles.Clear();
            markers.Clear();
            transitions.Clear();
            undoStack.Clear();
            redoStack.Clear();
            _currentProjectPath = null;
            _playingItem = null;
            _savedSnapshot = null;
            UpdateTimelineDisplay();
            lstSubtitles.Items.Clear();
            LogMessage("New project created", true);
        }

        /// <summary>v1.4 — writes the project to <paramref name="path"/> and remembers it for Ctrl+S.</summary>
        private bool WriteProjectFile(string path)
        {
            try
            {
                currentProjectFolder = Path.GetDirectoryName(path) ?? string.Empty;
                CollectTempMedia(path);
                StoreRelativePaths(Path.GetDirectoryName(path) ?? "");
                var projectData = new ProjectData
                {
                    TimelineItems = timelineItems,
                    Subtitles = subtitles,
                    Markers = markers,
                    Transitions = transitions,
                    CurrentTrackFilter = currentTrackFilter,
                    ZoomLevel = zoomLevel,
                    ProjectVersion = "5.1"
                };
                string json = JsonConvert.SerializeObject(projectData, Formatting.Indented);
                // v1.4: write to a temporary file first and then swap it in, keeping the
                // previous version as .bak — a crash or power cut while saving used to leave
                // a half-written, unreadable project.
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", true);
                else File.Move(tmp, path);
                _currentProjectPath = path;
                MarkProjectSaved();
                LogMessage(string.Format(L("project_saved_as"), Path.GetFileName(path)), true);
                PlayBeep();
                return true;
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(string.Format(L("project_save_error"), ex.Message), L("error_title"), MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>
        /// v1.4 — clips the AI creator, Video Engine, TTS etc. downloaded into the Windows TEMP
        /// folder are copied next to the project ("&lt;name&gt;_media") when it is saved.
        /// Windows cleans TEMP on its own, so a project opened weeks later had lost its clips.
        /// </summary>
        private void CollectTempMedia(string projectPath)
        {
            RootRelativePaths();
            string temp = Path.GetFullPath(Path.GetTempPath());
            // the program's own folder counts too: clips the AI saved there disappear with a clean build
            string programDir = Path.GetFullPath(AppPaths.BaseDir);
            var inTemp = timelineItems
                .Where(i => !string.IsNullOrEmpty(i.Path) && File.Exists(i.Path) &&
                            (Path.GetFullPath(i.Path).StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
                             Path.GetFullPath(i.Path).StartsWith(programDir, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (inTemp.Count == 0) return;

            string mediaDir = Path.Combine(Path.GetDirectoryName(projectPath) ?? "",
                                           Path.GetFileNameWithoutExtension(projectPath) + "_media");
            Directory.CreateDirectory(mediaDir);
            var copied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in inTemp)
            {
                string src = Path.GetFullPath(item.Path);
                if (!copied.TryGetValue(src, out string dest))
                {
                    dest = Path.Combine(mediaDir, Path.GetFileName(src));
                    for (int n = 2; File.Exists(dest); n++)
                        dest = Path.Combine(mediaDir, $"{Path.GetFileNameWithoutExtension(src)}_{n}{Path.GetExtension(src)}");
                    File.Copy(src, dest);
                    copied[src] = dest;
                }
                item.Path = dest;
            }
            LogMessage($"{copied.Count} temporary media files were copied into the project folder ({Path.GetFileName(mediaDir)}).", true);
        }

        private void SaveProject_Click(object sender, RoutedEventArgs e)
        {
            // v1.4: Save writes to the current project file; only the first save asks where.
            if (!string.IsNullOrEmpty(_currentProjectPath))
            {
                WriteProjectFile(_currentProjectPath);
                return;
            }
            SaveAsProject_Click(sender, e);
        }

        private void SaveAsProject_Click(object sender, RoutedEventArgs e) => SaveAsInteractive();

        /// <summary>Shows Save as; true when the project was written.</summary>
        private bool SaveAsInteractive()
        {
            var d = new WpfSaveFileDialog
            {
                Filter = "Iskra Project|*.iskra",
                DefaultExt = "iskra",
                FileName = string.IsNullOrEmpty(_currentProjectPath) ? "project.iskra" : Path.GetFileName(_currentProjectPath)
            };
            return d.ShowDialog() == true && WriteProjectFile(d.FileName);
        }

        private async void LoadProject_Click(object sender, RoutedEventArgs e)
        {
            if (!ConfirmSaveChanges("save_before_open")) return;
            var d = new WpfOpenFileDialog { Filter = "Iskra Project|*.iskra" };
            if (d.ShowDialog() == true)
                await LoadProjectFromFileAsync(d.FileName, d.FileName);
        }

        /// <summary>
        /// v1.4 — loads a project. <paramref name="projectPath"/> is where it belongs (null for a
        /// recovered autosave of a never-saved project); <paramref name="sourceFile"/> is what is
        /// actually read (the project itself, or the autosave copy).
        /// </summary>
        private async Task LoadProjectFromFileAsync(string sourceFile, string projectPath, bool recovered = false)
        {
            var d = new { FileName = projectPath ?? sourceFile };
            {
                try
                {
                    string json = await File.ReadAllTextAsync(sourceFile);
                    var projectData = JsonConvert.DeserializeObject<ProjectData>(json);
                    if (projectData == null) { LogMessage(L("project_corrupt"), true); return; }
                    timelineItems = projectData.TimelineItems ?? new List<TimelineItem>();
                    subtitles = projectData.Subtitles ?? new List<SubtitleItem>();
                    markers = projectData.Markers ?? new List<double>();
                    transitions = projectData.Transitions ?? new List<TransitionEffect>();
                    currentTrackFilter = projectData.CurrentTrackFilter;
                    zoomLevel = projectData.ZoomLevel;
                    currentProjectFolder = projectPath != null ? Path.GetDirectoryName(projectPath) ?? string.Empty : string.Empty;
                    _currentProjectPath = projectPath;
                    // v1.4: positions come from RecalculatePositions (per track, fixed positions
                    // kept) — the old loop laid every clip of every track end to end.
                    UpdateZoomDisplay();
                    UpdateTimelineDisplay();
                    UpdateMarkerDisplay();
                    lstSubtitles.Items.Clear();
                    foreach (var sub in subtitles) lstSubtitles.Items.Add($"{FormatTime(sub.Start)} -> {FormatTime(sub.End)}: {sub.Text}");
                    undoStack.Clear();
                    redoStack.Clear();
                    if (recovered)
                        _savedSnapshot = null;   // a recovered project counts as unsaved
                    else
                        MarkProjectSaved();
                    LogMessage(string.Format(L("project_loaded_clips"), Path.GetFileName(d.FileName), timelineItems.Count), true);
                    PlayBeep();

                    // v1.4: media of a moved project is found again (relative paths, then a folder search)
                    if (projectPath != null) FindMovedMedia(Path.GetDirectoryName(projectPath) ?? "");

                    await Task.Delay(100);
                }
                catch (Exception ex)
                {
                    WpfMessageBox.Show(string.Format(L("project_load_error"), ex.Message), L("error_title"), MessageBoxButton.OK, MessageBoxImage.Error);
                    LogMessage(string.Format(L("generic_error"), ex.Message), true);
                }
            }
        }

        private void Exit_Click(object sender, RoutedEventArgs e) => Close();

        private TimelineItem DeepCloneTimelineItem(TimelineItem source)
        {
            // v1.4: copies EVERY field — undo/copy/cut used to lose the fixed position,
            // the text on the image, the colour grade and the ambient sound of a clip.
            var clone = new TimelineItem
            {
                Path = source.Path,
                Duration = source.Duration,
                Name = source.Name,
                Type = source.Type,
                Volume = source.Volume,
                TrackIndex = source.TrackIndex,
                AudioDescription = source.AudioDescription,
                Start = source.Start,
                End = source.End,
                FixedPosition = source.FixedPosition,
                UseFixedPosition = source.UseFixedPosition,
                SourceOffset = source.SourceOffset,
                TransitionOut = source.TransitionOut,
                TransitionOutDuration = source.TransitionOutDuration,
                ContentTag = source.ContentTag,
                AmbientSoundPath = source.AmbientSoundPath,
                RelativePath = source.RelativePath,
                TextOverlay = source.TextOverlay == null ? new TextOverlayData() : new TextOverlayData
                {
                    Text = source.TextOverlay.Text,
                    Font = source.TextOverlay.Font,
                    SelectedFontSize = source.TextOverlay.SelectedFontSize,
                    Color = source.TextOverlay.Color,
                    Position = source.TextOverlay.Position,
                    Enabled = source.TextOverlay.Enabled
                },
                VideoEffect = source.VideoEffect != null ? new VideoEffectData
                {
                    Brightness = source.VideoEffect.Brightness,
                    Contrast = source.VideoEffect.Contrast,
                    Blur = source.VideoEffect.Blur,
                    Speed = source.VideoEffect.Speed
                } : null,
                // v1.4 (R4/R5): without these, Ctrl+Z after trimming, a speed change or "lower the
                // music under speech" also threw away the volume points, audio effects and descriptions
                VolumePoints = source.VolumePoints?.Select(p => new VolumePoint { Time = p.Time, Volume = p.Volume }).ToList(),
                AudioFx = source.AudioFx == null ? null : new AudioSettingsData
                {
                    BassBoost = source.AudioFx.BassBoost,
                    TrebleBoost = source.AudioFx.TrebleBoost,
                    ReverbAmount = source.AudioFx.ReverbAmount,
                    CompressorEnabled = source.AudioFx.CompressorEnabled
                },
                FrameDescriptions = source.FrameDescriptions?.Select(d => new FrameDescription { Start = d.Start, End = d.End, Text = d.Text }).ToList()
            };
            foreach (var kf in source.Keyframes)
            {
                clone.Keyframes.Add(new AnimationKeyframe
                {
                    Time = kf.Time,
                    Zoom = kf.Zoom,
                    Rotation = kf.Rotation,
                    X = kf.X,
                    Y = kf.Y,
                    Opacity = kf.Opacity
                });
            }
            return clone;
        }

        public void SaveState()
        {
            var copy = new List<TimelineItem>();
            foreach (var item in timelineItems)
            {
                copy.Add(DeepCloneTimelineItem(item));
            }
            undoStack.Push(copy);
            redoStack.Clear();
        }

        /// <summary>v1.4 — drops the snapshot taken before a dialog that was cancelled.</summary>
        private void DiscardLastUndo()
        {
            if (undoStack.Count > 0) undoStack.Pop();
        }

        /// <summary>v1.4: silently go back to the last snapshot (a dialog was cancelled after it had already changed clips).</summary>
        private void RestoreLastUndo()
        {
            if (undoStack.Count == 0) return;
            timelineItems = undoStack.Pop();
            UpdateTimelineDisplay();
        }

        private void Undo()
        {
            if (undoStack.Count > 0)
            {
                var previous = undoStack.Pop();
                var currentCopy = new List<TimelineItem>();
                foreach (var item in timelineItems) currentCopy.Add(DeepCloneTimelineItem(item));
                redoStack.Push(currentCopy);
                timelineItems = previous;
                UpdateTimelineDisplay();
                LogMessage(L("undo_done"), true);
                PlayBeep();
            }
            else LogMessage(L("no_more_undo2"), true);
        }

        private void Redo()
        {
            if (redoStack.Count > 0)
            {
                var next = redoStack.Pop();
                var currentCopy = new List<TimelineItem>();
                foreach (var item in timelineItems) currentCopy.Add(DeepCloneTimelineItem(item));
                undoStack.Push(currentCopy);
                timelineItems = next;
                UpdateTimelineDisplay();
                LogMessage("Redone", true);
                PlayBeep();
            }
            else LogMessage(L("no_more_redo2"), true);
        }

        private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
        private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

        private void CopyClip_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                _copiedClip = DeepCloneTimelineItem(item);
                LogMessage($"Copied clip: {item.Name}", true);
                PlayBeep();
            }
            else LogMessage("No clip selected to copy", true);
        }

        private void PasteClip_Click(object sender, RoutedEventArgs e)
        {
            if (_copiedClip == null)
            {
                LogMessage("No copied clip to paste", true);
                return;
            }
            SaveState();
            var newClip = DeepCloneTimelineItem(_copiedClip);
            newClip.Name = _copiedClip.Name + " (kopija)";
            timelineItems.Add(newClip);
            UpdateTimelineDisplay();
            LogMessage("Clip pasted", true);
            PlayBeep();
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (nativeListView?.Items.Count > 0)
                {
                    foreach (WinForms.ListViewItem item in nativeListView.Items)
                    {
                        item.Selected = true;
                    }
                    LogMessage($"Selected {nativeListView.Items.Count} clips", true);
                    PlayBeep();
                }
                else
                {
                    LogMessage("No clips to select", true);
                }
            }
            catch (Exception ex)
            {
                LogMessage(string.Format(L("select_error"), ex.Message), true);
            }
        }

        private void ClearAllMarkers_Click(object sender, RoutedEventArgs e)
        {
            markers.Clear();
            UpdateMarkerDisplay();
            LogMessage("All markers deleted", true);
            PlayBeep();
        }

        private void ResetZoom_Click(object sender, RoutedEventArgs e)
        {
            zoomLevel = 1.0;
            UpdateZoomDisplay();
            LogMessage("Zoom resetovan na 1.0x", true);
        }

        private void ShowLogWindow_Click(object sender, RoutedEventArgs e)
        {
            if (_logWindow != null)
            {
                _logWindow.Show();
                _logWindow.Activate();
                LogMessage("Log window opened", true);
            }
        }

        private void ShowAllTracks_Click(object sender, RoutedEventArgs e)
        {
            currentTrackFilter = -1;
            UpdateTimelineDisplay();
            if (cmbTrackSelector != null)
                cmbTrackSelector.SelectedIndex = 4;
            LogMessage("Showing all tracks", true);
        }

        private void ShowVideoTracksOnly_Click(object sender, RoutedEventArgs e)
        {
            currentTrackFilter = 0;
            UpdateTimelineDisplay();
            if (cmbTrackSelector != null)
                cmbTrackSelector.SelectedIndex = 0;
            LogMessage("Prikazane samo video trake", true);
        }

        private void ShowAudioTracksOnly_Click(object sender, RoutedEventArgs e)
        {
            currentTrackFilter = 2;
            UpdateTimelineDisplay();
            if (cmbTrackSelector != null)
                cmbTrackSelector.SelectedIndex = 2;
            LogMessage("Prikazane samo audio trake", true);
        }

        private void MoveClipUp_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item && item.TrackIndex > 0)
            {
                SaveState();
                item.TrackIndex--;
                UpdateTimelineDisplay();
                LogMessage($"Clip moved to track {item.TrackIndex + 1}", true);
                PlayBeep();
            }
            else LogMessage(L("cant_move_higher"), true);
        }

        private void MoveClipDown_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                SaveState();
                item.TrackIndex++;
                UpdateTimelineDisplay();
                LogMessage($"Clip moved to track {item.TrackIndex + 1}", true);
                PlayBeep();
            }
            else LogMessage(L("cant_move_lower"), true);
        }

        private void ResetEffects_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                SaveState();
                if (item.VideoEffect != null)
                {
                    item.VideoEffect.Brightness = 0;
                    item.VideoEffect.Contrast = 0;
                    item.VideoEffect.Blur = 0;
                }
                LogMessage($"Effects reset on {item.Name}", true);
                PlayBeep();
            }
            else LogMessage("Select a clip first", true);
        }

        private void ShowVideoEffectsTab_Click(object sender, RoutedEventArgs e) => tabEffects.IsSelected = true;
        private void ShowAudioEffectsTab_Click(object sender, RoutedEventArgs e) => tabEffects.IsSelected = true;
        private void ShowTransitionsTab_Click(object sender, RoutedEventArgs e) => tabTransitions.IsSelected = true;
        private void ShowAnimationTab_Click(object sender, RoutedEventArgs e) => tabAnimation.IsSelected = true;
        private void ShowSubtitlesTab_Click(object sender, RoutedEventArgs e) => tabSubtitles.IsSelected = true;

        private void ShowMarkersDialog_Click(object sender, RoutedEventArgs e)
        {
            if (markers.Count == 0)
            {
                LogMessage("No markers", true);
                return;
            }
            string markerList = string.Join(", ", markers.Select(m => FormatTime(m)));
            WpfMessageBox.Show($"Markers:\n{markerList}", "Marker list", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ToggleGPUAcceleration_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            if (menuItem != null)
            {
                useGPUAcceleration = menuItem.IsChecked;
                chkGPUAcceleration.IsChecked = useGPUAcceleration;
                LogMessage(useGPUAcceleration ? L("gpu_on") : L("gpu_off"), true);
            }
        }

        private void MnuMediaProviders_Click(object sender, RoutedEventArgs e)
        {
            // Pronadji otvoreni AIVideoCreator prozor
            var creator = System.Windows.Application.Current.Windows
                .OfType<AIVideoCreator>()
                .FirstOrDefault();

            var dlg = new MediaProvidersDialog(creator);
            dlg.Owner = this;
            dlg.ShowDialog();
        }

        private void ShowPreferences_Click(object sender, RoutedEventArgs e)
        {
            WpfMessageBox.Show(L("settings_wip"), L("settings_title"), MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>v1.4: opens %LOCALAPPDATA%\UltraVideoEditor\Logs (error details, render logs).</summary>
        private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(AppPaths.LogsDir) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex)
            {
                LogMessage(string.Format(L("generic_error"), ex.Message), true);
            }
        }

        private void ShowUserManual_Click(object sender, RoutedEventArgs e)
        {
            // v1.4: the manual used to be a "coming soon" message — open the built-in help instead.
            ShowHelp_Click(sender, e);
        }

        private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
        {
            LogMessage(L("check_updates"), true);

            UpdateChecker.UpdateInfo update;
            try
            {
                var currentVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                update = await UpdateChecker.CheckForUpdateAsync(currentVersion);
            }
            catch (Exception ex)
            {
                LogMessage(L("update_check_failed"), true);
                WpfMessageBox.Show(string.Format(L("update_check_failed_msg"), ex.Message),
                    L("updates_title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (update == null)
            {
                WpfMessageBox.Show(L("up_to_date"), L("updates_title"), MessageBoxButton.OK, MessageBoxImage.Information);
                LogMessage(L("no_updates"), true);
                return;
            }

            string newVerText = $"v{update.Version.Major}.{update.Version.Minor}.{Math.Max(update.Version.Build, 0)}";
            var choice = WpfMessageBox.Show(
                string.Format(L("update_available_msg"), newVerText, GetAppVersion()),
                L("updates_title"), MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (choice != MessageBoxResult.Yes) return;

            if (string.IsNullOrEmpty(update.DownloadUrl))
            {
                // Release postoji, ali (jos) nema prikacen .exe installer - otvaramo stranicu
                // release-a u browseru kao rezervnu opciju umesto da korisnik ostane bez ičega.
                LogMessage(L("update_opening_page"), true);
                try { Process.Start(new ProcessStartInfo(update.HtmlUrl) { UseShellExecute = true }); } catch (Exception swallowed) { AppLog.Error("Opening the release page", swallowed); }   // v1.4: was silent
                return;
            }

            try
            {
                LogMessage(L("update_downloading"), true);

                string tempPath = Path.Combine(Path.GetTempPath(), $"UltraVideoEditor-Update-{update.TagName}.exe");

                // Installer ima više stotina MB: podrazumevani HttpClient timeout (100 s) je
                // na sporijoj vezi prekidao preuzimanje, a GetByteArrayAsync je držao ceo fajl
                // u memoriji. Sada se fajl upisuje direktno na disk, sa najavom na svakih 25%.
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(60) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("UltraVideoEditor");
                    using var response = await http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();
                    long total = response.Content.Headers.ContentLength ?? -1;

                    await using var net = await response.Content.ReadAsStreamAsync();
                    await using var file = File.Create(tempPath);
                    var buffer = new byte[81920];
                    long done = 0;
                    int read, lastQuarter = 0;
                    while ((read = await net.ReadAsync(buffer)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read));
                        done += read;
                        if (total > 0)
                        {
                            int quarter = (int)(done * 4 / total);
                            if (quarter > lastQuarter && quarter < 4)
                            {
                                lastQuarter = quarter;
                                LogMessage($"{L("update_downloading")} {quarter * 25}%", true);
                            }
                        }
                    }
                }

                // v1.4: check the SHA-256 before running the installer (it gets administrator rights)
                string problem = await UpdateChecker.VerifyAsync(update, tempPath);
                if (problem != null)
                {
                    try { File.Delete(tempPath); } catch { }
                    AppLog.Error("Update check", new Exception(problem));
                    WpfMessageBox.Show(string.Format(L("update_verify_failed"), problem), L("updates_title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    if (!string.IsNullOrEmpty(update.HtmlUrl))
                        try { Process.Start(new ProcessStartInfo(update.HtmlUrl) { UseShellExecute = true }); } catch { }
                    return;
                }
                LogMessage(L("update_verified"), true);

                LogMessage(L("update_installing"), true);
                WpfMessageBox.Show(L("update_installing"), L("updates_title"), MessageBoxButton.OK, MessageBoxImage.Information);

                Process.Start(new ProcessStartInfo(tempPath) { UseShellExecute = true });
                WpfApplication.Current.Shutdown();
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(string.Format(L("update_download_failed"), ex.Message),
                    L("updates_title"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// v1.4 — About window with the author and the ways to support development (GitHub Sponsors,
        /// Ko-fi, PayPal). A message box cannot hold links, so this is a small window: the text is a
        /// read-only text field (JAWS/NVDA read it line by line with the arrow keys), the links are
        /// buttons that open the default browser. Escape closes it.
        /// </summary>
        // ── v1.4: ambient sound library straight from Adobe ─────────────────────
        private Process _soundDownload;

        /// <summary>
        /// Help → Download the sound library from Adobe. Runs Tools\DownloadAdobeSounds.ps1 in the
        /// background (Adobe's free Audition sound effects, Adobe's server and licence, converted to
        /// OGG) and announces each step. Editing goes on meanwhile; clicking again while it runs says so.
        /// </summary>
        private async void DownloadAdobeSounds_Click(object sender, RoutedEventArgs e)
        {
            if (_soundDownload != null && !_soundDownload.HasExited) { LogMessage(L("snd_running"), true); return; }
            string script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "DownloadAdobeSounds.ps1");
            if (!File.Exists(script)) { LogMessage(LF("snd_failed", script), true); return; }
            if (!AppPaths.FFmpegAvailable) { LogMessage(LF("snd_failed", "FFmpeg"), true); return; }
            if (WpfMessageBox.Show(L("snd_confirm"), L("snd_title"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" -Dest \"{AppPaths.DownloadedSoundsDir}\" -Ffmpeg \"{AppPaths.FFmpeg}\"")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            try
            {
                _soundDownload = Process.Start(psi);
                _soundDownload.DrainStderr();
                LogMessage(L("snd_started"), true);
                string line, last = null;
                int lastQuarter = -1;
                while ((line = await _soundDownload.StandardOutput.ReadLineAsync()) != null)
                {
                    line = line.Trim();
                    if (line.StartsWith("STEP ")) LogMessage(LF("snd_step", line.Substring(5)), true);
                    else if (line.StartsWith("PROGRESS "))
                    {
                        // "PROGRESS Weather 50/200": spoken at every quarter, written every time
                        var m = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)/(\d+)$");
                        int q = m.Success && int.Parse(m.Groups[2].Value) > 0 ? int.Parse(m.Groups[1].Value) * 4 / int.Parse(m.Groups[2].Value) : -1;
                        LogMessage(LF("snd_step", line.Substring(9)), q != lastQuarter && q > 0);
                        lastQuarter = q;
                    }
                    else if (line.StartsWith("ERROR ")) LogMessage(LF("snd_failed", line.Substring(6)), true);
                    else if (line.StartsWith("DONE")) last = line;
                }
                await _soundDownload.WaitForExitAsync();
                AIVideoCreator.ResetSoundIndex();
                LocalSoundLibrary.ResetIndex();
                var parts = (last ?? "").Split(' ');
                if (parts.Length >= 3 && parts[0] == "DONE")
                    LogMessage(LF("snd_done", parts[1], parts[2]), true);
                else
                    LogMessage(L("snd_partial"), true);
                PlayBeep();
            }
            catch (Exception ex)
            {
                AppLog.Error("Downloading the Adobe sound library", ex);
                LogMessage(LF("snd_failed", ex.Message), true);
            }
            finally { _soundDownload = null; }
        }

        /// <summary>v1.4 — opens THIRD-PARTY-NOTICES.md (next to the program) in the default text viewer.</summary>
        private void ShowThirdPartyNotices_Click(object sender, RoutedEventArgs e)
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "THIRD-PARTY-NOTICES.md");
            try
            {
                if (!File.Exists(path)) throw new FileNotFoundException(path);
                // .md has no default app on many computers: open it with Notepad, which every Windows has
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLog.Error("Opening the third-party licences", ex);
                LogMessage(LF("about_open_failed", "https://github.com/demirajvazi10-max/Ultra-Creative-suite/blob/main/UltraVideoEditor/THIRD-PARTY-NOTICES.md"), true);
            }
        }

        private void ShowAbout_Click(object sender, RoutedEventArgs e)
        {
            var links = new (string Key, string Url)[]
            {
                ("about_btn_sponsors", "https://github.com/sponsors/demirajvazi10-max"),
                ("about_btn_kofi",     "https://ko-fi.com/ultracreativesuite"),
                ("about_btn_paypal",   "https://paypal.me/demirajvazi10"),
                ("about_btn_repo",     "https://github.com/demirajvazi10-max/Ultra-Creative-suite"),
            };

            var win = new System.Windows.Window
            {
                Title = L("about_title"),
                Owner = this,
                Width = 520,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false
            };
            win.SetResourceReference(System.Windows.Window.BackgroundProperty, "ControlBg");
            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(18) };

            string text = string.Format(L("about_text"), GetAppVersion()) + Environment.NewLine + Environment.NewLine + L("about_support_intro");
            var info = new System.Windows.Controls.TextBox
            {
                Text = text.Replace("\n", Environment.NewLine),
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 14)
            };
            info.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextPrimary");
            AutomationProperties.SetName(info, L("about_title"));
            panel.Children.Add(info);

            foreach (var (key, url) in links)
            {
                var b = new System.Windows.Controls.Button
                {
                    Content = L(key),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 0, 0, 6),
                    ToolTip = url
                };
                b.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "PanelBg");
                b.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextPrimary");
                AutomationProperties.SetHelpText(b, url);
                b.Click += (s, a) =>
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                        LogMessage(L("about_opening_browser"), true);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("Opening " + url, ex);
                        LogMessage(LF("about_open_failed", url), true);
                    }
                };
                panel.Children.Add(b);
            }

            var close = new System.Windows.Controls.Button
            {
                Content = L("about_btn_close"),
                IsCancel = true,
                IsDefault = true,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                MinWidth = 100,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 8, 0, 0)
            };
            close.Click += (s, a) => win.Close();
            panel.Children.Add(close);

            win.Content = panel;
            win.Loaded += (s, a) => { info.Focus(); info.CaretIndex = 0; };
            win.ShowDialog();
        }

        /// <summary>
        /// Verzija aplikacije - čita se iz &lt;Version&gt; u UltraVideoEditor.csproj (podešava
        /// AssemblyVersion/FileVersion/InformationalVersion automatski pri build-u). To je
        /// JEDINO mesto koje treba promeniti pre svakog GitHub release-a/tag-a - naslov
        /// prozora i "About" dijalog je preuzimaju odavde, bez ručnog kucanja broja verzije
        /// na više mesta u kodu.
        /// </summary>
        private static string GetAppVersion()
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (v == null) return "v?";
            return v.Build > 0 ? $"v{v.Major}.{v.Minor}.{v.Build}" : $"v{v.Major}.{v.Minor}";
        }

        private void SetupDragDropFromExplorer()
        {
            this.AllowDrop = true;
            this.Drop += Window_Drop;
            this.DragEnter += Window_DragEnter;
            this.DragOver += Window_DragOver;
        }

        private void Window_DragEnter(object sender, WpfDragEventArgs e)
        {
            if (e.Data.GetDataPresent(WpfDataFormats.FileDrop))
            {
                e.Effects = WpfDragDropEffects.Copy;
                if (accessibilityMode) LogMessage("Drag files to add them to the timeline", true);
            }
            else
            {
                e.Effects = WpfDragDropEffects.None;
            }
            e.Handled = true;
        }

        private void Window_DragOver(object sender, WpfDragEventArgs e)
        {
            if (e.Data.GetDataPresent(WpfDataFormats.FileDrop))
            {
                e.Effects = WpfDragDropEffects.Copy;
            }
            else
            {
                e.Effects = WpfDragDropEffects.None;
            }
            e.Handled = true;
        }

        private async void Window_Drop(object sender, WpfDragEventArgs e)
        {
            if (!e.Data.GetDataPresent(WpfDataFormats.FileDrop))
            {
                if (accessibilityMode) LogMessage(L("no_drag_data"), true);
                return;
            }
            string[] files = (string[])e.Data.GetData(WpfDataFormats.FileDrop);
            if (files == null || files.Length == 0)
            {
                if (accessibilityMode) LogMessage("No files", true);
                return;
            }
            var supportedExtensions = new[] { ".mp4", ".avi", ".mov", ".mkv", ".mp3", ".wav", ".m4a", ".flac", ".ogg", ".jpg", ".jpeg", ".png", ".bmp" };
            var validFiles = files.Where(f => supportedExtensions.Contains(Path.GetExtension(f).ToLower())).ToArray();
            if (validFiles.Length == 0)
            {
                if (accessibilityMode) LogMessage(L("unsupported_file"), true);
                return;
            }
            if (accessibilityMode) LogMessage($"Adding {validFiles.Length} files...", true);
            SaveState();
            int videoCount = 0, audioCount = 0, imageCount = 0;
            foreach (string file in validFiles)
            {
                string ext = Path.GetExtension(file).ToLower();
                bool isImage = ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".bmp";
                bool isAudio = ext == ".mp3" || ext == ".wav" || ext == ".m4a" || ext == ".flac" || ext == ".ogg";
                try
                {
                    double duration = 5.0;
                    string type;
                    int targetTrack = 0;
                    if (isImage)
                    {
                        type = "Image";
                        duration = 5.0;
                        targetTrack = 0;
                        imageCount++;
                    }
                    else if (isAudio)
                    {
                        type = "Audio";
                        duration = await GetMediaDurationWithTimeoutAsync(file, TimeSpan.FromSeconds(30));
                        targetTrack = 2;
                        audioCount++;
                    }
                    else
                    {
                        type = "Video";
                        duration = await GetMediaDurationWithTimeoutAsync(file, TimeSpan.FromSeconds(30));
                        targetTrack = 0;
                        videoCount++;
                    }
                    timelineItems.Add(new TimelineItem
                    {
                        Path = file,
                        Duration = duration,
                        Name = Path.GetFileName(file),
                        Type = type,
                        Volume = 100,
                        TrackIndex = targetTrack,
                        VideoEffect = new VideoEffectData()
                    });
                }
                catch (Exception ex)
                {
                    if (accessibilityMode) LogMessage(string.Format(L("add_file_error"), Path.GetFileName(file), ex.Message), true);
                }
            }
            UpdateTimelineDisplay();
            if (accessibilityMode) LogMessage($"Added: {videoCount} video, {audioCount} audio, {imageCount} images", true);
            PlayBeep();
            e.Handled = true;
        }

        private async Task<double> GetMediaDurationWithTimeoutAsync(string filePath, TimeSpan timeout)
        {
            try
            {
                var task = MediaLoader.GetMediaDurationAsync(filePath, _libVLC);
                var completedTask = await Task.WhenAny(task, Task.Delay(timeout));
                if (completedTask == task)
                {
                    return await task;
                }
                else
                {
                    LogMessage(string.Format(L("duration_timeout"), Path.GetFileName(filePath)), true);
                    return 5.0;
                }
            }
            catch (Exception ex)
            {
                LogMessage(string.Format(L("duration_error"), Path.GetFileName(filePath), ex.Message), true);
                return 5.0;
            }
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            if (zoomLevel < 5.0)
            {
                zoomLevel = Math.Min(5.0, zoomLevel + 0.5);
                UpdateZoomDisplay();
                LogMessage(string.Format(L("zoom_level"), zoomLevel), false);
            }
        }

        private void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            if (zoomLevel > 0.5)
            {
                zoomLevel = Math.Max(0.5, zoomLevel - 0.5);
                UpdateZoomDisplay();
                LogMessage($"Zoom smanjen {zoomLevel:F1}x", false);
            }
        }

        // v1.4: markers are stored in TIMELINE time (clip start + position in the clip).
        // They used to store the player's clip-relative time, so a marker set in clip 5
        // pointed somewhere into clip 1 and "Cut at all markers" cut the wrong clip or nothing.
        private void AddMarker_Click(object sender, RoutedEventArgs e)
        {
            double t = TimelinePosition;
            if (t >= 0)
            {
                markers.Add(t);
                markers.Sort();
                UpdateMarkerDisplay();
                string inClip = _playingItem != null ? $" ({_playingItem.Name}, {FormatTimeDetailed(currentPlaybackPosition)})" : "";
                LogMessage($"Marker added {FormatTimeDetailed(t)}{inClip}", true);
                PlayBeep();
            }
        }

        /// <summary>v1.4 — moves the preview to a timeline time, switching clips if needed.</summary>
        private void SeekToTimeline(double t)
        {
            int track = _playingItem?.TrackIndex ?? 0;
            var target = timelineItems
                .Where(i => i.TrackIndex == track && t >= i.Start - 0.001 && t < i.End)
                .FirstOrDefault()
                ?? timelineItems.Where(i => !i.IsAudio && t >= i.Start - 0.001 && t < i.End).FirstOrDefault();
            if (target == null) return;

            double inClip = t - target.Start;
            if (ReferenceEquals(target, _playingItem) && _currentMedia != null)
            {
                _mediaPlayer.Time = (long)((target.SourceOffset + inClip) * 1000);
                currentPlaybackPosition = inClip;
            }
            else
            {
                PlaySelectedItem(target, inClip);
                SelectItemInList(target);
            }
            UpdatePositionDisplay();
        }

        private void SelectItemInList(TimelineItem target)
        {
            if (nativeListView == null) return;
            foreach (WinForms.ListViewItem lvi in nativeListView.Items)
            {
                if (ReferenceEquals(lvi.Tag, target))
                {
                    _selectionProcessing = true;   // don't trigger another auto-play
                    nativeListView.SelectedItems.Clear();
                    lvi.Selected = true;
                    lvi.EnsureVisible();
                    _selectionProcessing = false;
                    break;
                }
            }
        }

        private void NextMarker_Click(object sender, RoutedEventArgs e)
        {
            if (markers.Count == 0) { LogMessage("No markers", true); return; }
            double now = TimelinePosition;
            bool found = markers.Any(m => m > now + 0.1);
            double next = found ? markers.First(m => m > now + 0.1) : markers[0];
            SeekToTimeline(next);
            LogMessage(string.Format(found ? L("jumped_to_marker") : L("jumped_to_first"), FormatTime(next)), true);
            PlayBeep();
        }

        private void PrevMarker_Click(object sender, RoutedEventArgs e)
        {
            if (markers.Count == 0) { LogMessage("No markers", true); return; }
            double now = TimelinePosition;
            bool found = markers.Any(m => m < now - 0.1);
            double prev = found ? markers.Last(m => m < now - 0.1) : markers[markers.Count - 1];
            SeekToTimeline(prev);
            LogMessage(string.Format(found ? L("jumped_to_marker") : L("jumped_to_last"), FormatTime(prev)), true);
            PlayBeep();
        }

        private void CutClip_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count == 0 || !(nativeListView.SelectedItems[0].Tag is TimelineItem item))
            {
                LogMessage("Select a clip first", true);
                return;
            }

            // v1.4: position INSIDE the selected clip (see PositionInClip) — the old
            // "player time minus clip start" was negative for every clip except the first.
            double cutPosition = PositionInClip(item);
            if (cutPosition > 0.5 && cutPosition < item.Duration - 0.5)
            {
                SaveState();
                var secondPart = DeepCloneTimelineItem(item);
                secondPart.Duration = item.Duration - cutPosition;
                secondPart.Name = item.Name + " " + L("cut_part_2");
                // v1.4: the second part CONTINUES the source (it used to start the file from
                // the beginning again in the render), and keeps its place if the clip was fixed.
                if (!item.IsImage) secondPart.SourceOffset = item.SourceOffset + cutPosition;
                if (item.UseFixedPosition) secondPart.FixedPosition = item.FixedPosition + cutPosition;
                item.TransitionOut = null; item.TransitionOutDuration = 0;
                secondPart.Keyframes.Clear();
                foreach (var kf in item.Keyframes.Where(k => k.Time > cutPosition))
                {
                    secondPart.Keyframes.Add(new AnimationKeyframe
                    {
                        Time = kf.Time - cutPosition,
                        Zoom = kf.Zoom,
                        Rotation = kf.Rotation,
                        X = kf.X,
                        Y = kf.Y,
                        Opacity = kf.Opacity
                    });
                }
                item.Keyframes.RemoveAll(k => k.Time > cutPosition);
                item.Duration = cutPosition;
                item.Name = item.Name + " " + L("cut_part_1");
                int originalIndex = timelineItems.IndexOf(item);
                timelineItems.Insert(originalIndex + 1, secondPart);
                UpdateTimelineDisplay();
                LogMessage(string.Format(L("clip_cut_at"), FormatTime(cutPosition)), true);
                PlayBeep();
            }
            else
            {
                LogMessage(L("position_too_close"), true);
            }
        }

        private void CutAtAllMarkers_Click(object sender, RoutedEventArgs e)
        {
            if (markers.Count == 0) { LogMessage(L("no_markers_to_cut"), true); return; }
            SaveState();
            RecalculatePositions();
            // v1.4: cut only the tracks being edited — video tracks normally, or the audio
            // track that is shown. Before, the song on the audio track was chopped too.
            bool cutAudio = currentTrackFilter == 2 || currentTrackFilter == 3;
            var newItems = new List<TimelineItem>();
            foreach (var item in timelineItems)
            {
                bool onEditedTrack = currentTrackFilter >= 0
                    ? item.TrackIndex == currentTrackFilter
                    : (cutAudio ? item.IsAudio : !item.IsAudio);
                var markersInItem = onEditedTrack
                    ? markers.Where(m => m > item.Start && m < item.End).OrderBy(m => m).ToList()
                    : new List<double>();
                if (markersInItem.Count == 0)
                {
                    newItems.Add(item);
                    continue;
                }
                double currentOffset = 0;
                int partIndex = 1;
                foreach (var marker in markersInItem)
                {
                    double cutPos = marker - item.Start;
                    if (cutPos > 0.5 && cutPos < item.Duration - 0.5)
                    {
                        var part = DeepCloneTimelineItem(item);
                        part.Duration = cutPos - currentOffset;
                        part.Name = $"{item.Name} (deo {partIndex})";
                        if (!item.IsImage) part.SourceOffset = item.SourceOffset + currentOffset;
                        if (item.UseFixedPosition) part.FixedPosition = item.FixedPosition + currentOffset;
                        part.TransitionOut = null; part.TransitionOutDuration = 0;
                        part.Keyframes.Clear();
                        foreach (var kf in item.Keyframes.Where(k => k.Time > currentOffset && k.Time <= cutPos))
                        {
                            part.Keyframes.Add(new AnimationKeyframe
                            {
                                Time = kf.Time - currentOffset,
                                Zoom = kf.Zoom,
                                Rotation = kf.Rotation,
                                X = kf.X,
                                Y = kf.Y,
                                Opacity = kf.Opacity
                            });
                        }
                        newItems.Add(part);
                        currentOffset = cutPos;
                        partIndex++;
                    }
                }
                var lastPart = DeepCloneTimelineItem(item);
                lastPart.Duration = item.Duration - currentOffset;
                lastPart.Name = $"{item.Name} (deo {partIndex})";
                if (!item.IsImage) lastPart.SourceOffset = item.SourceOffset + currentOffset;
                if (item.UseFixedPosition) lastPart.FixedPosition = item.FixedPosition + currentOffset;
                lastPart.Keyframes.Clear();
                foreach (var kf in item.Keyframes.Where(k => k.Time > currentOffset))
                {
                    lastPart.Keyframes.Add(new AnimationKeyframe
                    {
                        Time = kf.Time - currentOffset,
                        Zoom = kf.Zoom,
                        Rotation = kf.Rotation,
                        X = kf.X,
                        Y = kf.Y,
                        Opacity = kf.Opacity
                    });
                }
                newItems.Add(lastPart);
            }
            timelineItems = newItems;
            UpdateTimelineDisplay();
            LogMessage(L("cut_all_done"), true);
            PlayBeep();
        }

        // v1.4: Move left/right swaps the clip with its neighbour ON THE SAME TRACK (it used
        // to swap with whatever was next in the list — often the song on the audio track — so
        // nothing visible happened). For clips with fixed positions the two slots are swapped.
        private void MoveLeft_Click(object sender, RoutedEventArgs e) => MoveOnTrack(-1);
        private void MoveRight_Click(object sender, RoutedEventArgs e) => MoveOnTrack(+1);

        private void MoveOnTrack(int direction)
        {
            if (!(nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item))
            {
                LogMessage("Select a clip first", true);
                return;
            }
            RecalculatePositions();
            var sameTrack = timelineItems.Where(i => i.TrackIndex == item.TrackIndex).OrderBy(i => i.Start).ToList();
            int pos = sameTrack.IndexOf(item);
            int otherPos = pos + direction;
            if (otherPos < 0 || otherPos >= sameTrack.Count)
            {
                LogMessage(direction < 0 ? "No clip to the left" : "No clip to the right", true);
                return;
            }
            var other = sameTrack[otherPos];
            SaveState();

            var first = direction < 0 ? other : item;    // the one that is earlier now
            var second = direction < 0 ? item : other;
            if (first.UseFixedPosition || second.UseFixedPosition)
            {
                double gap = Math.Max(0, second.Start - first.End);
                double firstStart = first.Start;
                second.UseFixedPosition = true;
                second.FixedPosition = firstStart;
                first.UseFixedPosition = true;
                first.FixedPosition = firstStart + second.Duration + gap;
            }
            int iItem = timelineItems.IndexOf(item);
            int iOther = timelineItems.IndexOf(other);
            timelineItems[iItem] = other;
            timelineItems[iOther] = item;
            UpdateTimelineDisplay();
            SelectItemInList(item);
            LogMessage(direction < 0 ? "Clip moved left" : "Clip moved right", true);
            PlayBeep();
        }

        private async void SetClipDuration_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                var dialog = new DurationDialog(item.Duration);
                if (dialog.ShowDialog() == true)
                {
                    SaveState();   // v1.4: only when something changes (Cancel left an empty undo step)
                    item.Duration = dialog.Duration;
                    UpdateTimelineDisplay();
                    LogMessage($"Duration changed to {FormatTime(item.Duration)}", true);
                    PlayBeep();
                }
            }
            else LogMessage("Select a clip first", true);
        }

        private async void SetClipVolume_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                var dialog = new VolumeControl(item.Volume);
                if (dialog.ShowDialog() == true)
                {
                    SaveState();
                    item.Volume = dialog.Volume;
                    LogMessage(string.Format(L("volume_set2"), dialog.Volume) +
                               (AudioAutomation.HasCurve(item) ? " " + L("vol_curve_note") : ""), true);
                    PlayBeep();
                    if (_mediaPlayer != null && _mediaPlayer.IsPlaying) _mediaPlayer.Volume = (int)item.Volume;
                }
            }
            else LogMessage("Select a clip first", true);
        }

        private void RemoveFromTimeline_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                SaveState();
                timelineItems.Remove(item);
                UpdateTimelineDisplay();
                LogMessage("Clip deleted", true);
                PlayBeep();
            }
        }

        private void TogglePlay_Click(object sender, RoutedEventArgs e)
        {
            if (_currentMedia == null && timelineItems.Count > 0)
            {
                var firstItem = (nativeListView?.SelectedItems.Count > 0 ? nativeListView.SelectedItems[0].Tag as TimelineItem : null)
                                ?? timelineItems.FirstOrDefault();
                if (firstItem != null)
                    PlaySelectedItem(firstItem);
                return;
            }
            if (_currentMedia == null)
            {
                LogMessage("Select a clip first", true);
                return;
            }
            if (isPlaying)
            {
                _mediaPlayer.Pause();
                isPlaying = false;
                if (btnPlay != null) btnPlay.Content = "▶ " + L("play");
                LogMessage(LF("paused_at", FormatTimeDetailed(currentPlaybackPosition)), true);
            }
            else if (PlayerEnded)
            {
                // v1.4: after the end, Play did nothing (the player stays "ended") — start again
                RestartAt(0, play: true);
                LogMessage(_mixPreviewActive ? LF("mix_resumed", FormatTime(0)) : L("playback_from_start"), true);
            }
            else
            {
                _mediaPlayer.Play();
                isPlaying = true;
                if (btnPlay != null) btnPlay.Content = "⏸ " + L("pause");
                // Announce which clip is playing and its duration
                var selItem = nativeListView?.SelectedItems.Count > 0
                    ? nativeListView.SelectedItems[0].Tag as TimelineItem : null;
                if (_mixPreviewActive)
                    LogMessage(LF("mix_resumed", FormatTime(currentPlaybackPosition)), true);
                else if (selItem != null)
                    LogMessage($"Playing: {selItem.Name}, duration {FormatTime(selItem.Duration)}", true);
                else
                    LogMessage(L("playback_started_msg"), true);
            }
        }

        private void SeekForward_Click(object sender, RoutedEventArgs e) => SeekBy(5000);

        private void SeekBack_Click(object sender, RoutedEventArgs e) => SeekBy(-5000);

        /// <summary>
        /// v1.4 — moves the player by a number of milliseconds inside the current clip and always
        /// announces the new position (before, the announcement depended on accessibility mode and
        /// a clip that was not loaded yet did nothing without a word).
        /// </summary>
        private void SeekBy(long ms)
        {
            if (_mediaPlayer == null || _currentMedia == null)
            {
                LogMessage(L("select_clip_first_play"), true);
                return;
            }
            // v1.4 (R5): stay inside the clip — a cut part or highlight starts in the middle of its
            // file, and seeking used to run past its in-point into footage that is not on the timeline.
            if (PlayerEnded)
            {
                // the player cannot move once it reached the end: reload at the new place, paused
                double len = _mixPreviewActive ? currentVideoDuration : (_playingItem?.Duration ?? currentVideoDuration);
                double target = Math.Clamp(currentPlaybackPosition + ms / 1000.0, 0, Math.Max(0, len - 0.05));
                RestartAt(target, play: false);
                LogMessage(Math.Abs(ms) < 1000 ? FormatTimeDetailed(target) : FormatTime(target), true);
                return;
            }
            var (minT, maxT) = PlayerTimeBounds();
            long newTime = Math.Clamp(_mediaPlayer.Time + ms, minT, maxT);
            _mediaPlayer.Time = newTime;
            currentPlaybackPosition = Math.Max(0, newTime / 1000.0 - (_playingItem?.SourceOffset ?? 0));
            UpdatePositionDisplay();
            LogMessage(Math.Abs(ms) < 1000 ? FormatTimeDetailed(currentPlaybackPosition) : FormatTime(currentPlaybackPosition), true);
        }

        // ══════════════════════════════════════════════════════════════
        // v1.4 (roadmap 4.2) — AUDIO DESCRIPTION FOR VIEWERS
        // ══════════════════════════════════════════════════════════════

        private string _lastRenderPath;
        private string _adScript;

        private void OpenAudioDescription_Click(object sender, RoutedEventArgs e)
        {
            if (timelineItems.Count == 0) { LogMessage(L("mix_nothing"), true); return; }
            RecalculatePositions();

            // where singing or speech is heard: the subtitles (lyrics) and the voice clips
            IEnumerable<(double, double)> Busy()
            {
                foreach (var s in subtitles) yield return (s.Start, s.End);
                var music = timelineItems.Where(i => i.IsAudio).OrderByDescending(i => i.Duration).FirstOrDefault();
                foreach (var v in timelineItems.Where(i => i.IsAudio && !ReferenceEquals(i, music) && i.Volume > 0 &&
                                                           !(i.Name ?? "").StartsWith("🔊") && !(i.Name ?? "").StartsWith("🌳") &&
                                                           !RenderEngine.IsAiCreatorClip(i)))
                    yield return (v.Start, v.Start + v.Duration);
            }

            string video = _lastRenderPath;
            if (string.IsNullOrEmpty(video) || !File.Exists(video))
            {
                string guess = string.IsNullOrEmpty(currentProjectFolder) ? null : Path.Combine(currentProjectFolder, "izlazni_video.mp4");
                video = guess != null && File.Exists(guess) ? guess : "";
            }
            var win = new AudioDescriptionWindow(this, timelineItems, Busy, video, _adScript, msg => LogMessage(msg, true));
            win.ShowDialog();
            _adScript = win.ScriptText;
            UpdateTimelineDisplay();   // descriptions made on the way are shown in the list
            _pendingPlaybackItem = null;
        }

        // ══════════════════════════════════════════════════════════════
        // v1.4 (R7) — GRAPHIC TIMELINE FOR SIGHTED EDITORS (mouse)
        // A second view of the same clips: everything goes through the same data, undo and list.
        // ══════════════════════════════════════════════════════════════

        private System.Windows.Threading.DispatcherTimer _graphicTimer;
        private double _graphicLastPlayhead = -1;

        private void InitGraphicTimeline()
        {
            if (graphicTimeline == null) return;
            graphicTimeline.Items = () => timelineItems;
            graphicTimeline.PlayheadTime = () => GraphicPlayhead();
            graphicTimeline.Markers = () => markers;
            graphicTimeline.ClipClicked += GraphicClipClicked;
            graphicTimeline.TimeClicked += GraphicTimeClicked;
            graphicTimeline.ClipMoved += GraphicClipMoved;
            graphicTimeline.ClipTrimmed += GraphicClipTrimmed;
            graphicTimeline.ZoomRequested += d => { if (d > 0) ZoomIn_Click(null, null); else ZoomOut_Click(null, null); };
            graphicTimeline.InfoChanged += s => { if (txtGraphicInfo != null) txtGraphicInfo.Text = s; };
            if (svGraphicTimeline != null)
                svGraphicTimeline.ScrollChanged += (s, e) =>
                {
                    graphicTimeline.ScrollOffset = e.HorizontalOffset;
                    graphicTimeline.InvalidateVisual();
                };

            // the playhead moves ten times a second; while playing, the view scrolls along
            _graphicTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _graphicTimer.Tick += (s, e) =>
            {
                if (pnlGraphicTimeline == null || pnlGraphicTimeline.Visibility != Visibility.Visible) return;
                double ph = GraphicPlayhead();
                if (Math.Abs(ph - _graphicLastPlayhead) < 0.02) return;
                _graphicLastPlayhead = ph;
                graphicTimeline.InvalidateVisual();
                if (isPlaying && svGraphicTimeline != null)
                {
                    double x = GraphicTimeline.LabelWidth + ph * graphicTimeline.PixelsPerSecond;
                    double left = svGraphicTimeline.HorizontalOffset, view = svGraphicTimeline.ViewportWidth;
                    if (view > 0 && (x < left + GraphicTimeline.LabelWidth || x > left + view - 40))
                        svGraphicTimeline.ScrollToHorizontalOffset(Math.Max(0, x - GraphicTimeline.LabelWidth - 60));
                }
            };
            _graphicTimer.Start();
            RefreshGraphicTimeline();
        }

        private double GraphicPlayhead() =>
            _mixPreviewActive || _playingItem != null ? TimelinePosition : (SelectedClip()?.Start ?? 0);

        private void RefreshGraphicTimeline()
        {
            if (graphicTimeline == null) return;
            graphicTimeline.PixelsPerSecond = 20 * zoomLevel;
            graphicTimeline.Selected = SelectedClip();
            graphicTimeline.Refresh();
        }

        /// <summary>Selects a clip in the clip list without starting it (the graphic view decides what plays).</summary>
        private void SelectClipInList(TimelineItem item)
        {
            if (nativeListView == null || item == null) return;
            if (currentTrackFilter != -1 && item.TrackIndex != currentTrackFilter)
            {
                currentTrackFilter = -1;
                if (cmbTrackSelector != null) cmbTrackSelector.SelectedIndex = 4;
                UpdateTimelineDisplay();
            }
            foreach (WinForms.ListViewItem li in nativeListView.Items)
            {
                if (!ReferenceEquals(li.Tag, item)) continue;
                nativeListView.SelectedItems.Clear();
                li.Selected = true;
                li.Focused = true;
                li.EnsureVisible();
                break;
            }
            _pendingPlaybackItem = null;
            graphicTimeline.Selected = item;
            graphicTimeline.InvalidateVisual();
        }

        /// <summary>Puts the playhead at a timeline time: in the montage mix, or in the clip under it.</summary>
        private void SeekTimelineTo(double t, TimelineItem clip, bool play)
        {
            if (_mixPreviewActive && _currentMedia != null)
            {
                if (PlayerEnded) RestartAt(t, play);
                else
                {
                    _mediaPlayer.Time = (long)(t * 1000);
                    currentPlaybackPosition = t;
                    if (play && !isPlaying) { _mediaPlayer.Play(); SetPlayingUi(true); }
                }
                return;
            }
            if (clip == null) return;
            PlaySelectedItem(clip, Math.Clamp(t - clip.Start, 0, Math.Max(0, clip.Duration - 0.05)), startPaused: !play);
        }

        private void GraphicClipClicked(TimelineItem item, double posInClip, bool doubleClick)
        {
            SelectClipInList(item);
            SeekTimelineTo(item.Start + posInClip, item, play: doubleClick);
            if (txtGraphicInfo != null) txtGraphicInfo.Text = $"{item.Name}  —  {FormatTimeDetailed(item.Start + posInClip)}";
        }

        private void GraphicTimeClicked(double t)
        {
            var clip = TimelineMixPreview.VideoAt(timelineItems, t) ??
                       timelineItems.FirstOrDefault(i => t >= i.Start && t < i.Start + i.Duration);
            if (clip != null && !_mixPreviewActive) SelectClipInList(clip);
            SeekTimelineTo(t, clip, play: false);
        }

        private void GraphicClipMoved(TimelineItem item, double newStart, int newTrack)
        {
            SaveState();
            item.TrackIndex = newTrack;
            item.UseFixedPosition = true;
            item.FixedPosition = newStart;
            UpdateTimelineDisplay();
            _pendingPlaybackItem = null;
            SelectClipInList(item);
            LogMessage(LF("gt_moved", Path.GetFileNameWithoutExtension(item.Name ?? ""), FormatTimeDetailed(item.Start), item.TrackName.Replace("🎬 ", "").Replace("🎵 ", "")), true);
        }

        private void GraphicClipTrimmed(TimelineItem item, double newStart, double newOffset, double newDuration)
        {
            SaveState();
            double shift = newStart - item.Start;
            if (Math.Abs(shift) > 0.001)
            {
                item.UseFixedPosition = true;   // the left edge moved: the clip keeps its new place
                item.FixedPosition = newStart;
                ShiftClipContent(item, shift);
            }
            item.SourceOffset = newOffset;
            item.Duration = newDuration;
            UpdateTimelineDisplay();
            _pendingPlaybackItem = null;
            SelectClipInList(item);
            LogMessage(LF("gt_trimmed", Path.GetFileNameWithoutExtension(item.Name ?? ""), FormatTimeDetailed(item.Start), FormatTimeDetailed(item.Duration)), true);
        }

        /// <summary>
        /// When the start of a clip is cut off (or put back), its volume points and picture
        /// descriptions move with the material they belong to.
        /// </summary>
        private static void ShiftClipContent(TimelineItem item, double shift)
        {
            if (item.VolumePoints != null)
            {
                foreach (var p in item.VolumePoints) p.Time -= shift;
                item.VolumePoints = item.VolumePoints.Where(p => p.Time >= -0.001 && p.Time <= item.Duration + 0.001).ToList();
                if (item.VolumePoints.Count == 0) item.VolumePoints = null;
            }
            if (item.FrameDescriptions != null)
            {
                foreach (var d in item.FrameDescriptions) { d.Start -= shift; d.End -= shift; }
                item.FrameDescriptions = item.FrameDescriptions.Where(d => d.End > 0.05).ToList();
                foreach (var d in item.FrameDescriptions) d.Start = Math.Max(0, d.Start);
            }
        }

        private void ToggleGraphicTimeline_Click(object sender, RoutedEventArgs e)
        {
            bool show = pnlGraphicTimeline.Visibility != Visibility.Visible;
            pnlGraphicTimeline.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            splGraphicTimeline.Visibility = pnlGraphicTimeline.Visibility;
            rowGraphicTimeline.Height = show ? new GridLength(262) : new GridLength(0);
            if (sender is MenuItem mi) mi.IsChecked = show;
            if (show) RefreshGraphicTimeline();
            LogMessage(L(show ? "gt_shown" : "gt_hidden"), true);
        }

        // ══════════════════════════════════════════════════════════════
        // v1.4 (R5) — PRECISE EDITING FROM THE KEYBOARD
        //   J / K / L   shuttle back / stop / forward (press again for 2× and 4×)
        //   , and .     one frame back / forward (the player pauses)
        //   Q / W       trim the start / the end of the clip to the playhead
        //   Ctrl+Alt+S  clip speed
        // ══════════════════════════════════════════════════════════════

        private string _lastKeyCmd;
        private long _lastKeyTick;
        private int _shuttle = 0;   // > 0 forward ×N, < 0 backward ×N, 0 = normal
        private System.Windows.Threading.DispatcherTimer _rewindTimer;

        /// <summary>True when the player reached the end (or stopped) and cannot simply continue.</summary>
        private bool PlayerEnded =>
            _mediaPlayer != null && _currentMedia != null &&
            (_mediaPlayer.State == LibVLCSharp.Shared.VLCState.Ended ||
             _mediaPlayer.State == LibVLCSharp.Shared.VLCState.Stopped ||
             _mediaPlayer.State == LibVLCSharp.Shared.VLCState.Error);

        /// <summary>
        /// v1.4 — reloads what was playing (the clip, or the montage mix) at a position, playing or
        /// paused. Needed after the end: LibVLC ignores Play and seeking once a file has ended,
        /// which made play, J/K/L and Alt+arrows silently do nothing ("position 00:00" forever).
        /// </summary>
        private void RestartAt(double pos, bool play)
        {
            if (_mixPreviewActive && _mixPath != null && File.Exists(_mixPath))
            {
                LoadMix(_mixPath, pos, play);
                return;
            }
            var item = _playingItem ?? SelectedClip();
            if (item != null) PlaySelectedItem(item, Math.Clamp(pos, 0, Math.Max(0, item.Duration - 0.05)), startPaused: !play);
        }

        /// <summary>
        /// True when the same command already ran for this very key press — a key pressed in the
        /// clip list can arrive both through Windows Forms and through the WPF window.
        /// </summary>
        private bool DuplicateKey(string id)
        {
            long now = Environment.TickCount64;
            bool dup = id == _lastKeyCmd && now - _lastKeyTick < 25;
            _lastKeyCmd = id;
            _lastKeyTick = now;
            return dup;
        }

        private static bool IsTextInput(object source) =>
            source is System.Windows.Controls.Primitives.TextBoxBase || source is System.Windows.Controls.PasswordBox ||
            source is System.Windows.Controls.ComboBox || (source is FrameworkElement fe && fe.TemplatedParent is System.Windows.Controls.ComboBox);

        private static double ClipSpeed(TimelineItem item)
        {
            double s = item?.VideoEffect?.Speed ?? 1.0;
            return s > 0.05 && s < 20 ? s : 1.0;
        }

        /// <summary>Player time range (ms) that belongs to the playing clip; the whole file otherwise.</summary>
        private (long Min, long Max) PlayerTimeBounds()
        {
            long length = _mediaPlayer?.Length ?? 0;
            long max = length > 0 ? length : long.MaxValue / 4;
            if (_playingItem == null || _playingItem.IsImage) return (0, max);
            long min = (long)Math.Round(_playingItem.SourceOffset * 1000);
            long end = (long)Math.Round((_playingItem.SourceOffset + _playingItem.Duration * ClipSpeed(_playingItem)) * 1000);
            return (Math.Min(min, max), Math.Max(min, Math.Min(end, max)));
        }

        private string FormatTimeFrames(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"mm\:ss\.ff");

        /// <summary>Handles J, K, L, Q, W, comma and period. Returns false for any other key.</summary>
        private bool HandleEditingKey(Key key)
        {
            if (key != Key.J && key != Key.K && key != Key.L && key != Key.Q && key != Key.W &&
                key != Key.OemComma && key != Key.OemPeriod) return false;
            if (DuplicateKey("ed" + key)) return true;
            switch (key)
            {
                case Key.J: Shuttle(-1); break;
                case Key.K: ShuttleStop(); break;
                case Key.L: Shuttle(+1); break;
                case Key.OemComma: StepFrame(-1); break;
                case Key.OemPeriod: StepFrame(+1); break;
                case Key.Q: TrimToPlayhead(trimStart: true); break;
                case Key.W: TrimToPlayhead(trimStart: false); break;
            }
            return true;
        }

        private bool PlayerReady()
        {
            if (_mediaPlayer != null && _currentMedia != null) return true;
            LogMessage(L("select_clip_first_play"), true);
            return false;
        }

        private void SetPlayingUi(bool playing)
        {
            isPlaying = playing;
            if (btnPlay != null) btnPlay.Content = playing ? "⏸ " + L("pause") : "▶ " + L("play");
        }

        /// <summary>L plays forward (again: 2×, 4×); J runs backward (again: 2×, 4×, 8×).</summary>
        private void Shuttle(int direction)
        {
            if (!PlayerReady()) return;
            if (PlayerEnded)
            {
                if (direction > 0) { RestartAt(0, play: true); LogMessage(L("playback_from_start"), true); }
                else { RestartAt(Math.Max(0, currentPlaybackPosition - 1), play: false); LogMessage(LF("paused_at", FormatTimeDetailed(currentPlaybackPosition)), true); }
                return;
            }
            double baseRate = ClipSpeed(_playingItem);
            if (direction > 0)
            {
                _rewindTimer?.Stop();
                _shuttle = _shuttle <= 0 ? 1 : Math.Min(_shuttle * 2, 4);
                _mediaPlayer.SetRate((float)(baseRate * _shuttle));
                if (!isPlaying) { _mediaPlayer.SetPause(false); _mediaPlayer.Play(); }
                SetPlayingUi(true);
                LogMessage(_shuttle == 1 ? L("shuttle_play") : LF("shuttle_forward", _shuttle), true);
                return;
            }

            // VLC cannot play backward: the player pauses and the position steps back four times
            // a second, so the editor hears where the playhead is going through the announcements.
            _shuttle = _shuttle >= 0 ? -1 : Math.Max(_shuttle * 2, -8);
            if (isPlaying) _mediaPlayer.SetPause(true);
            SetPlayingUi(false);
            _mediaPlayer.SetRate((float)baseRate);
            if (_rewindTimer == null)
            {
                _rewindTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _rewindTimer.Tick += (s, e) =>
                {
                    if (_shuttle >= 0 || _mediaPlayer == null || _currentMedia == null) { _rewindTimer.Stop(); return; }
                    var (minT, _) = PlayerTimeBounds();
                    long t = Math.Max(minT, _mediaPlayer.Time - 250L * -_shuttle);
                    _mediaPlayer.Time = t;
                    currentPlaybackPosition = Math.Max(0, t / 1000.0 - (_playingItem?.SourceOffset ?? 0));
                    UpdatePositionDisplay();
                    if (t <= minT)
                    {
                        _rewindTimer.Stop();
                        _shuttle = 0;
                        LogMessage(LF("shuttle_at_start", FormatTimeDetailed(currentPlaybackPosition)), true);
                    }
                };
            }
            _rewindTimer.Start();
            LogMessage(LF("shuttle_back", -_shuttle), true);
        }

        /// <summary>K: stop the shuttle, pause, and say where the playhead is.</summary>
        private void ShuttleStop()
        {
            if (!PlayerReady()) return;
            _rewindTimer?.Stop();
            _shuttle = 0;
            _mediaPlayer.SetRate((float)ClipSpeed(_playingItem));
            if (isPlaying) _mediaPlayer.SetPause(true);
            SetPlayingUi(false);
            LogMessage(LF("paused_at", FormatTimeDetailed(_mixPreviewActive ? currentPlaybackPosition : PositionInClip(_playingItem))), true);
        }

        private double FrameRateOfCurrentMedia()
        {
            try
            {
                foreach (var t in _currentMedia.Tracks)
                    if (t.TrackType == TrackType.Video && t.Data.Video.FrameRateNum > 0 && t.Data.Video.FrameRateDen > 0)
                    {
                        double fps = (double)t.Data.Video.FrameRateNum / t.Data.Video.FrameRateDen;
                        if (fps >= 5 && fps <= 240) return fps;
                    }
            }
            catch { }
            return 25.0;
        }

        /// <summary>Comma / period: one frame back / forward, paused, with the exact time announced.</summary>
        private void StepFrame(int direction)
        {
            if (!PlayerReady()) return;
            if (PlayerEnded)
            {
                RestartAt(Math.Max(0, currentPlaybackPosition - 0.04), play: false);
                LogMessage(FormatTimeFrames(currentPlaybackPosition), true);
                return;
            }
            _rewindTimer?.Stop();
            _shuttle = 0;
            if (isPlaying) _mediaPlayer.SetPause(true);
            SetPlayingUi(false);
            long frameMs = (long)Math.Max(1, Math.Round(1000.0 / FrameRateOfCurrentMedia()));
            var (minT, maxT) = PlayerTimeBounds();
            long t = Math.Clamp(_mediaPlayer.Time + direction * frameMs, minT, maxT);
            _mediaPlayer.Time = t;
            currentPlaybackPosition = Math.Max(0, t / 1000.0 - (_playingItem?.SourceOffset ?? 0));
            UpdatePositionDisplay();
            LogMessage(FormatTimeFrames(currentPlaybackPosition), true);
        }

        /// <summary>
        /// Q / W — trims the start or the end of the clip to the playhead (Premiere's ripple trim).
        /// The clips after it on the same track move up, so no gap is left. The clip then plays
        /// from its new start (Q) or its last two seconds (W) so the edit can be heard at once.
        /// </summary>
        private void TrimToPlayhead(bool trimStart)
        {
            var item = _playingItem ??
                       (nativeListView?.SelectedItems.Count > 0 ? nativeListView.SelectedItems[0].Tag as TimelineItem : null);
            if (item == null) { LogMessage(L("select_clip_first_play"), true); return; }
            double pos = PositionInClip(item);
            if (pos < 0.04 || pos > item.Duration - 0.04)
            {
                LogMessage(LF("trim_outside", FormatTimeDetailed(pos), FormatTimeDetailed(item.Duration)), true);
                PlayBeep();
                return;
            }
            SaveState();
            double before = item.Duration;
            if (trimStart)
            {
                if (!item.IsImage) item.SourceOffset += pos * ClipSpeed(item);
                item.Duration = before - pos;
                ShiftClipContent(item, pos);   // volume points and descriptions stay on the same picture/sound
            }
            else
            {
                item.Duration = pos;
            }
            UpdateTimelineDisplay();
            _pendingPlaybackItem = null;   // the list refresh must not restart the clip from 0
            LogMessage(LF(trimStart ? "trim_start_done" : "trim_end_done",
                          FormatTimeDetailed(before - item.Duration), FormatTimeDetailed(item.Duration)), true);
            PlaySelectedItem(item, trimStart ? 0 : Math.Max(0, item.Duration - 2));
        }

        /// <summary>Ctrl+Alt+S — choose the playback speed of the selected clip.</summary>
        private void SetClipSpeed_Click(object sender, RoutedEventArgs e)
        {
            if (!(nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item))
            {
                LogMessage("Select a clip first", true);
                return;
            }
            if (item.IsAudio || item.IsImage) { LogMessage(L("speed_video_only"), true); return; }

            double[] speeds = { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0 };
            double current = ClipSpeed(item);
            var dlg = new System.Windows.Window
            {
                Title = L("speed_title"),
                Owner = this,
                Width = 360,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false
            };
            dlg.SetResourceReference(System.Windows.Window.BackgroundProperty, "ControlBg");
            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
            var label = new System.Windows.Controls.Label { Content = L("speed_label"), Padding = new Thickness(0, 0, 0, 6) };
            label.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextPrimary");
            var list = new System.Windows.Controls.ListBox { Height = 200 };
            foreach (double s in speeds)
            {
                string text = s == 1.0 ? L("speed_normal")
                                       : (s.ToString("0.##", CultureInfo.CurrentCulture) + "×" + (s < 1 ? " — " + L("speed_slower") : " — " + L("speed_faster")));
                list.Items.Add(new System.Windows.Controls.ListBoxItem { Content = text, Tag = s });
            }
            int sel = Array.FindIndex(speeds, s => Math.Abs(s - current) < 0.001);
            list.SelectedIndex = sel >= 0 ? sel : 3;
            label.Target = list;
            AutomationProperties.SetName(list, L("speed_label"));
            var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var ok = new System.Windows.Controls.Button { Content = L("dlg_ok"), IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4) };
            var cancel = new System.Windows.Controls.Button { Content = L("review_cancel"), IsCancel = true, MinWidth = 80, Padding = new Thickness(10, 4, 10, 4) };
            ok.Click += (s, a) => dlg.DialogResult = true;
            list.MouseDoubleClick += (s, a) => dlg.DialogResult = true;
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(label);
            panel.Children.Add(list);
            panel.Children.Add(buttons);
            dlg.Content = panel;
            dlg.Loaded += (s, a) =>
            {
                list.Focus();
                (list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex) as System.Windows.Controls.ListBoxItem)?.Focus();
            };
            if (dlg.ShowDialog() != true || !(list.SelectedItem is System.Windows.Controls.ListBoxItem chosen) || !(chosen.Tag is double speed)) return;

            SaveState();
            item.VideoEffect ??= new VideoEffectData();
            item.VideoEffect.Speed = speed;
            UpdateTimelineDisplay();
            _pendingPlaybackItem = null;
            LogMessage(LF("speed_set", speed.ToString("0.##", CultureInfo.CurrentCulture)) +
                       (Math.Abs(speed - 1.0) > 0.001 ? " " + L("speed_sound_note") : ""), true);
            PlayBeep();
            PlaySelectedItem(item);
        }

        // ══════════════════════════════════════════════════════════════
        // v1.4 (R3) — HEAR THE WHOLE MONTAGE WITHOUT A RENDER
        // Ctrl+Shift+Space mixes every sound of the timeline (music, ambience, effects, the sound
        // of your own videos) into one file in a few seconds and plays it from the selected clip.
        // While it plays, the shot that is on screen at that moment is announced, so a blind
        // editor hears both the sound and the picture order, like pressing Space in Premiere.
        // ══════════════════════════════════════════════════════════════

        private bool _mixPreviewActive = false;
        private bool _mixAnnounceShots = true;
        private bool _mixBuilding = false;
        private TimelineItem _mixLastShot = null;
        private System.Windows.Threading.DispatcherTimer _mixShotTimer;

        private async void PlayMix_Click(object sender, RoutedEventArgs e)
        {
            // The mix is already loaded: the same key pauses and continues it.
            if (_mixPreviewActive && _currentMedia != null &&
                _mediaPlayer.State != LibVLCSharp.Shared.VLCState.Ended &&
                _mediaPlayer.State != LibVLCSharp.Shared.VLCState.Stopped)
            {
                if (isPlaying)
                {
                    _mediaPlayer.Pause();
                    isPlaying = false;
                    if (btnPlay != null) btnPlay.Content = "▶ " + L("play");
                    LogMessage(LF("mix_paused_at", FormatTime(currentPlaybackPosition)), true);
                }
                else
                {
                    _mediaPlayer.Play();
                    isPlaying = true;
                    if (btnPlay != null) btnPlay.Content = "⏸ " + L("pause");
                    LogMessage(LF("mix_resumed", FormatTime(currentPlaybackPosition)), true);
                }
                return;
            }
            if (_mixBuilding) { LogMessage(L("mix_building"), true); return; }
            if (timelineItems.Count == 0) { LogMessage(L("mix_nothing"), true); return; }

            // Where to start: where the player stands, else at the selected clip, else at 0.
            double total = TimelineMixPreview.TotalLength(timelineItems);
            double from = 0;
            if (!_mixPreviewActive && _playingItem != null) from = TimelinePosition;
            else if (!_mixPreviewActive && nativeListView?.SelectedItems.Count > 0 &&
                     nativeListView.SelectedItems[0].Tag is TimelineItem sel) from = sel.Start;
            if (from >= total - 0.5) from = 0;

            _mixBuilding = true;
            LogMessage(L("mix_building"), true);
            try
            {
                RecalculatePositions();
                var snapshot = timelineItems.ToList();
                var (path, count) = await TimelineMixPreview.BuildAsync(snapshot, CancellationToken.None);
                if (path == null)
                {
                    LogMessage(L("mix_nothing"), true);
                    PlayBeep();
                    return;
                }

                _mixPreviewActive = true;
                LoadMix(path, from, play: true);
                currentVideoDuration = total;
                UpdatePositionDisplay();
                LogMessage(LF("mix_playing", FormatTime(from), FormatTime(total), count), true);
            }
            catch (Exception ex)
            {
                AppLog.Error("Mixing the timeline for preview", ex);
                LogMessage(LF("mix_failed", ex.Message), true);
            }
            finally
            {
                _mixBuilding = false;
            }
        }

        private string _mixPath;

        /// <summary>Loads the montage mix into the player at a timeline position.</summary>
        private void LoadMix(string path, double from, bool play)
        {
            if (_mediaPlayer.IsPlaying) _mediaPlayer.Stop();
            _rewindTimer?.Stop();   // a J (backward) still running must not move the mix
            _shuttle = 0;
            var oldMedia = _currentMedia;
            _currentMedia = new Media(_libVLC, path);
            if (from > 0.01)
                _currentMedia.AddOption($":start-time={from.ToString("F3", CultureInfo.InvariantCulture)}");
            if (!play) _currentMedia.AddOption(":start-paused");
            _mediaPlayer.Media = _currentMedia;
            oldMedia?.Dispose();
            _mediaPlayer.Volume = 100;
            _mediaPlayer.SetRate(1f);
            _playingItem = null;               // positions are now timeline time
            _mixPath = path;
            _mixPreviewActive = true;
            currentPlaybackPosition = from;
            _mixLastShot = null;
            _mediaPlayer.Play();
            isPlaying = play;
            if (btnPlay != null) btnPlay.Content = play ? "⏸ " + L("pause") : "▶ " + L("play");
            StartFollowTimer();
        }

        /// <summary>Turns the shot announcements during the montage mix on or off.</summary>
        private void ToggleMixShotAnnouncements_Click(object sender, RoutedEventArgs e)
        {
            _mixAnnounceShots = !_mixAnnounceShots;
            if (sender is MenuItem mi) mi.IsChecked = _mixAnnounceShots;
            LogMessage(L(_mixAnnounceShots ? "mix_shots_on" : "mix_shots_off"), true);
        }

        /// <summary>
        /// v1.4 (R3/R4) — follows the playhead four times a second. During the montage mix it
        /// announces each new shot (name, on-screen text and its saved picture description);
        /// for a single clip it announces the saved description of the part the playhead
        /// enters — while playing, seeking, stepping frames or shuttling.
        /// </summary>
        private void StartFollowTimer()
        {
            if (_mixShotTimer == null)
            {
                _mixShotTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _mixShotTimer.Tick += (s, e) => FollowTick();
            }
            _followPart = null;
            _mixShotTimer.Start();
        }

        private FrameDescription _followPart;

        private void FollowTick()
        {
            if (_currentMedia == null) return;
            TimelineItem item;
            double pos;
            if (_mixPreviewActive)
            {
                item = TimelineMixPreview.VideoAt(timelineItems, TimelinePosition);
                pos = item == null ? 0 : TimelinePosition - item.Start;
            }
            else
            {
                item = _playingItem;
                pos = currentPlaybackPosition;
            }
            if (item == null) return;

            // v1.4 (R5): the clip preview follows the clip's volume curve
            if (!_mixPreviewActive && AudioAutomation.HasCurve(item) && _mediaPlayer != null)
            {
                int v = (int)Math.Round(Math.Clamp(AudioAutomation.VolumeAt(item, pos), 0, 200));
                if (_mediaPlayer.Volume != v) _mediaPlayer.Volume = v;
            }

            var part = _announceDescriptions ? item.DescriptionAt(pos) : null;
            bool shotChanged = !ReferenceEquals(item, _mixLastShot);
            bool partChanged = !ReferenceEquals(part, _followPart);
            _mixLastShot = item;
            _followPart = part;

            if (_mixPreviewActive && shotChanged)
            {
                if (!isPlaying || !_mixAnnounceShots) return;
                string text = item.TextOverlay?.Enabled == true && !string.IsNullOrWhiteSpace(item.TextOverlay.Text)
                    ? $"{item.Name} — {item.TextOverlay.Text}" : item.Name;
                if (part != null) text += ". " + part.Text;
                LogMessage(text, true);
                return;
            }
            if (partChanged && part != null)
                LogMessage(part.Text, true);
        }

        /// <summary>Leaves montage-mix mode (a single clip is about to be played).</summary>
        private void StopMixPreviewMode()
        {
            _mixPreviewActive = false;
            _mixLastShot = null;
            _followPart = null;
        }

        // ══════════════════════════════════════════════════════════════
        // v1.4 (R5) — VOLUME OVER TIME AND MUSIC UNDER SPEECH
        //   Ctrl+Shift+V  volume point at the playhead (the clip's volume curve)
        //   Menu commands: read the curve, remove it, lower the music under speech
        // ══════════════════════════════════════════════════════════════

        private TimelineItem SelectedClip() =>
            nativeListView?.SelectedItems.Count > 0 ? nativeListView.SelectedItems[0].Tag as TimelineItem : null;

        private void AddVolumePoint_Click(object sender, RoutedEventArgs e)
        {
            var item = _playingItem ?? SelectedClip();
            if (item == null || item.IsImage) { LogMessage(L("vol_select_sound_clip"), true); return; }
            double pos = Math.Clamp(PositionInClip(item), 0, item.Duration);
            var dlg = new VolumeControl(Math.Round(AudioAutomation.VolumeAt(item, pos))) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            SaveState();
            AudioAutomation.SetPoint(item, pos, dlg.Volume);
            LogMessage(LF("vol_point_set", FormatTimeDetailed(pos), Math.Round(dlg.Volume), item.VolumePoints.Count), true);
            PlayBeep();
        }

        private void ReadVolumeCurve_Click(object sender, RoutedEventArgs e)
        {
            var item = SelectedClip() ?? _playingItem;
            if (item == null) { LogMessage(L("vol_select_sound_clip"), true); return; }
            string d = AudioAutomation.Describe(item);
            LogMessage(d == null ? LF("vol_no_curve", Math.Round(item.Volume)) : LF("vol_curve", item.VolumePoints.Count, d), true);
        }

        private void ClearVolumeCurve_Click(object sender, RoutedEventArgs e)
        {
            var item = SelectedClip() ?? _playingItem;
            if (item == null || !AudioAutomation.HasCurve(item)) { LogMessage(L("vol_nothing_to_clear"), true); return; }
            SaveState();
            item.VolumePoints = null;
            if (ReferenceEquals(item, _playingItem) && _mediaPlayer != null) _mediaPlayer.Volume = (int)Math.Clamp(item.Volume, 0, 200);
            LogMessage(LF("vol_cleared", Math.Round(item.Volume)), true);
            PlayBeep();
        }

        /// <summary>
        /// Lowers the music to 30 % wherever speech or another sound of yours plays: other audio clips
        /// (AI sound effects and ambience excluded) and the sound of your own video clips. The music
        /// is the selected audio clip, or the longest one. Ctrl+Z undoes it.
        /// </summary>
        private async void DuckMusic_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedClip();
            var music = sel != null && sel.IsAudio ? sel
                      : timelineItems.Where(i => i.IsAudio && !string.IsNullOrEmpty(i.Path)).OrderByDescending(i => i.Duration).FirstOrDefault();
            if (music == null) { LogMessage(L("duck_no_music"), true); return; }
            RecalculatePositions();

            bool IsEffectOrAmbience(TimelineItem i) =>
                (i.Name ?? "").StartsWith("🔊") || (i.Name ?? "").StartsWith("🌳") || RenderEngine.IsAiCreatorClip(i);
            var voices = timelineItems.Where(i => !ReferenceEquals(i, music) && i.IsAudio && i.Volume > 0 && !IsEffectOrAmbience(i)).ToList();
            foreach (var v in timelineItems.Where(i => i.IsVideo && i.Volume > 0 && !IsEffectOrAmbience(i) &&
                                                       Math.Abs(ClipSpeed(i) - 1.0) < 0.001 && File.Exists(i.Path)))
                if (await ExportHelpers.HasAudioAsync(v.Path, CancellationToken.None)) voices.Add(v);

            if (voices.Count == 0) { LogMessage(L("duck_no_voice"), true); return; }
            SaveState();
            int places = AudioAutomation.DuckUnder(music, voices);
            if (places == 0) { LogMessage(L("duck_no_overlap"), true); return; }
            LogMessage(LF("duck_done", Path.GetFileNameWithoutExtension(music.Name ?? ""), places), true);
            PlayBeep();
        }

        // ══════════════════════════════════════════════════════════════
        // v1.4 (R4) — WHAT IS IN THE PICTURE HERE, FOR EVERY CLIP
        //   Ctrl+Shift+F  describe the frame under the playhead (also your own footage)
        //   Ctrl+Alt+D    describe the selected clip part by part (shot changes); the parts are
        //                 then announced while you move through the clip, and saved in the project
        // ══════════════════════════════════════════════════════════════

        private bool _announceDescriptions = true;
        private bool _frameDescribing = false;
        private CancellationTokenSource _describeCts;

        /// <summary>The clip under the playhead and the position inside it.</summary>
        private (TimelineItem Item, double Pos) ClipAtPlayhead()
        {
            if (_mixPreviewActive)
            {
                var shot = TimelineMixPreview.VideoAt(timelineItems, TimelinePosition);
                return (shot, shot == null ? 0 : TimelinePosition - shot.Start);
            }
            var item = _playingItem ??
                       (nativeListView?.SelectedItems.Count > 0 ? nativeListView.SelectedItems[0].Tag as TimelineItem : null);
            return (item, item == null ? 0 : PositionInClip(item));
        }

        private async void DescribeFrameHere_Click(object sender, RoutedEventArgs e)
        {
            if (_frameDescribing) { LogMessage(L("desc_busy"), true); return; }
            var (item, pos) = ClipAtPlayhead();
            if (item == null || string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
            {
                LogMessage(L("desc_no_picture"), true);
                return;
            }
            if (item.IsAudio) { LogMessage(L("desc_audio_clip"), true); return; }

            pos = Math.Clamp(pos, 0, Math.Max(0, item.Duration - 0.04));
            double fileTime = item.IsImage ? 0 : item.SourceOffset + pos * ClipSpeed(item);
            _frameDescribing = true;
            LogMessage(LF("desc_working", FormatTimeDetailed(pos)), true);
            try
            {
                var r = await FrameDescriber.DescribeFrameAsync(item.Path, fileTime, CancellationToken.None);
                if (r.Text != null)
                    LogMessage(r.CoverOnly ? LF("desc_cover", r.Text) : r.Text, true);
                else if (r.Error == "no_model") LogMessage(L("desc_no_model"), true);
                else if (r.Error == "no_picture") LogMessage(L("desc_sound_only"), true);
                else LogMessage(LF("desc_frame_failed", r.Detail), true);
            }
            catch (Exception ex)
            {
                AppLog.Error("Describing the frame", ex);
                LogMessage(LF("desc_failed", ex.Message), true);
            }
            finally
            {
                _frameDescribing = false;
            }
        }

        private async void DescribeClipParts_Click(object sender, RoutedEventArgs e)
        {
            var item = nativeListView?.SelectedItems.Count > 0 ? nativeListView.SelectedItems[0].Tag as TimelineItem : null;
            if (item == null) { LogMessage(L("select_clip_first_play"), true); return; }
            if (item.IsAudio || string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
            {
                LogMessage(L("desc_audio_clip"), true);
                return;
            }
            await DescribePartsAsync(new List<TimelineItem> { item });
        }

        private async void DescribeAllMyClips_Click(object sender, RoutedEventArgs e)
        {
            var items = timelineItems.Where(i => (i.IsVideo || i.IsImage) && !RenderEngine.IsAiCreatorClip(i) &&
                                                 !string.IsNullOrEmpty(i.Path) && File.Exists(i.Path) &&
                                                 (i.FrameDescriptions == null || i.FrameDescriptions.Count == 0))
                                     .ToList();
            if (items.Count == 0) { LogMessage(L("desc_parts_nothing"), true); return; }
            await DescribePartsAsync(items);
        }

        private void StopDescribing_Click(object sender, RoutedEventArgs e)
        {
            if (_describeCts == null) LogMessage(L("desc_parts_none"), true);
            else _describeCts.Cancel();
        }

        private void ToggleAnnounceDescriptions_Click(object sender, RoutedEventArgs e)
        {
            _announceDescriptions = !_announceDescriptions;
            if (sender is MenuItem mi) mi.IsChecked = _announceDescriptions;
            LogMessage(L(_announceDescriptions ? "desc_follow_on" : "desc_follow_off"), true);
        }

        /// <summary>Describes the clips one after another in the background; editing goes on meanwhile.</summary>
        private async Task DescribePartsAsync(List<TimelineItem> items)
        {
            if (_describeCts != null) { LogMessage(L("desc_parts_running"), true); return; }
            _describeCts = new CancellationTokenSource();
            var ct = _describeCts.Token;
            int done = 0;
            LogMessage(LF("desc_parts_start", items.Count), true);
            try
            {
                foreach (var item in items)
                {
                    string problem = null;
                    bool cover = false;
                    var parts = await FrameDescriber.DescribeByPartsAsync(item,
                        (d, t) => LogMessage(LF("desc_parts_progress", item.Name, d, t), false),
                        p => { if (p == "cover") cover = true; else problem = p; }, ct);
                    if (parts == null) { LogMessage(L("desc_no_model"), true); return; }
                    string name = Path.GetFileNameWithoutExtension(item.Name ?? "");
                    if (parts.Count == 0)
                    {
                        LogMessage(problem == "no_picture" ? LF("desc_clip_sound_only", name)
                                                           : LF("desc_clip_failed", name, problem ?? "?"), true);
                        continue;
                    }
                    item.FrameDescriptions = parts;
                    done++;
                    if (ReferenceEquals(item, _playingItem)) _followPart = null;
                    LogMessage(cover ? LF("desc_clip_cover_done", name, parts[0].Text)
                                     : LF("desc_parts_clip_done", name, parts.Count), true);
                }
                LogMessage(LF("desc_parts_done", done), true);
                PlayBeep();
            }
            catch (OperationCanceledException)
            {
                LogMessage(LF("desc_parts_stopped", done), true);
            }
            catch (Exception ex)
            {
                AppLog.Error("Describing clips by parts", ex);
                LogMessage(LF("desc_failed", ex.Message), true);
            }
            finally
            {
                _describeCts.Dispose();
                _describeCts = null;
            }
        }

        private void AddVideoTrack_Click(object sender, RoutedEventArgs e)
        {
            // v1.4: honest message — the four tracks always exist; nothing was ever "added".
            currentTrackFilter = 1;
            if (cmbTrackSelector != null) cmbTrackSelector.SelectedIndex = 1;
            UpdateTimelineDisplay();
            LogMessage("Video 2 track selected. New videos and images are added to it; Ctrl+Up / Ctrl+Down moves a clip between tracks.", true);
            PlayBeep();
        }

        private void AddAudioTrack_Click(object sender, RoutedEventArgs e)
        {
            currentTrackFilter = 3;
            if (cmbTrackSelector != null) cmbTrackSelector.SelectedIndex = 3;
            UpdateTimelineDisplay();
            LogMessage("Audio 2 track selected. New audio is added to it; Ctrl+Up / Ctrl+Down moves a clip between tracks.", true);
            PlayBeep();
        }

        private void TrackSelector_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (cmbTrackSelector.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                currentTrackFilter = int.Parse(item.Tag.ToString());
                UpdateTimelineDisplay();
                LogMessage($"Showing track: {((ComboBoxItem)cmbTrackSelector.SelectedItem).Content}", true);
            }
        }

        private void ApplyEffects_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                SaveState();
                if (item.VideoEffect == null) item.VideoEffect = new VideoEffectData();
                item.VideoEffect.Brightness = sldBrightness?.Value ?? 0;
                item.VideoEffect.Contrast = sldContrast?.Value ?? 0;
                item.VideoEffect.Blur = sldBlur?.Value ?? 0;
                // v1.4 (R5): the audio effects of the Effects tab are stored on the clip too and
                // reach the render and the montage mix (before, they only coloured the preview)
                if (item.IsAudio || item.IsVideo)
                {
                    item.AudioFx = new AudioSettingsData
                    {
                        BassBoost = sldBass?.Value ?? 0,
                        TrebleBoost = sldTreble?.Value ?? 0,
                        ReverbAmount = sldReverb?.Value ?? 0,
                        CompressorEnabled = chkCompressor?.IsChecked == true
                    };
                }
                LogMessage(LF("fx_applied_summary", item.Name,
                              Math.Round(item.VideoEffect.Brightness), Math.Round(item.VideoEffect.Contrast), Math.Round(item.VideoEffect.Blur),
                              Math.Round(item.AudioFx?.BassBoost ?? 0), Math.Round(item.AudioFx?.TrebleBoost ?? 0), Math.Round(item.AudioFx?.ReverbAmount ?? 0)), true);
                PlayBeep();
            }
            else LogMessage("Select a clip first", true);
        }

        private void UpdateAudioEffects()
        {
            if (_equalizer == null) return;
            try
            {
                float bassGain = (float)(sldBass.Value / 2.0);
                _equalizer.SetAmp(bassGain, 0);
                _equalizer.SetAmp(bassGain * 0.7f, 1);
                float trebleGain = (float)(sldTreble.Value / 2.0);
                _equalizer.SetAmp(trebleGain, 7);
                _equalizer.SetAmp(trebleGain * 0.9f, 8);
                _equalizer.SetAmp(trebleGain * 0.8f, 9);
            }
            catch (Exception swallowed) { AppLog.Error("Applying the equalizer", swallowed); }   // v1.4: was silent
        }

        private void AudioEffect_ValueChanged(object sender, RoutedEventArgs e) => UpdateAudioEffects();

        private async void PreviewBass_Click(object sender, RoutedEventArgs e) => await PreviewAudioEffect(sldBass.Value, 0, 0);
        private async void PreviewTreble_Click(object sender, RoutedEventArgs e) => await PreviewAudioEffect(0, sldTreble.Value, 0);
        private async void PreviewReverb_Click(object sender, RoutedEventArgs e) => await PreviewAudioEffect(0, 0, sldReverb.Value);

        private async Task PreviewAudioEffect(double bass, double treble, double reverb)
        {
            if (nativeListView?.SelectedItems.Count == 0 || !(nativeListView.SelectedItems[0].Tag is TimelineItem item) || !item.IsAudio)
            {
                LogMessage(L("select_audio_clip2"), true);
                return;
            }
            if (!File.Exists(item.Path))
            {
                LogMessage("Audio file does not exist", true);
                return;
            }
            try
            {
                LogMessage(string.Format(L("previewing_audio"), previewDurationSeconds), true);
                double oldBass = sldBass.Value;
                double oldTreble = sldTreble.Value;
                double oldReverb = sldReverb.Value;
                bool oldCompressor = chkCompressor.IsChecked == true;
                if (bass > 0) sldBass.Value = bass;
                if (treble > 0) sldTreble.Value = treble;
                if (reverb > 0) sldReverb.Value = reverb;
                UpdateAudioEffects();
                long savedPosition = 0;
                bool wasPlaying = false;
                if (_currentMedia != null)
                {
                    savedPosition = _mediaPlayer.Time;
                    wasPlaying = isPlaying;
                    if (wasPlaying) _mediaPlayer.Pause();
                }
                using (var previewMedia = new Media(_libVLC, item.Path))
                {
                    _mediaPlayer.Media = previewMedia;
                    _mediaPlayer.Volume = (int)item.Volume;
                    _mediaPlayer.Time = 0;
                    _mediaPlayer.Play();
                    await Task.Delay(previewDurationSeconds * 1000);
                    _mediaPlayer.Pause();
                }
                sldBass.Value = oldBass;
                sldTreble.Value = oldTreble;
                sldReverb.Value = oldReverb;
                chkCompressor.IsChecked = oldCompressor;
                UpdateAudioEffects();
                if (_currentMedia != null)
                {
                    _mediaPlayer.Media = _currentMedia;
                    _mediaPlayer.Time = savedPosition;
                    if (wasPlaying) _mediaPlayer.Play();
                }
                LogMessage(L("preview_done"), true);
            }
            catch (Exception ex)
            {
                LogMessage(string.Format(L("generic_error"), ex.Message), true);
            }
        }

        private void GPUAcceleration_Checked(object sender, RoutedEventArgs e)
        {
            useGPUAcceleration = chkGPUAcceleration.IsChecked == true;
            LogMessage(useGPUAcceleration ? L("gpu_on2") : L("gpu_off2"), true);
        }

        private void LoadTransitions()
        {
            if (lstTransitions == null) return;
            lstTransitions.Items.Clear();
            lstTransitions.Items.Add(new TransitionEffect { Name = "Fade In/Out", Type = TransitionType.Fade });
            lstTransitions.Items.Add(new TransitionEffect { Name = "Crossfade", Type = TransitionType.Crossfade });
            lstTransitions.Items.Add(new TransitionEffect { Name = "Slide Left", Type = TransitionType.SlideLeft });
            lstTransitions.Items.Add(new TransitionEffect { Name = "Slide Right", Type = TransitionType.SlideRight });
            lstTransitions.Items.Add(new TransitionEffect { Name = "Slide Up", Type = TransitionType.SlideUp });
            lstTransitions.Items.Add(new TransitionEffect { Name = "Slide Down", Type = TransitionType.SlideDown });
            lstTransitions.Items.Add(new TransitionEffect { Name = "Wipe Left", Type = TransitionType.WipeLeft });
            lstTransitions.Items.Add(new TransitionEffect { Name = "Wipe Right", Type = TransitionType.WipeRight });
            lstTransitions.Items.Add(new TransitionEffect { Name = "Zoom In", Type = TransitionType.ZoomIn });
            lstTransitions.Items.Add(new TransitionEffect { Name = "Zoom Out", Type = TransitionType.ZoomOut });
        }

        private void SetupDragDrop()
        {
            // Drag drop za Win32 ListView
        }

        private void Transition_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (lstTransitions.SelectedItem is TransitionEffect transition)
            {
                WpfDragDrop.DoDragDrop(lstTransitions, transition, WpfDragDropEffects.Copy);
            }
        }

        private void Timeline_Drop(object sender, WpfDragEventArgs e)
        {
            if (e.Data.GetData(typeof(TransitionEffect)) is TransitionEffect transition)
            {
                WpfPoint dropPoint = e.GetPosition(wfhTimeline);
                int dropIndex = GetDropIndex(dropPoint);
                if (dropIndex >= 0 && dropIndex < timelineItems.Count - 1)
                {
                    var t = new TransitionEffect { Name = transition.Name, Type = transition.Type, Duration = transitionDuration, ClipIndex1 = dropIndex, ClipIndex2 = dropIndex + 1 };
                    transitions.RemoveAll(x => x.ClipIndex1 == dropIndex || x.ClipIndex2 == dropIndex + 1);
                    transitions.Add(t);
                    SetClipTransition(timelineItems[dropIndex], t);
                    LogMessage(string.Format(L("transition_added"), transition.Name, dropIndex + 1, dropIndex + 2), true);
                    PlayBeep();
                }
                else
                {
                    LogMessage(L("transition_only_between"), true);
                }
            }
        }

        private int GetDropIndex(WpfPoint dropPoint)
        {
            if (nativeListView == null) return -1;
            int index = (int)(dropPoint.Y / 30);
            return Math.Clamp(index, 0, timelineItems.Count - 1);
        }

        private void sldTransitionDuration_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            transitionDuration = sldTransitionDuration.Value;
            if (accessibilityMode) LogMessage($"Transition duration: {transitionDuration:F1} seconds", true);
        }

        private void Transition_Selected(object sender, SelectionChangedEventArgs e)
        {
            if (lstTransitions.SelectedItem is TransitionEffect transition)
            {
                LogMessage(string.Format(L("effect_drag_tip"), transition.Name), true);
            }
        }

        private void RemoveTransition_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                int idx = timelineItems.IndexOf(item);
                foreach (var t in transitions.Where(x => x.ClipIndex1 == idx || x.ClipIndex2 == idx).ToList())
                    ClearClipTransition(t.ClipIndex1);
                item.TransitionOut = null; item.TransitionOutDuration = 0;
                transitions.RemoveAll(t => t.ClipIndex1 == idx || t.ClipIndex2 == idx);
                LogMessage("Tranzicija uklonjena", true);
                PlayBeep();
                UpdateTransitionIndicators();
            }
            else
            {
                LogMessage("Select the first clip of the pair to remove the transition", true);
            }
        }

        private void Keyframe_Selected(object sender, SelectionChangedEventArgs e)
        {
            if (lstKeyframes.SelectedItem is AnimationKeyframe kf)
            {
                currentEditingKeyframe = kf;
                sldZoom.Value = kf.Zoom;
                sldRotation.Value = kf.Rotation;
                sldX.Value = kf.X;
                sldY.Value = kf.Y;
                sldOpacity.Value = kf.Opacity;
                UpdateAnimValueDisplays();
                LogMessage(LanguageManager.F("loc_keyframe_at", FormatTime(kf.Time)), true);
            }
        }

        private void AnimValue_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateAnimValueDisplays();

        private void UpdateAnimValueDisplays()
        {
            if (txtZoomValue != null) txtZoomValue.Text = $"{sldZoom.Value:F2}";
            if (txtRotationValue != null) txtRotationValue.Text = $"{sldRotation.Value:F0}°";
            if (txtXValue != null) txtXValue.Text = $"{sldX.Value:F0}";
            if (txtYValue != null) txtYValue.Text = $"{sldY.Value:F0}";
            if (txtOpacityValue != null) txtOpacityValue.Text = $"{sldOpacity.Value:F2}";
        }

        private void AddKeyframe_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count == 0 || !(nativeListView.SelectedItems[0].Tag is TimelineItem item))
            {
                LogMessage("Select a clip first", true);
                return;
            }
            double time = PositionInClip(item);   // v1.4: was "player time − clip start"
            if (time < 0) time = 0;
            if (time > item.Duration) time = item.Duration;
            var kf = new AnimationKeyframe
            {
                Time = time,
                Zoom = sldZoom.Value,
                Rotation = sldRotation.Value,
                X = sldX.Value,
                Y = sldY.Value,
                Opacity = sldOpacity.Value
            };
            item.AddKeyframe(kf);
            UpdateKeyframeList(item);
            LogMessage($"Keyframe added at {FormatTime(time)}", true);
            PlayBeep();
        }
        private void AddKenBurnsKeyframes(TimelineItem item, double duration)
        {
            item.Keyframes.Clear();
            item.Keyframes.Add(new AnimationKeyframe { Time = 0, Zoom = 1.0, X = 0, Y = 0 });
            item.Keyframes.Add(new AnimationKeyframe { Time = duration, Zoom = 1.2, X = 50, Y = 30 });
        }
        private void RemoveKeyframe_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count == 0 || !(nativeListView.SelectedItems[0].Tag is TimelineItem item)) return;
            if (lstKeyframes.SelectedItem is AnimationKeyframe kf)
            {
                item.RemoveKeyframeAt(kf.Time);
                UpdateKeyframeList(item);
                LogMessage("Keyframe uklonjen", true);
                PlayBeep();
            }
        }

        private void ApplyKeyframe_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count == 0 || !(nativeListView.SelectedItems[0].Tag is TimelineItem item)) return;
            if (currentEditingKeyframe != null)
            {
                currentEditingKeyframe.Zoom = sldZoom.Value;
                currentEditingKeyframe.Rotation = sldRotation.Value;
                currentEditingKeyframe.X = sldX.Value;
                currentEditingKeyframe.Y = sldY.Value;
                currentEditingKeyframe.Opacity = sldOpacity.Value;
                UpdateKeyframeList(item);
                LogMessage(L("keyframe_updated"), true);
            }
            else
            {
                AddKeyframe_Click(sender, e);
            }
            PlayBeep();
        }

        private void PreviewAnimation_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count == 0 || !(nativeListView.SelectedItems[0].Tag is TimelineItem item))
            {
                LogMessage("Select a clip to preview the animation", true);
                return;
            }
            LogMessage(L("animation_preview_note"), true);
        }

        private void UpdateKeyframeList(TimelineItem item)
        {
            if (lstKeyframes == null) return;
            lstKeyframes.Items.Clear();
            foreach (var kf in item.Keyframes.OrderBy(k => k.Time))
            {
                lstKeyframes.Items.Add(kf);
            }
            if (txtSelectedClip != null)
                txtSelectedClip.Text = $"Selected clip: {item.Name} (Keyframes: {item.Keyframes.Count})";
        }

        private double GetAudioDuration(string filePath)
        {
            try
            {
                using (var media = new Media(_libVLC, filePath))
                {
                    media.AddOption("play-and-exit");
                    _ = media.Parse(MediaParseOptions.ParseNetwork);
                    Thread.Sleep(300);
                    return media.Duration / 1000.0;
                }
            }
            catch { return 5.0; }
        }

        /// <summary>v1.4 — splits text into pieces of at most <paramref name="max"/> characters at sentence or word boundaries.</summary>
        private static IEnumerable<string> SplitForTts(string text, int max)
        {
            var words = (text ?? "").Replace("\r", " ").Replace("\n", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();
            foreach (var w in words)
            {
                if (sb.Length > 0 && sb.Length + 1 + w.Length > max)
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(w.Length > max ? w.Substring(0, max) : w);
                if ((w.EndsWith(".") || w.EndsWith("!") || w.EndsWith("?")) && sb.Length > max * 0.6)
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
            }
            if (sb.Length > 0) yield return sb.ToString();
        }

        private async void GenerateVoice_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(currentProjectFolder)) { WpfMessageBox.Show(L("save_project_first2")); return; }
            if (string.IsNullOrWhiteSpace(txtVoiceText.Text)) { LogMessage("Enter text first", true); return; }
            btnGenerateVoice.IsEnabled = false;
            prgVoice.Visibility = Visibility.Visible;
            txtVoiceStatus.Text = "Generating...";
            LogMessage("Generating AI voice...", true);
            string lang = rbSrb.IsChecked == true ? "sr" : "en";
            string voiceoverPath = Path.Combine(currentProjectFolder, "AI_Voiceover.mp3");
            try
            {
                // v1.4: the free TTS endpoint accepts only ~200 characters per request — longer
                // texts failed completely. The text is now split at sentence/word boundaries and
                // the MP3 pieces are joined (MP3 frames can simply be concatenated).
                // A unique file name per generation (it used to overwrite the previous voice).
                voiceoverPath = Path.Combine(currentProjectFolder, $"AI_Voiceover_{DateTime.Now:yyyyMMdd_HHmmss}.mp3");
                using (var client = new HttpClient())
                using (var output = new MemoryStream())
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
                    foreach (string chunk in SplitForTts(txtVoiceText.Text, 190))
                    {
                        string url = $"https://translate.google.com/translate_tts?ie=UTF-8&q={Uri.EscapeDataString(chunk)}&tl={lang}&client=tw-ob";
                        var audioData = await client.GetByteArrayAsync(url);
                        output.Write(audioData, 0, audioData.Length);
                    }
                    File.WriteAllBytes(voiceoverPath, output.ToArray());
                }
                double voiceDuration = await GetMediaDurationWithTimeoutAsync(voiceoverPath, TimeSpan.FromSeconds(15));
                SaveState();
                timelineItems.Add(new TimelineItem { Path = voiceoverPath, Duration = voiceDuration, Name = "AI Voice", Type = "Audio", Volume = 100, TrackIndex = 3, VideoEffect = new VideoEffectData() });
                UpdateTimelineDisplay();
                LogMessage("AI voice generated", true);
                txtVoiceStatus.Text = L("voice_done");
                PlayBeep();
            }
            catch (Exception ex) { LogMessage(string.Format(L("generic_error"), ex.Message), true); }
            finally { btnGenerateVoice.IsEnabled = true; prgVoice.Visibility = Visibility.Collapsed; }
        }
        private async void GenerateAI_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(currentProjectFolder)) { WpfMessageBox.Show(L("save_project_first2")); return; }
            if (string.IsNullOrWhiteSpace(txtAIPrompt?.Text)) { LogMessage("Enter an image description", true); return; }
            string apiToken = GetCloudflareApiKey();
            if (string.IsNullOrEmpty(apiToken))
            {
                var dialog = new ApiKeyDialog("cloudflare", LanguageManager.T("loc_cf_token_images"));
                if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.ApiKey))
                {
                    apiToken = dialog.ApiKey;
                    SaveCloudflareApiKey(apiToken);
                    LogMessage(L("cf_key_saved"), true);
                }
                else
                {
                    LogMessage(L("cf_key_cancelled"), true);
                    return;
                }
            }
            string accountId = GetCloudflareAccountId();
            if (string.IsNullOrEmpty(accountId)) { LogMessage("Cloudflare Account ID is required for image generation.", true); return; }
            btnGenerate.IsEnabled = false;
            prgAI.Visibility = Visibility.Visible;
            txtAIStatus.Text = "Generating via Cloudflare...";
            LogMessage(LanguageManager.T("loc_cf_generating"), true);
            string[] prompts = txtAIPrompt.Text.Split(',');
            int generated = 0;
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiToken}");
                for (int idx = 0; idx < prompts.Length; idx++)
                {
                    string cleanP = prompts[idx].Trim();
                    if (string.IsNullOrEmpty(cleanP)) continue;
                    txtAIStatus.Text = $"{idx + 1}/{prompts.Length}: {cleanP} (Cloudflare)";
                    string img = Path.Combine(currentProjectFolder, $"AI_{Guid.NewGuid().ToString().Substring(0, 8)}.jpg");
                    try
                    {
                        string url = $"https://api.cloudflare.com/client/v4/accounts/{accountId}/ai/run/@cf/black-forest-labs/flux-1-schnell";
                        var requestBody = new { prompt = cleanP };
                        var content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");
                        var response = await client.PostAsync(url, content);
                        var responseBody = await response.Content.ReadAsStringAsync();
                        if (response.IsSuccessStatusCode)
                        {
                            var result = JsonConvert.DeserializeObject<CloudflareImageResponse>(responseBody);
                            if (result?.result?.image != null)
                            {
                                byte[] imageBytes = Convert.FromBase64String(result.result.image);
                                await File.WriteAllBytesAsync(img, imageBytes);
                                SaveState();
                                var dialog = new DurationDialog(5.0);
                                double duration = dialog.ShowDialog() == true ? dialog.Duration : 5.0;
                                LogMessage("Generating description for: " + cleanP + "...", false);
                                string imgDesc = await GenerateImageDescriptionAsync(img, cleanP);
                                if (string.IsNullOrEmpty(imgDesc)) imgDesc = LanguageManager.F("loc_ai_image_desc", cleanP);

                                timelineItems.Add(new TimelineItem
                                {
                                    Path = img,
                                    Duration = duration,
                                    Name = "AI: " + cleanP,
                                    AudioDescription = imgDesc,
                                    Type = "Image",
                                    Volume = 100,
                                    TrackIndex = 0,
                                    VideoEffect = new VideoEffectData()
                                });
                                generated++;
                                LogMessage("Image " + (idx + 1) + " ready. Description: " + imgDesc, true);
                            }
                            else
                            {
                                LogMessage(string.Format(L("cf_no_image"), cleanP), true);
                            }
                        }
                        else
                        {
                            LogMessage(string.Format(L("cf_api_error"), response.StatusCode), true);
                            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) break;
                        }
                    }
                    catch (Exception ex) { LogMessage(string.Format(L("cf_gen_error"), cleanP, ex.Message), true); }
                    if (prgAI != null) prgAI.Value = (idx + 1) * 100 / prompts.Length;
                    await Task.Delay(500);
                }
            }
            UpdateTimelineDisplay();
            btnGenerate.IsEnabled = true;
            prgAI.Visibility = Visibility.Collapsed;
            txtAIStatus.Text = string.Format(L("frames_done"), generated, prompts.Length);
            LogMessage($"AI frames generated: {generated}/{prompts.Length}", true);
            PlayBeep();
        }

        private void AudioClip_Selected(object sender, SelectionChangedEventArgs e)
        {
            if (cmbAudioClips.SelectedItem is TimelineItem item && item.IsAudio)
            {
                selectedAudioPath = item.Path;
                LogMessage($"Selected audio clip: {item.Name}", true);
            }
        }

        private void TranscribeAudio_Selected(object sender, SelectionChangedEventArgs e)
        {
            if (cmbTranscribeAudio.SelectedItem is TimelineItem item && item.IsAudio)
            {
                selectedTranscribeAudioPath = item.Path;
                LogMessage($"Selected audio for transcription: {item.Name}", true);
            }
        }

        private async void TranscribeAudio_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(selectedTranscribeAudioPath))
            {
                LogMessage("Select an audio file to transcribe", true);
                return;
            }

            if (!AITranscription.IsWhisperAvailable())
            {
                LogMessage("faster-whisper-xxl not found. Place faster-whisper-xxl.exe next to UltraVideoEditor.exe.", true);
                WpfMessageBox.Show(
                    "faster-whisper-xxl not found.\n\n" +
                    "Preuzmi faster-whisper-xxl.exe i postavi ga pored UltraVideoEditor.exe.\n\n" +
                    "Besplatno: https://github.com/Purfview/whisper-standalone-win/releases",
                    "Whisper is not installed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            btnTranscribe.IsEnabled = false;
            txtTranscriptionResult.Text = "Transcription in progress (large-v3)...";
            LogMessage(L("transcription_running"), true);
            try
            {
                string language = rbSrb.IsChecked == true ? "sr" : (rbEng.IsChecked == true ? "en" : "de");
                string ffmpegPath = AppPaths.FFmpeg;
                var transcriptionResult = await AITranscription.TranscribeAsync(
                    selectedTranscribeAudioPath,
                    language: language,
                    ffmpegPath: ffmpegPath,
                    modelSize: "large-v3");
                if (!transcriptionResult.Success)
                {
                    // v1.4: an error message used to be offered as subtitles
                    txtTranscriptionResult.Text = transcriptionResult.ErrorMessage;
                    LogMessage(transcriptionResult.ErrorMessage, true);
                    return;
                }
                // v1.4: when the song's lyrics are typed in the lyrics box, they replace what
                // Whisper wrote (sung songs are often misheard: "moja đele" for "moj Anđele").
                // Whisper is then only used for the timing; repeated choruses get their own lines.
                var typedLyrics = (txtLyrics?.Text ?? "")
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                if (typedLyrics.Count > 0 && transcriptionResult.WordTimings.Count > 0)
                {
                    if (AITranscription.TryApplySungAlignment(typedLyrics, transcriptionResult.WordTimings, transcriptionResult))
                    {
                        transcriptionResult.FullText = string.Join("\n", transcriptionResult.Lines.Select(l => l.Text));
                        LogMessage(string.Format(L("transcription_used_lyrics"), transcriptionResult.Lines.Count), true);
                    }
                    else
                    {
                        LogMessage(L("transcription_lyrics_not_matched"), true);
                    }
                }
                string result = transcriptionResult.FullText;
                txtTranscriptionResult.Text = result;
                LogMessage(L("transcription_done2"), true);
                if (WpfMessageBox.Show(L("add_as_subtitles"), L("add_subtitles_title"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    // v1.4: use Whisper's real timestamps for each line (the text used to be
                    // split into sentences and spread evenly over the file, ignoring the timing).
                    // Times are shifted by where that audio clip sits on the timeline.
                    var audioOnTimeline = timelineItems.FirstOrDefault(i => i.Path == selectedTranscribeAudioPath);
                    double offset = audioOnTimeline?.Start ?? 0;
                    SaveState();
                    int added = 0;
                    foreach (var line in transcriptionResult.Lines)
                    {
                        if (string.IsNullOrWhiteSpace(line.Text)) continue;
                        double s = offset + line.StartSeconds, en = offset + Math.Max(line.EndSeconds, line.StartSeconds + 0.5);
                        subtitles.Add(new SubtitleItem { Text = line.Text.Trim(), Start = s, End = en });
                        lstSubtitles.Items.Add($"{FormatTime(s)} -> {FormatTime(en)}: {line.Text.Trim()}");
                        added++;
                    }
                    LogMessage($"Added {added} subtitles from the transcript", true);
                }
            }
            catch (Exception ex)
            {
                txtTranscriptionResult.Text = string.Format(L("generic_error"), ex.Message);
                LogMessage(string.Format(L("at_error"), ex.Message), true);
            }
            finally { btnTranscribe.IsEnabled = true; }
        }

        private async void SyncLyrics_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(selectedAudioPath)) { LogMessage("Select an audio clip first", true); return; }
            if (string.IsNullOrWhiteSpace(txtLyrics.Text)) { LogMessage("Enter the song lyrics", true); return; }
            btnSyncLyrics.IsEnabled = false;
            prgSync.Visibility = Visibility.Visible;
            txtSyncStatus.Text = "Synchronizing...";
            LogMessage("Synchronizing lyrics...", true);
            try
            {
                string[] lines = txtLyrics.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var lyrics = lines.Where(l => !string.IsNullOrWhiteSpace(l.Trim())).ToList();
                if (lyrics.Count == 0) { LogMessage("No valid lyric lines", true); return; }
                double duration = await GetMediaDurationWithTimeoutAsync(selectedAudioPath, TimeSpan.FromSeconds(15));
                syncedSubtitles.Clear();

                // SYNC FIX: Attempting Whisper transcription for accurate audio timestamps.
                // Whisper returns START/END of each line directly from the audio waveform,
                // which eliminates "theoretical timing" drift.
                // Fallback: even distribution if Whisper is unavailable or finds no text.
                bool usedAlignment = false;
                if (AITranscription.IsWhisperAvailable())
                {
                    try
                    {
                        // FORCED ALIGNMENT: user has already provided text, Whisper only measures
                        // where each line is in the audio. No transcription, no guessing.
                        // Result: millisecond-accurate timestamps that follow the real voice.
                        txtSyncStatus.Text = "Whisper is listening and aligning text...";
                        LogMessage("🎵 Forced alignment: Whisper detecting where each line begins...", true);
                        string ffmpegPath = AppPaths.FFmpeg;

                        var alignResult = await AITranscription.ForcedAlignAsync(
                            audioPath:   selectedAudioPath,
                            userLines:   lyrics,
                            // v1.4: the SONG's language (the radio buttons), not the app's UI language
                            language:    rbSrb.IsChecked == true ? "sr" : (rbEng.IsChecked == true ? "en" : "de"),
                            ffmpegPath:  ffmpegPath,
                            modelSize:   "large-v3",
                            progress:    new Progress<string>(msg => Dispatcher.Invoke(() => txtSyncStatus.Text = msg)),
                            // v1.4: every repeat of the chorus gets its own subtitle, even when
                            // the chorus is typed only once
                            allowRepeats: true);

                        if (alignResult.Success && alignResult.Lines.Count > 0)
                        {
                            foreach (var line in alignResult.Lines)
                                syncedSubtitles.Add(new AISubtitle
                                {
                                    Text  = line.Text,
                                    Start = line.StartSeconds,
                                    End   = line.EndSeconds
                                });
                            usedAlignment = true;
                            LogMessage($"✅ Forced alignment: {alignResult.Lines.Count} lines aligned with the voice", true);

                            // Proslijedi word-level timestamps AIVideoCreatoru
                            if (alignResult.WordTimings?.Count > 0)
                            {
                                var creator = FindAIVideoCreator();
                                if (creator != null)
                                {
                                    creator.SetWordTimings(alignResult.WordTimings);
                                    LogMessage($"🔤 Word-level sync: {alignResult.WordTimings.Count} words", true);
                                }
                            }
                        }
                        else
                        {
                            LogMessage($"⚠ Alignment failed ({alignResult.ErrorMessage}) — using even distribution", true);
                        }
                    }
                    catch (Exception alignEx)
                    {
                        LogMessage($"⚠ Alignment error: {alignEx.Message} — falling back to even distribution", true);
                    }
                }

                if (!usedAlignment)
                {
                    // Fallback: ravnomjerna podjela
                    double timePerLine = duration / Math.Max(1, lyrics.Count);
                    for (int i = 0; i < lyrics.Count; i++)
                        syncedSubtitles.Add(new AISubtitle { Text = lyrics[i], Start = i * timePerLine, End = (i + 1) * timePerLine });
                    LogMessage("Sync: uniform distribution (install Whisper for audio-precise sync)", true);
                }

                lstAutoSubtitles.Items.Clear();
                foreach (var sub in syncedSubtitles)
                    lstAutoSubtitles.Items.Add($"[{FormatTime(sub.Start)} -> {FormatTime(sub.End)}] {sub.Text}");
                string method = usedAlignment ? " (forced alignment ✅)" : " (ravnomjerna podjela)";
                txtSyncStatus.Text = string.Format(L("subtitles_done"), syncedSubtitles.Count) + method;
                LogMessage(string.Format(L("subtitles_done"), syncedSubtitles.Count) + method, true);
                PlayBeep();
            }
            catch (Exception ex) { LogMessage(string.Format(L("generic_error"), ex.Message), true); }
            finally { btnSyncLyrics.IsEnabled = true; prgSync.Visibility = Visibility.Collapsed; }
        }

        private void AddSyncedSubtitles_Click(object sender, RoutedEventArgs e)
        {
            if (syncedSubtitles.Count == 0) { LogMessage("No subtitles to add", true); return; }
            SaveState();
            foreach (var sub in syncedSubtitles)
            {
                subtitles.Add(new SubtitleItem { Text = sub.Text, Start = sub.Start, End = sub.End });
                lstSubtitles.Items.Add($"{FormatTime(sub.Start)} -> {FormatTime(sub.End)}: {sub.Text}");
            }
            LogMessage($"Added {syncedSubtitles.Count} subtitles", true);
            PlayBeep();
        }

        private void OpenPhase3_Click(object sender, RoutedEventArgs e)
        {
            // Phase3Dialog zahteva HighlightResult iz prethodne analize
            // Ako je pokrenut kroz Highlight Engine, _lastHighlightResult je setovan
            if (_lastHighlightResult == null || !_lastHighlightResult.Success)
            {
                System.Windows.MessageBox.Show(
                    "Run AI Highlight Engine (Ctrl+Shift+H) and complete the analysis before Phase 3.",
                    "Phase 3", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }
            var dlg = new Phase3Dialog(
                _lastHighlightResult,
                _lastHighlightSourceVideo ?? "",
                _lastHighlightMusicPath   ?? "")
            { Owner = this };
            dlg.ShowDialog();
        }

        // ── Batch Export ─────────────────────────────────────────────
        private void OpenBatchExport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new BatchExportDialog { Owner = this };
            dlg.ShowDialog();
        }

        // ── Project Templates ────────────────────────────────────────
        private void OpenProjectTemplates_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new ProjectTemplateDialog { Owner = this };
            dlg.OnApply = ApplyTemplate;
            dlg.ShowDialog();
        }

        private void ApplyTemplate(ProjectTemplate t)
        {
            SaveState();   // v1.4: snapshot before the change (it was taken after, so Undo did nothing)
            // Apply template settings to MainWindow state
            if (!string.IsNullOrEmpty(t.Language))
            {
                _currentLanguage = t.Language;
                ApplyLanguage();
            }

            // Color grade na sve video klipoive
            if (!string.IsNullOrEmpty(t.ColorGradePreset) && t.ColorGradePreset != "Auto")
            {
                if (System.Enum.TryParse<GradePreset>(t.ColorGradePreset, out var preset))
                {
                    string filter = ColorGradingEngine.GetFilterForPreset(preset);
                    foreach (var item in timelineItems.Where(i => i.IsVideoTrack))
                    {
                        string existing = System.Text.RegularExpressions.Regex.Replace(
                            item.ContentTag ?? "", @"\[grade:[^\]]*\]", "").Trim();
                        item.ContentTag = string.IsNullOrEmpty(existing)
                            ? $"[grade:{filter}]"
                            : $"{existing} [grade:{filter}]";
                    }
                }
            }
            UpdateTimelineDisplay();
            LogMessage($"Template \"{t.Name}\" primenjen.", true);
        }

        // ── Faza 4C: Color Grading Engine ───────────────────────────
        private void OpenColorGrading_Click(object sender, RoutedEventArgs e)
        {
            var videoItems = timelineItems.Where(i => i.IsVideoTrack).ToList();
            if (videoItems.Count == 0)
            {
                System.Windows.MessageBox.Show(
                    "No video clips on the timeline.",
                    "Color Grading", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }
            SaveState();   // v1.4: before the dialog — it writes the grade into the clips directly
            var dlg = new ColorGradingDialog(videoItems) { Owner = this };
            if (dlg.ShowDialog() != true) { DiscardLastUndo(); return; }
            {
                UpdateTimelineDisplay();
                LogMessage("Color Grading: grade applied to the timeline clips.", true);
            }
        }

        // ── Faza 4A: Smart Scene Detection ──────────────────────────
        private void OpenSceneDetection_Click(object sender, RoutedEventArgs e)
        {
            // v1.4: snapshot BEFORE the dialog changes the timeline, so Undo removes the scenes
            SaveState();
            var dlg = new SceneDetectionDialog { Owner = this };
            if (dlg.ShowDialog() != true) { DiscardLastUndo(); return; }
            {
                var added = dlg.SelectedScenes;
                if (added?.Count > 0)
                {
                    UpdateTimelineDisplay();
                    LogMessage($"Smart Scene Detection: added {added.Count} scenes to the timeline.", true);
                }
            }
        }

        // ── Faza 4B: Timeline AI Asistent ────────────────────────────
        private void OpenTimelineAI_Click(object sender, RoutedEventArgs e)
        {
            if (timelineItems.Count == 0)
            {
                System.Windows.MessageBox.Show(
                    "Timeline is empty. Add clips before using the AI Assistant.",
                    "Timeline AI Asistent",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }
            SaveState();   // v1.4: before the dialog — it replaces the timeline itself
            // v1.4: the assistant works on copies, so trying commands and then pressing Cancel
            // leaves the timeline untouched (sorting used to change the real clips right away).
            var dlg = new TimelineAIAssistantDialog(timelineItems.Select(DeepCloneTimelineItem).ToList()) { Owner = this };
            if (dlg.ShowDialog() != true) { DiscardLastUndo(); return; }
            {
                timelineItems.Clear();
                foreach (var item in dlg.ResultItems)
                    timelineItems.Add(item);
                UpdateTimelineDisplay();
                LogMessage($"Timeline AI Assistant: changes applied. Clips: {timelineItems.Count}", true);
            }
        }

        private void OpenHighlightEngine_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new HighlightCreatorDialogV2 { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                // Segmenti su dostupni ako ih treba rucno obraditi
                var segments = dlg.ResultSegments;
                if (segments?.Count > 0)
                {
                    SaveState();
                    RecalculatePositions();
                    double cursor = timelineItems.Where(t => t.TrackIndex == 0).Select(t => t.End).DefaultIfEmpty(0).Max();
                    foreach (var seg in segments.OrderBy(s => s.Order))
                    {
                        // v1.4: the segment's position IN THE SOURCE goes to SourceOffset (it was
                        // put into Start, which is the timeline position — so every highlight was
                        // rendered from the beginning of the source video).
                        var item = new TimelineItem
                        {
                            Path             = seg.SourcePath,
                            SourceOffset     = seg.SourceStart,
                            Duration         = seg.Duration,
                            Name             = $"Highlight {seg.Order:D2}",
                            Type             = "Video",
                            TrackIndex       = 0,
                            FixedPosition    = cursor,
                            UseFixedPosition = true,
                            VideoEffect      = new VideoEffectData()
                        };
                        timelineItems.Add(item);
                        cursor += seg.Duration;
                    }
                    UpdateTimelineDisplay();
                    LogMessage($"Added {segments.Count} highlight segments to the timeline.", true);
                    PlayBeep();
                    // Save for Phase 3
                    _lastHighlightResult      = dlg.PublicResult;
                    _lastHighlightSourceVideo = dlg.ResultSegments.FirstOrDefault()?.SourcePath;
                    _lastHighlightMusicPath   = "";  // setuje se u Phase3Dialog
                }
            }
        }

        private void AddSubtitle_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtSubtitleText?.Text)) { LogMessage("Enter subtitle text", true); return; }
            bool startOk = double.TryParse(txtSubStart?.Text?.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double start);
            bool endOk = double.TryParse(txtSubEnd?.Text?.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double end);
            if (!startOk || !endOk) { LogMessage(L("time_format_error"), true); return; }
            subtitles.Add(new SubtitleItem { Text = txtSubtitleText.Text, Start = start, End = end });
            lstSubtitles.Items.Add($"{FormatTime(start)} -> {FormatTime(end)}: {txtSubtitleText.Text}");
            txtSubtitleText.Clear();
            LogMessage("Subtitle added", true);
            PlayBeep();
        }

        private void ClearSubtitles_Click(object sender, RoutedEventArgs e)
        {
            subtitles.Clear();
            lstSubtitles.Items.Clear();
            LogMessage("Subtitles deleted", true);
            PlayBeep();
        }

        private void ShowExportOptions_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ExportOptions(currentExportSettings);
            if (dialog.ShowDialog() == true)
            {
                currentExportSettings = dialog.Settings;
                LogMessage($"Export options: {currentExportSettings.Format}, {currentExportSettings.Quality}", true);
            }
        }

        private async void AddVideoImage_Click(object sender, RoutedEventArgs e)
        {
            var d = new WpfOpenFileDialog { Multiselect = true, Filter = "Video and images|*.mp4;*.avi;*.mov;*.mkv;*.jpg;*.png;*.jpeg;*.bmp" };
            if (d.ShowDialog() == true)
            {
                SaveState();
                int targetTrack = currentTrackFilter >= 0 && currentTrackFilter <= 1 ? currentTrackFilter : 0;
                foreach (var f in d.FileNames)
                {
                    if (!MediaLoader.IsValidMediaFile(f)) { LogMessage(string.Format(L("not_valid_media"), Path.GetFileName(f)), true); continue; }
                    try
                    {
                        double duration = 5.0;
                        string type = "Video";
                        string ext = Path.GetExtension(f).ToLower();
                        if (ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".bmp") { type = "Image"; duration = 5.0; }
                        else duration = await GetMediaDurationWithTimeoutAsync(f, TimeSpan.FromSeconds(30));
                        string autoDesc = "";
                        if (type == "Image")
                        {
                            LogMessage("Generating description for " + Path.GetFileName(f) + "...", false);
                            autoDesc = await GenerateImageDescriptionAsync(f);
                            if (string.IsNullOrEmpty(autoDesc))
                                autoDesc = "Slika: " + Path.GetFileNameWithoutExtension(f);
                        }
                        timelineItems.Add(new TimelineItem
                        {
                            Path = f,
                            Duration = duration,
                            Name = Path.GetFileName(f),
                            AudioDescription = autoDesc,
                            Type = type,
                            Volume = 100,
                            TrackIndex = targetTrack,
                            VideoEffect = new VideoEffectData()
                        });
                        if (!string.IsNullOrEmpty(autoDesc))
                            LogMessage("Added: " + Path.GetFileName(f) + ". Description: " + autoDesc, true);
                    }
                    catch (Exception ex) { LogMessage(string.Format(L("generic_error"), ex.Message), true); }
                }
                UpdateTimelineDisplay();
                LogMessage($"Items added: {d.FileNames.Length}", true);
                PlayBeep();
            }
        }

        private async void AddAudioOnly_Click(object sender, RoutedEventArgs e)
        {
            var d = new WpfOpenFileDialog { Multiselect = true, Filter = "Audio|*.mp3;*.wav;*.m4a;*.flac;*.ogg" };
            if (d.ShowDialog() == true)
            {
                SaveState();
                int targetTrack = (currentTrackFilter == 2 || currentTrackFilter == 3) ? currentTrackFilter : 2;
                foreach (var f in d.FileNames)
                {
                    if (!MediaLoader.IsValidMediaFile(f)) { LogMessage(string.Format(L("not_valid_audio"), Path.GetFileName(f)), true); continue; }
                    try
                    {
                        double duration = await GetMediaDurationWithTimeoutAsync(f, TimeSpan.FromSeconds(30));
                        timelineItems.Add(new TimelineItem { Path = f, Duration = duration, Name = "Audio: " + Path.GetFileName(f), Type = "Audio", Volume = 100, TrackIndex = targetTrack, VideoEffect = new VideoEffectData() });
                    }
                    catch (Exception ex) { LogMessage(string.Format(L("generic_error"), ex.Message), true); }
                }
                UpdateTimelineDisplay();
                LogMessage($"Items added: {d.FileNames.Length}", true);
                PlayBeep();
            }
        }
        // ── YouTube Import ──────────────────────────────────────────────────────
        private async void ImportFromYouTube_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new YouTubeDownloadDialog(_currentLanguage) { Owner = this };

            if (dlg.ShowDialog() != true || dlg.DownloadedFiles.Count == 0)
                return;

            SaveState();

            int added = 0;
            foreach (var filePath in dlg.DownloadedFiles)
            {
                if (!File.Exists(filePath))
                {
                    LogMessage($"File not found: {Path.GetFileName(filePath)}", true);
                    continue;
                }

                try
                {
                    string ext     = Path.GetExtension(filePath).ToLowerInvariant();
                    bool   isAudio = ext == ".mp3" || ext == ".wav" || ext == ".m4a"
                                  || ext == ".flac" || ext == ".ogg";

                    int track = isAudio
                        ? (currentTrackFilter == 2 || currentTrackFilter == 3 ? currentTrackFilter : 2)
                        : (currentTrackFilter >= 0 && currentTrackFilter <= 1 ? currentTrackFilter : 0);

                    double duration = await GetMediaDurationWithTimeoutAsync(filePath, TimeSpan.FromSeconds(30));

                    timelineItems.Add(new TimelineItem
                    {
                        Path        = filePath,
                        Duration    = duration,
                        Name        = Path.GetFileName(filePath),
                        Type        = isAudio ? "Audio" : "Video",
                        Volume      = 100,
                        TrackIndex  = track,
                        VideoEffect = new VideoEffectData()
                    });

                    LogMessage($"YouTube → Timeline: {Path.GetFileName(filePath)}", true);
                    added++;
                }
                catch (Exception ex)
                {
                    LogMessage($"Error adding {Path.GetFileName(filePath)}: {ex.Message}", true);
                }
            }

            if (added > 0)
            {
                UpdateTimelineDisplay();
                LogMessage($"Added {added} file(s) from YouTube to the timeline.", true);
                PlayBeep();
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  StartRenderToPath — render bez dijaloga, za AutoRender iz AIVideoCreator
        //  Koristi isti RenderSimpleAsync poziv kao FinalRender_Click
        // ══════════════════════════════════════════════════════════════════════
        public async Task StartRenderToPath(string outputPath)
        {
            if (timelineItems.Count == 0)
            {
                LogMessage("Auto-Render: No clips to render", true);
                return;
            }

            string format = Path.GetExtension(outputPath).ToLower().TrimStart('.');
            if (format != "mp4" && format != "webm" && format != "avi")
                format = "mp4";

            // Osiguraj da folder postoji
            string folder = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            _renderCancellation = new CancellationTokenSource();
            RecalculatePositions();
            prgRender.Visibility = Visibility.Visible;
            prgRender.Value = 0;
            btnRenderTool.IsEnabled = false;
            txtRenderStatus.Text = "Auto-Render in progress...";

            LogMessage($"🚀 Auto-Render: {timelineItems.Count} clips → {outputPath}", true);

            try
            {
                var renderProgress = new Progress<int>(percent =>
                    Dispatcher.Invoke(() => { prgRender.Value = percent; }));

                var renderSubtitles = subtitles.ToList();
                bool enableSubtitles = chkEnableSubtitles?.IsChecked == true;

                BeatInfo renderBeatInfo = null;
                try
                {
                    var aiCreator = FindAIVideoCreator();
                    if (aiCreator != null) renderBeatInfo = aiCreator.GetBeatInfo();
                }
                catch (Exception swallowed) { AppLog.Error("Reading beat info from the AI creator", swallowed); }   // v1.4: was silent

                await _renderEngine.RenderSimpleAsync(
                    timelineItems,
                    outputPath,
                    format,
                    renderProgress,
                    renderSubtitles,
                    currentExportSettings,
                    _renderCancellation.Token,
                    useGPUAcceleration,
                    _selectedResolution ?? "1920x1080",
                    AIVideoCreator.FastRenderMode,
                    enableSubtitles,
                    renderBeatInfo);

                if (File.Exists(outputPath))
                {
                    long fileSize = new FileInfo(outputPath).Length;
                    _lastRenderPath = outputPath;   // v1.4 (4.2): the audio description starts from this video
                    LogMessage(string.Format(L("render_done_log"), outputPath, fileSize / 1024 / 1024), true);
                    txtRenderStatus.Text = "Auto-Render complete!";
                    PlayBeep();

                    // Auto-close prozor posle 8 sekundi — ne treba klik
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(500); // small delay so render engine finishes cleanup
                        Dispatcher.Invoke(() =>
                        {
                            var toast = new System.Windows.Window
                            {
                                Title = "Auto-Render complete",
                                Width = 420, Height = 130,
                                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                                Left = SystemParameters.WorkArea.Right - 440,
                                Top  = SystemParameters.WorkArea.Bottom - 150,
                                Topmost = true,
                                ResizeMode = System.Windows.ResizeMode.NoResize,
                                WindowStyle = System.Windows.WindowStyle.ToolWindow,
                                Background = new System.Windows.Media.SolidColorBrush(
                                    System.Windows.Media.Color.FromRgb(30, 70, 30))
                            };
                            var txt = new System.Windows.Controls.TextBlock
                            {
                                Text = "Video sacuvan!" + Environment.NewLine +
                                       System.IO.Path.GetFileName(outputPath) + Environment.NewLine +
                                       "(" + (fileSize / 1024 / 1024) + " MB)" + Environment.NewLine +
                                       "The window will close in 8 seconds...",
                                Foreground = System.Windows.Media.Brushes.LightGreen,
                                FontSize = 13, TextAlignment = System.Windows.TextAlignment.Center,
                                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                                Margin = new System.Windows.Thickness(10)
                            };
                            toast.Content = txt;
                            toast.Show();

                            // Auto-close posle 8 sekundi
                            var timer = new System.Windows.Threading.DispatcherTimer
                                { Interval = TimeSpan.FromSeconds(8) };
                            timer.Tick += (s, e) => { timer.Stop(); toast.Close(); };
                            timer.Start();
                        });
                    });
                }
                else
                {
                    LogMessage($"Auto-Render: file was not created: {outputPath}", true);
                    txtRenderStatus.Text = "Auto-Render failed";
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Auto-Render error: {ex.Message}", true);
                txtRenderStatus.Text = "Auto-Render error";
            }
            finally
            {
                btnRenderTool.IsEnabled = true;
                prgRender.Visibility = Visibility.Collapsed;
            }
        }

        private async void FinalRender_Click(object sender, RoutedEventArgs e)
        {
            if (timelineItems.Count == 0)
            {
                LogMessage("No clips to render", true);
                return;
            }

            if (string.IsNullOrEmpty(currentProjectFolder))
            {
                SaveAsProject_Click(null, null);
                if (string.IsNullOrEmpty(currentProjectFolder))
                {
                    LogMessage(L("save_project_first2"), true);
                    return;
                }
            }
            RecalculatePositions();

            // v1.4: "Export audio only" (Export options) now really exports just the audio
            if (currentExportSettings?.ExportAudioOnly == true)
            {
                var audioDialog = new WpfSaveFileDialog { Filter = "MP3 audio|*.mp3|WAV audio|*.wav", DefaultExt = "mp3", FileName = "audio.mp3" };
                if (audioDialog.ShowDialog() != true) return;
                LogMessage("Exporting audio only...", true);
                bool audioOk = await ExportHelpers.ExportAudioOnlyAsync(timelineItems, audioDialog.FileName, CancellationToken.None);
                LogMessage(audioOk ? $"Audio exported: {audioDialog.FileName}" : "Audio export failed — no audio clip on the timeline?", true);
                if (audioOk) PlayBeep();
                return;
            }

            var saveDialog = new WpfSaveFileDialog
            {
                Filter = "MP4 video|*.mp4|WebM video|*.webm|AVI video|*.avi",
                DefaultExt = "mp4",
                FileName = "izlazni_video.mp4"
            };

            if (saveDialog.ShowDialog() != true) return;

            // v1.4: the renderer always produces H.264/AAC in MP4. For WebM/AVI it renders to
            // a temporary MP4 next to the target and converts at the end (WebM used to fail).
            string requestedPath = saveDialog.FileName;
            string requestedExt = Path.GetExtension(requestedPath).ToLowerInvariant();
            bool needsConversion = requestedExt == ".webm" || requestedExt == ".avi";
            string outputPath = needsConversion
                ? Path.Combine(Path.GetDirectoryName(requestedPath) ?? "", Path.GetFileNameWithoutExtension(requestedPath) + "_tmp_render.mp4")
                : requestedPath;
            string format = Path.GetExtension(outputPath).ToLower().TrimStart('.');
            if (format == "mp4") format = "mp4";
            else if (format == "webm") format = "webm";
            else if (format == "avi") format = "avi";
            else format = "mp4";

            // v1.4: always ask for the resolution (it was only asked when the project had
            // audio — otherwise 1920x1080 was used silently, even after choosing 9:16 before)
            string selectedResolution = _selectedResolution ?? "1920x1080";
            var resolutionDialog = new ResolutionDialog { Owner = this };
            if (resolutionDialog.ShowDialog() != true)
            {
                LogMessage("Rendering cancelled", true);
                return;
            }
            if (!string.IsNullOrEmpty(resolutionDialog.SelectedResolution))
            {
                selectedResolution = resolutionDialog.SelectedResolution;
                _selectedResolution = selectedResolution; // save for AIVideoCreator
            }

            _renderCancellation = new CancellationTokenSource();

            prgRender.Visibility = Visibility.Visible;
            prgRender.Value = 0;
            btnRenderTool.IsEnabled = false;
            txtRenderStatus.Text = "Rendering...";

            LogMessage("Rendering started...", true);

            try
            {
                string ffmpegExePath = AppPaths.FFmpeg;
                LogMessage($"Checking FFmpeg at: {ffmpegExePath}", true);

                if (!File.Exists(ffmpegExePath))
                {
                    LogMessage(L("ffmpeg_not_found"), true);
                    WpfMessageBox.Show(string.Format(L("ffmpeg_missing_msg"), ffmpegExePath),
                                    "FFmpeg is missing", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                LogMessage(L("ffmpeg_found"), true);

                var renderProgress = new Progress<int>(percent =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        prgRender.Value = percent;
                        if (accessibilityMode && percent % 10 == 0)
                        {
                            LogMessage($"Render progress: {percent}%", true);
                        }
                    });
                });

                var renderSubtitles = subtitles.ToList();
                bool enableSubtitles = chkEnableSubtitles?.IsChecked == true;

                // BEAT-SYNC: proslijedi BeatInfo ako je AIVideoCreator generisao analizu
                BeatInfo renderBeatInfo = null;
                try
                {
                    var aiCreator = FindAIVideoCreator();
                    if (aiCreator != null)
                        renderBeatInfo = aiCreator.GetBeatInfo();
                }
                catch (Exception swallowed) { AppLog.Error("Reading beat info from the AI creator", swallowed); }   // v1.4: was silent

                await _renderEngine.RenderSimpleAsync(
                    timelineItems,
                    outputPath,
                    format,
                    renderProgress,
                    renderSubtitles,
                    currentExportSettings,
                    _renderCancellation.Token,
                    useGPUAcceleration,
                    selectedResolution,  // PASSED RESOLUTION
                    AIVideoCreator.FastRenderMode,  // BRZI RENDER (bez Ken Burns)
                    enableSubtitles,    // HARD-ANCHOR subtitle toggle
                    renderBeatInfo);    // BEAT-SYNC: DownBeat snap za rezove

                if (needsConversion && File.Exists(outputPath))
                {
                    LogMessage($"Converting to {requestedExt.TrimStart('.').ToUpperInvariant()}...", true);
                    bool converted = await ExportHelpers.ConvertContainerAsync(outputPath, requestedPath, _renderCancellation?.Token ?? CancellationToken.None);
                    if (converted)
                    {
                        try { File.Delete(outputPath); } catch { }
                        outputPath = requestedPath;
                    }
                    else
                    {
                        LogMessage($"Conversion failed — the MP4 is kept: {outputPath}", true);
                    }
                }

                if (File.Exists(outputPath))
                {
                    long fileSize = new FileInfo(outputPath).Length;
                    string summary = BuildExportSummary();
                    _lastRenderPath = outputPath;   // v1.4 (4.2): the audio description starts from this video
                    LogMessage(string.Format(L("render_done_log"), outputPath, fileSize / 1024 / 1024), true);
                    if (!string.IsNullOrWhiteSpace(summary))
                        LogMessage(summary, true);
                    txtRenderStatus.Text = L("render_done_status");
                    PlayBeep();
                    string doneMsg = string.Format(L("render_done_msg"), outputPath, fileSize / 1024 / 1024);
                    if (!string.IsNullOrWhiteSpace(summary))
                        doneMsg += "\n\n" + summary;
                    WpfMessageBox.Show(doneMsg, L("render_done_title2"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    LogMessage(string.Format(L("render_no_file"), outputPath), true);
                    txtRenderStatus.Text = L("render_no_file_status");
                    WpfMessageBox.Show(string.Format(L("render_no_file_msg"), outputPath), L("error_title"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (OperationCanceledException)
            {
                LogMessage("Rendering cancelled", true);
                txtRenderStatus.Text = "Cancelled";
                PlayBeep();
            }
            catch (Exception ex)
            {
                // v1.4: details go to the log files; the message tells where they are instead of
                // reading a stack trace aloud.
                AppLog.Error("Render", ex);
                LogMessage($"RENDER ERROR: {ex.Message}", true);
                txtRenderStatus.Text = L("render_failed_status");
                string where = File.Exists(RenderEngine.LastErrorLogPath) ? RenderEngine.LastErrorLogPath : AppLog.TodayFile;
                WpfMessageBox.Show(string.Format(L("render_failed_msg"), ex.Message, where), L("error_title"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                prgRender.Visibility = Visibility.Collapsed;
                btnRenderTool.IsEnabled = true;
                _renderCancellation = null;
            }
        }

        /// <summary>
        /// Central place that applies a display mode: banner text/visibility,
        /// position-announce timer, menu checkmarks, and the log/beep feedback.
        /// Called both by the automatic startup detection and by the manual
        /// menu items / Ctrl+Shift+A, so there's exactly one code path that can
        /// get the banner out of sync with the actual mode.
        /// </summary>
        private string _themeBeforeLowVision;

        private void SetUIMode(UIMode mode, bool announce = true, bool byUser = true)
        {
            UIMode previous = _uiMode;
            _uiMode = mode;
            if (byUser) _modeChosenByUser = true;

            // v1.4: leaving Low vision gives back the theme you had before (it stayed high contrast)
            if (previous == UIMode.LowVision && mode != UIMode.LowVision && _themeBeforeLowVision != null)
            {
                _currentTheme = _themeBeforeLowVision;
                _themeBeforeLowVision = null;
                ApplyTheme();
            }
            if (mode == UIMode.LowVision && previous != UIMode.LowVision)
                _themeBeforeLowVision = _currentTheme;

            switch (mode)
            {
                case UIMode.Accessibility:
                    borderAccessibilityStatus.Visibility = Visibility.Visible;
                    txtAccessibilityStatus.Text = "♿ ACCESSIBILITY MODE ON";
                    try { if (_positionAnnounceOn) positionAnnounceTimer?.Start(); } catch { }
                    if (announce) LogMessage(L("accessibility_on"), true);
                    break;

                case UIMode.LowVision:
                    borderAccessibilityStatus.Visibility = Visibility.Visible;
                    txtAccessibilityStatus.Text = $"🔍 LOW VISION MODE ON ({_lowVisionScale:0.##}x)";
                    try { positionAnnounceTimer?.Stop(); } catch { }
                    // Low vision needs strong contrast as a baseline, plus
                    // enlarged text/controls across every window.
                    _currentTheme = "contrast";
                    ApplyTheme();
                    UiScaling.SetScale(_lowVisionScale);
                    ApplyNativeListViewScale(_lowVisionScale);
                    // v1.4: this message is said even though Low vision mode itself does not speak
                    if (announce) { LogMessage("Low vision mode on", false); AnnounceModeChange("Low vision mode on"); }
                    break;

                case UIMode.Sighted:
                default:
                    borderAccessibilityStatus.Visibility = Visibility.Collapsed;
                    try { positionAnnounceTimer?.Stop(); } catch { }
                    // Defensive reset: whatever mode we're coming from, Sighted
                    // and Accessibility both use normal (1.0x) scale. Without
                    // this, leaving Low Vision would leave everything enlarged.
                    UiScaling.SetScale(1.0);
                    ApplyNativeListViewScale(1.0);
                    // v1.4: said aloud once more — the mode being left was the one that speaks
                    if (announce) { LogMessage(L("accessibility_off"), false); AnnounceModeChange(L("accessibility_off")); }
                    break;
            }

            if (mnuModeSighted != null)
            {
                mnuModeSighted.IsChecked = mode == UIMode.Sighted;
                mnuModeAccessibility.IsChecked = mode == UIMode.Accessibility;
                mnuModeLowVision.IsChecked = mode == UIMode.LowVision;
            }

            if (announce) PlayBeep();
        }

        /// <summary>
        /// Runs once at startup: picks Accessibility if a screen reader is
        /// detected, otherwise Sighted. Never auto-selects Low Vision, since
        /// low vision can't be inferred the same way — the user turns that on
        /// manually from the View menu.
        /// </summary>
        /// <summary>Speaks a mode change even when the new mode is a silent one.</summary>
        private void AnnounceModeChange(string message)
        {
            try { if (ScreenReaderDetector.CurrentReader() != ScreenReaderDetector.Reader.None) AnnounceToJaws(message); }
            catch (Exception ex) { AppLog.Error("Announcing the mode change", ex); }
        }

        private void DetectAndApplyInitialMode()
        {
            bool screenReaderDetected = false;
            try { screenReaderDetected = ScreenReaderDetector.IsScreenReaderActive(); }
            catch { }

            SetUIMode(screenReaderDetected ? UIMode.Accessibility : UIMode.Sighted, announce: false, byUser: false);
            LogMessage(screenReaderDetected
                ? "Screen reader detected — starting in Accessibility mode."
                : "No screen reader detected — starting in Sighted mode.", true);
        }

        private void SetModeSighted_Click(object sender, RoutedEventArgs e) => SetUIMode(UIMode.Sighted);
        private void SetModeAccessibility_Click(object sender, RoutedEventArgs e) => SetUIMode(UIMode.Accessibility);
        private void SetModeLowVision_Click(object sender, RoutedEventArgs e) => SetUIMode(UIMode.LowVision);

        /// <summary>
        /// Quick toggle for Ctrl+Shift+A and the toolbar button: flips between
        /// Sighted and Accessibility. Low Vision is deliberately only reachable
        /// from the View menu, since it's not what this shortcut has ever meant.
        /// </summary>
        private void ToggleAccessibilityMode_Click(object sender, RoutedEventArgs e)
        {
            SetUIMode(_uiMode == UIMode.Accessibility ? UIMode.Sighted : UIMode.Accessibility);
        }

        private void SetupKeyboardCommands()
        {
            this.PreviewKeyDown += (sender, e) =>
            {
                // v1.4: seeking from the keyboard. Before, moving inside a clip was possible only with
                // the two on-screen buttons. Alt+Left/Right = 5 seconds, Alt+Shift+Left/Right = one
                // tenth of a second (fine positioning before a cut). WPF reports Alt combinations as
                // Key.System, the real key is in SystemKey.
                Key realKey = e.Key == Key.System ? e.SystemKey : e.Key;
                if ((realKey == Key.Left || realKey == Key.Right) &&
                    (Keyboard.Modifiers == ModifierKeys.Alt || Keyboard.Modifiers == (ModifierKeys.Alt | ModifierKeys.Shift)))
                {
                    bool fine = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
                    if (!DuplicateKey("seek" + realKey + fine))
                        SeekBy((realKey == Key.Right ? 1 : -1) * (fine ? 100 : 5000));
                    e.Handled = true;
                    return;
                }

                // v1.4 (R5): Premiere-style editing keys without modifiers — J/K/L shuttle,
                // comma/period one frame, Q/W trim the clip to the playhead. Never inside a text field.
                if (Keyboard.Modifiers == ModifierKeys.None && !IsTextInput(e.OriginalSource) && HandleEditingKey(e.Key))
                {
                    e.Handled = true;
                    return;
                }

                // Ctrl+G – Idi na broj klipa
                if (e.Key == Key.G && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    var dialog = new NumericDialog(nativeListView?.Items.Count ?? 0);
                    if (dialog.ShowDialog() == true)
                    {
                        NavigateToItemByNumber(dialog.SelectedNumber);
                    }
                    e.Handled = true;
                }
                if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    NewProject_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    LoadProject_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    SaveProject_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.F12)
                {
                    SaveAsProject_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = false; }
                else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = false; }
                else if (e.Key == Key.X && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = false; }
                // v1.4: these shortcuts were shown in the menus but nothing handled them — WPF menus
                // only DISPLAY the gesture text. Undo, Redo, Select all, Add video/image, Add audio,
                // Apply effects and Help now work from the keyboard. In a text field, Ctrl+Z / Ctrl+Y /
                // Ctrl+A keep their normal text meaning.
                else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    if (!(Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)) { SelectAll_Click(null, null); e.Handled = true; }
                }
                else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    if (!(Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)) { Undo_Click(null, null); e.Handled = true; }
                }
                else if (e.Key == Key.Y && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    if (!(Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)) { Redo_Click(null, null); e.Handled = true; }
                }
                else if (e.Key == Key.I && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    AddVideoImage_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.I && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    AddAudioOnly_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.E && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    ApplyEffects_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.Delete) { e.Handled = false; }
                else if (e.Key == Key.F1 && Keyboard.Modifiers == ModifierKeys.None)
                {
                    // the transition list uses F1 to read the selected effect's description
                    if (!lstTransitions.IsKeyboardFocusWithin) { ShowHelp_Click(null, null); e.Handled = true; }
                }
                else if (e.Key == Key.F2) { e.Handled = false; }
                else if (e.Key == Key.F3) { e.Handled = false; }
                else if (e.Key == Key.L && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    if (_logWindow != null) { _logWindow.Show(); _logWindow.Activate(); LogMessage("Log window opened", true); }
                    e.Handled = true;
                }
                else if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    nativeListView?.Focus();
                    if (nativeListView?.Items.Count > 0 && nativeListView.SelectedItems.Count == 0)
                        nativeListView.Items[0].Selected = true;
                    LogMessage("Focus on the clip list. Use the up/down arrows to navigate.", true);
                    e.Handled = true;
                }
                else if (e.Key == Key.A && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    ToggleAccessibilityMode_Click(null, null);
                    e.Handled = true;
                }
                else if ((e.Key == Key.OemPlus || e.Key == Key.Add) && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    IncreaseLowVisionTextSize_Click(null, null);
                    e.Handled = true;
                }
                else if ((e.Key == Key.OemMinus || e.Key == Key.Subtract) && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    DecreaseLowVisionTextSize_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    FinalRender_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.E && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    FinalRender_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    SetClipDuration_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.V && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt))
                {
                    SetClipVolume_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.M && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    AddMarker_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.N && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    NextMarker_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.D && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    OpenSceneDetection_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.G && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    OpenColorGrading_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.B && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    OpenBatchExport_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.P && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    OpenProjectTemplates_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.T && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    OpenTimelineAI_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.H && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    OpenHighlightEngine_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.D3 && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    OpenPhase3_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.OemComma && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    PrevMarker_Click(null, null);  // Ctrl+Shift+, za prethodni marker (bilo Key.P = konflikt)
                    e.Handled = true;
                }
                else if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    AddKeyframe_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.K && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    RemoveKeyframe_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    if (!DuplicateKey("play")) TogglePlay_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.Space && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    if (!DuplicateKey("mix")) PlayMix_Click(null, null);   // v1.4 (R3): hear the whole montage without a render
                    e.Handled = true;
                }
                else if (e.Key == Key.S && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt))
                {
                    SetClipSpeed_Click(null, null);   // v1.4 (R5): clip speed had no control at all
                    e.Handled = true;
                }
                else if (e.Key == Key.V && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    if (!DuplicateKey("volpoint")) AddVolumePoint_Click(null, null);   // v1.4 (R5)
                    e.Handled = true;
                }
                else if (e.Key == Key.F && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    if (!DuplicateKey("descframe")) DescribeFrameHere_Click(null, null);   // v1.4 (R4)
                    e.Handled = true;
                }
                else if (e.Key == Key.D && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt))
                {
                    if (!DuplicateKey("descparts")) DescribeClipParts_Click(null, null);   // v1.4 (R4)
                    e.Handled = true;
                }
                else if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    MoveLeft_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    MoveRight_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.Up && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    MoveClipUp_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.Down && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    MoveClipDown_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.Add && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    ZoomIn_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.Subtract && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    ZoomOut_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.D0 && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    ResetZoom_Click(null, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.X && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    CutAtAllMarkers_Click(null, null);
                    e.Handled = true;
                }
                else if ((e.Key == Key.Up || e.Key == Key.Down) && nativeListView?.Focused == true)
                {
                    int newIndex = -1;
                    if (nativeListView.SelectedItems.Count > 0)
                    {
                        int currentIndex = nativeListView.SelectedItems[0].Index;
                        newIndex = currentIndex + (e.Key == Key.Down ? 1 : -1);
                    }
                    else if (nativeListView.Items.Count > 0)
                    {
                        newIndex = 0;
                    }
                    if (newIndex >= 0 && newIndex < nativeListView.Items.Count)
                    {
                        nativeListView.SelectedItems.Clear();
                        nativeListView.Items[newIndex].Selected = true;
                        nativeListView.Items[newIndex].EnsureVisible();
                        // v1.4: no extra announcement here — ProcessSelectedItem already reads
                        // "N of TOTAL, type: name, duration..." (it was spoken twice)
                        e.Handled = true;
                    }
                }
                else if (e.Key == Key.PageUp && nativeListView?.Focused == true && nativeListView.Items.Count > 0)
                {
                    // v1.4: guard for an empty list and use the VISIBLE list (track filter),
                    // not the whole timeline — it crashed / jumped past the end before.
                    int newIndex = Math.Max(0, (nativeListView.SelectedItems.Count > 0 ? nativeListView.SelectedItems[0].Index : 0) - 5);
                    nativeListView.SelectedItems.Clear();
                    nativeListView.Items[newIndex].Selected = true;
                    nativeListView.Items[newIndex].EnsureVisible();
                    e.Handled = true;
                }
                else if (e.Key == Key.PageDown && nativeListView?.Focused == true && nativeListView.Items.Count > 0)
                {
                    int newIndex = Math.Min(nativeListView.Items.Count - 1, (nativeListView.SelectedItems.Count > 0 ? nativeListView.SelectedItems[0].Index : 0) + 5);
                    nativeListView.SelectedItems.Clear();
                    nativeListView.Items[newIndex].Selected = true;
                    nativeListView.Items[newIndex].EnsureVisible();
                    e.Handled = true;
                }
            };
        }

        private void ShowHelp_Click(object sender, RoutedEventArgs e)
        {
            var helpWindow = new HelpWindow();
            helpWindow.Owner = this;
            helpWindow.ShowDialog();
        }

        private void AddSubtitleToClip_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                var dialog = new TextOverlayDialog($"Add subtitle to: {item.Name}");
                if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.Text))
                {
                    subtitles.Add(new SubtitleItem { Text = dialog.Text, Start = item.Start, End = item.End });
                    lstSubtitles.Items.Add($"{FormatTime(item.Start)} -> {FormatTime(item.End)}: {dialog.Text}");
                    LogMessage($"Subtitle added to clip: {item.Name}", true);
                    PlayBeep();
                }
            }
            else LogMessage("Select a clip first", true);
        }

        private void AddIntroText_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new TextOverlayDialog("Intro text (song title)");
            if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.Text))
            {
                subtitles.Add(new SubtitleItem { Text = dialog.Text, Start = 0, End = 5 });
                lstSubtitles.Items.Add($"0:00 -> 0:05: {dialog.Text}");
                LogMessage($"Intro text added: {dialog.Text}", true);
                PlayBeep();
            }
        }

        private void AddOutroText_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new TextOverlayDialog("Outro text (author, channel)");
            if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.Text))
            {
                double totalDuration = GetTotalDuration();
                subtitles.Add(new SubtitleItem { Text = dialog.Text, Start = totalDuration - 5 > 0 ? totalDuration - 5 : 0, End = totalDuration });
                lstSubtitles.Items.Add($"{FormatTime(totalDuration - 5)} -> {FormatTime(totalDuration)}: {dialog.Text}");
                LogMessage($"Outro text added: {dialog.Text}", true);
                PlayBeep();
            }
        }

        private void AddTransitionBetweenSelected_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                int idx = timelineItems.IndexOf(item);
                if (idx >= 0 && idx < timelineItems.Count - 1)
                {
                    if (lstTransitions.SelectedItem is TransitionEffect transition)
                    {
                        // v1.4: a NEW transition object each time (the list item itself was
                        // reused, so adding it twice changed both) and stored on the clip
                        var t = new TransitionEffect { Name = transition.Name, Type = transition.Type, Duration = transitionDuration, ClipIndex1 = idx, ClipIndex2 = idx + 1 };
                        transitions.RemoveAll(x => x.ClipIndex1 == idx);
                        transitions.Add(t);
                        SetClipTransition(item, t);
                        LogMessage(string.Format(L("transition_added"), transition.Name, idx + 1, idx + 2), true);
                        PlayBeep();
                    }
                    else LogMessage("Select a transition first", true);
                }
                else LogMessage("Select the first clip of the pair (not the last)", true);
            }
        }

        private void AddTransitionBetweenSpecific_Click(object sender, RoutedEventArgs e)
        {
            if (timelineItems.Count < 2) { LogMessage("Not enough clips to add a transition", true); return; }
            if (lstTransitions.SelectedItem is not TransitionEffect transition) { LogMessage("Select a transition first", true); return; }
            var dialog = new TransitionDialog(timelineItems.Count);
            if (dialog.ShowDialog() == true)
            {
                int clipIndex = dialog.ClipIndex - 1;
                if (clipIndex >= 0 && clipIndex < timelineItems.Count - 1)
                {
                    var t = new TransitionEffect { Name = transition.Name, Type = transition.Type, Duration = transitionDuration, ClipIndex1 = clipIndex, ClipIndex2 = clipIndex + 1 };
                    transitions.RemoveAll(x => x.ClipIndex1 == clipIndex);
                    transitions.Add(t);
                    SetClipTransition(timelineItems[clipIndex], t);
                    LogMessage(string.Format(L("transition_added"), transition.Name, clipIndex + 1, clipIndex + 2), true);
                    PlayBeep();
                }
                else LogMessage("Invalid clip number", true);
            }
        }

        private void ClearPrompt_Click(object sender, RoutedEventArgs e)
        {
            txtAIPrompt.Clear();
            LogMessage("Prompt obrisan", true);
            PlayBeep();
        }

        private void PastePrompt_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (WpfClipboard.ContainsText())
                {
                    string text = WpfClipboard.GetText();
                    if (!string.IsNullOrEmpty(text))
                    {
                        txtAIPrompt.Text = text;
                        LogMessage(string.Format(L("prompt_pasted"), text.Length), true);
                        PlayBeep();
                    }
                    else
                    {
                        LogMessage(LanguageManager.T("loc_clipboard_empty"), true);
                    }
                }
                else
                {
                    LogMessage(L("clipboard_no_text"), true);
                }
            }
            catch (Exception ex)
            {
                LogMessage(string.Format(L("paste_error"), ex.Message), true);
            }
        }

        private void AddAudioDescription_Click(object sender, RoutedEventArgs e)
        {
            // v1.4: any clip with a picture can get a description, not only pictures
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item && !item.IsAudio)
            {
                var dialog = new TextOverlayDialog($"Audio description: {item.Name}");
                if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.Text))
                {
                    item.AudioDescription = dialog.Text;
                    LogMessage($"Audio description added for image {item.Index}: {dialog.Text}", true);
                    UpdateTimelineDisplay();
                    PlayBeep();
                }
            }
            else
            {
                LogMessage("Select an image first", true);
            }
        }

        private async void AutoDescribeImage_Click(object sender, RoutedEventArgs e)
        {
            if (!(nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item && item.IsImage))
            {
                // v1.4: for a video the frame under the playhead is described (same as Ctrl+Shift+F)
                DescribeFrameHere_Click(null, null);
                return;
            }
            if (!File.Exists(item.Path))
            {
                LogMessage(L("vd_file_missing"), true);
                return;
            }

            string ffmpegPath = AppPaths.FFmpeg;
            LogMessage(L("vd_analyzing"), true);

            VisionResult result;
            try
            {
                // Cheap no-ops if already initialized earlier in the session.
                await VisionAnalyzer.InitializeAsync(msg => LogMessage(msg, false));
                await VisionAnalyzer.InitializeQwenAsync(msg => LogMessage(msg, false));
                result = await VisionAnalyzer.AnalyzeClipAsync(item.Path, ffmpegPath);
            }
            catch (Exception ex)
            {
                LogMessage(LF("vd_analysis_failed", ex.Message), true);
                return;
            }

            string suggestion = BuildFrameDescription(result);
            if (string.IsNullOrWhiteSpace(suggestion))
            {
                LogMessage(L("vd_no_description"), true);
                return;
            }

            var dialog = new TextOverlayDialog(LF("vd_dialog_title", item.Name), suggestion);
            if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.Text))
            {
                item.AudioDescription = dialog.Text;
                LogMessage($"Audio description added for image {item.Index}: {dialog.Text}", true);
                UpdateTimelineDisplay();
                PlayBeep();
            }
        }

        /// <summary>
        /// Composes a short, human-readable draft description from a VisionAnalyzer result,
        /// for the user to review/edit before it's saved as the clip's audio description.
        /// Falls back to brightness/color heuristics when no AI labels are available
        /// (e.g. Ollama/ONNX not installed) so the feature still gives some result.
        /// </summary>
        private string BuildFrameDescription(VisionResult r)
        {
            if (r == null) return null;
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(r.TopLabel))
            {
                string extra = (r.Labels != null && r.Labels.Length > 0)
                    ? " (" + string.Join(", ", r.Labels.Take(5)) + ")"
                    : "";
                parts.Add(LF("vd_detected", r.TopLabel) + extra);
            }

            var traits = new List<string>();
            if (r.IsOutdoor) traits.Add(L("vd_outdoor"));
            if (r.IsWarm) traits.Add(L("vd_warm"));
            if (r.HasFaces) traits.Add(r.HasSmile ? L("vd_face_smile") : L("vd_face"));
            if (r.HasChildren) traits.Add(L("vd_children"));
            if (r.HasMotion) traits.Add(L("vd_motion"));
            if (traits.Count > 0)
                parts.Add(string.Join(", ", traits) + ".");

            if (parts.Count == 0)
            {
                // No AI labels available (ONNX/Ollama not installed) — fall back to
                // the brightness/saturation heuristics FFmpeg-only mode still gives us.
                if (r.Luminance >= 150) parts.Add(L("vd_bright"));
                else if (r.Luminance > 0 && r.Luminance <= 60) parts.Add(L("vd_dark"));
                else if (r.Luminance > 0) parts.Add(L("vd_moderate_light"));
            }

            return string.Join(" ", parts).Trim();
        }

        /// <summary>
        /// Composes a short, plain-language summary of what's in the just-finished export
        /// (duration, clip/subtitle/transition counts, and any suspicious silent gaps on
        /// audio tracks), so a blind editor can confirm the result without needing someone
        /// sighted to "just check it looks right".
        /// </summary>
        private string BuildExportSummary()
        {
            if (timelineItems.Count == 0) return null;

            var sb = new StringBuilder();
            sb.AppendLine(L("export_summary_header"));

            double totalDuration = timelineItems.Max(i => i.End);
            int videoClips = timelineItems.Count(i => !i.IsAudio && !i.IsImage);
            int imageClips = timelineItems.Count(i => i.IsImage);
            int audioClips = timelineItems.Count(i => i.IsAudio);

            sb.AppendLine(LF("export_summary_duration", FormatTime(totalDuration)));
            sb.AppendLine(LF("export_summary_clips", videoClips, imageClips, audioClips));

            if (transitions.Count > 0)
                sb.AppendLine(LF("export_summary_transitions", transitions.Count));

            if (subtitles.Count > 0)
                sb.AppendLine(LF("export_summary_subtitles", subtitles.Count));

            // Flag gaps of more than 1s on audio tracks — likely unintended silence
            // (e.g. background music/narration that doesn't cover the whole video).
            var audioItems = timelineItems.Where(i => i.IsAudio).OrderBy(i => i.Start).ToList();
            if (audioItems.Count > 0)
            {
                var gaps = new List<(double start, double end)>();
                double cursor = 0;
                foreach (var a in audioItems)
                {
                    if (a.Start - cursor > 1.0) gaps.Add((cursor, a.Start));
                    cursor = Math.Max(cursor, a.End);
                }
                if (totalDuration - cursor > 1.0) gaps.Add((cursor, totalDuration));

                foreach (var g in gaps.Take(5))
                    sb.AppendLine(LF("export_summary_gap", FormatTime(g.start), FormatTime(g.end)));
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// v1.4 — reads the description of ANY clip (it only worked for pictures): the clip's own
        /// description, and for a clip described by parts the part under the playhead.
        /// </summary>
        private void ReadAudioDescription_Click(object sender, RoutedEventArgs e)
        {
            var item = SelectedClip();
            if (item == null) { LogMessage(L("select_clip_first_play"), true); return; }
            var parts = new List<string>();
            string own = UsableDescription(item.AudioDescription);
            if (own != null) parts.Add(own);
            if (item.FrameDescriptions != null && item.FrameDescriptions.Count > 0)
            {
                double pos = ReferenceEquals(item, _playingItem) ? currentPlaybackPosition : 0;
                var here = item.DescriptionAt(pos) ?? item.FrameDescriptions[0];
                parts.Add(LF("desc_read_part", FormatTimeDetailed(here.Start), here.Text, item.FrameDescriptions.Count));
            }
            LogMessage(parts.Count > 0 ? string.Join(" ", parts) : L("desc_read_none"), true);
        }

        private async void GenerateScenario_Click(object sender, RoutedEventArgs e)
        {
            LogMessage("GenerateScenario_Click pozvan", true);

            if (string.IsNullOrWhiteSpace(txtAnimationPrompt.Text))
            {
                LogMessage(L("enter_animation_desc"), true);
                return;
            }

            string apiToken = GetCloudflareApiKey();
            if (string.IsNullOrEmpty(apiToken))
            {
                var dialog = new ApiKeyDialog("cloudflare", LanguageManager.T("loc_cf_token_story"));
                if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.ApiKey))
                {
                    apiToken = dialog.ApiKey;
                    SaveCloudflareApiKey(apiToken);
                    LogMessage(L("cf_api_key_saved2"), true);
                }
                else
                {
                    LogMessage(L("cf_api_cancelled2"), true);
                    return;
                }
            }

            var availableImages = timelineItems.Where(i => i.IsImage).Select(i => i.Name).ToList();
            if (availableImages.Count == 0)
            {
                LogMessage("No images on the timeline to create an animation", true);
                return;
            }

            LogMessage(L("ai_generating_scenario"), true);

            string prompt =
                "Create an animation scenario based on the following request: " + txtAnimationPrompt.Text + "\n" +
                "Available images: " + string.Join(", ", availableImages) + "\n" +
                "Return the result exclusively in JSON format with no extra text. " +
                "The JSON should be an array of scenes. Each scene should have: " +
                "imageName (image name), duration (seconds 3-10), " +
                "effect (Fade In/Out, Zoom In/Out, Slide Left/Right, None), " +
                "description (a short description in English). " +
                "Create between 3 and 6 scenes. Return ONLY the JSON array.";

            try
            {
                string result = await CallCloudflareAI(prompt, apiToken);
                LogMessage($"AI response (first 300 characters): {result?.Substring(0, Math.Min(300, result?.Length ?? 0))}", true);

                if (string.IsNullOrEmpty(result))
                {
                    LogMessage("AI did not return a response", true);
                    return;
                }

                string cleanedResult = result.Trim();

                if (cleanedResult.StartsWith("```"))
                    cleanedResult = cleanedResult.Substring(cleanedResult.IndexOf('\n') + 1);
                if (cleanedResult.EndsWith("```"))
                    cleanedResult = cleanedResult.Substring(0, cleanedResult.LastIndexOf("```"));

                int jsonStart = cleanedResult.IndexOf('[');
                int jsonEnd = cleanedResult.LastIndexOf(']');

                if (jsonStart < 0 || jsonEnd <= jsonStart)
                {
                    LogMessage("AI did not return a valid JSON array", true);
                    return;
                }

                string jsonArray = cleanedResult.Substring(jsonStart, jsonEnd - jsonStart + 1);
                string wrappedJson = $"{{\"scenes\": {jsonArray}}}";

                LogMessage($"JSON to parse: {wrappedJson.Substring(0, Math.Min(200, wrappedJson.Length))}", true);

                var scenes = JsonConvert.DeserializeObject<dynamic>(wrappedJson);

                _animationScenes.Clear();
                if (scenes?.scenes != null)
                {
                    foreach (var scene in scenes.scenes)
                    {
                        var newScene = new AnimationScene
                        {
                            ImageName = scene.imageName.ToString(),
                            Duration = double.TryParse(((string)scene.duration.ToString()).Replace(',', '.'),
                                           System.Globalization.NumberStyles.Float,
                                           System.Globalization.CultureInfo.InvariantCulture, out double sceneDur) && sceneDur > 0
                                           ? sceneDur : 3.0,
                            Effect = scene.effect.ToString(),
                            Description = scene.description.ToString(),
                            AvailableImages = availableImages,
                            AvailableEffects = new List<string> { "Fade In", "Fade Out", "Zoom In", "Zoom Out", "Slide Left", "Slide Right", "None" }
                        };
                        _animationScenes.Add(newScene);
                    }
                }
                var previewDialog = new AnimationPreviewDialog(_animationScenes, timelineItems.Where(i => i.IsImage).Select(i => i.Name).ToList());
                if (previewDialog.ShowDialog() == true)
                {
                    _animationScenes = previewDialog.GetScenes();
                    LogMessage(string.Format(L("scenario_confirmed"), _animationScenes.Count), true);
                    CreateAnimationFromScenes_Click(sender, e);
                }
                else
                {
                    LogMessage("Scenario generation cancelled", true);
                }
                lstAnimationScenes.ItemsSource = null;
                lstAnimationScenes.ItemsSource = _animationScenes;

                LogMessage($"Scenario generated: {_animationScenes.Count} scenes", true);
                PlayBeep();
            }
            catch (Exception ex)
            {
                LogMessage(string.Format(L("scenario_error"), ex.Message), true);
            }
        }

        private async void AISuggestLayout_Click(object sender, RoutedEventArgs e)
        {
            LogMessage("AISuggestLayout_Click called", true);

            var animacije = timelineItems.Where(i => i.Type == "Animation" || i.Name.Contains("animacija")).ToList();
            if (animacije.Count == 0)
            {
                LogMessage(L("no_animations"), true);
                return;
            }

            var audio = timelineItems.FirstOrDefault(i => i.IsAudio);
            double audioDuration = audio?.Duration ?? GetTotalDuration();

            string apiToken = GetCloudflareApiKey();
            if (string.IsNullOrEmpty(apiToken))
            {
                LogMessage(L("need_cf_key"), true);
                return;
            }

            var animacijeInfo = string.Join(", ", animacije.Select((a, i) => $"animation{i + 1}: {a.Duration}s"));

            string prompt =
                "I have an audio track that is " + audioDuration + " seconds long. " +
                "I have " + animacije.Count + " animations: " + animacijeInfo + ". " +
                "Suggest an optimal order and starting positions for these animations on the timeline " +
                "so that they cover the entire audio. " +
                "Return the result exclusively in JSON format with no extra text. " +
                "The JSON should contain a list called \"raspored\" where each element has: " +
                "animacija_index (a number from 1 to " + animacije.Count + "), " +
                "pocetak (number in seconds), kraj (number in seconds), " +
                "razlog (a short description in English).";

            LogMessage(L("ai_analyzing_audio"), true);

            try
            {
                string result = await CallCloudflareAI(prompt, apiToken);
                var layout = JsonConvert.DeserializeObject<AILayoutResponse>(result);

                if (layout?.raspored == null || layout.raspored.Count == 0)
                {
                    LogMessage("AI did not return a valid layout", true);
                    return;
                }

                var message = new StringBuilder();
                message.AppendLine(L("ai_arrangement_result"));
                foreach (var item in layout.raspored)
                {
                    if (item.animacija_index < 1 || item.animacija_index > animacije.Count) continue;  // v1.4: AI may return a bad index
                    var anim = animacije[item.animacija_index - 1];
                    message.AppendLine($"Animation {item.animacija_index}: '{anim.Name}' from {FormatTime(item.pocetak)} to {FormatTime(item.kraj)}. Reason: {item.razlog}");
                }
                message.AppendLine(L("arrangement_accept"));

                var resultDialog = WpfMessageBox.Show(message.ToString(), "AI Predlog rasporeda",
                                                   MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (resultDialog == MessageBoxResult.Yes)
                {
                    SaveState();

                    foreach (var anim in animacije)
                        timelineItems.Remove(anim);

                    foreach (var item in layout.raspored.OrderBy(p => p.pocetak))
                    {
                        if (item.animacija_index < 1 || item.animacija_index > animacije.Count) continue;
                        var anim = animacije[item.animacija_index - 1];
                        anim.UseFixedPosition = true;
                        anim.FixedPosition = Math.Max(0, item.pocetak);
                        anim.Duration = Math.Max(0.5, item.kraj - item.pocetak);
                        if (!timelineItems.Contains(anim)) timelineItems.Add(anim);
                    }

                    UpdateTimelineDisplay();
                    LogMessage(L("animations_arranged"), true);
                    PlayBeep();
                }
            }
            catch (Exception ex)
            {
                LogMessage(string.Format(L("arrangement_error"), ex.Message), true);
            }
        }

        private void AddScene_Click(object sender, RoutedEventArgs e)
        {
            var availableImages = timelineItems.Where(i => i.IsImage).Select(i => i.Name).ToList();
            var newScene = new AnimationScene
            {
                ImageName = availableImages.FirstOrDefault() ?? "",
                Duration = 5,
                Effect = "Fade In",
                Description = "Nova scena",
                AvailableImages = availableImages,
                AvailableEffects = new List<string> { "Fade In", "Fade Out", "Zoom In", "Zoom Out", "Slide Left", "Slide Right", "None" }
            };
            _animationScenes.Add(newScene);
            lstAnimationScenes.ItemsSource = null;
            lstAnimationScenes.ItemsSource = _animationScenes;
            LogMessage(LanguageManager.T("loc_scene_added"), true);
        }

        private void RemoveScene_Click(object sender, RoutedEventArgs e)
        {
            if (lstAnimationScenes.SelectedItem is AnimationScene scene)
            {
                _animationScenes.Remove(scene);
                lstAnimationScenes.ItemsSource = null;
                lstAnimationScenes.ItemsSource = _animationScenes;
                LogMessage(LanguageManager.T("loc_scene_deleted"), true);
                PlayBeep();
            }
            else
            {
                LogMessage(LanguageManager.T("loc_scene_select_delete"), true);
            }
        }

        private void MoveSceneUp_Click(object sender, RoutedEventArgs e)
        {
            if (lstAnimationScenes.SelectedItem is AnimationScene scene)
            {
                int index = _animationScenes.IndexOf(scene);
                if (index > 0)
                {
                    _animationScenes.RemoveAt(index);
                    _animationScenes.Insert(index - 1, scene);
                    lstAnimationScenes.ItemsSource = null;
                    lstAnimationScenes.ItemsSource = _animationScenes;
                    lstAnimationScenes.SelectedItem = scene;
                    LogMessage(LanguageManager.T("loc_scene_moved_up"), true);
                    PlayBeep();
                }
            }
        }

        private void MoveSceneDown_Click(object sender, RoutedEventArgs e)
        {
            if (lstAnimationScenes.SelectedItem is AnimationScene scene)
            {
                int index = _animationScenes.IndexOf(scene);
                if (index < _animationScenes.Count - 1)
                {
                    _animationScenes.RemoveAt(index);
                    _animationScenes.Insert(index + 1, scene);
                    lstAnimationScenes.ItemsSource = null;
                    lstAnimationScenes.ItemsSource = _animationScenes;
                    lstAnimationScenes.SelectedItem = scene;
                    LogMessage(LanguageManager.T("loc_scene_moved_down"), true);
                    PlayBeep();
                }
            }
        }

        private void SetAnimationPosition_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem animacija)
            {
                if (!double.TryParse(txtAnimationStartTime.Text.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double startTime))
                {
                    LogMessage("Enter a valid number of seconds", true);
                    return;
                }
                SaveState();
                animacija.UseFixedPosition = true;      // v1.4: Start alone was overwritten on the next refresh
                animacija.FixedPosition = Math.Max(0, startTime);
                UpdateTimelineDisplay();
                LogMessage($"Animacija '{animacija.Name}' postavljena na {FormatTime(startTime)}", true);
                PlayBeep();
            }
            else
            {
                LogMessage("Select an animation on the timeline first", true);
            }
        }

        private async void CreateAnimationFromScenes_Click(object sender, RoutedEventArgs e)
        {
            if (_animationScenes.Count == 0)
            {
                LogMessage("No scenes to create an animation", true);
                return;
            }

            LogMessage("Creating animation from scenes...", true);
            SaveState();

            foreach (var scene in _animationScenes)
            {
                var image = timelineItems.FirstOrDefault(i => i.IsImage && i.Name == scene.ImageName);
                if (image != null)
                {
                    var animacija = DeepCloneTimelineItem(image);
                    animacija.Name = $"Animation: {scene.Description}";
                    animacija.Type = "Animation";
                    animacija.Duration = scene.Duration;
                    animacija.Start = 0;
                    timelineItems.Add(animacija);
                }
            }
            UpdateTimelineDisplay();
            LogMessage($"Kreirano {_animationScenes.Count} animacija", true);
            PlayBeep();
        }

        private async Task<string> CallCloudflareAI(string prompt, string apiToken)
        {
            LogMessage(L("cf_ai_starting"), true);

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiToken}");
                client.Timeout = TimeSpan.FromSeconds(60);

                var requestBody = new
                {
                    messages = new[] { new { role = "user", content = prompt } },
                    max_tokens = 2000,
                    temperature = 0.7
                };

                string jsonBody = JsonConvert.SerializeObject(requestBody);
                LogMessage($"CallCloudflareAI: Request body: {jsonBody.Substring(0, Math.Min(200, jsonBody.Length))}", true);

                var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                // v1.4: own account ID + a current model (llama-2-7b-chat-int8 was retired by Cloudflare)
                string accountId = GetCloudflareAccountId();
                if (string.IsNullOrEmpty(accountId)) return "";
                string url = $"https://api.cloudflare.com/client/v4/accounts/{accountId}/ai/run/@cf/meta/llama-3.1-8b-instruct";
                LogMessage($"CallCloudflareAI: Pozivam URL: {url}", true);

                try
                {
                    var response = await client.PostAsync(url, content);
                    string responseBody = await response.Content.ReadAsStringAsync();

                    LogMessage($"CallCloudflareAI: Status code: {response.StatusCode}", true);
                    LogMessage($"CallCloudflareAI: Response (prvih 500): {responseBody.Substring(0, Math.Min(500, responseBody.Length))}", true);

                    if (response.IsSuccessStatusCode)
                    {
                        var result = JsonConvert.DeserializeObject<dynamic>(responseBody);
                        string aiResponse = result?.result?.response?.ToString();
                        LogMessage($"CallCloudflareAI: AI response: {aiResponse?.Substring(0, Math.Min(300, aiResponse?.Length ?? 0))}", true);
                        return aiResponse ?? "";
                    }
                    else
                    {
                        LogMessage(string.Format(L("cf_ai_api_error"), response.StatusCode, responseBody), true);
                        return "";
                    }
                }
                catch (Exception ex)
                {
                    LogMessage($"CallCloudflareAI: Izuzetak: {ex.Message}", true);
                    return "";
                }
            }
        }

        private void SetTransitionDuration_Click(object sender, RoutedEventArgs e)
        {
            if (lstTransitions.SelectedItem is TransitionEffect transition)
            {
                var dialog = new DurationDialog(transition.Duration);
                if (dialog.ShowDialog() == true)
                {
                    transition.Duration = dialog.Duration;
                    LogMessage(string.Format(L("transition_duration"), transition.Name, dialog.Duration), true);
                    PlayBeep();
                }
            }
            else
            {
                LogMessage("Select a transition first", true);
            }
        }

        private void SetZoomKeyframe_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                double time = PositionInClip(item);   // v1.4: was "player time − clip start"
                if (time < 0) time = 0;
                if (time > item.Duration) time = item.Duration;

                var existing = item.Keyframes.FirstOrDefault(k => Math.Abs(k.Time - time) < 0.1);
                if (existing != null)
                {
                    existing.Zoom = sldZoom.Value;
                    LogMessage(string.Format(L("keyframe_zoom"), FormatTime(time), sldZoom.Value), true);
                    UpdateKeyframeList(item);
                    PlayBeep();
                }
                else
                {
                    LogMessage($"No keyframe at {FormatTime(time)}. Add a keyframe first (Ctrl+K)", true);
                }
            }
            else
            {
                LogMessage("Select a clip first", true);
            }
        }

        private void SetRotationKeyframe_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                double time = PositionInClip(item);   // v1.4: was "player time − clip start"
                if (time < 0) time = 0;
                if (time > item.Duration) time = item.Duration;

                var existing = item.Keyframes.FirstOrDefault(k => Math.Abs(k.Time - time) < 0.1);
                if (existing != null)
                {
                    existing.Rotation = sldRotation.Value;
                    LogMessage(string.Format(L("keyframe_rotation"), FormatTime(time), sldRotation.Value), true);
                    UpdateKeyframeList(item);
                    PlayBeep();
                }
                else
                {
                    LogMessage($"No keyframe at {FormatTime(time)}. Add a keyframe first (Ctrl+K)", true);
                }
            }
            else
            {
                LogMessage("Select a clip first", true);
            }
        }

        private void SetPositionKeyframe_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                double time = PositionInClip(item);   // v1.4: was "player time − clip start"
                if (time < 0) time = 0;
                if (time > item.Duration) time = item.Duration;

                var existing = item.Keyframes.FirstOrDefault(k => Math.Abs(k.Time - time) < 0.1);
                if (existing != null)
                {
                    existing.X = sldX.Value;
                    existing.Y = sldY.Value;
                    LogMessage(string.Format(L("keyframe_position"), FormatTime(time), sldX.Value, sldY.Value), true);
                    UpdateKeyframeList(item);
                    PlayBeep();
                }
                else
                {
                    LogMessage($"No keyframe at {FormatTime(time)}. Add a keyframe first (Ctrl+K)", true);
                }
            }
            else
            {
                LogMessage("Select a clip first", true);
            }
        }

        private async Task<string> GenerateImageDescriptionAsync(string imagePath, string contextPrompt = "")
        {
            string openAiKey = GetOpenAiApiKey();
            if (!string.IsNullOrEmpty(openAiKey))
                return await DescribeWithGPT4Vision(imagePath, contextPrompt, openAiKey);

            string cfKey = GetCloudflareApiKey();
            if (!string.IsNullOrEmpty(cfKey))
                return await DescribeWithCloudflareLlava(imagePath, contextPrompt, cfKey);

            return "";
        }

        private async Task<string> DescribeWithGPT4Vision(string imagePath, string contextPrompt, string apiKey)
        {
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(imagePath);
                string b64 = Convert.ToBase64String(bytes);
                string prompt = string.IsNullOrWhiteSpace(contextPrompt)
                    ? "Describe the image briefly and clearly in English, in at most two sentences. The description is intended for blind users."
                    : "Describe the image briefly in English. Context: " + contextPrompt + ". At most two sentences.";

                var body = new
                {
                    model = "gpt-4o",
                    max_tokens = 150,
                    messages = new object[]
                    {
                        new { role = "user", content = new object[]
                        {
                            new { type = "text", text = prompt },
                            new { type = "image_url", image_url = new { url = "data:image/jpeg;base64," + b64 } }
                        }}
                    }
                };

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + apiKey);
                var resp = await client.PostAsync(
                    "https://api.openai.com/v1/chat/completions",
                    new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json"));

                if (resp.IsSuccessStatusCode)
                {
                    var text = await resp.Content.ReadAsStringAsync();
                    dynamic obj = JsonConvert.DeserializeObject(text);
                    return obj?.choices?[0]?.message?.content?.ToString()?.Trim() ?? "";
                }
            }
            catch (Exception ex) { LogMessage("GPT-4 Vision error: " + ex.Message, false); }
            return "";
        }

        private async Task<string> DescribeWithCloudflareLlava(string imagePath, string contextPrompt, string apiKey)
        {
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(imagePath);
                string b64 = Convert.ToBase64String(bytes);
                string accountId = GetCloudflareAccountId(askIfMissing: false);
                if (string.IsNullOrEmpty(accountId)) return "";
                string url = "https://api.cloudflare.com/client/v4/accounts/" + accountId + "/ai/run/@cf/llava-hf/llava-1.5-7b-hf";
                string prompt = string.IsNullOrWhiteSpace(contextPrompt)
                    ? "Describe the image briefly and clearly in English, in at most two sentences. The description is intended for blind users."
                    : "Describe the image briefly in English. Context: " + contextPrompt + ". At most two sentences.";

                var requestBody = new
                {
                    messages = new object[]
                    {
                        new { role = "user", content = new object[]
                        {
                            new { type = "text", text = prompt },
                            new { type = "image_url", image_url = new { url = "data:image/jpeg;base64," + b64 } }
                        }}
                    }
                };

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + apiKey);
                var resp = await client.PostAsync(url,
                    new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json"));

                if (resp.IsSuccessStatusCode)
                {
                    var text = await resp.Content.ReadAsStringAsync();
                    var obj = JsonConvert.DeserializeObject<LlavaResponse>(text);
                    return obj?.result?.response?.Trim() ?? "";
                }
            }
            catch (Exception ex) { LogMessage("Cloudflare llava error: " + ex.Message, false); }
            return "";
        }
        private void SetImagePosition_Click(object sender, RoutedEventArgs e)
        {
            if (!(nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item) || (!item.IsImage && !item.IsVideo))
            {
                LogMessage("Select an image or video clip on the timeline.", true);
                return;
            }

            var audioItem = timelineItems.FirstOrDefault(i => i.IsAudio);
            double audioDur = audioItem != null ? audioItem.Duration : GetTotalDuration();

            // v1.4: the snapshot is taken BEFORE the dialog, because "Evenly distribute"
            // changes the clips while the dialog is open (the old snapshot taken afterwards
            // made Undo restore the already-changed timeline, and Cancel kept the changes).
            SaveState();
            var dlg = new PositionDialog(item, timelineItems, audioDur);
            if (dlg.ShowDialog() != true)
            {
                if (dlg.AutoPlaceAll) RestoreLastUndo();
                else DiscardLastUndo();
                return;
            }
            {
                if (dlg.AutoPlaceAll)
                {
                    // The dialog already distributed the clips of this track evenly.
                    LogMessage("Even distribution applied.", true);
                }
                else if (dlg.SelectedPosition >= 0)
                {
                    LogMessage($"DEBUG: Setting '{item.Name}' to position {dlg.SelectedPosition:F2}s, duration {dlg.SelectedDuration:F2}s", true);

                    item.FixedPosition = dlg.SelectedPosition;
                    item.Duration = dlg.SelectedDuration;
                    item.UseFixedPosition = true;
                }

                // Ponovo rasporedi sve klipove
                ReorderAllClips();

                LogMessage(LanguageManager.F("loc_image_placed", item.Name, FormatTime(dlg.SelectedPosition)), true);
                UpdateTimelineDisplay();
                PlayBeep();
            }
        }
        private void ReorderAllClips()
        {
            // v1.4: this used to squeeze BOTH video tracks into one lane, push every
            // unpinned clip behind the last pinned one, re-sort the list and drop any item
            // that was neither audio nor picture. The shared layout keeps tracks parallel
            // and lets unpinned clips fill the gaps between pinned ones.
            RecalculatePositions();
            UpdateTimelineDisplay();
        }
        private void SetupTransitionContextMenus()
        {
            if (lstTransitions == null) return;

            var ctxTrans = new ContextMenu();
            var miApply = new MenuItem { Header = L("ctx_trans_apply_selected") };
            miApply.Click += (s, ev) => ApplyTransitionToSelectedClip();
            var miApplyAll = new MenuItem { Header = L("ctx_trans_apply_all") };
            miApplyAll.Click += (s, ev) => ApplyTransitionToAll();
            var miDesc = new MenuItem { Header = L("ctx_trans_read_desc") };
            miDesc.Click += (s, ev) => DescribeSelectedTransition();
            ctxTrans.Items.Add(miApply);
            ctxTrans.Items.Add(miApplyAll);
            ctxTrans.Items.Add(new Separator());
            ctxTrans.Items.Add(miDesc);
            lstTransitions.ContextMenu = ctxTrans;

            lstTransitions.KeyDown += (s, ev) =>
            {
                if (ev.Key == Key.Return || ev.Key == Key.Space) { ApplyTransitionToSelectedClip(); ev.Handled = true; }
                else if (ev.Key == Key.F1) { DescribeSelectedTransition(); ev.Handled = true; }
            };
        }

        /// <summary>
        /// v1.4 — stores the transition ON the outgoing clip, which is what the render reads.
        /// Before, transitions only went into a list of clip numbers that the render never
        /// looked at, so every transition chosen in the Transitions tab was ignored.
        /// </summary>
        private void SetClipTransition(TimelineItem outgoing, TransitionEffect t)
        {
            if (outgoing == null || t == null) return;
            outgoing.TransitionOut = TransitionEffect.ToXfadeName(t.Type);
            outgoing.TransitionOutDuration = Math.Max(0.1, t.Duration);
        }

        private void ClearClipTransition(int clipIndex)
        {
            if (clipIndex >= 0 && clipIndex < timelineItems.Count)
            {
                timelineItems[clipIndex].TransitionOut = null;
                timelineItems[clipIndex].TransitionOutDuration = 0;
            }
        }

        /// <summary>Previous clip on the same track (the transition goes from it into <paramref name="item"/>).</summary>
        private TimelineItem PreviousOnTrack(TimelineItem item)
        {
            RecalculatePositions();
            return timelineItems
                .Where(i => i.TrackIndex == item.TrackIndex && !ReferenceEquals(i, item) && i.Start < item.Start)
                .OrderByDescending(i => i.Start)
                .FirstOrDefault();
        }

        private void ApplyTransitionToSelectedClip()
        {
            if (!(lstTransitions.SelectedItem is TransitionEffect tpl))
            { LogMessage("Select a transition in the transitions list.", true); return; }
            if (nativeListView?.SelectedItems.Count == 0 || !(nativeListView.SelectedItems[0].Tag is TimelineItem item))
            { LogMessage("Select a clip that is not the first — the transition goes between the previous clip and the selected one.", true); return; }
            int idx = timelineItems.IndexOf(item);
            var prevItem = PreviousOnTrack(item);
            if (prevItem == null) { LogMessage("Select a clip that is not the first", true); return; }
            int prevIdx = timelineItems.IndexOf(prevItem);
            SaveState();
            var t = new TransitionEffect
            {
                Name = tpl.Name,
                Type = tpl.Type,
                Duration = transitionDuration,
                ClipIndex1 = prevIdx,
                ClipIndex2 = idx
            };
            transitions.RemoveAll(x => x.ClipIndex1 == prevIdx || x.ClipIndex2 == idx);
            transitions.Add(t);
            SetClipTransition(prevItem, t);
            string c1 = prevItem.Name;
            string c2 = item.Name;
            LogMessage("Tranzicija '" + t.Name + "' (" + transitionDuration.ToString("F1") + "s) izmedju '" + c1 + "' i '" + c2 + "'.", true);
            PlayBeep();
            UpdateTransitionIndicators();
        }

        private void ApplyTransitionToAll()
        {
            if (!(lstTransitions.SelectedItem is TransitionEffect tpl))
            { LogMessage("Select a transition in the list.", true); return; }
            var vids = timelineItems.Where(i => i.IsVideo || i.IsImage).ToList();
            if (vids.Count < 2) { LogMessage("At least 2 video/image clips are required.", true); return; }
            SaveState();
            transitions.Clear();
            RecalculatePositions();
            vids = vids.OrderBy(v => v.Start).ToList();
            for (int i = 1; i < vids.Count; i++)
            {
                int g1 = timelineItems.IndexOf(vids[i - 1]);
                int g2 = timelineItems.IndexOf(vids[i]);
                var t = new TransitionEffect { Name = tpl.Name, Type = tpl.Type, Duration = transitionDuration, ClipIndex1 = g1, ClipIndex2 = g2 };
                transitions.Add(t);
                SetClipTransition(vids[i - 1], t);
            }
            LogMessage("Tranzicija '" + tpl.Name + "' primenjena na " + transitions.Count + " prelaza.", true);
            PlayBeep();
            UpdateTransitionIndicators();
        }

        private void DescribeSelectedTransition()
        {
            if (!(lstTransitions.SelectedItem is TransitionEffect t))
            { LogMessage("No transition selected.", true); return; }
            string desc;
            if (t.Type == TransitionType.Fade) desc = LanguageManager.T("loc_fade_desc");
            else if (t.Type == TransitionType.Crossfade) desc = "Crossfade: the two clips gradually blend into each other.";
            else if (t.Type == TransitionType.SlideLeft) desc = "Slide Left: the new clip enters from the right and pushes the old one left.";
            else if (t.Type == TransitionType.SlideRight) desc = "Slide Right: the new clip enters from the left and pushes the old one right.";
            else if (t.Type == TransitionType.SlideUp) desc = "Slide Up: the new clip enters from below and pushes the old one up.";
            else if (t.Type == TransitionType.SlideDown) desc = "Slide Down: the new clip enters from above and pushes the old one down.";
            else if (t.Type == TransitionType.WipeLeft) desc = "Wipe Left: the new clip is revealed like a curtain from right to left.";
            else if (t.Type == TransitionType.WipeRight) desc = "Wipe Right: the new clip is revealed like a curtain from left to right.";
            else if (t.Type == TransitionType.ZoomIn) desc = "Zoom In: the next clip zooms in from the center and covers the previous one.";
            else if (t.Type == TransitionType.ZoomOut) desc = "Zoom Out: the previous clip shrinks and reveals the next one.";
            else desc = t.Name;
            LogMessage("Effect: " + t.Name + ". " + desc, true);
        }

        private void ShowTransitionOnSelectedClip()
        {
            if (nativeListView?.SelectedItems.Count == 0 || !(nativeListView.SelectedItems[0].Tag is TimelineItem item))
            { LogMessage("No clip selected.", true); return; }
            int idx = timelineItems.IndexOf(item);
            var before = transitions.FirstOrDefault(t => t.ClipIndex2 == idx);
            var after = transitions.FirstOrDefault(t => t.ClipIndex1 == idx);
            string msg = "";
            if (before != null) msg += "Before clip: '" + before.Name + "' (" + before.Duration.ToString("F1") + "s). ";
            if (after != null) msg += "After clip: '" + after.Name + "' (" + after.Duration.ToString("F1") + "s). ";
            if (msg == "") msg = "No transitions on this clip.";
            LogMessage(msg, true);
        }

        private void RemoveTransitionFromSelectedClip()
        {
            if (nativeListView?.SelectedItems.Count == 0 || !(nativeListView.SelectedItems[0].Tag is TimelineItem item))
            { LogMessage("No clip selected.", true); return; }
            int idx = timelineItems.IndexOf(item);
            foreach (var t in transitions.Where(x => x.ClipIndex1 == idx || x.ClipIndex2 == idx).ToList())
                ClearClipTransition(t.ClipIndex1);
            int removed = transitions.RemoveAll(t => t.ClipIndex1 == idx || t.ClipIndex2 == idx);
            if (removed > 0) { SaveState(); LogMessage("Removed " + removed + " transition(s) from clip " + (idx + 1) + ".", true); PlayBeep(); UpdateTransitionIndicators(); }
            else LogMessage("No transitions on this clip.", true);
        }

        private void UpdateTransitionIndicators()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // Opcionalno
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // Base row height (px) the timeline ListView uses at scale 1.0.
        // Rows in LVS_REPORT (Details) view size to the taller of the font
        // and the small-image-list icon height, so an invisible 1px-wide
        // image list is the standard trick to control row height directly.
        private const int TimelineBaseRowHeight = 20;
        private System.Windows.Forms.ImageList _timelineRowHeightImageList;

        /// <summary>
        /// Rescales the native WinForms timeline ListView for Low Vision mode.
        /// WPF's LayoutTransform (used everywhere else via UiScaling) does not
        /// reach into a WindowsFormsHost, so the font, column widths, and row
        /// height are resized here by hand, from the same base sizes
        /// InitNativeListView uses. Safe to call before the list view exists
        /// (e.g. during startup ordering) — it just no-ops.
        /// </summary>
        private void ApplyNativeListViewScale(double scale)
        {
            if (nativeListView == null) return;

            try
            {
                nativeListView.Font = new System.Drawing.Font("Segoe UI", TimelineBaseFontSize * (float)scale);

                var columns = nativeListView.Columns;
                for (int i = 0; i < columns.Count && i < TimelineColumnBaseWidths.Length; i++)
                    columns[i].Width = (int)Math.Round(TimelineColumnBaseWidths[i] * scale);

                // Font alone won't grow the row height enough to feel
                // comfortably "enlarged" at 1.5x+, so drive it explicitly
                // via a 1px-wide image list whose height sets the row height.
                var oldImageList = _timelineRowHeightImageList;
                var newImageList = new System.Windows.Forms.ImageList
                {
                    ImageSize = new System.Drawing.Size(1, (int)Math.Round(TimelineBaseRowHeight * scale)),
                    ColorDepth = System.Windows.Forms.ColorDepth.Depth32Bit
                };
                nativeListView.SmallImageList = newImageList;
                _timelineRowHeightImageList = newImageList;
                oldImageList?.Dispose(); // avoid leaking the GDI handle on repeated scale changes
            }
            catch (Exception ex)
            {
                LogMessage("Failed to scale timeline list view: " + ex.Message, false);
            }
        }

        /// <summary>
        /// Increases the Low Vision scale factor by one step (up to 2.5x) and
        /// re-applies it immediately if Low Vision mode is currently active.
        /// Bound to the View menu and Ctrl+Shift+OemPlus.
        /// </summary>
        private void IncreaseLowVisionTextSize_Click(object sender, RoutedEventArgs e)
        {
            _lowVisionScale = Math.Min(LowVisionScaleMax, _lowVisionScale + LowVisionScaleStep);
            if (_uiMode == UIMode.LowVision) SetUIMode(UIMode.LowVision, announce: false);
            LogMessage($"Low vision text size: {_lowVisionScale:0.##}x", true);
        }

        /// <summary>
        /// Decreases the Low Vision scale factor by one step (down to 1.0x)
        /// and re-applies it immediately if Low Vision mode is currently
        /// active. Bound to the View menu and Ctrl+Shift+OemMinus.
        /// </summary>
        private void DecreaseLowVisionTextSize_Click(object sender, RoutedEventArgs e)
        {
            _lowVisionScale = Math.Max(LowVisionScaleMin, _lowVisionScale - LowVisionScaleStep);
            if (_uiMode == UIMode.LowVision) SetUIMode(UIMode.LowVision, announce: false);
            LogMessage($"Low vision text size: {_lowVisionScale:0.##}x", true);
        }

        private void InitNativeListView()
        {
            try
            {
                if (wfhTimeline?.Child is WinForms.ListView listView)
                {
                    nativeListView = listView;
                    nativeListView.View = WinForms.View.Details;
                    nativeListView.FullRowSelect = true;
                    nativeListView.GridLines = false;
                    nativeListView.MultiSelect = true;
                    nativeListView.HideSelection = false;
                    nativeListView.BackColor = System.Drawing.Color.FromArgb(30, 30, 30);
                    nativeListView.ForeColor = System.Drawing.Color.White;
                    nativeListView.Font = new System.Drawing.Font("Segoe UI", 10);

                    nativeListView.Columns.Clear();
                    nativeListView.Columns.Add(L("listview_col_num"), 50, WinForms.HorizontalAlignment.Center);
                    nativeListView.Columns.Add(L("listview_col_name"), 350, WinForms.HorizontalAlignment.Left);
                    nativeListView.Columns.Add(L("listview_col_type"), 60, WinForms.HorizontalAlignment.Center);
                    nativeListView.Columns.Add(L("listview_col_duration"), 80, WinForms.HorizontalAlignment.Center);
                    nativeListView.Columns.Add(L("listview_col_start"), 80, WinForms.HorizontalAlignment.Center);
                    nativeListView.Columns.Add(L("listview_col_end"), 80, WinForms.HorizontalAlignment.Center);
                    nativeListView.Columns.Add(L("listview_col_audio_desc"), 300, WinForms.HorizontalAlignment.Left);

                    nativeListView.SelectedIndexChanged += NativeListView_SelectedIndexChanged;
                    nativeListView.KeyDown += NativeListView_KeyDown;
                    nativeListView.MouseDoubleClick += (s, e) =>
                    {
                        if (nativeListView.SelectedItems.Count > 0)
                            TogglePlay_Click(null, null);
                    };

                    var contextMenu = new WinForms.ContextMenuStrip();
                    contextMenu.Items.Add("✂ " + L("cut"), null, (s, e) => CutClip_Click(null, null));
                    contextMenu.Items.Add("📋 " + L("copy"), null, (s, e) => CopyClip_Click(null, null));
                    contextMenu.Items.Add(L("ctx_paste_clip"), null, (s, e) => PasteClip_Click(null, null));
                    contextMenu.Items.Add(new WinForms.ToolStripSeparator());
                    contextMenu.Items.Add(L("ctx_delete_clip"), null, (s, e) => RemoveFromTimeline_Click(null, null));
                    contextMenu.Items.Add(new WinForms.ToolStripSeparator());
                    contextMenu.Items.Add("⬅ " + L("move_left"), null, (s, e) => MoveLeft_Click(null, null));
                    contextMenu.Items.Add(L("ctx_move_right"), null, (s, e) => MoveRight_Click(null, null));
                    contextMenu.Items.Add(new WinForms.ToolStripSeparator());
                    contextMenu.Items.Add("⬅ " + L("ctx_move_first"), null, (s, e) => MoveToFirst_Click(null, null));
                    contextMenu.Items.Add(L("ctx_move_last"), null, (s, e) => MoveToLast_Click(null, null));
                    contextMenu.Items.Add("🔢 " + L("ctx_set_position"), null, (s, e) => MoveToPosition_Click(null, null));
                    contextMenu.Items.Add(new WinForms.ToolStripSeparator());
                    contextMenu.Items.Add("⏱ " + L("mnu_set_duration"), null, (s, e) => SetClipDuration_Click(null, null));
                    contextMenu.Items.Add(L("ctx_set_volume"), null, (s, e) => SetClipVolume_Click(null, null));
                    contextMenu.Items.Add(new WinForms.ToolStripSeparator());
                    contextMenu.Items.Add("🎤 " + L("ctx_add_audio_desc"), null, (s, e) => AddAudioDescription_Click(null, null));
                    contextMenu.Items.Add("🤖 " + L("ctx_auto_describe"), null, (s, e) => AutoDescribeImage_Click(null, null));
                    contextMenu.Items.Add(L("ctx_read_audio"), null, (s, e) => ReadAudioDescription_Click(null, null));
                    contextMenu.Items.Add(new WinForms.ToolStripSeparator());
                    contextMenu.Items.Add(L("ctx_set_position"), null, (s, e) => SetImagePosition_Click(null, null));
                    contextMenu.Items.Add(new WinForms.ToolStripSeparator());
                    contextMenu.Items.Add("📝 " + L("ctx_add_intro_text"), null, (s, e) => AddIntroText_Click(null, null));
                    contextMenu.Items.Add("📝 " + L("ctx_add_outro_text"), null, (s, e) => AddOutroText_Click(null, null));
                    contextMenu.Items.Add(new WinForms.ToolStripSeparator());
                    contextMenu.Items.Add("🎨 " + L("ctx_add_text_to_image"), null, (s, e) => AddTextToImage_Click(null, null));
                    nativeListView.ContextMenuStrip = contextMenu;
                    nativeListView.AccessibleName = "List of clips on the timeline. Use the arrow keys to navigate. Ctrl+Space for play/pause. Page Up/Down for quick scrolling.";

                    LogMessage("Win32 ListView inicijalizovan - kolone postavljene", true);
                    UpdateTimelineDisplay();
                }
                else
                {
                    LogMessage(L("listview_error"), true);
                }
            }
            catch (Exception ex)
            {
                LogMessage(string.Format(L("listview_init_error"), ex.Message), true);
            }
        }

        private void NativeListView_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_selectionProcessing) return;

            _selectionProcessing = true;
            _selectionDebounceTimer.Stop();
            _selectionDebounceTimer.Start();

            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                _pendingPlaybackItem = item;
                if (graphicTimeline != null) { graphicTimeline.Selected = item; graphicTimeline.InvalidateVisual(); }
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_selectionProcessing)
                {
                    ProcessSelectedItem();
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void ProcessSelectedItem()
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                // ── JAWS/NVDA correct reading order ──────────────────────────────
                // ALWAYS: "N of TOTAL, Type: Name, duration X, start Y, end Z"
                // NIKAD:  statusna poruka ispred naziva fajla
                //
                // Example: "1 of 21, Video: During spring and autumn, duration 00:09, start 00:04, end 00:13"
                //
                string typeLabel = item.Type switch
                {
                    "Video" => "Video",
                    "Audio" => "Audio",
                    "Image" => "Slika",
                    _ => item.Type ?? "Unknown"
                };
                string duration = TimeSpan.FromSeconds(item.Duration).ToString(@"mm\:ss");
                string start = TimeSpan.FromSeconds(item.Start).ToString(@"mm\:ss");
                string end = TimeSpan.FromSeconds(item.End).ToString(@"mm\:ss");
                string total = nativeListView.Items.Count.ToString();

                // Ime klipa: koristi AudioDescription ako postoji (bolji kontekst),
                // otherwise just the filename without path and extension
                // v1.4: the name always comes first; a real picture description follows it. AI tags
                // ("context:…") and old error texts ("Could not extract a frame…") are not read —
                // they used to replace the clip's name.
                string clipName = Path.GetFileNameWithoutExtension(item.Name ?? string.Empty);
                string spokenDesc = UsableDescription(item.AudioDescription);
                if (spokenDesc != null) clipName += " — " + spokenDesc;

                // Format that JAWS reads naturally and in the right order:
                // "1 of 21, Video: Clip name, duration 00:09, start 00:04, end 00:13"
                string msg = string.Format(L("timeline_item_label"), item.Index, total, typeLabel, clipName, duration, start, end);

                LogMessage(msg, true);

                // UIA provider notification — refresh accessibility tree for JAWS/NVDA
                nativeListView.Invoke((System.Windows.Forms.MethodInvoker)(() =>
                {
                    nativeListView.Refresh();
                }));
            }
        }

        private void NativeListView_KeyDown(object sender, WinFormsKeyEventArgs e)
        {
            // v1.4 (R3/R5): the clip list is a Windows Forms control, and key presses inside it
            // do not always reach the WPF window's shortcuts. The playback and editing keys are
            // therefore handled here as well; DuplicateKey makes sure a key that arrives through
            // both paths acts only once.
            bool noMods = !e.Control && !e.Alt && !e.Shift;
            if (e.KeyCode == WinForms.Keys.Space && e.Control && e.Shift && !e.Alt)
            {
                if (!DuplicateKey("mix")) PlayMix_Click(null, null);
                e.Handled = true; e.SuppressKeyPress = true;
                return;
            }
            if (e.KeyCode == WinForms.Keys.Space && e.Control && !e.Shift && !e.Alt)
            {
                if (!DuplicateKey("play")) TogglePlay_Click(null, null);
                e.Handled = true; e.SuppressKeyPress = true;
                return;
            }
            if ((e.KeyCode == WinForms.Keys.Left || e.KeyCode == WinForms.Keys.Right) && e.Alt && !e.Control)
            {
                Key k = e.KeyCode == WinForms.Keys.Right ? Key.Right : Key.Left;
                if (!DuplicateKey("seek" + k + e.Shift))
                    SeekBy((k == Key.Right ? 1 : -1) * (e.Shift ? 100 : 5000));
                e.Handled = true; e.SuppressKeyPress = true;
                return;
            }
            if (e.KeyCode == WinForms.Keys.S && e.Control && e.Alt && !e.Shift)
            {
                SetClipSpeed_Click(null, null);
                e.Handled = true; e.SuppressKeyPress = true;
                return;
            }
            if (e.KeyCode == WinForms.Keys.V && e.Control && e.Shift && !e.Alt)
            {
                if (!DuplicateKey("volpoint")) AddVolumePoint_Click(null, null);
                e.Handled = true; e.SuppressKeyPress = true;
                return;
            }
            if (e.KeyCode == WinForms.Keys.F && e.Control && e.Shift && !e.Alt)
            {
                if (!DuplicateKey("descframe")) DescribeFrameHere_Click(null, null);
                e.Handled = true; e.SuppressKeyPress = true;
                return;
            }
            if (e.KeyCode == WinForms.Keys.D && e.Control && e.Alt && !e.Shift)
            {
                if (!DuplicateKey("descparts")) DescribeClipParts_Click(null, null);
                e.Handled = true; e.SuppressKeyPress = true;
                return;
            }
            if (noMods)
            {
                Key wk = e.KeyCode switch
                {
                    WinForms.Keys.J => Key.J,
                    WinForms.Keys.K => Key.K,
                    WinForms.Keys.L => Key.L,
                    WinForms.Keys.Q => Key.Q,
                    WinForms.Keys.W => Key.W,
                    WinForms.Keys.Oemcomma => Key.OemComma,
                    WinForms.Keys.OemPeriod => Key.OemPeriod,
                    _ => Key.None
                };
                if (wk != Key.None && HandleEditingKey(wk))
                {
                    e.Handled = true; e.SuppressKeyPress = true;   // no type-ahead jump in the list
                    return;
                }
            }

            if (e.Control && e.KeyCode == WinForms.Keys.C)
            {
                CopyClip_Click(null, null);
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == WinForms.Keys.V)
            {
                PasteClip_Click(null, null);
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == WinForms.Keys.X)
            {
                CutClip_Click(null, null);
                e.Handled = true;
            }
            else if (e.KeyCode == WinForms.Keys.Delete)
            {
                RemoveFromTimeline_Click(null, null);
                e.Handled = true;
            }
            else if (e.KeyCode == WinForms.Keys.F5)
            {
                FinalRender_Click(null, null);
                e.Handled = true;
            }
            else if ((e.KeyCode == WinForms.Keys.Space || e.KeyCode == WinForms.Keys.Enter) && !e.Control && !e.Alt)
            {
                // Both Space and Enter trigger play — screen readers often use Enter
                if (!DuplicateKey("play")) TogglePlay_Click(null, null);
                e.Handled = true;
            }
            else if (e.KeyCode == WinForms.Keys.F6)
            {
                // F6 reads full description of selected clip (for JAWS/NVDA)
                ProcessSelectedItem();
                e.Handled = true;
            }
        }

        private void UpdateNativeListView()
        {
            if (nativeListView == null)
            {
                LogMessage("UpdateNativeListView: nativeListView is null", true);
                return;
            }

            try
            {
                // Zapamti indeks selektovanog elementa
                int selectedIndex = nativeListView.SelectedItems.Count > 0
                    ? nativeListView.SelectedItems[0].Index
                    : -1;

                nativeListView.BeginUpdate();
                nativeListView.Items.Clear();

                // v1.4: positions are computed for ALL tracks first (independently of the
                // track filter), so Start/End no longer change just because a filter is on.
                RecalculatePositions();

                var filteredItems = currentTrackFilter == -1
                    ? timelineItems
                    : timelineItems.Where(x => x.TrackIndex == currentTrackFilter).ToList();

                for (int i = 0; i < filteredItems.Count; i++)
                {
                    var item = filteredItems[i];
                    item.Index = i + 1;

                    var listItem = new WinForms.ListViewItem(item.Index.ToString());
                    listItem.SubItems.Add(item.Name);
                    listItem.SubItems.Add(item.TypeIcon);
                    listItem.SubItems.Add(TimeSpan.FromSeconds(item.Duration).ToString(@"mm\:ss"));
                    listItem.SubItems.Add(TimeSpan.FromSeconds(item.Start).ToString(@"mm\:ss"));
                    listItem.SubItems.Add(TimeSpan.FromSeconds(item.End).ToString(@"mm\:ss"));
                    listItem.SubItems.Add(item.AudioDescription ?? "");
                    listItem.Tag = item;

                    nativeListView.Items.Add(listItem);
                }

                // Vrati selekciju na isti indeks
                if (selectedIndex >= 0 && selectedIndex < nativeListView.Items.Count)
                {
                    nativeListView.Items[selectedIndex].Selected = true;
                    nativeListView.Items[selectedIndex].EnsureVisible();
                }
                else if (nativeListView.Items.Count > 0 && selectedIndex >= nativeListView.Items.Count)
                {
                    // Ako je obrisan zadnji, selektuj novi zadnji
                    nativeListView.Items[nativeListView.Items.Count - 1].Selected = true;
                }
                else if (selectedIndex == -1 && nativeListView.Items.Count > 0)
                {
                    // Ranije ništa nije bilo selektovano (npr. tek dodat prvi klip na
                    // praznu traku) — bez ovoga Cut i slične komande koje traže
                    // selektovan klip tiho ne rade dok korisnik ručno ne selektuje klip.
                    nativeListView.Items[0].Selected = true;
                    nativeListView.Items[0].EnsureVisible();
                }

                nativeListView.EndUpdate();

                if (txtTimelineInfo != null)
                {
                    string infoText = filteredItems.Count > 0
                        ? $"Total clips: {filteredItems.Count} | Total duration: {FormatTime(GetTotalDuration())} | Zoom: {zoomLevel:F1}x"
                        : "No clips on the timeline.";
                    txtTimelineInfo.Text = infoText;
                    // Help screen readers to read the updated status
                    AutomationProperties.SetName(txtTimelineInfo, infoText);
                }

                LogMessage(string.Format(L("listview_refreshed"), filteredItems.Count), false);
                RefreshGraphicTimeline();   // v1.4 (R7): the graphic view follows every list change
            }
            catch (Exception ex)
            {
                LogMessage(string.Format(L("listview_refresh_error"), ex.Message), true);
            }
        }

        private void PlaySelectedItem(TimelineItem item, double startInClip = 0, bool startPaused = false)
        {
            if (item == null || !File.Exists(item.Path)) return;

            try
            {
                if (_mediaPlayer.IsPlaying) _mediaPlayer.Stop();
                StopMixPreviewMode();   // v1.4 (R3): a single clip replaces the montage mix
                var oldMedia = _currentMedia;
                _currentMedia = new Media(_libVLC, item.Path);
                // v1.4: start where this clip really begins in its file (cut parts, highlights)
                // and stop where it ends, so the preview matches the render.
                double from = Math.Max(0, item.SourceOffset + Math.Max(0, startInClip));
                if (from > 0.01)
                    _currentMedia.AddOption($":start-time={from.ToString("F3", CultureInfo.InvariantCulture)}");
                // v1.4 (R5): a clip with a changed speed uses Duration × speed seconds of its file
                double clipSpeed = ClipSpeed(item);
                if (!item.IsImage && item.Duration > 0.1)
                    _currentMedia.AddOption($":stop-time={(item.SourceOffset + item.Duration * clipSpeed).ToString("F3", CultureInfo.InvariantCulture)}");
                if (startPaused) _currentMedia.AddOption(":start-paused");
                _shuttle = 0;
                _rewindTimer?.Stop();
                _mediaPlayer.Media = _currentMedia;
                oldMedia?.Dispose();
                _mediaPlayer.Volume = (int)item.Volume;
                _playingItem = item;
                currentPlaybackPosition = Math.Max(0, startInClip);
                currentVideoDuration = item.Duration;   // v1.4: "position X of Y" uses the clip's length, not the file's
                _mediaPlayer.Play();
                _mediaPlayer.SetRate((float)clipSpeed);
                isPlaying = !startPaused;
                StartFollowTimer();   // v1.4 (R4): saved picture descriptions follow the playhead
                if (btnPlay != null) btnPlay.Content = isPlaying ? "⏸ " + L("pause") : "▶ " + L("play");
            }
            catch (Exception ex)
            {
                LogMessage(string.Format(L("playback_error"), ex.Message), true);
            }
        }


        // ══════════════════════════════════════════════════════════════
        // POLLINATIONS.AI — besplatno generisanje slika, bez API kljuca
        // ══════════════════════════════════════════════════════════════

        private async void GenerateWithPollinations_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(currentProjectFolder))
            {
                System.Windows.MessageBox.Show("Save the project first!");
                return;
            }
            if (string.IsNullOrWhiteSpace(txtAIPrompt?.Text))
            {
                LogMessage("Enter an image description in the prompt field.", true);
                return;
            }

            if (btnGenerate != null) btnGenerate.IsEnabled = false;
            if (prgAI != null) prgAI.Visibility = System.Windows.Visibility.Visible;

            string[] prompts = txtAIPrompt.Text.Split(',');
            int generated = 0;

            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(90);
                for (int i = 0; i < prompts.Length; i++)
                {
                    string cleanP = prompts[i].Trim();
                    if (string.IsNullOrEmpty(cleanP)) continue;
                    LogMessage("Pollinations.ai generise: " + cleanP + "...", true);
                    try
                    {
                        // Pollinations.ai — samo URL sa promptom, nula konfiguracije
                        string url = "https://image.pollinations.ai/prompt/" +
                                     Uri.EscapeDataString(cleanP) +
                                     "?width=1024&height=576&nologo=true&seed=" + i;
                        byte[] bytes = await client.GetByteArrayAsync(url);
                        string imgPath = System.IO.Path.Combine(currentProjectFolder,
                            "Pol_" + Guid.NewGuid().ToString().Substring(0, 8) + ".jpg");
                        await File.WriteAllBytesAsync(imgPath, bytes);

                        string desc = await GenerateImageDescriptionAsync(imgPath, cleanP);
                        if (string.IsNullOrEmpty(desc)) desc = LanguageManager.F("loc_pollinations_desc", cleanP);

                        SaveState();
                        timelineItems.Add(new TimelineItem
                        {
                            Path = imgPath,
                            Duration = 5.0,
                            Name = "Pol: " + cleanP,
                            AudioDescription = desc,
                            Type = "Image",
                            Volume = 100,
                            TrackIndex = 0,
                            VideoEffect = new VideoEffectData()
                        });
                        generated++;
                        LogMessage("Image " + (i + 1) + " ready. Description: " + desc, true);
                        if (prgAI != null)
                            Dispatcher.Invoke(() => prgAI.Value = (i + 1) * 100 / prompts.Length);
                    }
                    catch (Exception ex) { LogMessage("Pollinations error: " + ex.Message, true); }
                    await Task.Delay(300);
                }
            }

            UpdateTimelineDisplay();
            if (btnGenerate != null) btnGenerate.IsEnabled = true;
            if (prgAI != null) prgAI.Visibility = System.Windows.Visibility.Collapsed;
            LogMessage("Pollinations.ai: generisano " + generated + "/" + prompts.Length + " slika.", true);
            PlayBeep();
        }

        // ══════════════════════════════════════════════════════════════
        // SKIA ANIMACIJE — animirani tekst, 7 stilova, bez eksternih alata
        // ══════════════════════════════════════════════════════════════

        private async void GenerateSkiaAnimation_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(currentProjectFolder))
            {
                System.Windows.MessageBox.Show("Save the project first!");
                return;
            }
            var dlg = new SkiaAnimationDialog();
            if (dlg.ShowDialog() != true) return;

            LogMessage("Generating SkiaSharp animation: " + dlg.AnimationText + "...", true);
            try
            {
                string outPath = System.IO.Path.Combine(currentProjectFolder,
                    "Skia_" + Guid.NewGuid().ToString().Substring(0, 8) + ".mp4");

                await Task.Run(() =>
                    SkiaAnimationEngine.RenderToVideo(
                        dlg.AnimationText,
                        dlg.AnimationStyle,
                        dlg.TextColor,
                        dlg.BgColor,
                        dlg.DurationSeconds,
                        outPath));

                string desc = "Text animation: " + dlg.AnimationText + ", style: " + dlg.AnimationStyle;
                SaveState();
                timelineItems.Add(new TimelineItem
                {
                    Path = outPath,
                    Duration = dlg.DurationSeconds,
                    Name = "Anim: " + dlg.AnimationText,
                    AudioDescription = desc,
                    Type = "Video",
                    Volume = 100,
                    TrackIndex = 0,
                    VideoEffect = new VideoEffectData()
                });
                UpdateTimelineDisplay();
                LogMessage("Skia animacija kreirana: " + System.IO.Path.GetFileName(outPath), true);
                PlayBeep();
            }
            catch (Exception ex)
            {
                LogMessage("Error generating animation: " + ex.Message, true);
            }
        }

        // ══════════════════════════════════════════════════════════════
        // CTRL+V FIX ZA AI PROMPT POLJE
        // ══════════════════════════════════════════════════════════════

        private void SetupAIPromptPaste()
        {
            if (txtAIPrompt == null) return;
            // PreviewKeyDown se okida PRE nego sto TextBox konzumira Ctrl+V
            txtAIPrompt.PreviewKeyDown += (s, ev) =>
            {
                if (ev.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    PastePrompt_Click(null, null);
                    ev.Handled = true;
                }
            };
        }
        private async void btnAutoArrange_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Find audio file
                var audioItem = timelineItems.FirstOrDefault(i => i.IsAudio);
                if (audioItem == null)
                {
                    LogMessage("No audio file on the timeline. Add a song first.", true);
                    return;
                }

                // Find all images
                var images = timelineItems.Where(i => i.IsImage &&
                    !i.Name.Contains("Intro") &&
                    !i.Name.Contains("Outro")).ToList();
                if (images.Count == 0)
                {
                    LogMessage("No images on the timeline. Add images first.", true);
                    return;
                }

                // Otvori dijalog
                var dialog = new AutoArrangeDialog(audioItem.Duration, images.Count);
                if (dialog.ShowDialog() != true) return;

                // Save current state for Undo
                SaveState();

                // Remove existing text layers
                var existingTextItems = timelineItems.Where(i => i.Name.Contains("Intro") || i.Name.Contains("Outro")).ToList();
                foreach (var textItem in existingTextItems)
                {
                    timelineItems.Remove(textItem);
                }

                double totalTime = audioItem.Duration;
                double availableTime = totalTime - dialog.IntroDuration - dialog.OutroDuration;
                double timePerImage = availableTime / images.Count;

                if (timePerImage <= 0)
                {
                    LogMessage("Not enough time for images. Reduce the text duration.", true);
                    return;
                }

                LogMessage(LanguageManager.F("loc_auto_layout", images.Count, timePerImage), true);

                // Initialize local sound library if sounds are enabled
                bool soundsEnabled = dialog.EnableTransitionSounds || dialog.EnableAmbientSounds;
                if (soundsEnabled)
                {
                    LogMessage("🔊 Local sound library active for slideshow", true);
                }
                // Lista za tranzicione zvukove
                var transitionSounds = new List<TimelineItem>();

                // Rasporedi slike
                double currentTime = dialog.IntroDuration;
                for (int i = 0; i < images.Count; i++)
                {
                    var img = images[i];

                    // Postavi poziciju
                    img.FixedPosition = currentTime;
                    img.Duration = timePerImage;
                    img.UseFixedPosition = true;
                    img.Start = currentTime;
                    img.End = currentTime + timePerImage;

                    // Ken Burns effect (if enabled)
                    if (dialog.EnableKenBurns)
                    {
                        AddKenBurnsKeyframes(img, timePerImage);
                    }
                    else
                    {
                        // Otherwise use the selected effect
                        string effectName = GetEffectName(dialog.EffectMode, dialog.EffectSequence, i);
                        AddKeyframesForEffect(img, effectName, timePerImage);
                    }

                    // Add text to image if enabled
                    if (dialog.TextOnImageEnabled)
                    {
                        string overlayText = dialog.OverlayText;
                        if (dialog.UseCounter)
                        {
                            overlayText = $"{overlayText} {i + 1}";
                        }

                        img.TextOverlay = new TextOverlayData
                        {
                            Text = overlayText,
                            Font = dialog.OverlayFont,
                            SelectedFontSize = 48,
                            Color = dialog.OverlayColor,
                            Position = "Centar",
                            Enabled = true
                        };
                    }

                    // Add transition sound (pop/whoosh) between scenes (except after last)
                    if (dialog.EnableTransitionSounds && i < images.Count - 1)
                    {
                        string transitionType = i % 2 == 0 ? "whoosh" : "pop";
                        string soundPath = LocalSoundLibrary.GetTransitionSound(transitionType);
                        if (!string.IsNullOrEmpty(soundPath))
                        {
                            var soundItem = new TimelineItem
                            {
                                Path = soundPath,
                                Duration = 0.5,
                                // v1.4: fixed, otherwise the sound was moved to the end of the audio track
                                UseFixedPosition = true,
                                FixedPosition = Math.Max(0, currentTime + timePerImage - 0.2),
                                TrackIndex = 3,
                                Start = currentTime + timePerImage - 0.2,
                                End = currentTime + timePerImage + 0.3,
                                Name = $"🔊 Tranzicija: {transitionType}",
                                Type = "Audio",
                                Volume = 30,
                                VideoEffect = new VideoEffectData()
                            };
                            transitionSounds.Add(soundItem);
                        }
                    }

                    LogMessage(LanguageManager.F("loc_image_n_at", i + 1, img.Name, FormatTime(img.Start), dialog.EnableKenBurns ? "Ken Burns zoom" : GetEffectName(dialog.EffectMode, dialog.EffectSequence, i)), false);

                    currentTime += timePerImage;
                }

                // Dodaj tranzicione zvukove u timeline
                foreach (var sound in transitionSounds)
                {
                    timelineItems.Add(sound);
                }
                if (transitionSounds.Count > 0)
                {
                    LogMessage($"Added {transitionSounds.Count} transition sounds", true);
                }

                // Ambijentalni zvukovi — lokalna biblioteka
                if (dialog.EnableAmbientSounds)
                {
                    LogMessage("Adding ambient sounds from the local library...", true);
                    var ambientSounds = new List<TimelineItem>();

                    string[] ambientTypes = { "birds", "children laughing", "wind" };
                    double ambientStart = dialog.IntroDuration;
                    double ambientEnd = totalTime - dialog.OutroDuration;

                    if (ambientEnd > ambientStart)
                    {
                        foreach (var ambientType in ambientTypes)
                        {
                            string soundPath = LocalSoundLibrary.GetAmbientSound(ambientType);
                            if (!string.IsNullOrEmpty(soundPath))
                            {
                                var ambientItem = new TimelineItem
                                {
                                    Path = soundPath,
                                    Duration = ambientEnd - ambientStart,
                                    UseFixedPosition = true,          // v1.4
                                    FixedPosition = ambientStart,
                                    Start = ambientStart,
                                    End = ambientEnd,
                                    Name = $"🌳 Ambient sound: {ambientType}",
                                    Type = "Audio",
                                    Volume = 15,
                                    TrackIndex = 3,
                                    VideoEffect = new VideoEffectData()
                                };
                                ambientSounds.Add(ambientItem);
                            }
                        }
                    }

                    foreach (var ambient in ambientSounds)
                    {
                        timelineItems.Add(ambient);
                    }
                    if (ambientSounds.Count > 0)
                    {
                        LogMessage($"Added {ambientSounds.Count} ambient sounds", true);
                    }
                }
                // v1.4: logo and intro text both used to sit at 0 s on top of each other.
                // The logo now takes the first part of the intro time, the title the rest.
                bool hasLogo = dialog.ShowLogo && !string.IsNullOrEmpty(dialog.LogoPath) && File.Exists(dialog.LogoPath);
                double logoDur = hasLogo ? Math.Min(dialog.LogoDuration, dialog.IntroDuration) : 0;
                if (hasLogo && !string.IsNullOrEmpty(dialog.IntroText) && dialog.IntroDuration - logoDur < 1.0)
                    logoDur = Math.Max(0, dialog.IntroDuration - 1.0);
                if (hasLogo && logoDur < 0.5)
                {
                    LogMessage("Not enough intro time for the logo — set a longer intro duration to show it.", true);
                    hasLogo = false;
                    logoDur = 0;
                }

                // ADD INTRO TEXT (at the start)
                if (!string.IsNullOrEmpty(dialog.IntroText) && dialog.IntroDuration - logoDur >= 0.5)
                {
                    double introDur = dialog.IntroDuration - logoDur;
                    string introImagePath = await CreateTextImage(dialog.IntroText, introDur, true);
                    var introTextItem = new TimelineItem
                    {
                        Path = introImagePath,
                        Duration = introDur,
                        FixedPosition = logoDur,
                        UseFixedPosition = true,
                        Start = logoDur,
                        End = logoDur + introDur,
                        Name = $"Intro text: {dialog.IntroText}",
                        Type = "Image",
                        Volume = 100,
                        TrackIndex = 0,
                        VideoEffect = new VideoEffectData()
                    };
                    timelineItems.Add(introTextItem);
                    LogMessage($"Added intro text: {dialog.IntroText}", true);
                }

                // DODAJ ODJAVNI TEKST (na kraju)
                if (!string.IsNullOrEmpty(dialog.OutroText))
                {
                    double outroStart = totalTime - dialog.OutroDuration;
                    if (outroStart < 0) outroStart = 0;

                    string outroImagePath = await CreateTextImage(dialog.OutroText, dialog.OutroDuration, false);
                    var outroTextItem = new TimelineItem
                    {
                        Path = outroImagePath,
                        Duration = dialog.OutroDuration,
                        FixedPosition = outroStart,
                        UseFixedPosition = true,
                        Start = outroStart,
                        End = outroStart + dialog.OutroDuration,
                        Name = $"Outro text: {dialog.OutroText}",
                        Type = "Image",
                        Volume = 100,
                        TrackIndex = 0,
                        VideoEffect = new VideoEffectData()
                    };
                    timelineItems.Add(outroTextItem);
                    LogMessage($"Added outro text: {dialog.OutroText}", true);
                }

                // Logo
                if (hasLogo)
                {
                    var logoItem = new TimelineItem
                    {
                        Path = dialog.LogoPath,
                        Duration = logoDur,
                        FixedPosition = 0,
                        UseFixedPosition = true,
                        Start = 0,
                        End = logoDur,
                        Name = "Logo kanala",
                        Type = "Image",
                        Volume = 100,
                        TrackIndex = 0,
                        VideoEffect = new VideoEffectData()
                    };
                    timelineItems.Add(logoItem);
                    LogMessage($"Added logo: {Path.GetFileName(dialog.LogoPath)}", true);
                }

                // Crossfade between images (if enabled)
                if (dialog.EnableCrossfade)
                {
                    AddCrossfadeBetweenImages(images, timePerImage, dialog.IntroDuration);
                    LogMessage(L("crossfade_applied"), true);
                }

                ReorderAllClips();
                LogMessage(string.Format(L("auto_arrange_done"), images.Count), true);
                PlayBeep();
            }
            catch (Exception ex)
            {
                LogMessage(string.Format(L("auto_arrange_error"), ex.Message), true);
            }
        }
        /// <summary>
        /// Creates an image with text for an intro or outro title card
        /// </summary>
        private async Task<string> CreateTextImage(string text, double duration, bool isIntro)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), $"text_{Guid.NewGuid()}.png");

            await Task.Run(() =>
            {
                int surfW = 1920, surfH = 1080;
                var resParts = _selectedResolution?.Split('x');
                if (resParts?.Length == 2 && int.TryParse(resParts[0], out int rw) && int.TryParse(resParts[1], out int rh))
                { surfW = rw; surfH = rh; }

                using (var surface = SKSurface.Create(new SKImageInfo(surfW, surfH)))
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
                            paint.Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);

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
        /// <summary>
        /// Adds crossfade between images (fade out at end of one, fade in at start of next)
        /// </summary>
        private void AddCrossfadeBetweenImages(List<TimelineItem> images, double timePerImage, double introDuration)
        {
            for (int i = 0; i < images.Count - 1; i++)
            {
                var current = images[i];
                var next = images[i + 1];

                double crossfadeDuration = 0.5;

                current.Keyframes.Add(new AnimationKeyframe { Time = current.Duration - crossfadeDuration, Opacity = 1 });
                current.Keyframes.Add(new AnimationKeyframe { Time = current.Duration, Opacity = 0 });

                next.Keyframes.Add(new AnimationKeyframe { Time = 0, Opacity = 0 });
                next.Keyframes.Add(new AnimationKeyframe { Time = crossfadeDuration, Opacity = 1 });
            }
        }
        private string GetEffectName(string mode, List<string> manualSequence, int index)
        {
            var effects = new List<string> {
        "ZoomIn", "ZoomOut", "SlideLeft", "SlideRight",
        "SlideUp", "SlideDown", "FadeIn", "FadeOut",
        "RotateLeft", "RotateRight", "KenBurns"
    };

            if (mode == "manual" && manualSequence.Count > 0)
            {
                return manualSequence[index % manualSequence.Count];
            }
            else if (mode == "random")
            {
                var random = new Random();
                return effects[random.Next(effects.Count)];
            }
            else // auto - cyclic
            {
                var autoEffects = new List<string> { "ZoomIn", "SlideLeft", "FadeIn", "ZoomOut", "SlideRight" };
                return autoEffects[index % autoEffects.Count];
            }
        }

        private void AddKeyframesForEffect(TimelineItem item, string effectName, double duration)
        {
            item.Keyframes.Clear();

            switch (effectName)
            {
                case "ZoomIn":
                    item.Keyframes.Add(new AnimationKeyframe { Time = 0, Zoom = 1.0 });
                    item.Keyframes.Add(new AnimationKeyframe { Time = duration, Zoom = 1.15 });
                    break;
                case "ZoomOut":
                    item.Keyframes.Add(new AnimationKeyframe { Time = 0, Zoom = 1.15 });
                    item.Keyframes.Add(new AnimationKeyframe { Time = duration, Zoom = 1.0 });
                    break;
                case "SlideLeft":
                    item.Keyframes.Add(new AnimationKeyframe { Time = 0, X = -100 });
                    item.Keyframes.Add(new AnimationKeyframe { Time = duration, X = 0 });
                    break;
                case "SlideRight":
                    item.Keyframes.Add(new AnimationKeyframe { Time = 0, X = 100 });
                    item.Keyframes.Add(new AnimationKeyframe { Time = duration, X = 0 });
                    break;
                case "SlideUp":
                    item.Keyframes.Add(new AnimationKeyframe { Time = 0, Y = -100 });
                    item.Keyframes.Add(new AnimationKeyframe { Time = duration, Y = 0 });
                    break;
                case "SlideDown":
                    item.Keyframes.Add(new AnimationKeyframe { Time = 0, Y = 100 });
                    item.Keyframes.Add(new AnimationKeyframe { Time = duration, Y = 0 });
                    break;
                case "FadeIn":
                    item.Keyframes.Add(new AnimationKeyframe { Time = 0, Opacity = 0 });
                    item.Keyframes.Add(new AnimationKeyframe { Time = duration, Opacity = 1 });
                    break;
                case "FadeOut":
                    item.Keyframes.Add(new AnimationKeyframe { Time = 0, Opacity = 1 });
                    item.Keyframes.Add(new AnimationKeyframe { Time = duration, Opacity = 0 });
                    break;
                case "RotateLeft":
                    item.Keyframes.Add(new AnimationKeyframe { Time = 0, Rotation = -5 });
                    item.Keyframes.Add(new AnimationKeyframe { Time = duration, Rotation = 0 });
                    break;
                case "RotateRight":
                    item.Keyframes.Add(new AnimationKeyframe { Time = 0, Rotation = 5 });
                    item.Keyframes.Add(new AnimationKeyframe { Time = duration, Rotation = 0 });
                    break;
                case "KenBurns":
                    item.Keyframes.Add(new AnimationKeyframe { Time = 0, Zoom = 1.0, X = 0, Y = 0 });
                    item.Keyframes.Add(new AnimationKeyframe { Time = duration, Zoom = 1.2, X = 50, Y = 30 });
                    break;
            }
        }

        private void AddAnimatedText(string text, double duration, double startTime, string animation)
        {
            var subtitle = new SubtitleItem
            {
                Text = text,
                Start = startTime,
                End = startTime + duration
            };
            subtitles.Add(subtitle);
            lstSubtitles.Items.Add($"{FormatTime(startTime)} -> {FormatTime(startTime + duration)}: {text}");

            LogMessage($"Added text: '{text}' from {FormatTime(startTime)} to {FormatTime(startTime + duration)}", true);
        }
        private void AddTextToImage_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count == 0 || !(nativeListView.SelectedItems[0].Tag is TimelineItem item))
            {
                LogMessage("Select an image first", true);
                return;
            }

            if (!item.IsImage)
            {
                LogMessage(L("text_only_on_image"), true);
                return;
            }

            var dialog = new TextOverlayDialog($"Add text to: {item.Name}", null, styleOptions: true);
            if (dialog.ShowDialog() == true)
            {
                item.TextOverlay = new TextOverlayData
                {
                    Text = dialog.Text,
                    Font = dialog.Font,
                    SelectedFontSize = dialog.SelectedFontSize,
                    Color = dialog.TextColor,
                    Position = dialog.Position,
                    Enabled = true
                };

                LogMessage($"Text '{dialog.Text}' added to image {item.Index}", true);
                PlayBeep();

                // Refresh display in ListView
                UpdateTimelineDisplay();
            }
        }
        private void NavigateToItemByNumber(int number)
        {
            if (nativeListView == null || nativeListView.Items.Count == 0) return;

            if (number >= 1 && number <= nativeListView.Items.Count)
            {
                nativeListView.Items[number - 1].Selected = true;
                nativeListView.Items[number - 1].EnsureVisible();
                LogMessage($"Navigated to clip {number}", true);
                PlayBeep();
            }
            else
            {
                LogMessage($"Number {number} does not exist (1-{nativeListView.Items.Count})", true);
            }
        }
        private void MoveToFirst_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                SaveState();
                timelineItems.Remove(item);
                timelineItems.Insert(0, item);
                UpdateTimelineDisplay();

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (nativeListView.Items.Count > 0)
                    {
                        nativeListView.Items[0].Selected = true;
                        nativeListView.Items[0].EnsureVisible();
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);

                LogMessage($"Clip '{item.Name}' moved to the first position", true);
                PlayBeep();
            }
        }

        private void MoveToLast_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                SaveState();
                timelineItems.Remove(item);
                timelineItems.Add(item);
                UpdateTimelineDisplay();

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    int lastIndex = nativeListView.Items.Count - 1;
                    if (lastIndex >= 0)
                    {
                        nativeListView.Items[lastIndex].Selected = true;
                        nativeListView.Items[lastIndex].EnsureVisible();
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);

                LogMessage($"Clip '{item.Name}' moved to the last position", true);
                PlayBeep();
            }
        }

        private void MoveToPosition_Click(object sender, RoutedEventArgs e)
        {
            if (nativeListView?.SelectedItems.Count > 0 && nativeListView.SelectedItems[0].Tag is TimelineItem item)
            {
                int maxPos = timelineItems.Count;
                var dialog = new NumericDialog(maxPos);
                if (dialog.ShowDialog() == true)
                {
                    int newPosition = dialog.SelectedNumber;
                    if (newPosition >= 1 && newPosition <= maxPos)
                    {
                        SaveState();
                        int oldIndex = timelineItems.IndexOf(item);
                        timelineItems.RemoveAt(oldIndex);
                        timelineItems.Insert(newPosition - 1, item);
                        UpdateTimelineDisplay();

                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (nativeListView.Items.Count > newPosition - 1)
                            {
                                nativeListView.Items[newPosition - 1].Selected = true;
                                nativeListView.Items[newPosition - 1].EnsureVisible();
                            }
                        }), System.Windows.Threading.DispatcherPriority.Background);

                        LogMessage($"Clip '{item.Name}' moved to position {newPosition}", true);
                        PlayBeep();
                    }
                }
            }
        }
        private async void btnPixabay_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new PixabaySearchDialog { Owner = this };
            if (dialog.ShowDialog() == true && dialog.DownloadedMediaPaths.Count > 0)
            {
                SaveState();

                foreach (var mediaPath in dialog.DownloadedMediaPaths)
                {
                    string ext = Path.GetExtension(mediaPath).ToLower();
                    string type = (ext == ".mp4" || ext == ".avi" || ext == ".mov" || ext == ".mkv" || ext == ".webm") ? "Video" : "Image";

                    // v1.4: videos get their real length (they were all cut to 5 seconds)
                    double dur = 5.0;
                    if (type == "Video")
                    {
                        double real = await GetMediaDurationWithTimeoutAsync(mediaPath, TimeSpan.FromSeconds(10));
                        if (real > 0.5) dur = real;
                    }

                    var newItem = new TimelineItem
                    {
                        Path = mediaPath,
                        Duration = dur,
                        Name = Path.GetFileName(mediaPath),
                        Type = type,
                        Volume = 100,
                        TrackIndex = 0,
                        VideoEffect = new VideoEffectData()
                    };
                    timelineItems.Add(newItem);
                }

                UpdateTimelineDisplay();
                LogMessage($"✅ Added {dialog.DownloadedMediaPaths.Count} media items from Pixabay", true);
                PlayBeep();
            }
        }
        private void btnAIVideoCreator_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new AIVideoCreator { Owner = this };
            dialog.ShowDialog();
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private void OpenVideoEngine_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new VideoEngineDialog();
            dlg.Owner = this;
            dlg.ShowDialog();
        }

        private void OpenVideoReview_Click(object sender, RoutedEventArgs e)
        {
            // v1.4: the review window used to run on its own UI thread. From there every
            // label lookup (Application.MainWindow) and "Go to" seek touched the main window
            // across threads, which WPF forbids — the window crashed or did nothing. All of
            // its work is async, so it now simply opens as a normal (modeless) window.
            var existing = System.Windows.Application.Current.Windows.OfType<VideoReviewDialog>().FirstOrDefault();
            if (existing != null) { existing.Activate(); return; }
            var dlg = new VideoReviewDialog { Owner = this };
            dlg.Show();
        }

        /// <summary>
        /// Seek media playera na zadanu poziciju u sekundama.
        /// Poziva VideoReviewDialog kad korisnik klikne "▶ Idi" na timestamp.
        /// </summary>
        public void SeekToPosition(double seconds)
        {
            try
            {
                if (_mediaPlayer != null)
                {
                    long ms = (long)(seconds * 1000);
                    Dispatcher.Invoke(() =>
                    {
                        _mediaPlayer.Time = ms;
                        if (!_mediaPlayer.IsPlaying) _mediaPlayer.Play();
                    });
                    LogMessage($"▶ Seek na {TimeSpan.FromSeconds(seconds):mm\\:ss}", false);
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Seek error: {ex.Message}", false);
            }
        }

        // ── Multi-Format Export ──────────────────────────────────────
        private async void ExportMultiFormat_Click(object sender, RoutedEventArgs e)
        {
            string finalVideo = GetLastRenderedVideo();
            if (string.IsNullOrEmpty(finalVideo) || !File.Exists(finalVideo))
            {
                System.Windows.MessageBox.Show(
                    L("no_render_for_export"),
                    "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string ffmpeg = AppPaths.FFmpeg;
            string dir = System.IO.Path.GetDirectoryName(finalVideo);
            string name = System.IO.Path.GetFileNameWithoutExtension(finalVideo);

            LogMessage(LanguageManager.T("loc_multiformat"), true);

            try
            {
                var results = await MultiFormatExport.ExportAllFormats(
                    finalVideo, dir, ffmpeg, name,
                    new System.Threading.CancellationToken());

                string nl = Environment.NewLine;
                string msg = "Export complete!" + nl + nl;
                msg += "Original (16:9): " + System.IO.Path.GetFileName(finalVideo) + nl;
                if (results.HasShorts) msg += "Shorts/TikTok (9:16): " + System.IO.Path.GetFileName(results.Shorts) + nl;
                if (results.HasSquare) msg += "Instagram (1:1): " + System.IO.Path.GetFileName(results.Square) + nl;
                msg += nl + "All files are in: " + dir;

                LogMessage(L("export_done"), true);
                System.Windows.MessageBox.Show(msg, "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                LogMessage(string.Format(L("export_error"), ex.Message), true);
            }
        }

        internal string LastRenderedVideoPath => GetLastRenderedVideo();

        private string GetLastRenderedVideo()
        {
            if (string.IsNullOrEmpty(currentProjectFolder)) return null;
            // v1.4: skip earlier exports, otherwise the next export made Shorts of the Shorts
            var files = System.IO.Directory.GetFiles(currentProjectFolder, "*.mp4")
                .Where(f => !f.Contains("_shorts_9x16") && !f.Contains("_instagram_1x1") && !f.Contains("_tmp_render"))
                .ToArray();
            if (files.Length == 0) return null;
            // Vrati najnoviji MP4 fajl
            return files.OrderByDescending(f => System.IO.File.GetLastWriteTime(f))
                        .FirstOrDefault();
        }

        // ── Vision AI - opisi klipova ────────────────────────────────
        /// <summary>
        /// v1.4 — a clip description worth reading aloud, or null for AI creator tags and for the
        /// error texts the old "Describe all clips" stored as if they were descriptions.
        /// </summary>
        private static string UsableDescription(string d)
        {
            if (string.IsNullOrWhiteSpace(d)) return null;
            if (d.Contains("context:") || d.Contains("mood:") || d.Contains("energy=") || d.Contains("static=")) return null;
            foreach (string bad in new[] { "Could not extract", "Error while describing", "Description not available", "File does not exist" })
                if (d.StartsWith(bad, StringComparison.OrdinalIgnoreCase)) return null;
            return d.Trim();
        }

        /// <summary>
        /// v1.4 — "Describe all clips": one description per clip (the middle of the clip) with the
        /// same describer as Ctrl+Shift+F. It handles songs saved as video (no picture, or only a
        /// cover picture), and a failure is reported instead of being stored as the description.
        /// </summary>
        private async void DescribeAllClips_Click(object sender, RoutedEventArgs e)
        {
            if (_describeCts != null) { LogMessage(L("desc_parts_running"), true); return; }
            var items = timelineItems.Where(i => !i.IsAudio && (i.IsVideo || i.IsImage) && !RenderEngine.IsAiCreatorClip(i) &&
                                                 !string.IsNullOrEmpty(i.Path) && File.Exists(i.Path) &&
                                                 UsableDescription(i.AudioDescription) == null).ToList();
            if (items.Count == 0) { LogMessage(L("desc_all_nothing"), true); return; }
            _describeCts = new CancellationTokenSource();
            var ct = _describeCts.Token;
            int done = 0;
            LogMessage(LF("desc_parts_start", items.Count), true);
            try
            {
                foreach (var item in items)
                {
                    double mid = item.IsImage ? 0 : item.SourceOffset + item.Duration / 2.0 * ClipSpeed(item);
                    var r = await FrameDescriber.DescribeFrameAsync(item.Path, mid, ct);
                    if (r.Error == "no_model") { LogMessage(L("desc_no_model"), true); return; }
                    string name = Path.GetFileNameWithoutExtension(item.Name ?? "");
                    if (r.Text == null)
                    {
                        // never store an error as the description (it replaced the clip name before)
                        if (UsableDescription(item.AudioDescription) == null) item.AudioDescription = null;
                        LogMessage(r.Error == "no_picture" ? LF("desc_clip_sound_only", name) : LF("desc_clip_failed", name, r.Detail), true);
                        continue;
                    }
                    item.AudioDescription = r.Text;
                    done++;
                    LogMessage($"{name} — {r.Text}", true);
                }
                LogMessage(LF("desc_parts_done", done), true);
            }
            catch (OperationCanceledException) { LogMessage(LF("desc_parts_stopped", done), true); }
            catch (Exception ex)
            {
                AppLog.Error("Describing all clips", ex);
                LogMessage(LF("desc_failed", ex.Message), true);
            }
            finally
            {
                _describeCts.Dispose();
                _describeCts = null;
                _pendingPlaybackItem = null;
                UpdateTimelineDisplay();
                _pendingPlaybackItem = null;   // refreshing the list must not restart the clip
            }
        }

        // OVE KLASE DODATI NA KRAJ FAJLA (poslije zatvaranja MainWindow klase)

        public class CloudflareImageResponse
        {
            public CloudflareImageResult result { get; set; }
            public bool success { get; set; }
        }

        public class CloudflareImageResult
        {
            public string image { get; set; }
        }

        public class LlavaResponse
        {
            public LlavaResult result { get; set; }
        }

        public class LlavaResult
        {
            public string response { get; set; }
        }

        /// <summary>
        /// Find AIVideoCreator instance in the visual tree of the window.
        /// Used for passing word-level timestamps after alignment.
        /// </summary>
        private AIVideoCreator FindAIVideoCreator()
        {
            // v1.4: AIVideoCreator is its own window, never a visual child of MainWindow,
            // so the old visual-tree search always returned null.
            return System.Windows.Application.Current?.Windows.OfType<AIVideoCreator>().FirstOrDefault()
                   ?? FindVisualChild<AIVideoCreator>(this);
        }

        private static T FindVisualChild<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
        {
            if (parent == null) return null;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T target) return target;
                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }

    }
}