#include "Style.h"

#include "DrumSynth.h"

namespace
{
    int V(DrumVoiceType type)
    {
        return static_cast<int>(type);
    }
}

std::vector<Style> BuiltInStyles()
{
    std::vector<Style> styles;

    // 1. Pop - straightforward four-on-the-floor-ish pop backing.
    {
        Style s;
        s.name = "Pop";
        s.suggestedTempoBpm = 100.0;
        s.beatsPerBar = 4.0;
        s.lengthBeats = 4.0;
        s.drumHits = {
            {0.0, V(DrumVoiceType::Kick), 0.9f},
            {2.0, V(DrumVoiceType::Kick), 0.85f},
            {1.0, V(DrumVoiceType::Snare), 0.85f},
            {3.0, V(DrumVoiceType::Snare), 0.85f},
            {0.0, V(DrumVoiceType::ClosedHat), 0.6f}, {0.5, V(DrumVoiceType::ClosedHat), 0.5f},
            {1.0, V(DrumVoiceType::ClosedHat), 0.6f}, {1.5, V(DrumVoiceType::ClosedHat), 0.5f},
            {2.0, V(DrumVoiceType::ClosedHat), 0.6f}, {2.5, V(DrumVoiceType::ClosedHat), 0.5f},
            {3.0, V(DrumVoiceType::ClosedHat), 0.6f}, {3.5, V(DrumVoiceType::ClosedHat), 0.5f},
        };
        s.bassNotes = {
            {0.0, 1.0, {0}, -1, 0.8f},
            {2.0, 1.0, {2}, -1, 0.75f},
        };
        s.kontraNotes = {
            {0.5, 0.25, {0, 1, 2}, 0, 0.5f},
            {2.5, 0.25, {0, 1, 2}, 0, 0.5f},
        };
        s.harmonijaNotes = {
            {0.0, 4.0, {0, 1, 2, 3}, 1, 0.3f},
        };
        styles.push_back(std::move(s));
    }

    // 2. Rock - driving eighth-note bass, backbeat snare.
    {
        Style s;
        s.name = "Rok";
        s.suggestedTempoBpm = 120.0;
        s.beatsPerBar = 4.0;
        s.lengthBeats = 4.0;
        s.drumHits = {
            {0.0, V(DrumVoiceType::Kick), 1.0f}, {0.5, V(DrumVoiceType::Kick), 0.7f}, {2.0, V(DrumVoiceType::Kick), 0.95f},
            {1.0, V(DrumVoiceType::Snare), 0.95f}, {3.0, V(DrumVoiceType::Snare), 0.95f},
            {0.0, V(DrumVoiceType::ClosedHat), 0.7f}, {0.5, V(DrumVoiceType::ClosedHat), 0.6f},
            {1.0, V(DrumVoiceType::ClosedHat), 0.7f}, {1.5, V(DrumVoiceType::ClosedHat), 0.6f},
            {2.0, V(DrumVoiceType::ClosedHat), 0.7f}, {2.5, V(DrumVoiceType::ClosedHat), 0.6f},
            {3.0, V(DrumVoiceType::ClosedHat), 0.7f}, {3.5, V(DrumVoiceType::ClosedHat), 0.6f},
        };
        s.bassNotes = {
            {0.0, 0.5, {0}, -1, 0.85f}, {0.5, 0.5, {0}, -1, 0.75f},
            {1.0, 0.5, {0}, -1, 0.85f}, {1.5, 0.5, {0}, -1, 0.75f},
            {2.0, 0.5, {0}, -1, 0.85f}, {2.5, 0.5, {0}, -1, 0.75f},
            {3.0, 0.5, {0}, -1, 0.85f}, {3.5, 0.5, {0}, -1, 0.75f},
        };
        s.kontraNotes = {
            {0.0, 0.4, {0, 2}, 0, 0.6f},
            {2.0, 0.4, {0, 2}, 0, 0.6f},
        };
        s.harmonijaNotes = {
            {0.0, 4.0, {0, 1, 2}, 1, 0.35f},
        };
        styles.push_back(std::move(s));
    }

    // 3. Ballada - slow, sparse, sustained.
    {
        Style s;
        s.name = "Balada";
        s.suggestedTempoBpm = 70.0;
        s.beatsPerBar = 4.0;
        s.lengthBeats = 4.0;
        s.drumHits = {
            {0.0, V(DrumVoiceType::Kick), 0.7f}, {2.5, V(DrumVoiceType::Kick), 0.55f},
            {2.0, V(DrumVoiceType::Snare), 0.5f},
            {0.0, V(DrumVoiceType::ClosedHat), 0.4f}, {1.0, V(DrumVoiceType::ClosedHat), 0.4f},
            {2.0, V(DrumVoiceType::ClosedHat), 0.4f}, {3.0, V(DrumVoiceType::ClosedHat), 0.4f},
        };
        s.bassNotes = {
            {0.0, 2.0, {0}, -1, 0.7f},
            {2.0, 2.0, {2}, -1, 0.65f},
        };
        s.kontraNotes = {
            {2.75, 0.25, {0, 1, 2}, 0, 0.4f},
        };
        s.harmonijaNotes = {
            {0.0, 4.0, {0, 1, 2, 3}, 1, 0.25f},
        };
        styles.push_back(std::move(s));
    }

    // 4. Valcer - 3/4 waltz, classic "oom-pah-pah".
    {
        Style s;
        s.name = "Valcer";
        s.suggestedTempoBpm = 130.0;
        s.beatsPerBar = 3.0;
        s.lengthBeats = 3.0;
        s.drumHits = {
            {0.0, V(DrumVoiceType::Kick), 0.85f},
            {0.0, V(DrumVoiceType::ClosedHat), 0.5f}, {1.0, V(DrumVoiceType::ClosedHat), 0.45f}, {2.0, V(DrumVoiceType::ClosedHat), 0.45f},
        };
        s.bassNotes = {
            {0.0, 1.0, {0}, -1, 0.8f},
        };
        s.kontraNotes = {
            {1.0, 0.6, {0, 1, 2}, 0, 0.55f},
            {2.0, 0.6, {0, 1, 2}, 0, 0.55f},
        };
        s.harmonijaNotes = {
            {0.0, 3.0, {0, 1, 2}, 1, 0.3f},
        };
        styles.push_back(std::move(s));
    }

    // 5. Latino - syncopated bossa/latin feel.
    {
        Style s;
        s.name = "Latino";
        s.suggestedTempoBpm = 110.0;
        s.beatsPerBar = 4.0;
        s.lengthBeats = 4.0;
        s.drumHits = {
            {0.0, V(DrumVoiceType::Kick), 0.85f}, {1.5, V(DrumVoiceType::Kick), 0.7f}, {3.0, V(DrumVoiceType::Kick), 0.8f},
            {2.0, V(DrumVoiceType::Clap), 0.75f},
            {0.0, V(DrumVoiceType::ClosedHat), 0.55f}, {0.5, V(DrumVoiceType::ClosedHat), 0.5f},
            {1.0, V(DrumVoiceType::ClosedHat), 0.55f}, {1.5, V(DrumVoiceType::ClosedHat), 0.5f},
            {2.0, V(DrumVoiceType::ClosedHat), 0.55f}, {2.5, V(DrumVoiceType::ClosedHat), 0.5f},
            {3.0, V(DrumVoiceType::ClosedHat), 0.55f}, {3.5, V(DrumVoiceType::ClosedHat), 0.5f},
        };
        s.bassNotes = {
            {0.0, 1.0, {0}, -1, 0.8f},
            {1.5, 1.0, {2}, -1, 0.7f},
            {2.5, 0.5, {0}, -1, 0.7f},
            {3.0, 1.0, {2}, -1, 0.65f},
        };
        s.kontraNotes = {
            {0.5, 0.4, {0, 1, 2}, 0, 0.5f},
            {1.5, 0.4, {0, 1, 2}, 0, 0.5f},
            {2.5, 0.4, {0, 1, 2}, 0, 0.5f},
            {3.5, 0.4, {0, 1, 2}, 0, 0.5f},
        };
        s.harmonijaNotes = {
            {0.0, 4.0, {0, 1, 2}, 1, 0.3f},
        };
        styles.push_back(std::move(s));
    }

    // 6. Sving - swung, laid-back jazz-trio feel (walking bass, brushy hats).
    {
        Style s;
        s.name = "Sving";
        s.suggestedTempoBpm = 120.0;
        s.beatsPerBar = 4.0;
        s.lengthBeats = 4.0;
        s.drumHits = {
            {0.0, V(DrumVoiceType::Kick), 0.55f}, {2.0, V(DrumVoiceType::Kick), 0.5f},
            {1.0, V(DrumVoiceType::Snare), 0.4f}, {3.0, V(DrumVoiceType::Snare), 0.4f},
            {0.0, V(DrumVoiceType::ClosedHat), 0.5f}, {1.0, V(DrumVoiceType::ClosedHat), 0.5f},
            {2.0, V(DrumVoiceType::ClosedHat), 0.5f}, {3.0, V(DrumVoiceType::ClosedHat), 0.5f},
        };
        s.bassNotes = {
            {0.0, 1.0, {0}, -1, 0.75f}, {1.0, 1.0, {2}, -1, 0.7f},
            {2.0, 1.0, {1}, -1, 0.7f}, {3.0, 1.0, {2}, -1, 0.7f},
        };
        s.kontraNotes = {
            {1.5, 0.3, {0, 1, 2, 3}, 0, 0.45f},
            {3.5, 0.3, {0, 1, 2, 3}, 0, 0.45f},
        };
        s.harmonijaNotes = {
            {0.0, 4.0, {0, 2, 3}, 1, 0.22f},
        };
        styles.push_back(std::move(s));
    }

    // 7. Regi - reggae one-drop: off-beat "skank" kontra, sparse kick/snare.
    {
        Style s;
        s.name = "Regi";
        s.suggestedTempoBpm = 85.0;
        s.beatsPerBar = 4.0;
        s.lengthBeats = 4.0;
        s.drumHits = {
            {2.0, V(DrumVoiceType::Kick), 0.85f},
            {2.0, V(DrumVoiceType::Snare), 0.6f},
            {0.5, V(DrumVoiceType::ClosedHat), 0.5f}, {1.5, V(DrumVoiceType::ClosedHat), 0.5f},
            {2.5, V(DrumVoiceType::ClosedHat), 0.5f}, {3.5, V(DrumVoiceType::ClosedHat), 0.5f},
        };
        s.bassNotes = {
            {0.0, 1.5, {0}, -1, 0.75f},
            {2.0, 2.0, {2}, -1, 0.7f},
        };
        s.kontraNotes = {
            {0.5, 0.3, {0, 1, 2}, 0, 0.55f}, {1.5, 0.3, {0, 1, 2}, 0, 0.55f},
            {2.5, 0.3, {0, 1, 2}, 0, 0.55f}, {3.5, 0.3, {0, 1, 2}, 0, 0.55f},
        };
        s.harmonijaNotes = {
            {0.0, 4.0, {0, 1, 2}, 1, 0.2f},
        };
        styles.push_back(std::move(s));
    }

    // 8. Fanki - funk: syncopated 16th-feel bass, tight ghost-note snare.
    {
        Style s;
        s.name = "Fanki";
        s.suggestedTempoBpm = 105.0;
        s.beatsPerBar = 4.0;
        s.lengthBeats = 4.0;
        s.drumHits = {
            {0.0, V(DrumVoiceType::Kick), 0.95f}, {0.75, V(DrumVoiceType::Kick), 0.6f},
            {2.25, V(DrumVoiceType::Kick), 0.7f}, {2.75, V(DrumVoiceType::Kick), 0.55f},
            {1.0, V(DrumVoiceType::Snare), 0.95f}, {1.75, V(DrumVoiceType::Snare), 0.35f},
            {3.0, V(DrumVoiceType::Snare), 0.95f}, {3.75, V(DrumVoiceType::Snare), 0.35f},
            {0.0, V(DrumVoiceType::ClosedHat), 0.6f}, {0.5, V(DrumVoiceType::ClosedHat), 0.5f},
            {1.0, V(DrumVoiceType::ClosedHat), 0.6f}, {1.5, V(DrumVoiceType::ClosedHat), 0.5f},
            {2.0, V(DrumVoiceType::ClosedHat), 0.6f}, {2.5, V(DrumVoiceType::ClosedHat), 0.5f},
            {3.0, V(DrumVoiceType::ClosedHat), 0.6f}, {3.5, V(DrumVoiceType::ClosedHat), 0.5f},
        };
        s.bassNotes = {
            {0.0, 0.5, {0}, -1, 0.85f}, {0.75, 0.25, {0}, -1, 0.6f},
            {1.5, 0.5, {0}, -1, 0.75f}, {2.25, 0.25, {2}, -1, 0.65f},
            {2.75, 0.5, {0}, -1, 0.8f}, {3.5, 0.5, {0}, -1, 0.65f},
        };
        s.kontraNotes = {
            {0.5, 0.2, {0, 1, 2}, 0, 0.5f}, {1.75, 0.2, {0, 1, 2}, 0, 0.45f},
            {2.5, 0.2, {0, 1, 2}, 0, 0.5f}, {3.75, 0.2, {0, 1, 2}, 0, 0.45f},
        };
        s.harmonijaNotes = {
            {0.0, 4.0, {0, 2}, 1, 0.18f},
        };
        styles.push_back(std::move(s));
    }

    // 9. Bluz - 12/8-ish shuffle feel approximated in 4/4, walking triad bass.
    {
        Style s;
        s.name = "Bluz";
        s.suggestedTempoBpm = 90.0;
        s.beatsPerBar = 4.0;
        s.lengthBeats = 4.0;
        s.drumHits = {
            {0.0, V(DrumVoiceType::Kick), 0.85f}, {2.0, V(DrumVoiceType::Kick), 0.8f},
            {1.0, V(DrumVoiceType::Snare), 0.8f}, {3.0, V(DrumVoiceType::Snare), 0.8f},
            {0.0, V(DrumVoiceType::ClosedHat), 0.5f}, {0.67, V(DrumVoiceType::ClosedHat), 0.4f},
            {1.0, V(DrumVoiceType::ClosedHat), 0.5f}, {1.67, V(DrumVoiceType::ClosedHat), 0.4f},
            {2.0, V(DrumVoiceType::ClosedHat), 0.5f}, {2.67, V(DrumVoiceType::ClosedHat), 0.4f},
            {3.0, V(DrumVoiceType::ClosedHat), 0.5f}, {3.67, V(DrumVoiceType::ClosedHat), 0.4f},
        };
        s.bassNotes = {
            {0.0, 1.0, {0}, -1, 0.8f}, {1.0, 1.0, {2}, -1, 0.75f},
            {2.0, 1.0, {0}, -1, 0.8f}, {3.0, 1.0, {2}, -1, 0.75f},
        };
        s.kontraNotes = {
            {0.67, 0.2, {0, 1, 2}, 0, 0.45f}, {2.67, 0.2, {0, 1, 2}, 0, 0.45f},
        };
        s.harmonijaNotes = {
            {0.0, 4.0, {0, 1, 3}, 1, 0.22f},
        };
        styles.push_back(std::move(s));
    }

    // 10. Orijentalni - Maqsum-style darbuka/dumbek skeleton (Dum-.-Tek-.-Tek-Dum-.-Tek
    // on the 8th-note grid, the classic pattern behind Balkan/Turkish "oriental"
    // pop and folk dance music), plus soft zils (closed hat) filling the 8ths.
    // Pairs naturally with Hijaz/Hijaz Kar auto-third harmonization (see
    // Scale.h) for melody, and with a Dominant7 manual chord for that
    // "exotic" harmonic color, though neither is required - the style alone
    // already carries the recognizable rhythmic feel.
    {
        Style s;
        s.name = "Orijentalni";
        s.suggestedTempoBpm = 108.0;
        s.beatsPerBar = 4.0;
        s.lengthBeats = 4.0;
        s.drumHits = {
            {0.0, V(DrumVoiceType::DumbekDum), 1.0f},
            {0.75, V(DrumVoiceType::DumbekTek), 0.35f},
            {1.0, V(DrumVoiceType::DumbekTek), 0.75f},
            {2.0, V(DrumVoiceType::DumbekTek), 0.75f},
            {2.5, V(DrumVoiceType::DumbekDum), 0.9f},
            {3.5, V(DrumVoiceType::DumbekTek), 0.8f},
            {0.0, V(DrumVoiceType::ClosedHat), 0.35f}, {0.5, V(DrumVoiceType::ClosedHat), 0.3f},
            {1.0, V(DrumVoiceType::ClosedHat), 0.35f}, {1.5, V(DrumVoiceType::ClosedHat), 0.3f},
            {2.0, V(DrumVoiceType::ClosedHat), 0.35f}, {2.5, V(DrumVoiceType::ClosedHat), 0.3f},
            {3.0, V(DrumVoiceType::ClosedHat), 0.35f}, {3.5, V(DrumVoiceType::ClosedHat), 0.3f},
        };
        s.bassNotes = {
            {0.0, 2.0, {0}, -1, 0.85f},
            {2.0, 0.5, {2}, -1, 0.7f},
            {2.5, 1.5, {0}, -1, 0.8f},
        };
        s.kontraNotes = {
            {1.0, 0.3, {0, 1, 2}, 0, 0.5f},
            {2.0, 0.3, {0, 1, 2}, 0, 0.5f},
            {3.5, 0.3, {0, 1, 2}, 0, 0.5f},
        };
        s.harmonijaNotes = {
            {0.0, 4.0, {0, 1, 2, 3}, 1, 0.25f},
        };
        styles.push_back(std::move(s));
    }

    return styles;
}
