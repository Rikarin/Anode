# S4: Cross-process wake for sandboxed plugins, in the same cycle

- **Date:** 2026-10-27
- **Question** (ADR-0003, plan §3.4 and §12 Phase 0 spike 4): can the engine hand an audio buffer to a plugin in
  another process and get it back within the same 64-frame callback (1.33 ms at 48 kHz), reliably, for up to ~50
  instances spread over a few sandbox processes?
- **Harness:** `engine/spikes/Anode.Spikes.SandboxWake` (NativeAOT, `anode-s4`) over the new native shim
  `engine/native/an_ipcwake`. Raw results: `results.jsonl` and `run.log` next to it.

## Verdict

**In-cycle processing is viable on macOS, with no extra buffer of latency, provided the wake is a kernel
address wait and the sandbox audio threads are real-time.** Across all 57 measured configurations with that setup
(1–50 instances, 1–4 sandbox processes, 64 and 32 frames, a real CoreAudio device and a null clock, with and without
a 10-thread background load on an already busy machine), **not one sandbox cycle missed its deadline**
(0 of 1.85 million sandbox handshakes), and CoreAudio reported **zero overloads**. The worst whole-callback time on the
device was **585 µs at 64 frames** (50 instances doing 500 µs of DSP in one sandbox) and **209 µs at 32 frames**,
against budgets of 1,333 µs and 667 µs.

What fails, and must not ship:

- **POSIX semaphores on macOS:** there is no `sem_timedwait`, so a host waiting with a deadline has to poll; doing
  that on the CoreAudio IO thread caused 30–397 driver overloads in 15 s and callbacks of up to 10 ms.
- **Pure spinning:** sandbox threads that spin between cycles are either demoted by the time-constraint scheduler
  or preempted; 33–66 % of sandbox cycles missed.
- **Sandbox threads without real-time policy:** under load, 32 % of sandbox cycles missed and CoreAudio reported 400
  overloads in 15 s. Real-time scheduling of the sandbox audio thread is not optional.

The one-buffer-latency fallback (process cycle N's input during cycle N+1) is **not needed** as the default. It
stays worth having as a per-sandbox mode for buffers below 32 frames or for plugins that are themselves too slow to
fit, but the wake itself is not the bottleneck.

## Machine and method

- **Machine:** MacBook Pro, Apple M1 Max (8 performance + 2 efficiency cores), 64 GB, macOS 27.2 (26B123), .NET
  10.0.403, NativeAOT, `DOTNET_GCgen0size=10000000` and anode-host's GC settings.
- **Device:** MacBook Pro Speakers (`BuiltInSpeakerDevice`) through `cth_audio` at 48 kHz, 64 and 32 frames. The
  callback writes nothing, so the output stays silent.
- **Ambient load:** other Phase 0 spikes ran on the same machine throughout (a headless Chromium at ~650 % CPU, a
  time-stretch benchmark): the one-minute load average was **14–40 even in the "idle" runs** (shown per row). "Load"
  adds this harness's own 10 default-priority threads that alternate arithmetic with 16 MB memory copies. So "idle"
  here is already a busier machine than a typical studio session.
- **Per run:** 3 s warm-up, 15 s measured (11,250 callbacks at 64 frames, 22,500 at 32).

**The handshake.** One shared-memory segment (`shm_open`) holds a header, one 512-byte control block per sandbox
(the two wake words on their own 128-byte cache lines, host-written and sandbox-written fields on separate lines) and
planar stereo float buffers per instance (instances dealt round-robin to the K sandboxes). Per callback the host:

1. writes every instance's input (a sine), then for each sandbox stores the cycle number and frame count and
   **signals** its `Go` word (atomic increment plus wake);
2. after waking all K sandboxes (so they run in parallel), **waits** on each sandbox's `Done` word in turn, with an
   absolute deadline of 80 % of the period (1,067 µs at 64 frames);
3. mixes the output of each sandbox that finished in time; a sandbox that did not is bypassed and counted as a miss,
   and is not signalled again until it has caught up (a "busy skip", also a miss). A completion the host only
   observed after the deadline counts as a miss too.

Each sandbox (a separate process, the same executable in `sandbox` role) runs one audio thread that waits on `Go`,
processes its instances **sequentially** (4 biquads per channel plus a gain, optionally a busy-wait of N µs per
instance to model a heavier plugin), and signals `Done`. A watchdog thread stops it if the host dies. With 0 biquads
the stub is a pure gain and the host checks every round trip carried that cycle's data (`corrupt`, 0 in every smoke
run).

**Measured** (100 ns histogram bins, all on one system-wide monotonic clock, `CLOCK_UPTIME_RAW`):

- **Wake:** host signal → the sandbox thread running (sandbox timestamp minus host timestamp). This is the pure
  cross-process wake latency.
- **All done:** callback start → the host has seen the last sandbox finish: everything the sandboxing adds to the
  callback, including the DSP.
- **Callback max:** the whole callback, measured by the host; for the device, `cth_audio` also measures natively
  around the managed call (`driverMaxPct` in the results) and CoreAudio reports overloads.
- **CPU:** host and sandbox process CPU time (`proc_pid_rusage`) over the measured window, as % of one core.

**Reproduce:** `engine/native/build.sh`, then `engine/spikes/Anode.Spikes.SandboxWake/run.sh BuiltInSpeakerDevice`
(about 25 minutes; device uids from `anode-s4 devices`), then `anode-s4 summarize results.jsonl`.

## Results

Times in µs. "Misses" counts sandbox cycles that missed the deadline, out of callbacks × K.

### A. Wake primitive (8 instances over 2 sandboxes, device, 64 frames)

| Primitive | Load (load avg) | Wake p50 / p99 / p99.9 / max | All done p50 / p99 / p99.9 / max | Callback max | Misses | Sandbox CPU % | Driver overloads |
|---|---|---|---|---|---|---|---|
| address wait (`os_sync_wait_on_address`, shared) | idle (14) | 8.2 / 16.4 / 25.3 / 32.6 | 32.7 / 45.2 / 51.9 / 162.4 | 165 | 0 / 22,502 | 3 | 0 |
| address wait | load (24) | 10.7 / 20.6 / 24.9 / 28.1 | 38.3 / 51.2 / 62.8 / 67.6 | 69 | 0 / 22,508 | 4 | 0 |
| address wait + 30 µs spin on both sides | idle (34) | 8.4 / 23.1 / 33.7 / 49.2 | 29.3 / 45.5 / 67.5 / 105.5 | 107 | 0 / 22,502 | 8 | 0 |
| address wait + 30 µs spin on both sides | load (36) | 11.3 / 21.6 / 33.2 / 61.3 | 33.8 / 49.7 / 65.5 / 100.3 | 105 | 0 / 22,504 | 8 | 0 |
| POSIX named semaphore | idle (23) | 7.9 / 15.1 / 22.2 / 43.3 | 30.2 / 199 / 2,476 / 4,161 | 4,168 | 70 / 22,424 | 3 | **30** |
| POSIX named semaphore | load (29) | 10.5 / 19.1 / 26.3 / 49.9 | 35.2 / 2,841 / 5,620 / 10,036 | 10,044 | 895 / 21,240 | 3 | **397** |
| spin (real-time threads) | idle (26) | 13.3 / 940 / 1,028 / 1,039 | 71 / 1,067 / 1,067 / 1,070 | 1,075 | 7,462 / 22,500 | 93 | 0 |
| spin (real-time threads) | load (33) | 87 / 995 / 1,031 / 1,040 | 330 / 1,067 / 1,067 / 1,071 | 1,082 | 14,867 / 22,508 | 39 | 0 |
| spin (normal threads) | idle (34) | 0.4 / 242 / 952 / 1,038 | 19 / 919 / 1,067 / 1,067 | 1,075 | 632 / 22,506 | 191 | 0 |
| spin (normal threads) | load (40) | 1.3 / 964 / 1,042 / 1,049 | 55 / 1,067 / 1,067 / 1,071 | 1,074 | 12,650 / 22,508 | 80 | 0 |

- The **address wait** is the answer: ~8–11 µs median wake, ~16–21 µs p99, under 65 µs worst, at 3–4 % CPU.
- **Spinning first buys nothing** (the median is already near the hardware floor) and doubles the CPU.
- **Semaphores** wake the sandbox just as fast, but the host side cannot block with a deadline on macOS. Its
  `sem_trywait` + `sched_yield` poll on the CoreAudio IO thread gives the CPU away at the worst time, and the device
  overloads. (A semaphore wake with an address-wait completion would work, but has no advantage over address waits.)
- **Spin** is fast only while every spinner owns a core. A spinning time-constraint thread overruns its declared
  computation and the scheduler demotes or preempts it; a normal-priority spinner loses its core to the load.
  Either way the sandbox misses a third to two thirds of its cycles (the host itself spun to the deadline, hence the
  ~80 % driver time and no overloads).

### B. Address wait, the grid (4 biquads per channel per instance, ~4 µs of DSP per instance)

| Clock | Frames | K | N | Load (load avg) | Wake p50 / p99 / p99.9 / max | All done p50 / p99 / p99.9 / max | Callback max | Misses | Host / sandbox CPU % | Driver overloads |
|---|---|---|---|---|---|---|---|---|---|---|
| device | 64 | 1 | 1 | idle (31) | 8.7 / 15.2 / 20.5 / 36.0 | 19.2 / 28.4 / 32.8 / 49.0 | 49 | 0 / 11,254 | 1 / 1 | 0 |
| device | 64 | 1 | 1 | load (34) | 13.4 / 22.3 / 26.8 / 30.7 | 24.3 / 35.8 / 41.2 / 46.8 | 51 | 0 / 11,254 | 2 / 1 | 0 |
| device | 64 | 1 | 8 | load (30) | 13.7 / 23.1 / 27.9 / 30.5 | 52.2 / 64.4 / 70.2 / 77.6 | 79 | 0 / 11,250 | 2 / 3 | 0 |
| device | 64 | 2 | 8 | load (30) | 10.6 / 20.8 / 25.3 / 38.4 | 37.8 / 51.8 / 65.3 / 159.3 | 166 | 0 / 22,506 | 2 / 4 | 0 |
| device | 64 | 4 | 8 | load (39) | 11.3 / 21.7 / 30.6 / 52.5 | 42.9 / 65.1 / 153.8 / 158.5 | 159 | 0 / 45,004 | 3 / 6 | 0 |
| device | 64 | 1 | 50 | idle (33) | 8.4 / 14.1 / 17.0 / 19.9 | 208.4 / 217.8 / 222.1 / 235.8 | 243 | 0 / 11,252 | 2 / 14 | 0 |
| device | 64 | 1 | 50 | load (34) | 12.9 / 21.9 / 26.8 / 30.8 | 214.2 / 228.4 / 234.7 / 241.4 | 251 | 0 / 11,252 | 3 / 14 | 0 |
| device | 64 | 2 | 50 | load (37) | 10.3 / 21.7 / 27.4 / 31.2 | 122.3 / 136.5 / 142.3 / 148.5 | 163 | 0 / 22,500 | 3 / 15 | 0 |
| device | 64 | 4 | 50 | idle (31) | 9.2 / 36.6 / 58.2 / 99.8 | 87.0 / 133.9 / 195.0 / 218.5 | 222 | 0 / 45,012 | 4 / 17 | 0 |
| device | 64 | 4 | 50 | load (34) | 10.1 / 21.9 / 28.1 / 56.4 | 89.6 / 108.3 / 141.9 / 212.4 | 222 | 0 / 45,012 | 4 / 17 | 0 |
| device | 32 | 1 | 1 | load (26) | 10.8 / 18.1 / 21.8 / 27.3 | 19.3 / 29.2 / 33.9 / 39.0 | 40 | 0 / 22,505 | 3 / 2 | 0 |
| device | 32 | 4 | 8 | idle (32) | 7.1 / 19.7 / 35.1 / 123.0 | 29.5 / 76.5 / 136.0 / 152.0 | 152 | 0 / 90,000 | 3 / 6 | 0 |
| device | 32 | 1 | 50 | load (27) | 10.0 / 17.8 / 21.8 / 26.8 | 113.1 / 124.3 / 129.6 / 147.1 | 161 | 0 / 22,506 | 4 / 15 | 0 |
| device | 32 | 2 | 50 | idle (22) | 5.5 / 13.6 / 23.6 / 40.5 | 62.2 / 79.3 / 86.2 / 182.5 | 190 | 0 / 45,004 | 3 / 15 | 0 |
| device | 32 | 4 | 50 | load (19) | 8.3 / 18.1 / 24.5 / 63.7 | 58.8 / 77.1 / 87.6 / 169.3 | 174 | 0 / 90,024 | 6 / 19 | 0 |
| null | 64 | 1 | 50 | idle (35) | 8.3 / 19.2 / 28.3 / 38.1 | 212.1 / 355.9 / 473.2 / 724.2 | 741 | 0 / 11,252 | 3 / 14 | — |
| null | 64 | 4 | 50 | idle (22) | 11.7 / 91.4 / 126.3 / 196.8 | 90.6 / 280.0 / 468.0 / 530.8 | 539 | 0 / 45,016 | 4 / 17 | — |
| null | 64 | 4 | 50 | load (23) | 8.9 / 20.8 / 27.7 / 40.8 | 91.6 / 110.3 / 119.9 / 208.7 | 212 | 0 / 45,016 | 4 / 17 | — |

(The full 42-run grid is in the appendix; the rows above are representative and include every row's worst case.)

### C. Heavier plugins: 50 instances × 10 µs of extra DSP (500 µs per cycle), device, 64 frames

| K | Load | Wake p50 / p99 / p99.9 / max | All done p50 / p99 / p99.9 / max | Callback max | Misses | Sandbox CPU % |
|---|---|---|---|---|---|---|
| 1 | idle (25) | 7.1 / 14.7 / 18.3 / 25.7 | 535.8 / 548.7 / 556.2 / 566.5 | 572 | 0 / 11,253 | 39 |
| 1 | load (36) | 11.6 / 20.6 / 24.6 / 33.6 | 544.8 / 561.7 / 569.3 / 573.3 | 585 | 0 / 11,254 | 39 |
| 2 | idle (26) | 7.2 / 32.1 / 47.9 / 81.7 | 281.4 / 348.2 / 373.1 / 422.8 | 434 | 0 / 22,504 | 40 |
| 2 | load (28) | 9.1 / 19.2 / 24.3 / 37.3 | 286.3 / 299.8 / 306.7 / 344.9 | 360 | 0 / 22,506 | 40 |
| 4 | idle (23) | 10.0 / 158.3 / 183.0 / 210.5 | 162.0 / 332.9 / 366.0 / 416.7 | 421 | 0 / 45,004 | 41 |
| 4 | load (30) | 9.8 / 20.3 / 26.3 / 50.2 | 164.6 / 188.7 / 283.6 / 300.9 | 304 | 0 / 45,016 | 41 |

### D. Sandbox threads joined to the device's IO workgroup (load, address wait)

| Frames | K | N | Burn µs | All done p50 / p99 / p99.9 / max (with workgroup) | Same without (B/C) |
|---|---|---|---|---|---|
| 64 | 2 | 50 | 0 | 120.9 / 136.4 / 145.4 / 232.5 | 122.3 / 136.5 / 142.3 / 148.5 |
| 64 | 4 | 50 | 0 | 88.7 / 104.8 / 115.7 / 207.8 | 89.6 / 108.3 / 141.9 / 212.4 |
| 32 | 2 | 50 | 0 | 67.1 / 80.3 / 86.1 / 192.5 | 67.2 / 79.8 / 85.6 / 96.7 |
| 32 | 4 | 50 | 0 | 58.6 / 74.0 / 85.9 / 185.8 | 58.8 / 77.1 / 87.6 / 169.3 |
| 64 | 4 | 50 | 10 | 164.3 / 186.8 / 289.3 / 300.3 | 164.6 / 188.7 / 283.6 / 300.9 |

Joining works cross-process (see below), but on top of `THREAD_TIME_CONSTRAINT_POLICY` it made **no measurable
difference** on this machine: every pair is within run-to-run noise.

### E. No real-time policy on the sandbox threads (4 × 50, load, device, 64 frames)

Wake p50 / p99 / max 47.5 / 783 / 1,120 µs; **13,962 of 43,416 sandbox cycles missed** (8,977 of them busy skips),
**400 driver overloads** in 15 s.

## Findings

1. **The wake is cheap and stable.** Median 6–13 µs, p99 11–23 µs in almost every address-wait run, regardless of
   K, N, buffer size or load. The occasional tail (p99 ~90–160 µs in a few idle 4-sandbox runs) comes from the four
   sandbox threads plus the host thread outnumbering free performance cores while other spikes ran; it never came
   close to the deadline.
2. **Sandboxing costs roughly 10–20 µs per sandbox per cycle**, on top of the plugins' own DSP: with 1 instance,
   "all done" is ~19–24 µs at the median, and adding sandboxes adds the cost of one more signal plus one more wait
   (the host issues the signals serially; the difference between "wake" and "all done" for one empty instance suggests a
   signal-plus-wait round costs ~10 µs beyond the wake itself).
3. **Parallelism across sandboxes pays for itself from a few hundred µs of DSP.** 50 instances × 10 µs: 545 µs in
   one sandbox, 286 µs in two, 165 µs in four. With the light stub (~4 µs per instance) 4 sandboxes still halve the
   time (214 → 90 µs). The cost is one more real-time thread per sandbox, and with K ≥ the free performance cores the
   tails get worse, not better.
4. **Load from normal-priority threads does not hurt real-time sandbox threads.** The "load" rows are often *tighter*
   than "idle" ones: the time-constraint threads still preempt the load, and (a likely explanation, not measured) busy
   performance cores stay clocked up. The "idle" tails probably come from frequency ramps and from the ambient spikes'
   own bursts.
5. **32 frames works** with the same margins: 667 µs budget, worst callback 209 µs with 50 instances.
6. **CPU** is low: the host thread blocks rather than spins (1–6 % of a core), and an idle sandbox costs nothing
   between cycles; sandbox CPU is the plugins' DSP plus ~1 % per sandbox.
7. **The null clock is slightly worse than the device** (one 741 µs outlier with 50 instances in one sandbox, idle):
   the null clock is a managed thread with a time-constraint policy, while CoreAudio's IO thread is in the device's
   workgroup. Still within budget.

## Recommendations

### Wake primitive per OS

| OS | Primitive | Notes |
|---|---|---|
| **macOS 14.4+** | `os_sync_wait_on_address` / `os_sync_wake_by_address_any` with the `_SHARED` flags on a 32-bit counter in the shared segment; deadline wait with `os_sync_wait_on_address_with_deadline` (`OS_CLOCK_MACH_ABSOLUTE_TIME`) | Measured here. Public API, cross-process verified. No spin phase. Older macOS: the private `__ulock_wait2(UL_COMPARE_AND_WAIT_SHARED)` is the same mechanism; Anode can simply require 14.4. Not POSIX semaphores (no timed wait). Mach semaphores (`semaphore_timedwait_signal`) would also work, but need the same Mach port hand-off as the workgroup. |
| **Linux** | `futex(FUTEX_WAIT / FUTEX_WAKE)` without `FUTEX_PRIVATE_FLAG` on the same counter, relative timeout recomputed from the deadline | Implemented in `an_ipcwake_posix.c` but **not compiled or measured here** (no Linux machine; Docker was not running). Needs `SCHED_FIFO` (rtprio limits or RTKit) for the sandbox thread; a failure must be surfaced, given E. |
| **Windows** | Named auto-reset events (`CreateEventW` in `Local\`, `SetEvent`, `WaitForSingleObject`) next to the counter in a `CreateFileMapping` section; the sandbox thread joins MMCSS ("Pro Audio") | Not built yet (CMake skips `an_ipcwake` on Windows). `WaitOnAddress` is process-local, so it cannot be used. `WaitForSingleObject` takes milliseconds: the host's deadline wait needs a short spin plus a 1 ms timeout or a high-resolution waitable timer. Needs its own measurement before ADR-0003 is accepted for Windows. |

All three share the protocol measured here: a counter per direction, the host signals all sandboxes first and then
waits on each with an absolute deadline, and a late sandbox is bypassed and not re-signalled until it has caught up.

### Real-time scheduling of the sandbox audio thread (required)

- **macOS:** `THREAD_TIME_CONSTRAINT_POLICY` with period = buffer period, computation = half of it, constraint = the
  period, preemptible. That was sufficient. Joining the device's **IO workgroup** also works out of process: the host
  publishes `os_workgroup_copy_port` of `kAudioDevicePropertyIOThreadOSWorkgroup` as a Mach bootstrap service
  (`bootstrap_check_in` under a per-run name), the sandbox looks it up, receives the send right in a Mach message and
  joins with `os_workgroup_create_with_port` + `os_workgroup_join` (`an_wg_*` in the shim). It did not change the
  numbers on top of the time-constraint policy, but it is Apple's documented model for auxiliary audio threads (and
  what AUv3 out-of-process hosting does), so the product should do both, with the workgroup as the primary and the
  policy as the fallback. A null-clock (offline/no device) run has no workgroup to join; there is no public API to
  create an interval workgroup.
- **Linux:** `SCHED_FIFO` at a priority just below the audio device thread (JACK/PipeWire convention), via RTKit when
  the user has no rtprio.
- **Windows:** `AvSetMmThreadCharacteristicsW(L"Pro Audio")`.

### Default sandbox grouping

- **Default: one sandbox process per vendor, capped by core count** — at most `min(vendors, performance cores − 2)`
  sandbox processes, with further vendors sharing. Two to four sandboxes covered 50 instances with a 6–10× margin.
- **Within a sandbox, instances on one chain run sequentially** (they have to); independent chains in one sandbox
  could later run on a small pool of sandbox audio threads, each with its own wake word.
- The per-cycle overhead (~10–20 µs per sandbox) means **a per-instance sandbox for every plugin is affordable for a
  handful of plugins but not for 50**: extrapolating, 50 processes would add several hundred µs to ~1 ms of serial signalling and 50 real-time threads.
  Per-instance stays an opt-in for crash-prone plugins.
- **Deadline:** 80 % of the period minus the host's own post-processing; on a miss, bypass with the dry signal (keep
  the host's own copy of the input, since a late sandbox may still be writing the shared buffer) and do not
  re-signal that sandbox until it reports the earlier cycle done.

### One-buffer-latency fallback

Not needed as the default. Keep it as a per-sandbox mode (report one buffer of latency to the delay compensation and
process with a one-cycle pipeline) for: buffers below 32 frames, Windows until its wake is measured, and sandboxes
whose miss rate crosses a threshold at run time.

## Notes and limits

- One machine, macOS only, and a busy one (load average 14–66 from parallel spikes). A quieter machine should only do
  better; a slower machine with fewer performance cores will see the K ≥ cores tails sooner.
- The "plugin" is a stub. Real plugins allocate, take locks and page-fault in their process call; that is a reason to
  sandbox them, and it shows up as misses (bypass), never as engine dropouts, because the host waits with a deadline.
- The host signals sandboxes serially from the audio thread; a host with many sandboxes could signal from a helper,
  but at K ≤ 4 the serial cost is ~10–15 µs.
- Only the host's timing of whole callbacks covers the managed code; `cth_audio`'s native timing (`driverMaxPct`) was
  at most 44 % for every address-wait run (the 500 µs DSP case), and ~5–30 % otherwise.
- The Linux futex and `SCHED_FIFO` code compiles in principle but was not built on Linux here; CI's Linux job builds
  the shim (`engine/native/build.sh`), so a mistake shows there first.

## Appendix: all runs

| Label | Clock | Frames | Kind | Load (load avg) | K | N | Burn µs | Wake p50/p99/p99.9/max µs | All done p50/p99/p99.9/max µs | Callback max µs | Misses / sandbox cycles | Missed cb | Host CPU % | Sandbox CPU % | Driver overloads |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| A primitive | device | 64 | address | idle (14) | 2 | 8 | 0 | 8.2 / 16.4 / 25.3 / 32.6 | 32.7 / 45.2 / 51.9 / 162.4 | 165.1 | 0 / 22502 | 0 | 1 | 3 | 0 |
| A primitive | device | 64 | address | load (24) | 2 | 8 | 0 | 10.7 / 20.6 / 24.9 / 28.1 | 38.3 / 51.2 / 62.8 / 67.6 | 68.8 | 0 / 22508 | 0 | 2 | 4 | 0 |
| A primitive | device | 64 | semaphore | idle (23) | 2 | 8 | 0 | 7.9 / 15.1 / 22.2 / 43.3 | 30.2 / 199.3 / 2475.7 / 4161 | 4167.7 | 70 / 22424 | 35 | 2 | 3 | 30 |
| A primitive | device | 64 | semaphore | load (29) | 2 | 8 | 0 | 10.5 / 19.1 / 26.3 / 49.9 | 35.2 / 2841.1 / 5620.1 / 10036.3 | 10044 | 895 / 21240 | 448 | 3 | 3 | 397 |
| A primitive | device | 64 | spin | idle (26) | 2 | 8 | 0 | 13.3 / 940.4 / 1028.2 / 1039.3 | 71.2 / 1067 / 1067.1 / 1069.5 | 1075.1 | 7462 / 22500 | 4209 | 23 | 93 | 0 |
| A primitive | device | 64 | spin | load (33) | 2 | 8 | 0 | 87 / 995.1 / 1030.7 / 1039.7 | 330.4 / 1067.2 / 1067.4 / 1070.7 | 1082 | 14867 / 22508 | 8615 | 29 | 39 | 0 |
| A primitive | device | 64 | spin (no rt) | idle (34) | 2 | 8 | 0 | 0.4 / 241.7 / 951.8 / 1038.4 | 19 / 919 / 1067.1 / 1067.3 | 1075 | 632 / 22506 | 444 | 5 | 191 | 0 |
| A primitive | device | 64 | spin (no rt) | load (40) | 2 | 8 | 0 | 1.3 / 963.9 / 1042.2 / 1049.4 | 54.9 / 1067.1 / 1067.3 / 1070.8 | 1073.6 | 12650 / 22508 | 8659 | 31 | 80 | 0 |
| A primitive | device | 64 | address+spin 30/30 | idle (34) | 2 | 8 | 0 | 8.4 / 23.1 / 33.7 / 49.2 | 29.3 / 45.5 / 67.5 / 105.5 | 106.7 | 0 / 22502 | 0 | 3 | 8 | 0 |
| A primitive | device | 64 | address+spin 30/30 | load (36) | 2 | 8 | 0 | 11.3 / 21.6 / 33.2 / 61.3 | 33.8 / 49.7 / 65.5 / 100.3 | 105 | 0 / 22504 | 0 | 3 | 8 | 0 |
| B grid | device | 64 | address | idle (31) | 1 | 1 | 0 | 8.7 / 15.2 / 20.5 / 36 | 19.2 / 28.4 / 32.8 / 49 | 49.3 | 0 / 11254 | 0 | 1 | 1 | 0 |
| B grid | device | 64 | address | load (34) | 1 | 1 | 0 | 13.4 / 22.3 / 26.8 / 30.7 | 24.3 / 35.8 / 41.2 / 46.8 | 50.9 | 0 / 11254 | 0 | 2 | 1 | 0 |
| B grid | device | 64 | address | idle (29) | 1 | 8 | 0 | 9.4 / 15.6 / 17.7 / 20.5 | 47.6 / 56.1 / 59.6 / 61.9 | 62.9 | 0 / 11253 | 0 | 1 | 3 | 0 |
| B grid | device | 64 | address | load (30) | 1 | 8 | 0 | 13.7 / 23.1 / 27.9 / 30.5 | 52.2 / 64.4 / 70.2 / 77.6 | 78.9 | 0 / 11250 | 0 | 2 | 3 | 0 |
| B grid | device | 64 | address | idle (29) | 2 | 8 | 0 | 8.4 / 15.4 / 18.2 / 32.5 | 32.7 / 41.5 / 46.6 / 162.8 | 164.8 | 0 / 22506 | 0 | 1 | 3 | 0 |
| B grid | device | 64 | address | load (30) | 2 | 8 | 0 | 10.6 / 20.8 / 25.3 / 38.4 | 37.8 / 51.8 / 65.3 / 159.3 | 166 | 0 / 22506 | 0 | 2 | 4 | 0 |
| B grid | device | 64 | address | idle (31) | 4 | 8 | 0 | 9.8 / 17.6 / 22.6 / 40.2 | 36.2 / 55.7 / 152.1 / 166 | 166.8 | 0 / 45012 | 0 | 2 | 5 | 0 |
| B grid | device | 64 | address | load (39) | 4 | 8 | 0 | 11.3 / 21.7 / 30.6 / 52.5 | 42.9 / 65.1 / 153.8 / 158.5 | 159.1 | 0 / 45004 | 0 | 3 | 6 | 0 |
| B grid | device | 64 | address | idle (33) | 1 | 50 | 0 | 8.4 / 14.1 / 17 / 19.9 | 208.4 / 217.8 / 222.1 / 235.8 | 242.5 | 0 / 11252 | 0 | 2 | 14 | 0 |
| B grid | device | 64 | address | load (34) | 1 | 50 | 0 | 12.9 / 21.9 / 26.8 / 30.8 | 214.2 / 228.4 / 234.7 / 241.4 | 251.3 | 0 / 11252 | 0 | 3 | 14 | 0 |
| B grid | device | 64 | address | idle (29) | 2 | 50 | 0 | 8.1 / 16.4 / 20.5 / 26.2 | 116.8 / 129.7 / 134.9 / 180.2 | 186.8 | 0 / 22504 | 0 | 3 | 15 | 0 |
| B grid | device | 64 | address | load (37) | 2 | 50 | 0 | 10.3 / 21.7 / 27.4 / 31.2 | 122.3 / 136.5 / 142.3 / 148.5 | 162.5 | 0 / 22500 | 0 | 3 | 15 | 0 |
| B grid | device | 64 | address | idle (31) | 4 | 50 | 0 | 9.2 / 36.6 / 58.2 / 99.8 | 87 / 133.9 / 195 / 218.5 | 221.5 | 0 / 45012 | 0 | 4 | 17 | 0 |
| B grid | device | 64 | address | load (34) | 4 | 50 | 0 | 10.1 / 21.9 / 28.1 / 56.4 | 89.6 / 108.3 / 141.9 / 212.4 | 222 | 0 / 45012 | 0 | 4 | 17 | 0 |
| B grid | device | 32 | address | idle (29) | 1 | 1 | 0 | 7.9 / 14.6 / 17.7 / 22.4 | 17.9 / 26.9 / 30.6 / 36.2 | 36.4 | 0 / 22503 | 0 | 2 | 2 | 0 |
| B grid | device | 32 | address | load (26) | 1 | 1 | 0 | 10.8 / 18.1 / 21.8 / 27.3 | 19.3 / 29.2 / 33.9 / 39 | 39.5 | 0 / 22505 | 0 | 3 | 2 | 0 |
| B grid | device | 32 | address | idle (23) | 1 | 8 | 0 | 7.4 / 13.6 / 17.2 / 22.4 | 29.8 / 38.6 / 43.2 / 47.2 | 47.8 | 0 / 22507 | 0 | 2 | 3 | 0 |
| B grid | device | 32 | address | load (37) | 1 | 8 | 0 | 10.3 / 17.8 / 22.3 / 34.6 | 32.4 / 42.5 / 48.2 / 62.2 | 67.3 | 0 / 22506 | 0 | 3 | 3 | 0 |
| B grid | device | 32 | address | idle (37) | 2 | 8 | 0 | 5.8 / 13.4 / 18 / 22.6 | 20.6 / 30.2 / 34.3 / 151.8 | 159 | 0 / 45014 | 0 | 2 | 4 | 0 |
| B grid | device | 32 | address | load (42) | 2 | 8 | 0 | 8 / 16.9 / 21.2 / 32.8 | 24.9 / 35.9 / 42.6 / 144.5 | 154.3 | 0 / 45014 | 0 | 3 | 5 | 0 |
| B grid | device | 32 | address | idle (32) | 4 | 8 | 0 | 7.1 / 19.7 / 35.1 / 123 | 29.5 / 76.5 / 136 / 152 | 152.3 | 0 / 90000 | 0 | 3 | 6 | 0 |
| B grid | device | 32 | address | load (37) | 4 | 8 | 0 | 8.8 / 17.2 / 22.7 / 51.3 | 29 / 51 / 79.2 / 150.3 | 157.4 | 0 / 90032 | 0 | 4 | 8 | 0 |
| B grid | device | 32 | address | idle (29) | 1 | 50 | 0 | 6.1 / 11 / 13.3 / 20.2 | 107.6 / 115.4 / 119 / 126 | 133.9 | 0 / 22507 | 0 | 3 | 14 | 0 |
| B grid | device | 32 | address | load (27) | 1 | 50 | 0 | 10 / 17.8 / 21.8 / 26.8 | 113.1 / 124.3 / 129.6 / 147.1 | 160.7 | 0 / 22506 | 0 | 4 | 15 | 0 |
| B grid | device | 32 | address | idle (22) | 2 | 50 | 0 | 5.5 / 13.6 / 23.6 / 40.5 | 62.2 / 79.3 / 86.2 / 182.5 | 190 | 0 / 45004 | 0 | 3 | 15 | 0 |
| B grid | device | 32 | address | load (22) | 2 | 50 | 0 | 8.3 / 17.3 / 21.7 / 33.5 | 67.2 / 79.8 / 85.6 / 96.7 | 98 | 0 / 45010 | 0 | 4 | 16 | 0 |
| B grid | device | 32 | address | idle (20) | 4 | 50 | 0 | 6.6 / 24.6 / 29.7 / 47.5 | 55 / 72.6 / 101.9 / 168.4 | 170 | 0 / 90016 | 0 | 5 | 18 | 0 |
| B grid | device | 32 | address | load (19) | 4 | 50 | 0 | 8.3 / 18.1 / 24.5 / 63.7 | 58.8 / 77.1 / 87.6 / 169.3 | 174.3 | 0 / 90024 | 0 | 6 | 19 | 0 |
| B grid | null | 64 | address | idle (25) | 1 | 1 | 0 | 8.4 / 22.9 / 33.5 / 42.1 | 18.3 / 42.6 / 59.6 / 70.3 | 72 | 0 / 11251 | 0 | 1 | 1 | — |
| B grid | null | 64 | address | load (59) | 1 | 1 | 0 | 10.4 / 19.6 / 24.4 / 42 | 20.5 / 33 / 40.6 / 51.6 | 52.1 | 0 / 11253 | 0 | 1 | 1 | — |
| B grid | null | 64 | address | idle (56) | 1 | 8 | 0 | 7.1 / 26.2 / 33.3 / 41.5 | 43.7 / 97.1 / 117.8 / 131 | 135 | 0 / 11250 | 0 | 1 | 3 | — |
| B grid | null | 64 | address | load (50) | 1 | 8 | 0 | 12.1 / 23.4 / 28.4 / 31.9 | 50 / 64.7 / 71.3 / 77 | 79.4 | 0 / 11252 | 0 | 2 | 3 | — |
| B grid | null | 64 | address | idle (41) | 2 | 8 | 0 | 7.6 / 23.3 / 27.4 / 33.4 | 32.5 / 51.8 / 59 / 157.1 | 161.8 | 0 / 22504 | 0 | 1 | 3 | — |
| B grid | null | 64 | address | load (42) | 2 | 8 | 0 | 9 / 21.6 / 27.3 / 33.6 | 35.9 / 50.8 / 56.7 / 62.4 | 63 | 0 / 22510 | 0 | 2 | 4 | — |
| B grid | null | 64 | address | idle (36) | 4 | 8 | 0 | 10.3 / 25.8 / 34.9 / 51.7 | 37.7 / 83.4 / 159.3 / 171.5 | 172.2 | 0 / 45008 | 0 | 2 | 5 | — |
| B grid | null | 64 | address | load (41) | 4 | 8 | 0 | 9.3 / 20.5 / 27.3 / 39.8 | 42.5 / 62.2 / 73.6 / 160.9 | 161.5 | 0 / 45016 | 0 | 3 | 5 | — |
| B grid | null | 64 | address | idle (35) | 1 | 50 | 0 | 8.3 / 19.2 / 28.3 / 38.1 | 212.1 / 355.9 / 473.2 / 724.2 | 740.7 | 0 / 11252 | 0 | 3 | 14 | — |
| B grid | null | 64 | address | load (37) | 1 | 50 | 0 | 11.9 / 21.2 / 25.1 / 30 | 213 / 228.5 / 236.1 / 266.8 | 271.6 | 0 / 11254 | 0 | 3 | 14 | — |
| B grid | null | 64 | address | idle (31) | 2 | 50 | 0 | 7.3 / 20.4 / 30.3 / 42.7 | 116.5 / 134.8 / 267.9 / 336.7 | 347.5 | 0 / 22504 | 0 | 3 | 15 | — |
| B grid | null | 64 | address | load (30) | 2 | 50 | 0 | 9.8 / 22.5 / 27.8 / 36 | 122.3 / 140.2 / 150.6 / 192.6 | 195 | 0 / 22500 | 0 | 3 | 15 | — |
| B grid | null | 64 | address | idle (22) | 4 | 50 | 0 | 11.7 / 91.4 / 126.3 / 196.8 | 90.6 / 280 / 468 / 530.8 | 539 | 0 / 45016 | 0 | 4 | 17 | — |
| B grid | null | 64 | address | load (23) | 4 | 50 | 0 | 8.9 / 20.8 / 27.7 / 40.8 | 91.6 / 110.3 / 119.9 / 208.7 | 212.4 | 0 / 45016 | 0 | 4 | 17 | — |
| C 10 µs/instance | device | 64 | address | idle (25) | 1 | 50 | 10 | 7.1 / 14.7 / 18.3 / 25.7 | 535.8 / 548.7 / 556.2 / 566.5 | 571.6 | 0 / 11253 | 0 | 2 | 39 | 0 |
| C 10 µs/instance | device | 64 | address | load (36) | 1 | 50 | 10 | 11.6 / 20.6 / 24.6 / 33.6 | 544.8 / 561.7 / 569.3 / 573.3 | 585 | 0 / 11254 | 0 | 3 | 39 | 0 |
| C 10 µs/instance | device | 64 | address | idle (26) | 2 | 50 | 10 | 7.2 / 32.1 / 47.9 / 81.7 | 281.4 / 348.2 / 373.1 / 422.8 | 434.2 | 0 / 22504 | 0 | 3 | 40 | 0 |
| C 10 µs/instance | device | 64 | address | load (28) | 2 | 50 | 10 | 9.1 / 19.2 / 24.3 / 37.3 | 286.3 / 299.8 / 306.7 / 344.9 | 360.3 | 0 / 22506 | 0 | 3 | 40 | 0 |
| C 10 µs/instance | device | 64 | address | idle (23) | 4 | 50 | 10 | 10 / 158.3 / 183 / 210.5 | 162 / 332.9 / 366 / 416.7 | 420.5 | 0 / 45004 | 0 | 3 | 41 | 0 |
| C 10 µs/instance | device | 64 | address | load (30) | 4 | 50 | 10 | 9.8 / 20.3 / 26.3 / 50.2 | 164.6 / 188.7 / 283.6 / 300.9 | 303.8 | 0 / 45016 | 0 | 4 | 41 | 0 |
| D workgroup | device | 64 | address +wg | load (46) | 2 | 50 | 0 | 9.5 / 20.7 / 25.4 / 41.3 | 120.9 / 136.4 / 145.4 / 232.5 | 245.5 | 0 / 22504 | 0 | 3 | 15 | 0 |
| D workgroup | device | 64 | address +wg | load (57) | 4 | 50 | 0 | 9.8 / 18.9 / 24.2 / 48 | 88.7 / 104.8 / 115.7 / 207.8 | 214.1 | 0 / 45004 | 0 | 4 | 17 | 0 |
| D workgroup | device | 32 | address +wg | load (59) | 2 | 50 | 0 | 8.1 / 17.9 / 22.9 / 28 | 67.1 / 80.3 / 86.1 / 192.5 | 208.8 | 0 / 45012 | 0 | 4 | 16 | 0 |
| D workgroup | device | 32 | address +wg | load (56) | 4 | 50 | 0 | 8.2 / 16.6 / 21.7 / 46.7 | 58.6 / 74 / 85.9 / 185.8 | 187.6 | 0 / 90004 | 0 | 6 | 19 | 0 |
| D workgroup | device | 64 | address +wg | load (58) | 4 | 50 | 10 | 10 / 20.7 / 26.4 / 35.3 | 164.3 / 186.8 / 289.3 / 300.3 | 303.4 | 0 / 45000 | 0 | 4 | 41 | 0 |
| E no rt | device | 64 | address (no rt) | load (66) | 4 | 50 | 0 | 47.5 / 782.9 / 1052.4 / 1120.4 | 178.5 / 1195.9 / 1208.9 / 1215.2 | 1226.2 | 13962 / 43416 | 3649 | 4 | 17 | 400 |
