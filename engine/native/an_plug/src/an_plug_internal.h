/*
 * an_plug internals: the instance base every format implements, errors and a tiny JSON writer.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#pragma once

#include "an_plug.h"

#include <atomic>
#include <cstdint>
#include <memory>
#include <string>
#include <vector>

namespace anplug {

/* Sets this thread's last error and returns -1. */
int32_t fail(const std::string &message);
void clear_error();

/* malloc'd copy of a string (for JSON handed to the caller). */
char *dup_string(const std::string &s);

struct ParamInfo {
    std::string id;
    std::string name;
    double min = 0;
    double max = 1;
    double def = 0;
    std::string unit;
    int32_t steps = 0;
    std::vector<std::string> valueNames;
};

/* A single-producer single-consumer ring of parameter edits (main thread -> audio thread). Fixed size, no
 * allocation after construction. */
class EditRing {
public:
    explicit EditRing(uint32_t capacity = 1024) : items(capacity), mask(capacity - 1) {}
    bool push(int32_t index, double value) {
        auto w = write.load(std::memory_order_relaxed);
        if (w - read.load(std::memory_order_acquire) > mask) {
            return false;
        }
        items[w & mask] = {index, value};
        write.store(w + 1, std::memory_order_release);
        return true;
    }
    bool pop(int32_t &index, double &value) {
        auto r = read.load(std::memory_order_relaxed);
        if (r == write.load(std::memory_order_acquire)) {
            return false;
        }
        index = items[r & mask].index;
        value = items[r & mask].value;
        read.store(r + 1, std::memory_order_release);
        return true;
    }

private:
    struct Item {
        int32_t index;
        double value;
    };
    std::vector<Item> items;
    uint32_t mask;
    std::atomic<uint32_t> write{0};
    std::atomic<uint32_t> read{0};
};

struct EditorWindow;

/* An instance of any format. */
struct Plugin {
    virtual ~Plugin();

    int32_t format = 0;
    std::string id, name, vendor, version, category;
    bool instrument = false;
    bool sidechainCapable = false;
    bool customEditor = false;
    std::vector<ParamInfo> params;
    std::unique_ptr<std::atomic<double>[]> values;
    std::unique_ptr<std::atomic<bool>[]> controllerDirty;
    std::atomic<bool> anyControllerDirty{false};
    std::atomic<int32_t> lastLatency{0};
    an_plug_event_cb callback = nullptr;
    void *callbackUser = nullptr;
    EditorWindow *editor = nullptr;
    bool active = false;

    /* Sizes the value arrays once `params` is known. */
    void initValues();
    void emit(int32_t kind, int32_t index, double value) const {
        if (callback) {
            callback(callbackUser, kind, index, value);
        }
    }

    virtual int32_t activate(double sampleRate, int32_t maxFrames, bool sidechain) = 0;
    virtual void deactivate() = 0;
    virtual int32_t process(const float *const *in, const float *const *sc, float *const *out, int32_t frames,
                            const an_plug_midi *midi, int32_t midiCount) = 0;
    virtual void reset() = 0;
    virtual void setParam(int32_t index, double value) = 0;
    virtual int32_t latency() = 0;
    virtual bool saveState(std::vector<uint8_t> &out) = 0;
    virtual bool loadState(const uint8_t *data, size_t size) = 0;
    /* Main thread: controller sync, latency changes. */
    virtual void idle();
    /* Editor support (macOS): puts the plugin's view into `container` (an NSView), giving its size (and resizing
     * the window through editor_resize first when it can); detachEditor takes it out again. Main thread. */
    virtual bool attachEditor(void *container, int32_t &width, int32_t &height) = 0;
    virtual void detachEditor() = 0;

    std::string describe(int32_t latencySamples) const;
};

/* Format entry points. */
int32_t vst3_scan(const char *path, bool instantiate, std::string &json);
int32_t vst3_open(const char *path, const char *id, Plugin **plugin);
int32_t au_scan(const char *path, bool instantiate, std::string &json);
int32_t au_open(const char *id, Plugin **plugin);

/* Editor windows (an_plug_editor_mac.mm). */
int32_t editor_open(Plugin *plugin, const char *title, int64_t parent, int32_t x, int32_t y, int32_t flags);
void editor_close(Plugin *plugin);
int32_t editor_window(Plugin *plugin);
/* Resizes the open editor's window to fit a view of width x height (VST3 resizeView). */
void editor_resize(Plugin *plugin, int32_t width, int32_t height);

/* A minimal JSON writer. */
class Json {
public:
    Json &beginObject();
    Json &endObject();
    Json &beginArray();
    Json &endArray();
    Json &key(const char *k);
    Json &value(const std::string &s);
    Json &value(const char *s);
    Json &value(double d);
    Json &value(int32_t i);
    Json &value(bool b);
    Json &raw(const std::string &s);
    const std::string &str() const { return out; }

private:
    void sep();
    std::string out;
    std::vector<bool> first;
    bool afterKey = false;
};

/* Maps a VST3 sub-category string or an AU subtype/name to Anode's effect categories. */
std::string category_from_vst3(const std::string &subCategories, bool &instrument);
std::string category_from_words(const std::string &text);

} // namespace anplug

struct an_plug {
    anplug::Plugin *impl;
};
