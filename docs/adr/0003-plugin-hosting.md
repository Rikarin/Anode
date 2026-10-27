# ADR-0003: Third-party plugin hosting and sandboxing

- **Status:** proposed (Phase 0 spikes 4 and 6 decide)
- **Date:** 2026-10-27

## Proposal

- **Formats:** CLAP (C ABI, hosted directly from C#), VST3 (C++ SDK behind the `an_plug` C shim; MIT since 3.8),
  AU v2/v3 on macOS (AudioToolbox behind the same shim).
- **Sandbox modes:** in-host (opt-in), grouped sandbox (default, one `anode-plugin` per group), per-instance sandbox.
  Sandboxed plugins process **in the same cycle** through shared-memory buffers and a futex/semaphore wake, so they add
  no buffer of latency.
- **Editors:** the sandbox owns native top-level editor windows, positioned by Electron's main process; browsers on the
  LAN get a generic parameter editor.
- **Failure:** a crashed sandbox bypasses its slots and the UI offers to reload it.

## Open questions

Latency of the cross-process wake at 32–64 frames on each OS; parenting native windows to Electron windows on Linux.
