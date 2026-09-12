#pragma once

#include <cstdint>
#include <string>

// Hosts a single VST3 instrument plug-in and feeds it the same note on/off
// events the built-in Synth receives (from the computer keyboard or a MIDI
// device). This class only ever talks to the plug-in through the official
// Steinberg VST3 SDK hosting helpers (see thirdparty/vst3sdk) - it never
// hand-rolls the VST3 COM-style interfaces itself, to keep this as close to
// "known correct" as possible.
//
// Thread safety, matching the convention already used by Synth/MidiInput:
// NoteOn/NoteOff can be called from the UI thread or the MIDI thread, while
// RenderAdditive runs on the real-time audio thread. A small mutex protects
// the short queue of pending note events between them; nothing is allocated
// on the audio thread itself.
//
// The VST3 SDK types themselves never appear in this header (pimpl), so the
// rest of the engine - and anything that includes AudioEngine.h - never has
// to see or link against the SDK headers.
class VstHost
{
public:
    VstHost();
    ~VstHost();

    VstHost(const VstHost&) = delete;
    VstHost& operator=(const VstHost&) = delete;

    // Loads the first instrument class found in the .vst3 module at
    // 'modulePath' (either the classic single-file form or the newer bundle
    // folder form - both are handled by the SDK's own module loader).
    // Must be called from the UI thread, engine stopped or not currently
    // rendering. Returns false and fills 'outError' with a human-readable
    // reason on failure (bad path, no instrument class inside, plug-in
    // failed to initialize, ...).
    bool LoadInstrument(const std::string& modulePath, double sampleRate, int maxBlockSize, std::string& outError);

    // Safe to call even if nothing is loaded.
    void UnloadInstrument();

    bool IsLoaded() const;
    std::string GetLoadedPluginName() const;

    void NoteOn(int midiNote, float velocity);
    void NoteOff(int midiNote);

    // Audio thread only. Adds the plug-in's output on top of whatever is
    // already in 'interleavedStereoOut' (so it mixes with the built-in
    // synth instead of replacing it). 'frames' must be <= the
    // 'maxBlockSize' passed to LoadInstrument. Does nothing if no
    // instrument is loaded.
    void RenderAdditive(float* interleavedStereoOut, int frames);

private:
    struct Impl;
    Impl* impl_;
};
