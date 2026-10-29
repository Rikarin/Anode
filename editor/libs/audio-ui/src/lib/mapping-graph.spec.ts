import { mappingGraphValue } from './mapping-graph';

describe('mappingGraphValue', () => {
  it('reads a graph piecewise linearly, flat beyond its ends, linear when it has too few points', () => {
    const curve = [
      { x: 0, y: 0.2 },
      { x: 0.5, y: 0.8 },
      { x: 1, y: 1 }
    ];
    expect(mappingGraphValue(curve, 0)).toBe(0.2);
    expect(mappingGraphValue(curve, 0.25)).toBeCloseTo(0.5);
    expect(mappingGraphValue(curve, 0.75)).toBeCloseTo(0.9);
    expect(mappingGraphValue(curve, 2)).toBe(1);
    expect(mappingGraphValue([], 0.3)).toBeCloseTo(0.3);
  });
});
