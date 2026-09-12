#include "AudioEngine.h"

#include <portaudio.h>

#include <algorithm>
#include <cmath>
#include <cstring>

#include "Scale.h"
#include "WavWriter.h"

namespace
{
    constexpr double kSampleRate = 44100.0;
    constexpr unsigned long kFramesPerBuffer = 256; // small buffer for low latency
    constexpr int kMaxCallbackFrames = 2048;         // safety cap for the stack buffer below

    // PortAudio's callback only takes one userData pointer, and everything
    // below needs to be reachable from it, so this small struct is what
    // actually gets passed through. 'engine' is used for note dispatch
    // (AudioEngine::NoteOn/NoteOff - the single place auto-third
    // harmonization is applied) and to read the current auto-third state for
    // the multi-track sequencer (which dispatches straight to each track's
    // own instrument rather than through engine->NoteOn), while the rest are
    // used directly for rendering/mute checks that don't go through the
    // engine.
    struct CallbackContext
    {
        AudioEngine* engine;
        Synth* synth;
        VstHost* vstHost;
        Sequencer* sequencer;
        SoundFontHost* soundFontHost;
        Arranger* arranger;
        MultiTrackSequencer* multiTrack;
        AccompanimentEngine* accompaniment;
    };

    int PaCallback(const void* /*input*/,
                    void* output,
                    unsigned long frameCount,
                    const PaStreamCallbackTimeInfo* /*timeInfo*/,
                    PaStreamCallbackFlags /*statusFlags*/,
                    void* userData)
    {
        auto* context = static_cast<CallbackContext*>(userData);
        auto* out = static_cast<float*>(output);

        // Render mono on the stack (no heap allocation on the audio thread),
        // then duplicate to stereo.
        float monoBuffer[kMaxCallbackFrames];
        unsigned long frames = frameCount < static_cast<unsigned long>(kMaxCallbackFrames)
                                    ? frameCount
                                    : static_cast<unsigned long>(kMaxCallbackFrames);

        // Advance the sequencer and the arranger first, so any note either
        // one starts or stops this block is heard in this exact block
        // rather than one block late. Only one of the two is normally
        // actually playing at a time (PlaySequencer/PlayArranger stop the
        // other), but both are always safe to advance regardless. Both
        // route through engine->NoteOn/NoteOff - the same single dispatch
        // point live keyboard/MIDI playing uses - so auto-third
        // harmonization (see AudioEngine::NoteOn) applies here too.
        context->sequencer->Advance(
            static_cast<int>(frames),
            kSampleRate,
            [context](int pitch, float velocity) { context->engine->NoteOn(pitch, velocity); },
            [context](int pitch) { context->engine->NoteOff(pitch); });

        context->arranger->Advance(
            static_cast<int>(frames),
            kSampleRate,
            [context](int pitch, float velocity) { context->engine->NoteOn(pitch, velocity); },
            [context](int pitch) { context->engine->NoteOff(pitch); });

        // The multi-track sequencer dispatches straight to each track's own
        // private instrument (see MultiTrackSequencer.h) rather than through
        // engine->NoteOn, since a single global NoteOn/NoteOff pair can't
        // represent "this note belongs to track 3's SoundFont instance" -
        // so auto-third's current state is read here and passed in directly
        // instead.
        context->multiTrack->Advance(static_cast<int>(frames), kSampleRate,
                                      context->engine->IsAutoThirdEnabled(),
                                      context->engine->IsAutoThirdUpper(),
                                      context->engine->GetAutoThirdScaleRoot(),
                                      context->engine->GetAutoThirdScaleType());

        // Auto-accompaniment runs independently of the transports above -
        // it's meant to play underneath live keyboard/MIDI performance
        // (and, incidentally, is also harmless to leave running alongside
        // the sequencer/arranger/multi-track, since it owns its own
        // private instruments and never touches theirs).
        context->accompaniment->Advance(static_cast<int>(frames), kSampleRate);

        // The built-in synth is only meant as a fallback so something is
        // audible before any real instrument is loaded. Once a VST3
        // instrument or a SoundFont bank is active, let it be the only
        // voice instead of always layering the plain sine synth underneath
        // too (reported as "hearing two sounds at once" once a SoundFont
        // bank was loaded).
        bool hasExternalInstrument = context->vstHost->IsLoaded() || context->soundFontHost->IsLoaded();
        if (hasExternalInstrument)
        {
            std::memset(monoBuffer, 0, sizeof(float) * frames);
        }
        else
        {
            context->synth->Render(monoBuffer, static_cast<int>(frames));
        }

        for (unsigned long i = 0; i < frames; ++i)
        {
            out[i * 2] = monoBuffer[i];
            out[i * 2 + 1] = monoBuffer[i];
        }

        // Adds a loaded VST3 instrument's output, and a loaded SoundFont
        // bank's output, on top of whatever's already there - each does
        // nothing if nothing is loaded.
        context->vstHost->RenderAdditive(out, static_cast<int>(frames));
        context->soundFontHost->RenderAdditive(out, static_cast<int>(frames));
        context->multiTrack->RenderAdditive(out, static_cast<int>(frames));
        context->accompaniment->RenderAdditive(out, static_cast<int>(frames));

        // Soft-clip the final mix. Several notes at once (a chord) - or
        // even one note, if a SoundFont instrument layers several sample
        // regions per key/velocity - can add up to sample values outside
        // the [-1, 1] range a float audio stream is supposed to stay
        // within. Left alone, that gets hard-clipped somewhere downstream
        // (the audio driver or device), which is what the reported
        // "crackling on a chord" was - an abrupt, harsh distortion. tanh()
        // bends peaks smoothly back towards +-1 instead of chopping them
        // off, and is very close to the identity function for the small
        // values normal single-note playing produces, so quiet/moderate
        // playing is left essentially untouched. A real
        // compressor/limiter (attack/release, not an instant per-sample
        // curve) would be a natural mixer-milestone upgrade later.
        for (unsigned long i = 0; i < frames * 2; ++i)
        {
            out[i] = std::tanh(out[i]);
        }

        return paContinue;
    }
}

bool AudioEngine::Init()
{
    if (Pa_Initialize() != paNoError)
    {
        return false;
    }
    synth_.Prepare(kSampleRate);
    accompaniment_.Prepare(kSampleRate);

    // Route MIDI note on/off through the same single dispatch point
    // (NoteOnLive/NoteOffLive, which call straight through to NoteOn/NoteOff
    // below) the computer keyboard uses, rather than touching
    // synth_/vstHost_/soundFontHost_ directly - that's what makes auto-third
    // harmonization apply the same way whether or not a MIDI device is
    // connected, and lets chord capture (see SetChordCaptureArmed) see MIDI
    // notes too. This callback runs on RtMidi's own background thread;
    // NoteOnLive/NoteOffLive are safe to call from there (the chord-capture
    // bookkeeping is mutex-guarded, and NoteOn/NoteOff just forward to
    // Synth/VstHost/SoundFontHost, which already handle being called from
    // more than one thread - see Synth.h).
    midiInput_.SetNoteCallback([this](int note, float velocity, bool isNoteOn)
    {
        if (isNoteOn)
        {
            NoteOnLive(note, velocity);
        }
        else
        {
            NoteOffLive(note);
        }
    });

    callbackContext_ = new CallbackContext{this, &synth_, &vstHost_, &sequencer_, &soundFontHost_, &arranger_, &multiTrack_, &accompaniment_};

    return true;
}

void AudioEngine::Shutdown()
{
    Stop();
    sequencer_.Stop();
    arranger_.Stop();
    multiTrack_.Stop();
    accompaniment_.Stop();
    midiInput_.Close();
    vstHost_.UnloadInstrument();
    soundFontHost_.UnloadBank();
    Pa_Terminate();

    delete static_cast<CallbackContext*>(callbackContext_);
    callbackContext_ = nullptr;
}

bool AudioEngine::Start()
{
    PaStream* stream = nullptr;
    PaError err = paNoError;

    // Prefer WASAPI (shared mode) over whichever host API PortAudio's own
    // "default" selection would pick. On Windows that default is typically
    // the decades-old MME backend, which commonly adds 100ms+ of latency -
    // far more than the ~5.8ms this callback's buffer size (256 frames @
    // 44100Hz) could otherwise deliver. WASAPI shared mode gets most of
    // that latency back down to single-digit milliseconds.
    //
    // Deliberately NOT using WASAPI *exclusive* mode: exclusive mode takes
    // the output device away from every other application for as long as
    // this stream is open. On a screen-reader user's machine that could
    // include JAWS's own speech output, if it happens to route through the
    // same device - silencing speech while this app is running would be a
    // serious accessibility regression, so shared mode is the right
    // trade-off even though exclusive mode alone could shave off a little
    // more latency still.
    //
    // If WASAPI isn't available (e.g. on a platform where it wasn't
    // compiled in) or opening it fails for any reason, this falls back to
    // the previous default-device behavior below, so nothing regresses.
    PaHostApiIndex wasapiIndex = Pa_HostApiTypeIdToHostApiIndex(paWASAPI);
    if (wasapiIndex != paHostApiNotFound)
    {
        const PaHostApiInfo* apiInfo = Pa_GetHostApiInfo(wasapiIndex);
        if (apiInfo != nullptr && apiInfo->defaultOutputDevice != paNoDevice)
        {
            const PaDeviceInfo* deviceInfo = Pa_GetDeviceInfo(apiInfo->defaultOutputDevice);
            if (deviceInfo != nullptr)
            {
                PaStreamParameters outputParams{};
                outputParams.device = apiInfo->defaultOutputDevice;
                outputParams.channelCount = 2;
                outputParams.sampleFormat = paFloat32;
                outputParams.suggestedLatency = deviceInfo->defaultLowOutputLatency;
                outputParams.hostApiSpecificStreamInfo = nullptr;

                err = Pa_OpenStream(
                    &stream,
                    nullptr, // no input
                    &outputParams,
                    kSampleRate,
                    kFramesPerBuffer,
                    paNoFlag,
                    PaCallback,
                    callbackContext_);
                if (err != paNoError)
                {
                    stream = nullptr;
                }
            }
        }
    }

    if (stream == nullptr)
    {
        err = Pa_OpenDefaultStream(
            &stream,
            0,    // no input channels (MIDI input arrives separately, not via audio input)
            2,    // stereo output
            paFloat32,
            kSampleRate,
            kFramesPerBuffer,
            PaCallback,
            callbackContext_);
    }

    if (err != paNoError || stream == nullptr)
    {
        return false;
    }

    err = Pa_StartStream(stream);
    if (err != paNoError)
    {
        Pa_CloseStream(stream);
        return false;
    }

    stream_ = stream;
    return true;
}

void AudioEngine::Stop()
{
    if (stream_ != nullptr)
    {
        auto* stream = static_cast<PaStream*>(stream_);
        Pa_StopStream(stream);
        Pa_CloseStream(stream);
        stream_ = nullptr;
    }
}

void AudioEngine::NoteOn(int midiNote, float velocity)
{
    // While auto-accompaniment's "chord from keyboard" mode is on, notes
    // below its split point are the chord-input zone (like a real
    // keyboard's left-hand accompaniment area) - they feed chord detection
    // instead of sounding as a normal note, so playing a left-hand chord
    // doesn't also add three extra notes to whatever you're playing with
    // the right hand.
    if (accompaniment_.IsChordInputAutoFromKeyboard() && midiNote < accompaniment_.GetSplitPoint())
    {
        accompaniment_.NoteHeldForChordDetection(midiNote, true);
        return;
    }

    synth_.NoteOn(midiNote, velocity);
    vstHost_.NoteOn(midiNote, velocity);
    soundFontHost_.NoteOn(midiNote, velocity);

    if (autoThirdEnabled_.load())
    {
        int harmonyNote = DiatonicThirdNote(midiNote, autoThirdScaleRoot_.load(), static_cast<ScaleType>(autoThirdScaleType_.load()), autoThirdUpper_.load());
        if (harmonyNote >= 0 && harmonyNote <= 127)
        {
            synth_.NoteOn(harmonyNote, velocity);
            vstHost_.NoteOn(harmonyNote, velocity);
            soundFontHost_.NoteOn(harmonyNote, velocity);
        }
    }
}

void AudioEngine::NoteOff(int midiNote)
{
    if (accompaniment_.IsChordInputAutoFromKeyboard() && midiNote < accompaniment_.GetSplitPoint())
    {
        accompaniment_.NoteHeldForChordDetection(midiNote, false);
        return;
    }

    synth_.NoteOff(midiNote);
    vstHost_.NoteOff(midiNote);
    soundFontHost_.NoteOff(midiNote);

    if (autoThirdEnabled_.load())
    {
        int harmonyNote = DiatonicThirdNote(midiNote, autoThirdScaleRoot_.load(), static_cast<ScaleType>(autoThirdScaleType_.load()), autoThirdUpper_.load());
        if (harmonyNote >= 0 && harmonyNote <= 127)
        {
            synth_.NoteOff(harmonyNote);
            vstHost_.NoteOff(harmonyNote);
            soundFontHost_.NoteOff(harmonyNote);
        }
    }
}

void AudioEngine::NoteOnLive(int midiNote, float velocity)
{
    if (chordCaptureArmed_.load())
    {
        std::lock_guard<std::mutex> lock(chordCaptureMutex_);
        bool alreadyHeld = false;
        for (const auto& entry : chordCaptureHeldNotes_)
        {
            if (entry.first == midiNote)
            {
                alreadyHeld = true;
                break;
            }
        }
        if (!alreadyHeld)
        {
            chordCaptureHeldNotes_.emplace_back(midiNote, velocity);
            if (chordCaptureHeldNotes_.size() > chordCapturePeakNotes_.size())
            {
                chordCapturePeakNotes_ = chordCaptureHeldNotes_;
            }
        }
    }

    NoteOn(midiNote, velocity);
}

void AudioEngine::NoteOffLive(int midiNote)
{
    if (chordCaptureArmed_.load())
    {
        std::lock_guard<std::mutex> lock(chordCaptureMutex_);
        chordCaptureHeldNotes_.erase(
            std::remove_if(chordCaptureHeldNotes_.begin(), chordCaptureHeldNotes_.end(),
                            [midiNote](const auto& entry) { return entry.first == midiNote; }),
            chordCaptureHeldNotes_.end());

        // Every held note was just released - hand the peak simultaneously-
        // held set off as the pending captured chord, ready for
        // TryTakeCapturedChord to pick up, and start fresh for the next one.
        if (chordCaptureHeldNotes_.empty() && !chordCapturePeakNotes_.empty())
        {
            chordCaptureCapturedChord_ = chordCapturePeakNotes_;
            chordCapturePeakNotes_.clear();
        }
    }

    NoteOff(midiNote);
}

void AudioEngine::SetChordCaptureArmed(bool armed)
{
    chordCaptureArmed_.store(armed);

    std::lock_guard<std::mutex> lock(chordCaptureMutex_);
    chordCaptureHeldNotes_.clear();
    chordCapturePeakNotes_.clear();
    chordCaptureCapturedChord_.clear();
}

bool AudioEngine::IsChordCaptureArmed() const
{
    return chordCaptureArmed_.load();
}

bool AudioEngine::TryTakeCapturedChord(std::vector<int>& notesOut, std::vector<float>& velocitiesOut)
{
    std::lock_guard<std::mutex> lock(chordCaptureMutex_);
    if (chordCaptureCapturedChord_.empty())
    {
        return false;
    }

    notesOut.clear();
    velocitiesOut.clear();
    for (const auto& entry : chordCaptureCapturedChord_)
    {
        notesOut.push_back(entry.first);
        velocitiesOut.push_back(entry.second);
    }
    chordCaptureCapturedChord_.clear();
    return true;
}

void AudioEngine::SetAutoThirdEnabled(bool enabled)
{
    autoThirdEnabled_.store(enabled);
}

bool AudioEngine::IsAutoThirdEnabled() const
{
    return autoThirdEnabled_.load();
}

void AudioEngine::SetAutoThirdUpper(bool upper)
{
    autoThirdUpper_.store(upper);
}

bool AudioEngine::IsAutoThirdUpper() const
{
    return autoThirdUpper_.load();
}

void AudioEngine::SetAutoThirdScale(int rootPitchClass, ScaleType scaleType)
{
    autoThirdScaleRoot_.store(((rootPitchClass % 12) + 12) % 12);
    autoThirdScaleType_.store(static_cast<int>(scaleType));
}

int AudioEngine::GetAutoThirdScaleRoot() const
{
    return autoThirdScaleRoot_.load();
}

ScaleType AudioEngine::GetAutoThirdScaleType() const
{
    return static_cast<ScaleType>(autoThirdScaleType_.load());
}

std::vector<std::string> AudioEngine::ListMidiPorts()
{
    return midiInput_.ListPorts();
}

bool AudioEngine::OpenMidiPort(unsigned int portIndex)
{
    return midiInput_.OpenPort(portIndex);
}

void AudioEngine::CloseMidiPort()
{
    midiInput_.Close();
}

bool AudioEngine::IsMidiPortOpen() const
{
    return midiInput_.IsOpen();
}

bool AudioEngine::LoadVstInstrument(const std::string& modulePath, std::string& outError)
{
    return vstHost_.LoadInstrument(modulePath, kSampleRate, kMaxCallbackFrames, outError);
}

void AudioEngine::UnloadVstInstrument()
{
    vstHost_.UnloadInstrument();
}

bool AudioEngine::IsVstInstrumentLoaded() const
{
    return vstHost_.IsLoaded();
}

std::string AudioEngine::GetVstInstrumentName() const
{
    return vstHost_.GetLoadedPluginName();
}

void AudioEngine::SetSequencerNotes(const SequencedNote* notes, int count)
{
    std::vector<SequencedNote> v;
    if (notes != nullptr && count > 0)
    {
        v.assign(notes, notes + count);
    }
    sequencer_.SetNotes(std::move(v));
}

void AudioEngine::SetSequencerTempoBpm(double bpm)
{
    sequencer_.SetTempoBpm(bpm);
}

void AudioEngine::SetSequencerLoopLengthBeats(double beats)
{
    sequencer_.SetLoopLengthBeats(beats);
}

void AudioEngine::PlaySequencer()
{
    // See the identical comments in PlayArranger()/PlayMultiTrack() - only
    // one of the three transports should be advancing at a time.
    arranger_.Stop();
    multiTrack_.Stop();
    sequencer_.Play();
}

void AudioEngine::StopSequencer()
{
    sequencer_.Stop();
}

bool AudioEngine::IsSequencerPlaying() const
{
    return sequencer_.IsPlaying();
}

bool AudioEngine::LoadSoundFontBank(const std::string& sf2Path, std::string& outError)
{
    return soundFontHost_.LoadBank(sf2Path, kSampleRate, outError);
}

void AudioEngine::UnloadSoundFontBank()
{
    soundFontHost_.UnloadBank();
}

bool AudioEngine::IsSoundFontBankLoaded() const
{
    return soundFontHost_.IsLoaded();
}

std::string AudioEngine::GetSoundFontBankName() const
{
    return soundFontHost_.GetLoadedBankName();
}

int AudioEngine::GetSoundFontPresetCount() const
{
    return soundFontHost_.GetPresetCount();
}

std::string AudioEngine::GetSoundFontPresetName(int presetIndex) const
{
    return soundFontHost_.GetPresetName(presetIndex);
}

bool AudioEngine::SelectSoundFontPreset(int presetIndex)
{
    return soundFontHost_.SelectPreset(presetIndex);
}

int AudioEngine::GetSelectedSoundFontPresetIndex() const
{
    return soundFontHost_.GetSelectedPresetIndex();
}

int AudioEngine::AddArrangerPattern(const std::string& name, double lengthBeats, const SequencedNote* notes, int count)
{
    std::vector<SequencedNote> v;
    if (notes != nullptr && count > 0)
    {
        v.assign(notes, notes + count);
    }
    return arranger_.AddPattern(name, lengthBeats, std::move(v));
}

void AudioEngine::RemoveArrangerPattern(int patternIndex)
{
    arranger_.RemovePattern(patternIndex);
}

int AudioEngine::GetArrangerPatternCount() const
{
    return arranger_.GetPatternCount();
}

std::string AudioEngine::GetArrangerPatternName(int patternIndex) const
{
    return arranger_.GetPatternName(patternIndex);
}

double AudioEngine::GetArrangerPatternLengthBeats(int patternIndex) const
{
    return arranger_.GetPatternLengthBeats(patternIndex);
}

int AudioEngine::GetArrangerPatternNoteCount(int patternIndex) const
{
    return arranger_.GetPatternNoteCount(patternIndex);
}

std::vector<SequencedNote> AudioEngine::GetArrangerPatternNotes(int patternIndex) const
{
    return arranger_.GetPatternNotes(patternIndex);
}

void AudioEngine::AppendToArrangerOrder(int patternIndex)
{
    arranger_.AppendToOrder(patternIndex);
}

void AudioEngine::RemoveLastFromArrangerOrder()
{
    arranger_.RemoveLastFromOrder();
}

void AudioEngine::ClearArrangerOrder()
{
    arranger_.ClearOrder();
}

int AudioEngine::GetArrangerOrderCount() const
{
    return arranger_.GetOrderCount();
}

int AudioEngine::GetArrangerOrderPatternIndexAt(int orderPosition) const
{
    return arranger_.GetOrderPatternIndexAt(orderPosition);
}

void AudioEngine::SetArrangerTempoBpm(double bpm)
{
    arranger_.SetTempoBpm(bpm);
}

double AudioEngine::GetArrangerTempoBpm() const
{
    return arranger_.GetTempoBpm();
}

void AudioEngine::PlayArranger()
{
    // The sequencer, the arranger, and the multi-track sequencer all feed
    // (or are) instruments that would otherwise fight over the same
    // transport - only one should actually be advancing at a time, so
    // playing one stops the other two, regardless of which UI button was
    // used to start it.
    sequencer_.Stop();
    multiTrack_.Stop();
    arranger_.Play();
}

void AudioEngine::StopArranger()
{
    arranger_.Stop();
}

bool AudioEngine::IsArrangerPlaying() const
{
    return arranger_.IsPlaying();
}

int AudioEngine::GetArrangerCurrentOrderPosition() const
{
    return arranger_.GetCurrentOrderPosition();
}

int AudioEngine::AddTrack(const std::string& name)
{
    return multiTrack_.AddTrack(name);
}

void AudioEngine::RemoveTrack(int trackIndex)
{
    multiTrack_.RemoveTrack(trackIndex);
}

int AudioEngine::GetTrackCount() const
{
    return multiTrack_.GetTrackCount();
}

std::string AudioEngine::GetTrackName(int trackIndex) const
{
    return multiTrack_.GetTrackName(trackIndex);
}

void AudioEngine::SetTrackName(int trackIndex, const std::string& name)
{
    multiTrack_.SetTrackName(trackIndex, name);
}

void AudioEngine::SetTrackNotes(int trackIndex, const SequencedNote* notes, int count)
{
    std::vector<SequencedNote> v;
    if (notes != nullptr && count > 0)
    {
        v.assign(notes, notes + count);
    }
    multiTrack_.SetTrackNotes(trackIndex, std::move(v));
}

void AudioEngine::SetTrackVolume(int trackIndex, float volume)
{
    multiTrack_.SetTrackVolume(trackIndex, volume);
}

float AudioEngine::GetTrackVolume(int trackIndex) const
{
    return multiTrack_.GetTrackVolume(trackIndex);
}

void AudioEngine::SetTrackPan(int trackIndex, float pan)
{
    multiTrack_.SetTrackPan(trackIndex, pan);
}

float AudioEngine::GetTrackPan(int trackIndex) const
{
    return multiTrack_.GetTrackPan(trackIndex);
}

void AudioEngine::SetTrackMute(int trackIndex, bool mute)
{
    multiTrack_.SetTrackMute(trackIndex, mute);
}

bool AudioEngine::IsTrackMuted(int trackIndex) const
{
    return multiTrack_.IsTrackMuted(trackIndex);
}

void AudioEngine::SetTrackSolo(int trackIndex, bool solo)
{
    multiTrack_.SetTrackSolo(trackIndex, solo);
}

bool AudioEngine::IsTrackSoloed(int trackIndex) const
{
    return multiTrack_.IsTrackSoloed(trackIndex);
}

void AudioEngine::SetTrackInstrumentMode(int trackIndex, MultiTrackSequencer::InstrumentMode mode)
{
    multiTrack_.SetTrackInstrumentMode(trackIndex, mode);
}

MultiTrackSequencer::InstrumentMode AudioEngine::GetTrackInstrumentMode(int trackIndex) const
{
    return multiTrack_.GetTrackInstrumentMode(trackIndex);
}

bool AudioEngine::LoadTrackSoundFontBank(int trackIndex, const std::string& sf2Path, std::string& outError)
{
    return multiTrack_.LoadTrackSoundFontBank(trackIndex, sf2Path, kSampleRate, outError);
}

void AudioEngine::UnloadTrackSoundFontBank(int trackIndex)
{
    multiTrack_.UnloadTrackSoundFontBank(trackIndex);
}

bool AudioEngine::IsTrackSoundFontBankLoaded(int trackIndex) const
{
    return multiTrack_.IsTrackSoundFontBankLoaded(trackIndex);
}

std::string AudioEngine::GetTrackSoundFontBankName(int trackIndex) const
{
    return multiTrack_.GetTrackSoundFontBankName(trackIndex);
}

int AudioEngine::GetTrackSoundFontPresetCount(int trackIndex) const
{
    return multiTrack_.GetTrackSoundFontPresetCount(trackIndex);
}

std::string AudioEngine::GetTrackSoundFontPresetName(int trackIndex, int presetIndex) const
{
    return multiTrack_.GetTrackSoundFontPresetName(trackIndex, presetIndex);
}

bool AudioEngine::SelectTrackSoundFontPreset(int trackIndex, int presetIndex)
{
    return multiTrack_.SelectTrackSoundFontPreset(trackIndex, presetIndex);
}

int AudioEngine::GetSelectedTrackSoundFontPresetIndex(int trackIndex) const
{
    return multiTrack_.GetSelectedTrackSoundFontPresetIndex(trackIndex);
}

bool AudioEngine::LoadTrackVstInstrument(int trackIndex, const std::string& modulePath, std::string& outError)
{
    return multiTrack_.LoadTrackVstInstrument(trackIndex, modulePath, kSampleRate, kMaxCallbackFrames, outError);
}

void AudioEngine::UnloadTrackVstInstrument(int trackIndex)
{
    multiTrack_.UnloadTrackVstInstrument(trackIndex);
}

bool AudioEngine::IsTrackVstInstrumentLoaded(int trackIndex) const
{
    return multiTrack_.IsTrackVstInstrumentLoaded(trackIndex);
}

std::string AudioEngine::GetTrackVstInstrumentName(int trackIndex) const
{
    return multiTrack_.GetTrackVstInstrumentName(trackIndex);
}

void AudioEngine::SetMultiTrackTempoBpm(double bpm)
{
    multiTrack_.SetTempoBpm(bpm);
}

double AudioEngine::GetMultiTrackTempoBpm() const
{
    return multiTrack_.GetTempoBpm();
}

void AudioEngine::SetMultiTrackLoopLengthBeats(double beats)
{
    multiTrack_.SetLoopLengthBeats(beats);
}

double AudioEngine::GetMultiTrackLoopLengthBeats() const
{
    return multiTrack_.GetLoopLengthBeats();
}

void AudioEngine::PlayMultiTrack()
{
    // See the identical comments in PlaySequencer()/PlayArranger() - only
    // one of the three transports should be advancing at a time.
    sequencer_.Stop();
    arranger_.Stop();
    multiTrack_.Play();
}

void AudioEngine::StopMultiTrack()
{
    multiTrack_.Stop();
}

bool AudioEngine::IsMultiTrackPlaying() const
{
    return multiTrack_.IsPlaying();
}

bool AudioEngine::RenderMultiTrackToWav(const std::string& outPath, double durationSeconds, std::string& outError)
{
    if (durationSeconds <= 0.0)
    {
        outError = "Trajanje izvoza mora biti veće od nule.";
        return false;
    }

    // The live audio callback also calls multiTrack_.Advance()/RenderAdditive()
    // on its own thread; rendering offline here at the same time would race
    // over the exact same track state (two different transports fighting
    // over one set of notes-still-sounding bookkeeping) and produce garbage
    // in both. Stopping the stream for the duration of this (typically
    // sub-second to a few seconds) offline render avoids that; it is
    // reopened afterwards exactly as it was.
    bool wasStreamRunning = (stream_ != nullptr);
    if (wasStreamRunning)
    {
        Stop();
    }

    multiTrack_.Stop(); // start the render from a clean, known position
    multiTrack_.Play();

    constexpr int kRenderBlockFrames = 512;
    const long long totalFrames = static_cast<long long>(durationSeconds * kSampleRate);
    std::vector<float> buffer;
    buffer.reserve(static_cast<size_t>(totalFrames) * 2);

    float block[kRenderBlockFrames * 2];
    long long framesRendered = 0;
    while (framesRendered < totalFrames)
    {
        int thisBlock = static_cast<int>(std::min<long long>(kRenderBlockFrames, totalFrames - framesRendered));
        std::memset(block, 0, sizeof(float) * thisBlock * 2);

        multiTrack_.Advance(thisBlock, kSampleRate, autoThirdEnabled_.load(), autoThirdUpper_.load(),
                             autoThirdScaleRoot_.load(), static_cast<ScaleType>(autoThirdScaleType_.load()));
        multiTrack_.RenderAdditive(block, thisBlock);

        // Same soft-clip as the live callback (see PaCallback's comment) -
        // a rendered file should sound exactly like what playing it back
        // live would have sounded like.
        for (int i = 0; i < thisBlock * 2; ++i)
        {
            buffer.push_back(std::tanh(block[i]));
        }

        framesRendered += thisBlock;
    }

    multiTrack_.Stop();

    bool ok = WriteWavPcm16Stereo(outPath, buffer, static_cast<int>(kSampleRate), outError);

    if (wasStreamRunning)
    {
        Start(); // best-effort - the export above already succeeded or failed independently of this
    }

    return ok;
}

int AudioEngine::GetAccompanimentStyleCount() const
{
    return accompaniment_.GetStyleCount();
}

std::string AudioEngine::GetAccompanimentStyleName(int index) const
{
    return accompaniment_.GetStyleName(index);
}

bool AudioEngine::SetAccompanimentStyleIndex(int index)
{
    return accompaniment_.SetStyleIndex(index);
}

int AudioEngine::GetSelectedAccompanimentStyleIndex() const
{
    return accompaniment_.GetSelectedStyleIndex();
}

int AudioEngine::SetAccompanimentCustomStyle(const Style& style, int replaceIndex)
{
    return accompaniment_.SetCustomStyle(style, replaceIndex);
}

bool AudioEngine::RemoveAccompanimentCustomStyle(int index)
{
    return accompaniment_.RemoveCustomStyle(index);
}

void AudioEngine::SetAccompanimentDrumsEnabled(bool enabled)
{
    accompaniment_.SetDrumsEnabled(enabled);
}

bool AudioEngine::AreAccompanimentDrumsEnabled() const
{
    return accompaniment_.AreDrumsEnabled();
}

void AudioEngine::SetAccompanimentBassEnabled(bool enabled)
{
    accompaniment_.SetBassEnabled(enabled);
}

bool AudioEngine::IsAccompanimentBassEnabled() const
{
    return accompaniment_.IsBassEnabled();
}

void AudioEngine::SetAccompanimentKontraEnabled(bool enabled)
{
    accompaniment_.SetKontraEnabled(enabled);
}

bool AudioEngine::IsAccompanimentKontraEnabled() const
{
    return accompaniment_.IsKontraEnabled();
}

void AudioEngine::SetAccompanimentHarmonijaEnabled(bool enabled)
{
    accompaniment_.SetHarmonijaEnabled(enabled);
}

bool AudioEngine::IsAccompanimentHarmonijaEnabled() const
{
    return accompaniment_.IsHarmonijaEnabled();
}

void AudioEngine::SetAccompanimentTempoBpm(double bpm)
{
    accompaniment_.SetTempoBpm(bpm);
}

double AudioEngine::GetAccompanimentTempoBpm() const
{
    return accompaniment_.GetTempoBpm();
}

void AudioEngine::PlayAccompaniment()
{
    accompaniment_.Play();
}

void AudioEngine::StopAccompaniment()
{
    accompaniment_.Stop();
}

bool AudioEngine::IsAccompanimentPlaying() const
{
    return accompaniment_.IsPlaying();
}

void AudioEngine::SetAccompanimentChordInputAutoFromKeyboard(bool autoFromKeyboard)
{
    accompaniment_.SetChordInputAutoFromKeyboard(autoFromKeyboard);
}

bool AudioEngine::IsAccompanimentChordInputAutoFromKeyboard() const
{
    return accompaniment_.IsChordInputAutoFromKeyboard();
}

void AudioEngine::SetAccompanimentSplitPoint(int midiNote)
{
    accompaniment_.SetSplitPoint(midiNote);
}

int AudioEngine::GetAccompanimentSplitPoint() const
{
    return accompaniment_.GetSplitPoint();
}

void AudioEngine::SetAccompanimentManualChord(int rootPitchClass, ChordQuality quality)
{
    accompaniment_.SetManualChord(rootPitchClass, quality);
}

int AudioEngine::GetAccompanimentCurrentChordRootPitchClass() const
{
    return accompaniment_.GetCurrentChordRootPitchClass();
}

ChordQuality AudioEngine::GetAccompanimentCurrentChordQuality() const
{
    return accompaniment_.GetCurrentChordQuality();
}

bool AudioEngine::LoadAccompanimentLayerSoundFontBank(AccompanimentEngine::MelodicLayer layer, const std::string& sf2Path, std::string& outError)
{
    return accompaniment_.LoadLayerSoundFontBank(layer, sf2Path, outError);
}

void AudioEngine::UnloadAccompanimentLayerSoundFontBank(AccompanimentEngine::MelodicLayer layer)
{
    accompaniment_.UnloadLayerSoundFontBank(layer);
}

bool AudioEngine::IsAccompanimentLayerSoundFontBankLoaded(AccompanimentEngine::MelodicLayer layer) const
{
    return accompaniment_.IsLayerSoundFontBankLoaded(layer);
}

std::string AudioEngine::GetAccompanimentLayerSoundFontBankName(AccompanimentEngine::MelodicLayer layer) const
{
    return accompaniment_.GetLayerSoundFontBankName(layer);
}

int AudioEngine::GetAccompanimentLayerSoundFontPresetCount(AccompanimentEngine::MelodicLayer layer) const
{
    return accompaniment_.GetLayerSoundFontPresetCount(layer);
}

std::string AudioEngine::GetAccompanimentLayerSoundFontPresetName(AccompanimentEngine::MelodicLayer layer, int presetIndex) const
{
    return accompaniment_.GetLayerSoundFontPresetName(layer, presetIndex);
}

bool AudioEngine::SelectAccompanimentLayerSoundFontPreset(AccompanimentEngine::MelodicLayer layer, int presetIndex)
{
    return accompaniment_.SelectLayerSoundFontPreset(layer, presetIndex);
}

int AudioEngine::GetSelectedAccompanimentLayerSoundFontPresetIndex(AccompanimentEngine::MelodicLayer layer) const
{
    return accompaniment_.GetSelectedLayerSoundFontPresetIndex(layer);
}

void AudioEngine::SetAccompanimentLayerInstrumentMode(AccompanimentEngine::MelodicLayer layer, AccompanimentEngine::LayerInstrumentMode mode)
{
    accompaniment_.SetLayerInstrumentMode(layer, mode);
}

AccompanimentEngine::LayerInstrumentMode AudioEngine::GetAccompanimentLayerInstrumentMode(AccompanimentEngine::MelodicLayer layer) const
{
    return accompaniment_.GetLayerInstrumentMode(layer);
}

bool AudioEngine::LoadAccompanimentLayerVstInstrument(AccompanimentEngine::MelodicLayer layer, const std::string& modulePath, std::string& outError)
{
    return accompaniment_.LoadLayerVstInstrument(layer, modulePath, outError);
}

void AudioEngine::UnloadAccompanimentLayerVstInstrument(AccompanimentEngine::MelodicLayer layer)
{
    accompaniment_.UnloadLayerVstInstrument(layer);
}

bool AudioEngine::IsAccompanimentLayerVstInstrumentLoaded(AccompanimentEngine::MelodicLayer layer) const
{
    return accompaniment_.IsLayerVstInstrumentLoaded(layer);
}

std::string AudioEngine::GetAccompanimentLayerVstInstrumentName(AccompanimentEngine::MelodicLayer layer) const
{
    return accompaniment_.GetLayerVstInstrumentName(layer);
}

void AudioEngine::SetAccompanimentChordVoicing(ChordVoicing voicing)
{
    accompaniment_.SetChordVoicing(voicing);
}

ChordVoicing AudioEngine::GetAccompanimentChordVoicing() const
{
    return accompaniment_.GetChordVoicing();
}
