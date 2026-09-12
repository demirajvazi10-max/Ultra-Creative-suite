#include "MultiTrackSequencer.h"

#include <cmath>
#include <cstring>

#include "Scale.h"

int MultiTrackSequencer::AddTrack(const std::string& name)
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    auto track = std::make_unique<TrackState>();
    track->name = name;
    track->sequencer.SetTempoBpm(tempoBpm_);
    track->sequencer.SetLoopLengthBeats(loopLengthBeats_);
    if (playing_)
    {
        // Joins playback immediately if a track is added mid-song. It
        // starts from beat 0 rather than wherever the other tracks
        // currently are - a minor, rare edge case (adding tracks while
        // stopped is the expected flow) rather than something worth a full
        // resync mechanism for.
        track->sequencer.Play();
    }
    tracks_.push_back(std::move(track));
    return static_cast<int>(tracks_.size()) - 1;
}

void MultiTrackSequencer::RemoveTrack(int index)
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    if (index < 0 || index >= static_cast<int>(tracks_.size()))
    {
        return;
    }
    tracks_.erase(tracks_.begin() + index);
}

int MultiTrackSequencer::GetTrackCount() const
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    return static_cast<int>(tracks_.size());
}

std::string MultiTrackSequencer::GetTrackName(int index) const
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    if (index < 0 || index >= static_cast<int>(tracks_.size()))
    {
        return std::string();
    }
    return tracks_[index]->name;
}

void MultiTrackSequencer::SetTrackName(int index, const std::string& name)
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    if (index < 0 || index >= static_cast<int>(tracks_.size()))
    {
        return;
    }
    tracks_[index]->name = name;
}

void MultiTrackSequencer::SetTrackNotes(int index, std::vector<SequencedNote> notes)
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    if (index < 0 || index >= static_cast<int>(tracks_.size()))
    {
        return;
    }
    tracks_[index]->sequencer.SetNotes(std::move(notes));
}

void MultiTrackSequencer::SetTrackVolume(int index, float volume)
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    if (index < 0 || index >= static_cast<int>(tracks_.size()))
    {
        return;
    }
    if (volume < 0.0f) volume = 0.0f;
    if (volume > 1.5f) volume = 1.5f;
    tracks_[index]->volume.store(volume);
}

float MultiTrackSequencer::GetTrackVolume(int index) const
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    if (index < 0 || index >= static_cast<int>(tracks_.size()))
    {
        return 0.0f;
    }
    return tracks_[index]->volume.load();
}

void MultiTrackSequencer::SetTrackPan(int index, float pan)
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    if (index < 0 || index >= static_cast<int>(tracks_.size()))
    {
        return;
    }
    if (pan < -1.0f) pan = -1.0f;
    if (pan > 1.0f) pan = 1.0f;
    tracks_[index]->pan.store(pan);
}

float MultiTrackSequencer::GetTrackPan(int index) const
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    if (index < 0 || index >= static_cast<int>(tracks_.size()))
    {
        return 0.0f;
    }
    return tracks_[index]->pan.load();
}

void MultiTrackSequencer::SetTrackMute(int index, bool mute)
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    if (index < 0 || index >= static_cast<int>(tracks_.size()))
    {
        return;
    }
    tracks_[index]->mute.store(mute);
}

bool MultiTrackSequencer::IsTrackMuted(int index) const
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    if (index < 0 || index >= static_cast<int>(tracks_.size()))
    {
        return false;
    }
    return tracks_[index]->mute.load();
}

void MultiTrackSequencer::SetTrackSolo(int index, bool solo)
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    if (index < 0 || index >= static_cast<int>(tracks_.size()))
    {
        return;
    }
    tracks_[index]->solo.store(solo);
}

bool MultiTrackSequencer::IsTrackSoloed(int index) const
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    if (index < 0 || index >= static_cast<int>(tracks_.size()))
    {
        return false;
    }
    return tracks_[index]->solo.load();
}

void MultiTrackSequencer::SetTrackInstrumentMode(int index, InstrumentMode mode)
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    if (index < 0 || index >= static_cast<int>(tracks_.size()))
    {
        return;
    }
    tracks_[index]->mode = mode;
}

MultiTrackSequencer::InstrumentMode MultiTrackSequencer::GetTrackInstrumentMode(int index) const
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    if (index < 0 || index >= static_cast<int>(tracks_.size()))
    {
        return InstrumentMode::BuiltInSynth;
    }
    return tracks_[index]->mode;
}

bool MultiTrackSequencer::LoadTrackSoundFontBank(int index, const std::string& sf2Path, double sampleRate, std::string& outError)
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    if (index < 0 || index >= static_cast<int>(tracks_.size()))
    {
        outError = "Nevažeći indeks trake.";
        return false;
    }
    bool ok = tracks_[index]->soundFont.LoadBank(sf2Path, sampleRate, outError);
    if (ok)
    {
        // Loading a bank for this track is itself the action that commits
        // the track to using it - matches how selecting an instrument
        // elsewhere in this engine (e.g. the shared SoundFont/VST3 loads)
        // immediately becomes what plays.
        tracks_[index]->mode = InstrumentMode::SoundFont;
    }
    return ok;
}

void MultiTrackSequencer::UnloadTrackSoundFontBank(int index)
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    if (index < 0 || index >= static_cast<int>(tracks_.size()))
    {
        return;
    }
    tracks_[index]->soundFont.UnloadBank();
}

bool MultiTrackSequencer::IsTrackSoundFontBankLoaded(int index) const
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    if (index < 0 || index >= static_cast<int>(tracks_.size()))
    {
        return false;
    }
    return tracks_[index]->soundFont.IsLoaded();
}

std::string MultiTrackSequencer::GetTrackSoundFontBankName(int index) const
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    if (index < 0 || index >= static_cast<int>(tracks_.size()))
    {
        return std::string();
    }
    return tracks_[index]->soundFont.GetLoadedBankName();
}

int MultiTrackSequencer::GetTrackSoundFontPresetCount(int index) const
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    if (index < 0 || index >= static_cast<int>(tracks_.size()))
    {
        return 0;
    }
    return tracks_[index]->soundFont.GetPresetCount();
}

std::string MultiTrackSequencer::GetTrackSoundFontPresetName(int index, int presetIndex) const
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    if (index < 0 || index >= static_cast<int>(tracks_.size()))
    {
        return std::string();
    }
    return tracks_[index]->soundFont.GetPresetName(presetIndex);
}

bool MultiTrackSequencer::SelectTrackSoundFontPreset(int index, int presetIndex)
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    if (index < 0 || index >= static_cast<int>(tracks_.size()))
    {
        return false;
    }
    return tracks_[index]->soundFont.SelectPreset(presetIndex);
}

int MultiTrackSequencer::GetSelectedTrackSoundFontPresetIndex(int index) const
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    if (index < 0 || index >= static_cast<int>(tracks_.size()))
    {
        return -1;
    }
    return tracks_[index]->soundFont.GetSelectedPresetIndex();
}

bool MultiTrackSequencer::LoadTrackVstInstrument(int index, const std::string& modulePath, double sampleRate, int maxBlockSize, std::string& outError)
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    if (index < 0 || index >= static_cast<int>(tracks_.size()))
    {
        outError = "Nevažeći indeks trake.";
        return false;
    }
    bool ok = tracks_[index]->vst.LoadInstrument(modulePath, sampleRate, maxBlockSize, outError);
    if (ok)
    {
        // Same convention as LoadTrackSoundFontBank above: loading it is
        // what commits the track to using it.
        tracks_[index]->mode = InstrumentMode::Vst;
    }
    return ok;
}

void MultiTrackSequencer::UnloadTrackVstInstrument(int index)
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    if (index < 0 || index >= static_cast<int>(tracks_.size()))
    {
        return;
    }
    tracks_[index]->vst.UnloadInstrument();
}

bool MultiTrackSequencer::IsTrackVstInstrumentLoaded(int index) const
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    if (index < 0 || index >= static_cast<int>(tracks_.size()))
    {
        return false;
    }
    return tracks_[index]->vst.IsLoaded();
}

std::string MultiTrackSequencer::GetTrackVstInstrumentName(int index) const
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    if (index < 0 || index >= static_cast<int>(tracks_.size()))
    {
        return std::string();
    }
    return tracks_[index]->vst.GetLoadedPluginName();
}

void MultiTrackSequencer::SetTempoBpm(double bpm)
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    tempoBpm_ = bpm > 0.0 ? bpm : 120.0;
    for (auto& t : tracks_)
    {
        t->sequencer.SetTempoBpm(tempoBpm_);
    }
}

double MultiTrackSequencer::GetTempoBpm() const
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    return tempoBpm_;
}

void MultiTrackSequencer::SetLoopLengthBeats(double beats)
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    loopLengthBeats_ = beats > 0.0 ? beats : 16.0;
    for (auto& t : tracks_)
    {
        t->sequencer.SetLoopLengthBeats(loopLengthBeats_);
    }
}

double MultiTrackSequencer::GetLoopLengthBeats() const
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    return loopLengthBeats_;
}

void MultiTrackSequencer::Play()
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    playing_ = true;
    for (auto& t : tracks_)
    {
        t->sequencer.Play();
    }
}

void MultiTrackSequencer::Stop()
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    playing_ = false;
    for (auto& t : tracks_)
    {
        t->sequencer.Stop();
    }
}

bool MultiTrackSequencer::IsPlaying() const
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    return playing_;
}

void MultiTrackSequencer::TrackNoteOn(TrackState& track, int pitch, float velocity)
{
    switch (track.mode)
    {
    case InstrumentMode::SoundFont: track.soundFont.NoteOn(pitch, velocity); break;
    case InstrumentMode::Vst: track.vst.NoteOn(pitch, velocity); break;
    default: track.synth.NoteOn(pitch, velocity); break;
    }
}

void MultiTrackSequencer::TrackNoteOff(TrackState& track, int pitch)
{
    switch (track.mode)
    {
    case InstrumentMode::SoundFont: track.soundFont.NoteOff(pitch); break;
    case InstrumentMode::Vst: track.vst.NoteOff(pitch); break;
    default: track.synth.NoteOff(pitch); break;
    }
}

void MultiTrackSequencer::Advance(int frames, double sampleRate, bool autoThirdEnabled, bool autoThirdUpper,
                                   int autoThirdScaleRoot, ScaleType autoThirdScaleType)
{
    std::lock_guard<std::mutex> lock(tracksMutex_);
    for (auto& trackPtr : tracks_)
    {
        TrackState* track = trackPtr.get();
        track->sequencer.Advance(
            frames,
            sampleRate,
            [track, autoThirdEnabled, autoThirdUpper, autoThirdScaleRoot, autoThirdScaleType](int pitch, float velocity)
            {
                TrackNoteOn(*track, pitch, velocity);

                if (autoThirdEnabled)
                {
                    int harmony = DiatonicThirdNote(pitch, autoThirdScaleRoot, autoThirdScaleType, autoThirdUpper);
                    if (harmony >= 0 && harmony <= 127)
                    {
                        TrackNoteOn(*track, harmony, velocity);
                    }
                }
            },
            [track, autoThirdEnabled, autoThirdUpper, autoThirdScaleRoot, autoThirdScaleType](int pitch)
            {
                TrackNoteOff(*track, pitch);

                if (autoThirdEnabled)
                {
                    int harmony = DiatonicThirdNote(pitch, autoThirdScaleRoot, autoThirdScaleType, autoThirdUpper);
                    if (harmony >= 0 && harmony <= 127)
                    {
                        TrackNoteOff(*track, harmony);
                    }
                }
            });
    }
}

void MultiTrackSequencer::RenderAdditive(float* interleavedStereoOut, int frames)
{
    if (frames <= 0)
    {
        return;
    }
    int clampedFrames = frames < kMaxScratchFrames ? frames : kMaxScratchFrames;

    std::lock_guard<std::mutex> lock(tracksMutex_);

    bool anySolo = false;
    for (auto& t : tracks_)
    {
        if (t->solo.load())
        {
            anySolo = true;
            break;
        }
    }

    for (auto& trackPtr : tracks_)
    {
        TrackState* track = trackPtr.get();
        bool audible = anySolo ? track->solo.load() : !track->mute.load();
        if (!audible)
        {
            continue;
        }

        float volume = track->volume.load();
        float pan = track->pan.load();
        // Equal-power pan law: at pan=0 both channels get ~0.7071 (-3dB
        // each, correct for a centered mono source in a stereo field)
        // rather than a linear pan's 1.0/1.0 (which would sound louder in
        // the center than panned fully to either side).
        float angle = (pan + 1.0f) * 0.7853981633974483f; // (pan+1) * pi/4
        float leftGain = std::cos(angle) * volume;
        float rightGain = std::sin(angle) * volume;

        if (track->mode == InstrumentMode::BuiltInSynth)
        {
            std::memset(track->scratchMono, 0, sizeof(float) * clampedFrames);
            track->synth.Render(track->scratchMono, clampedFrames);
            for (int i = 0; i < clampedFrames; ++i)
            {
                interleavedStereoOut[i * 2] += track->scratchMono[i] * leftGain;
                interleavedStereoOut[i * 2 + 1] += track->scratchMono[i] * rightGain;
            }
        }
        else
        {
            std::memset(track->scratchStereo, 0, sizeof(float) * clampedFrames * 2);
            if (track->mode == InstrumentMode::Vst)
            {
                track->vst.RenderAdditive(track->scratchStereo, clampedFrames);
            }
            else
            {
                track->soundFont.RenderAdditive(track->scratchStereo, clampedFrames);
            }
            for (int i = 0; i < clampedFrames; ++i)
            {
                interleavedStereoOut[i * 2] += track->scratchStereo[i * 2] * leftGain;
                interleavedStereoOut[i * 2 + 1] += track->scratchStereo[i * 2 + 1] * rightGain;
            }
        }
    }
}
