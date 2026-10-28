/**
 * Track automation maths (docs/05-phase-4.md): a lane is points sorted by tick, each with a value in its target's own
 * unit and a curve −1…1 that shapes the segment to the next point. Pure functions for drawing, hit-testing and editing
 * lanes; the editor converts values to the lane's height with its target's taper.
 */

/** A point of an automation lane (the contract's `AutomationPoint`). */
export interface AutomationPoint {
  readonly tick: number;
  readonly value: number;
  readonly curve: number;
}

/** Ticks to CSS x, values to CSS y. */
export interface AutomationProjection {
  tickToX: (tick: number) => number;
  valueToY: (value: number) => number;
}

/**
 * A segment's shape: the fraction of the way from one value to the next at `t` (0…1) of the way in time, for curve
 * `c` (−1…1). It is the fade curve's shape (docs/04-phase-3.md), `t^(2^(−2c))`: 0 is linear, positive bends towards
 * the next value early (convex), negative late (concave).
 */
export function curveShape(t: number, c: number): number {
  const position = Math.min(1, Math.max(0, t));
  return position ** (2 ** (-2 * Math.min(1, Math.max(-1, c))));
}

/**
 * A lane's value at `tick`: the first point's value before it, the last's after it, and between two points the curve
 * of the first. A stepped lane (mute) holds each value until the next point. Undefined for a lane with no points.
 */
export function automationValueAt(
  points: readonly AutomationPoint[],
  tick: number,
  stepped = false
): number | undefined {
  const first = points[0];
  if (!first) {
    return undefined;
  }
  if (tick <= first.tick) {
    return tick < first.tick ? first.value : lastAt(points, first.tick).value;
  }
  const index = segmentAt(points, tick);
  const a = points[index] as AutomationPoint;
  const b = points[index + 1];
  if (!b) {
    return a.value;
  }
  if (stepped || b.tick <= a.tick) {
    return a.value;
  }
  return a.value + (b.value - a.value) * curveShape((tick - a.tick) / (b.tick - a.tick), a.curve);
}

/** The last point at exactly `tick` (a vertical step has two points at one tick). */
function lastAt(points: readonly AutomationPoint[], tick: number): AutomationPoint {
  let found = points[0] as AutomationPoint;
  for (const point of points) {
    if (point.tick > tick) {
      break;
    }
    found = point;
  }
  return found;
}

/** The index of the last point at or before `tick` (0 before the first). */
export function segmentAt(points: readonly AutomationPoint[], tick: number): number {
  let lo = 0;
  let hi = points.length - 1;
  while (lo < hi) {
    const mid = (lo + hi + 1) >> 1;
    if ((points[mid]?.tick ?? 0) <= tick) {
      lo = mid;
    } else {
      hi = mid - 1;
    }
  }
  return Math.max(0, lo);
}

/** The drawn line of a lane and the area under it (closed to `bottomY`), as SVG path data. */
export interface AutomationPaths {
  line: string;
  area: string;
}

/**
 * The lane's curve from `fromTick` to `toTick` as SVG paths: flat before the first point and after the last, straight
 * between points on linear segments, and sampled at most `maxStepPx` apart along curved ones. A stepped lane draws
 * steps. An empty lane draws nothing.
 */
export function automationPaths(
  points: readonly AutomationPoint[],
  projection: AutomationProjection,
  fromTick: number,
  toTick: number,
  bottomY: number,
  options: { maxStepPx?: number; stepped?: boolean } = {}
): AutomationPaths {
  const first = points[0];
  const last = points.at(-1);
  if (!first || !last) {
    return { line: '', area: '' };
  }
  const { tickToX, valueToY } = projection;
  const step = Math.max(0.5, options.maxStepPx ?? 3);
  const out: [number, number][] = [];
  const push = (x: number, y: number) => {
    const previous = out.at(-1);
    if (previous?.[0] !== x || previous[1] !== y) {
      out.push([x, y]);
    }
  };
  push(tickToX(Math.min(fromTick, first.tick)), valueToY(first.value));
  for (let i = 0; i < points.length; i++) {
    const a = points[i] as AutomationPoint;
    const b = points[i + 1];
    const ax = tickToX(a.tick);
    push(ax, valueToY(a.value));
    if (!b) {
      break;
    }
    const bx = tickToX(b.tick);
    // Off screen: one straight piece is enough.
    if (b.tick < fromTick || a.tick > toTick) {
      push(bx, valueToY(options.stepped ? a.value : b.value));
      continue;
    }
    if (options.stepped) {
      push(bx, valueToY(a.value));
      continue;
    }
    if (a.curve !== 0 && bx - ax > step) {
      const from = Math.max(ax, tickToX(fromTick));
      const to = Math.min(bx, tickToX(toTick));
      const n = Math.ceil((to - from) / step);
      for (let k = 0; k <= n; k++) {
        const x = from + ((to - from) * k) / Math.max(1, n);
        const t = (x - ax) / (bx - ax);
        push(x, valueToY(a.value + (b.value - a.value) * curveShape(t, a.curve)));
      }
    }
    push(bx, valueToY(b.value));
  }
  push(tickToX(Math.max(toTick, last.tick)), valueToY(last.value));
  const line = out.map(([x, y], i) => `${i === 0 ? 'M' : 'L'}${fmt(x)} ${fmt(y)}`).join(' ');
  const firstX = out[0]?.[0] ?? 0;
  const lastX = out.at(-1)?.[0] ?? 0;
  const area = `${line} L${fmt(lastX)} ${fmt(bottomY)} L${fmt(firstX)} ${fmt(bottomY)} Z`;
  return { line, area };
}

function fmt(n: number): string {
  return (Math.round(n * 100) / 100).toString();
}

/** The point nearest (x, y) within `radiusPx`, or −1. */
export function hitPoint(
  points: readonly AutomationPoint[],
  x: number,
  y: number,
  projection: AutomationProjection,
  radiusPx = 6
): number {
  let best = -1;
  let bestDistance = radiusPx * radiusPx;
  points.forEach((point, i) => {
    const dx = projection.tickToX(point.tick) - x;
    const dy = projection.valueToY(point.value) - y;
    const d = dx * dx + dy * dy;
    if (d <= bestDistance) {
      best = i;
      bestDistance = d;
    }
  });
  return best;
}

/**
 * The segment (the index of its first point) whose curve passes within `radiusPx` of (x, y) vertically, between its
 * two points; −1 when none does. ⌃⇧-dragging a segment bends it.
 */
export function hitSegment(
  points: readonly AutomationPoint[],
  x: number,
  y: number,
  projection: AutomationProjection,
  radiusPx = 6
): number {
  for (let i = 0; i < points.length - 1; i++) {
    const a = points[i] as AutomationPoint;
    const b = points[i + 1] as AutomationPoint;
    const ax = projection.tickToX(a.tick);
    const bx = projection.tickToX(b.tick);
    if (x <= ax || x >= bx) {
      continue;
    }
    const value = a.value + (b.value - a.value) * curveShape((x - ax) / (bx - ax), a.curve);
    if (Math.abs(projection.valueToY(value) - y) <= radiusPx) {
      return i;
    }
  }
  return -1;
}

function sorted(points: AutomationPoint[]): AutomationPoint[] {
  return points.sort((a, b) => a.tick - b.tick);
}

/** The lane with a point added (after any at the same tick), and its index. */
export function insertPoint(
  points: readonly AutomationPoint[],
  point: AutomationPoint
): { points: AutomationPoint[]; index: number } {
  let index = points.findIndex(p => p.tick > point.tick);
  if (index < 0) {
    index = points.length;
  }
  const next = [...points];
  next.splice(index, 0, { ...point, tick: Math.max(0, point.tick) });
  return { points: next, index };
}

/** The lane without the points at `indices`. */
export function removePoints(points: readonly AutomationPoint[], indices: Iterable<number>): AutomationPoint[] {
  const drop = new Set(indices);
  return points.filter((_, i) => !drop.has(i));
}

/**
 * The lane with the points at `indices` moved by `dTick` and their values mapped by `shift` (the editor drags in the
 * lane's normalised height and maps back to the unit). The move is clamped so the moved points stay between the
 * unmoved points around each of them and at or after tick 0; so the order, and the indices, stay the same.
 */
export function movePoints(
  points: readonly AutomationPoint[],
  indices: Iterable<number>,
  dTick: number,
  shift: (value: number) => number = v => v
): AutomationPoint[] {
  const moving = new Set(indices);
  let lo = -Infinity;
  let hi = Infinity;
  points.forEach((point, i) => {
    if (!moving.has(i)) {
      return;
    }
    let before = 0;
    for (let k = i - 1; k >= 0; k--) {
      if (!moving.has(k)) {
        before = points[k]?.tick ?? 0;
        break;
      }
    }
    let after = Infinity;
    for (let k = i + 1; k < points.length; k++) {
      if (!moving.has(k)) {
        after = points[k]?.tick ?? Infinity;
        break;
      }
    }
    lo = Math.max(lo, before - point.tick);
    hi = Math.min(hi, after - point.tick);
  });
  const d = Math.min(hi, Math.max(lo, dTick));
  return points.map((point, i) =>
    moving.has(i) ? { ...point, tick: Math.round(point.tick + d), value: shift(point.value) } : point
  );
}

/** The lane with segment `index`'s curve set (clamped to −1…1). */
export function setCurve(points: readonly AutomationPoint[], index: number, curve: number): AutomationPoint[] {
  return points.map((point, i) =>
    i === index ? { ...point, curve: Math.round(Math.min(1, Math.max(-1, curve)) * 1000) / 1000 } : point
  );
}

/** The indices of the points from `startTick` to `endTick` (inclusive). */
export function pointsInRange(points: readonly AutomationPoint[], startTick: number, endTick: number): number[] {
  const lo = Math.min(startTick, endTick);
  const hi = Math.max(startTick, endTick);
  return points.flatMap((point, i) => (point.tick >= lo && point.tick <= hi ? [i] : []));
}

/**
 * The lane with a time range deleted, as Logic deletes an automation selection: the points inside go, and points at
 * the range's edges keep the curve's values outside it (only where the lane goes on beyond that edge).
 */
export function deleteRange(
  points: readonly AutomationPoint[],
  startTick: number,
  endTick: number,
  stepped = false
): AutomationPoint[] {
  const lo = Math.max(0, Math.min(startTick, endTick));
  const hi = Math.max(startTick, endTick);
  const inside = pointsInRange(points, lo, hi);
  const first = points[0];
  const last = points.at(-1);
  if (!first || !last || (inside.length === 0 && (hi < first.tick || lo > last.tick))) {
    return [...points];
  }
  const startValue = automationValueAt(points, lo, stepped) ?? first.value;
  const endValue = automationValueAt(points, hi, stepped) ?? last.value;
  const endCurve = points[segmentAt(points, hi)]?.curve ?? 0;
  const kept = removePoints(points, inside);
  if (points.some(p => p.tick < lo)) {
    kept.push({ tick: lo, value: startValue, curve: 0 });
  }
  if (points.some(p => p.tick > hi)) {
    kept.push({ tick: hi, value: endValue, curve: endCurve });
  }
  return sorted(kept);
}

/**
 * Ramer–Douglas–Peucker: the points of a drawn line worth keeping, within `tolerance` of the line through the kept ones
 * (in whatever space `x` and `y` measure, CSS pixels for a pencil stroke). The ends are always kept.
 */
export function simplify<T>(points: readonly T[], x: (p: T) => number, y: (p: T) => number, tolerance: number): T[] {
  if (points.length <= 2) {
    return [...points];
  }
  const keep = new Array<boolean>(points.length).fill(false);
  keep[0] = true;
  keep[points.length - 1] = true;
  const stack: [number, number][] = [[0, points.length - 1]];
  while (stack.length > 0) {
    const [a, b] = stack.pop() as [number, number];
    const pa = points[a] as T;
    const pb = points[b] as T;
    const ax = x(pa);
    const ay = y(pa);
    const dx = x(pb) - ax;
    const dy = y(pb) - ay;
    const length = Math.hypot(dx, dy) || 1;
    let worst = -1;
    let worstDistance = tolerance;
    for (let i = a + 1; i < b; i++) {
      const p = points[i] as T;
      const distance = Math.abs(dy * (x(p) - ax) - dx * (y(p) - ay)) / length;
      if (distance > worstDistance) {
        worst = i;
        worstDistance = distance;
      }
    }
    if (worst >= 0) {
      keep[worst] = true;
      stack.push([a, worst], [worst, b]);
    }
  }
  return points.filter((_, i) => keep[i]);
}

/**
 * The lane with a drawn stroke (pencil) laid over it: points of the stroke replace those within its time span; the
 * stroke's points are sorted by tick.
 */
export function replaceSpan(points: readonly AutomationPoint[], stroke: readonly AutomationPoint[]): AutomationPoint[] {
  if (stroke.length === 0) {
    return [...points];
  }
  const ordered = sorted([...stroke]);
  const lo = ordered[0]?.tick ?? 0;
  const hi = ordered.at(-1)?.tick ?? lo;
  return sorted([...points.filter(p => p.tick < lo || p.tick > hi), ...ordered]);
}
