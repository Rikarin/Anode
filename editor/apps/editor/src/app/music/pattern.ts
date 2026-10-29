import type {
  KeyEvent,
  Pattern,
  PatternPlayMode,
  PatternRegion,
  PatternRow,
  PatternRowKind,
  PatternStep
} from '@anode/api-client';

/** Ticks per 16th note (the default step). */
export const SixteenthTicks = 240;
/** The most steps a row has. */
export const MaxRowLength = 64;
/** The most repeats (ratchets) a step has. */
export const MaxRepeats = 8;
/** The longest gate, in steps. */
export const MaxGate = 16;

/** A note a pattern plays, in project ticks. */
export interface PatternNote {
  pitch: number;
  startTick: number;
  lengthTicks: number;
  velocity: number;
  /** The row that played it (its index), for drawing. */
  row: number;
}

/** A step as the Step Sequencer adds it: off, velocity 100, a whole-step gate, no repeats, always, on the grid. */
export function defaultStep(on = false): PatternStep {
  return { on, velocity: 100, gate: 1, repeats: 1, chance: 1, offset: 0 };
}

/** A row of `length` off steps. */
export function defaultRow(id: string, kind: PatternRowKind, note: number, name: string, length = 16): PatternRow {
  return {
    id,
    kind,
    note,
    name,
    length,
    stepTicks: null,
    playMode: 'forward',
    muted: false,
    steps: Array.from({ length }, () => defaultStep())
  };
}

/** A row at a new length: steps kept, new ones off, extra ones dropped. */
export function resizeRow(row: PatternRow, length: number): PatternRow {
  const n = Math.min(MaxRowLength, Math.max(1, Math.round(length)));
  return {
    ...row,
    length: n,
    steps: Array.from({ length: n }, (_, i) => row.steps[i] ?? defaultStep())
  };
}

/** A row's step length in ticks: its own rate, else the pattern's. */
export function rowStepTicks(row: PatternRow, pattern: Pattern): number {
  return row.stepTicks ?? pattern.stepTicks;
}

/** How many positions a row's pass takes: its length, or a ping-pong's 2·length − 2 (the ends are not repeated). */
export function cycleLength(row: Pick<PatternRow, 'length' | 'playMode'>): number {
  return row.playMode === 'pingPong' && row.length > 1 ? 2 * row.length - 2 : row.length;
}

// ---- the deterministic generator

/** FNV-1a over a string's UTF-16 code units, then MurmurHash3's 32-bit finaliser. */
function hash32(text: string): number {
  let h = 0x811c9dc5;
  for (let i = 0; i < text.length; i++) {
    h ^= text.charCodeAt(i);
    h = Math.imul(h, 0x01000193);
  }
  h ^= h >>> 16;
  h = Math.imul(h, 0x85ebca6b);
  h ^= h >>> 13;
  h = Math.imul(h, 0xc2b2ae35);
  h ^= h >>> 16;
  return h >>> 0;
}

/**
 * The pattern generator's draw for (seed, region, row, step, pass), uniform in [0, 1), as the Model's PatternUnroller
 * (engine/tests/Anode.Model.Tests/PatternVectors.json): FNV-1a 32 over the UTF-16 units of `"seed:region:row:step:pass"`,
 * finalised as MurmurHash3's fmix32, over 2^32. Chance draws step `k mod L` on pass `k div L` at position k; a random
 * row draws step `"r"` on pass k. The same project always gives the same notes; a reseed changes `seed`.
 */
export function patternDraw(
  seed: number,
  regionId: string,
  rowId: string,
  step: number | string,
  pass: number
): number {
  return hash32(`${seed}:${regionId}:${rowId}:${step}:${pass}`) / 4_294_967_296;
}

/** The step a row plays at position `position` (0-based, from the region's start) in its play mode. */
export function stepAt(
  row: Pick<PatternRow, 'length' | 'playMode' | 'id'>,
  position: number,
  draw: (step: string, pass: number) => number = () => 0
): number {
  const length = row.length;
  if (length <= 1) {
    return 0;
  }
  const mode: PatternPlayMode = row.playMode;
  switch (mode) {
    case 'reverse':
      return length - 1 - (position % length);
    case 'pingPong': {
      const k = position % (2 * length - 2);
      return k < length ? k : 2 * length - 2 - k;
    }
    case 'random':
      return Math.min(length - 1, Math.floor(draw('r', position) * length));
    default:
      return position % length;
  }
}

// ---- keys and chords

const Major = [0, 2, 4, 5, 7, 9, 11];
const Minor = [0, 2, 3, 5, 7, 8, 10];

/** The key at a tick: the last key event at or before it (C major without any). */
export function keyAt(keys: readonly KeyEvent[], tick: number): KeyEvent {
  let found: KeyEvent = { tick: 0, tonic: 0, mode: 'major' };
  for (const key of [...keys].sort((a, b) => a.tick - b.tick)) {
    if (key.tick <= tick) {
      found = key;
    }
  }
  return found;
}

/**
 * A chord-degree row's notes: the triad of the key's scale on degree `degree` (1…7), its root in the octave from
 * MIDI note 60, the third and fifth stacked above it.
 */
export function chordDegreeNotes(degree: number, key: Pick<KeyEvent, 'tonic' | 'mode'>): number[] {
  const scale = key.mode === 'minor' ? Minor : Major;
  const d = Math.min(7, Math.max(1, Math.round(degree))) - 1;
  const pc = (i: number) => (key.tonic + (scale[(d + i) % 7] ?? 0)) % 12;
  const root = 60 + pc(0);
  const up = (from: number, pitchClass: number) => from + ((pitchClass - (from % 12) + 12) % 12 || 12);
  const third = up(root, pc(2));
  const fifth = up(third, pc(4));
  return [root, third, fifth];
}

// ---- unrolling

export interface UnrollOptions {
  /** The project's key track (for chord-degree rows); C major when left out. */
  keys?: readonly KeyEvent[];
}

/**
 * The notes a pattern region plays, unrolled over its length (docs/07-phase-6.md, "Pattern regions"), in project
 * ticks, sorted by start then pitch:
 * - every row cycles from the region's start, at its own step length, independently of the others;
 * - a row's pass is its cycle (its length, or 2·length − 2 for ping-pong); a step with chance under 1 plays on a pass
 *   when {@link patternDraw} for (seed, region, row, step, pass) is under its chance;
 * - offset moves a step by that fraction of a step; swing delays a step that starts on an even 16th (the second, fourth
 *   … from the region's start) by swing × half a 16th;
 * - the gate is the note's length in steps; repeats split it evenly into that many notes;
 * - notes are whole ticks (rounded), start within the region (an early offset at the start is clamped to it) and end at
 *   its end at the latest; muted rows and the region's mute play nothing.
 */
export function unrollPattern(
  region: Pick<PatternRegion, 'id' | 'startTick' | 'lengthTicks' | 'pattern'> & { muted?: boolean },
  options: UnrollOptions = {}
): PatternNote[] {
  const notes: PatternNote[] = [];
  const { pattern } = region;
  const length = region.lengthTicks;
  if (region.muted || length <= 0) {
    return notes;
  }
  pattern.rows.forEach((row, rowIndex) => {
    if (row.muted || row.length < 1) {
      return;
    }
    const stepTicks = rowStepTicks(row, pattern);
    if (!(stepTicks > 0)) {
      return;
    }
    const draw = (step: number | string, pass: number) => patternDraw(pattern.seed, region.id, row.id, step, pass);
    for (let position = 0; position * stepTicks < length; position++) {
      const index = stepAt(row, position, draw);
      const step = row.steps[index];
      if (!step?.on) {
        continue;
      }
      if (step.chance < 1 && !(draw(position % row.length, Math.floor(position / row.length)) < step.chance)) {
        continue;
      }
      const nominal = position * stepTicks;
      let start = nominal + step.offset * stepTicks;
      if (pattern.swing > 0 && nominal % (2 * SixteenthTicks) === SixteenthTicks) {
        start += pattern.swing * (SixteenthTicks / 2);
      }
      start = Math.max(0, start);
      const gate = step.gate * stepTicks;
      const repeats = Math.max(1, Math.round(step.repeats));
      const each = gate / repeats;
      const pitches =
        row.kind === 'chordDegree'
          ? chordDegreeNotes(row.note, keyAt(options.keys ?? [], region.startTick + Math.max(0, Math.round(start))))
          : [row.note];
      for (let r = 0; r < repeats; r++) {
        const from = Math.round(start + r * each);
        const to = Math.min(length, Math.max(from + 1, Math.round(start + (r + 1) * each)));
        if (from >= length) {
          continue;
        }
        for (const pitch of pitches) {
          notes.push({
            pitch: Math.min(127, Math.max(0, pitch)),
            startTick: region.startTick + from,
            lengthTicks: to - from,
            velocity: Math.min(127, Math.max(1, Math.round(step.velocity))),
            row: rowIndex
          });
        }
      }
    }
  });
  return notes.sort((a, b) => a.startTick - b.startTick || a.pitch - b.pitch || a.row - b.row);
}

/** Whether a pattern passes the Server's `setPattern` checks (docs/07-phase-6.md); the first problem otherwise. */
export function patternProblem(pattern: Pattern): string | null {
  if (!(pattern.swing >= 0 && pattern.swing <= 1)) {
    return 'Swing is 0 to 1.';
  }
  if (!(pattern.stepTicks > 0)) {
    return 'A step needs a length.';
  }
  const ids = new Set<string>();
  for (const row of pattern.rows) {
    if (ids.has(row.id)) {
      return `Row ids must be unique (${row.id}).`;
    }
    ids.add(row.id);
    if (!Number.isInteger(row.length) || row.length < 1 || row.length > MaxRowLength) {
      return `A row has 1 to ${MaxRowLength} steps.`;
    }
    if (row.steps.length !== row.length) {
      return `Row ${row.name} has ${row.steps.length} steps, not ${row.length}.`;
    }
    if (row.stepTicks !== null && row.stepTicks !== undefined && !(row.stepTicks > 0)) {
      return 'A row rate needs a length.';
    }
    if (!(row.note >= 0 && row.note <= 127)) {
      return 'A row plays a note 0 to 127.';
    }
    for (const step of row.steps) {
      if (
        !(step.velocity >= 1 && step.velocity <= 127) ||
        !(step.gate > 0 && step.gate <= MaxGate) ||
        !(Number.isInteger(step.repeats) && step.repeats >= 1 && step.repeats <= MaxRepeats) ||
        !(step.chance >= 0 && step.chance <= 1) ||
        !(step.offset >= -0.5 && step.offset <= 0.5)
      ) {
        return 'A step value is out of its range.';
      }
    }
  }
  return null;
}
