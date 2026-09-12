#include "VstHost.h"

#include "public.sdk/source/vst/hosting/module.h"
#include "public.sdk/source/vst/hosting/plugprovider.h"
#include "public.sdk/source/vst/hosting/hostclasses.h"
#include "public.sdk/source/vst/hosting/eventlist.h"
#include "public.sdk/source/vst/hosting/parameterchanges.h"
#include "public.sdk/source/vst/hosting/processdata.h"

#include "pluginterfaces/vst/ivstaudioprocessor.h"
#include "pluginterfaces/vst/ivstcomponent.h"
#include "pluginterfaces/vst/ivstevents.h"
#include "pluginterfaces/base/funknown.h"

#include <mutex>
#include <vector>
#include <algorithm>

using namespace Steinberg;
using namespace Steinberg::Vst;

namespace
{
    // One process-wide host application object. VST3's PlugProvider (used
    // below) looks this up through PluginContextFactory rather than taking
    // it as a constructor argument, so it has to be set up once, the first
    // time any plug-in is loaded.
    HostApplication& GetHostApplication()
    {
        static HostApplication hostApp;
        return hostApp;
    }

    void EnsureHostContextRegistered()
    {
        static bool registered = false;
        if (!registered)
        {
            PluginContextFactory::instance().setPluginContext(&GetHostApplication());
            registered = true;
        }
    }

    struct PendingNote
    {
        int pitch;
        float velocity;
        bool isOn;
    };
}

struct VstHost::Impl
{
    // Everything here is only ever touched while 'mutex' is held, except
    // for the fields NoteOn/NoteOff themselves write into 'pending' (also
    // under 'mutex' - the lock is short, just pushing into a pre-reserved
    // vector).
    std::mutex mutex;

    VST3::Hosting::Module::Ptr module;
    IPtr<PlugProvider> provider;
    IPtr<IComponent> component;
    IPtr<IEditController> controller;
    IPtr<IAudioProcessor> audioProcessor;

    HostProcessData processData;
    EventList inputEvents{64};
    EventList outputEvents{64};
    ParameterChanges inputParamChanges{0};
    ParameterChanges outputParamChanges{0};

    int eventInBusIndex = -1;
    int outputChannelCount = 0;
    int maxBlockSize = 0;
    bool loaded = false;
    std::string pluginName;

    std::vector<PendingNote> pending;
    std::vector<PendingNote> pendingScratch; // reused every block, never (re)allocates

    Impl()
    {
        pending.reserve(64);
        pendingScratch.reserve(64);
    }

    void ResetAll()
    {
        if (audioProcessor)
        {
            audioProcessor->setProcessing(false);
        }
        if (component)
        {
            component->setActive(false);
        }
        processData.unprepare();
        audioProcessor.reset();
        controller.reset();
        component.reset();
        provider.reset(); // destroys -> disconnects + terminates the plug-in
        module.reset();   // unloads the .vst3 module

        eventInBusIndex = -1;
        outputChannelCount = 0;
        maxBlockSize = 0;
        loaded = false;
        pluginName.clear();
    }
};

VstHost::VstHost() : impl_(new Impl())
{
}

VstHost::~VstHost()
{
    UnloadInstrument();
    delete impl_;
}

bool VstHost::LoadInstrument(const std::string& modulePath, double sampleRate, int maxBlockSize, std::string& outError)
{
    std::lock_guard<std::mutex> lock(impl_->mutex);

    impl_->ResetAll();
    EnsureHostContextRegistered();

    std::string moduleError;
    auto module = VST3::Hosting::Module::create(modulePath, moduleError);
    if (!module)
    {
        outError = "Ne mogu da ucitam VST3 modul: " + moduleError;
        return false;
    }

    auto factory = module->getFactory();
    factory.setHostContext(&GetHostApplication());

    // Pick the first "Audio Module Class" (VST3's category name for both
    // instruments and effects). If more than one exists in the same
    // module, prefer one tagged as an instrument.
    VST3::Hosting::ClassInfo chosen;
    bool found = false;
    bool foundIsInstrument = false;
    for (const auto& info : factory.classInfos())
    {
        if (info.category() != kVstAudioEffectClass)
        {
            continue;
        }

        const bool isInstrument = info.subCategoriesString().find("Instrument") != std::string::npos;
        if (!found || (isInstrument && !foundIsInstrument))
        {
            chosen = info;
            found = true;
            foundIsInstrument = isInstrument;
        }
    }

    if (!found)
    {
        outError = "Ovaj VST3 fajl ne sadrzi nijedan audio modul (klasu 'Audio Module Class').";
        return false;
    }

    auto provider = owned(new PlugProvider(factory, chosen, true));
    if (!provider->initialize())
    {
        outError = "Plugin '" + chosen.name() + "' nije uspeo da se inicijalizuje.";
        return false;
    }

    IPtr<IComponent> component = provider->getComponentPtr();
    if (!component)
    {
        outError = "Plugin '" + chosen.name() + "' nije napravio audio komponentu.";
        return false;
    }

    IAudioProcessor* rawProcessor = nullptr;
    if (component->queryInterface(IAudioProcessor::iid, (void**)&rawProcessor) != kResultOk || !rawProcessor)
    {
        outError = "Plugin '" + chosen.name() + "' ne podrzava audio obradu (IAudioProcessor).";
        return false;
    }
    IPtr<IAudioProcessor> audioProcessor = owned(rawProcessor);

    const int32 numAudioOut = component->getBusCount(kAudio, kOutput);
    if (numAudioOut <= 0)
    {
        outError = "Plugin '" + chosen.name() + "' nema nijedan audio izlaz.";
        return false;
    }

    BusInfo outBusInfo{};
    component->getBusInfo(kAudio, kOutput, 0, outBusInfo);
    const int outputChannelCount = outBusInfo.channelCount > 0 ? outBusInfo.channelCount : 1;

    int eventInBusIndex = -1;
    if (component->getBusCount(kEvent, kInput) > 0)
    {
        eventInBusIndex = 0;
    }

    component->activateBus(kAudio, kOutput, 0, true);
    if (eventInBusIndex >= 0)
    {
        component->activateBus(kEvent, kInput, eventInBusIndex, true);
    }

    ProcessSetup setup{};
    setup.processMode = kRealtime;
    setup.symbolicSampleSize = kSample32;
    setup.maxSamplesPerBlock = maxBlockSize;
    setup.sampleRate = sampleRate;
    if (audioProcessor->setupProcessing(setup) != kResultOk)
    {
        outError = "Plugin '" + chosen.name() + "' je odbio zadate parametre obrade (sample rate / blok).";
        return false;
    }

    if (component->setActive(true) != kResultOk)
    {
        outError = "Plugin '" + chosen.name() + "' nije mogao da se aktivira.";
        return false;
    }

    audioProcessor->setProcessing(true);

    if (!impl_->processData.prepare(*component, maxBlockSize, kSample32))
    {
        outError = "Ne mogu da pripremim audio bafere za '" + chosen.name() + "'.";
        return false;
    }

    // Everything succeeded - commit to impl_.
    impl_->module = module;
    impl_->provider = provider;
    impl_->component = component;
    impl_->controller = provider->getControllerPtr();
    impl_->audioProcessor = audioProcessor;
    impl_->eventInBusIndex = eventInBusIndex;
    impl_->outputChannelCount = outputChannelCount;
    impl_->maxBlockSize = maxBlockSize;
    impl_->pluginName = chosen.name();
    impl_->loaded = true;

    return true;
}

void VstHost::UnloadInstrument()
{
    std::lock_guard<std::mutex> lock(impl_->mutex);
    impl_->ResetAll();
}

bool VstHost::IsLoaded() const
{
    std::lock_guard<std::mutex> lock(impl_->mutex);
    return impl_->loaded;
}

std::string VstHost::GetLoadedPluginName() const
{
    std::lock_guard<std::mutex> lock(impl_->mutex);
    return impl_->pluginName;
}

void VstHost::NoteOn(int midiNote, float velocity)
{
    std::lock_guard<std::mutex> lock(impl_->mutex);
    if (impl_->pending.size() < impl_->pending.capacity())
    {
        impl_->pending.push_back({midiNote, velocity, true});
    }
}

void VstHost::NoteOff(int midiNote)
{
    std::lock_guard<std::mutex> lock(impl_->mutex);
    if (impl_->pending.size() < impl_->pending.capacity())
    {
        impl_->pending.push_back({midiNote, 0.0f, false});
    }
}

void VstHost::RenderAdditive(float* interleavedStereoOut, int frames)
{
    std::lock_guard<std::mutex> lock(impl_->mutex);
    if (!impl_->loaded || frames <= 0 || frames > impl_->maxBlockSize)
    {
        return;
    }

    impl_->pendingScratch.clear();
    impl_->pendingScratch.swap(impl_->pending);

    impl_->inputEvents.clear();
    for (const auto& note : impl_->pendingScratch)
    {
        if (impl_->eventInBusIndex < 0)
        {
            continue; // this plug-in has no event/MIDI input - drop silently
        }

        Event event{};
        event.busIndex = impl_->eventInBusIndex;
        event.sampleOffset = 0;
        event.ppqPosition = 0;
        event.flags = Event::kIsLive;
        if (note.isOn)
        {
            event.type = Event::kNoteOnEvent;
            event.noteOn.channel = 0;
            event.noteOn.pitch = static_cast<int16>(note.pitch);
            event.noteOn.tuning = 0.0f;
            event.noteOn.velocity = note.velocity;
            event.noteOn.length = 0;
            event.noteOn.noteId = -1;
        }
        else
        {
            event.type = Event::kNoteOffEvent;
            event.noteOff.channel = 0;
            event.noteOff.pitch = static_cast<int16>(note.pitch);
            event.noteOff.velocity = 0.0f;
            event.noteOff.tuning = 0.0f;
            event.noteOff.noteId = -1;
        }
        impl_->inputEvents.addEvent(event);
    }

    impl_->outputEvents.clear();

    impl_->processData.numSamples = frames;
    impl_->processData.symbolicSampleSize = kSample32;
    impl_->processData.processMode = kRealtime;
    impl_->processData.inputEvents = &impl_->inputEvents;
    impl_->processData.outputEvents = &impl_->outputEvents;
    impl_->processData.inputParameterChanges = &impl_->inputParamChanges;
    impl_->processData.outputParameterChanges = &impl_->outputParamChanges;

    const tresult result = impl_->audioProcessor->process(impl_->processData);
    if (result != kResultOk || impl_->processData.numOutputs <= 0)
    {
        return;
    }

    const AudioBusBuffers& out = impl_->processData.outputs[0];
    if (out.numChannels <= 0 || out.channelBuffers32 == nullptr)
    {
        return;
    }

    const float* left = out.channelBuffers32[0];
    const float* right = out.numChannels > 1 ? out.channelBuffers32[1] : left;
    if (left == nullptr)
    {
        return;
    }
    if (right == nullptr)
    {
        right = left;
    }

    for (int i = 0; i < frames; ++i)
    {
        interleavedStereoOut[i * 2] += left[i];
        interleavedStereoOut[i * 2 + 1] += right[i];
    }
}
