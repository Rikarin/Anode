import { FrameBuilder } from '../frame-builder';
import { type TimelineScene, type TimelineView } from '../items';
import { type Waveform, WaveformSet } from '../waveform';
import { Canvas2DRenderer } from './canvas2d-renderer';
import { type RendererBackend, type RenderTarget, type TimelineRenderer } from './renderer';
import { WebGL2Renderer } from './webgl2-renderer';

/** A renderer for `canvas`: `preferred`, or for `'auto'` WebGL2 falling back to Canvas2D. */
export function createRenderer(canvas: RenderTarget, preferred: RendererBackend | 'auto' = 'auto'): TimelineRenderer {
  if (preferred === 'canvas2d') {
    return new Canvas2DRenderer(canvas);
  }
  try {
    return new WebGL2Renderer(canvas);
  } catch (error) {
    if (preferred === 'webgl2') {
      throw error;
    }
    return new Canvas2DRenderer(canvas);
  }
}

/** What one frame cost on the CPU, and what it drew. */
export interface FrameCost {
  /** Culling and building the draw lists. */
  readonly buildMs: number;
  /** Issuing the draw (uploads and draw calls for WebGL2; rasterising, mostly, for Canvas2D). */
  readonly drawMs: number;
  readonly visibleItems: number;
  readonly rects: number;
}

/**
 * A timeline view's drawing surface: a scene, a {@link FrameBuilder} over it and a renderer. Runs the same on the main
 * thread and in the render worker (timeline.worker.ts).
 */
export class TimelineCanvas {
  private builder: FrameBuilder | null = null;
  private width = 1;
  private height = 1;
  /** Waveforms the scene's regions refer to by key; they outlive scene changes. */
  readonly waveforms = new WaveformSet();

  constructor(readonly renderer: TimelineRenderer) {}

  resize(width: number, height: number, dpr: number): void {
    this.width = width;
    this.height = height;
    this.renderer.resize(width, height, dpr);
  }

  /** Replaces the scene and indexes it; returns how long indexing took, in milliseconds. */
  setScene(scene: TimelineScene): number {
    const started = performance.now();
    this.builder = new FrameBuilder(scene, undefined, this.waveforms);
    return performance.now() - started;
  }

  /** Adds one resolution of a waveform (or replaces it), for regions whose waveform key is `key`. */
  setWaveform(key: number, waveform: Waveform): void {
    this.waveforms.add(key, waveform);
  }

  deleteWaveform(key: number): void {
    this.waveforms.delete(key);
  }

  render(view: TimelineView): FrameCost {
    const started = performance.now();
    if (!this.builder) {
      return { buildMs: 0, drawMs: 0, visibleItems: 0, rects: 0 };
    }
    const batch = this.builder.build(view, this.width, this.height);
    const built = performance.now();
    this.renderer.draw(batch);
    return {
      buildMs: built - started,
      drawMs: performance.now() - built,
      visibleItems: batch.visibleItems,
      rects: batch.count
    };
  }
}
