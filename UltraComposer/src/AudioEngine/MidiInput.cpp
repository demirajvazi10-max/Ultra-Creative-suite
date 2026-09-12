#include "MidiInput.h"

#include <rtmidi/RtMidi.h>

struct MidiInput::Impl
{
    // RtMidiIn's constructor can itself throw (e.g. if the OS MIDI subsystem
    // can't be initialized at all) - caught here so a MIDI problem never
    // takes down the whole app. When this is null, MidiInput behaves as if
    // zero MIDI ports exist, which the UI already handles normally.
    RtMidiIn* rtMidiIn = nullptr;

    Impl()
    {
        try
        {
            rtMidiIn = new RtMidiIn();
        }
        catch (const RtMidiError&)
        {
            rtMidiIn = nullptr;
        }
    }

    ~Impl()
    {
        delete rtMidiIn;
    }
};

MidiInput::MidiInput() : impl_(new Impl())
{
}

MidiInput::~MidiInput()
{
    Close();
    delete impl_;
}

std::vector<std::string> MidiInput::ListPorts()
{
    std::vector<std::string> names;
    if (impl_->rtMidiIn == nullptr)
    {
        return names;
    }

    unsigned int count = impl_->rtMidiIn->getPortCount();
    names.reserve(count);
    for (unsigned int i = 0; i < count; ++i)
    {
        names.push_back(impl_->rtMidiIn->getPortName(i));
    }
    return names;
}

bool MidiInput::OpenPort(unsigned int portIndex)
{
    Close();

    if (impl_->rtMidiIn == nullptr)
    {
        return false;
    }

    try
    {
        impl_->rtMidiIn->openPort(portIndex);
        // We only care about note on/off for now - drop sysex, MIDI clock/timing
        // and active-sensing bytes before they ever reach HandleMessage().
        impl_->rtMidiIn->ignoreTypes(true, true, true);
        impl_->rtMidiIn->setCallback(&MidiInput::RtMidiCallback, this);
        isOpen_ = true;
        return true;
    }
    catch (const RtMidiError&)
    {
        isOpen_ = false;
        return false;
    }
}

void MidiInput::Close()
{
    if (isOpen_ && impl_->rtMidiIn != nullptr)
    {
        impl_->rtMidiIn->cancelCallback();
        impl_->rtMidiIn->closePort();
    }
    isOpen_ = false;
}

bool MidiInput::IsOpen() const
{
    return isOpen_;
}

void MidiInput::SetNoteCallback(NoteCallback callback)
{
    callback_ = std::move(callback);
}

void MidiInput::RtMidiCallback(double /*timeStamp*/, std::vector<unsigned char>* message, void* userData)
{
    auto* self = static_cast<MidiInput*>(userData);
    if (self != nullptr && message != nullptr)
    {
        self->HandleMessage(*message);
    }
}

void MidiInput::HandleMessage(const std::vector<unsigned char>& message)
{
    if (message.size() < 3 || !callback_)
    {
        return;
    }

    const unsigned char status = message[0];
    const unsigned char type = status & 0xF0;
    const int note = message[1];
    const int velocity = message[2];

    if (type == 0x90 && velocity > 0) // Note On
    {
        callback_(note, static_cast<float>(velocity) / 127.0f, true);
    }
    else if (type == 0x80 || (type == 0x90 && velocity == 0)) // Note Off (or Note On vel 0, same thing per spec)
    {
        callback_(note, 0.0f, false);
    }
}
