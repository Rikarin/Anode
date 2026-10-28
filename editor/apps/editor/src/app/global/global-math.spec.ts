import type { TempoEvent } from '@anode/api-client';
import { MeterMap } from '@anode/timeline';
import {
  addSignature,
  addTempoPoint,
  bpmAtY,
  bpmRange,
  moveTempoPoint,
  nearestBoundary,
  nextSection,
  nextSectionName,
  parseKey,
  removeTempoPoint,
  sectionBoundaries,
  tempoPath,
  toggleRamp,
  yAtBpm
} from './global-math';

const track: TempoEvent[] = [
  { tick: 0, bpm: 120, curve: 'step' },
  { tick: 3840, bpm: 90, curve: 'ramp' },
  { tick: 7680, bpm: 140, curve: 'step' }
];

describe('tempo lane maths', () => {
  it('maps tempos to heights and back', () => {
    const range = bpmRange(track);
    expect(range[0]).toBeLessThanOrEqual(80);
    expect(range[1]).toBeGreaterThanOrEqual(150);
    const y = yAtBpm(100, range, 60);
    expect(bpmAtY(y, range, 60)).toBeCloseTo(100, 1);
    expect(yAtBpm(range[1], range, 60)).toBe(4);
  });

  it('adds points in order, keeping the curve of the point before', () => {
    const added = addTempoPoint(track, 5000, 100);
    expect(added.map(e => e.tick)).toEqual([0, 3840, 5000, 7680]);
    expect(added[2]?.curve).toBe('ramp');
    expect(addTempoPoint(track, 3840, 95)).toHaveLength(3);
  });

  it('keeps a dragged point between its neighbours and the first at 0', () => {
    expect(moveTempoPoint(track, 1, 99_999, 2000)[1]).toEqual({ tick: 7679, bpm: 999, curve: 'ramp' });
    expect(moveTempoPoint(track, 1, -5, 5)[1]).toEqual({ tick: 1, bpm: 20, curve: 'ramp' });
    expect(moveTempoPoint(track, 0, 500, 130)[0]).toEqual({ tick: 0, bpm: 130, curve: 'step' });
  });

  it('removes and toggles ramps, never removing the first', () => {
    expect(removeTempoPoint(track, 0)).toHaveLength(3);
    expect(removeTempoPoint(track, 1)).toHaveLength(2);
    expect(toggleRamp(track, 0)[0]?.curve).toBe('ramp');
    expect(toggleRamp(track, 1)[1]?.curve).toBe('step');
  });

  it('draws steps flat and ramps sloped', () => {
    const path = tempoPath(
      track,
      t => t / 100,
      b => b,
      100
    );
    expect(path).toBe('0.0,120.0 38.4,120.0 38.4,90.0 76.8,140.0 100.0,140.0');
  });
});

describe('parseKey', () => {
  it('reads key names', () => {
    expect(parseKey('A minor')).toEqual({ tonic: 9, mode: 'minor' });
    expect(parseKey('Am')).toEqual({ tonic: 9, mode: 'minor' });
    expect(parseKey('F# major')).toEqual({ tonic: 6, mode: 'major' });
    expect(parseKey('Bb')).toEqual({ tonic: 10, mode: 'major' });
    expect(parseKey('H')).toBeNull();
  });
});

describe('signature and arrangement maths', () => {
  const meter = MeterMap.constant(4, 4);

  it('puts a signature change on the nearest bar line', () => {
    expect(addSignature([{ tick: 0, numerator: 4, denominator: 4 }], meter, 4000, 3, 4)).toEqual([
      { tick: 0, numerator: 4, denominator: 4 },
      { tick: 3840, numerator: 3, denominator: 4 }
    ]);
  });

  it('finds section boundaries and where a new section goes', () => {
    const sections = [
      { id: 's1', name: 'Intro', startTick: 0, lengthTicks: 3840 * 4, color: '#fff' },
      { id: 's2', name: 'Verse', startTick: 3840 * 4, lengthTicks: 3840 * 8, color: '#fff' }
    ];
    expect(sectionBoundaries(sections)).toEqual([0, 3840 * 4, 3840 * 12]);
    expect(nearestBoundary(sections, 3840 * 9)).toBe(3840 * 12);
    expect(nextSection(sections, meter)).toEqual({ startTick: 3840 * 12, lengthTicks: 3840 * 8 });
    expect(nextSectionName(sections)).toBe('Chorus');
  });
});
