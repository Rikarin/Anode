import type { ArrangementMarker, KeyEvent, SignatureEvent, TempoEvent } from '@anode/api-client';
import type { MeterMap } from '@anode/timeline';
import { rampBpm } from '../music/tempo-map';

export const MinBpm = 20;
export const MaxBpm = 999;

/** The BPM range the tempo lane shows: the track's tempos with room around them, at least 40 BPM tall. */
export function bpmRange(events: readonly TempoEvent[]): [number, number] {
  const bpms = events.map(e => e.bpm);
  const lo = Math.min(...bpms, 120);
  const hi = Math.max(...bpms, 120);
  const pad = Math.max(10, (40 - (hi - lo)) / 2);
  return [Math.max(MinBpm, Math.floor((lo - pad) / 10) * 10), Math.min(MaxBpm, Math.ceil((hi + pad) / 10) * 10)];
}

/** The y of a tempo in a lane `height` pixels tall (the fastest at the top). */
export function yAtBpm(bpm: number, range: readonly [number, number], height: number, inset = 4): number {
  const [lo, hi] = range;
  return inset + (1 - (bpm - lo) / (hi - lo)) * (height - 2 * inset);
}

/** The tempo at y, rounded to 0.1 BPM and kept to the lane's range. */
export function bpmAtY(y: number, range: readonly [number, number], height: number, inset = 4): number {
  const [lo, hi] = range;
  const f = 1 - (y - inset) / Math.max(1, height - 2 * inset);
  return clampBpm(lo + f * (hi - lo));
}

export function clampBpm(bpm: number): number {
  return Math.round(Math.min(MaxBpm, Math.max(MinBpm, bpm)) * 10) / 10;
}

/** Tempo events sorted, with the first at tick 0 (the contract's rule). */
function tidy(events: TempoEvent[]): TempoEvent[] {
  const sorted = events.sort((a, b) => a.tick - b.tick);
  if (sorted[0]) {
    sorted[0] = { ...sorted[0], tick: 0 };
  }
  return sorted;
}

/** Adds a tempo point (replacing one at the same tick); it keeps the curve of the point it follows. */
export function addTempoPoint(events: readonly TempoEvent[], tick: number, bpm: number): TempoEvent[] {
  const t = Math.max(0, Math.round(tick));
  const before = [...events].filter(e => e.tick <= t).pop();
  return tidy([
    ...events.filter(e => e.tick !== t),
    { tick: t, bpm: clampBpm(bpm), curve: before?.curve ?? 'step', shape: 0 }
  ]);
}

/**
 * Moves point `index` to (`tick`, `bpm`): the first point stays at tick 0; the others stay strictly between their
 * neighbours, so dragging never reorders the track.
 */
export function moveTempoPoint(events: readonly TempoEvent[], index: number, tick: number, bpm: number): TempoEvent[] {
  const next = events.map(e => ({ ...e }));
  const point = next[index];
  if (!point) {
    return next;
  }
  const lo = index === 0 ? 0 : (next[index - 1]?.tick ?? 0) + 1;
  const hi = index === 0 ? 0 : (next[index + 1]?.tick ?? Infinity) - 1;
  point.tick = Math.round(Math.min(hi, Math.max(lo, tick)));
  point.bpm = clampBpm(bpm);
  return next;
}

/** Removes a point (never the first). */
export function removeTempoPoint(events: readonly TempoEvent[], index: number): TempoEvent[] {
  return index <= 0 ? [...events] : events.filter((_, i) => i !== index);
}

/** Turns the curve from a point to the next into a ramp, or back into a step. */
export function toggleRamp(events: readonly TempoEvent[], index: number): TempoEvent[] {
  return events.map((e, i) => (i === index ? { ...e, curve: e.curve === 'ramp' ? 'step' : 'ramp' } : e));
}

/** Points a curved ramp is drawn with. */
const CurveSamples = 24;

/**
 * The tempo track's points as an SVG polyline: steps hold, ramps slope to the next point (along their curve shape,
 * sampled, when it is not 0), the last holds to `endX`.
 */
export function tempoPath(
  events: readonly TempoEvent[],
  x: (tick: number) => number,
  y: (bpm: number) => number,
  endX: number
): string {
  const points: string[] = [];
  events.forEach((e, i) => {
    const next = events[i + 1];
    points.push(`${x(e.tick).toFixed(1)},${y(e.bpm).toFixed(1)}`);
    if (!next) {
      points.push(`${Math.max(endX, x(e.tick)).toFixed(1)},${y(e.bpm).toFixed(1)}`);
    } else if (e.curve === 'step') {
      points.push(`${x(next.tick).toFixed(1)},${y(e.bpm).toFixed(1)}`);
    } else if (e.shape !== 0 && next.bpm !== e.bpm) {
      for (let k = 1; k < CurveSamples; k++) {
        const f = k / CurveSamples;
        const tick = e.tick + f * (next.tick - e.tick);
        points.push(`${x(tick).toFixed(1)},${y(rampBpm(e.bpm, next.bpm, f, e.shape)).toFixed(1)}`);
      }
    }
  });
  return points.join(' ');
}

/**
 * Where a ramp's curve handle sits: halfway along it in ticks, on its curve. Null for a step, the last point, or a ramp
 * between equal tempos (it has no curve to shape).
 */
export function curveHandle(events: readonly TempoEvent[], index: number): { tick: number; bpm: number } | null {
  const e = events[index];
  const next = events[index + 1];
  if (e?.curve !== 'ramp' || !next || next.bpm === e.bpm) {
    return null;
  }
  return { tick: (e.tick + next.tick) / 2, bpm: rampBpm(e.bpm, next.bpm, 0.5, e.shape) };
}

/** Pixels of vertical drag a whole unit of curve shape takes. */
export const ShapePixels = 40;

/**
 * A ramp's shape after its handle is dragged `up` pixels (negative: down): the handle follows the pointer, so up bends
 * a rising ramp to a faster start (a higher shape) and a falling one to a slower start; −1…1, to 0.01.
 */
export function shapeAfterDrag(shape: number, up: number, bpm0: number, bpm1: number): number {
  const direction = bpm1 >= bpm0 ? 1 : -1;
  const next = shape + (direction * up) / ShapePixels;
  return Math.round(Math.min(1, Math.max(-1, next)) * 100) / 100;
}

/** The events with point `index`'s ramp shaped `shape` (−1…1). */
export function withShape(events: readonly TempoEvent[], index: number, shape: number): TempoEvent[] {
  return events.map((e, i) => (i === index ? { ...e, shape: Math.min(1, Math.max(-1, shape)) } : e));
}

/**
 * The project ticks of a region's transients (file frames, from its analysis), through its timing: only those inside
 * the region, ascending.
 */
export function transientTicks(
  region: { fileOffset: number; length: number },
  transients: readonly number[],
  tickAt: (frame: number) => number
): number[] {
  const end = region.fileOffset + region.length;
  return transients.filter(f => f >= region.fileOffset && f < end).map(tickAt);
}

/** Adds a signature change at the bar line nearest `tick` (replacing one there); the first stays at 0. */
export function addSignature(
  events: readonly SignatureEvent[],
  meter: MeterMap,
  tick: number,
  numerator: number,
  denominator: number
): SignatureEvent[] {
  const at = meter.snapBar(tick);
  const sorted = [...events.filter(e => e.tick !== at), { tick: at, numerator, denominator }].sort(
    (a, b) => a.tick - b.tick
  );
  return sorted;
}

/** Adds a key change at `tick` (on a bar line; replacing one there). */
export function addKey(events: readonly KeyEvent[], tick: number, tonic: number, mode: KeyEvent['mode']): KeyEvent[] {
  return [...events.filter(e => e.tick !== tick), { tick, tonic, mode }].sort((a, b) => a.tick - b.tick);
}

/** Removes the event at `index` of a global track whose first event stays. */
export function removeEvent<T>(events: readonly T[], index: number): T[] {
  return index <= 0 ? [...events] : events.filter((_, i) => i !== index);
}

/** Pitch-class names for keys, as Logic spells them. */
export const KeyNames = ['C', 'C♯', 'D', 'E♭', 'E', 'F', 'F♯', 'G', 'A♭', 'A', 'B♭', 'B'];

export function keyName(event: Pick<KeyEvent, 'tonic' | 'mode'>): string {
  return `${KeyNames[event.tonic] ?? '?'} ${event.mode === 'minor' ? 'minor' : 'major'}`;
}

/** Section boundaries a section can be moved to: every section start and the end of the last one. */
export function sectionBoundaries(sections: readonly ArrangementMarker[]): number[] {
  const starts = sections.map(s => s.startTick);
  const end = Math.max(0, ...sections.map(s => s.startTick + s.lengthTicks));
  return [...new Set([...starts, end])].sort((a, b) => a - b);
}

/** The boundary nearest `tick` (where a dragged section is inserted). */
export function nearestBoundary(sections: readonly ArrangementMarker[], tick: number): number {
  const boundaries = sectionBoundaries(sections);
  return boundaries.reduce((best, b) => (Math.abs(b - tick) < Math.abs(best - tick) ? b : best), boundaries[0] ?? 0);
}

/** Where a new section goes: after the last, `bars` bars long (Logic adds 8-bar sections). */
export function nextSection(
  sections: readonly ArrangementMarker[],
  meter: MeterMap,
  bars = 8
): { startTick: number; lengthTicks: number } {
  const start = Math.max(0, ...sections.map(s => s.startTick + s.lengthTicks));
  const startBar = Math.round(meter.barAt(start));
  return { startTick: start, lengthTicks: meter.barStart(startBar + bars) - start };
}

/** Each global track's height in the Tracks area, in CSS pixels. */
export const GlobalLaneHeights = {
  arrangement: 24,
  marker: 22,
  tempo: 64,
  beatMapping: 40,
  signature: 22,
  key: 22
} as const;

/** "A minor", "Am", "F# major", "Bb" to a key, or null. */
export function parseKey(text: string): { tonic: number; mode: 'major' | 'minor' } | null {
  const match = /^\s*([A-Ga-g])\s*([#♯b♭]?)\s*(m|min|minor|maj|major)?\s*$/.exec(text);
  if (!match?.[1]) {
    return null;
  }
  const naturals: Record<string, number> = { c: 0, d: 2, e: 4, f: 5, g: 7, a: 9, b: 11 };
  const base = naturals[match[1].toLowerCase()] ?? 0;
  const accidental = match[2] === '#' || match[2] === '♯' ? 1 : match[2] === 'b' || match[2] === '♭' ? -1 : 0;
  const mode = match[3]?.startsWith('m') && !match[3].startsWith('maj') ? 'minor' : 'major';
  return { tonic: (base + accidental + 12) % 12, mode };
}

/** Logic's section names, in the order it offers them. */
export const SectionNames = ['Intro', 'Verse', 'Chorus', 'Bridge', 'Outro'];

/** The section name after these: Intro, Verse, Chorus, Verse, Chorus, Bridge, Chorus, Outro, then numbered. */
export function nextSectionName(sections: readonly ArrangementMarker[]): string {
  const order = ['Intro', 'Verse', 'Chorus', 'Verse', 'Chorus', 'Bridge', 'Chorus', 'Outro'];
  return order[sections.length] ?? `Section ${sections.length + 1}`;
}
