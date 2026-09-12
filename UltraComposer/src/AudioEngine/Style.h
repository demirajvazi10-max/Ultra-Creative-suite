#pragma once

#include <string>
#include <vector>

// One drum-layer event: which fixed percussion voice (see DrumVoiceType in
// DrumSynth.h) fires, and when within the pattern's loop. Kept as a plain
// int here (not DrumVoiceType) so this header doesn't need to include
// DrumSynth.h - Style.cpp casts it when building the built-in bank.
struct DrumHit
{
    double startBeat = 0.0;
    int drumVoice = 0;
    float velocity = 0.8f;
};

// One bass/kontra/harmonija event. Unlike a plain SequencedNote, this
// stores a *chord tone slot* (see Chord.h's ChordToneSemitones) rather
// than an absolute pitch, so the very same pattern automatically follows
// whatever chord is currently active - that's the whole point of an
// auto-accompaniment style. 'chordToneIndices' can hold more than one slot
// at once: bass patterns normally use just one (a single bass note),
// kontra/harmonija patterns use several (a chord stab or a sustained pad).
struct RelativePatternChordHit
{
    double startBeat = 0.0;
    double lengthBeats = 1.0;
    std::vector<int> chordToneIndices; // 0=root, 1=third, 2=fifth, 3=seventh/color
    int octaveOffset = 0;              // relative to the layer's own base octave
    float velocity = 0.7f;
};

// One complete built-in accompaniment style: a drum pattern plus three
// harmonic layers (bass/kontra/harmonija), all sharing one loop length and
// carrying a suggested tempo. Deliberately a single-bar (or single-measure,
// for the waltz) loop for this first bank - see README.md's
// "Auto-accompaniment" section for the reasoning and for how to extend
// this with longer, varied (e.g. verse/fill) patterns later.
struct Style
{
    std::string name;
    double suggestedTempoBpm = 100.0;
    double beatsPerBar = 4.0; // informational only (e.g. 3.0 for a waltz) - lengthBeats below is what actually drives playback
    double lengthBeats = 4.0;
    std::vector<DrumHit> drumHits;
    std::vector<RelativePatternChordHit> bassNotes;
    std::vector<RelativePatternChordHit> kontraNotes;
    std::vector<RelativePatternChordHit> harmonijaNotes;
};

// The built-in starter rhythm bank. Returns a freshly built vector each
// call (styles are small, plain data - no need to cache), in a fixed order
// the native/UI index-based API relies on staying stable across releases:
// append new styles at the end, don't reorder or remove existing ones, so
// a previously-chosen style index keeps meaning the same style later.
std::vector<Style> BuiltInStyles();
