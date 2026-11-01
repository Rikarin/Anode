import type { NotationRenderer, RenderedPage, RenderOptions } from './renderer';
import type { WorkerReply, WorkerRequest } from './worker-protocol';

/** The part of a `Worker` the renderer uses (a fake one in tests). */
export interface WorkerLike {
  postMessage(message: WorkerRequest): void;
  addEventListener(type: 'message', listener: (event: MessageEvent<WorkerReply>) => void): void;
  addEventListener(type: 'error', listener: (event: ErrorEvent) => void): void;
  terminate(): void;
}

/** A render that a newer one replaced before it started: the caller drops it (the newer one answers). */
export class RenderSuperseded extends Error {
  constructor() {
    super('A newer render replaced this one.');
    this.name = 'RenderSuperseded';
  }
}

/** The last render's timing, for the Score Editor's diagnostics and the spike measurements. */
export interface RenderTiming {
  path: 'cached' | 'layout' | 'load';
  /** In Verovio. */
  engineMs: number;
  /** In the worker, including the first load of Verovio. */
  workerMs: number;
  /** From the request to the answer on the main thread. */
  totalMs: number;
}

interface Pending {
  id: number;
  mei: string;
  options: RenderOptions;
  resolve: (pages: RenderedPage[]) => void;
  reject: (error: Error) => void;
  started: number;
}

/** Where Verovio's files are served: `verovio/` beside the editor's `index.html` (the build's assets). */
export function defaultVerovioBase(): string {
  return new URL('verovio/', document.baseURI).href;
}

function createWorker(): WorkerLike {
  return new Worker(new URL('./verovio.worker', import.meta.url), { type: 'module', name: 'notation' });
}

/**
 * The Score Editor's renderer (ADR-0011): Verovio in a Web Worker, started on the first render, which loads Verovio's
 * files then. One render runs at a time; while it runs, a newer request replaces any that waits (the waiting one is
 * rejected with {@link RenderSuperseded}), so typing or dragging never queues stale engravings.
 */
export class VerovioNotationRenderer implements NotationRenderer {
  private worker: WorkerLike | null = null;
  private nextId = 1;
  private running: Pending | null = null;
  private waiting: Pending | null = null;
  /** How the last finished render went. */
  lastTiming: RenderTiming | null = null;

  constructor(private readonly options: { baseUrl?: () => string; createWorker?: () => WorkerLike } = {}) {}

  render(mei: string, options: RenderOptions): Promise<RenderedPage[]> {
    return new Promise<RenderedPage[]>((resolve, reject) => {
      const request: Pending = { id: this.nextId++, mei, options, resolve, reject, started: performance.now() };
      if (this.running) {
        this.waiting?.reject(new RenderSuperseded());
        this.waiting = request;
        return;
      }
      this.start(request);
    });
  }

  dispose(): void {
    this.worker?.terminate();
    this.worker = null;
    const stopped = new Error('The notation renderer was stopped.');
    this.running?.reject(stopped);
    this.waiting?.reject(stopped);
    this.running = null;
    this.waiting = null;
  }

  private start(request: Pending): void {
    this.running = request;
    this.ensureWorker().postMessage({ type: 'render', id: request.id, mei: request.mei, options: request.options });
  }

  private ensureWorker(): WorkerLike {
    if (this.worker) {
      return this.worker;
    }
    const worker = (this.options.createWorker ?? createWorker)();
    worker.addEventListener('message', event => this.answer(event.data));
    worker.addEventListener('error', event => this.fail(new Error(event.message || 'The notation worker failed.')));
    worker.postMessage({ type: 'init', baseUrl: (this.options.baseUrl ?? defaultVerovioBase)() });
    this.worker = worker;
    return worker;
  }

  private answer(reply: WorkerReply): void {
    const request = this.running;
    if (request?.id !== reply.id) {
      return;
    }
    if (reply.type === 'rendered') {
      this.lastTiming = {
        path: reply.path,
        engineMs: reply.ms,
        workerMs: reply.totalMs,
        totalMs: performance.now() - request.started
      };
      request.resolve(reply.pages);
    } else {
      request.reject(new Error(reply.message));
    }
    this.next();
  }

  private fail(error: Error): void {
    this.running?.reject(error);
    this.worker?.terminate();
    this.worker = null;
    this.next();
  }

  private next(): void {
    this.running = null;
    const waiting = this.waiting;
    this.waiting = null;
    if (waiting) {
      this.start(waiting);
    }
  }
}
