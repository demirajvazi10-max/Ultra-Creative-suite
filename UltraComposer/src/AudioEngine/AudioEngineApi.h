#pragma once

#include <cstdint>

// Plain C ABI for the native audio engine. Kept deliberately simple (no
// C++ classes, no exceptions crossing the boundary) so the C# side can call
// it safely via P/Invoke, and so this boundary is easy to port to another
// host language later if needed.

#ifdef _WIN32
#define UC_API extern "C" __declspec(dllexport)
#else
#define UC_API extern "C"
#endif

// Initializes PortAudio and the synth engine. Call once at startup.
UC_API bool UC_Init();

// Shuts everything down. Call once, on exit.
UC_API void UC_Shutdown();

// Opens the default audio output device and starts the real-time callback.
UC_API bool UC_Start();

// Stops and closes the audio stream. Safe to call even if not started.
UC_API void UC_Stop();

// Starts a note (MIDI note number 0-127, velocity 0.0-1.0).
UC_API void UC_NoteOn(int midiNote, float velocity);

// Releases a note started with UC_NoteOn.
UC_API void UC_NoteOff(int midiNote);

// --- Chord capture ("Slušaj akord" / chord-by-shape entry) ---
// While armed, watches every LIVE note - UC_NoteOn/UC_NoteOff, which computer
// keyboard input and MIDI hardware input both funnel through, but sequencer/
// arranger/multi-track playback never does - and remembers the largest set
// of notes held down together since the last time nothing was held. The
// moment every one of those notes is released again, that peak set becomes
// the pending "captured chord", picked up once via UC_TryTakeCapturedChord.
// Meant to be polled on a UI timer while armed, so someone can play a chord
// by shape (or a single note) instead of typing each note name into a
// sequencer/multi-track note list.

// Arms or disarms capture. Arming (re-)starts from a clean slate - any notes
// already held at that moment are not counted until released and re-pressed -
// and clears any not-yet-collected pending capture.
UC_API void UC_SetChordCaptureArmed(bool armed);
UC_API bool UC_IsChordCaptureArmed();

// If a chord has been captured since arming (or since the last successful
// call here) and not yet collected, copies its notes into 'notesOut'/
// 'velocitiesOut' (each of length 'maxNotes', in the order the notes were
// first pressed; velocity 0.0-1.0) and returns the chord's true note count
// (which may exceed 'maxNotes' - only the first 'maxNotes' are copied).
// Returns 0 if nothing is pending yet. Consumes the pending capture either
// way a nonzero count is returned, so each captured chord is only ever
// reported once.
UC_API int32_t UC_TryTakeCapturedChord(int32_t* notesOut, float* velocitiesOut, int32_t maxNotes);

// --- MIDI hardware input (e.g. a USB-MIDI keyboard) ---

// Number of MIDI input ports currently visible to the system. Call again
// after plugging in a device - there is no hot-plug notification yet.
UC_API int UC_GetMidiPortCount();

// Copies the name of the MIDI port at 'index' (0-based, < UC_GetMidiPortCount())
// into 'buffer' (size 'bufferSize', ANSI, null-terminated). Returns false if
// the index is out of range or the arguments are invalid; the name is
// truncated (but still null-terminated) if the buffer is too small.
UC_API bool UC_GetMidiPortName(int index, char* buffer, int bufferSize);

// Opens the MIDI port at 'index' for input, closing any previously open port.
UC_API bool UC_OpenMidiPort(int index);

// Closes the currently open MIDI port, if any.
UC_API void UC_CloseMidiPort();

// True if a MIDI port is currently open.
UC_API bool UC_IsMidiPortOpen();

// --- VST3 instrument hosting ---

// Loads the first instrument found inside the .vst3 module at 'modulePath'
// (either a classic single-file .vst3 or the newer bundle-folder form).
// Replaces any previously loaded instrument. Returns false on failure and
// copies a human-readable reason into 'errorBuffer' (size 'errorBufferSize',
// ANSI, null-terminated, truncated if too small).
UC_API bool UC_LoadVstInstrument(const char* modulePath, char* errorBuffer, int errorBufferSize);

// Unloads the current VST3 instrument, if any. Safe to call even if none is loaded.
UC_API void UC_UnloadVstInstrument();

// True if a VST3 instrument is currently loaded.
UC_API bool UC_IsVstInstrumentLoaded();

// Copies the loaded instrument's display name into 'buffer' (size
// 'bufferSize', ANSI, null-terminated). Returns false if nothing is loaded
// or the arguments are invalid.
UC_API bool UC_GetVstInstrumentName(char* buffer, int bufferSize);

// --- Simple single-track sequencer ---

// One note in the sequencer's pattern. Times are in beats, not seconds -
// they automatically follow whatever tempo is set with
// UC_Sequencer_SetTempoBpm.
struct UC_SequencedNote
{
    double startBeat;
    double lengthBeats;
    int32_t pitch;
    float velocity;
};

// Replaces the whole pattern with 'notes' (an array of 'count' entries).
// Pass count=0 (notes may be null) to clear the pattern. Safe to call
// while playing - takes effect immediately.
UC_API void UC_Sequencer_SetNotes(const UC_SequencedNote* notes, int32_t count);

// Sets the playback tempo in beats per minute. Invalid (<= 0) values are
// ignored and 120 is used instead.
UC_API void UC_Sequencer_SetTempoBpm(double bpm);

// Sets how many beats the pattern loops after. Invalid (<= 0) values are
// ignored and 16 is used instead.
UC_API void UC_Sequencer_SetLoopLengthBeats(double beats);

// Starts playback from beat 0, looping forever until UC_Sequencer_Stop.
UC_API void UC_Sequencer_Play();

// Stops playback and releases any notes still sounding.
UC_API void UC_Sequencer_Stop();

// True if the sequencer is currently playing.
UC_API bool UC_Sequencer_IsPlaying();

// --- SoundFont (.sf2) instrument banks ---

// Loads the SoundFont bank at 'sf2Path' and selects its first/default
// instrument. Replaces any previously loaded bank. Returns false on
// failure and copies a human-readable reason into 'errorBuffer' (size
// 'errorBufferSize', ANSI, null-terminated, truncated if too small).
UC_API bool UC_LoadSoundFontBank(const char* sf2Path, char* errorBuffer, int errorBufferSize);

// Unloads the current SoundFont bank, if any. Safe to call even if none is loaded.
UC_API void UC_UnloadSoundFontBank();

// True if a SoundFont bank is currently loaded.
UC_API bool UC_IsSoundFontBankLoaded();

// Copies the loaded bank's file name into 'buffer' (size 'bufferSize',
// ANSI, null-terminated). Returns false if nothing is loaded.
UC_API bool UC_GetSoundFontBankName(char* buffer, int bufferSize);

// Number of instruments ("presets") in the loaded bank, or 0 if nothing is loaded.
UC_API int UC_GetSoundFontPresetCount();

// Copies the display name of the instrument at 'presetIndex' (0-based,
// < UC_GetSoundFontPresetCount()) into 'buffer' (size 'bufferSize', ANSI,
// null-terminated). Returns false if the index is out of range or nothing
// is loaded.
UC_API bool UC_GetSoundFontPresetName(int presetIndex, char* buffer, int bufferSize);

// Switches the loaded bank to play the instrument at 'presetIndex'.
// Returns false if the index is out of range or nothing is loaded.
UC_API bool UC_SelectSoundFontPreset(int presetIndex);

// Index of the currently selected instrument in the loaded bank.
UC_API int UC_GetSelectedSoundFontPresetIndex();

// --- Auto-third harmonization ---
// While enabled, every note played anywhere (computer keyboard, MIDI
// hardware, the sequencer, and the arranger) also triggers a second note
// harmonized a diatonic third above or below it, using whatever scale is
// set via UC_SetAutoThirdScale (a plain major/minor third fixed at 4/3
// semitones would be wrong for several scale degrees - e.g. in C major, the
// diatonic third above D is F, a MINOR third, not major). Off by default.

UC_API void UC_SetAutoThirdEnabled(bool enabled);
UC_API bool UC_IsAutoThirdEnabled();

// true = harmonize with the third above, false = the third below.
UC_API void UC_SetAutoThirdUpper(bool upper);
UC_API bool UC_IsAutoThirdUpper();

// The scale auto-third resolves its diatonic interval against - major,
// natural minor, or one of two "oriental"-flavored scales (Hijaz/Hijaz Kar,
// still plain 12-tone-equal-temperament, no quarter-tones) widely used in
// Balkan/Turkish/Arabic music. 'rootPitchClass' is 0-11 (0=C, 1=C#/Db, ...
// 11=B, same convention as UC_Accompaniment_SetManualChord's root).
// Defaults to C major. A note that isn't in the selected scale (a
// chromatic/"blue" note) still gets a plain fixed major third, since
// there's no single correct diatonic answer for it.
enum UC_ScaleType : int32_t
{
    UC_ScaleType_Major = 0,
    UC_ScaleType_NaturalMinor = 1,
    UC_ScaleType_Hijaz = 2,
    UC_ScaleType_HijazKar = 3,
};
UC_API void UC_SetAutoThirdScale(int32_t rootPitchClass, int32_t scaleType /* UC_ScaleType */);
UC_API int32_t UC_GetAutoThirdScaleRoot();
UC_API int32_t UC_GetAutoThirdScaleType(); // UC_ScaleType

// --- Arranger: chains multiple named patterns into one ordered playback
// sequence (see Arranger.h). A pattern is a note list (same UC_SequencedNote
// shape the plain sequencer uses) plus a length in beats; the "order" is a
// list of pattern indices, played back to back, looping the whole
// arrangement once it reaches the end. Playing the arranger stops the plain
// sequencer, and vice versa - both feed the same instruments. ---

// Adds a new named pattern and returns its index (0-based, in the order
// patterns were added), or copies 'notes' (an array of 'count' entries,
// which may be null/0 for an empty pattern) as its content. 'lengthBeats'
// must be > 0 (invalid values fall back to 4).
UC_API int32_t UC_Arranger_AddPattern(const char* name, double lengthBeats,
                                       const UC_SequencedNote* notes, int32_t count);

// Removes the pattern at 'patternIndex'. Any references to it in the
// current order are dropped, and references to later patterns are shifted
// down by one to keep matching their new indices.
UC_API void UC_Arranger_RemovePattern(int32_t patternIndex);

UC_API int32_t UC_Arranger_GetPatternCount();

// Copies the pattern's name into 'buffer' (size 'bufferSize', ANSI,
// null-terminated). Returns false if the index is out of range.
UC_API bool UC_Arranger_GetPatternName(int32_t patternIndex, char* buffer, int32_t bufferSize);

UC_API double UC_Arranger_GetPatternLengthBeats(int32_t patternIndex);

// For project save (.adem) - reads a pattern's own note data back out,
// since it's otherwise never exposed again once added (see Arranger.h's
// GetPatternNotes doc comment). Get the count first, allocate an array of
// at least that size, then fill it; returns false if the index is out of
// range (outNotes is left untouched in that case).
UC_API int32_t UC_Arranger_GetPatternNoteCount(int32_t patternIndex);
UC_API bool UC_Arranger_GetPatternNotes(int32_t patternIndex, UC_SequencedNote* outNotes, int32_t maxCount);

// Appends 'patternIndex' to the end of the play order. No-op if out of range.
UC_API void UC_Arranger_AppendToOrder(int32_t patternIndex);

// Removes the last entry from the play order. No-op if the order is empty.
UC_API void UC_Arranger_RemoveLastFromOrder();

// Clears the whole play order (patterns themselves are untouched).
UC_API void UC_Arranger_ClearOrder();

UC_API int32_t UC_Arranger_GetOrderCount();

// The pattern index at position 'orderPosition' in the play order, or -1 if
// out of range.
UC_API int32_t UC_Arranger_GetOrderPatternIndexAt(int32_t orderPosition);

// Invalid (<= 0) values are ignored and 120 is used instead.
UC_API void UC_Arranger_SetTempoBpm(double bpm);
UC_API double UC_Arranger_GetTempoBpm();

// Starts playback from the top of the order, looping the whole arrangement
// forever until UC_Arranger_Stop. Does nothing if the order is empty.
UC_API void UC_Arranger_Play();
UC_API void UC_Arranger_Stop();
UC_API bool UC_Arranger_IsPlaying();

// Which position in the play order (see UC_Arranger_GetOrderPatternIndexAt)
// is currently playing, or -1 if stopped or the order is empty.
UC_API int32_t UC_Arranger_GetCurrentOrderPosition();

// --- Multi-track sequencing + mixer ---
// Several simultaneous tracks, each with its own note pattern (same
// UC_SequencedNote shape as the plain sequencer/arranger) and its own
// instrument - either the built-in synth, or a SoundFont bank/preset loaded
// just for that track - mixed together with per-track volume/pan/mute/solo.
// All tracks share one tempo and one loop length. Playing this stops the
// plain sequencer and the arranger, and vice versa.

// Instrument mode for a track, passed to UC_MultiTrack_SetTrackInstrumentMode
// and returned by UC_MultiTrack_GetTrackInstrumentMode.
enum UC_TrackInstrumentMode : int32_t
{
    UC_TrackInstrumentMode_BuiltInSynth = 0,
    UC_TrackInstrumentMode_SoundFont = 1,
    UC_TrackInstrumentMode_Vst = 2,
};

// Adds a new, empty track (built-in synth, full volume, centered pan, not
// muted/soloed) and returns its index (0-based, matches insertion order).
UC_API int32_t UC_MultiTrack_AddTrack(const char* name);

// Removes the track at 'trackIndex'. No-op if out of range.
UC_API void UC_MultiTrack_RemoveTrack(int32_t trackIndex);

UC_API int32_t UC_MultiTrack_GetTrackCount();

// Copies the track's name into 'buffer' (size 'bufferSize', ANSI,
// null-terminated). Returns false if the index is out of range.
UC_API bool UC_MultiTrack_GetTrackName(int32_t trackIndex, char* buffer, int32_t bufferSize);

// Returns false if the index is out of range.
UC_API bool UC_MultiTrack_SetTrackName(int32_t trackIndex, const char* name);

// Replaces the whole note pattern for the track at 'trackIndex'. Returns
// false if the index is out of range. Safe to call while playing.
UC_API bool UC_MultiTrack_SetTrackNotes(int32_t trackIndex, const UC_SequencedNote* notes, int32_t count);

// Volume: 0 (silent) to ~1.5 (some headroom above unity), clamped. Pan: -1
// (fully left) to 1 (fully right), clamped, 0 = centered. All the
// UC_MultiTrack_Set*/Is*/Get* per-track functions below return false (or,
// for the float/int getters, 0/-1) if 'trackIndex' is out of range.
UC_API bool UC_MultiTrack_SetTrackVolume(int32_t trackIndex, float volume);
UC_API float UC_MultiTrack_GetTrackVolume(int32_t trackIndex);
UC_API bool UC_MultiTrack_SetTrackPan(int32_t trackIndex, float pan);
UC_API float UC_MultiTrack_GetTrackPan(int32_t trackIndex);
UC_API bool UC_MultiTrack_SetTrackMute(int32_t trackIndex, bool mute);
UC_API bool UC_MultiTrack_IsTrackMuted(int32_t trackIndex);
// While any track is soloed, only soloed tracks are audible (their own mute
// is ignored); with no track soloed, every unmuted track plays.
UC_API bool UC_MultiTrack_SetTrackSolo(int32_t trackIndex, bool solo);
UC_API bool UC_MultiTrack_IsTrackSoloed(int32_t trackIndex);

UC_API bool UC_MultiTrack_SetTrackInstrumentMode(int32_t trackIndex, int32_t mode /* UC_TrackInstrumentMode */);
UC_API int32_t UC_MultiTrack_GetTrackInstrumentMode(int32_t trackIndex);

// Each track's SoundFont bank is its own independent load (even if it's the
// same .sf2 file another track also loaded), so tracks can each sound a
// different instrument from a bank at the same time. Loading a bank for a
// track automatically switches that track to SoundFont mode. Returns false
// on failure (bad index, or the load itself failing) and copies a
// human-readable reason into 'errorBuffer' (size 'errorBufferSize', ANSI,
// null-terminated, truncated if too small).
UC_API bool UC_MultiTrack_LoadTrackSoundFontBank(int32_t trackIndex, const char* sf2Path,
                                                  char* errorBuffer, int32_t errorBufferSize);
UC_API void UC_MultiTrack_UnloadTrackSoundFontBank(int32_t trackIndex);
UC_API bool UC_MultiTrack_IsTrackSoundFontBankLoaded(int32_t trackIndex);
UC_API bool UC_MultiTrack_GetTrackSoundFontBankName(int32_t trackIndex, char* buffer, int32_t bufferSize);
UC_API int32_t UC_MultiTrack_GetTrackSoundFontPresetCount(int32_t trackIndex);
UC_API bool UC_MultiTrack_GetTrackSoundFontPresetName(int32_t trackIndex, int32_t presetIndex,
                                                       char* buffer, int32_t bufferSize);
UC_API bool UC_MultiTrack_SelectTrackSoundFontPreset(int32_t trackIndex, int32_t presetIndex);
UC_API int32_t UC_MultiTrack_GetSelectedTrackSoundFontPresetIndex(int32_t trackIndex);

// Same idea as the per-track SoundFont functions above, but a VST3 plug-in -
// each track hosts its own independent plug-in instance. Loading one for a
// track immediately switches that track to Vst mode.
UC_API bool UC_MultiTrack_LoadTrackVstInstrument(int32_t trackIndex, const char* modulePath,
                                                  char* errorBuffer, int32_t errorBufferSize);
UC_API void UC_MultiTrack_UnloadTrackVstInstrument(int32_t trackIndex);
UC_API bool UC_MultiTrack_IsTrackVstInstrumentLoaded(int32_t trackIndex);
UC_API bool UC_MultiTrack_GetTrackVstInstrumentName(int32_t trackIndex, char* buffer, int32_t bufferSize);

// Shared by every track - see the block comment above.
UC_API void UC_MultiTrack_SetTempoBpm(double bpm);
UC_API double UC_MultiTrack_GetTempoBpm();
UC_API void UC_MultiTrack_SetLoopLengthBeats(double beats);
UC_API double UC_MultiTrack_GetLoopLengthBeats();

// Starts every track's transport together, from beat 0. Loops forever until
// UC_MultiTrack_Stop.
UC_API void UC_MultiTrack_Play();
UC_API void UC_MultiTrack_Stop();
UC_API bool UC_MultiTrack_IsPlaying();

// Renders the current multi-track mix offline (not through the live audio
// device) for 'durationSeconds' and writes it as a 16-bit PCM stereo .wav
// file at 'outPath' - meant for bringing a finished composition into Ultra
// Audio Editor (or anywhere else) for further mixing, e.g. with vocals.
// Briefly stops and restarts live audio playback if it was running, so the
// offline render doesn't race with it; returns false and copies a
// human-readable reason into 'errorBuffer' (size 'errorBufferSize', ANSI,
// null-terminated, truncated if too small) on failure.
UC_API bool UC_MultiTrack_RenderToWav(const char* outPath, double durationSeconds,
                                       char* errorBuffer, int32_t errorBufferSize);

// --- Auto-accompaniment ---
// Picks a built-in "style" (a drum pattern plus bass/kontra/harmonija
// layers - see Style.h/Style.cpp) that automatically follows whatever
// chord is currently active, exactly like a home keyboard's auto-
// accompaniment. Runs independently of the sequencer/arranger/multi-track
// transports above - it's meant to play underneath live keyboard/MIDI
// performance, not in place of a programmed transport, so starting/
// stopping it does not stop (or get stopped by) any of those.

// Which harmonic layer a UC_Accompaniment_Set/IsLayerEnabled call refers
// to. Drums are also a "layer" here for one consistent enable/disable API,
// even though they don't carry chord information.
enum UC_AccompanimentLayer : int32_t
{
    UC_AccompanimentLayer_Drums = 0,
    UC_AccompanimentLayer_Bass = 1,
    UC_AccompanimentLayer_Kontra = 2,
    UC_AccompanimentLayer_Harmonija = 3,
};

// The small, fixed chord-quality vocabulary the built-in styles and the
// manual chord picker use - see Chord.h for why this set (not a full jazz
// chord vocabulary) is enough here.
enum UC_ChordQuality : int32_t
{
    UC_ChordQuality_Major = 0,
    UC_ChordQuality_Minor = 1,
    UC_ChordQuality_Dominant7 = 2,
    UC_ChordQuality_Diminished = 3,
};

UC_API int32_t UC_Accompaniment_GetStyleCount();

// Copies the style's name into 'buffer' (size 'bufferSize', ANSI,
// null-terminated). Returns false if the index is out of range.
UC_API bool UC_Accompaniment_GetStyleName(int32_t index, char* buffer, int32_t bufferSize);

// Selects the style at 'index'. Resets playback position to the start and
// adopts that style's own suggested tempo (still freely adjustable
// afterwards via UC_Accompaniment_SetTempoBpm). Returns false if the index
// is out of range.
UC_API bool UC_Accompaniment_SetStyleIndex(int32_t index);
UC_API int32_t UC_Accompaniment_GetSelectedStyleIndex();

// Enables/disables one layer while playing (or before) - disabling one
// cleanly releases any note it currently has held rather than leaving it
// stuck sounding.
UC_API void UC_Accompaniment_SetLayerEnabled(int32_t layer /* UC_AccompanimentLayer */, bool enabled);
UC_API bool UC_Accompaniment_IsLayerEnabled(int32_t layer /* UC_AccompanimentLayer */);

// Invalid (<= 0) values are ignored and 100 is used instead.
UC_API void UC_Accompaniment_SetTempoBpm(double bpm);
UC_API double UC_Accompaniment_GetTempoBpm();

UC_API void UC_Accompaniment_Play();
UC_API void UC_Accompaniment_Stop();
UC_API bool UC_Accompaniment_IsPlaying();

// Chord input. In "auto" mode, notes played below 'splitPoint' (a MIDI
// note number, default 60 = C4) feed chord detection instead of sounding
// normally - the same left-hand-plays-the-chord convention a real
// keyboard's auto-accompaniment split uses. In manual mode, the split
// zone plays normally and UC_Accompaniment_SetManualChord drives the
// chord instead.
UC_API void UC_Accompaniment_SetChordInputAutoFromKeyboard(bool autoFromKeyboard);
UC_API bool UC_Accompaniment_IsChordInputAutoFromKeyboard();
UC_API void UC_Accompaniment_SetSplitPoint(int32_t midiNote);
UC_API int32_t UC_Accompaniment_GetSplitPoint();

// Sets the chord directly (works in either mode, though it's meant for
// manual mode's UI picker - in auto mode, the next chord played on the
// keyboard's chord zone will override it again).
UC_API void UC_Accompaniment_SetManualChord(int32_t rootPitchClass, int32_t quality /* UC_ChordQuality */);

// The currently active chord, however it was set (detected from the
// keyboard, or set manually) - for live UI display ("current chord: C
// major").
UC_API int32_t UC_Accompaniment_GetCurrentChordRootPitchClass();
UC_API int32_t UC_Accompaniment_GetCurrentChordQuality(); // UC_ChordQuality

// Which melodic layer a UC_Accompaniment_*LayerSoundFont*/LayerInstrumentMode
// call refers to. Drums aren't included here - they're always procedural
// (see DrumSynth), never SoundFont-backed.
enum UC_AccompanimentMelodicLayer : int32_t
{
    UC_AccompanimentMelodicLayer_Bass = 0,
    UC_AccompanimentMelodicLayer_Kontra = 1,
    UC_AccompanimentMelodicLayer_Harmonija = 2,
};

// Whether a melodic layer sounds through the built-in synth (default),
// through its own independently loaded SoundFont bank/preset, or through
// its own independently loaded VST3 instrument.
enum UC_AccompanimentInstrumentMode : int32_t
{
    UC_AccompanimentInstrumentMode_BuiltInSynth = 0,
    UC_AccompanimentInstrumentMode_SoundFont = 1,
    UC_AccompanimentInstrumentMode_Vst = 2,
};

// Per-layer SoundFont instrument, so e.g. "harmonija" can sound like real
// strings from a loaded bank instead of the plain built-in synth. Each
// layer's bank is its own independent load, even if it's the same .sf2
// file another layer (or the main shared SoundFont bank used for live
// playing) already loaded - that's what lets bass/kontra/harmonija each
// sound a different instrument from the same bank at once. Loading a bank
// for a layer immediately switches that layer to SoundFont mode.
UC_API bool UC_Accompaniment_LoadLayerSoundFontBank(int32_t layer /* UC_AccompanimentMelodicLayer */,
                                                     const char* sf2Path,
                                                     char* errorBuffer, int32_t errorBufferSize);
UC_API void UC_Accompaniment_UnloadLayerSoundFontBank(int32_t layer /* UC_AccompanimentMelodicLayer */);
UC_API bool UC_Accompaniment_IsLayerSoundFontBankLoaded(int32_t layer /* UC_AccompanimentMelodicLayer */);
UC_API bool UC_Accompaniment_GetLayerSoundFontBankName(int32_t layer /* UC_AccompanimentMelodicLayer */,
                                                        char* buffer, int32_t bufferSize);
UC_API int32_t UC_Accompaniment_GetLayerSoundFontPresetCount(int32_t layer /* UC_AccompanimentMelodicLayer */);
UC_API bool UC_Accompaniment_GetLayerSoundFontPresetName(int32_t layer /* UC_AccompanimentMelodicLayer */,
                                                          int32_t presetIndex, char* buffer, int32_t bufferSize);
UC_API bool UC_Accompaniment_SelectLayerSoundFontPreset(int32_t layer /* UC_AccompanimentMelodicLayer */, int32_t presetIndex);
UC_API int32_t UC_Accompaniment_GetSelectedLayerSoundFontPresetIndex(int32_t layer /* UC_AccompanimentMelodicLayer */);
UC_API void UC_Accompaniment_SetLayerInstrumentMode(int32_t layer /* UC_AccompanimentMelodicLayer */,
                                                     int32_t mode /* UC_AccompanimentInstrumentMode */);
UC_API int32_t UC_Accompaniment_GetLayerInstrumentMode(int32_t layer /* UC_AccompanimentMelodicLayer */); // UC_AccompanimentInstrumentMode

// Per-layer VST3 instrument, same idea as the per-layer SoundFont functions
// above - loading a plug-in for a layer immediately switches that layer to
// Vst mode.
UC_API bool UC_Accompaniment_LoadLayerVstInstrument(int32_t layer /* UC_AccompanimentMelodicLayer */,
                                                     const char* modulePath,
                                                     char* errorBuffer, int32_t errorBufferSize);
UC_API void UC_Accompaniment_UnloadLayerVstInstrument(int32_t layer /* UC_AccompanimentMelodicLayer */);
UC_API bool UC_Accompaniment_IsLayerVstInstrumentLoaded(int32_t layer /* UC_AccompanimentMelodicLayer */);
UC_API bool UC_Accompaniment_GetLayerVstInstrumentName(int32_t layer /* UC_AccompanimentMelodicLayer */,
                                                        char* buffer, int32_t bufferSize);

// How many chord tones the accompaniment's kontra/harmonija layers actually
// sound, overriding what the selected style's pattern data authored per hit.
// AsAuthored keeps the style's own choice (the default); Triad forces a
// plain root/third/fifth; Seventh forces the full four-tone chord. Never
// affects bass (see AccompanimentEngine.h's ChordVoicing doc comment for
// why). Takes effect immediately, including on whatever is already
// sustaining.
enum UC_ChordVoicing : int32_t
{
    UC_ChordVoicing_AsAuthored = 0,
    UC_ChordVoicing_Triad = 1,
    UC_ChordVoicing_Seventh = 2,
};
UC_API void UC_Accompaniment_SetChordVoicing(int32_t voicing /* UC_ChordVoicing */);
UC_API int32_t UC_Accompaniment_GetChordVoicing(); // UC_ChordVoicing

// --- Custom rhythm/style ("Sopstveni ritam") ---
// Lets the person build their own accompaniment style in the UI instead of
// only picking from the built-in bank: one bar ("takt") of drum hits plus
// bass/kontra/harmonija chord-tone hits, with more bars addable afterwards
// as variations. The UI does all of the bar-by-bar editing and chaining
// itself (see MainWindow's custom-rhythm editor) and only calls this once,
// with the whole thing already flattened into one set of hit arrays (each
// subsequent bar's hits already offset in time to play after the previous
// one) - the native side just needs to install it as a style, exactly like
// one of the built-in ones.

// Same percussion voices as DrumSynth's DrumVoiceType (Style.h's built-in
// bank uses the same values already, just not exposed to the ABI before now
// since nothing outside Style.cpp needed to name one directly).
enum UC_DrumVoiceType : int32_t
{
    UC_DrumVoiceType_Kick = 0,
    UC_DrumVoiceType_Snare = 1,
    UC_DrumVoiceType_ClosedHat = 2,
    UC_DrumVoiceType_OpenHat = 3,
    UC_DrumVoiceType_Clap = 4,
    UC_DrumVoiceType_Crash = 5,
    UC_DrumVoiceType_Tom = 6,
    UC_DrumVoiceType_DumbekDum = 7, // low, resonant darbuka/dumbek "Dum" stroke
    UC_DrumVoiceType_DumbekTek = 8, // higher, crisp darbuka/dumbek "Tek" stroke
};

// One drum hit, in beats from the start of the whole (possibly multi-bar)
// custom rhythm.
struct UC_CustomDrumHit
{
    double startBeat;
    int32_t drumVoice; // UC_DrumVoiceType
    float velocity;
};

// One bass/kontra/harmonija hit. 'chordToneMask' packs which chord tone(s)
// this hit sounds as bits 0-3 (bit 0 = root, bit 1 = third, bit 2 = fifth,
// bit 3 = seventh/color tone) - bass patterns should normally set just one
// bit (a single bass note), kontra/harmonija can set several for a chord
// stab or pad, matching Style.h's RelativePatternChordHit.chordToneIndices.
struct UC_CustomChordHit
{
    double startBeat;
    double lengthBeats;
    int32_t chordToneMask;
    int32_t octaveOffset;
    float velocity;
};

// Adds (or, if 'replaceIndex' names an existing custom style, edits in
// place) a custom style built this way, and selects it immediately (see
// UC_Accompaniment_SetStyleIndex) so it plays right away. Any number of
// custom styles can coexist - pass -1 for 'replaceIndex' to always add a
// new one alongside any already saved. Any of the hit arrays may be null
// when its count is 0 (an empty layer is fine - e.g. a rhythm with no
// harmonija part). Returns the resulting style's index (also usable later
// with UC_Accompaniment_SetStyleIndex/UC_Accompaniment_RemoveCustomStyle),
// or -1 if every array is empty - there is nothing to play.
UC_API int32_t UC_Accompaniment_SetCustomStyle(
    const char* name,
    double tempoBpm,
    double lengthBeats,
    double beatsPerBar,
    const UC_CustomDrumHit* drumHits, int32_t drumHitCount,
    const UC_CustomChordHit* bassHits, int32_t bassHitCount,
    const UC_CustomChordHit* kontraHits, int32_t kontraHitCount,
    const UC_CustomChordHit* harmonijaHits, int32_t harmonijaHitCount,
    int32_t replaceIndex);

// Removes a previously-saved custom style ('index' must be a custom style,
// not one of the built-in ones - see UC_Accompaniment_SetCustomStyle).
// Returns false if 'index' isn't a valid custom style index.
UC_API bool UC_Accompaniment_RemoveCustomStyle(int32_t index);
