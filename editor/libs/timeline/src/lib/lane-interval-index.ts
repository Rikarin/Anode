import { type TimelineItems } from './items';

/**
 * Finds the items a viewport shows: those on lanes `[laneFrom, laneTo)` that overlap ticks `[from, to)`.
 *
 * Layout: items grouped by lane (compressed rows, like CSR), each lane sorted by start, with a running maximum of the
 * end tick alongside. A query per lane is two binary searches and a scan:
 * - `hi` = the first item starting at or after `to` (nothing from there on can overlap);
 * - `lo` = the first item whose running max end exceeds `from` (nothing before it can overlap);
 * - scan `[lo, hi)` keeping items whose own end exceeds `from`.
 *
 * The scan visits only overlapping items plus the short ones that start inside the window of a long earlier item, so
 * it is output-sensitive for timeline data (notes and regions rarely nest deeply). A per-lane interval tree would bound
 * that worst case too; the spike measured this simpler layout as fast enough (docs/spikes/S1-timeline-rendering.md).
 */
export class LaneIntervalIndex {
  /** `laneOffset[l]..laneOffset[l + 1]` is lane `l`'s slice of the arrays below. */
  private readonly laneOffset: Uint32Array;
  private readonly ids: Uint32Array;
  private readonly starts: Float64Array;
  private readonly ends: Float64Array;
  private readonly maxEnds: Float64Array;

  constructor(
    items: TimelineItems,
    /** Lanes in the scene; items on a lane at or past it are dropped. */
    readonly laneCount: number
  ) {
    const { count, start, length, lane } = items;

    // Bucket by lane (counting sort), then sort each lane's bucket by start.
    const laneOffset = new Uint32Array(laneCount + 1);
    for (let i = 0; i < count; i++) {
      const l = lane[i] ?? laneCount;
      if (l < laneCount) {
        laneOffset[l + 1] = (laneOffset[l + 1] ?? 0) + 1;
      }
    }
    for (let l = 0; l < laneCount; l++) {
      laneOffset[l + 1] = (laneOffset[l + 1] ?? 0) + (laneOffset[l] ?? 0);
    }
    const kept = laneOffset[laneCount] ?? 0;
    const ids = new Uint32Array(kept);
    const cursor = laneOffset.slice(0, laneCount);
    for (let i = 0; i < count; i++) {
      const l = lane[i] ?? laneCount;
      if (l < laneCount) {
        const at = cursor[l] ?? 0;
        ids[at] = i;
        cursor[l] = at + 1;
      }
    }
    const byStart = (a: number, b: number) => (start[a] ?? 0) - (start[b] ?? 0) || a - b;
    for (let l = 0; l < laneCount; l++) {
      ids.subarray(laneOffset[l], laneOffset[l + 1]).sort(byStart);
    }

    const starts = new Float64Array(kept);
    const ends = new Float64Array(kept);
    const maxEnds = new Float64Array(kept);
    for (let l = 0; l < laneCount; l++) {
      let maxEnd = -Infinity;
      for (let k = laneOffset[l] ?? 0, end = laneOffset[l + 1] ?? 0; k < end; k++) {
        const id = ids[k] ?? 0;
        const s = start[id] ?? 0;
        const e = s + (length[id] ?? 0);
        starts[k] = s;
        ends[k] = e;
        maxEnd = Math.max(maxEnd, e);
        maxEnds[k] = maxEnd;
      }
    }

    this.laneOffset = laneOffset;
    this.ids = ids;
    this.starts = starts;
    this.ends = ends;
    this.maxEnds = maxEnds;
  }

  /** Items indexed (those on a lane below {@link laneCount}). */
  get size(): number {
    return this.ids.length;
  }

  /**
   * Writes the ids of the items on lanes `[laneFrom, laneTo)` overlapping ticks `[from, to)` into `out` from `offset`,
   * lane by lane and by start within a lane, and returns how many it wrote. `out` needs room for every match: size it
   * to {@link size} once and reuse it.
   */
  query(from: number, to: number, laneFrom: number, laneTo: number, out: Uint32Array, offset = 0): number {
    const { laneOffset, ids, starts, ends, maxEnds } = this;
    const first = Math.max(0, Math.floor(laneFrom));
    const last = Math.min(this.laneCount, Math.ceil(laneTo));
    let n = offset;
    for (let l = first; l < last; l++) {
      const begin = laneOffset[l] ?? 0;
      const end = laneOffset[l + 1] ?? 0;
      const hi = lowerBound(starts, to, begin, end);
      const lo = upperBound(maxEnds, from, begin, hi);
      for (let k = lo; k < hi; k++) {
        if ((ends[k] ?? 0) > from) {
          out[n++] = ids[k] ?? 0;
        }
      }
    }
    return n - offset;
  }
}

/** The first index in `[begin, end)` whose value is `>= value` (`end` if none); `values` ascending. */
export function lowerBound(values: Float64Array, value: number, begin = 0, end = values.length): number {
  let lo = begin;
  let hi = end;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    if ((values[mid] ?? 0) < value) {
      lo = mid + 1;
    } else {
      hi = mid;
    }
  }
  return lo;
}

/** The first index in `[begin, end)` whose value is `> value` (`end` if none); `values` ascending. */
export function upperBound(values: Float64Array, value: number, begin = 0, end = values.length): number {
  let lo = begin;
  let hi = end;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    if ((values[mid] ?? 0) <= value) {
      lo = mid + 1;
    } else {
      hi = mid;
    }
  }
  return lo;
}
