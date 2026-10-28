# ADR-0003: Third-party plugin hosting and sandboxing

- **Status:** accepted (spike S4 for audio, spike S6 for editor windows; macOS measured). Built in Phase 4 workstream
  E: `engine/native/an_plug`, `engine/src/Anode.Plugins`, `engine/src/Anode.PluginSandbox`.
- **Date:** 2026-10-27
- **Spikes:** [S4](../spikes/S4-sandbox-wake.md) (harness `engine/spikes/Anode.Spikes.SandboxWake`, shim
  `engine/native/an_ipcwake`, now the product's), [S6](../spikes/S6-plugin-windows.md).

## Decision

- **Formats:** VST3 (C++ SDK v3.8.0 behind the `an_plug` C shim; MIT since 3.8), CLAP (its C headers, behind the
  same shim) and AU v2/v3 on macOS (AudioToolbox, same shim), all in the sandbox. **CLAP moved into the sandbox**
  (Phase 4): one code path and crash isolation for every format; CLAP plugins keep their own ids in the catalogue, so
  Phase 2 projects find them. Phase 2's in-process CLAP instrument host stays in `Anode.Engine` until the engine
  creates instruments through the factory.
- **Sandbox modes:** in-host (opt-in), **grouped sandbox (default)**, per-instance sandbox (opt-in; each sandbox costs
  about 10–20 µs per cycle). Default grouping: one `anode-plugin` process per vendor, capped at about the number of
  performance cores minus 2.
- **In-cycle processing:** sandboxed plugins process **in the same callback** through planar buffers in shared memory.
  The host signals every sandbox in parallel and waits with a deadline at **80 % of the period**; a sandbox that misses
  is bypassed with the host's copy of the dry input and is not signalled again until it has finished the late cycle.
- **Wake primitive** (S4: anything else failed):
  - macOS: shared `os_sync_wait_on_address` with an absolute deadline (requires **macOS 14.4**).
  - Linux: shared (non-private) futex.
  - Windows: named auto-reset events over a file mapping (`WaitOnAddress` is process-local).
  - Not POSIX semaphores (no `sem_timedwait` on macOS: 30–397 overloads per 15 s), not pure spinning (33–66 % misses).
- **Real-time sandbox threads are required** (without them: 32 % misses). macOS: join the device's IO `os_workgroup`
  (published by the host over a Mach bootstrap service), time-constraint policy as the fallback; Linux `SCHED_FIFO`;
  Windows MMCSS "Pro Audio".
- **One-buffer-latency mode** stays as a per-sandbox fallback: buffers under 32 frames, Windows until measured, and
  sandboxes that keep missing.
- **Editors (S6):** the sandbox owns native top-level editor windows: by default a floating utility panel over the
  desktop app's window, which the sandbox finds by the app's process (no handle passes through the host or server);
  optionally a normal window ordered directly above it. Cross-process child windows do not exist in public API, and
  Electron's native handle (an `NSView *`) is meaningless outside its process. Browsers on the LAN get a generic
  parameter editor.
- **Failure:** a crashed sandbox bypasses its slots and the UI offers to reload it.

## Evidence (M1 Max, built-in output, 48 kHz)

57 configurations with the address wait and real-time sandbox threads (1/8/50 instances, 1/2/4 sandboxes, 64 and 32
frames, device and null clock, idle and loaded): **0 deadline misses in 1.85 M handshakes, 0 CoreAudio overloads**. Wake
latency p50 6–13 µs, p99 11–23 µs. 50 instances at 64 frames finish in 87–208 µs p50 (worst callback 251 µs of
1,333 µs); with 500 µs of extra DSP, 165–545 µs.

## Open

Linux futex and `SCHED_FIFO` code never compiled locally (CI builds it with `-Werror`); Windows not built (neither the
wake nor editor windows); only the built-in device tested. Editor windows: focus and clicks while Electron is active
were not measured (S6).
