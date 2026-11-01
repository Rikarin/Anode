# @anode/notation

The Score Editor's engraving (docs/10-phase-9.md, docs/adr/0011-notation-rendering.md): the Server's notation engine
derives the score (`GET /project/score`: a `ScoreView` and its MEI 5), and this library turns the MEI into SVG with
Verovio (the `verovio` npm package 6.4.0, LGPL-3.0, loaded unmodified as its own files, lazily, in a Web Worker), and
maps SVG elements back to the model.

- `VerovioNotationRenderer` (`NotationRenderer`): starts the worker (`verovio.worker.ts`) on the first render; the
  worker imports `verovio-module.mjs` (the WebAssembly is embedded in it) and `verovio.mjs` from `<editor>/verovio/`,
  which the editor build copies from `node_modules/verovio/dist` (apps/editor/project.json). One render at a time; a
  newer request replaces a waiting one (`RenderSuperseded`).
- `VerovioEngine`: the worker's engine (also used in Node by the tests and the spike): a cache of the loaded MEI and
  options, `redoLayout` when only the options change, and `RenderOptions.pages` to engrave only the visible pages
  after an edit. `engraveOptions(view, layout)`: page size, margins and staff size (Verovio's `unit`) from the layout;
  the linear view is one system (`breaks: none`). Zoom is CSS (`pageSize`), never a re-engrave.
- The id map: `ScoreIds`, `noteIdOf`, `symbolIdOf`, `lyricIdOf`, `chordSymbolIdOf`; and reading the SVG back
  (`geometry.ts`): `hitOf` (an element's model object, checked by class and prefix), `notePoints`, `notesInRect`,
  `noteNear`, `staffBoxes`, `staffAt`, `staffPositionAt`, `stepsOfDrag`, `tickAtX` (a display tick measured against
  the drawn events), `toScorePoint` and `fromScorePoint` (pointer ↔ drawing units).

`nx run notation:spike` measures ADR-0011's criteria (recorded there).
