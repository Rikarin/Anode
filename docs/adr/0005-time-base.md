# ADR-0005: Time base

- **Status:** accepted
- **Date:** 2026-10-27

## Decision

- Musical positions are `Tick`s: `long` at **960 PPQ**. Absolute positions are `SamplePos` (`long`) at the project's
  sample rate.
- A `TempoMap` (constant segments and linear ramps, in tempo sets) converts between them; lookups are O(log n).
- Regions carry a time base: **musical** (moves with tempo) or **absolute** (locked to a sample/SMPTE position).
- The server compiles musical data to sample positions for the host (ADR-0006); a tempo change recompiles affected
  tracks.

## Why

960 PPQ divides evenly by 2, 3, 4, 5, 6, 8, 10, 12, 15, 16, 20, 24, 32 and 64, covering every common tuplet and grid,
and `long` ticks never overflow in practice. Keeping the host in samples keeps its scheduling arithmetic trivial.
