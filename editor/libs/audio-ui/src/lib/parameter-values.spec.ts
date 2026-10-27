import { autoTaper, formatParameter, fromNormal, snapToStep, toNormal } from './parameter-values';
import { keyboardKeys, pitchName } from './piano-keyboard';

describe('parameter values', () => {
  it('chooses a logarithmic taper for ranges spanning two decades', () => {
    expect(autoTaper(20, 20_000)).toBe('log');
    expect(autoTaper(0.001, 10)).toBe('log');
    expect(autoTaper(0, 1)).toBe('linear');
    expect(autoTaper(-100, 100)).toBe('linear');
  });

  it('maps a logarithmic range so each decade gets equal travel', () => {
    const range = { min: 20, max: 20_000 };
    expect(toNormal(200, range)).toBeCloseTo(1 / 3);
    expect(toNormal(2000, range)).toBeCloseTo(2 / 3);
    expect(fromNormal(0.5, range)).toBeCloseTo(632.46, 1);
    expect(fromNormal(toNormal(2400, range), range)).toBeCloseTo(2400);
  });

  it('keeps discrete parameters on their steps', () => {
    const range = { min: -2, max: 2, steps: 4 };
    expect(fromNormal(0.6, range)).toBe(0);
    expect(fromNormal(0.9, range)).toBe(2);
    expect(snapToStep(1.4, { min: 1, max: 4, steps: 3 })).toBe(1);
    expect(snapToStep(9, { min: 1, max: 4, steps: 3 })).toBe(4);
  });

  it('formats values with their units', () => {
    expect(formatParameter(2400, 'Hz')).toBe('2.40 kHz');
    expect(formatParameter(12_500, 'Hz')).toBe('12.5 kHz');
    expect(formatParameter(440, 'Hz')).toBe('440 Hz');
    expect(formatParameter(0.005, 's')).toBe('5.0 ms');
    expect(formatParameter(0.3, 's')).toBe('300 ms');
    expect(formatParameter(1.5, 's')).toBe('1.50 s');
    expect(formatParameter(-6, 'dB')).toBe('-6.0 dB');
    expect(formatParameter(7, 'ct')).toBe('+7.0 ct');
    expect(formatParameter(-1, 'oct', 4)).toBe('-1 oct');
    expect(formatParameter(3, '', 3)).toBe('3');
    expect(formatParameter(0.5)).toBe('0.50');
  });
});

describe('piano keyboard', () => {
  it('names pitches as Logic does: middle C is C3', () => {
    expect(pitchName(60)).toBe('C3');
    expect(pitchName(61)).toBe('C♯3');
    expect(pitchName(0)).toBe('C-2');
    expect(pitchName(127)).toBe('G8');
  });

  it('lays out an octave and the top C, black keys between their white ones', () => {
    const keys = keyboardKeys(60, 1);
    expect(keys).toHaveLength(13);
    expect(keys.filter(k => !k.black).map(k => k.left)).toEqual([0, 1, 2, 3, 4, 5, 6, 7]);
    const cSharp = keys.find(k => k.pitch === 61);
    expect(cSharp?.black).toBe(true);
    expect(cSharp?.left).toBeGreaterThan(0);
    expect(cSharp?.left).toBeLessThan(1);
    expect(keys.find(k => k.pitch === 70)?.left).toBeGreaterThan(5);
  });

  it('starts on a white key', () => {
    expect(keyboardKeys(61, 1)[0]?.pitch).toBe(60);
  });
});
