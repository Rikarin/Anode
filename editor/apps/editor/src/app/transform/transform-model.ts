import type {
  ControlEvent,
  EditCommand,
  MidiRegion,
  Note,
  TransformConditionOp,
  TransformField,
  TransformOperationKind,
  TransformSet
} from '@anode/api-client';
import type {
  TransformGridCell,
  TransformGridChange,
  TransformGridColumn,
  TransformGridOption,
  TransformGridRow
} from '@anode/audio-ui/lazy';
import type { MeterMap } from '@anode/timeline';
import { formatLength, formatPosition, parseLength, parsePosition } from '../midi-edit/position';
import { applyTransform, isFactorySet, StatusOf, type TransformResult } from '../midi-edit/transform';

/**
 * The MIDI Transform window's model (docs/08-phase-7.md, "MIDI Transform"): the grid's columns and menus, a set shown
 * as grid cells (positions and lengths as bar beat division tick), edits of the grid turned back into the set, the
 * events a set applies to, and the `transform` command.
 */

/** The grid's columns, in Logic's order; status and subposition are conditions only. */
export const TransformColumns: readonly (TransformGridColumn & { field: TransformField })[] = [
  { field: 'status', label: 'Status' },
  { field: 'position', label: 'Position' },
  { field: 'subposition', label: 'Subposition' },
  { field: 'length', label: 'Length' },
  { field: 'channel', label: 'Channel' },
  { field: 'pitch', label: 'Pitch' },
  { field: 'velocity', label: 'Velocity' }
];

const ConditionOps: readonly (TransformGridOption & { value: TransformConditionOp })[] = [
  { value: 'all', label: 'All', operands: 0 },
  { value: 'equal', label: 'Equal', operands: 1 },
  { value: 'unequal', label: 'Unequal', operands: 1 },
  { value: 'lessEqual', label: 'Less or Equal', operands: 1 },
  { value: 'greaterEqual', label: 'Greater or Equal', operands: 1 },
  { value: 'inside', label: 'Inside', operands: 2 },
  { value: 'outside', label: 'Outside', operands: 2 }
];

const StatusOps = ConditionOps.filter(o => o.value === 'all' || o.value === 'equal' || o.value === 'unequal');

const OperationOps: readonly (TransformGridOption & { value: TransformOperationKind })[] = [
  { value: 'thru', label: 'Thru', operands: 0 },
  { value: 'fix', label: 'Fix', operands: 1 },
  { value: 'add', label: 'Add', operands: 1 },
  { value: 'subtract', label: 'Subtract', operands: 1 },
  { value: 'min', label: 'Min', operands: 1 },
  { value: 'max', label: 'Max', operands: 1 },
  { value: 'flip', label: 'Flip', operands: 1 },
  { value: 'multiply', label: 'Multiply', operands: 1 },
  { value: 'divide', label: 'Divide', operands: 1 },
  { value: 'scale', label: 'Scale', operands: 2 },
  { value: 'range', label: 'Range', operands: 2 },
  { value: 'random', label: 'Random', operands: 2 },
  { value: 'plusMinusRandom', label: '± Random', operands: 1 },
  { value: 'quantize', label: 'Quantize', operands: 1 },
  { value: 'exponent', label: 'Exponent', operands: 1 },
  { value: 'crescendo', label: 'Crescendo', operands: 2 },
  { value: 'relativeCrescendo', label: 'Rel. Crescendo', operands: 2 },
  { value: 'useMap', label: 'Use Map', operands: 0 }
];

/** The operations a field offers: `exponent` curves channel, pitch and velocity; the map is for pitch and velocity. */
function operationOps(field: TransformField): readonly TransformGridOption[] {
  return OperationOps.filter(o => {
    if (o.value === 'exponent') {
      return field === 'channel' || field === 'pitch' || field === 'velocity';
    }
    if (o.value === 'useMap') {
      return field === 'pitch' || field === 'velocity';
    }
    return true;
  });
}

/** The Status column's event types (the status nibble as a number). */
export const StatusChoices = [
  { value: String(StatusOf.note), label: 'Note' },
  { value: String(StatusOf.polyPressure), label: 'Poly Pressure' },
  { value: String(StatusOf.controlChange), label: 'Control Change' },
  { value: String(StatusOf.programChange), label: 'Program Change' },
  { value: String(StatusOf.channelPressure), label: 'Channel Pressure' },
  { value: String(StatusOf.pitchBend), label: 'Pitch Bend' }
] as const;

export type TransformRow = 'condition' | 'operation';

/** How a value is shown and typed. */
export type ValueKind = 'position' | 'length' | 'number' | 'status';

/**
 * How a cell's value reads: a position condition, and an operation setting a position (fix, min, max, flip, range,
 * random), are positions; position deltas (add, subtract, ± random, quantize, scale's offset), lengths and subpositions
 * are lengths; factors (multiply, divide, scale's factor, exponent) and ramps are numbers; status is a type.
 */
export function valueKind(row: TransformRow, field: TransformField, op: string, part: 'a' | 'b'): ValueKind {
  if (field === 'status') {
    return 'status';
  }
  if (field !== 'position' && field !== 'length' && field !== 'subposition') {
    return 'number';
  }
  if (op === 'multiply' || op === 'divide' || op === 'exponent' || op === 'crescendo' || op === 'relativeCrescendo') {
    return 'number';
  }
  if (op === 'scale') {
    return part === 'a' ? 'number' : 'length';
  }
  if (field === 'position') {
    const absolute = row === 'condition' || ['fix', 'min', 'max', 'flip', 'range', 'random'].includes(op);
    return absolute ? 'position' : 'length';
  }
  return 'length';
}

const Hints: Record<ValueKind, string | undefined> = {
  position: 'bar beat division tick',
  length: 'bars beats divisions ticks',
  number: undefined,
  status: undefined
};

/** A value as the grid shows it. */
export function formatValue(kind: ValueKind, value: number, meter: MeterMap): string {
  switch (kind) {
    case 'position':
      return formatPosition(value, meter);
    case 'length':
      return value < 0 ? `-${formatLength(-value, meter)}` : formatLength(value, meter);
    case 'number':
    case 'status':
      return String(Number(value.toFixed(4)));
  }
}

/** A typed value, or null when it is not one. */
export function parseValue(kind: ValueKind, text: string, meter: MeterMap): number | null {
  const trimmed = text.trim();
  switch (kind) {
    case 'position':
      return parsePosition(trimmed, meter);
    case 'length': {
      const negative = trimmed.startsWith('-');
      const ticks = parseLength(negative ? trimmed.slice(1) : trimmed, meter);
      return ticks === null ? null : negative ? -ticks : ticks;
    }
    case 'number':
    case 'status': {
      if (!/^[-+]?(\d+\.?\d*|\.\d+)$/.test(trimmed)) {
        return null;
      }
      return Number(trimmed);
    }
  }
}

/** A sensible first value when an operator first needs one. */
function defaultValue(row: TransformRow, field: TransformField, op: string, part: 'a' | 'b'): number {
  if (field === 'status') {
    return StatusOf.note;
  }
  if (op === 'multiply' || op === 'divide') {
    return 2;
  }
  if (op === 'exponent') {
    return 1;
  }
  switch (field) {
    case 'position':
      return valueKind(row, field, op, part) === 'position' ? 0 : 240;
    case 'subposition':
      return 0;
    case 'length':
      return part === 'b' ? 1920 : 240;
    case 'channel':
      return part === 'b' ? 16 : 1;
    case 'pitch':
      return part === 'b' ? 72 : 60;
    case 'velocity':
      return part === 'b' ? 127 : 100;
  }
}

/** An empty set: no conditions, no operations, select and operate. */
export function emptySet(): TransformSet {
  return { id: '', name: 'Untitled', conditions: [], operations: [], map: null, mode: 'selectAndOperate', seed: 0 };
}

function conditionOf(set: TransformSet, field: TransformField) {
  return set.conditions.find(c => c.field === field) ?? null;
}

function operationOf(set: TransformSet, field: TransformField) {
  // The last one given wins, as the engine reads it.
  return [...set.operations].reverse().find(o => o.field === field) ?? null;
}

/** Texts typed that did not parse, by `row:field:part`. */
export type Drafts = ReadonlyMap<string, string>;

export function draftKey(row: string, field: string, part: 'a' | 'b'): string {
  return `${row}:${field}:${part}`;
}

/** The set as the grid's two rows. */
export function gridRows(set: TransformSet, meter: MeterMap, drafts: Drafts = new Map()): TransformGridRow[] {
  const cell = (
    row: TransformRow,
    field: TransformField,
    op: string,
    a: number,
    b: number,
    options: readonly TransformGridOption[]
  ): TransformGridCell => {
    const text = (part: 'a' | 'b', value: number) =>
      drafts.get(draftKey(row, field, part)) ?? formatValue(valueKind(row, field, op, part), value, meter);
    const invalid = (['a', 'b'] as const).filter(part => drafts.has(draftKey(row, field, part)));
    const kind = valueKind(row, field, op, 'a');
    return {
      op,
      options,
      a: text('a', a),
      b: text('b', b),
      ...(field === 'status' ? { choices: StatusChoices } : {}),
      ...(invalid.length ? { invalid } : {}),
      ...(Hints[kind] ? { hint: Hints[kind] } : {})
    };
  };
  return [
    {
      id: 'condition',
      label: 'Select Events by Conditions',
      noun: 'condition',
      cells: TransformColumns.map(({ field }) => {
        const c = conditionOf(set, field);
        return cell(
          'condition',
          field,
          c?.op ?? 'all',
          c?.a ?? 0,
          c?.b ?? 0,
          field === 'status' ? StatusOps : ConditionOps
        );
      })
    },
    {
      id: 'operation',
      label: 'Operations on Selected Events',
      noun: 'operation',
      cells: TransformColumns.map(({ field }) => {
        if (field === 'status' || field === 'subposition') {
          return null;
        }
        const o = operationOf(set, field);
        return cell('operation', field, o?.op ?? 'thru', o?.a ?? 0, o?.b ?? 0, operationOps(field));
      })
    }
  ];
}

/**
 * A grid edit applied: the set with the cell's operator or value changed (conditions that match all and `thru`
 * operations are left out of the set), and the drafts with a value that did not parse kept as typed.
 */
export function applyGridChange(
  set: TransformSet,
  drafts: Drafts,
  change: TransformGridChange,
  meter: MeterMap
): { set: TransformSet; drafts: Drafts } {
  const row = change.row as TransformRow;
  const field = change.field as TransformField;
  const nextDrafts = new Map(drafts);
  const current = row === 'condition' ? conditionOf(set, field) : operationOf(set, field);
  let op: string = current?.op ?? (row === 'condition' ? 'all' : 'thru');
  let a = current?.a ?? 0;
  let b = current?.b ?? 0;

  if (change.part === 'op') {
    const before = op;
    op = change.value;
    nextDrafts.delete(draftKey(row, field, 'a'));
    nextDrafts.delete(draftKey(row, field, 'b'));
    const empty = before === 'all' || before === 'thru';
    for (const part of ['a', 'b'] as const) {
      // Keep the values when they read the same way under the new operator; else start from a default.
      if (empty || valueKind(row, field, before, part) !== valueKind(row, field, op, part)) {
        const value = defaultValue(row, field, op, part);
        if (part === 'a') {
          a = value;
        } else {
          b = value;
        }
      }
    }
  } else {
    const value = parseValue(valueKind(row, field, op, change.part), change.value, meter);
    if (value === null) {
      nextDrafts.set(draftKey(row, field, change.part), change.value);
      return { set, drafts: nextDrafts };
    }
    nextDrafts.delete(draftKey(row, field, change.part));
    if (change.part === 'a') {
      a = value;
    } else {
      b = value;
    }
  }

  if (row === 'condition') {
    const others = set.conditions.filter(c => c.field !== field);
    const conditions =
      op === 'all' ? others : [...others, { field, op: op as TransformConditionOp, a, b }].sort(byColumn);
    return { set: { ...set, conditions }, drafts: nextDrafts };
  }
  const others = set.operations.filter(o => o.field !== field);
  const operations =
    op === 'thru' ? others : [...others, { field, op: op as TransformOperationKind, a, b }].sort(byColumn);
  return { set: { ...set, operations }, drafts: nextDrafts };
}

function byColumn(x: { field: TransformField }, y: { field: TransformField }): number {
  const index = (f: TransformField) => TransformColumns.findIndex(c => c.field === f);
  return index(x.field) - index(y.field);
}

/** Whether a set draws random numbers (so each Apply takes a fresh seed). */
export function isRandom(set: TransformSet): boolean {
  return set.operations.some(o => o.op === 'random' || o.op === 'plusMinusRandom');
}

/** Whether a set's operations read its 128-step map. */
export function usesMap(set: TransformSet): boolean {
  return set.operations.some(o => o.op === 'useMap');
}

/** A fresh seed for a random set: 1…2^31−1. */
export function newSeed(random: () => number = Math.random): number {
  return 1 + Math.floor(random() * 2_147_483_646);
}

/** A new library id for a user's set. */
export function newSetId(random: () => number = Math.random): string {
  return `trs_${Math.floor(random() * 0xffffffff)
    .toString(16)
    .padStart(8, '0')}${Date.now().toString(16).slice(-6)}`;
}

/** The library's sets in menu order: the factory presets first (as listed), then the user's by name. */
export function presetMenu(sets: readonly TransformSet[]): TransformSet[] {
  const factory = sets.filter(s => isFactorySet(s.id));
  const user = sets.filter(s => !isFactorySet(s.id)).sort((a, b) => a.name.localeCompare(b.name));
  return [...factory, ...user];
}

/** The events a transform applies to, with their positions in project ticks. */
export interface TransformScope {
  regionIds: string[];
  notes: Note[];
  controls: ControlEvent[];
  /** The selection limits it (the command sends `noteIds` and `controlIds`). */
  selection: boolean;
}

/**
 * What a transform applies to in `regions`: the selected notes and controller events among theirs when any is
 * selected, else all of them; `all` ignores the selection (Select Only picks among every event).
 */
export function transformScope(
  regions: readonly Pick<MidiRegion, 'id' | 'startTick' | 'notes' | 'controls'>[],
  selectedNotes: ReadonlySet<number>,
  selectedControls: ReadonlySet<number>,
  all = false
): TransformScope {
  const notes = regions.flatMap(r => r.notes.map(n => ({ ...n, startTick: n.startTick + r.startTick })));
  const controls = regions.flatMap(r => r.controls.map(c => ({ ...c, tick: c.tick + r.startTick })));
  const selection =
    !all && (notes.some(n => selectedNotes.has(n.id)) || controls.some(c => selectedControls.has(c.id)));
  return {
    regionIds: regions.map(r => r.id),
    notes: selection ? notes.filter(n => selectedNotes.has(n.id)) : notes,
    controls: selection ? controls.filter(c => selectedControls.has(c.id)) : controls,
    selection
  };
}

/** The events of a scope that a set's conditions select. */
export function matching(scope: TransformScope, set: TransformSet, meter: MeterMap): TransformResult {
  return applyTransform(scope.notes, scope.controls, { ...set, mode: 'selectOnly' }, meter);
}

/** The `transform` command for operate only or select and operate: the scope's selection, when it has one. */
export function transformCommand(scope: TransformScope, set: TransformSet): EditCommand {
  return {
    type: 'transform',
    regionIds: scope.regionIds,
    set: { ...set, map: set.map ?? null },
    ...(scope.selection ? { noteIds: scope.notes.map(n => n.id), controlIds: scope.controls.map(c => c.id) } : {})
  };
}
