/*
 * an_plug: VST3 hosting over the Steinberg VST3 SDK (MIT). SPDX-License-Identifier: GPL-3.0-or-later
 */
#include "an_plug_internal.h"

#include "pluginterfaces/base/funknown.h"
#include "pluginterfaces/base/ipluginbase.h"
#include "pluginterfaces/gui/iplugview.h"
#include "pluginterfaces/vst/ivstaudioprocessor.h"
#include "pluginterfaces/vst/ivstcomponent.h"
#include "pluginterfaces/vst/ivsteditcontroller.h"
#include "pluginterfaces/vst/ivstevents.h"
#include "pluginterfaces/vst/ivstmessage.h"
#include "pluginterfaces/vst/ivstprocesscontext.h"
#include "public.sdk/source/common/memorystream.h"
#include "public.sdk/source/vst/hosting/connectionproxy.h"
#include "public.sdk/source/vst/hosting/eventlist.h"
#include "public.sdk/source/vst/hosting/hostclasses.h"
#include "public.sdk/source/vst/hosting/module.h"
#include "public.sdk/source/vst/hosting/parameterchanges.h"
#include "public.sdk/source/vst/utility/stringconvert.h"

#include <algorithm>
#include <cstring>
#include <map>
#include <mutex>

using namespace Steinberg;
using namespace Steinberg::Vst;

namespace anplug {

namespace {

std::string str16(const TChar *s) {
    return Steinberg::Vst::StringConvert::convert(s);
}

/* Loaded modules stay loaded for the life of the process: unloading a plug-in bundle while anything of it may
 * still run (a timer, a thread it started) is a classic crash. */
std::mutex modulesGate;
std::map<std::string, VST3::Hosting::Module::Ptr> modules;

VST3::Hosting::Module::Ptr loadModule(const std::string &path, std::string &error) {
    std::lock_guard<std::mutex> lock(modulesGate);
    auto it = modules.find(path);
    if (it != modules.end()) {
        return it->second;
    }
    auto module = VST3::Hosting::Module::create(path, error);
    if (module) {
        modules[path] = module;
    }
    return module;
}

HostApplication &hostContext() {
    static HostApplication *app = new HostApplication();
    return *app;
}

struct Vst3Plugin;

class ComponentHandler : public IComponentHandler {
public:
    explicit ComponentHandler(Vst3Plugin *owner) : owner(owner) {}
    tresult PLUGIN_API queryInterface(const TUID iid, void **obj) override {
        QUERY_INTERFACE(iid, obj, FUnknown::iid, IComponentHandler)
        QUERY_INTERFACE(iid, obj, IComponentHandler::iid, IComponentHandler)
        *obj = nullptr;
        return kNoInterface;
    }
    uint32 PLUGIN_API addRef() override { return 1000; }
    uint32 PLUGIN_API release() override { return 1000; }
    tresult PLUGIN_API beginEdit(ParamID) override { return kResultOk; }
    tresult PLUGIN_API performEdit(ParamID id, ParamValue value) override;
    tresult PLUGIN_API endEdit(ParamID) override { return kResultOk; }
    tresult PLUGIN_API restartComponent(int32 flags) override;

private:
    Vst3Plugin *owner;
};

class PlugFrame : public IPlugFrame {
public:
    explicit PlugFrame(Vst3Plugin *owner) : owner(owner) {}
    tresult PLUGIN_API queryInterface(const TUID iid, void **obj) override {
        QUERY_INTERFACE(iid, obj, FUnknown::iid, IPlugFrame)
        QUERY_INTERFACE(iid, obj, IPlugFrame::iid, IPlugFrame)
        *obj = nullptr;
        return kNoInterface;
    }
    uint32 PLUGIN_API addRef() override { return 1000; }
    uint32 PLUGIN_API release() override { return 1000; }
    tresult PLUGIN_API resizeView(IPlugView *view, ViewRect *newSize) override;

private:
    Vst3Plugin *owner;
};

struct Vst3Plugin final : Plugin {
    VST3::Hosting::Module::Ptr module;
    IPtr<IComponent> component;
    IPtr<IAudioProcessor> processor;
    IPtr<IEditController> controller;
    bool singleComponent = false;
    IPtr<ConnectionProxy> componentConnection;
    IPtr<ConnectionProxy> controllerConnection;
    ComponentHandler handler{this};
    PlugFrame frame{this};
    std::vector<ParamID> ids;
    std::map<ParamID, int32_t> indexOf;
    int32_t audioInputs = 0, audioOutputs = 0, eventInputs = 0;
    int32_t sidechainBus = -1;
    int32_t mainInChannels = 2, mainOutChannels = 2, scChannels = 2;
    bool sidechainActive = false;
    ParameterChanges inChanges;
    ParameterChanges outChanges;
    EventList events{512};
    ProcessContext context{};
    EditRing edits;
    std::vector<float> silence;
    std::vector<float> scratch;
    std::atomic<bool> latencyChanged{false};
    IPtr<IPlugView> view;

    ~Vst3Plugin() override {
        if (componentConnection && controllerConnection) {
            componentConnection->disconnect();
            controllerConnection->disconnect();
        }
        if (controller) {
            controller->setComponentHandler(nullptr);
            if (!singleComponent) {
                controller->terminate();
            }
        }
        if (component) {
            component->terminate();
        }
        controller = nullptr;
        processor = nullptr;
        component = nullptr;
    }

    int32_t index(ParamID id) const {
        auto it = indexOf.find(id);
        return it == indexOf.end() ? -1 : it->second;
    }

    void readParams() {
        params.clear();
        ids.clear();
        indexOf.clear();
        if (!controller) {
            initValues();
            return;
        }
        auto count = controller->getParameterCount();
        for (int32 i = 0; i < count; i++) {
            ParameterInfo info{};
            if (controller->getParameterInfo(i, info) != kResultOk) {
                continue;
            }
            if (info.flags & (ParameterInfo::kIsHidden | ParameterInfo::kIsReadOnly | ParameterInfo::kIsBypass |
                              ParameterInfo::kIsProgramChange)) {
                continue;
            }
            ParamInfo p;
            p.id = std::to_string(info.id);
            p.name = str16(info.title);
            p.unit = str16(info.units);
            p.min = 0;
            p.max = 1;
            p.def = info.defaultNormalizedValue;
            p.steps = info.stepCount;
            if ((info.flags & ParameterInfo::kIsList) && info.stepCount > 0 && info.stepCount <= 128) {
                for (int32 s = 0; s <= info.stepCount; s++) {
                    String128 text{};
                    controller->getParamStringByValue(info.id, static_cast<double>(s) / info.stepCount, text);
                    p.valueNames.push_back(str16(text));
                }
            }
            indexOf[info.id] = static_cast<int32_t>(params.size());
            ids.push_back(info.id);
            params.push_back(std::move(p));
        }
        initValues();
        for (size_t i = 0; i < ids.size(); i++) {
            values[i].store(controller->getParamNormalized(ids[i]));
        }
    }

    void readBuses() {
        audioInputs = component->getBusCount(kAudio, kInput);
        audioOutputs = component->getBusCount(kAudio, kOutput);
        eventInputs = component->getBusCount(kEvent, kInput);
        sidechainBus = -1;
        for (int32 b = 1; b < audioInputs; b++) {
            BusInfo info{};
            if (component->getBusInfo(kAudio, kInput, b, info) == kResultOk && info.busType == kAux) {
                sidechainBus = b;
                break;
            }
        }
        sidechainCapable = sidechainBus >= 0 && !instrument;
    }

    int32_t activate(double sampleRate, int32_t maxFrames, bool sidechain) override {
        sidechainActive = sidechain && sidechainBus >= 0;
        // Stereo everywhere; other buses keep their own arrangement and stay inactive.
        std::vector<SpeakerArrangement> in(static_cast<size_t>(audioInputs)), out(static_cast<size_t>(audioOutputs));
        for (int32 b = 0; b < audioInputs; b++) {
            processor->getBusArrangement(kInput, b, in[b]);
        }
        for (int32 b = 0; b < audioOutputs; b++) {
            processor->getBusArrangement(kOutput, b, out[b]);
        }
        if (audioInputs > 0) {
            in[0] = SpeakerArr::kStereo;
        }
        if (sidechainBus >= 0) {
            in[static_cast<size_t>(sidechainBus)] = SpeakerArr::kStereo;
        }
        if (audioOutputs > 0) {
            out[0] = SpeakerArr::kStereo;
        }
        if (processor->setBusArrangements(in.data(), audioInputs, out.data(), audioOutputs) != kResultOk) {
            // The plugin keeps its own layout: work with what it has (a mono plugin gets the left channel).
            for (int32 b = 0; b < audioInputs; b++) {
                processor->getBusArrangement(kInput, b, in[b]);
            }
            for (int32 b = 0; b < audioOutputs; b++) {
                processor->getBusArrangement(kOutput, b, out[b]);
            }
        }
        mainInChannels = audioInputs > 0 ? SpeakerArr::getChannelCount(in[0]) : 0;
        mainOutChannels = audioOutputs > 0 ? SpeakerArr::getChannelCount(out[0]) : 0;
        scChannels = sidechainBus >= 0 ? SpeakerArr::getChannelCount(in[static_cast<size_t>(sidechainBus)]) : 0;
        if (mainOutChannels < 1 || mainOutChannels > 2 || mainInChannels > 2 || scChannels > 2) {
            return fail("unsupported bus layout (only mono and stereo buses are hosted)");
        }
        for (int32 b = 0; b < audioInputs; b++) {
            component->activateBus(kAudio, kInput, b, b == 0 || (b == sidechainBus && sidechainActive));
        }
        for (int32 b = 0; b < audioOutputs; b++) {
            component->activateBus(kAudio, kOutput, b, b == 0);
        }
        for (int32 b = 0; b < eventInputs; b++) {
            component->activateBus(kEvent, kInput, b, b == 0);
        }
        ProcessSetup setup{kRealtime, kSample32, maxFrames, sampleRate};
        if (processor->setupProcessing(setup) != kResultOk) {
            return fail("setupProcessing failed");
        }
        if (component->setActive(true) != kResultOk) {
            return fail("setActive failed");
        }
        processor->setProcessing(true);
        silence.assign(static_cast<size_t>(maxFrames) * 2, 0.0f);
        scratch.assign(static_cast<size_t>(maxFrames) * 4, 0.0f);
        inChanges.setMaxParameters(static_cast<int32>(std::max<size_t>(ids.size(), 1)));
        outChanges.setMaxParameters(static_cast<int32>(std::max<size_t>(ids.size(), 1)) + 64);
        context = {};
        context.sampleRate = sampleRate;
        context.tempo = 120;
        context.timeSigNumerator = 4;
        context.timeSigDenominator = 4;
        context.state = ProcessContext::kPlaying | ProcessContext::kTempoValid | ProcessContext::kTimeSigValid;
        return 0;
    }

    void deactivate() override {
        processor->setProcessing(false);
        component->setActive(false);
    }

    void setParam(int32_t index, double value) override {
        // The audio thread (or before activation): queued for the next process call.
        int32 queueIndex = 0;
        if (auto *queue = inChanges.addParameterData(ids[static_cast<size_t>(index)], queueIndex)) {
            int32 pointIndex = 0;
            queue->addPoint(0, value, pointIndex);
        }
        controllerDirty[index].store(true, std::memory_order_relaxed);
        anyControllerDirty.store(true, std::memory_order_release);
    }

    int32_t process(const float *const *in, const float *const *sc, float *const *out, int32_t frames,
                    const an_plug_midi *midi, int32_t midiCount) override {
        // Edits from the plugin's editor (main thread) since the last call.
        int32_t editIndex;
        double editValue;
        while (edits.pop(editIndex, editValue)) {
            int32 q = 0;
            if (auto *queue = inChanges.addParameterData(ids[static_cast<size_t>(editIndex)], q)) {
                int32 p = 0;
                queue->addPoint(0, editValue, p);
            }
        }

        events.clear();
        for (int32_t i = 0; i < midiCount; i++) {
            const auto &m = midi[i];
            Event e{};
            e.busIndex = 0;
            e.sampleOffset = std::clamp(m.frame, 0, frames - 1);
            auto kind = m.status & 0xF0;
            auto channel = static_cast<int16>(m.status & 0x0F);
            if (kind == 0x90 && m.data2 > 0) {
                e.type = Event::kNoteOnEvent;
                e.noteOn = {channel, static_cast<int16>(m.data1), 0.0f, m.data2 / 127.0f, 0, -1};
            } else if (kind == 0x80 || (kind == 0x90 && m.data2 == 0)) {
                e.type = Event::kNoteOffEvent;
                e.noteOff = {channel, static_cast<int16>(m.data1), m.data2 / 127.0f, -1, 0.0f};
            } else {
                continue;
            }
            events.addEvent(e);
        }

        float *inPtrs[2], *scPtrs[2];
        float *outPtrs[2] = {out[0], out[1]};
        AudioBusBuffers inputs[2]{};
        int32 numInputs = 0;
        if (audioInputs > 0) {
            const float *l = in && !instrument ? in[0] : silence.data();
            const float *r = in && !instrument ? in[1] : silence.data();
            if (mainInChannels == 1) {
                // A mono input: the average of the two channels.
                auto *m = scratch.data();
                for (int32_t i = 0; i < frames; i++) {
                    m[i] = 0.5f * (l[i] + r[i]);
                }
                inPtrs[0] = m;
            } else {
                inPtrs[0] = const_cast<float *>(l);
                inPtrs[1] = const_cast<float *>(r);
            }
            inputs[0].numChannels = mainInChannels;
            inputs[0].channelBuffers32 = inPtrs;
            numInputs = 1;
            for (int32 b = 1; b < audioInputs && b <= sidechainBus; b++) {
                numInputs = b + 1;
            }
            if (sidechainBus >= 0 && numInputs > sidechainBus) {
                const float *l2 = sidechainActive && sc ? sc[0] : silence.data();
                const float *r2 = sidechainActive && sc ? sc[1] : silence.data();
                scPtrs[0] = const_cast<float *>(l2);
                scPtrs[1] = const_cast<float *>(scChannels == 1 ? l2 : r2);
                inputs[sidechainBus].numChannels = scChannels;
                inputs[sidechainBus].channelBuffers32 = scPtrs;
            }
            // Buses between the main input and the sidechain (rare) get silence.
            for (int32 b = 1; b < numInputs; b++) {
                if (b != sidechainBus) {
                    static float *none[1] = {nullptr};
                    inputs[b].numChannels = 0;
                    inputs[b].channelBuffers32 = none;
                }
            }
        }
        AudioBusBuffers outputs[1]{};
        outputs[0].numChannels = mainOutChannels;
        outputs[0].channelBuffers32 = outPtrs;

        ProcessData data;
        data.processMode = kRealtime;
        data.symbolicSampleSize = kSample32;
        data.numSamples = frames;
        data.numInputs = numInputs;
        data.numOutputs = audioOutputs > 0 ? 1 : 0;
        data.inputs = numInputs > 0 ? inputs : nullptr;
        data.outputs = outputs;
        data.inputParameterChanges = &inChanges;
        data.outputParameterChanges = &outChanges;
        data.inputEvents = eventInputs > 0 ? &events : nullptr;
        data.processContext = &context;
        auto result = processor->process(data);
        context.projectTimeSamples += frames;
        inChanges.clearQueue();

        if (mainOutChannels == 1) {
            std::memcpy(out[1], out[0], sizeof(float) * static_cast<size_t>(frames));
        }

        // Values the plugin itself reports.
        auto changed = outChanges.getParameterCount();
        for (int32 i = 0; i < changed; i++) {
            auto *queue = outChanges.getParameterData(i);
            auto idx = queue ? index(queue->getParameterId()) : -1;
            auto points = queue ? queue->getPointCount() : 0;
            if (idx >= 0 && points > 0) {
                int32 offset;
                ParamValue v;
                if (queue->getPoint(points - 1, offset, v) == kResultOk) {
                    values[idx].store(v, std::memory_order_relaxed);
                    controllerDirty[idx].store(true, std::memory_order_relaxed);
                    anyControllerDirty.store(true, std::memory_order_release);
                }
            }
        }
        outChanges.clearQueue();
        return result == kResultOk ? 0 : -2;
    }

    void reset() override {
        processor->setProcessing(false);
        processor->setProcessing(true);
    }

    int32_t latency() override { return processor ? static_cast<int32_t>(processor->getLatencySamples()) : 0; }

    void idle() override {
        if (controller && anyControllerDirty.exchange(false, std::memory_order_acquire)) {
            for (size_t i = 0; i < ids.size(); i++) {
                if (controllerDirty[i].exchange(false, std::memory_order_relaxed)) {
                    controller->setParamNormalized(ids[i], values[i].load(std::memory_order_relaxed));
                }
            }
        }
        Plugin::idle();
    }

    bool saveState(std::vector<uint8_t> &out) override {
        auto componentStream = owned(new MemoryStream());
        if (component->getState(componentStream) != kResultOk) {
            fail("the component could not save its state");
            return false;
        }
        auto controllerStream = owned(new MemoryStream());
        auto hasController = controller && !singleComponent && controller->getState(controllerStream) == kResultOk;
        auto put32 = [&out](uint32_t v) {
            for (int i = 0; i < 4; i++) {
                out.push_back(static_cast<uint8_t>(v >> (8 * i)));
            }
        };
        out.clear();
        const char magic[4] = {'A', 'N', 'V', '3'};
        out.insert(out.end(), magic, magic + 4);
        auto cs = static_cast<uint32_t>(componentStream->getSize());
        put32(cs);
        out.insert(out.end(), componentStream->getData(), componentStream->getData() + cs);
        auto ks = hasController ? static_cast<uint32_t>(controllerStream->getSize()) : 0u;
        put32(ks);
        if (ks > 0) {
            out.insert(out.end(), controllerStream->getData(), controllerStream->getData() + ks);
        }
        return true;
    }

    bool loadState(const uint8_t *data, size_t size) override {
        auto get32 = [data](size_t at) {
            uint32_t v = 0;
            for (int i = 0; i < 4; i++) {
                v |= static_cast<uint32_t>(data[at + i]) << (8 * i);
            }
            return v;
        };
        if (size < 12 || std::memcmp(data, "ANV3", 4) != 0) {
            fail("not a VST3 state");
            return false;
        }
        auto cs = get32(4);
        if (8 + static_cast<size_t>(cs) + 4 > size) {
            fail("truncated VST3 state");
            return false;
        }
        auto ks = get32(8 + cs);
        if (8 + static_cast<size_t>(cs) + 4 + ks > size) {
            fail("truncated VST3 state");
            return false;
        }
        auto componentStream = owned(new MemoryStream());
        int32 written = 0;
        componentStream->write(const_cast<uint8_t *>(data + 8), static_cast<int32>(cs), &written);
        componentStream->seek(0, IBStream::kIBSeekSet, nullptr);
        if (component->setState(componentStream) != kResultOk) {
            fail("the component refused the state");
            return false;
        }
        if (controller) {
            componentStream->seek(0, IBStream::kIBSeekSet, nullptr);
            controller->setComponentState(componentStream);
            if (ks > 0 && !singleComponent) {
                auto controllerStream = owned(new MemoryStream());
                controllerStream->write(const_cast<uint8_t *>(data + 12 + cs), static_cast<int32>(ks), &written);
                controllerStream->seek(0, IBStream::kIBSeekSet, nullptr);
                controller->setState(controllerStream);
            }
            for (size_t i = 0; i < ids.size(); i++) {
                values[i].store(controller->getParamNormalized(ids[i]));
            }
        }
        return true;
    }

    bool attachEditor(void *container, int32_t &width, int32_t &height) override {
        if (!controller) {
            fail("no controller");
            return false;
        }
        auto *v = controller->createView(ViewType::kEditor);
        if (!v) {
            fail("the plugin has no editor");
            return false;
        }
        view = owned(v);
        if (view->isPlatformTypeSupported(kPlatformTypeNSView) != kResultTrue) {
            view = nullptr;
            fail("the editor does not support NSView");
            return false;
        }
        view->setFrame(&frame);
        ViewRect rect{};
        if (view->getSize(&rect) == kResultOk) {
            width = rect.getWidth();
            height = rect.getHeight();
        }
        editor_resize(this, width, height);
        if (view->attached(container, kPlatformTypeNSView) != kResultOk) {
            view->setFrame(nullptr);
            view = nullptr;
            fail("the editor did not attach");
            return false;
        }
        return true;
    }

    void detachEditor() override {
        if (view) {
            view->removed();
            view->setFrame(nullptr);
            view = nullptr;
        }
    }
};

tresult PLUGIN_API ComponentHandler::performEdit(ParamID id, ParamValue value) {
    auto idx = owner->index(id);
    if (idx < 0) {
        return kResultFalse;
    }
    owner->values[idx].store(value, std::memory_order_relaxed);
    owner->edits.push(idx, value);
    owner->emit(0, idx, value);
    return kResultOk;
}

tresult PLUGIN_API ComponentHandler::restartComponent(int32 flags) {
    if (flags & kLatencyChanged) {
        owner->latencyChanged.store(true);
    }
    if (flags & kParamValuesChanged) {
        for (size_t i = 0; i < owner->ids.size(); i++) {
            owner->values[i].store(owner->controller->getParamNormalized(owner->ids[i]));
        }
    }
    return kResultOk;
}

tresult PLUGIN_API PlugFrame::resizeView(IPlugView *view, ViewRect *newSize) {
    if (!view || !newSize) {
        return kInvalidArgument;
    }
    editor_resize(owner, newSize->getWidth(), newSize->getHeight());
    view->onSize(newSize);
    return kResultOk;
}

bool findClass(const VST3::Hosting::PluginFactory &factory, const std::string &id, VST3::Hosting::ClassInfo &found) {
    for (auto &info : factory.classInfos()) {
        if (info.category() == kVstAudioEffectClass && info.ID().toString() == id) {
            found = info;
            return true;
        }
    }
    return false;
}

int32_t openClass(const VST3::Hosting::Module::Ptr &module, const VST3::Hosting::ClassInfo &info,
                  Vst3Plugin **result) {
    auto factory = module->getFactory();
    auto plugin = std::make_unique<Vst3Plugin>();
    plugin->format = AN_PLUG_VST3;
    plugin->module = module;
    plugin->id = info.ID().toString();
    plugin->name = info.name();
    plugin->vendor = info.vendor().empty() ? factory.info().vendor() : info.vendor();
    plugin->version = info.version();
    plugin->category = category_from_vst3(info.subCategoriesString(), plugin->instrument);

    plugin->component = factory.createInstance<IComponent>(info.ID());
    if (!plugin->component) {
        return fail("could not create the component");
    }
    if (plugin->component->initialize(&hostContext()) != kResultOk) {
        plugin->component = nullptr;
        return fail("the component did not initialize");
    }
    plugin->processor = FUnknownPtr<IAudioProcessor>(plugin->component);
    if (!plugin->processor) {
        return fail("the component has no audio processor");
    }
    if (auto single = FUnknownPtr<IEditController>(plugin->component)) {
        plugin->controller = single;
        plugin->singleComponent = true;
    } else {
        TUID cid;
        if (plugin->component->getControllerClassId(cid) == kResultTrue) {
            auto controllerId = VST3::UID::fromTUID(cid);
            plugin->controller = factory.createInstance<IEditController>(controllerId);
            if (plugin->controller && plugin->controller->initialize(&hostContext()) != kResultOk) {
                plugin->controller = nullptr;
            }
        }
    }
    if (plugin->controller) {
        plugin->controller->setComponentHandler(&plugin->handler);
        if (!plugin->singleComponent) {
            FUnknownPtr<IConnectionPoint> a(plugin->component), b(plugin->controller);
            if (a && b) {
                plugin->componentConnection = owned(new ConnectionProxy(a));
                plugin->controllerConnection = owned(new ConnectionProxy(b));
                plugin->componentConnection->connect(b);
                plugin->controllerConnection->connect(a);
            }
            auto stream = owned(new MemoryStream());
            if (plugin->component->getState(stream) == kResultOk) {
                stream->seek(0, IBStream::kIBSeekSet, nullptr);
                plugin->controller->setComponentState(stream);
            }
        }
        if (auto *v = plugin->controller->createView(ViewType::kEditor)) {
            plugin->customEditor = v->isPlatformTypeSupported(kPlatformTypeNSView) == kResultTrue ||
                                   v->isPlatformTypeSupported(kPlatformTypeHWND) == kResultTrue ||
                                   v->isPlatformTypeSupported(kPlatformTypeX11EmbedWindowID) == kResultTrue;
            v->release();
        }
    }
    plugin->readBuses();
    plugin->readParams();
    *result = plugin.release();
    return 0;
}

} // namespace

int32_t vst3_scan(const char *path, bool instantiate, std::string &json) {
    std::string error;
    auto module = loadModule(path, error);
    if (!module) {
        return fail(error.empty() ? "could not load the module" : error);
    }
    auto factory = module->getFactory();
    Json j;
    j.beginArray();
    for (auto &info : factory.classInfos()) {
        if (info.category() != kVstAudioEffectClass) {
            continue;
        }
        if (instantiate) {
            Vst3Plugin *plugin = nullptr;
            if (openClass(module, info, &plugin) != 0) {
                return -1;
            }
            j.raw(plugin->describe(plugin->latency()));
            delete plugin;
        } else {
            Vst3Plugin bare;
            bare.format = AN_PLUG_VST3;
            bare.id = info.ID().toString();
            bare.name = info.name();
            bare.vendor = info.vendor().empty() ? factory.info().vendor() : info.vendor();
            bare.version = info.version();
            bare.category = category_from_vst3(info.subCategoriesString(), bare.instrument);
            bare.initValues();
            j.raw(bare.describe(0));
        }
    }
    j.endArray();
    json = j.str();
    return 0;
}

int32_t vst3_open(const char *path, const char *id, Plugin **result) {
    std::string error;
    auto module = loadModule(path, error);
    if (!module) {
        return fail(error.empty() ? "could not load the module" : error);
    }
    VST3::Hosting::ClassInfo info;
    if (!findClass(module->getFactory(), id, info)) {
        return fail(std::string("no audio module class ") + id + " in " + path);
    }
    Vst3Plugin *plugin = nullptr;
    auto r = openClass(module, info, &plugin);
    if (r == 0) {
        *result = plugin;
    }
    return r;
}

} // namespace anplug

