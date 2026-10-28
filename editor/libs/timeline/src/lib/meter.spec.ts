import { GridLevel } from './grid';
import { MeterMap } from './meter';

describe('MeterMap', () => {
  const map = new MeterMap([
    { tick: 0, numerator: 4, denominator: 4 },
    { tick: 2 * 3840, numerator: 3, denominator: 4 },
    { tick: 2 * 3840 + 2 * 2880, numerator: 6, denominator: 8 }
  ]);

  it('counts bars through signature changes', () => {
    expect(map.position(0)).toEqual({ bar: 1, beat: 1, division: 1, tick: 1 });
    expect(map.position(7680)).toEqual({ bar: 3, beat: 1, division: 1, tick: 1 });
    expect(map.position(7680 + 2880 + 960)).toEqual({ bar: 4, beat: 2, division: 1, tick: 1 });
    expect(map.barStart(4)).toBe(7680 + 2 * 2880);
    expect(map.barAt(7680 + 2 * 2880 + 2880)).toBe(5);
    expect(map.signatureAt(20_000)).toEqual({ numerator: 6, denominator: 8 });
  });

  it('moves a change off a bar line to the next bar line', () => {
    const m = new MeterMap([
      { tick: 0, numerator: 4, denominator: 4 },
      { tick: 1000, numerator: 3, denominator: 4 }
    ]);
    expect(m.barStart(1)).toBe(3840);
    expect(m.barTicksAt(3840)).toBe(2880);
  });

  it('snaps to bars and beats of the meter in force', () => {
    expect(map.snapBar(9000)).toBe(7680);
    expect(map.snapBar(9300)).toBe(7680 + 2880);
    expect(map.snapBeat(7680 + 1000)).toBe(7680 + 960);
    expect(map.snapStep(100, 240)).toBe(0);
    expect(map.isBarLine(7680 + 2880)).toBe(true);
  });

  it('draws grid lines per segment', () => {
    const lines = map.gridLines(7680, 7680 + 2880, 960);
    expect(lines.map(l => l.level)).toEqual([GridLevel.Bar, GridLevel.Beat, GridLevel.Beat, GridLevel.Bar]);
    const bars = map.gridLines(0, 7680 + 2 * 2880, 1e9, 2);
    expect(bars.map(l => l.tick)).toEqual([0, 7680, 7680 + 2 * 2880]);
  });
});
