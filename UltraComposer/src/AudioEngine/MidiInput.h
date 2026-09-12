#pragma once

#include <functional>
#include <string>
#include <vector>

// Real-time MIDI input (a USB-MIDI keyboard, e.g. a Yamaha P-125). Wraps
// RtMidi; RtMidi.h is kept out of this header (pimpl) so the rest of the
// engine doesn't need to see it.
//
// IMPORTANT: the note callback is invoked directly on RtMidi's own
// background thread, not the audio thread and not the UI thread. Whatever
// it calls into must be safe to call from an arbitrary thread (Synth's
// NoteOn/NoteOff are - see Synth.h).
class MidiInput
{
public:
    MidiInput();
    ~MidiInput();

    MidiInput(const MidiInput&) = delete;
    MidiInput& operator=(const MidiInput&) = delete;

    // Human-readable names of all MIDI input ports currently visible to the
    // system (re-enumerate after plugging in a new device - there's no
    // hot-plug notification here yet).
    std::vector<std::string> ListPorts();

    // Opens the port at 'portIndex' (from ListPorts()), closing any
    // previously open port first. Returns false if the port could not be
    // opened.
    bool OpenPort(unsigned int portIndex);

    void Close();
    bool IsOpen() const;

    // isNoteOn == false means note-off (velocity is meaningless in that case).
    using NoteCallback = std::function<void(int midiNote, float velocity, bool isNoteOn)>;
    void SetNoteCallback(NoteCallback callback);

private:
    struct Impl;
    Impl* impl_;
    NoteCallback callback_;
    bool isOpen_ = false;

    static void RtMidiCallback(double timeStamp, std::vector<unsigned char>* message, void* userData);
    void HandleMessage(const std::vector<unsigned char>& message);
};
