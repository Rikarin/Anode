// The render worker: owns a transferred OffscreenCanvas and draws a TimelineCanvas into it, fed with the scene's typed
// arrays (transferred once) and a small view message per frame. Started by createTimelineWorker().
import { type TimelineView } from '../items';
import { type TimelineRenderer } from './renderer';
import { createRenderer, TimelineCanvas } from './timeline-canvas';
import { type FramePacing, type FrameRecords, type WorkerRequest, type WorkerResponse } from './worker-protocol';

/** The slice of DedicatedWorkerGlobalScope this file uses (the library compiles against the DOM lib only). */
interface WorkerScope {
  onmessage: ((event: MessageEvent<WorkerRequest>) => void) | null;
  postMessage(message: WorkerResponse, transfer?: Transferable[]): void;
  requestAnimationFrame?: (callback: (time: number) => void) => number;
}

const scope = self as unknown as WorkerScope;
let surface: TimelineCanvas | null = null;
let renderer: TimelineRenderer | null = null;
let pacing: FramePacing = 'message';
/** The latest view waiting for the worker's next animation frame (`animation-frame` pacing). */
let pending: { view: TimelineView; sentAt: number } | null = null;

const records = {
  drawnAt: [] as number[],
  latencyMs: [] as number[],
  buildMs: [] as number[],
  drawMs: [] as number[],
  visibleItems: [] as number[]
};

function epochNow(): number {
  return performance.timeOrigin + performance.now();
}

function frame(view: TimelineView, sentAt: number): void {
  if (!surface) {
    return;
  }
  const cost = surface.render(view);
  const now = epochNow();
  records.drawnAt.push(now);
  records.latencyMs.push(now - sentAt);
  records.buildMs.push(cost.buildMs);
  records.drawMs.push(cost.drawMs);
  records.visibleItems.push(cost.visibleItems);
}

/** Draws a requested view: now (`message` pacing), or at the worker's next animation frame, latest view wins. */
function requestFrame(view: TimelineView, sentAt: number): void {
  const animationFrame = scope.requestAnimationFrame;
  if (pacing === 'message' || !animationFrame) {
    frame(view, sentAt);
    scope.postMessage({ type: 'drawn', id: 0 });
    return;
  }
  const scheduled = pending !== null;
  pending = { view, sentAt };
  if (!scheduled) {
    animationFrame(() => {
      if (pending) {
        frame(pending.view, pending.sentAt);
        pending = null;
      }
      scope.postMessage({ type: 'drawn', id: 0 });
    });
  }
}

function collect(): FrameRecords {
  const result: FrameRecords = {
    drawnAt: Float64Array.from(records.drawnAt),
    latencyMs: Float64Array.from(records.latencyMs),
    buildMs: Float64Array.from(records.buildMs),
    drawMs: Float64Array.from(records.drawMs),
    visibleItems: Uint32Array.from(records.visibleItems)
  };
  for (const column of Object.values(records)) {
    column.length = 0;
  }
  return result;
}

/** Frames drawn and waited on back to back, stopping early after `budgetMs` (a software rasteriser can be slow). */
function measure(views: readonly TimelineView[], budgetMs: number): Float64Array {
  const cost = new Float64Array(views.length);
  if (!surface || !renderer) {
    return cost;
  }
  const begun = performance.now();
  let n = 0;
  for (const view of views) {
    const started = performance.now();
    surface.render(view);
    renderer.finish();
    cost[n++] = performance.now() - started;
    if (performance.now() - begun > budgetMs) {
      break;
    }
  }
  return cost.slice(0, n);
}

scope.onmessage = ({ data: request }) => {
  try {
    switch (request.type) {
      case 'init': {
        renderer = createRenderer(request.canvas, request.backend);
        pacing = request.pacing;
        surface = new TimelineCanvas(renderer);
        surface.resize(request.width, request.height, request.dpr);
        scope.postMessage({
          type: 'ready',
          id: request.id,
          backend: renderer.backend,
          description: renderer.description,
          workerAnimationFrame: typeof scope.requestAnimationFrame === 'function'
        });
        break;
      }
      case 'scene': {
        const indexMs = surface?.setScene(request.scene) ?? 0;
        scope.postMessage({ type: 'scene-ready', id: request.id, indexMs, indexed: request.scene.items.count });
        break;
      }
      case 'resize':
        surface?.resize(request.width, request.height, request.dpr);
        break;
      case 'frame':
        requestFrame(request.view, request.sentAt);
        break;
      case 'collect': {
        const result = collect();
        scope.postMessage({ type: 'frames', id: request.id, records: result }, [
          result.drawnAt.buffer,
          result.latencyMs.buffer,
          result.buildMs.buffer,
          result.drawMs.buffer,
          result.visibleItems.buffer
        ]);
        break;
      }
      case 'measure': {
        const costMs = measure(request.views, request.budgetMs);
        scope.postMessage({ type: 'measured', id: request.id, costMs }, [costMs.buffer]);
        break;
      }
    }
  } catch (error) {
    const id = 'id' in request ? request.id : -1;
    scope.postMessage({ type: 'error', id, message: error instanceof Error ? error.message : String(error) });
  }
};
