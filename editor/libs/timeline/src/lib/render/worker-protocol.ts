import { type TimelineScene, type TimelineView } from '../items';
import { type RendererBackend } from './renderer';

/**
 * When the worker draws a requested frame: as soon as the request arrives (`message`), or at the worker's own next
 * `requestAnimationFrame` (`animation-frame`; falls back to `message` where workers have no animation frames).
 */
export type FramePacing = 'message' | 'animation-frame';

/** Main thread → render worker. Requests with an `id` get a response with the same `id`. */
export type WorkerRequest =
  | {
      readonly type: 'init';
      readonly id: number;
      readonly canvas: OffscreenCanvas;
      readonly backend: RendererBackend | 'auto';
      readonly width: number;
      readonly height: number;
      readonly dpr: number;
      readonly pacing: FramePacing;
    }
  | { readonly type: 'scene'; readonly id: number; readonly scene: TimelineScene }
  | { readonly type: 'resize'; readonly width: number; readonly height: number; readonly dpr: number }
  /** Draw `view` now. `sentAt` (epoch milliseconds) lets the worker record input-to-draw latency. */
  | { readonly type: 'frame'; readonly view: TimelineView; readonly sentAt: number }
  /** Return and clear the frame records collected since the last `collect`. */
  | { readonly type: 'collect'; readonly id: number }
  /** Draw each view back to back, waiting for the GPU after each, and return what each took (bench only). */
  | {
      readonly type: 'measure';
      readonly id: number;
      readonly views: readonly TimelineView[];
      /** Stop after this long, returning the frames measured so far. */
      readonly budgetMs: number;
    };

/** Per-frame records, one entry per drawn frame. Times in milliseconds; `drawnAt` is epoch milliseconds. */
export interface FrameRecords {
  readonly drawnAt: Float64Array;
  readonly latencyMs: Float64Array;
  readonly buildMs: Float64Array;
  readonly drawMs: Float64Array;
  readonly visibleItems: Uint32Array;
}

/** Render worker → main thread. */
export type WorkerResponse =
  | {
      readonly type: 'ready';
      readonly id: number;
      readonly backend: RendererBackend;
      readonly description: string;
      /** Whether the worker scope has `requestAnimationFrame` (it can pace itself to the display). */
      readonly workerAnimationFrame: boolean;
    }
  /** A `frame` was drawn; the client may send the next one. */
  | { readonly type: 'drawn'; readonly id: 0 }
  | { readonly type: 'scene-ready'; readonly id: number; readonly indexMs: number; readonly indexed: number }
  | { readonly type: 'frames'; readonly id: number; readonly records: FrameRecords }
  | { readonly type: 'measured'; readonly id: number; readonly costMs: Float64Array }
  | { readonly type: 'error'; readonly id: number; readonly message: string };
