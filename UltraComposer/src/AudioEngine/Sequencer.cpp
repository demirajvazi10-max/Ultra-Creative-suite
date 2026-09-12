#include "Sequencer.h"

#include <algorithm>
#include <utility>

void Sequencer::SetNotes(std::vector<SequencedNote> notes)
{
    std::lock_guard<std::mutex> lock(mutex_);
    notes_ = std::move(notes);
    std::sort(notes_.begin(), notes_.end(), [](const SequencedNote& a, const SequencedNote& b)
    {
        return a.startBeat < b.startBeat;
    });
}

void Sequencer::SetTempoBpm(double bpm)
{
    std::lock_guard<std::mutex> lock(mutex_);
    tempoBpm_ = bpm > 0.0 ? bpm : 120.0;
}

double Sequencer::GetTempoBpm() const
{
    std::lock_guard<std::mutex> lock(mutex_);
    return tempoBpm_;
}

void Sequencer::SetLoopLengthBeats(double beats)
{
    std::lock_guard<std::mutex> lock(mutex_);
    loopLengthBeats_ = beats > 0.0 ? beats : 16.0;
}

double Sequencer::GetLoopLengthBeats() const
{
    std::lock_guard<std::mutex> lock(mutex_);
    return loopLengthBeats_;
}

void Sequencer::Play()
{
    std::lock_guard<std::mutex> lock(mutex_);
    positionBeats_ = 0.0;
    activeNotes_.clear();
    playing_ = true;
}

void Sequencer::Stop()
{
    std::lock_guard<std::mutex> lock(mutex_);
    playing_ = false;
    positionBeats_ = 0.0;
    // The actual NoteOff calls have to go through the real caller
    // (Synth/VstHost via Advance()'s callbacks), not from here directly -
    // queue them up so the very next Advance() flushes them.
    notesToStopOnNextAdvance_.insert(notesToStopOnNextAdvance_.end(),
                                      activeNotes_.begin(), activeNotes_.end());
    activeNotes_.clear();
}

bool Sequencer::IsPlaying() const
{
    std::lock_guard<std::mutex> lock(mutex_);
    return playing_;
}

double Sequencer::GetPositionBeats() const
{
    std::lock_guard<std::mutex> lock(mutex_);
    return positionBeats_;
}

void Sequencer::Advance(int frames,
                         double sampleRate,
                         const std::function<void(int, float)>& onNoteOn,
                         const std::function<void(int)>& onNoteOff)
{
    // Everything that touches shared state happens under the lock, but the
    // onNoteOn/onNoteOff callbacks themselves are invoked *after* it is
    // released below. Callbacks end up calling into Synth/VstHost (which
    // have their own locks), and calling out to arbitrary code while
    // holding this lock would risk a deadlock the moment any caller's
    // callback ever calls back into a locked Sequencer method (e.g.
    // GetPositionBeats()) - easy to do by accident, hard to diagnose, and
    // fatal here since this runs on the real-time audio thread.
    std::vector<int> stopPitches;
    std::vector<std::pair<int, float>> onEvents;
    std::vector<int> offEvents;

    {
        std::lock_guard<std::mutex> lock(mutex_);

        stopPitches.swap(notesToStopOnNextAdvance_);

        if (playing_ && sampleRate > 0.0 && frames > 0)
        {
            double beatsPerSecond = tempoBpm_ / 60.0;
            double remainingBeats = (static_cast<double>(frames) / sampleRate) * beatsPerSecond;
            double windowStart = positionBeats_;

            // A block can cross the loop boundary (or, with a very short
            // loop length, even several loop boundaries), so this walks
            // forward in segments that each stop *at* the loop point
            // instead of past it. That matters for correctness: a plain
            // "advance then fmod()" would leave the position at a small
            // positive remainder after wrapping, and a note starting
            // exactly at beat 0 would then permanently fail its
            // "startBeat >= windowStart" check on every later loop pass.
            // The guard counter is just a safety valve against a
            // pathologically tiny loop length; in practice this runs once.
            for (int guard = 0; guard < 64 && remainingBeats > 0.0; ++guard)
            {
                bool wraps = loopLengthBeats_ > 0.0 && (windowStart + remainingBeats) >= loopLengthBeats_;
                double windowEnd = wraps ? loopLengthBeats_ : (windowStart + remainingBeats);

                // Half-open interval [windowStart, windowEnd) for both
                // onset and offset, so a note boundary that falls exactly
                // on a segment edge is counted in exactly one segment -
                // never twice, never skipped.
                for (const SequencedNote& note : notes_)
                {
                    double endBeat = note.startBeat + note.lengthBeats;

                    if (note.startBeat >= windowStart && note.startBeat < windowEnd)
                    {
                        onEvents.emplace_back(note.pitch, note.velocity);
                        activeNotes_.push_back(note.pitch);
                    }

                    if (endBeat >= windowStart && endBeat < windowEnd)
                    {
                        offEvents.push_back(note.pitch);
                        activeNotes_.erase(std::remove(activeNotes_.begin(), activeNotes_.end(), note.pitch),
                                            activeNotes_.end());
                    }
                }

                remainingBeats -= (windowEnd - windowStart);

                if (wraps)
                {
                    // Anything still sounding at the loop point needs to be
                    // cut off now: a note's end beat is always less than
                    // loopLengthBeats_, so it would otherwise never be
                    // reached again after wrapping.
                    for (int pitch : activeNotes_)
                    {
                        offEvents.push_back(pitch);
                    }
                    activeNotes_.clear();
                    windowStart = 0.0;
                }
                else
                {
                    windowStart = windowEnd;
                }
            }

            positionBeats_ = windowStart;
        }
    } // <-- mutex released here, before any callback runs

    for (int pitch : stopPitches)
    {
        onNoteOff(pitch);
    }
    for (const auto& [pitch, velocity] : onEvents)
    {
        onNoteOn(pitch, velocity);
    }
    for (int pitch : offEvents)
    {
        onNoteOff(pitch);
    }
}
