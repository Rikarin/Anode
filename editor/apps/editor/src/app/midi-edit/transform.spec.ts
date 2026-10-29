import type { ControlEvent, Note, SignatureEvent, TransformSet } from '@anode/api-client';
import { MeterMap } from '@anode/timeline';
import { loadVectors } from './shared-vectors';
import {
  applyTransform,
  fitPresetToSelection,
  identityMap,
  roundEven,
  transformDraw,
  TransformPresets
} from './transform';

function note(id: number, startTick: number, pitch: number, velocity = 80, lengthTicks = 240, channel = 1): Note {
  return { id, pitch, velocity, channel, startTick, lengthTicks, articulationId: 0 };
}

function set(partial: Partial<TransformSet>): TransformSet {
  return {
    id: 'trs_test',
    name: 'Test',
    conditions: [],
    operations: [],
    map: null,
    mode: 'selectAndOperate',
    seed: 7,
    ...partial
  };
}

const notes = [note(1, 0, 48, 60), note(2, 480, 60, 70), note(3, 960, 62, 90), note(4, 1440, 72, 110)];
const controls: ControlEvent[] = [
  { id: 10, kind: 'controlChange', channel: 1, number: 1, value: 64, tick: 240 },
  { id: 11, kind: 'pitchBend', channel: 1, number: 0, value: 0, tick: 720 }
];

describe('MIDI Transform (hand-worked from docs/08-phase-7.md)', () => {
  it('selects by ANDed conditions and leaves events in select-only mode', () => {
    const result = applyTransform(
      notes,
      controls,
      set({
        mode: 'selectOnly',
        conditions: [
          { field: 'status', op: 'equal', a: 144, b: 0 },
          { field: 'pitch', op: 'greaterEqual', a: 61, b: 0 }
        ],
        operations: [{ field: 'velocity', op: 'fix', a: 1, b: 0 }]
      })
    );
    expect(result.selectedNoteIds).toEqual([3, 4]);
    expect(result.selectedControlIds).toEqual([]);
    expect(result.notes).toEqual(notes);
  });

  it('applies the golden path’s Select and Operate: above C4, velocity 100 and a 1/16', () => {
    // C4 in Logic's naming (middle C is C3) is 72; "above" is greater than it.
    const result = applyTransform(
      notes,
      controls,
      set({
        conditions: [
          { field: 'status', op: 'equal', a: 144, b: 0 },
          { field: 'pitch', op: 'greaterEqual', a: 61, b: 0 }
        ],
        operations: [
          { field: 'velocity', op: 'fix', a: 100, b: 0 },
          { field: 'length', op: 'fix', a: 240, b: 0 }
        ]
      })
    );
    expect(result.notes.map(n => [n.id, n.velocity, n.lengthTicks])).toEqual([
      [1, 60, 240],
      [2, 70, 240],
      [3, 100, 240],
      [4, 100, 240]
    ]);
    expect(result.controls).toEqual(controls);
  });

  it('runs every operation as defined on the enum, rounded and clamped', () => {
    const one = [note(1, 1000, 60, 64, 500)];
    const velocity = (op: TransformSet['operations'][number]['op'], a: number, b = 0, map: number[] | null = null) =>
      applyTransform(one, [], set({ operations: [{ field: 'velocity', op, a, b }], map })).notes[0]?.velocity;
    expect(velocity('thru', 5)).toBe(64);
    expect(velocity('fix', 5)).toBe(5);
    expect(velocity('add', 100)).toBe(127);
    expect(velocity('subtract', 100)).toBe(1);
    expect(velocity('min', 80)).toBe(80);
    expect(velocity('max', 30)).toBe(30);
    expect(velocity('flip', 50)).toBe(36);
    expect(velocity('multiply', 1.5)).toBe(96);
    expect(velocity('divide', 3)).toBe(21);
    expect(velocity('divide', 0)).toBe(64);
    expect(velocity('scale', 0.5, 10)).toBe(42);
    expect(velocity('range', 70, 90)).toBe(70);
    expect(velocity('quantize', 10)).toBe(60);
    expect(velocity('exponent', 2)).toBe(32);
    expect(
      velocity(
        'useMap',
        0,
        0,
        identityMap().map(v => 127 - v)
      )
    ).toBe(63);
    const random = velocity('random', 10, 20) ?? 0;
    expect(random).toBeGreaterThanOrEqual(10);
    expect(random).toBeLessThanOrEqual(20);
    const jitter = velocity('plusMinusRandom', 5) ?? 0;
    expect(Math.abs(jitter - 64)).toBeLessThanOrEqual(5);
  });

  it('ramps crescendos over the selection’s positions', () => {
    const result = applyTransform(
      notes,
      [],
      set({ operations: [{ field: 'velocity', op: 'crescendo', a: 40, b: 100 }] })
    );
    expect(result.notes.map(n => n.velocity)).toEqual([40, 60, 80, 100]);
    const relative = applyTransform(
      notes,
      [],
      set({ operations: [{ field: 'velocity', op: 'relativeCrescendo', a: 0, b: 15 }] })
    );
    expect(relative.notes.map(n => n.velocity)).toEqual([60, 75, 100, 125]);
  });

  it('applies operations in field order, whatever their order in the list', () => {
    const result = applyTransform(
      [note(1, 100, 60)],
      [],
      set({
        operations: [
          { field: 'velocity', op: 'fix', a: 99, b: 0 },
          { field: 'position', op: 'quantize', a: 240, b: 0 },
          { field: 'pitch', op: 'add', a: 2, b: 0 }
        ]
      })
    );
    expect(result.notes[0]).toMatchObject({ startTick: 0, pitch: 62, velocity: 99 });
  });

  it('treats a controller’s first data byte as pitch and its value as velocity', () => {
    const result = applyTransform(
      [],
      controls,
      set({
        conditions: [{ field: 'status', op: 'equal', a: 176, b: 0 }],
        operations: [
          { field: 'pitch', op: 'fix', a: 74, b: 0 },
          { field: 'velocity', op: 'add', a: 100, b: 0 }
        ]
      })
    );
    expect(result.controls).toEqual([{ ...controls[0], number: 74, value: 127 }, controls[1]]);
    const bend = applyTransform([], controls, set({ operations: [{ field: 'velocity', op: 'fix', a: -9000, b: 0 }] }));
    expect(bend.controls[1]?.value).toBe(-8192);
  });

  it('tests subposition within the bar, under the signature track', () => {
    const signatures: SignatureEvent[] = [
      { tick: 0, numerator: 4, denominator: 4 },
      { tick: 3840, numerator: 3, denominator: 4 }
    ];
    const meter = new MeterMap(signatures);
    const result = applyTransform(
      [note(1, 960, 60), note(2, 3840, 60), note(3, 3840 + 2880, 60), note(4, 3840 + 960, 60)],
      [],
      set({ mode: 'selectOnly', conditions: [{ field: 'subposition', op: 'equal', a: 0, b: 0 }] }),
      meter
    );
    expect(result.selectedNoteIds).toEqual([2, 3]);
  });

  it('is deterministic: the same seed and ids give the same result; another seed another', () => {
    const humanize = { ...(TransformPresets[0] as TransformSet), seed: 42 };
    const a = applyTransform(notes, [], humanize);
    expect(applyTransform(notes, [], humanize)).toEqual(a);
    expect(applyTransform(notes, [], { ...humanize, seed: 43 }).notes).not.toEqual(a.notes);
    expect(transformDraw(42, 1, 'velocity')).toBe(transformDraw(42, 1, 'velocity'));
    expect(transformDraw(42, 1, 'velocity')).not.toBe(transformDraw(42, 2, 'velocity'));
  });

  it('lists the ten factory presets, and fits the reverse ones to the selection', () => {
    expect(TransformPresets.map(p => p.id)).toEqual([
      'factory.humanize',
      'factory.quantize',
      'factory.crescendo',
      'factory.fixedVelocity',
      'factory.transpose',
      'factory.doubleSpeed',
      'factory.halfSpeed',
      'factory.reversePosition',
      'factory.reversePitch',
      'factory.lengthLimits'
    ]);
    const reverse = fitPresetToSelection(
      TransformPresets.find(p => p.id === 'factory.reversePosition') as TransformSet,
      { notes, controls: [] }
    );
    const result = applyTransform(notes, [], reverse);
    expect(result.notes.map(n => n.startTick)).toEqual([1440, 960, 480, 0]);
    const pitch = fitPresetToSelection(TransformPresets.find(p => p.id === 'factory.reversePitch') as TransformSet, {
      notes,
      controls: []
    });
    expect(applyTransform(notes, [], pitch).notes.map(n => n.pitch)).toEqual([72, 60, 58, 48]);
  });

  it('rounds half to even, as .NET does', () => {
    expect([0.5, 1.5, 2.5, -0.5, -1.5, 2.4, 2.6].map(roundEven)).toEqual([0, 2, 2, 0, -2, 2, 3]);
  });
});

/**
 * Parity with the Model's TransformEngine (workstream E): the shared vectors in
 * engine/tests/Anode.Model.Tests/TransformVectors.json, when the file is there. Each case is a set, the notes and
 * controller events (positions in project ticks) and the signature track, and the expected events and selection.
 */
interface TransformVector {
  name: string;
  set: TransformSet;
  notes?: Note[];
  controls?: ControlEvent[];
  signatures?: SignatureEvent[];
  expected: {
    notes?: (Omit<Note, 'articulationId'> & { articulationId?: number })[];
    controls?: ControlEvent[];
    selectedNoteIds?: number[];
    selectedControlIds?: number[];
  };
}

describe('MIDI Transform parity with the Model', async () => {
  const loaded = await loadVectors('TransformVectors.json');
  const cases: TransformVector[] = Array.isArray(loaded)
    ? (loaded as TransformVector[])
    : ((loaded as { cases?: TransformVector[] } | null)?.cases ?? []);

  it.skipIf(loaded === null)('matches every shared vector', () => {
    for (const v of cases) {
      const meter = new MeterMap(v.signatures?.length ? v.signatures : [{ tick: 0, numerator: 4, denominator: 4 }]);
      const result = applyTransform(v.notes ?? [], v.controls ?? [], v.set, meter);
      if (v.expected.notes) {
        expect(result.notes, v.name).toEqual(
          v.expected.notes.map(n => ({ ...n, articulationId: n.articulationId ?? 0 }))
        );
      }
      if (v.expected.controls) {
        expect(result.controls, v.name).toEqual(v.expected.controls);
      }
      if (v.expected.selectedNoteIds) {
        expect(
          [...result.selectedNoteIds].sort((a, b) => a - b),
          v.name
        ).toEqual([...v.expected.selectedNoteIds].sort((a, b) => a - b));
      }
      if (v.expected.selectedControlIds) {
        expect(
          [...result.selectedControlIds].sort((a, b) => a - b),
          v.name
        ).toEqual([...v.expected.selectedControlIds].sort((a, b) => a - b));
      }
    }
  });
});
