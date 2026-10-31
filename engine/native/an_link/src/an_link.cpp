/* SPDX-License-Identifier: GPL-3.0-or-later
 * an_link (see include/an_link.h): Anode's names over the Link SDK's C extension, abl_link. An an_link and an
 * an_link_session_state are the abl_link handles themselves (the pointer to Link's implementation inside them), so
 * the wrappers add no state and no allocation of their own. */
#include "an_link.h"

#include <abl_link.h>

namespace {

abl_link Link(an_link *link) { return abl_link{link}; }

abl_link_session_state State(an_link_session_state *state) { return abl_link_session_state{state}; }

} // namespace

extern "C" {

an_link *an_link_create(double bpm)
{
    if (!(bpm >= 20.0 && bpm <= 999.0)) return nullptr;
    return static_cast<an_link *>(abl_link_create(bpm).impl);
}

void an_link_destroy(an_link *link)
{
    if (link) abl_link_destroy(Link(link));
}

void an_link_enable(an_link *link, bool enable) { abl_link_enable(Link(link), enable); }

bool an_link_is_enabled(an_link *link) { return abl_link_is_enabled(Link(link)); }

void an_link_enable_start_stop_sync(an_link *link, bool enable) { abl_link_enable_start_stop_sync(Link(link), enable); }

bool an_link_is_start_stop_sync_enabled(an_link *link) { return abl_link_is_start_stop_sync_enabled(Link(link)); }

uint64_t an_link_num_peers(an_link *link) { return abl_link_num_peers(Link(link)); }

int64_t an_link_clock_micros(an_link *link) { return abl_link_clock_micros(Link(link)); }

an_link_session_state *an_link_create_session_state(void)
{
    return static_cast<an_link_session_state *>(abl_link_create_session_state().impl);
}

void an_link_destroy_session_state(an_link_session_state *state)
{
    if (state) abl_link_destroy_session_state(State(state));
}

void an_link_capture_audio_session_state(an_link *link, an_link_session_state *state)
{
    abl_link_capture_audio_session_state(Link(link), State(state));
}

void an_link_commit_audio_session_state(an_link *link, an_link_session_state *state)
{
    abl_link_commit_audio_session_state(Link(link), State(state));
}

void an_link_capture_app_session_state(an_link *link, an_link_session_state *state)
{
    abl_link_capture_app_session_state(Link(link), State(state));
}

void an_link_commit_app_session_state(an_link *link, an_link_session_state *state)
{
    abl_link_commit_app_session_state(Link(link), State(state));
}

double an_link_tempo(an_link_session_state *state) { return abl_link_tempo(State(state)); }

void an_link_set_tempo(an_link_session_state *state, double bpm, int64_t time)
{
    abl_link_set_tempo(State(state), bpm, time);
}

double an_link_beat_at_time(an_link_session_state *state, int64_t time, double quantum)
{
    return abl_link_beat_at_time(State(state), time, quantum);
}

double an_link_phase_at_time(an_link_session_state *state, int64_t time, double quantum)
{
    return abl_link_phase_at_time(State(state), time, quantum);
}

int64_t an_link_time_at_beat(an_link_session_state *state, double beat, double quantum)
{
    return abl_link_time_at_beat(State(state), beat, quantum);
}

void an_link_request_beat_at_time(an_link_session_state *state, double beat, int64_t time, double quantum)
{
    abl_link_request_beat_at_time(State(state), beat, time, quantum);
}

bool an_link_is_playing(an_link_session_state *state) { return abl_link_is_playing(State(state)); }

void an_link_set_is_playing(an_link_session_state *state, bool is_playing, int64_t time)
{
    abl_link_set_is_playing(State(state), is_playing, time);
}

void an_link_set_is_playing_and_request_beat_at_time(an_link_session_state *state, bool is_playing, int64_t time,
                                                     double beat, double quantum)
{
    abl_link_set_is_playing_and_request_beat_at_time(State(state), is_playing, time, beat, quantum);
}

const char *an_link_version(void) { return AN_LINK_UPSTREAM_VERSION; }

} // extern "C"
