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

Phase 0 spike: 60 fps with 20k visible notes in Electron and iPad Safari.
