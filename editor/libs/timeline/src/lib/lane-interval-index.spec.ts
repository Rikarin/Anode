import { createItems, type TimelineItems } from './items';
import { LaneIntervalIndex, lowerBound, upperBound } from './lane-interval-index';

/** A deterministic PRNG (mulberry32), so a failing case reproduces. */
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

function items(spans: [start: number, length: number, lane: number][]): TimelineItems {
  const result = createItems(spans.length);
  spans.forEach(([start, length, lane], i) => {
    result.start[i] = start;
    result.length[i] = length;
    result.lane[i] = lane;
  });
  return result;
}

function query(index: LaneIntervalIndex, from: number, to: number, laneFrom: number, laneTo: number): number[] {
  const out = new Uint32Array(index.size);
  return Array.from(out.subarray(0, index.query(from, to, laneFrom, laneTo, out)));
}

describe('LaneIntervalIndex', () => {
  it('finds the items overlapping a half-open tick range on the queried lanes', () => {
    const index = new LaneIntervalIndex(
      items([
        [0, 10, 0], // 0: ends where the range starts: excluded
        [5, 20, 0], // 1: straddles the start
        [12, 2, 0], // 2: inside
        [30, 5, 0], // 3: starts where the range ends: excluded
        [15, 1, 1], // 4: other lane
        [0, 100, 2] // 5: spans the range on lane 2
      ]),
      3
    );
    expect(query(index, 10, 30, 0, 1)).toEqual([1, 2]);
    expect(query(index, 10, 30, 0, 3)).toEqual([1, 2, 4, 5]);
    expect(query(index, 10, 30, 1, 2)).toEqual([4]);
    expect(query(index, 200, 300, 0, 3)).toEqual([]);
  });

  it('keeps short items that follow a long one inside the window', () => {
    const index = new LaneIntervalIndex(
      items([
        [0, 1000, 0],
        [10, 5, 0],
        [500, 5, 0]
      ]),
      1
    );
    expect(query(index, 400, 600, 0, 1)).toEqual([0, 2]);
  });

  it('drops items on lanes past the lane count and clamps fractional lane ranges', () => {
    const index = new LaneIntervalIndex(
      items([
        [0, 10, 0],
        [0, 10, 1],
        [0, 10, 5]
      ]),
      2
    );
    expect(index.size).toBe(2);
    expect(query(index, 0, 10, -3.5, 0.2)).toEqual([0]);
    expect(query(index, 0, 10, 0.7, 99)).toEqual([0, 1]);
  });

  it('writes from an offset and returns only what it wrote', () => {
    const index = new LaneIntervalIndex(items([[0, 10, 0]]), 1);
    const out = new Uint32Array(3).fill(7);
    expect(index.query(0, 5, 0, 1, out, 2)).toBe(1);
    expect(Array.from(out)).toEqual([7, 7, 0]);
  });

  it('agrees with a brute-force scan on random data', () => {
    const next = random(42);
    const lanes = 16;
    const data = createItems(5000);
    for (let i = 0; i < data.count; i++) {
      data.start[i] = Math.floor(next() * 100_000);
      // Mostly short items, a few long ones (the case the running max end exists for).
      data.length[i] = next() < 0.02 ? Math.floor(next() * 20_000) : 1 + Math.floor(next() * 500);
      data.lane[i] = Math.floor(next() * lanes);
    }
    const index = new LaneIntervalIndex(data, lanes);

    for (let q = 0; q < 200; q++) {
      const from = Math.floor(next() * 100_000);
      const to = from + Math.floor(next() * 10_000);
      const laneFrom = Math.floor(next() * lanes);
      const laneTo = laneFrom + Math.ceil(next() * (lanes - laneFrom));
      const expected: number[] = [];
      for (let i = 0; i < data.count; i++) {
        const start = data.start[i] ?? 0;
        const lane = data.lane[i] ?? 0;
        if (lane >= laneFrom && lane < laneTo && start < to && start + (data.length[i] ?? 0) > from) {
          expected.push(i);
        }
      }
      expect(query(index, from, to, laneFrom, laneTo).sort((a, b) => a - b)).toEqual(expected);
    }
  });
});

describe('lowerBound and upperBound', () => {
  const values = new Float64Array([1, 2, 2, 2, 5]);

  it('bracket a run of equal values', () => {
    expect(lowerBound(values, 2)).toBe(1);
    expect(upperBound(values, 2)).toBe(4);
  });

  it('return the ends for values outside the range', () => {
    expect(lowerBound(values, 0)).toBe(0);
    expect(upperBound(values, 9)).toBe(5);
    expect(lowerBound(values, 9, 1, 3)).toBe(3);
  });
});
