# ADR-0006: Bulk data between server and host through shared memory

- **Status:** accepted on macOS (spike S3); Linux and Windows to be confirmed by running the spike there
- **Date:** 2026-10-27
- **Spike:** [S3](../spikes/S3-bulk-ipc.md). Harness: `engine/spikes/Anode.Spikes.BulkIpc`.

## Decision

Control stays JSON frames over the Unix socket (as in Cathode). Bulk data — compiled playback models (per-track event
arrays and audio playlists), automation ramps, recording peaks — goes into shared-memory segments of blittable structs.

- **Segment format:** header (magic `ANPM`, version, counts, offsets, length in samples, optional checksum), track
  table, events (24 bytes), playlist entries (40 bytes), each array on a 64-byte boundary.
- **Segments:** **one segment per track** for edits; a **pool of two whole-model slots**, mapped once and reused, for
  project load and global changes (tempo map). Never a fresh segment per swap: it misses the target at the tail and
  disturbs the audio thread while old segments are unmapped (callbacks up to 0.74 ms).
- **Shared memory:** macOS: POSIX `shm_open` + `mmap`, through a small C wrapper in the native shim (`shm_open` is
  variadic). **Not file-backed `MemoryMappedFile` on macOS:** it stalled the real-time thread 30–100 ms per swap. Linux:
  a file in `/dev/shm`. Windows: `Local\` named mappings.
- **Validation:** always check the layout and touch every page; checksum and sort-order checks always on in debug and
  tests, and in release for per-track segments, off for whole-model segments over a few MB.
- **Lifetime:** the host swaps the model in with an atomic pointer swap (RCU) and replies only once it has mapped the
  new segment and the old one can no longer be in use; the server unlinks the name as soon as the reply arrives; the
  host unmaps only on its control thread. Telemetry keeps Cathode's seqlock region.

## Evidence (M1 Max, NativeAOT, 100 swaps per scenario)

1M events (23.2 MB) end to end: pooled 1.45 / 2.17 / 3.35 ms (p50 / p95 / max); pooled with checksum and verify 4.2 /
5.0 / 5.1 ms; one dirty track of 128: 0.9 / 1.4 / 1.4 ms. 5M events pooled: 4.8 ms p50, 8.0 ms max. **0 bytes
allocated on the audio thread**, no late callbacks with pooled segments, no leaks.

## Open

Linux and Windows unmeasured (Docker's default 64 MB `/dev/shm` is too small for a 5M-event pool). The host's control
thread should use Cathode's reused-buffer frame parsing.
