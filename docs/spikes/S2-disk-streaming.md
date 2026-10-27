# S2: Disk streaming of 128 tracks at 64 frames, cycle jump included

- **Status:** green (go)
- **Date:** 2026-10-27
- **Spike:** S2, from [01-implementation-plan.md §12, Phase 0](../01-implementation-plan.md#phase-0-foundations-and-spikes-6-weeks); the
  design under test is [§3.2 Disk streaming](../01-implementation-plan.md#32-real-time-engine-anodeengine-anode-host). Harness:
  `engine/spikes/Anode.Spikes.DiskStream`.

## Question

Can a NativeAOT .NET engine stream 128 mono tracks of 48 kHz / 24-bit audio from disk at 64-frame buffers, including a
cycle (loop) jump, with **no underruns** and **zero allocation on the audio thread**?

## Answer

**Yes, with a wide margin.** Across 26 measured configurations (32, 64, 128 and 256 tracks; warm and cold page cache;
no cycle, an 8-bar cycle and a 1-bar cycle; with and without a competing reader saturating the SSD at ~2.8 GB/s), the
default design had:

- **0 underruns, 0 wrong samples, 0 driver overloads, 0 bytes allocated on the callback thread and 0 GCs.**
- At 128 tracks the callback took **27–44 µs at p50** and **≤ 113 µs at worst** (2–3 % and 8.7 % of the 1,333 µs
  period). At 256 tracks the worst was 187 µs (14 %).
- The rings never dropped below **1.15 s** of the 2 s they hold, while the slowest single read took 59 ms.
- The whole process used **4–6 % of one core** at 128 tracks, and the callback alone used 2–3 %.

The only underruns came from deliberately undersized rings in the design sweep (0.25 s; 0.5 s with four disk threads).
They set the lower bound in the recommended parameters below.

## How it was measured

- **Machine:** MacBook Pro, Apple M1 Max (10 cores), 64 GB RAM, internal Apple SSD (Apple Fabric NVMe, 161 GB free of
  926 GB), macOS 27.2. .NET SDK 10.0.403.
- **Build:** NativeAOT publish with Anode.Host's GC settings (workstation, non-concurrent) and the host's gen0 budget
  `DOTNET_GCgen0size=10000000` (ADR-0001).
- **Device:** MacBook Pro Speakers through `cth_audio` (`engine/native`), 48 kHz, 64 frames. The output stays silent
  because the callback mixes into a bus that is discarded. Anode.Audio has no public stream API yet (only its device
  list), so the spike binds the `cth_audio_open/start/stop/stats/timing` entry points itself.
- **Test files:** `track000.wav` to `track255.wav`, mono 48 kHz / 24-bit PCM, 2 to 5 minutes long (7.7 GB in total),
  written to `engine/artifacts/spike-audio` (git-ignored). The audio data starts at byte 4096 (behind a JUNK chunk, as
  BWF writers do), so page-aligned reads are also frame-aligned. Each sample is a function of its frame index and track.
- **Streamer** (one per disk, `DiskStreamer.cs`):
  - Each disk thread owns a share of the tracks.
  - Each pass, it refills the track with the least audio buffered among those whose ring has room for a whole read.
  - Reads are single positional reads (`RandomAccess.Read`, i.e. `pread`), widened to page boundaries, into a native
    page-aligned scratch buffer. The thread decodes 24-bit to float straight into the track's ring.
  - It walks the transport's play order. At the cycle end it clips the read and continues from the cycle start, so the
    ring already holds the audio after the jump when the playhead gets there.
  - It sleeps 1 ms when no ring needs a read.
- **Rings:** one lock-free SPSC float ring per track, in native memory (`SpscRing.cs`). 2 s × 128 tracks is 49 MB that
  stays off the GC heap.
- **Callback** (`Mixer.cs`):
  - Mixes every track's next 64 frames into a panned stereo bus.
  - Before playing a track, checks that its next sample is the file's sample for the current timeline position
    ("position errors"). This proves the cycle jump is seamless and sample-exact.
  - On an underrun it plays silence and later drops the late frames to stay in time.
  - Records its duration, its allocation (`GC.GetAllocatedBytesForCurrentThread` against a baseline taken on that same
    thread) and the lowest ring level after mixing ("low water").
- **Cold cache:**
  - Before the run, each file is mapped and `msync(MS_INVALIDATE)` drops it from the unified buffer cache.
  - `mincore` confirms 0 % residency (the "Resident" column).
  - During the run the streamer reads with `F_NOCACHE`, so cycle re-reads go to the SSD too.
  - Verified independently: `cat` of all 3.9 GB took 0.84 s after eviction (SSD) against 0.36 s warm (RAM).
- **Warm cache:** every file is read once through the page cache first (`--prewarm`), which gives 100 % residency.
- **Competing load (`--io-hog`):** a second thread reads all the test files back to back in 1 MiB `F_NOCACHE` reads,
  unthrottled. It reached 2.7–2.9 GB/s. This stands in for a sample library loading or a backup running.
- **Cycle:** 120 bpm in 4/4. The 8-bar cycle (bars 5–12, 16 s) jumps twice in a 40 s window. The 1-bar cycle (2 s,
  which equals the ring) jumps every 2 s.
- **Each run:** the rings are primed until full ("Prime", the time a locate takes), then 3 s of warm-up and 40 s
  measured (30 s for the headroom rows, 20 s for the sweep).
- **Self-tests:** the checks must catch what they claim to catch. A streamer that ignores the cycle (`--naive-cycle`)
  produced 42,064 position errors. Allocating 40 B per callback (`--alloc-in-callback`) was reported as 120,120 B over
  3,003 callbacks. An earlier trial with a 0.1 s ring and 12 KiB reads produced 2,117 underruns.
- **Reproduce:** `engine/spikes/Anode.Spikes.DiskStream/run.sh BuiltInSpeakerDevice` (or `run.sh` alone, for the
  null clock: a time-constraint real-time thread ticking at the buffer period), then `python3 -I summarize.py
  results.jsonl`. The raw data is committed: `results.jsonl` and `run.log`.

## Results

"Low water" is the least audio any ring held after a callback, which is the headroom left at worst. "Worst % of
period" is the longest callback against the 1,333 µs budget. The managed and native (`cth_audio` around the whole call)
figures agree, and the larger one is shown. "Disk" is the measured read rate; the rate needed for real time is in
parentheses, and the ~4 % above it comes from widening reads to page boundaries.

### Main matrix (default design: ring 2 s, 96 KiB reads, one disk thread)

| Tracks | Cache | Cycle | Prime | Underruns | Position errors | Jumps | Low water | Callback p50 / p99 / max (µs) | Worst % | Callback alloc | GCs | Disk MB/s (needed) | Read p50 / p99 / max (ms) | CPU process / callback |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 32 | warm | — | 11 ms | 0 | 0 | 0 | 1239 ms | 10.0 / 20.0 / 49.0 | 3.8 % | 0 B | 0 | 4.8 (4.6) | 0.07 / 0.95 / 59.3 | 2.1 % / 0.8 % |
| 32 | warm | 8 bars | 11 ms | 0 | 0 | 2 | 1304 ms | 9.5 / 20.5 / 55.8 | 4.2 % | 0 B | 0 | 4.8 (4.6) | 0.07 / 1.06 / 4.9 | 2.1 % / 0.8 % |
| 32 | cold | — | 16 ms | 0 | 0 | 0 | 1256 ms | 9.5 / 19.5 / 40.5 | 3.3 % | 0 B | 0 | 4.8 (4.6) | 0.25 / 4.18 / 15.2 | 2.2 % / 0.7 % |
| 32 | cold | 8 bars | 18 ms | 0 | 0 | 2 | 1263 ms | 9.0 / 20.5 / 52.5 | 4.0 % | 0 B | 0 | 4.8 (4.6) | 0.24 / 1.97 / 11.7 | 2.2 % / 0.7 % |
| 64 | warm | — | 9 ms | 0 | 0 | 0 | 1297 ms | 17.0 / 36.5 / 57.3 | 4.5 % | 0 B | 0 | 9.5 (9.2) | 0.07 / 0.49 / 5.5 | 3.1 % / 1.3 % |
| 64 | warm | 8 bars | 9 ms | 0 | 0 | 2 | 1305 ms | 24.0 / 41.0 / 61.2 | 5.0 % | 0 B | 0 | 9.6 (9.2) | 0.07 / 0.47 / 5.1 | 3.8 % / 1.7 % |
| 64 | cold | — | 36 ms | 0 | 0 | 0 | 1152 ms | 24.0 / 36.5 / 76.0 | 5.8 % | 0 B | 0 | 9.5 (9.2) | 0.24 / 3.76 / 23.1 | 4.1 % / 1.8 % |
| 64 | cold | 8 bars | 37 ms | 0 | 0 | 2 | 1231 ms | 26.5 / 46.5 / 87.5 | 6.7 % | 0 B | 0 | 9.6 (9.2) | 0.24 / 4.78 / 10.1 | 4.1 % / 2.0 % |
| **128** | warm | — | 19 ms | **0** | 0 | 0 | 1272 ms | 44.0 / 71.5 / 107.9 | 8.3 % | **0 B** | 0 | 19.0 (18.4) | 0.07 / 1.13 / 7.2 | 6.0 % / 3.3 % |
| **128** | warm | 8 bars | 18 ms | **0** | 0 | 2 | 1300 ms | 29.5 / 52.5 / 113.0 | 8.7 % | **0 B** | 0 | 19.3 (18.4) | 0.06 / 0.14 / 2.0 | 4.3 % / 2.2 % |
| **128** | cold | — | 57 ms | **0** | 0 | 0 | 1255 ms | 27.5 / 50.5 / 108.8 | 8.4 % | **0 B** | 0 | 19.0 (18.4) | 0.22 / 0.63 / 14.1 | 4.3 % / 2.1 % |
| **128** | cold | 8 bars | 62 ms | **0** | **0** | 2 | 1251 ms | 27.5 / 58.0 / 102.5 | 7.8 % | **0 B** | 0 | 19.3 (18.4) | 0.22 / 1.15 / 4.8 | 4.6 % / 2.2 % |

### Headroom (cold, default design)

| Configuration | Prime | Underruns | Position errors | Jumps | Low water | Callback p50 / p99 / max (µs) | Worst % | Alloc | Disk MB/s (needed) | Read p50 / p99 / max (ms) | Competing load | CPU process / callback |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 128, 1-bar cycle (jump every 2 s) | 58 ms | 0 | 0 | 15 | 1209 ms | 26.0 / 45.5 / 88.8 | 6.8 % | 0 B | 19.1 (18.4) | 0.22 / 1.00 / 13.4 | — | 4.1 % / 2.0 % |
| 128, 8-bar cycle + io hog | 57 ms | 0 | 0 | 2 | 1255 ms | 29.0 / 52.5 / 94.5 | 7.2 % | 0 B | 19.2 (18.4) | 0.24 / 0.59 / 3.6 | 2772 MB/s | 14.1 % / 2.2 % |
| 256, 8-bar cycle | 146 ms | 0 | 0 | 2 | 1211 ms | 62.0 / 110.5 / 187.4 | 14.1 % | 0 B | 38.3 (36.9) | 0.22 / 1.47 / 5.3 | — | 8.7 % / 4.9 % |
| 256, 8-bar cycle + io hog | 120 ms | 0 | 0 | 2 | 1185 ms | 49.5 / 90.0 / 135.7 | 10.3 % | 0 B | 38.3 (36.9) | 0.24 / 0.64 / 7.0 | 2794 MB/s | 15.9 % / 3.9 % |

The process CPU with the competing load includes the reader thread itself.

### Design sweep (cold, 1-bar cycle, competing load at ~2.8 GB/s)

| Tracks | Ring | Read | Disk threads | Underruns | Low water | Read p50 / p99 / max (ms) | Worst % |
|---|---|---|---|---|---|---|---|
| 128 | 0.25 s | 24 KiB | 1 | **18** | 0 ms | 0.18 / 0.44 / 10.3 | 6.0 % |
| 128 | 0.5 s | 24 KiB | 1 | 0 | 279 ms | 0.18 / 0.41 / 4.6 | 9.6 % |
| 128 | 0.5 s | 48 KiB | 1 | 0 | 125 ms | 0.20 / 0.42 / 2.0 | 6.3 % |
| 128 | 1 s | 48 KiB | 1 | 0 | 607 ms | 0.20 / 0.43 / 2.6 | 5.9 % |
| 128 | 1 s | 96 KiB | 1 | 0 | 280 ms | 0.24 / 0.47 / 2.8 | 10.1 % |
| 128 | 2 s | 192 KiB | 1 | 0 | 589 ms | 0.28 / 0.52 / 2.7 | 6.6 % |
| 128 | 4 s | 384 KiB | 1 | 0 | 1211 ms | 0.39 / 1.42 / 2.8 | 6.7 % |
| 256 | 0.5 s | 48 KiB | 1 | 0 | 100 ms | 0.20 / 0.42 / 1.9 | 12.5 % |
| 256 | 0.5 s | 48 KiB | 2 | 0 | 125 ms | 0.21 / 0.49 / 3.2 | 10.3 % |
| 256 | 0.5 s | 48 KiB | 4 | **48** | 0 ms | 0.23 / 3.25 / 9.3 | 14.4 % |

A first pass of the same matrix (before warm runs were prewarmed) also had zero underruns in every row of the main
matrix and the headroom table. In the sweep, its 0.25 s and four-thread rows were clean, with low water of 36 ms and
64 ms. Rings under 1 s sit on that edge: whether they underrun depends on the run.

## Findings

1. **Disk bandwidth is not the constraint on an NVMe SSD.**
   - 128 tracks need 18.4 MB/s, under 1 % of what this SSD delivers sequentially (4.6 GB/s cold).
   - Even with another reader taking ~2.8 GB/s, read latency stayed at 0.2 ms p50 and a few ms p99.
   - The worst single read in the whole matrix was 59 ms (warm, 32 tracks, a one-off). That is still under 5 % of the
     2 s ring.
2. **The cycle jump costs nothing when the streamer follows play order.**
   - The ring already holds the audio after the jump, so the callback does not know a jump happened.
   - The sample check found **no wrong frame** across 2 to 15 jumps per run. The deliberately naive streamer, run as a
     control, failed the check 42,064 times.
   - A 1-bar cycle as long as the ring (re-reading its start every 2 s) behaved like no cycle at all.
3. **Zero allocation and zero GCs hold on the callback in NativeAOT.**
   - The callback touches only preallocated arrays and native rings. The allocation check is proven to fire (self-test).
   - No collection ran in any run, the disk threads included. They decode into native memory, and their only managed
     objects are created before the run starts.
4. **The callback scales linearly at ~0.2–0.35 µs per track.**
   - This covers the mix plus the sample check, scalar and not yet vectorised. 128 tracks take 2–3 % of the period at
     p50 and under 9 % at worst.
   - Streaming leaves nearly the whole budget for DSP and plugins.
5. **Locates are fast.** Filling 2 s for all 128 tracks from a cold cache took ~60 ms (37 MB), and 256 tracks took
   ~130 ms. A transport can start rolling once each ring holds a few hundred ms, well before that.
6. **The failures in the sweep come from scheduling, not the disk.**
   - The streamer's rule is "refill once a whole read fits". With a ring barely bigger than one read, every ring drains
     in lockstep after the prime and becomes eligible together.
   - One read per track is then queued behind the others, which adds up to tens of ms. A ring that small holds only
     ~80 ms when it asks.
   - Four threads made it worse, not better: they contend for the CPU with the competing reader and with each other,
     and the SSD gains nothing from more queue depth at 40 MB/s.

## Recommended design parameters

| Parameter | Value | Why |
|---|---|---|
| Ring per track | **2 s** (minimum 1 s) | 2 s kept ≥ 1.15 s of headroom everywhere. Below 1 s the margin depends on scheduling (finding 6), and slower disks (USB, HDD, network) have read tails this SSD does not. 2 s × 128 mono tracks as float is 49 MB of native memory. |
| Read size | **96 KiB per read (32,768 frames, 0.68 s) for mono**, about ⅓ of the ring; scale with channel count | Large enough that 128 tracks need only ~190 reads/s, small enough that the refill threshold leaves > 1 s in the ring. Page-align the file offset (widen and discard), which costs ~4 % extra bandwidth and keeps `F_NOCACHE` reads unbuffered. |
| Disk threads | **1 per physical device** | One thread sustained 256 tracks plus a competing 2.8 GB/s reader. More threads did not help, and four hurt. Revisit for RAID or network volumes. |
| Refill policy | Emptiest-first, refill when **below ⅔ of the ring** (not "when a whole read fits"), with a deadline-ordered queue | Avoids the lockstep drain in finding 6 and lets rings smaller than 1 s work if memory ever matters (it does not at 49 MB). |
| Cycle | Stream in **play order**: clip the read at the cycle end and continue from the cycle start. On a cycle or locate change while playing, flush and re-prime from the new play order | Gives a seamless, sample-exact jump with no special case on the audio thread. Re-priming 128 tracks takes ~60 ms cold. |
| Page cache | Read with **`F_NOCACHE`** on macOS (`O_DIRECT` or `posix_fadvise(DONTNEED)` on Linux, `FILE_FLAG_NO_BUFFERING` on Windows) | Streams are read once per pass, so caching them only evicts other things. Cold reads were fast enough that the cache gave nothing measurable here. A shared block cache (§3.2) should hold cycle starts and region heads, not whole files. |
| Ring memory | **Native**, 64-byte aligned (`NativeMemory.AlignedAlloc`) | Keeps tens of MB off the LOH and out of gen2 budgets (Cathode ADR-0001's follow-up). |
| Ring format | float (as measured) | int24-in-int32 or int16 would halve or quarter the memory, but memory is not the constraint. Keep float until it is. |

## Notes and limits

- **One machine, one internal NVMe SSD, macOS only.** No spinning disk, USB/Thunderbolt external drive, network volume
  or Windows/Linux was measured. The 2 s ring is chosen with those in mind: their read tails (tens to hundreds of ms)
  still fit in it, but that has to be confirmed when the Windows and Linux backends exist. An external-drive run of
  this harness (`--dir` on that volume) is cheap and should be done before Phase 1 closes.
- **Freshly written, contiguous files.** Fragmentation and many short regions per track (more seeks) were not tested.
  Comped takes and edits will raise the read count. At ~0.2 ms per cold read there is room for ~20× the read rate
  measured here.
- **Mono only.** Stereo tracks double the bytes and halve the frames per read at the same read size. 256 mono tracks
  (the 256 rows) stand in for 128 stereo tracks, and they passed.
- **The callback did no DSP.** It measures streaming and mixing, not a full graph. Its 2–3 % share is what streaming
  adds to the budget.
- **The device was the built-in speakers,** with CoreAudio's own I/O thread. A null clock (a time-constraint real-time
  thread, `run.sh` without a device) gave the same picture in trials: ~26 µs p50 and ~144 µs worst at 128 tracks. The
  same thread without the real-time policy showed 6.5 ms preemptions, so a null-clock soak must use the time-constraint
  policy (Linux: `SCHED_FIFO`).
- **Cold-cache eviction** uses `msync(MS_INVALIDATE)` on a mapping, which needs no root (unlike `purge`). It is verified
  by `mincore` and by read throughput, as above.
- **`fcntl` is variadic,** and on Apple arm64 its third argument is passed on the stack. The spike's P/Invoke passes it
  as a ninth argument (`Posix.cs`). The engine should set `F_NOCACHE` in the native shim instead.
- **Anode.Audio exposes no stream API yet.** The spike binds `cth_audio` itself. Phase 1's session type should cover
  open, start, stop, stats and timing.
- The process's 4–6 % CPU at 128 tracks includes the disk thread and its 1 ms polling sleep. An event signalled by the
  callback (a semaphore post, real-time safe on macOS) would remove the polling.
