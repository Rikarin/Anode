# Spike S1: timeline rendering at 60 fps

- **Question** (docs/01-implementation-plan.md §12, Phase 0 spike 1; ADR-0007): can a canvas/WebGL2 timeline render
  2,000 visible regions or 20,000 visible notes at 60 fps, in an OffscreenCanvas worker, in Chromium (Electron 45) and in
  WebKit (the Safari/iPadOS proxy)?
- **Date:** 2026-10-27
- **Answer:** **yes, go.** WebGL2 in an OffscreenCanvas worker held the display rate with zero frames over the 60 Hz
  budget in Chromium, Electron 45 and WebKit, provided the worker paces itself with its own `requestAnimationFrame`.
  Canvas2D is a usable fallback for regions everywhere and for notes in Chromium, but not for 20k notes in WebKit
  (~30 fps).
- **Code:** `editor/libs/timeline` (renderers, index, worker) and `editor/libs/timeline/bench` (bench page, Playwright
  runner). Condensed per-run results: [`S1-timeline-rendering.results.json`](S1-timeline-rendering.results.json).

## Machine

| | |
| --- | --- |
| Computer | MacBook Pro, Apple M1 Max (10 CPU cores, 32 GPU cores), 64 GB, on AC power |
| Display | Built-in Liquid Retina XDR, 3456 × 2234, ProMotion (Chromium ran at 120 Hz, WebKit caps `requestAnimationFrame` at 60 Hz) |
| OS | macOS 27.2 (Darwin 27.2.0) |
| Chromium | Chrome for Testing 156.0.8078.4 (Playwright 1.64.1), ANGLE on Metal |
| Electron | 45.0.0 (Chromium 156.0.8078.48), the workspace's devDependency, ANGLE on Metal |
| WebKit | Playwright WebKit 27.2 (macOS build), "Apple GPU" |

## What was built

All framework-free TypeScript in `@anode/timeline`:

- **Data.** `TimelineItems`: structure-of-arrays columns (`start`/`length` as `Float64Array` ticks, `lane` `Uint16Array`,
  `color` packed RGBA `Uint32Array`). Posted to the worker once, with the buffers transferred (no copy, no
  serialisation).
- **Spatial index.** `LaneIntervalIndex`: items bucketed per lane (CSR layout), sorted by start, with a running maximum of
  the end tick. A query per visible lane is two binary searches plus a scan of the candidates. Only items in the visible
  tick × lane window are turned into instances.
- **Frame building.** `FrameBuilder` turns scene + view into a backend-neutral `RectBatch` (lane shading, grid lines,
  culled items, ruler, bar numbers, playhead), reusing its typed arrays frame to frame.
- **WebGL2 renderer.** One unit quad, one instance per rectangle: `vec4` position/size (`Float32Array`), normalised
  RGBA bytes, a style byte. Borders and region name bars are shaded in the fragment shader, so it's one instance per
  item. Per frame: three orphaning `bufferData` uploads and one `drawArraysInstanced`, plus one more for bar-number
  glyphs from a digit atlas rasterised with OffscreenCanvas 2D.
- **Canvas2D renderer.** Same `RectBatch`, rectangles grouped by colour per layer: one path and one `fill` per colour
  (plus a border and a name-bar pass for notes/regions).
- **Worker.** `timeline.worker.ts` owns the transferred `OffscreenCanvas`. `TimelineWorkerClient` sends one small view
  message per frame, with one frame in flight and the latest view winning while the worker is busy. Two pacing modes:
  draw when the message arrives (`message`), or draw on the worker's own `requestAnimationFrame`
  (`animation-frame`, now the default).

## Method

- **Bench page** (`libs/timeline/bench`, Vite, served cross-origin isolated so `performance.now()` has 5 µs resolution):
  a 1600 × 900 CSS px canvas at DPR 2 (3200 × 1800 device px).
  - **Notes scene:** 128 pitches × 128 bars × 18 notes per pitch per bar (294,912 notes). The camera shows 96 pitches ×
    12–16 bars, so **21,035–28,139 notes are visible** on every frame.
  - **Regions scene:** 200 tracks × 256 bars (~58k regions). The camera shows 40 tracks × 48–64 bars, so **2,276–3,059
    regions are visible**.
  - The camera moves continuously from main-thread `requestAnimationFrame`: horizontal scroll (10 or 15 bars/s), zoom
    oscillating ±14% every 4 s, vertical scroll over the full lane range every 6 s, and a playhead sweeping the view every
    2 s.
  - Each run: 1 s warm-up, then **10 s recorded**, then 240 extra frames each forced to complete with `readPixels` /
    `getImageData` ("GPU-synced cost": the whole frame including GPU work, capped at 8 s).
- **Recorded per frame:** the time it was drawn (worker or main thread), its CPU cost (culling + building + issuing
  draw calls), the latency from the main-thread request to the draw, and the visible item count. Main-thread
  `requestAnimationFrame` intervals are recorded too.
  - **Frame interval** = time between consecutive draws.
  - **Dropped** = refreshes missed against the measured display refresh (an interval of ≈2 refreshes counts as 1).
  - **>25 ms** = intervals over 1.5 × 16.7 ms: frames that missed a 60 Hz deadline, which is the budget.
- **Matrix:** {notes, regions} × {WebGL2, Canvas2D} × {worker, main thread} for each target, driven by
  `libs/timeline/bench/run-bench.mts` (Playwright):
  - Chromium headed;
  - Chromium headless (full Chromium in new headless mode with `--use-angle=metal`, real GPU);
  - Electron 45 (a `BrowserWindow`, `electron-main.cjs`);
  - WebKit headed and headless;
  - Chromium headless shell (Playwright's default, which falls back to SwiftShader).
- **Two pacing runs:** worker runs were first measured with `message` pacing (the full matrix). The worker runs were
  then repeated with `animation-frame` pacing in WebKit, Electron and headed Chromium.
- **Reproduce** (from `editor/`): `pnpm exec playwright install chromium webkit` once, then
  `pnpm exec nx run timeline:bench-run`. Pass `-- --pacing=message` for the first series. Interactively:
  `pnpm exec nx run timeline:bench` and open `http://localhost:4510/?scene=notes&backend=webgl2&thread=worker`.

## Results

**Columns:**
- **fps:** frames drawn per second.
- **interval:** p50 / p95 / p99 between draws, in ms.
- **>25 ms:** frames over the 60 Hz budget, out of ~600 (WebKit) or ~1,200 (Chromium at 120 Hz).
- **CPU p95:** per-frame CPU cost.
- **synced:** p50 / p95 GPU-synced frame cost.
- **latency:** p50 / p99, worker only.

All times are in ms.

### 20,000+ visible notes

| Target (refresh) | Backend | Thread (pacing) | fps | interval | >25 ms | dropped | CPU p95 | synced | latency |
| --- | --- | --- | --: | --- | --: | --: | --: | --- | --- |
| Chromium headed (120 Hz) | WebGL2 | worker (message) | 120.1 | 8.3 / 9.3 / 9.4 | 0 | 0 | 0.43 | 1.3 / 1.5 | 0.4 / 0.6 |
| | WebGL2 | worker (animation-frame) | 120.0 | 8.3 / 10.0 / 11.3 | 0 | 1 | 1.55 | 1.8 / 4.3 | 8.7 / 11.6 |
| | WebGL2 | main | 120.0 | 8.3 / 9.1 / 9.2 | 0 | 0 | 0.39 | 1.2 / 1.4 | |
| | Canvas2D | worker (message) | 120.2 | 8.3 / 9.2 / 9.4 | 0 | 0 | 3.55 | 8.1 / 9.8 | 3.2 / 3.9 |
| | Canvas2D | main | 120.1 | 8.3 / 9.2 / 9.3 | 0 | 0 | 3.50 | 38 / 45 † | |
| Electron 45 (120 Hz) | WebGL2 | worker (message) | 120.0 | 8.3 / 11.6 / 12.9 | 0 | 19 | 0.93 | 1.6 / 2.5 | 0.5 / 3.5 |
| | WebGL2 | worker (animation-frame) | 120.1 | 8.3 / 9.9 / 10.8 | 0 | 0 | 1.35 | 1.7 / 3.4 | 8.7 / 11.2 |
| | WebGL2 | main | 120.1 | 8.3 / 9.1 / 9.3 | 0 | 0 | 0.47 | 1.9 / 2.8 | |
| | Canvas2D | worker (message) | 120.0 | 8.3 / 10.1 / 14.0 | 0 | 21 | 5.32 | 10.0 / 12.4 | 3.6 / 10.2 |
| | Canvas2D | worker (animation-frame) | 120.0 | 8.3 / 9.1 / 9.3 | 0 | 0 | 3.56 | 8.3 / 10.0 | 11.3 / 12.0 |
| | Canvas2D | main | 120.1 | 8.3 / 9.2 / 9.3 | 0 | 0 | 3.51 | 43 / 63 † | |
| WebKit headed (60 Hz) | WebGL2 | worker (message) | 59.5 | 16.7 / 21.1 / 31.6 | **15** | 16 | 1.05 | 1.6 / 2.2 | 0.7 / 30.7 |
| | WebGL2 | worker (animation-frame) | 60.0 | 16.7 / 17.3 / 17.7 | **0** | 0 | 2.48 | 3.1 / 4.8 | 2.3 / 2.9 |
| | WebGL2 | main | 59.5 | 16.7 / 17.5 / 18.2 | 1 | 6 | 2.43 | 1.9 / 3.9 | |
| | Canvas2D | worker (message) | 28.8 | 33.8 / 44.4 / 50.0 | 287 | 310 | 9.10 | 40 / 46 | 42 / 61 |
| | Canvas2D | worker (animation-frame) | 35.2 | 28.0 / 31.7 / 32.9 | 340 | 343 | 5.93 | 34 / 39 | 37 / 48 |
| | Canvas2D | main | 34.9 | 28.7 / 33.1 / 36.9 | 323 | 325 | 6.41 | 36 / 43 | |
| WebKit headless (60 Hz) | WebGL2 | worker (message) | 59.3 | 16.7 / 23.9 / 29.9 | 24 | 24 | 3.86 | 3.7 / 5.0 | 2.5 / 35.0 |
| | WebGL2 | worker (animation-frame) | 60.0 | 16.7 / 17.4 / 17.6 | 3 | 3 | 2.48 | 3.0 / 3.6 | 15.2 / 28.4 |
| | WebGL2 | main | 60.1 | 16.7 / 18.1 / 19.1 | 0 | 0 | 2.42 | 1.4 / 2.1 | |
| | Canvas2D | worker / main | 31.6 / 33.8 | 31.4 / 29.0 (p50) | 311 / 309 | | 8.4 | 38 / 32 (p50) | |
| Chromium headless, GPU (60 Hz) | WebGL2 | worker / main | 60.1 / 60.1 | 16.7 / 17.5 / 17.6 | 0 / 0 | 0 / 0 | 0.46 | 1.4 / 1.5 | 0.5 / 0.7 |
| | Canvas2D | worker / main | 60.1 / 60.1 | 16.7 / 17.4 / 17.7 | 0 / 0 | 0 / 0 | 3.6 | 8.3 / 10.1 | 3.3 / 3.9 |
| Chromium headless shell (SwiftShader) | WebGL2 | worker / main | 0.4 / 0.4 | 2,447–4,666 (p50) | | | 4.2 | 2,500–4,800 | |
| | Canvas2D | worker / main | 14.2 / 15.4 | 68 / 66 (p50) | | | 5.4 | 72 / 39 (p50) | |

† Chromium's accelerated Canvas2D on the main thread pays for a GPU readback in `getImageData`, so this "synced" cost
overstates real frame cost; its cadence (0 drops at 120 Hz) is the meaningful number.

### 2,000+ visible regions

| Target (refresh) | Backend | Thread (pacing) | fps | interval | >25 ms | dropped | CPU p95 | synced |
| --- | --- | --- | --: | --- | --: | --: | --: | --- |
| Chromium headed (120 Hz) | WebGL2 | worker / main | 120.0 / 120.1 | 8.3 / 9.3 / 9.3 | 0 / 0 | 0 / 0 | 0.14 | 0.8 / 1.0 |
| | Canvas2D | worker / main | 120.0 / 120.2 | 8.3 / 9.3 / 9.4 | 0 / 0 | 0 / 0 | 0.72 | 2.3 / 3.2 (worker) |
| Electron 45 (120 Hz) | WebGL2 | worker (message) / main | 120.0 / 120.1 | 8.3 / 9.2 / 10.1 | 0 / 0 | 0 / 0 | 0.31 | 1.3 / 2.4 |
| | WebGL2 | worker (animation-frame) | 120.1 | 8.3 / 9.3 / 10.2 | 0 | 0 | 0.38 | 1.0 / 2.6 |
| | Canvas2D | worker / main | 120.0 / 120.2 | 8.3 / 9.1 / 9.3 | 0 / 0 | 0 / 0 | 0.88 | 3.7 / 5.1 (worker) |
| WebKit headed (60 Hz) | WebGL2 | worker (message) | 59.6 | 16.7 / 23.9 / 31.2 | **23** | 25 | 0.55 | 1.3 / 3.6 |
| | WebGL2 | worker (animation-frame) | 60.1 | 16.7 / 17.5 / 17.7 | **0** | 0 | 0.38 | 0.9 / 1.2 |
| | WebGL2 | main | 60.1 | 16.7 / 17.5 / 17.8 | 0 | 0 | 0.44 | 0.9 / 1.9 |
| | Canvas2D | worker (message / animation-frame) | 60.1 / 60.0 | 16.7 / 17.5 / 18.0 | 0 / 0 | 0 / 0 | 1.24 | 8.5 / 9.4 |
| | Canvas2D | main | 60.1 | 16.7 / 17.5 / 18.3 | 1 | 1 | 1.34 | 8.2 / 10.0 |
| WebKit headless (60 Hz) | WebGL2 | worker (message / animation-frame) | 59.8 / 60.0 | 16.7 / 18.8 / 25.2 → 17.7 (p99) | 7 → 0 | 8 → 0 | 0.46 | 1.3 / 2.2 |
| | WebGL2 / Canvas2D | main | 60.1 | 16.7 / 17.4–17.6 / 17.6–21.7 | 0–3 | 0–3 | 1.3 | 1.1 / 8.5 |
| Chromium headless, GPU (60 Hz) | all four | | 60.1 | 16.7 / ≤19.6 / ≤20.7 | 0 | 0 | ≤3.5 | 0.9–12.6 |
| Chromium headless shell (SwiftShader) | WebGL2 | worker / main | 12.6 / 14.7 | 78 / 67 (p50) | | | 0.2 | 57 / 58 (p50) |

### Other measurements

- **Index build** (once per scene, in the worker):
  - 294,912 notes: 27–42 ms;
  - ~58k regions: 4.5–6 ms.
- **Per-frame culling + build** for 21k–28k visible notes: 0.3–0.5 ms p50 in Chromium, about 2 ms in WebKit (its JIT
  is slower on the typed-array loops). For ~3k regions: about 0.1 ms. The index is not a bottleneck.
- **Headroom.** The GPU-synced cost of a 28k-note WebGL2 frame at 3200 × 1800 is 1.2–3.1 ms p50 and ≤ 4.8 ms p95 on this
  machine. That's 3–5× under a 16.7 ms frame and 2–4× under an 8.3 ms (120 Hz) frame.
- **Main-thread smoothness.** In worker mode the main thread's own animation-frame intervals stayed at the display rate
  (p99 9.3 ms at 120 Hz, 17.6 ms at 60 Hz), apart from WebKit's Canvas2D runs.

## Did OffscreenCanvas + WebGL2 work?

| Target | `transferControlToOffscreen` | WebGL2 in worker | Worker `requestAnimationFrame` | GPU |
| --- | --- | --- | --- | --- |
| Chromium 156 (headed, new headless) | yes | yes | yes | ANGLE Metal, M1 Max |
| Electron 45.0.0 | yes | yes | yes | ANGLE Metal, M1 Max |
| WebKit 27.2 (headed, headless) | yes | yes | yes | Apple GPU |
| Chromium headless shell | yes | yes, but software (SwiftShader) | yes | SwiftShader |

No context creation failures, shader issues or fallbacks in any GPU-backed target. The Canvas2D-in-worker path
(OffscreenCanvas 2D) also worked everywhere.

## Verdict against the budget

The budget is 60 fps with 2,000 visible regions or 20,000 visible notes.

- **WebGL2 in an OffscreenCanvas worker: pass** in Chromium, Electron 45 and WebKit. With `animation-frame` pacing, no
  frame missed a 60 Hz deadline in any of them:
  - Chromium and Electron held **120 Hz** with 20k–28k notes;
  - WebKit held its 60 Hz cap with 0 drops headed.
- **WebKit needs worker-side pacing.** With `message` pacing, WebKit's worker path missed 2.5–4% of 60 Hz deadlines
  (p99 interval ~30 ms, worst 50 ms, latency p99 ~30 ms). The per-frame work was ~1–4 ms, so this is WebKit's
  OffscreenCanvas commit scheduling, not load. Drawing on the worker's own `requestAnimationFrame` removed it entirely
  (0 frames over budget headed, p99 17.7 ms).
- **Canvas2D fallback: partial.**
  - It passes for 2k+ regions everywhere, and for 20k+ notes in Chromium/Electron (3.5 ms CPU; 0 drops at 120 Hz).
  - It **fails for 20k notes in WebKit** at 29–35 fps, about 30 ms per frame, on the main thread or in a worker.
- **Main-thread WebGL2** also passes everywhere. The worker is about isolation from main-thread work (Angular,
  IPC, GC), not throughput.

## Recommendation for ADR-0007

Accept the proposal, with these amendments:

1. **Primary path:** WebGL2 instanced quads in an OffscreenCanvas worker, as proposed.
   - Scene data goes over as transferred structure-of-arrays columns; then one small view message per frame, one in
     flight, latest wins.
   - The worker **draws on its own `requestAnimationFrame`**, not on message arrival. Message-driven drawing was
     measurably janky in WebKit.
   - The cost is about one display frame of added input-to-draw latency in Chromium/Electron (p50 8.7 ms at 120 Hz vs
     0.5 ms). If that matters for direct manipulation (drag, pencil), consider `message` pacing on Chromium/Electron
     only, where it was also within budget.
2. **Keep the same renderer runnable on the main thread** (`TimelineCanvas` is thread-agnostic). This is the escape
   hatch if a platform's worker canvas misbehaves, and the easiest place for tests and Storybook.
3. **Canvas2D is a correctness fallback, not a performance-equivalent one.** On WebKit it needs level of detail to stay
   within budget at piano-roll densities. For example, merge notes narrower than one device pixel per lane into
   coverage runs, or cap drawn items and show density. Make that a requirement for the Canvas2D path before it ships on
   iPadOS.
4. **Spatial index:** per-lane sorted-by-start arrays with a running max end are sufficient:
   - 30–40 ms to index 295k notes;
   - well under 1 ms per frame to cull 28k visible notes.

   Keep the interval tree in the plan only as the fix for pathological nesting (very long items overlapping many short
   ones), which this layout scans linearly.
5. **Performance CI:** Playwright's default Chromium headless shell renders WebGL in software (SwiftShader: 0.4 fps
   here), so it can't enforce frame budgets. Budget checks need full Chromium with a GPU (`channel: 'chromium'`,
   `--use-angle=metal` on a macOS runner, or a GPU Linux runner). Otherwise gate only on per-frame CPU cost.
6. **Still to verify** before calling iPadOS done: a real iPad (Safari on iPadOS, A-series and M-series GPUs), Windows
   and Linux Electron GPUs, and the parts of ADR-0007 this spike did not exercise:
   - waveform textures;
   - glyph-atlas text for region names (only bar numbers were drawn);
   - context-loss recovery;
   - the off-screen ARIA tree.

   The M1 Max headroom (≤ 5 ms p95 GPU-synced for 28k notes) suggests a GPU 3× slower would still fit 60 Hz.

## Caveats and things that did not work

- **WebKit is a proxy.** WebKit here is Playwright's macOS build, not Safari on iPadOS: same engine, different GPU
  and thermal envelope. WebKit caps `requestAnimationFrame` at 60 Hz, so its numbers are against 60 Hz; Chromium and
  Electron ran at the display's 120 Hz.
- **Draw time, not presentation time.** Frame intervals are measured when the worker (or main thread) finishes issuing
  a frame; no API exposes presentation time to a worker. The GPU-synced pass and the main-thread intervals are the
  cross-checks. Each configuration ran once for 10 s; repeat runs were not done, so treat single-digit drop counts as
  noise.
- **Harness quirk, not a product issue.** Headed and new-headless Chrome for Testing crashed at startup (SIGBUS)
  whenever the runner was the foreground process of the agent's shell. Running it under `timeout` (its own process
  group) avoided it every time. The runner also builds the page in a child process rather than in-process.
- **The headless shell's numbers** are software rendering and are reported only to show why CI cannot use it for
  budgets.
- **Not tested:** variable refresh, battery and low-power mode, very long items (index worst case), more than one
  timeline view at once.
