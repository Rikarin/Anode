/*
 * an_plug: the C ABI, dispatching to the formats. SPDX-License-Identifier: GPL-3.0-or-later
 */
#include "an_plug_internal.h"

#include <algorithm>
#include <cctype>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fcntl.h>
#include <unistd.h>

namespace anplug {

namespace {
thread_local std::string lastError;
}

int32_t fail(const std::string &message) {
    lastError = message;
    return -1;
}

void clear_error() { lastError.clear(); }

char *dup_string(const std::string &s) {
    auto *p = static_cast<char *>(std::malloc(s.size() + 1));
    if (p) {
        std::memcpy(p, s.c_str(), s.size() + 1);
    }
    return p;
}

Plugin::~Plugin() = default;

void Plugin::initValues() {
    values.reset(new std::atomic<double>[params.size()]);
    controllerDirty.reset(new std::atomic<bool>[params.size()]);
    for (size_t i = 0; i < params.size(); i++) {
        values[i].store(params[i].def);
        controllerDirty[i].store(false);
    }
}

void Plugin::idle() {
    auto l = latency();
    if (l != lastLatency.exchange(l)) {
        emit(1, -1, l);
    }
}

std::string Plugin::describe(int32_t latencySamples) const {
    Json j;
    j.beginObject();
    j.key("id").value(id);
    j.key("name").value(name);
    j.key("vendor").value(vendor);
    j.key("version").value(version);
    j.key("format").value(format == AN_PLUG_VST3 ? "vst3" : "au");
    j.key("category").value(instrument ? std::string() : category);
    j.key("isInstrument").value(instrument);
    j.key("hasSidechain").value(sidechainCapable);
    j.key("hasEditor").value(customEditor);
    j.key("latency").value(latencySamples);
    j.key("parameters").beginArray();
    for (auto &p : params) {
        j.beginObject();
        j.key("id").value(p.id);
        j.key("name").value(p.name);
        j.key("min").value(p.min);
        j.key("max").value(p.max);
        j.key("default").value(p.def);
        j.key("unit").value(p.unit);
        j.key("steps").value(p.steps);
        if (!p.valueNames.empty()) {
            j.key("valueNames").beginArray();
            for (auto &n : p.valueNames) {
                j.value(n);
            }
            j.endArray();
        }
        j.endObject();
    }
    j.endArray();
    j.endObject();
    return j.str();
}

// ---- JSON ----------------------------------------------------------------------------------------------------------

void Json::sep() {
    if (afterKey) {
        afterKey = false;
        return;
    }
    if (!first.empty()) {
        if (!first.back()) {
            out += ',';
        }
        first.back() = false;
    }
}

Json &Json::beginObject() {
    sep();
    out += '{';
    first.push_back(true);
    return *this;
}

Json &Json::endObject() {
    out += '}';
    first.pop_back();
    return *this;
}

Json &Json::beginArray() {
    sep();
    out += '[';
    first.push_back(true);
    return *this;
}

Json &Json::endArray() {
    out += ']';
    first.pop_back();
    return *this;
}

static void write_string(std::string &out, const char *s) {
    out += '"';
    for (const char *c = s; *c; c++) {
        auto ch = static_cast<unsigned char>(*c);
        switch (ch) {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:
                if (ch < 0x20) {
                    char buf[8];
                    std::snprintf(buf, sizeof buf, "\\u%04x", ch);
                    out += buf;
                } else {
                    out += static_cast<char>(ch);
                }
        }
    }
    out += '"';
}

Json &Json::key(const char *k) {
    sep();
    write_string(out, k);
    out += ':';
    afterKey = true;
    return *this;
}

Json &Json::value(const char *s) {
    sep();
    write_string(out, s);
    return *this;
}

Json &Json::value(const std::string &s) { return value(s.c_str()); }

Json &Json::value(double d) {
    sep();
    if (!std::isfinite(d)) {
        out += "0";
        return *this;
    }
    char buf[40];
    std::snprintf(buf, sizeof buf, "%.17g", d);
    out += buf;
    return *this;
}

Json &Json::value(int32_t i) {
    sep();
    out += std::to_string(i);
    return *this;
}

Json &Json::value(bool b) {
    sep();
    out += b ? "true" : "false";
    return *this;
}

Json &Json::raw(const std::string &s) {
    sep();
    out += s;
    return *this;
}

// ---- Categories ----------------------------------------------------------------------------------------------------

static std::string lower(std::string s) {
    std::transform(s.begin(), s.end(), s.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return s;
}

std::string category_from_words(const std::string &text) {
    auto t = lower(text);
    struct Rule {
        const char *word;
        const char *category;
    };
    static const Rule rules[] = {
        {"reverb", "Reverb"},     {"verb", "Reverb"},          {"delay", "Delay"},        {"echo", "Delay"},
        {"eq", "EQ"},             {"shelf", "EQ"},             {"parametric", "EQ"},      {"graphic", "EQ"},
        {"compress", "Dynamics"}, {"limit", "Dynamics"},       {"gate", "Dynamics"},      {"expander", "Dynamics"},
        {"dynamics", "Dynamics"}, {"de-ess", "Dynamics"},      {"chorus", "Modulation"},  {"flang", "Modulation"},
        {"phas", "Modulation"},   {"tremolo", "Modulation"},   {"modulat", "Modulation"}, {"distort", "Distortion"},
        {"overdrive", "Distortion"}, {"saturat", "Distortion"}, {"crush", "Distortion"},  {"filter", "Filter"},
        {"pass", "Filter"},       {"meter", "Metering"},       {"analy", "Metering"},     {"loudness", "Metering"},
    };
    for (auto &r : rules) {
        if (t.find(r.word) != std::string::npos) {
            return r.category;
        }
    }
    return "Utility";
}

std::string category_from_vst3(const std::string &subCategories, bool &instrument) {
    // "Fx|EQ", "Instrument|Synth", "Fx|Delay|Stereo"...
    auto s = lower(subCategories);
    instrument = s.find("instrument") != std::string::npos && s.find("fx") == std::string::npos;
    if (instrument) {
        return "";
    }
    if (s.find("reverb") != std::string::npos) return "Reverb";
    if (s.find("delay") != std::string::npos) return "Delay";
    if (s.find("eq") != std::string::npos) return "EQ";
    if (s.find("dynamics") != std::string::npos) return "Dynamics";
    if (s.find("modulation") != std::string::npos) return "Modulation";
    if (s.find("distortion") != std::string::npos) return "Distortion";
    if (s.find("filter") != std::string::npos) return "Filter";
    if (s.find("analyzer") != std::string::npos) return "Metering";
    return "Utility";
}

} // namespace anplug

using namespace anplug;

extern "C" {

const char *an_plug_last_error(void) { return lastError.c_str(); }

void an_plug_free(void *p) { std::free(p); }

int32_t an_plug_format_supported(int32_t format) {
#if defined(__APPLE__)
    return format == AN_PLUG_VST3 || format == AN_PLUG_AU;
#else
    return format == AN_PLUG_VST3;
#endif
}

int32_t an_plug_scan(int32_t format, const char *path, int32_t instantiate, char **json) {
    clear_error();
    std::string out;
    int32_t r;
    switch (format) {
        case AN_PLUG_VST3:
            if (!path) {
                return fail("VST3: a path is required");
            }
            r = vst3_scan(path, instantiate != 0, out);
            break;
#if defined(__APPLE__)
        case AN_PLUG_AU: r = au_scan(path, instantiate != 0, out); break;
#endif
        default: return fail("unsupported format");
    }
    if (r == 0) {
        *json = dup_string(out);
    }
    return r;
}

int32_t an_plug_open(int32_t format, const char *path, const char *id, an_plug **plug) {
    clear_error();
    Plugin *impl = nullptr;
    int32_t r;
    switch (format) {
        case AN_PLUG_VST3:
            if (!path || !id) {
                return fail("VST3: a path and a class id are required");
            }
            r = vst3_open(path, id, &impl);
            break;
#if defined(__APPLE__)
        case AN_PLUG_AU:
            if (!id) {
                return fail("AU: a component id is required");
            }
            r = au_open(id, &impl);
            break;
#endif
        default: return fail("unsupported format");
    }
    if (r != 0) {
        return r;
    }
    impl->lastLatency.store(impl->latency());
    *plug = new an_plug{impl};
    return 0;
}

int32_t an_plug_describe(an_plug *plug, char **json) {
    *json = dup_string(plug->impl->describe(plug->impl->latency()));
    return 0;
}

int32_t an_plug_param_count(an_plug *plug) { return static_cast<int32_t>(plug->impl->params.size()); }

int32_t an_plug_is_instrument(an_plug *plug) { return plug->impl->instrument ? 1 : 0; }

int32_t an_plug_activate(an_plug *plug, double sample_rate, int32_t max_frames, int32_t sidechain) {
    clear_error();
    if (plug->impl->active) {
        return fail("already active");
    }
    auto r = plug->impl->activate(sample_rate, max_frames, sidechain != 0 && plug->impl->sidechainCapable);
    if (r == 0) {
        plug->impl->active = true;
        plug->impl->lastLatency.store(plug->impl->latency());
    }
    return r;
}

void an_plug_deactivate(an_plug *plug) {
    if (plug->impl->active) {
        plug->impl->deactivate();
        plug->impl->active = false;
    }
}

void an_plug_close(an_plug *plug) {
    if (!plug) {
        return;
    }
    editor_close(plug->impl);
    an_plug_deactivate(plug);
    delete plug->impl;
    delete plug;
}

int32_t an_plug_process(an_plug *plug, const float *const *in, const float *const *sidechain, float *const *out,
                        int32_t frames, const an_plug_midi *midi, int32_t midi_count) {
    if (!plug->impl->active) {
        return -1;
    }
    return plug->impl->process(in, sidechain, out, frames, midi, midi ? midi_count : 0);
}

void an_plug_reset(an_plug *plug) {
    if (plug->impl->active) {
        plug->impl->reset();
    }
}

void an_plug_param_set(an_plug *plug, int32_t index, double value) {
    auto *p = plug->impl;
    if (index < 0 || static_cast<size_t>(index) >= p->params.size()) {
        return;
    }
    value = std::clamp(value, p->params[index].min, p->params[index].max);
    p->values[index].store(value, std::memory_order_relaxed);
    p->setParam(index, value);
}

double an_plug_param_get(an_plug *plug, int32_t index) {
    auto *p = plug->impl;
    if (index < 0 || static_cast<size_t>(index) >= p->params.size()) {
        return 0;
    }
    return p->values[index].load(std::memory_order_relaxed);
}

int32_t an_plug_latency(an_plug *plug) { return plug->impl->latency(); }

int32_t an_plug_state_save(an_plug *plug, uint8_t **data, int64_t *size) {
    clear_error();
    std::vector<uint8_t> bytes;
    if (!plug->impl->saveState(bytes)) {
        return lastError.empty() ? fail("the plugin could not save its state") : -1;
    }
    *data = static_cast<uint8_t *>(std::malloc(bytes.empty() ? 1 : bytes.size()));
    if (!bytes.empty()) {
        std::memcpy(*data, bytes.data(), bytes.size());
    }
    *size = static_cast<int64_t>(bytes.size());
    return 0;
}

int32_t an_plug_state_load(an_plug *plug, const uint8_t *data, int64_t size) {
    clear_error();
    if (!plug->impl->loadState(data, static_cast<size_t>(size))) {
        return lastError.empty() ? fail("the plugin refused the state") : -1;
    }
    return 0;
}

void an_plug_set_event_callback(an_plug *plug, an_plug_event_cb callback, void *user) {
    plug->impl->callback = callback;
    plug->impl->callbackUser = user;
}

void an_plug_idle(an_plug *plug) { plug->impl->idle(); }

int32_t an_plug_has_editor(an_plug *plug) { return plug->impl->customEditor ? 1 : 0; }

int32_t an_plug_editor_open(an_plug *plug, const char *title, int64_t parent_window, int32_t x, int32_t y,
                            int32_t flags) {
    clear_error();
    return editor_open(plug->impl, title ? title : plug->impl->name.c_str(), parent_window, x, y, flags);
}

void an_plug_editor_close(an_plug *plug) { editor_close(plug->impl); }

int32_t an_plug_editor_window(an_plug *plug) { return editor_window(plug->impl); }

int32_t an_plug_redirect_stdout(void) {
    std::fflush(stdout);
    int fd = dup(1);
    if (fd < 0) {
        return fail("dup failed");
    }
    fcntl(fd, F_SETFD, FD_CLOEXEC);
    dup2(2, 1);
    return fd;
}

void an_plug_flush_denormals(void) {
#if defined(__aarch64__)
    uint64_t fpcr;
    __asm__ volatile("mrs %0, fpcr" : "=r"(fpcr));
    fpcr |= (1ULL << 24); // FZ
    __asm__ volatile("msr fpcr, %0" : : "r"(fpcr));
#elif defined(__x86_64__)
    unsigned int csr;
    __asm__ volatile("stmxcsr %0" : "=m"(csr));
    csr |= 0x8040; // FTZ | DAZ
    __asm__ volatile("ldmxcsr %0" : : "m"(csr));
#endif
}

void an_plug_crash(void) {
    volatile int *p = nullptr;
    *p = 1;
}

} // extern "C"
