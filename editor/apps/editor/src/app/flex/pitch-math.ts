import type { KeyEvent, PitchNote, PitchSegment } from '@anode/api-client';
import { noteName } from '../midi/midi-math';

/** Cents of a semitone. */
export const Semitone = 100;

/** The ranges Flex Pitch's hotspots set (docs/06-phase-5.md: drift and vibrato 0…2, 1 unchanged). */
export const HotspotRanges = {
  drift: [0, 2],
  vibrato: [0, 2],
  formantCents: [-1200, 1200],
  gainDb: [-30, 12]
} as const;

export type Hotspot = keyof typeof HotspotRanges;

/** MIDI cents (6900 is A4) of a frequency, and back. */
export function hzToCents(hz: number): number {
  return 6900 + 1200 * Math.log2(hz / 440);
}

export function centsToHz(cents: number): number {
  return 440 * 2 ** ((cents - 6900) / 1200);
}

/** "C3 +12" (the nearest note and how far off it is, in cents), as Flex Pitch labels a note. */
export function pitchLabel(cents: number): string {
  const nearest = Math.round(cents / Semitone);
  const off = Math.round(cents - nearest * Semitone);
  const sign = off > 0 ? '+' : off < 0 ? '−' : '±';
  return `${noteName(nearest)} ${sign}${Math.abs(off)}`;
}

/** Semitones of a scale, by mode, from its tonic. */
const ScaleSteps: Record<KeyEvent['mode'], readonly number[]> = {
  major: [0, 2, 4, 5, 7, 9, 11],
  minor: [0, 2, 3, 5, 7, 8, 10]
};

/** A scale to quantise to: chromatic, or a key's. */
export type PitchScale = 'chromatic' | Pick<KeyEvent, 'tonic' | 'mode'>;

/** The pitch of the scale nearest `cents` (whole semitones). */
export function nearestInScale(cents: number, scale: PitchScale = 'chromatic'): number {
  const semis = cents / Semitone;
  if (scale === 'chromatic') {
    return Math.round(semis) * Semitone;
  }
  const steps = ScaleSteps[scale.mode];
  let best = Math.round(semis);
  let distance = Infinity;
  for (let s = Math.floor(semis) - 2; s <= Math.ceil(semis) + 2; s++) {
    const pc = (((s - scale.tonic) % 12) + 12) % 12;
    if (steps.includes(pc) && Math.abs(s - semis) < distance) {
      best = s;
      distance = Math.abs(s - semis);
    }
  }
  return best * Semitone;
}

/**
 * "Set to perfect pitch": each note's target moves `percent` (0…100) of the way to the nearest pitch of the scale (a
 * dragged note keeps its new pitch, only its remaining deviation goes).
 */
export function perfectPitch(
  notes: readonly PitchNote[],
  percent: number,
  scale: PitchScale = 'chromatic',
  only?: ReadonlySet<number>
): PitchNote[] {
  const f = Math.min(100, Math.max(0, percent)) / 100;
  return notes.map((n, i) =>
    only && !only.has(i)
      ? n
      : { ...n, targetCents: round2(n.targetCents + (nearestInScale(n.targetCents, scale) - n.targetCents) * f) }
  );
}

/**
 * A pitch drag by `deltaCents`: in whole semitones (keeping the note's deviation), or freely with `fine` (⌥, Logic's
 * fine pitch).
 */
export function dragPitch(note: PitchNote, deltaCents: number, fine = false): PitchNote {
  const delta = fine ? Math.round(deltaCents) : Math.round(deltaCents / Semitone) * Semitone;
  return { ...note, targetCents: round2(note.targetCents + delta) };
}

/** A hotspot's value after a vertical drag of `dy` pixels (up is more), clamped to its range. */
export function dragHotspot(note: PitchNote, hotspot: Hotspot, dy: number): PitchNote {
  const [lo, hi] = HotspotRanges[hotspot];
  const perPixel = hotspot === 'formantCents' ? 10 : hotspot === 'gainDb' ? 0.25 : 0.02;
  const value = Math.min(hi, Math.max(lo, note[hotspot] - dy * perPixel));
  return { ...note, [hotspot]: round2(value) };
}

/** Splits note `index` at file frame `frame` (inside it) into two with the same settings. */
export function splitNote(notes: readonly PitchNote[], index: number, frame: number): PitchNote[] {
  const note = notes[index];
  const at = Math.round(frame);
  if (!note || at <= note.startFrame || at >= note.endFrame) {
    return [...notes];
  }
  return [...notes.slice(0, index), { ...note, endFrame: at }, { ...note, startFrame: at }, ...notes.slice(index + 1)];
}

/**
 * Joins note `index` with the next: one note over both, its detected pitch and settings their length-weighted averages,
 * its target the detected pitch plus their average correction.
 */
export function joinNotes(notes: readonly PitchNote[], index: number): PitchNote[] {
  const a = notes[index];
  const b = notes[index + 1];
  if (!a || !b) {
    return [...notes];
  }
  const wa = Math.max(1, a.endFrame - a.startFrame);
  const wb = Math.max(1, b.endFrame - b.startFrame);
  const mean = (x: number, y: number) => round2((x * wa + y * wb) / (wa + wb));
  const detected = mean(a.detectedCents, b.detectedCents);
  const joined: PitchNote = {
    startFrame: a.startFrame,
    endFrame: b.endFrame,
    detectedCents: detected,
    targetCents: round2(detected + mean(a.targetCents - a.detectedCents, b.targetCents - b.detectedCents)),
    drift: mean(a.drift, b.drift),
    vibrato: mean(a.vibrato, b.vibrato),
    formantCents: mean(a.formantCents, b.formantCents),
    gainDb: mean(a.gainDb, b.gainDb)
  };
  return [...notes.slice(0, index), joined, ...notes.slice(index + 2)];
}

/** Flex Pitch's starting notes from a file's analysed notes inside `[from, to)`, unchanged (target = detected). */
export function notesFromSegments(segments: readonly PitchSegment[], from = -Infinity, to = Infinity): PitchNote[] {
  return segments
    .filter(s => s.endFrame > from && s.startFrame < to)
    .map(s => ({
      startFrame: Math.max(s.startFrame, from),
      endFrame: Math.min(s.endFrame, to),
      detectedCents: round2(s.cents),
      targetCents: round2(s.cents),
      drift: 1,
      vibrato: 1,
      formantCents: 0,
      gainDb: 0
    }));
}

/**
 * The pitch a note plays at a hop of the analysed curve (docs/06-phase-5.md, compilation): the curve around the note's
 * own trend (its mean) scaled by `vibrato`, plus the correction `target − detected`. Null where unvoiced.
 */
export function playedCents(note: PitchNote, curve: number | null, trend: number): number | null {
  if (curve === null) {
    return null;
  }
  return trend + (curve - trend) * note.vibrato + (note.targetCents - note.detectedCents);
}

/** The analysed curve's mean over a note (its trend), or the detected pitch where it is unvoiced throughout. */
export function noteTrend(note: PitchNote, pitch: readonly (number | null)[], hop: number): number {
  let sum = 0;
  let count = 0;
  for (let h = Math.ceil(note.startFrame / hop); h * hop < note.endFrame && h < pitch.length; h++) {
    const value = pitch[h];
    if (value !== null && value !== undefined) {
      sum += value;
      count++;
    }
  }
  return count > 0 ? sum / count : note.detectedCents;
}

function round2(value: number): number {
  return Math.round(value * 100) / 100;
}
