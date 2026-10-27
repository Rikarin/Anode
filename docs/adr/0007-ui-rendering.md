# ADR-0007: Timeline views render on canvas, not the DOM

- **Status:** proposed (Phase 0 spike 1 decides)
- **Date:** 2026-10-27

## Proposal

The Tracks area, Piano Roll, Audio Track Editor, Step Editor, automation lanes and waveforms render through
`@anode/timeline`: WebGL2 (instanced quads, waveform textures, glyph atlas) in an OffscreenCanvas worker, with a
Canvas2D fallback. The DOM is for chrome only (headers, inspectors, menus), built with xUI. An off-screen ARIA tree
mirrors the focused lane for accessibility.

Target: 60 fps with 2,000 visible regions or 20,000 visible notes, in Electron and in Safari on iPadOS.
