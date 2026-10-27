/*
 * an_sys: fixed-signature wrappers over system calls the engine host needs.
 * SPDX-License-Identifier: GPL-3.0-or-later
 *
 * shm_open and fcntl are variadic. On Apple arm64 variadic arguments are passed on the stack, not in registers, so a
 * plain P/Invoke passes garbage as the mode or the flag (docs/spikes/S2-disk-streaming.md, S3-bulk-ipc.md). These
 * wrappers take every argument in a register. The rest (the clock, the real-time policy) is here so the engine does
 * not bind libSystem itself.
 *
 * Functions return 0 (or a descriptor) on success and -1 on failure with errno set, unless documented otherwise.
 */
#ifndef AN_SYS_H
#define AN_SYS_H

#include <stdint.h>

#if defined(_WIN32)
#define AN_SYS_API __declspec(dllexport)
#else
#define AN_SYS_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Opens a POSIX shared-memory object. create: 1 creates it (exclusively, mode 0600) and opens it read-write; 0 opens
 * an existing one, read-write when writable is 1, else read-only. Returns the descriptor, or -1 (errno). */
AN_SYS_API int32_t an_sys_shm_open(const char *name, int32_t create, int32_t writable);

/* Reads and writes on fd bypass the page cache (macOS F_NOCACHE; Linux: advises the kernel to drop the file's cached
 * pages, POSIX_FADV_DONTNEED). Returns 0, or -1 (errno). */
AN_SYS_API int32_t an_sys_nocache(int32_t fd, int32_t enable);

/* Monotonic nanoseconds: CLOCK_UPTIME_RAW on macOS (the clock .NET's Stopwatch reads there), CLOCK_MONOTONIC on
 * Linux. */
AN_SYS_API uint64_t an_sys_now_ns(void);

/* Sleeps the calling thread until an_sys_now_ns() >= t (mach_wait_until / clock_nanosleep TIMER_ABSTIME). */
AN_SYS_API void an_sys_sleep_until_ns(uint64_t t);

/* Gives the calling thread real-time scheduling for a periodic workload: THREAD_TIME_CONSTRAINT_POLICY on macOS
 * (period, computation and constraint in nanoseconds), SCHED_FIFO on Linux (needs rtprio or CAP_SYS_NICE; the times
 * are ignored). Returns 0, or -1 when the system refused. */
AN_SYS_API int32_t an_sys_rt_promote(uint64_t period_ns, uint64_t computation_ns, uint64_t constraint_ns);

#ifdef __cplusplus
}
#endif

#endif
