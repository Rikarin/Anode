import {
  type AutomationPoint,
  automationPaths,
  automationValueAt,
  curveShape,
  deleteRange,
  hitPoint,
  hitSegment,
  insertPoint,
  movePoints,
  pointsInRange,
  removePoints,
  replaceSpan,
  setCurve,
  simplify
} from './automation';

const p = (tick: number, value: number, curve = 0): AutomationPoint => ({ tick, value, curve });
// 1 px per tick; values 0…1 over 100 px, 0 at the bottom.
const projection = { tickToX: (t: number) => t, valueToY: (v: number) => 100 - v * 100 };

describe('curveShape', () => {
  it('is linear at 0 and keeps its ends at any curve', () => {
    expect(curveShape(0.25, 0)).toBeCloseTo(0.25);
    for (const c of [-1, -0.5, 0.5, 1]) {
      expect(curveShape(0, c)).toBe(0);
      expect(curveShape(1, c)).toBe(1);
    }
  });

  it('bends early for positive curves and late for negative ones, as fades do', () => {
    expect(curveShape(0.5, 1)).toBeCloseTo(Math.SQRT1_2 ** 0.5, 5); // 0.5^(1/4)
    expect(curveShape(0.5, 1)).toBeGreaterThan(0.5);
    expect(curveShape(0.5, -1)).toBeCloseTo(0.0625); // 0.5^4
  });

  it('clamps its arguments', () => {
    expect(curveShape(2, 0)).toBe(1);
    expect(curveShape(0.5, 5)).toBeCloseTo(curveShape(0.5, 1));
  });
});

describe('automationValueAt', () => {
  const lane = [p(100, 0), p(200, 1, 1), p(300, 0.5)];

  it('is undefined for an empty lane', () => {
    expect(automationValueAt([], 10)).toBeUndefined();
  });

  it('holds the first value before it and the last after it', () => {
    expect(automationValueAt(lane, 0)).toBe(0);
    expect(automationValueAt(lane, 1_000)).toBe(0.5);
  });

  it('interpolates linear and curved segments', () => {
    expect(automationValueAt(lane, 150)).toBeCloseTo(0.5);
    expect(automationValueAt(lane, 250)).toBeCloseTo(1 - 0.5 * curveShape(0.5, 1));
  });

  it('steps a stepped lane and takes the later point of a vertical step', () => {
    expect(automationValueAt(lane, 150, true)).toBe(0);
    const step = [p(0, 0), p(100, 0), p(100, 1), p(200, 1)];
    expect(automationValueAt(step, 100)).toBe(1);
    expect(automationValueAt(step, 99)).toBe(0);
  });
});

describe('automationPaths', () => {
  it('draws nothing for an empty lane', () => {
    expect(automationPaths([], projection, 0, 100, 100)).toEqual({ line: '', area: '' });
  });

  it('runs flat to the view edges and straight between linear points', () => {
    const { line, area } = automationPaths([p(50, 0.5), p(150, 1)], projection, 0, 400, 100);
    expect(line).toBe('M0 50 L50 50 L150 0 L400 0');
    expect(area).toBe('M0 50 L50 50 L150 0 L400 0 L400 100 L0 100 Z');
  });

  it('samples curved segments at most the step apart', () => {
    const { line } = automationPaths([p(0, 0, 1), p(100, 1)], projection, 0, 100, 100, { maxStepPx: 10 });
    const xs = line
      .split(/[ML]/)
      .filter(Boolean)
      .map(s => Number(s.trim().split(' ')[0]));
    expect(xs.length).toBeGreaterThanOrEqual(11);
    for (let i = 1; i < xs.length; i++) {
      expect((xs[i] ?? 0) - (xs[i - 1] ?? 0)).toBeLessThanOrEqual(10.001);
    }
    // the curve's middle is above the straight line
    expect(line).toContain(`L50 ${Math.round((100 - curveShape(0.5, 1) * 100) * 100) / 100}`);
  });

  it('draws steps for a stepped lane', () => {
    const { line } = automationPaths([p(0, 0), p(100, 1)], projection, 0, 200, 100, { stepped: true });
    expect(line).toBe('M0 100 L100 100 L100 0 L200 0');
  });
});

describe('hit-testing', () => {
  const lane = [p(0, 0), p(100, 1), p(200, 0)];

  it('finds the nearest point within the radius', () => {
    expect(hitPoint(lane, 103, 2, projection)).toBe(1);
    expect(hitPoint(lane, 110, 2, projection)).toBe(-1);
    expect(hitPoint(lane, 1, 99, projection, 4)).toBe(0);
  });

  it('finds the segment the pointer is on, between its points', () => {
    expect(hitSegment(lane, 50, 50, projection)).toBe(0);
    expect(hitSegment(lane, 150, 52, projection)).toBe(1);
    expect(hitSegment(lane, 50, 20, projection)).toBe(-1);
    expect(hitSegment(lane, 100, 0, projection)).toBe(-1); // on a point: not a segment
  });

  it('follows the curve of a bent segment', () => {
    const bent = [p(0, 0, -1), p(100, 1)];
    const y = projection.valueToY(curveShape(0.5, -1));
    expect(hitSegment(bent, 50, y, projection, 2)).toBe(0);
    expect(hitSegment(bent, 50, 50, projection, 2)).toBe(-1);
  });
});

describe('editing', () => {
  const lane = [p(0, 0), p(100, 0.5), p(200, 1), p(300, 0)];

  it('inserts a point in time order', () => {
    const { points, index } = insertPoint(lane, p(150, 0.2));
    expect(index).toBe(2);
    expect(points.map(q => q.tick)).toEqual([0, 100, 150, 200, 300]);
    expect(insertPoint([], p(-5, 1)).points[0]?.tick).toBe(0);
  });

  it('removes points', () => {
    expect(removePoints(lane, [1, 2]).map(q => q.tick)).toEqual([0, 300]);
  });

  it('moves selected points between their unmoved neighbours', () => {
    expect(movePoints(lane, [1], 50).map(q => q.tick)).toEqual([0, 150, 200, 300]);
    expect(movePoints(lane, [1], 500).map(q => q.tick)).toEqual([0, 200, 200, 300]);
    expect(movePoints(lane, [1, 2], 500).map(q => q.tick)).toEqual([0, 200, 300, 300]);
    expect(movePoints(lane, [0], -50).map(q => q.tick)).toEqual([0, 100, 200, 300]);
    expect(movePoints(lane, [3], 1_000).map(q => q.tick)).toEqual([0, 100, 200, 1_300]);
  });

  it('maps the moved values', () => {
    const moved = movePoints(lane, [1, 2], 0, v => Math.min(1, v + 0.25));
    expect(moved.map(q => q.value)).toEqual([0, 0.75, 1, 0]);
  });

  it('sets a clamped curve', () => {
    expect(setCurve(lane, 1, 3)[1]?.curve).toBe(1);
    expect(setCurve(lane, 1, -0.25)[1]?.curve).toBe(-0.25);
  });

  it('lists the points in a range either way round', () => {
    expect(pointsInRange(lane, 50, 200)).toEqual([1, 2]);
    expect(pointsInRange(lane, 200, 50)).toEqual([1, 2]);
  });
});

describe('deleteRange', () => {
  it('removes the points inside and keeps the values at both edges', () => {
    const lane = [p(0, 0), p(100, 1), p(200, 0)];
    const result = deleteRange(lane, 50, 150);
    expect(result).toEqual([p(0, 0), p(50, 0.5), p(150, 0.5), p(200, 0)]);
    expect(automationValueAt(result, 25)).toBeCloseTo(automationValueAt(lane, 25) ?? NaN);
    expect(automationValueAt(result, 175)).toBeCloseTo(automationValueAt(lane, 175) ?? NaN);
  });

  it('adds an edge point only where the lane goes on beyond it', () => {
    expect(deleteRange([p(100, 1), p(200, 0)], 0, 150)).toEqual([p(150, 0.5), p(200, 0)]);
    expect(deleteRange([p(100, 1), p(200, 0)], 150, 400)).toEqual([p(100, 1), p(150, 0.5)]);
    expect(deleteRange([p(100, 1), p(200, 0)], 0, 400)).toEqual([]);
  });

  it('leaves a lane alone when the range is outside it', () => {
    const lane = [p(100, 1), p(200, 0)];
    expect(deleteRange(lane, 300, 400)).toEqual(lane);
  });

  it('keeps the curve of the segment the end falls in', () => {
    const result = deleteRange([p(0, 0), p(100, 1, 0.5), p(300, 0)], 50, 150);
    expect(result.find(q => q.tick === 150)?.curve).toBe(0.5);
  });
});

describe('drawing', () => {
  it('simplifies a stroke to the points that matter', () => {
    const line = Array.from({ length: 11 }, (_, i) => ({ x: i * 10, y: i < 6 ? i * 10 : 100 - i * 10 }));
    const kept = simplify(
      line,
      q => q.x,
      q => q.y,
      1
    );
    expect(kept.map(q => q.x)).toEqual([0, 50, 100]);
  });

  it('lays a stroke over the lane, replacing what it spans', () => {
    const lane = [p(0, 0), p(100, 1), p(200, 0), p(300, 1)];
    expect(replaceSpan(lane, [p(250, 0.5), p(50, 0.2)]).map(q => q.tick)).toEqual([0, 50, 250, 300]);
  });
});
