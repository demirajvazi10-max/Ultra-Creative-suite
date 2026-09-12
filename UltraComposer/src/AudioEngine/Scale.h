#pragma once

// Scale helper for auto-third harmonization (see AudioEngine's
// SetAutoThirdEnabled/SetAutoThirdScale). A plain +4/-4 semitone shift is a
// major third everywhere, which is wrong for several scale degrees - e.g.
// in C major, D's diatonic third above is F, a MINOR third (3 semitones),
// not major. This picks the correct diatonic interval for whatever scale
// degree 'midiNote' falls on, rooted at 'scaleRootPitchClass', falling back
// to a fixed major third only for a note that isn't in the scale at all (a
// deliberately chromatic/"blue" note) - there's no single correct diatonic
// answer for those, so the old unconditional behavior is a reasonable
// fallback.
//
// Besides major/natural minor, this also offers Hijaz and Hijaz Kar (double
// harmonic major) - two 7-note scales, still plain 12-tone-equal-temperament
// (no microtones/quarter-tones - that would need per-note pitch-bending, a
// bigger separate change), that carry the augmented-2nd interval widely
// recognized as the "oriental"/Balkan-Turkish sound in this kind of music.
// The degree-stepping math below only ever looks a scale's own note table -
// it has no built-in assumption of even spacing - so it works unchanged for
// these too, exactly as it already did for major/natural minor.
enum class ScaleType
{
    Major = 0,
    NaturalMinor = 1,
    Hijaz = 2,    // 1 b2 3 4 5 b6 b7 - "Freygish"/Phrygian dominant
    HijazKar = 3, // 1 b2 3 4 5 b6 7  - double harmonic major/"Byzantine"
};

inline int DiatonicThirdNote(int midiNote, int scaleRootPitchClass, ScaleType scaleType, bool upper)
{
    static constexpr int kMajorScale[7] = {0, 2, 4, 5, 7, 9, 11};
    static constexpr int kMinorScale[7] = {0, 2, 3, 5, 7, 8, 10};  // natural minor
    static constexpr int kHijazScale[7] = {0, 1, 4, 5, 7, 8, 10};  // Hijaz
    static constexpr int kHijazKarScale[7] = {0, 1, 4, 5, 7, 8, 11}; // Hijaz Kar

    const int* scale = kMajorScale;
    switch (scaleType)
    {
    case ScaleType::NaturalMinor: scale = kMinorScale; break;
    case ScaleType::Hijaz: scale = kHijazScale; break;
    case ScaleType::HijazKar: scale = kHijazKarScale; break;
    case ScaleType::Major: default: scale = kMajorScale; break;
    }

    int root = ((scaleRootPitchClass % 12) + 12) % 12;
    int pitchClass = ((midiNote % 12) + 12) % 12;
    int relative = ((pitchClass - root) % 12 + 12) % 12;

    int degree = -1;
    for (int i = 0; i < 7; ++i)
    {
        if (scale[i] == relative)
        {
            degree = i;
            break;
        }
    }

    if (degree < 0)
    {
        // Not a scale note - fall back to a fixed major third, the old
        // unconditional behavior.
        return midiNote + (upper ? 4 : -4);
    }

    int rawIndex = degree + (upper ? 2 : -2);
    int thirdDegree = ((rawIndex % 7) + 7) % 7;
    int octaveShift = 0;
    if (rawIndex >= 7) octaveShift = 12;
    else if (rawIndex < 0) octaveShift = -12;

    int intervalFromRoot = scale[thirdDegree] - scale[degree] + octaveShift;
    return midiNote + intervalFromRoot;
}
