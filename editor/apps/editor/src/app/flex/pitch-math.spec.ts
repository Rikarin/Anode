import type { PitchNote } from '@anode/api-client';
import {
  centsToHz,
  dragHotspot,
  dragPitch,
  hzToCents,
  joinNotes,
  nearestInScale,
  noteTrend,
  notesFromSegments,
  perfectPitch,
  pitchLabel,
  playedCents,
  splitNote
} from './pitch-math';

const note = (overrides: Partial<PitchNote> = {}): PitchNote => ({
  startFrame: 0,
  endFrame: 4800,
  detectedCents: 6912,
  targetCents: 6912,
  drift: 1,
  vibrato: 1,
  formantCents: 0,
  gainDb: 0,
  ...overrides
});

describe('pitch-note maths', () => {
  it('converts between hertz and MIDI cents', () => {
    expect(hzToCents(440)).toBeCloseTo(6900);
    expect(centsToHz(6000)).toBeCloseTo(261.63, 1);
    expect(hzToCents(centsToHz(6543.21))).toBeCloseTo(6543.21);
  });

  it('labels a pitch with its nearest note (C3 is 60, as in Logic) and deviation', () => {
    expect(pitchLabel(6912)).toBe('A3 +12');
    expect(pitchLabel(6890)).toBe('A3 −10');
    expect(pitchLabel(6000)).toBe('C3 ±0');
  });

  it('quantises to the chromatic grid or a scale', () => {
    expect(nearestInScale(6140)).toBe(6100);
    // C♯ is not in C major: 6140 goes to C (6000) or D (6200), whichever is nearer.
    expect(nearestInScale(6140, { tonic: 0, mode: 'major' })).toBe(6200);
    expect(nearestInScale(6060, { tonic: 0, mode: 'major' })).toBe(6000);
    expect(nearestInScale(6340, { tonic: 9, mode: 'minor' })).toBe(6400);
  });

  it('sets notes to perfect pitch by a percentage', () => {
    const notes = [note(), note({ detectedCents: 6170, targetCents: 6370 })];
    expect(perfectPitch(notes, 100).map(n => n.targetCents)).toEqual([6900, 6400]);
    expect(perfectPitch(notes, 50).map(n => n.targetCents)).toEqual([6906, 6385]);
    expect(perfectPitch(notes, 100, 'chromatic', new Set([1]))[0]?.targetCents).toBe(6912);
  });

  it('drags pitch by semitones, or finely', () => {
    expect(dragPitch(note(), 230).targetCents).toBe(7112);
    expect(dragPitch(note(), 230, true).targetCents).toBe(7142);
  });

  it('drags hotspots within their ranges', () => {
    expect(dragHotspot(note(), 'vibrato', 25).vibrato).toBe(0.5);
    expect(dragHotspot(note(), 'drift', -500).drift).toBe(2);
    expect(dragHotspot(note(), 'gainDb', -8).gainDb).toBe(2);
    expect(dragHotspot(note(), 'formantCents', 10).formantCents).toBe(-100);
  });

  it('splits and joins notes', () => {
    const split = splitNote([note()], 0, 1200);
    expect(split.map(n => [n.startFrame, n.endFrame])).toEqual([
      [0, 1200],
      [1200, 4800]
    ]);
    expect(splitNote([note()], 0, 9000)).toHaveLength(1);
    const a = note({ endFrame: 1000, detectedCents: 6000, targetCents: 6000 });
    const b = note({ startFrame: 1000, endFrame: 4000, detectedCents: 6100, targetCents: 6300, gainDb: -4 });
    const [joined] = joinNotes([a, b], 0);
    expect(joined).toMatchObject({ startFrame: 0, endFrame: 4000, detectedCents: 6075, targetCents: 6225, gainDb: -3 });
  });

  it('makes notes from analysed segments, clipped to a region', () => {
    const notes = notesFromSegments(
      [
        { startFrame: 0, endFrame: 1000, cents: 6000.4, confidence: 1 },
        { startFrame: 1500, endFrame: 3000, cents: 6200, confidence: 0.9 }
      ],
      500,
      2000
    );
    expect(notes.map(n => [n.startFrame, n.endFrame, n.targetCents])).toEqual([
      [500, 1000, 6000.4],
      [1500, 2000, 6200]
    ]);
  });

  it('plays the curve around its trend scaled by vibrato, shifted by the correction', () => {
    const n = note({ detectedCents: 6900, targetCents: 7100, vibrato: 0.5 });
    const curve = [6890, 6910, null, 6900];
    const trend = noteTrend(n, curve, 1200);
    expect(trend).toBeCloseTo(6900);
    expect(playedCents(n, 6910, trend)).toBeCloseTo(7105);
    expect(playedCents(n, null, trend)).toBeNull();
  });
});
