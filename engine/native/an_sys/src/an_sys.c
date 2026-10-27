/*
 * an_sys for macOS and Linux. See include/an_sys.h.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#if defined(__linux__)
#define _GNU_SOURCE
#endif

#include <errno.h>
#include <fcntl.h>
#include <pthread.h>
#include <sched.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>

#if defined(__APPLE__)
#include <mach/mach.h>
#include <mach/mach_time.h>
#include <mach/thread_policy.h>
#endif

#include "an_sys.h"

int32_t an_sys_shm_open(const char *name, int32_t create, int32_t writable) {
    int flags = create ? (O_CREAT | O_EXCL | O_RDWR) : (writable ? O_RDWR : O_RDONLY);
    return shm_open(name, flags, (mode_t)0600);
}

int32_t an_sys_nocache(int32_t fd, int32_t enable) {
#if defined(__APPLE__)
    return fcntl(fd, F_NOCACHE, enable ? 1 : 0);
#elif defined(__linux__)
    if (!enable) {
        return 0;
    }
    int r = posix_fadvise(fd, 0, 0, POSIX_FADV_DONTNEED);
    if (r != 0) {
        errno = r;
        return -1;
    }
    return 0;
#else
    (void)fd;
    (void)enable;
    return 0;
#endif
}

#if defined(__APPLE__)
static mach_timebase_info_data_t timebase(void) {
    static mach_timebase_info_data_t info; /* racing initialisers write the same value */
    if (info.denom == 0) {
        mach_timebase_info(&info);
    }
    return info;
}

static uint64_t ns_to_ticks(uint64_t ns) {
    mach_timebase_info_data_t tb = timebase();
    return (uint64_t)((__uint128_t)ns * tb.denom / tb.numer);
}

uint64_t an_sys_now_ns(void) { return clock_gettime_nsec_np(CLOCK_UPTIME_RAW); }

void an_sys_sleep_until_ns(uint64_t t) { mach_wait_until(ns_to_ticks(t)); }
#else
uint64_t an_sys_now_ns(void) {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint64_t)ts.tv_sec * 1000000000ull + (uint64_t)ts.tv_nsec;
}

void an_sys_sleep_until_ns(uint64_t t) {
    struct timespec ts;
    ts.tv_sec = (time_t)(t / 1000000000ull);
    ts.tv_nsec = (long)(t % 1000000000ull);
    while (clock_nanosleep(CLOCK_MONOTONIC, TIMER_ABSTIME, &ts, NULL) == EINTR) {
    }
}
#endif

int32_t an_sys_rt_promote(uint64_t period_ns, uint64_t computation_ns, uint64_t constraint_ns) {
#if defined(__APPLE__)
    thread_time_constraint_policy_data_t policy;
    policy.period = (uint32_t)ns_to_ticks(period_ns);
    policy.computation = (uint32_t)ns_to_ticks(computation_ns);
    policy.constraint = (uint32_t)ns_to_ticks(constraint_ns);
    policy.preemptible = 1;
    kern_return_t kr = thread_policy_set(pthread_mach_thread_np(pthread_self()), THREAD_TIME_CONSTRAINT_POLICY,
                                         (thread_policy_t)&policy, THREAD_TIME_CONSTRAINT_POLICY_COUNT);
    return kr == KERN_SUCCESS ? 0 : -1;
#elif defined(__linux__)
    (void)period_ns;
    (void)computation_ns;
    (void)constraint_ns;
    struct sched_param param;
    memset(&param, 0, sizeof param);
    param.sched_priority = 80;
    int r = pthread_setschedparam(pthread_self(), SCHED_FIFO, &param);
    if (r != 0) {
        errno = r;
        return -1;
    }
    return 0;
#else
    (void)period_ns;
    (void)computation_ns;
    (void)constraint_ns;
    errno = ENOSYS;
    return -1;
#endif
}
