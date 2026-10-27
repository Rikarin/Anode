# ADR-0007: Timeline views render on canvas, not the DOM

- **Status:** accepted (spike S1); iPad, Windows and Linux GPUs still to confirm
- **Date:** 2026-10-27
- **Spike:** [S1](../spikes/S1-timeline-rendering.md). Code: `editor/libs/timeline` (`nx run timeline:bench-run`).

## Decision

The Tracks area, Piano Roll, Audio Track Editor, Step Editor, automation lanes and waveforms render through
`@anode/timeline`; the DOM is for chrome only (headers, inspectors, menus), built with xUI.

- **Renderer:** WebGL2 instanced quads in an **OffscreenCanvas worker**. The worker draws on **its own
  `requestAnimationFrame`, latest view wins** (message-driven pacing caused 30–50 ms hitches in WebKit). It costs about
  one frame of added latency in Chromium.
- **Main-thread mode** of the same renderer stays as an escape hatch.
- **Canvas2D is a correctness fallback only:** it cannot hold 60 fps with 20k notes in WebKit without level of detail.
- **Index:** sorted-by-start arrays per lane; an interval tree only for heavily nested long items.
- **CI:** frame-budget checks need GPU-backed Chromium (`--use-angle=metal` or a GPU runner); Playwright's default
  headless shell renders WebGL in software.
- An off-screen ARIA tree mirrors the focused lane for accessibility (not built yet).

## Evidence (M1 Max, 3200×1800 device px, 10 s of scroll, zoom and playhead)

21k–28k visible notes or 2.3k–3.1k regions: Chromium and Electron 45 at 120 fps (frame p99 9.4–10.8 ms), WebKit at
60 fps (p99 17.7 ms), **0 frames over 25 ms** with worker rAF pacing. Full frame cost 1.2–3.1 ms p50, so 3–5× headroom
at 60 Hz. Index build 27–42 ms for 295k notes; per-frame culling 0.3–2 ms.

## Open

Real iPad Safari, Windows and Linux GPUs, waveform textures, region-name text, recovery from a lost GPU context, and the
ARIA tree. Each configuration ran once.
