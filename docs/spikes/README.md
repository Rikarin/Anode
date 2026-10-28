# Phase 0 spikes

Feasibility spikes from [the plan, §12 Phase 0](../01-implementation-plan.md). Each one answers one question with a
measurement, and feeds an ADR. Harnesses live in `engine/spikes/` (and `editor/libs/timeline/bench` for S1); every
write-up names its machine, method and the open points.

| Spike | Question | Verdict | Feeds |
|---|---|---|---|
| [S1](S1-timeline-rendering.md) | 20k notes at 60 fps in a WebGL2 OffscreenCanvas worker? | Go: 120 fps Chromium/Electron, 60 fps WebKit, 0 dropped | [ADR-0007](../adr/0007-ui-rendering.md) |
| [S2](S2-disk-streaming.md) | 128 tracks from disk at 64 frames with cycle, no allocation? | Go: 0 underruns in 26 configurations, worst callback 113 µs | Plan §3.2 |
| [S3](S3-bulk-ipc.md) | 1M-event model swapped into the host in < 10 ms? | Go with pooled / per-track POSIX shm: 1.5 ms p50 | [ADR-0006](../adr/0006-bulk-ipc.md) |
| [S4](S4-sandbox-wake.md) | Sandboxed plugins processed in the same 64-frame cycle? | Go with shared address wait + RT threads: 0 misses in 1.85 M | [ADR-0003](../adr/0003-plugin-hosting.md) |
| [S5](S5-time-stretch.md) | Signalsmith Stretch for Flex Time / Pitch / Varispeed? | Go with conditions: polyphonic yes; monophonic pitch wants PSOLA | [ADR-0008](../adr/0008-third-party-licensing.md), plan §11 |
| [S6](S6-plugin-windows.md) | Native plugin editor windows parented to Electron windows? | Go as top-level windows of the sandbox (floating panel, or ordered over the app's window by its window number); no cross-process child windows | [ADR-0003](../adr/0003-plugin-hosting.md) |

All measured on one M1 Max under macOS 27.2, with the spikes running concurrently (load noted in each write-up).
Linux and Windows runs are outstanding for S2–S5.
