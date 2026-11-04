import { TestBed } from '@angular/core/testing';
import { MasterMeter, MeterBus, meterLevel, meterZeroDb } from './meters';

describe('strip meter levels', () => {
  const tone = { left: 0.5, right: 0.5, rmsLeft: 0.5 * Math.SQRT1_2, rmsRight: 0.5 * Math.SQRT1_2, lufs: -9 };

  it('shows the peaks of a peak meter', () => {
    expect(meterLevel(tone, 'peak').left).toBe(0.5);
  });

  it('shows true RMS 3 dB under a sine and K-system back at its peak (AES17)', () => {
    expect(20 * Math.log10(meterLevel(tone, 'rms').left)).toBeCloseTo(-9.03, 2);
    expect(meterLevel(tone, 'k14').left).toBeCloseTo(0.5, 6);
    expect(meterLevel(tone, 'k20').right).toBeCloseTo(0.5, 6);
  });

  it('shows loudness on both bars, and nothing before its first frame', () => {
    expect(20 * Math.log10(meterLevel(tone, 'lufs').left)).toBeCloseTo(-9, 6);
    expect(meterLevel({ left: 1, right: 1 }, 'lufs')).toEqual({ left: 0, right: 0 });
    expect(meterLevel({ left: 1, right: 1 }, 'rms')).toEqual({ left: 0, right: 0 });
  });

  it('marks a K-system meter at its 0', () => {
    expect([meterZeroDb('k20'), meterZeroDb('k14'), meterZeroDb('k12')]).toEqual([-20, -14, -12]);
    expect(meterZeroDb('peak')).toBeNull();
  });

  it('keeps the levels the Server sends for a strip, and drops them when it stops', () => {
    const bus = TestBed.inject(MeterBus);
    const master = { trackId: 'master', peakLeft: 0, peakRight: 0 };
    bus.apply([{ trackId: 'a', peakLeft: 1, peakRight: 1, rmsLeft: 0.25, rmsRight: 0.25 }], master, 1);
    expect(bus.levelAs('a', 'rms').left).toBe(0.25);
    expect(bus.levelAs('a', 'peak').left).toBe(1);
    bus.apply([{ trackId: 'a', peakLeft: 1, peakRight: 1 }], { ...master, lufs: -20 }, 2);
    expect(bus.levelAs('a', 'rms').left).toBe(0);
    expect(bus.levelAs(MasterMeter, 'lufs').left).toBeCloseTo(0.1, 6);
  });
});
