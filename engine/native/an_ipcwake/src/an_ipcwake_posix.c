/*
 * an_ipcwake for macOS and Linux. See include/an_ipcwake.h.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#if defined(__linux__)
#define _GNU_SOURCE
#endif

#include <errno.h>
#include <fcntl.h>
#include <pthread.h>
#include <sched.h>
#include <semaphore.h>
#include <signal.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>

#if defined(__APPLE__)
#include <libproc.h>
#include <mach/mach.h>
#include <mach/mach_time.h>
#include <mach/thread_policy.h>
#include <os/os_sync_wait_on_address.h>
#include <sys/resource.h>
#elif defined(__linux__)
#include <linux/futex.h>
#include <sys/syscall.h>
#endif

#include "an_ipcwake.h"

static _Thread_local char last_error[256];

static int32_t fail(const char *format, ...) {
    va_list args;
    va_start(args, format);
    vsnprintf(last_error, sizeof last_error, format, args);
    va_end(args);
    return -1;
}

const char *an_ipcwake_last_error(void) { return last_error; }

/* ---- Clock ------------------------------------------------------------------------------------------------------ */

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

static uint64_t ticks_to_ns(uint64_t ticks) {
    mach_timebase_info_data_t tb = timebase();
    return (uint64_t)((__uint128_t)ticks * tb.numer / tb.denom);
}

uint64_t an_now_ns(void) { return clock_gettime_nsec_np(CLOCK_UPTIME_RAW); }

void an_sleep_until_ns(uint64_t t) {
    /* CLOCK_UPTIME_RAW in nanoseconds is mach_absolute_time scaled by the timebase. */
    mach_wait_until(ns_to_ticks(t));
}
#else
uint64_t an_now_ns(void) {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint64_t)ts.tv_sec * 1000000000u + (uint64_t)ts.tv_nsec;
}

void an_sleep_until_ns(uint64_t t) {
    struct timespec ts;
    ts.tv_sec = (time_t)(t / 1000000000u);
    ts.tv_nsec = (long)(t % 1000000000u);
    while (clock_nanosleep(CLOCK_MONOTONIC, TIMER_ABSTIME, &ts, NULL) == EINTR) {
    }
}
#endif

void an_cpu_relax(void) {
#if defined(__aarch64__) || defined(__arm__)
    __asm__ __volatile__("yield");
#elif defined(__x86_64__) || defined(__i386__)
    __builtin_ia32_pause();
#endif
}

/* ---- Shared memory ---------------------------------------------------------------------------------------------- */

struct an_shm {
    void *data;
    uint64_t size;
    char name[64];
};

static int32_t map_segment(int fd, uint64_t size, const char *name, an_shm **shm) {
    void *data = mmap(NULL, (size_t)size, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    close(fd);
    if (data == MAP_FAILED) {
        return fail("mmap %s: %s", name, strerror(errno));
    }
    an_shm *s = calloc(1, sizeof *s);
    if (s == NULL) {
        munmap(data, (size_t)size);
        return fail("out of memory");
    }
    s->data = data;
    s->size = size;
    snprintf(s->name, sizeof s->name, "%s", name);
    *shm = s;
    return 0;
}

int32_t an_shm_create(const char *name, uint64_t size, an_shm **shm) {
    *shm = NULL;
    shm_unlink(name); /* a stale segment from a crashed run */
    int fd = shm_open(name, O_RDWR | O_CREAT | O_EXCL, 0600);
    if (fd < 0) {
        return fail("shm_open %s: %s", name, strerror(errno));
    }
    if (ftruncate(fd, (off_t)size) != 0) {
        int error = errno;
        close(fd);
        shm_unlink(name);
        return fail("ftruncate %s: %s", name, strerror(error));
    }
    return map_segment(fd, size, name, shm);
}

int32_t an_shm_open(const char *name, an_shm **shm) {
    *shm = NULL;
    int fd = shm_open(name, O_RDWR, 0600);
    if (fd < 0) {
        return fail("shm_open %s: %s", name, strerror(errno));
    }
    struct stat st;
    if (fstat(fd, &st) != 0 || st.st_size <= 0) {
        close(fd);
        return fail("fstat %s: %s", name, strerror(errno));
    }
    return map_segment(fd, (uint64_t)st.st_size, name, shm);
}

void *an_shm_data(const an_shm *shm) { return shm->data; }

uint64_t an_shm_size(const an_shm *shm) { return shm->size; }

void an_shm_close(an_shm *shm, int32_t unlink) {
    if (shm == NULL) {
        return;
    }
    munmap(shm->data, (size_t)shm->size);
    if (unlink) {
        shm_unlink(shm->name);
    }
    free(shm);
}

/* ---- Wake words ------------------------------------------------------------------------------------------------- */

struct an_wake {
    int32_t kind;
    uint32_t *word;
    sem_t *sem;
    char name[64];
};

static uint32_t load_word(const uint32_t *word) { return __atomic_load_n(word, __ATOMIC_ACQUIRE); }

int32_t an_wake_supported(int32_t kind) {
    switch (kind) {
    case AN_WAKE_SPIN:
    case AN_WAKE_SEMAPHORE:
        return 1;
    case AN_WAKE_ADDRESS:
#if defined(__APPLE__)
        if (__builtin_available(macOS 14.4, *)) {
            return 1;
        }
        return 0;
#elif defined(__linux__)
        return 1;
#else
        return 0;
#endif
    default:
        return 0;
    }
}

int32_t an_wake_create(int32_t kind, uint32_t *word, const char *name, int32_t create, an_wake **wake) {
    *wake = NULL;
    if (!an_wake_supported(kind)) {
        return fail("wake kind %d is not supported here", kind);
    }
    if (((uintptr_t)word & 3u) != 0) {
        return fail("wake word is not 4-byte aligned");
    }
    an_wake *w = calloc(1, sizeof *w);
    if (w == NULL) {
        return fail("out of memory");
    }
    w->kind = kind;
    w->word = word;
    if (kind == AN_WAKE_SEMAPHORE) {
        if (name == NULL) {
            free(w);
            return fail("a semaphore wake needs a name");
        }
        snprintf(w->name, sizeof w->name, "%s", name);
        if (create) {
            sem_unlink(name);
            w->sem = sem_open(name, O_CREAT | O_EXCL, 0600, 0);
        } else {
            w->sem = sem_open(name, 0);
        }
        if (w->sem == SEM_FAILED) {
            int error = errno;
            free(w);
            return fail("sem_open %s: %s", name, strerror(error));
        }
    }
    *wake = w;
    return 0;
}

void an_wake_destroy(an_wake *wake, int32_t unlink) {
    if (wake == NULL) {
        return;
    }
    if (wake->sem != NULL) {
        sem_close(wake->sem);
        if (unlink) {
            sem_unlink(wake->name);
        }
    }
    free(wake);
}

uint32_t an_wake_load(const an_wake *wake) { return load_word(wake->word); }

void an_wake_signal(an_wake *wake) {
    __atomic_fetch_add(wake->word, 1u, __ATOMIC_SEQ_CST);
    switch (wake->kind) {
    case AN_WAKE_ADDRESS:
#if defined(__APPLE__)
        if (__builtin_available(macOS 14.4, *)) {
            /* -1/ENOENT when nobody is blocked (the waiter is spinning or not there yet): nothing to do. */
            os_sync_wake_by_address_any(wake->word, sizeof(uint32_t), OS_SYNC_WAKE_BY_ADDRESS_SHARED);
        }
#elif defined(__linux__)
        syscall(SYS_futex, wake->word, FUTEX_WAKE, 1, NULL, NULL, 0);
#endif
        break;
    case AN_WAKE_SEMAPHORE:
        sem_post(wake->sem);
        break;
    default:
        break;
    }
}

/* Blocks once until woken, the word changes or the deadline passes. 0 woken (or spurious), 1 deadline, <0 error. */
static int32_t block_address(an_wake *wake, uint32_t seen, uint64_t deadline) {
#if defined(__APPLE__)
    if (__builtin_available(macOS 14.4, *)) {
        int r;
        if (deadline == AN_WAIT_FOREVER) {
            r = os_sync_wait_on_address(wake->word, seen, sizeof(uint32_t), OS_SYNC_WAIT_ON_ADDRESS_SHARED);
        } else {
            r = os_sync_wait_on_address_with_deadline(wake->word, seen, sizeof(uint32_t),
                                                      OS_SYNC_WAIT_ON_ADDRESS_SHARED, OS_CLOCK_MACH_ABSOLUTE_TIME,
                                                      ns_to_ticks(deadline));
        }
        if (r >= 0 || errno == EINTR || errno == EFAULT) {
            return 0;
        }
        if (errno == ETIMEDOUT) {
            return 1;
        }
        return fail("os_sync_wait_on_address: %s", strerror(errno));
    }
    return fail("os_sync_wait_on_address needs macOS 14.4");
#elif defined(__linux__)
    struct timespec relative;
    struct timespec *timeout = NULL;
    if (deadline != AN_WAIT_FOREVER) {
        uint64_t now = an_now_ns();
        if (now >= deadline) {
            return 1;
        }
        uint64_t left = deadline - now;
        relative.tv_sec = (time_t)(left / 1000000000u);
        relative.tv_nsec = (long)(left % 1000000000u);
        timeout = &relative;
    }
    long r = syscall(SYS_futex, wake->word, FUTEX_WAIT, seen, timeout, NULL, 0);
    if (r == 0 || errno == EAGAIN || errno == EINTR) {
        return 0;
    }
    if (errno == ETIMEDOUT) {
        return 1;
    }
    return fail("futex wait: %s", strerror(errno));
#else
    (void)wake;
    (void)seen;
    (void)deadline;
    return fail("no address wait on this platform");
#endif
}

static int32_t block_semaphore(an_wake *wake, uint64_t deadline) {
    if (deadline == AN_WAIT_FOREVER) {
        if (sem_wait(wake->sem) == 0 || errno == EINTR) {
            return 0;
        }
        return fail("sem_wait: %s", strerror(errno));
    }
#if defined(__APPLE__)
    /* No sem_timedwait on macOS: poll, yielding the core between tries. */
    for (;;) {
        if (sem_trywait(wake->sem) == 0) {
            return 0;
        }
        if (errno != EAGAIN && errno != EINTR) {
            return fail("sem_trywait: %s", strerror(errno));
        }
        if (an_now_ns() >= deadline) {
            return 1;
        }
        sched_yield();
    }
#else
    uint64_t now = an_now_ns();
    if (now >= deadline) {
        return 1;
    }
    struct timespec real;
    clock_gettime(CLOCK_REALTIME, &real);
    uint64_t at = (uint64_t)real.tv_sec * 1000000000u + (uint64_t)real.tv_nsec + (deadline - now);
    struct timespec ts;
    ts.tv_sec = (time_t)(at / 1000000000u);
    ts.tv_nsec = (long)(at % 1000000000u);
    if (sem_timedwait(wake->sem, &ts) == 0 || errno == EINTR) {
        return 0;
    }
    if (errno == ETIMEDOUT) {
        return 1;
    }
    return fail("sem_timedwait: %s", strerror(errno));
#endif
}

int32_t an_wake_wait(an_wake *wake, uint32_t seen, uint64_t timeout_ns, uint64_t spin_ns) {
    if (load_word(wake->word) != seen) {
        return 0;
    }
    uint64_t start = an_now_ns();
    uint64_t deadline = timeout_ns == AN_WAIT_FOREVER || start + timeout_ns < start ? AN_WAIT_FOREVER
                                                                                     : start + timeout_ns;
    if (wake->kind == AN_WAKE_SPIN) {
        spin_ns = timeout_ns; /* spinning all the way is the kind */
    }
    if (spin_ns > 0) {
        uint64_t spin_until = start + spin_ns < start ? AN_WAIT_FOREVER : start + spin_ns;
        if (spin_until > deadline) {
            spin_until = deadline;
        }
        for (;;) {
            for (int i = 0; i < 16; i++) {
                if (load_word(wake->word) != seen) {
                    return 0;
                }
                an_cpu_relax();
            }
            uint64_t now = an_now_ns();
            if (now >= spin_until) {
                break;
            }
        }
        if (wake->kind == AN_WAKE_SPIN || an_now_ns() >= deadline) {
            return load_word(wake->word) != seen ? 0 : 1;
        }
    }
    for (;;) {
        int32_t r = wake->kind == AN_WAKE_ADDRESS ? block_address(wake, seen, deadline)
                                                  : block_semaphore(wake, deadline);
        if (load_word(wake->word) != seen) {
            return 0;
        }
        if (r != 0) {
            return r;
        }
        if (deadline != AN_WAIT_FOREVER && an_now_ns() >= deadline) {
            return 1;
        }
    }
}

/* ---- Threads and processes -------------------------------------------------------------------------------------- */

int32_t an_rt_promote(uint64_t period_ns, uint64_t computation_ns, uint64_t constraint_ns) {
#if defined(__APPLE__)
    thread_time_constraint_policy_data_t policy;
    policy.period = (uint32_t)ns_to_ticks(period_ns);
    policy.computation = (uint32_t)ns_to_ticks(computation_ns);
    policy.constraint = (uint32_t)ns_to_ticks(constraint_ns);
    policy.preemptible = 1;
    kern_return_t kr = thread_policy_set(pthread_mach_thread_np(pthread_self()), THREAD_TIME_CONSTRAINT_POLICY,
                                         (thread_policy_t)&policy, THREAD_TIME_CONSTRAINT_POLICY_COUNT);
    if (kr != KERN_SUCCESS) {
        return fail("thread_policy_set(THREAD_TIME_CONSTRAINT_POLICY): %s", mach_error_string(kr));
    }
    return 0;
#elif defined(__linux__)
    (void)period_ns;
    (void)computation_ns;
    (void)constraint_ns;
    struct sched_param param;
    memset(&param, 0, sizeof param);
    param.sched_priority = 80;
    int r = pthread_setschedparam(pthread_self(), SCHED_FIFO, &param);
    if (r != 0) {
        return fail("pthread_setschedparam(SCHED_FIFO): %s", strerror(r));
    }
    return 0;
#else
    (void)period_ns;
    (void)computation_ns;
    (void)constraint_ns;
    return fail("no real-time policy on this platform");
#endif
}

uint64_t an_process_cpu_ns(int32_t pid) {
#if defined(__APPLE__)
    struct rusage_info_v2 info;
    if (proc_pid_rusage(pid, RUSAGE_INFO_V2, (rusage_info_t *)&info) != 0) {
        return 0;
    }
    /* Reported in Mach absolute time units, which are not nanoseconds on Apple silicon. */
    return ticks_to_ns(info.ri_user_time + info.ri_system_time);
#elif defined(__linux__)
    clockid_t clock;
    struct timespec ts;
    if (clock_getcpuclockid((pid_t)pid, &clock) != 0 || clock_gettime(clock, &ts) != 0) {
        return 0;
    }
    return (uint64_t)ts.tv_sec * 1000000000u + (uint64_t)ts.tv_nsec;
#else
    (void)pid;
    return 0;
#endif
}

int32_t an_process_alive(int32_t pid) { return kill((pid_t)pid, 0) == 0 || errno == EPERM ? 1 : 0; }
