# ADR-0009: Scripter runs in the host on QuickJS-ng, with an arena and a time budget

- **Status:** accepted
- **Date:** 2026-10-29

## Context

Scripter is Logic's JavaScript MIDI effect. The plan (§7) put it in `an_plug`, the sandbox library, for isolation.
Phase 6 then built MIDI FX as an `IMidiEffect` chain on the host's audio thread, before the instrument. Running a script
in a sandbox would cost a cross-process round trip per block (spike S4's wake path) for an effect that only moves MIDI
events.

## Decision

- **Where it runs:** in `anode-host`, as the built-in MIDI effect `anode.scripter`, through a new native library
  `an_script` that embeds **QuickJS-ng** (MIT), pinned by commit and fetched by `engine/native/build.sh` like
  Signalsmith.
- **Memory:** each instance gets its own runtime and context, with a fixed arena allocator (4 MiB by default) given to
  QuickJS as its malloc functions. Nothing on the audio thread reaches the system allocator. When the arena runs out,
  that is an error, not growth. The garbage collector runs only on the control thread, between blocks.
- **Time:** an interrupt handler stops a call that runs past a budget (25 % of the block period by default). A script
  that is interrupted or throws is bypassed (its MIDI passes through), and `pluginProblem` reports the error and line;
  editing the script re-enables it.
- **Compilation:** script source compiles on the control thread; the audio thread only calls the compiled
  `HandleMIDI`/`ProcessMIDI`.
- **API:** a compatible subset of Logic's Scripter API (see docs/08-phase-7.md).
- **Sandbox:** no `std`/`os` modules and no I/O; `Trace` output goes through a lock-free ring to the control thread.

## Consequences

- A QuickJS defect could take the host down with it. That is accepted for a MIDI-only engine with a bounded arena and
  budget, and revisited if it happens.
- Scripts are deterministic per run except `Math.random`, which is seeded per instance at load.
