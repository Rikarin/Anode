/* SPDX-License-Identifier: GPL-3.0-or-later
 * CoreMIDI input and output for cth_audio (see cth_audio.h). Uses the MIDI 1.0 protocol through MIDIEventList, so
 * words are Universal MIDI Packets: type 1 (system common and real-time), type 2 (MIDI 1.0 channel voice) and type 3
 * (7-bit SysEx, in pieces of up to six bytes) are decoded and encoded.
 *
 * Virtual ports (Anode Phase 8): a virtual input is a destination this client creates (MIDIDestinationCreate), whose
 * messages enter the rings like a source's; a virtual output is a source it creates (MIDISourceCreate), which sends
 * with MIDIReceivedEventList. Each carries its cth uid as a string property, so every process using cth_audio lists it
 * under that uid; this process lists its own as inputs and outputs in their own right, not as the other side's
 * destinations and sources. Port order: other sources, this client's virtual inputs, other destinations, this
 * client's virtual outputs. */
#include <CoreMIDI/CoreMIDI.h>
#include <mach/mach_time.h>
#include <dispatch/dispatch.h>
#include <os/lock.h>
#include <pthread.h>
#include <stdatomic.h>
#include <stdio.h>
#include <string.h>
#include "cth_audio.h"
#include "cth_midi_sysex.h"

#define RING_SIZE 4096 /* power of two */
#define MAX_VIRTUAL 8
#define MAX_ASSEMBLIES 256
#define UID_PROPERTY CFSTR("cth uid")

typedef struct virtual_port {
    char uid[64];
    char name[256];
    int32_t is_input;
    MIDIEndpointRef endpoint; /* 0: free slot */
    _Atomic int32_t index;    /* a virtual input's port index, as cth_midi_port_info numbers it now */
} virtual_port;

static MIDIClientRef client;
static MIDIPortRef input_port;
static MIDIPortRef output_port;
static cth_midi_event ring[RING_SIZE];
static _Atomic uint32_t ring_head; /* written by the producers, under producer_lock */
static _Atomic uint32_t ring_tail; /* written by the reader */
static os_unfair_lock producer_lock = OS_UNFAIR_LOCK_INIT; /* the input port and the virtual inputs may call back on
                                                              different threads */
static os_unfair_lock sysex_lock = OS_UNFAIR_LOCK_INIT;
static cth_sysex_ring sysex_ring;
static cth_sysex_assembly assemblies[MAX_ASSEMBLIES]; /* by source index + 1 (0: unknown); under producer_lock */
static virtual_port virtuals[MAX_VIRTUAL];
static mach_timebase_info_data_t timebase;

static uint64_t to_ns(uint64_t host_time)
{
    if (timebase.denom == 0) mach_timebase_info(&timebase);
    return host_time * timebase.numer / timebase.denom;
}

static uint64_t to_host_time(uint64_t ns)
{
    if (timebase.denom == 0) mach_timebase_info(&timebase);
    return ns * timebase.denom / timebase.numer;
}

uint64_t cth_midi_now_ns(void) { return to_ns(mach_absolute_time()); }

/* Caller holds producer_lock. */
static void push(uint64_t time_ns, uint8_t status, uint8_t d1, uint8_t d2, uint8_t length, int32_t source)
{
    uint32_t head = atomic_load_explicit(&ring_head, memory_order_relaxed);
    uint32_t tail = atomic_load_explicit(&ring_tail, memory_order_acquire);
    if (head - tail >= RING_SIZE) return; /* full: drop */
    cth_midi_event *e = &ring[head & (RING_SIZE - 1)];
    e->time_ns = time_ns;
    e->data[0] = status;
    e->data[1] = d1;
    e->data[2] = d2;
    e->length = length;
    e->source = source;
    atomic_store_explicit(&ring_head, head + 1, memory_order_release);
}

static uint8_t channel_length(uint8_t status)
{
    switch (status & 0xF0) {
    case 0xC0:
    case 0xD0: return 2;
    default: return 3;
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

/* A type 3 (SysEx7) packet's piece. Caller holds producer_lock. */
static void receive_sysex(uint32_t word0, uint32_t word1, uint64_t time, int32_t source)
{
    cth_sysex_assembly *a = &assemblies[(uint32_t)(source + 1) % MAX_ASSEMBLIES];
    uint32_t kind = (word0 >> 20) & 0xF; /* 0 complete, 1 start, 2 continue, 3 end */
    uint32_t count = (word0 >> 16) & 0xF;
    uint8_t bytes[6] = {(uint8_t)(word0 >> 8), (uint8_t)word0, (uint8_t)(word1 >> 24), (uint8_t)(word1 >> 16),
                        (uint8_t)(word1 >> 8), (uint8_t)word1};
    if (count > 6) count = 6;
    for (uint32_t i = 0; i < count; i++) bytes[i] &= 0x7F;
    if (kind == 0 || kind == 1) cth_sysex_begin(a);
    cth_sysex_append(a, bytes, count);
    if (kind == 0 || kind == 3) {
        uint32_t length = cth_sysex_end(a);
        if (length > 0) {
            os_unfair_lock_lock(&sysex_lock);
            cth_sysex_ring_push(&sysex_ring, time, source, a->data, length);
            os_unfair_lock_unlock(&sysex_lock);
        }
    }
}

static void receive(const MIDIEventList *list, int32_t source)
{
    os_unfair_lock_lock(&producer_lock);
    const MIDIEventPacket *packet = &list->packet[0];
    for (UInt32 p = 0; p < list->numPackets; p++) {
        uint64_t time = packet->timeStamp ? to_ns(packet->timeStamp) : cth_midi_now_ns();
        for (UInt32 w = 0; w < packet->wordCount; w++) {
            uint32_t word = packet->words[w];
            uint32_t type = word >> 28;
            uint8_t status = (word >> 16) & 0xFF;
            if (type == 0x2) {
                push(time, status, (word >> 8) & 0x7F, word & 0x7F, channel_length(status), source);
            } else if (type == 0x1 && status >= 0xF1) {
                /* MTC quarter frames, song position, real-time (clock, start, continue, stop, …) */
                push(time, status, (word >> 8) & 0x7F, word & 0x7F, system_length(status), source);
            } else if (type == 0x3) {
                uint32_t second = w + 1 < packet->wordCount ? packet->words[w + 1] : 0;
                receive_sysex(word, second, time, source);
                w += 1;
            } else if (type == 0x4) {
                w += 1; /* 64-bit MIDI 2.0 channel voice: not in the MIDI 1.0 protocol */
            } else if (type == 0x5) {
                w += 3; /* 128-bit data */
            }
        }

        packet = MIDIEventPacketNext(packet);
    }
    os_unfair_lock_unlock(&producer_lock);
}

static void copy_string(MIDIObjectRef object, CFStringRef property, char *out, size_t size)
{
    CFStringRef value = NULL;
    out[0] = 0;
    if (MIDIObjectGetStringProperty(object, property, &value) == noErr && value) {
        CFStringGetCString(value, out, (CFIndex)size, kCFStringEncodingUTF8);
        CFRelease(value);
    }
}

/* An endpoint's cth uid: the property a cth_audio virtual port carries, else its CoreMIDI unique id. */
static void endpoint_uid(MIDIEndpointRef endpoint, char *uid, size_t size)
{
    copy_string(endpoint, UID_PROPERTY, uid, size);
    if (uid[0]) return;
    SInt32 unique = 0;
    MIDIObjectGetIntegerProperty(endpoint, kMIDIPropertyUniqueID, &unique);
    snprintf(uid, size, "%d", (int)unique);
}

static int is_own(MIDIEndpointRef endpoint)
{
    for (int i = 0; i < MAX_VIRTUAL; i++) {
        if (virtuals[i].endpoint && virtuals[i].endpoint == endpoint) return 1;
    }
    return 0;
}

/* The other processes' sources (other 1) or destinations (other 0), in CoreMIDI's order. */
static ItemCount other_count(int sources)
{
    ItemCount count = sources ? MIDIGetNumberOfSources() : MIDIGetNumberOfDestinations(), n = 0;
    for (ItemCount i = 0; i < count; i++) {
        if (!is_own(sources ? MIDIGetSource(i) : MIDIGetDestination(i))) n++;
    }
    return n;
}

static MIDIEndpointRef other_at(int sources, ItemCount index)
{
    ItemCount count = sources ? MIDIGetNumberOfSources() : MIDIGetNumberOfDestinations(), n = 0;
    for (ItemCount i = 0; i < count; i++) {
        MIDIEndpointRef endpoint = sources ? MIDIGetSource(i) : MIDIGetDestination(i);
        if (is_own(endpoint)) continue;
        if (n++ == index) return endpoint;
    }
    return 0;
}

/* This client's virtual inputs (is_input 1) or outputs (0), in slot order. */
static int32_t virtual_count(int32_t is_input)
{
    int32_t n = 0;
    for (int i = 0; i < MAX_VIRTUAL; i++) {
        if (virtuals[i].endpoint && virtuals[i].is_input == is_input) n++;
    }
    return n;
}

static virtual_port *virtual_at(int32_t is_input, int32_t index)
{
    int32_t n = 0;
    for (int i = 0; i < MAX_VIRTUAL; i++) {
        if (!virtuals[i].endpoint || virtuals[i].is_input != is_input) continue;
        if (n++ == index) return &virtuals[i];
    }
    return NULL;
}

int32_t cth_midi_port_count(void)
{
    return (int32_t)(other_count(1) + other_count(0)) + virtual_count(1) + virtual_count(0);
}

int32_t cth_midi_port_info(int32_t index, cth_midi_port *port)
{
    if (!port || index < 0) return -1;
    int32_t sources = (int32_t)other_count(1);
    int32_t inputs = virtual_count(1);
    int32_t destinations = (int32_t)other_count(0);
    int32_t outputs = virtual_count(0);
    virtual_port *own = NULL;
    MIDIEndpointRef endpoint = 0;
    if (index < sources) {
        endpoint = other_at(1, (ItemCount)index);
        port->is_input = 1;
    } else if (index < sources + inputs) {
        own = virtual_at(1, index - sources);
    } else if (index < sources + inputs + destinations) {
        endpoint = other_at(0, (ItemCount)(index - sources - inputs));
        port->is_input = 0;
    } else if (index < sources + inputs + destinations + outputs) {
        own = virtual_at(0, index - sources - inputs - destinations);
    }

    if (own) {
        snprintf(port->uid, sizeof port->uid, "%s", own->uid);
        snprintf(port->name, sizeof port->name, "%s", own->name);
        port->is_input = own->is_input;
        return 0;
    }

    if (!endpoint) return -1;
    endpoint_uid(endpoint, port->uid, sizeof port->uid);
    copy_string(endpoint, kMIDIPropertyDisplayName, port->name, sizeof port->name);
    return 0;
}

/* CoreMIDI tells a client about endpoints that come and go (another app's virtual port, a device plugged in) through
 * the run loop of the thread that created it, and the client's endpoint lists change only then: a client made on a
 * thread without a run loop never sees a port that appears after it. So the client is made on a thread of its own
 * that runs one, for the life of the process. */
static pthread_t loop_thread;
static CFRunLoopRef loop;
static dispatch_semaphore_t loop_ready;
static pthread_once_t loop_once = PTHREAD_ONCE_INIT;

static void keep_alive(CFRunLoopTimerRef timer, void *info)
{
    (void)timer;
    (void)info;
}

static void *loop_main(void *arg)
{
    (void)arg;
    pthread_setname_np("cth midi");
    loop = CFRunLoopGetCurrent();
    CFRunLoopTimerRef timer = CFRunLoopTimerCreate(NULL, 1e12, 1e12, 0, 0, keep_alive, NULL); /* never fires */
    CFRunLoopAddTimer(loop, timer, kCFRunLoopDefaultMode);
    dispatch_semaphore_signal(loop_ready);
    for (;;) CFRunLoopRun();
    return NULL;
}

static void start_loop(void)
{
    loop_ready = dispatch_semaphore_create(0);
    if (pthread_create(&loop_thread, NULL, loop_main, NULL) == 0) {
        pthread_detach(loop_thread);
        dispatch_semaphore_wait(loop_ready, DISPATCH_TIME_FOREVER);
    }
}

static int32_t create_client(void)
{
    if (MIDIClientCreate(CFSTR("Cathode"), NULL, NULL, &client) != noErr) {
        client = 0;
        return -1;
    }
    if (MIDIInputPortCreateWithProtocol(client, CFSTR("Cathode In"), kMIDIProtocol_1_0, &input_port,
                                        ^(const MIDIEventList *list, void *source) {
                                          receive(list, (int32_t)(intptr_t)source - 1); /* refCon: index + 1 */
                                        })
        != noErr) return -2;
    if (MIDIOutputPortCreate(client, CFSTR("Cathode Out"), &output_port) != noErr) return -3;
    return 0;
}

static int32_t ensure_client(void)
{
    if (client) return 0;
    pthread_once(&loop_once, start_loop);
    if (!loop) return create_client(); /* no thread: a client that sees the ports of now */
    __block int32_t result = 0;
    dispatch_semaphore_t done = dispatch_semaphore_create(0);
    CFRunLoopPerformBlock(loop, kCFRunLoopDefaultMode, ^{
      result = create_client();
      dispatch_semaphore_signal(done);
    });
    CFRunLoopWakeUp(loop);
    dispatch_semaphore_wait(done, DISPATCH_TIME_FOREVER);
    dispatch_release(done);
    return result;
}

int32_t cth_midi_open(void)
{
    int32_t result = ensure_client();
    if (result != 0) return result;

    ItemCount count = MIDIGetNumberOfSources();
    ItemCount index = 0;
    for (ItemCount i = 0; i < count; i++) {
        MIDIEndpointRef source = MIDIGetSource(i);
        if (is_own(source)) continue; /* this client's virtual outputs are not inputs here */
        MIDIPortDisconnectSource(input_port, source); /* reconnecting is harmless; refresh picks up new devices */
        MIDIPortConnectSource(input_port, source, (void *)(intptr_t)(index + 1)); /* sources come first */
        index++;
    }

    int32_t n = 0;
    for (int i = 0; i < MAX_VIRTUAL; i++) {
        if (virtuals[i].endpoint && virtuals[i].is_input) {
            atomic_store(&virtuals[i].index, (int32_t)index + n++);
        }
    }

    return 0;
}

int32_t cth_midi_read(cth_midi_event *events, int32_t max)
{
    uint32_t tail = atomic_load_explicit(&ring_tail, memory_order_relaxed);
    uint32_t head = atomic_load_explicit(&ring_head, memory_order_acquire);
    int32_t n = 0;
    while (tail != head && n < max) {
        events[n++] = ring[tail & (RING_SIZE - 1)];
        tail++;
    }

    atomic_store_explicit(&ring_tail, tail, memory_order_release);
    return n;
}

int32_t cth_midi_read_sysex(uint8_t *buffer, int32_t capacity, uint64_t *time_ns, int32_t *source)
{
    os_unfair_lock_lock(&sysex_lock);
    int32_t result = cth_sysex_ring_pop(&sysex_ring, buffer, capacity, time_ns, source);
    os_unfair_lock_unlock(&sysex_lock);
    return result;
}

/* ---- Output ------------------------------------------------------------------------------------------------------ */

typedef struct send_list {
    union {
        MIDIEventList list;
        uint8_t bytes[4096];
    } storage;
    MIDIEventPacket *packet;
    MIDITimeStamp time;
} send_list;

static void list_begin(send_list *s) { s->packet = MIDIEventListInit(&s->storage.list, kMIDIProtocol_1_0); }

/* Sends the list to the destination with this uid (NULL: all) and to this client's virtual outputs that match;
 * returns how many took it. */
static int32_t list_send(const send_list *s, const char *destination_uid)
{
    int32_t sent = 0;
    ItemCount count = MIDIGetNumberOfDestinations();
    for (ItemCount i = 0; i < count; i++) {
        MIDIEndpointRef destination = MIDIGetDestination(i);
        if (is_own(destination)) continue; /* this client's virtual inputs */
        if (destination_uid) {
            char uid[64];
            endpoint_uid(destination, uid, sizeof uid);
            if (strcmp(uid, destination_uid) != 0) continue;
        }

        if (MIDISendEventList(output_port, destination, &s->storage.list) == noErr) sent++;
    }

    for (int i = 0; i < MAX_VIRTUAL; i++) {
        virtual_port *v = &virtuals[i];
        if (!v->endpoint || v->is_input) continue;
        if (destination_uid && strcmp(v->uid, destination_uid) != 0) continue;
        if (MIDIReceivedEventList(v->endpoint, &s->storage.list) == noErr) sent++;
    }

    return sent;
}

/* Adds one UMP (1 or 2 words), sending what the list holds first when it is full. */
static int list_add(send_list *s, const uint32_t *words, int count, const char *destination_uid, int32_t *sent)
{
    MIDIEventPacket *next = MIDIEventListAdd(&s->storage.list, sizeof s->storage, s->packet, s->time, (ByteCount)count,
                                             words);
    if (next) {
        s->packet = next;
        return 1;
    }

    *sent += list_send(s, destination_uid);
    list_begin(s);
    next = MIDIEventListAdd(&s->storage.list, sizeof s->storage, s->packet, s->time, (ByteCount)count, words);
    if (!next) return 0;
    s->packet = next;
    return 1;
}

static int32_t send_messages(const char *destination_uid, const uint8_t *data, int32_t length, MIDITimeStamp time)
{
    if (!output_port || !data || length < 1) return -1;
    send_list s;
    s.time = time;
    list_begin(&s);
    int32_t sent = 0, added = 0;
    for (int32_t at = 0; at < length;) {
        int32_t size = cth_midi_message_length(data + at, length - at);
        if (size <= 0) return -1;
        const uint8_t *m = data + at;
        uint8_t status = m[0];
        uint32_t words[2];
        if (status != 0xF0) {
            uint32_t type = status < 0xF0 ? 0x2u : 0x1u;
            words[0] = (type << 28) | ((uint32_t)status << 16) | (size > 1 ? (uint32_t)m[1] << 8 : 0)
                       | (size > 2 ? m[2] : 0);
            if (!list_add(&s, words, 1, destination_uid, &sent)) return -2;
            added++;
        } else {
            /* SysEx: the bytes between F0 and F7 in pieces of six. */
            int32_t payload = size - 2;
            const uint8_t *p = m + 1;
            int32_t done = 0;
            do {
                int32_t n = payload - done < 6 ? payload - done : 6;
                uint32_t kind = payload <= 6 ? 0 : done == 0 ? 1 : done + n >= payload ? 3 : 2;
                uint8_t b[6] = {0, 0, 0, 0, 0, 0};
                memcpy(b, p + done, (size_t)n);
                words[0] = (0x3u << 28) | (kind << 20) | ((uint32_t)n << 16) | ((uint32_t)b[0] << 8) | b[1];
                words[1] = ((uint32_t)b[2] << 24) | ((uint32_t)b[3] << 16) | ((uint32_t)b[4] << 8) | b[5];
                if (!list_add(&s, words, 2, destination_uid, &sent)) return -2;
                added++;
                done += n;
            } while (done < payload);
        }

        at += size;
    }

    if (added > 0 && s.storage.list.numPackets > 0) sent += list_send(&s, destination_uid);
    return sent > 0 ? 0 : -3;
}

int32_t cth_midi_send(const char *destination_uid, const uint8_t *data, int32_t length)
{
    return send_messages(destination_uid, data, length, 0);
}

int32_t cth_midi_send_at(const char *destination_uid, const uint8_t *data, int32_t length, uint64_t time_ns)
{
    int32_t result = send_messages(destination_uid, data, length, time_ns ? to_host_time(time_ns) : 0);
    return result < 0 ? result : 1;
}

/* ---- Virtual ports ----------------------------------------------------------------------------------------------- */

static int32_t fail(int32_t code, const char *message)
{
    cth_set_last_error(message);
    return code;
}

int32_t cth_midi_virtual_open(const char *name, const char *uid, int32_t is_input)
{
    if (!name || !uid || !uid[0] || strlen(uid) > 63) return fail(-1, "a virtual port needs a name and a uid");
    if (ensure_client() != 0) return fail(-1, "CoreMIDI refused the client");
    virtual_port *slot = NULL;
    for (int i = 0; i < MAX_VIRTUAL; i++) {
        if (virtuals[i].endpoint && strcmp(virtuals[i].uid, uid) == 0) return fail(-3, "that uid is already open");
        if (!virtuals[i].endpoint && !slot) slot = &virtuals[i];
    }
    if (!slot) return fail(-4, "too many virtual ports");

    CFStringRef display = CFStringCreateWithCString(NULL, name, kCFStringEncodingUTF8);
    if (!display) return fail(-1, "the name is not UTF-8");
    MIDIEndpointRef endpoint = 0;
    OSStatus status;
    int slot_index = (int)(slot - virtuals);
    if (is_input) {
        atomic_store(&slot->index, -1);
        status = MIDIDestinationCreateWithProtocol(client, display, kMIDIProtocol_1_0, &endpoint,
                                                   ^(const MIDIEventList *list, void *connection) {
                                                     (void)connection;
                                                     receive(list, atomic_load(&virtuals[slot_index].index));
                                                   });
    } else {
        status = MIDISourceCreateWithProtocol(client, display, kMIDIProtocol_1_0, &endpoint);
    }
    CFRelease(display);
    if (status != noErr || !endpoint) return fail(-5, "CoreMIDI refused the virtual port");

    /* The uid, for every cth_audio process; and a unique id derived from it, so apps that remember ports by their
     * CoreMIDI unique id find it again across runs (another endpoint may hold it already: then CoreMIDI's stays). */
    CFStringRef property = CFStringCreateWithCString(NULL, uid, kCFStringEncodingUTF8);
    if (property) {
        MIDIObjectSetStringProperty(endpoint, UID_PROPERTY, property);
        CFRelease(property);
    }
    uint32_t hash = 2166136261u;
    for (const char *c = uid; *c; c++) hash = (hash ^ (uint8_t)*c) * 16777619u;
    MIDIObjectSetIntegerProperty(endpoint, kMIDIPropertyUniqueID, (SInt32)(hash & 0x7FFFFFFF));

    snprintf(slot->uid, sizeof slot->uid, "%s", uid);
    snprintf(slot->name, sizeof slot->name, "%s", name);
    slot->is_input = is_input ? 1 : 0;
    if (is_input) {
        /* Its index until the next cth_midi_open: after the other sources and the virtual inputs before it. */
        int32_t n = 0;
        for (int i = 0; i < slot_index; i++) {
            if (virtuals[i].endpoint && virtuals[i].is_input) n++;
        }
        atomic_store(&slot->index, (int32_t)other_count(1) + n);
    }
    slot->endpoint = endpoint;
    return 0;
}

int32_t cth_midi_virtual_close(const char *uid)
{
    if (!uid) return -1;
    for (int i = 0; i < MAX_VIRTUAL; i++) {
        if (virtuals[i].endpoint && strcmp(virtuals[i].uid, uid) == 0) {
            MIDIEndpointDispose(virtuals[i].endpoint);
            virtuals[i].endpoint = 0;
            return 0;
        }
    }
    return -1;
}

void cth_midi_close(void)
{
    for (int i = 0; i < MAX_VIRTUAL; i++) {
        if (virtuals[i].endpoint) {
            MIDIEndpointDispose(virtuals[i].endpoint);
            virtuals[i].endpoint = 0;
        }
    }

    if (client) {
        MIDIClientDispose(client);
        client = 0;
        input_port = 0;
        output_port = 0;
    }

    os_unfair_lock_lock(&producer_lock);
    for (int i = 0; i < MAX_ASSEMBLIES; i++) cth_sysex_free(&assemblies[i]);
    os_unfair_lock_unlock(&producer_lock);
}
