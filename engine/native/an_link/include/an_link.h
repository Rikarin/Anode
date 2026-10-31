/*
 * an_link: Ableton Link behind a C ABI for Anode's engine host (docs/09-phase-8.md, "Sync"). A thin wrapper over the
 * subset of the Link SDK's own C API (abl_link.h) that Anode needs, with Anode's names; built with the Link SDK, pinned
 * and fetched by engine/native/build.sh (workstream A). Link is GPL-2.0-or-later (ADR-0008).
 * SPDX-License-Identifier: GPL-3.0-or-later
 *
 * Declaration only: workstream A adds src/an_link.cpp and the CMake target.
 *
 * Times are microseconds on Link's clock (an_link_clock_micros), which on macOS is mach_absolute_time converted and on
 * Windows the performance counter: the host converts a buffer's output host time to it. Beats and phases are at a
 * quantum (beats per cycle; Anode passes the bar at the playhead, numerator × 4 / denominator).
 *
 * Threading, as Link's: the audio thread captures and commits the audio session state (an_link_capture_audio_session_state,
 * an_link_commit_audio_session_state), which neither allocates nor blocks; every other thread uses the app session state.
 * A session state is a value the caller owns: capture copies the link's into it, commit publishes it. create and
 * destroy run on a control thread.
 */
#ifndef AN_LINK_H
#define AN_LINK_H

#include <stdbool.h>
#include <stdint.h>

#if defined(_WIN32)
#define AN_LINK_API __declspec(dllexport)
#else
#define AN_LINK_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct an_link an_link;
typedef struct an_link_session_state an_link_session_state;

/* A Link instance at bpm (disabled: it joins no session until an_link_enable). NULL when it cannot be created. */
AN_LINK_API an_link *an_link_create(double bpm);
AN_LINK_API void an_link_destroy(an_link *link);

/* Joins (true) or leaves (false) the network session, and whether it has. */
AN_LINK_API void an_link_enable(an_link *link, bool enable);
AN_LINK_API bool an_link_is_enabled(an_link *link);

/* Whether start/stop is shared with peers that also enable it. */
AN_LINK_API void an_link_enable_start_stop_sync(an_link *link, bool enable);
AN_LINK_API bool an_link_is_start_stop_sync_enabled(an_link *link);

/* The number of other peers in the session. */
AN_LINK_API uint64_t an_link_num_peers(an_link *link);

/* Now, in microseconds on Link's clock. */
AN_LINK_API int64_t an_link_clock_micros(an_link *link);

/* A session state value for the capture and commit calls (create and destroy off the audio thread). */
AN_LINK_API an_link_session_state *an_link_create_session_state(void);
AN_LINK_API void an_link_destroy_session_state(an_link_session_state *state);

/* The audio thread's capture and commit: real-time safe. */
AN_LINK_API void an_link_capture_audio_session_state(an_link *link, an_link_session_state *state);
AN_LINK_API void an_link_commit_audio_session_state(an_link *link, an_link_session_state *state);

/* Any other thread's capture and commit. */
AN_LINK_API void an_link_capture_app_session_state(an_link *link, an_link_session_state *state);
AN_LINK_API void an_link_commit_app_session_state(an_link *link, an_link_session_state *state);

/* The session's tempo, and a change of it from time on (committed with the state). */
AN_LINK_API double an_link_tempo(an_link_session_state *state);
AN_LINK_API void an_link_set_tempo(an_link_session_state *state, double bpm, int64_t time);

/* The beat at time, and its phase in [0, quantum). */
AN_LINK_API double an_link_beat_at_time(an_link_session_state *state, int64_t time, double quantum);
AN_LINK_API double an_link_phase_at_time(an_link_session_state *state, int64_t time, double quantum);

/* The time of beat (at the quantum's phase). */
AN_LINK_API int64_t an_link_time_at_beat(an_link_session_state *state, double beat, double quantum);

/* Asks for beat to fall at time, honouring the session's phase at the quantum (with peers, the beat moves to the next
 * time it is in phase). */
AN_LINK_API void an_link_request_beat_at_time(an_link_session_state *state, double beat, int64_t time, double quantum);

/* Start/stop: whether the session is playing, and a change of it at time (shared when start/stop sync is on). */
AN_LINK_API bool an_link_is_playing(an_link_session_state *state);
AN_LINK_API void an_link_set_is_playing(an_link_session_state *state, bool is_playing, int64_t time);

/* Starts or stops at time and asks for beat at that time, in one change (Link's set_is_playing_and_request_beat_at_time):
 * how the host starts the session on Anode's play. */
AN_LINK_API void an_link_set_is_playing_and_request_beat_at_time(an_link_session_state *state, bool is_playing,
                                                                 int64_t time, double beat, double quantum);

/* The pinned upstream: "Link 3.x.y+<commit>". */
AN_LINK_API const char *an_link_version(void);

#ifdef __cplusplus
}
#endif

#endif
