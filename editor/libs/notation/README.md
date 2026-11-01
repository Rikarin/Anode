# @anode/notation

The Score Editor's engraving (docs/10-phase-9.md, docs/adr/0011-notation-rendering.md): the Server's notation engine
derives the score (`GET /project/score`: a `ScoreView` and its MEI 5), and this library turns the MEI into SVG with
Verovio (the `verovio` npm package, LGPL-3.0, loaded unmodified as its own WebAssembly and JavaScript files, lazily, in a
Web Worker), and maps SVG elements back to the model.

## What exists (Phase 9 contracts)

- `NotationRenderer`: `render(mei, options)` → the pages of SVG (one page for the linear view), with
  `RenderOptions` (view, layout, width and zoom). `PendingNotationRenderer` stands in until workstream C, rejecting
  every call.
- The id map, implemented: `ScoreIds` (the element ids the engine writes as MEI `xml:id`s, which Verovio keeps in its
  SVG) and `noteIdOf`, `symbolIdOf`, `lyricIdOf`, `chordSymbolIdOf` to read them back.

Workstream C adds the worker, the Verovio loader and options from the layout, and records ADR-0011's spike measurements.
