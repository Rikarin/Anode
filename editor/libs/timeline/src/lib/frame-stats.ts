/** A distribution summary of frame timings, in milliseconds. */
export interface Distribution {
  readonly count: number;
  readonly mean: number;
  readonly p50: number;
  readonly p95: number;
  readonly p99: number;
  readonly max: number;
}

/** The `p`-th percentile (0–100) of ascending `sorted`, by linear interpolation between closest ranks. */
export function percentile(sorted: ArrayLike<number>, p: number): number {
  if (sorted.length === 0) {
    return Number.NaN;
  }
  const rank = (Math.min(100, Math.max(0, p)) / 100) * (sorted.length - 1);
  const lo = Math.floor(rank);
  const hi = Math.ceil(rank);
  const a = sorted[lo] ?? Number.NaN;
  const b = sorted[hi] ?? Number.NaN;
  return a + (b - a) * (rank - lo);
}

export function summarize(values: ArrayLike<number>): Distribution {
  const sorted = Float64Array.from(values).sort();
  let sum = 0;
  for (const value of sorted) {
    sum += value;
  }
  return {
    count: sorted.length,
    mean: sorted.length ? sum / sorted.length : Number.NaN,
    p50: percentile(sorted, 50),
    p95: percentile(sorted, 95),
    p99: percentile(sorted, 99),
    max: sorted.length ? (sorted[sorted.length - 1] ?? Number.NaN) : Number.NaN
  };
}

/**
 * Display refreshes missed between consecutive presented frames: an interval of about two refresh periods means one
 * frame was dropped, three means two. Half a period of jitter is tolerated either way.
 */
export function droppedFrames(intervals: Iterable<number>, refreshMs: number): number {
  let dropped = 0;
  for (const interval of intervals) {
    dropped += Math.max(0, Math.round(interval / refreshMs) - 1);
  }
  return dropped;
}

/** Intervals between consecutive timestamps. */
export function intervals(timestamps: ArrayLike<number>): Float64Array {
  const out = new Float64Array(Math.max(0, timestamps.length - 1));
  for (let i = 1; i < timestamps.length; i++) {
    out[i - 1] = (timestamps[i] ?? 0) - (timestamps[i - 1] ?? 0);
  }
  return out;
}
