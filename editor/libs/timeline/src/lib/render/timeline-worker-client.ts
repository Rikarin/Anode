import { sceneBuffers, type TimelineScene, type TimelineView } from '../items';
import { type Waveform } from '../waveform';
import { type RendererBackend } from './renderer';
import { type FramePacing, type FrameRecords, type WorkerRequest, type WorkerResponse } from './worker-protocol';

/** Starts the render worker. Bundlers (Vite, Angular's esbuild builder) emit the worker from this `new URL` pattern. */
export function createTimelineWorker(): Worker {
  return new Worker(new URL('./timeline.worker.ts', import.meta.url), { type: 'module', name: 'timeline-render' });
}

type Pending = (response: WorkerResponse) => void;

/** Distributes `Omit` over a union, so each request variant keeps its own fields. */
type WithoutId<T> = T extends unknown ? Omit<T, 'id'> : never;

/**
 * The main thread's handle on a render worker drawing into `canvas` (control transferred to the worker on
 * {@link init}). Frames are fire-and-forget; everything else is a promise.
 */
export class TimelineWorkerClient {
  private nextId = 1;
  private readonly pending = new Map<number, Pending>();
  /** A frame is with the worker; newer views wait in {@link nextView}, latest wins. */
  private frameInFlight = false;
  private nextView: { view: TimelineView; requestedAt: number } | null = null;
  /** Views replaced before the worker could draw them. */
  skippedFrames = 0;

  constructor(private readonly worker: Worker = createTimelineWorker()) {
    worker.onmessage = ({ data }: MessageEvent<WorkerResponse>) => {
      if (data.type === 'drawn') {
        this.frameInFlight = false;
        const next = this.nextView;
        if (next) {
          this.nextView = null;
          this.frame(next.view, next.requestedAt);
        }
        return;
      }
      const resolve = this.pending.get(data.id);
      this.pending.delete(data.id);
      resolve?.(data);
    };
  }

  /** Whether this browser can hand a canvas to a worker at all (WebKit since Safari 16.4). */
  static supported(canvas: HTMLCanvasElement): boolean {
    return typeof canvas.transferControlToOffscreen === 'function' && typeof Worker === 'function';
  }

  async init(
    canvas: HTMLCanvasElement,
    backend: RendererBackend | 'auto',
    width: number,
    height: number,
    dpr: number,
    pacing: FramePacing = 'animation-frame'
  ): Promise<{ backend: RendererBackend; description: string; workerAnimationFrame: boolean }> {
    const offscreen = canvas.transferControlToOffscreen();
    const response = await this.request({ type: 'init', canvas: offscreen, backend, width, height, dpr, pacing }, [
      offscreen
    ]);
    if (response.type !== 'ready') {
      throw new Error(describe(response));
    }
    return response;
  }

  /** Sends the scene, transferring its columns: they are unusable on this thread afterwards. */
  async setScene(scene: TimelineScene): Promise<number> {
    const response = await this.request({ type: 'scene', scene }, sceneBuffers(scene));
    if (response.type !== 'scene-ready') {
      throw new Error(describe(response));
    }
    return response.indexMs;
  }

  resize(width: number, height: number, dpr: number): void {
    this.post({ type: 'resize', width, height, dpr });
  }

  /** Sends one resolution of a waveform (copied: the caller keeps its own), or forgets the key's with `null`. */
  setWaveform(key: number, waveform: Waveform | null): void {
    this.post({ type: 'waveform', key, waveform });
  }

  /**
   * Asks for `view` to be drawn. One frame is in flight at a time: while the worker is busy, a newer view replaces the
   * waiting one, so a slow frame costs a skipped frame rather than a growing queue (and growing latency).
   */
  frame(view: TimelineView, requestedAt = performance.timeOrigin + performance.now()): void {
    if (this.frameInFlight) {
      if (this.nextView) {
        this.skippedFrames++;
      }
      this.nextView = { view, requestedAt };
      return;
    }
    this.frameInFlight = true;
    this.post({ type: 'frame', view, sentAt: requestedAt });
  }

  async collect(): Promise<FrameRecords> {
    const response = await this.request({ type: 'collect' });
    if (response.type !== 'frames') {
      throw new Error(describe(response));
    }
    return response.records;
  }

  async measure(views: readonly TimelineView[], budgetMs = 8000): Promise<Float64Array> {
    const response = await this.request({ type: 'measure', views, budgetMs });
    if (response.type !== 'measured') {
      throw new Error(describe(response));
    }
    return response.costMs;
  }

  terminate(): void {
    this.worker.terminate();
  }

  private post(message: WorkerRequest, transfer: Transferable[] = []): void {
    this.worker.postMessage(message, transfer);
  }

  private request(message: WithoutId<Extract<WorkerRequest, { id: number }>>, transfer: Transferable[] = []) {
    const id = this.nextId++;
    return new Promise<WorkerResponse>(resolve => {
      this.pending.set(id, resolve);
      this.post({ ...message, id }, transfer);
    });
  }
}

function describe(response: WorkerResponse): string {
  return response.type === 'error' ? response.message : `Unexpected ${response.type} from the render worker.`;
}
