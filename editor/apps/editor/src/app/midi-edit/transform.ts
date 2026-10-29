import type {
  ControlEvent,
  ControlKind,
  Note,
  TransformCondition,
  TransformField,
  TransformOperation,
  TransformSet
} from '@anode/api-client';
import { MeterMap } from '@anode/timeline';

/**
 * The MIDI Transform engine, a TypeScript port of the Model's `TransformEngine` (docs/08-phase-7.md; the rules are
 * the `about` of engine/tests/Anode.Model.Tests/TransformVectors.json, which this port reproduces exactly). The
 * editor runs "select only" itself and the fake Server runs the `transform` command with it.
 *
 * - Conditions are ANDed; `all` matches everything.
 * - P0 and P1 are the least and greatest original positions of the matching events. Position multiply, divide and
 *   scale act relative to P0; flip on position is P0 + P1 + 2a − x; ramps run by original position over P0…P1.
 * - Operations apply in field order (position, length, channel, pitch, velocity), at most one per field; each result
 *   is rounded half away from zero, then clamped: position ≥ 0, a note's length ≥ 1, channel 1…16, pitch 0…127, a
 *   note's velocity 1…127, a pitch bend's −8192…8191, other controllers' 0…127. A controller has no length, and only
 *   control changes and poly pressure have a pitch: an operation on a field an event lacks leaves it.
 * - Randomness: each matching event has its own SplitMix64 (state = seed XOR id × 0xD1B54A32D192ED03); every random
 *   and ±random operation draws once, in field order, even on a field the event lacks.
 */
export interface TransformResult {
  notes: Note[];
  controls: ControlEvent[];
  selectedNoteIds: number[];
  selectedControlIds: number[];
}

/** The field order operations apply in. */
export const TransformFieldOrder: readonly TransformField[] = ['position', 'length', 'channel', 'pitch', 'velocity'];

/** The MIDI status (high nibble) of each controller kind, and of a note (144). */
export const StatusOf: Record<ControlKind | 'note', number> = {
  note: 144,
  polyPressure: 160,
  controlChange: 176,
  programChange: 192,
  channelPressure: 208,
  pitchBend: 224
};

/** Rounds half away from zero (the Model's `MidpointRounding.AwayFromZero`). */
export function roundAway(value: number): number {
  // + 0 turns −0 into 0.
  return Math.sign(value) * Math.round(Math.abs(value)) + 0;
}

const Mask64 = (1n << 64n) - 1n;

/** SplitMix64, the Transform engine's generator (64-bit maths in BigInt). */
export class SplitMix64 {
  private state: bigint;

  constructor(seed: number | bigint, id: number | bigint) {
    const s = BigInt.asUintN(64, BigInt(seed));
    const mixed = (BigInt.asUintN(64, BigInt(id)) * 0xd1b54a32d192ed03n) & Mask64;
    this.state = s ^ mixed;
  }

  next(): bigint {
    this.state = (this.state + 0x9e3779b97f4a7c15n) & Mask64;
    let z = this.state;
    z = ((z ^ (z >> 30n)) * 0xbf58476d1ce4e5b9n) & Mask64;
    z = ((z ^ (z >> 27n)) * 0x94d049bb133111ebn) & Mask64;
    return z ^ (z >> 31n);
  }

  /** An integer in lo…hi: lo + next() mod (hi − lo + 1). */
  between(lo: number, hi: number): number {
    return lo + Number(this.next() % BigInt(hi - lo + 1));
  }
}

/** One event as the engine sees it. */
interface Item {
  kind: ControlKind | 'note';
  id: number;
  position: number;
  length: number;
  channel: number;
  pitch: number;
  velocity: number;
}

type OperatedField = 'position' | 'length' | 'channel' | 'pitch' | 'velocity';

function noteItem(n: Note): Item {
  return {
    kind: 'note',
    id: n.id,
    position: n.startTick,
    length: n.lengthTicks,
    channel: n.channel,
    pitch: n.pitch,
    velocity: n.velocity
  };
}

function controlItem(c: ControlEvent): Item {
  return {
    kind: c.kind,
    id: c.id,
    position: c.tick,
    length: 0,
    channel: c.channel,
    pitch: hasNumber(c.kind) ? c.number : 0,
    velocity: c.value
  };
}

function hasNumber(kind: ControlKind | 'note'): boolean {
  return kind === 'note' || kind === 'controlChange' || kind === 'polyPressure';
}

/** A field's clamp range for an event kind, or null when the event has no such field. */
export function fieldRange(field: TransformField, kind: ControlKind | 'note'): [number, number] | null {
  switch (field) {
    case 'position':
      return [0, Number.MAX_SAFE_INTEGER];
    case 'length':
      return kind === 'note' ? [1, Number.MAX_SAFE_INTEGER] : null;
    case 'channel':
      return [1, 16];
    case 'pitch':
      return hasNumber(kind) ? [0, 127] : null;
    case 'velocity':
      if (kind === 'note') {
        return [1, 127];
      }
      return kind === 'pitchBend' ? [-8192, 8191] : [0, 127];
    case 'status':
    case 'subposition':
      return null;
  }
}

function valueOf(item: Item, field: TransformField, meter: MeterMap): number {
  switch (field) {
    case 'position':
      return item.position;
    case 'length':
      return item.length;
    case 'channel':
      return item.channel;
    case 'pitch':
      return item.pitch;
    case 'velocity':
      return item.velocity;
    case 'status':
      return StatusOf[item.kind];
    case 'subposition': {
      const position = Math.max(0, item.position);
      return item.position - meter.barStart(Math.floor(meter.barAt(position) + 1e-9));
    }
  }
}

/** Whether a value meets a condition. */
export function meets(value: number, condition: TransformCondition): boolean {
  const { a, b } = condition;
  switch (condition.op) {
    case 'all':
      return true;
    case 'equal':
      return value === a;
    case 'unequal':
      return value !== a;
    case 'lessEqual':
      return value <= a;
    case 'greaterEqual':
      return value >= a;
    case 'inside':
      return value >= a && value <= b;
    case 'outside':
      return value < a || value > b;
  }
}

/** What the operations need beyond the event: the selection's extent, the generator, the map. */
interface Context {
  p0: number;
  p1: number;
  l0: number;
  l1: number;
  random: SplitMix64;
  map: readonly number[] | null;
}

/** An operation's result for `x` (before rounding and clamping); null when the event lacks the field. */
function operate(operation: TransformOperation, item: Item, x: number, original: number, ctx: Context): number {
  const { a, b } = operation;
  const position = operation.field === 'position';
  const t = ctx.p1 > ctx.p0 ? (original - ctx.p0) / (ctx.p1 - ctx.p0) : 0;
  switch (operation.op) {
    case 'thru':
      return x;
    case 'fix':
      return a;
    case 'add':
      return x + a;
    case 'subtract':
      return x - a;
    case 'min':
      return Math.max(x, a);
    case 'max':
      return Math.min(x, a);
    case 'flip':
      return position ? ctx.p0 + ctx.p1 + 2 * a - x : 2 * a - x;
    case 'multiply':
      return position ? ctx.p0 + (x - ctx.p0) * a : x * a;
    case 'divide':
      if (a === 0) {
        return x;
      }
      return position ? ctx.p0 + (x - ctx.p0) / a : x / a;
    case 'scale':
      return position ? ctx.p0 + (x - ctx.p0) * a + b : x * a + b;
    case 'range':
      return Math.min(Math.max(x, Math.min(a, b)), Math.max(a, b));
    case 'random':
      return ctx.random.between(roundAway(Math.min(a, b)), roundAway(Math.max(a, b)));
    case 'plusMinusRandom': {
      const r = roundAway(Math.abs(a));
      return x + ctx.random.between(-r, r);
    }
    case 'quantize':
      return a > 0 ? roundAway(x / a) * a : x;
    case 'exponent': {
      const range = exponentRange(operation.field, item.kind, ctx);
      if (!range || !(a > 0) || range[1] <= range[0]) {
        return x;
      }
      const [lo, hi] = range;
      const ratio = Math.min(1, Math.max(0, (x - lo) / (hi - lo)));
      return lo + Math.pow(ratio, a) * (hi - lo);
    }
    case 'crescendo':
      return a + (b - a) * t;
    case 'relativeCrescendo':
      return x + a + (b - a) * t;
    case 'useMap': {
      const r = roundAway(x);
      if (r < 0 || r > 127) {
        return x;
      }
      return ctx.map ? (ctx.map[r] ?? r) : r;
    }
  }
}

function exponentRange(field: TransformField, kind: ControlKind | 'note', ctx: Context): [number, number] | null {
  switch (field) {
    case 'position':
      return [ctx.p0, ctx.p1];
    case 'length':
      return [ctx.l0, ctx.l1];
    default:
      return fieldRange(field, kind);
  }
}

/**
 * Applies a set to notes and controller events whose positions are project ticks (the caller adds each region's
 * start before and subtracts it after). Every event comes back, in the order given; the matching ones are changed
 * unless the mode is `selectOnly`. `meter` places bar lines for `subposition`.
 */
export function applyTransform(
  notes: readonly Note[],
  controls: readonly ControlEvent[],
  set: TransformSet,
  meter: MeterMap = MeterMap.constant(4, 4)
): TransformResult {
  const conditions = set.conditions.filter(c => c.op !== 'all');
  const matches = (item: Item) => conditions.every(c => meets(valueOf(item, c.field, meter), c));
  const noteItems = notes.map(noteItem);
  const controlItems = controls.map(controlItem);
  const selectedNotes = noteItems.filter(matches);
  const selectedControls = controlItems.filter(matches);
  const result: TransformResult = {
    notes: notes.map(n => ({ ...n })),
    controls: controls.map(c => ({ ...c })),
    selectedNoteIds: selectedNotes.map(i => i.id),
    selectedControlIds: selectedControls.map(i => i.id)
  };
  if (set.mode === 'selectOnly') {
    return result;
  }

  const byField = new Map<TransformField, TransformOperation>();
  for (const operation of set.operations) {
    if (TransformFieldOrder.includes(operation.field)) {
      byField.set(operation.field, operation);
    }
  }
  const operations = TransformFieldOrder.map(f => byField.get(f)).filter(
    (o): o is TransformOperation => o !== undefined
  );

  const positions = [...selectedNotes, ...selectedControls].map(i => i.position);
  const lengths = selectedNotes.map(i => i.length);
  const extent = {
    p0: positions.length ? Math.min(...positions) : 0,
    p1: positions.length ? Math.max(...positions) : 0,
    l0: lengths.length ? Math.min(...lengths) : 0,
    l1: lengths.length ? Math.max(...lengths) : 0
  };

  const transform = (item: Item): Item => {
    const ctx: Context = { ...extent, random: new SplitMix64(set.seed, item.id), map: set.map ?? null };
    const next = { ...item };
    for (const operation of operations) {
      const range = fieldRange(operation.field, item.kind);
      if (!range) {
        // The event lacks the field; a random operation still draws.
        if (operation.op === 'random' || operation.op === 'plusMinusRandom') {
          ctx.random.next();
        }
        continue;
      }
      const field = operation.field as OperatedField;
      const value = operate(operation, item, next[field], item.position, ctx);
      next[field] = Math.min(range[1], Math.max(range[0], roundAway(value)));
    }
    return next;
  };

  const noteIds = new Set(result.selectedNoteIds);
  const controlIds = new Set(result.selectedControlIds);
  result.notes = notes.map(n => {
    if (!noteIds.has(n.id)) {
      return { ...n };
    }
    const t = transform(noteItem(n));
    return {
      ...n,
      startTick: t.position,
      lengthTicks: t.length,
      channel: t.channel,
      pitch: t.pitch,
      velocity: t.velocity
    };
  });
  result.controls = controls.map(c => {
    if (!controlIds.has(c.id)) {
      return { ...c };
    }
    const t = transform(controlItem(c));
    return {
      ...c,
      tick: t.position,
      channel: t.channel,
      number: hasNumber(c.kind) ? t.pitch : c.number,
      value: t.velocity
    };
  });
  return result;
}

const Notes: TransformCondition[] = [{ field: 'status', op: 'equal', a: 144, b: 0 }];

function preset(key: string, name: string, operations: TransformOperation[]): TransformSet {
  return { id: `factory.${key}`, name, conditions: Notes, operations, map: null, mode: 'selectAndOperate', seed: 0 };
}

/**
 * The factory presets in menu order, as the Model's `TransformEngine.Presets` (ids `factory.…`; every one on notes,
 * select and operate, seed 0); the fake Server lists them first in `GET /library/transform-sets`.
 */
export const TransformPresets: readonly TransformSet[] = [
  preset('humanize', 'Humanize', [
    { field: 'position', op: 'plusMinusRandom', a: 10, b: 0 },
    { field: 'length', op: 'plusMinusRandom', a: 10, b: 0 },
    { field: 'velocity', op: 'plusMinusRandom', a: 10, b: 0 }
  ]),
  preset('quantize', 'Quantize Notes (1/16)', [{ field: 'position', op: 'quantize', a: 240, b: 0 }]),
  preset('crescendo', 'Crescendo', [{ field: 'velocity', op: 'crescendo', a: 40, b: 120 }]),
  preset('fixedVelocity', 'Fixed Velocity', [{ field: 'velocity', op: 'fix', a: 100, b: 0 }]),
  preset('transpose', 'Transpose (+12)', [{ field: 'pitch', op: 'add', a: 12, b: 0 }]),
  preset('doubleSpeed', 'Double Speed', [
    { field: 'position', op: 'divide', a: 2, b: 0 },
    { field: 'length', op: 'divide', a: 2, b: 0 }
  ]),
  preset('halfSpeed', 'Half Speed', [
    { field: 'position', op: 'multiply', a: 2, b: 0 },
    { field: 'length', op: 'multiply', a: 2, b: 0 }
  ]),
  preset('reversePosition', 'Reverse Position', [{ field: 'position', op: 'flip', a: 0, b: 0 }]),
  preset('reversePitch', 'Reverse Pitch (around C3)', [{ field: 'pitch', op: 'flip', a: 60, b: 0 }]),
  preset('lengthLimits', 'Length Limits', [{ field: 'length', op: 'range', a: 120, b: 3840 }]),
  preset('randomVelocity', 'Random Velocity', [{ field: 'velocity', op: 'random', a: 60, b: 120 }]),
  preset('velocityCompress', 'Velocity Compress', [{ field: 'velocity', op: 'scale', a: 0.5, b: 48 }])
];

/** Whether a set is a factory preset (never saved over or deleted). */
export function isFactorySet(id: string): boolean {
  return id.startsWith('factory.');
}

/**
 * A preset as it applies to a selection. The engine's position flip already mirrors around the selection's extent
 * (P0 + P1 + 2a − x) and Reverse Pitch flips around C3, so every preset comes back as it is; kept for the window.
 */
export function fitPresetToSelection(set: TransformSet, _events?: unknown): TransformSet {
  return set;
}

/** The identity map (`useMap` with no map): 0…127. */
export function identityMap(): number[] {
  return Array.from({ length: 128 }, (_, i) => i);
}
