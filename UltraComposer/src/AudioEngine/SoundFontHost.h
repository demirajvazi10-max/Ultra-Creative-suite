#pragma once

#include <string>

// Hosts a SoundFont (.sf2) instrument bank - a collection of GM-style
// ready-made instrument sounds (piano, strings, brass, drums, ...), the
// same idea as the built-in sound banks in Cakewalk or similar DAWs -
// using TinySoundFont (vendored under thirdparty/tsf, MIT license) to do
// the actual synthesis. Only one instrument/preset from the loaded bank
// plays at a time; switching presets is meant to feel like picking a
// different instrument, not like loading a different plug-in.
//
// Thread safety, matching the convention already used by Synth/MidiInput/
// VstHost elsewhere in this engine: a single mutex guards everything,
// including the full body of RenderAdditive, which runs on the real-time
// audio thread.
class SoundFontHost
{
public:
    SoundFontHost();
    ~SoundFontHost();

    SoundFontHost(const SoundFontHost&) = delete;
    SoundFontHost& operator=(const SoundFontHost&) = delete;

    // Loads the SoundFont bank at 'sf2Path' and selects its first preset
    // (or General MIDI's "Acoustic Grand Piano", bank 0 program 0, if the
    // bank has one). Replaces any previously loaded bank. Returns false
    // and fills 'outError' with a human-readable reason on failure.
    bool LoadBank(const std::string& sf2Path, double sampleRate, std::string& outError);

    // Safe to call even if nothing is loaded.
    void UnloadBank();

    bool IsLoaded() const;

    // Just the file name of the loaded .sf2 (not the full path), for
    // display purposes.
    std::string GetLoadedBankName() const;

    // Instruments ("presets", in SoundFont terminology) inside the loaded
    // bank - typically the 128 General MIDI instruments plus a drum kit,
    // but varies by bank. Returns 0/empty if nothing is loaded.
    int GetPresetCount() const;
    std::string GetPresetName(int presetIndex) const;

    // Switches which instrument plays. Stops any currently sounding notes
    // first (they were voiced against the old instrument and can't simply
    // carry over). Returns false if presetIndex is out of range or nothing
    // is loaded.
    bool SelectPreset(int presetIndex);
    int GetSelectedPresetIndex() const;

    void NoteOn(int midiNote, float velocity);
    void NoteOff(int midiNote);

    // Audio thread only. Adds the selected instrument's output on top of
    // whatever is already in 'interleavedStereoOut' (so it mixes with the
    // built-in synth and any loaded VST3 instrument, instead of replacing
    // them). Does nothing if no bank is loaded.
    void RenderAdditive(float* interleavedStereoOut, int frames);

private:
    struct Impl;
    Impl* impl_;
};
