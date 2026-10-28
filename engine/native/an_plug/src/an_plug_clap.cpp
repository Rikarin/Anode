/*
 * an_plug: CLAP hosting (the CLAP headers, MIT, pinned in an_testclap/CMakeLists.txt). With this, CLAP effects and
 * instruments run in the anode-plugin sandbox like VST3 and AU (docs/05-phase-4.md, "CLAP"). GPL-3.0-or-later
 */
#include "an_plug_internal.h"

#include <clap/clap.h>

#include <algorithm>
#include <cstring>
#include <map>
#include <mutex>

#if defined(__APPLE__)
#include <CoreFoundation/CoreFoundation.h>
#else
#include <dlfcn.h>
#endif

namespace anplug {

namespace {

/* A loaded CLAP module: kept for the life of the process (see the VST3 modules). */
struct ClapModule {
    const clap_plugin_entry_t *entry = nullptr;
    const clap_plugin_factory_t *factory = nullptr;
};

std::mutex modulesGate;
std::map<std::string, ClapModule> modules;

bool loadModule(const std::string &path, ClapModule &out) {
    std::lock_guard<std::mutex> lock(modulesGate);
    auto it = modules.find(path);
    if (it != modules.end()) {
        out = it->second;
        return true;
    }
    const clap_plugin_entry_t *entry = nullptr;
#if defined(__APPLE__)
    auto url = CFURLCreateFromFileSystemRepresentation(nullptr, reinterpret_cast<const UInt8 *>(path.c_str()),
                                                       static_cast<CFIndex>(path.size()), true);
    auto bundle = url ? CFBundleCreate(nullptr, url) : nullptr;
    if (url) {
        CFRelease(url);
    }
    if (!bundle || !CFBundleLoadExecutable(bundle)) {
        fail("could not load the CLAP bundle " + path);
        return false;
    }
    entry = static_cast<const clap_plugin_entry_t *>(CFBundleGetDataPointerForName(bundle, CFSTR("clap_entry")));
#else
    auto *handle = dlopen(path.c_str(), RTLD_LOCAL | RTLD_NOW);
    if (!handle) {
        fail(std::string("could not load ") + path + ": " + dlerror());
        return false;
    }
    entry = static_cast<const clap_plugin_entry_t *>(dlsym(handle, "clap_entry"));
#endif
    if (!entry) {
        fail("no clap_entry in " + path);
        return false;
    }
    if (!clap_version_is_compatible(entry->clap_version) || !entry->init(path.c_str())) {
        fail("the CLAP entry did not initialise");
        return false;
    }
    auto *factory = static_cast<const clap_plugin_factory_t *>(entry->get_factory(CLAP_PLUGIN_FACTORY_ID));
    if (!factory) {
        fail("no CLAP plugin factory in " + path);
        return false;
    }
    out = {entry, factory};
    modules[path] = out;
    return true;
}

std::string categoryOf(const clap_plugin_descriptor_t *d, bool &instrument) {
    instrument = false;
    std::string words;
    bool effect = false;
    for (auto f = d->features; f && *f; f++) {
        std::string feature = *f;
        if (feature == CLAP_PLUGIN_FEATURE_INSTRUMENT) {
            instrument = true;
        } else if (feature == CLAP_PLUGIN_FEATURE_AUDIO_EFFECT) {
            effect = true;
        } else if (feature == CLAP_PLUGIN_FEATURE_EQUALIZER) {
            words += " eq";
        } else if (feature == CLAP_PLUGIN_FEATURE_ANALYZER) {
            words += " meter";
        } else {
            words += " " + feature;
        }
    }
    instrument = instrument && !effect;
    return instrument ? "" : category_from_words(words);
}

/* Input events: parameter values, then notes, sorted by time (built per block, no allocation). */
struct EventList {
    std::vector<clap_event_param_value_t> params;
    std::vector<clap_event_note_t> notes;
    std::vector<const clap_event_header_t *> order;
    clap_input_events_t in{this, &EventList::size, &EventList::get};

    void reserve(size_t p, size_t n) {
        params.reserve(p);
        notes.reserve(n);
        order.reserve(p + n);
    }
    void clear() {
        params.clear();
        notes.clear();
        order.clear();
    }
    static uint32_t size(const clap_input_events_t *list) {
        return static_cast<uint32_t>(static_cast<EventList *>(list->ctx)->order.size());
    }
    static const clap_event_header_t *get(const clap_input_events_t *list, uint32_t index) {
        auto *self = static_cast<EventList *>(list->ctx);
        return index < self->order.size() ? self->order[index] : nullptr;
    }
};

struct ClapPlugin;

const void *hostExtension(const clap_host_t *, const char *) { return nullptr; }
void hostRequestRestart(const clap_host_t *) {}
void hostRequestProcess(const clap_host_t *) {}
void hostRequestCallback(const clap_host_t *host);

struct ClapPlugin final : Plugin {
    clap_host_t host{};
    const clap_plugin_t *plugin = nullptr;
    const clap_plugin_params_t *paramsExt = nullptr;
    const clap_plugin_state_t *stateExt = nullptr;
    const clap_plugin_latency_t *latencyExt = nullptr;
    const clap_plugin_gui_t *guiExt = nullptr;
    const clap_plugin_audio_ports_t *portsExt = nullptr;
    std::vector<clap_id> ids;
    std::map<clap_id, int32_t> indexOf;
    std::vector<void *> cookies;
    std::atomic<bool> callbackRequested{false};
    bool processing = false;
    bool guiCreated = false;
    int32_t mainIn = -1, sidechainIn = -1, mainOut = -1;
    uint32_t inputPorts = 0, outputPorts = 0;
    uint32_t mainInChannels = 2, mainOutChannels = 2, scChannels = 2;
    bool sidechainActive = false;
    int64_t steadyTime = 0;
    EventList events;
    EditRing hostSet;   // audio thread: values set since the last block
    EditRing edited{};  // audio -> main: values the plugin reported (its editor)
    std::vector<float> silence, scratch;
    std::vector<clap_audio_buffer_t> inputs, outputs;
    float *inPtrs[2]{}, *scPtrs[2]{}, *outPtrs[2]{}, *spare[2]{};

    ClapPlugin() {
        host.clap_version = CLAP_VERSION;
        host.host_data = this;
        host.name = "Anode";
        host.vendor = "Anode";
        host.url = "https://github.com/anode-daw";
        host.version = "0.1";
        host.get_extension = &hostExtension;
        host.request_restart = &hostRequestRestart;
        host.request_process = &hostRequestProcess;
        host.request_callback = &hostRequestCallback;
    }

    ~ClapPlugin() override {
        if (plugin) {
            if (guiCreated && guiExt) {
                guiExt->destroy(plugin);
            }
            plugin->destroy(plugin);
        }
    }

    int32_t index(clap_id id) const {
        auto it = indexOf.find(id);
        return it == indexOf.end() ? -1 : it->second;
    }

    void readParams() {
        params.clear();
        ids.clear();
        indexOf.clear();
        cookies.clear();
        auto count = paramsExt ? paramsExt->count(plugin) : 0;
        for (uint32_t i = 0; i < count; i++) {
            clap_param_info_t info{};
            if (!paramsExt->get_info(plugin, i, &info)) {
                continue;
            }
            if (info.flags & (CLAP_PARAM_IS_HIDDEN | CLAP_PARAM_IS_READONLY | CLAP_PARAM_IS_BYPASS)) {
                continue;
            }
            ParamInfo p;
            p.id = std::to_string(info.id);
            p.name = info.name;
            p.min = info.min_value;
            p.max = info.max_value;
            p.def = info.default_value;
            if (info.flags & CLAP_PARAM_IS_STEPPED) {
                p.steps = static_cast<int32_t>(info.max_value - info.min_value);
                if (p.steps > 0 && p.steps <= 128) {
                    for (int32_t s = 0; s <= p.steps; s++) {
                        char text[CLAP_NAME_SIZE] = {0};
                        if (paramsExt->value_to_text(plugin, info.id, info.min_value + s, text, sizeof text)) {
                            p.valueNames.push_back(text);
                        } else {
                            p.valueNames.clear();
                            break;
                        }
                    }
                }
            }
            if (p.max <= p.min) {
                continue;
            }
            indexOf[info.id] = static_cast<int32_t>(params.size());
            ids.push_back(info.id);
            cookies.push_back(info.cookie);
            params.push_back(std::move(p));
        }
        initValues();
        refreshValues();
    }

    void refreshValues() {
        for (size_t i = 0; i < ids.size(); i++) {
            double v = 0;
            if (paramsExt->get_value(plugin, ids[i], &v)) {
                values[i].store(v);
            }
        }
    }

    void readPorts() {
        inputPorts = portsExt ? portsExt->count(plugin, true) : 0;
        outputPorts = portsExt ? portsExt->count(plugin, false) : 0;
        for (uint32_t i = 0; i < inputPorts; i++) {
            clap_audio_port_info_t info{};
            if (!portsExt->get(plugin, i, true, &info)) {
                continue;
            }
            if ((info.flags & CLAP_AUDIO_PORT_IS_MAIN) && mainIn < 0) {
                mainIn = static_cast<int32_t>(i);
                mainInChannels = std::min(info.channel_count, 2u);
            } else if (sidechainIn < 0) {
                sidechainIn = static_cast<int32_t>(i);
                scChannels = std::min(info.channel_count, 2u);
            }
        }
        for (uint32_t i = 0; i < outputPorts; i++) {
            clap_audio_port_info_t info{};
            if (portsExt->get(plugin, i, false, &info) && (info.flags & CLAP_AUDIO_PORT_IS_MAIN) && mainOut < 0) {
                mainOut = static_cast<int32_t>(i);
                mainOutChannels = std::min(info.channel_count, 2u);
            }
        }
        if (mainOut < 0 && outputPorts > 0) {
            mainOut = 0;
        }
        sidechainCapable = sidechainIn >= 0 && !instrument;
    }

    int32_t activate(double sampleRate, int32_t maxFrames, bool sidechain) override {
        sidechainActive = sidechain && sidechainIn >= 0;
        if (mainOut < 0) {
            return fail("the CLAP plugin has no audio output");
        }
        silence.assign(static_cast<size_t>(maxFrames), 0.0f);
        scratch.assign(static_cast<size_t>(maxFrames) * 4, 0.0f);
        spare[0] = scratch.data() + 2 * maxFrames;
        spare[1] = scratch.data() + 3 * maxFrames;
        events.reserve(ids.size() + 64, 1024);
        inputs.assign(inputPorts, clap_audio_buffer_t{});
        outputs.assign(outputPorts, clap_audio_buffer_t{});
        if (!plugin->activate(plugin, sampleRate, 1, static_cast<uint32_t>(maxFrames))) {
            return fail("the CLAP plugin did not activate");
        }
        steadyTime = 0;
        return 0;
    }

    void deactivate() override {
        if (processing) {
            plugin->stop_processing(plugin);
            processing = false;
        }
        plugin->deactivate(plugin);
    }

    void setParam(int32_t index, double value) override { hostSet.push(index, value); }

    int32_t process(const float *const *in, const float *const *sc, float *const *out, int32_t frames,
                    const an_plug_midi *midi, int32_t midiCount) override {
        if (!processing) {
            processing = plugin->start_processing(plugin);
        }
        events.clear();
        int32_t idx;
        double value;
        while (hostSet.pop(idx, value)) {
            clap_event_param_value_t e{};
            e.header = {sizeof e, 0, CLAP_CORE_EVENT_SPACE_ID, CLAP_EVENT_PARAM_VALUE, 0};
            e.param_id = ids[static_cast<size_t>(idx)];
            e.cookie = cookies[static_cast<size_t>(idx)];
            e.note_id = -1;
            e.port_index = -1;
            e.channel = -1;
            e.key = -1;
            e.value = value;
            if (events.params.size() < events.params.capacity()) {
                events.params.push_back(e);
            }
        }
        for (int32_t i = 0; i < midiCount && events.notes.size() < events.notes.capacity(); i++) {
            auto kind = midi[i].status & 0xF0;
            clap_event_note_t n{};
            auto time = static_cast<uint32_t>(std::clamp(midi[i].frame, 0, frames - 1));
            if (kind == 0x90 && midi[i].data2 > 0) {
                n.header = {sizeof n, time, CLAP_CORE_EVENT_SPACE_ID, CLAP_EVENT_NOTE_ON, 0};
            } else if (kind == 0x80 || kind == 0x90) {
                n.header = {sizeof n, time, CLAP_CORE_EVENT_SPACE_ID, CLAP_EVENT_NOTE_OFF, 0};
            } else {
                continue;
            }
            n.note_id = -1;
            n.port_index = 0;
            n.channel = static_cast<int16_t>(midi[i].status & 0x0F);
            n.key = midi[i].data1;
            n.velocity = midi[i].data2 / 127.0;
            events.notes.push_back(n);
        }
        for (auto &p : events.params) {
            events.order.push_back(&p.header);
        }
        for (auto &n : events.notes) {
            events.order.push_back(&n.header);
        }
        std::stable_sort(events.order.begin(), events.order.end(),
                         [](const clap_event_header_t *a, const clap_event_header_t *b) { return a->time < b->time; });

        for (auto &b : inputs) {
            b = {};
            b.data32 = spare;
            b.channel_count = 0;
        }
        if (mainIn >= 0 && !instrument) {
            const float *l = in ? in[0] : silence.data();
            const float *r = in ? in[1] : silence.data();
            if (mainInChannels == 1) {
                auto *m = scratch.data();
                for (int32_t i = 0; i < frames; i++) {
                    m[i] = 0.5f * (l[i] + r[i]);
                }
                inPtrs[0] = m;
            } else {
                inPtrs[0] = const_cast<float *>(l);
                inPtrs[1] = const_cast<float *>(r);
            }
            inputs[static_cast<size_t>(mainIn)].data32 = inPtrs;
            inputs[static_cast<size_t>(mainIn)].channel_count = mainInChannels;
        }
        if (sidechainIn >= 0) {
            const float *l = sidechainActive && sc ? sc[0] : silence.data();
            const float *r = sidechainActive && sc ? sc[1] : silence.data();
            scPtrs[0] = const_cast<float *>(l);
            scPtrs[1] = const_cast<float *>(r);
            inputs[static_cast<size_t>(sidechainIn)].data32 = scPtrs;
            inputs[static_cast<size_t>(sidechainIn)].channel_count = scChannels;
        }
        for (auto &b : outputs) {
            b = {};
            b.data32 = spare;
            b.channel_count = 0;
        }
        outPtrs[0] = out[0];
        outPtrs[1] = out[1];
        outputs[static_cast<size_t>(mainOut)].data32 = outPtrs;
        outputs[static_cast<size_t>(mainOut)].channel_count = mainOutChannels;

        clap_output_events_t outEvents{this, &ClapPlugin::tryPush};
        clap_process_t p{};
        p.steady_time = steadyTime;
        p.frames_count = static_cast<uint32_t>(frames);
        p.audio_inputs = inputs.data();
        p.audio_inputs_count = static_cast<uint32_t>(inputs.size());
        p.audio_outputs = outputs.data();
        p.audio_outputs_count = static_cast<uint32_t>(outputs.size());
        p.in_events = &events.in;
        p.out_events = &outEvents;
        auto status = plugin->process(plugin, &p);
        steadyTime += frames;
        if (mainOutChannels == 1) {
            std::memcpy(out[1], out[0], sizeof(float) * static_cast<size_t>(frames));
        }
        return status == CLAP_PROCESS_ERROR ? -2 : 0;
    }

    static bool tryPush(const clap_output_events_t *list, const clap_event_header_t *e) {
        auto *self = static_cast<ClapPlugin *>(list->ctx);
        if (e->space_id == CLAP_CORE_EVENT_SPACE_ID && e->type == CLAP_EVENT_PARAM_VALUE) {
            auto *v = reinterpret_cast<const clap_event_param_value_t *>(e);
            auto idx = self->index(v->param_id);
            if (idx >= 0) {
                self->values[idx].store(v->value, std::memory_order_relaxed);
                self->edited.push(idx, v->value);
            }
        }
        return true;
    }

    void reset() override { plugin->reset(plugin); }

    int32_t latency() override { return latencyExt ? static_cast<int32_t>(latencyExt->get(plugin)) : 0; }

    void idle() override {
        if (callbackRequested.exchange(false)) {
            plugin->on_main_thread(plugin);
        }
        int32_t idx;
        double value;
        while (edited.pop(idx, value)) {
            emit(0, idx, value);
        }
        Plugin::idle();
    }

    static int64_t write(const clap_ostream_t *s, const void *buffer, uint64_t size) {
        auto *out = static_cast<std::vector<uint8_t> *>(s->ctx);
        auto *b = static_cast<const uint8_t *>(buffer);
        out->insert(out->end(), b, b + size);
        return static_cast<int64_t>(size);
    }

    struct Reader {
        const uint8_t *data;
        size_t size;
        size_t at;
    };

    static int64_t read(const clap_istream_t *s, void *buffer, uint64_t size) {
        auto *r = static_cast<Reader *>(s->ctx);
        auto n = std::min<uint64_t>(size, r->size - r->at);
        std::memcpy(buffer, r->data + r->at, n);
        r->at += n;
        return static_cast<int64_t>(n);
    }

    bool saveState(std::vector<uint8_t> &out) override {
        if (!stateExt) {
            fail("the CLAP plugin keeps no state");
            return false;
        }
        out.clear();
        clap_ostream_t s{&out, &ClapPlugin::write};
        if (!stateExt->save(plugin, &s)) {
            fail("the CLAP plugin could not save its state");
            return false;
        }
        return true;
    }

    bool loadState(const uint8_t *data, size_t size) override {
        if (!stateExt) {
            fail("the CLAP plugin keeps no state");
            return false;
        }
        Reader reader{data, size, 0};
        clap_istream_t s{&reader, &ClapPlugin::read};
        if (!stateExt->load(plugin, &s)) {
            fail("the CLAP plugin refused the state");
            return false;
        }
        refreshValues();
        return true;
    }

    bool attachEditor(void *container, int32_t &width, int32_t &height) override {
#if defined(__APPLE__)
        if (!guiExt || !guiExt->is_api_supported(plugin, CLAP_WINDOW_API_COCOA, false)) {
            fail("the CLAP plugin has no embeddable Cocoa editor");
            return false;
        }
        if (!guiExt->create(plugin, CLAP_WINDOW_API_COCOA, false)) {
            fail("the CLAP plugin did not create its editor");
            return false;
        }
        guiCreated = true;
        uint32_t w = 0, h = 0;
        if (guiExt->get_size(plugin, &w, &h)) {
            width = static_cast<int32_t>(w);
            height = static_cast<int32_t>(h);
            editor_resize(this, width, height);
        }
        clap_window_t window{};
        window.api = CLAP_WINDOW_API_COCOA;
        window.cocoa = container;
        if (!guiExt->set_parent(plugin, &window)) {
            guiExt->destroy(plugin);
            guiCreated = false;
            fail("the CLAP editor did not attach");
            return false;
        }
        guiExt->show(plugin);
        return true;
#else
        (void)container;
        (void)width;
        (void)height;
        return false;
#endif
    }

    void detachEditor() override {
        if (guiCreated) {
            guiExt->hide(plugin);
            guiExt->destroy(plugin);
            guiCreated = false;
        }
    }
};

void hostRequestCallback(const clap_host_t *host) {
    static_cast<ClapPlugin *>(host->host_data)->callbackRequested.store(true);
}

int32_t openPlugin(const ClapModule &module, const clap_plugin_descriptor_t *d, ClapPlugin **result) {
    auto p = std::make_unique<ClapPlugin>();
    p->format = AN_PLUG_CLAP;
    p->id = d->id;
    p->name = d->name ? d->name : d->id;
    p->vendor = d->vendor ? d->vendor : "";
    p->version = d->version ? d->version : "";
    p->category = categoryOf(d, p->instrument);
    p->plugin = module.factory->create_plugin(module.factory, &p->host, d->id);
    if (!p->plugin) {
        return fail(std::string("could not create ") + d->id);
    }
    if (!p->plugin->init(p->plugin)) {
        p->plugin->destroy(p->plugin);
        p->plugin = nullptr;
        return fail(std::string("could not initialise ") + d->id);
    }
    auto ext = [&](const char *id) { return p->plugin->get_extension(p->plugin, id); };
    p->paramsExt = static_cast<const clap_plugin_params_t *>(ext(CLAP_EXT_PARAMS));
    p->stateExt = static_cast<const clap_plugin_state_t *>(ext(CLAP_EXT_STATE));
    p->latencyExt = static_cast<const clap_plugin_latency_t *>(ext(CLAP_EXT_LATENCY));
    p->guiExt = static_cast<const clap_plugin_gui_t *>(ext(CLAP_EXT_GUI));
    p->portsExt = static_cast<const clap_plugin_audio_ports_t *>(ext(CLAP_EXT_AUDIO_PORTS));
#if defined(__APPLE__)
    p->customEditor = p->guiExt && p->guiExt->is_api_supported(p->plugin, CLAP_WINDOW_API_COCOA, false);
#endif
    p->readPorts();
    p->readParams();
    *result = p.release();
    return 0;
}

} // namespace

int32_t clap_scan(const char *path, bool instantiate, std::string &json) {
    ClapModule module;
    if (!loadModule(path, module)) {
        return -1;
    }
    Json j;
    j.beginArray();
    auto count = module.factory->get_plugin_count(module.factory);
    for (uint32_t i = 0; i < count; i++) {
        auto *d = module.factory->get_plugin_descriptor(module.factory, i);
        if (!d) {
            continue;
        }
        if (instantiate) {
            ClapPlugin *p = nullptr;
            if (openPlugin(module, d, &p) != 0) {
                return -1;
            }
            j.raw(p->describe(p->latency()));
            delete p;
        } else {
            ClapPlugin bare;
            bare.format = AN_PLUG_CLAP;
            bare.id = d->id;
            bare.name = d->name ? d->name : d->id;
            bare.vendor = d->vendor ? d->vendor : "";
            bare.version = d->version ? d->version : "";
            bare.category = categoryOf(d, bare.instrument);
            bare.initValues();
            j.raw(bare.describe(0));
        }
    }
    j.endArray();
    json = j.str();
    return 0;
}

int32_t clap_open(const char *path, const char *id, Plugin **result) {
    ClapModule module;
    if (!loadModule(path, module)) {
        return -1;
    }
    auto count = module.factory->get_plugin_count(module.factory);
    for (uint32_t i = 0; i < count; i++) {
        auto *d = module.factory->get_plugin_descriptor(module.factory, i);
        if (d && std::strcmp(d->id, id) == 0) {
            ClapPlugin *p = nullptr;
            auto r = openPlugin(module, d, &p);
            if (r == 0) {
                *result = p;
            }
            return r;
        }
    }
    return fail(std::string("no CLAP plugin ") + id + " in " + path);
}

} // namespace anplug
