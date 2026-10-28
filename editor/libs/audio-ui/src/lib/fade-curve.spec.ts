import { fadeCurveGain } from './fade-curve';

describe('fadeCurveGain', () => {
  it('is linear at 0 and mirrors for a fade-out', () => {
    expect(fadeCurveGain(0.25, 0)).toBeCloseTo(0.25);
    expect(fadeCurveGain(0.25, 0, true)).toBeCloseTo(0.75);
  });

  it('starts slowly at −1 and fast at +1 (x^(2^(−2c)))', () => {
    expect(fadeCurveGain(0.5, -1)).toBeCloseTo(0.5 ** 4);
    expect(fadeCurveGain(0.5, 1)).toBeCloseTo(0.5 ** 0.25);
    expect(fadeCurveGain(0, 1)).toBe(0);
    expect(fadeCurveGain(1, -1)).toBe(1);
  });
});
