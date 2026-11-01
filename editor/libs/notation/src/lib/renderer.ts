import type { ScoreLayout, ScoreViewMode } from '@anode/api-client';

/** How to engrave: the view (one long system, or pages), the score's layout, and the viewport. */
export interface RenderOptions {
  view: ScoreViewMode;
  /** Page size, margins, staff size and texts; Verovio's page and scale options follow it. */
  layout: ScoreLayout;
  /** Zoom in percent (100, the default: the layout's staff size). */
  zoom?: number;
}

/** One engraved page: its SVG (element ids as `ScoreIds`), 1-based number, and size in CSS pixels. */
export interface RenderedPage {
  page: number;
  svg: string;
  width: number;
  height: number;
}

/**
 * Engraves MEI into SVG pages (docs/adr/0011-notation-rendering.md): Verovio in a Web Worker, loaded lazily on the
 * first render. One page for the linear view.
 */
export interface NotationRenderer {
  render(mei: string, options: RenderOptions): Promise<RenderedPage[]>;
  /** Stops the worker. */
  dispose(): void;
}

/** Stands in until the editor workstream (C) adds Verovio: every render is refused. */
export class PendingNotationRenderer implements NotationRenderer {
  render(_mei: string, _options: RenderOptions): Promise<RenderedPage[]> {
    return Promise.reject(new Error('The notation renderer is not implemented yet (Phase 9, workstream C).'));
  }

  dispose(): void {
    // nothing to stop
  }
}
