import { TempoMap } from './tempo-map';
import { compiledSamplesToTick, detuneSemitones, formatSpeed, midiTranspose, parseSpeed } from './varispeed';

describe('Varispeed', () => {
  it('shows the speed as percent, tempo or detune', () => {
    expect(formatSpeed(0.9, 'percent')).toBe('−10.00 %');
    expect(formatSpeed(1.25, 'percent')).toBe('+25.00 %');
    expect(formatSpeed(0.9, 'tempo', 120)).toBe('108.00 bpm');
    expect(formatSpeed(0.9, 'detune')).toBe('−1.82 st');
    expect(formatSpeed(1, 'detune')).toBe('0.00 st');
  });

  it('reads typed values in each unit', () => {
    expect(parseSpeed('-10', 'percent')).toBe(0.9);
    expect(parseSpeed('108', 'tempo', 120)).toBe(0.9);
    expect(parseSpeed('-1.824', 'detune')).toBeCloseTo(0.9, 3);
    expect(parseSpeed('400', 'percent')).toBe(1.5);
    expect(parseSpeed('', 'percent')).toBeNull();
  });

  it('detunes tape-style and transposes MIDI by whole semitones', () => {
    expect(detuneSemitones(0.9)).toBeCloseTo(-1.824, 3);
    expect(midiTranspose(0.9)).toBe(-2);
    expect(midiTranspose(1.06)).toBe(1);
  });

  it('maps compiled samples to ticks', () => {
    // At 90 %, 1 s of compiled samples is 0.9 s of the tempo track: 1 728 ticks at 120 bpm.
    expect(compiledSamplesToTick(48_000, 48_000, TempoMap.constant(120), 0.9)).toBeCloseTo(1728);
  });
});
