using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using UltraComposer.Interop;

namespace UltraComposer.App;

public partial class MainWindow : Window
{
    private readonly KeyboardPianoMapper _mapper = new();
    private readonly HashSet<Key> _heldKeys = new();
    private readonly ObservableCollection<SequencerNoteRow> _sequencerNotes = new();
    private readonly DispatcherTimer _arrangerStatusTimer;
    private readonly DispatcherTimer _accompanimentStatusTimer;
    private bool _engineRunning;
    private bool _isPopulatingSoundFontPresets;
    private bool _isPopulatingAccLayerCombo;
    private bool _isPopulatingAccLayerPresets;
    private bool _isPopulatingAccChordVoicing;
    private bool _isPopulatingAutoThirdScale;

    // --- Multi-trake i mikser ---
    // One note collection per native track, kept in the same order/indices
    // as the engine's own track list (see AddTrackButton_Click/RemoveTrackButton_Click)
    // so _trackNotes[i] always matches native track i.
    private readonly List<ObservableCollection<SequencerNoteRow>> _trackNotes = new();
    private int _selectedTrackIndex = -1;
    private bool _isPopulatingTrackList;
    private bool _isPopulatingTrackSoundFontPresets;

    // --- Sopstveni ritam (custom rhythm/style builder, Auto-pratnja view) ---
    // The hit lists below hold only the *working* bar currently being
    // edited; "Dodaj kao novi takt" snapshots them into _customRhythmBars
    // and clears these back to empty, ready for the next bar/variation -
    // see AddCustomRhythmBarButton_Click.
    private readonly ObservableCollection<CustomRhythmDrumHitRow> _customRhythmDrumHits = new();
    private readonly ObservableCollection<CustomRhythmMelodicHitRow> _customRhythmMelodicHits = new();
    private readonly List<CustomRhythmBarSnapshot> _customRhythmBars = new();

    // Every custom rhythm saved so far via "Sačuvaj ritam" (any number can
    // coexist - see AccompanimentEngine::SetCustomStyle/RemoveCustomStyle).
    // Each entry's NativeStyleIndex is its current slot in the native style
    // list ("Ritam" combo); RemoveSavedCustomRhythmButton_Click keeps every
    // later entry's index in sync whenever one is removed, mirroring the
    // native shift, so this never needs a fresh lookup.
    // _editingSavedCustomRhythmIndex is which entry (if any) "Sačuvaj ritam"
    // currently updates in place rather than adding as a new one - set by
    // saving a brand new rhythm or by "Uredi izabrani", cleared by "Novi
    // ritam" or by removing that same entry.
    private readonly List<SavedCustomRhythm> _savedCustomRhythms = new();
    private int? _editingSavedCustomRhythmIndex;

    // --- Path bookkeeping for project save (.adem, see ProjectFile.cs) ---
    // The native side only exposes a loaded bank/plug-in's display NAME
    // (see AccompanimentGetLayerSoundFontBankName/GetLayerVstInstrumentName),
    // not the file path it was loaded from, and the per-layer/per-track
    // path textboxes themselves get overwritten with that same display name
    // whenever the selection changes (see LoadSelectedAccLayerIntoPanel) -
    // so the actual path has to be remembered here, at the moment of a
    // successful load, or it's lost. Indexed 0=Bass/1=Kontra/2=Harmonija,
    // matching AudioEngineInterop.AccompanimentMelodicLayer; _trackSoundFontPaths
    // is kept parallel to _trackNotes (same index per native track).
    private readonly string?[] _accLayerSoundFontPaths = new string?[3];
    private readonly string?[] _accLayerVstPaths = new string?[3];
    private readonly List<string?> _trackSoundFontPaths = new();
    private readonly List<string?> _trackVstPaths = new();

    /// <summary>Path last saved to or opened from via the Fajl menu - "Sačuvaj projekat" reuses it, "Sačuvaj kao..." always asks again.</summary>
    private string? _currentProjectPath;

    /// <summary>
    /// Name of the currently connected MIDI device, or null if none is
    /// connected. Tracked here because the native side only exposes
    /// "is a port open" (UC_IsMidiPortOpen), not which one by name - needed
    /// to re-render the MIDI status line correctly after a language switch.
    /// </summary>
    private string? _connectedMidiDeviceName;

    // --- Piano roll (a supplementary visual view of the sequencer's notes) ---
    private const double PianoRollCellWidth = 30.0;
    private const double PianoRollCellHeight = 14.0;
    private const int PianoRollLowestPitch = 36; // C2
    private const int PianoRollHighestPitch = 96; // C7
    private SequencerNoteRow? _pianoRollSelectedNote;

    // --- "Slušaj akord" (chord-by-shape entry - see AudioEngine::NoteOnLive/
    // NoteOffLive and SetChordCaptureArmed) - listens for notes actually
    // played live (computer keyboard or MIDI, never sequencer/arranger/
    // multi-track playback) and inserts the played note(s) as one chord into
    // the Sekvenser's or the selected Multi-trake track's note list, reusing
    // whatever Početak/Trajanje that view's "Dodaj notu/akord" row already
    // has set. Only one of the two views can be armed at a time, since the
    // native engine has a single capture slot - _chordCaptureTarget says
    // which (if either) currently is; _chordCaptureTrackIndex is which
    // native track to insert into while MultiTrack is armed (fixed at the
    // moment of arming, not re-read from _selectedTrackIndex on every tick,
    // so switching tracks mid-listen doesn't silently redirect captures).
    private enum ChordCaptureTarget { None, Sequencer, MultiTrack }
    private readonly DispatcherTimer _chordCaptureTimer;
    private ChordCaptureTarget _chordCaptureTarget = ChordCaptureTarget.None;
    private int _chordCaptureTrackIndex = -1;

    public MainWindow()
    {
        InitializeComponent();
        NotesListView.ItemsSource = _sequencerNotes;
        _sequencerNotes.CollectionChanged += (_, _) => RedrawPianoRoll();
        CustomRhythmDrumListView.ItemsSource = _customRhythmDrumHits;
        CustomRhythmMelodicListView.ItemsSource = _customRhythmMelodicHits;

        // The bundled default SoundFont bank is copied next to the exe at
        // build time (see UltraComposer.App.csproj) - prefill its path so
        // "Učitaj banku" works with zero typing.
        SoundFontPathTextBox.Text = System.IO.Path.Combine(AppContext.BaseDirectory, "GeneralUser-GS.sf2");

        _arrangerStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _arrangerStatusTimer.Tick += ArrangerStatusTimer_Tick;

        // Runs continuously (not just while "Sviraj pratnju" is playing),
        // since the current chord can change from left-hand playing at any
        // time - not just while the accompaniment transport itself is
        // running - and there is no native-side event to push that change
        // to the UI, only polling (same reasoning as _arrangerStatusTimer,
        // just not tied to a Play/Stop state here).
        _accompanimentStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _accompanimentStatusTimer.Tick += (_, _) => RefreshAccompanimentCurrentChordText();

        // Only runs while "Slušaj akord" is actually armed (started/stopped
        // in ArmChordCapture/DisarmChordCapture) - a shorter interval than
        // the status timers above since this is the direct result of the
        // person just releasing a key and expects the insert to feel
        // immediate, not the display of a background/ambient state.
        _chordCaptureTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _chordCaptureTimer.Tick += (_, _) => ChordCaptureTimer_Tick();

        Localization.LanguageChanged += ApplyLanguage;
        ApplyLanguage();
        UpdateOctaveText();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _engineRunning = AudioEngineInterop.Init() && AudioEngineInterop.Start();
        StatusText.Text = _engineRunning
            ? Localization.T("Status.EngineRunning")
            : Localization.T("Status.EngineFailed");

        if (_engineRunning)
        {
            RefreshMidiPorts();
            PopulateAccompanimentStyles();
            PopulateAccompanimentManualChordCombos();
            PopulateAccInstrumentLayerCombo();
            PopulateAccChordVoicingCombo();
            PopulateAutoThirdScaleCombo();
            PopulateCustomRhythmCombos();
            _accompanimentStatusTimer.Start();
        }
    }

    // --- Meni: Fajl / Prikaz / Jezik / Pomoć ---

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // --- Projekat (.adem) - save/load, see ProjectFile.cs for the format ---

    private void OpenProjectMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Localization.T("Proj.OpenDialogTitle"),
            Filter = $"{Localization.T("Proj.FileTypeName")} (*.adem)|*.adem|{Localization.T("Common.AllFiles")}|*.*",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        ProjectFile.ProjectData? data;
        try
        {
            data = ProjectFile.Load(dialog.FileName);
        }
        catch (Exception ex)
        {
            StatusText.Text = Localization.F("Proj.StatusLoadFailed", ex.Message);
            return;
        }

        if (data == null)
        {
            StatusText.Text = Localization.F("Proj.StatusLoadFailed", Localization.T("Proj.StatusUnreadable"));
            return;
        }

        ApplyProjectData(data);
        _currentProjectPath = dialog.FileName;
        StatusText.Text = Localization.F("Proj.StatusOpened", dialog.FileName);
    }

    private void SaveProjectMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProjectPath == null)
        {
            SaveProjectAsMenuItem_Click(sender, e);
            return;
        }

        SaveProjectToPath(_currentProjectPath);
    }

    private void SaveProjectAsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Localization.T("Proj.SaveDialogTitle"),
            Filter = $"{Localization.T("Proj.FileTypeName")} (*.adem)|*.adem",
            DefaultExt = ".adem",
            FileName = "moj-projekat.adem",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        SaveProjectToPath(dialog.FileName);
    }

    private void SaveProjectToPath(string path)
    {
        try
        {
            ProjectFile.Save(GatherProjectData(), path);
        }
        catch (Exception ex)
        {
            StatusText.Text = Localization.F("Proj.StatusSaveFailed", ex.Message);
            return;
        }

        _currentProjectPath = path;
        StatusText.Text = Localization.F("Proj.StatusSaved", path);
    }

    private static List<ProjectFile.NoteData> ToNoteDataList(ObservableCollection<SequencerNoteRow> rows)
    {
        var result = new List<ProjectFile.NoteData>(rows.Count);
        foreach (SequencerNoteRow row in rows)
        {
            result.Add(new ProjectFile.NoteData { Pitch = row.Pitch, StartBeat = row.StartBeat, LengthBeats = row.LengthBeats, Velocity = row.Velocity });
        }
        return result;
    }

    private static ObservableCollection<SequencerNoteRow> FromNoteDataList(List<ProjectFile.NoteData> notes)
    {
        var result = new ObservableCollection<SequencerNoteRow>();
        foreach (ProjectFile.NoteData note in notes)
        {
            result.Add(new SequencerNoteRow(note.Pitch, note.StartBeat, note.LengthBeats, note.Velocity));
        }
        return result;
    }

    /// <summary>Native note velocities are 0..1 floats (see BuildSequencedNotesArray); project files always store the 1-127 MIDI-style form the UI uses everywhere else.</summary>
    private static List<ProjectFile.NoteData> ToNoteDataListFromNative(AudioEngineInterop.SequencedNote[] notes)
    {
        var result = new List<ProjectFile.NoteData>(notes.Length);
        foreach (AudioEngineInterop.SequencedNote note in notes)
        {
            int velocity = (int)Math.Round(note.Velocity * 127f);
            if (velocity < 1)
            {
                velocity = 1;
            }
            else if (velocity > 127)
            {
                velocity = 127;
            }
            result.Add(new ProjectFile.NoteData { Pitch = note.Pitch, StartBeat = note.StartBeat, LengthBeats = note.LengthBeats, Velocity = velocity });
        }
        return result;
    }

    private static AudioEngineInterop.SequencedNote[] ToNativeNotes(List<ProjectFile.NoteData> notes)
    {
        var result = new AudioEngineInterop.SequencedNote[notes.Count];
        for (int i = 0; i < notes.Count; i++)
        {
            result[i] = new AudioEngineInterop.SequencedNote
            {
                StartBeat = notes[i].StartBeat,
                LengthBeats = notes[i].LengthBeats,
                Pitch = notes[i].Pitch,
                Velocity = notes[i].Velocity / 127f,
            };
        }
        return result;
    }

    /// <summary>
    /// Collects everything "sve zivo" - the full project state - into one
    /// ProjectData ready for ProjectFile.Save. Reads native state through
    /// AudioEngineInterop wherever a getter exists; falls back to the
    /// path-bookkeeping fields (_accLayerSoundFontPaths etc.) and UI text
    /// boxes where the native side only exposes a display name, not the
    /// actual file path (see those fields' doc comment).
    /// </summary>
    private ProjectFile.ProjectData GatherProjectData()
    {
        var data = new ProjectFile.ProjectData();

        // --- Sekvenser ---
        data.SequencerNotes = ToNoteDataList(_sequencerNotes);
        double.TryParse(TempoTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double seqTempo);
        data.SequencerTempoBpm = seqTempo > 0 ? seqTempo : 120.0;
        double.TryParse(LoopLengthTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double seqLoop);
        data.SequencerLoopLengthBeats = seqLoop > 0 ? seqLoop : 16.0;

        // --- Multi-trake i mikser ---
        int trackCount = AudioEngineInterop.MultiTrackGetTrackCount();
        for (int i = 0; i < trackCount; i++)
        {
            var mode = AudioEngineInterop.MultiTrackGetTrackInstrumentMode(i);
            var track = new ProjectFile.TrackData
            {
                Name = AudioEngineInterop.MultiTrackGetTrackName(i) ?? Localization.F("Mix.DefaultTrackName", i + 1),
                Notes = i < _trackNotes.Count ? ToNoteDataList(_trackNotes[i]) : new List<ProjectFile.NoteData>(),
                Volume = AudioEngineInterop.MultiTrackGetTrackVolume(i),
                Pan = AudioEngineInterop.MultiTrackGetTrackPan(i),
                Mute = AudioEngineInterop.MultiTrackIsTrackMuted(i),
                Solo = AudioEngineInterop.MultiTrackIsTrackSoloed(i),
                InstrumentMode = (int)mode,
                SoundFontPresetIndex = AudioEngineInterop.MultiTrackGetSelectedTrackSoundFontPresetIndex(i),
            };
            if (mode == AudioEngineInterop.TrackInstrumentMode.SoundFont && i < _trackSoundFontPaths.Count)
            {
                track.SoundFontPath = _trackSoundFontPaths[i];
            }
            else if (mode == AudioEngineInterop.TrackInstrumentMode.Vst && i < _trackVstPaths.Count)
            {
                track.VstPath = _trackVstPaths[i];
            }
            data.Tracks.Add(track);
        }
        data.MultiTrackTempoBpm = AudioEngineInterop.MultiTrackGetTempoBpm();
        data.MultiTrackLoopLengthBeats = AudioEngineInterop.MultiTrackGetLoopLengthBeats();

        // --- Aranžer ---
        int patternCount = AudioEngineInterop.ArrangerGetPatternCount();
        for (int i = 0; i < patternCount; i++)
        {
            data.ArrangerPatterns.Add(new ProjectFile.PatternData
            {
                Name = AudioEngineInterop.ArrangerGetPatternName(i) ?? Localization.F("Arr.DefaultPatternName", i + 1),
                LengthBeats = AudioEngineInterop.ArrangerGetPatternLengthBeats(i),
                Notes = ToNoteDataListFromNative(AudioEngineInterop.ArrangerGetPatternNotes(i)),
            });
        }
        int orderCount = AudioEngineInterop.ArrangerGetOrderCount();
        for (int i = 0; i < orderCount; i++)
        {
            data.ArrangerOrder.Add(AudioEngineInterop.ArrangerGetOrderPatternIndexAt(i));
        }
        data.ArrangerTempoBpm = AudioEngineInterop.ArrangerGetTempoBpm();

        // --- Auto-pratnja ---
        var acc = data.Accompaniment;
        acc.SelectedStyleIndex = AudioEngineInterop.AccompanimentGetSelectedStyleIndex();
        acc.TempoBpm = AudioEngineInterop.AccompanimentGetTempoBpm();
        acc.DrumsEnabled = AudioEngineInterop.AccompanimentIsLayerEnabled(AudioEngineInterop.AccompanimentLayer.Drums);
        acc.BassEnabled = AudioEngineInterop.AccompanimentIsLayerEnabled(AudioEngineInterop.AccompanimentLayer.Bass);
        acc.KontraEnabled = AudioEngineInterop.AccompanimentIsLayerEnabled(AudioEngineInterop.AccompanimentLayer.Kontra);
        acc.HarmonijaEnabled = AudioEngineInterop.AccompanimentIsLayerEnabled(AudioEngineInterop.AccompanimentLayer.Harmonija);
        acc.ChordInputAutoFromKeyboard = AudioEngineInterop.IsAccompanimentChordInputAutoFromKeyboard();
        acc.SplitPoint = AudioEngineInterop.AccompanimentGetSplitPoint();
        // The manual chord picker's own selections (not "whatever chord is
        // currently sounding", which in Auto mode is transient, live-playing
        // state rather than a setting worth persisting).
        acc.ManualChordRootPitchClass = AccManualRootComboBox.SelectedIndex >= 0 ? AccManualRootComboBox.SelectedIndex : 0;
        acc.ManualChordQuality = AccManualQualityComboBox.SelectedIndex >= 0 ? AccManualQualityComboBox.SelectedIndex : 0;
        acc.ChordVoicing = (int)AudioEngineInterop.AccompanimentGetChordVoicing();

        FillLayerInstrumentData(acc.Bass, AudioEngineInterop.AccompanimentMelodicLayer.Bass);
        FillLayerInstrumentData(acc.Kontra, AudioEngineInterop.AccompanimentMelodicLayer.Kontra);
        FillLayerInstrumentData(acc.Harmonija, AudioEngineInterop.AccompanimentMelodicLayer.Harmonija);

        // Only the already-saved custom rhythms round-trip (see
        // _savedCustomRhythms) - whatever's still sitting unsaved in the
        // working bar editor is treated like any other unsaved draft (an
        // in-progress sequencer note field, say) and isn't persisted.
        foreach (SavedCustomRhythm saved in _savedCustomRhythms)
        {
            var rhythmData = new ProjectFile.CustomRhythmData { Name = saved.Name };
            foreach (CustomRhythmBarSnapshot bar in saved.Bars)
            {
                rhythmData.Bars.Add(ToCustomRhythmBarData(bar));
            }
            acc.CustomRhythms.Add(rhythmData);
        }

        // --- Auto-terca ---
        data.AutoThird.Enabled = AudioEngineInterop.IsAutoThirdEnabled();
        data.AutoThird.Upper = AudioEngineInterop.IsAutoThirdUpper();
        data.AutoThird.ScaleRootPitchClass = AudioEngineInterop.GetAutoThirdScaleRoot();
        data.AutoThird.ScaleType = (int)AudioEngineInterop.GetAutoThirdScaleType();

        // --- Banka instrumenata (glavni, deljeni instrument za živo sviranje) ---
        if (AudioEngineInterop.IsSoundFontBankLoaded())
        {
            data.MainBank.SoundFontPath = SoundFontPathTextBox.Text?.Trim();
            data.MainBank.SoundFontPresetIndex = AudioEngineInterop.GetSelectedSoundFontPresetIndex();
        }
        if (AudioEngineInterop.IsVstInstrumentLoaded())
        {
            data.MainBank.VstPath = VstPathTextBox.Text?.Trim();
        }

        return data;
    }

    private void FillLayerInstrumentData(ProjectFile.LayerInstrumentData target, AudioEngineInterop.AccompanimentMelodicLayer layer)
    {
        var mode = AudioEngineInterop.AccompanimentGetLayerInstrumentMode(layer);
        target.Mode = (int)mode;
        if (mode == AudioEngineInterop.AccompanimentInstrumentMode.SoundFont)
        {
            target.SoundFontPath = _accLayerSoundFontPaths[(int)layer];
            target.SoundFontPresetIndex = AudioEngineInterop.AccompanimentGetSelectedLayerSoundFontPresetIndex(layer);
        }
        else if (mode == AudioEngineInterop.AccompanimentInstrumentMode.Vst)
        {
            target.VstPath = _accLayerVstPaths[(int)layer];
        }
    }

    /// <summary>Converts one committed custom-rhythm bar to its project-file form - used by GatherProjectData.</summary>
    private static ProjectFile.CustomRhythmBarData ToCustomRhythmBarData(CustomRhythmBarSnapshot bar)
    {
        var barData = new ProjectFile.CustomRhythmBarData { LengthBeats = bar.LengthBeats };
        foreach (CustomRhythmDrumHitRow hit in bar.DrumHits)
        {
            barData.DrumHits.Add(new ProjectFile.CustomRhythmDrumHitData { Voice = (int)hit.Voice, StartBeat = hit.StartBeat, Velocity = hit.Velocity });
        }
        foreach (CustomRhythmMelodicHitRow hit in bar.MelodicHits)
        {
            barData.MelodicHits.Add(new ProjectFile.CustomRhythmMelodicHitData
            {
                Layer = (int)hit.Layer,
                StartBeat = hit.StartBeat,
                LengthBeats = hit.LengthBeats,
                Root = hit.Root,
                Third = hit.Third,
                Fifth = hit.Fifth,
                Seventh = hit.Seventh,
                OctaveOffset = hit.OctaveOffset,
                Velocity = hit.Velocity,
            });
        }
        return barData;
    }

    /// <summary>The inverse of ToCustomRhythmBarData - used by ApplyProjectData.</summary>
    private static CustomRhythmBarSnapshot FromCustomRhythmBarData(ProjectFile.CustomRhythmBarData barData)
    {
        var drumHits = new List<CustomRhythmDrumHitRow>();
        foreach (ProjectFile.CustomRhythmDrumHitData hit in barData.DrumHits)
        {
            drumHits.Add(new CustomRhythmDrumHitRow((AudioEngineInterop.CustomDrumVoice)hit.Voice, hit.StartBeat, hit.Velocity));
        }
        var melodicHits = new List<CustomRhythmMelodicHitRow>();
        foreach (ProjectFile.CustomRhythmMelodicHitData hit in barData.MelodicHits)
        {
            melodicHits.Add(new CustomRhythmMelodicHitRow(
                (AudioEngineInterop.AccompanimentMelodicLayer)hit.Layer, hit.StartBeat, hit.LengthBeats,
                hit.Root, hit.Third, hit.Fifth, hit.Seventh, hit.OctaveOffset, hit.Velocity));
        }
        return new CustomRhythmBarSnapshot(barData.LengthBeats, drumHits, melodicHits);
    }

    /// <summary>
    /// Restores everything a ProjectData carries back into the native
    /// engine and the UI - the inverse of GatherProjectData. Existing
    /// sequencer/track/pattern content is replaced entirely (matching
    /// "Otvori projekat" opening a *different* project, not merging into
    /// the current one).
    /// </summary>
    private void ApplyProjectData(ProjectFile.ProjectData data)
    {
        // --- Sekvenser ---
        _sequencerNotes.Clear();
        foreach (ProjectFile.NoteData note in data.SequencerNotes)
        {
            _sequencerNotes.Add(new SequencerNoteRow(note.Pitch, note.StartBeat, note.LengthBeats, note.Velocity));
        }
        PushNotesToEngine();
        AudioEngineInterop.SetSequencerTempoBpm(data.SequencerTempoBpm);
        AudioEngineInterop.SetSequencerLoopLengthBeats(data.SequencerLoopLengthBeats);
        TempoTextBox.Text = data.SequencerTempoBpm.ToString("0.##", CultureInfo.InvariantCulture);
        LoopLengthTextBox.Text = data.SequencerLoopLengthBeats.ToString("0.##", CultureInfo.InvariantCulture);
        SequencerStatusText.Text = _sequencerNotes.Count == 0 ? Localization.T("Seq.StatusEmpty") : Localization.T("Seq.StatusStopped");

        // --- Multi-trake i mikser --- (remove every existing track first, then rebuild from the file)
        while (AudioEngineInterop.MultiTrackGetTrackCount() > 0)
        {
            AudioEngineInterop.MultiTrackRemoveTrack(0);
        }
        _trackNotes.Clear();
        _trackSoundFontPaths.Clear();
        _trackVstPaths.Clear();
        _selectedTrackIndex = -1;

        foreach (ProjectFile.TrackData track in data.Tracks)
        {
            int index = AudioEngineInterop.MultiTrackAddTrack(track.Name);
            _trackNotes.Add(FromNoteDataList(track.Notes));
            _trackSoundFontPaths.Add(null);
            _trackVstPaths.Add(null);
            AudioEngineInterop.MultiTrackSetTrackNotes(index, ToNativeNotes(track.Notes));
            AudioEngineInterop.MultiTrackSetTrackVolume(index, track.Volume);
            AudioEngineInterop.MultiTrackSetTrackPan(index, track.Pan);
            AudioEngineInterop.MultiTrackSetTrackMute(index, track.Mute);
            AudioEngineInterop.MultiTrackSetTrackSolo(index, track.Solo);

            if (track.InstrumentMode == (int)AudioEngineInterop.TrackInstrumentMode.SoundFont && !string.IsNullOrEmpty(track.SoundFontPath))
            {
                AudioEngineInterop.MultiTrackLoadTrackSoundFontBank(index, track.SoundFontPath);
                if (track.SoundFontPresetIndex >= 0)
                {
                    AudioEngineInterop.MultiTrackSelectTrackSoundFontPreset(index, track.SoundFontPresetIndex);
                }
                _trackSoundFontPaths[index] = track.SoundFontPath;
            }
            else if (track.InstrumentMode == (int)AudioEngineInterop.TrackInstrumentMode.Vst && !string.IsNullOrEmpty(track.VstPath))
            {
                AudioEngineInterop.MultiTrackLoadTrackVstInstrument(index, track.VstPath);
                _trackVstPaths[index] = track.VstPath;
            }
            else
            {
                AudioEngineInterop.MultiTrackSetTrackInstrumentMode(index, AudioEngineInterop.TrackInstrumentMode.BuiltInSynth);
            }
        }
        AudioEngineInterop.MultiTrackSetTempoBpm(data.MultiTrackTempoBpm);
        AudioEngineInterop.MultiTrackSetLoopLengthBeats(data.MultiTrackLoopLengthBeats);
        RefreshTrackList();
        MixTrackDetailPanel.IsEnabled = false;
        MixStatusText.Text = AudioEngineInterop.MultiTrackGetTrackCount() == 0 ? Localization.T("Mix.StatusEmpty") : Localization.T("Mix.StatusStopped");

        // --- Aranžer --- (remove every existing pattern first, then rebuild)
        AudioEngineInterop.ArrangerClearOrder();
        while (AudioEngineInterop.ArrangerGetPatternCount() > 0)
        {
            AudioEngineInterop.ArrangerRemovePattern(0);
        }
        foreach (ProjectFile.PatternData pattern in data.ArrangerPatterns)
        {
            AudioEngineInterop.ArrangerAddPattern(pattern.Name, pattern.LengthBeats, ToNativeNotes(pattern.Notes));
        }
        foreach (int patternIndex in data.ArrangerOrder)
        {
            AudioEngineInterop.ArrangerAppendToOrder(patternIndex);
        }
        AudioEngineInterop.ArrangerSetTempoBpm(data.ArrangerTempoBpm);
        RefreshArrangerPatternsList();
        RefreshArrangerOrderList();
        ArrangerStatusText.Text = Localization.T("Arr.StatusStopped");

        // --- Auto-pratnja ---
        var acc = data.Accompaniment;
        AudioEngineInterop.AccompanimentSetLayerEnabled(AudioEngineInterop.AccompanimentLayer.Drums, acc.DrumsEnabled);
        AudioEngineInterop.AccompanimentSetLayerEnabled(AudioEngineInterop.AccompanimentLayer.Bass, acc.BassEnabled);
        AudioEngineInterop.AccompanimentSetLayerEnabled(AudioEngineInterop.AccompanimentLayer.Kontra, acc.KontraEnabled);
        AudioEngineInterop.AccompanimentSetLayerEnabled(AudioEngineInterop.AccompanimentLayer.Harmonija, acc.HarmonijaEnabled);
        AccDrumsCheckBox.IsChecked = acc.DrumsEnabled;
        AccBassCheckBox.IsChecked = acc.BassEnabled;
        AccKontraCheckBox.IsChecked = acc.KontraEnabled;
        AccHarmonijaCheckBox.IsChecked = acc.HarmonijaEnabled;

        AudioEngineInterop.AccompanimentSetChordInputAutoFromKeyboard(acc.ChordInputAutoFromKeyboard);
        AccChordAutoRadio.IsChecked = acc.ChordInputAutoFromKeyboard;
        AccChordManualRadio.IsChecked = !acc.ChordInputAutoFromKeyboard;
        AudioEngineInterop.AccompanimentSetSplitPoint(acc.SplitPoint);
        AccSplitPointTextBox.Text = acc.SplitPoint.ToString(CultureInfo.InvariantCulture);

        if (AccManualRootComboBox.Items.Count > acc.ManualChordRootPitchClass)
        {
            AccManualRootComboBox.SelectedIndex = acc.ManualChordRootPitchClass;
        }
        if (AccManualQualityComboBox.Items.Count > acc.ManualChordQuality)
        {
            AccManualQualityComboBox.SelectedIndex = acc.ManualChordQuality;
        }
        AudioEngineInterop.AccompanimentSetManualChord(acc.ManualChordRootPitchClass, (AudioEngineInterop.ChordQuality)acc.ManualChordQuality);

        AudioEngineInterop.AccompanimentSetChordVoicing((AudioEngineInterop.AccompanimentChordVoicing)acc.ChordVoicing);

        ApplyLayerInstrumentData(acc.Bass, AudioEngineInterop.AccompanimentMelodicLayer.Bass);
        ApplyLayerInstrumentData(acc.Kontra, AudioEngineInterop.AccompanimentMelodicLayer.Kontra);
        ApplyLayerInstrumentData(acc.Harmonija, AudioEngineInterop.AccompanimentMelodicLayer.Harmonija);

        // Clear the working bar editor (a fresh, empty draft after reload -
        // see GatherProjectData's comment on why unsaved bars don't round-
        // trip) and every previously-installed custom style, then rebuild
        // each saved rhythm fresh, in the same order they were saved in -
        // that's what keeps each one's native style index (and so the
        // "Ritam" selection below) lined up with what was saved.
        _customRhythmDrumHits.Clear();
        _customRhythmMelodicHits.Clear();
        _customRhythmBars.Clear();
        _editingSavedCustomRhythmIndex = null;
        _savedCustomRhythms.Clear();
        CustomRhythmNameTextBox.Text = string.Empty;
        CustomRhythmBarsStatusText.Text = Localization.T("Cr.StatusNoBars");
        CustomRhythmStatusText.Text = string.Empty;

        foreach (ProjectFile.CustomRhythmData rhythmData in acc.CustomRhythms)
        {
            var bars = new List<CustomRhythmBarSnapshot>();
            foreach (ProjectFile.CustomRhythmBarData barData in rhythmData.Bars)
            {
                bars.Add(FromCustomRhythmBarData(barData));
            }
            string name = rhythmData.Name.Length > 0 ? rhythmData.Name : Localization.T("Cr.DefaultName");
            int index = InstallCustomRhythmFromBars(name, acc.TempoBpm, bars);
            if (index >= 0)
            {
                _savedCustomRhythms.Add(new SavedCustomRhythm { Name = name, Bars = bars, NativeStyleIndex = index });
            }
        }
        RefreshSavedCustomRhythmsList();

        AudioEngineInterop.AccompanimentSetStyleIndex(acc.SelectedStyleIndex);
        AudioEngineInterop.AccompanimentSetTempoBpm(acc.TempoBpm);
        AccTempoTextBox.Text = acc.TempoBpm.ToString("0.##", CultureInfo.InvariantCulture);

        PopulateAccompanimentStyles();
        PopulateAccompanimentManualChordCombos();
        PopulateAccInstrumentLayerCombo();
        PopulateAccChordVoicingCombo();
        RefreshAccompanimentCurrentChordText();
        AccStatusText.Text = Localization.T("Acc.StatusStopped");

        // --- Auto-terca ---
        AudioEngineInterop.SetAutoThirdEnabled(data.AutoThird.Enabled);
        AudioEngineInterop.SetAutoThirdUpper(data.AutoThird.Upper);
        AudioEngineInterop.SetAutoThirdScale(data.AutoThird.ScaleRootPitchClass, (AudioEngineInterop.ScaleType)data.AutoThird.ScaleType);
        AutoThirdCheckBox.IsChecked = data.AutoThird.Enabled;
        AutoThirdUpperRadio.IsChecked = data.AutoThird.Upper;
        AutoThirdLowerRadio.IsChecked = !data.AutoThird.Upper;
        PopulateAutoThirdScaleCombo();

        // --- Banka instrumenata (glavni instrument) ---
        if (!string.IsNullOrEmpty(data.MainBank.SoundFontPath))
        {
            SoundFontPathTextBox.Text = data.MainBank.SoundFontPath;
            LoadSoundFontButton_Click(this, new RoutedEventArgs());
            if (data.MainBank.SoundFontPresetIndex >= 0 && data.MainBank.SoundFontPresetIndex != AudioEngineInterop.GetSelectedSoundFontPresetIndex())
            {
                AudioEngineInterop.SelectSoundFontPreset(data.MainBank.SoundFontPresetIndex);
                if (data.MainBank.SoundFontPresetIndex < SoundFontPresetComboBox.Items.Count)
                {
                    SoundFontPresetComboBox.SelectedIndex = data.MainBank.SoundFontPresetIndex;
                }
            }
        }
        if (!string.IsNullOrEmpty(data.MainBank.VstPath))
        {
            VstPathTextBox.Text = data.MainBank.VstPath;
            LoadVstButton_Click(this, new RoutedEventArgs());
        }
    }

    private void ApplyLayerInstrumentData(ProjectFile.LayerInstrumentData layerData, AudioEngineInterop.AccompanimentMelodicLayer layer)
    {
        var mode = (AudioEngineInterop.AccompanimentInstrumentMode)layerData.Mode;
        if (mode == AudioEngineInterop.AccompanimentInstrumentMode.SoundFont && !string.IsNullOrEmpty(layerData.SoundFontPath))
        {
            AudioEngineInterop.AccompanimentLoadLayerSoundFontBank(layer, layerData.SoundFontPath);
            if (layerData.SoundFontPresetIndex >= 0)
            {
                AudioEngineInterop.AccompanimentSelectLayerSoundFontPreset(layer, layerData.SoundFontPresetIndex);
            }
            _accLayerSoundFontPaths[(int)layer] = layerData.SoundFontPath;
        }
        else if (mode == AudioEngineInterop.AccompanimentInstrumentMode.Vst && !string.IsNullOrEmpty(layerData.VstPath))
        {
            AudioEngineInterop.AccompanimentLoadLayerVstInstrument(layer, layerData.VstPath);
            _accLayerVstPaths[(int)layer] = layerData.VstPath;
        }
        else
        {
            AudioEngineInterop.AccompanimentSetLayerInstrumentMode(layer, AudioEngineInterop.AccompanimentInstrumentMode.BuiltInSynth);
        }
    }

    /// <summary>
    /// The single "Uputstvo za upotrebu"/"User Guide" window, if one is
    /// currently open - reused (brought to front) rather than opening a
    /// second one on repeat menu clicks. Non-modal (Show, not ShowDialog) so
    /// it can sit open side by side with the main window while trying steps.
    /// </summary>
    private HelpWindow? _helpWindow;

    private void UserGuideMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_helpWindow != null)
        {
            _helpWindow.Activate();
            return;
        }

        _helpWindow = new HelpWindow();
        _helpWindow.Owner = this;
        _helpWindow.Closed += (_, _) => _helpWindow = null;
        _helpWindow.Show();
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            this,
            string.Format(Localization.T("About.Text"), GetAppVersion()),
            Localization.T("About.Title"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    /// <summary>
    /// The version shown in the About dialog - reflects the single
    /// &lt;Version&gt; property in UltraComposer.App.csproj (bump that one
    /// place before each GitHub tag/release), same pattern as Ultra Video
    /// Editor/Ultra Audio Editor. Falls back to "dev" if, for whatever
    /// reason, no informational version was embedded at build time.
    /// </summary>
    private static string GetAppVersion()
    {
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return string.IsNullOrWhiteSpace(info) ? "dev" : info;
    }

    private void LanguageSerbianMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Localization.SetLanguage(AppLanguage.Serbian);
        LanguageSerbianMenuItem.IsChecked = true;
        LanguageEnglishMenuItem.IsChecked = false;
    }

    private void LanguageEnglishMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Localization.SetLanguage(AppLanguage.English);
        LanguageEnglishMenuItem.IsChecked = true;
        LanguageSerbianMenuItem.IsChecked = false;
    }

    private void ShowInstrumentsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SwitchView(AppView.Instruments);
    }

    private void ShowSequencerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SwitchView(AppView.Sequencer);
    }

    private void ShowArrangerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SwitchView(AppView.Arranger);
    }

    private void ShowMultiTrackMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SwitchView(AppView.MultiTrack);
    }

    private void ShowAccompanimentMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SwitchView(AppView.Accompaniment);
    }

    private enum AppView
    {
        Instruments,
        Sequencer,
        Arranger,
        MultiTrack,
        Accompaniment,
    }

    /// <summary>
    /// Shows only the chosen view's controls and hides the others -
    /// everything shared (the loaded VST3/SoundFont instrument, MIDI
    /// connection, selected preset) lives in the native engine, not in any
    /// one view, so switching views never affects it.
    /// </summary>
    private void SwitchView(AppView view)
    {
        InstrumentsView.Visibility = view == AppView.Instruments ? Visibility.Visible : Visibility.Collapsed;
        SequencerView.Visibility = view == AppView.Sequencer ? Visibility.Visible : Visibility.Collapsed;
        ArrangerView.Visibility = view == AppView.Arranger ? Visibility.Visible : Visibility.Collapsed;
        MultiTrackView.Visibility = view == AppView.MultiTrack ? Visibility.Visible : Visibility.Collapsed;
        AccompanimentView.Visibility = view == AppView.Accompaniment ? Visibility.Visible : Visibility.Collapsed;

        ShowInstrumentsMenuItem.IsChecked = view == AppView.Instruments;
        ShowSequencerMenuItem.IsChecked = view == AppView.Sequencer;
        ShowArrangerMenuItem.IsChecked = view == AppView.Arranger;
        ShowMultiTrackMenuItem.IsChecked = view == AppView.MultiTrack;
        ShowAccompanimentMenuItem.IsChecked = view == AppView.Accompaniment;

        if (view == AppView.Arranger)
        {
            RefreshArrangerPatternsList();
            RefreshArrangerOrderList();
        }
        else if (view == AppView.Sequencer && PianoRollToggleCheckBox.IsChecked == true)
        {
            RedrawPianoRoll();
        }
        else if (view == AppView.MultiTrack)
        {
            RefreshTrackList();
        }
        else if (view == AppView.Accompaniment)
        {
            RefreshAccompanimentCurrentChordText();
        }
    }

    /// <summary>
    /// Re-applies every piece of UI text from <see cref="Localization"/> in
    /// the currently selected language. Called once at startup and again
    /// every time the language is switched (see Localization.LanguageChanged).
    /// Status lines that depend on runtime state (what's loaded/connected,
    /// whether something is playing) are recomputed from that state rather
    /// than just re-translated word for word, so they stay accurate; a
    /// transient one-off message (e.g. "note added") isn't retroactively
    /// translated if it's already on screen - only what's shown from this
    /// point forward is guaranteed correct in the new language.
    /// </summary>
    private void ApplyLanguage()
    {
        static string T(string key) => Localization.T(key);

        FileMenuItem.Header = T("Menu.File");
        OpenProjectMenuItem.Header = T("Menu.File.OpenProject");
        SaveProjectMenuItem.Header = T("Menu.File.SaveProject");
        SaveProjectAsMenuItem.Header = T("Menu.File.SaveProjectAs");
        ExitMenuItem.Header = T("Menu.File.Exit");
        AutomationProperties.SetName(ExitMenuItem, T("Menu.File.Exit.AutomationName"));

        ViewMenuItem.Header = T("Menu.View");
        AutomationProperties.SetName(ViewMenuItem, T("Menu.View.AutomationName"));
        ShowInstrumentsMenuItem.Header = T("Menu.View.Instruments");
        ShowSequencerMenuItem.Header = T("Menu.View.Sequencer");
        ShowArrangerMenuItem.Header = T("Menu.View.Arranger");
        ShowMultiTrackMenuItem.Header = T("Menu.View.MultiTrack");

        LanguageMenuItem.Header = T("Menu.Language");
        LanguageSerbianMenuItem.Header = T("Menu.Language.Serbian");
        LanguageEnglishMenuItem.Header = T("Menu.Language.English");

        HelpMenuItem.Header = T("Menu.Help");
        UserGuideMenuItem.Header = T("Menu.Help.UserGuide");
        AboutMenuItem.Header = T("Menu.Help.About");

        // --- MIDI ---
        MidiLabelText.Text = T("Midi.Label");
        AutomationProperties.SetName(MidiPortComboBox, T("Midi.ComboAutomationName"));
        RefreshMidiButton.Content = T("Midi.RefreshButton");
        bool midiOpen = _engineRunning && AudioEngineInterop.IsMidiPortOpen();
        ConnectMidiButton.Content = midiOpen ? T("Midi.DisconnectButton") : T("Midi.ConnectButton");
        MidiStatusText.Text = midiOpen && _connectedMidiDeviceName != null
            ? Localization.F("Midi.StatusConnected", _connectedMidiDeviceName)
            : T("Midi.StatusNoDeviceShort");

        // --- VST3 ---
        VstIntroText.Text = T("Vst.Intro");
        AutomationProperties.SetName(VstPathTextBox, T("Vst.PathAutomationName"));
        BrowseVstButton.Content = T("Common.Browse");
        LoadVstButton.Content = T("Vst.LoadButton");
        UnloadVstButton.Content = T("Vst.UnloadButton");
        VstStatusText.Text = _engineRunning && AudioEngineInterop.IsVstInstrumentLoaded()
            ? Localization.F("Vst.StatusLoaded", AudioEngineInterop.GetVstInstrumentName() ?? T("Vst.DefaultInstrumentName"))
            : T("Vst.StatusNoneLoaded");

        // --- SoundFont ---
        SfIntroText.Text = T("Sf.Intro");
        AutomationProperties.SetName(SoundFontPathTextBox, T("Sf.PathAutomationName"));
        BrowseSoundFontButton.Content = T("Common.Browse");
        LoadSoundFontButton.Content = T("Sf.LoadButton");
        UnloadSoundFontButton.Content = T("Sf.UnloadButton");
        SfInstrumentLabelText.Text = T("Sf.InstrumentLabel");
        AutomationProperties.SetName(SoundFontPresetComboBox, T("Sf.ComboAutomationName"));
        if (_engineRunning && AudioEngineInterop.IsSoundFontBankLoaded())
        {
            string bankName = AudioEngineInterop.GetSoundFontBankName() ?? T("Sf.DefaultBankName");
            int presetCount = AudioEngineInterop.GetSoundFontPresetCount();
            int selectedIndex = AudioEngineInterop.GetSelectedSoundFontPresetIndex();
            string instrumentName = selectedIndex >= 0 ? (AudioEngineInterop.GetSoundFontPresetName(selectedIndex) ?? "") : "";
            SoundFontStatusText.Text = Localization.F("Sf.StatusLoaded", bankName, presetCount, instrumentName);
        }
        else
        {
            SoundFontStatusText.Text = T("Sf.StatusNoneLoaded");
        }

        // --- Automatska terca ---
        AutoThirdSectionTitleText.Text = T("AutoThird.SectionTitle");
        AutoThirdCheckBox.Content = T("AutoThird.CheckboxLabel");
        AutoThirdUpperRadio.Content = T("AutoThird.UpperRadio");
        AutoThirdLowerRadio.Content = T("AutoThird.LowerRadio");
        AutoThirdScaleLabelText.Text = T("AutoThird.ScaleLabel");
        AutomationProperties.SetName(AutoThirdScaleRootComboBox, T("AutoThird.ScaleRootAutomationName"));
        AutoThirdScaleMajorRadio.Content = T("AutoThird.ScaleMajorRadio");
        AutoThirdScaleMinorRadio.Content = T("AutoThird.ScaleMinorRadio");
        AutoThirdScaleHijazRadio.Content = T("AutoThird.ScaleHijazRadio");
        AutoThirdScaleHijazKarRadio.Content = T("AutoThird.ScaleHijazKarRadio");
        if (_engineRunning)
        {
            // Pitch-class names are language-dependent, so the combo
            // contents need rebuilding, not just re-reading.
            PopulateAutoThirdScaleCombo();
        }

        // --- Klavijatura za sviranje ---
        AutomationProperties.SetName(PerformanceKeyboardBorder, T("Keyboard.AutomationName"));
        AutomationProperties.SetHelpText(PerformanceKeyboardBorder, T("Keyboard.AutomationHelp"));
        KeyboardIntroText.Text = T("Keyboard.Intro");
        UpdateOctaveText();

        // --- Sekvenser ---
        SeqTitleText.Text = T("Seq.Title");
        SeqNoteLabelText.Text = T("Seq.NoteLabel");
        AutomationProperties.SetName(NotePitchTextBox, T("Seq.NoteAutomationName"));
        AutomationProperties.SetHelpText(NotePitchTextBox, T("Seq.NoteAutomationHelp"));
        SeqStartLabelText.Text = T("Seq.StartLabel");
        AutomationProperties.SetName(NoteStartTextBox, T("Seq.StartAutomationName"));
        SeqLengthLabelText.Text = T("Seq.LengthLabel");
        AutomationProperties.SetName(NoteLengthTextBox, T("Seq.LengthAutomationName"));
        SeqVelocityLabelText.Text = T("Seq.VelocityLabel");
        AutomationProperties.SetName(NoteVelocityTextBox, T("Seq.VelocityAutomationName"));
        AddNoteButton.Content = T("Seq.AddButton");
        ListenForChordButton.Content = T("Seq.ListenButton");
        StopListenForChordButton.Content = T("Seq.StopListenButton");
        AutomationProperties.SetName(NotesListView, T("Seq.ListAutomationName"));
        SeqColNote.Header = T("Seq.ColNote");
        SeqColStart.Header = T("Seq.ColStart");
        SeqColLength.Header = T("Seq.ColLength");
        SeqColVelocity.Header = T("Seq.ColVelocity");
        RemoveNoteButton.Content = T("Seq.RemoveButton");
        ClearNotesButton.Content = T("Seq.ClearButton");
        SeqTempoLabelText.Text = T("Seq.TempoLabel");
        AutomationProperties.SetName(TempoTextBox, T("Seq.TempoAutomationName"));
        SeqLoopLabelText.Text = T("Seq.LoopLabel");
        AutomationProperties.SetName(LoopLengthTextBox, T("Seq.LoopAutomationName"));
        PlaySequencerButton.Content = T("Seq.PlayButton");
        StopSequencerButton.Content = T("Seq.StopButton");
        SequencerStatusText.Text = StopSequencerButton.IsEnabled
            ? T("Seq.StatusPlaying")
            : (_sequencerNotes.Count == 0 ? T("Seq.StatusEmpty") : T("Seq.StatusStopped"));
        PianoRollToggleCheckBox.Content = T("PianoRoll.ToggleCheckbox");
        PianoRollInstructionsText.Text = T("PianoRoll.Instructions");
        AutomationProperties.SetName(PianoRollCanvas, T("PianoRoll.CanvasAutomationName"));
        SeqTipText.Text = T("Seq.Tip");
        NotesListView.Items.Refresh(); // re-render note names (solfege vs letter) in the new language

        // --- Aranžer ---
        ArrTitleText.Text = T("Arr.Title");
        ArrNameLabelText.Text = T("Arr.NameLabel");
        AutomationProperties.SetName(ArrangerPatternNameTextBox, T("Arr.NameAutomationName"));
        SavePatternButton.Content = T("Arr.SaveButton");
        ArrSaveExplainText.Text = T("Arr.SaveExplain");
        ArrPatternsLabelText.Text = T("Arr.PatternsLabel");
        AutomationProperties.SetName(ArrangerPatternsListBox, T("Arr.PatternsAutomationName"));
        AppendPatternToOrderButton.Content = T("Arr.AppendButton");
        RemovePatternButton.Content = T("Arr.RemovePatternButton");
        ArrOrderLabelText.Text = T("Arr.OrderLabel");
        AutomationProperties.SetName(ArrangerOrderListBox, T("Arr.OrderAutomationName"));
        RemoveLastFromOrderButton.Content = T("Arr.RemoveLastButton");
        ClearOrderButton.Content = T("Arr.ClearOrderButton");
        ArrTempoLabelText.Text = T("Arr.TempoLabel");
        AutomationProperties.SetName(ArrangerTempoTextBox, T("Arr.TempoAutomationName"));
        PlayArrangerButton.Content = T("Arr.PlayButton");
        StopArrangerButton.Content = T("Arr.StopButton");
        if (_engineRunning)
        {
            RefreshArrangerPatternsList();
            RefreshArrangerOrderList();
        }
        if (!StopArrangerButton.IsEnabled)
        {
            ArrangerStatusText.Text = (_engineRunning && AudioEngineInterop.ArrangerGetOrderCount() > 0)
                ? T("Arr.StatusStopped")
                : T("Arr.StatusEmpty");
        }

        // --- Multi-trake i mikser ---
        MixTitleText.Text = T("Mix.Title");
        MixNewTrackNameLabelText.Text = T("Mix.NewTrackNameLabel");
        AutomationProperties.SetName(NewTrackNameTextBox, T("Mix.NewTrackNameAutomationName"));
        AddTrackButton.Content = T("Mix.AddTrackButton");
        MixTracksLabelText.Text = T("Mix.TracksLabel");
        AutomationProperties.SetName(MixTracksListBox, T("Mix.TrackListAutomationName"));
        RemoveTrackButton.Content = T("Mix.RemoveTrackButton");
        MixSelectedTrackTitleText.Text = T("Mix.SelectedTrackTitle");
        MixRenameLabelText.Text = T("Mix.RenameLabel");
        AutomationProperties.SetName(TrackNameTextBox, T("Mix.RenameAutomationName"));
        RenameTrackButton.Content = T("Mix.RenameButton");
        MixInstrumentModeLabelText.Text = T("Mix.InstrumentModeLabel");
        TrackSynthRadio.Content = T("Mix.SynthRadio");
        TrackSoundFontRadio.Content = T("Mix.SoundFontRadio");
        TrackVstRadio.Content = T("Mix.VstRadio");
        MixSfPathLabelText.Text = T("Mix.SfPathLabel");
        AutomationProperties.SetName(TrackSoundFontPathTextBox, T("Mix.SfPathAutomationName"));
        BrowseTrackSoundFontButton.Content = T("Common.Browse");
        LoadTrackSoundFontButton.Content = T("Mix.LoadBankButton");
        MixVstPathLabelText.Text = T("Mix.VstPathLabel");
        AutomationProperties.SetName(TrackVstPathTextBox, T("Mix.VstPathAutomationName"));
        BrowseTrackVstButton.Content = T("Common.Browse");
        LoadTrackVstButton.Content = T("Mix.LoadVstButton");
        MixPresetLabelText.Text = T("Mix.PresetLabel");
        AutomationProperties.SetName(TrackSoundFontPresetComboBox, T("Mix.PresetAutomationName"));
        MixVolumeLabelText.Text = T("Mix.VolumeLabel");
        AutomationProperties.SetName(TrackVolumeTextBox, T("Mix.VolumeAutomationName"));
        MixPanLabelText.Text = T("Mix.PanLabel");
        AutomationProperties.SetName(TrackPanTextBox, T("Mix.PanAutomationName"));
        ApplyVolumePanButton.Content = T("Mix.ApplyVolumePanButton");
        TrackMuteCheckBox.Content = T("Mix.MuteCheckbox");
        TrackSoloCheckBox.Content = T("Mix.SoloCheckbox");
        MixNotesTitleText.Text = T("Mix.NotesTitle");
        MixTrackNoteLabelText.Text = T("Seq.NoteLabel");
        AutomationProperties.SetName(TrackNotePitchTextBox, T("Seq.NoteAutomationName"));
        AutomationProperties.SetHelpText(TrackNotePitchTextBox, T("Seq.NoteAutomationHelp"));
        MixTrackStartLabelText.Text = T("Seq.StartLabel");
        AutomationProperties.SetName(TrackNoteStartTextBox, T("Seq.StartAutomationName"));
        MixTrackLengthLabelText.Text = T("Seq.LengthLabel");
        AutomationProperties.SetName(TrackNoteLengthTextBox, T("Seq.LengthAutomationName"));
        MixTrackVelocityLabelText.Text = T("Seq.VelocityLabel");
        AutomationProperties.SetName(TrackNoteVelocityTextBox, T("Seq.VelocityAutomationName"));
        AddTrackNoteButton.Content = T("Seq.AddButton");
        TrackListenForChordButton.Content = T("Seq.ListenButton");
        TrackStopListenForChordButton.Content = T("Seq.StopListenButton");
        AutomationProperties.SetName(TrackNotesListView, T("Mix.TrackNotesListAutomationName"));
        TrackSeqColNote.Header = T("Seq.ColNote");
        TrackSeqColStart.Header = T("Seq.ColStart");
        TrackSeqColLength.Header = T("Seq.ColLength");
        TrackSeqColVelocity.Header = T("Seq.ColVelocity");
        RemoveTrackNoteButton.Content = T("Seq.RemoveButton");
        ClearTrackNotesButton.Content = T("Seq.ClearButton");
        TrackNotesListView.Items.Refresh(); // re-render note names (solfege vs letter) in the new language
        MixTempoLabelText.Text = T("Seq.TempoLabel");
        AutomationProperties.SetName(MixTempoTextBox, T("Mix.TempoAutomationName"));
        MixLoopLabelText.Text = T("Seq.LoopLabel");
        AutomationProperties.SetName(MixLoopLengthTextBox, T("Mix.LoopAutomationName"));
        PlayMultiTrackButton.Content = T("Mix.PlayButton");
        StopMultiTrackButton.Content = T("Mix.StopButton");
        MixExportTitleText.Text = T("Mix.ExportTitle");
        MixExportDurationLabelText.Text = T("Mix.ExportDurationLabel");
        AutomationProperties.SetName(ExportDurationTextBox, T("Mix.ExportDurationAutomationName"));
        ExportWavButton.Content = T("Mix.ExportButton");
        if (_engineRunning)
        {
            RefreshTrackList();
        }
        if (!StopMultiTrackButton.IsEnabled)
        {
            MixStatusText.Text = (_engineRunning && AudioEngineInterop.MultiTrackGetTrackCount() > 0)
                ? T("Mix.StatusStopped")
                : T("Mix.StatusEmpty");
        }

        // --- Auto-pratnja ---
        ShowAccompanimentMenuItem.Header = T("Menu.View.Accompaniment");
        AccTitleText.Text = T("Acc.Title");
        AccStyleLabelText.Text = T("Acc.StyleLabel");
        AutomationProperties.SetName(AccStyleComboBox, T("Acc.StyleAutomationName"));
        AccLayersLabelText.Text = T("Acc.LayersLabel");
        AccDrumsCheckBox.Content = T("Acc.DrumsCheckbox");
        AccBassCheckBox.Content = T("Acc.BassCheckbox");
        AccKontraCheckBox.Content = T("Acc.KontraCheckbox");
        AccHarmonijaCheckBox.Content = T("Acc.HarmonijaCheckbox");
        AccChordInputLabelText.Text = T("Acc.ChordInputLabel");
        AccChordAutoRadio.Content = T("Acc.ChordAutoRadio");
        AccChordManualRadio.Content = T("Acc.ChordManualRadio");
        AccSplitLabelText.Text = T("Acc.SplitLabel");
        AutomationProperties.SetName(AccSplitPointTextBox, T("Acc.SplitAutomationName"));
        ApplySplitPointButton.Content = T("Acc.ApplyButton");
        AccSplitExplainText.Text = T("Acc.SplitExplain");
        AccManualRootLabelText.Text = T("Acc.ManualRootLabel");
        AutomationProperties.SetName(AccManualRootComboBox, T("Acc.ManualRootAutomationName"));
        AccManualQualityLabelText.Text = T("Acc.ManualQualityLabel");
        AutomationProperties.SetName(AccManualQualityComboBox, T("Acc.ManualQualityAutomationName"));
        ApplyManualChordButton.Content = T("Acc.ApplyChordButton");
        AccTempoLabelText.Text = T("Seq.TempoLabel");
        AutomationProperties.SetName(AccTempoTextBox, T("Acc.TempoAutomationName"));
        ApplyAccTempoButton.Content = T("Acc.ApplyButton");
        PlayAccompanimentButton.Content = T("Acc.PlayButton");
        StopAccompanimentButton.Content = T("Acc.StopButton");
        AccTipText.Text = T("Acc.Tip");
        AccInstrumentsTitleText.Text = T("Acc.InstrumentsTitle");
        AccInstrumentLayerLabelText.Text = T("Acc.InstrumentLayerLabel");
        AutomationProperties.SetName(AccInstrumentLayerComboBox, T("Acc.InstrumentLayerAutomationName"));
        AccLayerSynthRadio.Content = T("Acc.LayerSynthRadio");
        AccLayerSoundFontRadio.Content = T("Acc.LayerSoundFontRadio");
        AccLayerVstRadio.Content = T("Acc.LayerVstRadio");
        AccLayerSfPathLabelText.Text = T("Acc.LayerSfPathLabel");
        AutomationProperties.SetName(AccLayerSoundFontPathTextBox, T("Acc.LayerSfPathAutomationName"));
        AccBrowseLayerSoundFontButton.Content = T("Common.Browse");
        AccLoadLayerSoundFontButton.Content = T("Acc.LoadLayerSoundFontButton");
        AccLayerPresetLabelText.Text = T("Acc.LayerPresetLabel");
        AccLayerVstPathLabelText.Text = T("Acc.LayerVstPathLabel");
        AutomationProperties.SetName(AccLayerVstPathTextBox, T("Acc.LayerVstPathAutomationName"));
        AccBrowseLayerVstButton.Content = T("Common.Browse");
        AccLoadLayerVstButton.Content = T("Acc.LoadLayerVstButton");
        AccChordVoicingTitleText.Text = T("Acc.ChordVoicingTitle");
        AccChordVoicingLabelText.Text = T("Acc.ChordVoicingLabel");
        AutomationProperties.SetName(AccChordVoicingComboBox, T("Acc.ChordVoicingAutomationName"));
        AccChordVoicingExplainText.Text = T("Acc.ChordVoicingExplain");
        if (_engineRunning)
        {
            // Style/root/quality names are language-dependent, so the combo
            // contents themselves need rebuilding, not just re-reading -
            // preserve whichever native selection is already active rather
            // than resetting it, so switching language mid-use doesn't
            // silently change the chosen style or chord.
            PopulateAccompanimentStyles();
            PopulateAccompanimentManualChordCombos();
            RefreshAccompanimentCurrentChordText();
            AccStatusText.Text = AudioEngineInterop.IsAccompanimentPlaying()
                ? T("Acc.StatusPlaying")
                : T("Acc.StatusStopped");
            // Layer names ("Bas"/"Kontra"/"Harmonija") are language-dependent
            // too, and this also refreshes AccLayerInstrumentStatusText.
            PopulateAccInstrumentLayerCombo();
            // Chord-voicing option names are language-dependent too.
            PopulateAccChordVoicingCombo();
        }

        // --- Sopstveni ritam (custom rhythm/style builder) ---
        CustomRhythmTitleText.Text = T("Cr.Title");
        CustomRhythmLengthLabelText.Text = T("Cr.LengthLabel");
        AutomationProperties.SetName(CustomRhythmLengthTextBox, T("Cr.LengthAutomationName"));
        CustomRhythmDrumTitleText.Text = T("Cr.DrumTitle");
        CustomRhythmDrumVoiceLabelText.Text = T("Cr.DrumVoiceLabel");
        AutomationProperties.SetName(CustomRhythmDrumVoiceComboBox, T("Cr.DrumVoiceAutomationName"));
        CustomRhythmDrumBeatLabelText.Text = T("Cr.DrumBeatLabel");
        AutomationProperties.SetName(CustomRhythmDrumBeatTextBox, T("Cr.DrumBeatAutomationName"));
        CustomRhythmDrumVelocityLabelText.Text = T("Cr.VelocityLabel");
        AutomationProperties.SetName(CustomRhythmDrumVelocityTextBox, T("Cr.DrumVelocityAutomationName"));
        AddCustomRhythmDrumHitButton.Content = T("Cr.AddDrumHitButton");
        CrColDrumVoice.Header = T("Cr.ColDrumVoice");
        CrColDrumBeat.Header = T("Cr.ColBeat");
        CrColDrumVelocity.Header = T("Cr.ColVelocity");
        RemoveCustomRhythmDrumHitButton.Content = T("Cr.RemoveButton");
        ClearCustomRhythmDrumHitsButton.Content = T("Cr.ClearDrumHitsButton");
        CustomRhythmMelodicTitleText.Text = T("Cr.MelodicTitle");
        CustomRhythmLayerLabelText.Text = T("Cr.LayerLabel");
        AutomationProperties.SetName(CustomRhythmLayerComboBox, T("Cr.LayerAutomationName"));
        CustomRhythmMelodicBeatLabelText.Text = T("Cr.MelodicBeatLabel");
        AutomationProperties.SetName(CustomRhythmMelodicBeatTextBox, T("Cr.MelodicBeatAutomationName"));
        CustomRhythmMelodicLengthLabelText.Text = T("Cr.MelodicLengthLabel");
        AutomationProperties.SetName(CustomRhythmMelodicLengthTextBox, T("Cr.MelodicLengthAutomationName"));
        CustomRhythmTonesLabelText.Text = T("Cr.TonesLabel");
        CustomRhythmRootCheckBox.Content = T("Cr.RootCheckBox");
        CustomRhythmThirdCheckBox.Content = T("Cr.ThirdCheckBox");
        CustomRhythmFifthCheckBox.Content = T("Cr.FifthCheckBox");
        CustomRhythmSeventhCheckBox.Content = T("Cr.SeventhCheckBox");
        CustomRhythmOctaveLabelText.Text = T("Cr.OctaveLabel");
        AutomationProperties.SetName(CustomRhythmOctaveTextBox, T("Cr.OctaveAutomationName"));
        CustomRhythmMelodicVelocityLabelText.Text = T("Cr.VelocityLabel");
        AutomationProperties.SetName(CustomRhythmMelodicVelocityTextBox, T("Cr.MelodicVelocityAutomationName"));
        AddCustomRhythmMelodicHitButton.Content = T("Cr.AddButton");
        CrColLayer.Header = T("Cr.ColLayer");
        CrColMelodicBeat.Header = T("Cr.ColBeat");
        CrColMelodicLength.Header = T("Cr.ColLength");
        CrColTones.Header = T("Cr.ColTones");
        CrColMelodicVelocity.Header = T("Cr.ColVelocity");
        RemoveCustomRhythmMelodicHitButton.Content = T("Cr.RemoveButton");
        ClearCustomRhythmMelodicHitsButton.Content = T("Cr.ClearButton");
        AddCustomRhythmBarButton.Content = T("Cr.AddBarButton");
        RemoveLastCustomRhythmBarButton.Content = T("Cr.RemoveLastBarButton");
        ClearCustomRhythmBarsButton.Content = T("Cr.ClearBarsButton");
        CustomRhythmBarsStatusText.Text = _customRhythmBars.Count == 0
            ? T("Cr.StatusNoBars")
            : Localization.F("Cr.StatusBarCount", _customRhythmBars.Count);
        CustomRhythmNameLabelText.Text = T("Cr.NameLabel");
        AutomationProperties.SetName(CustomRhythmNameTextBox, T("Cr.NameAutomationName"));
        UseCustomRhythmButton.Content = T("Cr.UseButton");
        NewCustomRhythmButton.Content = T("Cr.NewButton");
        CustomRhythmSavedTitleText.Text = T("Cr.SavedTitle");
        AutomationProperties.SetName(SavedCustomRhythmsListBox, T("Cr.SavedListAutomationName"));
        EditSavedCustomRhythmButton.Content = T("Cr.EditSavedButton");
        RemoveSavedCustomRhythmButton.Content = T("Cr.RemoveSavedButton");
        CustomRhythmTipText.Text = T("Cr.Tip");
        if (_engineRunning)
        {
            // Drum voice / layer names inside the combo and every already
            // -added row's display text are language-dependent too.
            PopulateCustomRhythmCombos();
            CustomRhythmDrumListView.Items.Refresh();
            CustomRhythmMelodicListView.Items.Refresh();
        }

        // --- Deljeni status ---
        StatusText.Text = _engineRunning ? T("Status.EngineRunning") : T("Status.EngineNotRunning");
    }

    private void RefreshMidiPorts()
    {
        MidiPortComboBox.Items.Clear();

        int count = AudioEngineInterop.GetMidiPortCount();
        for (int i = 0; i < count; i++)
        {
            MidiPortComboBox.Items.Add(AudioEngineInterop.GetMidiPortName(i) ?? Localization.F("Midi.DefaultDeviceName", i));
        }

        if (count == 0)
        {
            ConnectMidiButton.IsEnabled = false;
            MidiStatusText.Text = Localization.T("Midi.StatusNoDeviceLong");
            return;
        }

        ConnectMidiButton.IsEnabled = true;
        MidiPortComboBox.SelectedIndex = 0;

        if (count == 1)
        {
            // Only one device found - just connect to it directly instead of
            // making the person pick from a list of one.
            ConnectToSelectedMidiPort();
        }
        else
        {
            MidiStatusText.Text = Localization.F("Midi.StatusFoundMultiple", count);
        }
    }

    private void RefreshMidiButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshMidiPorts();
    }

    private void ConnectMidiButton_Click(object sender, RoutedEventArgs e)
    {
        if (AudioEngineInterop.IsMidiPortOpen())
        {
            AudioEngineInterop.CloseMidiPort();
            _connectedMidiDeviceName = null;
            ConnectMidiButton.Content = Localization.T("Midi.ConnectButton");
            MidiStatusText.Text = Localization.T("Midi.StatusDisconnected");
        }
        else
        {
            ConnectToSelectedMidiPort();
        }
    }

    private void ConnectToSelectedMidiPort()
    {
        int index = MidiPortComboBox.SelectedIndex;
        if (index < 0)
        {
            return;
        }

        string name = MidiPortComboBox.Items[index]?.ToString() ?? "MIDI";
        if (AudioEngineInterop.OpenMidiPort(index))
        {
            _connectedMidiDeviceName = name;
            ConnectMidiButton.Content = Localization.T("Midi.DisconnectButton");
            MidiStatusText.Text = Localization.F("Midi.StatusConnected", name);
        }
        else
        {
            MidiStatusText.Text = Localization.F("Midi.StatusConnectFailed", name);
        }
    }

    private void BrowseVstButton_Click(object sender, RoutedEventArgs e)
    {
        // CheckFileExists is deliberately off: a modern VST3 is often a
        // bundle *folder* (e.g. "Something.vst3\Contents\x86_64-win\...")
        // rather than a single file, and Windows shows that folder as a
        // normal navigable folder here too. Browsing to it and pressing
        // "Open" with its name in the box still fills in the right path;
        // typing/pasting the path directly (see the label above the box)
        // works the same way and avoids this dialog's quirks entirely.
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Localization.T("Vst.BrowseDialogTitle"),
            Filter = $"VST3 (*.vst3)|*.vst3|{Localization.T("Common.AllFiles")}|*.*",
            CheckFileExists = false,
            CheckPathExists = true,
        };

        if (dialog.ShowDialog(this) == true)
        {
            VstPathTextBox.Text = dialog.FileName;
        }
    }

    private void LoadVstButton_Click(object sender, RoutedEventArgs e)
    {
        string path = VstPathTextBox.Text?.Trim() ?? string.Empty;
        if (path.Length == 0)
        {
            VstStatusText.Text = Localization.T("Vst.StatusEmptyPath");
            return;
        }

        string? error = AudioEngineInterop.LoadVstInstrument(path);
        if (error == null)
        {
            string name = AudioEngineInterop.GetVstInstrumentName() ?? Localization.T("Vst.DefaultInstrumentName");
            VstStatusText.Text = Localization.F("Vst.StatusLoaded", name);
            UnloadVstButton.IsEnabled = true;
        }
        else
        {
            VstStatusText.Text = Localization.F("Vst.StatusLoadFailed", error);
        }
    }

    private void UnloadVstButton_Click(object sender, RoutedEventArgs e)
    {
        AudioEngineInterop.UnloadVstInstrument();
        UnloadVstButton.IsEnabled = false;
        VstStatusText.Text = Localization.T("Vst.StatusUnloaded");
    }

    // --- Zvučna banka (SoundFont, .sf2) ---

    private void BrowseSoundFontButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Localization.T("Sf.BrowseDialogTitle"),
            Filter = $"SoundFont (*.sf2)|*.sf2|{Localization.T("Common.AllFiles")}|*.*",
        };

        if (dialog.ShowDialog(this) == true)
        {
            SoundFontPathTextBox.Text = dialog.FileName;
        }
    }

    private void LoadSoundFontButton_Click(object sender, RoutedEventArgs e)
    {
        string path = SoundFontPathTextBox.Text?.Trim() ?? string.Empty;
        if (path.Length == 0)
        {
            SoundFontStatusText.Text = Localization.T("Sf.StatusEmptyPath");
            return;
        }

        string? error = AudioEngineInterop.LoadSoundFontBank(path);
        if (error != null)
        {
            SoundFontStatusText.Text = Localization.F("Sf.StatusLoadFailed", error);
            return;
        }

        string bankName = AudioEngineInterop.GetSoundFontBankName() ?? Localization.T("Sf.DefaultBankName");
        int presetCount = AudioEngineInterop.GetSoundFontPresetCount();
        int selectedIndex = AudioEngineInterop.GetSelectedSoundFontPresetIndex();

        _isPopulatingSoundFontPresets = true;
        SoundFontPresetComboBox.Items.Clear();
        for (int i = 0; i < presetCount; i++)
        {
            SoundFontPresetComboBox.Items.Add(AudioEngineInterop.GetSoundFontPresetName(i) ?? Localization.F("Sf.DefaultInstrumentName", i));
        }
        SoundFontPresetComboBox.SelectedIndex = selectedIndex >= 0 && selectedIndex < presetCount ? selectedIndex : -1;
        _isPopulatingSoundFontPresets = false;

        SoundFontPresetComboBox.IsEnabled = true;
        UnloadSoundFontButton.IsEnabled = true;

        string instrumentName = selectedIndex >= 0 ? (SoundFontPresetComboBox.SelectedItem?.ToString() ?? "") : "";
        SoundFontStatusText.Text = Localization.F("Sf.StatusLoaded", bankName, presetCount, instrumentName);
    }

    private void UnloadSoundFontButton_Click(object sender, RoutedEventArgs e)
    {
        AudioEngineInterop.UnloadSoundFontBank();
        _isPopulatingSoundFontPresets = true;
        SoundFontPresetComboBox.Items.Clear();
        _isPopulatingSoundFontPresets = false;
        SoundFontPresetComboBox.IsEnabled = false;
        UnloadSoundFontButton.IsEnabled = false;
        SoundFontStatusText.Text = Localization.T("Sf.StatusUnloaded");
    }

    private void SoundFontPresetComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isPopulatingSoundFontPresets)
        {
            return;
        }

        int index = SoundFontPresetComboBox.SelectedIndex;
        if (index < 0)
        {
            return;
        }

        if (AudioEngineInterop.SelectSoundFontPreset(index))
        {
            SoundFontStatusText.Text = Localization.F("Sf.StatusPresetChanged", SoundFontPresetComboBox.SelectedItem);
        }
    }

    // --- Sekvenser (jednostavan spisak nota, sviranje u petlji) ---

    private void AddNoteButton_Click(object sender, RoutedEventArgs e)
    {
        // One or more note names/chord tones, separated by comma and/or
        // spaces - e.g. "C4", or "C4, E4, G4" for a whole chord added in
        // one click, all starting at the same beat. A plain MIDI number
        // (0-127) still works too, for anyone who prefers it.
        string[] tokens = NotePitchTextBox.Text.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            SequencerStatusText.Text = Localization.T("Seq.StatusNeedNote");
            return;
        }

        var pitches = new List<int>(tokens.Length);
        foreach (string token in tokens)
        {
            if (!SequencerNoteRow.TryParsePitch(token, out int parsedPitch))
            {
                SequencerStatusText.Text = Localization.F("Seq.StatusInvalidNote", token);
                return;
            }

            pitches.Add(parsedPitch);
        }

        if (!double.TryParse(NoteStartTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double startBeat) || startBeat < 0)
        {
            SequencerStatusText.Text = Localization.T("Seq.StatusInvalidStart");
            return;
        }

        if (!double.TryParse(NoteLengthTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double lengthBeats) || lengthBeats <= 0)
        {
            SequencerStatusText.Text = Localization.T("Seq.StatusInvalidLength");
            return;
        }

        if (!int.TryParse(NoteVelocityTextBox.Text, out int velocity) || velocity < 1 || velocity > 127)
        {
            SequencerStatusText.Text = Localization.T("Seq.StatusInvalidVelocity");
            return;
        }

        foreach (int pitch in pitches)
        {
            _sequencerNotes.Add(new SequencerNoteRow(pitch, startBeat, lengthBeats, velocity));
        }

        PushNotesToEngine();
        SequencerStatusText.Text = pitches.Count == 1
            ? Localization.F("Seq.StatusAddedNote", _sequencerNotes.Count)
            : Localization.F("Seq.StatusAddedChord", pitches.Count, _sequencerNotes.Count);
    }

    private void RemoveNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (NotesListView.SelectedItem is SequencerNoteRow row)
        {
            _sequencerNotes.Remove(row);
            PushNotesToEngine();
            SequencerStatusText.Text = Localization.F("Seq.StatusRemoved", _sequencerNotes.Count);
        }
        else
        {
            SequencerStatusText.Text = Localization.T("Seq.StatusRemoveNeedSelection");
        }
    }

    private void ClearNotesButton_Click(object sender, RoutedEventArgs e)
    {
        _sequencerNotes.Clear();
        PushNotesToEngine();
        SequencerStatusText.Text = Localization.T("Seq.StatusCleared");
    }

    private void ListenForChordButton_Click(object sender, RoutedEventArgs e)
    {
        ArmChordCapture(ChordCaptureTarget.Sequencer, -1);
        ListenForChordButton.IsEnabled = false;
        StopListenForChordButton.IsEnabled = true;
        SequencerStatusText.Text = Localization.T("Seq.StatusChordListening");
    }

    private void StopListenForChordButton_Click(object sender, RoutedEventArgs e)
    {
        DisarmChordCapture();
        ListenForChordButton.IsEnabled = true;
        StopListenForChordButton.IsEnabled = false;
        SequencerStatusText.Text = Localization.T("Seq.StatusChordListenStopped");
    }

    private void PushNotesToEngine()
    {
        AudioEngineInterop.SetSequencerNotes(BuildSequencedNotesArray(_sequencerNotes));
    }

    /// <summary>
    /// Converts a sequencer note list into the native SequencedNote[] shape.
    /// Shared by the plain sequencer (PushNotesToEngine), "Sačuvaj kao
    /// obrazac" in the Aranžer view (which snapshots the plain sequencer's
    /// list into a named Arranger pattern), and every track in the
    /// multi-track view (PushTrackNotesToEngine) - one conversion, several
    /// independent note lists.
    /// </summary>
    private AudioEngineInterop.SequencedNote[] BuildSequencedNotesArray(ObservableCollection<SequencerNoteRow> sourceNotes)
    {
        var notes = new AudioEngineInterop.SequencedNote[sourceNotes.Count];
        for (int i = 0; i < sourceNotes.Count; i++)
        {
            SequencerNoteRow row = sourceNotes[i];
            notes[i] = new AudioEngineInterop.SequencedNote
            {
                StartBeat = row.StartBeat,
                LengthBeats = row.LengthBeats,
                Pitch = row.Pitch,
                Velocity = row.Velocity / 127f,
            };
        }

        return notes;
    }

    /// <summary>Pushes one multi-track track's own note list to the native engine.</summary>
    private void PushTrackNotesToEngine(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= _trackNotes.Count)
        {
            return;
        }
        AudioEngineInterop.MultiTrackSetTrackNotes(trackIndex, BuildSequencedNotesArray(_trackNotes[trackIndex]));
    }

    // --- "Slušaj akord" - shared machinery for both the Sekvenser and
    // Multi-trake "Slušaj akord"/"Prestani da slušaš" button pairs (see the
    // _chordCaptureTimer field's doc comment). Each view's own Click
    // handlers (ListenForChordButton_Click/StopListenForChordButton_Click
    // for the Sekvenser; TrackListenForChordButton_Click/
    // TrackStopListenForChordButton_Click for Multi-trake) only arm/disarm
    // and flip their own two buttons' IsEnabled - everything else happens
    // here.

    /// <summary>
    /// Arms chord capture for <paramref name="target"/>. If the other view
    /// had it armed, its own Start/Stop buttons are restored first, so the
    /// two views never disagree about which one is actually listening (the
    /// native engine only has one capture slot - see AudioEngine.h).
    /// </summary>
    private void ArmChordCapture(ChordCaptureTarget target, int trackIndex)
    {
        if (_chordCaptureTarget == ChordCaptureTarget.Sequencer && target != ChordCaptureTarget.Sequencer)
        {
            ListenForChordButton.IsEnabled = true;
            StopListenForChordButton.IsEnabled = false;
        }
        else if (_chordCaptureTarget == ChordCaptureTarget.MultiTrack && target != ChordCaptureTarget.MultiTrack)
        {
            TrackListenForChordButton.IsEnabled = true;
            TrackStopListenForChordButton.IsEnabled = false;
        }

        _chordCaptureTarget = target;
        _chordCaptureTrackIndex = trackIndex;
        AudioEngineInterop.SetChordCaptureArmed(true);
        _chordCaptureTimer.Start();
    }

    private void DisarmChordCapture()
    {
        _chordCaptureTarget = ChordCaptureTarget.None;
        _chordCaptureTrackIndex = -1;
        AudioEngineInterop.SetChordCaptureArmed(false);
        _chordCaptureTimer.Stop();
    }

    private void ChordCaptureTimer_Tick()
    {
        List<(int Pitch, int Velocity)>? chord = AudioEngineInterop.TryTakeCapturedChord();
        if (chord == null || chord.Count == 0)
        {
            return;
        }

        switch (_chordCaptureTarget)
        {
            case ChordCaptureTarget.Sequencer:
                InsertCapturedChord(_sequencerNotes, chord, NoteStartTextBox, NoteLengthTextBox, PushNotesToEngine, SequencerStatusText);
                break;

            case ChordCaptureTarget.MultiTrack:
                if (_chordCaptureTrackIndex >= 0 && _chordCaptureTrackIndex < _trackNotes.Count)
                {
                    int trackIndex = _chordCaptureTrackIndex;
                    InsertCapturedChord(_trackNotes[trackIndex], chord, TrackNoteStartTextBox, TrackNoteLengthTextBox,
                        () => PushTrackNotesToEngine(trackIndex), MixStatusText);
                }
                break;
        }
    }

    /// <summary>
    /// Inserts one chord captured by "Slušaj akord" into a note list, reusing
    /// the same Početak/Trajanje fields "Dodaj notu/akord" uses so a captured
    /// chord looks exactly like a manually typed one once it's in the list.
    /// Each note keeps its own played velocity rather than the Jačina
    /// textbox's value, since the whole point of playing it live is to
    /// capture how hard each note was struck. Afterwards, Početak
    /// auto-advances by Trajanje so the next captured chord lands right
    /// after this one - matching how a bar-by-bar pattern (a chord per
    /// beat/bar) gets built up without retyping the start beat every time.
    /// </summary>
    private void InsertCapturedChord(
        ObservableCollection<SequencerNoteRow> notes,
        List<(int Pitch, int Velocity)> chord,
        TextBox startTextBox,
        TextBox lengthTextBox,
        Action pushToEngine,
        TextBlock statusText)
    {
        if (!double.TryParse(startTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double startBeat) || startBeat < 0)
        {
            startBeat = 0;
        }
        if (!double.TryParse(lengthTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double lengthBeats) || lengthBeats <= 0)
        {
            lengthBeats = 1.0;
        }

        foreach ((int pitch, int velocity) in chord)
        {
            notes.Add(new SequencerNoteRow(pitch, startBeat, lengthBeats, velocity));
        }

        pushToEngine();

        double nextStart = startBeat + lengthBeats;
        startTextBox.Text = nextStart.ToString(CultureInfo.InvariantCulture);

        statusText.Text = chord.Count == 1
            ? Localization.F("Seq.StatusChordCapturedNote", chord[0].Velocity, startBeat, notes.Count, nextStart)
            : Localization.F("Seq.StatusChordCapturedChord", chord.Count, startBeat, notes.Count, nextStart);
    }

    private void PlaySequencerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sequencerNotes.Count == 0)
        {
            SequencerStatusText.Text = Localization.T("Seq.StatusNeedNoteBeforePlay");
            return;
        }

        if (!double.TryParse(TempoTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double tempo) || tempo <= 0)
        {
            SequencerStatusText.Text = Localization.T("Seq.StatusInvalidTempo");
            return;
        }

        if (!double.TryParse(LoopLengthTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double loopLength) || loopLength <= 0)
        {
            SequencerStatusText.Text = Localization.T("Seq.StatusInvalidLoopLength");
            return;
        }

        AudioEngineInterop.SetSequencerTempoBpm(tempo);
        AudioEngineInterop.SetSequencerLoopLengthBeats(loopLength);
        AudioEngineInterop.PlaySequencer();
        PlaySequencerButton.IsEnabled = false;
        StopSequencerButton.IsEnabled = true;
        SequencerStatusText.Text = Localization.T("Seq.StatusPlaying");

        // The native engine stops the arranger and the multi-track sequencer
        // whenever the plain sequencer starts (all three share one
        // transport) - mirror that here so their views' buttons don't lie
        // about what's actually playing.
        StopArrangerUiOnly();
        StopMultiTrackUiOnly();
    }

    private void StopSequencerButton_Click(object sender, RoutedEventArgs e)
    {
        AudioEngineInterop.StopSequencer();
        PlaySequencerButton.IsEnabled = true;
        StopSequencerButton.IsEnabled = false;
        SequencerStatusText.Text = Localization.T("Seq.StatusStopped");
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        _accompanimentStatusTimer.Stop();
        _chordCaptureTimer.Stop();

        if (_engineRunning)
        {
            AudioEngineInterop.StopSequencer();
            AudioEngineInterop.StopArranger();
            AudioEngineInterop.StopMultiTrack();
            AudioEngineInterop.StopAccompaniment();
            AudioEngineInterop.Stop();
            AudioEngineInterop.Shutdown();
        }
    }

    // --- Automatska terca ---

    private void AutoThirdCheckBox_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = AutoThirdCheckBox.IsChecked == true;
        AudioEngineInterop.SetAutoThirdEnabled(enabled);
        StatusText.Text = enabled
            ? Localization.F("AutoThird.EnabledStatus", CurrentAutoThirdDirectionText())
            : Localization.T("AutoThird.DisabledStatus");
    }

    private void AutoThirdDirection_Click(object sender, RoutedEventArgs e)
    {
        AudioEngineInterop.SetAutoThirdUpper(AutoThirdUpperRadio.IsChecked == true);
        if (AutoThirdCheckBox.IsChecked == true)
        {
            StatusText.Text = Localization.F("AutoThird.DirectionStatus", CurrentAutoThirdDirectionText());
        }
    }

    private string CurrentAutoThirdDirectionText()
    {
        return Localization.T(AutoThirdUpperRadio.IsChecked == true ? "AutoThird.Upper" : "AutoThird.Lower");
    }

    /// <summary>
    /// Fills the auto-third scale-root combo with all 12 pitch classes'
    /// (language-dependent) names, reusing the same convention as the
    /// manual-chord root combo (item index IS the pitch class), and selects
    /// whichever scale the native engine currently has active without
    /// triggering the SelectionChanged handler while doing so.
    /// </summary>
    private void PopulateAutoThirdScaleCombo()
    {
        int root = AudioEngineInterop.GetAutoThirdScaleRoot();
        AudioEngineInterop.ScaleType scaleType = AudioEngineInterop.GetAutoThirdScaleType();

        _isPopulatingAutoThirdScale = true;
        AutoThirdScaleRootComboBox.Items.Clear();
        for (int pitchClass = 0; pitchClass < 12; pitchClass++)
        {
            AutoThirdScaleRootComboBox.Items.Add(PitchClassName(pitchClass));
        }
        AutoThirdScaleRootComboBox.SelectedIndex = root >= 0 && root < 12 ? root : 0;
        AutoThirdScaleMajorRadio.IsChecked = scaleType == AudioEngineInterop.ScaleType.Major;
        AutoThirdScaleMinorRadio.IsChecked = scaleType == AudioEngineInterop.ScaleType.NaturalMinor;
        AutoThirdScaleHijazRadio.IsChecked = scaleType == AudioEngineInterop.ScaleType.Hijaz;
        AutoThirdScaleHijazKarRadio.IsChecked = scaleType == AudioEngineInterop.ScaleType.HijazKar;
        _isPopulatingAutoThirdScale = false;
    }

    private void AutoThirdScale_Changed(object sender, RoutedEventArgs e)
    {
        if (_isPopulatingAutoThirdScale || AutoThirdScaleRootComboBox.SelectedIndex < 0)
        {
            return;
        }

        AudioEngineInterop.ScaleType scaleType = CurrentAutoThirdScaleTypeSelection();
        AudioEngineInterop.SetAutoThirdScale(AutoThirdScaleRootComboBox.SelectedIndex, scaleType);
    }

    /// <summary>Reads which of the four scale-type radio buttons is currently checked.</summary>
    private AudioEngineInterop.ScaleType CurrentAutoThirdScaleTypeSelection()
    {
        if (AutoThirdScaleMinorRadio.IsChecked == true) return AudioEngineInterop.ScaleType.NaturalMinor;
        if (AutoThirdScaleHijazRadio.IsChecked == true) return AudioEngineInterop.ScaleType.Hijaz;
        if (AutoThirdScaleHijazKarRadio.IsChecked == true) return AudioEngineInterop.ScaleType.HijazKar;
        return AudioEngineInterop.ScaleType.Major;
    }

    // --- Piano roll (vizuelni prikaz sekvensera, dodatak spisku) ---

    private void PianoRollToggleCheckBox_Click(object sender, RoutedEventArgs e)
    {
        bool show = PianoRollToggleCheckBox.IsChecked == true;
        PianoRollPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            RedrawPianoRoll();
        }
    }

    /// <summary>
    /// Redraws the whole piano roll canvas from scratch - background row
    /// bands (one per semitone, shaded darker for black keys), octave
    /// boundary lines, vertical beat lines, and a rectangle per note
    /// currently in <see cref="_sequencerNotes"/>. Simple full-redraw rather
    /// than incremental updates, since the note list is short and this is a
    /// supplementary visual view, not the real-time engine.
    /// </summary>
    private void RedrawPianoRoll()
    {
        if (PianoRollCanvas == null || PianoRollPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        PianoRollCanvas.Children.Clear();

        int rowCount = PianoRollHighestPitch - PianoRollLowestPitch + 1;
        double canvasWidth = PianoRollCanvas.Width;
        double canvasHeight = rowCount * PianoRollCellHeight;

        for (int row = 0; row < rowCount; row++)
        {
            int pitch = PianoRollHighestPitch - row;
            var band = new Rectangle
            {
                Width = canvasWidth,
                Height = PianoRollCellHeight,
                Fill = IsBlackKey(pitch) ? Brushes.Gainsboro : Brushes.White,
            };
            Canvas.SetLeft(band, 0);
            Canvas.SetTop(band, row * PianoRollCellHeight);
            PianoRollCanvas.Children.Add(band);

            if (((pitch % 12) + 12) % 12 == 0) // C note - octave boundary, drawn a bit darker
            {
                var octaveLine = new Line
                {
                    X1 = 0,
                    X2 = canvasWidth,
                    Y1 = row * PianoRollCellHeight,
                    Y2 = row * PianoRollCellHeight,
                    Stroke = Brushes.DarkGray,
                    StrokeThickness = 1,
                };
                PianoRollCanvas.Children.Add(octaveLine);
            }
        }

        int beatCount = (int)(canvasWidth / PianoRollCellWidth);
        for (int beat = 0; beat <= beatCount; beat++)
        {
            bool isBarLine = beat % 4 == 0;
            var beatLine = new Line
            {
                X1 = beat * PianoRollCellWidth,
                X2 = beat * PianoRollCellWidth,
                Y1 = 0,
                Y2 = canvasHeight,
                Stroke = isBarLine ? Brushes.Gray : Brushes.LightGray,
                StrokeThickness = isBarLine ? 1 : 0.5,
            };
            PianoRollCanvas.Children.Add(beatLine);
        }

        foreach (SequencerNoteRow note in _sequencerNotes)
        {
            if (note.Pitch < PianoRollLowestPitch || note.Pitch > PianoRollHighestPitch)
            {
                continue; // outside the visible range - still fully editable via the list above
            }

            int row = PianoRollHighestPitch - note.Pitch;
            var rect = new Rectangle
            {
                Width = Math.Max(4.0, (note.LengthBeats * PianoRollCellWidth) - 2),
                Height = PianoRollCellHeight - 2,
                Fill = ReferenceEquals(note, _pianoRollSelectedNote) ? Brushes.OrangeRed : Brushes.SteelBlue,
                Stroke = Brushes.Black,
                StrokeThickness = 0.5,
                Tag = note,
            };
            Canvas.SetLeft(rect, (note.StartBeat * PianoRollCellWidth) + 1);
            Canvas.SetTop(rect, (row * PianoRollCellHeight) + 1);
            PianoRollCanvas.Children.Add(rect);
        }
    }

    private static bool IsBlackKey(int pitch)
    {
        int semitone = ((pitch % 12) + 12) % 12;
        return semitone == 1 || semitone == 3 || semitone == 6 || semitone == 8 || semitone == 10;
    }

    private void PianoRollCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Rectangle clickedRect && clickedRect.Tag is SequencerNoteRow existingNote)
        {
            // Clicking an existing note selects it (highlighted) rather than
            // adding a new one on top of it.
            _pianoRollSelectedNote = existingNote;
            RedrawPianoRoll();
            return;
        }

        Point p = e.GetPosition(PianoRollCanvas);
        int beat = (int)(p.X / PianoRollCellWidth);
        int row = (int)(p.Y / PianoRollCellHeight);
        int pitch = PianoRollHighestPitch - row;
        if (pitch < 0 || pitch > 127 || beat < 0)
        {
            return;
        }

        if (!double.TryParse(NoteLengthTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double lengthBeats) || lengthBeats <= 0)
        {
            lengthBeats = 1.0;
        }
        if (!int.TryParse(NoteVelocityTextBox.Text, out int velocity) || velocity < 1 || velocity > 127)
        {
            velocity = 100;
        }

        _sequencerNotes.Add(new SequencerNoteRow(pitch, beat, lengthBeats, velocity));
        PushNotesToEngine();
        SequencerStatusText.Text = Localization.F("Seq.StatusAddedNote", _sequencerNotes.Count);
    }

    private void PianoRollCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Rectangle clickedRect && clickedRect.Tag is SequencerNoteRow existingNote)
        {
            _sequencerNotes.Remove(existingNote);
            if (ReferenceEquals(_pianoRollSelectedNote, existingNote))
            {
                _pianoRollSelectedNote = null;
            }
            PushNotesToEngine();
            SequencerStatusText.Text = Localization.F("Seq.StatusRemoved", _sequencerNotes.Count);
            e.Handled = true;
        }
    }

    private void PerformanceKeyboard_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        StatusText.Text = Localization.T("Status.PlayModeActive");
    }

    private void PerformanceKeyboard_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Release anything still held so a note can't get stuck on when focus moves away.
        foreach (Key key in _heldKeys)
        {
            if (_mapper.TryGetMidiNote(key, out int note))
            {
                AudioEngineInterop.NoteOff(note);
            }
        }
        _heldKeys.Clear();

        StatusText.Text = _engineRunning ? Localization.T("Status.EngineRunning") : Localization.T("Status.EngineNotRunning");
    }

    private void PerformanceKeyboard_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_engineRunning)
        {
            return;
        }

        if (e.Key == Key.PageUp)
        {
            _mapper.ShiftOctave(1);
            UpdateOctaveText();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.PageDown)
        {
            _mapper.ShiftOctave(-1);
            UpdateOctaveText();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Up || e.Key == Key.Down)
        {
            CycleSoundFontPreset(e.Key == Key.Up ? 1 : -1);
            e.Handled = true;
            return;
        }

        if (_heldKeys.Contains(e.Key))
        {
            // Ignore OS key-repeat while a key is held down.
            e.Handled = true;
            return;
        }

        if (_mapper.TryGetMidiNote(e.Key, out int note))
        {
            _heldKeys.Add(e.Key);
            AudioEngineInterop.NoteOn(note, 0.9f);
            e.Handled = true;
        }
    }

    private void PerformanceKeyboard_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_engineRunning)
        {
            return;
        }

        if (_heldKeys.Remove(e.Key) && _mapper.TryGetMidiNote(e.Key, out int note))
        {
            AudioEngineInterop.NoteOff(note);
            e.Handled = true;
        }
    }

    private void UpdateOctaveText()
    {
        OctaveText.Text = Localization.F("Keyboard.Octave", _mapper.BaseOctave);
    }

    /// <summary>
    /// Switches to the next/previous instrument in the loaded SoundFont
    /// bank (wrapping around at both ends), without leaving the performance
    /// keyboard region - so switching sounds while playing doesn't require
    /// tabbing away to the "Instrument" dropdown. Keeps that dropdown and
    /// this shortcut in sync with each other either way.
    /// </summary>
    private void CycleSoundFontPreset(int delta)
    {
        if (!AudioEngineInterop.IsSoundFontBankLoaded())
        {
            SoundFontStatusText.Text = Localization.T("Sf.StatusNoneLoadedForCycle");
            return;
        }

        int presetCount = AudioEngineInterop.GetSoundFontPresetCount();
        if (presetCount <= 0)
        {
            return;
        }

        int current = AudioEngineInterop.GetSelectedSoundFontPresetIndex();
        int next = ((current + delta) % presetCount + presetCount) % presetCount;
        if (!AudioEngineInterop.SelectSoundFontPreset(next))
        {
            return;
        }

        _isPopulatingSoundFontPresets = true;
        SoundFontPresetComboBox.SelectedIndex = next;
        _isPopulatingSoundFontPresets = false;

        string instrumentName = AudioEngineInterop.GetSoundFontPresetName(next) ?? Localization.F("Sf.DefaultInstrumentName", next);
        SoundFontStatusText.Text = Localization.F("Sf.StatusPresetChanged", instrumentName);
    }

    // --- Aranžer ---

    private void SavePatternButton_Click(object sender, RoutedEventArgs e)
    {
        string name = ArrangerPatternNameTextBox.Text?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            ArrangerStatusText.Text = Localization.T("Arr.StatusNeedName");
            return;
        }

        if (_sequencerNotes.Count == 0)
        {
            ArrangerStatusText.Text = Localization.T("Arr.StatusSequencerEmpty");
            return;
        }

        if (!double.TryParse(LoopLengthTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double loopLength) || loopLength <= 0)
        {
            ArrangerStatusText.Text = Localization.T("Arr.StatusInvalidLoopLength");
            return;
        }

        AudioEngineInterop.ArrangerAddPattern(name, loopLength, BuildSequencedNotesArray(_sequencerNotes));
        ArrangerPatternNameTextBox.Text = string.Empty;
        RefreshArrangerPatternsList();
        ArrangerStatusText.Text = Localization.F("Arr.StatusSaved", name, _sequencerNotes.Count, loopLength);
    }

    private void AppendPatternToOrderButton_Click(object sender, RoutedEventArgs e)
    {
        int index = ArrangerPatternsListBox.SelectedIndex;
        if (index < 0)
        {
            ArrangerStatusText.Text = Localization.T("Arr.StatusNeedPatternSelection");
            return;
        }

        AudioEngineInterop.ArrangerAppendToOrder(index);
        RefreshArrangerOrderList();
        ArrangerStatusText.Text = Localization.T("Arr.StatusAppended");
    }

    private void RemovePatternButton_Click(object sender, RoutedEventArgs e)
    {
        int index = ArrangerPatternsListBox.SelectedIndex;
        if (index < 0)
        {
            ArrangerStatusText.Text = Localization.T("Arr.StatusNeedRemoveSelection");
            return;
        }

        AudioEngineInterop.ArrangerRemovePattern(index);
        RefreshArrangerPatternsList();
        RefreshArrangerOrderList();
        ArrangerStatusText.Text = Localization.T("Arr.StatusPatternRemoved");
    }

    private void RemoveLastFromOrderButton_Click(object sender, RoutedEventArgs e)
    {
        AudioEngineInterop.ArrangerRemoveLastFromOrder();
        RefreshArrangerOrderList();
        ArrangerStatusText.Text = Localization.T("Arr.StatusLastRemoved");
    }

    private void ClearOrderButton_Click(object sender, RoutedEventArgs e)
    {
        AudioEngineInterop.ArrangerClearOrder();
        RefreshArrangerOrderList();
        ArrangerStatusText.Text = Localization.T("Arr.StatusOrderCleared");
    }

    private void RefreshArrangerPatternsList()
    {
        ArrangerPatternsListBox.Items.Clear();
        int count = AudioEngineInterop.ArrangerGetPatternCount();
        for (int i = 0; i < count; i++)
        {
            string name = AudioEngineInterop.ArrangerGetPatternName(i) ?? Localization.F("Arr.DefaultPatternName", i);
            double length = AudioEngineInterop.ArrangerGetPatternLengthBeats(i);
            ArrangerPatternsListBox.Items.Add(Localization.F("Arr.PatternListFormat", name, length));
        }
    }

    private void RefreshArrangerOrderList()
    {
        ArrangerOrderListBox.Items.Clear();
        int count = AudioEngineInterop.ArrangerGetOrderCount();
        for (int i = 0; i < count; i++)
        {
            int patternIndex = AudioEngineInterop.ArrangerGetOrderPatternIndexAt(i);
            string name = patternIndex >= 0
                ? (AudioEngineInterop.ArrangerGetPatternName(patternIndex) ?? Localization.F("Arr.DefaultPatternName", patternIndex))
                : Localization.T("Arr.UnknownPattern");
            ArrangerOrderListBox.Items.Add(Localization.F("Arr.OrderListFormat", i + 1, name));
        }
    }

    private void PlayArrangerButton_Click(object sender, RoutedEventArgs e)
    {
        if (AudioEngineInterop.ArrangerGetOrderCount() == 0)
        {
            ArrangerStatusText.Text = Localization.T("Arr.StatusOrderEmpty");
            return;
        }

        if (!double.TryParse(ArrangerTempoTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double tempo) || tempo <= 0)
        {
            ArrangerStatusText.Text = Localization.T("Arr.StatusInvalidTempo");
            return;
        }

        AudioEngineInterop.ArrangerSetTempoBpm(tempo);
        AudioEngineInterop.PlayArranger();
        PlayArrangerButton.IsEnabled = false;
        StopArrangerButton.IsEnabled = true;
        _arrangerStatusTimer.Start();

        // The native engine stops the plain sequencer and the multi-track
        // sequencer whenever the arranger starts - mirror that here so their
        // views' buttons stay honest.
        PlaySequencerButton.IsEnabled = true;
        StopSequencerButton.IsEnabled = false;
        StopMultiTrackUiOnly();
    }

    private void StopArrangerButton_Click(object sender, RoutedEventArgs e)
    {
        AudioEngineInterop.StopArranger();
        StopArrangerUiOnly();
        ArrangerStatusText.Text = Localization.T("Arr.StatusStopped");
    }

    /// <summary>
    /// Updates just the Aranžer view's own controls to reflect "stopped",
    /// without calling AudioEngineInterop.StopArranger() again - used when
    /// something else (starting the plain sequencer) already stopped it
    /// natively, so only the UI needs to catch up.
    /// </summary>
    private void StopArrangerUiOnly()
    {
        PlayArrangerButton.IsEnabled = true;
        StopArrangerButton.IsEnabled = false;
        _arrangerStatusTimer.Stop();
    }

    private void ArrangerStatusTimer_Tick(object? sender, EventArgs e)
    {
        if (!AudioEngineInterop.IsArrangerPlaying())
        {
            StopArrangerUiOnly();
            ArrangerStatusText.Text = Localization.T("Arr.StatusStopped");
            return;
        }

        int position = AudioEngineInterop.ArrangerGetCurrentOrderPosition();
        if (position < 0)
        {
            return;
        }

        int patternIndex = AudioEngineInterop.ArrangerGetOrderPatternIndexAt(position);
        string name = patternIndex >= 0
            ? (AudioEngineInterop.ArrangerGetPatternName(patternIndex) ?? Localization.F("Arr.DefaultPatternName", patternIndex))
            : Localization.T("Arr.UnknownPattern");
        ArrangerStatusText.Text = Localization.F("Arr.StatusPlayingFormat", position + 1, AudioEngineInterop.ArrangerGetOrderCount(), name);
    }

    // --- Multi-trake i mikser ---

    private void AddTrackButton_Click(object sender, RoutedEventArgs e)
    {
        string name = NewTrackNameTextBox.Text?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            name = Localization.F("Mix.DefaultTrackName", _trackNotes.Count + 1);
        }

        int index = AudioEngineInterop.MultiTrackAddTrack(name);
        _trackNotes.Add(new ObservableCollection<SequencerNoteRow>());
        _trackSoundFontPaths.Add(null);
        _trackVstPaths.Add(null);
        NewTrackNameTextBox.Text = string.Empty;
        RefreshTrackList();
        MixTracksListBox.SelectedIndex = index;
        MixStatusText.Text = Localization.F("Mix.StatusTrackAdded", name);
    }

    private void RemoveTrackButton_Click(object sender, RoutedEventArgs e)
    {
        int index = MixTracksListBox.SelectedIndex;
        if (index < 0)
        {
            MixStatusText.Text = Localization.T("Mix.StatusNeedTrackSelection");
            return;
        }

        // A removed/shifted track index would otherwise leave an armed
        // "Slušaj akord" silently inserting into the wrong (or a now
        // out-of-range) track - simplest and safest is to just disarm it
        // whenever any track is removed, same as if "Prestani da slušaš"
        // had been clicked.
        if (_chordCaptureTarget == ChordCaptureTarget.MultiTrack)
        {
            DisarmChordCapture();
            TrackListenForChordButton.IsEnabled = true;
            TrackStopListenForChordButton.IsEnabled = false;
        }

        string name = AudioEngineInterop.MultiTrackGetTrackName(index) ?? Localization.F("Mix.DefaultTrackName", index + 1);
        AudioEngineInterop.MultiTrackRemoveTrack(index);
        _trackNotes.RemoveAt(index);
        _trackSoundFontPaths.RemoveAt(index);
        _trackVstPaths.RemoveAt(index);
        _selectedTrackIndex = -1;
        MixTrackDetailPanel.IsEnabled = false;
        RefreshTrackList();

        // RefreshTrackList() restores the list's own selection (by position)
        // internally while its "populating" guard is still up, so it never
        // fires MixTracksListBox_SelectionChanged - sync the detail panel to
        // match whatever ended up selected (a track that shifted into the
        // same slot, or nothing) explicitly, here.
        _selectedTrackIndex = MixTracksListBox.SelectedIndex;
        if (_selectedTrackIndex >= 0)
        {
            LoadSelectedTrackIntoDetailPanel();
            MixTrackDetailPanel.IsEnabled = true;
        }

        MixStatusText.Text = Localization.F("Mix.StatusTrackRemoved", name);
    }

    /// <summary>
    /// Rebuilds the track list from the native engine, preserving the
    /// current selection (by position) where it's still valid. Called after
    /// any change that affects what the list should show - adding/removing
    /// a track, renaming, changing volume/pan/mute/solo/instrument.
    /// </summary>
    private void RefreshTrackList()
    {
        _isPopulatingTrackList = true;
        int previousSelection = MixTracksListBox.SelectedIndex;
        MixTracksListBox.Items.Clear();

        int count = AudioEngineInterop.MultiTrackGetTrackCount();
        for (int i = 0; i < count; i++)
        {
            MixTracksListBox.Items.Add(BuildTrackListEntryText(i));
        }

        if (previousSelection >= 0 && previousSelection < count)
        {
            MixTracksListBox.SelectedIndex = previousSelection;
        }
        _isPopulatingTrackList = false;
    }

    private string BuildTrackListEntryText(int index)
    {
        string name = AudioEngineInterop.MultiTrackGetTrackName(index) ?? Localization.F("Mix.DefaultTrackName", index + 1);
        var mode = AudioEngineInterop.MultiTrackGetTrackInstrumentMode(index);
        string instrumentText = mode == AudioEngineInterop.TrackInstrumentMode.SoundFont
            ? (AudioEngineInterop.MultiTrackGetTrackSoundFontBankName(index) ?? Localization.T("Mix.SoundFontRadio"))
            : Localization.T("Mix.SynthRadio");
        float volume = AudioEngineInterop.MultiTrackGetTrackVolume(index);
        float pan = AudioEngineInterop.MultiTrackGetTrackPan(index);
        bool mute = AudioEngineInterop.MultiTrackIsTrackMuted(index);
        bool solo = AudioEngineInterop.MultiTrackIsTrackSoloed(index);
        string flags = string.Concat(
            mute ? Localization.T("Mix.MuteFlag") : string.Empty,
            solo ? Localization.T("Mix.SoloFlag") : string.Empty);

        return Localization.F("Mix.TrackListFormat", index + 1, name, instrumentText, volume, pan, flags);
    }

    private void MixTracksListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isPopulatingTrackList)
        {
            return;
        }

        _selectedTrackIndex = MixTracksListBox.SelectedIndex;
        if (_selectedTrackIndex < 0)
        {
            MixTrackDetailPanel.IsEnabled = false;
            return;
        }

        LoadSelectedTrackIntoDetailPanel();
        MixTrackDetailPanel.IsEnabled = true;
    }

    /// <summary>
    /// Fills every control in the "Izabrana traka" detail panel from the
    /// native engine's current state for <see cref="_selectedTrackIndex"/>,
    /// and rebinds the note list to that track's own note collection.
    /// </summary>
    private void LoadSelectedTrackIntoDetailPanel()
    {
        int index = _selectedTrackIndex;
        if (index < 0 || index >= _trackNotes.Count)
        {
            return;
        }

        TrackNameTextBox.Text = AudioEngineInterop.MultiTrackGetTrackName(index) ?? string.Empty;

        var mode = AudioEngineInterop.MultiTrackGetTrackInstrumentMode(index);
        TrackSynthRadio.IsChecked = mode == AudioEngineInterop.TrackInstrumentMode.BuiltInSynth;
        TrackSoundFontRadio.IsChecked = mode == AudioEngineInterop.TrackInstrumentMode.SoundFont;
        TrackVstRadio.IsChecked = mode == AudioEngineInterop.TrackInstrumentMode.Vst;

        TrackVstPathTextBox.Text = AudioEngineInterop.MultiTrackIsTrackVstInstrumentLoaded(index)
            ? (AudioEngineInterop.MultiTrackGetTrackVstInstrumentName(index) ?? string.Empty)
            : string.Empty;

        TrackVolumeTextBox.Text = AudioEngineInterop.MultiTrackGetTrackVolume(index).ToString("0.00", CultureInfo.InvariantCulture);
        TrackPanTextBox.Text = AudioEngineInterop.MultiTrackGetTrackPan(index).ToString("0.00", CultureInfo.InvariantCulture);
        TrackMuteCheckBox.IsChecked = AudioEngineInterop.MultiTrackIsTrackMuted(index);
        TrackSoloCheckBox.IsChecked = AudioEngineInterop.MultiTrackIsTrackSoloed(index);

        _isPopulatingTrackSoundFontPresets = true;
        TrackSoundFontPresetComboBox.Items.Clear();
        bool bankLoaded = AudioEngineInterop.MultiTrackIsTrackSoundFontBankLoaded(index);
        TrackSoundFontPathTextBox.Text = bankLoaded
            ? (AudioEngineInterop.MultiTrackGetTrackSoundFontBankName(index) ?? string.Empty)
            : string.Empty;
        if (bankLoaded)
        {
            int presetCount = AudioEngineInterop.MultiTrackGetTrackSoundFontPresetCount(index);
            int selectedPreset = AudioEngineInterop.MultiTrackGetSelectedTrackSoundFontPresetIndex(index);
            for (int i = 0; i < presetCount; i++)
            {
                TrackSoundFontPresetComboBox.Items.Add(
                    AudioEngineInterop.MultiTrackGetTrackSoundFontPresetName(index, i) ?? Localization.F("Sf.DefaultInstrumentName", i));
            }
            TrackSoundFontPresetComboBox.SelectedIndex = selectedPreset >= 0 && selectedPreset < presetCount ? selectedPreset : -1;
            TrackSoundFontPresetComboBox.IsEnabled = true;
        }
        else
        {
            TrackSoundFontPresetComboBox.IsEnabled = false;
        }
        _isPopulatingTrackSoundFontPresets = false;

        TrackNotesListView.ItemsSource = _trackNotes[index];
    }

    private void RenameTrackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTrackIndex < 0)
        {
            MixStatusText.Text = Localization.T("Mix.StatusNeedTrackSelection");
            return;
        }

        string name = TrackNameTextBox.Text?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            MixStatusText.Text = Localization.T("Mix.StatusNeedTrackName");
            return;
        }

        AudioEngineInterop.MultiTrackSetTrackName(_selectedTrackIndex, name);
        RefreshTrackList();
        MixStatusText.Text = Localization.F("Mix.StatusRenamed", name);
    }

    private void TrackInstrumentMode_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTrackIndex < 0)
        {
            return;
        }

        AudioEngineInterop.TrackInstrumentMode mode;
        string statusKey;
        if (TrackSoundFontRadio.IsChecked == true)
        {
            mode = AudioEngineInterop.TrackInstrumentMode.SoundFont;
            statusKey = "Mix.StatusModeSoundFont";
        }
        else if (TrackVstRadio.IsChecked == true)
        {
            mode = AudioEngineInterop.TrackInstrumentMode.Vst;
            statusKey = "Mix.StatusModeVst";
        }
        else
        {
            mode = AudioEngineInterop.TrackInstrumentMode.BuiltInSynth;
            statusKey = "Mix.StatusModeSynth";
        }
        AudioEngineInterop.MultiTrackSetTrackInstrumentMode(_selectedTrackIndex, mode);
        RefreshTrackList();
        MixStatusText.Text = Localization.T(statusKey);
    }

    private void BrowseTrackSoundFontButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Localization.T("Sf.BrowseDialogTitle"),
            Filter = $"SoundFont (*.sf2)|*.sf2|{Localization.T("Common.AllFiles")}|*.*",
        };

        if (dialog.ShowDialog(this) == true)
        {
            TrackSoundFontPathTextBox.Text = dialog.FileName;
        }
    }

    private void LoadTrackSoundFontButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTrackIndex < 0)
        {
            MixStatusText.Text = Localization.T("Mix.StatusNeedTrackSelection");
            return;
        }

        string path = TrackSoundFontPathTextBox.Text?.Trim() ?? string.Empty;
        if (path.Length == 0)
        {
            MixStatusText.Text = Localization.T("Sf.StatusEmptyPath");
            return;
        }

        string? error = AudioEngineInterop.MultiTrackLoadTrackSoundFontBank(_selectedTrackIndex, path);
        if (error != null)
        {
            MixStatusText.Text = Localization.F("Sf.StatusLoadFailed", error);
            return;
        }

        _trackSoundFontPaths[_selectedTrackIndex] = path;

        // Loading a bank auto-switches the native track to SoundFont mode
        // (see MultiTrackSequencer::LoadTrackSoundFontBank) - reflect that
        // in the radio buttons too.
        TrackSoundFontRadio.IsChecked = true;
        LoadSelectedTrackIntoDetailPanel();
        RefreshTrackList();
        MixStatusText.Text = Localization.F("Mix.StatusBankLoaded",
            AudioEngineInterop.MultiTrackGetTrackSoundFontBankName(_selectedTrackIndex) ?? Localization.T("Sf.DefaultBankName"));
    }

    private void BrowseTrackVstButton_Click(object sender, RoutedEventArgs e)
    {
        // CheckFileExists off for the same reason as the other VST3 browse
        // dialogs in this app - a VST3 is often a bundle folder, not a
        // single file.
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Localization.T("Vst.BrowseDialogTitle"),
            Filter = $"VST3 (*.vst3)|*.vst3|{Localization.T("Common.AllFiles")}|*.*",
            CheckFileExists = false,
            CheckPathExists = true,
        };

        if (dialog.ShowDialog(this) == true)
        {
            TrackVstPathTextBox.Text = dialog.FileName;
        }
    }

    private void LoadTrackVstButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTrackIndex < 0)
        {
            MixStatusText.Text = Localization.T("Mix.StatusNeedTrackSelection");
            return;
        }

        string path = TrackVstPathTextBox.Text?.Trim() ?? string.Empty;
        if (path.Length == 0)
        {
            MixStatusText.Text = Localization.T("Vst.StatusEmptyPath");
            return;
        }

        string? error = AudioEngineInterop.MultiTrackLoadTrackVstInstrument(_selectedTrackIndex, path);
        if (error != null)
        {
            MixStatusText.Text = Localization.F("Vst.StatusLoadFailed", error);
            return;
        }

        _trackVstPaths[_selectedTrackIndex] = path;

        // Loading a plug-in auto-switches the native track to Vst mode (see
        // MultiTrackSequencer::LoadTrackVstInstrument) - reflect that in the
        // radio buttons too.
        TrackVstRadio.IsChecked = true;
        LoadSelectedTrackIntoDetailPanel();
        RefreshTrackList();
        MixStatusText.Text = Localization.F("Mix.StatusVstLoaded",
            AudioEngineInterop.MultiTrackGetTrackVstInstrumentName(_selectedTrackIndex) ?? path);
    }

    private void TrackSoundFontPresetComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isPopulatingTrackSoundFontPresets || _selectedTrackIndex < 0)
        {
            return;
        }

        int index = TrackSoundFontPresetComboBox.SelectedIndex;
        if (index < 0)
        {
            return;
        }

        if (AudioEngineInterop.MultiTrackSelectTrackSoundFontPreset(_selectedTrackIndex, index))
        {
            MixStatusText.Text = Localization.F("Sf.StatusPresetChanged", TrackSoundFontPresetComboBox.SelectedItem);
        }
    }

    private void ApplyVolumePanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTrackIndex < 0)
        {
            MixStatusText.Text = Localization.T("Mix.StatusNeedTrackSelection");
            return;
        }

        if (!float.TryParse(TrackVolumeTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float volume)
            || volume < 0f || volume > 1.5f)
        {
            MixStatusText.Text = Localization.T("Mix.StatusInvalidVolume");
            return;
        }

        if (!float.TryParse(TrackPanTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float pan)
            || pan < -1f || pan > 1f)
        {
            MixStatusText.Text = Localization.T("Mix.StatusInvalidPan");
            return;
        }

        AudioEngineInterop.MultiTrackSetTrackVolume(_selectedTrackIndex, volume);
        AudioEngineInterop.MultiTrackSetTrackPan(_selectedTrackIndex, pan);
        RefreshTrackList();
        MixStatusText.Text = Localization.T("Mix.StatusVolumePanApplied");
    }

    private void TrackMuteCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTrackIndex < 0)
        {
            return;
        }
        AudioEngineInterop.MultiTrackSetTrackMute(_selectedTrackIndex, TrackMuteCheckBox.IsChecked == true);
        RefreshTrackList();
    }

    private void TrackSoloCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTrackIndex < 0)
        {
            return;
        }
        AudioEngineInterop.MultiTrackSetTrackSolo(_selectedTrackIndex, TrackSoloCheckBox.IsChecked == true);
        RefreshTrackList();
    }

    private void AddTrackNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTrackIndex < 0)
        {
            MixStatusText.Text = Localization.T("Mix.StatusNeedTrackSelection");
            return;
        }

        string[] tokens = TrackNotePitchTextBox.Text.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            MixStatusText.Text = Localization.T("Seq.StatusNeedNote");
            return;
        }

        var pitches = new List<int>(tokens.Length);
        foreach (string token in tokens)
        {
            if (!SequencerNoteRow.TryParsePitch(token, out int parsedPitch))
            {
                MixStatusText.Text = Localization.F("Seq.StatusInvalidNote", token);
                return;
            }
            pitches.Add(parsedPitch);
        }

        if (!double.TryParse(TrackNoteStartTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double startBeat) || startBeat < 0)
        {
            MixStatusText.Text = Localization.T("Seq.StatusInvalidStart");
            return;
        }

        if (!double.TryParse(TrackNoteLengthTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double lengthBeats) || lengthBeats <= 0)
        {
            MixStatusText.Text = Localization.T("Seq.StatusInvalidLength");
            return;
        }

        if (!int.TryParse(TrackNoteVelocityTextBox.Text, out int velocity) || velocity < 1 || velocity > 127)
        {
            MixStatusText.Text = Localization.T("Seq.StatusInvalidVelocity");
            return;
        }

        ObservableCollection<SequencerNoteRow> notes = _trackNotes[_selectedTrackIndex];
        foreach (int pitch in pitches)
        {
            notes.Add(new SequencerNoteRow(pitch, startBeat, lengthBeats, velocity));
        }

        PushTrackNotesToEngine(_selectedTrackIndex);
        MixStatusText.Text = pitches.Count == 1
            ? Localization.F("Seq.StatusAddedNote", notes.Count)
            : Localization.F("Seq.StatusAddedChord", pitches.Count, notes.Count);
    }

    private void TrackListenForChordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTrackIndex < 0)
        {
            MixStatusText.Text = Localization.T("Mix.StatusNeedTrackSelection");
            return;
        }

        ArmChordCapture(ChordCaptureTarget.MultiTrack, _selectedTrackIndex);
        TrackListenForChordButton.IsEnabled = false;
        TrackStopListenForChordButton.IsEnabled = true;
        MixStatusText.Text = Localization.T("Seq.StatusChordListening");
    }

    private void TrackStopListenForChordButton_Click(object sender, RoutedEventArgs e)
    {
        DisarmChordCapture();
        TrackListenForChordButton.IsEnabled = true;
        TrackStopListenForChordButton.IsEnabled = false;
        MixStatusText.Text = Localization.T("Seq.StatusChordListenStopped");
    }

    private void RemoveTrackNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTrackIndex < 0)
        {
            MixStatusText.Text = Localization.T("Mix.StatusNeedTrackSelection");
            return;
        }

        if (TrackNotesListView.SelectedItem is SequencerNoteRow row)
        {
            ObservableCollection<SequencerNoteRow> notes = _trackNotes[_selectedTrackIndex];
            notes.Remove(row);
            PushTrackNotesToEngine(_selectedTrackIndex);
            MixStatusText.Text = Localization.F("Seq.StatusRemoved", notes.Count);
        }
        else
        {
            MixStatusText.Text = Localization.T("Seq.StatusRemoveNeedSelection");
        }
    }

    private void ClearTrackNotesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTrackIndex < 0)
        {
            MixStatusText.Text = Localization.T("Mix.StatusNeedTrackSelection");
            return;
        }

        _trackNotes[_selectedTrackIndex].Clear();
        PushTrackNotesToEngine(_selectedTrackIndex);
        MixStatusText.Text = Localization.T("Seq.StatusCleared");
    }

    private void PlayMultiTrackButton_Click(object sender, RoutedEventArgs e)
    {
        if (AudioEngineInterop.MultiTrackGetTrackCount() == 0)
        {
            MixStatusText.Text = Localization.T("Mix.StatusEmpty");
            return;
        }

        if (!double.TryParse(MixTempoTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double tempo) || tempo <= 0)
        {
            MixStatusText.Text = Localization.T("Mix.StatusInvalidTempo");
            return;
        }

        if (!double.TryParse(MixLoopLengthTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double loopLength) || loopLength <= 0)
        {
            MixStatusText.Text = Localization.T("Mix.StatusInvalidLoopLength");
            return;
        }

        AudioEngineInterop.MultiTrackSetTempoBpm(tempo);
        AudioEngineInterop.MultiTrackSetLoopLengthBeats(loopLength);
        AudioEngineInterop.PlayMultiTrack();
        PlayMultiTrackButton.IsEnabled = false;
        StopMultiTrackButton.IsEnabled = true;
        MixStatusText.Text = Localization.T("Mix.StatusPlaying");

        // The native engine stops the plain sequencer and the arranger
        // whenever multi-track playback starts (all three share one
        // transport) - mirror that here so their views' buttons don't lie
        // about what's actually playing.
        PlaySequencerButton.IsEnabled = true;
        StopSequencerButton.IsEnabled = false;
        StopArrangerUiOnly();
    }

    private void StopMultiTrackButton_Click(object sender, RoutedEventArgs e)
    {
        AudioEngineInterop.StopMultiTrack();
        StopMultiTrackUiOnly();
        MixStatusText.Text = Localization.T("Mix.StatusStopped");
    }

    /// <summary>
    /// Updates just the multi-track view's own Play/Stop buttons to reflect
    /// "stopped", without calling AudioEngineInterop.StopMultiTrack() again -
    /// used when something else (starting the plain sequencer or the
    /// arranger) already stopped it natively, so only the UI needs to catch up.
    /// </summary>
    private void StopMultiTrackUiOnly()
    {
        PlayMultiTrackButton.IsEnabled = true;
        StopMultiTrackButton.IsEnabled = false;
    }

    private void ExportWavButton_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(ExportDurationTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double duration) || duration <= 0)
        {
            MixStatusText.Text = Localization.T("Mix.StatusExportInvalidDuration");
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Localization.T("Mix.ExportDialogTitle"),
            Filter = "WAV (*.wav)|*.wav",
            DefaultExt = ".wav",
            FileName = "ultra-composer-export.wav",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        string? error = AudioEngineInterop.MultiTrackRenderToWav(dialog.FileName, duration);
        MixStatusText.Text = error == null
            ? Localization.F("Mix.ExportStatusSuccess", dialog.FileName)
            : Localization.F("Mix.ExportStatusFailed", error);
    }

    // --- Auto-pratnja (ritam, bas, kontra, harmonija) ---

    /// <summary>
    /// Fills the style combo from the native built-in rhythm bank, keeping
    /// whichever style is currently selected in the engine selected here too
    /// (rather than resetting to the first one) - matters when this is
    /// called again after a language switch, since style *names* ("Pop",
    /// "Rok", ...) aren't actually translated (they're proper names), but
    /// the call is still cheap and harmless to repeat.
    /// </summary>
    private void PopulateAccompanimentStyles()
    {
        int previousIndex = AudioEngineInterop.AccompanimentGetSelectedStyleIndex();

        AccStyleComboBox.SelectionChanged -= AccStyleComboBox_SelectionChanged;
        AccStyleComboBox.Items.Clear();
        int count = AudioEngineInterop.AccompanimentGetStyleCount();
        for (int i = 0; i < count; i++)
        {
            AccStyleComboBox.Items.Add(AudioEngineInterop.AccompanimentGetStyleName(i) ?? Localization.F("Acc.DefaultStyleName", i));
        }
        AccStyleComboBox.SelectedIndex = previousIndex >= 0 && previousIndex < count ? previousIndex : (count > 0 ? 0 : -1);
        AccStyleComboBox.SelectionChanged += AccStyleComboBox_SelectionChanged;

        if (AccStyleComboBox.SelectedIndex >= 0)
        {
            AccTempoTextBox.Text = AudioEngineInterop.AccompanimentGetTempoBpm().ToString("0.##", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Fills the manual-chord root/quality combos with names in the current
    /// language. SelectedIndex maps directly to the value each combo
    /// represents (0-11 for the root's pitch class, 0-3 for the
    /// AudioEngineInterop.ChordQuality ordinal) - no separate lookup table
    /// needed.
    /// </summary>
    private void PopulateAccompanimentManualChordCombos()
    {
        int previousRoot = AccManualRootComboBox.SelectedIndex;
        AccManualRootComboBox.Items.Clear();
        for (int pitchClass = 0; pitchClass < 12; pitchClass++)
        {
            AccManualRootComboBox.Items.Add(PitchClassName(pitchClass));
        }
        AccManualRootComboBox.SelectedIndex = previousRoot >= 0 && previousRoot < 12 ? previousRoot : 0;

        int previousQuality = AccManualQualityComboBox.SelectedIndex;
        AccManualQualityComboBox.Items.Clear();
        AccManualQualityComboBox.Items.Add(ChordQualityName(AudioEngineInterop.ChordQuality.Major));
        AccManualQualityComboBox.Items.Add(ChordQualityName(AudioEngineInterop.ChordQuality.Minor));
        AccManualQualityComboBox.Items.Add(ChordQualityName(AudioEngineInterop.ChordQuality.Dominant7));
        AccManualQualityComboBox.Items.Add(ChordQualityName(AudioEngineInterop.ChordQuality.Diminished));
        AccManualQualityComboBox.SelectedIndex = previousQuality >= 0 && previousQuality < 4 ? previousQuality : 0;
    }

    /// <summary>General/solfege name in Serbian, letter+accidental name in English - same convention as SequencerNoteRow's note names, just for a bare pitch class (no octave).</summary>
    private static string PitchClassName(int pitchClass)
    {
        int normalized = ((pitchClass % 12) + 12) % 12;
        if (Localization.Current == AppLanguage.English)
        {
            string[] englishNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
            return englishNames[normalized];
        }
        string[] names = { "C", "Cis", "D", "Dis", "E", "F", "Fis", "G", "Gis", "A", "Ais", "H" };
        return names[normalized];
    }

    private static string ChordQualityName(AudioEngineInterop.ChordQuality quality) => quality switch
    {
        AudioEngineInterop.ChordQuality.Minor => Localization.T("Acc.QualityMinor"),
        AudioEngineInterop.ChordQuality.Dominant7 => Localization.T("Acc.QualityDominant7"),
        AudioEngineInterop.ChordQuality.Diminished => Localization.T("Acc.QualityDiminished"),
        _ => Localization.T("Acc.QualityMajor"),
    };

    /// <summary>
    /// Refreshes the live "current chord" display from whatever the native
    /// engine currently has active - polled on a timer (see
    /// _accompanimentStatusTimer) since chord changes triggered by playing
    /// notes in the keyboard-split zone happen natively, with no event to
    /// push that change to the UI.
    /// </summary>
    private void RefreshAccompanimentCurrentChordText()
    {
        if (!_engineRunning)
        {
            return;
        }
        int root = AudioEngineInterop.AccompanimentGetCurrentChordRootPitchClass();
        AudioEngineInterop.ChordQuality quality = AudioEngineInterop.AccompanimentGetCurrentChordQuality();
        AccCurrentChordText.Text = Localization.F("Acc.CurrentChordFormat", PitchClassName(root), ChordQualityName(quality));
    }

    private void AccStyleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AccStyleComboBox.SelectedIndex < 0)
        {
            return;
        }
        AudioEngineInterop.AccompanimentSetStyleIndex(AccStyleComboBox.SelectedIndex);
        // Selecting a style adopts its own suggested tempo natively (see
        // AccompanimentEngine::SetStyleIndex) - reflect that here rather
        // than leaving whatever was previously typed in the tempo box.
        AccTempoTextBox.Text = AudioEngineInterop.AccompanimentGetTempoBpm().ToString("0.##", CultureInfo.InvariantCulture);
        AccStatusText.Text = Localization.F("Acc.StatusStyleSelected", AccStyleComboBox.Items[AccStyleComboBox.SelectedIndex]?.ToString() ?? "");
    }

    private void AccLayerCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender == AccDrumsCheckBox)
        {
            AudioEngineInterop.AccompanimentSetLayerEnabled(AudioEngineInterop.AccompanimentLayer.Drums, AccDrumsCheckBox.IsChecked == true);
        }
        else if (sender == AccBassCheckBox)
        {
            AudioEngineInterop.AccompanimentSetLayerEnabled(AudioEngineInterop.AccompanimentLayer.Bass, AccBassCheckBox.IsChecked == true);
        }
        else if (sender == AccKontraCheckBox)
        {
            AudioEngineInterop.AccompanimentSetLayerEnabled(AudioEngineInterop.AccompanimentLayer.Kontra, AccKontraCheckBox.IsChecked == true);
        }
        else if (sender == AccHarmonijaCheckBox)
        {
            AudioEngineInterop.AccompanimentSetLayerEnabled(AudioEngineInterop.AccompanimentLayer.Harmonija, AccHarmonijaCheckBox.IsChecked == true);
        }
    }

    private void AccChordInputMode_Click(object sender, RoutedEventArgs e)
    {
        bool autoFromKeyboard = AccChordAutoRadio.IsChecked == true;
        AudioEngineInterop.AccompanimentSetChordInputAutoFromKeyboard(autoFromKeyboard);
        AccStatusText.Text = autoFromKeyboard ? Localization.T("Acc.StatusChordAuto") : Localization.T("Acc.StatusChordManual");
    }

    private void ApplySplitPointButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SequencerNoteRow.TryParsePitch(AccSplitPointTextBox.Text, out int splitPoint))
        {
            AccStatusText.Text = Localization.T("Acc.StatusInvalidSplitPoint");
            return;
        }
        AudioEngineInterop.AccompanimentSetSplitPoint(splitPoint);
        AccStatusText.Text = Localization.T("Acc.StatusSplitApplied");
    }

    private void ApplyManualChordButton_Click(object sender, RoutedEventArgs e)
    {
        if (AccManualRootComboBox.SelectedIndex < 0 || AccManualQualityComboBox.SelectedIndex < 0)
        {
            return;
        }
        AudioEngineInterop.AccompanimentSetManualChord(AccManualRootComboBox.SelectedIndex, (AudioEngineInterop.ChordQuality)AccManualQualityComboBox.SelectedIndex);
        RefreshAccompanimentCurrentChordText();
        AccStatusText.Text = Localization.T("Acc.StatusChordApplied");
    }

    private void ApplyAccTempoButton_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(AccTempoTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double tempo) || tempo <= 0)
        {
            AccStatusText.Text = Localization.T("Acc.StatusInvalidTempo");
            return;
        }
        AudioEngineInterop.AccompanimentSetTempoBpm(tempo);
        AccStatusText.Text = Localization.T("Acc.StatusTempoApplied");
    }

    private void PlayAccompanimentButton_Click(object sender, RoutedEventArgs e)
    {
        if (AudioEngineInterop.AccompanimentGetStyleCount() == 0 || AccStyleComboBox.SelectedIndex < 0)
        {
            AccStatusText.Text = Localization.T("Acc.StatusNoStyle");
            return;
        }
        AudioEngineInterop.PlayAccompaniment();
        PlayAccompanimentButton.IsEnabled = false;
        StopAccompanimentButton.IsEnabled = true;
        AccStatusText.Text = Localization.T("Acc.StatusPlaying");
    }

    private void StopAccompanimentButton_Click(object sender, RoutedEventArgs e)
    {
        AudioEngineInterop.StopAccompaniment();
        PlayAccompanimentButton.IsEnabled = true;
        StopAccompanimentButton.IsEnabled = false;
        AccStatusText.Text = Localization.T("Acc.StatusStopped");
    }

    // --- Auto-pratnja: instrument po sloju (bas/kontra/harmonija iz zvučne banke) ---

    private static string LayerDisplayName(AudioEngineInterop.AccompanimentMelodicLayer layer) => layer switch
    {
        AudioEngineInterop.AccompanimentMelodicLayer.Kontra => Localization.T("Acc.KontraCheckbox"),
        AudioEngineInterop.AccompanimentMelodicLayer.Harmonija => Localization.T("Acc.HarmonijaCheckbox"),
        _ => Localization.T("Acc.BassCheckbox"),
    };

    private AudioEngineInterop.AccompanimentMelodicLayer GetSelectedAccLayer() => AccInstrumentLayerComboBox.SelectedIndex switch
    {
        1 => AudioEngineInterop.AccompanimentMelodicLayer.Kontra,
        2 => AudioEngineInterop.AccompanimentMelodicLayer.Harmonija,
        _ => AudioEngineInterop.AccompanimentMelodicLayer.Bass,
    };

    /// <summary>
    /// The "Sloj" combo's 4th entry - "Komplet" (all three layers at once) -
    /// isn't a real native MelodicLayer, just a UI convenience for loading
    /// one SoundFont bank into bass/kontra/harmonija together instead of
    /// picking each one separately.
    /// </summary>
    private bool IsAllAccLayersSelected() => AccInstrumentLayerComboBox.SelectedIndex == 3;

    /// <summary>
    /// Fills the "Sloj" combo with the three melodic layers' (language-
    /// dependent) display names plus "Komplet" (all three at once), keeping
    /// whichever one was already selected (defaulting to Bas), then loads
    /// that selection's current instrument settings into the panel below.
    /// </summary>
    private void PopulateAccInstrumentLayerCombo()
    {
        int previousIndex = AccInstrumentLayerComboBox.SelectedIndex;

        _isPopulatingAccLayerCombo = true;
        AccInstrumentLayerComboBox.Items.Clear();
        AccInstrumentLayerComboBox.Items.Add(Localization.T("Acc.BassCheckbox"));
        AccInstrumentLayerComboBox.Items.Add(Localization.T("Acc.KontraCheckbox"));
        AccInstrumentLayerComboBox.Items.Add(Localization.T("Acc.HarmonijaCheckbox"));
        AccInstrumentLayerComboBox.Items.Add(Localization.T("Acc.AllLayersOption"));
        AccInstrumentLayerComboBox.SelectedIndex = previousIndex >= 0 && previousIndex < 4 ? previousIndex : 0;
        _isPopulatingAccLayerCombo = false;

        LoadSelectedAccLayerIntoPanel();
    }

    /// <summary>Display label for a layer's current/chosen instrument mode, for status text.</summary>
    private static string LayerModeLabel(AudioEngineInterop.AccompanimentInstrumentMode mode) => mode switch
    {
        AudioEngineInterop.AccompanimentInstrumentMode.SoundFont => Localization.T("Acc.LayerSoundFontRadio"),
        AudioEngineInterop.AccompanimentInstrumentMode.Vst => Localization.T("Acc.LayerVstRadio"),
        _ => Localization.T("Acc.LayerSynthRadio"),
    };

    /// <summary>
    /// Fills every control below the "Sloj" combo from the native engine's
    /// current state for whichever layer is currently selected - mirrors
    /// LoadSelectedTrackIntoDetailPanel's role for the multi-track mixer.
    /// When "Komplet" is selected, there's no single mode/preset spanning
    /// all three layers, so the mode radios and preset list are cleared and
    /// disabled instead, leaving just the SoundFont path/browse/load
    /// controls (which AccLoadLayerSoundFontButton_Click routes to
    /// LoadSoundFontIntoAllAccLayers for that case) - "Komplet" only bulk-
    /// loads a SoundFont bank, not a VST3 instrument, so the VST controls
    /// are disabled there too.
    /// </summary>
    private void LoadSelectedAccLayerIntoPanel()
    {
        AccLoadLayerSoundFontButton.Content = IsAllAccLayersSelected()
            ? Localization.T("Acc.LoadAllLayersButton")
            : Localization.T("Acc.LoadLayerSoundFontButton");

        if (IsAllAccLayersSelected())
        {
            AccLayerSynthRadio.IsChecked = false;
            AccLayerSoundFontRadio.IsChecked = false;
            AccLayerVstRadio.IsChecked = false;
            AccLayerSynthRadio.IsEnabled = false;
            AccLayerSoundFontRadio.IsEnabled = false;
            AccLayerVstRadio.IsEnabled = false;

            _isPopulatingAccLayerPresets = true;
            AccLayerSoundFontPresetComboBox.Items.Clear();
            AccLayerSoundFontPresetComboBox.IsEnabled = false;
            _isPopulatingAccLayerPresets = false;

            AccLayerSoundFontPathTextBox.Text = string.Empty;
            AccLayerVstPathTextBox.Text = string.Empty;
            AccLayerVstPathTextBox.IsEnabled = false;
            AccBrowseLayerVstButton.IsEnabled = false;
            AccLoadLayerVstButton.IsEnabled = false;
            AccLayerInstrumentStatusText.Text = Localization.T("Acc.StatusAllLayersHint");
            return;
        }

        AccLayerSynthRadio.IsEnabled = true;
        AccLayerSoundFontRadio.IsEnabled = true;
        AccLayerVstRadio.IsEnabled = true;
        AccLayerVstPathTextBox.IsEnabled = true;
        AccBrowseLayerVstButton.IsEnabled = true;
        AccLoadLayerVstButton.IsEnabled = true;

        var layer = GetSelectedAccLayer();

        var mode = AudioEngineInterop.AccompanimentGetLayerInstrumentMode(layer);
        AccLayerSynthRadio.IsChecked = mode == AudioEngineInterop.AccompanimentInstrumentMode.BuiltInSynth;
        AccLayerSoundFontRadio.IsChecked = mode == AudioEngineInterop.AccompanimentInstrumentMode.SoundFont;
        AccLayerVstRadio.IsChecked = mode == AudioEngineInterop.AccompanimentInstrumentMode.Vst;

        _isPopulatingAccLayerPresets = true;
        AccLayerSoundFontPresetComboBox.Items.Clear();
        bool bankLoaded = AudioEngineInterop.AccompanimentIsLayerSoundFontBankLoaded(layer);
        AccLayerSoundFontPathTextBox.Text = bankLoaded
            ? (AudioEngineInterop.AccompanimentGetLayerSoundFontBankName(layer) ?? string.Empty)
            : string.Empty;
        if (bankLoaded)
        {
            int presetCount = AudioEngineInterop.AccompanimentGetLayerSoundFontPresetCount(layer);
            int selectedPreset = AudioEngineInterop.AccompanimentGetSelectedLayerSoundFontPresetIndex(layer);
            for (int i = 0; i < presetCount; i++)
            {
                AccLayerSoundFontPresetComboBox.Items.Add(
                    AudioEngineInterop.AccompanimentGetLayerSoundFontPresetName(layer, i) ?? Localization.F("Sf.DefaultInstrumentName", i));
            }
            AccLayerSoundFontPresetComboBox.SelectedIndex = selectedPreset >= 0 && selectedPreset < presetCount ? selectedPreset : -1;
            AccLayerSoundFontPresetComboBox.IsEnabled = true;
        }
        else
        {
            AccLayerSoundFontPresetComboBox.IsEnabled = false;
        }
        _isPopulatingAccLayerPresets = false;

        bool vstLoaded = AudioEngineInterop.AccompanimentIsLayerVstInstrumentLoaded(layer);
        AccLayerVstPathTextBox.Text = vstLoaded
            ? (AudioEngineInterop.AccompanimentGetLayerVstInstrumentName(layer) ?? string.Empty)
            : string.Empty;

        AccLayerInstrumentStatusText.Text = Localization.F("Acc.StatusLayerInstrument",
            LayerDisplayName(layer), LayerModeLabel(mode));
    }

    private void AccInstrumentLayerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isPopulatingAccLayerCombo || AccInstrumentLayerComboBox.SelectedIndex < 0)
        {
            return;
        }
        LoadSelectedAccLayerIntoPanel();
    }

    private void AccLayerInstrumentMode_Click(object sender, RoutedEventArgs e)
    {
        if (IsAllAccLayersSelected())
        {
            // The mode radios are disabled while "Komplet" is selected (see
            // LoadSelectedAccLayerIntoPanel), so this shouldn't normally
            // fire, but guard anyway rather than acting on a meaningless
            // "all layers" mode switch.
            return;
        }

        var layer = GetSelectedAccLayer();
        AudioEngineInterop.AccompanimentInstrumentMode mode;
        if (AccLayerSoundFontRadio.IsChecked == true)
        {
            mode = AudioEngineInterop.AccompanimentInstrumentMode.SoundFont;
        }
        else if (AccLayerVstRadio.IsChecked == true)
        {
            mode = AudioEngineInterop.AccompanimentInstrumentMode.Vst;
        }
        else
        {
            mode = AudioEngineInterop.AccompanimentInstrumentMode.BuiltInSynth;
        }
        AudioEngineInterop.AccompanimentSetLayerInstrumentMode(layer, mode);
        AccLayerInstrumentStatusText.Text = Localization.F("Acc.StatusLayerInstrument",
            LayerDisplayName(layer), LayerModeLabel(mode));
    }

    private void AccBrowseLayerSoundFontButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Localization.T("Sf.BrowseDialogTitle"),
            Filter = $"SoundFont (*.sf2)|*.sf2|{Localization.T("Common.AllFiles")}|*.*",
        };

        if (dialog.ShowDialog(this) == true)
        {
            AccLayerSoundFontPathTextBox.Text = dialog.FileName;
        }
    }

    private void AccLoadLayerSoundFontButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsAllAccLayersSelected())
        {
            LoadSoundFontIntoAllAccLayers();
            return;
        }

        string path = AccLayerSoundFontPathTextBox.Text?.Trim() ?? string.Empty;
        if (path.Length == 0)
        {
            AccLayerInstrumentStatusText.Text = Localization.T("Sf.StatusEmptyPath");
            return;
        }

        var layer = GetSelectedAccLayer();
        string? error = AudioEngineInterop.AccompanimentLoadLayerSoundFontBank(layer, path);
        if (error != null)
        {
            AccLayerInstrumentStatusText.Text = Localization.F("Sf.StatusLoadFailed", error);
            return;
        }

        // Loading a bank auto-switches the native layer to SoundFont mode
        // (see AccompanimentEngine::LoadLayerSoundFontBank) - reflect that
        // in the radio buttons too.
        _accLayerSoundFontPaths[(int)layer] = path;
        AccLayerSoundFontRadio.IsChecked = true;
        LoadSelectedAccLayerIntoPanel();
        AccLayerInstrumentStatusText.Text = Localization.F("Acc.StatusLayerBankLoaded",
            LayerDisplayName(layer),
            AudioEngineInterop.AccompanimentGetLayerSoundFontBankName(layer) ?? Localization.T("Sf.DefaultBankName"));
    }

    private void AccBrowseLayerVstButton_Click(object sender, RoutedEventArgs e)
    {
        // CheckFileExists off for the same reason as the main "Banka
        // instrumenata" VST browse dialog - a VST3 is often a bundle
        // folder, not a single file (see BrowseVstButton_Click's comment).
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Localization.T("Vst.BrowseDialogTitle"),
            Filter = $"VST3 (*.vst3)|*.vst3|{Localization.T("Common.AllFiles")}|*.*",
            CheckFileExists = false,
            CheckPathExists = true,
        };

        if (dialog.ShowDialog(this) == true)
        {
            AccLayerVstPathTextBox.Text = dialog.FileName;
        }
    }

    private void AccLoadLayerVstButton_Click(object sender, RoutedEventArgs e)
    {
        // No "Komplet" case here - unlike the SoundFont load button, this
        // one is disabled entirely while "Komplet" is selected (see
        // LoadSelectedAccLayerIntoPanel), since bulk-loading one VST3 into
        // all three layers wasn't asked for.
        string path = AccLayerVstPathTextBox.Text?.Trim() ?? string.Empty;
        if (path.Length == 0)
        {
            AccLayerInstrumentStatusText.Text = Localization.T("Vst.StatusEmptyPath");
            return;
        }

        var layer = GetSelectedAccLayer();
        string? error = AudioEngineInterop.AccompanimentLoadLayerVstInstrument(layer, path);
        if (error != null)
        {
            AccLayerInstrumentStatusText.Text = Localization.F("Vst.StatusLoadFailed", error);
            return;
        }

        // Loading a plug-in auto-switches the native layer to Vst mode
        // (see AccompanimentEngine::LoadLayerVstInstrument) - reflect that
        // in the radio buttons too.
        _accLayerVstPaths[(int)layer] = path;
        AccLayerVstRadio.IsChecked = true;
        LoadSelectedAccLayerIntoPanel();
        AccLayerInstrumentStatusText.Text = Localization.F("Acc.StatusLayerVstLoaded",
            LayerDisplayName(layer),
            AudioEngineInterop.AccompanimentGetLayerVstInstrumentName(layer) ?? Localization.T("Vst.DefaultInstrumentName"));
    }

    private void AccLayerSoundFontPresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isPopulatingAccLayerPresets)
        {
            return;
        }

        int index = AccLayerSoundFontPresetComboBox.SelectedIndex;
        if (index < 0)
        {
            return;
        }

        var layer = GetSelectedAccLayer();
        if (AudioEngineInterop.AccompanimentSelectLayerSoundFontPreset(layer, index))
        {
            AccLayerInstrumentStatusText.Text = Localization.F("Acc.StatusLayerPresetChanged",
                LayerDisplayName(layer), AccLayerSoundFontPresetComboBox.SelectedItem);
        }
    }

    /// <summary>
    /// Scans a layer's loaded preset names (case-insensitively) for the
    /// first one containing any of 'keywords' - used by
    /// LoadSoundFontIntoAllAccLayers to pick a sensible starting instrument
    /// per layer (e.g. something with "bass" in its name for the bass
    /// layer) without requiring the person to hunt through the list
    /// manually. Returns -1 if nothing matched (the bank's own default
    /// preset, already selected by LoadBank, is left in place then).
    /// </summary>
    private static int FindPresetIndexByKeywords(AudioEngineInterop.AccompanimentMelodicLayer layer, int presetCount, string[] keywords)
    {
        for (int i = 0; i < presetCount; i++)
        {
            string? name = AudioEngineInterop.AccompanimentGetLayerSoundFontPresetName(layer, i);
            if (name == null)
            {
                continue;
            }
            string lower = name.ToLowerInvariant();
            foreach (string keyword in keywords)
            {
                if (lower.Contains(keyword))
                {
                    return i;
                }
            }
        }
        return -1;
    }

    /// <summary>
    /// "Komplet" (the 4th "Sloj" entry): loads one .sf2 file - typed/browsed
    /// right here in the Auto-pratnja view - into all three melodic layers
    /// at once (each its own independent copy, so they can each pick a
    /// different instrument from it) and switches them all to SoundFont
    /// mode, guessing a sensible starting instrument per layer by name.
    /// Replaces the earlier "use the main bank" shortcut, which only worked
    /// off whatever happened to already be loaded in the separate "Zvučna
    /// banka" view - this instead works from its own path, so it's usable
    /// on its own regardless of what (if anything) is loaded there.
    /// </summary>
    private void LoadSoundFontIntoAllAccLayers()
    {
        string path = AccLayerSoundFontPathTextBox.Text?.Trim() ?? string.Empty;
        if (path.Length == 0)
        {
            AccLayerInstrumentStatusText.Text = Localization.T("Sf.StatusEmptyPath");
            return;
        }

        (AudioEngineInterop.AccompanimentMelodicLayer Layer, string[] Keywords)[] layers =
        {
            (AudioEngineInterop.AccompanimentMelodicLayer.Bass, new[] { "bass" }),
            (AudioEngineInterop.AccompanimentMelodicLayer.Kontra, new[] { "piano", "guitar", "accordion" }),
            (AudioEngineInterop.AccompanimentMelodicLayer.Harmonija, new[] { "string", "pad", "choir", "organ" }),
        };

        foreach (var (layer, keywords) in layers)
        {
            string? error = AudioEngineInterop.AccompanimentLoadLayerSoundFontBank(layer, path);
            if (error != null)
            {
                AccLayerInstrumentStatusText.Text = Localization.F("Sf.StatusLoadFailed", error);
                return;
            }

            _accLayerSoundFontPaths[(int)layer] = path;

            int presetCount = AudioEngineInterop.AccompanimentGetLayerSoundFontPresetCount(layer);
            int match = FindPresetIndexByKeywords(layer, presetCount, keywords);
            if (match >= 0)
            {
                AudioEngineInterop.AccompanimentSelectLayerSoundFontPreset(layer, match);
            }
        }

        AccLayerInstrumentStatusText.Text = Localization.T("Acc.StatusAllLayersDone");
    }

    // --- Auto-pratnja: gustina akorda (trozvuk / četvorozvuk / kako je ritam napisao) ---

    private static string ChordVoicingDisplayName(AudioEngineInterop.AccompanimentChordVoicing voicing) => voicing switch
    {
        AudioEngineInterop.AccompanimentChordVoicing.Triad => Localization.T("Acc.ChordVoicingTriad"),
        AudioEngineInterop.AccompanimentChordVoicing.Seventh => Localization.T("Acc.ChordVoicingSeventh"),
        _ => Localization.T("Acc.ChordVoicingAsAuthored"),
    };

    /// <summary>
    /// Fills the chord-voicing combo with its three options (language-
    /// dependent display names) and selects whichever one the native engine
    /// currently has active, without triggering the SelectionChanged
    /// handler while doing so.
    /// </summary>
    private void PopulateAccChordVoicingCombo()
    {
        var current = AudioEngineInterop.AccompanimentGetChordVoicing();

        _isPopulatingAccChordVoicing = true;
        AccChordVoicingComboBox.Items.Clear();
        AccChordVoicingComboBox.Items.Add(Localization.T("Acc.ChordVoicingAsAuthored"));
        AccChordVoicingComboBox.Items.Add(Localization.T("Acc.ChordVoicingTriad"));
        AccChordVoicingComboBox.Items.Add(Localization.T("Acc.ChordVoicingSeventh"));
        AccChordVoicingComboBox.SelectedIndex = (int)current;
        _isPopulatingAccChordVoicing = false;
    }

    private void AccChordVoicingComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isPopulatingAccChordVoicing || AccChordVoicingComboBox.SelectedIndex < 0)
        {
            return;
        }

        var voicing = (AudioEngineInterop.AccompanimentChordVoicing)AccChordVoicingComboBox.SelectedIndex;
        AudioEngineInterop.AccompanimentSetChordVoicing(voicing);
    }

    // --- Sopstveni ritam (custom rhythm/style builder) ---
    // Builds one "takt" (bar) at a time out of drum hits plus bass/kontra/
    // harmonija chord-tone hits (see the row classes at the end of this
    // file); "Dodaj kao novi takt" snapshots the working bar into
    // _customRhythmBars and starts a fresh one, so additional bars can be
    // added afterwards as variations, exactly like the Aranžer chains
    // sequencer patterns. "Koristi kao aktivan ritam" flattens every
    // committed bar (offsetting each one's hits by the beats already played
    // by the bars before it) into flat arrays and sends them to the native
    // engine in one call (AudioEngineInterop.AccompanimentSetCustomStyle),
    // which installs and immediately selects it - no per-hit native calls
    // needed, the whole thing is built here in the UI first.

    private void PopulateCustomRhythmCombos()
    {
        int previousDrumVoice = CustomRhythmDrumVoiceComboBox.SelectedIndex;
        int previousLayer = CustomRhythmLayerComboBox.SelectedIndex;

        CustomRhythmDrumVoiceComboBox.Items.Clear();
        CustomRhythmDrumVoiceComboBox.Items.Add(CustomRhythmMelodicHitRow.DrumVoiceDisplayName(AudioEngineInterop.CustomDrumVoice.Kick));
        CustomRhythmDrumVoiceComboBox.Items.Add(CustomRhythmMelodicHitRow.DrumVoiceDisplayName(AudioEngineInterop.CustomDrumVoice.Snare));
        CustomRhythmDrumVoiceComboBox.Items.Add(CustomRhythmMelodicHitRow.DrumVoiceDisplayName(AudioEngineInterop.CustomDrumVoice.ClosedHat));
        CustomRhythmDrumVoiceComboBox.Items.Add(CustomRhythmMelodicHitRow.DrumVoiceDisplayName(AudioEngineInterop.CustomDrumVoice.OpenHat));
        CustomRhythmDrumVoiceComboBox.Items.Add(CustomRhythmMelodicHitRow.DrumVoiceDisplayName(AudioEngineInterop.CustomDrumVoice.Clap));
        CustomRhythmDrumVoiceComboBox.Items.Add(CustomRhythmMelodicHitRow.DrumVoiceDisplayName(AudioEngineInterop.CustomDrumVoice.Crash));
        CustomRhythmDrumVoiceComboBox.Items.Add(CustomRhythmMelodicHitRow.DrumVoiceDisplayName(AudioEngineInterop.CustomDrumVoice.Tom));
        CustomRhythmDrumVoiceComboBox.Items.Add(CustomRhythmMelodicHitRow.DrumVoiceDisplayName(AudioEngineInterop.CustomDrumVoice.DumbekDum));
        CustomRhythmDrumVoiceComboBox.Items.Add(CustomRhythmMelodicHitRow.DrumVoiceDisplayName(AudioEngineInterop.CustomDrumVoice.DumbekTek));
        CustomRhythmDrumVoiceComboBox.SelectedIndex =
            previousDrumVoice >= 0 && previousDrumVoice < CustomRhythmDrumVoiceComboBox.Items.Count ? previousDrumVoice : 0;

        CustomRhythmLayerComboBox.Items.Clear();
        CustomRhythmLayerComboBox.Items.Add(CustomRhythmMelodicHitRow.LayerDisplayName(AudioEngineInterop.AccompanimentMelodicLayer.Bass));
        CustomRhythmLayerComboBox.Items.Add(CustomRhythmMelodicHitRow.LayerDisplayName(AudioEngineInterop.AccompanimentMelodicLayer.Kontra));
        CustomRhythmLayerComboBox.Items.Add(CustomRhythmMelodicHitRow.LayerDisplayName(AudioEngineInterop.AccompanimentMelodicLayer.Harmonija));
        CustomRhythmLayerComboBox.SelectedIndex =
            previousLayer >= 0 && previousLayer < CustomRhythmLayerComboBox.Items.Count ? previousLayer : 0;
    }

    private void AddCustomRhythmDrumHitButton_Click(object sender, RoutedEventArgs e)
    {
        if (CustomRhythmDrumVoiceComboBox.SelectedIndex < 0)
        {
            return;
        }

        if (!double.TryParse(CustomRhythmDrumBeatTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double startBeat) || startBeat < 0)
        {
            CustomRhythmStatusText.Text = Localization.T("Cr.StatusInvalidBeat");
            return;
        }

        if (!int.TryParse(CustomRhythmDrumVelocityTextBox.Text, out int velocity) || velocity < 1 || velocity > 127)
        {
            CustomRhythmStatusText.Text = Localization.T("Cr.StatusInvalidVelocity");
            return;
        }

        var voice = (AudioEngineInterop.CustomDrumVoice)CustomRhythmDrumVoiceComboBox.SelectedIndex;
        _customRhythmDrumHits.Add(new CustomRhythmDrumHitRow(voice, startBeat, velocity));
        CustomRhythmStatusText.Text = Localization.F("Cr.StatusDrumHitAdded", _customRhythmDrumHits.Count);
    }

    private void RemoveCustomRhythmDrumHitButton_Click(object sender, RoutedEventArgs e)
    {
        if (CustomRhythmDrumListView.SelectedItem is CustomRhythmDrumHitRow row)
        {
            _customRhythmDrumHits.Remove(row);
            CustomRhythmStatusText.Text = Localization.F("Cr.StatusDrumHitRemoved", _customRhythmDrumHits.Count);
        }
        else
        {
            CustomRhythmStatusText.Text = Localization.T("Cr.StatusRemoveNeedSelection");
        }
    }

    private void ClearCustomRhythmDrumHitsButton_Click(object sender, RoutedEventArgs e)
    {
        _customRhythmDrumHits.Clear();
        CustomRhythmStatusText.Text = Localization.T("Cr.StatusDrumHitsCleared");
    }

    private void AddCustomRhythmMelodicHitButton_Click(object sender, RoutedEventArgs e)
    {
        if (CustomRhythmLayerComboBox.SelectedIndex < 0)
        {
            return;
        }

        if (!double.TryParse(CustomRhythmMelodicBeatTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double startBeat) || startBeat < 0)
        {
            CustomRhythmStatusText.Text = Localization.T("Cr.StatusInvalidBeat");
            return;
        }

        if (!double.TryParse(CustomRhythmMelodicLengthTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double lengthBeats) || lengthBeats <= 0)
        {
            CustomRhythmStatusText.Text = Localization.T("Cr.StatusInvalidLength");
            return;
        }

        bool root = CustomRhythmRootCheckBox.IsChecked == true;
        bool third = CustomRhythmThirdCheckBox.IsChecked == true;
        bool fifth = CustomRhythmFifthCheckBox.IsChecked == true;
        bool seventh = CustomRhythmSeventhCheckBox.IsChecked == true;
        if (!root && !third && !fifth && !seventh)
        {
            CustomRhythmStatusText.Text = Localization.T("Cr.StatusNeedTone");
            return;
        }

        if (!int.TryParse(CustomRhythmOctaveTextBox.Text, out int octaveOffset))
        {
            octaveOffset = 0;
        }

        if (!int.TryParse(CustomRhythmMelodicVelocityTextBox.Text, out int velocity) || velocity < 1 || velocity > 127)
        {
            CustomRhythmStatusText.Text = Localization.T("Cr.StatusInvalidVelocity");
            return;
        }

        var layer = (AudioEngineInterop.AccompanimentMelodicLayer)CustomRhythmLayerComboBox.SelectedIndex;
        _customRhythmMelodicHits.Add(new CustomRhythmMelodicHitRow(layer, startBeat, lengthBeats, root, third, fifth, seventh, octaveOffset, velocity));
        CustomRhythmStatusText.Text = Localization.F("Cr.StatusMelodicHitAdded", _customRhythmMelodicHits.Count);
    }

    private void RemoveCustomRhythmMelodicHitButton_Click(object sender, RoutedEventArgs e)
    {
        if (CustomRhythmMelodicListView.SelectedItem is CustomRhythmMelodicHitRow row)
        {
            _customRhythmMelodicHits.Remove(row);
            CustomRhythmStatusText.Text = Localization.F("Cr.StatusMelodicHitRemoved", _customRhythmMelodicHits.Count);
        }
        else
        {
            CustomRhythmStatusText.Text = Localization.T("Cr.StatusRemoveNeedSelection");
        }
    }

    private void ClearCustomRhythmMelodicHitsButton_Click(object sender, RoutedEventArgs e)
    {
        _customRhythmMelodicHits.Clear();
        CustomRhythmStatusText.Text = Localization.T("Cr.StatusMelodicHitsCleared");
    }

    private void AddCustomRhythmBarButton_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(CustomRhythmLengthTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double lengthBeats) || lengthBeats <= 0)
        {
            CustomRhythmStatusText.Text = Localization.T("Cr.StatusInvalidLength");
            return;
        }

        if (_customRhythmDrumHits.Count == 0 && _customRhythmMelodicHits.Count == 0)
        {
            CustomRhythmStatusText.Text = Localization.T("Cr.StatusBarEmpty");
            return;
        }

        _customRhythmBars.Add(new CustomRhythmBarSnapshot(
            lengthBeats,
            new List<CustomRhythmDrumHitRow>(_customRhythmDrumHits),
            new List<CustomRhythmMelodicHitRow>(_customRhythmMelodicHits)));

        // Fresh working bar for the next variation - the bar length stays
        // as a convenient starting point (most variations share the same
        // meter) but is still freely editable before the next hit is added.
        _customRhythmDrumHits.Clear();
        _customRhythmMelodicHits.Clear();

        CustomRhythmBarsStatusText.Text = Localization.F("Cr.StatusBarCount", _customRhythmBars.Count);
        CustomRhythmStatusText.Text = Localization.F("Cr.StatusBarAdded", _customRhythmBars.Count);
    }

    private void RemoveLastCustomRhythmBarButton_Click(object sender, RoutedEventArgs e)
    {
        if (_customRhythmBars.Count == 0)
        {
            CustomRhythmStatusText.Text = Localization.T("Cr.StatusNoBars");
            return;
        }

        _customRhythmBars.RemoveAt(_customRhythmBars.Count - 1);
        CustomRhythmBarsStatusText.Text = _customRhythmBars.Count == 0
            ? Localization.T("Cr.StatusNoBars")
            : Localization.F("Cr.StatusBarCount", _customRhythmBars.Count);
        CustomRhythmStatusText.Text = Localization.T("Cr.StatusLastBarRemoved");
    }

    private void ClearCustomRhythmBarsButton_Click(object sender, RoutedEventArgs e)
    {
        _customRhythmBars.Clear();
        CustomRhythmBarsStatusText.Text = Localization.T("Cr.StatusNoBars");
        CustomRhythmStatusText.Text = Localization.T("Cr.StatusBarsCleared");
    }

    /// <summary>
    /// Flattens every bar currently in <see cref="_customRhythmBars"/> (in
    /// order, offsetting each bar's hits by the cumulative length of the
    /// bars before it) into one Style and installs it via
    /// AccompanimentSetCustomStyle - shared by "Sačuvaj ritam" and project
    /// load (see ApplyProjectData), since both need to turn a bar list into
    /// an active custom style. 'replaceIndex' is -1 to add a new custom
    /// style, or an existing custom style's own index to edit it in place
    /// (see AccompanimentEngine::SetCustomStyle). Returns the resulting
    /// style index, or -1 if 'bars' is empty.
    /// </summary>
    private int InstallCustomRhythmFromBars(string name, double tempoBpm, List<CustomRhythmBarSnapshot> bars, int replaceIndex = -1)
    {
        if (bars.Count == 0)
        {
            return -1;
        }

        var drumHits = new List<AudioEngineInterop.CustomDrumHit>();
        var bassHits = new List<AudioEngineInterop.CustomChordHit>();
        var kontraHits = new List<AudioEngineInterop.CustomChordHit>();
        var harmonijaHits = new List<AudioEngineInterop.CustomChordHit>();

        double totalLengthBeats = 0.0;
        foreach (CustomRhythmBarSnapshot bar in bars)
        {
            double barOffset = totalLengthBeats;
            foreach (CustomRhythmDrumHitRow hit in bar.DrumHits)
            {
                drumHits.Add(new AudioEngineInterop.CustomDrumHit
                {
                    StartBeat = hit.StartBeat + barOffset,
                    DrumVoice = (int)hit.Voice,
                    Velocity = hit.Velocity / 127f,
                });
            }

            foreach (CustomRhythmMelodicHitRow hit in bar.MelodicHits)
            {
                var chordHit = new AudioEngineInterop.CustomChordHit
                {
                    StartBeat = hit.StartBeat + barOffset,
                    LengthBeats = hit.LengthBeats,
                    ChordToneMask = hit.ChordToneMask,
                    OctaveOffset = hit.OctaveOffset,
                    Velocity = hit.Velocity / 127f,
                };
                switch (hit.Layer)
                {
                    case AudioEngineInterop.AccompanimentMelodicLayer.Bass:
                        bassHits.Add(chordHit);
                        break;
                    case AudioEngineInterop.AccompanimentMelodicLayer.Kontra:
                        kontraHits.Add(chordHit);
                        break;
                    default:
                        harmonijaHits.Add(chordHit);
                        break;
                }
            }

            totalLengthBeats += bar.LengthBeats;
        }

        return AudioEngineInterop.AccompanimentSetCustomStyle(
            name, tempoBpm, totalLengthBeats, bars[0].LengthBeats,
            drumHits.ToArray(), bassHits.ToArray(), kontraHits.ToArray(), harmonijaHits.ToArray(),
            replaceIndex);
    }

    /// <summary>
    /// "Sačuvaj ritam" - saves the rhythm currently being built. If
    /// <see cref="_editingSavedCustomRhythmIndex"/> points at a
    /// previously-saved rhythm (set by saving a new one, or by "Uredi
    /// izabrani"), this updates that same one in place; otherwise it's
    /// added as a brand new custom rhythm alongside any already saved -
    /// any number can coexist (see AccompanimentEngine::SetCustomStyle).
    /// </summary>
    private void UseCustomRhythmButton_Click(object sender, RoutedEventArgs e)
    {
        if (_customRhythmBars.Count == 0)
        {
            CustomRhythmStatusText.Text = Localization.T("Cr.StatusNoBars");
            return;
        }

        string name = CustomRhythmNameTextBox.Text?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            CustomRhythmStatusText.Text = Localization.T("Cr.StatusNeedName");
            return;
        }

        double tempoBpm = double.TryParse(AccTempoTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedTempo) && parsedTempo > 0
            ? parsedTempo
            : 100.0;

        bool isUpdate = _editingSavedCustomRhythmIndex.HasValue
            && _editingSavedCustomRhythmIndex.Value >= 0
            && _editingSavedCustomRhythmIndex.Value < _savedCustomRhythms.Count;
        int replaceIndex = isUpdate ? _savedCustomRhythms[_editingSavedCustomRhythmIndex!.Value].NativeStyleIndex : -1;

        int index = InstallCustomRhythmFromBars(name, tempoBpm, _customRhythmBars, replaceIndex);
        if (index < 0)
        {
            CustomRhythmStatusText.Text = Localization.T("Cr.StatusBarEmpty");
            return;
        }

        // CustomRhythmBarSnapshot is immutable, so this new List just needs
        // its own copy of the references - no deep clone required, and the
        // working _customRhythmBars can go on being edited (or cleared by
        // "Novi ritam") afterwards without disturbing what was just saved.
        var barsCopy = new List<CustomRhythmBarSnapshot>(_customRhythmBars);

        if (isUpdate)
        {
            SavedCustomRhythm entry = _savedCustomRhythms[_editingSavedCustomRhythmIndex!.Value];
            entry.Name = name;
            entry.Bars = barsCopy;
            entry.NativeStyleIndex = index;
            CustomRhythmStatusText.Text = Localization.F("Cr.StatusUpdated", name);
        }
        else
        {
            _savedCustomRhythms.Add(new SavedCustomRhythm { Name = name, Bars = barsCopy, NativeStyleIndex = index });
            _editingSavedCustomRhythmIndex = _savedCustomRhythms.Count - 1;
            CustomRhythmStatusText.Text = Localization.F("Cr.StatusUsed", name);
        }

        PopulateAccompanimentStyles();
        RefreshSavedCustomRhythmsList();
    }

    /// <summary>
    /// "Novi ritam" - clears the working bar editor so a distinct custom
    /// rhythm can be built from scratch, without touching any already-saved
    /// rhythm (those stay exactly as they were, in the native style list
    /// and in <see cref="_savedCustomRhythms"/>).
    /// </summary>
    private void NewCustomRhythmButton_Click(object sender, RoutedEventArgs e)
    {
        _customRhythmDrumHits.Clear();
        _customRhythmMelodicHits.Clear();
        _customRhythmBars.Clear();
        _editingSavedCustomRhythmIndex = null;
        CustomRhythmNameTextBox.Text = string.Empty;
        CustomRhythmBarsStatusText.Text = Localization.T("Cr.StatusNoBars");
        CustomRhythmStatusText.Text = Localization.T("Cr.StatusNewStarted");
    }

    /// <summary>
    /// "Uredi izabrani" - loads a previously-saved rhythm's bars back into
    /// the working editor so it can be tweaked further; the next "Sačuvaj
    /// ritam" then updates this same entry in place (see
    /// UseCustomRhythmButton_Click) instead of adding a duplicate.
    /// </summary>
    private void EditSavedCustomRhythmButton_Click(object sender, RoutedEventArgs e)
    {
        int index = SavedCustomRhythmsListBox.SelectedIndex;
        if (index < 0 || index >= _savedCustomRhythms.Count)
        {
            CustomRhythmSavedStatusText.Text = Localization.T("Cr.StatusNeedSavedSelection");
            return;
        }

        SavedCustomRhythm entry = _savedCustomRhythms[index];
        _customRhythmDrumHits.Clear();
        _customRhythmMelodicHits.Clear();
        _customRhythmBars.Clear();
        _customRhythmBars.AddRange(entry.Bars);
        _editingSavedCustomRhythmIndex = index;
        CustomRhythmNameTextBox.Text = entry.Name;
        CustomRhythmBarsStatusText.Text = _customRhythmBars.Count == 0
            ? Localization.T("Cr.StatusNoBars")
            : Localization.F("Cr.StatusBarCount", _customRhythmBars.Count);
        CustomRhythmSavedStatusText.Text = Localization.F("Cr.StatusEditLoaded", entry.Name);
    }

    /// <summary>
    /// "Ukloni izabrani ritam" - removes a saved custom rhythm from the
    /// native engine and from <see cref="_savedCustomRhythms"/>, keeping
    /// every remaining entry's NativeStyleIndex in sync with the native
    /// shift (see AccompanimentEngine::RemoveCustomStyle).
    /// </summary>
    private void RemoveSavedCustomRhythmButton_Click(object sender, RoutedEventArgs e)
    {
        int index = SavedCustomRhythmsListBox.SelectedIndex;
        if (index < 0 || index >= _savedCustomRhythms.Count)
        {
            CustomRhythmSavedStatusText.Text = Localization.T("Cr.StatusNeedSavedSelection");
            return;
        }

        SavedCustomRhythm entry = _savedCustomRhythms[index];
        if (!AudioEngineInterop.AccompanimentRemoveCustomStyle(entry.NativeStyleIndex))
        {
            CustomRhythmSavedStatusText.Text = Localization.T("Cr.StatusRemoveFailed");
            return;
        }

        _savedCustomRhythms.RemoveAt(index);
        foreach (SavedCustomRhythm remaining in _savedCustomRhythms)
        {
            if (remaining.NativeStyleIndex > entry.NativeStyleIndex)
            {
                remaining.NativeStyleIndex--;
            }
        }

        if (_editingSavedCustomRhythmIndex == index)
        {
            _editingSavedCustomRhythmIndex = null;
        }
        else if (_editingSavedCustomRhythmIndex.HasValue && _editingSavedCustomRhythmIndex.Value > index)
        {
            _editingSavedCustomRhythmIndex--;
        }

        PopulateAccompanimentStyles();
        RefreshSavedCustomRhythmsList();
        CustomRhythmSavedStatusText.Text = Localization.F("Cr.StatusRemoved", entry.Name);
    }

    private void RefreshSavedCustomRhythmsList()
    {
        int previousIndex = SavedCustomRhythmsListBox.SelectedIndex;
        SavedCustomRhythmsListBox.Items.Clear();
        foreach (SavedCustomRhythm entry in _savedCustomRhythms)
        {
            SavedCustomRhythmsListBox.Items.Add(entry.Name);
        }
        SavedCustomRhythmsListBox.SelectedIndex = previousIndex >= 0 && previousIndex < _savedCustomRhythms.Count ? previousIndex : -1;
    }
}

/// <summary>
/// One row shown in the sequencer's note list. Immutable - notes aren't
/// edited in place, only added/removed, so this is just a display record.
/// </summary>
public sealed class SequencerNoteRow
{
    public SequencerNoteRow(int pitch, double startBeat, double lengthBeats, int velocity)
    {
        Pitch = pitch;
        StartBeat = startBeat;
        LengthBeats = lengthBeats;
        Velocity = velocity;
    }

    public int Pitch { get; }
    public double StartBeat { get; }
    public double LengthBeats { get; }

    /// <summary>MIDI velocity, 1-127 (matches how it's entered in the UI).</summary>
    public int Velocity { get; }

    /// <summary>e.g. "60 (C4)" - the raw MIDI number plus a readable note name (language-dependent).</summary>
    public string PitchDisplay => $"{Pitch} ({MidiNoteName(Pitch)})";

    private static string MidiNoteName(int pitch)
    {
        int octave = (pitch / 12) - 1;
        int semitone = ((pitch % 12) + 12) % 12;

        if (Localization.Current == AppLanguage.English)
        {
            // English speakers expect the letter+accidental convention.
            string[] englishNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
            return $"{englishNames[semitone]}{octave}";
        }

        // General/solfege names (taught via Braille music notation): "H" is
        // the natural B, and the five black keys are named cis/dis/fis/gis/ais
        // rather than spelled with a sharp symbol.
        string[] names = { "C", "Cis", "D", "Dis", "E", "F", "Fis", "G", "Gis", "A", "Ais", "H" };
        return $"{names[semitone]}{octave}";
    }

    /// <summary>
    /// General/solfege note names, semitone offset from C (0-11), exactly
    /// as taught via Braille music notation rather than with sharp/flat
    /// symbols: "h" is the natural B, and "b" is B-flat (not English's
    /// "B" = natural - that's the one deliberate difference from the older
    /// letter+accidental spelling below). Both the sharp and flat spelling
    /// of each black key are accepted ("cis" and "des" are the same pitch),
    /// since either might be the one someone actually learned. Accepted
    /// regardless of the current interface language - only the *display*
    /// name (see MidiNoteName) follows the language switch, not what's
    /// accepted as input, so nothing already typed ever stops parsing.
    /// </summary>
    private static readonly Dictionary<string, int> GermanNoteNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["c"] = 0, ["cis"] = 1,
        ["d"] = 2, ["des"] = 1, ["dis"] = 3,
        ["e"] = 4, ["es"] = 3, ["eis"] = 5,
        ["f"] = 5, ["fis"] = 6,
        ["g"] = 7, ["ges"] = 6, ["gis"] = 8,
        ["a"] = 9, ["as"] = 8, ["ais"] = 10,
        ["h"] = 11, ["b"] = 10, ["his"] = 12, ["ces"] = -1,
    };

    /// <summary>
    /// Parses one pitch token, in any of three forms - a plain MIDI number
    /// from 0 to 127; a general/solfege note name (letter name, e.g. "c",
    /// "cis", "des", "h" for natural B, "b" for B-flat - see
    /// <see cref="GermanNoteNames"/>) followed by an octave number, e.g.
    /// "cis4"; or the letter-plus-accidental spelling ("C4", "C#4", "Db4")
    /// for anyone who prefers it. Octave numbering matches the display in
    /// <see cref="MidiNoteName"/> (C4/c4 = middle C = 60), the usual
    /// convention in most DAWs; "C-1"/"c-1" = 0, the lowest MIDI note.
    /// </summary>
    public static bool TryParsePitch(string token, out int midiNote)
    {
        midiNote = 0;
        token = token.Trim();
        if (token.Length == 0)
        {
            return false;
        }

        if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rawNumber)
            && rawNumber >= 0 && rawNumber <= 127)
        {
            midiNote = rawNumber;
            return true;
        }

        // Split into the leading letters (the note name) and whatever
        // follows (meant to be the octave number) - try the general/solfege
        // names first, since that's the naming most people actually learned.
        int alphaLen = 0;
        while (alphaLen < token.Length && char.IsLetter(token[alphaLen]))
        {
            alphaLen++;
        }
        string namePart = token.Substring(0, alphaLen);
        string octavePart = token.Substring(alphaLen);

        if (alphaLen > 0 && GermanNoteNames.TryGetValue(namePart, out int germanSemitone)
            && int.TryParse(octavePart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int germanOctave))
        {
            int germanNote = ((germanOctave + 1) * 12) + germanSemitone;
            if (germanNote < 0 || germanNote > 127)
            {
                return false;
            }

            midiNote = germanNote;
            return true;
        }

        // Fall back to the letter-plus-accidental spelling (C4, C#4, Db4, ...).
        var semitoneFromLetter = new Dictionary<char, int>
        {
            ['A'] = 9, ['B'] = 11, ['C'] = 0, ['D'] = 2, ['E'] = 4, ['F'] = 5, ['G'] = 7,
        };

        char letter = char.ToUpperInvariant(token[0]);
        if (!semitoneFromLetter.TryGetValue(letter, out int semitone))
        {
            return false;
        }

        int pos = 1;
        while (pos < token.Length && (token[pos] == '#' || token[pos] == 'b'))
        {
            semitone += token[pos] == '#' ? 1 : -1;
            pos++;
        }

        string oldOctavePart = token.Substring(pos);
        if (!int.TryParse(oldOctavePart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int octave))
        {
            return false;
        }

        int note = ((octave + 1) * 12) + semitone;
        if (note < 0 || note > 127)
        {
            return false;
        }

        midiNote = note;
        return true;
    }
}

/// <summary>
/// One drum hit in the custom rhythm builder's working bar (see MainWindow's
/// "Sopstveni ritam" section). Immutable, same reasoning as SequencerNoteRow.
/// </summary>
public sealed class CustomRhythmDrumHitRow
{
    public CustomRhythmDrumHitRow(AudioEngineInterop.CustomDrumVoice voice, double startBeat, int velocity)
    {
        Voice = voice;
        StartBeat = startBeat;
        Velocity = velocity;
    }

    public AudioEngineInterop.CustomDrumVoice Voice { get; }
    public double StartBeat { get; }

    /// <summary>MIDI-style velocity, 1-127 (matches how it's entered in the UI).</summary>
    public int Velocity { get; }

    public string VoiceDisplay => CustomRhythmMelodicHitRow.DrumVoiceDisplayName(Voice);
}

/// <summary>
/// One bass/kontra/harmonija hit in the custom rhythm builder's working bar.
/// 'Root'/'Third'/'Fifth'/'Seventh' are which chord tone(s) this hit sounds
/// (see Style.h's RelativePatternChordHit.chordToneIndices) - a bass hit
/// should normally check just one, kontra/harmonija can check several for a
/// stab or pad. Immutable, same reasoning as SequencerNoteRow.
/// </summary>
public sealed class CustomRhythmMelodicHitRow
{
    public CustomRhythmMelodicHitRow(AudioEngineInterop.AccompanimentMelodicLayer layer, double startBeat, double lengthBeats,
        bool root, bool third, bool fifth, bool seventh, int octaveOffset, int velocity)
    {
        Layer = layer;
        StartBeat = startBeat;
        LengthBeats = lengthBeats;
        Root = root;
        Third = third;
        Fifth = fifth;
        Seventh = seventh;
        OctaveOffset = octaveOffset;
        Velocity = velocity;
    }

    public AudioEngineInterop.AccompanimentMelodicLayer Layer { get; }
    public double StartBeat { get; }
    public double LengthBeats { get; }
    public bool Root { get; }
    public bool Third { get; }
    public bool Fifth { get; }
    public bool Seventh { get; }
    public int OctaveOffset { get; }

    /// <summary>MIDI-style velocity, 1-127 (matches how it's entered in the UI).</summary>
    public int Velocity { get; }

    /// <summary>
    /// Bit 0=root, 1=third, 2=fifth, 3=seventh - matches the native
    /// UC_CustomChordHit.chordToneMask packing exactly (see AudioEngineApi.h).
    /// </summary>
    public int ChordToneMask => (Root ? 1 : 0) | (Third ? 2 : 0) | (Fifth ? 4 : 0) | (Seventh ? 8 : 0);

    public string LayerDisplay => LayerDisplayName(Layer);

    public string TonesDisplay
    {
        get
        {
            var parts = new List<string>();
            if (Root)
            {
                parts.Add(Localization.T("Cr.RootCheckBox"));
            }
            if (Third)
            {
                parts.Add(Localization.T("Cr.ThirdCheckBox"));
            }
            if (Fifth)
            {
                parts.Add(Localization.T("Cr.FifthCheckBox"));
            }
            if (Seventh)
            {
                parts.Add(Localization.T("Cr.SeventhCheckBox"));
            }
            return parts.Count == 0 ? "-" : string.Join(" + ", parts);
        }
    }

    public static string LayerDisplayName(AudioEngineInterop.AccompanimentMelodicLayer layer) => layer switch
    {
        AudioEngineInterop.AccompanimentMelodicLayer.Bass => Localization.T("Cr.LayerBass"),
        AudioEngineInterop.AccompanimentMelodicLayer.Kontra => Localization.T("Cr.LayerKontra"),
        AudioEngineInterop.AccompanimentMelodicLayer.Harmonija => Localization.T("Cr.LayerHarmonija"),
        _ => layer.ToString(),
    };

    public static string DrumVoiceDisplayName(AudioEngineInterop.CustomDrumVoice voice) => voice switch
    {
        AudioEngineInterop.CustomDrumVoice.Kick => Localization.T("Cr.DrumKick"),
        AudioEngineInterop.CustomDrumVoice.Snare => Localization.T("Cr.DrumSnare"),
        AudioEngineInterop.CustomDrumVoice.ClosedHat => Localization.T("Cr.DrumClosedHat"),
        AudioEngineInterop.CustomDrumVoice.OpenHat => Localization.T("Cr.DrumOpenHat"),
        AudioEngineInterop.CustomDrumVoice.Clap => Localization.T("Cr.DrumClap"),
        AudioEngineInterop.CustomDrumVoice.Crash => Localization.T("Cr.DrumCrash"),
        AudioEngineInterop.CustomDrumVoice.Tom => Localization.T("Cr.DrumTom"),
        AudioEngineInterop.CustomDrumVoice.DumbekDum => Localization.T("Cr.DrumDumbekDum"),
        AudioEngineInterop.CustomDrumVoice.DumbekTek => Localization.T("Cr.DrumDumbekTek"),
        _ => voice.ToString(),
    };
}

/// <summary>
/// One committed bar in the custom rhythm being built - a snapshot of the
/// working hit lists at the moment "Dodaj kao novi takt" was clicked (see
/// MainWindow.AddCustomRhythmBarButton_Click), plus that bar's own length in
/// beats. Bars play back-to-back in the order they were added, looping the
/// whole chain - additional bars are how "variations" work (see the custom
/// rhythm editor's tip text).
/// </summary>
public sealed class CustomRhythmBarSnapshot
{
    public CustomRhythmBarSnapshot(double lengthBeats, List<CustomRhythmDrumHitRow> drumHits, List<CustomRhythmMelodicHitRow> melodicHits)
    {
        LengthBeats = lengthBeats;
        DrumHits = drumHits;
        MelodicHits = melodicHits;
    }

    public double LengthBeats { get; }
    public List<CustomRhythmDrumHitRow> DrumHits { get; }
    public List<CustomRhythmMelodicHitRow> MelodicHits { get; }
}

/// <summary>
/// One custom rhythm saved via "Sačuvaj ritam" in the "Sopstveni ritam"
/// editor - its name, its bar-by-bar breakdown (still editable later via
/// "Uredi izabrani"), and which native style slot it currently occupies.
/// Unlike CustomRhythmBarSnapshot, this is intentionally mutable: saving
/// changes in place, or removing an earlier entry (which shifts every
/// later one's native slot down by one - see AccompanimentEngine::
/// RemoveCustomStyle), both update an existing SavedCustomRhythm instead of
/// replacing it.
/// </summary>
public sealed class SavedCustomRhythm
{
    public string Name = "";
    public List<CustomRhythmBarSnapshot> Bars = new();
    public int NativeStyleIndex;
}
