#pragma once

#include <array>
#include <atomic>
#include <string>

#include "SoundFontHost.h"
#include "Synth.h"
#include "VstHost.h"

// How a performance layer picks its instrument - same 3-way idea already
// used per-track (MultiTrackSequencer::InstrumentMode) and per-accompaniment-
// layer (AccompanimentEngine::LayerInstrumentMode), kept as its own enum
// here rather than reusing either of those, since this feature is otherwise
// unrelated to both.
enum class PerformanceLayerMode
{
    BuiltInSynth = 0,
    SoundFont = 1,
    Vst = 2,
};

// Which part of the keyboard a layer responds to - this one field is what
// makes both "Layer" and "Split" (Demir's own requested real-keyboard
// features) the same mechanism instead of two separate ones: every layer
// set to Whole is a classic Layer/Dual (they all sound together, across the
// whole keyboard); one layer set to Lower and another to Upper is a classic
// Split (the keyboard is divided into two zones at the shared split point -
// see AudioEngine::SetPerformanceSplitPoint). The two combine freely, e.g.
// one Whole layer as a pad underneath a Lower/Upper split of two others.
enum class PerformanceLayerRange
{
    Whole = 0,
    Lower = 1,
    Upper = 2,
};

// Up to 3 additional instruments ("Sloj 2/3/4" in the UI) that can sound
// alongside the pre-existing single main "Banka instrumenata" instrument
// (AudioEngine's own synth_/vstHost_/soundFontHost_ - "Sloj 1", left
// completely untouched by this class, though it gained the same
// enabled/volume/octave/range knobs directly on AudioEngine so it can take
// part in a Layer/Split setup too - see AudioEngine::SetMainLayerVolume and
// friends) for every note source that already goes through AudioEngine::
// NoteOn/NoteOff (live keyboard/MIDI, the plain sequencer, and the
// arranger - see that function's own comment for why those three share one
// dispatch point).
//
// Each layer optionally transposes its own notes by whole octaves before
// playing them (e.g. a Whole-range synth pad an octave up as a bright top
// layer over an unshifted Lower-range bass) - this happens BEFORE the range
// check below reads the note as actually played, so transposing a layer
// never moves it out of (or into) its own zone. As with every other
// instrument-swap in this engine (e.g. SoundFontHost::SelectPreset "stops
// any currently sounding notes first"), changing a layer's range/octave
// while a note played through it is still held can leave that one note
// stuck or silently dropped rather than cleanly retargeted - a narrow,
// pre-existing class of rough edge in this engine, not something new.
//
// Thread safety follows the same convention as MultiTrackSequencer's
// TrackState: the array is fixed-size for the object's whole lifetime (no
// add/remove), so no container-level mutex is needed - every per-layer
// field here is a plain atomic, and SoundFontHost/VstHost are already
// internally mutex-guarded per their own class comments. Safe to configure
// from the UI thread while DispatchNoteOn/DispatchNoteOff/RenderAdditive
// run on the real-time audio thread.
class PerformanceLayers
{
public:
    static constexpr int kLayerCount = 3; // shown to the user as "Sloj 2", "Sloj 3", "Sloj 4"

    void Prepare(double sampleRate);

    void SetEnabled(int layerIndex, bool enabled);
    bool IsEnabled(int layerIndex) const;
    void SetMode(int layerIndex, PerformanceLayerMode mode);
    PerformanceLayerMode GetMode(int layerIndex) const;
    void SetVolume(int layerIndex, float volume); // 0 .. ~1.5, clamped
    float GetVolume(int layerIndex) const;
    void SetOctaveOffset(int layerIndex, int octaves); // -2..2, clamped
    int GetOctaveOffset(int layerIndex) const;
    void SetRange(int layerIndex, PerformanceLayerRange range);
    PerformanceLayerRange GetRange(int layerIndex) const;

    bool LoadSoundFontBank(int layerIndex, const std::string& sf2Path, double sampleRate, std::string& outError);
    void UnloadSoundFontBank(int layerIndex);
    bool IsSoundFontBankLoaded(int layerIndex) const;
    std::string GetSoundFontBankName(int layerIndex) const;
    int GetSoundFontPresetCount(int layerIndex) const;
    std::string GetSoundFontPresetName(int layerIndex, int presetIndex) const;
    bool SelectSoundFontPreset(int layerIndex, int presetIndex);
    int GetSelectedSoundFontPresetIndex(int layerIndex) const;

    // Same idea as the SoundFont methods above, but a VST3 plug-in - each
    // layer's plug-in is its own independent instance. 'maxBlockSize' must
    // be >= the largest 'frames' ever passed to RenderAdditive() (matches
    // kMaxScratchFrames below, same convention as every other VstHost in
    // this engine).
    bool LoadVstInstrument(int layerIndex, const std::string& modulePath, double sampleRate, int maxBlockSize, std::string& outError);
    void UnloadVstInstrument(int layerIndex);
    bool IsVstInstrumentLoaded(int layerIndex) const;
    std::string GetVstInstrumentName(int layerIndex) const;

    // Real-time audio thread (called from AudioEngine::NoteOn/NoteOff, so
    // also covers the sequencer/arranger, not just live keyboard/MIDI - see
    // that function's own comment). 'splitPoint' is AudioEngine's shared
    // performance split point (see AudioEngine::SetPerformanceSplitPoint) -
    // passed in rather than owned here so the main layer (Sloj 1, not part
    // of this class) can be range-gated against the exact same value.
    void DispatchNoteOn(int midiNote, float velocity, int splitPoint);
    void DispatchNoteOff(int midiNote, int splitPoint);

    // Audio thread only. Adds every enabled layer's output on top of
    // whatever is already in 'interleavedStereoOut'. A disabled layer, or
    // one in SoundFont/Vst mode with nothing loaded, contributes silence.
    void RenderAdditive(float* interleavedStereoOut, int frames);

private:
    // Kept in sync with AudioEngine.cpp's kMaxCallbackFrames (the real-time
    // callback's own safety cap) - same duplicated-constant convention
    // MultiTrackSequencer.h already uses for its own scratch buffers.
    static constexpr int kMaxScratchFrames = 2048;

    struct LayerState
    {
        std::atomic<bool> enabled{false};
        std::atomic<int> mode{0};          // PerformanceLayerMode, packed as int for std::atomic simplicity
        std::atomic<float> volume{1.0f};
        std::atomic<int> octaveOffset{0};
        std::atomic<int> range{0};         // PerformanceLayerRange, packed as int
        Synth synth;
        SoundFontHost soundFont;
        VstHost vst;
        float scratchMono[kMaxScratchFrames]{};
        float scratchStereo[kMaxScratchFrames * 2]{};
    };

    bool InRange(const LayerState& layer, int midiNote, int splitPoint) const;
    bool ValidIndex(int layerIndex) const;

    std::array<LayerState, kLayerCount> layers_;
};
