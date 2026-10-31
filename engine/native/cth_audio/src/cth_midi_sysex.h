/* SPDX-License-Identifier: GPL-3.0-or-later
 * SysEx helpers shared by the MIDI backends (Anode Phase 8; see cth_midi_read_sysex and cth_midi_send in cth_audio.h):
 *   - a ring of variable-length records (header: length, source, time; then the bytes), which the caller guards with
 *     its own lock (producers are the backend's input threads, the consumer the one reader; neither is real-time);
 *   - an assembler per input that collects a SysEx arriving in pieces;
 *   - a parser that splits a byte string into whole messages for cth_midi_send.
 * Header-only (static functions) so each backend compiles it with its own locks and flags. */
#ifndef CTH_MIDI_SYSEX_H
#define CTH_MIDI_SYSEX_H

#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include "cth_audio.h"

/* Sets cth_audio_last_error (each audio backend defines it). */
void cth_set_last_error(const char *text);

#define CTH_SYSEX_RING_SIZE 65536u /* power of two */
#define CTH_SYSEX_HEADER 16u

typedef struct cth_sysex_ring {
    uint8_t data[CTH_SYSEX_RING_SIZE];
    uint32_t head; /* bytes written, modulo 2^32 */
    uint32_t tail; /* bytes read */
} cth_sysex_ring;

static inline void cth_sysex_ring_write(cth_sysex_ring *ring, uint32_t at, const void *bytes, uint32_t length)
{
    const uint8_t *from = (const uint8_t *)bytes;
    for (uint32_t i = 0; i < length; i++) ring->data[(at + i) & (CTH_SYSEX_RING_SIZE - 1)] = from[i];
}

static inline void cth_sysex_ring_read(const cth_sysex_ring *ring, uint32_t at, void *bytes, uint32_t length)
{
    uint8_t *to = (uint8_t *)bytes;
    for (uint32_t i = 0; i < length; i++) to[i] = ring->data[(at + i) & (CTH_SYSEX_RING_SIZE - 1)];
}

/* Queues one message; drops it (returns 0) when the ring has no room. Caller holds the lock. */
static inline int cth_sysex_ring_push(cth_sysex_ring *ring, uint64_t time_ns, int32_t source, const uint8_t *bytes,
                               uint32_t length)
{
    if (length == 0 || length > CTH_MIDI_SYSEX_MAX) return 0;
    if (CTH_SYSEX_RING_SIZE - (ring->head - ring->tail) < CTH_SYSEX_HEADER + length) return 0;
    uint8_t header[CTH_SYSEX_HEADER];
    memcpy(header, &length, 4);
    memcpy(header + 4, &source, 4);
    memcpy(header + 8, &time_ns, 8);
    cth_sysex_ring_write(ring, ring->head, header, CTH_SYSEX_HEADER);
    cth_sysex_ring_write(ring, ring->head + CTH_SYSEX_HEADER, bytes, length);
    ring->head += CTH_SYSEX_HEADER + length;
    return 1;
}

/* cth_midi_read_sysex's contract. Caller holds the lock. */
static inline int32_t cth_sysex_ring_pop(cth_sysex_ring *ring, uint8_t *buffer, int32_t capacity, uint64_t *time_ns,
                                  int32_t *source)
{
    if (ring->head == ring->tail) return 0;
    uint8_t header[CTH_SYSEX_HEADER];
    uint32_t length;
    int32_t from;
    uint64_t time;
    cth_sysex_ring_read(ring, ring->tail, header, CTH_SYSEX_HEADER);
    memcpy(&length, header, 4);
    memcpy(&from, header + 4, 4);
    memcpy(&time, header + 8, 8);
    if (!buffer || capacity < (int32_t)length) return -(int32_t)length;
    cth_sysex_ring_read(ring, ring->tail + CTH_SYSEX_HEADER, buffer, length);
    ring->tail += CTH_SYSEX_HEADER + length;
    if (time_ns) *time_ns = time;
    if (source) *source = from;
    return (int32_t)length;
}

/* One input's SysEx in progress: F0, the bytes so far; finished by F7. The buffer is allocated on first use. */
typedef struct cth_sysex_assembly {
    uint8_t *data;
    uint32_t length;
    int active;    /* an F0 was seen and no F7 yet */
    int overflow;  /* longer than CTH_MIDI_SYSEX_MAX: dropped at its end */
} cth_sysex_assembly;

static inline void cth_sysex_begin(cth_sysex_assembly *a)
{
    if (!a->data) a->data = (uint8_t *)malloc(CTH_MIDI_SYSEX_MAX);
    a->length = 0;
    a->overflow = !a->data;
    a->active = 1;
    if (a->data) a->data[a->length++] = 0xF0;
}

static inline void cth_sysex_append(cth_sysex_assembly *a, const uint8_t *bytes, uint32_t count)
{
    if (!a->active || a->overflow) return;
    if (a->length + count > CTH_MIDI_SYSEX_MAX - 1) {
        a->overflow = 1;
        return;
    }
    memcpy(a->data + a->length, bytes, count);
    a->length += count;
}

/* Ends the message with F7; returns its length (0 when there is nothing whole to queue). */
static inline uint32_t cth_sysex_end(cth_sysex_assembly *a)
{
    if (!a->active) return 0;
    a->active = 0;
    if (a->overflow || !a->data) return 0;
    a->data[a->length++] = 0xF7;
    return a->length;
}

static inline void cth_sysex_free(cth_sysex_assembly *a)
{
    free(a->data);
    a->data = NULL;
    a->length = 0;
    a->active = 0;
}

/* The length of the whole message at data[0] (which must be a status byte) within length bytes, or 0 when it is not
 * one: channel messages by their status, system common, real-time, and a SysEx through its F7. */
static inline int32_t cth_midi_message_length(const uint8_t *data, int32_t length)
{
    if (length < 1) return 0;
    uint8_t status = data[0];
    int32_t need;
    if (status < 0x80) return 0;
    if (status < 0xF0) need = ((status & 0xF0) == 0xC0 || (status & 0xF0) == 0xD0) ? 2 : 3;
    else if (status == 0xF0) {
        for (int32_t i = 1; i < length; i++) {
            if (data[i] == 0xF7) return i + 1;
            if (data[i] >= 0x80) return 0; /* a status byte inside a SysEx: not whole */
        }
        return 0;
    } else if (status == 0xF1 || status == 0xF3) need = 2;
    else if (status == 0xF2) need = 3;
    else if (status == 0xF7) return 0;
    else need = 1; /* F4, F5 (undefined), F6, real-time */
    if (need > length) {
        /* Before Phase 8 a short message could be sent with fewer data bytes than its status implies (a two-byte
         * note, say); keep accepting a lone message that way, as the backends always did. */
        return need > 1 && length >= 1 && length <= 3 && status < 0xF0 ? length : 0;
    }
    for (int32_t i = 1; i < need; i++) {
        if (data[i] >= 0x80) return 0;
    }
    return need;
}

#endif
