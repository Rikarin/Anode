/*
 * Two-level segregated fits over one fixed block of memory (see arena.h).
 *
 * Every block starts with a 16-byte header: the previous physical block (meaningful only while that block is free) and
 * this block's payload size, a multiple of 16 whose low bits hold two flags (this block free, previous block free).
 * A free block's payload starts with its free-list links. A zero-size used block at the end stops merging.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#include "arena.h"

#include <string.h>

#if defined(_MSC_VER)
#include <intrin.h>
#endif

struct arena_block {
    arena_block *prev_phys;
    size_t size;
    arena_block *next_free; /* payload, while free */
    arena_block *prev_free;
};

#define ALIGN 16u
#define HEADER 16u
#define MIN_PAYLOAD 16u
#define FLAG_FREE ((size_t)1)
#define FLAG_PREV_FREE ((size_t)2)
#define FLAGS (FLAG_FREE | FLAG_PREV_FREE)
#define SL_LOG 4
#define SMALL_LIMIT ((size_t)1 << (SL_LOG + 4)) /* 256: below it, one class per 16 bytes */

_Static_assert(sizeof(void *) == 8, "an_script's arena assumes 64-bit pointers");
_Static_assert(offsetof(arena_block, next_free) == HEADER, "block header layout");
_Static_assert((1 << SL_LOG) == ARENA_SL_COUNT, "second-level count");

static int msb64(uint64_t x) {
#if defined(_MSC_VER)
    unsigned long index;
    _BitScanReverse64(&index, x);
    return (int)index;
#else
    return 63 - __builtin_clzll(x);
#endif
}

static int lsb64(uint64_t x) {
#if defined(_MSC_VER)
    unsigned long index;
    _BitScanForward64(&index, x);
    return (int)index;
#else
    return __builtin_ctzll(x);
#endif
}

static size_t block_size(const arena_block *b) { return b->size & ~FLAGS; }

static void *payload(arena_block *b) { return (char *)b + HEADER; }

static arena_block *from_payload(const void *ptr) { return (arena_block *)((char *)ptr - HEADER); }

static arena_block *next_phys(const arena_block *b) {
    return (arena_block *)((char *)b + HEADER + block_size(b));
}

static void mapping(size_t size, int *fl, int *sl) {
    if (size < SMALL_LIMIT) {
        *fl = 0;
        *sl = (int)(size / ALIGN);
    } else {
        int m = msb64(size);
        *fl = m - (SL_LOG + 4) + 1;
        *sl = (int)((size >> (m - SL_LOG)) ^ ((size_t)1 << SL_LOG));
    }
}

static void insert_free(arena *a, arena_block *b) {
    int fl, sl;
    mapping(block_size(b), &fl, &sl);
    arena_block *head = a->heads[fl][sl];
    b->next_free = head;
    b->prev_free = NULL;
    if (head) {
        head->prev_free = b;
    }
    a->heads[fl][sl] = b;
    a->fl_bitmap |= (uint64_t)1 << fl;
    a->sl_bitmap[fl] |= 1u << sl;
}

static void remove_free(arena *a, arena_block *b) {
    int fl, sl;
    mapping(block_size(b), &fl, &sl);
    if (b->prev_free) {
        b->prev_free->next_free = b->next_free;
    } else {
        a->heads[fl][sl] = b->next_free;
        if (!b->next_free) {
            a->sl_bitmap[fl] &= ~(1u << sl);
            if (!a->sl_bitmap[fl]) {
                a->fl_bitmap &= ~((uint64_t)1 << fl);
            }
        }
    }
    if (b->next_free) {
        b->next_free->prev_free = b->prev_free;
    }
}

/* A free block with room for size bytes (size already a multiple of 16), removed from its list; NULL when none. */
static arena_block *take_suitable(arena *a, size_t size) {
    if (size >= SMALL_LIMIT) {
        size += ((size_t)1 << (msb64(size) - SL_LOG)) - 1; /* round up so any block of the class fits */
    }
    int fl, sl;
    mapping(size, &fl, &sl);
    if (fl >= ARENA_FL_COUNT) {
        return NULL;
    }
    uint32_t sl_map = a->sl_bitmap[fl] & (~0u << sl);
    if (!sl_map) {
        uint64_t fl_map = fl + 1 < 64 ? a->fl_bitmap & (~(uint64_t)0 << (fl + 1)) : 0;
        if (!fl_map) {
            return NULL;
        }
        fl = lsb64(fl_map);
        sl_map = a->sl_bitmap[fl];
    }
    sl = lsb64(sl_map);
    arena_block *b = a->heads[fl][sl];
    remove_free(a, b);
    return b;
}

/* Marks a free block (not in a list) used, telling its successor. */
static void mark_used(arena *a, arena_block *b) {
    b->size &= ~FLAG_FREE;
    arena_block *next = next_phys(b);
    next->size &= ~FLAG_PREV_FREE;
    a->used += HEADER + block_size(b);
    if (a->used > a->peak) {
        a->peak = a->used;
    }
}

/* Frees what a used block b holds beyond size bytes, when that is a block's worth. */
static void trim(arena *a, arena_block *b, size_t size) {
    size_t have = block_size(b);
    if (have < size + HEADER + MIN_PAYLOAD) {
        return;
    }
    arena_block *rest = (arena_block *)((char *)b + HEADER + size);
    rest->size = (have - size - HEADER) | FLAG_FREE; /* b is used: no PREV_FREE */
    b->size = size | (b->size & FLAG_PREV_FREE);
    a->used -= HEADER + block_size(rest);
    arena_block *next = next_phys(rest);
    if (next->size & FLAG_FREE) {
        remove_free(a, next);
        rest->size += HEADER + block_size(next);
        next = next_phys(rest);
    }
    next->prev_phys = rest;
    next->size |= FLAG_PREV_FREE;
    insert_free(a, rest);
}

static size_t adjust(size_t size) {
    if (size < MIN_PAYLOAD) {
        size = MIN_PAYLOAD;
    }
    return (size + ALIGN - 1) & ~(size_t)(ALIGN - 1);
}

int arena_init(arena *a, void *memory, size_t bytes) {
    memset(a, 0, sizeof(*a));
    uintptr_t start = ((uintptr_t)memory + ALIGN - 1) & ~(uintptr_t)(ALIGN - 1);
    uintptr_t end = ((uintptr_t)memory + bytes) & ~(uintptr_t)(ALIGN - 1);
    if (end <= start || end - start < 4 * HEADER + MIN_PAYLOAD) {
        return 0;
    }
    a->base = (char *)start;
    a->size = (size_t)(end - start);
    arena_block *first = (arena_block *)a->base;
    first->prev_phys = NULL;
    first->size = (a->size - 2 * HEADER) | FLAG_FREE;
    arena_block *sentinel = next_phys(first);
    sentinel->prev_phys = first;
    sentinel->size = FLAG_PREV_FREE; /* used, size 0 */
    insert_free(a, first);
    return 1;
}

void *arena_malloc(arena *a, size_t size) {
    if (size > a->size) {
        return NULL;
    }
    size = adjust(size);
    arena_block *b = take_suitable(a, size);
    if (!b) {
        return NULL;
    }
    mark_used(a, b);
    trim(a, b, size);
    return payload(b);
}

void *arena_calloc(arena *a, size_t count, size_t size) {
    if (size && count > a->size / size) {
        return NULL;
    }
    void *p = arena_malloc(a, count * size);
    if (p) {
        memset(p, 0, count * size);
    }
    return p;
}

void arena_free(arena *a, void *ptr) {
    if (!ptr) {
        return;
    }
    arena_block *b = from_payload(ptr);
    a->used -= HEADER + block_size(b);
    b->size |= FLAG_FREE;
    if (b->size & FLAG_PREV_FREE) {
        arena_block *prev = b->prev_phys;
        remove_free(a, prev);
        prev->size += HEADER + block_size(b);
        b = prev;
    }
    arena_block *next = next_phys(b);
    if (next->size & FLAG_FREE) {
        remove_free(a, next);
        b->size += HEADER + block_size(next);
        next = next_phys(b);
    }
    next->prev_phys = b;
    next->size |= FLAG_PREV_FREE;
    insert_free(a, b);
}

void *arena_realloc(arena *a, void *ptr, size_t size) {
    if (!ptr) {
        return arena_malloc(a, size);
    }
    if (size == 0) {
        arena_free(a, ptr);
        return NULL;
    }
    if (size > a->size) {
        return NULL;
    }
    arena_block *b = from_payload(ptr);
    size_t want = adjust(size);
    size_t have = block_size(b);
    if (want <= have) {
        trim(a, b, want);
        return ptr;
    }
    arena_block *next = next_phys(b);
    if ((next->size & FLAG_FREE) && have + HEADER + block_size(next) >= want) {
        remove_free(a, next);
        size_t grown = HEADER + block_size(next);
        b->size += grown;
        a->used += grown;
        if (a->used > a->peak) {
            a->peak = a->used;
        }
        next_phys(b)->size &= ~FLAG_PREV_FREE;
        trim(a, b, want);
        return ptr;
    }
    void *moved = arena_malloc(a, size);
    if (!moved) {
        return NULL;
    }
    memcpy(moved, ptr, have);
    arena_free(a, ptr);
    return moved;
}

size_t arena_usable_size(const void *ptr) { return ptr ? block_size(from_payload(ptr)) : 0; }

int arena_check(const arena *a) {
    size_t used = 0;
    size_t free_bytes = 0;
    int prev_free = 0;
    const arena_block *prev = NULL;
    const arena_block *b = (const arena_block *)a->base;
    for (;;) {
        if (((b->size & FLAG_PREV_FREE) != 0) != prev_free) {
            return 0;
        }
        if (prev_free && b->prev_phys != prev) {
            return 0;
        }
        if (block_size(b) == 0 && !(b->size & FLAG_FREE)) {
            break; /* the sentinel */
        }
        int is_free = (b->size & FLAG_FREE) != 0;
        if (is_free && prev_free) {
            return 0; /* two free neighbours should have merged */
        }
        if (is_free) {
            free_bytes += HEADER + block_size(b);
        } else {
            used += HEADER + block_size(b);
        }
        prev_free = is_free;
        prev = b;
        b = next_phys(b);
        if ((const char *)b >= a->base + a->size) {
            return 0;
        }
    }
    size_t listed = 0;
    for (int fl = 0; fl < ARENA_FL_COUNT; fl++) {
        for (int sl = 0; sl < ARENA_SL_COUNT; sl++) {
            for (const arena_block *f = a->heads[fl][sl]; f; f = f->next_free) {
                if (!(f->size & FLAG_FREE)) {
                    return 0;
                }
                listed += HEADER + block_size(f);
            }
        }
    }
    return used == a->used && listed == free_bytes && used + free_bytes + HEADER == a->size;
}
