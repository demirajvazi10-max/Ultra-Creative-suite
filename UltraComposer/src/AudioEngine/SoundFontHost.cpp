#include "SoundFontHost.h"

// TinySoundFont's tsf_load_filename() calls the plain (non "_s") fopen(),
// which MSVC otherwise flags as deprecated. Guarded with #ifndef so this
// doesn't clash if a build ever defines this project-wide too (see the
// comment in UltraComposer.AudioEngine.vcxproj) - only this one
// translation unit needs it.
#ifndef _CRT_SECURE_NO_WARNINGS
#define _CRT_SECURE_NO_WARNINGS
#endif

// TinySoundFont is a single-header library: exactly one translation unit
// in the whole program must #define TSF_IMPLEMENTATION before including
// it, to get the actual function bodies (every other file would only ever
// need tsf.h's declarations, but nothing else in this project includes
// it, so this one file carries both).
#define TSF_IMPLEMENTATION
#include "thirdparty/tsf/tsf.h"

#include <mutex>

struct SoundFontHost::Impl
{
    std::mutex mutex;

    tsf* font = nullptr;
    int presetIndex = 0;
    std::string bankFileName;
    bool loaded = false;
};

SoundFontHost::SoundFontHost() : impl_(new Impl())
{
}

SoundFontHost::~SoundFontHost()
{
    UnloadBank();
    delete impl_;
}

bool SoundFontHost::LoadBank(const std::string& sf2Path, double sampleRate, std::string& outError)
{
    // Reading and parsing a whole .sf2 file is genuinely slow - the bundled
    // ~30MB default bank alone measured 80-550ms locally, and a slower disk
    // (or one an antivirus is busy scanning) can easily take longer. The
    // real-time audio callback needs impl_->mutex on every single buffer
    // (RenderAdditive runs roughly every 5-6ms), so this whole load must
    // happen OUTSIDE the lock - holding it here would starve the audio
    // thread for hundreds of buffers in a row, and some audio backends
    // respond to that by giving up on the stream entirely (silence from
    // then on, for everything already playing too, and it doesn't come
    // back just because a bank gets unloaded afterwards - the stream is
    // already gone). Only the final pointer swap below needs the lock, and
    // that's just a few field assignments.
    tsf* font = tsf_load_filename(sf2Path.c_str());
    if (font == nullptr)
    {
        outError = "Ne mogu da ucitam SoundFont banku (proveri da li je putanja ispravna i da je fajl .sf2): " + sf2Path;
        return false;
    }

    tsf_set_output(font, TSF_STEREO_INTERLEAVED, static_cast<int>(sampleRate), 0.0f);
    tsf_set_max_voices(font, 64);

    const int presetCount = tsf_get_presetcount(font);
    if (presetCount <= 0)
    {
        tsf_close(font);
        outError = "Ovaj SoundFont fajl ne sadrzi nijedan instrument (preset).";
        return false;
    }

    // Prefer General MIDI's "Acoustic Grand Piano" (bank 0, program 0) as
    // the starting instrument, since that's what most people expect to
    // hear first; fall back to whatever preset 0 happens to be otherwise.
    int startPreset = tsf_get_presetindex(font, 0, 0);
    if (startPreset < 0)
    {
        startPreset = 0;
    }

    size_t lastSlash = sf2Path.find_last_of("/\\");
    std::string fileName = lastSlash == std::string::npos ? sf2Path : sf2Path.substr(lastSlash + 1);

    // Swap the new bank in - fast, lock held only for field assignments.
    // The old bank (if any) is closed afterwards, outside the lock too:
    // tsf_close() only frees memory (no file I/O), but for a large bank
    // that can still be a few milliseconds, which is still better spent
    // outside a lock the audio thread wants every ~5ms.
    tsf* oldFont = nullptr;
    {
        std::lock_guard<std::mutex> lock(impl_->mutex);
        oldFont = impl_->font;
        impl_->font = font;
        impl_->presetIndex = startPreset;
        impl_->bankFileName = fileName;
        impl_->loaded = true;
    }
    if (oldFont != nullptr)
    {
        tsf_close(oldFont);
    }
    return true;
}

void SoundFontHost::UnloadBank()
{
    // Same reasoning as LoadBank(): close the old font outside the lock.
    tsf* oldFont = nullptr;
    {
        std::lock_guard<std::mutex> lock(impl_->mutex);
        oldFont = impl_->font;
        impl_->font = nullptr;
        impl_->presetIndex = 0;
        impl_->bankFileName.clear();
        impl_->loaded = false;
    }
    if (oldFont != nullptr)
    {
        tsf_close(oldFont);
    }
}

bool SoundFontHost::IsLoaded() const
{
    std::lock_guard<std::mutex> lock(impl_->mutex);
    return impl_->loaded;
}

std::string SoundFontHost::GetLoadedBankName() const
{
    std::lock_guard<std::mutex> lock(impl_->mutex);
    return impl_->bankFileName;
}

int SoundFontHost::GetPresetCount() const
{
    std::lock_guard<std::mutex> lock(impl_->mutex);
    return impl_->loaded ? tsf_get_presetcount(impl_->font) : 0;
}

std::string SoundFontHost::GetPresetName(int presetIndex) const
{
    std::lock_guard<std::mutex> lock(impl_->mutex);
    if (!impl_->loaded || presetIndex < 0 || presetIndex >= tsf_get_presetcount(impl_->font))
    {
        return std::string();
    }
    const char* name = tsf_get_presetname(impl_->font, presetIndex);
    return name != nullptr ? std::string(name) : std::string();
}

bool SoundFontHost::SelectPreset(int presetIndex)
{
    std::lock_guard<std::mutex> lock(impl_->mutex);
    if (!impl_->loaded || presetIndex < 0 || presetIndex >= tsf_get_presetcount(impl_->font))
    {
        return false;
    }

    // Notes already sounding were voiced against the old instrument -
    // tsf_note_off() has to be called with the same preset index that
    // started the note, so there's no clean way to hand them over to the
    // new one. Cutting them off here is the same tradeoff a real
    // instrument-switch button makes in most simple DAWs.
    tsf_note_off_all(impl_->font);
    impl_->presetIndex = presetIndex;
    return true;
}

int SoundFontHost::GetSelectedPresetIndex() const
{
    std::lock_guard<std::mutex> lock(impl_->mutex);
    return impl_->presetIndex;
}

void SoundFontHost::NoteOn(int midiNote, float velocity)
{
    std::lock_guard<std::mutex> lock(impl_->mutex);
    if (impl_->loaded)
    {
        tsf_note_on(impl_->font, impl_->presetIndex, midiNote, velocity);
    }
}

void SoundFontHost::NoteOff(int midiNote)
{
    std::lock_guard<std::mutex> lock(impl_->mutex);
    if (impl_->loaded)
    {
        tsf_note_off(impl_->font, impl_->presetIndex, midiNote);
    }
}

void SoundFontHost::RenderAdditive(float* interleavedStereoOut, int frames)
{
    std::lock_guard<std::mutex> lock(impl_->mutex);
    if (!impl_->loaded || frames <= 0)
    {
        return;
    }

    // flag_mixing=1: add onto the caller's buffer instead of overwriting
    // it, so this mixes with the built-in synth and any loaded VST3
    // instrument instead of replacing them.
    tsf_render_float(impl_->font, interleavedStereoOut, frames, /*flag_mixing=*/1);
}
