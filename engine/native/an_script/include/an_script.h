/*
 * an_script: a sandboxed JavaScript engine behind a C ABI, for Anode's Scripter MIDI effect (docs/adr/0009-scripter.md,
 * docs/08-phase-7.md). Embeds QuickJS-ng (MIT), pinned by commit and fetched by engine/native/build.sh.
 * SPDX-License-Identifier: GPL-3.0-or-later
 *
 * Implemented in src/an_script.c over the arena allocator in src/arena.c (two-level segregated fits: constant-time
 * malloc and free, no locks).
 *
 * Each instance owns one QuickJS runtime and context whose allocations come from a fixed arena given at creation:
 * nothing reaches the system allocator after an_script_create, and an exhausted arena is an error
 * (AN_SCRIPT_OUT_OF_MEMORY), never growth. There are no std or os modules and no I/O. The context defines two native
 * globals for the Scripter prelude that workstream D writes in JavaScript:
 *   __trace(text)        appends one line (the text, converted to a string, cut at AN_SCRIPT_TRACE_LINE_MAX - 1 bytes)
 *                        to the trace ring; when the ring is full the line is dropped and counted;
 *   __emit(a, ..., h)    appends one record of AN_SCRIPT_EMIT_WIDTH numbers (missing ones are 0) to the emit buffer,
 *                        which the host drains with an_script_read_emitted after each call (the Scripter defines the
 *                        fields: Anode.Dsp/MidiEffects/Scripter/ScriptPrelude.cs); when the buffer is full the record is
 *                        dropped and counted (__emit then returns false).
 * Math.random is replaced by a xorshift64* generator seeded from the seed given at creation, so a run is deterministic.
 * The context has the base objects, Date, eval, RegExp, JSON, Map/Set and typed arrays; no Promise, Proxy, WeakRef or
 * modules. Recursion is limited to 256 KiB of the calling thread's stack (an error past it).
 *
 * Threading: create, destroy, compile, collect and reset run on the control thread. call, the global accessors,
 * eval_json and read_emitted may run on the real-time thread: they allocate only from the arena, and the garbage
 * collector never runs during them (it runs in an_script_collect). Apart from the trace ring, an instance is used by
 * one thread at a time: the host serialises the control thread's calls with the real-time thread's. The trace ring is
 * single-producer (the thread that calls) single-consumer (an_script_read_trace): lock-free, so those two may overlap.
 *
 * Error lines: the line reported for an error is the first frame of its stack in a script whose name does not start
 * with '<': the host compiles its own code (the prelude) under such a name, so an error raised in the prelude on
 * behalf of the user's script reports the user's line. For an interruption the line is QuickJS's last recorded
 * position, which in a tight loop can be the statement before it.
 *
 * Strings are UTF-8. A length of -1 means NUL-terminated.
 */
#ifndef AN_SCRIPT_H
#define AN_SCRIPT_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#define AN_SCRIPT_API __declspec(dllexport)
#else
#define AN_SCRIPT_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct an_script an_script;

/* Results. Every function that returns int32_t returns one of these (or a count, where it says so). */
#define AN_SCRIPT_OK 0
#define AN_SCRIPT_ERROR (-1)         /* a syntax error or an uncaught exception: an_script_last_error has the message */
#define AN_SCRIPT_INTERRUPTED (-2)   /* the call ran past its budget and was stopped */
#define AN_SCRIPT_OUT_OF_MEMORY (-3) /* the arena is exhausted */
#define AN_SCRIPT_NO_FUNCTION (-4)   /* no global function of that name */
#define AN_SCRIPT_INVALID (-5)       /* a bad argument */

#define AN_SCRIPT_DEFAULT_ARENA_BYTES (4u * 1024u * 1024u) /* ADR-0009: 4 MiB */
#define AN_SCRIPT_EMIT_WIDTH 8                             /* numbers per __emit record */
#define AN_SCRIPT_TRACE_LINE_MAX 256                       /* bytes per trace line, the terminating NUL included */

/* Creates an instance whose runtime lives in an arena of arena_bytes (at least 256 KiB), with Math.random seeded by
 * random_seed, and room for emit_capacity __emit records and trace_capacity trace lines. AN_SCRIPT_OK and *script on
 * success; AN_SCRIPT_OUT_OF_MEMORY when the arena or the buffers cannot be allocated, or the arena cannot hold an empty
 * context. */
AN_SCRIPT_API int32_t an_script_create(size_t arena_bytes, uint64_t random_seed, int32_t emit_capacity,
                                       int32_t trace_capacity, an_script **script);

AN_SCRIPT_API void an_script_destroy(an_script *script);

/* Compiles source and runs it as a global script (top-level code runs now, under the control budget of
 * an_script_set_budget; its function declarations become globals). It may be called more than once: the prelude, then
 * the user's script. On AN_SCRIPT_ERROR, an_script_last_error has the message and the line in this source. name labels
 * it in messages ("Script"). */
AN_SCRIPT_API int32_t an_script_compile(an_script *script, const char *source, int32_t source_length,
                                        const char *name);

/* Calls global function name with argc numbers, interrupting it after budget_us microseconds of wall time (0: no
 * budget). Its result, converted to a number (NaN when it is not one), goes to *result when result is not NULL.
 * AN_SCRIPT_OK, or AN_SCRIPT_ERROR (an exception: an_script_last_error has it and the line), AN_SCRIPT_INTERRUPTED,
 * AN_SCRIPT_OUT_OF_MEMORY or AN_SCRIPT_NO_FUNCTION. After an interruption or out-of-memory the instance stays usable,
 * but its script's state may be inconsistent: the host bypasses it until it is reset and compiled again. */
AN_SCRIPT_API int32_t an_script_call(an_script *script, const char *name, const double *args, int32_t argc,
                                     int64_t budget_us, double *result);

/* Number globals (parameter values, timing info for the prelude): set creates or replaces global name; get reads it,
 * converted to a number (AN_SCRIPT_INVALID when it does not exist). */
AN_SCRIPT_API int32_t an_script_set_global(an_script *script, const char *name, double value);
AN_SCRIPT_API int32_t an_script_get_global(an_script *script, const char *name, double *value);

/* Evaluates expression (under the control budget of an_script_set_budget) and writes JSON.stringify of its value,
 * NUL-terminated, into buffer: how the host reads PluginParameters after a compile, and after UpdatePluginParameters
 * on the real-time thread (it allocates only from the arena). Returns the JSON's length in bytes (not counting the
 * NUL; when it is capacity or more the output was cut, and a larger buffer gets it all), or a negative result. */
AN_SCRIPT_API int32_t an_script_eval_json(an_script *script, const char *expression, char *buffer, int32_t capacity);

/* Moves up to capacity __emit records (capacity * AN_SCRIPT_EMIT_WIDTH numbers) into values, oldest first, and
 * returns how many it moved. *dropped (when not NULL) gets the records dropped since the previous read. */
AN_SCRIPT_API int32_t an_script_read_emitted(an_script *script, double *values, int32_t capacity, int32_t *dropped);

/* Takes the oldest trace line into buffer (NUL-terminated, cut to capacity - 1 bytes) and returns its length, or 0 when
 * the ring is empty (an empty line reads as one space). With a NULL buffer or a capacity of 0 it takes no line and
 * returns 0, reporting only the dropped count. *dropped (when not NULL) gets the lines dropped since the previous read. */
AN_SCRIPT_API int32_t an_script_read_trace(an_script *script, char *buffer, int32_t capacity, int32_t *dropped);

/* The last error's message (NUL-terminated, cut to capacity - 1 bytes) and its 1-based line (0: unknown); returns the
 * message's length, 0 when there was no error since the last compile or reset. */
AN_SCRIPT_API int32_t an_script_last_error(const an_script *script, char *buffer, int32_t capacity, int32_t *line);

/* Runs the garbage collector (control thread, between blocks). */
AN_SCRIPT_API void an_script_collect(an_script *script);

/* Bytes of the arena in use, and its size. */
AN_SCRIPT_API size_t an_script_memory_used(const an_script *script);
AN_SCRIPT_API size_t an_script_arena_bytes(const an_script *script);

/* Discards every global, the compiled scripts, the emit buffer, the trace ring and the last error: a fresh context in
 * the same arena, Math.random reseeded with the creation seed, ready to compile again. */
AN_SCRIPT_API int32_t an_script_reset(an_script *script);

/* The budget, in microseconds of wall time, for compile and eval_json (0, the default: none). An interrupted compile
 * returns AN_SCRIPT_INTERRUPTED with the line it was running. */
AN_SCRIPT_API void an_script_set_budget(an_script *script, int64_t budget_us);

/* Walks the arena and checks its bookkeeping: AN_SCRIPT_OK when it is consistent (tests). */
AN_SCRIPT_API int32_t an_script_check(const an_script *script);

/* The pinned upstream: "quickjs-ng 0.17.0+6d46d07". */
AN_SCRIPT_API const char *an_script_version(void);

#ifdef __cplusplus
}
#endif

#endif
