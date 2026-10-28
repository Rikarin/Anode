import { RULER_HEIGHT } from './frame-builder';
import { gridStep } from './grid';
import { ItemFlag, type PixelRect, type TimelineScene, type TimelineView } from './items';
import { type LaneIntervalIndex } from './lane-interval-index';

/** What part of a region the pointer is over, which says what a drag there does (Logic's pointer tool). */
export type RegionZone = 'body' | 'trim-start' | 'trim-end' | 'fade-in' | 'fade-out';

export interface RegionHit {
  /** The item's index in the scene. */
  readonly index: number;
  readonly zone: RegionZone;
}

/** How close to a region's edge (CSS pixels) the pointer trims (lower half) or fades (upper half) instead of moving. */
export const EDGE_PX = 6;
/** How close to a fade's end (CSS pixels) the pointer grabs its handle. */
export const FADE_HANDLE_PX = 4;

/** The tick at x (CSS pixels from the canvas's left edge). */
export function tickAtX(view: TimelineView, x: number): number {
  return view.originTick + x / view.pixelsPerTick;
}

/** The x of a tick. */
export function xAtTick(view: TimelineView, tick: number): number {
  return (tick - view.originTick) * view.pixelsPerTick;
}

/** The lane at y (CSS pixels from the canvas's top edge), fractional; negative over the ruler. */
export function laneAtY(view: TimelineView, y: number): number {
  const top = view.lanesTop ?? RULER_HEIGHT;
  return y < top ? -1 : view.firstLane + (y - top) / view.laneHeight;
}

/** The y of a lane's top edge. */
export function yAtLane(view: TimelineView, lane: number): number {
  return (view.lanesTop ?? RULER_HEIGHT) + (lane - view.firstLane) * view.laneHeight;
}

/**
 * The region under (x, y) and the zone the pointer is in, or null. Where regions overlap, the one drawn last (the
 * latest start) wins, as it is on top. A region's edges trim in its lower half and fade in its upper half; an existing
 * fade's end is a handle in the upper half too.
 */
export function hitTest(
  scene: TimelineScene,
  index: LaneIntervalIndex,
  view: TimelineView,
  x: number,
  y: number
): RegionHit | null {
  const lanePosition = laneAtY(view, y);
  const lane = Math.floor(lanePosition);
  if (lane < 0 || lane >= scene.laneCount) {
    return null;
  }

  const ppt = view.pixelsPerTick;
  const tick = tickAtX(view, x);
  const tolerance = EDGE_PX / ppt;
  const found = new Uint32Array(Math.max(1, index.size));
  const count = index.query(tick - tolerance, tick + tolerance, lane, lane + 1, found);
  const upper = lanePosition - lane < 0.5;
  const { items, regions } = scene;
  let best: RegionHit | null = null;
  for (let k = 0; k < count; k++) {
    const id = found[k] ?? 0;
    const start = items.start[id] ?? 0;
    const xStart = xAtTick(view, start);
    const xEnd = xAtTick(view, start + (items.length[id] ?? 0));
    if (x < xStart - EDGE_PX || x > xEnd + EDGE_PX) {
      continue;
    }

    const flag = regions?.flags[id] ?? 0;
    if (flag & ItemFlag.Inner) {
      continue; // a part of the item under it: the item is what the pointer takes
    }
    if (flag & ItemFlag.Folder) {
      if (x > xStart && x < xEnd) {
        best = { index: id, zone: 'body' };
      }
      continue;
    }
    const edge = Math.min(EDGE_PX, (xEnd - xStart) / 4);
    const fades = regions !== undefined && !(flag & ItemFlag.Midi);
    let zone: RegionZone | null = null;
    if (fades && upper) {
      const own = regions.rate[id] ?? 0;
      const rate = own > 0 ? own : regions.framesPerTick;
      const fadeIn = ((regions.fadeIn[id] ?? 0) / rate) * ppt;
      const fadeOut = ((regions.fadeOut[id] ?? 0) / rate) * ppt;
      if (fadeIn > 0 && Math.abs(x - (xStart + fadeIn)) <= FADE_HANDLE_PX) {
        zone = 'fade-in';
      } else if (fadeOut > 0 && Math.abs(x - (xEnd - fadeOut)) <= FADE_HANDLE_PX) {
        zone = 'fade-out';
      }
    }
    if (zone === null) {
      if (Math.abs(x - xStart) <= edge) {
        zone = upper && fades ? 'fade-in' : 'trim-start';
      } else if (Math.abs(xEnd - x) <= edge) {
        zone = upper && fades ? 'fade-out' : 'trim-end';
      } else if (x > xStart && x < xEnd) {
        zone = 'body';
      }
    }
    if (zone !== null) {
      best = { index: id, zone };
    }
  }
  return best;
}

/** The items a marquee touches: those on lanes it covers whose span overlaps its ticks. */
export function itemsInRect(
  scene: TimelineScene,
  index: LaneIntervalIndex,
  view: TimelineView,
  rect: PixelRect
): number[] {
  const from = tickAtX(view, rect.x);
  const to = tickAtX(view, rect.x + rect.width);
  const laneFrom = Math.floor(Math.max(0, laneAtY(view, Math.max(rect.y, view.lanesTop ?? RULER_HEIGHT))));
  const laneTo = Math.min(scene.laneCount, Math.floor(laneAtY(view, rect.y + rect.height)) + 1);
  if (laneTo <= laneFrom || to <= from) {
    return [];
  }
  const found = new Uint32Array(Math.max(1, index.size));
  const count = index.query(from, to, laneFrom, laneTo, found);
  return Array.from(found.subarray(0, count));
}

/**
 * The snap step at this zoom (Logic's "Smart" snap): the finest grid whose lines are at least `minSpacingPx` apart,
 * so snapping follows what the grid shows.
 */
export function snapStep(ppq: number, beatsPerBar: number, pixelsPerTick: number, minSpacingPx = 12): number {
  return gridStep(ppq, beatsPerBar, pixelsPerTick, minSpacingPx);
}

/** `tick` rounded to the nearest multiple of `step` (no snapping for a step of 0 or less), never below 0. */
export function snapTick(tick: number, step: number): number {
  return Math.max(0, step > 0 ? Math.round(tick / step) * step : Math.round(tick));
}
