// Synthetic scenes and the scripted camera for the S1 rendering bench (docs/spikes/S1-timeline-rendering.md).
import { createItems, packColor, RULER_HEIGHT, type TimelineScene, type TimelineView } from '../src';

export type BenchSceneKind = 'notes' | 'regions';

const PPQ = 960;
const BEATS_PER_BAR = 4;
const BAR = PPQ * BEATS_PER_BAR;

/**
 * Sizes chosen so the animated view never shows fewer items than the budget:
 * - notes: 128 pitches × 128 bars, 18 notes per pitch per bar (295k notes); the view shows 96 pitches × 12–16 bars,
 *   so 20.7k–27.6k notes;
 * - regions: 200 tracks × 256 bars, ~1.14 regions per track per bar (58k regions); the view shows 40 tracks × 48–64
 *   bars, so 2.2k–2.9k regions.
 */
interface SceneSpec {
  readonly laneCount: number;
  readonly bars: number;
  readonly visibleLanes: number;
  /** Bars across the canvas: `mean ± swing`, oscillating. */
  readonly barsMean: number;
  readonly barsSwing: number;
  /** Horizontal scroll speed. */
  readonly barsPerSecond: number;
}

export const specs: Record<BenchSceneKind, SceneSpec> = {
  notes: { laneCount: 128, bars: 128, visibleLanes: 96, barsMean: 14, barsSwing: 2, barsPerSecond: 10 },
  regions: { laneCount: 200, bars: 256, visibleLanes: 40, barsMean: 56, barsSwing: 8, barsPerSecond: 15 }
};

/** mulberry32: a seeded PRNG, so every browser draws the same scene. */
function random(seed: number): () => number {
  let state = seed >>> 0;
  return () => {
    state = (state + 0x6d2b79f5) >>> 0;
    let t = state;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/** Velocity colours (blue → red) for notes; Logic-like track colours for regions. */
const velocityColors = Array.from({ length: 16 }, (_, i) => {
  const t = i / 15;
  return packColor(
    Math.round(60 + 195 * t),
    Math.round(140 + 60 * (1 - Math.abs(2 * t - 1))),
    Math.round(230 - 190 * t)
  );
});
const trackColors = [
  '#4f8fe8',
  '#45b36b',
  '#e0a33a',
  '#d65a5a',
  '#9b6fe0',
  '#3fb8b8',
  '#e07ab8',
  '#8fa83a',
  '#5a6fd6',
  '#c48a4a',
  '#4ab88a',
  '#b85ad6'
].map(hex => {
  const rgb = parseInt(hex.slice(1), 16);
  return packColor(rgb >> 16, (rgb >> 8) & 0xff, rgb & 0xff);
});

export function generateScene(kind: BenchSceneKind): TimelineScene {
  const spec = specs[kind];
  const next = random(kind === 'notes' ? 1 : 2);
  if (kind === 'notes') {
    const perBar = 18;
    const items = createItems(spec.laneCount * spec.bars * perBar);
    let i = 0;
    for (let lane = 0; lane < spec.laneCount; lane++) {
      for (let bar = 0; bar < spec.bars; bar++) {
        for (let n = 0; n < perBar; n++, i++) {
          items.start[i] = Math.floor((bar + next()) * BAR);
          items.length[i] = Math.floor(BAR * (1 / 32 + next() * (1 / 8 - 1 / 32)));
          items.lane[i] = lane;
          items.color[i] = velocityColors[Math.floor(next() * velocityColors.length)] ?? 0;
        }
      }
    }
    return { kind, ppq: PPQ, beatsPerBar: BEATS_PER_BAR, laneCount: spec.laneCount, items };
  }

  // Regions: walk each track, 0.5–1 bar regions separated by 0–¼ bar gaps.
  const starts: number[] = [];
  const lengths: number[] = [];
  const lanes: number[] = [];
  for (let lane = 0; lane < spec.laneCount; lane++) {
    let tick = Math.floor(next() * BAR);
    while (tick < spec.bars * BAR) {
      const length = Math.floor(BAR * (0.5 + 0.5 * next()));
      starts.push(tick);
      lengths.push(length);
      lanes.push(lane);
      tick += length + Math.floor(BAR * 0.25 * next());
    }
  }
  const items = createItems(starts.length);
  items.start.set(starts);
  items.length.set(lengths);
  items.lane.set(lanes);
  for (let i = 0; i < items.count; i++) {
    items.color[i] = trackColors[(items.lane[i] ?? 0) % trackColors.length] ?? 0;
  }
  return { kind, ppq: PPQ, beatsPerBar: BEATS_PER_BAR, laneCount: spec.laneCount, items };
}

/**
 * The scripted camera at `t` ms: continuous horizontal scroll, a zoom oscillating ±(swing) bars every 4 s, vertical
 * scroll oscillating every 6 s, and a playhead sweeping the visible span every 2 s.
 */
export function viewAt(kind: BenchSceneKind, t: number, width: number, height: number): TimelineView {
  const spec = specs[kind];
  const seconds = t / 1000;
  const bars = spec.barsMean + spec.barsSwing * Math.sin((2 * Math.PI * seconds) / 4);
  const visibleTicks = bars * BAR;
  const scrollRange = (spec.bars - spec.barsMean - spec.barsSwing) * BAR;
  const originTick = (seconds * spec.barsPerSecond * BAR) % scrollRange;
  const laneHeight = (height - RULER_HEIGHT) / spec.visibleLanes;
  const maxFirstLane = spec.laneCount - spec.visibleLanes;
  return {
    originTick,
    pixelsPerTick: width / visibleTicks,
    firstLane: (maxFirstLane / 2) * (1 + Math.sin((2 * Math.PI * seconds) / 6)),
    laneHeight,
    playheadTick: originTick + visibleTicks * ((seconds / 2) % 1)
  };
}
