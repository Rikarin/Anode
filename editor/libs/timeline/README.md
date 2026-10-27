# @anode/timeline

The canvas/WebGL2 rendering engine behind every time-based view in Anode: the Tracks area, the Audio Track Editor, the
Piano Roll, the Step Editor, the Audio File Editor and automation lanes (docs/01-implementation-plan.md §6.3).

xUI has no timeline, piano roll or waveform component, and a DOM element per region or note does not scale to Logic-size
projects (10k+ regions, 100k+ notes). The plan:

- **Renderer**: WebGL2 (instanced quads for regions and notes, textures for waveform tiles) with a Canvas2D fallback,
  text from a glyph atlas, running in an `OffscreenCanvas` worker fed with transferable typed arrays from the stores.
- **Virtualisation**: an interval tree per lane, only visible tiles drawn, waveform level of detail from the Server's
  multi-resolution peak files.
- **Shared kernel**: the tick ↔ pixel scale (this library's first piece, `TimeScale`), grid and snap, selection,
  drag/resize/trim handles, the tool system, ruler, playhead, cycle area and locators.
- **Accessibility**: a parallel off-screen ARIA tree for the focused lane and keyboard navigation of regions and notes.

Framework-free TypeScript at its core, with thin Angular components on top, so the worker can import it.

## What exists (Phase 0 spike S1)

- `TimeScale`, `gridStep`/`gridLines`: the tick ↔ pixel and grid maths.
- `LaneIntervalIndex`: items bucketed per lane, sorted by start, with a running max end, so a viewport query is two
  binary searches and a scan per visible lane.
- `FrameBuilder`: scene + view → a backend-neutral `RectBatch` (lane backgrounds, grid, culled items, ruler, bar numbers,
  playhead).
- Renderers behind one `TimelineRenderer` interface: `WebGL2Renderer` (instanced quads, digit glyph atlas) and
  `Canvas2DRenderer` (colour-batched paths). `TimelineCanvas` ties a scene, a builder and a renderer together.
- `TimelineWorkerClient` + `timeline.worker.ts`: the same, in a worker on a transferred `OffscreenCanvas`; scene columns
  are transferred once, then one small view message per frame, latest wins while the worker is busy.

## Phase 1 additions

- `RegionDetails` on a scene (file offset, fades, gain, flags, names, waveform keys) and per-frame view overlays
  (cycle area, project end, marquee, `ItemOverride`s for drag previews).
- Waveforms: `parsePeaks` reads the Server's `PeakFormat`; a `WaveformSet` per renderer keeps several resolutions per
  file and the frame builder draws min/max columns from the finest useful one.
- Text: both renderers draw printable ASCII from one glyph atlas (bar numbers, region names).
- `hitTest`, `itemsInRect`, `snapStep`/`snapTick` for the pointer tools, on the main thread's copy of the scene.

Still open: waveform textures (columns are rectangles today), context-loss recovery, the ARIA tree.

## Bench

`bench/` is the S1 rendering bench (results and method: `docs/spikes/S1-timeline-rendering.md`).

- `pnpm exec nx run timeline:bench` serves it on http://localhost:4510/
  (`?scene=notes|regions&backend=webgl2|canvas2d&thread=worker|main&duration=10000`).
- `pnpm exec nx run timeline:bench-run` builds it and runs the matrix in Chromium, WebKit and Electron via Playwright
  (`-- --targets=chromium-headed,webkit-headed,electron --duration=10000`), writing JSON to
  `dist/bench/timeline-results/`. Needs `pnpm exec playwright install chromium webkit` once.
