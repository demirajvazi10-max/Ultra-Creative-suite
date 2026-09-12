using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UltraComposer.App;

/// <summary>
/// The .adem project file format. Captures the complete state of an Ultra
/// Composer project - per Demir's explicit requirement, "sve zivo, dakle,
/// kompletno sve sto se desava u projektu" (everything happening in the
/// project, alive) - so it can be saved and reopened exactly as it was
/// left: every sequencer/multi-track/arranger note, every auto-pratnja
/// setting (including a custom rhythm still being built), every loaded
/// SoundFont/VST3 path, and the auto-third setting.
///
/// Plain JSON under the hood, just saved with a ".adem" extension (the name
/// is a nod to the reason this whole suite exists - see README.md) rather
/// than a custom binary format - human-diagnosable if something ever goes
/// wrong, and there's no real-time performance reason here to need
/// anything more compact; project files are read/written only on explicit
/// Save/Open, never on the audio thread.
///
/// This class only defines the shape of a project and does the actual file
/// read/write. Gathering the current state into a ProjectData (Save) and
/// applying a loaded ProjectData back into the engine and UI (Open) both
/// live in MainWindow, since only it can reach both the native engine and
/// the on-screen controls/lists.
/// </summary>
public static class ProjectFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static void Save(ProjectData data, string path)
    {
        string json = JsonSerializer.Serialize(data, Options);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Loads a project file. Returns null (rather than throwing) if the
    /// file can't be parsed at all - the caller is expected to report that
    /// as a load failure; a partially-recognizable file still loads with
    /// whatever fields it has, since every field below has a safe default.
    /// </summary>
    public static ProjectData? Load(string path)
    {
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ProjectData>(json, Options);
    }

    /// <summary>One note - same shape everywhere it's used (Sequencer, every multi-track track, every Arranger pattern).</summary>
    public sealed class NoteData
    {
        public int Pitch { get; set; }
        public double StartBeat { get; set; }
        public double LengthBeats { get; set; } = 1.0;

        /// <summary>MIDI-style velocity, 1-127 (matches how it's entered/shown in the UI).</summary>
        public int Velocity { get; set; } = 100;
    }

    public sealed class TrackData
    {
        public string Name { get; set; } = "";
        public List<NoteData> Notes { get; set; } = new();
        public float Volume { get; set; } = 1.0f;
        public float Pan { get; set; } = 0.0f;
        public bool Mute { get; set; }
        public bool Solo { get; set; }

        /// <summary>Matches AudioEngineInterop.TrackInstrumentMode.</summary>
        public int InstrumentMode { get; set; }
        public string? SoundFontPath { get; set; }
        public int SoundFontPresetIndex { get; set; } = -1;
        public string? VstPath { get; set; }
    }

    public sealed class PatternData
    {
        public string Name { get; set; } = "";
        public double LengthBeats { get; set; } = 4.0;
        public List<NoteData> Notes { get; set; } = new();
    }

    public sealed class CustomRhythmDrumHitData
    {
        /// <summary>Matches AudioEngineInterop.CustomDrumVoice.</summary>
        public int Voice { get; set; }
        public double StartBeat { get; set; }
        public int Velocity { get; set; } = 100;
    }

    public sealed class CustomRhythmMelodicHitData
    {
        /// <summary>Matches AudioEngineInterop.AccompanimentMelodicLayer.</summary>
        public int Layer { get; set; }
        public double StartBeat { get; set; }
        public double LengthBeats { get; set; } = 1.0;
        public bool Root { get; set; } = true;
        public bool Third { get; set; } = true;
        public bool Fifth { get; set; } = true;
        public bool Seventh { get; set; }
        public int OctaveOffset { get; set; }
        public int Velocity { get; set; } = 90;
    }

    public sealed class CustomRhythmBarData
    {
        public double LengthBeats { get; set; } = 4.0;
        public List<CustomRhythmDrumHitData> DrumHits { get; set; } = new();
        public List<CustomRhythmMelodicHitData> MelodicHits { get; set; } = new();
    }

    /// <summary>One custom rhythm saved in "Sopstveni ritam" - see AccompanimentData.CustomRhythms.</summary>
    public sealed class CustomRhythmData
    {
        public string Name { get; set; } = "";
        public List<CustomRhythmBarData> Bars { get; set; } = new();
    }

    public sealed class LayerInstrumentData
    {
        /// <summary>Matches AudioEngineInterop.AccompanimentInstrumentMode.</summary>
        public int Mode { get; set; }
        public string? SoundFontPath { get; set; }
        public int SoundFontPresetIndex { get; set; } = -1;
        public string? VstPath { get; set; }
    }

    public sealed class AccompanimentData
    {
        public int SelectedStyleIndex { get; set; }
        public double TempoBpm { get; set; } = 100.0;
        public bool DrumsEnabled { get; set; } = true;
        public bool BassEnabled { get; set; } = true;
        public bool KontraEnabled { get; set; } = true;
        public bool HarmonijaEnabled { get; set; } = true;
        public bool ChordInputAutoFromKeyboard { get; set; } = true;
        public int SplitPoint { get; set; } = 60;

        /// <summary>Matches AudioEngineInterop.ChordQuality.</summary>
        public int ManualChordQuality { get; set; }
        public int ManualChordRootPitchClass { get; set; }

        /// <summary>Matches AudioEngineInterop.AccompanimentChordVoicing.</summary>
        public int ChordVoicing { get; set; }

        public LayerInstrumentData Bass { get; set; } = new();
        public LayerInstrumentData Kontra { get; set; } = new();
        public LayerInstrumentData Harmonija { get; set; } = new();

        /// <summary>
        /// Every custom rhythm saved so far in "Sopstveni ritam" - any
        /// number can coexist (see AccompanimentEngine::SetCustomStyle).
        /// Each one is saved as its editable bar-by-bar breakdown (not just
        /// the flattened style installed into the engine), so it can still
        /// be reopened for further editing later. Empty if none were saved.
        /// </summary>
        public List<CustomRhythmData> CustomRhythms { get; set; } = new();
    }

    public sealed class AutoThirdData
    {
        public bool Enabled { get; set; }
        public bool Upper { get; set; } = true;
        public int ScaleRootPitchClass { get; set; }

        /// <summary>Matches AudioEngineInterop.ScaleType (0=Major, 1=NaturalMinor, 2=Hijaz, 3=HijazKar). Replaces the old MinorScale bool - an older .adem file that still has "MinorScale" just opens as Major (the safe default), same as any other field it predates.</summary>
        public int ScaleType { get; set; }
    }

    /// <summary>The shared instrument used for live keyboard/MIDI playing in "Banka instrumenata".</summary>
    public sealed class MainBankData
    {
        public string? SoundFontPath { get; set; }
        public int SoundFontPresetIndex { get; set; } = -1;
        public string? VstPath { get; set; }
    }

    public sealed class ProjectData
    {
        /// <summary>Bumped only if a future change makes an old .adem file ambiguous to load; every field here already has a safe default, so older files still open.</summary>
        public int FormatVersion { get; set; } = 1;

        public List<NoteData> SequencerNotes { get; set; } = new();
        public double SequencerTempoBpm { get; set; } = 120.0;
        public double SequencerLoopLengthBeats { get; set; } = 16.0;

        public List<TrackData> Tracks { get; set; } = new();
        public double MultiTrackTempoBpm { get; set; } = 120.0;
        public double MultiTrackLoopLengthBeats { get; set; } = 16.0;

        public List<PatternData> ArrangerPatterns { get; set; } = new();
        public List<int> ArrangerOrder { get; set; } = new();
        public double ArrangerTempoBpm { get; set; } = 120.0;

        public AccompanimentData Accompaniment { get; set; } = new();
        public AutoThirdData AutoThird { get; set; } = new();
        public MainBankData MainBank { get; set; } = new();
    }
}
