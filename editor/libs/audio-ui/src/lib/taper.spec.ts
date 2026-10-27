import { faderDb, faderPosition, formatDb, formatPan, linearToDb, MaxFaderDb, SilenceDb } from './taper';

describe('fader taper', () => {
  it('puts 0 dB at three quarters of the travel and the ends at −∞ and +6 dB', () => {
    expect(faderPosition(0)).toBeCloseTo(0.78);
    expect(faderPosition(SilenceDb)).toBe(0);
    expect(faderPosition(-Infinity)).toBe(0);
    expect(faderPosition(MaxFaderDb)).toBe(1);
    expect(faderPosition(12)).toBe(1);
  });

  it('is monotonic and its inverse round-trips', () => {
    let previous = -1;
    for (let db = -120; db <= 6; db += 0.5) {
      const position = faderPosition(db);
      expect(position).toBeGreaterThan(previous);
      expect(faderDb(position)).toBeCloseTo(db, 6);
      previous = position;
    }
    expect(faderDb(0)).toBe(SilenceDb);
    expect(faderDb(1)).toBe(MaxFaderDb);
  });

  it('formats levels, pans and peaks as a mixer shows them', () => {
    expect(formatDb(-6)).toBe('−6.0 dB');
    expect(formatDb(2.25)).toBe('+2.3 dB');
    expect(formatDb(-0.01)).toBe('0.0 dB');
    expect(formatDb(SilenceDb, false)).toBe('−∞');
    expect(formatPan(0)).toBe('C');
    expect(formatPan(-1)).toBe('L64');
    expect(formatPan(1)).toBe('R63');
    expect(formatPan(0.25)).toBe('R16');
    expect(linearToDb(1)).toBe(0);
    expect(linearToDb(0.5)).toBeCloseTo(-6.02, 2);
    expect(linearToDb(0)).toBe(-Infinity);
  });
});
