import type { DrumPad, Pattern, PatternPlayMode, PatternRow, PatternRowKind, PatternStep } from '@anode/api-client';
import { clampStepValue, type StepValueMode } from '@anode/audio-ui';
import { noteName } from '../midi/midi-math';
import { defaultRow, MaxRowLength, resizeRow, SixteenthTicks, stepAt } from '../music/pattern';

/** A row rate the Step Sequencer offers: ticks per step (null: the pattern's own). */
export const RowRates: readonly { ticks: number | null; label: string }[] = [
  { ticks: null, label: 'Pattern' },
  { ticks: 960, label: '1/4' },
  { ticks: 480, label: '1/8' },
  { ticks: 320, label: '1/8T' },
  { ticks: 240, label: '1/16' },
  { ticks: 160, label: '1/16T' },
  { ticks: 120, label: '1/32' }
];

export const PlayModes: readonly { value: PatternPlayMode; label: string }[] = [
  { value: 'forward', label: 'Forward' },
  { value: 'reverse', label: 'Reverse' },
  { value: 'pingPong', label: 'Ping-Pong' },
  { value: 'random', label: 'Random' }
];

/** A row rate's label ("1/16", "Pattern"); a rate the menu does not list shows its ticks. */
export function rateLabel(ticks: number | null | undefined): string {
  return RowRates.find(r => r.ticks === (ticks ?? null))?.label ?? `${ticks} ticks`;
}

/**
 * A row's name as the Step Sequencer shows it: a pad's name on a pad row (the DMD's, else the row's), the note's name
 * ("C3") on a note row, "Degree N" on a chord-degree row.
 */
export function rowName(row: Pick<PatternRow, 'kind' | 'note' | 'name'>, pads: readonly DrumPad[] = []): string {
  switch (row.kind) {
    case 'pad':
      return pads.find(p => p.note === row.note)?.name ?? (row.name || noteName(row.note));
    case 'chordDegree':
      return `Degree ${row.note}`;
    default:
      return noteName(row.note);
  }
}

function mapRow(pattern: Pattern, rowId: string, change: (row: PatternRow) => PatternRow): Pattern {
  return { ...pattern, rows: pattern.rows.map(row => (row.id === rowId ? change(row) : row)) };
}

function mapStep(row: PatternRow, index: number, change: (step: PatternStep) => PatternStep): PatternRow {
  return { ...row, steps: row.steps.map((step, i) => (i === index ? change(step) : step)) };
}

/** Turns a step on or off. */
export function toggleStep(pattern: Pattern, rowId: string, index: number): Pattern {
  return mapRow(pattern, rowId, row => mapStep(row, index, step => ({ ...step, on: !step.on })));
}

/** Sets a step's value in a mode (clamped to its range); an edited step is turned on, as in Logic. */
export function setStepValue(
  pattern: Pattern,
  rowId: string,
  index: number,
  mode: StepValueMode,
  value: number
): Pattern {
  return mapRow(pattern, rowId, row =>
    mapStep(row, index, step => ({ ...step, on: true, [mode]: clampStepValue(mode, value) }))
  );
}

/** A row at a new length (1…64): steps kept, new ones off. */
export function setRowLength(pattern: Pattern, rowId: string, length: number): Pattern {
  return mapRow(pattern, rowId, row => resizeRow(row, length));
}

export function setRowRate(pattern: Pattern, rowId: string, stepTicks: number | null): Pattern {
  return mapRow(pattern, rowId, row => ({ ...row, stepTicks }));
}

export function setRowPlayMode(pattern: Pattern, rowId: string, playMode: PatternPlayMode): Pattern {
  return mapRow(pattern, rowId, row => ({ ...row, playMode }));
}

export function setRowMuted(pattern: Pattern, rowId: string, muted: boolean): Pattern {
  return mapRow(pattern, rowId, row => ({ ...row, muted }));
}

/** What a row plays: a note (0…127), a chord degree (1…7) or a pad (by its note); its name follows. */
export function setRowNote(
  pattern: Pattern,
  rowId: string,
  note: number,
  pads: readonly DrumPad[] = [],
  kind?: PatternRowKind
): Pattern {
  return mapRow(pattern, rowId, row => {
    const k = kind ?? row.kind;
    const n =
      k === 'chordDegree' ? Math.min(7, Math.max(1, Math.round(note))) : Math.min(127, Math.max(0, Math.round(note)));
    return { ...row, kind: k, note: n, name: rowName({ kind: k, note: n, name: row.name }, pads) };
  });
}

export function addRow(pattern: Pattern, row: PatternRow): Pattern {
  return { ...pattern, rows: [...pattern.rows, row] };
}

export function removeRow(pattern: Pattern, rowId: string): Pattern {
  return { ...pattern, rows: pattern.rows.filter(row => row.id !== rowId) };
}

/** A new row's id, unique in the pattern. */
export function newRowId(pattern: Pattern): string {
  const ids = new Set(pattern.rows.map(r => r.id));
  for (let n = pattern.rows.length + 1; ; n++) {
    const id = `row_${n}`;
    if (!ids.has(id)) {
      return id;
    }
  }
}

/** A new row of a kind on a note (a pad's note, a pitch, a degree), 16 steps or the pattern's longest row. */
export function newRow(
  pattern: Pattern,
  kind: PatternRowKind,
  note: number,
  pads: readonly DrumPad[] = []
): PatternRow {
  const length = Math.max(16, ...pattern.rows.map(r => r.length));
  return defaultRow(
    newRowId(pattern),
    kind,
    note,
    rowName({ kind, note, name: '' }, pads),
    Math.min(MaxRowLength, length)
  );
}

/** The pads of a Drum Machine Designer no pad row plays yet. */
export function unusedPads(pattern: Pattern, pads: readonly DrumPad[]): DrumPad[] {
  const used = new Set(pattern.rows.filter(r => r.kind === 'pad').map(r => r.note));
  return pads.filter(p => !used.has(p.note));
}

/** Sets swing (0…1). */
export function setSwing(pattern: Pattern, swing: number): Pattern {
  return { ...pattern, swing: Math.round(Math.min(1, Math.max(0, swing)) * 100) / 100 };
}

// ---- templates

/** GM drum notes the templates write to (Logic's Drum Machine Designer layout). */
const Kick = [36];
const Snare = [38, 40];
const ClosedHat = [42];

/** A pattern template: which steps (of 16) each GM part plays; Clear All turns every step off. */
export interface PatternTemplate {
  id: string;
  label: string;
  parts: { notes: readonly number[]; name: string; steps: (i: number) => boolean }[];
}

export const PatternTemplates: readonly PatternTemplate[] = [
  { id: 'four', label: 'Four on the Floor', parts: [{ notes: Kick, name: 'Kick', steps: i => i % 4 === 0 }] },
  {
    id: 'backbeat',
    label: 'Backbeat',
    parts: [
      { notes: Kick, name: 'Kick', steps: i => i % 16 === 0 || i % 16 === 8 },
      { notes: Snare, name: 'Snare', steps: i => i % 8 === 4 }
    ]
  },
  { id: 'hats8', label: 'Hats 8ths', parts: [{ notes: ClosedHat, name: 'Closed Hat', steps: i => i % 2 === 0 }] },
  { id: 'hats16', label: 'Hats 16ths', parts: [{ notes: ClosedHat, name: 'Closed Hat', steps: () => true }] },
  { id: 'clear', label: 'Clear All', parts: [] }
];

/**
 * A template applied: each part writes its steps over the row playing its note (on a DMD, a pad row; else a note row),
 * adding that row when there is none (on a DMD only when it has the pad). Clear All turns every step off.
 */
export function applyTemplate(pattern: Pattern, template: PatternTemplate, pads: readonly DrumPad[] | null): Pattern {
  if (template.parts.length === 0) {
    return {
      ...pattern,
      rows: pattern.rows.map(row => ({ ...row, steps: row.steps.map(step => ({ ...step, on: false })) }))
    };
  }
  let next = pattern;
  for (const part of template.parts) {
    const kind: PatternRowKind = pads ? 'pad' : 'note';
    let row = part.notes.map(n => next.rows.find(r => r.kind === kind && r.note === n)).find(r => r !== undefined);
    if (!row) {
      const note = pads ? part.notes.find(n => pads.some(p => p.note === n)) : part.notes[0];
      if (note === undefined) {
        continue;
      }
      row = { ...newRow(next, kind, note, pads ?? []), ...(pads ? {} : { name: part.name }) };
      next = addRow(next, row);
    }
    next = mapRow(next, row.id, r => ({
      ...r,
      steps: r.steps.map((step, i) => ({ ...step, on: part.steps(i) }))
    }));
  }
  return next;
}

// ---- randomise

/** Mulberry32: a small, fast seeded generator, uniform in [0, 1). */
export function seededRandom(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4_294_967_296;
  };
}

export type RandomiseWhat = 'steps' | 'velocity';

/**
 * Randomises the steps (on or off, about a third on) or the velocities (40…127) of every row, or of one row, from a
 * seed: the same seed gives the same pattern.
 */
export function randomise(pattern: Pattern, what: RandomiseWhat, seed: number, rowId: string | null = null): Pattern {
  const random = seededRandom(seed);
  return {
    ...pattern,
    rows: pattern.rows.map(row =>
      rowId !== null && row.id !== rowId
        ? row
        : {
            ...row,
            steps: row.steps.map(step =>
              what === 'steps'
                ? { ...step, on: random() < 0.35 }
                : { ...step, velocity: 40 + Math.floor(random() * 88) }
            )
          }
    )
  };
}

// ---- recording

/**
 * Step record: a note lights step `cursor` of the row playing it (a pad row on a DMD with that pad, else a note row),
 * adding a row when none does. Returns the pattern and the row; null when a DMD has no pad for the note.
 */
export function recordStep(
  pattern: Pattern,
  pitch: number,
  velocity: number,
  cursor: number,
  pads: readonly DrumPad[] | null
): { pattern: Pattern; rowId: string } | null {
  const kind: PatternRowKind = pads ? 'pad' : 'note';
  let next = pattern;
  let row = next.rows.find(r => r.kind === kind && r.note === pitch);
  if (!row) {
    if (pads && !pads.some(p => p.note === pitch)) {
      return null;
    }
    row = newRow(next, kind, pitch, pads ?? []);
    next = addRow(next, row);
  }
  const index = cursor % row.length;
  next = mapRow(next, row.id, r =>
    mapStep(r, index, step => ({ ...step, on: true, velocity: Math.min(127, Math.max(1, Math.round(velocity))) }))
  );
  return { pattern: next, rowId: row.id };
}

/** The step a row plays at `tick` from the region's start, in its rate and play mode; null before the start. */
export function stepAtTick(row: PatternRow, pattern: Pattern, tick: number): number | null {
  if (tick < 0) {
    return null;
  }
  const stepTicks = row.stepTicks ?? pattern.stepTicks;
  if (!(stepTicks > 0)) {
    return null;
  }
  return stepAt(row, Math.floor(tick / stepTicks));
}

/** Live record: a note heard at `tick` (from the region's start) lights the step under the playhead of its row. */
export function recordLive(
  pattern: Pattern,
  pitch: number,
  velocity: number,
  tick: number,
  pads: readonly DrumPad[] | null
): Pattern | null {
  const kind: PatternRowKind = pads ? 'pad' : 'note';
  const existing = pattern.rows.find(r => r.kind === kind && r.note === pitch);
  const row = existing ?? (pads && !pads.some(p => p.note === pitch) ? null : newRow(pattern, kind, pitch, pads ?? []));
  if (!row) {
    return null;
  }
  // The step nearest the note (a note just before a step lands on it), in the row's play order.
  const stepTicks = row.stepTicks ?? pattern.stepTicks;
  const index = stepAtTick(row, pattern, Math.max(0, tick + stepTicks / 2));
  if (index === null) {
    return null;
  }
  const next = existing ? pattern : addRow(pattern, row);
  return mapRow(next, row.id, r =>
    mapStep(r, index, step => ({ ...step, on: true, velocity: Math.min(127, Math.max(1, Math.round(velocity))) }))
  );
}

/** A pattern's step grid: how many 16ths a step of the pattern is (for the beat lines). */
export function stepsPerBeat(pattern: Pattern): number {
  return Math.max(1, Math.round(960 / (pattern.stepTicks || SixteenthTicks)));
}
