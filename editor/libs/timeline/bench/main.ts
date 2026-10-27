// The S1 rendering bench: draws a synthetic scene through @anode/timeline while a scripted camera scrolls, zooms and
// moves the playhead, and records frame timings. Parameters come from the query string:
//   ?scene=notes|regions  &backend=webgl2|canvas2d  &thread=worker|main  &duration=10000 (ms)  &warmup=1000 (ms)
//   &pacing=animation-frame|message (worker only: draw on the worker's own requestAnimationFrame, or on message)
// The result lands in `window.benchResult` (read by run-bench.mts) and on the page.
import {
  createRenderer,
  droppedFrames,
  type FramePacing,
  intervals,
  type RendererBackend,
  summarize,
  TimelineCanvas,
  type TimelineView,
  TimelineWorkerClient
} from '../src';
import { type BenchSceneKind, generateScene, viewAt } from './scenes';

const WIDTH = 1600;
const HEIGHT = 900;
/** A 60 Hz display refresh: the budget. */
const BUDGET_MS = 1000 / 60;
const MEASURE_FRAMES = 240;

type Thread = 'worker' | 'main';

declare global {
  interface Window {
    benchResult?: unknown;
  }
}

const params = new URLSearchParams(location.search);
const kind = (params.get('scene') ?? 'notes') as BenchSceneKind;
const backend = (params.get('backend') ?? 'webgl2') as RendererBackend;
const thread = (params.get('thread') ?? 'worker') as Thread;
const pacing = (params.get('pacing') ?? 'animation-frame') as FramePacing;
const duration = Number(params.get('duration') ?? 10_000);
const warmup = Number(params.get('warmup') ?? 1000);

const status = document.getElementById('status') as HTMLElement;
const progress = document.getElementById('progress') as HTMLElement;
const canvas = document.getElementById('timeline') as HTMLCanvasElement;
canvas.style.width = `${WIDTH}px`;
canvas.style.height = `${HEIGHT}px`;

function nextFrame(): Promise<number> {
  return new Promise(resolve => requestAnimationFrame(resolve));
}

/** The display's refresh period: the median of 90 idle animation frames. */
async function refreshPeriod(): Promise<number> {
  const stamps: number[] = [];
  for (let i = 0; i < 91; i++) {
    stamps.push(await nextFrame());
  }
  return summarize(intervals(stamps)).p50;
}

/** A surface on this thread, or a worker's; same calls either way. */
interface Driver {
  readonly backend: RendererBackend;
  readonly description: string;
  readonly workerAnimationFrame: boolean | null;
  readonly indexMs: number;
  /** Views the worker was too busy to draw (replaced by a newer one); 0 on the main thread. */
  skipped(): number;
  frame(view: TimelineView): void;
  /** Per-frame records: when drawn (epoch ms), CPU costs, visible items, latency (worker only). */
  collect(): Promise<{
    drawnAt: Float64Array;
    buildMs: Float64Array;
    drawMs: Float64Array;
    visibleItems: Uint32Array;
    latencyMs: Float64Array | null;
  }>;
  measure(views: TimelineView[]): Promise<Float64Array>;
}

async function workerDriver(): Promise<Driver> {
  if (!TimelineWorkerClient.supported(canvas)) {
    throw new Error('transferControlToOffscreen is not supported here.');
  }
  const client = new TimelineWorkerClient();
  const ready = await client.init(canvas, backend, WIDTH, HEIGHT, devicePixelRatio, pacing);
  const indexMs = await client.setScene(generateScene(kind));
  return {
    ...ready,
    indexMs,
    skipped: () => client.skippedFrames,
    frame: view => client.frame(view),
    collect: () => client.collect(),
    measure: views => client.measure(views)
  };
}

function mainDriver(): Driver {
  const renderer = createRenderer(canvas, backend);
  const surface = new TimelineCanvas(renderer);
  surface.resize(WIDTH, HEIGHT, devicePixelRatio);
  const indexMs = surface.setScene(generateScene(kind));
  const records = { drawnAt: [] as number[], buildMs: [] as number[], drawMs: [] as number[], visible: [] as number[] };
  return {
    backend: renderer.backend,
    description: renderer.description,
    workerAnimationFrame: null,
    indexMs,
    skipped: () => 0,
    frame: view => {
      const cost = surface.render(view);
      records.drawnAt.push(performance.timeOrigin + performance.now());
      records.buildMs.push(cost.buildMs);
      records.drawMs.push(cost.drawMs);
      records.visible.push(cost.visibleItems);
    },
    collect: () => {
      const result = {
        drawnAt: Float64Array.from(records.drawnAt),
        buildMs: Float64Array.from(records.buildMs),
        drawMs: Float64Array.from(records.drawMs),
        visibleItems: Uint32Array.from(records.visible),
        latencyMs: null
      };
      records.drawnAt.length = records.buildMs.length = records.drawMs.length = records.visible.length = 0;
      return Promise.resolve(result);
    },
    measure: views => {
      const cost: number[] = [];
      const begun = performance.now();
      for (const view of views) {
        const started = performance.now();
        surface.render(view);
        renderer.finish();
        cost.push(performance.now() - started);
        if (performance.now() - begun > 8000) {
          break;
        }
      }
      return Promise.resolve(Float64Array.from(cost));
    }
  };
}

/** Drives the camera for `ms`, one frame per main-thread animation frame; returns the animation frame timestamps. */
async function animate(driver: Driver, from: number, ms: number): Promise<number[]> {
  const stamps: number[] = [];
  const started = await nextFrame();
  for (let now = started; now - started < ms; now = await nextFrame()) {
    stamps.push(now);
    if (stamps.length % 60 === 0) {
      progress.textContent = `${stamps.length} frames, ${Math.round(now - started)} ms`;
    }
    driver.frame(viewAt(kind, from + now - started, WIDTH, HEIGHT));
  }
  return stamps;
}

function round(value: number): number {
  return Math.round(value * 1000) / 1000;
}

function rounded<T extends object>(summary: T): T {
  return Object.fromEntries(Object.entries(summary).map(([k, v]) => [k, typeof v === 'number' ? round(v) : v])) as T;
}

async function run(): Promise<unknown> {
  const refreshMs = await refreshPeriod();
  const generated = performance.now();
  const driver = thread === 'worker' ? await workerDriver() : mainDriver();
  const setupMs = performance.now() - generated;

  status.textContent = `Warming up (${driver.backend}, ${thread})…`;
  await animate(driver, 0, warmup);
  await driver.collect();
  const skippedBefore = driver.skipped();

  status.textContent = `Measuring ${kind} for ${duration / 1000} s (${driver.backend}, ${thread})…`;
  const mainStamps = await animate(driver, warmup, duration);
  status.textContent = 'Collecting…';
  const records = await driver.collect();
  status.textContent = `Measuring GPU-inclusive cost over ${MEASURE_FRAMES} frames…`;
  const skippedViews = driver.skipped() - skippedBefore;

  // Cost with the GPU included: each frame waited on (readPixels / getImageData), back to back.
  const views = Array.from({ length: MEASURE_FRAMES }, (_, i) =>
    viewAt(kind, warmup + (i * duration) / MEASURE_FRAMES, WIDTH, HEIGHT)
  );
  const syncCost = await driver.measure(views);

  const drawnIntervals = intervals(records.drawnAt);
  const cpu = records.buildMs.map((build, i) => build + (records.drawMs[i] ?? 0));
  let visibleMin = Infinity;
  let visibleMax = 0;
  let visibleSum = 0;
  for (const visible of records.visibleItems) {
    visibleMin = Math.min(visibleMin, visible);
    visibleMax = Math.max(visibleMax, visible);
    visibleSum += visible;
  }
  const dropped = droppedFrames(drawnIntervals, refreshMs);
  const expectedFrames = Math.round(duration / refreshMs);
  return {
    ok: true,
    scene: kind,
    thread,
    pacing: thread === 'worker' ? pacing : null,
    backendRequested: backend,
    backend: driver.backend,
    gpu: driver.description,
    workerAnimationFrame: driver.workerAnimationFrame,
    userAgent: navigator.userAgent,
    dpr: devicePixelRatio,
    canvas: { width: WIDTH, height: HEIGHT, deviceWidth: canvas.width || WIDTH * devicePixelRatio },
    refreshMs: round(refreshMs),
    setupMs: round(setupMs),
    indexMs: round(driver.indexMs),
    durationMs: duration,
    frames: records.drawnAt.length,
    expectedFrames,
    fps: round((records.drawnAt.length / duration) * 1000),
    visibleItems: {
      min: visibleMin,
      mean: Math.round(visibleSum / Math.max(1, records.visibleItems.length)),
      max: visibleMax
    },
    droppedFrames: dropped,
    skippedViews,
    droppedPct: round((100 * dropped) / Math.max(1, expectedFrames)),
    /** Frames presented more than 1.5 × 16.7 ms after the previous one: a missed 60 Hz deadline. */
    over60HzBudget: drawnIntervals.filter(dt => dt > 1.5 * BUDGET_MS).length,
    frameIntervalMs: rounded(summarize(drawnIntervals)),
    mainThreadIntervalMs: rounded(summarize(intervals(mainStamps))),
    cpuMs: rounded(summarize(cpu)),
    buildMs: rounded(summarize(records.buildMs)),
    drawMs: rounded(summarize(records.drawMs)),
    latencyMs: records.latencyMs ? rounded(summarize(records.latencyMs)) : null,
    syncFrameCostMs: rounded(summarize(syncCost))
  };
}

run().then(
  result => {
    window.benchResult = result;
    status.textContent = JSON.stringify(result, null, 2);
  },
  (error: unknown) => {
    const message = error instanceof Error ? error.message : String(error);
    window.benchResult = { ok: false, scene: kind, thread, backendRequested: backend, error: message };
    status.textContent = `Failed: ${message}`;
  }
);
