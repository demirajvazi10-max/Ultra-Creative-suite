#include "AudioEngineApi.h"

#include <algorithm>
#include <cstring>
#include <string>
#include <vector>

#include "AudioEngine.h"
#include "Sequencer.h"

namespace
{
    AudioEngine& GetEngine()
    {
        static AudioEngine engine;
        return engine;
    }
}

bool UC_Init()
{
    return GetEngine().Init();
}

void UC_Shutdown()
{
    GetEngine().Shutdown();
}

bool UC_Start()
{
    return GetEngine().Start();
}

void UC_Stop()
{
    GetEngine().Stop();
}

void UC_NoteOn(int midiNote, float velocity)
{
    // The computer keyboard is a genuinely LIVE note source, so it goes
    // through *Live (see AudioEngine.h) rather than plain NoteOn - that's
    // what lets chord capture observe it.
    GetEngine().NoteOnLive(midiNote, velocity);
}

void UC_NoteOff(int midiNote)
{
    GetEngine().NoteOffLive(midiNote);
}

void UC_SetChordCaptureArmed(bool armed)
{
    GetEngine().SetChordCaptureArmed(armed);
}

bool UC_IsChordCaptureArmed()
{
    return GetEngine().IsChordCaptureArmed();
}

int32_t UC_TryTakeCapturedChord(int32_t* notesOut, float* velocitiesOut, int32_t maxNotes)
{
    if (notesOut == nullptr || velocitiesOut == nullptr || maxNotes <= 0)
    {
        return 0;
    }

    std::vector<int> notes;
    std::vector<float> velocities;
    if (!GetEngine().TryTakeCapturedChord(notes, velocities))
    {
        return 0;
    }

    int32_t count = static_cast<int32_t>(notes.size());
    int32_t copyCount = std::min(count, maxNotes);
    for (int32_t i = 0; i < copyCount; ++i)
    {
        notesOut[i] = notes[static_cast<size_t>(i)];
        velocitiesOut[i] = velocities[static_cast<size_t>(i)];
    }
    return count;
}

int UC_GetMidiPortCount()
{
    return static_cast<int>(GetEngine().ListMidiPorts().size());
}

bool UC_GetMidiPortName(int index, char* buffer, int bufferSize)
{
    if (index < 0 || buffer == nullptr || bufferSize <= 0)
    {
        return false;
    }

    auto ports = GetEngine().ListMidiPorts();
    if (static_cast<size_t>(index) >= ports.size())
    {
        return false;
    }

    const std::string& name = ports[static_cast<size_t>(index)];
    size_t copyLength = name.size();
    if (copyLength >= static_cast<size_t>(bufferSize))
    {
        copyLength = static_cast<size_t>(bufferSize) - 1;
    }
    std::memcpy(buffer, name.data(), copyLength);
    buffer[copyLength] = '\0';
    return true;
}

bool UC_OpenMidiPort(int index)
{
    if (index < 0)
    {
        return false;
    }
    return GetEngine().OpenMidiPort(static_cast<unsigned int>(index));
}

void UC_CloseMidiPort()
{
    GetEngine().CloseMidiPort();
}

bool UC_IsMidiPortOpen()
{
    return GetEngine().IsMidiPortOpen();
}

namespace
{
    // Shared by UC_LoadVstInstrument and UC_GetVstInstrumentName: copies a
    // std::string into a caller-supplied ANSI buffer, truncating (but always
    // null-terminating) if it doesn't fit.
    void CopyToBuffer(const std::string& text, char* buffer, int bufferSize)
    {
        if (buffer == nullptr || bufferSize <= 0)
        {
            return;
        }
        size_t copyLength = text.size();
        if (copyLength >= static_cast<size_t>(bufferSize))
        {
            copyLength = static_cast<size_t>(bufferSize) - 1;
        }
        std::memcpy(buffer, text.data(), copyLength);
        buffer[copyLength] = '\0';
    }
}

bool UC_LoadVstInstrument(const char* modulePath, char* errorBuffer, int errorBufferSize)
{
    if (modulePath == nullptr)
    {
        CopyToBuffer("Nije prosledjena putanja do .vst3 fajla.", errorBuffer, errorBufferSize);
        return false;
    }

    std::string error;
    bool ok = GetEngine().LoadVstInstrument(modulePath, error);
    if (!ok)
    {
        CopyToBuffer(error, errorBuffer, errorBufferSize);
    }
    return ok;
}

void UC_UnloadVstInstrument()
{
    GetEngine().UnloadVstInstrument();
}

bool UC_IsVstInstrumentLoaded()
{
    return GetEngine().IsVstInstrumentLoaded();
}

bool UC_GetVstInstrumentName(char* buffer, int bufferSize)
{
    if (!GetEngine().IsVstInstrumentLoaded())
    {
        return false;
    }
    CopyToBuffer(GetEngine().GetVstInstrumentName(), buffer, bufferSize);
    return true;
}

void UC_Sequencer_SetNotes(const UC_SequencedNote* notes, int32_t count)
{
    std::vector<SequencedNote> converted;
    if (notes != nullptr && count > 0)
    {
        converted.reserve(static_cast<size_t>(count));
        for (int32_t i = 0; i < count; ++i)
        {
            SequencedNote n;
            n.startBeat = notes[i].startBeat;
            n.lengthBeats = notes[i].lengthBeats;
            n.pitch = notes[i].pitch;
            n.velocity = notes[i].velocity;
            converted.push_back(n);
        }
    }
    GetEngine().SetSequencerNotes(converted.data(), static_cast<int>(converted.size()));
}

void UC_Sequencer_SetTempoBpm(double bpm)
{
    GetEngine().SetSequencerTempoBpm(bpm);
}

void UC_Sequencer_SetLoopLengthBeats(double beats)
{
    GetEngine().SetSequencerLoopLengthBeats(beats);
}

void UC_Sequencer_Play()
{
    GetEngine().PlaySequencer();
}

void UC_Sequencer_Stop()
{
    GetEngine().StopSequencer();
}

bool UC_Sequencer_IsPlaying()
{
    return GetEngine().IsSequencerPlaying();
}

bool UC_LoadSoundFontBank(const char* sf2Path, char* errorBuffer, int errorBufferSize)
{
    if (sf2Path == nullptr)
    {
        CopyToBuffer("Nije prosledjena putanja do .sf2 fajla.", errorBuffer, errorBufferSize);
        return false;
    }

    std::string error;
    bool ok = GetEngine().LoadSoundFontBank(sf2Path, error);
    if (!ok)
    {
        CopyToBuffer(error, errorBuffer, errorBufferSize);
    }
    return ok;
}

void UC_UnloadSoundFontBank()
{
    GetEngine().UnloadSoundFontBank();
}

bool UC_IsSoundFontBankLoaded()
{
    return GetEngine().IsSoundFontBankLoaded();
}

bool UC_GetSoundFontBankName(char* buffer, int bufferSize)
{
    if (!GetEngine().IsSoundFontBankLoaded())
    {
        return false;
    }
    CopyToBuffer(GetEngine().GetSoundFontBankName(), buffer, bufferSize);
    return true;
}

int UC_GetSoundFontPresetCount()
{
    return GetEngine().GetSoundFontPresetCount();
}

bool UC_GetSoundFontPresetName(int presetIndex, char* buffer, int bufferSize)
{
    if (presetIndex < 0 || presetIndex >= GetEngine().GetSoundFontPresetCount())
    {
        return false;
    }
    CopyToBuffer(GetEngine().GetSoundFontPresetName(presetIndex), buffer, bufferSize);
    return true;
}

bool UC_SelectSoundFontPreset(int presetIndex)
{
    return GetEngine().SelectSoundFontPreset(presetIndex);
}

int UC_GetSelectedSoundFontPresetIndex()
{
    return GetEngine().GetSelectedSoundFontPresetIndex();
}

void UC_SetAutoThirdEnabled(bool enabled)
{
    GetEngine().SetAutoThirdEnabled(enabled);
}

bool UC_IsAutoThirdEnabled()
{
    return GetEngine().IsAutoThirdEnabled();
}

void UC_SetAutoThirdUpper(bool upper)
{
    GetEngine().SetAutoThirdUpper(upper);
}

bool UC_IsAutoThirdUpper()
{
    return GetEngine().IsAutoThirdUpper();
}

void UC_SetAutoThirdScale(int32_t rootPitchClass, int32_t scaleType)
{
    GetEngine().SetAutoThirdScale(rootPitchClass, static_cast<ScaleType>(scaleType));
}

int32_t UC_GetAutoThirdScaleRoot()
{
    return GetEngine().GetAutoThirdScaleRoot();
}

int32_t UC_GetAutoThirdScaleType()
{
    return static_cast<int32_t>(GetEngine().GetAutoThirdScaleType());
}

int32_t UC_Arranger_AddPattern(const char* name, double lengthBeats,
                                const UC_SequencedNote* notes, int32_t count)
{
    std::vector<SequencedNote> converted;
    if (notes != nullptr && count > 0)
    {
        converted.reserve(static_cast<size_t>(count));
        for (int32_t i = 0; i < count; ++i)
        {
            SequencedNote n;
            n.startBeat = notes[i].startBeat;
            n.lengthBeats = notes[i].lengthBeats;
            n.pitch = notes[i].pitch;
            n.velocity = notes[i].velocity;
            converted.push_back(n);
        }
    }
    std::string patternName = (name != nullptr) ? std::string(name) : std::string();
    return static_cast<int32_t>(GetEngine().AddArrangerPattern(
        patternName, lengthBeats, converted.data(), static_cast<int>(converted.size())));
}

void UC_Arranger_RemovePattern(int32_t patternIndex)
{
    GetEngine().RemoveArrangerPattern(patternIndex);
}

int32_t UC_Arranger_GetPatternCount()
{
    return static_cast<int32_t>(GetEngine().GetArrangerPatternCount());
}

bool UC_Arranger_GetPatternName(int32_t patternIndex, char* buffer, int32_t bufferSize)
{
    if (patternIndex < 0 || patternIndex >= GetEngine().GetArrangerPatternCount())
    {
        return false;
    }
    CopyToBuffer(GetEngine().GetArrangerPatternName(patternIndex), buffer, bufferSize);
    return true;
}

double UC_Arranger_GetPatternLengthBeats(int32_t patternIndex)
{
    return GetEngine().GetArrangerPatternLengthBeats(patternIndex);
}

int32_t UC_Arranger_GetPatternNoteCount(int32_t patternIndex)
{
    return static_cast<int32_t>(GetEngine().GetArrangerPatternNoteCount(patternIndex));
}

bool UC_Arranger_GetPatternNotes(int32_t patternIndex, UC_SequencedNote* outNotes, int32_t maxCount)
{
    if (outNotes == nullptr || maxCount <= 0)
    {
        return false;
    }
    if (patternIndex < 0 || patternIndex >= GetEngine().GetArrangerPatternCount())
    {
        return false; // out-of-range index
    }

    std::vector<SequencedNote> notes = GetEngine().GetArrangerPatternNotes(patternIndex);
    int32_t count = static_cast<int32_t>(notes.size()) < maxCount ? static_cast<int32_t>(notes.size()) : maxCount;
    for (int32_t i = 0; i < count; ++i)
    {
        outNotes[i].startBeat = notes[i].startBeat;
        outNotes[i].lengthBeats = notes[i].lengthBeats;
        outNotes[i].pitch = notes[i].pitch;
        outNotes[i].velocity = notes[i].velocity;
    }
    return true; // valid index; count may legitimately be 0 for an empty pattern
}

void UC_Arranger_AppendToOrder(int32_t patternIndex)
{
    GetEngine().AppendToArrangerOrder(patternIndex);
}

void UC_Arranger_RemoveLastFromOrder()
{
    GetEngine().RemoveLastFromArrangerOrder();
}

void UC_Arranger_ClearOrder()
{
    GetEngine().ClearArrangerOrder();
}

int32_t UC_Arranger_GetOrderCount()
{
    return static_cast<int32_t>(GetEngine().GetArrangerOrderCount());
}

int32_t UC_Arranger_GetOrderPatternIndexAt(int32_t orderPosition)
{
    return static_cast<int32_t>(GetEngine().GetArrangerOrderPatternIndexAt(orderPosition));
}

void UC_Arranger_SetTempoBpm(double bpm)
{
    GetEngine().SetArrangerTempoBpm(bpm);
}

double UC_Arranger_GetTempoBpm()
{
    return GetEngine().GetArrangerTempoBpm();
}

void UC_Arranger_Play()
{
    GetEngine().PlayArranger();
}

void UC_Arranger_Stop()
{
    GetEngine().StopArranger();
}

bool UC_Arranger_IsPlaying()
{
    return GetEngine().IsArrangerPlaying();
}

int32_t UC_Arranger_GetCurrentOrderPosition()
{
    return static_cast<int32_t>(GetEngine().GetArrangerCurrentOrderPosition());
}

int32_t UC_MultiTrack_AddTrack(const char* name)
{
    return static_cast<int32_t>(GetEngine().AddTrack(name != nullptr ? std::string(name) : std::string()));
}

void UC_MultiTrack_RemoveTrack(int32_t trackIndex)
{
    GetEngine().RemoveTrack(trackIndex);
}

int32_t UC_MultiTrack_GetTrackCount()
{
    return static_cast<int32_t>(GetEngine().GetTrackCount());
}

bool UC_MultiTrack_GetTrackName(int32_t trackIndex, char* buffer, int32_t bufferSize)
{
    if (trackIndex < 0 || trackIndex >= GetEngine().GetTrackCount())
    {
        return false;
    }
    CopyToBuffer(GetEngine().GetTrackName(trackIndex), buffer, bufferSize);
    return true;
}

bool UC_MultiTrack_SetTrackName(int32_t trackIndex, const char* name)
{
    if (trackIndex < 0 || trackIndex >= GetEngine().GetTrackCount())
    {
        return false;
    }
    GetEngine().SetTrackName(trackIndex, name != nullptr ? std::string(name) : std::string());
    return true;
}

bool UC_MultiTrack_SetTrackNotes(int32_t trackIndex, const UC_SequencedNote* notes, int32_t count)
{
    if (trackIndex < 0 || trackIndex >= GetEngine().GetTrackCount())
    {
        return false;
    }
    std::vector<SequencedNote> converted;
    if (notes != nullptr && count > 0)
    {
        converted.reserve(static_cast<size_t>(count));
        for (int32_t i = 0; i < count; ++i)
        {
            SequencedNote n;
            n.startBeat = notes[i].startBeat;
            n.lengthBeats = notes[i].lengthBeats;
            n.pitch = notes[i].pitch;
            n.velocity = notes[i].velocity;
            converted.push_back(n);
        }
    }
    GetEngine().SetTrackNotes(trackIndex, converted.data(), static_cast<int>(converted.size()));
    return true;
}

bool UC_MultiTrack_SetTrackVolume(int32_t trackIndex, float volume)
{
    if (trackIndex < 0 || trackIndex >= GetEngine().GetTrackCount())
    {
        return false;
    }
    GetEngine().SetTrackVolume(trackIndex, volume);
    return true;
}

float UC_MultiTrack_GetTrackVolume(int32_t trackIndex)
{
    return GetEngine().GetTrackVolume(trackIndex);
}

bool UC_MultiTrack_SetTrackPan(int32_t trackIndex, float pan)
{
    if (trackIndex < 0 || trackIndex >= GetEngine().GetTrackCount())
    {
        return false;
    }
    GetEngine().SetTrackPan(trackIndex, pan);
    return true;
}

float UC_MultiTrack_GetTrackPan(int32_t trackIndex)
{
    return GetEngine().GetTrackPan(trackIndex);
}

bool UC_MultiTrack_SetTrackMute(int32_t trackIndex, bool mute)
{
    if (trackIndex < 0 || trackIndex >= GetEngine().GetTrackCount())
    {
        return false;
    }
    GetEngine().SetTrackMute(trackIndex, mute);
    return true;
}

bool UC_MultiTrack_IsTrackMuted(int32_t trackIndex)
{
    return GetEngine().IsTrackMuted(trackIndex);
}

bool UC_MultiTrack_SetTrackSolo(int32_t trackIndex, bool solo)
{
    if (trackIndex < 0 || trackIndex >= GetEngine().GetTrackCount())
    {
        return false;
    }
    GetEngine().SetTrackSolo(trackIndex, solo);
    return true;
}

bool UC_MultiTrack_IsTrackSoloed(int32_t trackIndex)
{
    return GetEngine().IsTrackSoloed(trackIndex);
}

bool UC_MultiTrack_SetTrackInstrumentMode(int32_t trackIndex, int32_t mode)
{
    if (trackIndex < 0 || trackIndex >= GetEngine().GetTrackCount())
    {
        return false;
    }
    MultiTrackSequencer::InstrumentMode modeEnum;
    switch (mode)
    {
    case UC_TrackInstrumentMode_SoundFont: modeEnum = MultiTrackSequencer::InstrumentMode::SoundFont; break;
    case UC_TrackInstrumentMode_Vst: modeEnum = MultiTrackSequencer::InstrumentMode::Vst; break;
    default: modeEnum = MultiTrackSequencer::InstrumentMode::BuiltInSynth; break;
    }
    GetEngine().SetTrackInstrumentMode(trackIndex, modeEnum);
    return true;
}

int32_t UC_MultiTrack_GetTrackInstrumentMode(int32_t trackIndex)
{
    switch (GetEngine().GetTrackInstrumentMode(trackIndex))
    {
    case MultiTrackSequencer::InstrumentMode::SoundFont: return UC_TrackInstrumentMode_SoundFont;
    case MultiTrackSequencer::InstrumentMode::Vst: return UC_TrackInstrumentMode_Vst;
    default: return UC_TrackInstrumentMode_BuiltInSynth;
    }
}

bool UC_MultiTrack_LoadTrackSoundFontBank(int32_t trackIndex, const char* sf2Path,
                                           char* errorBuffer, int32_t errorBufferSize)
{
    if (trackIndex < 0 || trackIndex >= GetEngine().GetTrackCount())
    {
        CopyToBuffer("Nevažeći indeks trake.", errorBuffer, errorBufferSize);
        return false;
    }
    if (sf2Path == nullptr)
    {
        CopyToBuffer("Nije prosledjena putanja do .sf2 fajla.", errorBuffer, errorBufferSize);
        return false;
    }

    std::string error;
    bool ok = GetEngine().LoadTrackSoundFontBank(trackIndex, sf2Path, error);
    if (!ok)
    {
        CopyToBuffer(error, errorBuffer, errorBufferSize);
    }
    return ok;
}

void UC_MultiTrack_UnloadTrackSoundFontBank(int32_t trackIndex)
{
    GetEngine().UnloadTrackSoundFontBank(trackIndex);
}

bool UC_MultiTrack_IsTrackSoundFontBankLoaded(int32_t trackIndex)
{
    return GetEngine().IsTrackSoundFontBankLoaded(trackIndex);
}

bool UC_MultiTrack_GetTrackSoundFontBankName(int32_t trackIndex, char* buffer, int32_t bufferSize)
{
    if (!GetEngine().IsTrackSoundFontBankLoaded(trackIndex))
    {
        return false;
    }
    CopyToBuffer(GetEngine().GetTrackSoundFontBankName(trackIndex), buffer, bufferSize);
    return true;
}

int32_t UC_MultiTrack_GetTrackSoundFontPresetCount(int32_t trackIndex)
{
    return static_cast<int32_t>(GetEngine().GetTrackSoundFontPresetCount(trackIndex));
}

bool UC_MultiTrack_GetTrackSoundFontPresetName(int32_t trackIndex, int32_t presetIndex,
                                                char* buffer, int32_t bufferSize)
{
    if (presetIndex < 0 || presetIndex >= GetEngine().GetTrackSoundFontPresetCount(trackIndex))
    {
        return false;
    }
    CopyToBuffer(GetEngine().GetTrackSoundFontPresetName(trackIndex, presetIndex), buffer, bufferSize);
    return true;
}

bool UC_MultiTrack_SelectTrackSoundFontPreset(int32_t trackIndex, int32_t presetIndex)
{
    return GetEngine().SelectTrackSoundFontPreset(trackIndex, presetIndex);
}

int32_t UC_MultiTrack_GetSelectedTrackSoundFontPresetIndex(int32_t trackIndex)
{
    return static_cast<int32_t>(GetEngine().GetSelectedTrackSoundFontPresetIndex(trackIndex));
}

bool UC_MultiTrack_LoadTrackVstInstrument(int32_t trackIndex, const char* modulePath,
                                           char* errorBuffer, int32_t errorBufferSize)
{
    if (trackIndex < 0 || trackIndex >= GetEngine().GetTrackCount())
    {
        CopyToBuffer("Nevažeći indeks trake.", errorBuffer, errorBufferSize);
        return false;
    }
    if (modulePath == nullptr)
    {
        CopyToBuffer("Nije prosledjena putanja do .vst3 fajla.", errorBuffer, errorBufferSize);
        return false;
    }

    std::string error;
    bool ok = GetEngine().LoadTrackVstInstrument(trackIndex, modulePath, error);
    if (!ok)
    {
        CopyToBuffer(error, errorBuffer, errorBufferSize);
    }
    return ok;
}

void UC_MultiTrack_UnloadTrackVstInstrument(int32_t trackIndex)
{
    GetEngine().UnloadTrackVstInstrument(trackIndex);
}

bool UC_MultiTrack_IsTrackVstInstrumentLoaded(int32_t trackIndex)
{
    return GetEngine().IsTrackVstInstrumentLoaded(trackIndex);
}

bool UC_MultiTrack_GetTrackVstInstrumentName(int32_t trackIndex, char* buffer, int32_t bufferSize)
{
    if (!GetEngine().IsTrackVstInstrumentLoaded(trackIndex))
    {
        return false;
    }
    CopyToBuffer(GetEngine().GetTrackVstInstrumentName(trackIndex), buffer, bufferSize);
    return true;
}

void UC_MultiTrack_SetTempoBpm(double bpm)
{
    GetEngine().SetMultiTrackTempoBpm(bpm);
}

double UC_MultiTrack_GetTempoBpm()
{
    return GetEngine().GetMultiTrackTempoBpm();
}

void UC_MultiTrack_SetLoopLengthBeats(double beats)
{
    GetEngine().SetMultiTrackLoopLengthBeats(beats);
}

double UC_MultiTrack_GetLoopLengthBeats()
{
    return GetEngine().GetMultiTrackLoopLengthBeats();
}

void UC_MultiTrack_Play()
{
    GetEngine().PlayMultiTrack();
}

void UC_MultiTrack_Stop()
{
    GetEngine().StopMultiTrack();
}

bool UC_MultiTrack_IsPlaying()
{
    return GetEngine().IsMultiTrackPlaying();
}

bool UC_MultiTrack_RenderToWav(const char* outPath, double durationSeconds,
                                char* errorBuffer, int32_t errorBufferSize)
{
    if (outPath == nullptr)
    {
        CopyToBuffer("Nije prosledjena putanja za izvoz.", errorBuffer, errorBufferSize);
        return false;
    }

    std::string error;
    bool ok = GetEngine().RenderMultiTrackToWav(outPath, durationSeconds, error);
    if (!ok)
    {
        CopyToBuffer(error, errorBuffer, errorBufferSize);
    }
    return ok;
}

int32_t UC_Accompaniment_GetStyleCount()
{
    return static_cast<int32_t>(GetEngine().GetAccompanimentStyleCount());
}

bool UC_Accompaniment_GetStyleName(int32_t index, char* buffer, int32_t bufferSize)
{
    std::string name = GetEngine().GetAccompanimentStyleName(index);
    if (name.empty())
    {
        return false;
    }
    CopyToBuffer(name, buffer, bufferSize);
    return true;
}

bool UC_Accompaniment_SetStyleIndex(int32_t index)
{
    return GetEngine().SetAccompanimentStyleIndex(index);
}

int32_t UC_Accompaniment_GetSelectedStyleIndex()
{
    return static_cast<int32_t>(GetEngine().GetSelectedAccompanimentStyleIndex());
}

void UC_Accompaniment_SetLayerEnabled(int32_t layer, bool enabled)
{
    switch (layer)
    {
    case UC_AccompanimentLayer_Drums: GetEngine().SetAccompanimentDrumsEnabled(enabled); break;
    case UC_AccompanimentLayer_Bass: GetEngine().SetAccompanimentBassEnabled(enabled); break;
    case UC_AccompanimentLayer_Kontra: GetEngine().SetAccompanimentKontraEnabled(enabled); break;
    case UC_AccompanimentLayer_Harmonija: GetEngine().SetAccompanimentHarmonijaEnabled(enabled); break;
    default: break;
    }
}

bool UC_Accompaniment_IsLayerEnabled(int32_t layer)
{
    switch (layer)
    {
    case UC_AccompanimentLayer_Drums: return GetEngine().AreAccompanimentDrumsEnabled();
    case UC_AccompanimentLayer_Bass: return GetEngine().IsAccompanimentBassEnabled();
    case UC_AccompanimentLayer_Kontra: return GetEngine().IsAccompanimentKontraEnabled();
    case UC_AccompanimentLayer_Harmonija: return GetEngine().IsAccompanimentHarmonijaEnabled();
    default: return false;
    }
}

void UC_Accompaniment_SetTempoBpm(double bpm)
{
    GetEngine().SetAccompanimentTempoBpm(bpm);
}

double UC_Accompaniment_GetTempoBpm()
{
    return GetEngine().GetAccompanimentTempoBpm();
}

void UC_Accompaniment_Play()
{
    GetEngine().PlayAccompaniment();
}

void UC_Accompaniment_Stop()
{
    GetEngine().StopAccompaniment();
}

bool UC_Accompaniment_IsPlaying()
{
    return GetEngine().IsAccompanimentPlaying();
}

void UC_Accompaniment_SetChordInputAutoFromKeyboard(bool autoFromKeyboard)
{
    GetEngine().SetAccompanimentChordInputAutoFromKeyboard(autoFromKeyboard);
}

bool UC_Accompaniment_IsChordInputAutoFromKeyboard()
{
    return GetEngine().IsAccompanimentChordInputAutoFromKeyboard();
}

void UC_Accompaniment_SetSplitPoint(int32_t midiNote)
{
    GetEngine().SetAccompanimentSplitPoint(midiNote);
}

int32_t UC_Accompaniment_GetSplitPoint()
{
    return static_cast<int32_t>(GetEngine().GetAccompanimentSplitPoint());
}

void UC_Accompaniment_SetManualChord(int32_t rootPitchClass, int32_t quality)
{
    GetEngine().SetAccompanimentManualChord(rootPitchClass, static_cast<ChordQuality>(quality));
}

int32_t UC_Accompaniment_GetCurrentChordRootPitchClass()
{
    return static_cast<int32_t>(GetEngine().GetAccompanimentCurrentChordRootPitchClass());
}

int32_t UC_Accompaniment_GetCurrentChordQuality()
{
    return static_cast<int32_t>(GetEngine().GetAccompanimentCurrentChordQuality());
}

namespace
{
    AccompanimentEngine::MelodicLayer ToMelodicLayer(int32_t layer)
    {
        switch (layer)
        {
        case UC_AccompanimentMelodicLayer_Kontra: return AccompanimentEngine::MelodicLayer::Kontra;
        case UC_AccompanimentMelodicLayer_Harmonija: return AccompanimentEngine::MelodicLayer::Harmonija;
        case UC_AccompanimentMelodicLayer_Bass:
        default: return AccompanimentEngine::MelodicLayer::Bass;
        }
    }
}

bool UC_Accompaniment_LoadLayerSoundFontBank(int32_t layer, const char* sf2Path, char* errorBuffer, int32_t errorBufferSize)
{
    if (sf2Path == nullptr)
    {
        CopyToBuffer("Nije prosledjena putanja do .sf2 fajla.", errorBuffer, errorBufferSize);
        return false;
    }

    std::string error;
    bool ok = GetEngine().LoadAccompanimentLayerSoundFontBank(ToMelodicLayer(layer), sf2Path, error);
    if (!ok)
    {
        CopyToBuffer(error, errorBuffer, errorBufferSize);
    }
    return ok;
}

void UC_Accompaniment_UnloadLayerSoundFontBank(int32_t layer)
{
    GetEngine().UnloadAccompanimentLayerSoundFontBank(ToMelodicLayer(layer));
}

bool UC_Accompaniment_IsLayerSoundFontBankLoaded(int32_t layer)
{
    return GetEngine().IsAccompanimentLayerSoundFontBankLoaded(ToMelodicLayer(layer));
}

bool UC_Accompaniment_GetLayerSoundFontBankName(int32_t layer, char* buffer, int32_t bufferSize)
{
    std::string name = GetEngine().GetAccompanimentLayerSoundFontBankName(ToMelodicLayer(layer));
    if (name.empty())
    {
        return false;
    }
    CopyToBuffer(name, buffer, bufferSize);
    return true;
}

int32_t UC_Accompaniment_GetLayerSoundFontPresetCount(int32_t layer)
{
    return static_cast<int32_t>(GetEngine().GetAccompanimentLayerSoundFontPresetCount(ToMelodicLayer(layer)));
}

bool UC_Accompaniment_GetLayerSoundFontPresetName(int32_t layer, int32_t presetIndex, char* buffer, int32_t bufferSize)
{
    std::string name = GetEngine().GetAccompanimentLayerSoundFontPresetName(ToMelodicLayer(layer), presetIndex);
    if (name.empty())
    {
        return false;
    }
    CopyToBuffer(name, buffer, bufferSize);
    return true;
}

bool UC_Accompaniment_SelectLayerSoundFontPreset(int32_t layer, int32_t presetIndex)
{
    return GetEngine().SelectAccompanimentLayerSoundFontPreset(ToMelodicLayer(layer), presetIndex);
}

int32_t UC_Accompaniment_GetSelectedLayerSoundFontPresetIndex(int32_t layer)
{
    return static_cast<int32_t>(GetEngine().GetSelectedAccompanimentLayerSoundFontPresetIndex(ToMelodicLayer(layer)));
}

void UC_Accompaniment_SetLayerInstrumentMode(int32_t layer, int32_t mode)
{
    GetEngine().SetAccompanimentLayerInstrumentMode(ToMelodicLayer(layer), static_cast<AccompanimentEngine::LayerInstrumentMode>(mode));
}

int32_t UC_Accompaniment_GetLayerInstrumentMode(int32_t layer)
{
    return static_cast<int32_t>(GetEngine().GetAccompanimentLayerInstrumentMode(ToMelodicLayer(layer)));
}

bool UC_Accompaniment_LoadLayerVstInstrument(int32_t layer, const char* modulePath, char* errorBuffer, int32_t errorBufferSize)
{
    if (modulePath == nullptr)
    {
        CopyToBuffer("Nije prosledjena putanja do .vst3 fajla.", errorBuffer, errorBufferSize);
        return false;
    }

    std::string error;
    bool ok = GetEngine().LoadAccompanimentLayerVstInstrument(ToMelodicLayer(layer), modulePath, error);
    if (!ok)
    {
        CopyToBuffer(error, errorBuffer, errorBufferSize);
    }
    return ok;
}

void UC_Accompaniment_UnloadLayerVstInstrument(int32_t layer)
{
    GetEngine().UnloadAccompanimentLayerVstInstrument(ToMelodicLayer(layer));
}

bool UC_Accompaniment_IsLayerVstInstrumentLoaded(int32_t layer)
{
    return GetEngine().IsAccompanimentLayerVstInstrumentLoaded(ToMelodicLayer(layer));
}

bool UC_Accompaniment_GetLayerVstInstrumentName(int32_t layer, char* buffer, int32_t bufferSize)
{
    std::string name = GetEngine().GetAccompanimentLayerVstInstrumentName(ToMelodicLayer(layer));
    if (name.empty())
    {
        return false;
    }
    CopyToBuffer(name, buffer, bufferSize);
    return true;
}

void UC_Accompaniment_SetChordVoicing(int32_t voicing)
{
    GetEngine().SetAccompanimentChordVoicing(static_cast<ChordVoicing>(voicing));
}

int32_t UC_Accompaniment_GetChordVoicing()
{
    return static_cast<int32_t>(GetEngine().GetAccompanimentChordVoicing());
}

namespace
{
    // Unpacks a UC_CustomChordHit's bit-packed chordToneMask (bit 0=root,
    // 1=third, 2=fifth, 3=seventh) into the plain index list
    // RelativePatternChordHit actually stores.
    std::vector<int> ChordToneMaskToIndices(int32_t mask)
    {
        std::vector<int> indices;
        for (int bit = 0; bit < 4; ++bit)
        {
            if ((mask & (1 << bit)) != 0)
            {
                indices.push_back(bit);
            }
        }
        return indices;
    }

    void AppendCustomChordHits(std::vector<RelativePatternChordHit>& dest,
                                const UC_CustomChordHit* hits, int32_t count)
    {
        if (hits == nullptr || count <= 0)
        {
            return;
        }
        dest.reserve(dest.size() + static_cast<size_t>(count));
        for (int32_t i = 0; i < count; ++i)
        {
            RelativePatternChordHit hit;
            hit.startBeat = hits[i].startBeat;
            hit.lengthBeats = hits[i].lengthBeats;
            hit.chordToneIndices = ChordToneMaskToIndices(hits[i].chordToneMask);
            hit.octaveOffset = hits[i].octaveOffset;
            hit.velocity = hits[i].velocity;
            dest.push_back(hit);
        }
    }
}

int32_t UC_Accompaniment_SetCustomStyle(
    const char* name,
    double tempoBpm,
    double lengthBeats,
    double beatsPerBar,
    const UC_CustomDrumHit* drumHits, int32_t drumHitCount,
    const UC_CustomChordHit* bassHits, int32_t bassHitCount,
    const UC_CustomChordHit* kontraHits, int32_t kontraHitCount,
    const UC_CustomChordHit* harmonijaHits, int32_t harmonijaHitCount,
    int32_t replaceIndex)
{
    Style style;
    style.name = name != nullptr ? name : "";
    style.suggestedTempoBpm = tempoBpm > 0.0 ? tempoBpm : 100.0;
    style.beatsPerBar = beatsPerBar > 0.0 ? beatsPerBar : 4.0;
    style.lengthBeats = lengthBeats > 0.0 ? lengthBeats : 4.0;

    if (drumHits != nullptr && drumHitCount > 0)
    {
        style.drumHits.reserve(static_cast<size_t>(drumHitCount));
        for (int32_t i = 0; i < drumHitCount; ++i)
        {
            DrumHit hit;
            hit.startBeat = drumHits[i].startBeat;
            hit.drumVoice = drumHits[i].drumVoice;
            hit.velocity = drumHits[i].velocity;
            style.drumHits.push_back(hit);
        }
    }

    AppendCustomChordHits(style.bassNotes, bassHits, bassHitCount);
    AppendCustomChordHits(style.kontraNotes, kontraHits, kontraHitCount);
    AppendCustomChordHits(style.harmonijaNotes, harmonijaHits, harmonijaHitCount);

    return static_cast<int32_t>(GetEngine().SetAccompanimentCustomStyle(style, replaceIndex));
}

bool UC_Accompaniment_RemoveCustomStyle(int32_t index)
{
    return GetEngine().RemoveAccompanimentCustomStyle(index);
}
