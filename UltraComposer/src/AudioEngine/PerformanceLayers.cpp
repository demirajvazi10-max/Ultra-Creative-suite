#include "PerformanceLayers.h"

#include <algorithm>
#include <cstring>

bool PerformanceLayers::ValidIndex(int layerIndex) const
{
    return layerIndex >= 0 && layerIndex < kLayerCount;
}

bool PerformanceLayers::InRange(const LayerState& layer, int midiNote, int splitPoint) const
{
    switch (static_cast<PerformanceLayerRange>(layer.range.load()))
    {
    case PerformanceLayerRange::Lower: return midiNote < splitPoint;
    case PerformanceLayerRange::Upper: return midiNote >= splitPoint;
    default: return true; // Whole
    }
}

void PerformanceLayers::Prepare(double sampleRate)
{
    for (auto& layer : layers_)
    {
        layer.synth.Prepare(sampleRate);
    }
}

void PerformanceLayers::SetEnabled(int layerIndex, bool enabled)
{
    if (!ValidIndex(layerIndex)) return;
    layers_[layerIndex].enabled.store(enabled);
}

bool PerformanceLayers::IsEnabled(int layerIndex) const
{
    if (!ValidIndex(layerIndex)) return false;
    return layers_[layerIndex].enabled.load();
}

void PerformanceLayers::SetMode(int layerIndex, PerformanceLayerMode mode)
{
    if (!ValidIndex(layerIndex)) return;
    layers_[layerIndex].mode.store(static_cast<int>(mode));
}

PerformanceLayerMode PerformanceLayers::GetMode(int layerIndex) const
{
    if (!ValidIndex(layerIndex)) return PerformanceLayerMode::BuiltInSynth;
    return static_cast<PerformanceLayerMode>(layers_[layerIndex].mode.load());
}

void PerformanceLayers::SetVolume(int layerIndex, float volume)
{
    if (!ValidIndex(layerIndex)) return;
    if (volume < 0.0f) volume = 0.0f;
    if (volume > 1.5f) volume = 1.5f;
    layers_[layerIndex].volume.store(volume);
}

float PerformanceLayers::GetVolume(int layerIndex) const
{
    if (!ValidIndex(layerIndex)) return 0.0f;
    return layers_[layerIndex].volume.load();
}

void PerformanceLayers::SetOctaveOffset(int layerIndex, int octaves)
{
    if (!ValidIndex(layerIndex)) return;
    if (octaves < -2) octaves = -2;
    if (octaves > 2) octaves = 2;
    layers_[layerIndex].octaveOffset.store(octaves);
}

int PerformanceLayers::GetOctaveOffset(int layerIndex) const
{
    if (!ValidIndex(layerIndex)) return 0;
    return layers_[layerIndex].octaveOffset.load();
}

void PerformanceLayers::SetRange(int layerIndex, PerformanceLayerRange range)
{
    if (!ValidIndex(layerIndex)) return;
    layers_[layerIndex].range.store(static_cast<int>(range));
}

PerformanceLayerRange PerformanceLayers::GetRange(int layerIndex) const
{
    if (!ValidIndex(layerIndex)) return PerformanceLayerRange::Whole;
    return static_cast<PerformanceLayerRange>(layers_[layerIndex].range.load());
}

bool PerformanceLayers::LoadSoundFontBank(int layerIndex, const std::string& sf2Path, double sampleRate, std::string& outError)
{
    if (!ValidIndex(layerIndex))
    {
        outError = "Nevazeci indeks sloja.";
        return false;
    }
    bool ok = layers_[layerIndex].soundFont.LoadBank(sf2Path, sampleRate, outError);
    if (ok)
    {
        // Same convention as MultiTrackSequencer::LoadTrackSoundFontBank and
        // AccompanimentEngine::LoadLayerSoundFontBank - loading an
        // instrument immediately switches this layer to that mode, so the
        // UI's mode radio and this layer's actual dispatch never disagree.
        layers_[layerIndex].mode.store(static_cast<int>(PerformanceLayerMode::SoundFont));
    }
    return ok;
}

void PerformanceLayers::UnloadSoundFontBank(int layerIndex)
{
    if (!ValidIndex(layerIndex)) return;
    layers_[layerIndex].soundFont.UnloadBank();
}

bool PerformanceLayers::IsSoundFontBankLoaded(int layerIndex) const
{
    if (!ValidIndex(layerIndex)) return false;
    return layers_[layerIndex].soundFont.IsLoaded();
}

std::string PerformanceLayers::GetSoundFontBankName(int layerIndex) const
{
    if (!ValidIndex(layerIndex)) return {};
    return layers_[layerIndex].soundFont.GetLoadedBankName();
}

int PerformanceLayers::GetSoundFontPresetCount(int layerIndex) const
{
    if (!ValidIndex(layerIndex)) return 0;
    return layers_[layerIndex].soundFont.GetPresetCount();
}

std::string PerformanceLayers::GetSoundFontPresetName(int layerIndex, int presetIndex) const
{
    if (!ValidIndex(layerIndex)) return {};
    return layers_[layerIndex].soundFont.GetPresetName(presetIndex);
}

bool PerformanceLayers::SelectSoundFontPreset(int layerIndex, int presetIndex)
{
    if (!ValidIndex(layerIndex)) return false;
    return layers_[layerIndex].soundFont.SelectPreset(presetIndex);
}

int PerformanceLayers::GetSelectedSoundFontPresetIndex(int layerIndex) const
{
    if (!ValidIndex(layerIndex)) return -1;
    return layers_[layerIndex].soundFont.GetSelectedPresetIndex();
}

bool PerformanceLayers::LoadVstInstrument(int layerIndex, const std::string& modulePath, double sampleRate, int maxBlockSize, std::string& outError)
{
    if (!ValidIndex(layerIndex))
    {
        outError = "Nevazeci indeks sloja.";
        return false;
    }
    bool ok = layers_[layerIndex].vst.LoadInstrument(modulePath, sampleRate, maxBlockSize, outError);
    if (ok)
    {
        layers_[layerIndex].mode.store(static_cast<int>(PerformanceLayerMode::Vst));
    }
    return ok;
}

void PerformanceLayers::UnloadVstInstrument(int layerIndex)
{
    if (!ValidIndex(layerIndex)) return;
    layers_[layerIndex].vst.UnloadInstrument();
}

bool PerformanceLayers::IsVstInstrumentLoaded(int layerIndex) const
{
    if (!ValidIndex(layerIndex)) return false;
    return layers_[layerIndex].vst.IsLoaded();
}

std::string PerformanceLayers::GetVstInstrumentName(int layerIndex) const
{
    if (!ValidIndex(layerIndex)) return {};
    return layers_[layerIndex].vst.GetLoadedPluginName();
}

void PerformanceLayers::DispatchNoteOn(int midiNote, float velocity, int splitPoint)
{
    for (auto& layer : layers_)
    {
        if (!layer.enabled.load()) continue;
        if (!InRange(layer, midiNote, splitPoint)) continue;

        int outNote = midiNote + layer.octaveOffset.load() * 12;
        if (outNote < 0 || outNote > 127) continue;

        switch (static_cast<PerformanceLayerMode>(layer.mode.load()))
        {
        case PerformanceLayerMode::SoundFont: layer.soundFont.NoteOn(outNote, velocity); break;
        case PerformanceLayerMode::Vst: layer.vst.NoteOn(outNote, velocity); break;
        default: layer.synth.NoteOn(outNote, velocity); break;
        }
    }
}

void PerformanceLayers::DispatchNoteOff(int midiNote, int splitPoint)
{
    for (auto& layer : layers_)
    {
        if (!layer.enabled.load()) continue;
        if (!InRange(layer, midiNote, splitPoint)) continue;

        int outNote = midiNote + layer.octaveOffset.load() * 12;
        if (outNote < 0 || outNote > 127) continue;

        switch (static_cast<PerformanceLayerMode>(layer.mode.load()))
        {
        case PerformanceLayerMode::SoundFont: layer.soundFont.NoteOff(outNote); break;
        case PerformanceLayerMode::Vst: layer.vst.NoteOff(outNote); break;
        default: layer.synth.NoteOff(outNote); break;
        }
    }
}

void PerformanceLayers::RenderAdditive(float* interleavedStereoOut, int frames)
{
    if (frames <= 0) return;
    int clampedFrames = frames < kMaxScratchFrames ? frames : kMaxScratchFrames;

    for (auto& layer : layers_)
    {
        if (!layer.enabled.load()) continue;

        float volume = layer.volume.load();
        auto mode = static_cast<PerformanceLayerMode>(layer.mode.load());

        if (mode == PerformanceLayerMode::BuiltInSynth)
        {
            std::memset(layer.scratchMono, 0, sizeof(float) * clampedFrames);
            layer.synth.Render(layer.scratchMono, clampedFrames);
            for (int i = 0; i < clampedFrames; ++i)
            {
                interleavedStereoOut[i * 2] += layer.scratchMono[i] * volume;
                interleavedStereoOut[i * 2 + 1] += layer.scratchMono[i] * volume;
            }
        }
        else
        {
            std::memset(layer.scratchStereo, 0, sizeof(float) * clampedFrames * 2);
            if (mode == PerformanceLayerMode::Vst)
            {
                layer.vst.RenderAdditive(layer.scratchStereo, clampedFrames);
            }
            else
            {
                layer.soundFont.RenderAdditive(layer.scratchStereo, clampedFrames);
            }
            for (int i = 0; i < clampedFrames; ++i)
            {
                interleavedStereoOut[i * 2] += layer.scratchStereo[i * 2] * volume;
                interleavedStereoOut[i * 2 + 1] += layer.scratchStereo[i * 2 + 1] * volume;
            }
        }
    }
}
