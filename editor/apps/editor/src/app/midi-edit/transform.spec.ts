import type { ControlEvent, Note, SignatureEvent, TransformSet } from '@anode/api-client';
import { MeterMap } from '@anode/timeline';
import { loadVectors } from './shared-vectors';
import {
  applyTransform,
  fitPresetToSelection,
  identityMap,
  roundAway,
  SplitMix64,
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
    expect(velocity('exponent', 2)).toBe(33);
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
    expect(new SplitMix64(42, 1).next()).toBe(new SplitMix64(42, 1).next());
    expect(new SplitMix64(42, 1).next()).not.toBe(new SplitMix64(42, 2).next());
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
      'factory.lengthLimits',
      'factory.randomVelocity',
      'factory.velocityCompress'
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

  it('rounds half away from zero, as the Model does', () => {
    expect([0.5, 1.5, 2.5, -0.5, -1.5, 2.4, 2.6].map(roundAway)).toEqual([1, 2, 3, -1, -2, 2, 3]);
  });
});

/**
 * Parity with the Model's TransformEngine (workstream E): engine/tests/Anode.Model.Tests/TransformVectors.json, when
 * the file is there. Each case applies its set to the file's notes and controls (under its signatures, else 4/4) and
 * expects every event back in order and the matching ids in input order; `draws` pins SplitMix64 outputs.
 */
interface TransformVectors {
  notes: Note[];
  controls: ControlEvent[];
  draws: { seed: number; id: number; values: string[] }[];
  cases: {
    name: string;
    set: TransformSet;
    signatures?: SignatureEvent[];
    expected: { notes: Note[]; controls: ControlEvent[]; selectedNoteIds: number[]; selectedControlIds: number[] };
  }[];
}

describe('MIDI Transform parity with the Model', async () => {
  const vectors = (await loadVectors('TransformVectors.json')) as TransformVectors | null;

  it.skipIf(vectors === null)('draws as the Model’s SplitMix64 does', () => {
    for (const d of vectors?.draws ?? []) {
      const random = new SplitMix64(d.seed, d.id);
      expect(
        d.values.map(() => random.next().toString()),
        `${d.seed}/${d.id}`
      ).toEqual(d.values);
    }
  });

  it.skipIf(vectors === null)('matches every shared vector', () => {
    for (const v of vectors?.cases ?? []) {
      const meter = new MeterMap(v.signatures?.length ? v.signatures : [{ tick: 0, numerator: 4, denominator: 4 }]);
      const result = applyTransform(vectors?.notes ?? [], vectors?.controls ?? [], { map: null, ...v.set }, meter);
      expect(result, v.name).toEqual(v.expected);
    }
  });

  it.skipIf(vectors === null)('has the Model’s factory presets', () => {
    const presets = (vectors?.cases ?? []).filter(
      c => c.name.startsWith('preset ') && c.set.id.startsWith('factory.') && c.set.seed === 0
    );
    expect(TransformPresets.map(p => ({ ...p, map: undefined }))).toEqual(
      presets.map(c => ({ ...c.set, map: undefined }))
    );
  });
});
