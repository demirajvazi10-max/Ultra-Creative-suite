#pragma once

#include <atomic>
#include <mutex>
#include <string>
#include <utility>
#include <vector>

#include "AccompanimentEngine.h"
#include "Arranger.h"
#include "Chord.h"
#include "MidiInput.h"
#include "MultiTrackSequencer.h"
#include "Scale.h"
#include "Sequencer.h"
#include "SoundFontHost.h"
#include "Synth.h"
#include "VstHost.h"

// Owns the PortAudio stream, the Synth, MIDI hardware input, and an
// optional loaded VST3 instrument. This is the only class that touches
// PortAudio directly, so the rest of the engine stays portable.
class AudioEngine
{
public:
    bool Init();
    void Shutdown();
    bool Start();
    void Stop();

    // The single shared entry point for "play/stop this note", used by
    // every note source in the engine (computer keyboard, MIDI hardware,
    // the sequencer, and the arranger) - see PaCallback and the MIDI input
    // callback in AudioEngine.cpp. Feeds the built-in synth, a loaded VST3
    // instrument, and a loaded SoundFont bank; whichever combination is
    // actually loaded, you hear it (see AudioEngine.cpp's PaCallback for
    // which one wins when more than one is loaded). Also the single place
    // auto-third harmonization (see SetAutoThirdEnabled) is applied, so it
    // automatically covers every note source without needing to be added
    // to each one separately.
    void NoteOn(int midiNote, float velocity);
    void NoteOff(int midiNote);

    // Same as NoteOn/NoteOff (and call through to them, so everything above
    // - auto-third included - still applies), but additionally observed by
    // chord capture below when it's armed. Used only by the two genuinely
    // LIVE note sources - computer keyboard input and MIDI hardware input
    // (see AudioEngine.cpp's Init() for the MIDI callback) - never by the
    // sequencer's or arranger's programmatic playback (PaCallback calls
    // plain NoteOn/NoteOff for those), so a captured chord only ever
    // reflects notes a person actually played by hand, regardless of
    // whatever a pattern happens to be playing back at the same moment.
    void NoteOnLive(int midiNote, float velocity);
    void NoteOffLive(int midiNote);

    // Chord capture ("Slušaj akord" / chord-by-shape entry): while armed,
    // remembers the largest set of NoteOnLive notes held down together since
    // the last time nothing was held, and hands that off as the "captured
    // chord" the moment every one of those notes is released again. Meant to
    // be polled from the UI (TryTakeCapturedChord) while armed, so someone
    // can play a chord (or a single note) by shape instead of typing each
    // note name into a sequencer/multi-track note list. Arming clears any
    // notes already mid-press and any not-yet-collected pending capture, so
    // it always starts from a clean slate.
    void SetChordCaptureArmed(bool armed);
    bool IsChordCaptureArmed() const;
    // Returns false if nothing is pending yet; otherwise fills notesOut/
    // velocitiesOut (in press order) with the captured chord and returns
    // true, consuming it so each captured chord is only ever reported once.
    bool TryTakeCapturedChord(std::vector<int>& notesOut, std::vector<float>& velocitiesOut);

    // Auto-third harmonization: while enabled, every note played anywhere
    // (computer keyboard, MIDI hardware, the sequencer, the arranger, and
    // every multi-track track) also triggers a second note a diatonic third
    // above or below it - a quick way to get two-part harmony without a
    // second hand or an actual MIDI chord. Off by default. Safe to call
    // from the UI thread while the audio thread is running (plain atomic
    // fields, no lock needed).
    void SetAutoThirdEnabled(bool enabled);
    bool IsAutoThirdEnabled() const;
    void SetAutoThirdUpper(bool upper); // true = third above, false = third below
    bool IsAutoThirdUpper() const;
    // Which key the added third is diatonic in (see Scale.h - major, natural
    // minor, or one of the two "oriental"-flavored scales, Hijaz/Hijaz Kar)
    // - a note that isn't in this scale falls back to a fixed major third,
    // since there's no single correct diatonic answer for a deliberately
    // chromatic note. Defaults to C major.
    void SetAutoThirdScale(int rootPitchClass, ScaleType scaleType);
    int GetAutoThirdScaleRoot() const;
    ScaleType GetAutoThirdScaleType() const;

    // MIDI hardware input (e.g. a USB-MIDI keyboard). Notes played on an
    // open MIDI port feed the same Synth (and VST3 instrument) as the
    // computer-keyboard input.
    std::vector<std::string> ListMidiPorts();
    bool OpenMidiPort(unsigned int portIndex);
    void CloseMidiPort();
    bool IsMidiPortOpen() const;

    // VST3 instrument hosting.
    bool LoadVstInstrument(const std::string& modulePath, std::string& outError);
    void UnloadVstInstrument();
    bool IsVstInstrumentLoaded() const;
    std::string GetVstInstrumentName() const;

    // Simple single-track sequencer playback (see Sequencer.h). Notes feed
    // both the built-in synth and a loaded VST3 instrument, same as live
    // keyboard/MIDI input.
    void SetSequencerNotes(const SequencedNote* notes, int count);
    void SetSequencerTempoBpm(double bpm);
    void SetSequencerLoopLengthBeats(double beats);
    void PlaySequencer();
    void StopSequencer();
    bool IsSequencerPlaying() const;

    // SoundFont (.sf2) instrument banks (see SoundFontHost.h) - ready-made
    // GM-style instrument sounds (piano, strings, drums, ...), same idea
    // as Cakewalk's built-in sound bank.
    bool LoadSoundFontBank(const std::string& sf2Path, std::string& outError);
    void UnloadSoundFontBank();
    bool IsSoundFontBankLoaded() const;
    std::string GetSoundFontBankName() const;
    int GetSoundFontPresetCount() const;
    std::string GetSoundFontPresetName(int presetIndex) const;
    bool SelectSoundFontPreset(int presetIndex);
    int GetSelectedSoundFontPresetIndex() const;

    // Arranger: chains multiple named patterns (see Arranger.h) into one
    // ordered playback sequence, looping the whole arrangement once it
    // reaches the end. A pattern is typically built by composing in the
    // plain sequencer above, then saving that as a named pattern here
    // (AddArrangerPattern is given a snapshot of the sequencer's current
    // notes - the two don't stay linked afterwards). Playing the arranger
    // stops the plain sequencer, and vice versa (PlaySequencer stops the
    // arranger) - both feed the same instruments, so only one should
    // actually be advancing the transport at a time.
    int AddArrangerPattern(const std::string& name, double lengthBeats, const SequencedNote* notes, int count);
    void RemoveArrangerPattern(int patternIndex);
    int GetArrangerPatternCount() const;
    std::string GetArrangerPatternName(int patternIndex) const;
    double GetArrangerPatternLengthBeats(int patternIndex) const;
    // For project save (see Arranger::GetPatternNotes's doc comment).
    int GetArrangerPatternNoteCount(int patternIndex) const;
    std::vector<SequencedNote> GetArrangerPatternNotes(int patternIndex) const;
    void AppendToArrangerOrder(int patternIndex);
    void RemoveLastFromArrangerOrder();
    void ClearArrangerOrder();
    int GetArrangerOrderCount() const;
    int GetArrangerOrderPatternIndexAt(int orderPosition) const;
    void SetArrangerTempoBpm(double bpm);
    double GetArrangerTempoBpm() const;
    void PlayArranger();
    void StopArranger();
    bool IsArrangerPlaying() const;
    int GetArrangerCurrentOrderPosition() const;

    // Multi-track sequencing + mixer (see MultiTrackSequencer.h): several
    // simultaneous tracks, each with its own note pattern and its own
    // instrument (built-in synth, or an independently loaded SoundFont bank/
    // preset), mixed together with per-track volume/pan/mute/solo. Playing
    // this stops the plain sequencer and the arranger, and vice versa - same
    // "only one transport at a time" rule as between those two.
    int AddTrack(const std::string& name);
    void RemoveTrack(int trackIndex);
    int GetTrackCount() const;
    std::string GetTrackName(int trackIndex) const;
    void SetTrackName(int trackIndex, const std::string& name);
    void SetTrackNotes(int trackIndex, const SequencedNote* notes, int count);
    void SetTrackVolume(int trackIndex, float volume);
    float GetTrackVolume(int trackIndex) const;
    void SetTrackPan(int trackIndex, float pan);
    float GetTrackPan(int trackIndex) const;
    void SetTrackMute(int trackIndex, bool mute);
    bool IsTrackMuted(int trackIndex) const;
    void SetTrackSolo(int trackIndex, bool solo);
    bool IsTrackSoloed(int trackIndex) const;
    void SetTrackInstrumentMode(int trackIndex, MultiTrackSequencer::InstrumentMode mode);
    MultiTrackSequencer::InstrumentMode GetTrackInstrumentMode(int trackIndex) const;
    bool LoadTrackSoundFontBank(int trackIndex, const std::string& sf2Path, std::string& outError);
    void UnloadTrackSoundFontBank(int trackIndex);
    bool IsTrackSoundFontBankLoaded(int trackIndex) const;
    std::string GetTrackSoundFontBankName(int trackIndex) const;
    int GetTrackSoundFontPresetCount(int trackIndex) const;
    std::string GetTrackSoundFontPresetName(int trackIndex, int presetIndex) const;
    bool SelectTrackSoundFontPreset(int trackIndex, int presetIndex);
    int GetSelectedTrackSoundFontPresetIndex(int trackIndex) const;
    // Same idea as the per-track SoundFont methods above, but a VST3
    // plug-in - each track hosts its own independent instance.
    bool LoadTrackVstInstrument(int trackIndex, const std::string& modulePath, std::string& outError);
    void UnloadTrackVstInstrument(int trackIndex);
    bool IsTrackVstInstrumentLoaded(int trackIndex) const;
    std::string GetTrackVstInstrumentName(int trackIndex) const;
    void SetMultiTrackTempoBpm(double bpm);
    double GetMultiTrackTempoBpm() const;
    void SetMultiTrackLoopLengthBeats(double beats);
    double GetMultiTrackLoopLengthBeats() const;
    void PlayMultiTrack();
    void StopMultiTrack();
    bool IsMultiTrackPlaying() const;

    // Renders the current multi-track mix offline (not through the live
    // audio device) for 'durationSeconds' and writes it as a 16-bit PCM
    // stereo .wav file at 'outPath' - so the finished composition can be
    // brought into Ultra Audio Editor (or anywhere else) for further mixing
    // with vocals, etc. Briefly stops and restarts the live audio stream (if
    // it was running) so this offline render doesn't race with the real-time
    // callback over the same track state; any live-monitored sound (e.g. a
    // MIDI keyboard being played at that exact moment) is silent for that
    // short window. Returns false and fills 'outError' on failure.
    bool RenderMultiTrackToWav(const std::string& outPath, double durationSeconds, std::string& outError);

    // Auto-accompaniment (see AccompanimentEngine.h): a built-in style
    // (drums + bass + kontra + harmonija) that follows whatever chord is
    // currently active. Deliberately independent of the sequencer/
    // arranger/multi-track "only one transport at a time" rule above - it
    // owns its own instruments and is meant to run underneath live
    // playing, not in place of a programmed transport.
    int GetAccompanimentStyleCount() const;
    std::string GetAccompanimentStyleName(int index) const;
    bool SetAccompanimentStyleIndex(int index);
    int GetSelectedAccompanimentStyleIndex() const;
    // Custom rhythm/style built via the UI's "Sopstveni ritam" editor - see
    // AccompanimentEngine::SetCustomStyle. Returns the resulting style's
    // index (also now the selected one), or -1 if 'style' has no hits at
    // all.
    int SetAccompanimentCustomStyle(const Style& style, int replaceIndex = -1);
    // Removes a previously-saved custom style (see AccompanimentEngine::
    // RemoveCustomStyle) - built-in styles can't be removed this way.
    bool RemoveAccompanimentCustomStyle(int index);
    void SetAccompanimentDrumsEnabled(bool enabled);
    bool AreAccompanimentDrumsEnabled() const;
    void SetAccompanimentBassEnabled(bool enabled);
    bool IsAccompanimentBassEnabled() const;
    void SetAccompanimentKontraEnabled(bool enabled);
    bool IsAccompanimentKontraEnabled() const;
    void SetAccompanimentHarmonijaEnabled(bool enabled);
    bool IsAccompanimentHarmonijaEnabled() const;
    void SetAccompanimentTempoBpm(double bpm);
    double GetAccompanimentTempoBpm() const;
    void PlayAccompaniment();
    void StopAccompaniment();
    bool IsAccompanimentPlaying() const;
    void SetAccompanimentChordInputAutoFromKeyboard(bool autoFromKeyboard);
    bool IsAccompanimentChordInputAutoFromKeyboard() const;
    void SetAccompanimentSplitPoint(int midiNote);
    int GetAccompanimentSplitPoint() const;
    void SetAccompanimentManualChord(int rootPitchClass, ChordQuality quality);
    int GetAccompanimentCurrentChordRootPitchClass() const;
    ChordQuality GetAccompanimentCurrentChordQuality() const;

    // Per-layer instrument for the accompaniment's bass/kontra/harmonija
    // (see AccompanimentEngine::LayerInstrumentMode) - lets each one sound
    // through its own independently loaded SoundFont bank/preset instead
    // of the built-in synth, e.g. so "harmonija" can sound like real
    // strings from a loaded bank.
    bool LoadAccompanimentLayerSoundFontBank(AccompanimentEngine::MelodicLayer layer, const std::string& sf2Path, std::string& outError);
    void UnloadAccompanimentLayerSoundFontBank(AccompanimentEngine::MelodicLayer layer);
    bool IsAccompanimentLayerSoundFontBankLoaded(AccompanimentEngine::MelodicLayer layer) const;
    std::string GetAccompanimentLayerSoundFontBankName(AccompanimentEngine::MelodicLayer layer) const;
    int GetAccompanimentLayerSoundFontPresetCount(AccompanimentEngine::MelodicLayer layer) const;
    std::string GetAccompanimentLayerSoundFontPresetName(AccompanimentEngine::MelodicLayer layer, int presetIndex) const;
    bool SelectAccompanimentLayerSoundFontPreset(AccompanimentEngine::MelodicLayer layer, int presetIndex);
    int GetSelectedAccompanimentLayerSoundFontPresetIndex(AccompanimentEngine::MelodicLayer layer) const;
    void SetAccompanimentLayerInstrumentMode(AccompanimentEngine::MelodicLayer layer, AccompanimentEngine::LayerInstrumentMode mode);
    AccompanimentEngine::LayerInstrumentMode GetAccompanimentLayerInstrumentMode(AccompanimentEngine::MelodicLayer layer) const;

    // Per-layer VST3 instrument, same idea as the per-layer SoundFont
    // methods above.
    bool LoadAccompanimentLayerVstInstrument(AccompanimentEngine::MelodicLayer layer, const std::string& modulePath, std::string& outError);
    void UnloadAccompanimentLayerVstInstrument(AccompanimentEngine::MelodicLayer layer);
    bool IsAccompanimentLayerVstInstrumentLoaded(AccompanimentEngine::MelodicLayer layer) const;
    std::string GetAccompanimentLayerVstInstrumentName(AccompanimentEngine::MelodicLayer layer) const;

    // How many chord tones the accompaniment's kontra/harmonija layers
    // actually sound (see ChordVoicing in AccompanimentEngine.h) - never
    // affects bass. Takes effect immediately.
    void SetAccompanimentChordVoicing(ChordVoicing voicing);
    ChordVoicing GetAccompanimentChordVoicing() const;

private:
    Synth synth_;
    MidiInput midiInput_;
    VstHost vstHost_;
    Sequencer sequencer_;
    SoundFontHost soundFontHost_;
    Arranger arranger_;
    MultiTrackSequencer multiTrack_;
    AccompanimentEngine accompaniment_;
    std::atomic<bool> autoThirdEnabled_{false};
    std::atomic<bool> autoThirdUpper_{true};
    std::atomic<int> autoThirdScaleRoot_{0};    // pitch class 0-11, default C
    std::atomic<int> autoThirdScaleType_{static_cast<int>(ScaleType::Major)}; // stored as int for std::atomic simplicity; see ScaleType in Scale.h
    void* stream_ = nullptr;          // opaque PaStream*, kept out of this header on purpose
    void* callbackContext_ = nullptr; // opaque CallbackContext*, see AudioEngine.cpp

    // Chord capture state (see SetChordCaptureArmed/TryTakeCapturedChord).
    // heldNotes_ tracks what's down right now (while armed); peakNotes_ is
    // the largest heldNotes_ has grown to since it was last empty; the
    // moment heldNotes_ empties out again, peakNotes_ becomes capturedChord_
    // and is cleared, ready to grow again for the next chord. All three are
    // (pitch, velocity) pairs in first-pressed order. NoteOnLive/NoteOffLive
    // run on whichever thread the live note source calls from (the UI thread
    // for computer keyboard, RtMidi's background thread for MIDI hardware),
    // so the mutex guards against those two racing each other - never
    // against the audio thread, which never touches this state.
    std::atomic<bool> chordCaptureArmed_{false};
    std::mutex chordCaptureMutex_;
    std::vector<std::pair<int, float>> chordCaptureHeldNotes_;
    std::vector<std::pair<int, float>> chordCapturePeakNotes_;
    std::vector<std::pair<int, float>> chordCaptureCapturedChord_;
};
