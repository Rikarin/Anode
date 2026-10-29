import type {
  Articulation,
  ControlEvent,
  ControlEventChange,
  ControlKind,
  EditCommand,
  MidiRegion,
  NoteChange
} from '@anode/api-client';
import type { MeterMap } from '@anode/timeline';
import { formatLength, formatPosition, parseLength, parsePosition } from '../midi-edit/position';
import { noteName } from '../midi/midi-math';

/**
 * The Event List's logic (docs/08-phase-7.md, "Event List"): its rows (every note and controller event of the shown
 * regions, in time order), the type filters, the cells as text, numeric edits turned into commands, and insert and
 * delete. Pure, so the component stays a view.
 */

/** An event's kind as the Event List shows it: a note, or one of the controller kinds. */
export type EventKind = 'note' | ControlKind;

/** The filter buttons: each shows a group of kinds. With none pressed, every kind shows. */
export type EventFilter = 'notes' | 'controls' | 'pitchBend' | 'pressure' | 'program';

export const EventFilters: readonly { value: EventFilter; label: string; kinds: readonly EventKind[] }[] = [
  { value: 'notes', label: 'Notes', kinds: ['note'] },
  { value: 'controls', label: 'Controls', kinds: ['controlChange'] },
  { value: 'pitchBend', label: 'Pitch Bend', kinds: ['pitchBend'] },
  { value: 'pressure', label: 'Pressure', kinds: ['channelPressure', 'polyPressure'] },
  { value: 'program', label: 'Program', kinds: ['programChange'] }
];

/** The columns, in order. */
export type EventColumn = 'position' | 'status' | 'channel' | 'num' | 'val' | 'length' | 'articulation';

export const EventColumns: readonly { key: EventColumn; label: string }[] = [
  { key: 'position', label: 'Position' },
  { key: 'status', label: 'Status' },
  { key: 'channel', label: 'Ch' },
  { key: 'num', label: 'Num' },
  { key: 'val', label: 'Val' },
  { key: 'length', label: 'Length' },
  { key: 'articulation', label: 'Articulation' }
];

/** The column widths (CSS grid template), shared by the header and the rows so they line up. */
export const EventGridColumns = '7.5rem 6.5rem 2.75rem 4.5rem 4.5rem 6.5rem minmax(7rem, 1fr)';

/** One row: a note or a controller event, placed in project time. */
export interface EventRowData {
  /** Unique among rows: `n<id>` for a note, `c<id>` for a controller event. */
  key: string;
  kind: EventKind;
  /** The note's or event's id. */
  id: number;
  regionId: string;
  regionStart: number;
  /** The event's position in project ticks (its region's start plus its own tick). */
  tick: number;
  channel: number;
  /** A note's pitch, a CC's controller, a poly pressure's key; 0 for the others. */
  num: number;
  /** A note's velocity, else the event's value. */
  value: number;
  /** A note's length in ticks; null for controller events. */
  length: number | null;
  /** A note's articulation (0: none). */
  articulationId: number;
}

/** A cell as text, for {@link EventColumn}s. */
export interface EventCellData {
  key: EventColumn;
  /** The column's name: the cell's and its editor's accessible name. */
  label: string;
  text: string;
  editable: boolean;
  /** A choice cell (articulation): its options, value as text. */
  options?: readonly { value: string; label: string }[];
  /** The value an editor starts from (the text, or a choice's value). */
  value?: string;
}

/** Controllers before notes at one position (Logic sends a program change before the note it sets up). */
const KindOrder: Readonly<Record<EventKind, number>> = {
  programChange: 0,
  controlChange: 1,
  pitchBend: 2,
  channelPressure: 3,
  polyPressure: 4,
  note: 5
};

/** Every event of `regions`, in time order (controllers before notes at one tick, then by pitch or number). */
export function buildRows(
  regions: readonly MidiRegion[],
  filters: ReadonlySet<EventFilter> = new Set()
): EventRowData[] {
  const shown = shownKinds(filters);
  const rows: EventRowData[] = [];
  for (const region of regions) {
    if (shown.has('note')) {
      for (const note of region.notes) {
        rows.push({
          key: `n${note.id}`,
          kind: 'note',
          id: note.id,
          regionId: region.id,
          regionStart: region.startTick,
          tick: region.startTick + note.startTick,
          channel: note.channel,
          num: note.pitch,
          value: note.velocity,
          length: note.lengthTicks,
          articulationId: note.articulationId
        });
      }
    }
    for (const control of region.controls) {
      if (shown.has(control.kind)) {
        rows.push(controlRow(region, control));
      }
    }
  }
  return rows.sort((a, b) => a.tick - b.tick || KindOrder[a.kind] - KindOrder[b.kind] || a.num - b.num || a.id - b.id);
}

function controlRow(region: MidiRegion, control: ControlEvent): EventRowData {
  return {
    key: `c${control.id}`,
    kind: control.kind,
    id: control.id,
    regionId: region.id,
    regionStart: region.startTick,
    tick: region.startTick + control.tick,
    channel: control.channel,
    num: control.number,
    value: control.value,
    length: null,
    articulationId: 0
  };
}

/** The kinds the pressed filters show: all when none is pressed. */
export function shownKinds(filters: ReadonlySet<EventFilter>): ReadonlySet<EventKind> {
  if (filters.size === 0) {
    return new Set(EventFilters.flatMap(f => f.kinds));
  }
  return new Set(EventFilters.filter(f => filters.has(f.value)).flatMap(f => f.kinds));
}

/** The Status column's text. */
export function statusText(kind: EventKind): string {
  switch (kind) {
    case 'note':
      return 'Note';
    case 'controlChange':
      return 'Control';
    case 'pitchBend':
      return 'Pitch Bend';
    case 'channelPressure':
      return 'Pressure';
    case 'polyPressure':
      return 'Poly Pressure';
    case 'programChange':
      return 'Program';
  }
}

/** Whether the Num column means something (and can be edited) for a kind. */
function hasNum(kind: EventKind): boolean {
  return kind === 'note' || kind === 'controlChange' || kind === 'polyPressure';
}

/** An articulation's label in the column and its menu: the symbol before the name when it has one. */
export function articulationLabel(articulation: Pick<Articulation, 'name' | 'symbol'>): string {
  return articulation.symbol ? `${articulation.symbol} ${articulation.name}` : articulation.name;
}

/** The cells of a row, as text. `articulations` is the track's set's list (null: the track has no set). */
export function rowCells(
  row: EventRowData,
  meter: MeterMap,
  articulations: readonly Articulation[] | null
): EventCellData[] {
  const note = row.kind === 'note';
  const num = note || row.kind === 'polyPressure' ? noteName(row.num) : hasNum(row.kind) ? `${row.num}` : '';
  const cells: EventCellData[] = [
    { key: 'position', label: 'Position', text: formatPosition(row.tick, meter), editable: true },
    { key: 'status', label: 'Status', text: statusText(row.kind), editable: false },
    { key: 'channel', label: 'Ch', text: `${row.channel}`, editable: true },
    { key: 'num', label: 'Num', text: num, editable: hasNum(row.kind) },
    { key: 'val', label: 'Val', text: `${row.value}`, editable: true },
    {
      key: 'length',
      label: 'Length',
      text: row.length === null ? '' : formatLength(row.length, meter, row.tick),
      editable: note
    }
  ];
  if (note && articulations && articulations.length > 0) {
    const current = articulations.find(a => a.id === row.articulationId);
    cells.push({
      key: 'articulation',
      label: 'Articulation',
      text: current ? articulationLabel(current) : row.articulationId ? `#${row.articulationId}` : '',
      editable: true,
      value: `${row.articulationId}`,
      options: [
        { value: '0', label: 'None' },
        ...articulations.map(a => ({ value: `${a.id}`, label: articulationLabel(a) }))
      ]
    });
  } else {
    cells.push({
      key: 'articulation',
      label: 'Articulation',
      text: note && row.articulationId ? `#${row.articulationId}` : '',
      editable: false
    });
  }
  return cells;
}

const PitchClasses: Readonly<Record<string, number>> = { c: 0, d: 2, e: 4, f: 5, g: 7, a: 9, b: 11 };

/** A pitch typed as a number (0…127) or a name as Logic writes it (`C3` is 60; `C#3`, `C♯3`, `Db3`, `D♭3`, `C-1`). */
export function parsePitch(text: string): number | null {
  const trimmed = text.trim();
  if (/^\d+$/.test(trimmed)) {
    const value = Number(trimmed);
    return value <= 127 ? value : null;
  }
  const match = /^([a-gA-G])([#♯b♭]?)(-?\d+)$/.exec(trimmed);
  if (!match) {
    return null;
  }
  const [, letter = '', accidental = '', octave = ''] = match;
  const shift = accidental === '#' || accidental === '♯' ? 1 : accidental === 'b' || accidental === '♭' ? -1 : 0;
  const pitch = (PitchClasses[letter.toLowerCase()] ?? 0) + shift + (Number(octave) + 2) * 12;
  return pitch >= 0 && pitch <= 127 ? pitch : null;
}

function parseInteger(text: string, min: number, max: number): number | null {
  const trimmed = text.trim().replace('−', '-');
  if (!/^-?\d+$/.test(trimmed)) {
    return null;
  }
  const value = Number(trimmed);
  return value >= min && value <= max ? value : null;
}

/** A row's value range: velocity 1…127, pitch bend −8192…8191, the rest 0…127. */
export function valueRange(kind: EventKind): [number, number] {
  if (kind === 'pitchBend') {
    return [-8192, 8191];
  }
  return kind === 'note' ? [1, 127] : [0, 127];
}

/** What an edit of a cell does: the command, or why the text was refused. */
export type CellEdit = { command: EditCommand } | { error: string } | { unchanged: true };

/**
 * The command a cell's typed text makes: `editNotes` for a note, `setControls` for a controller event, positions
 * relative to the event's region. Text that is not a value of the column is refused with a reason.
 */
export function editCell(row: EventRowData, column: EventColumn, text: string, meter: MeterMap): CellEdit {
  const note = row.kind === 'note';
  let field: 'tick' | 'length' | 'channel' | 'num' | 'value' | 'articulationId';
  let value: number | null;
  switch (column) {
    case 'position': {
      const tick = parsePosition(text, meter);
      if (tick === null) {
        return { error: `“${text}” is not a position (bar beat division tick).` };
      }
      if (tick < row.regionStart) {
        return { error: 'An event cannot move before its region’s start.' };
      }
      field = 'tick';
      value = tick - row.regionStart;
      break;
    }
    case 'length': {
      if (!note) {
        return { error: 'Only notes have a length.' };
      }
      value = parseLength(text, meter, row.tick);
      if (value === null || value < 1) {
        return { error: `“${text}” is not a length (bars beats divisions ticks).` };
      }
      field = 'length';
      break;
    }
    case 'channel':
      value = parseInteger(text, 1, 16);
      if (value === null) {
        return { error: 'A channel is 1 to 16.' };
      }
      field = 'channel';
      break;
    case 'num':
      if (!hasNum(row.kind)) {
        return { error: `A ${statusText(row.kind).toLowerCase()} event has no number.` };
      }
      value = row.kind === 'controlChange' ? parseInteger(text, 0, 127) : parsePitch(text);
      if (value === null) {
        return { error: row.kind === 'controlChange' ? 'A controller is 0 to 127.' : `“${text}” is not a note.` };
      }
      field = 'num';
      break;
    case 'val': {
      const [min, max] = valueRange(row.kind);
      value = parseInteger(text, min, max);
      if (value === null) {
        return { error: `A value is ${min} to ${max}.` };
      }
      field = 'value';
      break;
    }
    case 'articulation':
      if (!note) {
        return { error: 'Only notes have an articulation.' };
      }
      value = parseInteger(text, 0, 254);
      if (value === null) {
        return { error: 'Not an articulation.' };
      }
      field = 'articulationId';
      break;
    case 'status':
      return { error: 'An event’s kind cannot change: delete it and insert another.' };
  }

  const before: Record<typeof field, number> = {
    tick: row.tick - row.regionStart,
    length: row.length ?? 0,
    channel: row.channel,
    num: row.num,
    value: row.value,
    articulationId: row.articulationId
  };
  if (before[field] === value) {
    return { unchanged: true };
  }
  if (note) {
    const names = {
      tick: 'startTick',
      length: 'lengthTicks',
      channel: 'channel',
      num: 'pitch',
      value: 'velocity',
      articulationId: 'articulationId'
    } as const;
    const change: NoteChange = { id: row.id, [names[field]]: value };
    return { command: { type: 'editNotes', regionId: row.regionId, changes: [change] } };
  }
  if (field === 'length' || field === 'articulationId') {
    return { unchanged: true };
  }
  const controlNames = { tick: 'tick', channel: 'channel', num: 'number', value: 'value' } as const;
  const change: ControlEventChange = { id: row.id, [controlNames[field]]: value };
  return { command: { type: 'setControls', regionId: row.regionId, changes: [change] } };
}

/** The undo label of a cell edit ("Note Velocity", "Control Position"). */
export function editLabel(row: EventRowData, column: EventColumn): string {
  const name = EventColumns.find(c => c.key === column)?.label ?? column;
  const field =
    column === 'val' ? (row.kind === 'note' ? 'Velocity' : 'Value') : column === 'channel' ? 'Channel' : name;
  return `${statusText(row.kind)} ${field}`;
}

/** The kinds the "+" menu inserts, with their defaults. */
export const InsertKinds: readonly { kind: EventKind; label: string }[] = [
  { kind: 'note', label: 'Note' },
  { kind: 'controlChange', label: 'Control Change' },
  { kind: 'programChange', label: 'Program Change' },
  { kind: 'pitchBend', label: 'Pitch Bend' },
  { kind: 'channelPressure', label: 'Channel Pressure' },
  { kind: 'polyPressure', label: 'Poly Pressure' }
];

/** The defaults an inserted event gets: a 1/16 C3 at velocity 100, a CC 1 (modulation), the rest at 0. */
export const InsertDefaults = { pitch: 60, velocity: 100, lengthTicks: 240, controller: 1, key: 60 } as const;

/**
 * The command inserting an event of `kind` at project tick `tick`: into the shown region holding it (the one
 * starting last where several do), else the first shown region, at its start or end when the tick is outside it.
 */
export function insertCommand(
  kind: EventKind,
  tick: number,
  regions: readonly Pick<MidiRegion, 'id' | 'startTick' | 'lengthTicks'>[],
  channel = 1
): EditCommand | null {
  let region = null as Pick<MidiRegion, 'id' | 'startTick' | 'lengthTicks'> | null;
  for (const r of regions) {
    if (tick >= r.startTick && tick < r.startTick + r.lengthTicks && (!region || r.startTick >= region.startTick)) {
      region = r;
    }
  }
  region ??= regions[0] ?? null;
  if (!region) {
    return null;
  }
  const relative = Math.max(0, Math.min(Math.max(0, region.lengthTicks - 1), Math.round(tick - region.startTick)));
  if (kind === 'note') {
    return {
      type: 'addNotes',
      regionId: region.id,
      notes: [
        {
          pitch: InsertDefaults.pitch,
          velocity: InsertDefaults.velocity,
          channel,
          startTick: relative,
          lengthTicks: InsertDefaults.lengthTicks
        }
      ]
    };
  }
  const number =
    kind === 'controlChange' ? InsertDefaults.controller : kind === 'polyPressure' ? InsertDefaults.key : 0;
  return { type: 'addControls', regionId: region.id, events: [{ kind, channel, number, value: 0, tick: relative }] };
}

/** Commands deleting the selected rows: a `deleteNotes` and a `deleteControls` per region (one undo step). */
export function deleteRowsCommands(rows: readonly EventRowData[]): EditCommand[] {
  const notes = new Map<string, number[]>();
  const controls = new Map<string, number[]>();
  for (const row of rows) {
    const map = row.kind === 'note' ? notes : controls;
    map.set(row.regionId, [...(map.get(row.regionId) ?? []), row.id]);
  }
  return [
    ...[...notes].map(([regionId, noteIds]): EditCommand => ({ type: 'deleteNotes', regionId, noteIds })),
    ...[...controls].map(([regionId, eventIds]): EditCommand => ({ type: 'deleteControls', regionId, eventIds }))
  ];
}

/** The row playing at `tick`: the last row at or before it; -1 before the first. */
export function playingRow(rows: readonly Pick<EventRowData, 'tick'>[], tick: number): number {
  let lo = 0;
  let hi = rows.length - 1;
  let found = -1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    if ((rows[mid]?.tick ?? 0) <= tick) {
      found = mid;
      lo = mid + 1;
    } else {
      hi = mid - 1;
    }
  }
  return found;
}

/**
 * The selection after a click on `rows[index]`: just it; with ⌘, it toggled in or out; with ⇧, every row from the
 * anchor (the last plain click) to it.
 */
export function clickSelection(
  rows: readonly Pick<EventRowData, 'key'>[],
  selected: ReadonlySet<string>,
  index: number,
  anchor: number,
  modifiers: { shift: boolean; toggle: boolean }
): Set<string> {
  const key = rows[index]?.key;
  if (key === undefined) {
    return new Set(selected);
  }
  if (modifiers.shift && anchor >= 0 && anchor < rows.length) {
    const [from, to] = anchor <= index ? [anchor, index] : [index, anchor];
    return new Set([...selected, ...rows.slice(from, to + 1).map(r => r.key)]);
  }
  if (modifiers.toggle) {
    const next = new Set(selected);
    if (!next.delete(key)) {
      next.add(key);
    }
    return next;
  }
  return new Set([key]);
}
