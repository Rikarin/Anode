import type { MidiRegion } from '@anode/api-client';
import { MeterMap } from '@anode/timeline';
import {
  buildRows,
  clickSelection,
  deleteRowsCommands,
  editCell,
  editLabel,
  insertCommand,
  parsePitch,
  playingRow,
  rowCells,
  shownKinds
} from './event-rows';

const meter = MeterMap.constant(4, 4);

function region(): MidiRegion {
  return {
    id: 'mid_1',
    trackId: 'trk_1',
    name: 'Riff',
    startTick: 3840,
    lengthTicks: 7680,
    playback: {} as MidiRegion['playback'],
    muted: false,
    notes: [
      { id: 1, pitch: 60, velocity: 100, channel: 1, startTick: 0, lengthTicks: 480, articulationId: 0 },
      { id: 2, pitch: 64, velocity: 90, channel: 1, startTick: 960, lengthTicks: 240, articulationId: 2 }
    ],
    controls: [
      { id: 10, kind: 'programChange', channel: 1, number: 0, value: 5, tick: 0 },
      { id: 11, kind: 'controlChange', channel: 1, number: 74, value: 64, tick: 480 },
      { id: 12, kind: 'pitchBend', channel: 2, number: 0, value: -4096, tick: 960 },
      { id: 13, kind: 'channelPressure', channel: 1, number: 0, value: 30, tick: 1440 },
      { id: 14, kind: 'polyPressure', channel: 1, number: 62, value: 40, tick: 1440 }
    ]
  };
}

describe('Event List rows', () => {
  it('lists every event in time order, controllers before notes at one tick', () => {
    const rows = buildRows([region()]);
    expect(rows.map(r => r.key)).toEqual(['c10', 'n1', 'c11', 'c12', 'n2', 'c13', 'c14']);
    expect(rows[1]).toMatchObject({ kind: 'note', tick: 3840, num: 60, value: 100, length: 480, regionStart: 3840 });
  });

  it('filters by kind: none pressed shows everything, pressed filters show only their kinds', () => {
    expect(shownKinds(new Set()).size).toBe(6);
    expect(buildRows([region()], new Set(['notes'])).map(r => r.key)).toEqual(['n1', 'n2']);
    expect(buildRows([region()], new Set(['pressure', 'program'])).map(r => r.key)).toEqual(['c10', 'c13', 'c14']);
    expect(buildRows([region()], new Set(['controls', 'pitchBend'])).map(r => r.key)).toEqual(['c11', 'c12']);
  });

  it('shows cells as Logic does: positions, statuses, pitch names, lengths, articulations', () => {
    const rows = buildRows([region()]);
    const text = (key: string) =>
      rowCells(rows.find(r => r.key === key) ?? (rows[0] as never), meter, [
        { id: 1, name: 'Legato', symbol: null },
        { id: 2, name: 'Staccato', symbol: '•' }
      ]).map(c => c.text);
    expect(text('n1')).toEqual(['2 1 1 1', 'Note', '1', 'C3', '100', '0 0 2 0', '']);
    expect(text('n2')).toEqual(['2 2 1 1', 'Note', '1', 'E3', '90', '0 0 1 0', '• Staccato']);
    expect(text('c11')).toEqual(['2 1 3 1', 'Control', '1', '74', '64', '', '']);
    expect(text('c12')).toEqual(['2 2 1 1', 'Pitch Bend', '2', '', '-4096', '', '']);
    expect(text('c14')).toEqual(['2 2 3 1', 'Poly Pressure', '1', 'D3', '40', '', '']);
    const note = rowCells(rows[1] as never, meter, [{ id: 1, name: 'Legato' }]);
    expect(note.find(c => c.key === 'articulation')?.options?.map(o => o.label)).toEqual(['None', 'Legato']);
    expect(rowCells(rows[1] as never, meter, null).find(c => c.key === 'articulation')?.editable).toBe(false);
    expect(rowCells(rows[0] as never, meter, null).map(c => c.editable)).toEqual([
      true,
      false,
      true,
      false,
      true,
      false,
      false
    ]);
  });

  it('parses pitches as numbers or names', () => {
    expect(parsePitch('60')).toBe(60);
    expect(parsePitch('C3')).toBe(60);
    expect(parsePitch('c#3')).toBe(61);
    expect(parsePitch('D♭3')).toBe(61);
    expect(parsePitch('C-2')).toBe(0);
    expect(parsePitch('G8')).toBe(127);
    expect(parsePitch('A8')).toBeNull();
    expect(parsePitch('128')).toBeNull();
    expect(parsePitch('H3')).toBeNull();
  });

  it('turns note edits into editNotes, relative to the region', () => {
    const note = buildRows([region()]).find(r => r.key === 'n1');
    if (!note) {
      throw new Error('no note');
    }
    expect(editCell(note, 'position', '2 3 1 1', meter)).toEqual({
      command: { type: 'editNotes', regionId: 'mid_1', changes: [{ id: 1, startTick: 1920 }] }
    });
    expect(editCell(note, 'length', '0 1 0 0', meter)).toEqual({
      command: { type: 'editNotes', regionId: 'mid_1', changes: [{ id: 1, lengthTicks: 960 }] }
    });
    expect(editCell(note, 'val', '64', meter)).toEqual({
      command: { type: 'editNotes', regionId: 'mid_1', changes: [{ id: 1, velocity: 64 }] }
    });
    expect(editCell(note, 'num', 'D3', meter)).toEqual({
      command: { type: 'editNotes', regionId: 'mid_1', changes: [{ id: 1, pitch: 62 }] }
    });
    expect(editCell(note, 'channel', '3', meter)).toEqual({
      command: { type: 'editNotes', regionId: 'mid_1', changes: [{ id: 1, channel: 3 }] }
    });
    expect(editCell(note, 'articulation', '2', meter)).toEqual({
      command: { type: 'editNotes', regionId: 'mid_1', changes: [{ id: 1, articulationId: 2 }] }
    });
    expect(editCell(note, 'val', '100', meter)).toEqual({ unchanged: true });
    expect(editLabel(note, 'val')).toBe('Note Velocity');
  });

  it('turns controller edits into setControls', () => {
    const rows = buildRows([region()]);
    const cc = rows.find(r => r.key === 'c11');
    const bend = rows.find(r => r.key === 'c12');
    if (!cc || !bend) {
      throw new Error('no events');
    }
    expect(editCell(cc, 'position', '2 2', meter)).toEqual({
      command: { type: 'setControls', regionId: 'mid_1', changes: [{ id: 11, tick: 960 }] }
    });
    expect(editCell(cc, 'num', '1', meter)).toEqual({
      command: { type: 'setControls', regionId: 'mid_1', changes: [{ id: 11, number: 1 }] }
    });
    expect(editCell(cc, 'val', '127', meter)).toEqual({
      command: { type: 'setControls', regionId: 'mid_1', changes: [{ id: 11, value: 127 }] }
    });
    expect(editCell(bend, 'val', '−8192', meter)).toEqual({
      command: { type: 'setControls', regionId: 'mid_1', changes: [{ id: 12, value: -8192 }] }
    });
    expect(editLabel(cc, 'val')).toBe('Control Value');
  });

  it('refuses text that is not a value of the column, without a command', () => {
    const rows = buildRows([region()]);
    const note = rows.find(r => r.key === 'n1') as never;
    const cc = rows.find(r => r.key === 'c11') as never;
    const program = rows.find(r => r.key === 'c10') as never;
    for (const [row, column, text] of [
      [note, 'position', 'soon'],
      [note, 'position', '1 1 1 1'], // before the region's start (bar 2)
      [note, 'length', '0'],
      [note, 'length', 'x'],
      [note, 'val', '0'],
      [note, 'val', '128'],
      [note, 'channel', '17'],
      [note, 'num', 'Q3'],
      [cc, 'val', '-1'],
      [cc, 'num', '200'],
      [cc, 'length', '1'],
      [program, 'num', '3'],
      [program, 'status', 'Note']
    ] as const) {
      expect('error' in editCell(row, column, text, meter), `${column} ${text}`).toBe(true);
    }
  });

  it('inserts at a tick in the region holding it, or the first region', () => {
    const regions = [region(), { ...region(), id: 'mid_2', startTick: 15360 }];
    expect(insertCommand('note', 4800, regions)).toEqual({
      type: 'addNotes',
      regionId: 'mid_1',
      notes: [{ pitch: 60, velocity: 100, channel: 1, startTick: 960, lengthTicks: 240 }]
    });
    expect(insertCommand('controlChange', 15360, regions, 2)).toEqual({
      type: 'addControls',
      regionId: 'mid_2',
      events: [{ kind: 'controlChange', channel: 2, number: 1, value: 0, tick: 0 }]
    });
    expect(insertCommand('programChange', 0, regions)).toEqual({
      type: 'addControls',
      regionId: 'mid_1',
      events: [{ kind: 'programChange', channel: 1, number: 0, value: 0, tick: 0 }]
    });
    expect(insertCommand('polyPressure', 3840, regions)).toMatchObject({ events: [{ number: 60 }] });
    expect(insertCommand('note', 0, [])).toBeNull();
  });

  it('deletes notes and controller events in one batch', () => {
    const rows = buildRows([region()]).filter(r => ['n1', 'c11', 'c12'].includes(r.key));
    expect(deleteRowsCommands(rows)).toEqual([
      { type: 'deleteNotes', regionId: 'mid_1', noteIds: [1] },
      { type: 'deleteControls', regionId: 'mid_1', eventIds: [11, 12] }
    ]);
  });

  it('finds the row at the playhead', () => {
    const rows = [{ tick: 0 }, { tick: 480 }, { tick: 480 }, { tick: 960 }];
    expect(playingRow(rows, -1)).toBe(-1);
    expect(playingRow(rows, 0)).toBe(0);
    expect(playingRow(rows, 500)).toBe(2);
    expect(playingRow(rows, 5000)).toBe(3);
  });

  it('selects by click, ⌘-click and ⇧-click', () => {
    const rows = ['a', 'b', 'c', 'd'].map(key => ({ key }));
    const plain = { shift: false, toggle: false };
    expect([...clickSelection(rows, new Set(['a']), 2, 0, plain)]).toEqual(['c']);
    expect([...clickSelection(rows, new Set(['a']), 2, 0, { shift: false, toggle: true })]).toEqual(['a', 'c']);
    expect([...clickSelection(rows, new Set(['a', 'c']), 2, 0, { shift: false, toggle: true })]).toEqual(['a']);
    expect([...clickSelection(rows, new Set(['b']), 3, 1, { shift: true, toggle: false })]).toEqual(['b', 'c', 'd']);
  });
});
