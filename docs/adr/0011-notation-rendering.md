# ADR-0011: The score is derived on the server and engraved by Verovio in the editor

- **Status:** accepted
- **Date:** 2026-11-01

## Context

The Score Editor (Phase 9) has to turn MIDI into readable notation, engrave it, let the user edit it by clicking, and
print it. The plan (§6) proposed Verovio (LGPL-3.0, C++ compiled to WebAssembly) rendering MEI or MusicXML that the
server generates, with VexFlow (MIT) and an in-house layout as the alternative, and asked for a spike.

## Decision

- **Split of work:** the musical decisions (display quantise, durations, ties, tuplets, voices, spelling, measures)
  are made on the server by `Anode.Notation`, deterministically, so MusicXML export, PDF and the editor agree. The
  graphical engraving (spacing, beams' slopes, collision avoidance, system and page breaks within the layout) is
  Verovio's.
- **Renderer: Verovio**, from the `verovio` npm package, in a Web Worker in the editor, fed MEI 5 from the server.
  VexFlow would need an in-house layout engine for systems, pages, collisions and lyrics, which is most of the
  engraving work; Verovio does it and reads MEI natively.
- **Licence:** Verovio is LGPL-3.0. It is loaded as its own WebAssembly and JavaScript files, unmodified, so it stays
  replaceable as the LGPL requires; it is listed in ADR-0008.
- **Mapping back:** MEI elements carry `xml:id`s derived from model ids (`n{noteId}`, `s{symbolId}`). Verovio keeps
  them in its SVG, so clicks map to notes and symbols without asking the server.
- **Printing:** the page view's SVG, printed to PDF by Electron (`printToPDF`) on desktop and by the browser elsewhere.
- **Spike criteria** (measured by workstream C, recorded here): a 4-staff, 100-bar score renders in the worker in under
  500 ms, and re-renders a one-note edit in under 150 ms on an M1; the WebAssembly loads lazily, outside the initial
  bundle. If Verovio misses them badly, revisit with VexFlow for the linear view only.

## Spike measurements

Measured by workstream C on 2026-11-01 on an Apple M1 Max (64 GB), Verovio 6.4.0 (`verovio` npm, pinned), with the
engine the worker runs (`libs/notation`, `VerovioEngine`) on a 4-staff, 100-bar score (each bar per staff: four beamed
eighths, two quarters, a syllable; a dynamic and a chord symbol per bar; `spike-mei.ts`), A4 at 7 mm staves. Medians of
five to seven runs; "one-note edit" is new MEI with one pitch changed, same options.

| | Linear view (one system) | Page view (7 pages) |
|---|---|---|
| Full render, Node (`nx run notation:spike`) | 142 ms | 177 ms (all pages) |
| One-note edit, Node | 141 ms | 179 ms all pages; **106 ms** the visible page |
| Full render, Chromium, worker round trip | 145 ms | 190 ms |
| One-note edit, Chromium, worker round trip | 147–149 ms | 195 ms all pages; **116 ms** the visible page |
| First load in the worker (fetch and compile the WebAssembly, first small render) | 136 ms | |

Both criteria are met: under 500 ms for the full score, and under 150 ms for a one-note edit with the re-render path
(the page view engraves the visible pages first, the rest from the cache right after; the linear view is one page and
sits just under the limit, the cost being Verovio's layout of the whole system, not SVG output). Zoom never re-engraves
(the pages scale as CSS), and an options change on unchanged MEI re-lays out without parsing (`redoLayout`). The
WebAssembly loads lazily: `verovio-module.mjs` (7.4 MB, the WebAssembly embedded in Verovio's own module) and
`verovio.mjs` are copied unmodified to `<editor>/verovio/` by the build and imported by the worker on its first render,
so they are replaceable files outside every bundle; the editor's initial bundle does not grow by Verovio.

Findings for the integration: Verovio 6.4 ignores `pgHead` (the page header with the title) in MEI that says
`meiversion="5.1"`; the renderer gives it `5.0` (`forVerovio`), whose header encoding is the same, so the engine's MEI
writer may say either. Verovio draws titles, labels, lyrics and harmony as SVG `<text>` (kept as text in a PDF), and
generates ids for elements without an `xml:id` that can begin with the engine's prefixes (`s1x4…`), so hit testing
checks an element's class and the model's `ssym_`, `llyr_`, `hchd_` prefixes as well as its id. The desktop CSP allows
`'wasm-unsafe-eval'` for the worker's WebAssembly.

## Consequences

- Two layout engines are never involved: what the editor shows is what prints.
- The editor bundle grows by Verovio (several MB), loaded only when the Score Editor opens.
- The server never engraves, so a headless server cannot produce PDFs by itself (an export from the editor can).
