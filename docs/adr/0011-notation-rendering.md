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

## Consequences

- Two layout engines are never involved: what the editor shows is what prints.
- The editor bundle grows by Verovio (several MB), loaded only when the Score Editor opens.
- The server never engraves, so a headless server cannot produce PDFs by itself (an export from the editor can).
