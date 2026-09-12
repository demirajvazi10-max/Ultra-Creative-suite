#pragma once

#include <atomic>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include "Scale.h"
#include "Sequencer.h"
#include "SoundFontHost.h"
#include "Synth.h"
#include "VstHost.h"

// Multiple simultaneous tracks, each with its own note pattern and its own
// instrument (the built-in synth, or an independently loaded SoundFont bank
// and preset), mixed together with per-track volume/pan/mute/solo. Requested
// as one combined feature ("multi-track sequencing + mixer") since a mixer
// has nothing to mix without more than one track.
//
// All tracks share one tempo and one loop length - they're playing one song
// together, not independent loops. Each track still keeps its own Sequencer
// instance internally, purely to reuse its already-tested, sample-accurate
// note on/off timing rather than duplicating that logic here: since every
// track is driven by the same tempo/loop length/frame count and was told to
// Play() at the same instant, their positions stay in lockstep automatically
// (identical deterministic arithmetic applied identically every block) -
// nothing has to explicitly keep them synchronized.
//
// Thread safety follows the same convention already used by Sequencer and
// Arranger: editing (Add/RemoveTrack, per-track setters, Play/Stop) happens
// on the UI thread; Advance()/RenderAdditive() run on the real-time audio
// thread. A single mutex guards the track list itself (so the audio thread
// never sees a track added/removed mid-iteration); per-track fast-changing
// mixer values (volume/pan/mute/solo) are plain atomics so they can be
// tweaked from the UI thread without taking that lock. Locking a mutex at
// all on the audio thread is the same accepted relaxation of strict
// real-time purism already used by Sequencer/Arranger/Synth elsewhere in
// this engine - the critical sections here are just as small.
class MultiTrackSequencer
{
public:
    enum class InstrumentMode
    {
        BuiltInSynth = 0,
        SoundFont = 1,
        Vst = 2,
    };

    // Adds a new, empty track (built-in synth, full volume, centered pan,
    // not muted/soloed, already following the current shared tempo/loop
    // length) and returns its index (0-based, matches insertion order).
    int AddTrack(const std::string& name);

    // Removes the track at 'index'. No-op if out of range.
    void RemoveTrack(int index);
    int GetTrackCount() const;

    std::string GetTrackName(int index) const;
    void SetTrackName(int index, const std::string& name);

    // Replaces the whole note pattern for the track at 'index'. Safe to
    // call while playing - takes effect immediately, same as the plain
    // Sequencer.
    void SetTrackNotes(int index, std::vector<SequencedNote> notes);

    void SetTrackVolume(int index, float volume);   // 0 .. ~1.5, clamped
    float GetTrackVolume(int index) const;
    void SetTrackPan(int index, float pan);         // -1 (left) .. 1 (right), clamped
    float GetTrackPan(int index) const;
    void SetTrackMute(int index, bool mute);
    bool IsTrackMuted(int index) const;
    // While any track is soloed, only soloed tracks are audible (their own
    // mute is ignored); with no track soloed, every unmuted track plays.
    void SetTrackSolo(int index, bool solo);
    bool IsTrackSoloed(int index) const;

    void SetTrackInstrumentMode(int index, InstrumentMode mode);
    InstrumentMode GetTrackInstrumentMode(int index) const;

    // Each track's SoundFont bank is its own independent load (even if it's
    // the same .sf2 file another track also loaded), so tracks can each
    // sound a different instrument from a bank at the same time - unlike
    // the single shared SoundFontHost used by the plain sequencer/arranger/
    // live keyboard (AudioEngine's own), which only ever plays one preset
    // at a time across the whole engine.
    bool LoadTrackSoundFontBank(int index, const std::string& sf2Path, double sampleRate, std::string& outError);
    void UnloadTrackSoundFontBank(int index);
    bool IsTrackSoundFontBankLoaded(int index) const;
    std::string GetTrackSoundFontBankName(int index) const;
    int GetTrackSoundFontPresetCount(int index) const;
    std::string GetTrackSoundFontPresetName(int index, int presetIndex) const;
    bool SelectTrackSoundFontPreset(int index, int presetIndex);
    int GetSelectedTrackSoundFontPresetIndex(int index) const;

    // Same idea as the SoundFont methods above, but a VST3 plug-in instead -
    // each track's plug-in is its own independent instance (own VstHost),
    // so several tracks can each host a different (or even the same) plug-in
    // at once. Loading one for a track immediately switches that track to
    // Vst mode. 'maxBlockSize' must be >= the largest 'frames' ever passed
    // to Advance()/RenderAdditive() (matches kMaxScratchFrames below, same
    // as every other VstHost in this engine).
    bool LoadTrackVstInstrument(int index, const std::string& modulePath, double sampleRate, int maxBlockSize, std::string& outError);
    void UnloadTrackVstInstrument(int index);
    bool IsTrackVstInstrumentLoaded(int index) const;
    std::string GetTrackVstInstrumentName(int index) const;

    // Shared by every track - see the class comment for why.
    void SetTempoBpm(double bpm);
    double GetTempoBpm() const;
    void SetLoopLengthBeats(double beats);
    double GetLoopLengthBeats() const;

    // Starts every track's transport together, from beat 0. Loops forever
    // until Stop().
    void Play();
    void Stop();
    bool IsPlaying() const;

    // Real-time audio thread. Advances every track's transport, dispatching
    // its note on/off events to that track's own instrument (applying
    // auto-third harmonization there too, matching every other note source
    // in this engine), then mixes every track's rendered output into
    // 'interleavedStereoOut' according to its volume/pan/mute/solo.
    void Advance(int frames, double sampleRate, bool autoThirdEnabled, bool autoThirdUpper,
                 int autoThirdScaleRoot, ScaleType autoThirdScaleType);
    void RenderAdditive(float* interleavedStereoOut, int frames);

private:
    // Kept in sync with AudioEngine.cpp's kMaxCallbackFrames (the real-time
    // callback's own safety cap) - each track needs a fixed, non-allocating
    // scratch buffer at least that large.
    static constexpr int kMaxScratchFrames = 2048;

    struct TrackState
    {
        std::string name;
        Sequencer sequencer;
        Synth synth;
        SoundFontHost soundFont;
        VstHost vst;
        InstrumentMode mode = InstrumentMode::BuiltInSynth;
        std::atomic<float> volume{1.0f};
        std::atomic<float> pan{0.0f};
        std::atomic<bool> mute{false};
        std::atomic<bool> solo{false};
        float scratchMono[kMaxScratchFrames]{};
        float scratchStereo[kMaxScratchFrames * 2]{};
    };

    // Routes a note on/off to whichever instrument the track is currently
    // using - shared by Advance()'s main-note and auto-third-harmony
    // dispatch, which previously duplicated this as an inline two-way
    // (synth-or-soundfont) branch; now three-way with Vst added.
    static void TrackNoteOn(TrackState& track, int pitch, float velocity);
    static void TrackNoteOff(TrackState& track, int pitch);

    mutable std::mutex tracksMutex_;
    std::vector<std::unique_ptr<TrackState>> tracks_;
    double tempoBpm_ = 120.0;
    double loopLengthBeats_ = 16.0;
    bool playing_ = false;
};
