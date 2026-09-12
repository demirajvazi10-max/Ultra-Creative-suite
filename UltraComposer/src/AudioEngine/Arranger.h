#pragma once

#include <functional>
#include <mutex>
#include <string>
#include <vector>

#include "Sequencer.h" // reuses SequencedNote

// One named, reusable clip of notes - the same shape as a Sequencer pattern
// (a note list plus a loop length in beats), just given a name and kept
// around so it can be reused more than once in an arrangement.
struct ArrangerPattern
{
    std::string name;
    std::vector<SequencedNote> notes;
    double lengthBeats = 4.0;
};

// Chains multiple named patterns into one ordered playback sequence (verse,
// verse, chorus, verse, ... - a simple, JAWS-friendly first slice of
// "arranger" functionality), looping the whole arrangement from the top
// once it reaches the end. Patterns are typically built by composing in the
// plain Sequencer first, then saving that as a named pattern here.
//
// Thread safety, matching the convention already used by Sequencer: editing
// (AddPattern/SetOrder/Play/Stop/...) happens on the UI thread, Advance()
// runs on the real-time audio thread, and both sides share one mutex. Like
// Sequencer::Advance(), this collects events under the lock but only
// invokes the onNoteOn/onNoteOff callbacks after releasing it, so a
// callback that calls back into a locked method here can't deadlock.
class Arranger
{
public:
    // Adds a new pattern, returns its index (0-based, matches insertion
    // order). 'name' may be empty (still gets an index; the UI is expected
    // to require a real name before calling this).
    int AddPattern(const std::string& name, double lengthBeats, std::vector<SequencedNote> notes);

    // Removes a pattern and fixes up the order list: references to the
    // removed pattern are dropped, and references to patterns after it are
    // shifted down by one to keep matching the new indices.
    void RemovePattern(int patternIndex);

    int GetPatternCount() const;
    std::string GetPatternName(int patternIndex) const;
    double GetPatternLengthBeats(int patternIndex) const;

    // Reads a pattern's note data back out - needed for project save (see
    // .adem format in MainWindow/ProjectFile), since a pattern's notes only
    // ever get *into* the Arranger via AddPattern and are otherwise never
    // exposed again once the Sequencer view that built them has moved on.
    int GetPatternNoteCount(int patternIndex) const;
    std::vector<SequencedNote> GetPatternNotes(int patternIndex) const;

    // The arrangement's play order: a list of pattern indices (may repeat
    // the same pattern any number of times), played back to back.
    void AppendToOrder(int patternIndex); // no-op if patternIndex is out of range
    void RemoveLastFromOrder();
    void ClearOrder();
    int GetOrderCount() const;
    int GetOrderPatternIndexAt(int orderPosition) const;

    void SetTempoBpm(double bpm);
    double GetTempoBpm() const;

    // Starts playback from the top of the order. Loops forever (the whole
    // arrangement repeats) until Stop() is called. Does nothing if the
    // order is empty.
    void Play();
    void Stop();
    bool IsPlaying() const;

    // Which slot in the order list (see GetOrderPatternIndexAt) is
    // currently playing, or -1 if stopped or the order is empty.
    int GetCurrentOrderPosition() const;

    // Real-time thread.
    void Advance(int frames,
                 double sampleRate,
                 const std::function<void(int midiNote, float velocity)>& onNoteOn,
                 const std::function<void(int midiNote)>& onNoteOff);

private:
    // Cumulative beat boundaries of the current order, e.g. for an order of
    // 3 patterns with lengths [4, 8, 4]: {0, 4, 12, 16}. Element i..i+1 is
    // the i-th slot's beat range within the whole arrangement; the last
    // element is the arrangement's total length. Must be called with the
    // lock already held.
    std::vector<double> ComputeBoundariesLocked() const;

    mutable std::mutex mutex_;
    std::vector<ArrangerPattern> patterns_;
    std::vector<int> order_;
    std::vector<int> activeNotes_;
    std::vector<int> notesToStopOnNextAdvance_;
    double tempoBpm_ = 120.0;
    double positionBeats_ = 0.0;
    bool playing_ = false;
};
