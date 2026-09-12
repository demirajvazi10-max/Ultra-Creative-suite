#pragma once

#include <atomic>
#include <mutex>
#include <string>
#include <vector>

#include "Chord.h"
#include "DrumSynth.h"
#include "SoundFontHost.h"
#include "Style.h"
#include "Synth.h"
#include "VstHost.h"

// How many chord tones the kontra and harmonija layers actually sound,
// overriding what each style's pattern data authored per hit (see
// Style.h's RelativePatternChordHit) - some people want a plain triad
// (root/third/fifth) no matter what a style's pattern was written with, or
// conversely always the full four-tone chord (adding the seventh/color
// tone). AsAuthored keeps today's behavior: whatever each hit's own
// chordToneIndices specifies. The bass layer is deliberately never
// affected - its pattern references a single chord-tone slot per hit to
// pick WHICH note serves as the bass note (root vs fifth, say), not a
// stack of simultaneous tones, so overriding it the same way would turn a
// walking bass line into a bass chord.
enum class ChordVoicing
{
    AsAuthored = 0,
    Triad = 1,
    Seventh = 2,
};

// Auto-accompaniment: pick a built-in Style (see Style.h/Style.cpp) and it
// plays a full backing - drums, bass, "kontra" (rhythmic chord stabs) and
// harmonija (a sustained pad) - that automatically follows whatever chord
// is currently active, exactly like the auto-accompaniment feature on a
// home keyboard. Meant to run *underneath* live playing (computer keyboard
// or a MIDI device) - it deliberately does not stop the plain sequencer,
// arranger, or multi-track transports, since there's no conflict: it owns
// its own private instruments (bassSynth_/kontraSynth_/harmonijaSynth_/
// drumSynth_, plus an independent, optional SoundFont bank per melodic
// layer - see MelodicLayer/LayerInstrumentMode below), just like each
// MultiTrackSequencer track does.
//
// The current chord can come from either (or both, switchable) of:
//  - "Auto" mode: playing chords with the left hand below a configurable
//    split point on the keyboard. AudioEngine::NoteOn/NoteOff route notes
//    below the split here (see NoteHeldForChordDetection) instead of
//    playing them normally, and the chord is re-detected from whatever is
//    currently held every time that set changes.
//  - "Manual" mode: SetManualChord() is called directly from the UI (a
//    root + quality picker), and notes below the split play normally
//    instead of being absorbed.
//
// Whenever the current chord changes, every melodic layer that has a note
// already sustaining immediately re-voices it against the new chord (see
// Advance()'s retuning step) rather than waiting for that pattern hit's
// next scheduled onset - some of the built-in styles hold a single
// harmonija/kontra note for a whole bar, and without this a chord change
// could go unheard for several seconds, which felt like "the accompaniment
// doesn't react to chords at all" during testing.
//
// Thread safety follows the same convention as Sequencer/Arranger/
// MultiTrackSequencer: editing (Play/Stop/SetStyleIndex/layer toggles/
// tempo/instrument selection) happens on the UI thread, Advance()/
// RenderAdditive() run on the real-time audio thread, and mutex_ guards
// the shared pattern-playback state. Unlike those classes, Advance() here
// dispatches directly to this engine's own owned Synth/SoundFontHost/
// DrumSynth instances instead of going through a caller-supplied
// onNoteOn/onNoteOff callback - there is no other owner for these
// instruments to hand events to, the same reasoning MultiTrackSequencer's
// per-track dispatch already uses. The current chord itself is kept as a
// single packed atomic<int> (lock-free), since it is written from the
// MIDI/UI thread and read from the audio thread every block.
class AccompanimentEngine
{
public:
    // Which of the three melodic layers a Load/Select/Get*SoundFont* or
    // Set/GetLayerInstrumentMode call refers to (drums are always
    // procedural - see DrumSynth - so they aren't part of this).
    enum class MelodicLayer
    {
        Bass = 0,
        Kontra = 1,
        Harmonija = 2,
    };

    // Whether a melodic layer sounds through its own private built-in
    // Synth (the original behavior, and still the default so nothing
    // changes for anyone who doesn't touch this), through its own
    // independently loaded SoundFont bank/preset, or through its own
    // independently loaded VST3 instrument - e.g. so "harmonija" can sound
    // like real strings from a loaded bank or plug-in instead of a plain
    // sine pad. Each layer's SoundFont/VST is its own separate load (even
    // if it's the same file another layer already loaded, or the same file
    // already loaded into the main shared SoundFontHost/VstHost elsewhere
    // in the engine), exactly like MultiTrackSequencer's per-track
    // SoundFont - that's what lets three layers sound three different
    // instruments from the same bank/plug-in at once.
    enum class LayerInstrumentMode
    {
        BuiltInSynth = 0,
        SoundFont = 1,
        Vst = 2,
    };

    void Prepare(double sampleRate);

    // Style selection. Selecting a style resets playback position to the
    // start and adopts that style's suggested tempo (still freely
    // adjustable afterwards via SetTempoBpm) - matching how style buttons
    // behave on a real arranger keyboard.
    int GetStyleCount() const;
    std::string GetStyleName(int index) const;
    bool SetStyleIndex(int index);
    int GetSelectedStyleIndex() const;

    // Adds or replaces a custom, user-authored style built via the
    // "Sopstveni ritam" (custom rhythm) editor in the UI - one bar built up
    // from drum/bass/kontra/harmonija hits, optionally with more bars
    // chained after it as variations, all flattened into one Style by the
    // caller before this is called. Any number of custom styles can coexist
    // after the built-in bank: passing 'replaceIndex' as -1 (the default)
    // always adds a new one, alongside whichever custom styles are already
    // saved; passing an existing custom style's own index instead edits it
    // in place (used while still tweaking a rhythm you haven't finished
    // with yet, so re-saving the same one doesn't pile up a new "Ritam"
    // entry on every edit). Immediately selected via SetStyleIndex so it
    // can be played right away. A style with no hits at all in any layer is
    // rejected. Returns the resulting style's index, or -1 on rejection.
    int SetCustomStyle(const Style& style, int replaceIndex = -1);

    // Removes a custom style ('index' must be >= the built-in style count -
    // the built-in bank itself can't be shrunk this way). Shifts every
    // later style's index down by one; if the removed style was the
    // currently selected one, falls back to selecting style 0. Returns
    // false if 'index' isn't a valid custom style index.
    bool RemoveCustomStyle(int index);

    // Layers: drums, bass, kontra (rhythmic chord stabs), harmonija (a
    // sustained pad) can each be switched on/off independently while
    // playing - disabling one cleanly releases any note it currently has
    // held rather than leaving it stuck sounding.
    void SetDrumsEnabled(bool enabled);
    bool AreDrumsEnabled() const;
    void SetBassEnabled(bool enabled);
    bool IsBassEnabled() const;
    void SetKontraEnabled(bool enabled);
    bool IsKontraEnabled() const;
    void SetHarmonijaEnabled(bool enabled);
    bool IsHarmonijaEnabled() const;

    void SetTempoBpm(double bpm);
    double GetTempoBpm() const;

    void Play();
    void Stop();
    bool IsPlaying() const;

    // Chord input.
    void SetChordInputAutoFromKeyboard(bool autoFromKeyboard);
    bool IsChordInputAutoFromKeyboard() const;
    void SetSplitPoint(int midiNote);
    int GetSplitPoint() const;
    // Called by AudioEngine::NoteOn/NoteOff for every note below the split
    // point while auto mode is active - re-detects the current chord from
    // whichever notes in the zone are currently held. Releasing every held
    // note keeps the last-detected chord sounding rather than silencing
    // the accompaniment, matching real keyboards.
    void NoteHeldForChordDetection(int midiNote, bool isOn);
    // Sets the chord directly (Manual mode's UI picker); also usable at
    // any time regardless of mode.
    void SetManualChord(int rootPitchClass, ChordQuality quality);
    int GetCurrentChordRootPitchClass() const;
    ChordQuality GetCurrentChordQuality() const;

    // Per-layer instrument (see LayerInstrumentMode above). Loading a
    // SoundFont bank for a layer is itself the action that commits that
    // layer to using it (switches its mode to SoundFont), matching how
    // MultiTrackSequencer's per-track SoundFont load already behaves.
    // Switching a layer's mode (directly, or via a new load) immediately
    // releases whatever that layer currently has sustaining through its
    // OLD source first, so nothing is left hanging.
    bool LoadLayerSoundFontBank(MelodicLayer layer, const std::string& sf2Path, std::string& outError);
    void UnloadLayerSoundFontBank(MelodicLayer layer);
    bool IsLayerSoundFontBankLoaded(MelodicLayer layer) const;
    std::string GetLayerSoundFontBankName(MelodicLayer layer) const;
    int GetLayerSoundFontPresetCount(MelodicLayer layer) const;
    std::string GetLayerSoundFontPresetName(MelodicLayer layer, int presetIndex) const;
    bool SelectLayerSoundFontPreset(MelodicLayer layer, int presetIndex);
    int GetSelectedLayerSoundFontPresetIndex(MelodicLayer layer) const;
    void SetLayerInstrumentMode(MelodicLayer layer, LayerInstrumentMode mode);
    LayerInstrumentMode GetLayerInstrumentMode(MelodicLayer layer) const;

    // Per-layer VST3 instrument - same idea as the per-layer SoundFont
    // methods above, and the same commits-on-load behavior (loading a
    // plug-in for a layer switches that layer to Vst mode immediately).
    bool LoadLayerVstInstrument(MelodicLayer layer, const std::string& modulePath, std::string& outError);
    void UnloadLayerVstInstrument(MelodicLayer layer);
    bool IsLayerVstInstrumentLoaded(MelodicLayer layer) const;
    std::string GetLayerVstInstrumentName(MelodicLayer layer) const;

    // How many chord tones kontra/harmonija actually sound (see ChordVoicing
    // above). Never affects bass. Takes effect immediately - both newly
    // triggered hits and, via the same retuning path a chord change already
    // uses (see Advance()), whatever is currently sustaining.
    void SetChordVoicing(ChordVoicing voicing);
    ChordVoicing GetChordVoicing() const;

    // Real-time thread.
    void Advance(int frames, double sampleRate);
    void RenderAdditive(float* interleavedStereoOut, int frames);

private:
    static constexpr int kMaxScratchFrames = 2048; // must stay >= AudioEngine.cpp's kMaxCallbackFrames
    static constexpr int kBassBaseNote = 36;        // C2
    static constexpr int kKontraBaseNote = 55;      // G3
    static constexpr int kHarmonijaBaseNote = 60;   // C4

    std::vector<Style> styles_;
    int selectedStyleIndex_ = 0;
    // How many entries at the front of styles_ came from BuiltInStyles() -
    // set once in Prepare(). Every style from this index onward is a custom
    // one (see SetCustomStyle/RemoveCustomStyle) - any number of them can
    // coexist, no longer limited to a single fixed slot.
    int builtInStyleCount_ = 0;

    mutable std::mutex mutex_;
    double tempoBpm_ = 100.0;
    double positionBeats_ = 0.0;
    bool playing_ = false;
    // One resolved-pitch-set slot per pattern entry (see Style.h's
    // RelativePatternChordHit) for each melodic layer, so an already-
    // sounding hit is released at exactly the pitch(es) it was actually
    // triggered with - even if the chord has since changed - while a new
    // hit always resolves against whatever chord is current right now.
    std::vector<std::vector<int>> bassLastResolved_;
    std::vector<std::vector<int>> kontraLastResolved_;
    std::vector<std::vector<int>> harmonijaLastResolved_;
    // Chord the melodic layers were last retuned against (see Advance()) -
    // whenever the live chord differs from this, every currently-sustaining
    // hit is immediately re-voiced to the new chord instead of waiting for
    // its pattern's next onset. -1 is an impossible packed value, so the
    // very first Advance() call never spuriously retunes (nothing is
    // sustaining yet at that point anyway).
    int lastChordPackedForRetune_ = -1;
    // Same idea as lastChordPackedForRetune_ above, but for ChordVoicing:
    // changing the voicing selector while kontra/harmonija notes are already
    // sustaining re-voices them immediately too, instead of waiting for the
    // next pattern hit. -1 is likewise an impossible packed value (real
    // values are 0/1/2).
    int lastVoicingForRetune_ = -1;

    std::atomic<bool> drumsEnabled_{true};
    std::atomic<bool> bassEnabled_{true};
    std::atomic<bool> kontraEnabled_{true};
    std::atomic<bool> harmonijaEnabled_{true};

    std::atomic<bool> autoChordFromKeyboard_{true};
    std::atomic<int> splitPoint_{60}; // C4
    std::atomic<int> currentChordPacked_{0}; // quality*12 + rootPitchClass; default C major

    std::mutex heldNotesMutex_;
    std::vector<int> heldNotes_;

    double sampleRate_ = 44100.0;

    Synth bassSynth_;
    Synth kontraSynth_;
    Synth harmonijaSynth_;
    DrumSynth drumSynth_;

    // Independent per-layer SoundFont banks (see LayerInstrumentMode) - each
    // is its own SoundFontHost instance, separate from AudioEngine's shared
    // one used for live keyboard/MIDI playing, so a layer can sound a
    // completely different instrument (or the same bank file loaded three
    // times with three different presets picked) without disturbing what
    // the main keyboard plays.
    SoundFontHost bassSoundFont_;
    SoundFontHost kontraSoundFont_;
    SoundFontHost harmonijaSoundFont_;
    // Independent per-layer VST3 instruments, same reasoning as the
    // per-layer SoundFont hosts above.
    VstHost bassVst_;
    VstHost kontraVst_;
    VstHost harmonijaVst_;
    std::atomic<int> bassInstrumentMode_{0};      // LayerInstrumentMode, packed as int
    std::atomic<int> kontraInstrumentMode_{0};
    std::atomic<int> harmonijaInstrumentMode_{0};

    // ChordVoicing override for kontra/harmonija (see the enum's doc comment
    // and SetChordVoicing above). Packed as int for lock-free access from
    // the UI thread; read on the audio thread inside Advance()'s locked
    // section, same convention as currentChordPacked_.
    std::atomic<int> chordVoicing_{0}; // ChordVoicing::AsAuthored

    float bassScratch_[kMaxScratchFrames]{};
    float kontraScratch_[kMaxScratchFrames]{};
    float harmonijaScratch_[kMaxScratchFrames]{};
    float drumScratch_[kMaxScratchFrames]{};

    static Chord DetectChordFromHeldNotes(const std::vector<int>& heldNotes);

    // Small switch-based accessors so the per-layer methods above (and
    // Advance()'s dispatch) don't need to repeat the same three-way
    // if/else at every call site.
    Synth& SynthFor(MelodicLayer layer);
    SoundFontHost& SoundFontHostFor(MelodicLayer layer);
    const SoundFontHost& SoundFontHostFor(MelodicLayer layer) const;
    VstHost& VstHostFor(MelodicLayer layer);
    const VstHost& VstHostFor(MelodicLayer layer) const;
    std::atomic<int>& InstrumentModeFor(MelodicLayer layer);
    const std::atomic<int>& InstrumentModeFor(MelodicLayer layer) const;
    std::vector<std::vector<int>>& LastResolvedFor(MelodicLayer layer);

    // Sends 'offEvents' then 'onEvents' to whichever source (built-in synth,
    // this layer's own SoundFont bank, or this layer's own VST3 instrument)
    // is currently selected for it.
    void DispatchLayer(MelodicLayer layer, const std::vector<int>& offEvents,
                        const std::vector<std::pair<int, float>>& onEvents);
};
