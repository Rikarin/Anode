import type { GrooveTemplate, Note } from '@anode/api-client';
import { type GrooveOnset, grooveFromNotes, grooveFromOnsets, grooveTarget, quantizeToGroove } from './groove';
import { loadVectors } from './shared-vectors';

function note(id: number, startTick: number, velocity = 100): Note {
  return { id, pitch: 36, velocity, channel: 1, startTick, lengthTicks: 120, articulationId: 0 };
}

describe('groove templates (hand-worked from docs/08-phase-7.md)', () => {
  // A swung bar of eighths: every second eighth 80 ticks late, the off-beats softer.
  const swung = Array.from({ length: 8 }, (_, i) => note(i + 1, i * 480 + (i % 2 ? 80 : 0), i % 2 ? 64 : 127));

  it('takes the strongest onset per grid step, with velocity scales relative to the strongest', () => {
    const groove = grooveFromNotes('grv_1', 'Swing', swung, 0, 3840, 480);
    expect(groove).toMatchObject({ id: 'grv_1', name: 'Swing', lengthTicks: 3840, stepTicks: 480 });
    expect(groove.points.map(p => p.tick)).toEqual([0, 560, 960, 1520, 1920, 2480, 2880, 3440]);
    expect(groove.points[0]?.velocityScale).toBe(1);
    expect(groove.points[1]?.velocityScale).toBeCloseTo(64 / 127, 9);
  });

  it('keeps the grid position at scale 1 for a step without an onset, and the stronger of two', () => {
    const groove = grooveFromOnsets(
      'grv_2',
      'Sparse',
      [
        { tick: 1000 + 10, strength: 0.2 },
        { tick: 1000 + 30, strength: 0.9 },
        { tick: 1000 + 480 * 3 - 50, strength: 0.45 }
      ],
      1000,
      1920,
      480
    );
    expect(groove.points).toEqual([
      { tick: 30, velocityScale: 1 },
      { tick: 480, velocityScale: 1 },
      { tick: 960, velocityScale: 1 },
      { tick: 1390, velocityScale: 0.5 }
    ]);
  });

  it('moves a tick to its nearest step’s template position, repeating from the project start, by the strength', () => {
    const groove = grooveFromNotes('grv_1', 'Swing', swung, 0, 3840, 480);
    expect(grooveTarget(groove, 480, 1)).toEqual({ tick: 560, velocityScale: 64 / 127 });
    expect(grooveTarget(groove, 3840 + 470, 1).tick).toBe(3840 + 560);
    expect(grooveTarget(groove, 480, 0.5).tick).toBe(520);
    expect(grooveTarget(groove, 960 + 30, 1).tick).toBe(960);
  });

  it('quantises a straight region’s notes onto the groove, velocities when asked', () => {
    const groove: GrooveTemplate = grooveFromNotes('grv_1', 'Swing', swung, 0, 3840, 480);
    const straight = Array.from({ length: 4 }, (_, i) => note(i + 1, i * 480 - 1920, 100));
    // The region starts at bar 1's second half (tick 1920); its notes are relative to it.
    const quantised = quantizeToGroove(straight, 1920 * 2, groove, 1, true);
    expect(quantised.map(n => n.startTick + 3840)).toEqual([1920, 2480, 2880, 3440]);
    expect(quantised.map(n => n.velocity)).toEqual([100, 50, 100, 50]);
    expect(quantizeToGroove(straight, 3840, groove, 1, false).map(n => n.velocity)).toEqual([100, 100, 100, 100]);
    const some = quantizeToGroove(straight, 3840, groove, 1, false, new Set([2]));
    expect(some.map(n => n.startTick)).toEqual([-1920, -1440 + 80, -960, -480]);
  });
});

/**
 * Parity with the Model's Grooves (workstream E): engine/tests/Anode.Model.Tests/GrooveVectors.json, when the file is
 * there: extraction from onsets and from notes, targets, and quantise (velocity scales to 1e-12).
 */
interface GrooveVectors {
  grooves: GrooveTemplate[];
  extract: {
    name: string;
    onsets: GrooveOnset[];
    startTick: number;
    lengthTicks: number;
    stepTicks: number;
    expected: GrooveTemplate;
  }[];
  fromNotes: {
    name: string;
    notes: Note[];
    startTick: number;
    lengthTicks: number;
    stepTicks: number;
    expected: GrooveTemplate;
  }[];
  targets: {
    name: string;
    grooveId: string;
    tick: number;
    strength: number;
    expectedTick: number;
    expectedVelocityScale: number;
  }[];
  quantize: {
    name: string;
    groove: GrooveTemplate;
    regionStartTick: number;
    strength: number;
    velocity: boolean;
    notes: Note[];
    expected: Note[];
  }[];
}

function expectGroove(actual: GrooveTemplate, expected: GrooveTemplate, name: string): void {
  expect({ ...actual, points: actual.points.map(p => p.tick) }, name).toEqual({
    ...expected,
    points: expected.points.map(p => p.tick)
  });
  actual.points.forEach((p, i) => {
    expect(Math.abs(p.velocityScale - (expected.points[i]?.velocityScale ?? NaN)), name).toBeLessThan(1e-12);
  });
}

describe('Groove parity with the Model', async () => {
  const vectors = (await loadVectors('GrooveVectors.json')) as GrooveVectors | null;

  it.skipIf(vectors === null)('extracts as the Model does', () => {
    for (const v of vectors?.extract ?? []) {
      const actual = grooveFromOnsets(
        v.expected.id,
        v.expected.name,
        v.onsets,
        v.startTick,
        v.lengthTicks,
        v.stepTicks
      );
      expectGroove(actual, v.expected, v.name);
    }
    for (const v of vectors?.fromNotes ?? []) {
      const actual = grooveFromNotes(v.expected.id, v.expected.name, v.notes, v.startTick, v.lengthTicks, v.stepTicks);
      expectGroove(actual, v.expected, v.name);
    }
  });

  it.skipIf(vectors === null)('targets and quantises as the Model does', () => {
    for (const v of vectors?.targets ?? []) {
      const groove = vectors?.grooves.find(g => g.id === v.grooveId);
      expect(groove, v.name).toBeDefined();
      if (groove) {
        const target = grooveTarget(groove, v.tick, v.strength);
        expect(target.tick, v.name).toBe(v.expectedTick);
        expect(Math.abs(target.velocityScale - v.expectedVelocityScale), v.name).toBeLessThan(1e-12);
      }
    }
    for (const v of vectors?.quantize ?? []) {
      expect(quantizeToGroove(v.notes, v.regionStartTick, v.groove, v.strength, v.velocity), v.name).toEqual(
        v.expected
      );
    }
  });
});
