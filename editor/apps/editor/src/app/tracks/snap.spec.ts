import { MeterMap } from '@anode/timeline';
import { TempoMap } from '../music/tempo-map';
import { snapBy, type SnapContext, snapUnit } from './snap';

const context: SnapContext = {
  meter: new MeterMap([
    { tick: 0, numerator: 4, denominator: 4 },
    { tick: 3840, numerator: 3, denominator: 4 }
  ]),
  tempo: TempoMap.constant(120),
  sampleRate: 48_000,
  pixelsPerTick: 48 / 960
};

describe('snapBy', () => {
  it('snaps to bars, beats and divisions of the signature in force', () => {
    expect(snapBy(2100, 'bar', context)).toBe(3840);
    expect(snapBy(3840 + 1500, 'bar', context)).toBe(3840 + 2880);
    expect(snapBy(1000, 'beat', context)).toBe(960);
    expect(snapBy(130, 'division', context)).toBe(240);
    expect(snapBy(130.4, 'ticks', context)).toBe(130);
  });

  it('follows the zoom in smart mode', () => {
    expect(snapBy(130, 'smart', context)).toBe(240); // 48 px a beat: sixteenths are 12 px
    expect(snapBy(1300, 'smart', { ...context, pixelsPerTick: 2 / 960 })).toBe(0); // bars
    expect(snapUnit(0, 'smart', context)).toBe(240);
  });

  it('snaps to SMPTE frames and samples of clock time', () => {
    // 25 fps at 120 bpm: a frame is 0.04 s = 76.8 ticks.
    expect(snapBy(100, 'frames', context)).toBe(77);
    // A tick is 25 samples at 120 bpm and 48 kHz: 1/25 tick is one sample.
    expect(snapBy(1.01, 'samples', context)).toBeCloseTo(1, 6);
    expect(snapUnit(0, 'samples', context)).toBe(0);
  });

  it('never snaps below the start', () => {
    expect(snapBy(-500, 'beat', context)).toBe(0);
  });
});
