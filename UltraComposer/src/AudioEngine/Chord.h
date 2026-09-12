#pragma once

// Shared chord vocabulary for the auto-accompaniment engine (Style.h /
// AccompanimentEngine.h). Deliberately a small, fixed set of qualities -
// covers the vast majority of pop/rock/folk songs the built-in styles are
// written for, without the extra UI/detection complexity a full jazz chord
// vocabulary (extensions, alterations, slash chords, ...) would add for
// very little practical benefit here.
enum class ChordQuality
{
    Major = 0,
    Minor = 1,
    Dominant7 = 2,
    Diminished = 3
};

constexpr int kChordQualityCount = 4;

// One chord: a root pitch class (0=C, 1=C#/Db, ... 11=B) plus a quality.
struct Chord
{
    int rootPitchClass = 0; // 0-11
    ChordQuality quality = ChordQuality::Major;
};

// Semitone offsets from the root for each of the 4 "chord tone slots" a
// RelativePatternChordHit (see Style.h) can reference: [root, third, fifth,
// seventh/color tone]. Every quality fills all 4 slots (even qualities
// without a natural 7th still get a sensible color tone) so a pattern can
// safely reference index 3 regardless of which quality happens to be
// active when it plays.
inline const int* ChordToneSemitones(ChordQuality quality)
{
    static constexpr int kMajor[4] = {0, 4, 7, 11};      // root, maj3, 5th, maj7
    static constexpr int kMinor[4] = {0, 3, 7, 10};      // root, min3, 5th, min7
    static constexpr int kDominant7[4] = {0, 4, 7, 10};  // root, maj3, 5th, min7
    static constexpr int kDiminished[4] = {0, 3, 6, 9};  // root, min3, dim5, dim7

    switch (quality)
    {
    case ChordQuality::Minor: return kMinor;
    case ChordQuality::Dominant7: return kDominant7;
    case ChordQuality::Diminished: return kDiminished;
    case ChordQuality::Major:
    default: return kMajor;
    }
}

// Resolves one chord-tone slot (0-3) plus an octave offset (relative to a
// caller-supplied base MIDI note, e.g. 36 for a bass layer's "octave 0")
// down to an absolute MIDI note number, clamped into the valid 0-127 range.
inline int ResolveChordToneMidiNote(const Chord& chord, int chordToneIndex, int octaveOffset, int baseMidiNote)
{
    const int* tones = ChordToneSemitones(chord.quality);
    int index = chordToneIndex < 0 ? 0 : (chordToneIndex > 3 ? 3 : chordToneIndex);
    int note = baseMidiNote + chord.rootPitchClass + tones[index] + (octaveOffset * 12);
    if (note < 0) note = 0;
    if (note > 127) note = 127;
    return note;
}
