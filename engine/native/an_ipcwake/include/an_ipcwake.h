/*
 * an_ipcwake: cross-process wake primitives for the plugin sandbox (docs/adr/0003-plugin-hosting.md, spike S4 in
 * docs/spikes/S4-sandbox-wake.md).
 * SPDX-License-Identifier: GPL-3.0-or-later
 *
 * The host and an anode-plugin sandbox share one memory segment. Each direction of a handshake is a 32-bit counter in
 * that segment (a "wake word"): the signaller increments it and wakes the other side, the waiter blocks until it
 * differs from the value it last saw. How the waiter blocks is the wake kind:
 *
 *   AN_WAKE_SPIN       busy-polls the word. Portable, lowest latency, burns a core per waiter.
 *   AN_WAKE_ADDRESS    blocks in the kernel on the word itself: os_sync_wait_on_address with
 *                      OS_SYNC_WAIT_ON_ADDRESS_SHARED on macOS 14.4+, a non-private futex on Linux.
 *   AN_WAKE_SEMAPHORE  a POSIX named semaphore next to the word (macOS has no sem_timedwait, so a finite wait there
 *                      polls sem_trywait; Linux uses sem_timedwait).
 *
 * Any kind can spin first for a bounded time before it blocks (spin-then-wait). Windows is not built yet: the
 * documented plan is named auto-reset events (CreateEventW/SetEvent/WaitForSingleObject) over a CreateFileMapping
 * segment, since WaitOnAddress only works within one process.
 *
 * Functions return 0 on success and a negative value on failure (see an_ipcwake_last_error) unless documented
 * otherwise. Nothing here allocates or takes a lock on the signal and wait paths.
 */
#ifndef AN_IPCWAKE_H
#define AN_IPCWAKE_H

#include <stdint.h>

#if defined(_WIN32)
#define AN_API __declspec(dllexport)
#else
#define AN_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define AN_WAKE_SPIN 0
#define AN_WAKE_ADDRESS 1
#define AN_WAKE_SEMAPHORE 2

/* A timeout that never expires. */
#define AN_WAIT_FOREVER UINT64_MAX

/* ---- Shared memory ------------------------------------------------------------------------------------------- */

typedef struct an_shm an_shm;

/* Creates (replacing any stale one) and maps a zeroed segment. name: "/something", at most 30 characters. */
AN_API int32_t an_shm_create(const char *name, uint64_t size, an_shm **shm);
/* Maps an existing segment at its full size. */
AN_API int32_t an_shm_open(const char *name, an_shm **shm);
AN_API void *an_shm_data(const an_shm *shm);
AN_API uint64_t an_shm_size(const an_shm *shm);
/* Unmaps; unlink removes the name too (the creator does that once the other side has mapped it, or at exit). */
AN_API void an_shm_close(an_shm *shm, int32_t unlink);

/* ---- Wake words ---------------------------------------------------------------------------------------------- */

typedef struct an_wake an_wake;

/* 1 when the kind works on this system (AN_WAKE_ADDRESS needs macOS 14.4 on Apple platforms). */
AN_API int32_t an_wake_supported(int32_t kind);
/* word: a 4-byte aligned counter inside a shared segment. name: the semaphore name for AN_WAKE_SEMAPHORE ("/x", at
 * most 30 characters), ignored otherwise. create: 1 on the side that creates the semaphore (the host). */
AN_API int32_t an_wake_create(int32_t kind, uint32_t *word, const char *name, int32_t create, an_wake **wake);
AN_API void an_wake_destroy(an_wake *wake, int32_t unlink);
/* The word's current value (acquire). */
AN_API uint32_t an_wake_load(const an_wake *wake);
/* Increments the word (release) and wakes its waiter. Real-time safe. */
AN_API void an_wake_signal(an_wake *wake);
/* Waits until the word differs from seen: spins for up to spin_ns, then blocks for the rest of timeout_ns
 * (AN_WAIT_FOREVER: no limit). Returns 0 when the word changed, 1 on timeout, negative on error. */
AN_API int32_t an_wake_wait(an_wake *wake, uint32_t seen, uint64_t timeout_ns, uint64_t spin_ns);

/* ---- Clock, threads and processes ---------------------------------------------------------------------------- */

/* Monotonic nanoseconds, the same clock in every process (CLOCK_UPTIME_RAW on macOS, CLOCK_MONOTONIC on Linux). */
AN_API uint64_t an_now_ns(void);
/* Sleeps the calling thread until an_now_ns() >= t (mach_wait_until / clock_nanosleep TIMER_ABSTIME). */
AN_API void an_sleep_until_ns(uint64_t t);
/* One spin-loop hint (yield on arm64, pause on x86). */
AN_API void an_cpu_relax(void);

/* Gives the calling thread real-time scheduling for a periodic audio workload: THREAD_TIME_CONSTRAINT_POLICY on
 * macOS (period, computation and constraint in nanoseconds), SCHED_FIFO on Linux (needs rtprio or CAP_SYS_NICE; the
 * times are ignored). Returns 0 on success, negative when the OS refused. */
AN_API int32_t an_rt_promote(uint64_t period_ns, uint64_t computation_ns, uint64_t constraint_ns);

/* CPU time (user + system) a process has used so far, in nanoseconds; 0 when it cannot be read. */
AN_API uint64_t an_process_cpu_ns(int32_t pid);
/* 1 while the process exists. */
AN_API int32_t an_process_alive(int32_t pid);

AN_API const char *an_ipcwake_last_error(void);

/* ---- Audio workgroup sharing (macOS only; the functions fail elsewhere) ---------------------------------------- */

typedef struct an_wg_server an_wg_server;
typedef struct an_wg_member an_wg_member;

/* Host: publishes the IO thread workgroup of the CoreAudio device with this UID (NULL: the default output) as the
 * Mach bootstrap service `service`, answering requests on a background thread until an_wg_unpublish. */
AN_API int32_t an_wg_publish_device(const char *device_uid, const char *service, an_wg_server **server);
AN_API void an_wg_unpublish(an_wg_server *server);
/* Sandbox: fetches the published workgroup and joins the calling thread to it. Leave from the same thread. */
AN_API int32_t an_wg_join(const char *service, an_wg_member **member);
AN_API void an_wg_leave(an_wg_member *member);
AN_API const char *an_wg_last_error(void);

#ifdef __cplusplus
}
#endif

#endif
