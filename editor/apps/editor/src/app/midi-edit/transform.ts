import type {
  ControlEvent,
  ControlKind,
  Note,
  TransformCondition,
  TransformField,
  TransformOperation,
  TransformOperationKind,
  TransformSet
} from '@anode/api-client';
import { MeterMap } from '@anode/timeline';
import { hash32 } from '../music/pattern';

/**
 * The MIDI Transform engine, a TypeScript port of the Model's `TransformEngine` (docs/08-phase-7.md, "Rules the
 * contracts imply"; checked against the shared engine/tests/Anode.Model.Tests/TransformVectors.json). The editor runs
 * "select only" itself and the fake Server runs the `transform` command with it.
 *
 * - Conditions are ANDed; a field without one (or with `all`) matches everything.
 * - Operations apply to each matching event in field order (position, length, channel, pitch, velocity), whatever
 *   their order in the list; each result is rounded half to even (as .NET's `Math.Round`) and clamped to the field's
 *   range: a note's position ≥ 0, length ≥ 1, channel 1…16, pitch 0…127, velocity 1…127; a controller's value its
 *   kind's range (pitch bend −8192…8191, else 0…127). A controller has no length, and only control changes and poly
 *   pressure have a `pitch` (their first data byte); operations on what an event lacks leave it.
 * - Ramps (`crescendo`, `relativeCrescendo`) run over the matching events' positions as they were before the
 *   operations, from the earliest to the latest.
 * - Random operations draw {@link transformDraw}(seed, event id, field), so a set always does the same to an event.
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

/**
 * A random operation's draw for an event, uniform in [0, 1): FNV-1a 32 over the UTF-16 units of
 * `"seed:id:field"`, finalised as MurmurHash3's fmix32, over 2^32 (the pattern generator's scheme).
 */
export function transformDraw(seed: number, eventId: number, field: TransformField): number {
  return hash32(`${seed}:${eventId}:${field}`) / 4_294_967_296;
}

/** Rounds half to even, as .NET's `Math.Round(double)`. */
export function roundEven(value: number): number {
  const floor = Math.floor(value);
  const fraction = value - floor;
  if (Math.abs(fraction - 0.5) < 1e-9) {
    return floor % 2 === 0 ? floor : floor + 1;
  }
  return Math.round(value);
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

/** A field's range for an event kind, or null when the event has no such field. */
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

/** The span `exponent` normalises over: a note's velocity and pitch 0…127, else the field's range. */
function curveRange(field: TransformField, kind: ControlKind | 'note'): [number, number] | null {
  if (field === 'velocity' || field === 'pitch') {
    return kind === 'pitchBend' && field === 'velocity' ? [-8192, 8191] : [0, 127];
  }
  if (field === 'channel') {
    return [1, 16];
  }
  return null;
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
      return position - meter.barStart(Math.floor(meter.barAt(position)));
    }
  }
}

/** Whether an event meets a condition. */
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

/** What an operation makes of `x` (before rounding and clamping). */
export function operate(
  op: TransformOperationKind,
  x: number,
  a: number,
  b: number,
  context: { draw: () => number; ramp: () => number; range: [number, number] | null; map: readonly number[] | null }
): number {
  switch (op) {
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
      return 2 * a - x;
    case 'multiply':
      return x * a;
    case 'divide':
      return a === 0 ? x : x / a;
    case 'scale':
      return x * a + b;
    case 'range':
      return Math.min(Math.max(x, Math.min(a, b)), Math.max(a, b));
    case 'random':
      return a + context.draw() * (b - a);
    case 'plusMinusRandom':
      return x + (context.draw() * 2 - 1) * a;
    case 'quantize':
      return a > 0 ? roundEven(x / a) * a : x;
    case 'exponent': {
      const range = context.range;
      if (!range || !(a > 0)) {
        return x;
      }
      const [lo, hi] = range;
      const unit = Math.min(1, Math.max(0, (x - lo) / (hi - lo)));
      return lo + Math.pow(unit, a) * (hi - lo);
    }
    case 'crescendo':
      return context.ramp();
    case 'relativeCrescendo':
      return x + context.ramp();
    case 'useMap': {
      const index = Math.min(127, Math.max(0, roundEven(x)));
      return context.map ? (context.map[index] ?? index) : index;
    }
  }
}

/**
 * Applies a set to notes and controller events whose positions are project ticks (the caller adds each region's
 * start before and subtracts it after). Every event comes back, in the order given; the matching ones are changed
 * unless the mode is `selectOnly`. `signatures` places bar lines for `subposition`.
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
  const selectedNotes = new Set(noteItems.filter(matches).map(i => i.id));
  const selectedControls = new Set(controlItems.filter(matches).map(i => i.id));
  const result: TransformResult = {
    notes: notes.map(n => ({ ...n })),
    controls: controls.map(c => ({ ...c })),
    selectedNoteIds: [...selectedNotes],
    selectedControlIds: [...selectedControls]
  };
  if (set.mode === 'selectOnly') {
    return result;
  }

  // The last operation given for a field wins; status and subposition are conditions only.
  const byField = new Map<TransformField, TransformOperation>();
  for (const operation of set.operations) {
    if (TransformFieldOrder.includes(operation.field)) {
      byField.set(operation.field, operation);
    }
  }
  const operations = TransformFieldOrder.map(f => byField.get(f)).filter(
    (o): o is TransformOperation => o !== undefined && o.op !== 'thru'
  );

  const selected = [
    ...noteItems.filter(i => selectedNotes.has(i.id)),
    ...controlItems.filter(i => selectedControls.has(i.id))
  ];
  const positions = selected.map(i => i.position);
  const first = positions.length ? Math.min(...positions) : 0;
  const last = positions.length ? Math.max(...positions) : 0;

  const transform = (item: Item): Item => {
    const original = item.position;
    const next = { ...item };
    for (const operation of operations) {
      const range = fieldRange(operation.field, item.kind);
      if (!range) {
        continue;
      }
      const x = next[operation.field as 'position' | 'length' | 'channel' | 'pitch' | 'velocity'];
      const value = operate(operation.op, x, operation.a, operation.b, {
        draw: () => transformDraw(set.seed, item.id, operation.field),
        ramp: () =>
          last > first
            ? operation.a + ((operation.b - operation.a) * (original - first)) / (last - first)
            : operation.a,
        range: curveRange(operation.field, item.kind),
        map: set.map ?? null
      });
      next[operation.field as 'position' | 'length' | 'channel' | 'pitch' | 'velocity'] = Math.min(
        range[1],
        Math.max(range[0], roundEven(value))
      );
    }
    return next;
  };

  result.notes = notes.map(n => {
    if (!selectedNotes.has(n.id)) {
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
    if (!selectedControls.has(c.id)) {
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

const Sixteenth = 240;

function preset(
  key: string,
  name: string,
  operations: TransformOperation[],
  conditions: TransformCondition[] = [],
  map: number[] | null = null
): TransformSet {
  return { id: `factory.${key}`, name, conditions, operations, map, mode: 'selectAndOperate', seed: 0 };
}

/**
 * The factory presets in menu order (ids `factory.…`, `TransformPresetIds`), as the fake Server lists them first in
 * `GET /library/transform-sets`. Reverse Position and Reverse Pitch flip around `a`, which the Transform window sets
 * to the middle of the selection when it loads them (see {@link fitPresetToSelection}).
 */
export const TransformPresets: readonly TransformSet[] = [
  preset('humanize', 'Humanize', [
    { field: 'position', op: 'plusMinusRandom', a: 10, b: 0 },
    { field: 'length', op: 'plusMinusRandom', a: 10, b: 0 },
    { field: 'velocity', op: 'plusMinusRandom', a: 10, b: 0 }
  ]),
  preset(
    'quantize',
    'Quantize Notes',
    [{ field: 'position', op: 'quantize', a: Sixteenth, b: 0 }],
    [{ field: 'status', op: 'equal', a: 144, b: 0 }]
  ),
  preset(
    'crescendo',
    'Crescendo',
    [{ field: 'velocity', op: 'crescendo', a: 40, b: 120 }],
    [{ field: 'status', op: 'equal', a: 144, b: 0 }]
  ),
  preset(
    'fixedVelocity',
    'Fixed Velocity',
    [{ field: 'velocity', op: 'fix', a: 100, b: 0 }],
    [{ field: 'status', op: 'equal', a: 144, b: 0 }]
  ),
  preset(
    'transpose',
    'Transpose',
    [{ field: 'pitch', op: 'add', a: 12, b: 0 }],
    [{ field: 'status', op: 'equal', a: 144, b: 0 }]
  ),
  preset('doubleSpeed', 'Double Speed', [
    { field: 'position', op: 'divide', a: 2, b: 0 },
    { field: 'length', op: 'divide', a: 2, b: 0 }
  ]),
  preset('halfSpeed', 'Half Speed', [
    { field: 'position', op: 'multiply', a: 2, b: 0 },
    { field: 'length', op: 'multiply', a: 2, b: 0 }
  ]),
  preset('reversePosition', 'Reverse Position', [{ field: 'position', op: 'flip', a: 0, b: 0 }]),
  preset(
    'reversePitch',
    'Reverse Pitch',
    [{ field: 'pitch', op: 'flip', a: 60, b: 0 }],
    [{ field: 'status', op: 'equal', a: 144, b: 0 }]
  ),
  preset(
    'lengthLimits',
    'Length Limits',
    [{ field: 'length', op: 'range', a: 120, b: 1920 }],
    [{ field: 'status', op: 'equal', a: 144, b: 0 }]
  )
];

/** Whether a set is a factory preset (never saved over or deleted). */
export function isFactorySet(id: string): boolean {
  return id.startsWith('factory.');
}

/**
 * A preset fitted to the events it will apply to: Reverse Position flips around the middle of their positions and
 * Reverse Pitch around the middle of their pitches (Logic's "reverse"); other sets come back as they are.
 */
export function fitPresetToSelection(
  set: TransformSet,
  events: {
    notes: readonly Pick<Note, 'startTick' | 'pitch' | 'lengthTicks'>[];
    controls: readonly Pick<ControlEvent, 'tick'>[];
  }
): TransformSet {
  const middle = (values: number[]) => (values.length ? (Math.min(...values) + Math.max(...values)) / 2 : 0);
  if (set.id === 'factory.reversePosition') {
    const a = middle([...events.notes.map(n => n.startTick), ...events.controls.map(c => c.tick)]);
    return { ...set, operations: set.operations.map(o => (o.field === 'position' ? { ...o, a } : o)) };
  }
  if (set.id === 'factory.reversePitch' && events.notes.length) {
    const a = middle(events.notes.map(n => n.pitch));
    return { ...set, operations: set.operations.map(o => (o.field === 'pitch' ? { ...o, a } : o)) };
  }
  return set;
}

/** The identity map (`useMap` with no map): 0…127. */
export function identityMap(): number[] {
  return Array.from({ length: 128 }, (_, i) => i);
}
