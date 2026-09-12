using System.Runtime.InteropServices;
using System.Text;

namespace UltraComposer.Interop;

/// <summary>
/// Thin P/Invoke bridge to the native audio engine (UltraComposer.AudioEngine.dll).
/// Every call here is expected to return quickly - the actual audio processing
/// happens on PortAudio's own real-time thread, never on the caller's thread,
/// so calling these from the UI thread is safe.
/// </summary>
public static class AudioEngineInterop
{
    private const string NativeLibrary = "UltraComposer.AudioEngine";

    [DllImport(NativeLibrary, EntryPoint = "UC_Init")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool Init();

    [DllImport(NativeLibrary, EntryPoint = "UC_Shutdown")]
    public static extern void Shutdown();

    [DllImport(NativeLibrary, EntryPoint = "UC_Start")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool Start();

    [DllImport(NativeLibrary, EntryPoint = "UC_Stop")]
    public static extern void Stop();

    [DllImport(NativeLibrary, EntryPoint = "UC_NoteOn")]
    public static extern void NoteOn(int midiNote, float velocity);

    [DllImport(NativeLibrary, EntryPoint = "UC_NoteOff")]
    public static extern void NoteOff(int midiNote);

    // --- Chord capture ("Slušaj akord" / chord-by-shape entry) ---
    // While armed, the native engine watches every LIVE note (computer
    // keyboard via NoteOn/NoteOff above, and MIDI hardware - never
    // sequencer/arranger/multi-track playback, which doesn't route through
    // those) and remembers the largest set of notes held down together since
    // the last time nothing was held. The moment every one of those notes is
    // released again, that peak set becomes the pending "captured chord",
    // picked up once via TryTakeCapturedChord - lets someone play a chord
    // (or a single note) by shape instead of typing each note name.

    [DllImport(NativeLibrary, EntryPoint = "UC_SetChordCaptureArmed")]
    public static extern void SetChordCaptureArmed([MarshalAs(UnmanagedType.I1)] bool armed);

    [DllImport(NativeLibrary, EntryPoint = "UC_IsChordCaptureArmed")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool IsChordCaptureArmed();

    [DllImport(NativeLibrary, EntryPoint = "UC_TryTakeCapturedChord")]
    private static extern int TryTakeCapturedChordNative([Out] int[] notesOut, [Out] float[] velocitiesOut, int maxNotes);

    /// <summary>
    /// Polls for a chord captured since arming. Returns null if none is
    /// pending yet (nothing has been played and fully released since the
    /// last call); otherwise the captured notes as (Pitch, Velocity 1-127)
    /// pairs, in the order they were first pressed. Each call consumes the
    /// pending capture, so this is meant to be polled on a timer while armed
    /// (see MainWindow's chord-capture timer).
    /// </summary>
    public static List<(int Pitch, int Velocity)>? TryTakeCapturedChord()
    {
        const int maxNotes = 32;
        var notes = new int[maxNotes];
        var velocities = new float[maxNotes];
        int count = TryTakeCapturedChordNative(notes, velocities, maxNotes);
        if (count <= 0)
        {
            return null;
        }

        int actual = Math.Min(count, maxNotes);
        var result = new List<(int, int)>(actual);
        for (int i = 0; i < actual; i++)
        {
            int velocity = (int)Math.Round(velocities[i] * 127f);
            if (velocity < 1) velocity = 1;
            else if (velocity > 127) velocity = 127;
            result.Add((notes[i], velocity));
        }

        return result;
    }

    // --- MIDI hardware input (e.g. a USB-MIDI keyboard like the Yamaha P-125) ---

    [DllImport(NativeLibrary, EntryPoint = "UC_GetMidiPortCount")]
    public static extern int GetMidiPortCount();

    [DllImport(NativeLibrary, EntryPoint = "UC_GetMidiPortName", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool GetMidiPortNameNative(int index, StringBuilder buffer, int bufferSize);

    /// <summary>Name of the MIDI port at <paramref name="index"/>, or null if the index is invalid.</summary>
    public static string? GetMidiPortName(int index)
    {
        var buffer = new StringBuilder(256);
        return GetMidiPortNameNative(index, buffer, buffer.Capacity) ? buffer.ToString() : null;
    }

    [DllImport(NativeLibrary, EntryPoint = "UC_OpenMidiPort")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool OpenMidiPort(int index);

    [DllImport(NativeLibrary, EntryPoint = "UC_CloseMidiPort")]
    public static extern void CloseMidiPort();

    [DllImport(NativeLibrary, EntryPoint = "UC_IsMidiPortOpen")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool IsMidiPortOpen();

    // --- VST3 instrument hosting ---

    [DllImport(NativeLibrary, EntryPoint = "UC_LoadVstInstrument", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool LoadVstInstrumentNative(string modulePath, StringBuilder errorBuffer, int errorBufferSize);

    /// <summary>
    /// Loads the first instrument found inside the .vst3 module at <paramref name="modulePath"/>.
    /// Returns null on success, or a human-readable error message on failure.
    /// </summary>
    public static string? LoadVstInstrument(string modulePath)
    {
        var errorBuffer = new StringBuilder(512);
        bool ok = LoadVstInstrumentNative(modulePath, errorBuffer, errorBuffer.Capacity);
        return ok ? null : errorBuffer.ToString();
    }

    [DllImport(NativeLibrary, EntryPoint = "UC_UnloadVstInstrument")]
    public static extern void UnloadVstInstrument();

    [DllImport(NativeLibrary, EntryPoint = "UC_IsVstInstrumentLoaded")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool IsVstInstrumentLoaded();

    [DllImport(NativeLibrary, EntryPoint = "UC_GetVstInstrumentName", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool GetVstInstrumentNameNative(StringBuilder buffer, int bufferSize);

    /// <summary>Display name of the currently loaded VST3 instrument, or null if none is loaded.</summary>
    public static string? GetVstInstrumentName()
    {
        var buffer = new StringBuilder(256);
        return GetVstInstrumentNameNative(buffer, buffer.Capacity) ? buffer.ToString() : null;
    }

    // --- Simple single-track sequencer ---

    /// <summary>
    /// One note in the sequencer's pattern. Layout must match the native
    /// UC_SequencedNote struct exactly (AudioEngineApi.h): two doubles,
    /// then a 32-bit int, then a float, in that order.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SequencedNote
    {
        public double StartBeat;
        public double LengthBeats;
        public int Pitch;
        public float Velocity;
    }

    [DllImport(NativeLibrary, EntryPoint = "UC_Sequencer_SetNotes")]
    private static extern void SetSequencerNotesNative(SequencedNote[] notes, int count);

    /// <summary>Replaces the whole sequencer pattern. Safe to call while playing.</summary>
    public static void SetSequencerNotes(SequencedNote[] notes)
    {
        SetSequencerNotesNative(notes, notes.Length);
    }

    [DllImport(NativeLibrary, EntryPoint = "UC_Sequencer_SetTempoBpm")]
    public static extern void SetSequencerTempoBpm(double bpm);

    [DllImport(NativeLibrary, EntryPoint = "UC_Sequencer_SetLoopLengthBeats")]
    public static extern void SetSequencerLoopLengthBeats(double beats);

    [DllImport(NativeLibrary, EntryPoint = "UC_Sequencer_Play")]
    public static extern void PlaySequencer();

    [DllImport(NativeLibrary, EntryPoint = "UC_Sequencer_Stop")]
    public static extern void StopSequencer();

    [DllImport(NativeLibrary, EntryPoint = "UC_Sequencer_IsPlaying")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool IsSequencerPlaying();

    // --- SoundFont (.sf2) instrument banks ---

    [DllImport(NativeLibrary, EntryPoint = "UC_LoadSoundFontBank", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool LoadSoundFontBankNative(string sf2Path, StringBuilder errorBuffer, int errorBufferSize);

    /// <summary>
    /// Loads the SoundFont bank at <paramref name="sf2Path"/>. Returns null
    /// on success, or a human-readable error message on failure.
    /// </summary>
    public static string? LoadSoundFontBank(string sf2Path)
    {
        var errorBuffer = new StringBuilder(512);
        bool ok = LoadSoundFontBankNative(sf2Path, errorBuffer, errorBuffer.Capacity);
        return ok ? null : errorBuffer.ToString();
    }

    [DllImport(NativeLibrary, EntryPoint = "UC_UnloadSoundFontBank")]
    public static extern void UnloadSoundFontBank();

    [DllImport(NativeLibrary, EntryPoint = "UC_IsSoundFontBankLoaded")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool IsSoundFontBankLoaded();

    [DllImport(NativeLibrary, EntryPoint = "UC_GetSoundFontBankName", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool GetSoundFontBankNameNative(StringBuilder buffer, int bufferSize);

    /// <summary>File name of the loaded SoundFont bank, or null if none is loaded.</summary>
    public static string? GetSoundFontBankName()
    {
        var buffer = new StringBuilder(256);
        return GetSoundFontBankNameNative(buffer, buffer.Capacity) ? buffer.ToString() : null;
    }

    [DllImport(NativeLibrary, EntryPoint = "UC_GetSoundFontPresetCount")]
    public static extern int GetSoundFontPresetCount();

    [DllImport(NativeLibrary, EntryPoint = "UC_GetSoundFontPresetName", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool GetSoundFontPresetNameNative(int presetIndex, StringBuilder buffer, int bufferSize);

    /// <summary>Display name of the instrument at <paramref name="presetIndex"/>, or null if out of range.</summary>
    public static string? GetSoundFontPresetName(int presetIndex)
    {
        var buffer = new StringBuilder(256);
        return GetSoundFontPresetNameNative(presetIndex, buffer, buffer.Capacity) ? buffer.ToString() : null;
    }

    [DllImport(NativeLibrary, EntryPoint = "UC_SelectSoundFontPreset")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SelectSoundFontPreset(int presetIndex);

    [DllImport(NativeLibrary, EntryPoint = "UC_GetSelectedSoundFontPresetIndex")]
    public static extern int GetSelectedSoundFontPresetIndex();

    // --- Auto-third harmonization ---
    // While enabled, every note played anywhere (computer keyboard, MIDI
    // hardware, the sequencer, and the arranger) also triggers a second note
    // harmonized a diatonic third above or below it, against whatever scale
    // is set via SetAutoThirdScale. Off by default.

    [DllImport(NativeLibrary, EntryPoint = "UC_SetAutoThirdEnabled")]
    public static extern void SetAutoThirdEnabled([MarshalAs(UnmanagedType.I1)] bool enabled);

    [DllImport(NativeLibrary, EntryPoint = "UC_IsAutoThirdEnabled")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool IsAutoThirdEnabled();

    /// <summary>true = harmonize with the third above, false = the third below.</summary>
    [DllImport(NativeLibrary, EntryPoint = "UC_SetAutoThirdUpper")]
    public static extern void SetAutoThirdUpper([MarshalAs(UnmanagedType.I1)] bool upper);

    [DllImport(NativeLibrary, EntryPoint = "UC_IsAutoThirdUpper")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool IsAutoThirdUpper();

    /// <summary>
    /// The scale auto-third resolves its diatonic interval against - major,
    /// natural minor, or one of two "oriental"-flavored scales (Hijaz/Hijaz
    /// Kar, still plain 12-tone-equal-temperament, no quarter-tones) widely
    /// used in Balkan/Turkish/Arabic music. Matches the native UC_ScaleType
    /// enum.
    /// </summary>
    public enum ScaleType
    {
        Major = 0,
        NaturalMinor = 1,
        Hijaz = 2,
        HijazKar = 3,
    }

    /// <summary>
    /// <paramref name="rootPitchClass"/> is 0-11 (0=C, 1=C#/Db, ... 11=B).
    /// Defaults to C major. A note outside the selected scale still gets a
    /// plain fixed major third, since there's no single correct diatonic
    /// answer for a chromatic/"blue" note.
    /// </summary>
    [DllImport(NativeLibrary, EntryPoint = "UC_SetAutoThirdScale")]
    public static extern void SetAutoThirdScale(int rootPitchClass, ScaleType scaleType);

    [DllImport(NativeLibrary, EntryPoint = "UC_GetAutoThirdScaleRoot")]
    public static extern int GetAutoThirdScaleRoot();

    [DllImport(NativeLibrary, EntryPoint = "UC_GetAutoThirdScaleType")]
    public static extern ScaleType GetAutoThirdScaleType();

    // --- Arranger: chains multiple named patterns into one ordered playback
    // sequence. A pattern is a note list (same SequencedNote shape the plain
    // sequencer uses) plus a length in beats; the "order" is a list of
    // pattern indices, played back to back, looping the whole arrangement
    // once it reaches the end. Playing the arranger stops the plain
    // sequencer, and vice versa - both feed the same instruments. ---

    [DllImport(NativeLibrary, EntryPoint = "UC_Arranger_AddPattern", CharSet = CharSet.Ansi)]
    private static extern int ArrangerAddPatternNative(string name, double lengthBeats, SequencedNote[] notes, int count);

    /// <summary>
    /// Adds a new named pattern (a snapshot of notes, typically taken from
    /// the plain sequencer) and returns its index.
    /// </summary>
    public static int ArrangerAddPattern(string name, double lengthBeats, SequencedNote[] notes)
    {
        return ArrangerAddPatternNative(name, lengthBeats, notes, notes.Length);
    }

    [DllImport(NativeLibrary, EntryPoint = "UC_Arranger_RemovePattern")]
    public static extern void ArrangerRemovePattern(int patternIndex);

    [DllImport(NativeLibrary, EntryPoint = "UC_Arranger_GetPatternCount")]
    public static extern int ArrangerGetPatternCount();

    [DllImport(NativeLibrary, EntryPoint = "UC_Arranger_GetPatternName", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool ArrangerGetPatternNameNative(int patternIndex, StringBuilder buffer, int bufferSize);

    /// <summary>Name of the pattern at <paramref name="patternIndex"/>, or null if out of range.</summary>
    public static string? ArrangerGetPatternName(int patternIndex)
    {
        var buffer = new StringBuilder(256);
        return ArrangerGetPatternNameNative(patternIndex, buffer, buffer.Capacity) ? buffer.ToString() : null;
    }

    [DllImport(NativeLibrary, EntryPoint = "UC_Arranger_GetPatternLengthBeats")]
    public static extern double ArrangerGetPatternLengthBeats(int patternIndex);

    [DllImport(NativeLibrary, EntryPoint = "UC_Arranger_GetPatternNoteCount")]
    public static extern int ArrangerGetPatternNoteCount(int patternIndex);

    [DllImport(NativeLibrary, EntryPoint = "UC_Arranger_GetPatternNotes")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool ArrangerGetPatternNotesNative(int patternIndex, [Out] SequencedNote[] outNotes, int maxCount);

    /// <summary>
    /// The pattern's own note data (for project save - see .adem format in
    /// ProjectFile.cs). Returns an empty array if the index is out of range
    /// or the pattern genuinely has no notes.
    /// </summary>
    public static SequencedNote[] ArrangerGetPatternNotes(int patternIndex)
    {
        int count = ArrangerGetPatternNoteCount(patternIndex);
        if (count <= 0)
        {
            return Array.Empty<SequencedNote>();
        }

        var notes = new SequencedNote[count];
        return ArrangerGetPatternNotesNative(patternIndex, notes, count) ? notes : Array.Empty<SequencedNote>();
    }

    [DllImport(NativeLibrary, EntryPoint = "UC_Arranger_AppendToOrder")]
    public static extern void ArrangerAppendToOrder(int patternIndex);

    [DllImport(NativeLibrary, EntryPoint = "UC_Arranger_RemoveLastFromOrder")]
    public static extern void ArrangerRemoveLastFromOrder();

    [DllImport(NativeLibrary, EntryPoint = "UC_Arranger_ClearOrder")]
    public static extern void ArrangerClearOrder();

    [DllImport(NativeLibrary, EntryPoint = "UC_Arranger_GetOrderCount")]
    public static extern int ArrangerGetOrderCount();

    [DllImport(NativeLibrary, EntryPoint = "UC_Arranger_GetOrderPatternIndexAt")]
    public static extern int ArrangerGetOrderPatternIndexAt(int orderPosition);

    [DllImport(NativeLibrary, EntryPoint = "UC_Arranger_SetTempoBpm")]
    public static extern void ArrangerSetTempoBpm(double bpm);

    [DllImport(NativeLibrary, EntryPoint = "UC_Arranger_GetTempoBpm")]
    public static extern double ArrangerGetTempoBpm();

    [DllImport(NativeLibrary, EntryPoint = "UC_Arranger_Play")]
    public static extern void PlayArranger();

    [DllImport(NativeLibrary, EntryPoint = "UC_Arranger_Stop")]
    public static extern void StopArranger();

    [DllImport(NativeLibrary, EntryPoint = "UC_Arranger_IsPlaying")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool IsArrangerPlaying();

    [DllImport(NativeLibrary, EntryPoint = "UC_Arranger_GetCurrentOrderPosition")]
    public static extern int ArrangerGetCurrentOrderPosition();

    // --- Multi-track sequencing + mixer ---
    // Several simultaneous tracks, each with its own note pattern (same
    // SequencedNote shape as the plain sequencer/arranger) and its own
    // instrument - either the built-in synth, or a SoundFont bank/preset
    // loaded just for that track - mixed together with per-track
    // volume/pan/mute/solo. All tracks share one tempo and one loop length.
    // Playing this stops the plain sequencer and the arranger, and vice
    // versa.

    /// <summary>Instrument mode for a track. Matches the native UC_TrackInstrumentMode enum.</summary>
    public enum TrackInstrumentMode
    {
        BuiltInSynth = 0,
        SoundFont = 1,
        Vst = 2,
    }

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_AddTrack", CharSet = CharSet.Ansi)]
    public static extern int MultiTrackAddTrack(string name);

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_RemoveTrack")]
    public static extern void MultiTrackRemoveTrack(int trackIndex);

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_GetTrackCount")]
    public static extern int MultiTrackGetTrackCount();

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_GetTrackName", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool MultiTrackGetTrackNameNative(int trackIndex, StringBuilder buffer, int bufferSize);

    /// <summary>Name of the track at <paramref name="trackIndex"/>, or null if out of range.</summary>
    public static string? MultiTrackGetTrackName(int trackIndex)
    {
        var buffer = new StringBuilder(256);
        return MultiTrackGetTrackNameNative(trackIndex, buffer, buffer.Capacity) ? buffer.ToString() : null;
    }

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_SetTrackName", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool MultiTrackSetTrackName(int trackIndex, string name);

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_SetTrackNotes")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool MultiTrackSetTrackNotesNative(int trackIndex, SequencedNote[] notes, int count);

    /// <summary>Replaces the whole note pattern for the track at <paramref name="trackIndex"/>. Safe to call while playing.</summary>
    public static bool MultiTrackSetTrackNotes(int trackIndex, SequencedNote[] notes)
    {
        return MultiTrackSetTrackNotesNative(trackIndex, notes, notes.Length);
    }

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_SetTrackVolume")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool MultiTrackSetTrackVolume(int trackIndex, float volume);

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_GetTrackVolume")]
    public static extern float MultiTrackGetTrackVolume(int trackIndex);

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_SetTrackPan")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool MultiTrackSetTrackPan(int trackIndex, float pan);

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_GetTrackPan")]
    public static extern float MultiTrackGetTrackPan(int trackIndex);

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_SetTrackMute")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool MultiTrackSetTrackMute(int trackIndex, [MarshalAs(UnmanagedType.I1)] bool mute);

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_IsTrackMuted")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool MultiTrackIsTrackMuted(int trackIndex);

    /// <summary>
    /// While any track is soloed, only soloed tracks are audible (their own
    /// mute is ignored); with no track soloed, every unmuted track plays.
    /// </summary>
    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_SetTrackSolo")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool MultiTrackSetTrackSolo(int trackIndex, [MarshalAs(UnmanagedType.I1)] bool solo);

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_IsTrackSoloed")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool MultiTrackIsTrackSoloed(int trackIndex);

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_SetTrackInstrumentMode")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool MultiTrackSetTrackInstrumentMode(int trackIndex, TrackInstrumentMode mode);

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_GetTrackInstrumentMode")]
    public static extern TrackInstrumentMode MultiTrackGetTrackInstrumentMode(int trackIndex);

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_LoadTrackSoundFontBank", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool MultiTrackLoadTrackSoundFontBankNative(int trackIndex, string sf2Path, StringBuilder errorBuffer, int errorBufferSize);

    /// <summary>
    /// Loads a SoundFont bank just for this track (independent of any other
    /// track's bank, even if it's the same file) and switches the track to
    /// SoundFont mode. Returns null on success, or a human-readable error
    /// message on failure.
    /// </summary>
    public static string? MultiTrackLoadTrackSoundFontBank(int trackIndex, string sf2Path)
    {
        var errorBuffer = new StringBuilder(512);
        bool ok = MultiTrackLoadTrackSoundFontBankNative(trackIndex, sf2Path, errorBuffer, errorBuffer.Capacity);
        return ok ? null : errorBuffer.ToString();
    }

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_UnloadTrackSoundFontBank")]
    public static extern void MultiTrackUnloadTrackSoundFontBank(int trackIndex);

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_IsTrackSoundFontBankLoaded")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool MultiTrackIsTrackSoundFontBankLoaded(int trackIndex);

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_GetTrackSoundFontBankName", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool MultiTrackGetTrackSoundFontBankNameNative(int trackIndex, StringBuilder buffer, int bufferSize);

    /// <summary>File name of the track's loaded SoundFont bank, or null if none is loaded.</summary>
    public static string? MultiTrackGetTrackSoundFontBankName(int trackIndex)
    {
        var buffer = new StringBuilder(256);
        return MultiTrackGetTrackSoundFontBankNameNative(trackIndex, buffer, buffer.Capacity) ? buffer.ToString() : null;
    }

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_GetTrackSoundFontPresetCount")]
    public static extern int MultiTrackGetTrackSoundFontPresetCount(int trackIndex);

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_GetTrackSoundFontPresetName", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool MultiTrackGetTrackSoundFontPresetNameNative(int trackIndex, int presetIndex, StringBuilder buffer, int bufferSize);

    /// <summary>Display name of the track's bank instrument at <paramref name="presetIndex"/>, or null if out of range.</summary>
    public static string? MultiTrackGetTrackSoundFontPresetName(int trackIndex, int presetIndex)
    {
        var buffer = new StringBuilder(256);
        return MultiTrackGetTrackSoundFontPresetNameNative(trackIndex, presetIndex, buffer, buffer.Capacity) ? buffer.ToString() : null;
    }

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_SelectTrackSoundFontPreset")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool MultiTrackSelectTrackSoundFontPreset(int trackIndex, int presetIndex);

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_GetSelectedTrackSoundFontPresetIndex")]
    public static extern int MultiTrackGetSelectedTrackSoundFontPresetIndex(int trackIndex);

    // Same idea as the per-track SoundFont methods above, but a VST3
    // plug-in - each track hosts its own independent plug-in instance.

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_LoadTrackVstInstrument", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool MultiTrackLoadTrackVstInstrumentNative(int trackIndex, string modulePath, StringBuilder errorBuffer, int errorBufferSize);

    /// <summary>
    /// Loads a VST3 instrument just for this track (independent of any
    /// other track's plug-in, even if it's the same file) and switches the
    /// track to Vst mode. Returns null on success, or a human-readable
    /// error message on failure.
    /// </summary>
    public static string? MultiTrackLoadTrackVstInstrument(int trackIndex, string modulePath)
    {
        var errorBuffer = new StringBuilder(512);
        bool ok = MultiTrackLoadTrackVstInstrumentNative(trackIndex, modulePath, errorBuffer, errorBuffer.Capacity);
        return ok ? null : errorBuffer.ToString();
    }

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_UnloadTrackVstInstrument")]
    public static extern void MultiTrackUnloadTrackVstInstrument(int trackIndex);

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_IsTrackVstInstrumentLoaded")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool MultiTrackIsTrackVstInstrumentLoaded(int trackIndex);

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_GetTrackVstInstrumentName", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool MultiTrackGetTrackVstInstrumentNameNative(int trackIndex, StringBuilder buffer, int bufferSize);

    /// <summary>Display name of the track's loaded VST3 instrument, or null if none is loaded.</summary>
    public static string? MultiTrackGetTrackVstInstrumentName(int trackIndex)
    {
        var buffer = new StringBuilder(256);
        return MultiTrackGetTrackVstInstrumentNameNative(trackIndex, buffer, buffer.Capacity) ? buffer.ToString() : null;
    }

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_SetTempoBpm")]
    public static extern void MultiTrackSetTempoBpm(double bpm);

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_GetTempoBpm")]
    public static extern double MultiTrackGetTempoBpm();

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_SetLoopLengthBeats")]
    public static extern void MultiTrackSetLoopLengthBeats(double beats);

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_GetLoopLengthBeats")]
    public static extern double MultiTrackGetLoopLengthBeats();

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_Play")]
    public static extern void PlayMultiTrack();

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_Stop")]
    public static extern void StopMultiTrack();

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_IsPlaying")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool IsMultiTrackPlaying();

    [DllImport(NativeLibrary, EntryPoint = "UC_MultiTrack_RenderToWav", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool MultiTrackRenderToWavNative(string outPath, double durationSeconds, StringBuilder errorBuffer, int errorBufferSize);

    /// <summary>
    /// Renders the current multi-track mix offline for <paramref name="durationSeconds"/>
    /// and writes it as a 16-bit PCM stereo .wav file at <paramref name="outPath"/> -
    /// meant for bringing a finished composition into Ultra Audio Editor for
    /// further mixing. Returns null on success, or a human-readable error
    /// message on failure.
    /// </summary>
    public static string? MultiTrackRenderToWav(string outPath, double durationSeconds)
    {
        var errorBuffer = new StringBuilder(512);
        bool ok = MultiTrackRenderToWavNative(outPath, durationSeconds, errorBuffer, errorBuffer.Capacity);
        return ok ? null : errorBuffer.ToString();
    }

    // --- Auto-accompaniment ---
    // A built-in "style" (drum pattern + bass/kontra/harmonija layers) that
    // automatically follows whatever chord is currently active, like a home
    // keyboard's auto-accompaniment. Runs independently of the sequencer/
    // arranger/multi-track transports above.

    /// <summary>Which harmonic layer a Set/IsAccompanimentLayerEnabled call refers to. Matches the native UC_AccompanimentLayer enum.</summary>
    public enum AccompanimentLayer
    {
        Drums = 0,
        Bass = 1,
        Kontra = 2,
        Harmonija = 3,
    }

    /// <summary>The small, fixed chord-quality vocabulary the built-in styles and the manual chord picker use. Matches the native UC_ChordQuality enum.</summary>
    public enum ChordQuality
    {
        Major = 0,
        Minor = 1,
        Dominant7 = 2,
        Diminished = 3,
    }

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_GetStyleCount")]
    public static extern int AccompanimentGetStyleCount();

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_GetStyleName", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AccompanimentGetStyleNameNative(int index, StringBuilder buffer, int bufferSize);

    public static string? AccompanimentGetStyleName(int index)
    {
        var buffer = new StringBuilder(256);
        return AccompanimentGetStyleNameNative(index, buffer, buffer.Capacity) ? buffer.ToString() : null;
    }

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_SetStyleIndex")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool AccompanimentSetStyleIndex(int index);

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_GetSelectedStyleIndex")]
    public static extern int AccompanimentGetSelectedStyleIndex();

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_SetLayerEnabled")]
    public static extern void AccompanimentSetLayerEnabled(AccompanimentLayer layer, [MarshalAs(UnmanagedType.I1)] bool enabled);

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_IsLayerEnabled")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool AccompanimentIsLayerEnabled(AccompanimentLayer layer);

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_SetTempoBpm")]
    public static extern void AccompanimentSetTempoBpm(double bpm);

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_GetTempoBpm")]
    public static extern double AccompanimentGetTempoBpm();

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_Play")]
    public static extern void PlayAccompaniment();

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_Stop")]
    public static extern void StopAccompaniment();

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_IsPlaying")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool IsAccompanimentPlaying();

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_SetChordInputAutoFromKeyboard")]
    public static extern void AccompanimentSetChordInputAutoFromKeyboard([MarshalAs(UnmanagedType.I1)] bool autoFromKeyboard);

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_IsChordInputAutoFromKeyboard")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool IsAccompanimentChordInputAutoFromKeyboard();

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_SetSplitPoint")]
    public static extern void AccompanimentSetSplitPoint(int midiNote);

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_GetSplitPoint")]
    public static extern int AccompanimentGetSplitPoint();

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_SetManualChord")]
    public static extern void AccompanimentSetManualChord(int rootPitchClass, ChordQuality quality);

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_GetCurrentChordRootPitchClass")]
    public static extern int AccompanimentGetCurrentChordRootPitchClass();

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_GetCurrentChordQuality")]
    public static extern ChordQuality AccompanimentGetCurrentChordQuality();

    // --- Auto-accompaniment: per-layer instrument (SoundFont) ---
    // Each of bass/kontra/harmonija can sound through its own independently
    // loaded SoundFont bank/preset instead of the built-in synth - e.g. so
    // "harmonija" can sound like real strings from a loaded bank. Drums
    // aren't included (always procedural).

    /// <summary>Which melodic layer a *LayerSoundFont*/LayerInstrumentMode call refers to. Matches the native UC_AccompanimentMelodicLayer enum.</summary>
    public enum AccompanimentMelodicLayer
    {
        Bass = 0,
        Kontra = 1,
        Harmonija = 2,
    }

    /// <summary>Whether a melodic layer sounds through the built-in synth, its own loaded SoundFont bank, or its own loaded VST3 instrument. Matches the native UC_AccompanimentInstrumentMode enum.</summary>
    public enum AccompanimentInstrumentMode
    {
        BuiltInSynth = 0,
        SoundFont = 1,
        Vst = 2,
    }

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_LoadLayerSoundFontBank", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AccompanimentLoadLayerSoundFontBankNative(AccompanimentMelodicLayer layer, string sf2Path, StringBuilder errorBuffer, int errorBufferSize);

    /// <summary>
    /// Loads a SoundFont bank just for this layer (independent of any other
    /// layer's bank, and of the main shared SoundFont bank used for live
    /// playing, even if it's the same file) and switches the layer to
    /// SoundFont mode. Returns null on success, or a human-readable error
    /// message on failure.
    /// </summary>
    public static string? AccompanimentLoadLayerSoundFontBank(AccompanimentMelodicLayer layer, string sf2Path)
    {
        var errorBuffer = new StringBuilder(512);
        bool ok = AccompanimentLoadLayerSoundFontBankNative(layer, sf2Path, errorBuffer, errorBuffer.Capacity);
        return ok ? null : errorBuffer.ToString();
    }

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_UnloadLayerSoundFontBank")]
    public static extern void AccompanimentUnloadLayerSoundFontBank(AccompanimentMelodicLayer layer);

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_IsLayerSoundFontBankLoaded")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool AccompanimentIsLayerSoundFontBankLoaded(AccompanimentMelodicLayer layer);

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_GetLayerSoundFontBankName", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AccompanimentGetLayerSoundFontBankNameNative(AccompanimentMelodicLayer layer, StringBuilder buffer, int bufferSize);

    /// <summary>File name of this layer's loaded SoundFont bank, or null if none is loaded.</summary>
    public static string? AccompanimentGetLayerSoundFontBankName(AccompanimentMelodicLayer layer)
    {
        var buffer = new StringBuilder(256);
        return AccompanimentGetLayerSoundFontBankNameNative(layer, buffer, buffer.Capacity) ? buffer.ToString() : null;
    }

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_GetLayerSoundFontPresetCount")]
    public static extern int AccompanimentGetLayerSoundFontPresetCount(AccompanimentMelodicLayer layer);

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_GetLayerSoundFontPresetName", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AccompanimentGetLayerSoundFontPresetNameNative(AccompanimentMelodicLayer layer, int presetIndex, StringBuilder buffer, int bufferSize);

    /// <summary>Display name of this layer's bank instrument at <paramref name="presetIndex"/>, or null if out of range.</summary>
    public static string? AccompanimentGetLayerSoundFontPresetName(AccompanimentMelodicLayer layer, int presetIndex)
    {
        var buffer = new StringBuilder(256);
        return AccompanimentGetLayerSoundFontPresetNameNative(layer, presetIndex, buffer, buffer.Capacity) ? buffer.ToString() : null;
    }

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_SelectLayerSoundFontPreset")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool AccompanimentSelectLayerSoundFontPreset(AccompanimentMelodicLayer layer, int presetIndex);

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_GetSelectedLayerSoundFontPresetIndex")]
    public static extern int AccompanimentGetSelectedLayerSoundFontPresetIndex(AccompanimentMelodicLayer layer);

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_SetLayerInstrumentMode")]
    public static extern void AccompanimentSetLayerInstrumentMode(AccompanimentMelodicLayer layer, AccompanimentInstrumentMode mode);

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_GetLayerInstrumentMode")]
    public static extern AccompanimentInstrumentMode AccompanimentGetLayerInstrumentMode(AccompanimentMelodicLayer layer);

    // --- Auto-accompaniment: per-layer instrument (VST3) ---
    // Same idea as the per-layer SoundFont functions above, for a VST3
    // instrument instead of a SoundFont bank.

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_LoadLayerVstInstrument", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AccompanimentLoadLayerVstInstrumentNative(AccompanimentMelodicLayer layer, string modulePath, StringBuilder errorBuffer, int errorBufferSize);

    /// <summary>
    /// Loads a VST3 instrument just for this layer (independent of any
    /// other layer's plug-in, and of the main shared VST3 instrument used
    /// for live playing, even if it's the same file) and switches the layer
    /// to Vst mode. Returns null on success, or a human-readable error
    /// message on failure.
    /// </summary>
    public static string? AccompanimentLoadLayerVstInstrument(AccompanimentMelodicLayer layer, string modulePath)
    {
        var errorBuffer = new StringBuilder(512);
        bool ok = AccompanimentLoadLayerVstInstrumentNative(layer, modulePath, errorBuffer, errorBuffer.Capacity);
        return ok ? null : errorBuffer.ToString();
    }

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_UnloadLayerVstInstrument")]
    public static extern void AccompanimentUnloadLayerVstInstrument(AccompanimentMelodicLayer layer);

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_IsLayerVstInstrumentLoaded")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool AccompanimentIsLayerVstInstrumentLoaded(AccompanimentMelodicLayer layer);

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_GetLayerVstInstrumentName", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AccompanimentGetLayerVstInstrumentNameNative(AccompanimentMelodicLayer layer, StringBuilder buffer, int bufferSize);

    /// <summary>Display name of this layer's loaded VST3 instrument, or null if none is loaded.</summary>
    public static string? AccompanimentGetLayerVstInstrumentName(AccompanimentMelodicLayer layer)
    {
        var buffer = new StringBuilder(256);
        return AccompanimentGetLayerVstInstrumentNameNative(layer, buffer, buffer.Capacity) ? buffer.ToString() : null;
    }

    /// <summary>
    /// How many chord tones the accompaniment's kontra/harmonija layers
    /// actually sound, overriding what the selected style authored per hit.
    /// Never affects bass. Matches the native UC_ChordVoicing enum.
    /// </summary>
    public enum AccompanimentChordVoicing
    {
        AsAuthored = 0,
        Triad = 1,
        Seventh = 2,
    }

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_SetChordVoicing")]
    public static extern void AccompanimentSetChordVoicing(AccompanimentChordVoicing voicing);

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_GetChordVoicing")]
    public static extern AccompanimentChordVoicing AccompanimentGetChordVoicing();

    // --- Custom rhythm/style ("Sopstveni ritam") ---
    // Lets the person build their own accompaniment style in the UI: one
    // bar of drum/bass/kontra/harmonija hits, with more bars addable
    // afterwards as variations. The UI (MainWindow) owns all of the
    // bar-by-bar editing and chaining and only calls
    // AccompanimentSetCustomStyle once, with everything already flattened.

    /// <summary>
    /// Same percussion voice set as the native DrumVoiceType/UC_DrumVoiceType
    /// used by the built-in style bank (see DrumSynth.h). Used by the
    /// custom rhythm editor's drum-hit list.
    /// </summary>
    public enum CustomDrumVoice
    {
        Kick = 0,
        Snare = 1,
        ClosedHat = 2,
        OpenHat = 3,
        Clap = 4,
        Crash = 5,
        Tom = 6,
        DumbekDum = 7,
        DumbekTek = 8,
    }

    /// <summary>
    /// One drum hit for a custom rhythm. Layout must match the native
    /// UC_CustomDrumHit struct exactly (AudioEngineApi.h): a double, then a
    /// 32-bit int, then a float, in that order.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CustomDrumHit
    {
        public double StartBeat;
        public int DrumVoice; // CustomDrumVoice
        public float Velocity;
    }

    /// <summary>
    /// One bass/kontra/harmonija hit for a custom rhythm. 'ChordToneMask'
    /// packs which chord tone(s) this hit sounds as bits 0-3 (bit 0=root,
    /// 1=third, 2=fifth, 3=seventh) - a bass hit should normally set just
    /// one bit (a single bass note); kontra/harmonija can set several for a
    /// chord stab or pad. Layout must match the native UC_CustomChordHit
    /// struct exactly: two doubles, then two 32-bit ints, then a float.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CustomChordHit
    {
        public double StartBeat;
        public double LengthBeats;
        public int ChordToneMask;
        public int OctaveOffset;
        public float Velocity;
    }

    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_SetCustomStyle", CharSet = CharSet.Ansi)]
    private static extern int AccompanimentSetCustomStyleNative(
        string name, double tempoBpm, double lengthBeats, double beatsPerBar,
        CustomDrumHit[] drumHits, int drumHitCount,
        CustomChordHit[] bassHits, int bassHitCount,
        CustomChordHit[] kontraHits, int kontraHitCount,
        CustomChordHit[] harmonijaHits, int harmonijaHitCount,
        int replaceIndex);

    /// <summary>
    /// Adds (or, if <paramref name="replaceIndex"/> names an existing custom
    /// style, edits in place) a custom, user-authored rhythm/style built via
    /// the "Sopstveni ritam" editor, and selects it immediately so it plays
    /// right away. Any number of custom styles can coexist - pass -1 for
    /// <paramref name="replaceIndex"/> to always add a new one alongside any
    /// already saved. Any hit array may be empty. Returns the resulting
    /// style's index (also usable later with AccompanimentSetStyleIndex/
    /// AccompanimentRemoveCustomStyle), or -1 if every array is empty.
    /// </summary>
    public static int AccompanimentSetCustomStyle(
        string name, double tempoBpm, double lengthBeats, double beatsPerBar,
        CustomDrumHit[] drumHits, CustomChordHit[] bassHits, CustomChordHit[] kontraHits, CustomChordHit[] harmonijaHits,
        int replaceIndex = -1)
    {
        return AccompanimentSetCustomStyleNative(
            name, tempoBpm, lengthBeats, beatsPerBar,
            drumHits, drumHits.Length,
            bassHits, bassHits.Length,
            kontraHits, kontraHits.Length,
            harmonijaHits, harmonijaHits.Length,
            replaceIndex);
    }

    /// <summary>
    /// Removes a previously-saved custom style (<paramref name="index"/>
    /// must be a custom style, not one of the built-in ones). Returns false
    /// if it isn't a valid custom style index.
    /// </summary>
    [DllImport(NativeLibrary, EntryPoint = "UC_Accompaniment_RemoveCustomStyle")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool AccompanimentRemoveCustomStyle(int index);
}
