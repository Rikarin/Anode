import type { ControlEvent, MidiRegion, Note, TransformSet } from '@anode/api-client';
import { MeterMap } from '@anode/timeline';
import { applyTransform, TransformPresets } from '../midi-edit/transform';
import {
  applyGridChange,
  type Drafts,
  emptySet,
  formatValue,
  gridRows,
  isRandom,
  matching,
  newSeed,
  newSetId,
  parseValue,
  presetMenu,
  transformCommand,
  transformScope,
  usesMap,
  valueKind
} from './transform-model';

const meter = MeterMap.constant(4, 4);

function note(id: number, pitch: number, startTick: number, velocity = 80, lengthTicks = 480): Note {
  return { id, pitch, startTick, velocity, lengthTicks, channel: 1, articulationId: 0 };
}

function control(id: number, tick: number, value: number): ControlEvent {
  return { id, kind: 'controlChange', number: 1, tick, value, channel: 1 };
}

function region(id: string, startTick: number, notes: Note[], controls: ControlEvent[] = []) {
  return { id, startTick, notes, controls } as Pick<MidiRegion, 'id' | 'startTick' | 'notes' | 'controls'>;
}

/** Edits a set through the grid as a user would: an operator, then values as typed. */
function edit(
  set: TransformSet,
  edits: [row: string, field: string, part: 'op' | 'a' | 'b', value: string][],
  drafts: Drafts = new Map()
) {
  let state = { set, drafts };
  for (const [row, field, part, value] of edits) {
    state = applyGridChange(state.set, state.drafts, { row, field, part, value }, meter);
  }
  return state;
}

describe('Transform window model', () => {
  it('reads positions and lengths as bar beat division tick by what the operator does', () => {
    expect(valueKind('condition', 'position', 'inside', 'a')).toBe('position');
    expect(valueKind('operation', 'position', 'fix', 'a')).toBe('position');
    expect(valueKind('operation', 'position', 'plusMinusRandom', 'a')).toBe('length');
    expect(valueKind('operation', 'position', 'quantize', 'a')).toBe('length');
    expect(valueKind('operation', 'position', 'divide', 'a')).toBe('number');
    expect(valueKind('operation', 'length', 'scale', 'a')).toBe('number');
    expect(valueKind('operation', 'length', 'scale', 'b')).toBe('length');
    expect(valueKind('operation', 'length', 'fix', 'a')).toBe('length');
    expect(valueKind('condition', 'subposition', 'equal', 'a')).toBe('length');
    expect(valueKind('condition', 'pitch', 'greaterEqual', 'a')).toBe('number');
    expect(valueKind('condition', 'status', 'equal', 'a')).toBe('status');

    expect(formatValue('position', 1920 + 240 + 5, meter)).toBe('1 3 2 6');
    expect(parseValue('position', '1 3 2 6', meter)).toBe(2165);
    expect(formatValue('length', 240, meter)).toBe('0 0 1 0');
    expect(parseValue('length', '0 0 1 0', meter)).toBe(240);
    expect(parseValue('length', '10', meter)).toBe(10);
    expect(formatValue('length', -10, meter)).toBe('-0 0 0 10');
    expect(parseValue('length', '-0 0 0 10', meter)).toBe(-10);
    expect(formatValue('number', 0.5, meter)).toBe('0.5');
    expect(parseValue('number', ' 73 ', meter)).toBe(73);
    expect(parseValue('number', '1.5', meter)).toBe(1.5);
    expect(parseValue('number', 'x', meter)).toBeNull();
    expect(parseValue('position', '0 1', meter)).toBeNull();
  });

  it('shows a set as the grid’s rows: every column a condition, the event fields an operation', () => {
    const humanize = TransformPresets[0] as TransformSet;
    const [conditions, operations] = gridRows(humanize, meter);
    expect(conditions?.cells.map(c => c?.op)).toEqual(['equal', 'all', 'all', 'all', 'all', 'all', 'all']);
    expect(conditions?.cells[0]?.choices?.map(c => c.label)).toContain('Note');
    expect(operations?.cells.map(c => c?.op ?? null)).toEqual([
      null,
      'plusMinusRandom',
      null,
      'plusMinusRandom',
      'thru',
      'thru',
      'plusMinusRandom'
    ]);
    expect(operations?.cells[1]).toMatchObject({ a: '0 0 0 10', hint: 'bars beats divisions ticks' });
    expect(operations?.cells[6]).toMatchObject({ a: '10' });
    expect(operations?.cells[5]?.options.map(o => o.value)).toContain('useMap');
    expect(operations?.cells[1]?.options.map(o => o.value)).not.toContain('useMap');
  });

  it('builds the golden path’s custom set from grid edits', () => {
    const { set, drafts } = edit(emptySet(), [
      ['condition', 'status', 'op', 'equal'],
      ['condition', 'pitch', 'op', 'greaterEqual'],
      ['condition', 'pitch', 'a', '73'],
      ['operation', 'velocity', 'op', 'fix'],
      ['operation', 'velocity', 'a', '100'],
      ['operation', 'length', 'op', 'fix'],
      ['operation', 'length', 'a', '0 0 1 0']
    ]);
    expect(drafts.size).toBe(0);
    expect(set.conditions).toEqual([
      { field: 'status', op: 'equal', a: 144, b: 144 },
      { field: 'pitch', op: 'greaterEqual', a: 73, b: 72 }
    ]);
    expect(set.operations).toEqual([
      { field: 'length', op: 'fix', a: 240, b: 1920 },
      { field: 'velocity', op: 'fix', a: 100, b: 127 }
    ]);

    const notes = [note(1, 60, 0), note(2, 73, 480), note(3, 84, 960, 30, 960)];
    const result = applyTransform(notes, [control(9, 0, 64)], set, meter);
    expect(result.notes.map(n => [n.pitch, n.velocity, n.lengthTicks])).toEqual([
      [60, 80, 480],
      [73, 100, 240],
      [84, 100, 240]
    ]);
    expect(result.controls[0]?.value).toBe(64);
    expect(result.selectedNoteIds).toEqual([2, 3]);
  });

  it('keeps a value that does not parse as typed, and drops cells back to all or thru', () => {
    const typed = edit(emptySet(), [
      ['condition', 'position', 'op', 'inside'],
      ['condition', 'position', 'b', '2 x']
    ]);
    expect(typed.set.conditions).toEqual([{ field: 'position', op: 'inside', a: 0, b: 0 }]);
    expect(typed.drafts.get('condition:position:b')).toBe('2 x');
    const row = gridRows(typed.set, meter, typed.drafts)[0];
    expect(row?.cells[1]).toMatchObject({ a: '1 1 1 1', b: '2 x', invalid: ['b'] });

    const fixed = edit(typed.set, [['condition', 'position', 'b', '2 1 1 1']], typed.drafts);
    expect(fixed.drafts.size).toBe(0);
    expect(fixed.set.conditions[0]).toMatchObject({ a: 0, b: 3840 });

    const cleared = edit(fixed.set, [['condition', 'position', 'op', 'all']], fixed.drafts);
    expect(cleared.set.conditions).toEqual([]);
    const thru = edit(TransformPresets[0] as TransformSet, [['operation', 'velocity', 'op', 'thru']]);
    expect(thru.set.operations.map(o => o.field)).toEqual(['position', 'length']);
  });

  it('keeps values that read the same way when the operator changes', () => {
    const { set } = edit(emptySet(), [
      ['operation', 'velocity', 'op', 'fix'],
      ['operation', 'velocity', 'a', '90'],
      ['operation', 'velocity', 'op', 'add']
    ]);
    expect(set.operations).toEqual([{ field: 'velocity', op: 'add', a: 90, b: 127 }]);
    const position = edit(emptySet(), [
      ['operation', 'position', 'op', 'fix'],
      ['operation', 'position', 'a', '2 1 1 1'],
      ['operation', 'position', 'op', 'add']
    ]);
    // A position became a delta: a fresh 1/16.
    expect(position.set.operations[0]).toMatchObject({ op: 'add', a: 240 });
  });

  it('knows random and map sets, and makes seeds and library ids', () => {
    expect(isRandom(TransformPresets[0] as TransformSet)).toBe(true);
    expect(isRandom(TransformPresets[1] as TransformSet)).toBe(false);
    expect(usesMap(edit(emptySet(), [['operation', 'pitch', 'op', 'useMap']]).set)).toBe(true);
    expect(newSeed(() => 0)).toBe(1);
    expect(newSeed(() => 0.999999999)).toBeLessThan(2_147_483_647);
    expect(newSetId(() => 0.5)).toMatch(/^trs_7fffffff[0-9a-f]{6}$/);
  });

  it('lists the factory presets first, then the user’s by name', () => {
    const user = (id: string, name: string): TransformSet => ({ ...emptySet(), id, name });
    const menu = presetMenu([user('trs_b', 'Zed'), ...TransformPresets, user('trs_a', 'Accents')]);
    expect(menu.map(s => s.id).slice(0, 2)).toEqual(['factory.humanize', 'factory.quantize']);
    expect(menu.slice(-2).map(s => s.name)).toEqual(['Accents', 'Zed']);
  });

  it('acts on the selection when there is one, in project ticks, and builds the command', () => {
    const regions = [
      region('mid_1', 1920, [note(1, 60, 0), note(2, 64, 480)], [control(5, 0, 10)]),
      region('mid_2', 3840, [note(3, 67, 0)])
    ];
    const all = transformScope(regions, new Set(), new Set());
    expect(all.selection).toBe(false);
    expect(all.notes.map(n => n.startTick)).toEqual([1920, 2400, 3840]);
    expect(all.controls.map(c => c.tick)).toEqual([1920]);
    const set = edit(emptySet(), [
      ['operation', 'velocity', 'op', 'fix'],
      ['operation', 'velocity', 'a', '100']
    ]).set;
    expect(transformCommand(all, set)).toEqual({
      type: 'transform',
      regionIds: ['mid_1', 'mid_2'],
      set: { ...set, map: null }
    });

    const selected = transformScope(regions, new Set([2, 3]), new Set());
    expect(selected.selection).toBe(true);
    expect(transformCommand(selected, set)).toMatchObject({ noteIds: [2, 3], controlIds: [] });
    expect(matching(selected, set, meter).selectedNoteIds).toEqual([2, 3]);
    // Select Only looks at every event.
    expect(transformScope(regions, new Set([2]), new Set(), true).notes).toHaveLength(3);
  });
});
