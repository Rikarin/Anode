import type { GrooveTemplate, Note } from '@anode/api-client';
import { grooveFromNotes, grooveFromOnsets, grooveTarget, quantizeToGroove } from './groove';
import { loadVectors, vectorCases } from './shared-vectors';

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
 * Parity with the Model's Grooves (workstream E): the shared vectors in engine/tests/Anode.Model.Tests/GrooveVectors.json,
 * when the file is there. A case is one of: extraction from onsets (`onsets`, `startTick`, `lengthTicks`, `stepTicks`
 * → `expected` template), extraction from notes (`notes` …), or a quantise (`groove`, `notes`, `regionStartTick`,
 * `strength`, `velocity` → `expected` notes).
 */
interface GrooveVector {
  name: string;
  kind?: string;
  onsets?: { tick: number; strength: number }[];
  notes?: Note[];
  startTick?: number;
  lengthTicks?: number;
  stepTicks?: number;
  groove?: GrooveTemplate;
  regionStartTick?: number;
  strength?: number;
  velocity?: boolean;
  expected: GrooveTemplate | Note[] | { notes: Note[] } | { groove: GrooveTemplate };
}

describe('Groove parity with the Model', async () => {
  const loaded = await loadVectors('GrooveVectors.json');
  const cases = vectorCases<GrooveVector>(loaded);

  it.skipIf(loaded === null)('matches every shared vector', () => {
    for (const v of cases) {
      if (v.groove) {
        const expected = Array.isArray(v.expected) ? v.expected : (v.expected as { notes: Note[] }).notes;
        const actual = quantizeToGroove(
          v.notes ?? [],
          v.regionStartTick ?? 0,
          v.groove,
          v.strength ?? 1,
          v.velocity ?? false
        );
        expect(
          actual.map(n => [n.id, n.startTick, n.velocity]),
          v.name
        ).toEqual(expected.map(n => [n.id, n.startTick, n.velocity]));
        continue;
      }
      const expected = 'groove' in v.expected ? v.expected.groove : (v.expected as GrooveTemplate);
      const id = expected.id;
      const name = expected.name;
      const actual = v.onsets
        ? grooveFromOnsets(id, name, v.onsets, v.startTick ?? 0, v.lengthTicks ?? 3840, v.stepTicks ?? 240)
        : grooveFromNotes(id, name, v.notes ?? [], v.startTick ?? 0, v.lengthTicks ?? 3840, v.stepTicks ?? 240);
      expect(actual.lengthTicks, v.name).toBe(expected.lengthTicks);
      expect(actual.stepTicks, v.name).toBe(expected.stepTicks);
      expect(
        actual.points.map(p => p.tick),
        v.name
      ).toEqual(expected.points.map(p => p.tick));
      actual.points.forEach((p, i) =>
        expect(p.velocityScale, v.name).toBeCloseTo(expected.points[i]?.velocityScale ?? 0, 6)
      );
    }
  });
});
