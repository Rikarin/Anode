import type { MeterMap } from '@anode/timeline';

/** Ticks per quarter note. */
const Ppq = 960;

/**
 * Logic's positions and lengths as the Event List and the Transform window show them: bar, beat, division and tick
 * (`1 1 1 1` is the project start; a length counts from 0, `0 1 0 0` a beat). Divisions are 1/`division` notes (a
 * 1/16 by default), at most a beat; ticks run from 1 within a division.
 */
export interface PositionFormat {
  division?: number;
}

function divisionTicks(beatTicks: number, division: number): number {
  return Math.min(beatTicks, (Ppq * 4) / division);
}

/** A project tick as `bar beat division tick`, each from 1 (`1 1 1 1` at tick 0). */
export function formatPosition(tick: number, meter: MeterMap, format: PositionFormat = {}): string {
  const negative = tick < 0;
  const p = meter.position(Math.abs(Math.round(tick)), format.division ?? 16);
  const text = `${p.bar} ${p.beat} ${p.division} ${p.tick}`;
  return negative ? `-${text}` : text;
}

/**
 * Parses a position typed as `bar beat division tick` (spaces, dots or colons between; later fields may be left out
 * and count as 1) into a project tick, or null when it is not one. Fields past their range carry over (`1 5 1 1` in
 * 4/4 is bar 2), as Logic's fields do.
 */
export function parsePosition(text: string, meter: MeterMap, format: PositionFormat = {}): number | null {
  const fields = splitFields(text);
  if (!fields || fields.length === 0 || fields.length > 4) {
    return null;
  }
  const [bar = 1, beat = 1, division = 1, tick = 1] = fields;
  if (bar < 1 || beat < 1 || division < 1 || tick < 1) {
    return null;
  }
  const barStart = meter.barStart(bar - 1);
  const beatTicks = meter.beatTicksAt(barStart);
  const step = divisionTicks(beatTicks, format.division ?? 16);
  return Math.round(barStart + (beat - 1) * beatTicks + (division - 1) * step + (tick - 1));
}

/**
 * A length in ticks as `bars beats divisions ticks`, each from 0, measured in the meter at `at` (a note's start):
 * 480 ticks in 4/4 is `0 0 2 0`.
 */
export function formatLength(length: number, meter: MeterMap, at = 0, format: PositionFormat = {}): string {
  const ticks = Math.max(0, Math.round(length));
  const barTicks = meter.barTicksAt(Math.max(0, at));
  const beatTicks = meter.beatTicksAt(Math.max(0, at));
  const step = divisionTicks(beatTicks, format.division ?? 16);
  const bars = Math.floor(ticks / barTicks);
  const inBar = ticks - bars * barTicks;
  const beats = Math.floor(inBar / beatTicks);
  const inBeat = inBar - beats * beatTicks;
  const divisions = Math.floor(inBeat / step);
  return `${bars} ${beats} ${divisions} ${inBeat - divisions * step}`;
}

/**
 * Parses a length typed as `bars beats divisions ticks` (each from 0; a single number is ticks, as Logic reads a
 * bare length field's last value; two or three fields are the leading ones) into ticks, or null.
 */
export function parseLength(text: string, meter: MeterMap, at = 0, format: PositionFormat = {}): number | null {
  const fields = splitFields(text);
  if (!fields || fields.length === 0 || fields.length > 4 || fields.some(f => f < 0)) {
    return null;
  }
  if (fields.length === 1) {
    return fields[0] ?? null;
  }
  const [bars = 0, beats = 0, divisions = 0, ticks = 0] = fields;
  const barTicks = meter.barTicksAt(Math.max(0, at));
  const beatTicks = meter.beatTicksAt(Math.max(0, at));
  const step = divisionTicks(beatTicks, format.division ?? 16);
  return Math.round(bars * barTicks + beats * beatTicks + divisions * step + ticks);
}

function splitFields(text: string): number[] | null {
  const parts = text
    .trim()
    .split(/[\s.:,]+/)
    .filter(p => p !== '');
  const numbers = parts.map(p => (/^\d+$/.test(p) ? Number(p) : NaN));
  return numbers.some(n => Number.isNaN(n)) ? null : numbers;
}
