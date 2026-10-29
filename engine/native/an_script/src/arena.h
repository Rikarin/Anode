/*
 * A fixed-arena allocator for an_script's QuickJS runtime: two-level segregated fits (TLSF), so malloc, free and
 * realloc take constant time, never lock and never reach the system allocator. Not thread-safe: an instance's runtime
 * is used by one thread at a time (include/an_script.h).
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#ifndef AN_SCRIPT_ARENA_H
#define AN_SCRIPT_ARENA_H

#include <stddef.h>
#include <stdint.h>

#define ARENA_FL_COUNT 40
#define ARENA_SL_COUNT 16

typedef struct arena_block arena_block;

typedef struct arena {
    char *base;  /* the arena's first block */
    size_t size; /* usable bytes from base */
    size_t used; /* bytes in used blocks, headers included */
    size_t peak;
    uint64_t fl_bitmap;
    uint32_t sl_bitmap[ARENA_FL_COUNT];
    arena_block *heads[ARENA_FL_COUNT][ARENA_SL_COUNT];
} arena;

/* Makes memory[0 .. bytes) one free block (discarding whatever it held). False when it is too small. */
int arena_init(arena *a, void *memory, size_t bytes);

void *arena_malloc(arena *a, size_t size);
void *arena_calloc(arena *a, size_t count, size_t size);
void arena_free(arena *a, void *ptr);
/* realloc semantics: NULL ptr mallocs; size 0 frees and returns NULL; on failure the block is left as it was. */
void *arena_realloc(arena *a, void *ptr, size_t size);
/* The bytes usable at ptr (at least what was asked for). */
size_t arena_usable_size(const void *ptr);

/* Walks every block and checks the free lists: 1 when the arena is consistent (tests). */
int arena_check(const arena *a);

#endif
