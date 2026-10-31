/* SPDX-License-Identifier: GPL-3.0-or-later
 * WinMM MIDI input and output for cth_audio (see cth_audio.h). WinMM numbers ports by position, which shifts when
 * devices come and go, so uids are "in:" or "out:" + the port name + "#n" for the n-th port of that name: stable
 * across runs as long as the same devices are plugged in. Inputs call back on a WinMM thread; events are timestamped
 * there on the QueryPerformanceCounter clock and queued in a ring that cth_midi_read drains. SysEx (Anode Phase 8)
 * arrives in buffers each input keeps queued with WinMM (MIM_LONGDATA), is assembled per input and queued for
 * cth_midi_read_sysex; it goes out with midiOutLongMsg. WinMM has no virtual ports. */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <mmsystem.h>
#include <stdio.h>
#include <string.h>
#include "cth_audio.h"
#include "cth_midi_sysex.h"

#define RING_SIZE 4096 /* power of two */
#define MAX_PORTS 64
#define SYSEX_BUFFERS 4
#define SYSEX_BUFFER_SIZE 1024

static HMIDIIN inputs[MAX_PORTS];
static MIDIHDR sysex_headers[MAX_PORTS][SYSEX_BUFFERS];
static char sysex_buffers[MAX_PORTS][SYSEX_BUFFERS][SYSEX_BUFFER_SIZE];
static cth_sysex_assembly assemblies[MAX_PORTS]; /* under producer_lock */
static volatile LONG closing;                    /* buffers coming back are not queued again */
static SRWLOCK sysex_lock = SRWLOCK_INIT;
static cth_sysex_ring sysex_ring;
static int input_count;
static HMIDIOUT outputs[MAX_PORTS]; /* opened on first send, by WinMM index */
static int opened;
static SRWLOCK ports_lock = SRWLOCK_INIT;   /* the handle tables */
static SRWLOCK producer_lock = SRWLOCK_INIT; /* input callbacks may run on more than one thread */
static cth_midi_event ring[RING_SIZE];
static volatile LONG ring_head; /* written by the WinMM callbacks */
static volatile LONG ring_tail; /* written by the reader */

uint64_t cth_midi_now_ns(void)
{
    static LARGE_INTEGER frequency;
    LARGE_INTEGER now;
    if (frequency.QuadPart == 0) QueryPerformanceFrequency(&frequency);
    QueryPerformanceCounter(&now);
    /* Split to avoid overflowing 64 bits for long uptimes. */
    uint64_t seconds = (uint64_t)now.QuadPart / (uint64_t)frequency.QuadPart;
    uint64_t rest = (uint64_t)now.QuadPart % (uint64_t)frequency.QuadPart;
    return seconds * 1000000000u + rest * 1000000000u / (uint64_t)frequency.QuadPart;
}

static void push(uint64_t time_ns, uint8_t status, uint8_t d1, uint8_t d2, uint8_t length, int32_t source)
{
    AcquireSRWLockExclusive(&producer_lock);
    uint32_t head = (uint32_t)ring_head;
    uint32_t tail = (uint32_t)InterlockedCompareExchange(&ring_tail, 0, 0);
    if (head - tail < RING_SIZE) { /* full: drop */
        cth_midi_event *e = &ring[head & (RING_SIZE - 1)];
        e->time_ns = time_ns;
        e->data[0] = status;
        e->data[1] = d1;
        e->data[2] = d2;
        e->length = length;
        e->source = source;
        InterlockedExchange(&ring_head, (LONG)(head + 1)); /* full barrier: the event is visible first */
    }
    ReleaseSRWLockExclusive(&producer_lock);
}

static uint8_t channel_length(uint8_t status)
{
    switch (status & 0xF0) {
    case 0xC0:
    case 0xD0: return 2;
    default: return 3;
    }
}

/* A buffer of SysEx came back (MIM_LONGDATA): its bytes go to the input's assembly, and it is queued again. */
static void receive_sysex(HMIDIIN handle, int32_t source, MIDIHDR *header, uint64_t time)
{
    const uint8_t *bytes = (const uint8_t *)header->lpData;
    DWORD count = header->dwBytesRecorded;
    if (source >= 0 && source < MAX_PORTS && count > 0) {
        AcquireSRWLockExclusive(&producer_lock);
        cth_sysex_assembly *a = &assemblies[source];
        for (DWORD i = 0; i < count; i++) {
            uint8_t b = bytes[i];
            if (b == 0xF0) {
                cth_sysex_begin(a);
            } else if (b == 0xF7) {
                uint32_t length = cth_sysex_end(a);
                if (length > 0) {
                    AcquireSRWLockExclusive(&sysex_lock);
                    cth_sysex_ring_push(&sysex_ring, time, source, a->data, length);
                    ReleaseSRWLockExclusive(&sysex_lock);
                }
            } else if (b < 0x80) {
                cth_sysex_append(a, &b, 1);
            }
        }
        ReleaseSRWLockExclusive(&producer_lock);
    }

    if (!InterlockedCompareExchange(&closing, 0, 0)) {
        header->dwBytesRecorded = 0;
        midiInAddBuffer(handle, header, sizeof *header);
    }
}

static uint8_t system_length(uint8_t status)
{
    switch (status) {
    case 0xF1:
    case 0xF3: return 2;
    case 0xF2: return 3;
    default: return 1;
    }
}

static void CALLBACK receive(HMIDIIN handle, UINT message, DWORD_PTR instance, DWORD_PTR param1, DWORD_PTR param2)
{
    (void)param2;
    int32_t source = (int32_t)instance; /* the WinMM input index, which is its port index (inputs come first) */
    uint64_t time = cth_midi_now_ns();
    if (message == MIM_LONGDATA) {
        receive_sysex(handle, source, (MIDIHDR *)param1, time);
        return;
    }
    if (message != MIM_DATA) return;
    uint8_t status = (uint8_t)(param1 & 0xFF);
    if (status >= 0x80 && status < 0xF0) {
        push(time, status, (uint8_t)((param1 >> 8) & 0x7F), (uint8_t)((param1 >> 16) & 0x7F), channel_length(status),
             source);
    } else if (status >= 0xF1 && status != 0xF7) {
        /* MTC quarter frames, song position, real-time (clock, start, continue, stop, …) */
        push(time, status, (uint8_t)((param1 >> 8) & 0x7F), (uint8_t)((param1 >> 16) & 0x7F), system_length(status),
             source);
    }
}

static void port_name(int is_input, UINT index, char *out, size_t size)
{
    wchar_t wide[MAXPNAMELEN];
    MMRESULT result;
    if (is_input) {
        MIDIINCAPSW caps;
        result = midiInGetDevCapsW(index, &caps, sizeof caps);
        memcpy(wide, caps.szPname, sizeof wide);
    } else {
        MIDIOUTCAPSW caps;
        result = midiOutGetDevCapsW(index, &caps, sizeof caps);
        memcpy(wide, caps.szPname, sizeof wide);
    }
    wide[MAXPNAMELEN - 1] = 0;
    if (result != MMSYSERR_NOERROR || !WideCharToMultiByte(CP_UTF8, 0, wide, -1, out, (int)size, NULL, NULL)) out[0] = 0;
}

/* Writes "<prefix><name>#<n>" for port index of the given kind, n counting earlier ports with the same name. */
static void port_uid(int is_input, UINT index, char *uid, size_t uid_size, char *name, size_t name_size)
{
    char current[256], earlier[256];
    int duplicates = 0;
    port_name(is_input, index, current, sizeof current);
    for (UINT i = 0; i < index; i++) {
        port_name(is_input, i, earlier, sizeof earlier);
        if (strcmp(earlier, current) == 0) duplicates++;
    }
    if (name) snprintf(name, name_size, "%s", current);
    snprintf(uid, uid_size, "%s%s#%d", is_input ? "in:" : "out:", current, duplicates);
}

int32_t cth_midi_port_count(void) { return (int32_t)(midiInGetNumDevs() + midiOutGetNumDevs()); }

int32_t cth_midi_port_info(int32_t index, cth_midi_port *port)
{
    if (!port || index < 0) return -1;
    UINT sources = midiInGetNumDevs();
    UINT destinations = midiOutGetNumDevs();
    if ((UINT)index >= sources + destinations) return -1;
    port->is_input = (UINT)index < sources;
    UINT i = port->is_input ? (UINT)index : (UINT)index - sources;
    port_uid(port->is_input, i, port->uid, sizeof port->uid, port->name, sizeof port->name);
    return 0;
}

static void close_all(void)
{
    InterlockedExchange(&closing, 1);
    for (int i = 0; i < input_count; i++) {
        if (!inputs[i]) continue;
        midiInStop(inputs[i]);
        midiInReset(inputs[i]); /* returns the SysEx buffers */
        for (int b = 0; b < SYSEX_BUFFERS; b++) {
            midiInUnprepareHeader(inputs[i], &sysex_headers[i][b], sizeof(MIDIHDR));
        }
        midiInClose(inputs[i]);
        inputs[i] = NULL;
    }
    input_count = 0;
    InterlockedExchange(&closing, 0);
    for (int i = 0; i < MAX_PORTS; i++) {
        if (!outputs[i]) continue;
        midiOutReset(outputs[i]);
        midiOutClose(outputs[i]);
        outputs[i] = NULL;
    }
}

int32_t cth_midi_open(void)
{
    AcquireSRWLockExclusive(&ports_lock);
    close_all(); /* indices shift when devices come and go, so start over */
    UINT count = midiInGetNumDevs();
    if (count > MAX_PORTS) count = MAX_PORTS;
    for (UINT i = 0; i < count; i++) {
        /* A port another application holds exclusively stays closed; the rest still work. */
        if (midiInOpen(&inputs[i], i, (DWORD_PTR)receive, (DWORD_PTR)i, CALLBACK_FUNCTION) == MMSYSERR_NOERROR) {
            for (int b = 0; b < SYSEX_BUFFERS; b++) {
                MIDIHDR *header = &sysex_headers[i][b];
                memset(header, 0, sizeof *header);
                header->lpData = sysex_buffers[i][b];
                header->dwBufferLength = SYSEX_BUFFER_SIZE;
                if (midiInPrepareHeader(inputs[i], header, sizeof *header) == MMSYSERR_NOERROR) {
                    midiInAddBuffer(inputs[i], header, sizeof *header);
                }
            }
            midiInStart(inputs[i]);
        } else {
            inputs[i] = NULL;
        }
    }
    input_count = (int)count;
    opened = 1;
    ReleaseSRWLockExclusive(&ports_lock);
    return 0;
}

int32_t cth_midi_read(cth_midi_event *events, int32_t max)
{
    uint32_t tail = (uint32_t)ring_tail;
    uint32_t head = (uint32_t)InterlockedCompareExchange(&ring_head, 0, 0);
    int32_t n = 0;
    while (tail != head && n < max) {
        events[n++] = ring[tail & (RING_SIZE - 1)];
        tail++;
    }

    InterlockedExchange(&ring_tail, (LONG)tail);
    return n;
}

int32_t cth_midi_read_sysex(uint8_t *buffer, int32_t capacity, uint64_t *time_ns, int32_t *source)
{
    AcquireSRWLockExclusive(&sysex_lock);
    int32_t result = cth_sysex_ring_pop(&sysex_ring, buffer, capacity, time_ns, source);
    ReleaseSRWLockExclusive(&sysex_lock);
    return result;
}

/* Sends the whole messages in data to one opened output: short ones with midiOutShortMsg, SysEx with midiOutLongMsg
 * (waiting until WinMM is done with the buffer). */
static int send_to(HMIDIOUT output, const uint8_t *data, int32_t length)
{
    for (int32_t at = 0; at < length;) {
        int32_t size = cth_midi_message_length(data + at, length - at);
        if (size <= 0) return 0;
        const uint8_t *m = data + at;
        if (m[0] == 0xF0) {
            MIDIHDR header;
            memset(&header, 0, sizeof header);
            header.lpData = (LPSTR)m;
            header.dwBufferLength = (DWORD)size;
            header.dwBytesRecorded = (DWORD)size;
            if (midiOutPrepareHeader(output, &header, sizeof header) != MMSYSERR_NOERROR) return 0;
            MMRESULT result = midiOutLongMsg(output, &header, sizeof header);
            if (result == MMSYSERR_NOERROR) {
                for (int spin = 0; spin < 2000 && !(header.dwFlags & MHDR_DONE); spin++) Sleep(1);
            }
            while (midiOutUnprepareHeader(output, &header, sizeof header) == MIDIERR_STILLPLAYING) Sleep(1);
            if (result != MMSYSERR_NOERROR) return 0;
        } else {
            DWORD message = m[0] | (size > 1 ? (DWORD)m[1] << 8 : 0) | (size > 2 ? (DWORD)m[2] << 16 : 0);
            if (midiOutShortMsg(output, message) != MMSYSERR_NOERROR) return 0;
        }
        at += size;
    }
    return 1;
}

int32_t cth_midi_send(const char *destination_uid, const uint8_t *data, int32_t length)
{
    if (!opened || !data || length < 1) return -1;
    for (int32_t at = 0; at < length;) { /* refuse what is not whole messages before sending any */
        int32_t size = cth_midi_message_length(data + at, length - at);
        if (size <= 0) return -1;
        at += size;
    }

    int32_t sent = 0;
    AcquireSRWLockExclusive(&ports_lock);
    UINT count = midiOutGetNumDevs();
    if (count > MAX_PORTS) count = MAX_PORTS;
    for (UINT i = 0; i < count; i++) {
        if (destination_uid) {
            char uid[64];
            port_uid(0, i, uid, sizeof uid, NULL, 0);
            if (strcmp(uid, destination_uid) != 0) continue;
        }
        if (!outputs[i] && midiOutOpen(&outputs[i], i, 0, 0, CALLBACK_NULL) != MMSYSERR_NOERROR) {
            outputs[i] = NULL;
            continue;
        }
        if (send_to(outputs[i], data, length)) sent++;
    }
    ReleaseSRWLockExclusive(&ports_lock);
    return sent > 0 ? 0 : -3;
}

int32_t cth_midi_send_at(const char *destination_uid, const uint8_t *data, int32_t length, uint64_t time_ns)
{
    (void)time_ns; /* WinMM has no scheduled output: the caller holds the event until it is due */
    return cth_midi_send(destination_uid, data, length);
}

void cth_midi_close(void)
{
    AcquireSRWLockExclusive(&ports_lock);
    close_all();
    opened = 0;
    ReleaseSRWLockExclusive(&ports_lock);
    AcquireSRWLockExclusive(&producer_lock);
    for (int i = 0; i < MAX_PORTS; i++) cth_sysex_free(&assemblies[i]);
    ReleaseSRWLockExclusive(&producer_lock);
}

int32_t cth_midi_virtual_open(const char *name, const char *uid, int32_t is_input)
{
    (void)name; (void)uid; (void)is_input;
    cth_set_last_error("WinMM has no virtual MIDI ports");
    return CTH_MIDI_VIRTUAL_UNSUPPORTED;
}

int32_t cth_midi_virtual_close(const char *uid)
{
    (void)uid;
    return -1;
}
