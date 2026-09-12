#include "AccompanimentEngine.h"

#include <algorithm>
#include <cstring>

namespace
{
    int PackChord(const Chord& chord)
    {
        return static_cast<int>(chord.quality) * 12 + chord.rootPitchClass;
    }

    Chord UnpackChord(int packed)
    {
        int normalized = packed < 0 ? 0 : packed;
        return Chord{normalized % 12, static_cast<ChordQuality>(normalized / 12)};
    }

    // Flushes every pitch a layer currently has held into 'offEvents' and
    // clears the tracking - used both for the ordinary "pattern hit ended"
    // case (mirrored per-hit in ProcessLayerSegment below) and as a safety
    // net whenever a layer needs to fall silent immediately (disabled,
    // Stop(), style change, loop wrap, instrument-source switch).
    void FlushLayer(std::vector<std::vector<int>>& lastResolved, std::vector<int>& offEvents)
    {
        for (auto& pitches : lastResolved)
        {
            for (int pitch : pitches)
            {
                offEvents.push_back(pitch);
            }
            pitches.clear();
        }
    }

    // Applies a ChordVoicing override (see AccompanimentEngine.h's enum) to
    // one pattern hit's authored chord-tone indices. 'allowOverride' is
    // false for bass (a bass hit's index picks WHICH single note is the
    // bass note - root vs fifth, say - not a stack of tones, so forcing a
    // fixed set here would turn a walking bass line into a bass chord) and
    // true for kontra/harmonija, so this is a no-op (returns 'authored'
    // unchanged) whenever either that flag is false or voicing is
    // AsAuthored. Every ChordQuality fills all 4 chord-tone slots (see
    // Chord.h's ChordToneSemitones), so Triad's {0,1,2} and Seventh's
    // {0,1,2,3} always resolve to real notes regardless of which chord is
    // currently active - no clamping needed.
    std::vector<int> ApplyChordVoicing(const std::vector<int>& authored, ChordVoicing voicing, bool allowOverride)
    {
        if (!allowOverride || voicing == ChordVoicing::AsAuthored)
        {
            return authored;
        }
        if (voicing == ChordVoicing::Triad)
        {
            return {0, 1, 2};
        }
        return {0, 1, 2, 3}; // Seventh
    }

    // Walks one [windowStart, windowEnd) segment of a melodic layer's
    // pattern (bass/kontra/harmonija), resolving each hit's chord tones
    // against whatever chord is current right now, exactly at the moment
    // it starts - so a note already sounding is never retroactively
    // retuned by a later chord change, only the *next* hit picks up the
    // new chord (an already-sounding hit still gets retuned immediately by
    // Advance()'s separate retune step below, whenever the chord actually
    // changes). Mirrors Sequencer::Advance's half-open-interval,
    // walk-forward-in-segments approach.
    void ProcessLayerSegment(const std::vector<RelativePatternChordHit>& pattern,
                              std::vector<std::vector<int>>& lastResolved,
                              double windowStart, double windowEnd, bool wraps,
                              const Chord& chord, int baseMidiNote,
                              ChordVoicing voicing, bool allowVoicingOverride,
                              std::vector<std::pair<int, float>>& onEvents,
                              std::vector<int>& offEvents)
    {
        for (size_t i = 0; i < pattern.size() && i < lastResolved.size(); ++i)
        {
            const RelativePatternChordHit& hit = pattern[i];
            double endBeat = hit.startBeat + hit.lengthBeats;

            if (hit.startBeat >= windowStart && hit.startBeat < windowEnd)
            {
                std::vector<int> toneIndices = ApplyChordVoicing(hit.chordToneIndices, voicing, allowVoicingOverride);
                std::vector<int> resolved;
                resolved.reserve(toneIndices.size());
                for (int toneIndex : toneIndices)
                {
                    resolved.push_back(ResolveChordToneMidiNote(chord, toneIndex, hit.octaveOffset, baseMidiNote));
                }
                for (int pitch : resolved)
                {
                    onEvents.emplace_back(pitch, hit.velocity);
                }
                lastResolved[i] = std::move(resolved);
            }

            if (endBeat >= windowStart && endBeat < windowEnd)
            {
                for (int pitch : lastResolved[i])
                {
                    offEvents.push_back(pitch);
                }
                lastResolved[i].clear();
            }
        }

        if (wraps)
        {
            FlushLayer(lastResolved, offEvents);
        }
    }

    // Re-voices every hit a layer currently has sustaining (lastResolved[i]
    // non-empty) against a NEW chord (or a newly changed ChordVoicing),
    // right away - this is what makes a chord change (or a voicing change)
    // audible immediately instead of only at that hit's next scheduled
    // onset. A hit with nothing currently sustaining is left alone (nothing
    // to retune, and it will resolve fresh against the new chord/voicing
    // whenever it next starts anyway).
    void RetuneLayerToChord(const std::vector<RelativePatternChordHit>& pattern,
                             std::vector<std::vector<int>>& lastResolved,
                             const Chord& chord, int baseMidiNote,
                             ChordVoicing voicing, bool allowVoicingOverride,
                             std::vector<std::pair<int, float>>& onEvents,
                             std::vector<int>& offEvents)
    {
        for (size_t i = 0; i < pattern.size() && i < lastResolved.size(); ++i)
        {
            if (lastResolved[i].empty())
            {
                continue;
            }

            for (int pitch : lastResolved[i])
            {
                offEvents.push_back(pitch);
            }

            const RelativePatternChordHit& hit = pattern[i];
            std::vector<int> toneIndices = ApplyChordVoicing(hit.chordToneIndices, voicing, allowVoicingOverride);
            std::vector<int> resolved;
            resolved.reserve(toneIndices.size());
            for (int toneIndex : toneIndices)
            {
                resolved.push_back(ResolveChordToneMidiNote(chord, toneIndex, hit.octaveOffset, baseMidiNote));
            }
            for (int pitch : resolved)
            {
                onEvents.emplace_back(pitch, hit.velocity);
            }
            lastResolved[i] = std::move(resolved);
        }
    }
}

void AccompanimentEngine::Prepare(double sampleRate)
{
    sampleRate_ = sampleRate;
    bassSynth_.Prepare(sampleRate);
    kontraSynth_.Prepare(sampleRate);
    harmonijaSynth_.Prepare(sampleRate);
    drumSynth_.Prepare(sampleRate);

    std::lock_guard<std::mutex> lock(mutex_);
    styles_ = BuiltInStyles();
    builtInStyleCount_ = static_cast<int>(styles_.size());
    selectedStyleIndex_ = styles_.empty() ? -1 : 0;
    if (selectedStyleIndex_ >= 0)
    {
        tempoBpm_ = styles_[selectedStyleIndex_].suggestedTempoBpm;
        bassLastResolved_.assign(styles_[selectedStyleIndex_].bassNotes.size(), {});
        kontraLastResolved_.assign(styles_[selectedStyleIndex_].kontraNotes.size(), {});
        harmonijaLastResolved_.assign(styles_[selectedStyleIndex_].harmonijaNotes.size(), {});
    }
}

int AccompanimentEngine::GetStyleCount() const
{
    std::lock_guard<std::mutex> lock(mutex_);
    return static_cast<int>(styles_.size());
}

std::string AccompanimentEngine::GetStyleName(int index) const
{
    std::lock_guard<std::mutex> lock(mutex_);
    if (index < 0 || index >= static_cast<int>(styles_.size()))
    {
        return std::string();
    }
    return styles_[index].name;
}

bool AccompanimentEngine::SetStyleIndex(int index)
{
    std::vector<int> bassOff, kontraOff, harmonijaOff;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        if (index < 0 || index >= static_cast<int>(styles_.size()))
        {
            return false;
        }

        FlushLayer(bassLastResolved_, bassOff);
        FlushLayer(kontraLastResolved_, kontraOff);
        FlushLayer(harmonijaLastResolved_, harmonijaOff);

        selectedStyleIndex_ = index;
        positionBeats_ = 0.0;
        tempoBpm_ = styles_[index].suggestedTempoBpm;
        bassLastResolved_.assign(styles_[index].bassNotes.size(), {});
        kontraLastResolved_.assign(styles_[index].kontraNotes.size(), {});
        harmonijaLastResolved_.assign(styles_[index].harmonijaNotes.size(), {});
    }
    DispatchLayer(MelodicLayer::Bass, bassOff, {});
    DispatchLayer(MelodicLayer::Kontra, kontraOff, {});
    DispatchLayer(MelodicLayer::Harmonija, harmonijaOff, {});
    return true;
}

int AccompanimentEngine::SetCustomStyle(const Style& style, int replaceIndex)
{
    if (style.drumHits.empty() && style.bassNotes.empty() && style.kontraNotes.empty() && style.harmonijaNotes.empty())
    {
        return -1;
    }

    int index;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        if (replaceIndex >= builtInStyleCount_ && replaceIndex < static_cast<int>(styles_.size()))
        {
            // A valid existing custom style was named - edit it in place so
            // its "Ritam" entry doesn't shift or duplicate while it's still
            // being tweaked.
            styles_[replaceIndex] = style;
            index = replaceIndex;
        }
        else
        {
            // No valid existing slot given - added as a new custom style,
            // alongside any others already saved.
            styles_.push_back(style);
            index = static_cast<int>(styles_.size()) - 1;
        }
    }
    // Outside the lock above - SetStyleIndex takes it again itself, and also
    // resets playback position / adopts the suggested tempo / flushes
    // whatever was sustaining, exactly like picking any built-in style.
    SetStyleIndex(index);
    return index;
}

bool AccompanimentEngine::RemoveCustomStyle(int index)
{
    bool wasSelected = false;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        if (index < builtInStyleCount_ || index >= static_cast<int>(styles_.size()))
        {
            return false;
        }
        styles_.erase(styles_.begin() + index);
        if (selectedStyleIndex_ == index)
        {
            wasSelected = true;
        }
        else if (selectedStyleIndex_ > index)
        {
            // The selected style is still the same style, just shifted one
            // slot down by the removal - no need to reset its playback
            // position/resolved-note state for a plain renumbering.
            selectedStyleIndex_--;
        }
    }
    if (wasSelected)
    {
        // The style actually playing just disappeared - fall back to style
        // 0 (always a built-in one, so always valid) via the normal
        // selection path, same as picking any style from the UI.
        SetStyleIndex(0);
    }
    return true;
}

int AccompanimentEngine::GetSelectedStyleIndex() const
{
    std::lock_guard<std::mutex> lock(mutex_);
    return selectedStyleIndex_;
}

void AccompanimentEngine::SetDrumsEnabled(bool enabled) { drumsEnabled_.store(enabled); }
bool AccompanimentEngine::AreDrumsEnabled() const { return drumsEnabled_.load(); }
void AccompanimentEngine::SetBassEnabled(bool enabled) { bassEnabled_.store(enabled); }
bool AccompanimentEngine::IsBassEnabled() const { return bassEnabled_.load(); }
void AccompanimentEngine::SetKontraEnabled(bool enabled) { kontraEnabled_.store(enabled); }
bool AccompanimentEngine::IsKontraEnabled() const { return kontraEnabled_.load(); }
void AccompanimentEngine::SetHarmonijaEnabled(bool enabled) { harmonijaEnabled_.store(enabled); }
bool AccompanimentEngine::IsHarmonijaEnabled() const { return harmonijaEnabled_.load(); }

void AccompanimentEngine::SetTempoBpm(double bpm)
{
    std::lock_guard<std::mutex> lock(mutex_);
    tempoBpm_ = bpm > 0.0 ? bpm : 100.0;
}

double AccompanimentEngine::GetTempoBpm() const
{
    std::lock_guard<std::mutex> lock(mutex_);
    return tempoBpm_;
}

void AccompanimentEngine::Play()
{
    std::vector<int> bassOff, kontraOff, harmonijaOff;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        FlushLayer(bassLastResolved_, bassOff);
        FlushLayer(kontraLastResolved_, kontraOff);
        FlushLayer(harmonijaLastResolved_, harmonijaOff);

        positionBeats_ = 0.0;
        playing_ = true;

        if (selectedStyleIndex_ >= 0 && selectedStyleIndex_ < static_cast<int>(styles_.size()))
        {
            const Style& style = styles_[selectedStyleIndex_];
            bassLastResolved_.assign(style.bassNotes.size(), {});
            kontraLastResolved_.assign(style.kontraNotes.size(), {});
            harmonijaLastResolved_.assign(style.harmonijaNotes.size(), {});
        }
    }
    DispatchLayer(MelodicLayer::Bass, bassOff, {});
    DispatchLayer(MelodicLayer::Kontra, kontraOff, {});
    DispatchLayer(MelodicLayer::Harmonija, harmonijaOff, {});
}

void AccompanimentEngine::Stop()
{
    std::vector<int> bassOff, kontraOff, harmonijaOff;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        playing_ = false;
        positionBeats_ = 0.0;
        FlushLayer(bassLastResolved_, bassOff);
        FlushLayer(kontraLastResolved_, kontraOff);
        FlushLayer(harmonijaLastResolved_, harmonijaOff);
    }
    DispatchLayer(MelodicLayer::Bass, bassOff, {});
    DispatchLayer(MelodicLayer::Kontra, kontraOff, {});
    DispatchLayer(MelodicLayer::Harmonija, harmonijaOff, {});
}

bool AccompanimentEngine::IsPlaying() const
{
    std::lock_guard<std::mutex> lock(mutex_);
    return playing_;
}

void AccompanimentEngine::SetChordInputAutoFromKeyboard(bool autoFromKeyboard)
{
    autoChordFromKeyboard_.store(autoFromKeyboard);
    if (!autoFromKeyboard)
    {
        // Leaving auto mode: forget whatever was physically held so a
        // later switch back to auto mode starts from a clean slate
        // instead of reacting to stale key state.
        std::lock_guard<std::mutex> lock(heldNotesMutex_);
        heldNotes_.clear();
    }
}

bool AccompanimentEngine::IsChordInputAutoFromKeyboard() const
{
    return autoChordFromKeyboard_.load();
}

void AccompanimentEngine::SetSplitPoint(int midiNote)
{
    if (midiNote < 0) midiNote = 0;
    if (midiNote > 127) midiNote = 127;
    splitPoint_.store(midiNote);
}

int AccompanimentEngine::GetSplitPoint() const
{
    return splitPoint_.load();
}

Chord AccompanimentEngine::DetectChordFromHeldNotes(const std::vector<int>& heldNotes)
{
    int rootNote = *std::min_element(heldNotes.begin(), heldNotes.end());
    int rootPitchClass = ((rootNote % 12) + 12) % 12;

    bool hasMinorThird = false;
    bool hasMajorThird = false;
    bool hasMinorSeventh = false;
    bool hasDiminishedFifth = false;

    for (int note : heldNotes)
    {
        int interval = ((note - rootNote) % 12 + 12) % 12;
        if (interval == 3) hasMinorThird = true;
        if (interval == 4) hasMajorThird = true;
        if (interval == 10) hasMinorSeventh = true;
        if (interval == 6) hasDiminishedFifth = true;
    }

    ChordQuality quality = ChordQuality::Major;
    if (hasMinorThird && hasDiminishedFifth)
    {
        quality = ChordQuality::Diminished;
    }
    else if (hasMinorThird)
    {
        quality = ChordQuality::Minor;
    }
    else if (hasMajorThird && hasMinorSeventh)
    {
        quality = ChordQuality::Dominant7;
    }
    else
    {
        // A single held note (or root+fifth alone, or root+major third)
        // defaults to Major - the most common expectation, and matches
        // what most keyboards' single-finger/fingered chord modes do too.
        quality = ChordQuality::Major;
    }

    return Chord{rootPitchClass, quality};
}

void AccompanimentEngine::NoteHeldForChordDetection(int midiNote, bool isOn)
{
    std::vector<int> snapshot;
    {
        std::lock_guard<std::mutex> lock(heldNotesMutex_);
        if (isOn)
        {
            if (std::find(heldNotes_.begin(), heldNotes_.end(), midiNote) == heldNotes_.end())
            {
                heldNotes_.push_back(midiNote);
            }
        }
        else
        {
            heldNotes_.erase(std::remove(heldNotes_.begin(), heldNotes_.end(), midiNote), heldNotes_.end());
        }
        snapshot = heldNotes_;
    }

    if (snapshot.empty())
    {
        // Keep the last chord sounding rather than silencing the
        // accompaniment the instant the left hand lifts - matches how
        // real keyboards behave.
        return;
    }

    currentChordPacked_.store(PackChord(DetectChordFromHeldNotes(snapshot)));
}

void AccompanimentEngine::SetManualChord(int rootPitchClass, ChordQuality quality)
{
    int normalizedRoot = ((rootPitchClass % 12) + 12) % 12;
    currentChordPacked_.store(PackChord(Chord{normalizedRoot, quality}));
}

int AccompanimentEngine::GetCurrentChordRootPitchClass() const
{
    return UnpackChord(currentChordPacked_.load()).rootPitchClass;
}

ChordQuality AccompanimentEngine::GetCurrentChordQuality() const
{
    return UnpackChord(currentChordPacked_.load()).quality;
}

Synth& AccompanimentEngine::SynthFor(MelodicLayer layer)
{
    switch (layer)
    {
    case MelodicLayer::Kontra: return kontraSynth_;
    case MelodicLayer::Harmonija: return harmonijaSynth_;
    case MelodicLayer::Bass:
    default: return bassSynth_;
    }
}

SoundFontHost& AccompanimentEngine::SoundFontHostFor(MelodicLayer layer)
{
    switch (layer)
    {
    case MelodicLayer::Kontra: return kontraSoundFont_;
    case MelodicLayer::Harmonija: return harmonijaSoundFont_;
    case MelodicLayer::Bass:
    default: return bassSoundFont_;
    }
}

const SoundFontHost& AccompanimentEngine::SoundFontHostFor(MelodicLayer layer) const
{
    return const_cast<AccompanimentEngine*>(this)->SoundFontHostFor(layer);
}

VstHost& AccompanimentEngine::VstHostFor(MelodicLayer layer)
{
    switch (layer)
    {
    case MelodicLayer::Kontra: return kontraVst_;
    case MelodicLayer::Harmonija: return harmonijaVst_;
    case MelodicLayer::Bass:
    default: return bassVst_;
    }
}

const VstHost& AccompanimentEngine::VstHostFor(MelodicLayer layer) const
{
    return const_cast<AccompanimentEngine*>(this)->VstHostFor(layer);
}

std::atomic<int>& AccompanimentEngine::InstrumentModeFor(MelodicLayer layer)
{
    switch (layer)
    {
    case MelodicLayer::Kontra: return kontraInstrumentMode_;
    case MelodicLayer::Harmonija: return harmonijaInstrumentMode_;
    case MelodicLayer::Bass:
    default: return bassInstrumentMode_;
    }
}

const std::atomic<int>& AccompanimentEngine::InstrumentModeFor(MelodicLayer layer) const
{
    return const_cast<AccompanimentEngine*>(this)->InstrumentModeFor(layer);
}

std::vector<std::vector<int>>& AccompanimentEngine::LastResolvedFor(MelodicLayer layer)
{
    switch (layer)
    {
    case MelodicLayer::Kontra: return kontraLastResolved_;
    case MelodicLayer::Harmonija: return harmonijaLastResolved_;
    case MelodicLayer::Bass:
    default: return bassLastResolved_;
    }
}

void AccompanimentEngine::DispatchLayer(MelodicLayer layer, const std::vector<int>& offEvents,
                                         const std::vector<std::pair<int, float>>& onEvents)
{
    LayerInstrumentMode mode = static_cast<LayerInstrumentMode>(InstrumentModeFor(layer).load());
    switch (mode)
    {
    case LayerInstrumentMode::SoundFont:
    {
        SoundFontHost& host = SoundFontHostFor(layer);
        for (int pitch : offEvents) host.NoteOff(pitch);
        for (const auto& [pitch, velocity] : onEvents) host.NoteOn(pitch, velocity);
        break;
    }
    case LayerInstrumentMode::Vst:
    {
        VstHost& vst = VstHostFor(layer);
        for (int pitch : offEvents) vst.NoteOff(pitch);
        for (const auto& [pitch, velocity] : onEvents) vst.NoteOn(pitch, velocity);
        break;
    }
    case LayerInstrumentMode::BuiltInSynth:
    default:
    {
        Synth& synth = SynthFor(layer);
        for (int pitch : offEvents) synth.NoteOff(pitch);
        for (const auto& [pitch, velocity] : onEvents) synth.NoteOn(pitch, velocity);
        break;
    }
    }
}

bool AccompanimentEngine::LoadLayerSoundFontBank(MelodicLayer layer, const std::string& sf2Path, std::string& outError)
{
    bool ok = SoundFontHostFor(layer).LoadBank(sf2Path, sampleRate_, outError);
    if (ok)
    {
        // Loading a bank for this layer is itself the action that commits
        // it to using that bank - matches MultiTrackSequencer's per-track
        // SoundFont load.
        SetLayerInstrumentMode(layer, LayerInstrumentMode::SoundFont);
    }
    return ok;
}

void AccompanimentEngine::UnloadLayerSoundFontBank(MelodicLayer layer)
{
    SoundFontHostFor(layer).UnloadBank();
}

bool AccompanimentEngine::IsLayerSoundFontBankLoaded(MelodicLayer layer) const
{
    return SoundFontHostFor(layer).IsLoaded();
}

std::string AccompanimentEngine::GetLayerSoundFontBankName(MelodicLayer layer) const
{
    return SoundFontHostFor(layer).GetLoadedBankName();
}

int AccompanimentEngine::GetLayerSoundFontPresetCount(MelodicLayer layer) const
{
    return SoundFontHostFor(layer).GetPresetCount();
}

std::string AccompanimentEngine::GetLayerSoundFontPresetName(MelodicLayer layer, int presetIndex) const
{
    return SoundFontHostFor(layer).GetPresetName(presetIndex);
}

bool AccompanimentEngine::SelectLayerSoundFontPreset(MelodicLayer layer, int presetIndex)
{
    return SoundFontHostFor(layer).SelectPreset(presetIndex);
}

int AccompanimentEngine::GetSelectedLayerSoundFontPresetIndex(MelodicLayer layer) const
{
    return SoundFontHostFor(layer).GetSelectedPresetIndex();
}

bool AccompanimentEngine::LoadLayerVstInstrument(MelodicLayer layer, const std::string& modulePath, std::string& outError)
{
    bool ok = VstHostFor(layer).LoadInstrument(modulePath, sampleRate_, kMaxScratchFrames, outError);
    if (ok)
    {
        // Same commits-on-load convention as LoadLayerSoundFontBank above.
        SetLayerInstrumentMode(layer, LayerInstrumentMode::Vst);
    }
    return ok;
}

void AccompanimentEngine::UnloadLayerVstInstrument(MelodicLayer layer)
{
    VstHostFor(layer).UnloadInstrument();
}

bool AccompanimentEngine::IsLayerVstInstrumentLoaded(MelodicLayer layer) const
{
    return VstHostFor(layer).IsLoaded();
}

std::string AccompanimentEngine::GetLayerVstInstrumentName(MelodicLayer layer) const
{
    return VstHostFor(layer).GetLoadedPluginName();
}

void AccompanimentEngine::SetLayerInstrumentMode(MelodicLayer layer, LayerInstrumentMode mode)
{
    int newModeInt = static_cast<int>(mode);
    int oldModeInt = InstrumentModeFor(layer).exchange(newModeInt);
    if (oldModeInt == newModeInt)
    {
        return;
    }

    // Flush whatever this layer currently has sustaining through the OLD
    // source before the switch, so a note already sounding on the built-in
    // synth (say) doesn't hang forever once new hits start going to the
    // SoundFont instead (or vice versa) - same safety net already used
    // when a layer is disabled outright.
    std::vector<int> off;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        FlushLayer(LastResolvedFor(layer), off);
    }

    LayerInstrumentMode oldMode = static_cast<LayerInstrumentMode>(oldModeInt);
    switch (oldMode)
    {
    case LayerInstrumentMode::SoundFont:
    {
        SoundFontHost& host = SoundFontHostFor(layer);
        for (int pitch : off) host.NoteOff(pitch);
        break;
    }
    case LayerInstrumentMode::Vst:
    {
        VstHost& vst = VstHostFor(layer);
        for (int pitch : off) vst.NoteOff(pitch);
        break;
    }
    case LayerInstrumentMode::BuiltInSynth:
    default:
    {
        Synth& synth = SynthFor(layer);
        for (int pitch : off) synth.NoteOff(pitch);
        break;
    }
    }
}

AccompanimentEngine::LayerInstrumentMode AccompanimentEngine::GetLayerInstrumentMode(MelodicLayer layer) const
{
    return static_cast<LayerInstrumentMode>(InstrumentModeFor(layer).load());
}

void AccompanimentEngine::SetChordVoicing(ChordVoicing voicing)
{
    chordVoicing_.store(static_cast<int>(voicing));
}

ChordVoicing AccompanimentEngine::GetChordVoicing() const
{
    return static_cast<ChordVoicing>(chordVoicing_.load());
}

void AccompanimentEngine::Advance(int frames, double sampleRate)
{
    std::vector<std::pair<int, float>> bassOn, kontraOn, harmonijaOn;
    std::vector<int> bassOff, kontraOff, harmonijaOff;
    std::vector<std::pair<int, float>> drumTriggers;

    int chordPackedRaw = currentChordPacked_.load();
    Chord chord = UnpackChord(chordPackedRaw);
    int voicingRaw = chordVoicing_.load();
    ChordVoicing voicing = static_cast<ChordVoicing>(voicingRaw);
    bool drumsOn = drumsEnabled_.load();
    bool bassOn_ = bassEnabled_.load();
    bool kontraOn_ = kontraEnabled_.load();
    bool harmonijaOn_ = harmonijaEnabled_.load();

    {
        std::lock_guard<std::mutex> lock(mutex_);

        if (!bassOn_) FlushLayer(bassLastResolved_, bassOff);
        if (!kontraOn_) FlushLayer(kontraLastResolved_, kontraOff);
        if (!harmonijaOn_) FlushLayer(harmonijaLastResolved_, harmonijaOff);

        bool hasStyle = selectedStyleIndex_ >= 0 && selectedStyleIndex_ < static_cast<int>(styles_.size());
        const Style* stylePtr = hasStyle ? &styles_[selectedStyleIndex_] : nullptr;

        // The chord OR the chord-voicing selector changed since the last
        // block: re-voice whatever is currently sustaining right away
        // rather than waiting for each pattern hit's own next scheduled
        // onset (see RetuneLayerToChord's comment - some styles hold one
        // note per whole bar, and without this a change could go unheard
        // for several seconds).
        if (chordPackedRaw != lastChordPackedForRetune_ || voicingRaw != lastVoicingForRetune_)
        {
            lastChordPackedForRetune_ = chordPackedRaw;
            lastVoicingForRetune_ = voicingRaw;
            if (stylePtr != nullptr)
            {
                if (bassOn_)
                {
                    RetuneLayerToChord(stylePtr->bassNotes, bassLastResolved_, chord, kBassBaseNote,
                                        voicing, /*allowVoicingOverride=*/false, bassOn, bassOff);
                }
                if (kontraOn_)
                {
                    RetuneLayerToChord(stylePtr->kontraNotes, kontraLastResolved_, chord, kKontraBaseNote,
                                        voicing, /*allowVoicingOverride=*/true, kontraOn, kontraOff);
                }
                if (harmonijaOn_)
                {
                    RetuneLayerToChord(stylePtr->harmonijaNotes, harmonijaLastResolved_, chord, kHarmonijaBaseNote,
                                        voicing, /*allowVoicingOverride=*/true, harmonijaOn, harmonijaOff);
                }
            }
        }

        if (playing_ && sampleRate > 0.0 && frames > 0 && hasStyle)
        {
            const Style& style = *stylePtr;
            double loopLength = style.lengthBeats > 0.0 ? style.lengthBeats : 4.0;
            double beatsPerSecond = tempoBpm_ / 60.0;
            double remainingBeats = (static_cast<double>(frames) / sampleRate) * beatsPerSecond;
            double windowStart = positionBeats_;

            for (int guard = 0; guard < 64 && remainingBeats > 0.0; ++guard)
            {
                bool wraps = (windowStart + remainingBeats) >= loopLength;
                double windowEnd = wraps ? loopLength : (windowStart + remainingBeats);

                if (drumsOn)
                {
                    for (const DrumHit& hit : style.drumHits)
                    {
                        if (hit.startBeat >= windowStart && hit.startBeat < windowEnd)
                        {
                            drumTriggers.emplace_back(hit.drumVoice, hit.velocity);
                        }
                    }
                }

                if (bassOn_)
                {
                    ProcessLayerSegment(style.bassNotes, bassLastResolved_, windowStart, windowEnd, wraps,
                                         chord, kBassBaseNote, voicing, /*allowVoicingOverride=*/false, bassOn, bassOff);
                }
                if (kontraOn_)
                {
                    ProcessLayerSegment(style.kontraNotes, kontraLastResolved_, windowStart, windowEnd, wraps,
                                         chord, kKontraBaseNote, voicing, /*allowVoicingOverride=*/true, kontraOn, kontraOff);
                }
                if (harmonijaOn_)
                {
                    ProcessLayerSegment(style.harmonijaNotes, harmonijaLastResolved_, windowStart, windowEnd, wraps,
                                         chord, kHarmonijaBaseNote, voicing, /*allowVoicingOverride=*/true, harmonijaOn, harmonijaOff);
                }

                remainingBeats -= (windowEnd - windowStart);
                windowStart = wraps ? 0.0 : windowEnd;
            }

            positionBeats_ = windowStart;
        }
    } // <-- mutex released before dispatching to the owned instruments below

    DispatchLayer(MelodicLayer::Bass, bassOff, bassOn);
    DispatchLayer(MelodicLayer::Kontra, kontraOff, kontraOn);
    DispatchLayer(MelodicLayer::Harmonija, harmonijaOff, harmonijaOn);

    for (const auto& [voice, velocity] : drumTriggers)
    {
        drumSynth_.Trigger(static_cast<DrumVoiceType>(voice), velocity);
    }
}

void AccompanimentEngine::RenderAdditive(float* interleavedStereoOut, int frames)
{
    int clamped = frames < kMaxScratchFrames ? frames : kMaxScratchFrames;

    bassSynth_.Render(bassScratch_, clamped);
    kontraSynth_.Render(kontraScratch_, clamped);
    harmonijaSynth_.Render(harmonijaScratch_, clamped);
    drumSynth_.Render(drumScratch_, clamped);

    for (int i = 0; i < clamped; ++i)
    {
        float mono = bassScratch_[i] + kontraScratch_[i] + harmonijaScratch_[i] + drumScratch_[i];
        interleavedStereoOut[i * 2] += mono;
        interleavedStereoOut[i * 2 + 1] += mono;
    }

    // Any layer currently routed to its own SoundFont bank or VST3
    // instrument (see LayerInstrumentMode) adds its sound here too,
    // additively - same convention AudioEngine::PaCallback already uses to
    // mix the shared synth/VST/SoundFont together. Harmless (and silent) to
    // call this for a layer that has nothing loaded or no notes sounding.
    bassSoundFont_.RenderAdditive(interleavedStereoOut, clamped);
    kontraSoundFont_.RenderAdditive(interleavedStereoOut, clamped);
    harmonijaSoundFont_.RenderAdditive(interleavedStereoOut, clamped);
    bassVst_.RenderAdditive(interleavedStereoOut, clamped);
    kontraVst_.RenderAdditive(interleavedStereoOut, clamped);
    harmonijaVst_.RenderAdditive(interleavedStereoOut, clamped);
}
