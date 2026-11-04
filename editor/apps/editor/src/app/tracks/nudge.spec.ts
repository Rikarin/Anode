import { MeterMap } from '@anode/timeline';
import { RegionTiming, type TimedRegion } from '../music/region-timing';
import { TempoMap } from '../music/tempo-map';
import { nextPoint, nudgeCommands, nudgeTicks, previousPoint, transientTicks } from './nudge';
import { type SnapContext } from './snap';

const context: SnapContext = {
  meter: new MeterMap([
    { tick: 0, numerator: 4, denominator: 4 },
    { tick: 3840, numerator: 3, denominator: 4 }
  ]),
  tempo: TempoMap.constant(120),
  sampleRate: 48_000,
  pixelsPerTick: 48 / 960
};

describe('nudgeTicks', () => {
  it('moves by the chosen value of the signature in force', () => {
    expect(nudgeTicks(0, 'tick', 1, context)).toBe(1);
    expect(nudgeTicks(0, 'division', 1, context)).toBe(240);
    expect(nudgeTicks(960, 'beat', -1, context)).toBe(-960);
    expect(nudgeTicks(0, 'bar', 1, context)).toBe(3840);
    expect(nudgeTicks(3840, 'bar', 1, context)).toBe(2880);
  });

  it('counts the bar before when going back, so there and back returns', () => {
    expect(nudgeTicks(3840, 'bar', -1, context)).toBe(-3840);
  });

  it('measures a frame in clock time, at the very least a tick', () => {
    // 25 fps at 120 bpm: a frame is 0.04 s, 76.8 ticks.
    expect(nudgeTicks(960, 'frame', 1, context)).toBe(77);
    expect(nudgeTicks(960, 'frame', -1, context)).toBe(-77);
    expect(nudgeTicks(960, 'frame', 1, { ...context, frameRate: 100_000 })).toBe(1);
  });
});

describe('nudgeCommands', () => {
  const regions = [
    { id: 'a', startTick: 960 },
    { id: 'b', startTick: 4800 }
  ];

  it('moves every region by the same distance', () => {
    expect(nudgeCommands(regions, 'beat', 1, context)).toEqual([
      { type: 'moveRegion', regionId: 'a', startTick: 1920 },
      { type: 'moveRegion', regionId: 'b', startTick: 5760 }
    ]);
  });

  it('stops the earliest at the project start, the others keeping their distance', () => {
    expect(nudgeCommands(regions, 'bar', -1, context)).toEqual([
      { type: 'moveRegion', regionId: 'a', startTick: 0 },
      { type: 'moveRegion', regionId: 'b', startTick: 3840 }
    ]);
    expect(nudgeCommands([{ id: 'a', startTick: 0 }], 'bar', -1, context)).toEqual([]);
    expect(nudgeCommands([], 'bar', 1, context)).toEqual([]);
  });
});

describe('next and previous points', () => {
  it('skips the point the playhead is on', () => {
    expect(nextPoint([0, 960, 1920], 960)).toBe(1920);
    expect(previousPoint([0, 960, 1920], 960)).toBe(0);
    expect(nextPoint([960], 1000)).toBeNull();
    expect(previousPoint([960], 960.2)).toBeNull();
  });
});

describe('transientTicks', () => {
  it('finds only the transients in the part of the file a region plays, at their ticks', () => {
    const region: TimedRegion = {
      startTick: 3840,
      fileOffset: 24_000,
      length: 48_000,
      follow: 'off',
      flexMarkers: [],
      transpose: 0,
      fineTune: 0,
      pitchNotes: []
    };
    const timing = new RegionTiming(region, false, null, context);
    // 120 bpm: a beat is 24 000 frames.
    expect(transientTicks(timing, [0, 24_000, 48_000, 72_000])).toEqual([3840, 4800]);
  });
});
