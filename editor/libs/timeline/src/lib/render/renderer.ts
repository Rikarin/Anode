import { type RectBatch } from '../frame-builder';

export type RendererBackend = 'webgl2' | 'canvas2d';

/** A canvas a renderer can draw into: a DOM canvas on the main thread, or an `OffscreenCanvas` in a worker. */
export type RenderTarget = HTMLCanvasElement | OffscreenCanvas;

/** Draws {@link RectBatch}es into a canvas. WebGL2 and Canvas2D implement the same contract. */
export interface TimelineRenderer {
  readonly backend: RendererBackend;
  /** The GPU (or rasteriser) behind the context, as far as the browser tells. */
  readonly description: string;
  /** Sizes the backing store: `width` × `height` CSS pixels at `dpr` device pixels per CSS pixel. */
  resize(width: number, height: number, dpr: number): void;
  draw(batch: RectBatch): void;
  /** Blocks until everything drawn so far has been rasterised (benchmarks only: it stalls the pipeline). */
  finish(): void;
  dispose(): void;
}

/** Why a backend could not start. */
export class RendererUnavailableError extends Error {
  constructor(
    readonly backend: RendererBackend,
    message: string
  ) {
    super(message);
    this.name = 'RendererUnavailableError';
  }
}
