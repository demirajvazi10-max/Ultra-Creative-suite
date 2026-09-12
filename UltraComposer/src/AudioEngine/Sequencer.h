#pragma once

#include <functional>
#include <mutex>
#include <vector>

// One note in a simple, single-track sequence: a MIDI note number, plus a
// start time and length measured in beats (not seconds), so the whole
// pattern automatically follows whatever tempo is set.
struct SequencedNote
{
    double startBeat = 0.0;
    double lengthBeats = 1.0;
    int pitch = 60;
    float velocity = 0.8f;
};

// A minimal, single-track playback sequencer: holds a list of timed notes
// and, once started, fires NoteOn/NoteOff callbacks as the transport
// crosses each note's start/end time. Loops back to beat 0 once it reaches
// the configured loop length.
//
// Editing (SetNotes/SetTempoBpm/Play/Stop) happens on the UI thread;
// Advance() runs on the real-time audio thread. Both sides share one
// mutex - the same accepted relaxation of strict real-time purism already
// used by Synth and MidiInput elsewhere in this engine (see Synth.h and
// VstHost.cpp).
class Sequencer
{
public:
    // Replaces the whole pattern. Safe to call while playing.
    void SetNotes(std::vector<SequencedNote> notes);

    void SetTempoBpm(double bpm);
    double GetTempoBpm() const;

    void SetLoopLengthBeats(double beats);
    double GetLoopLengthBeats() const;

    // Starts playback from beat 0. Loops forever until Stop() is called.
    void Play();
    // Stops playback; anything still sounding is released on the next
    // Advance() call.
    void Stop();
    bool IsPlaying() const;
    double GetPositionBeats() const;

    // Real-time thread. Advances the transport by 'frames' samples at
    // 'sampleRate' and fires onNoteOn/onNoteOff for any note boundaries
    // crossed during this block.
    void Advance(int frames,
                 double sampleRate,
                 const std::function<void(int midiNote, float velocity)>& onNoteOn,
                 const std::function<void(int midiNote)>& onNoteOff);

private:
    mutable std::mutex mutex_;
    std::vector<SequencedNote> notes_;
    std::vector<int> activeNotes_;
    std::vector<int> notesToStopOnNextAdvance_;
    double tempoBpm_ = 120.0;
    double loopLengthBeats_ = 16.0;
    double positionBeats_ = 0.0;
    bool playing_ = false;
};
