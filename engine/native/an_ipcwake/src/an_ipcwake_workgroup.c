/*
 * an_ipcwake: sharing the audio device's os_workgroup with a sandbox process (macOS). See include/an_ipcwake.h.
 * SPDX-License-Identifier: GPL-3.0-or-later
 *
 * The host checks a Mach service in with the bootstrap server under a per-run name and answers every request on it
 * with a send right to the device's IO workgroup port (os_workgroup_copy_port). A sandbox looks the service up, asks
 * once, turns the port back into a workgroup (os_workgroup_create_with_port) and joins its audio thread to it, so the
 * scheduler treats the sandbox's thread as part of the device's real-time work. Other platforms have no workgroups:
 * the functions fail there.
 */
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "an_ipcwake.h"

#if defined(__APPLE__)
#include <CoreAudio/CoreAudio.h>
#include <mach/mach.h>
#include <os/workgroup.h>
#include <pthread.h>
#include <servers/bootstrap.h>

#define REQUEST_ID 0x414e5701
#define STOP_ID 0x414e5702

static _Thread_local char wg_error[256];

static int32_t wg_fail(const char *format, ...) {
    va_list args;
    va_start(args, format);
    vsnprintf(wg_error, sizeof wg_error, format, args);
    va_end(args);
    return -1;
}

const char *an_wg_last_error(void) { return wg_error; }

struct an_wg_server {
    mach_port_t service;     /* receive right checked in with the bootstrap server */
    mach_port_t workgroup;   /* send right to the device's workgroup */
    pthread_t thread;
};

struct an_wg_member {
    os_workgroup_t workgroup;
    os_workgroup_join_token_s token;
    int joined;
};

typedef struct {
    mach_msg_header_t header;
    mach_msg_body_t body;
    mach_msg_port_descriptor_t port;
} port_message;

typedef struct {
    port_message message;
    mach_msg_trailer_t trailer;
} port_message_received;

static int32_t device_workgroup_port(const char *device_uid, mach_port_t *port) {
    AudioObjectPropertyAddress address = {kAudioHardwarePropertyDefaultOutputDevice, kAudioObjectPropertyScopeGlobal,
                                          kAudioObjectPropertyElementMain};
    AudioObjectID device = kAudioObjectUnknown;
    UInt32 size = sizeof device;
    OSStatus status;
    if (device_uid == NULL) {
        status = AudioObjectGetPropertyData(kAudioObjectSystemObject, &address, 0, NULL, &size, &device);
    } else {
        CFStringRef uid = CFStringCreateWithCString(NULL, device_uid, kCFStringEncodingUTF8);
        address.mSelector = kAudioHardwarePropertyTranslateUIDToDevice;
        status = AudioObjectGetPropertyData(kAudioObjectSystemObject, &address, sizeof uid, &uid, &size, &device);
        CFRelease(uid);
    }
    if (status != noErr || device == kAudioObjectUnknown) {
        return wg_fail("no device for %s (%d)", device_uid ? device_uid : "default output", (int)status);
    }
    os_workgroup_t workgroup = NULL;
    address.mSelector = kAudioDevicePropertyIOThreadOSWorkgroup;
    size = sizeof workgroup;
    status = AudioObjectGetPropertyData(device, &address, 0, NULL, &size, &workgroup);
    if (status != noErr || workgroup == NULL) {
        return wg_fail("kAudioDevicePropertyIOThreadOSWorkgroup: %d", (int)status);
    }
    int r = os_workgroup_copy_port(workgroup, port);
    os_release(workgroup);
    if (r != 0) {
        return wg_fail("os_workgroup_copy_port: %d", r);
    }
    return 0;
}

static void *serve(void *arg) {
    an_wg_server *server = arg;
    for (;;) {
        union {
            mach_msg_header_t header;
            char bytes[512];
        } request;
        memset(&request, 0, sizeof request);
        kern_return_t kr = mach_msg(&request.header, MACH_RCV_MSG, 0, sizeof request, server->service,
                                    MACH_MSG_TIMEOUT_NONE, MACH_PORT_NULL);
        if (kr != KERN_SUCCESS || request.header.msgh_id == STOP_ID) {
            if (kr == KERN_SUCCESS) {
                mach_msg_destroy(&request.header);
            }
            return NULL;
        }
        if (request.header.msgh_remote_port == MACH_PORT_NULL) {
            mach_msg_destroy(&request.header);
            continue;
        }
        port_message reply;
        memset(&reply, 0, sizeof reply);
        reply.header.msgh_bits =
            MACH_MSGH_BITS(MACH_MSGH_BITS_REMOTE(request.header.msgh_bits), 0) | MACH_MSGH_BITS_COMPLEX;
        reply.header.msgh_size = sizeof reply;
        reply.header.msgh_remote_port = request.header.msgh_remote_port;
        reply.header.msgh_local_port = MACH_PORT_NULL;
        reply.header.msgh_id = REQUEST_ID + 100;
        reply.body.msgh_descriptor_count = 1;
        reply.port.name = server->workgroup;
        reply.port.disposition = MACH_MSG_TYPE_COPY_SEND;
        reply.port.type = MACH_MSG_PORT_DESCRIPTOR;
        kr = mach_msg(&reply.header, MACH_SEND_MSG | MACH_SEND_TIMEOUT, sizeof reply, 0, MACH_PORT_NULL, 1000,
                      MACH_PORT_NULL);
        if (kr != KERN_SUCCESS) {
            mach_msg_destroy(&request.header);
        }
    }
}

int32_t an_wg_publish_device(const char *device_uid, const char *service, an_wg_server **out) {
    *out = NULL;
    an_wg_server *server = calloc(1, sizeof *server);
    if (server == NULL) {
        return wg_fail("out of memory");
    }
    if (device_workgroup_port(device_uid, &server->workgroup) != 0) {
        free(server);
        return -1;
    }
    kern_return_t kr = bootstrap_check_in(bootstrap_port, service, &server->service);
    if (kr != KERN_SUCCESS) {
        mach_port_deallocate(mach_task_self(), server->workgroup);
        free(server);
        return wg_fail("bootstrap_check_in %s: %s", service, bootstrap_strerror(kr));
    }
    if (pthread_create(&server->thread, NULL, serve, server) != 0) {
        mach_port_mod_refs(mach_task_self(), server->service, MACH_PORT_RIGHT_RECEIVE, -1);
        mach_port_deallocate(mach_task_self(), server->workgroup);
        free(server);
        return wg_fail("pthread_create failed");
    }
    *out = server;
    return 0;
}

void an_wg_unpublish(an_wg_server *server) {
    if (server == NULL) {
        return;
    }
    mach_msg_header_t stop;
    memset(&stop, 0, sizeof stop);
    stop.msgh_bits = MACH_MSGH_BITS(MACH_MSG_TYPE_MAKE_SEND, 0);
    stop.msgh_size = sizeof stop;
    stop.msgh_remote_port = server->service;
    stop.msgh_id = STOP_ID;
    mach_msg(&stop, MACH_SEND_MSG, sizeof stop, 0, MACH_PORT_NULL, MACH_MSG_TIMEOUT_NONE, MACH_PORT_NULL);
    pthread_join(server->thread, NULL);
    mach_port_mod_refs(mach_task_self(), server->service, MACH_PORT_RIGHT_RECEIVE, -1);
    mach_port_deallocate(mach_task_self(), server->workgroup);
    free(server);
}

int32_t an_wg_join(const char *service, an_wg_member **out) {
    *out = NULL;
    mach_port_t server = MACH_PORT_NULL;
    kern_return_t kr = bootstrap_look_up(bootstrap_port, service, &server);
    if (kr != KERN_SUCCESS) {
        return wg_fail("bootstrap_look_up %s: %s", service, bootstrap_strerror(kr));
    }
    mach_port_t reply = MACH_PORT_NULL;
    kr = mach_port_allocate(mach_task_self(), MACH_PORT_RIGHT_RECEIVE, &reply);
    if (kr != KERN_SUCCESS) {
        mach_port_deallocate(mach_task_self(), server);
        return wg_fail("mach_port_allocate: %s", mach_error_string(kr));
    }
    union {
        mach_msg_header_t header;
        port_message_received received;
        char bytes[512];
    } message;
    memset(&message, 0, sizeof message);
    message.header.msgh_bits = MACH_MSGH_BITS(MACH_MSG_TYPE_COPY_SEND, MACH_MSG_TYPE_MAKE_SEND_ONCE);
    message.header.msgh_size = sizeof message.header;
    message.header.msgh_remote_port = server;
    message.header.msgh_local_port = reply;
    message.header.msgh_id = REQUEST_ID;
    kr = mach_msg(&message.header, MACH_SEND_MSG | MACH_RCV_MSG | MACH_SEND_TIMEOUT | MACH_RCV_TIMEOUT,
                  sizeof message.header, sizeof message, reply, 2000, MACH_PORT_NULL);
    mach_port_deallocate(mach_task_self(), server);
    mach_port_mod_refs(mach_task_self(), reply, MACH_PORT_RIGHT_RECEIVE, -1);
    if (kr != KERN_SUCCESS) {
        return wg_fail("workgroup request: %s", mach_error_string(kr));
    }
    if (!(message.header.msgh_bits & MACH_MSGH_BITS_COMPLEX) ||
        message.received.message.body.msgh_descriptor_count != 1 ||
        message.received.message.port.type != MACH_MSG_PORT_DESCRIPTOR) {
        mach_msg_destroy(&message.header);
        return wg_fail("unexpected workgroup reply");
    }
    mach_port_t port = message.received.message.port.name;
    an_wg_member *member = calloc(1, sizeof *member);
    if (member == NULL) {
        mach_port_deallocate(mach_task_self(), port);
        return wg_fail("out of memory");
    }
    member->workgroup = os_workgroup_create_with_port("anode-sandbox", port);
    mach_port_deallocate(mach_task_self(), port);
    if (member->workgroup == NULL) {
        free(member);
        return wg_fail("os_workgroup_create_with_port failed");
    }
    int r = os_workgroup_join(member->workgroup, &member->token);
    if (r != 0) {
        os_release(member->workgroup);
        free(member);
        return wg_fail("os_workgroup_join: %d (%s)", r, strerror(r));
    }
    member->joined = 1;
    *out = member;
    return 0;
}

void an_wg_leave(an_wg_member *member) {
    if (member == NULL) {
        return;
    }
    if (member->joined) {
        os_workgroup_leave(member->workgroup, &member->token);
    }
    os_release(member->workgroup);
    free(member);
}
#else
const char *an_wg_last_error(void) { return "os_workgroup is macOS only"; }

int32_t an_wg_publish_device(const char *device_uid, const char *service, an_wg_server **out) {
    (void)device_uid;
    (void)service;
    *out = NULL;
    return -1;
}

void an_wg_unpublish(an_wg_server *server) { (void)server; }

int32_t an_wg_join(const char *service, an_wg_member **out) {
    (void)service;
    *out = NULL;
    return -1;
}

void an_wg_leave(an_wg_member *member) { (void)member; }
#endif
