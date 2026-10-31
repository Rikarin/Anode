/*
 * cth_audio: the C ABI between Cathode's managed engine and the platform audio APIs.
 * SPDX-License-Identifier: GPL-3.0-or-later
 *
 * The OS driver owns the callback thread (CoreAudio, JACK), or the shim runs its own real-time thread (ALSA). The shim
 * deinterleaves device buffers into planar float channels, enables flush-to-zero for the thread, and calls the
 * registered process function.
 */
#ifndef CTH_AUDIO_H
#define CTH_AUDIO_H

#include <stdint.h>

#if defined(_WIN32)
#define CTH_API __declspec(dllexport)
#else
#define CTH_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Largest callback the shim will deliver; bigger device buffers are split. */
#define CTH_MAX_FRAMES 4096

typedef struct cth_stream cth_stream;

/* Planar callback. Output channels arrive zeroed. Runs on the real-time thread. */
typedef void (*cth_process_fn)(void *user, const float *const *inputs, int32_t input_channels,
                               float *const *outputs, int32_t output_channels, int32_t frames);

typedef struct cth_stream_config {
    double sample_rate;      /* in: requested rate, 0 keeps the device rate; out: actual */
    int32_t buffer_frames;   /* in: requested buffer size; out: actual */
    int32_t input_channels;  /* in: wanted input channels (0 = none); out: actual */
    int32_t output_channels; /* out: device output channels */
} cth_stream_config;

typedef struct cth_stream_stats {
    uint64_t callbacks;
    uint64_t overloads;            /* driver-reported overloads (dropouts) */
    int32_t input_latency_frames;  /* device + safety offset + stream latency + buffer */
    int32_t output_latency_frames;
} cth_stream_stats;

/* Wall-clock time of each callback measured around the whole process call, including any wait to enter the
 * managed runtime (for example while a GC is suspending managed threads). 1 % bins of the buffer period;
 * the last bin collects everything at or above 200 %. */
#define CTH_TIMING_BINS 201

typedef struct cth_stream_timing {
    uint64_t bins[CTH_TIMING_BINS];
    uint64_t over_budget; /* callbacks that took longer than their buffer period */
    double max_percent;
} cth_stream_timing;

typedef struct cth_device_info {
    char uid[256];
    char name[256];
    int32_t input_channels;
    int32_t output_channels;
    double sample_rate;
} cth_device_info;

/* 0 on success, negative on failure (see cth_audio_last_error). */
CTH_API int32_t cth_audio_device_count(void);
CTH_API int32_t cth_audio_device_info(int32_t index, cth_device_info *info);
/* The driver's name for one channel of a device (channel 0-based; input or output side), for labelling an input
 * map: a user label from Audio MIDI Setup or the driver's own ("Mic/Inst 1"). Writes an empty string when the
 * driver names none. 0 on success. */
CTH_API int32_t cth_audio_channel_name(int32_t device_index, int32_t is_input, int32_t channel, char *name,
                                       int32_t size);
CTH_API int32_t cth_audio_open(const char *device_uid, cth_stream_config *config,
                               cth_process_fn process, void *user, cth_stream **stream);
CTH_API int32_t cth_audio_start(cth_stream *stream);
CTH_API int32_t cth_audio_stop(cth_stream *stream);
CTH_API void cth_audio_close(cth_stream *stream);
CTH_API void cth_audio_stats(const cth_stream *stream, cth_stream_stats *stats);
CTH_API void cth_audio_timing(const cth_stream *stream, cth_stream_timing *timing);
CTH_API void cth_audio_reset_timing(cth_stream *stream);

/* Device health, polled by the host (docs/adr/0002-native-audio-backend.md). The backend raises it when something
 * outside the stream invalidates it; it never goes back down, and LOST outranks CHANGED. Either way the stream will
 * not recover by itself: the host reopens it (it exits and its supervisor restarts it). */
#define CTH_STREAM_OK 0
/* Another application changed the device's sample rate, buffer size or format, or (for a stream opened with a NULL
 * uid) the default device changed: the stream runs on, but at settings the engine was not prepared for. */
#define CTH_STREAM_CHANGED 1
/* The device was unplugged, disabled or taken away: the callback no longer runs. */
#define CTH_STREAM_LOST 2
CTH_API int32_t cth_audio_status(const cth_stream *stream);

CTH_API const char *cth_audio_backend(void);
CTH_API const char *cth_audio_last_error(void);

/* ---- MIDI (docs/02-architecture.md 5.10) ----------------------------------------------------------------------
 * cth_midi_open connects every MIDI input source; incoming messages are timestamped by the OS and queued in a
 * lock-free ring that cth_midi_read drains (from one thread). Times are nanoseconds on the cth_midi_now_ns clock.
 * Channel messages, system common (MTC quarter frame, song position, song select, tune request) and system real-time
 * (clock, start, stop) are delivered there; SysEx goes to a separate ring that cth_midi_read_sysex drains (Anode). */

typedef struct cth_midi_port {
    char uid[64];   /* stable across runs (CoreMIDI unique id; WinMM "in:"/"out:" + name + "#n";
                       "<client name>:<port>" on the ALSA sequencer) */
    char name[256];
    int32_t is_input;
} cth_midi_port;

typedef struct cth_midi_event {
    uint64_t time_ns;
    uint8_t data[3];
    uint8_t length;
    int32_t source; /* the input's index as cth_midi_port_info numbered it at the last cth_midi_open; -1 unknown */
} cth_midi_event;

CTH_API int32_t cth_midi_port_count(void);
CTH_API int32_t cth_midi_port_info(int32_t index, cth_midi_port *port);
/* 0 on success. Connects all current sources; call again to pick up devices plugged in since. */
CTH_API int32_t cth_midi_open(void);
/* Copies up to max queued events into events; returns how many. Events that overflowed the ring are lost. */
CTH_API int32_t cth_midi_read(cth_midi_event *events, int32_t max);
/* Copies the oldest queued SysEx message (F0 … F7, whole) into buffer and returns its length, with its arrival time
 * and source as cth_midi_event has them; 0 when none is queued; -(its length) when capacity is too small, leaving it
 * queued. One reader, as cth_midi_read. Messages longer than CTH_MIDI_SYSEX_MAX bytes, and messages that overflow the
 * ring, are lost. (Anode Phase 8: Mackie Control's and HUI's SysEx, and MTC full frames.) */
#define CTH_MIDI_SYSEX_MAX 16384
CTH_API int32_t cth_midi_read_sysex(uint8_t *buffer, int32_t capacity, uint64_t *time_ns, int32_t *source);
/* Sends to the destination with this uid, or to every destination when uid is NULL: one or more whole messages
 * (channel, system common, real-time, or SysEx F0 … F7, without running status) of any total length. Returns 0 when
 * a destination took them, negative when none did or the bytes are not whole messages. (Before Anode Phase 8: one
 * short message of 1–3 bytes, which is still what length 1–3 means.) */
CTH_API int32_t cth_midi_send(const char *destination_uid, const uint8_t *data, int32_t length);
/* As cth_midi_send, delivered at time_ns on the cth_midi_now_ns clock (0 or a past time: now). CoreMIDI schedules it;
 * the other backends send at once, so their callers hold an event until it is due. Returns 1 when the backend
 * scheduled it, 0 when it sent it at once, negative on failure. */
CTH_API int32_t cth_midi_send_at(const char *destination_uid, const uint8_t *data, int32_t length, uint64_t time_ns);
CTH_API void cth_midi_close(void);
CTH_API uint64_t cth_midi_now_ns(void);

/* ---- Virtual MIDI ports (Anode Phase 8, docs/09-phase-8.md) ---------------------------------------------------
 * A virtual port is one this process publishes for other apps (CoreMIDI MIDIDestinationCreate / MIDISourceCreate):
 *   - an input (is_input 1) is a destination other apps send to; its messages enter the cth_midi_read ring like any
 *     source's, timestamped the same way, with source the port's index as cth_midi_port_info numbers it;
 *   - an output (is_input 0) is a source other apps receive from; cth_midi_send and cth_midi_send_at reach it by uid.
 * name is what other apps show ("Anode In", "Anode Out"); uid is this port's uid in cth_midi_port_info (Anode's are
 * "anode:in" and "anode:out"; at most 63 bytes), stable across runs. The port is listed by cth_midi_port_count and
 * cth_midi_port_info from the next cth_midi_open (which connects a virtual input as it does sources) until it is closed.
 * Another process using cth_audio lists it under the same uid, from its side (CoreMIDI: the uid is a property of the
 * endpoint): "anode:in" as an output it can send to, "anode:out" as an input it hears.
 * Returns 0 on success; CTH_MIDI_VIRTUAL_UNSUPPORTED where the backend has no virtual ports (WinMM); another negative
 * value on failure (a uid already open, CoreMIDI refusing), with cth_audio_last_error saying why. */
#define CTH_MIDI_VIRTUAL_UNSUPPORTED (-2)

CTH_API int32_t cth_midi_virtual_open(const char *name, const char *uid, int32_t is_input);
/* Removes the virtual port with this uid (other apps see it disappear); 0, or negative when none is open. Ports still
 * open at cth_midi_close are closed with it. */
CTH_API int32_t cth_midi_virtual_close(const char *uid);

/* Enables flush-to-zero / denormals-are-zero on the calling thread (x86 MXCSR, ARM64 FPCR.FZ). */
CTH_API void cth_fpu_flush_denormals(void);

#ifdef __cplusplus
}
#endif

#endif
