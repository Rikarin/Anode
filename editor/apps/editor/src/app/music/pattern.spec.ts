import type { KeyEvent, Pattern, PatternRegion, PatternRow, PatternStep } from '@anode/api-client';
import {
  chordDegreeNotes,
  cycleLength,
  defaultRow,
  defaultStep,
  patternDraw,
  patternProblem,
  resizeRow,
  stepAt,
  unrollPattern
} from './pattern';

const Bar = 3840;

function row(on: number[], overrides: Partial<PatternRow> = {}, step: Partial<PatternStep> = {}): PatternRow {
  const base = defaultRow(overrides.id ?? 'r1', 'note', 60, 'C3', overrides.length ?? 16);
  return {
    ...base,
    ...overrides,
    steps: base.steps.map((_, i) => ({ ...defaultStep(on.includes(i)), ...(on.includes(i) ? step : {}) }))
  };
}

function region(rows: PatternRow[], overrides: Partial<Pattern> = {}, lengthTicks = Bar): PatternRegion {
  return {
    id: 'pat_1',
    trackId: 'trk_1',
    name: 'Pattern',
    startTick: 0,
    lengthTicks,
    muted: false,
    pattern: { rows, swing: 0, seed: 1, stepTicks: 240, ...overrides }
  };
}

const starts = (r: PatternRegion, keys?: KeyEvent[]) => unrollPattern(r, keys ? { keys } : {}).map(n => n.startTick);

describe('Step Sequencer patterns (hand-worked from docs/07-phase-6.md)', () => {
  it('plays a four-on-the-floor row of 16ths: a note per lit step, a step long', () => {
    const notes = unrollPattern(region([row([0, 4, 8, 12])]));
    expect(notes.map(n => [n.startTick, n.lengthTicks, n.pitch, n.velocity])).toEqual([
      [0, 240, 60, 100],
      [960, 240, 60, 100],
      [1920, 240, 60, 100],
      [2880, 240, 60, 100]
    ]);
  });

  it('places notes after the region start, and plays nothing when muted', () => {
    const r = { ...region([row([0])]), startTick: 7680 };
    expect(starts(r)).toEqual([7680]);
    expect(unrollPattern({ ...r, muted: true })).toEqual([]);
    expect(unrollPattern(region([row([0], { muted: true })]))).toEqual([]);
  });

  it('cycles rows of different lengths independently (a 7-step hat against 16 steps)', () => {
    const hat = row([0], { id: 'hat', length: 7, note: 42 });
    const kick = row([0], { id: 'kick', note: 36 });
    const notes = unrollPattern(region([kick, hat]));
    expect(notes.filter(n => n.pitch === 42).map(n => n.startTick)).toEqual([0, 7 * 240, 14 * 240]);
    expect(notes.filter(n => n.pitch === 36).map(n => n.startTick)).toEqual([0]);
    // Over two bars the hat keeps cycling from the region start, not from the bar.
    const twoBars = unrollPattern(region([hat], {}, 2 * Bar)).map(n => n.startTick / 240);
    expect(twoBars).toEqual([0, 7, 14, 21, 28]);
  });

  it('walks ping-pong without repeating the end steps: 0 1 2 3 2 1 0 1 …', () => {
    expect(cycleLength({ length: 4, playMode: 'pingPong' })).toBe(6);
    const order = Array.from({ length: 13 }, (_, i) => stepAt({ id: 'r', length: 4, playMode: 'pingPong' }, i));
    expect(order).toEqual([0, 1, 2, 3, 2, 1, 0, 1, 2, 3, 2, 1, 0]);
    expect(starts(region([row([3], { length: 4, playMode: 'pingPong' })])).map(t => t / 240)).toEqual([3, 9, 15]);
    expect(starts(region([row([2], { length: 4, playMode: 'pingPong' })])).map(t => t / 240)).toEqual([
      2, 4, 8, 10, 14
    ]);
  });

  it('walks reverse from the last step, and a one-step row plays every step', () => {
    expect(starts(region([row([0], { length: 4, playMode: 'reverse' })])).map(t => t / 240)).toEqual([3, 7, 11, 15]);
    expect(starts(region([row([0], { length: 1, playMode: 'pingPong' })])).length).toBe(16);
  });

  it('draws random steps from the generator: the same notes every time, and a step of the row each time', () => {
    const r = region([row([1, 5], { length: 8, playMode: 'random' })]);
    const first = starts(r);
    expect(starts(r)).toEqual(first);
    for (let i = 0; i < 16; i++) {
      const s = stepAt({ id: 'r1', length: 8, playMode: 'random' }, i, (step, pass) =>
        patternDraw(1, 'pat_1', 'r1', step, pass)
      );
      expect(s).toBeGreaterThanOrEqual(0);
      expect(s).toBeLessThan(8);
    }
  });

  it('delays even 16ths by swing × half a 16th', () => {
    const r = region([row([0, 1, 2, 3])], { swing: 1 });
    expect(starts(r)).toEqual([0, 240 + 120, 480, 720 + 120]);
    expect(starts(region([row([1])], { swing: 0.5 }))).toEqual([240 + 60]);
  });

  it('moves a step by its offset (−50 … +50 % of a step), clamped to the region start', () => {
    expect(starts(region([row([4], {}, { offset: 0.5 })]))).toEqual([960 + 120]);
    expect(starts(region([row([4], {}, { offset: -0.25 })]))).toEqual([960 - 60]);
    const early = unrollPattern(region([row([0], {}, { offset: -0.5 })]));
    expect(early.map(n => [n.startTick, n.lengthTicks])).toEqual([[0, 120]]);
  });

  it('lengthens by the gate (in steps), ties over steps, and ends at the region end', () => {
    expect(unrollPattern(region([row([0], {}, { gate: 0.5 })]))[0]?.lengthTicks).toBe(120);
    expect(unrollPattern(region([row([0], {}, { gate: 2.5 })]))[0]?.lengthTicks).toBe(600);
    expect(unrollPattern(region([row([15], {}, { gate: 4 })]))[0]?.lengthTicks).toBe(240);
  });

  it('splits the gate evenly into repeats (ratchets)', () => {
    const notes = unrollPattern(region([row([0], {}, { repeats: 3 })]));
    expect(notes.map(n => [n.startTick, n.lengthTicks])).toEqual([
      [0, 80],
      [80, 80],
      [160, 80]
    ]);
    const tied = unrollPattern(region([row([0], {}, { repeats: 4, gate: 2 })]));
    expect(tied.map(n => n.startTick)).toEqual([0, 120, 240, 360]);
  });

  it('plays a step with chance 0 never, 1 always, and between deterministically by (seed, region, row, step, pass)', () => {
    expect(unrollPattern(region([row([0, 1, 2, 3], {}, { chance: 0 })]))).toEqual([]);
    expect(unrollPattern(region([row([0, 1, 2, 3], {}, { chance: 1 })])).length).toBe(4);
    const half = region([row([0], { length: 1 }, { chance: 0.5 })], {}, 64 * 240);
    const once = starts(half);
    expect(starts(half)).toEqual(once);
    expect(once.length).toBeGreaterThan(16);
    expect(once.length).toBeLessThan(48);
    const reseeded = starts({ ...half, pattern: { ...half.pattern, seed: 99 } });
    expect(reseeded).not.toEqual(once);
    expect(patternDraw(1, 'pat_1', 'r1', 0, 0)).toBe(patternDraw(1, 'pat_1', 'r1', 0, 0));
    expect(patternDraw(1, 'pat_1', 'r1', 0, 0)).not.toBe(patternDraw(1, 'pat_1', 'r1', 0, 1));
  });

  it('uses a row rate over the pattern step (8ths against 16ths, triplets)', () => {
    expect(starts(region([row([0, 1], { length: 2, stepTicks: 480 })])).map(t => t / 480)).toEqual([
      0, 1, 2, 3, 4, 5, 6, 7
    ]);
    expect(unrollPattern(region([row([0], { stepTicks: 160 })]))[0]?.lengthTicks).toBe(160);
  });

  it('plays chord degrees as triads of the key at the step', () => {
    expect(chordDegreeNotes(1, { tonic: 0, mode: 'major' })).toEqual([60, 64, 67]);
    expect(chordDegreeNotes(5, { tonic: 0, mode: 'major' })).toEqual([67, 71, 74]);
    // D minor, degree 2: E G B♭.
    expect(chordDegreeNotes(2, { tonic: 2, mode: 'minor' })).toEqual([64, 67, 70]);
    const chords = region([{ ...row([0]), kind: 'chordDegree', note: 1 }]);
    const keys: KeyEvent[] = [{ tick: 0, tonic: 2, mode: 'minor' }];
    expect(unrollPattern(chords, { keys }).map(n => n.pitch)).toEqual([62, 65, 69]);
  });

  it('resizes rows keeping their steps, and checks patterns as the Server does', () => {
    const r = row([0, 15]);
    expect(resizeRow(r, 7).steps.length).toBe(7);
    expect(resizeRow(r, 20).steps[15]?.on).toBe(true);
    expect(resizeRow(r, 99).length).toBe(64);
    expect(patternProblem(region([r]).pattern)).toBeNull();
    expect(patternProblem({ ...region([r]).pattern, swing: 2 })).toMatch(/Swing/);
    expect(patternProblem(region([r, r]).pattern)).toMatch(/unique/);
    expect(patternProblem(region([{ ...r, length: 3 }]).pattern)).toMatch(/steps/);
    expect(patternProblem(region([row([0], {}, { repeats: 9 })]).pattern)).toMatch(/range/);
  });
});

/**
 * Parity with the Model's pattern compiler (workstream B): the shared vectors in
 * engine/tests/Anode.Model.Tests/PatternVectors.json, when the file is there. Each case is a region (and the key
 * track), and the notes it plays in project ticks (pitch, startTick, lengthTicks, velocity), in any order.
 */
interface Vector {
  name: string;
  region: PatternRegion;
  keys?: KeyEvent[];
  notes?: ExpectedNote[];
  expected?: { notes: ExpectedNote[] } | ExpectedNote[];
}

interface ExpectedNote {
  pitch: number;
  startTick?: number;
  tick?: number;
  lengthTicks?: number;
  length?: number;
  velocity: number;
}

async function loadVectors(): Promise<Vector[] | null> {
  const proc = (globalThis as { process?: { cwd(): string; env: Record<string, string | undefined> } }).process;
  if (!proc) {
    return null;
  }
  try {
    const fsName = 'node:fs';
    const fs = (await import(/* @vite-ignore */ fsName)) as {
      existsSync(path: string): boolean;
      readFileSync(path: string, encoding: string): string;
    };
    const root = proc.cwd().replace(/\/editor(\/.*)?$/, '');
    const path = proc.env['ANODE_PATTERN_VECTORS'] ?? `${root}/engine/tests/Anode.Model.Tests/PatternVectors.json`;
    if (!fs.existsSync(path)) {
      return null;
    }
    const parsed = JSON.parse(fs.readFileSync(path, 'utf8')) as Vector[] | { cases: Vector[] };
    return Array.isArray(parsed) ? parsed : parsed.cases;
  } catch {
    return null;
  }
}

function expectedNotes(v: Vector): ExpectedNote[] {
  if (v.notes) {
    return v.notes;
  }
  const e = v.expected;
  return Array.isArray(e) ? e : (e?.notes ?? []);
}

const key = (n: { pitch: number; start: number; length: number; velocity: number }) =>
  `${n.start}:${n.pitch}:${n.length}:${n.velocity}`;

describe('Pattern parity with the Model', async () => {
  const vectors = await loadVectors();
  it.skipIf(vectors === null)('matches every shared vector', () => {
    for (const v of vectors ?? []) {
      const actual = unrollPattern(v.region, v.keys ? { keys: v.keys } : {})
        .map(n => key({ pitch: n.pitch, start: n.startTick, length: n.lengthTicks, velocity: n.velocity }))
        .sort();
      const expected = expectedNotes(v)
        .map(n =>
          key({
            pitch: n.pitch,
            start: n.startTick ?? n.tick ?? 0,
            length: n.lengthTicks ?? n.length ?? 0,
            velocity: n.velocity
          })
        )
        .sort();
      expect(actual, v.name).toEqual(expected);
    }
  });
});
