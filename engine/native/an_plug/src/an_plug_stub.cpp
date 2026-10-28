/*
 * an_plug off macOS: no editor windows and no AU yet; the main loop is a plain wait. GPL-3.0-or-later
 */
#include "an_plug_internal.h"

#include <condition_variable>
#include <deque>
#include <functional>
#include <mutex>

namespace anplug {

int32_t editor_open(Plugin *, const char *, int64_t, int32_t, int32_t, int32_t) {
    return fail("plugin editor windows are not implemented on this platform yet");
}
void editor_close(Plugin *) {}
int32_t editor_window(Plugin *) { return 0; }
void editor_resize(Plugin *, int32_t, int32_t) {}
int32_t au_scan(const char *, bool, std::string &) { return fail("Audio Units are macOS only"); }
int32_t au_open(const char *, Plugin **) { return fail("Audio Units are macOS only"); }

namespace {
std::mutex gate;
std::condition_variable wake;
std::deque<std::function<void()>> work;
bool quit = false;
} // namespace

} // namespace anplug

using namespace anplug;

extern "C" {

void an_plug_main_run(void) {
    std::unique_lock<std::mutex> lock(gate);
    while (!quit) {
        wake.wait(lock, [] { return quit || !work.empty(); });
        while (!work.empty()) {
            auto item = std::move(work.front());
            work.pop_front();
            lock.unlock();
            item();
            lock.lock();
        }
    }
}

void an_plug_main_quit(void) {
    std::lock_guard<std::mutex> lock(gate);
    quit = true;
    wake.notify_all();
}

void an_plug_main_call(void (*fn)(void *), void *ctx) {
    std::mutex doneGate;
    std::condition_variable doneWake;
    bool done = false;
    {
        std::lock_guard<std::mutex> lock(gate);
        work.emplace_back([&] {
            fn(ctx);
            std::lock_guard<std::mutex> l(doneGate);
            done = true;
            doneWake.notify_all();
        });
        wake.notify_all();
    }
    std::unique_lock<std::mutex> l(doneGate);
    doneWake.wait(l, [&] { return done; });
}

int32_t an_plug_window_info(int64_t, int32_t *, int32_t *, int32_t *, int32_t *, int32_t *, int32_t *, int32_t *) {
    return -1;
}

} // extern "C"
