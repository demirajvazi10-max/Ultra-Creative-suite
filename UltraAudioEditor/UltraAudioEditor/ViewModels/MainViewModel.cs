using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using UltraAudioEditor.Models;
using UltraAudioEditor.Services;

using UltraAudioEditor.Localization;

namespace UltraAudioEditor.ViewModels
{
    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;
        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        { _execute = execute; _canExecute = canExecute; }
        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        { _execute = _ => execute(); _canExecute = canExecute == null ? null : _ => canExecute(); }
        public bool CanExecute(object? p) => _canExecute?.Invoke(p) ?? true;
        public void Execute(object? p) => _execute(p);
        public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
        public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
    }

    public class RelayCommand<T> : ICommand
    {
        private readonly Action<T?> _execute;
        private readonly Func<T?, bool>? _canExecute;
        public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
        { _execute = execute; _canExecute = canExecute; }
        public bool CanExecute(object? p) => _canExecute?.Invoke(p is T t ? t : default) ?? true;
        public void Execute(object? p) => _execute(p is T t ? t : default);
        public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    }

    public partial class MainViewModel : INotifyPropertyChanged
    {
        public AudioProject Project { get; set; } = new();
        private readonly AudioEngine _engine = new();
        public readonly AnthropicService AI = new();

        // Undo/Redo čuva KOMPLETNO stanje projekta (trake, klipovi, efekti, markeri)
        // kao JSON. Ranije su se ovde čuvali samo nazivi/mute/volume traka i ništa se
        // nije vraćalo — Ctrl+Z je samo izgovarao "Poništeno".
        private const int MaxUndo = 100;
        private readonly List<string> _undoStack = new();
        private readonly Stack<string> _redoStack = new();

        // Keš talasnih oblika po putanji fajla — undo/redo i lepljenje ne čitaju fajl ponovo.
        private readonly Dictionary<string, float[]> _waveCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, double> _lengthCache = new(StringComparer.OrdinalIgnoreCase);

        private AudioClip? _clipboard;
        private readonly AudioRecorder _recorder = new();
        private double _recordStart;
        private bool _recordPlayAlong;
        private int _recordCounter;
        private System.Windows.Threading.DispatcherTimer? _recordTimer;
        private System.Windows.Threading.DispatcherTimer? _previewTimer;

        static readonly string[] TrackColors = {
            "#378ADD","#1D9E75","#D85A30","#D4537E",
            "#7F77DD","#BA7517","#639922","#E24B4A",
            "#5DCAA5","#EF9F27","#97C459","#F09595"
        };

        // --- Transport ---
        private bool _isPlaying; public bool IsPlaying { get => _isPlaying; set { _isPlaying = value; OnPropertyChanged(); OnPropertyChanged(nameof(PlayPauseLabel)); } }
        private bool _isLooping; public bool IsLooping { get => _isLooping; set { _isLooping = value; OnPropertyChanged(); } }
        private bool _isRecording; public bool IsRecording { get => _isRecording; set { _isRecording = value; OnPropertyChanged(); } }
        private double _playheadPosition; public double PlayheadPosition { get => _playheadPosition; set { _playheadPosition = value; OnPropertyChanged(); OnPropertyChanged(nameof(TimeDisplay)); } }
        public string TimeDisplay => TimeSpan.FromSeconds(PlayheadPosition).ToString(@"hh\:mm\:ss\.fff");
        public string PlayPauseLabel => IsPlaying ? "⏸  Pauziraj" : "▶  Reprodukuj";

        private float _masterVolume = 0.8f; public float MasterVolume { get => _masterVolume; set { _masterVolume = value; _engine.MasterVolume = value; OnPropertyChanged(); } }
        private double _zoomLevel = 1.0; public double ZoomLevel { get => _zoomLevel; set { _zoomLevel = Math.Clamp(value, 0.1, 20); OnPropertyChanged(); } }

        // --- Selection ---
        private AudioTrack? _selectedTrack; public AudioTrack? SelectedTrack { get => _selectedTrack; set { _selectedTrack = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasSelectedTrack)); } }
        public bool HasSelectedTrack => _selectedTrack != null;
        private AudioClip? _selectedClip;
        public AudioClip? SelectedClip
        {
            get => _selectedClip;
            set
            {
                bool changed = !ReferenceEquals(_selectedClip, value);
                _selectedClip = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelectedClip));
                // Najavljuj samo kad se izbor STVARNO promeni — inače svako osvežavanje
                // liste (posle sečenja, pomeranja...) pregazi korisnu poruku o tome šta je urađeno.
                if (value != null && changed && !SuppressSelectionAnnounce)
                    Announce(string.Format(Lang.T("clip_selected"), value.Name, value.StartTime, value.Duration));
            }
        }
        /// <summary>Dok je true, izbor klipa se ne najavljuje (koristi se dok se lista programski osvežava).</summary>
        public bool SuppressSelectionAnnounce { get; set; }

        // --- Status ---
        private string _statusMessage = Lang.T("ready_hint");
        public string StatusMessage { get => _statusMessage; set { _statusMessage = value; OnPropertyChanged(); } }

        // --- AI Panel ---
        private string _aiResult = Lang.T("ai_results_placeholder");
        public string AiResult { get => _aiResult; set { _aiResult = value; OnPropertyChanged(); } }
        private int _aiProgress; public int AiProgress { get => _aiProgress; set { _aiProgress = value; OnPropertyChanged(); } }
        private bool _isAiBusy; public bool IsAiBusy { get => _isAiBusy; set { _isAiBusy = value; OnPropertyChanged(); } }
        private bool _isJawsMode = false;
        public bool IsJawsMode { get => _isJawsMode; set { _isJawsMode = value; OnPropertyChanged(); } }
        private string _aiApiKey = ""; public string AiApiKey { get => _aiApiKey; set { _aiApiKey = value; AI.SetApiKey(value); OnPropertyChanged(); } }

        // Ollama (lokalno) je podrazumevani provajder — bez API ključa, bez cloud-a.
        private AiProvider _providerChoice = AiProvider.Ollama;
        private void SetProvider(AiProvider p)
        {
            _providerChoice = p;
            AI.Provider = p;
            OnPropertyChanged(nameof(UseOllama));
            OnPropertyChanged(nameof(UseGroq));
            OnPropertyChanged(nameof(UseAnthropic));
            OnPropertyChanged(nameof(ApiKeyHint));
            OnPropertyChanged(nameof(ApiKeyLink));
            OnPropertyChanged(nameof(NeedsApiKey));
        }
        public bool UseOllama { get => _providerChoice == AiProvider.Ollama; set { if (value) SetProvider(AiProvider.Ollama); } }
        public bool UseGroq { get => _providerChoice == AiProvider.Groq; set { if (value) SetProvider(AiProvider.Groq); } }
        public bool UseAnthropic { get => _providerChoice == AiProvider.Anthropic; set { if (value) SetProvider(AiProvider.Anthropic); } }
        public bool NeedsApiKey => _providerChoice != AiProvider.Ollama;
        public string ApiKeyHint => _providerChoice switch
        {
            AiProvider.Groq      => Lang.T("groq_key_hint"),
            AiProvider.Anthropic => Lang.T("anthropic_key_hint"),
            _                    => Lang.T("ollama_key_hint")
        };
        public string ApiKeyLink => _providerChoice switch
        {
            AiProvider.Groq      => "https://console.groq.com/keys",
            AiProvider.Anthropic => "https://console.anthropic.com",
            _                    => "https://ollama.com/download"
        };

        private string _selectedLanguage = "Srpski"; public string SelectedLanguage { get => _selectedLanguage; set { _selectedLanguage = value; OnPropertyChanged(); } }
        private string _selectedNoiseLevel = "Srednji"; public string SelectedNoiseLevel { get => _selectedNoiseLevel; set { _selectedNoiseLevel = value; OnPropertyChanged(); } }
        private float _silenceThreshold = -40f; public float SilenceThreshold { get => _silenceThreshold; set { _silenceThreshold = value; OnPropertyChanged(); } }

        // --- Export ---
        private string _exportFormat = "WAV"; public string ExportFormat { get => _exportFormat; set { _exportFormat = value; OnPropertyChanged(); } }
        private int _exportBitRate = 192; public int ExportBitRate { get => _exportBitRate; set { _exportBitRate = value; OnPropertyChanged(); } }
        private int _exportSampleRate = 44100; public int ExportSampleRate { get => _exportSampleRate; set { _exportSampleRate = value; OnPropertyChanged(); } }

        // --- Commands ---
        public ICommand NewProjectCommand { get; }
        public ICommand ImportAudioCommand { get; }
        public ICommand ExportCommand { get; }
        public ICommand AddTrackCommand { get; }
        public ICommand RemoveTrackCommand { get; }
        public ICommand PlayPauseCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand RecordCommand { get; }
        public ICommand ToStartCommand { get; }
        public ICommand ToEndCommand { get; }
        public ICommand LoopCommand { get; }
        public ICommand UndoCommand { get; }
        public ICommand RedoCommand { get; }
        public ICommand NormalizeCommand { get; }
        public ICommand FadeInCommand { get; }
        public ICommand FadeOutCommand { get; }
        public ICommand ZoomInCommand { get; }
        public ICommand ZoomOutCommand { get; }
        public ICommand ZoomFitCommand { get; }
        // AI Commands
        public ICommand AiTranscribeCommand { get; }
        public ICommand AiNoiseRemoveCommand { get; }
        public ICommand AiSmartCutCommand { get; }
        public ICommand AiVocalSepCommand { get; }
        public ICommand AiDescribeCommand { get; }
        public ICommand AiVocalMixCommand { get; }
        public ICommand AiEqRecommendCommand { get; }
        public ICommand AiAutoLevelCommand { get; }
        public ICommand MoveTrackUpCommand { get; }
        public ICommand MoveTrackDownCommand { get; }
        public ICommand DuplicateTrackCommand { get; }
        public ICommand MuteAllCommand { get; }
        public ICommand UnmuteAllCommand { get; }
        // Clip komande
        public ICommand MoveClipLeftCommand { get; }
        public ICommand MoveClipLeftFineCommand { get; }
        public ICommand MoveClipRightCommand { get; }
        public ICommand MoveClipRightFineCommand { get; }
        public ICommand SetClipPositionCommand { get; }
        public ICommand DeleteClipCommand { get; }
        public ICommand ToggleWorkspaceModeCommand { get; }
        public ICommand SaveProjectCommand { get; }
        public ICommand SaveProjectAsCommand { get; }
        public ICommand OpenProjectCommand { get; }
        public ICommand SelectClipCommand { get; }
        public ICommand AnnounceStatusCommand { get; }
        // Uređivanje (novo)
        public ICommand SplitAtPlayheadCommand { get; }
        public ICommand CutAtAllMarkersCommand { get; }
        public ICommand CopyClipCommand { get; }
        public ICommand PasteClipCommand { get; }
        public ICommand AddMarkerCommand { get; }
        public ICommand NextMarkerCommand { get; }
        public ICommand PrevMarkerCommand { get; }
        public ICommand DeleteMarkerCommand { get; }
        public ICommand ClearMarkersCommand { get; }
        public ICommand DeleteBetweenMarkersCommand { get; }
        public ICommand PreviewCutCommand { get; }
        public ICommand QuickStatusCommand { get; }
        public ICommand ShowLogCommand { get; }
        public ICommand OpenEffectCommand { get; }
        public ICommand DisableAllEffectsCommand { get; }
        public bool HasSelectedClip => _selectedClip != null;
        public bool HasClipboard => _clipboard != null;

        // ─── Zaštita od duplog okidanja ──────────────────────────────────────
        // Prečica može stići i iz WinForms liste (JAWS mod) i iz WPF prozora
        // (WindowsFormsHost ponekad prosledi taster i jednom i drugom). Ista komanda
        // u roku od 150 ms se izvršava samo jednom — inače bi npr. Space pauzirao
        // pa odmah ponovo pustio, ili bi Ctrl+X presekao dva puta.
        private readonly Dictionary<string, long> _lastFire = new();
        public bool Gate(string id, int ms = 150)
        {
            long now = Environment.TickCount64;
            if (_lastFire.TryGetValue(id, out long t) && now - t < ms) return false;
            _lastFire[id] = now;
            return true;
        }
        private RelayCommand Cmd(string id, Action a, Func<bool>? can = null)
            => new RelayCommand(() => { if (Gate(id)) a(); }, can);

        public MainViewModel()
        {
            _engine.PositionChanged += (_, pos) =>
                Application.Current?.Dispatcher.Invoke(() => { if (!_scrubbing) PlayheadPosition = pos; });
            // Ranije je ovde stajala poruka "Reprodukcija završena" — ali WaveOut javlja
            // PlaybackStopped i pri SVAKOM ručnom zaustavljanju/pauzi/preslušavanju, pa je
            // čitač ekrana posle svakog Stop-a pričao dve poruke. Sada samo ažuriramo stanje.
            _engine.PlaybackStopped += (_, __) =>
                Application.Current?.Dispatcher.Invoke(() => { if (!IsRecording) IsPlaying = false; });

            NewProjectCommand = Cmd("new", NewProject);
            ImportAudioCommand = Cmd("import", ImportAudio);
            ExportCommand = Cmd("export", ExportAudio);
            AddTrackCommand = Cmd("addtrack", AddTrack);
            RemoveTrackCommand = new RelayCommand(RemoveTrack, () => HasSelectedTrack);
            PlayPauseCommand = Cmd("playpause", PlayPause);
            StopCommand = Cmd("stop", Stop);
            RecordCommand = Cmd("record", ToggleRecord);
            ToStartCommand = Cmd("tostart", () => { SeekTo(0, announce: false); Announce(Lang.T("to_start")); });
            ToEndCommand = Cmd("toend", () => { SeekTo(Project.ContentEnd, announce: false); Announce(string.Format(Lang.T("to_end"), Fmt(PlayheadPosition))); });
            LoopCommand = Cmd("loop", () => { IsLooping = !IsLooping; Announce(IsLooping ? Lang.T("loop_on") : Lang.T("loop_off")); });
            UndoCommand = Cmd("undo", Undo);
            RedoCommand = Cmd("redo", Redo);
            NormalizeCommand = new RelayCommand(NormalizeSelected, () => HasSelectedTrack);
            FadeInCommand = new RelayCommand(ApplyFadeIn, () => HasSelectedTrack);
            FadeOutCommand = new RelayCommand(ApplyFadeOut, () => HasSelectedTrack);
            ZoomInCommand = Cmd("zoomin", () => { ZoomLevel *= 1.5; Announce($"Zoom: {ZoomLevel * 100:F0}%"); });
            ZoomOutCommand = Cmd("zoomout", () => { ZoomLevel /= 1.5; Announce($"Zoom: {ZoomLevel * 100:F0}%"); });
            ZoomFitCommand = Cmd("zoomfit", () => { ZoomLevel = 1; Announce(Lang.T("zoom_reset")); });
            AiTranscribeCommand = new RelayCommand(async () => await RunAI(AiTranscribe));
            AiNoiseRemoveCommand = new RelayCommand(async () => await RunAI(AiNoiseRemove));
            AiSmartCutCommand = new RelayCommand(async () => await RunAI(AiSmartCut));
            AiVocalSepCommand = new RelayCommand(async () => await RunAI(AiVocalSep));
            AiDescribeCommand = new RelayCommand(async () => await RunAI(AiDescribe));
            AiVocalMixCommand = new RelayCommand(async () => await RunAI(AiVocalMix));
            AiEqRecommendCommand = new RelayCommand(async () => await RunAI(AiEqRecommend));
            AiAutoLevelCommand = new RelayCommand(async () => await RunAI(AiAutoLevel));
            MoveTrackUpCommand = Cmd("trackup", MoveTrackUp, () => HasSelectedTrack && Project.Tracks.IndexOf(SelectedTrack!) > 0);
            MoveTrackDownCommand = Cmd("trackdown", MoveTrackDown, () => HasSelectedTrack && Project.Tracks.IndexOf(SelectedTrack!) < Project.Tracks.Count - 1);
            DuplicateTrackCommand = Cmd("duptrack", DuplicateTrack, () => HasSelectedTrack);
            MuteAllCommand = new RelayCommand(() => { PushUndo(); foreach (var t in Project.Tracks) t.IsMuted = true; NotifyEdited(); Announce(Lang.T("all_muted")); });
            UnmuteAllCommand = new RelayCommand(() => { PushUndo(); foreach (var t in Project.Tracks) t.IsMuted = false; NotifyEdited(); Announce(Lang.T("all_unmuted")); });
            // Bez CanExecute: stavke u meniju Klip su uvek dostupne (ranije su bile sive
            // dok klip nije izabran, pa je meni delovao prazno). Bez izabranog klipa
            // program to jasno kaže.
            MoveClipLeftCommand      = Cmd("clipmove", () => MoveClip(-1.0));
            MoveClipLeftFineCommand  = Cmd("clipmove", () => MoveClip(-0.1));
            MoveClipRightCommand     = Cmd("clipmove", () => MoveClip(1.0));
            MoveClipRightFineCommand = Cmd("clipmove", () => MoveClip(0.1));
            SetClipPositionCommand   = Cmd("setpos", OpenSetClipPositionDialog);
            DeleteClipCommand        = new RelayCommand(DeleteSelectedClip);
            ToggleWorkspaceModeCommand = Cmd("mode", ToggleWorkspaceMode);
            SaveProjectCommand   = Cmd("save", SaveProject);
            SaveProjectAsCommand = Cmd("saveas", SaveProjectAs);
            OpenProjectCommand   = Cmd("open", OpenProject);
            AnnounceStatusCommand = Cmd("fullstatus", AnnounceProjectStatus);
            SelectClipCommand = new RelayCommand<object>(param =>
            {
                if (param is Models.AudioClip clip)
                {
                    var track = Project.Tracks.FirstOrDefault(t => t.Clips.Contains(clip));
                    if (track != null) SelectClip(clip, track);
                }
            });

            SplitAtPlayheadCommand      = Cmd("split", SplitAtPlayhead);
            CutAtAllMarkersCommand      = Cmd("cutmarkers", CutAtAllMarkers);
            CopyClipCommand             = Cmd("copy", CopyClip);
            PasteClipCommand            = Cmd("paste", PasteClip);
            AddMarkerCommand            = Cmd("addmarker", AddMarker);
            NextMarkerCommand           = Cmd("nextmarker", () => JumpMarker(+1));
            PrevMarkerCommand           = Cmd("prevmarker", () => JumpMarker(-1));
            DeleteMarkerCommand         = Cmd("delmarker", DeleteNearestMarker);
            ClearMarkersCommand         = Cmd("clearmarkers", ClearMarkers);
            DeleteBetweenMarkersCommand = Cmd("ripple", DeleteBetweenMarkers);
            PreviewCutCommand           = Cmd("preview", PreviewAroundPlayhead);
            QuickStatusCommand          = Cmd("quickstatus", AnnounceQuickStatus);
            ShowLogCommand              = Cmd("log", () => OnShowLog?.Invoke());
            InitProCommands();
            // Meni Efekti: CommandParameter je naziv efekta (npr. "Reverb") — otvara dijalog
            // tog efekta za IZABRANU traku. Isti dijalog kao iz menija trake (Shift+F10).
            OpenEffectCommand = new RelayCommand<object>(p =>
            {
                if (SelectedTrack == null) { Announce(Lang.T("fx_select_track")); return; }
                if (p is string name && Enum.TryParse<EffectType>(name, out var type))
                {
                    PushUndo();
                    OnOpenEffect?.Invoke(SelectedTrack, type);
                    NotifyEdited();
                }
            });
            DisableAllEffectsCommand = new RelayCommand(() =>
            {
                if (SelectedTrack == null) { Announce(Lang.T("fx_select_track")); return; }
                PushUndo();
                var fx = SelectedTrack.Effects;
                fx.EqEnabled = fx.ReverbEnabled = fx.DelayEnabled = fx.CompressorEnabled = false;
                fx.NoiseGateEnabled = fx.BassBostEnabled = fx.PitchEnabled = fx.ChorusEnabled = false;
                NotifyEdited();
                Announce(string.Format(Lang.T("fx_all_off"), SelectedTrack.Name));
            });

            // Inicijalne demo trake
            AddTrackInternal("Vokal 1", "#378ADD", TrackType.Vocal);
            AddTrackInternal("Instrumental", "#1D9E75", TrackType.Instrumental);
            AddTrackInternal(Lang.T("effects_header"), "#D85A30", TrackType.Effects);
            _undoStack.Clear();
        }

        // ════════════════════════════════════════════════════════════════════
        // TASTATURA — jedno mesto za sve prečice editora.
        // Poziva se i iz WPF prozora i iz nativne liste (JAWS mod). Vraća true ako
        // je taster obrađen. fromList = true: fokus je u WinForms listi, gde WPF
        // KeyBinding-zi ne stižu pouzdano, pa i opšte prečice (Ctrl+S, Ctrl+Z...) idu ovuda.
        // ════════════════════════════════════════════════════════════════════
        public bool HandleKey(Key key, ModifierKeys mods, bool fromList)
        {
            bool none = mods == ModifierKeys.None;
            bool ctrl = mods == ModifierKeys.Control;
            bool shift = mods == ModifierKeys.Shift;
            bool alt = mods == ModifierKeys.Alt;
            bool ctrlShift = mods == (ModifierKeys.Control | ModifierKeys.Shift);
            bool ctrlAlt = mods == (ModifierKeys.Control | ModifierKeys.Alt);
            bool altShift = mods == (ModifierKeys.Alt | ModifierKeys.Shift);

            ICommand? c = key switch
            {
                Key.F6 when none        => QuickStatusCommand,
                Key.F6 when shift       => AnnounceStatusCommand,
                Key.X when ctrl         => SplitAtPlayheadCommand,
                Key.X when ctrlShift    => CutAtAllMarkersCommand,
                Key.C when ctrl         => CopyClipCommand,
                Key.V when ctrl         => PasteClipCommand,
                Key.M when none         => AddMarkerCommand,
                // Kretanje kroz vreme — radi BILO GDE u programu (lista, dugmad, klizač)
                Key.Right when alt      => ForwardCommand,
                Key.Left when alt       => BackCommand,
                Key.Right when altShift => ForwardFineCommand,
                Key.Left when altShift  => BackFineCommand,
                Key.Home when alt       => ToStartCommand,
                Key.End when alt        => ToEndCommand,
                Key.G when ctrl         => GoToTimeCommand,
                Key.PageDown when alt   => NextMarkerCommand,
                Key.PageUp when alt     => PrevMarkerCommand,
                Key.Delete when ctrl    => DeleteBetweenMarkersCommand,
                Key.P when none         => PreviewCutCommand,
                Key.R when ctrl         => RecordCommand,
                Key.L when ctrlShift    => ShowLogCommand,
                Key.I when none         => SelStartCommand,
                Key.O when none         => SelEndCommand,
                Key.A when ctrl         => SelectAllCommand,
                Key.Escape when none && HasSelection => ClearSelectionCommand,
                Key.Space when shift    => PlaySelectionCommand,
                Key.F7 when none        => StatisticsCommand,
                _ => null
            };

            if (c == null && fromList)
            {
                c = key switch
                {
                    Key.Space when none  => PlayPauseCommand,
                    Key.S when none      => StopCommand,
                    Key.L when none      => LoopCommand,
                    Key.S when ctrl      => SaveProjectCommand,
                    Key.S when ctrlShift => SaveProjectAsCommand,
                    Key.O when ctrl      => OpenProjectCommand,
                    Key.I when ctrl      => ImportAudioCommand,
                    Key.E when ctrl      => ExportCommand,
                    Key.N when ctrl      => NewProjectCommand,
                    Key.Z when ctrl      => UndoCommand,
                    Key.Y when ctrl      => RedoCommand,
                    Key.T when ctrlAlt   => AddTrackCommand,
                    Key.D when ctrl      => DuplicateTrackCommand,
                    Key.W when alt       => ToggleWorkspaceModeCommand,
                    Key.Add when ctrl      => ZoomInCommand,
                    Key.Subtract when ctrl => ZoomOutCommand,
                    _ => null
                };
            }

            if (c == null) return false;
            if (c.CanExecute(null)) c.Execute(null);
            return true;
        }

        string GetTrackInfo() => Project.Tracks.Count == 0 ? Lang.T("no_tracks") :
            string.Join("; ", Project.Tracks.Select(t =>
                $"{t.Name} ({t.Type}, {t.Clips.Count} klipova, ~{t.Clips.Sum(c => c.Duration):F1}s)"));

        public void Announce(string msg)
        {
            // Ista poruka dva puta zaredom (npr. "Marker dodat") — čitač ekrana ne bi
            // pročitao ponovo jer se tekst nije promenio. Nevidljiv razmak na kraju je
            // dovoljan da se tekst "promeni", a ništa se dodatno ne izgovara.
            if (msg == _statusMessage) msg += "​";
            StatusMessage = msg;
            DebugLog.Write("Announce: " + msg.Replace("​", "").Replace(Environment.NewLine, " | "));
        }

        public static string Fmt(double sec) => Views.Controls.AccessibleTrackList.FormatSec(sec);

        /// <summary>Posle svake izmene: osveži listu, talasne oblike i dužinu projekta.</summary>
        public void NotifyEdited()
        {
            Project.RaiseDurationChanged();
            Controls.WaveformControl.RefreshAll();
            OnRebuildTrackList?.Invoke();
        }

        // ════════════════════════════════════════════════════════════════════
        // UNDO / REDO
        // ════════════════════════════════════════════════════════════════════
        /// <summary>Zapamti trenutno stanje PRE izmene (poziva se pre svake izmene projekta).</summary>
        public void PushUndo()
        {
            _undoStack.Add(SerializeState());
            if (_undoStack.Count > MaxUndo) _undoStack.RemoveAt(0);
            _redoStack.Clear();
        }

        private string SerializeState() => Newtonsoft.Json.JsonConvert.SerializeObject(ToSaveData());

        private void RestoreState(string json)
        {
            var data = Newtonsoft.Json.JsonConvert.DeserializeObject<ProjectSaveData>(json);
            if (data == null) return;
            string? trackId = SelectedTrack?.Id, clipId = SelectedClip?.Id;
            if (IsPlaying) { _engine.Stop(); IsPlaying = false; }
            ApplySaveData(data, keepNameAndPath: true);
            var t = Project.Tracks.FirstOrDefault(x => x.Id == trackId) ?? Project.Tracks.FirstOrDefault();
            SuppressSelectionAnnounce = true;
            SelectedTrack = t;
            SelectedClip = t?.Clips.FirstOrDefault(c => c.Id == clipId);
            SuppressSelectionAnnounce = false;
            NotifyEdited();
        }

        private void Undo()
        {
            if (_undoStack.Count == 0) { Announce(Lang.T("nothing_to_undo")); return; }
            _redoStack.Push(SerializeState());
            var json = _undoStack[^1];
            _undoStack.RemoveAt(_undoStack.Count - 1);
            RestoreState(json);
            Announce(Lang.T("undone"));
        }

        private void Redo()
        {
            if (_redoStack.Count == 0) { Announce(Lang.T("nothing_to_redo")); return; }
            _undoStack.Add(SerializeState());
            RestoreState(_redoStack.Pop());
            Announce(Lang.T("redone"));
        }

        private float[] GetWaveform(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Array.Empty<float>();
            if (_waveCache.TryGetValue(path, out var w)) return w;
            w = AudioEngine.LoadWaveformData(path);
            _waveCache[path] = w;
            return w;
        }

        public double GetFileLength(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            if (_lengthCache.TryGetValue(path, out var d)) return d;
            d = 0;
            try { using var r = new NAudio.Wave.AudioFileReader(path); d = r.TotalTime.TotalSeconds; } catch { }
            if (d > 0) _lengthCache[path] = d;
            return d;
        }

        /// <summary>Napravi klip iz fajla (dužina + talasni oblik), na zadatoj poziciji.</summary>
        public AudioClip CreateClipFromFile(string path, double start)
        {
            double dur = GetFileLength(path);
            return new AudioClip
            {
                Name = System.IO.Path.GetFileName(path),
                FilePath = path,
                StartTime = Math.Max(0, start),
                Duration = dur > 0 ? dur : 5,
                SourceDuration = dur,
                WaveformData = GetWaveform(path)
            };
        }

        // ════════════════════════════════════════════════════════════════════
        // PROJEKAT
        // ════════════════════════════════════════════════════════════════════
        private void NewProject()
        {
            if (MessageBox.Show(Lang.T("new_project_confirm"),
                    "Ultra Audio Editor", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            Stop();
            Project.Tracks.Clear();
            Project.Markers.Clear();
            Project.Tags = new AudioTags();
            SelectionStart = SelectionEnd = null;
            Project.FilePath = "";
            Project.Name = Lang.T("default_project_name");
            _undoStack.Clear(); _redoStack.Clear();
            PlayheadPosition = 0;
            NotifyEdited();
            Announce(Lang.T("new_project_created"));
        }

        /// <summary>
        /// Vraća putanju koju NAudio može da čita. Za formate koje Windows ne zna (OGG, Opus,
        /// WMA bez kodeka...) fajl se jednom pretvori u WAV preko FFmpeg-a. null = ne može.
        /// </summary>
        public async Task<string?> EnsureReadableAsync(string path)
        {
            try { using var r = new NAudio.Wave.AudioFileReader(path); if (r.TotalTime.TotalSeconds > 0) return path; }
            catch { }
            if (!await Views.BatchConvertWindow.EnsureFfmpegAsync(Application.Current?.MainWindow))
            { Announce(string.Format(Lang.T("import_unsupported"), System.IO.Path.GetFileName(path))); return null; }
            try
            {
                Announce(string.Format(Lang.T("import_converting"), System.IO.Path.GetFileName(path)));
                return await FfmpegService.ConvertToWavAsync(path);
            }
            catch (Exception ex)
            {
                DebugLog.Write("Import convert failed: " + ex);
                Announce(string.Format(Lang.T("import_unsupported"), System.IO.Path.GetFileName(path)));
                return null;
            }
        }

        private async void ImportAudio()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = Lang.T("dlg_import_audio"),
                Filter = Lang.T("audio_filter"),
                Multiselect = true
            };
            if (dlg.ShowDialog() != true) return;
            foreach (var original in dlg.FileNames)
            {
                var path = await EnsureReadableAsync(original);
                if (path == null) continue;
                var track = AddTrackInternal(System.IO.Path.GetFileNameWithoutExtension(original));
                track.Type = TrackType.Audio;
                var clip = CreateClipFromFile(path, 0);
                clip.Name = System.IO.Path.GetFileName(original);
                track.Clips.Add(clip);
                Announce(string.Format(Lang.T("imported_file"), clip.Name, clip.Duration));
            }
            NotifyEdited();
        }

        // ════════════════════════════════════════════════════════════════════
        // IZVOZ — sa proverom glasnoće i kratkim rezimeom na kraju
        // ════════════════════════════════════════════════════════════════════
        private async void ExportAudio()
        {
            if (!Project.Tracks.Any(t => t.Clips.Count > 0)) { Announce(Lang.T("export_nothing")); return; }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = Lang.T("dlg_export_audio"),
                Filter = "WAV|*.wav|MP3|*.mp3|M4A (AAC)|*.m4a|AIFF|*.aiff|FLAC|*.flac|OGG Vorbis|*.ogg|Opus|*.opus",
                FilterIndex = ExportFormat switch { "MP3" => 2, "M4A" => 3, "AIFF" => 4, "FLAC" => 5, "OGG" => 6, "OPUS" => 7, _ => 1 },
                FileName = Project.Name
            };
            if (dlg.ShowDialog() != true) return;
            string fmtName = dlg.FilterIndex switch { 2 => "MP3", 3 => "M4A", 4 => "AIFF", 5 => "FLAC", 6 => "OGG", 7 => "OPUS", _ => "WAV" };
            var fmt = fmtName switch
            {
                "MP3" => Services.ExportFormat.MP3,
                "M4A" => Services.ExportFormat.M4A,
                "AIFF" => Services.ExportFormat.AIFF,
                _ => Services.ExportFormat.WAV
            };
            // FFmpeg je potreban za FLAC/OGG/Opus, i za oznake pesme (naslov, izvođač, omot) u MP3/M4A.
            bool tagsWanted = !Project.Tags.IsEmpty && fmtName is "MP3" or "M4A" or "FLAC" or "OGG" or "OPUS";
            bool useFfmpeg = fmtName is "FLAC" or "OGG" or "OPUS" || tagsWanted;
            if (useFfmpeg && !FfmpegService.IsAvailable)
            {
                Announce(Lang.T("ffmpeg_downloading"));
                bool ready = await Views.BatchConvertWindow.EnsureFfmpegAsync(Application.Current?.MainWindow);
                if (!ready)
                {
                    if (fmtName is "FLAC" or "OGG" or "OPUS") { Announce(Lang.T("ffmpeg_needed")); return; }
                    useFfmpeg = false;   // MP3/M4A i bez FFmpeg-a, samo bez oznaka
                }
            }
            string outPath = dlg.FileName;
            if (IsPlaying) { _engine.Stop(); IsPlaying = false; }

            Announce(Lang.T("export_running"));
            AudioEngine.MixRenderResult? mix = null;
            try
            {
                var prog = new Action<int>(pct => Application.Current?.Dispatcher.InvokeAsync(() => AiProgress = pct));
                mix = await Task.Run(() => AudioEngine.RenderMix(Project, prog));

                if (mix.ClipsMixed == 0)
                {
                    MessageBox.Show(Lang.T("export_no_audible"), Lang.T("export_done_title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    Announce(Lang.T("export_no_audible"));
                    return;
                }

                float gain = 1f;
                if (mix.IsClipping)
                {
                    var answer = MessageBox.Show(string.Format(Lang.T("export_too_loud_msg"), mix.PeakDb),
                        Lang.T("export_too_loud_title"), MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                    if (answer == MessageBoxResult.Cancel) { Announce(Lang.T("export_cancelled")); return; }
                    if (answer == MessageBoxResult.Yes) gain = 0.98f / mix.Peak;   // vrh na -0.2 dB
                }

                if (useFfmpeg)
                {
                    string wav16 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ultraaudio_enc_{Guid.NewGuid():N}.wav");
                    try
                    {
                        await Task.Run(() => AudioEngine.EncodeMix(mix.TempPath, wav16, Services.ExportFormat.WAV, gain, _exportBitRate, prog));
                        await FfmpegService.EncodeAsync(wav16, outPath, fmtName, _exportBitRate, Project.Tags.IsEmpty ? null : Project.Tags);
                    }
                    finally { try { File.Delete(wav16); } catch { } }
                }
                else
                    await Task.Run(() => AudioEngine.EncodeMix(mix.TempPath, outPath, fmt, gain, _exportBitRate, prog));

                double finalPeakDb = mix.PeakDb + 20 * Math.Log10(gain);
                long size = 0; try { size = new FileInfo(outPath).Length; } catch { }
                var sb = new System.Text.StringBuilder();
                sb.AppendLine(string.Format(Lang.T("export_sum_file"), outPath));
                sb.AppendLine(string.Format(Lang.T("export_sum_length"), Fmt(mix.Seconds), size / 1048576.0));
                sb.AppendLine(string.Format(Lang.T("export_sum_tracks"), mix.TracksMixed, mix.ClipsMixed));
                if (mix.TracksSkipped > 0) sb.AppendLine(string.Format(Lang.T("export_sum_skipped"), mix.TracksSkipped));
                if (mix.MissingFiles > 0) sb.AppendLine(string.Format(Lang.T("export_sum_missing"), mix.MissingFiles));
                sb.AppendLine(string.Format(Lang.T("export_sum_peak"), Math.Min(0, finalPeakDb)));
                try
                {
                    var exported = await Task.Run(() => AudioBuffers.ReadWholeFile(mix.TempPath, Project.SampleRate));
                    if (Math.Abs(gain - 1f) > 0.0001f) Services.Dsp.Processing.Gain(exported, 20 * Math.Log10(gain));
                    var (lufs, _) = await Task.Run(() => Services.Dsp.Loudness.Measure(exported, Project.SampleRate));
                    sb.AppendLine(string.Format(Lang.T("export_sum_lufs"), lufs));
                }
                catch { }
                if (useFfmpeg && !Project.Tags.IsEmpty) sb.AppendLine(Lang.T("export_sum_tags"));
                if (gain < 1f) sb.AppendLine(string.Format(Lang.T("export_sum_lowered"), -20 * Math.Log10(gain)));
                else if (mix.IsClipping) sb.AppendLine(Lang.T("export_sum_clipped"));
                if (finalPeakDb < -12) sb.AppendLine(Lang.T("export_sum_quiet"));

                Announce(string.Format(Lang.T("export_done_announce"), System.IO.Path.GetFileName(outPath)));
                DebugLog.Write("Export summary: " + sb.ToString().Replace(Environment.NewLine, " | "));
                MessageBox.Show(sb.ToString(), Lang.T("export_done_title"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                DebugLog.Write("Export failed: " + ex);
                MessageBox.Show(string.Format(Lang.T("error_prefix"), ex.Message), Lang.T("error_title"), MessageBoxButton.OK, MessageBoxImage.Error);
                Announce(Lang.T("export_failed"));
            }
            finally
            {
                AiProgress = 0;
                if (mix != null) try { File.Delete(mix.TempPath); } catch { }
            }
        }

        public AudioTrack AddTrackInternal(string? name = null, string? colorHex = null, TrackType type = TrackType.Audio)
        {
            PushUndo();
            int idx = Project.Tracks.Count;
            var color = colorHex ?? TrackColors[idx % TrackColors.Length];
            var wc = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color);
            var track = new AudioTrack
            {
                Name = name ?? string.Format(Lang.T("track_n"), idx + 1),
                Color = wc,
                Type = type
            };
            Project.Tracks.Add(track);
            SelectedTrack = track;
            Announce(string.Format(Lang.T("track_added"), track.Name, Project.Tracks.Count));
            return track;
        }

        private void AddTrack() => AddTrackInternal();

        private void RemoveTrack()
        {
            if (SelectedTrack == null) return;
            PushUndo();
            string name = SelectedTrack.Name;
            Project.Tracks.Remove(SelectedTrack);
            SelectedTrack = Project.Tracks.LastOrDefault();
            NotifyEdited();
            Announce(string.Format(Lang.T("track_deleted"), name, Project.Tracks.Count));
        }

        // ════════════════════════════════════════════════════════════════════
        // TRANSPORT
        // ════════════════════════════════════════════════════════════════════
        private void StopPreviewTimer() { _previewTimer?.Stop(); _previewTimer = null; }

        private void PlayPause()
        {
            if (IsRecording) { ToggleRecord(); return; }
            StopPreviewTimer();
            if (IsPlaying) { _engine.Pause(); IsPlaying = false; Announce(string.Format(Lang.T("paused_at"), Fmt(PlayheadPosition))); }
            else
            {
                _engine.Play(Project, PlayheadPosition);
                IsPlaying = true;
                Announce(Lang.T("playback_started"));
            }
        }

        private void Stop()
        {
            if (IsRecording) { ToggleRecord(); return; }
            StopPreviewTimer();
            _engine.Stop();
            IsPlaying = false;
            PlayheadPosition = 0;
            Announce(Lang.T("stopped"));
        }

        /// <summary>
        /// P — preslušaj rez: pusti 2 sekunde pre i 2 sekunde posle trenutne pozicije,
        /// pa vrati poziciju tačno tamo gde je bila. Ništa se ne izgovara — da govor
        /// ne prekriva zvuk koji korisnik baš želi da čuje.
        /// </summary>
        private void PreviewAroundPlayhead()
        {
            if (IsRecording) return;
            StopPreviewTimer();
            double ph = PlayheadPosition;
            double from = Math.Max(0, ph - 2.0);
            _engine.Stop();
            _engine.Play(Project, from);
            IsPlaying = true;
            _previewTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds((ph - from) + 2.0) };
            _previewTimer.Tick += (_, __) =>
            {
                StopPreviewTimer();
                _engine.Stop();
                IsPlaying = false;
                PlayheadPosition = ph;
            };
            _previewTimer.Start();
        }

        // ════════════════════════════════════════════════════════════════════
        // SNIMANJE (Ctrl+R uključi/isključi; Space ili S takođe zaustavljaju)
        // ════════════════════════════════════════════════════════════════════
        private async void ToggleRecord()
        {
            if (IsRecording) { await StopRecordingAsync(); return; }

            if (!AudioRecorder.HasInputDevice()) { Announce(Lang.T("rec_no_device")); return; }
            StopPreviewTimer();
            _engine.Stop();
            IsPlaying = false;

            _recordStart = PlayheadPosition;
            string path = System.IO.Path.Combine(AudioRecorder.DefaultRecordingsFolder(),
                $"Recording_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
            try
            {
                // Najava PRE početka snimanja — da glas čitača ekrana ne završi u snimku.
                Announce(Lang.T("rec_started"));
                _recorder.Start(path, Project.SampleRate);
            }
            catch (Exception ex)
            {
                DebugLog.Write("Record start failed: " + ex);
                Announce(string.Format(Lang.T("rec_error"), ex.Message));
                return;
            }
            IsRecording = true;

            // Ako projekat već ima zvuk, pušta se uz snimanje (pevanje preko matrice).
            _recordPlayAlong = Project.Tracks.Any(t => !t.IsMuted && t.Clips.Count > 0);
            if (_recordPlayAlong) { _engine.Play(Project, _recordStart); IsPlaying = true; }
            else
            {
                _recordTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                _recordTimer.Tick += (_, __) => PlayheadPosition = _recordStart + _recorder.RecordedSeconds;
                _recordTimer.Start();
            }
        }

        private async Task StopRecordingAsync()
        {
            _recordTimer?.Stop(); _recordTimer = null;
            await _recorder.StopAsync();
            _engine.Stop();
            IsPlaying = false;
            IsRecording = false;

            string? path = _recorder.FilePath;
            if (_recorder.LastError != null) Announce(string.Format(Lang.T("rec_error"), _recorder.LastError));
            if (path == null || !File.Exists(path)) return;

            double len = GetFileLength(path);
            if (len < 0.3)
            {
                try { File.Delete(path); } catch { }
                Announce(Lang.T("rec_too_short"));
                PlayheadPosition = _recordStart;
                return;
            }

            // Poravnanje sa podlogom: pevač čuje matricu sa zakašnjenjem izlaza zvuka,
            // pa je i njegov glas u snimku kasniji za isto toliko — to se odseče sa početka.
            double trim = _recordPlayAlong ? Math.Min(AudioEngine.OutputLatencyMs / 1000.0, len / 2) : 0;
            double dur = len - trim;

            PushUndo();
            var target = SelectedTrack;
            bool overlaps = target != null && target.Clips.Any(c => c.StartTime < _recordStart + dur && c.EndTime > _recordStart);
            if (target == null || overlaps)
            {
                // AddTrackInternal sam dodaje undo korak — skidamo ga da ceo snimak bude JEDAN korak.
                int before = _undoStack.Count;
                target = AddTrackInternal(string.Format(Lang.T("rec_track_name"), ++_recordCounter), null, TrackType.Vocal);
                if (_undoStack.Count > before) _undoStack.RemoveAt(_undoStack.Count - 1);
            }
            else _recordCounter++;

            var clip = new AudioClip
            {
                Name = System.IO.Path.GetFileName(path),
                FilePath = path,
                StartTime = _recordStart,
                TrimStart = trim,
                Duration = dur,
                SourceDuration = len,
                WaveformData = GetWaveform(path)
            };
            target.Clips.Add(clip);
            SuppressSelectionAnnounce = true;
            SelectedTrack = target;
            SelectedClip = clip;
            SuppressSelectionAnnounce = false;
            PlayheadPosition = clip.EndTime;
            NotifyEdited();

            var msg = string.Format(Lang.T("rec_done"), dur, target.Name, Fmt(clip.StartTime));
            float peak = _recorder.PeakLevel;
            if (peak < 0.01f) msg += " " + Lang.T("rec_warn_silent");
            else if (peak >= 0.99f) msg += " " + Lang.T("rec_warn_loud");
            Announce(msg);
        }

        // ════════════════════════════════════════════════════════════════════
        // SEČENJE, KOPIRANJE, LEPLJENJE
        // ════════════════════════════════════════════════════════════════════
        private const double Eps = 0.01;

        private static bool Covers(AudioClip c, double t) => c.StartTime + Eps < t && c.EndTime - Eps > t;

        /// <summary>Preseci klip na zadatom vremenu; vraća desni deo (ili null).</summary>
        private static AudioClip? SplitClip(AudioTrack track, AudioClip clip, double t)
        {
            if (!Covers(clip, t)) return null;
            if (clip.SourceDuration <= 0) clip.SourceDuration = clip.TrimStart + clip.Duration;
            double leftDur = t - clip.StartTime;
            var right = clip.CloneWithNewId();
            right.StartTime = t;
            right.TrimStart = clip.TrimStart + leftDur;
            right.Duration = clip.Duration - leftDur;
            clip.Duration = leftDur;
            track.Clips.Insert(track.Clips.IndexOf(clip) + 1, right);
            return right;
        }

        /// <summary>Ctrl+X — preseci klip na izabranoj traci tamo gde je pozicija (playhead).</summary>
        private void SplitAtPlayhead()
        {
            double ph = PlayheadPosition;
            AudioTrack? track = SelectedTrack;
            AudioClip? clip = null;
            if (track != null)
                clip = (SelectedClip != null && track.Clips.Contains(SelectedClip) && Covers(SelectedClip, ph))
                    ? SelectedClip : track.Clips.FirstOrDefault(c => Covers(c, ph));
            if (clip == null && track == null)
            {
                track = Project.Tracks.FirstOrDefault(t => t.Clips.Any(c => Covers(c, ph)));
                clip = track?.Clips.FirstOrDefault(c => Covers(c, ph));
            }
            if (track == null || clip == null) { Announce(string.Format(Lang.T("split_none"), Fmt(ph))); return; }

            PushUndo();
            var right = SplitClip(track, clip, ph)!;
            SuppressSelectionAnnounce = true;
            SelectedTrack = track;
            SelectedClip = right;
            SuppressSelectionAnnounce = false;
            NotifyEdited();
            Announce(string.Format(Lang.T("split_done"), Fmt(ph), clip.Duration, right.Duration));
        }

        /// <summary>Ctrl+Shift+X — preseci izabranu traku (ili sve trake ako nijedna nije izabrana) na svim markerima.</summary>
        private void CutAtAllMarkers()
        {
            if (Project.Markers.Count == 0) { Announce(Lang.T("markers_none")); return; }
            var tracks = SelectedTrack != null ? new List<AudioTrack> { SelectedTrack } : Project.Tracks.ToList();
            bool any = tracks.Any(t => Project.Markers.Any(m => t.Clips.Any(c => Covers(c, m))));
            if (!any) { Announce(Lang.T("cut_markers_none")); return; }

            PushUndo();
            int cuts = 0;
            foreach (var t in tracks)
                foreach (var m in Project.Markers.ToList())
                {
                    var c = t.Clips.FirstOrDefault(x => Covers(x, m));
                    if (c != null && SplitClip(t, c, m) != null) cuts++;
                }
            NotifyEdited();
            Announce(SelectedTrack != null
                ? string.Format(Lang.T("cut_markers_done_track"), cuts, SelectedTrack.Name)
                : string.Format(Lang.T("cut_markers_done_all"), cuts));
        }

        private void CopyClip()
        {
            if (CopySelectionToClipboard()) return;   // izbor ima prednost: kopira se izabrani deo trake
            if (SelectedClip == null) { Announce(Lang.T("copy_none")); return; }
            _clipboard = SelectedClip.CloneWithNewId();
            if (_clipboard.SourceDuration <= 0) _clipboard.SourceDuration = GetFileLength(_clipboard.FilePath);
            Announce(string.Format(Lang.T("copy_done"), _clipboard.Name, _clipboard.Duration));
        }

        private void PasteClip()
        {
            if (_clipboard == null) { Announce(Lang.T("paste_empty")); return; }
            var track = SelectedTrack;
            if (track == null) { Announce(Lang.T("paste_no_track")); return; }
            PushUndo();
            var c = _clipboard.CloneWithNewId();
            c.StartTime = PlayheadPosition;
            track.Clips.Add(c);
            SuppressSelectionAnnounce = true;
            SelectedClip = c;
            SuppressSelectionAnnounce = false;
            PlayheadPosition = c.EndTime;
            NotifyEdited();
            Announce(string.Format(Lang.T("paste_done"), c.Name, track.Name, Fmt(c.StartTime), Fmt(c.EndTime)));
        }

        // ════════════════════════════════════════════════════════════════════
        // MARKERI
        // ════════════════════════════════════════════════════════════════════
        private int MarkerNumber(double m) => Project.Markers.IndexOf(m) + 1;

        private void AddMarker()
        {
            double ph = Math.Round(PlayheadPosition, 3);
            var existing = Project.Markers.FirstOrDefault(m => Math.Abs(m - ph) < 0.05);
            if (Project.Markers.Any(m => Math.Abs(m - ph) < 0.05))
            { Announce(string.Format(Lang.T("marker_exists"), MarkerNumber(existing), Fmt(existing))); return; }
            PushUndo();
            int idx = 0;
            while (idx < Project.Markers.Count && Project.Markers[idx] < ph) idx++;
            Project.Markers.Insert(idx, ph);
            NotifyEdited();
            Announce(string.Format(Lang.T("marker_added"), idx + 1, Fmt(ph), Project.Markers.Count));
        }

        private void JumpMarker(int dir)
        {
            if (Project.Markers.Count == 0) { Announce(Lang.T("markers_none")); return; }
            double ph = PlayheadPosition;
            double? target = dir > 0
                ? Project.Markers.Where(m => m > ph + 0.001).Cast<double?>().FirstOrDefault()
                : Project.Markers.Where(m => m < ph - 0.001).Cast<double?>().LastOrDefault();
            if (target == null) { Announce(dir > 0 ? Lang.T("marker_no_next") : Lang.T("marker_no_prev")); return; }
            if (IsPlaying) { _engine.Stop(); IsPlaying = false; }
            PlayheadPosition = target.Value;
            Announce(string.Format(Lang.T("marker_at"), MarkerNumber(target.Value), Project.Markers.Count, Fmt(target.Value)));
        }

        /// <summary>Obriši marker najbliži trenutnoj poziciji.</summary>
        public void DeleteNearestMarker()
        {
            if (Project.Markers.Count == 0) { Announce(Lang.T("markers_none")); return; }
            double ph = PlayheadPosition;
            double m = Project.Markers.OrderBy(x => Math.Abs(x - ph)).First();
            DeleteMarker(m);
        }

        public void DeleteMarker(double m)
        {
            int n = MarkerNumber(m);
            if (n <= 0) return;
            PushUndo();
            Project.Markers.Remove(m);
            NotifyEdited();
            Announce(string.Format(Lang.T("marker_deleted"), n, Fmt(m), Project.Markers.Count));
        }

        public void ClearMarkers()
        {
            if (Project.Markers.Count == 0) { Announce(Lang.T("markers_none")); return; }
            PushUndo();
            int n = Project.Markers.Count;
            Project.Markers.Clear();
            NotifyEdited();
            Announce(string.Format(Lang.T("markers_cleared"), n));
        }

        public void GoToMarker(double m)
        {
            if (IsPlaying) { _engine.Stop(); IsPlaying = false; }
            PlayheadPosition = m;
        }

        /// <summary>
        /// Ctrl+Delete — izbaci sve između dva markera oko trenutne pozicije, na SVIM
        /// trakama (da vokal i matrica ostanu sinhronizovani), i pomeri ostatak unazad
        /// da ne ostane rupa. Oba markera se brišu jer je posao završen.
        /// </summary>
        private void DeleteBetweenMarkers()
        {
            if (HasSelection) { RippleDeleteSelection(); return; }   // Ctrl+Delete briše izbor ako postoji
            double ph = PlayheadPosition;
            double? a = Project.Markers.Where(m => m <= ph + 0.001).Cast<double?>().LastOrDefault();
            double? b = Project.Markers.Where(m => m > ph + 0.001).Cast<double?>().FirstOrDefault();
            if (a == null || b == null) { Announce(Lang.T("ripple_need_markers")); return; }
            double A = a.Value, B = b.Value, gap = B - A;

            PushUndo();
            foreach (var track in Project.Tracks)
            {
                foreach (var c in track.Clips.ToList())
                {
                    if (c.SourceDuration <= 0) c.SourceDuration = c.TrimStart + c.Duration;
                    double s = c.StartTime, e = c.EndTime;
                    if (e <= A + Eps) continue;                                   // ceo pre reza
                    if (s >= B - Eps) { c.StartTime = s - gap; continue; }         // ceo posle — pomeri
                    if (s >= A - Eps && e <= B + Eps) { track.Clips.Remove(c); continue; } // ceo unutra
                    if (s < A && e > B)                                           // prelazi preko oba
                    {
                        var right = c.CloneWithNewId();
                        right.StartTime = A;
                        right.TrimStart = c.TrimStart + (B - s);
                        right.Duration = e - B;
                        c.Duration = A - s;
                        track.Clips.Insert(track.Clips.IndexOf(c) + 1, right);
                        continue;
                    }
                    if (s < A) { c.Duration = A - s; continue; }                  // ulazi u rez sa leve strane
                    // počinje unutra, završava posle
                    c.TrimStart += B - s;
                    c.Duration = e - B;
                    c.StartTime = A;
                }
            }
            var kept = Project.Markers.Where(m => m < A - 0.001 || m > B + 0.001)
                                      .Select(m => m > B ? m - gap : m).ToList();
            Project.Markers.Clear();
            foreach (var m in kept) Project.Markers.Add(m);
            if (SelectedClip != null && !Project.Tracks.Any(t => t.Clips.Contains(SelectedClip)))
            { SuppressSelectionAnnounce = true; SelectedClip = null; SuppressSelectionAnnounce = false; }
            PlayheadPosition = A;
            NotifyEdited();
            Announce(string.Format(Lang.T("ripple_done"), gap, Fmt(A), Fmt(B)));
        }

        // ════════════════════════════════════════════════════════════════════
        // EFEKTI NA FAJLU (normalizacija, fade) — rade na izabranom klipu
        // ════════════════════════════════════════════════════════════════════
        private AudioClip? TargetClipOfSelectedTrack()
        {
            if (SelectedTrack == null) return null;
            if (SelectedClip != null && SelectedTrack.Clips.Contains(SelectedClip)) return SelectedClip;
            return SelectedTrack.Clips.FirstOrDefault();
        }

        private void RunFileOperation(Action<string, string> op, string doneMessage)
        {
            var clip = TargetClipOfSelectedTrack();
            if (clip == null || !File.Exists(clip.FilePath)) { Announce(Lang.T("no_audio_fade")); return; }
            PushUndo();
            string src = clip.FilePath;
            Announce(Lang.T("processing"));
            Task.Run(() =>
            {
                string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ultraaudio_{Guid.NewGuid():N}.wav");
                try { op(src, temp); }
                catch (Exception ex) { DebugLog.Write("File operation failed: " + ex); }
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (!File.Exists(temp)) { Announce(Lang.T("operation_failed")); return; }
                    clip.FilePath = temp;
                    clip.WaveformData = GetWaveform(temp);
                    NotifyEdited();
                    Announce(doneMessage);
                });
            });
        }

        private void NormalizeSelected() => RunFileOperation((i, o) => AudioEngine.NormalizeFile(i, o), Lang.T("normalize_done"));
        private void ApplyFadeIn() => RunFileOperation((i, o) => AudioEngine.ApplyFadeIn(i, o, 2.0), Lang.T("fade_in_applied"));
        private void ApplyFadeOut() => RunFileOperation((i, o) => AudioEngine.ApplyFadeOut(i, o, 2.0), Lang.T("fade_out_applied"));

        private void MoveTrackUp()
        {
            if (SelectedTrack == null) return;
            int i = Project.Tracks.IndexOf(SelectedTrack);
            if (i > 0) { PushUndo(); Project.Tracks.Move(i, i - 1); Announce(string.Format(Lang.T("track_moved_up"), SelectedTrack.Name)); }
        }

        private void MoveTrackDown()
        {
            if (SelectedTrack == null) return;
            int i = Project.Tracks.IndexOf(SelectedTrack);
            if (i < Project.Tracks.Count - 1) { PushUndo(); Project.Tracks.Move(i, i + 1); Announce(string.Format(Lang.T("track_moved_down"), SelectedTrack.Name)); }
        }

        private void DuplicateTrack()
        {
            if (SelectedTrack == null) return;
            var orig = SelectedTrack;
            var dup = AddTrackInternal(orig.Name + " (kopija)", "#" + orig.Color.R.ToString("X2") + orig.Color.G.ToString("X2") + orig.Color.B.ToString("X2"), orig.Type);
            dup.Volume = orig.Volume;
            dup.Pan = orig.Pan;
            foreach (var c in orig.Clips)
                dup.Clips.Add(c.CloneWithNewId());
            NotifyEdited();
            Announce(string.Format(Lang.T("track_duplicated"), orig.Name));
        }

        private async Task RunAI(Func<Task> aiFunc)
        {
            if (!AI.HasApiKey)
            {
                string providerName = UseGroq ? "Groq (besplatno)" : "Anthropic";
                string link = UseGroq ? "console.groq.com" : "console.anthropic.com";
                Announce(string.Format(Lang.T("need_api_key_announce"), providerName));
                MessageBox.Show(
                    string.Format(Lang.T("need_api_key_msg"), providerName, link),
                    Lang.T("need_api_key_title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            IsAiBusy = true;
            AiProgress = 0;
            try { await aiFunc(); }
            catch (Exception ex) { AiResult = string.Format(Lang.T("error_prefix"), ex.Message); Announce(Lang.T("ai_error")); DebugLog.Write("AI error: " + ex); }
            finally { IsAiBusy = false; }
        }

        private async Task AiTranscribe()
        {
            Announce("AI transkripcija pokrenuta...");
            var prog = new Progress<int>(p => { AiProgress = p; });
            AiResult = await AI.TranscribeAudioAsync(GetTrackInfo(), SelectedLanguage, prog);
            Announce(Lang.T("transcription_done"));
        }

        private async Task AiNoiseRemove()
        {
            Announce(Lang.T("noise_started"));
            var prog = new Progress<int>(p => AiProgress = p);
            AiResult = await AI.AnalyzeNoiseAsync(GetTrackInfo(), SelectedNoiseLevel, prog);
            Announce(Lang.T("noise_done"));
        }

        private async Task AiSmartCut()
        {
            Announce("AI SmartCut analiza pokrenuta...");
            var prog = new Progress<int>(p => AiProgress = p);
            AiResult = await AI.SmartCutAnalysisAsync(GetTrackInfo(), SilenceThreshold, prog);
            Announce(Lang.T("smartcut_done"));
        }

        private async Task AiVocalSep()
        {
            Announce("AI savjeti za vokalnu separaciju...");
            var prog = new Progress<int>(p => AiProgress = p);
            AiResult = await AI.VocalSeparationAdviceAsync(GetTrackInfo(), prog);
            Announce(Lang.T("vocal_tips_done"));
        }

        private async Task AiDescribe()
        {
            Announce(Lang.T("describe_started"));
            var prog = new Progress<int>(p => AiProgress = p);
            AiResult = await AI.DescribeAudioAsync(GetTrackInfo(), $"Projekat: {Project.Name}, {Project.Tracks.Count} traka, {Project.SampleRate}Hz/{Project.BitDepth}bit", prog);
            Announce(Lang.T("describe_done"));
        }

        private async Task AiVocalMix()
        {
            var vocals = string.Join(", ", Project.Tracks.Where(t => t.Type == TrackType.Vocal).Select(t => t.Name));
            var inst = string.Join(", ", Project.Tracks.Where(t => t.Type == TrackType.Instrumental).Select(t => t.Name));
            Announce(Lang.T("vocalmix_started"));
            var prog = new Progress<int>(p => AiProgress = p);
            AiResult = await AI.VocalMixAdviceAsync(vocals.Length > 0 ? vocals : "nije definirano", inst.Length > 0 ? inst : "nije definirano", prog);
            Announce(Lang.T("vocalmix_done"));
        }

        private async Task AiEqRecommend()
        {
            if (SelectedTrack == null) { Announce(Lang.T("eq_select_track")); return; }
            Announce(string.Format(Lang.T("eq_started"), SelectedTrack.Name));
            var prog = new Progress<int>(p => AiProgress = p);
            AiResult = await AI.EqRecommendationsAsync(SelectedTrack.Name, SelectedTrack.Type.ToString(), prog);
            Announce(Lang.T("eq_done"));
        }

        private async Task AiAutoLevel()
        {
            Announce(Lang.T("autolevel_started"));
            var prog = new Progress<int>(p => AiProgress = p);
            AiResult = await AI.AutoLevelAnalysisAsync(GetTrackInfo(), prog);
            Announce(Lang.T("autolevel_done"));
        }


        // ─── Clip pozicioniranje ─────────────────────────────────────────────

        /// Pomjeri odabrani klip za delta sekundi (pozitivno = desno, negativno = lijevo)
        private void MoveClip(double deltaSec)
        {
            if (_selectedClip == null) { Announce(Lang.T("clip_select_first")); return; }
            PushUndo();
            _selectedClip.StartTime = Math.Max(0, _selectedClip.StartTime + deltaSec);
            NotifyEdited();
            Announce(string.Format(Lang.T("clip_moved"), _selectedClip.Name, _selectedClip.StartTime));
        }

        /// Otvori dialog za precizno postavljanje pozicije klipa
        public void OpenSetClipPositionDialog()
        {
            if (_selectedClip == null) { Announce(Lang.T("clip_select_first")); return; }
            var dlg = new Views.SetClipPositionDialog(_selectedClip.StartTime, _selectedClip.Name);
            if (dlg.ShowDialog() == true)
            {
                PushUndo();
                double newPos = dlg.ResultSeconds;
                _selectedClip.StartTime = Math.Max(0, newPos);
                NotifyEdited();
                Announce(string.Format(Lang.T("clip_set"), _selectedClip.Name, _selectedClip.StartTime));
            }
        }

        private void DeleteSelectedClip()
        {
            if (_selectedClip == null) { Announce(Lang.T("clip_select_first")); return; }
            PushUndo();
            string name = _selectedClip.Name;
            foreach (var track in Project.Tracks)
                track.Clips.Remove(_selectedClip);
            SuppressSelectionAnnounce = true;
            SelectedClip = null;
            SuppressSelectionAnnounce = false;
            NotifyEdited();
            Announce(string.Format(Lang.T("clip_deleted"), name));
        }


        // ─── Save / Load Projekta ────────────────────────────────────────────

        private void SaveProject()
        {
            if (string.IsNullOrEmpty(Project.FilePath))
                SaveProjectAs();
            else
                DoSave(Project.FilePath);
        }

        private void SaveProjectAs()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = Lang.T("dlg_save_project"),
                Filter = Lang.T("project_filter"),
                FileName = Project.Name,
                DefaultExt = ".paproj"
            };
            if (dlg.ShowDialog() != true) return;
            Project.FilePath = dlg.FileName;
            Project.Name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
            DoSave(dlg.FileName);
        }

        private ProjectSaveData ToSaveData() => new ProjectSaveData
        {
            Name       = Project.Name,
            SampleRate = Project.SampleRate,
            BitDepth   = Project.BitDepth,
            Bpm        = Project.Bpm,
            Markers    = Project.Markers.ToList(),
            Tags       = Project.Tags,
            Tracks     = Project.Tracks.Select(t => new TrackSaveData
            {
                Id      = t.Id,
                Name    = t.Name,
                Type    = t.Type.ToString(),
                Volume  = t.Volume,
                Pan     = t.Pan,
                IsMuted = t.IsMuted,
                IsSolo  = t.IsSolo,
                ColorHex = $"#{t.Color.R:X2}{t.Color.G:X2}{t.Color.B:X2}",
                Envelope = t.Envelope.Select(p => new EnvelopePoint { Time = p.Time, Db = p.Db }).ToList(),
                Clips   = t.Clips.Select(c => new ClipSaveData
                {
                    Id        = c.Id,
                    Name      = c.Name,
                    FilePath  = c.FilePath,
                    StartTime = c.StartTime,
                    Duration  = c.Duration,
                    TrimStart = c.TrimStart,
                    SourceDuration = c.SourceDuration
                }).ToList(),
                Effects = new EffectsSaveData
                {
                    EqEnabled        = t.Effects.EqEnabled,
                    EqLow            = t.Effects.EqLow,
                    EqMid            = t.Effects.EqMid,
                    EqHigh           = t.Effects.EqHigh,
                    ReverbEnabled    = t.Effects.ReverbEnabled,
                    ReverbMix        = t.Effects.ReverbMix,
                    ReverbRoom       = t.Effects.ReverbRoom,
                    ReverbDamping    = t.Effects.ReverbDamping,
                    ReverbPreDelay   = t.Effects.ReverbPreDelay,
                    DelayMix         = t.Effects.DelayMix,
                    CompMakeup       = t.Effects.CompMakeup,
                    ChorusRate       = t.Effects.ChorusRate,
                    ChorusMix        = t.Effects.ChorusMix,
                    EqLowFreq        = t.Effects.EqLowFreq,
                    EqMidFreq        = t.Effects.EqMidFreq,
                    EqMidQ           = t.Effects.EqMidQ,
                    EqHighFreq       = t.Effects.EqHighFreq,
                    HighPassFreq     = t.Effects.HighPassFreq,
                    LowPassFreq      = t.Effects.LowPassFreq,
                    Order            = t.Effects.Order.Select(o => o.ToString()).ToList(),
                    DelayEnabled     = t.Effects.DelayEnabled,
                    DelayTime        = t.Effects.DelayTime,
                    DelayFeedback    = t.Effects.DelayFeedback,
                    CompressorEnabled= t.Effects.CompressorEnabled,
                    CompThreshold    = t.Effects.CompThreshold,
                    CompRatio        = t.Effects.CompRatio,
                    CompAttack       = t.Effects.CompAttack,
                    CompRelease      = t.Effects.CompRelease,
                    PitchEnabled     = t.Effects.PitchEnabled,
                    PitchSemitones   = t.Effects.PitchSemitones,
                    NoiseGateEnabled = t.Effects.NoiseGateEnabled,
                    GateThreshold    = t.Effects.GateThreshold,
                    BassBostEnabled  = t.Effects.BassBostEnabled,
                    BassGain         = t.Effects.BassGain,
                    ChorusEnabled    = t.Effects.ChorusEnabled,
                    ChorusDepth      = t.Effects.ChorusDepth
                }
            }).ToList()
        };

        /// <summary>Zameni sadržaj projekta podacima iz fajla ili iz undo snimka.</summary>
        private void ApplySaveData(ProjectSaveData data, bool keepNameAndPath)
        {
            Project.Tracks.Clear();
            Project.Markers.Clear();
            if (!keepNameAndPath) Project.Name = data.Name;
            Project.SampleRate = data.SampleRate;
            Project.BitDepth   = data.BitDepth;
            Project.Bpm        = data.Bpm;
            foreach (var m in (data.Markers ?? new List<double>()).OrderBy(x => x)) Project.Markers.Add(m);
            Project.Tags = data.Tags ?? new AudioTags();
            foreach (var td in data.Tracks)
            {
                var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(td.ColorHex ?? "#378ADD");
                var track = new Models.AudioTrack
                {
                    Id      = td.Id,
                    Name    = td.Name,
                    Color   = color,
                    Volume  = td.Volume,
                    Pan     = td.Pan,
                    IsMuted = td.IsMuted,
                    IsSolo  = td.IsSolo,
                    Type    = Enum.TryParse<Models.TrackType>(td.Type, out var tt) ? tt : Models.TrackType.Audio,
                    Envelope = (td.Envelope ?? new List<EnvelopePoint>()).OrderBy(p => p.Time).ToList()
                };
                if (td.Effects != null)
                {
                    var fx = track.Effects;
                    fx.EqEnabled = td.Effects.EqEnabled; fx.EqLow = td.Effects.EqLow; fx.EqMid = td.Effects.EqMid; fx.EqHigh = td.Effects.EqHigh;
                    fx.ReverbEnabled = td.Effects.ReverbEnabled; fx.ReverbMix = td.Effects.ReverbMix; fx.ReverbRoom = td.Effects.ReverbRoom;
                    fx.DelayEnabled = td.Effects.DelayEnabled; fx.DelayTime = td.Effects.DelayTime; fx.DelayFeedback = td.Effects.DelayFeedback;
                    fx.CompressorEnabled = td.Effects.CompressorEnabled; fx.CompThreshold = td.Effects.CompThreshold;
                    fx.CompRatio = td.Effects.CompRatio; fx.CompAttack = td.Effects.CompAttack; fx.CompRelease = td.Effects.CompRelease;
                    fx.PitchEnabled = td.Effects.PitchEnabled; fx.PitchSemitones = td.Effects.PitchSemitones;
                    fx.NoiseGateEnabled = td.Effects.NoiseGateEnabled; fx.GateThreshold = td.Effects.GateThreshold;
                    fx.BassBostEnabled = td.Effects.BassBostEnabled; fx.BassGain = td.Effects.BassGain;
                    fx.ChorusEnabled = td.Effects.ChorusEnabled; fx.ChorusDepth = td.Effects.ChorusDepth;
                    fx.ReverbDamping = td.Effects.ReverbDamping; fx.ReverbPreDelay = td.Effects.ReverbPreDelay;
                    fx.DelayMix = td.Effects.DelayMix; fx.CompMakeup = td.Effects.CompMakeup;
                    fx.ChorusRate = td.Effects.ChorusRate; fx.ChorusMix = td.Effects.ChorusMix;
                    fx.EqLowFreq = td.Effects.EqLowFreq; fx.EqMidFreq = td.Effects.EqMidFreq; fx.EqMidQ = td.Effects.EqMidQ;
                    fx.EqHighFreq = td.Effects.EqHighFreq; fx.HighPassFreq = td.Effects.HighPassFreq; fx.LowPassFreq = td.Effects.LowPassFreq;
                    var order = (td.Effects.Order ?? new List<string>())
                        .Select(o => Enum.TryParse<EffectType>(o, out var et) ? (EffectType?)et : null)
                        .Where(o => o.HasValue).Select(o => o!.Value).Distinct().ToList();
                    fx.Order = order.Count > 0 ? order : TrackEffects.DefaultOrder();
                }
                foreach (var cd in td.Clips)
                {
                    var clip = new Models.AudioClip
                    {
                        Id        = cd.Id,
                        Name      = cd.Name,
                        FilePath  = cd.FilePath,
                        StartTime = cd.StartTime,
                        Duration  = cd.Duration,
                        TrimStart = cd.TrimStart,
                        SourceDuration = cd.SourceDuration > 0 ? cd.SourceDuration : GetFileLength(cd.FilePath),
                        WaveformData = GetWaveform(cd.FilePath)
                    };
                    track.Clips.Add(clip);
                }
                Project.Tracks.Add(track);
            }
        }

        private void DoSave(string path)
        {
            try
            {
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(ToSaveData(), Newtonsoft.Json.Formatting.Indented);
                System.IO.File.WriteAllText(path, json, System.Text.Encoding.UTF8);
                Announce(string.Format(Lang.T("project_saved"), System.IO.Path.GetFileName(path)));
            }
            catch (Exception ex)
            {
                DebugLog.Write("Save failed: " + ex);
                MessageBox.Show(string.Format(Lang.T("save_error"), ex.Message), Lang.T("error_title"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenProject()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = Lang.T("dlg_open_project"),
                Filter = Lang.T("project_filter")
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                string json = System.IO.File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8);
                var data = Newtonsoft.Json.JsonConvert.DeserializeObject<ProjectSaveData>(json);
                if (data == null) throw new Exception(Lang.T("invalid_file_format"));
                StopPreviewTimer();
                _engine.Stop(); IsPlaying = false;
                ApplySaveData(data, keepNameAndPath: false);
                SelectionStart = SelectionEnd = null;
                Project.FilePath = dlg.FileName;
                _undoStack.Clear(); _redoStack.Clear();
                SelectedTrack = Project.Tracks.FirstOrDefault();
                PlayheadPosition = 0;
                NotifyEdited();
                Announce(string.Format(Lang.T("project_opened"), Project.Name, Project.Tracks.Count));
            }
            catch (Exception ex)
            {
                DebugLog.Write("Open failed: " + ex);
                MessageBox.Show(string.Format(Lang.T("open_error"), ex.Message), Lang.T("error_title"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public Action? OnToggleWorkspaceMode { get; set; }
        public Action? OnRebuildTrackList { get; set; }
        public Action? OnShowLog { get; set; }
        public Action<AudioTrack, EffectType>? OnOpenEffect { get; set; }

        // ════════════════════════════════════════════════════════════════════
        // STATUS
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// F6 — "gde sam": vreme, koliko do kraja, izabrana traka, šta je ispod pozicije
        /// i najbliži marker. Samo izgovori — bez otvaranja prozora.
        /// </summary>
        public void AnnounceQuickStatus()
        {
            double ph = PlayheadPosition;
            double end = Project.ContentEnd;
            var parts = new List<string>();

            if (IsRecording) parts.Add(string.Format(Lang.T("qs_recording"), _recorder.RecordedSeconds));
            if (HasSelection) parts.Add(SelectionText);
            parts.Add(string.Format(Lang.T("qs_time"), Fmt(ph), Fmt(end), Fmt(Math.Max(0, end - ph))));

            var track = SelectedTrack;
            if (track != null)
            {
                parts.Add(string.Format(Lang.T("qs_track"), track.Name,
                    track.IsMuted ? Lang.T("qs_muted") : "", track.IsSolo ? Lang.T("qs_solo") : ""));
                var under = track.Clips.FirstOrDefault(c => c.StartTime <= ph && c.EndTime > ph);
                parts.Add(under != null
                    ? string.Format(Lang.T("qs_under"), under.Name, Fmt(under.StartTime), Fmt(under.EndTime))
                    : Lang.T("qs_under_none"));
            }
            else
            {
                int playing = Project.Tracks.Count(t => t.Clips.Any(c => c.StartTime <= ph && c.EndTime > ph));
                parts.Add(string.Format(Lang.T("qs_no_track"), playing));
            }

            if (Project.Markers.Count > 0)
            {
                double m = Project.Markers.OrderBy(x => Math.Abs(x - ph)).First();
                double d = m - ph;
                parts.Add(Math.Abs(d) < 0.05
                    ? string.Format(Lang.T("qs_on_marker"), MarkerNumber(m), Project.Markers.Count)
                    : string.Format(d > 0 ? Lang.T("qs_marker_ahead") : Lang.T("qs_marker_behind"),
                        MarkerNumber(m), Project.Markers.Count, Math.Abs(d)));
            }
            Announce(string.Join(" ", parts));
        }

        /// Shift+F6 — kompletan pregled projekta u posebnom prozoru (sve trake, klipovi, markeri)
        public void AnnounceProjectStatus()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(string.Format(Lang.T("st_project"), Project.Name));
            sb.AppendLine($"Playhead: {TimeDisplay} ({PlayheadPosition:F3}s)");
            sb.AppendLine(string.Format(Lang.T("st_tracks"), Project.Tracks.Count));
            foreach (var track in Project.Tracks)
            {
                sb.AppendLine($"");
                sb.AppendLine(string.Format(Lang.T("st_track_line"), track.Name, track.Volume, track.IsMuted ? Lang.T("yes") : Lang.T("no"), track.IsSolo ? Lang.T("yes") : Lang.T("no")));
                if (track.Clips.Count == 0)
                    sb.AppendLine(Lang.T("st_no_clips"));
                else
                    foreach (var clip in track.Clips.OrderBy(c => c.StartTime))
                        sb.AppendLine(string.Format(Lang.T("st_clip_line"), clip.Name, clip.StartTime, clip.Duration, clip.StartTime + clip.Duration));
            }
            sb.AppendLine("");
            if (Project.Markers.Count == 0) sb.AppendLine(Lang.T("st_no_markers"));
            else
            {
                sb.AppendLine(string.Format(Lang.T("st_markers"), Project.Markers.Count));
                int n = 1;
                foreach (var m in Project.Markers) sb.AppendLine(string.Format(Lang.T("st_marker_line"), n++, Fmt(m)));
            }
            var msg = sb.ToString();
            Application.Current?.Dispatcher.Invoke(() =>
            {
                var win = new Views.ProjectStatusWindow(msg) { Owner = Application.Current.MainWindow };
                win.Show();
            });
        }
        public void ToggleWorkspaceMode()
        {
            OnToggleWorkspaceMode?.Invoke();
        }

        /// <summary>Pozovi iz code-behind kada korisnik klikne na klip blok</summary>
        public void SelectClip(AudioClip clip, AudioTrack track)
        {
            SelectedTrack = track;
            SelectedClip = clip;
        }

        public void Dispose()
        {
            try { _recorder.Dispose(); } catch { }
            try { _engine.Dispose(); } catch { }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

// ─── Save/Load DTO klase ────────────────────────────────────────────────────
namespace UltraAudioEditor.ViewModels
{
    public class ProjectSaveData
    {
        public string Name { get; set; } = "";
        public int SampleRate { get; set; } = 44100;
        public int BitDepth { get; set; } = 24;
        public double Bpm { get; set; } = 120;
        public List<double>? Markers { get; set; } = new();
        public AudioTags? Tags { get; set; }
        public List<TrackSaveData> Tracks { get; set; } = new();
    }
    public class TrackSaveData
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Type { get; set; } = "Audio";
        public float Volume { get; set; } = 0.8f;
        public float Pan { get; set; }
        public bool IsMuted { get; set; }
        public bool IsSolo { get; set; }
        public string? ColorHex { get; set; }
        public List<ClipSaveData> Clips { get; set; } = new();
        public List<EnvelopePoint>? Envelope { get; set; }
        public EffectsSaveData? Effects { get; set; }
    }
    public class ClipSaveData
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string FilePath { get; set; } = "";
        public double StartTime { get; set; }
        public double Duration { get; set; }
        public double TrimStart { get; set; }
        public double SourceDuration { get; set; }
    }
    public class EffectsSaveData
    {
        public bool EqEnabled { get; set; }
        public float EqLow { get; set; }
        public float EqMid { get; set; }
        public float EqHigh { get; set; }
        public bool ReverbEnabled { get; set; }
        public float ReverbMix { get; set; }
        public float ReverbRoom { get; set; }
        public bool DelayEnabled { get; set; }
        public float DelayTime { get; set; }
        public float DelayFeedback { get; set; }
        public bool CompressorEnabled { get; set; }
        public float CompThreshold { get; set; }
        public float CompRatio { get; set; }
        public float CompAttack { get; set; }
        public float CompRelease { get; set; }
        public bool PitchEnabled { get; set; }
        public float PitchSemitones { get; set; }
        public bool NoiseGateEnabled { get; set; }
        public float GateThreshold { get; set; }
        public bool BassBostEnabled { get; set; }
        public float BassGain { get; set; }
        public bool ChorusEnabled { get; set; }
        public float ChorusDepth { get; set; }
        // Novi parametri (stari projekti ih nemaju -> podrazumevane vrednosti)
        public float ReverbDamping { get; set; } = 0.5f;
        public float ReverbPreDelay { get; set; } = 15f;
        public float DelayMix { get; set; } = 0.4f;
        public float CompMakeup { get; set; } = 0f;
        public float ChorusRate { get; set; } = 0.8f;
        public float ChorusMix { get; set; } = 0.5f;
        public float EqLowFreq { get; set; } = 200f;
        public float EqMidFreq { get; set; } = 1000f;
        public float EqMidQ { get; set; } = 0.7f;
        public float EqHighFreq { get; set; } = 8000f;
        public float HighPassFreq { get; set; }
        public float LowPassFreq { get; set; }
        public List<string>? Order { get; set; }
    }
}
