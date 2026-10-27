# Spike S3: bulk IPC through shared memory

- **Date:** 2026-10-27
- **Question** ([plan §12, Phase 0](../01-implementation-plan.md#phase-0-foundations-and-spikes-6-weeks), [ADR-0006](../adr/0006-bulk-ipc.md)):
  can anode-server hand anode-host a compiled playback model of about 1,000,000 events (plus audio playlists) through
  shared memory, and the host swap it in atomically, in under 10 ms end to end, with zero allocations on the audio
  thread?
- **Harness:** `engine/spikes/Anode.Spikes.BulkIpc` (`run.sh`; raw output of the run below in `results.md` there).

## Verdict

**Yes, on POSIX shared memory, with the host prefaulting and the segments reused or kept small.** For a 1,000,000-event
model (23.2 MB) on macOS:

| Way the model travels | Frame sent → audio thread uses it (p50 / p95 / max) | Including the server's write | Audio thread |
|---|---|---|---|
| **Pooled segment** (two slots, mapped once), prefault only | **0.58 / 1.33 / 2.54 ms** | **1.45 / 2.17 / 3.35 ms** | 0 slow callbacks, worst 24 µs |
| Pooled segment, checksum + full verify | 2.35 / 3.11 / 3.24 ms | 4.21 / 5.01 / 5.10 ms | 0 slow, worst 24 µs |
| **Per-track segment** (one dirty track of 128 re-sent) | **0.86 / 1.34 / 1.39 ms** | **0.88 / 1.38 / 1.42 ms** | 0–1 slow, worst 13–177 µs |
| New segment per swap, prefault only | 2.65 / 8.16 / 17.2 ms | 6.0 / 15.2 / 23.1 ms | 27 slow (up to 408 µs) |
| File in `$TMPDIR` (the only `MemoryMappedFile` option on macOS) | 34.5 / 37.4 / 43.4 ms | 38.2 / 41.2 / 47.0 ms | **a dropout every swap** (64–66 ms stalls) |

- **Zero bytes allocated on the audio thread** in every scenario (all sizes, all backends, 100 swaps each), and no late
  callback on shared memory.
- **No leaks** over 100 swaps per scenario (about 1,900 swaps in the run): one segment (or the pool's two, or 128 per
  track) mapped afterwards, host file descriptors constant, no shared-memory names left behind.
- A new segment per swap makes the 10 ms target at the median but not at the tail, and it disturbs the audio thread
  (see findings 3 and 4). Reusing segments removes both problems.
- macOS's file-backed mapping fails outright, and the reason is structural (finding 2).

## How it was measured

- **Machine:** Apple M1 Max (8 performance + 2 efficiency cores), 64 GB, macOS 27.2, 16 KB pages, .NET 10.0.403. Both
  roles published **NativeAOT**, as anode-host ships (`run.sh`; zero AOT or trim warnings).
- **Processes:** `server` spawns `host` (the same executable) and talks to it over a Unix domain socket using
  Anode.Ipc's wire format: a 4-byte little-endian length, then JSON with a `type` discriminator. The spike adds its own
  message types (`loadSegment`, `segmentLoaded`, `reset`, `stats`) in a spike-local `JsonSerializerContext`; it does
  not touch `Anode.Ipc`.
- **Model:** 128 tracks over ten minutes at 48 kHz. Events are 24-byte `PlaybackEvent`s, sorted per track. Each
  track has 64 `AudioPlaylistEntry`s (40 bytes). Totals are 10k, 100k, 1M and 5M events; the server builds the model
  in managed arrays, as a compiler would.
- **Audio thread:** a **Mach time-constraint thread**, as a CoreAudio IOProc is: period 1.333 ms (64 frames at 48 kHz),
  computation a quarter of that, woken with `mach_wait_until`. Each callback:
  - loads the root (one `Volatile.Read`);
  - for every track, binary-searches the events to the window start, sums the events in the 64-frame window, and
    binary-searches the playlist entry playing at the window start;
  - counts its allocations with `GC.GetAllocatedBytesForCurrentThread()` and its duration.
- **One swap:**
  1. The server writes the model into a segment and sends `loadSegment {segmentId, path, length, version, track,
     commit}`.
  2. The host maps the segment, validates the layout and prefaults it (optionally also verifying the checksum and the
     sort order, and `mlock`ing it). It then builds a new root and publishes it with `Interlocked.Exchange`.
  3. The host waits until the audio thread has rendered with the new root and every callback that could hold the old
     one has finished. It unmaps what only the old root used, then replies.
  4. The server removes the segment's name.

  All timestamps are `Stopwatch` ticks, which are one system-wide monotonic clock on macOS, Linux and Windows, so they
  compare across the two processes.
- **Columns:**
  - *Write*: server, segment create + copy + checksum.
  - *Host work*: frame sent → root published.
  - *Frame → first use*: frame sent → start of the first callback that renders the new model. It includes waiting
    for the next buffer boundary, 0–1.33 ms. A random pause before each swap spreads the swaps over the callback
    phase.
  - *End to end*: server starts writing → first use.
  - *Slow callback*: one that took more than 10 % of the period. *Late*: one that started more than a period late
    (a dropout on a device).
- **Runs:** 3 warm-up swaps plus 100 measured swaps per scenario; each edit changes one random track. Earlier
  development runs of the same matrix gave the same picture; the tails of the fresh-segment rows varied the most.

## Results

Milliseconds, p50 / p95 / max over 100 swaps. "Shm" is a POSIX shared-memory object (`shm_open` + `mmap`); "file" is
`MemoryMappedFile` over a file in `$TMPDIR` (APFS).

| Backend, segments, validation | Events | Segment | Write | Host work | **Frame → first use** | **End to end** | Slow / late callbacks | Worst callback |
|---|---|---|---|---|---|---|---|---|
| shm, new per swap, checksum + verify | 10k | 0.55 MB | 0.15 | 0.19 | 0.88 / 3.54 / 9.31 | 1.03 / 4.12 / 9.47 | 0 / 0 | 17 µs |
| shm, new per swap, checksum + verify | 100k | 2.6 MB | 0.57 | 0.59 | 1.45 / 2.78 / 26.9 | 2.07 / 4.09 / 27.8 | 0 / 0 | 77 µs |
| shm, new per swap, checksum + verify | 1M | 23.2 MB | 4.47 | 4.32 | 4.96 / 9.15 / 10.9 | 9.67 / 15.9 / 22.5 | 0 / 0 | 31 µs |
| shm, new per swap, checksum + verify | 5M | 115 MB | 23.7 | 17.0 | 17.9 / 23.2 / 41.6 | 42.1 / 65.2 / 87.0 | 163 / 0 | 719 µs |
| shm, new per swap, prefault only | 1M | 23.2 MB | 3.27 | 1.82 | 2.65 / 8.16 / 17.2 | 6.02 / 15.2 / 23.1 | 27 / 0 | 408 µs |
| shm, new per swap, prefault only | 5M | 115 MB | 14.7 | 6.83 | 7.49 / 11.4 / 19.0 | 22.5 / 30.9 / 48.2 | 136 / 0 | 741 µs |
| shm, new per swap, prefault + mlock | 1M | 23.2 MB | 2.48 | 2.73 | 3.30 / 3.92 / 4.36 | 5.77 / 6.50 / 6.88 | 0 / 0 | 22 µs |
| **shm, pooled, checksum + verify** | 1M | 23.2 MB | 1.87 | 1.81 | 2.35 / 3.11 / 3.24 | 4.21 / 5.01 / 5.10 | 0 / 0 | 24 µs |
| **shm, pooled, prefault only** | 1M | 23.2 MB | 0.85 | 0.07 | **0.58 / 1.33 / 2.54** | **1.45 / 2.17 / 3.35** | 0 / 0 | 24 µs |
| shm, pooled, checksum + verify | 5M | 115 MB | 9.05 | 8.65 | 9.42 / 10.0 / 12.9 | 18.5 / 19.2 / 22.0 | 0 / 0 | 58 µs |
| shm, pooled, prefault only | 5M | 115 MB | 3.92 | 0.15 | 0.87 / 1.45 / 1.51 | 4.82 / 5.40 / 7.99 | 0 / 0 | 38 µs |
| **shm, per track, checksum + verify** | 1M | 0.18 MB | 0.03 | 0.06 | 0.85 / 1.35 / 1.39 | 0.88 / 1.38 / 1.43 | 0 / 0 | 13 µs |
| shm, per track, prefault only | 1M | 0.18 MB | 0.03 | 0.05 | 0.86 / 1.34 / 1.39 | 0.88 / 1.38 / 1.42 | 1 / 0 | 177 µs |
| shm, per track, checksum + verify | 5M | 0.9 MB | 0.15 | 0.16 | 0.94 / 1.41 / 3.27 | 1.11 / 1.58 / 3.40 | 1 / 0 | 267 µs |
| shm, per track, prefault only | 5M | 0.9 MB | 0.11 | 0.09 | 0.93 / 1.37 / 1.44 | 1.04 / 1.50 / 1.56 | 0 / 0 | 31 µs |
| file, new per swap, checksum + verify | 1M | 23.2 MB | 4.79 | 3.09 | 33.2 / 36.7 / 41.8 | 38.0 / 41.3 / 46.6 | 102 / 102 | 64 ms |
| file, new per swap, prefault only | 1M | 23.2 MB | 3.76 | 1.35 | 34.5 / 37.4 / 43.4 | 38.2 / 41.2 / 47.0 | 102 / 102 | 66 ms |
| file, new per swap, checksum + verify | 5M | 115 MB | 27.6 | 18.1 | 83.3 / 95.5 / 104 | 113 / 123 / 142 | 116 / 107 | 194 ms |
| file, new per swap, prefault only | 5M | 115 MB | 17.5 | 6.46 | 94.6 / 103 / 110 | 113 / 122 / 127 | 110 / 103 | 184 ms (1.04 s in another run) |

Per-track loading of the whole model first (128 segments, one swap): **11–12 ms at 1M events, 29–43 ms at 5M.**

**Step costs at 1M events (23.2 MB, p50):**

| Step | Cost | Notes |
|---|---|---|
| Frame over the socket | 0.05–0.09 ms | |
| `mmap` of a shm object | 0.02–0.03 ms | 0.11 ms through `MemoryMappedFile` on a file |
| Layout validation + prefault, fresh segment | 1.2–1.8 ms | About 0.7 µs per 16 KB page |
| Layout validation + prefault, pooled segment | 0.02 ms | Already mapped |
| Checksum (4-lane hardware CRC32C) | ~1.0 ms on the server | About 22 GB/s |
| Checksum + sort-order check | ~1.7–1.95 ms on the host | |
| Server copy, fresh segment | 3.2 ms | Zero-fill page faults |
| Server copy, pooled segment | 0.85 ms | |
| Server cleanup (unmap + unlink) | 0.4 ms (shm), 8 ms (file) | Done after the reply, off the swap's path |
| `mlock` | 1.45 ms | |
| Waiting for the next callback | 0.5–0.8 ms | Bounded by the 1.33 ms period |

**Host health:**
- Audio-thread allocation: 0 B in every scenario.
- Host GCs: at most one gen0 per scenario. It comes from the control thread (JSON, roots, sets), not the audio thread.
- Host resident memory goes back to 9–16 MB after every scenario's reset; it does not grow with the number of swaps.
- Host file descriptors: 7 before and after every shm scenario.

## Findings

1. **The swap itself is cheap; moving the bytes is what costs.**
   - Publishing a root is one `Interlocked.Exchange`. The audio thread picks it up at the next callback (≤ 1.33 ms).
   - The grace period is one more callback. It has already passed by the time the first use is seen, because a
     single audio thread starts its next callback only after the previous one completes.
   - What scales with the model is page faults (server zero-fill, host prefault), the copy, and optional verification.
     All three run off the audio thread, so the question becomes how many bytes move per edit.
2. **On macOS, a file-backed mapping cannot carry audio-thread data.** It is the only cross-platform
   `MemoryMappedFile` route, because .NET supports named mappings only on Windows. Each swap stalled the real-time
   thread for 30–100 ms, and once for 1.04 s.
   - The stall is not tied to the host's own work. It lands while the host is idle (the slow-callback attribution
     puts 102 of 102 there) and on the model the audio thread had already been rendering.
   - In an earlier variant, the server unmapped its view *before* sending the frame. The cost then moved to the
     host's prefault instead: 31–33 ms for 23 MB, about 21 µs per page, with a clean audio thread.
   - This is consistent with the unified buffer cache cleaning the server's dirty file pages when its view goes away,
     which disconnects every other mapping of them. The next touch of each page is then a slow fault, and if the
     audio thread is the one to touch it, the audio thread pays.
   - The server's unmap of a dirty file mapping also costs 8–50 ms.
   - A POSIX shared-memory object has none of this: no backing file, no cleaning, a fault in about 0.7 µs per page.
3. **A new segment per swap disturbs the audio thread at large sizes, even on shared memory.** At 1M–5M events,
   callbacks ran at 30–55 % of the budget (up to 741 µs, against about 20 µs normally). There were no late callbacks
   on a real-time thread, but a loaded engine has no such headroom.
   - The attribution puts these slow callbacks in two places:
     - while the host unmaps the retired 115 MB segment (95–126 per 100 swaps);
     - while the server is faulting in the next segment (19–35).
   - The cause inside the kernel was not isolated: VM map or pmap work on the shared object, or memory-system
     contention.
   - With **pooled segments** (map once, write in place, never unmap during playback) there were **zero** slow
     callbacks at 1M and 5M, worst 24–58 µs. With per-track segments, almost none.
   - `mlock` also gave zero in the final run, but 3 in an earlier one. It costs 1.45 ms and does not stop the server's
     faults.
4. **Fresh segments put the tail over 10 ms.** At 1M events the median end-to-end time is 6–10 ms, but p95 reaches
   15–16 ms and the maximum 22–23 ms. Both the server's zero-fill faults and the host's prefault scale with size and
   with whatever else the machine is doing.
   - Pooling removes the zero-fill (copy 3.2 → 0.85 ms) and the prefault (1.6 → 0.02 ms). The whole 1M swap
     becomes 1.45 ms at the median and 3.35 ms at worst.
5. **Per-track segments make the swap independent of the project's size.** An edit recompiles the dirty tracks
   (plan §3.3), and only those travel.
   - One track of a 1M-event, 128-track project is 0.18 MB. Its swap takes 0.9 ms at the median and 1.4 ms at worst,
     mostly the wait for the next buffer. At 5M events it is 1.0–1.1 ms.
   - The root is an array of per-track views, each with the segment that owns it. A root mixes segments freely, and
     a segment is unmapped when the last root using it is retired, tracked by reference counts on the control thread.
   - Loading a whole project as 128 segments takes 11–43 ms. That is a project-open cost, not an edit cost.
6. **Validation:**
   - **Layout validation is mandatory and cheap.** The host recomputes the canonical offsets from the header's counts
     and checks every track entry against them, so no header value can make the audio thread read outside the
     mapping. It costs O(tracks) and is noise next to the prefault.
   - **The prefault is mandatory.** Without it, the audio thread takes the page faults of every page it reaches.
   - **Checksum and sort-order checks** catch server bugs, not transport errors: the socket's send and receive order
     the shared writes and reads. They cost about 1 ms on the server and 1.7–1.9 ms on the host per 23 MB. They are
     affordable at 1M pooled (4.2 ms end to end) and in per-track swaps, but not at 5M whole-model (18.5 ms).
     Recommendation: always on in debug builds and tests; in release, on for per-track segments and off for
     whole-model segments above a few MB.
7. **The audio thread does not allocate, by construction.**
   - The root is a managed object, but the audio thread only reads it (references, arrays of pointer structs) and
     stamps two `long` fields.
   - Everything allocating (roots, reference sets, JSON) happens on the host's control thread. That thread allocated
     enough for one gen0 collection per few hundred swaps. Production should use Cathode's reused frame buffers and
     span-based parsing for these messages, per ADR-0001.

## Recommended segment format

One format serves a whole model and a single track, which is the same format with one track entry. It is little-endian
and native-aligned, every array starts on a 64-byte boundary, and it contains no pointers.

| Offset | Content |
|---|---|
| 0 | `SegmentHeader`, 88 B: `Magic` = `ANPM`, `Format` = 1, `HeaderSize`, `TotalLength`, `ModelVersion`, `TrackCount`, `Flags` (bit 0: checksum present), `TrackTableOffset`, `EventsOffset`, `EventCount`, `PlaylistOffset`, `PlaylistCount`, `LengthSamples`, `Checksum` |
| align 64 | `TrackEntry[TrackCount]`, 40 B each: `TrackId`, `Flags`, `EventStart`, `EventCount`, `PlaylistStart`, `PlaylistCount` (element indices) |
| align 64 | `PlaybackEvent[EventCount]`, 24 B each: `SamplePos` (long), `Track`, `Kind`, `Data1`, `Data2`, pad, `Value` (float), pad. Sorted by `SamplePos` within each track's slice |
| align 64 | `AudioPlaylistEntry[PlaylistCount]`, 40 B each: `Start`, `FileOffset`, `Length` (long), `FileId`, `Gain`, `FadeIn`, `FadeOut`. Sorted and non-overlapping per track |

- **Validation:**
  - The offsets must equal the ones computed from the counts.
  - `TotalLength` must not exceed the mapping, so a pooled slot can hold a smaller model.
  - Every track entry must lie inside its array.
  - The checksum (CRC32C over everything after the header) and the sort order are checked when the flag or the build
    asks for it.
- **Phase 1 additions:** automation ramp arrays, the tempo map, and a per-track generation, so that a root can say
  which compile each track came from.
- **Padding is explicit,** so the checksum never covers uninitialised bytes.

## Recommended lifetime protocol

1. **Server allocates.**
   - Whole models: from a pool of two slots per size class, mapped once by both sides and reallocated larger when
     the model outgrows them.
   - Track edits: a new small segment per dirty track.
   - It writes, then sends `loadSegment {segmentId, name, length, version, track, commit}`. The name is a shm name on
     macOS, a `/dev/shm` path on Linux, or `Local\…` on Windows.
2. **Host maps** (or reuses its mapping of a pooled slot), validates the layout, prefaults, and verifies if
   configured.
   - It stages track loads until one arrives with `commit`, so a multi-track recompile lands in one swap.
   - On commit it builds a new root and publishes it with `Interlocked.Exchange`. The exchange is a full fence, so
     the next step's read cannot move above it.
3. **Grace (RCU).** The audio thread runs `Interlocked.Increment(started)` before it loads the root and
   `Volatile.Write(completed)` after it finishes rendering. After the exchange, the host reads `g = started` and waits
   until `completed ≥ g`; no callback can hold the old root after that.
   - This is one callback at most.
   - With Phase 1's worker threads, the counters belong to the cycle: the device callback increments `started`
     before handing out work and sets `completed` once the workers have finished. Workers never load the root
     themselves.
4. **Release.** Once grace has passed, the control thread drops the old root's references. A per-track segment with
   no references left is unmapped there; a pooled slot stays mapped. Nothing is freed on the audio thread.
5. **Reply.** The host answers `segmentLoaded {segmentId, released: [...]}`. Its arrival tells the server two things:
   - the host has its own mapping, so the server unlinks the name immediately (on Windows it closes its handle);
   - the slots listed in `released` are free to be written again.

   No name outlives the handshake, so a crash of either side cannot leave named memory behind. A crashed server
   leaves the host playing its current root; a restarted host gets the model re-sent on reconnect.
6. **Off the critical path:** the server unmaps or unlinks after the reply, and the host unmaps retired per-track
   segments after the reply too.

## Cross-platform notes

- **macOS (measured).**
  - Use POSIX shared memory (`shm_open`, `ftruncate`, `mmap`) through `LibraryImport`, which is AOT-safe. Do not use
    `MemoryMappedFile` over a file (finding 2): macOS has no tmpfs, `$TMPDIR` is APFS, and .NET does not support
    named mappings on Unix.
  - `shm_open` is **variadic** (the mode). On Apple arm64, variadic arguments are passed on the stack, so a plain
    three-argument P/Invoke passes garbage as the mode. The harness passes the mode as the ninth argument, after six
    unused register arguments. Production should wrap `shm_open` in the native shim instead (a C function with a
    fixed signature next to `cth_audio`).
  - Names are limited to 31 characters (`PSHMNAMLEN`).
  - A shm object can be sized only once, which suits fixed-capacity pool slots.
  - shm objects cannot be listed, so a leaked name is invisible. Unlinking right after the host maps is what keeps
    leaks from happening, and names should carry the server's pid.
  - The simulated thread uses `THREAD_TIME_CONSTRAINT_POLICY`. With a spinning normal-priority thread (an earlier
    variant), OS preemption showed up as late callbacks of up to 5 ms in several scenarios and hid the effects
    measured here.
- **Linux (not measured: no Linux machine, and Docker was not running).**
  - `MemoryMappedFile.CreateFromFile` on a file in `/dev/shm` is tmpfs-backed, so it needs no P/Invoke and should
    behave like macOS's shm objects (no writeback, no cleaning). This is the harness's default on Linux.
  - Alternatives:
    - `memfd_create` plus passing the descriptor over the Unix socket (`SCM_RIGHTS`) has no names at all, but needs
      `sendmsg` P/Invoke.
    - `MAP_POPULATE` would replace the read prefault, but `MemoryMappedFile` does not expose it.
  - Docker's default `/dev/shm` is 64 MB: too small for a 5M-event pool (2 × 115 MB). The headless server image
    needs `--shm-size`.
  - `RLIMIT_MEMLOCK` is often 64 KB–8 MB, so do not rely on `mlock`.
  - **Run the harness on Linux** (`run.sh` works unchanged) before ADR-0006 is accepted.
- **Windows (compiled, not run).** `MemoryMappedFile.CreateNew` and `OpenExisting` with `Local\anode-…` names give
  pagefile-backed, pure-managed shared memory.
  - The object lives while any handle is open, which matches the protocol exactly: the server closes its handle
    after the reply, and a crash leaks nothing.
  - Measure it with the Windows backend in Phase 1.
- **NativeAOT:** everything here (`MemoryMappedFile`, `LibraryImport`, source-generated JSON, `Process.Start` of
  itself) publishes with zero warnings and ran as AOT.

## Limits

- **One machine, one OS.** Linux and Windows numbers are still to come (above).
- **The audio thread is simulated:** a Mach real-time thread paced at 64 frames, not a `cth_audio` device callback.
  Its render is a lookup, not DSP. The slow-callback counts therefore show disturbance, not dropouts; on a loaded
  engine, 0.4–0.7 ms of disturbance would be dropouts.
- **The control path is synchronous:** one swap at a time, with the server waiting for each reply. A real server
  coalesces edits; the pool's two slots assume at most one swap in flight per size class.
- **The compiler is not real:** the server copies prebuilt arrays. A compiler writing straight into the segment
  saves the copy but not the page faults, which pooling removes.

## Consequences for ADR-0006

- Accept the proposal with these specifics:
  - POSIX shared memory on macOS through the native shim, `/dev/shm` on Linux, named mappings on Windows;
  - the segment format above;
  - **per-track segments for edits** and **pooled whole-model segments** for project load and global changes;
  - the lifetime protocol above (reply after grace, the server unlinks on reply, unmapping only on the control
    thread).
- The target (1M events in under 10 ms with no allocation on the audio thread) is met with a wide margin: 1.45 ms
  median and 3.35 ms worst, including the server's write, with a pooled segment, and 0.9 ms with per-track segments.
- Add a Phase 1 test like Cathode's `HostAllocationTests`: zero allocation on the audio path across swaps, and no
  slow callbacks attributed to segment release.
