# ADR-0006: Bulk data between server and host through shared memory

- **Status:** proposed (Phase 0 spike 3 decides)
- **Date:** 2026-10-27

## Proposal

Control stays JSON frames over the Unix socket (as in Cathode). Bulk data — compiled playback models (per-track event
arrays and audio playlists), automation ramps, recording peaks — goes into shared-memory segments of blittable structs.
A JSON frame names the segment (`{segmentId, offset, length, version}`); the host maps it and swaps it in atomically
(RCU). Telemetry keeps Cathode's seqlock region.

Target: swapping a one-million-event playback model in under 10 ms with no allocation on the audio thread.
