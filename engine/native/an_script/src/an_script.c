/*
 * an_script: QuickJS-ng behind a C ABI, with an arena per instance, a time budget per call and a trace ring (see
 * include/an_script.h and docs/adr/0009-scripter.md).
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#include "an_script.h"

#include "arena.h"

#include <quickjs.h>

#include <math.h>
#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#if defined(_WIN32)
#include <windows.h>
#else
#include <time.h>
#endif

#define MIN_ARENA_BYTES ((size_t)256 * 1024)
#define MAX_STACK_BYTES ((size_t)256 * 1024) /* JS recursion limit: well inside an audio thread's stack */
#define ERROR_MAX 512
#define MAX_ARGS 16

struct an_script {
    arena heap;
    void *memory;
    size_t memory_bytes;
    JSRuntime *rt;
    JSContext *ctx;
    uint64_t seed;
    uint64_t random_state;

    /* Budget: the deadline in microseconds of the monotonic clock (0: none), and what stopped the current call. */
    int64_t deadline_us;
    int64_t control_budget_us;
    int interrupted;
    int out_of_memory;

    double *emit;
    int32_t emit_capacity;
    int32_t emit_count;
    int32_t emit_dropped;

    char *trace; /* trace_capacity lines of AN_SCRIPT_TRACE_LINE_MAX bytes */
    uint32_t trace_capacity;
    _Atomic uint32_t trace_head; /* written by the producer */
    _Atomic uint32_t trace_tail; /* written by the consumer */
    _Atomic int32_t trace_dropped;

    char error[ERROR_MAX];
    int32_t error_line;
};

static int64_t now_us(void) {
#if defined(_WIN32)
    static LARGE_INTEGER frequency;
    LARGE_INTEGER counter;
    if (frequency.QuadPart == 0) {
        QueryPerformanceFrequency(&frequency);
    }
    QueryPerformanceCounter(&counter);
    return (int64_t)((double)counter.QuadPart * 1e6 / (double)frequency.QuadPart);
#else
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (int64_t)ts.tv_sec * 1000000 + ts.tv_nsec / 1000;
#endif
}

/* QuickJS's allocator: the instance's arena. A failure marks the call out of memory. */

static void *mf_calloc(void *opaque, size_t count, size_t size) {
    an_script *s = opaque;
    void *p = arena_calloc(&s->heap, count, size);
    if (!p && count && size) {
        s->out_of_memory = 1;
    }
    return p;
}

static void *mf_malloc(void *opaque, size_t size) {
    an_script *s = opaque;
    void *p = arena_malloc(&s->heap, size);
    if (!p) {
        s->out_of_memory = 1;
    }
    return p;
}

static void mf_free(void *opaque, void *ptr) {
    an_script *s = opaque;
    arena_free(&s->heap, ptr);
}

static void *mf_realloc(void *opaque, void *ptr, size_t size) {
    an_script *s = opaque;
    void *p = arena_realloc(&s->heap, ptr, size);
    if (!p && size) {
        s->out_of_memory = 1;
    }
    return p;
}

static size_t mf_usable_size(const void *ptr) { return arena_usable_size(ptr); }

static const JSMallocFunctions malloc_functions = {mf_calloc, mf_malloc, mf_free, mf_realloc, mf_usable_size};

static int interrupt_handler(JSRuntime *rt, void *opaque) {
    (void)rt;
    an_script *s = opaque;
    if (s->deadline_us != 0 && now_us() >= s->deadline_us) {
        s->interrupted = 1;
        return 1;
    }
    return 0;
}

/* Natives the prelude builds on. */

static an_script *instance_of(JSContext *ctx) { return JS_GetContextOpaque(ctx); }

static JSValue native_trace(JSContext *ctx, JSValueConst this_val, int argc, JSValueConst *argv) {
    (void)this_val;
    an_script *s = instance_of(ctx);
    size_t length = 0;
    const char *text = JS_ToCStringLen(ctx, &length, argc > 0 ? argv[0] : JS_UNDEFINED);
    if (!text) {
        return JS_EXCEPTION;
    }
    uint32_t head = atomic_load_explicit(&s->trace_head, memory_order_relaxed);
    uint32_t tail = atomic_load_explicit(&s->trace_tail, memory_order_acquire);
    if (head - tail >= s->trace_capacity) {
        atomic_fetch_add_explicit(&s->trace_dropped, 1, memory_order_relaxed);
    } else {
        char *line = s->trace + (size_t)(head % s->trace_capacity) * AN_SCRIPT_TRACE_LINE_MAX;
        if (length > AN_SCRIPT_TRACE_LINE_MAX - 1) {
            length = AN_SCRIPT_TRACE_LINE_MAX - 1;
            while (length > 0 && ((unsigned char)text[length] & 0xC0) == 0x80) {
                length--; /* do not cut a UTF-8 sequence */
            }
        }
        if (length == 0) {
            line[0] = ' '; /* an empty line reads as one space, so it is not mistaken for an empty ring */
            line[1] = '\0';
        } else {
            memcpy(line, text, length);
            line[length] = '\0';
        }
        atomic_store_explicit(&s->trace_head, head + 1, memory_order_release);
    }
    JS_FreeCString(ctx, text);
    return JS_UNDEFINED;
}

static JSValue native_emit(JSContext *ctx, JSValueConst this_val, int argc, JSValueConst *argv) {
    (void)this_val;
    an_script *s = instance_of(ctx);
    if (s->emit_count >= s->emit_capacity) {
        s->emit_dropped++;
        return JS_FALSE;
    }
    double *record = s->emit + (size_t)s->emit_count * AN_SCRIPT_EMIT_WIDTH;
    for (int i = 0; i < AN_SCRIPT_EMIT_WIDTH; i++) {
        double value = 0;
        if (i < argc && JS_ToFloat64(ctx, &value, argv[i]) < 0) {
            return JS_EXCEPTION;
        }
        record[i] = value;
    }
    s->emit_count++;
    return JS_TRUE;
}

static uint64_t next_random(uint64_t *state) {
    /* xorshift64* */
    uint64_t x = *state;
    x ^= x >> 12;
    x ^= x << 25;
    x ^= x >> 27;
    *state = x;
    return x * 0x2545F4914F6CDD1DULL;
}

static JSValue native_random(JSContext *ctx, JSValueConst this_val, int argc, JSValueConst *argv) {
    (void)this_val;
    (void)argc;
    (void)argv;
    an_script *s = instance_of(ctx);
    return JS_NewFloat64(ctx, (double)(next_random(&s->random_state) >> 11) * 0x1.0p-53);
}

static void seed_random(an_script *s) {
    /* splitmix64 of the seed, so nearby seeds give unrelated sequences; never 0 */
    uint64_t z = s->seed + 0x9E3779B97F4A7C15ULL;
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ULL;
    z = (z ^ (z >> 27)) * 0x94D049BB133111EBULL;
    z ^= z >> 31;
    s->random_state = z ? z : 0x2545F4914F6CDD1DULL;
}

static void clear_buffers(an_script *s) {
    s->emit_count = 0;
    s->emit_dropped = 0;
    atomic_store(&s->trace_head, 0);
    atomic_store(&s->trace_tail, 0);
    atomic_store(&s->trace_dropped, 0);
    s->error[0] = '\0';
    s->error_line = 0;
}

static int define_function(JSContext *ctx, JSValueConst target, const char *name, JSCFunction *function, int length) {
    JSValue f = JS_NewCFunction(ctx, function, name, length);
    if (JS_IsException(f)) {
        return -1;
    }
    return JS_SetPropertyStr(ctx, target, name, f) < 0 ? -1 : 0;
}

/* A fresh runtime and context in the (re-initialised) arena. Returns 0, or -1 when the arena cannot hold them. */
static int start_runtime(an_script *s) {
    s->rt = NULL;
    s->ctx = NULL;
    if (!arena_init(&s->heap, s->memory, s->memory_bytes)) {
        return -1;
    }
    s->out_of_memory = 0;
    s->interrupted = 0;
    s->deadline_us = 0;
    seed_random(s);
    JSRuntime *rt = JS_NewRuntime2(&malloc_functions, s);
    if (!rt) {
        return -1;
    }
    /* No automatic collection: it would run inside calls on the audio thread. an_script_collect runs it. */
    JS_SetGCThreshold(rt, (size_t)-1);
    JS_SetMaxStackSize(rt, MAX_STACK_BYTES);
    JS_SetInterruptHandler(rt, interrupt_handler, s);
    JSContext *ctx = JS_NewContextRaw(rt);
    if (!ctx) {
        return -1;
    }
    s->rt = rt;
    s->ctx = ctx;
    JS_SetContextOpaque(ctx, s);
    if (JS_AddIntrinsicBaseObjects(ctx) || JS_AddIntrinsicDate(ctx) || JS_AddIntrinsicEval(ctx) ||
        JS_AddIntrinsicRegExp(ctx) || JS_AddIntrinsicJSON(ctx) || JS_AddIntrinsicMapSet(ctx) ||
        JS_AddIntrinsicTypedArrays(ctx)) {
        return -1;
    }
    JSValue global = JS_GetGlobalObject(ctx);
    JSValue math = JS_GetPropertyStr(ctx, global, "Math");
    int failed = define_function(ctx, global, "__trace", native_trace, 1) ||
                 define_function(ctx, global, "__emit", native_emit, AN_SCRIPT_EMIT_WIDTH) ||
                 JS_IsException(math) || define_function(ctx, math, "random", native_random, 0);
    JS_FreeValue(ctx, math);
    JS_FreeValue(ctx, global);
    return failed || s->out_of_memory ? -1 : 0;
}

static void copy_text(char *buffer, size_t capacity, const char *text) {
    size_t length = strlen(text);
    if (length > capacity - 1) {
        length = capacity - 1;
        while (length > 0 && ((unsigned char)text[length] & 0xC0) == 0x80) {
            length--;
        }
    }
    memcpy(buffer, text, length);
    buffer[length] = '\0';
}

/* The first line of a stack trace in a script whose name does not start with '<' (the host's own code, such as the
 * prelude) or is "native": "    at f (Script:12:5)" or "    at Script:12:5". 0 when there is none. */
static int32_t line_of(const char *stack) {
    const char *p = stack;
    while (p && *p) {
        const char *end = strchr(p, '\n');
        size_t length = end ? (size_t)(end - p) : strlen(p);
        const char *at = NULL;
        for (size_t i = 0; i + 3 <= length; i++) {
            if (p[i] == 'a' && p[i + 1] == 't' && p[i + 2] == ' ') {
                at = p + i + 3;
                break;
            }
        }
        if (at) {
            const char *location = at;
            const char *location_end = p + length;
            if (location_end > location && location_end[-1] == ')') {
                const char *open = location_end;
                while (open > location && open[-1] != '(') {
                    open--;
                }
                if (open > location) {
                    location = open;
                    location_end--;
                }
            }
            /* location .. location_end is "file:line:col" */
            const char *colon2 = location_end;
            while (colon2 > location && colon2[-1] != ':') {
                colon2--;
            }
            const char *colon1 = colon2 > location ? colon2 - 1 : location;
            while (colon1 > location && colon1[-1] != ':') {
                colon1--;
            }
            if (colon1 > location + 1 && *location != '<' && strncmp(location, "native", 6) != 0) {
                long line = strtol(colon1, NULL, 10);
                if (line > 0) {
                    return (int32_t)line;
                }
            }
        }
        p = end ? end + 1 : NULL;
    }
    return 0;
}

/* Takes the pending exception and turns it into a result, recording the message and line. */
static int32_t take_exception(an_script *s) {
    JSContext *ctx = s->ctx;
    JSValue exception = JS_GetException(ctx);
    int32_t result = s->out_of_memory ? AN_SCRIPT_OUT_OF_MEMORY
                     : s->interrupted ? AN_SCRIPT_INTERRUPTED
                                      : AN_SCRIPT_ERROR;
    /* Reading the message allocates; after running out of memory there may be no room for it. */
    int was_out_of_memory = s->out_of_memory;
    s->out_of_memory = 0;
    int64_t deadline = s->deadline_us;
    s->deadline_us = 0; /* reading the stack must not be interrupted */
    s->error_line = 0;
    if (result == AN_SCRIPT_OUT_OF_MEMORY) {
        copy_text(s->error, ERROR_MAX, "out of memory: the script's arena is full");
    } else {
        const char *message = JS_ToCString(ctx, exception);
        if (result == AN_SCRIPT_INTERRUPTED) {
            copy_text(s->error, ERROR_MAX, "interrupted: the script ran past its time budget");
        } else {
            copy_text(s->error, ERROR_MAX, message ? message : "error");
        }
        if (message) {
            JS_FreeCString(ctx, message);
        }
    }
    if (JS_IsObject(exception)) {
        JSValue stack = JS_GetPropertyStr(ctx, exception, "stack");
        const char *text = JS_IsString(stack) ? JS_ToCString(ctx, stack) : NULL;
        if (text) {
            s->error_line = line_of(text);
            JS_FreeCString(ctx, text);
        }
        JS_FreeValue(ctx, stack);
    }
    JS_FreeValue(ctx, exception);
    if (JS_HasException(ctx)) {
        JS_FreeValue(ctx, JS_GetException(ctx)); /* from reading the message */
    }
    s->deadline_us = deadline;
    s->out_of_memory = was_out_of_memory;
    return result;
}

static void begin(an_script *s, int64_t budget_us) {
    JS_UpdateStackTop(s->rt); /* the calling thread's stack */
    s->interrupted = 0;
    s->out_of_memory = 0;
    s->deadline_us = budget_us > 0 ? now_us() + budget_us : 0;
}

/* Ends a call: an out-of-memory during it (even one the script caught) is the call's result. */
static int32_t end(an_script *s, int32_t result) {
    if (result == AN_SCRIPT_OK && s->out_of_memory) {
        copy_text(s->error, ERROR_MAX, "out of memory: the script's arena is full");
        s->error_line = 0;
        result = AN_SCRIPT_OUT_OF_MEMORY;
    }
    s->deadline_us = 0;
    s->interrupted = 0;
    s->out_of_memory = 0;
    return result;
}

int32_t an_script_create(size_t arena_bytes, uint64_t random_seed, int32_t emit_capacity, int32_t trace_capacity,
                         an_script **script) {
    if (!script) {
        return AN_SCRIPT_INVALID;
    }
    *script = NULL;
    if (arena_bytes < MIN_ARENA_BYTES || emit_capacity < 1 || trace_capacity < 1) {
        return AN_SCRIPT_INVALID;
    }
    an_script *s = calloc(1, sizeof(*s));
    if (!s) {
        return AN_SCRIPT_OUT_OF_MEMORY;
    }
    s->memory_bytes = arena_bytes;
    s->memory = malloc(arena_bytes);
    s->emit = malloc((size_t)emit_capacity * AN_SCRIPT_EMIT_WIDTH * sizeof(double));
    s->trace = malloc((size_t)trace_capacity * AN_SCRIPT_TRACE_LINE_MAX);
    if (!s->memory || !s->emit || !s->trace) {
        an_script_destroy(s);
        return AN_SCRIPT_OUT_OF_MEMORY;
    }
    /* Touch every page now, so the audio thread never faults one in. */
    memset(s->memory, 0, arena_bytes);
    s->emit_capacity = emit_capacity;
    s->trace_capacity = (uint32_t)trace_capacity;
    s->seed = random_seed;
    clear_buffers(s);
    if (start_runtime(s) != 0) {
        an_script_destroy(s);
        return AN_SCRIPT_OUT_OF_MEMORY;
    }
    *script = s;
    return AN_SCRIPT_OK;
}

void an_script_destroy(an_script *script) {
    if (!script) {
        return;
    }
    /* Everything the runtime holds lives in the arena: dropping the arena frees it all. */
    free(script->memory);
    free(script->emit);
    free(script->trace);
    free(script);
}

int32_t an_script_compile(an_script *script, const char *source, int32_t source_length, const char *name) {
    if (!script || !script->ctx || !source || source_length < -1) {
        return AN_SCRIPT_INVALID;
    }
    size_t length = source_length < 0 ? strlen(source) : (size_t)source_length;
    /* JS_Eval wants a terminating NUL after the source: a copy, in the arena like everything else. */
    char *copy = arena_malloc(&script->heap, length + 1);
    if (!copy) {
        copy_text(script->error, ERROR_MAX, "out of memory: the script does not fit its arena");
        script->error_line = 0;
        return AN_SCRIPT_OUT_OF_MEMORY;
    }
    memcpy(copy, source, length);
    copy[length] = '\0';
    script->error[0] = '\0';
    script->error_line = 0;
    begin(script, script->control_budget_us);
    JSValue value = JS_Eval(script->ctx, copy, length, name ? name : "Script", JS_EVAL_TYPE_GLOBAL);
    arena_free(&script->heap, copy);
    int32_t result = AN_SCRIPT_OK;
    if (JS_IsException(value)) {
        result = take_exception(script);
    } else {
        JS_FreeValue(script->ctx, value);
    }
    return end(script, result);
}

int32_t an_script_call(an_script *script, const char *name, const double *args, int32_t argc, int64_t budget_us,
                       double *result) {
    if (!script || !script->ctx || !name || argc < 0 || argc > MAX_ARGS || (argc > 0 && !args)) {
        return AN_SCRIPT_INVALID;
    }
    if (result) {
        *result = NAN;
    }
    JSContext *ctx = script->ctx;
    begin(script, budget_us);
    JSValue global = JS_GetGlobalObject(ctx);
    JSValue function = JS_GetPropertyStr(ctx, global, name);
    int32_t status = AN_SCRIPT_OK;
    if (JS_IsException(function)) {
        status = take_exception(script);
    } else if (!JS_IsFunction(ctx, function)) {
        status = AN_SCRIPT_NO_FUNCTION;
    } else {
        JSValue argv[MAX_ARGS];
        for (int32_t i = 0; i < argc; i++) {
            argv[i] = JS_NewFloat64(ctx, args[i]);
        }
        JSValue value = JS_Call(ctx, function, global, argc, argv);
        if (JS_IsException(value)) {
            status = take_exception(script);
        } else {
            if (result && JS_IsNumber(value)) {
                JS_ToFloat64(ctx, result, value);
            } else if (result && JS_IsBool(value)) {
                *result = JS_ToBool(ctx, value) ? 1 : 0;
            }
            JS_FreeValue(ctx, value);
        }
    }
    JS_FreeValue(ctx, function);
    JS_FreeValue(ctx, global);
    return end(script, status);
}

int32_t an_script_set_global(an_script *script, const char *name, double value) {
    if (!script || !script->ctx || !name) {
        return AN_SCRIPT_INVALID;
    }
    begin(script, 0);
    JSValue global = JS_GetGlobalObject(script->ctx);
    int32_t status = AN_SCRIPT_OK;
    if (JS_SetPropertyStr(script->ctx, global, name, JS_NewFloat64(script->ctx, value)) < 0) {
        status = take_exception(script);
    }
    JS_FreeValue(script->ctx, global);
    return end(script, status);
}

int32_t an_script_get_global(an_script *script, const char *name, double *value) {
    if (!script || !script->ctx || !name || !value) {
        return AN_SCRIPT_INVALID;
    }
    begin(script, 0);
    JSValue global = JS_GetGlobalObject(script->ctx);
    JSValue v = JS_GetPropertyStr(script->ctx, global, name);
    int32_t status = AN_SCRIPT_OK;
    if (JS_IsException(v)) {
        status = take_exception(script);
    } else if (JS_IsUndefined(v)) {
        status = AN_SCRIPT_INVALID;
    } else if (JS_ToFloat64(script->ctx, value, v) < 0) {
        status = take_exception(script);
    }
    JS_FreeValue(script->ctx, v);
    JS_FreeValue(script->ctx, global);
    return end(script, status);
}

int32_t an_script_eval_json(an_script *script, const char *expression, char *buffer, int32_t capacity) {
    if (!script || !script->ctx || !expression || !buffer || capacity < 1) {
        return AN_SCRIPT_INVALID;
    }
    JSContext *ctx = script->ctx;
    buffer[0] = '\0';
    begin(script, script->control_budget_us);
    JSValue value = JS_Eval(ctx, expression, strlen(expression), "<eval>", JS_EVAL_TYPE_GLOBAL);
    if (JS_IsException(value)) {
        return end(script, take_exception(script));
    }
    JSValue json = JS_JSONStringify(ctx, value, JS_UNDEFINED, JS_UNDEFINED);
    JS_FreeValue(ctx, value);
    if (JS_IsException(json)) {
        return end(script, take_exception(script));
    }
    int32_t status = 0;
    size_t length = 0;
    const char *text = JS_IsUndefined(json) ? "null" : JS_ToCStringLen(ctx, &length, json);
    if (JS_IsUndefined(json)) {
        length = 4;
    }
    if (!text) {
        status = take_exception(script);
    } else {
        size_t copied = length < (size_t)capacity - 1 ? length : (size_t)capacity - 1;
        memcpy(buffer, text, copied);
        buffer[copied] = '\0';
        status = length > INT32_MAX ? INT32_MAX : (int32_t)length;
        if (!JS_IsUndefined(json)) {
            JS_FreeCString(ctx, text);
        }
    }
    JS_FreeValue(ctx, json);
    return end(script, status);
}

int32_t an_script_read_emitted(an_script *script, double *values, int32_t capacity, int32_t *dropped) {
    if (!script || (capacity > 0 && !values) || capacity < 0) {
        return AN_SCRIPT_INVALID;
    }
    int32_t count = script->emit_count < capacity ? script->emit_count : capacity;
    size_t width = AN_SCRIPT_EMIT_WIDTH;
    if (count > 0) {
        memcpy(values, script->emit, (size_t)count * width * sizeof(double));
        memmove(script->emit, script->emit + (size_t)count * width,
                (size_t)(script->emit_count - count) * width * sizeof(double));
    }
    script->emit_count -= count;
    if (dropped) {
        *dropped = script->emit_dropped;
    }
    script->emit_dropped = 0;
    return count;
}

int32_t an_script_read_trace(an_script *script, char *buffer, int32_t capacity, int32_t *dropped) {
    if (!script) {
        return AN_SCRIPT_INVALID;
    }
    if (dropped) {
        *dropped = atomic_exchange_explicit(&script->trace_dropped, 0, memory_order_relaxed);
    }
    if (!buffer || capacity < 1) {
        return 0; /* only the dropped count */
    }
    uint32_t tail = atomic_load_explicit(&script->trace_tail, memory_order_relaxed);
    uint32_t head = atomic_load_explicit(&script->trace_head, memory_order_acquire);
    if (head == tail) {
        buffer[0] = '\0';
        return 0;
    }
    const char *line = script->trace + (size_t)(tail % script->trace_capacity) * AN_SCRIPT_TRACE_LINE_MAX;
    copy_text(buffer, (size_t)capacity, line);
    atomic_store_explicit(&script->trace_tail, tail + 1, memory_order_release);
    return (int32_t)strlen(buffer);
}

int32_t an_script_last_error(const an_script *script, char *buffer, int32_t capacity, int32_t *line) {
    if (!script) {
        return AN_SCRIPT_INVALID;
    }
    if (line) {
        *line = script->error_line;
    }
    if (buffer && capacity > 0) {
        copy_text(buffer, (size_t)capacity, script->error);
    }
    return (int32_t)strlen(script->error);
}

void an_script_collect(an_script *script) {
    if (script && script->rt) {
        JS_UpdateStackTop(script->rt);
        JS_RunGC(script->rt);
    }
}

size_t an_script_memory_used(const an_script *script) { return script ? script->heap.used : 0; }

size_t an_script_arena_bytes(const an_script *script) { return script ? script->heap.size : 0; }

int32_t an_script_reset(an_script *script) {
    if (!script) {
        return AN_SCRIPT_INVALID;
    }
    clear_buffers(script);
    /* The old runtime lives in the arena: re-initialising the arena discards it. */
    return start_runtime(script) == 0 ? AN_SCRIPT_OK : AN_SCRIPT_OUT_OF_MEMORY;
}

void an_script_set_budget(an_script *script, int64_t budget_us) {
    if (script) {
        script->control_budget_us = budget_us > 0 ? budget_us : 0;
    }
}

int32_t an_script_check(const an_script *script) {
    return script && arena_check(&script->heap) ? AN_SCRIPT_OK : AN_SCRIPT_INVALID;
}

const char *an_script_version(void) { return AN_SCRIPT_UPSTREAM_VERSION; }
