import {
  addGraphPoint,
  controlIds,
  controlValueFor,
  graphProblem,
  graphValue,
  LinearGraph,
  mappedValue,
  moveGraphPoint,
  removeGraphPoint,
  smartControlsOf
} from './smart-math';

describe('Smart Control maths (docs/07-phase-6.md)', () => {
  it('lists every control of a layout, and shows null controls as the 8-knob layout at 0, unmapped', () => {
    expect(controlIds('knobs8')).toEqual([
      'knob1',
      'knob2',
      'knob3',
      'knob4',
      'knob5',
      'knob6',
      'knob7',
      'knob8',
      'switch1',
      'switch2',
      'padX',
      'padY'
    ]);
    expect(controlIds('knobs12').length).toBe(16);
    const shown = smartControlsOf(null);
    expect(shown.layout).toBe('knobs8');
    expect(shown.controls.every(c => c.value === 0 && c.mappings.length === 0)).toBe(true);
    expect(shown.controls.find(c => c.id === 'switch1')?.kind).toBe('switch');
    expect(shown.controls.find(c => c.id === 'padY')?.kind).toBe('pad');
  });

  it('reads a graph piecewise linearly, flat beyond its ends', () => {
    expect(graphValue(LinearGraph, 0.3)).toBeCloseTo(0.3);
    const curve = [
      { x: 0, y: 0 },
      { x: 0.5, y: 0.8 },
      { x: 1, y: 1 }
    ];
    expect(graphValue(curve, 0.25)).toBeCloseTo(0.4);
    expect(graphValue(curve, 0.75)).toBeCloseTo(0.9);
    expect(graphValue(curve, -1)).toBe(0);
    expect(graphValue(curve, 2)).toBe(1);
    // A step: two points on one x.
    expect(
      graphValue(
        [
          { x: 0, y: 0 },
          { x: 0.5, y: 0 },
          { x: 0.5, y: 1 },
          { x: 1, y: 1 }
        ],
        0.6
      )
    ).toBe(1);
  });

  it('maps a value through the graph, then inverts, then into the range', () => {
    const mapping = { min: 20, max: 20_000, invert: false, graph: [...LinearGraph] };
    expect(mappedValue(mapping, 0)).toBe(20);
    expect(mappedValue(mapping, 1)).toBe(20_000);
    expect(mappedValue(mapping, 0.5)).toBeCloseTo(10_010);
    const inverted = { ...mapping, invert: true };
    expect(mappedValue(inverted, 0)).toBe(20_000);
    expect(mappedValue(inverted, 1)).toBe(20);
    // Graph, then invert: y 0.8 at 0.5 becomes 0.2 of −24…24 dB.
    const shaped = {
      min: -24,
      max: 24,
      invert: true,
      graph: [
        { x: 0, y: 0 },
        { x: 0.5, y: 0.8 },
        { x: 1, y: 1 }
      ]
    };
    expect(mappedValue(shaped, 0.5)).toBeCloseTo(-24 + 0.2 * 48);
    // A range may run downwards.
    expect(mappedValue({ min: 1, max: 0, invert: false, graph: [...LinearGraph] }, 0.25)).toBeCloseTo(0.75);
  });

  it('finds the control value that gives a parameter value (automatic mapping)', () => {
    const mapping = { min: 0, max: 10, invert: false, graph: [...LinearGraph] };
    expect(controlValueFor(mapping, 2.5)).toBeCloseTo(0.25, 3);
    expect(controlValueFor({ ...mapping, invert: true }, 2.5)).toBeCloseTo(0.75, 3);
    expect(controlValueFor({ ...mapping, min: 5, max: 5 }, 5)).toBe(0);
  });

  it('edits graphs within their rules: 2–16 points, ends at x 0 and 1, sorted', () => {
    expect(graphProblem(LinearGraph)).toBeNull();
    expect(graphProblem([{ x: 0, y: 0 }])).toMatch(/2 to 16/);
    expect(
      graphProblem([
        { x: 0.1, y: 0 },
        { x: 1, y: 1 }
      ])
    ).toMatch(/x 0 to x 1/);
    expect(
      graphProblem([
        { x: 0, y: 0 },
        { x: 0.6, y: 1 },
        { x: 0.4, y: 1 },
        { x: 1, y: 1 }
      ])
    ).toMatch(/sorted/);
    expect(
      graphProblem([
        { x: 0, y: -1 },
        { x: 1, y: 1 }
      ])
    ).toMatch(/0…1/);
    const added = addGraphPoint(LinearGraph, 0.5, 0.9);
    expect(added).toEqual([
      { x: 0, y: 0 },
      { x: 0.5, y: 0.9 },
      { x: 1, y: 1 }
    ]);
    expect(addGraphPoint(LinearGraph, 0)).toEqual(LinearGraph);
    const moved = moveGraphPoint(added, 1, 2, 0.1);
    expect(moved[1]).toEqual({ x: 1, y: 0.1 });
    expect(moveGraphPoint(added, 0, 0.5, 0.3)[0]).toEqual({ x: 0, y: 0.3 });
    expect(removeGraphPoint(added, 1)).toEqual(LinearGraph);
    expect(removeGraphPoint(added, 0)).toEqual(added);
    let full = [...LinearGraph];
    for (let i = 1; i < 30; i++) {
      full = addGraphPoint(full, i / 31);
    }
    expect(full.length).toBe(16);
  });
});
