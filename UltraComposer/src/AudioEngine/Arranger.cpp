#include "Arranger.h"

#include <algorithm>
#include <utility>

namespace
{
    // Finds the slot i such that boundaries[i] <= beat < boundaries[i+1].
    // 'boundaries' always has order-size()+1 entries (see
    // Arranger::ComputeBoundariesLocked). Falls back to the last slot for a
    // beat that lands exactly on (or numerically just past) the final
    // boundary, since the caller always treats reaching the end specially
    // anyway (wrapping back to 0) right after this is used.
    int FindSlotIndex(const std::vector<double>& boundaries, double beat)
    {
        for (size_t i = 0; i + 1 < boundaries.size(); ++i)
        {
            if (beat >= boundaries[i] && beat < boundaries[i + 1])
            {
                return static_cast<int>(i);
            }
        }
        return boundaries.size() >= 2 ? static_cast<int>(boundaries.size()) - 2 : -1;
    }
}

int Arranger::AddPattern(const std::string& name, double lengthBeats, std::vector<SequencedNote> notes)
{
    std::lock_guard<std::mutex> lock(mutex_);
    ArrangerPattern pattern;
    pattern.name = name;
    pattern.lengthBeats = lengthBeats > 0.0 ? lengthBeats : 4.0;
    pattern.notes = std::move(notes);
    patterns_.push_back(std::move(pattern));
    return static_cast<int>(patterns_.size()) - 1;
}

void Arranger::RemovePattern(int patternIndex)
{
    std::lock_guard<std::mutex> lock(mutex_);
    if (patternIndex < 0 || patternIndex >= static_cast<int>(patterns_.size()))
    {
        return;
    }

    patterns_.erase(patterns_.begin() + patternIndex);

    std::vector<int> newOrder;
    newOrder.reserve(order_.size());
    for (int idx : order_)
    {
        if (idx == patternIndex)
        {
            continue; // drop references to the removed pattern
        }
        newOrder.push_back(idx > patternIndex ? idx - 1 : idx);
    }
    order_ = std::move(newOrder);
    positionBeats_ = 0.0; // the timeline just changed shape underneath any in-progress playback
}

int Arranger::GetPatternCount() const
{
    std::lock_guard<std::mutex> lock(mutex_);
    return static_cast<int>(patterns_.size());
}

std::string Arranger::GetPatternName(int patternIndex) const
{
    std::lock_guard<std::mutex> lock(mutex_);
    if (patternIndex < 0 || patternIndex >= static_cast<int>(patterns_.size()))
    {
        return std::string();
    }
    return patterns_[patternIndex].name;
}

double Arranger::GetPatternLengthBeats(int patternIndex) const
{
    std::lock_guard<std::mutex> lock(mutex_);
    if (patternIndex < 0 || patternIndex >= static_cast<int>(patterns_.size()))
    {
        return 0.0;
    }
    return patterns_[patternIndex].lengthBeats;
}

int Arranger::GetPatternNoteCount(int patternIndex) const
{
    std::lock_guard<std::mutex> lock(mutex_);
    if (patternIndex < 0 || patternIndex >= static_cast<int>(patterns_.size()))
    {
        return 0;
    }
    return static_cast<int>(patterns_[patternIndex].notes.size());
}

std::vector<SequencedNote> Arranger::GetPatternNotes(int patternIndex) const
{
    std::lock_guard<std::mutex> lock(mutex_);
    if (patternIndex < 0 || patternIndex >= static_cast<int>(patterns_.size()))
    {
        return {};
    }
    return patterns_[patternIndex].notes;
}

void Arranger::AppendToOrder(int patternIndex)
{
    std::lock_guard<std::mutex> lock(mutex_);
    if (patternIndex < 0 || patternIndex >= static_cast<int>(patterns_.size()))
    {
        return;
    }
    order_.push_back(patternIndex);
}

void Arranger::RemoveLastFromOrder()
{
    std::lock_guard<std::mutex> lock(mutex_);
    if (!order_.empty())
    {
        order_.pop_back();
    }
}

void Arranger::ClearOrder()
{
    std::lock_guard<std::mutex> lock(mutex_);
    order_.clear();
    positionBeats_ = 0.0;
}

int Arranger::GetOrderCount() const
{
    std::lock_guard<std::mutex> lock(mutex_);
    return static_cast<int>(order_.size());
}

int Arranger::GetOrderPatternIndexAt(int orderPosition) const
{
    std::lock_guard<std::mutex> lock(mutex_);
    if (orderPosition < 0 || orderPosition >= static_cast<int>(order_.size()))
    {
        return -1;
    }
    return order_[orderPosition];
}

void Arranger::SetTempoBpm(double bpm)
{
    std::lock_guard<std::mutex> lock(mutex_);
    tempoBpm_ = bpm > 0.0 ? bpm : 120.0;
}

double Arranger::GetTempoBpm() const
{
    std::lock_guard<std::mutex> lock(mutex_);
    return tempoBpm_;
}

void Arranger::Play()
{
    std::lock_guard<std::mutex> lock(mutex_);
    positionBeats_ = 0.0;
    activeNotes_.clear();
    playing_ = true;
}

void Arranger::Stop()
{
    std::lock_guard<std::mutex> lock(mutex_);
    playing_ = false;
    positionBeats_ = 0.0;
    notesToStopOnNextAdvance_.insert(notesToStopOnNextAdvance_.end(),
                                      activeNotes_.begin(), activeNotes_.end());
    activeNotes_.clear();
}

bool Arranger::IsPlaying() const
{
    std::lock_guard<std::mutex> lock(mutex_);
    return playing_;
}

int Arranger::GetCurrentOrderPosition() const
{
    std::lock_guard<std::mutex> lock(mutex_);
    if (!playing_ || order_.empty())
    {
        return -1;
    }
    std::vector<double> boundaries = ComputeBoundariesLocked();
    if (boundaries.back() <= 0.0)
    {
        return -1;
    }
    return FindSlotIndex(boundaries, positionBeats_);
}

std::vector<double> Arranger::ComputeBoundariesLocked() const
{
    std::vector<double> boundaries;
    boundaries.reserve(order_.size() + 1);
    boundaries.push_back(0.0);
    double cumulative = 0.0;
    for (int patternIndex : order_)
    {
        double length = (patternIndex >= 0 && patternIndex < static_cast<int>(patterns_.size()))
                             ? patterns_[patternIndex].lengthBeats
                             : 0.0;
        cumulative += (length > 0.0 ? length : 0.0);
        boundaries.push_back(cumulative);
    }
    return boundaries;
}

void Arranger::Advance(int frames,
                        double sampleRate,
                        const std::function<void(int, float)>& onNoteOn,
                        const std::function<void(int)>& onNoteOff)
{
    // Same lock-then-release-before-callback discipline as Sequencer::Advance()
    // - see the comment there for why.
    std::vector<int> stopPitches;
    std::vector<std::pair<int, float>> onEvents;
    std::vector<int> offEvents;

    {
        std::lock_guard<std::mutex> lock(mutex_);

        stopPitches.swap(notesToStopOnNextAdvance_);

        if (playing_ && sampleRate > 0.0 && frames > 0 && !order_.empty())
        {
            std::vector<double> boundaries = ComputeBoundariesLocked();
            double totalLength = boundaries.back();

            if (totalLength > 0.0)
            {
                double beatsPerSecond = tempoBpm_ / 60.0;
                double remainingBeats = (static_cast<double>(frames) / sampleRate) * beatsPerSecond;
                double windowStart = positionBeats_;

                // Generalizes Sequencer's loop-wrap segmenting to however
                // many pattern boundaries the current order has: each pass
                // advances only as far as the end of the pattern currently
                // playing (or the end of the whole arrangement, whichever
                // comes first), then either moves into the next pattern or
                // wraps the whole arrangement back to beat 0.
                for (int guard = 0; guard < 64 && remainingBeats > 0.0; ++guard)
                {
                    int slotIndex = FindSlotIndex(boundaries, windowStart);
                    if (slotIndex < 0)
                    {
                        break; // malformed state (shouldn't happen) - stay safe rather than loop forever
                    }

                    double slotStart = boundaries[slotIndex];
                    double slotEnd = boundaries[slotIndex + 1];
                    double windowEnd = std::min(windowStart + remainingBeats, slotEnd);

                    const ArrangerPattern& pattern = patterns_[order_[slotIndex]];
                    double localStart = windowStart - slotStart;
                    double localEnd = windowEnd - slotStart;

                    for (const SequencedNote& note : pattern.notes)
                    {
                        double endBeat = note.startBeat + note.lengthBeats;

                        if (note.startBeat >= localStart && note.startBeat < localEnd)
                        {
                            onEvents.emplace_back(note.pitch, note.velocity);
                            activeNotes_.push_back(note.pitch);
                        }

                        if (endBeat >= localStart && endBeat < localEnd)
                        {
                            offEvents.push_back(note.pitch);
                            activeNotes_.erase(std::remove(activeNotes_.begin(), activeNotes_.end(), note.pitch),
                                                activeNotes_.end());
                        }
                    }

                    remainingBeats -= (windowEnd - windowStart);

                    bool reachedArrangementEnd = windowEnd >= totalLength;
                    bool reachedSlotEnd = windowEnd >= slotEnd;

                    if (reachedArrangementEnd)
                    {
                        for (int pitch : activeNotes_)
                        {
                            offEvents.push_back(pitch);
                        }
                        activeNotes_.clear();
                        windowStart = 0.0;
                    }
                    else if (reachedSlotEnd)
                    {
                        // Moving into the next pattern in the order - cut
                        // notes off at the boundary rather than carrying
                        // them into a different pattern's context.
                        for (int pitch : activeNotes_)
                        {
                            offEvents.push_back(pitch);
                        }
                        activeNotes_.clear();
                        windowStart = windowEnd;
                    }
                    else
                    {
                        windowStart = windowEnd;
                    }
                }

                positionBeats_ = windowStart;
            }
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
