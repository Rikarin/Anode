import type { TempoEvent } from '@anode/api-client';
import { MeterMap } from '@anode/timeline';
import { TempoMap } from '../music/tempo-map';
import { beatMappingGesture, mapBeats, mappingFor } from './beat-mapping';

const Sr = 48_000;
const at120: TempoEvent[] = [{ tick: 0, bpm: 120, curve: 'step', shape: 0 }];

describe('beat mapping', () => {
  it('sets the tempo so that a beat lands on a sample', () => {
    // Bar 2 (tick 3 840, 2 s at 120) onto 2.4 s: 100 bpm until there, then 120 again.
    const events = mapBeats(at120, [{ tick: 3840, sample: 2.4 * Sr }], Sr);
    expect(events).toEqual([
      { tick: 0, bpm: 100, curve: 'step', shape: 0 },
      { tick: 3840, bpm: 120, curve: 'step', shape: 0 }
    ]);
    expect(new TempoMap(events).secondsAt(3840)).toBeCloseTo(2.4);
  });

  it('keeps earlier mappings', () => {
    const events = mapBeats(
      at120,
      [
        { tick: 3840, sample: 2.4 * Sr },
        { tick: 7680, sample: 4.2 * Sr }
      ],
      Sr
    );
    const map = new TempoMap(events);
    expect(map.secondsAt(3840)).toBeCloseTo(2.4);
    expect(map.secondsAt(7680)).toBeCloseTo(4.2);
  });

  it('refuses a beat mapped before its anchor', () => {
    const events = mapBeats(at120, [{ tick: 3840, sample: 2 * Sr }], Sr);
    expect(() => mapBeats(events, [{ tick: 7680, sample: Sr }], Sr)).toThrow(RangeError);
  });

  it('drags a beat onto the nearest transient', () => {
    const meter = MeterMap.constant(4, 4);
    // A press near beat 2 of bar 2, dropped 30 ticks from a transient at 5 000 (10 px at 4 ticks a pixel).
    expect(beatMappingGesture(meter, 4790, 5030, [4000, 5000], 4)).toEqual({
      beatTick: 4800,
      targetTick: 5000,
      onTransient: true
    });
    expect(beatMappingGesture(meter, 4790, 5300, [5000], 4)).toEqual({
      beatTick: 4800,
      targetTick: 5300,
      onTransient: false
    });
    expect(beatMappingGesture(meter, 100, 500, [], 4)).toBeNull();
    expect(mappingFor(TempoMap.constant(120), 4800, 5000, Sr)).toEqual({ tick: 4800, sample: 125_000 });
  });
});
