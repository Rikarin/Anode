/*
 * an_plug: a C ABI over third-party plugin formats, for the anode-plugin sandbox and the plugin scanner
 * (docs/adr/0003-plugin-hosting.md, docs/05-phase-4.md workstream E).
 * SPDX-License-Identifier: GPL-3.0-or-later
 *
 * Formats: VST3 (the Steinberg VST3 SDK, MIT, pinned in CMakeLists.txt) and, on macOS, Audio Units v2 and v3
 * (AudioToolbox). CLAP stays in C#: see docs/05-phase-4.md and Anode.Plugins.
 *
 * Model. A plugin instance is opened from a file (a .vst3 bundle) or a component (AU) and a class id, activated for a
 * sample rate, a maximum block and an optional sidechain, and processed on one audio thread. Every instance is stereo:
 * a main stereo input (absent for instruments), an optional stereo sidechain, a stereo output. Parameters are numbered
 * 0..n-1 by an_plug (hidden and bypass parameters left out) and carry their value in the plugin's own range
 * (`min`..`max` from an_plug_describe; VST3 values are the normalised 0..1).
 *
 * Threads. an_plug_open, _describe, _activate, _deactivate, _state_*, _editor_* and _close are "main thread" calls:
 * the sandbox makes them on the process's main thread (an_plug_main_run, an_plug_main_call), as VST3 and AU expect.
 * an_plug_process, an_plug_param_set and an_plug_reset are audio-thread calls on one thread at a time; they do not
 * allocate (beyond what the plugin itself does) or lock. an_plug_param_get and an_plug_latency are safe anywhere.
 *
 * Functions return 0 on success and a negative value on failure, with the reason in an_plug_last_error (per thread),
 * unless documented otherwise.
 */
#ifndef AN_PLUG_H
#define AN_PLUG_H

#include <stdint.h>

#if defined(_WIN32)
#define AN_PLUG_API __declspec(dllexport)
#else
#define AN_PLUG_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define AN_PLUG_VST3 1
#define AN_PLUG_AU 2

typedef struct an_plug an_plug;

/* The reason the last call on this thread failed. */
AN_PLUG_API const char *an_plug_last_error(void);

/* Frees memory an_plug returned (JSON, state). */
AN_PLUG_API void an_plug_free(void *p);

/* 1 when the format can be hosted on this system. */
AN_PLUG_API int32_t an_plug_format_supported(int32_t format);

/* ---- Scanning ------------------------------------------------------------------------------------------------ */

/*
 * Describes plugins as a JSON array (malloc'd, UTF-8; free with an_plug_free):
 *   VST3: every audio-module class in the bundle at `path`.
 *   AU:   path NULL lists every installed effect, music effect and instrument component without instantiating them
 *         (only `id`, `name`, `vendor`, `version`, `isInstrument`); otherwise `path` is one component id
 *         ("aufx.dely.appl") and that component is described.
 * instantiate: 1 opens each class to read its parameters, buses, latency and editor (what the scanner does, in a
 * sandbox, since a plugin may crash); 0 reads only what the factory says.
 * Each element: { id, name, vendor, version, format, category, isInstrument, hasSidechain, hasEditor, latency,
 * parameters: [ { id, name, min, max, default, unit, steps, valueNames? } ] }.
 */
AN_PLUG_API int32_t an_plug_scan(int32_t format, const char *path, int32_t instantiate, char **json);

/* ---- Instances ----------------------------------------------------------------------------------------------- */

/* Opens class `id` (as an_plug_scan reports it) of the file or component at `path` (AU: NULL, the id says it all). */
AN_PLUG_API int32_t an_plug_open(int32_t format, const char *path, const char *id, an_plug **plug);
/* The instance as one element of an_plug_scan's array (malloc'd JSON). */
AN_PLUG_API int32_t an_plug_describe(an_plug *plug, char **json);
/* Number of parameters, as described. */
AN_PLUG_API int32_t an_plug_param_count(an_plug *plug);
/* 1 for an instrument (MIDI in, no main audio input). */
AN_PLUG_API int32_t an_plug_is_instrument(an_plug *plug);

/* Configures stereo buses (plus a stereo sidechain when `sidechain` is 1 and the plugin has one) and starts
 * processing. Deactivate before activating again with other settings. */
AN_PLUG_API int32_t an_plug_activate(an_plug *plug, double sample_rate, int32_t max_frames, int32_t sidechain);
AN_PLUG_API void an_plug_deactivate(an_plug *plug);
/* Deactivates if needed, closes the editor and destroys the instance. */
AN_PLUG_API void an_plug_close(an_plug *plug);

/* A MIDI message `frame` frames into the block. */
typedef struct an_plug_midi {
    int32_t frame;
    uint8_t status;
    uint8_t data1;
    uint8_t data2;
    uint8_t reserved;
} an_plug_midi;

/*
 * Processes `frames` (<= the activated maximum) frames. in: 2 channel pointers (ignored for instruments, may be
 * NULL); sidechain: 2 channel pointers or NULL (silence); out: 2 channel pointers, written. midi: events sorted by
 * frame (instruments; may be NULL). Audio thread. Returns 0, or negative when the instance is not active.
 */
AN_PLUG_API int32_t an_plug_process(an_plug *plug, const float *const *in, const float *const *sidechain,
                                    float *const *out, int32_t frames, const an_plug_midi *midi,
                                    int32_t midi_count);
/* Clears tails (stop, locate). Audio thread, between process calls. */
AN_PLUG_API void an_plug_reset(an_plug *plug);

/* Sets parameter `index` to `value` (its own range) for the next process call. Audio thread (or before activation). */
AN_PLUG_API void an_plug_param_set(an_plug *plug, int32_t index, double value);
/* The parameter's current value (last set by the host, the plugin's editor or the plugin itself). Any thread. */
AN_PLUG_API double an_plug_param_get(an_plug *plug, int32_t index);
/* The latency the plugin reports now, in samples. */
AN_PLUG_API int32_t an_plug_latency(an_plug *plug);

/* The plugin's whole state (component and controller for VST3, ClassInfo for AU), malloc'd. */
AN_PLUG_API int32_t an_plug_state_save(an_plug *plug, uint8_t **data, int64_t *size);
/* Restores a state from an_plug_state_save; parameter values are re-read afterwards. */
AN_PLUG_API int32_t an_plug_state_load(an_plug *plug, const uint8_t *data, int64_t size);

/*
 * Called on the main thread when something other than the host changed the plugin: kind 0, the editor (or the plugin)
 * set parameter `index` to `value`; kind 1, the latency changed (value: samples); kind 2, the editor window closed.
 */
typedef void (*an_plug_event_cb)(void *user, int32_t kind, int32_t index, double value);
AN_PLUG_API void an_plug_set_event_callback(an_plug *plug, an_plug_event_cb callback, void *user);

/* Main thread, periodically (the sandbox does it ~30 times a second): pushes host-set values to the plugin's
 * controller, so its editor shows them, and reports latency changes. */
AN_PLUG_API void an_plug_idle(an_plug *plug);

/* ---- Editor windows (spike S6, docs/spikes/S6-plugin-windows.md) --------------------------------------------- */

/* Editor window flags. */
#define AN_PLUG_EDITOR_FLOATING 1 /* a floating utility panel (above normal windows of every app) */

/* 1 when the plugin has its own editor (AU: a custom Cocoa or v3 view, not the generic one). */
AN_PLUG_API int32_t an_plug_has_editor(an_plug *plug);
/*
 * Opens the plugin's editor in a top-level window owned by this process, titled `title`, its top-left corner at
 * (x, y) in screen points from the top-left of the main screen (x < 0: centred). parent_window: a window number
 * (CGWindowID, possibly another process's) to order the editor directly above, or 0; with x < 0 and a parent the
 * editor goes over the parent's top-left corner. Opening an open editor moves and re-orders it. An AU without its own
 * view gets the generic AU view. Main thread. Returns the editor's window number (> 0), or negative.
 */
AN_PLUG_API int32_t an_plug_editor_open(an_plug *plug, const char *title, int64_t parent_window, int32_t x, int32_t y,
                                        int32_t flags);
AN_PLUG_API void an_plug_editor_close(an_plug *plug);
/* The open editor's window number, or 0. */
AN_PLUG_API int32_t an_plug_editor_window(an_plug *plug);

/* What the window server knows about window `window` (any process; no screen-recording permission needed): its
 * owner's pid, its bounds in points (top-left origin), its layer and whether it is on screen. 0, or -1 if unknown. */
AN_PLUG_API int32_t an_plug_window_info(int64_t window, int32_t *pid, int32_t *x, int32_t *y, int32_t *width,
                                        int32_t *height, int32_t *layer, int32_t *onscreen);

/* The main window of process `pid` (its largest on-screen window at the normal level), or 0: how a sandbox finds
 * the desktop app's window without a handle being passed through the host and the server. */
AN_PLUG_API int32_t an_plug_app_window(int32_t pid);
/* Window `window`'s place in the on-screen order, front to back (0 is frontmost), or -1 when it is not on screen. */
AN_PLUG_API int32_t an_plug_window_z(int64_t window);

/* ---- The sandbox's main thread ------------------------------------------------------------------------------- */

/* Runs the main loop (macOS: an accessory NSApplication, no Dock icon) on the calling thread, which must be the
 * process's main thread, until an_plug_main_quit. */
AN_PLUG_API void an_plug_main_run(void);
AN_PLUG_API void an_plug_main_quit(void);
/* Runs fn(ctx) on the main thread and waits for it (directly when called on the main thread). Needs
 * an_plug_main_run running. */
AN_PLUG_API void an_plug_main_call(void (*fn)(void *), void *ctx);

/* Keeps stdout for the caller: returns a close-on-exec duplicate of descriptor 1 and points descriptor 1 at
 * descriptor 2, so whatever plugins print goes to stderr instead of the sandbox's protocol. */
AN_PLUG_API int32_t an_plug_redirect_stdout(void);

/* Flushes denormals to zero on the calling thread (the sandbox's audio thread). */
AN_PLUG_API void an_plug_flush_denormals(void);

/* Deliberately crashes the process (SIGSEGV), for crash-isolation tests. */
AN_PLUG_API void an_plug_crash(void);

#ifdef __cplusplus
}
#endif

#endif
