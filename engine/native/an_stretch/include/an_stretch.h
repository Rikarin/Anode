/*
 * an_stretch: time stretch and pitch shift behind a C ABI, for Anode's managed engine (spike S5,
 * docs/spikes/S5-time-stretch.md). Wraps Signalsmith Stretch (MIT). SPDX-License-Identifier: GPL-3.0-or-later
 *
 * Audio is planar float: input[channel][frame]. Input and output buffers must not alias.
 *
 * Threading: create, configure and destroy run on the control thread and allocate. Everything else may run on the
 * real-time thread: it does not allocate once created (the library only resizes vectors within their capacity), but a
 * call that crosses a spectral block boundary does a whole block's FFT work unless split computation is on.
 */
#ifndef AN_STRETCH_H
#define AN_STRETCH_H

#include <stdint.h>

#if defined(_WIN32)
#define AN_STRETCH_API __declspec(dllexport)
#else
#define AN_STRETCH_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct an_stretch an_stretch;

typedef enum an_stretch_preset {
    AN_STRETCH_PRESET_DEFAULT = 0, /* 120 ms block, 30 ms interval */
    AN_STRETCH_PRESET_CHEAPER = 1  /* 100 ms block, 40 ms interval */
} an_stretch_preset;

/* A preset. split_computation spreads each block's work over the following interval, for one more interval of output
 * latency. The random seed is fixed, so renders are reproducible. 0 on success. */
AN_STRETCH_API int32_t an_stretch_create(int32_t channels, double sample_rate, int32_t preset,
                                         int32_t split_computation, an_stretch **stretch);

/* Manual block and interval sizes, in frames. 0 on success. */
AN_STRETCH_API int32_t an_stretch_create_custom(int32_t channels, double sample_rate, int32_t block_frames,
                                                int32_t interval_frames, int32_t split_computation,
                                                an_stretch **stretch);

AN_STRETCH_API void an_stretch_destroy(an_stretch *stretch);

/* Clears all internal state; the configuration and the pitch/formant settings are kept. */
AN_STRETCH_API void an_stretch_reset(an_stretch *stretch);

/* Pitch: a frequency factor (2 = up an octave) or semitones. tonality_limit_hz > 0 keeps the timbre above that
 * frequency closer to the input (a non-linear frequency map); 0 is a plain transposition. */
AN_STRETCH_API void an_stretch_set_transpose_factor(an_stretch *stretch, double factor, double tonality_limit_hz);
AN_STRETCH_API void an_stretch_set_transpose_semitones(an_stretch *stretch, double semitones,
                                                       double tonality_limit_hz);

/* Formants: a factor (1 = unchanged). With compensate_pitch, the formants also stay put under a transposition, so
 * factor 1 plus compensate_pitch is "pitch shift with formant preservation". base_hz is a rough fundamental for the
 * formant analysis (0 = estimate it). */
AN_STRETCH_API void an_stretch_set_formant_factor(an_stretch *stretch, double factor, int32_t compensate_pitch);
AN_STRETCH_API void an_stretch_set_formant_base(an_stretch *stretch, double base_hz);

/* Streaming: consumes input_frames, produces output_frames; their ratio is the playback rate for this call. */
AN_STRETCH_API void an_stretch_process(an_stretch *stretch, const float *const *input, int32_t input_frames,
                                       float *const *output, int32_t output_frames);

/* Moves the read position without producing output: feed an_stretch_seek_length() frames ending at the new position.
 * playback_rate (input frames per output frame) is a hint for the first block. */
AN_STRETCH_API void an_stretch_seek(an_stretch *stretch, const float *const *input, int32_t input_frames,
                                    double playback_rate);
AN_STRETCH_API int32_t an_stretch_seek_length(const an_stretch *stretch);

/* Starts a sound so the next output frame is aligned to its first input frame: resets, then consumes input_frames
 * (an_stretch_output_seek_length(rate) of them) and pre-computes the pre-roll. */
AN_STRETCH_API void an_stretch_output_seek(an_stretch *stretch, const float *const *input, int32_t input_frames);
AN_STRETCH_API int32_t an_stretch_output_seek_length(const an_stretch *stretch, double playback_rate);

/* Ends a sound: reads output_frames more (at least the output latency), assuming silent input. */
AN_STRETCH_API void an_stretch_flush(an_stretch *stretch, float *const *output, int32_t output_frames,
                                     double playback_rate);

/* Offline: stretches a whole buffer to exactly output_frames, aligned (outputSeek + process + flush). 0 on success,
 * -1 when the input is shorter than one seek length (the output is then zeroed). Not real-time safe. */
AN_STRETCH_API int32_t an_stretch_exact(an_stretch *stretch, const float *const *input, int32_t input_frames,
                                        float *const *output, int32_t output_frames);

/* Latency, in frames: input must be supplied input_latency ahead of the processing time, and output comes
 * output_latency behind it. */
AN_STRETCH_API int32_t an_stretch_input_latency(const an_stretch *stretch);
AN_STRETCH_API int32_t an_stretch_output_latency(const an_stretch *stretch);
AN_STRETCH_API int32_t an_stretch_block_frames(const an_stretch *stretch);
AN_STRETCH_API int32_t an_stretch_interval_frames(const an_stretch *stretch);

/* The pinned upstream release, e.g. "signalsmith-stretch 1.4.0+88061fd (linear 0.6.6, accelerate)". */
AN_STRETCH_API const char *an_stretch_version(void);
AN_STRETCH_API const char *an_stretch_last_error(void);

#ifdef __cplusplus
}
#endif

#endif
