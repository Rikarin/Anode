import type { MidiRegion } from '@anode/api-client';
import { hitTest, LaneIntervalIndex, RULER_HEIGHT, type TimelineView } from '@anode/timeline';
import { NeutralPlayback, playedNote, quantizeNotes, quantizeTick, velocityColor } from '../midi/midi-math';
import {
  deleteCommands,
  moveNotes,
  noteCommands,
  notePlays,
  offsetVelocities,
  pencilNote,
  type PlacedNote,
  placeNotes,
  quantizeCommands,
  regionAt,
  resizeEnd,
  resizeStart,
  transposeNotes,
  velocityAtY
} from './note-edit';
import { buildNotesScene, laneOfPitch, pitchOfLane } from './notes-scene';

function region(
  id: string,
  startTick: number,
  lengthTicks: number,
  notes: [number, number, number, number][]
): MidiRegion {
  return {
    id,
    trackId: 'trk_1',
    name: id,
    startTick,
    lengthTicks,
    playback: { ...NeutralPlayback },
    notes: notes.map(([noteId, pitch, start, length]) => ({
      id: noteId,
      pitch,
      velocity: 100,
      channel: 1,
      startTick: start,
      lengthTicks: length
    })),
    controls: []
  };
}

// Two regions of one track: bar 1 (two notes) and bar 3 (one note, plus one that starts before the region).
const regions = [
  region('mid_a', 0, 3840, [
    [1, 60, 0, 480],
    [2, 64, 960, 480]
  ]),
  region('mid_b', 7680, 3840, [
    [3, 67, 240, 240],
    [4, 72, -240, 480]
  ])
];
const spans = regions.map(r => ({ id: r.id, start: r.startTick, end: r.startTick + r.lengthTicks }));
const placed = placeNotes(regions);
const byId = new Map(placed.map(n => [n.id, n]));
const note = (id: number): PlacedNote => byId.get(id) as PlacedNote;

describe('quantise maths', () => {
  it('moves a tick to the nearest grid line, by the strength', () => {
    expect(quantizeTick(250, 240)).toBe(240);
    expect(quantizeTick(370, 240)).toBe(480);
    expect(quantizeTick(300, 240, 0.5)).toBe(270);
    expect(quantizeTick(300, 0)).toBe(300);
  });

  it('delays every second grid line by the swing', () => {
    // A 1/8 grid with full swing: the off-beat (480) moves to 480 + 240.
    expect(quantizeTick(500, 480, 1, 1)).toBe(720);
    expect(quantizeTick(960, 480, 1, 1)).toBe(960);
    expect(quantizeTick(600, 480, 1, 0.5)).toBe(600);
  });

  it('quantises relative notes to the absolute grid', () => {
    const offset = region('mid_c', 100, 3840, [[9, 60, 150, 100]]);
    expect(quantizeNotes(offset, 240)[0]?.startTick).toBe(140); // 250 absolute → 240
    expect(quantizeNotes(offset, 240, 1, 0, new Set([1]))[0]?.startTick).toBe(150);
  });

  it('plays a note through its region playback, clamped', () => {
    const r = { startTick: 0, playback: { ...NeutralPlayback, transpose: 100, velocity: -127, quantizeTicks: 240 } };
    const played = playedNote({ id: 1, pitch: 60, velocity: 100, channel: 1, startTick: 250, lengthTicks: 10 }, r);
    expect(played).toMatchObject({ pitch: 127, velocity: 1, startTick: 240 });
  });

  it('colours soft notes cool and loud ones warm', () => {
    expect(velocityColor(1)).toMatch(/^#[0-9a-f]{6}$/);
    const red = (hex: string) => parseInt(hex.slice(1, 3), 16);
    const blue = (hex: string) => parseInt(hex.slice(5, 7), 16);
    expect(red(velocityColor(127))).toBeGreaterThan(red(velocityColor(10)));
    expect(blue(velocityColor(10))).toBeGreaterThan(blue(velocityColor(127)));
  });
});

describe('the notes scene', () => {
  const scene = buildNotesScene(regions, new Set([2]), 4);

  it('places notes in project time on their pitch lanes', () => {
    expect(scene.notes.map(n => n.start)).toEqual([0, 960, 7920, 7440]);
    expect(Array.from(scene.scene.items.lane)).toEqual([67, 63, 60, 55]);
    expect(laneOfPitch(127)).toBe(0);
    expect(pitchOfLane(67.7)).toBe(60);
  });

  it('flags selected notes and the ones that do not play', () => {
    expect(Array.from(scene.scene.noteFlags ?? [])).toEqual([0, 1, 0, 2]);
    expect(notePlays(note(4), spans[1] ?? { id: '', start: 0, end: 0 })).toBe(false);
  });

  it('hit-tests notes: their body moves, their ends resize', () => {
    const view: TimelineView = {
      originTick: 0,
      pixelsPerTick: 0.1,
      firstLane: 60,
      laneHeight: 10,
      playheadTick: 0
    };
    const index = new LaneIntervalIndex(scene.scene.items, 128);
    const y = RULER_HEIGHT + (laneOfPitch(64) - 60) * 10 + 5;
    expect(hitTest(scene.scene, index, view, 120, y)).toEqual({ index: 1, zone: 'body' });
    expect(hitTest(scene.scene, index, view, 97, y)).toEqual({ index: 1, zone: 'trim-start' });
    expect(hitTest(scene.scene, index, view, 143, y)).toEqual({ index: 1, zone: 'trim-end' });
    expect(hitTest(scene.scene, index, view, 120, y + 10)).toBeNull();
  });
});

describe('note edits', () => {
  it('moves notes by snapped time and whole semitones, none off the keyboard', () => {
    const moved = moveNotes([note(1), note(2)], note(1), 250, 2, 240);
    expect(moved.map(n => [n.start, n.pitch])).toEqual([
      [240, 62],
      [1200, 66]
    ]);
    expect(moveNotes([note(1)], note(1), -5000, 100, 240)[0]).toMatchObject({ start: 0, pitch: 127 });
  });

  it('resizes either end on the grid, never shorter than a step', () => {
    expect(resizeEnd(note(1), 1000, 240).length).toBe(960);
    expect(resizeEnd(note(1), 10, 240).length).toBe(240);
    expect(resizeStart(note(2), 700, 240)).toMatchObject({ start: 720, length: 720 });
    expect(resizeStart(note(2), 2000, 240)).toMatchObject({ start: 1200, length: 240 });
  });

  it('turns edited notes into editNotes per region, relative to its start', () => {
    const regionsById = new Map(regions.map(r => [r.id, r]));
    const moved = [{ ...note(1), start: 240 }, { ...note(3), pitch: 70 }, note(2)];
    expect(noteCommands(moved, byId, regionsById)).toEqual([
      { type: 'editNotes', regionId: 'mid_a', changes: [{ id: 1, startTick: 240 }] },
      { type: 'editNotes', regionId: 'mid_b', changes: [{ id: 3, pitch: 70 }] }
    ]);
  });

  it('copies notes with ⌥ as new ones', () => {
    const regionsById = new Map(regions.map(r => [r.id, r]));
    expect(noteCommands([{ ...note(3), start: 8640 }], byId, regionsById, true)).toEqual([
      {
        type: 'addNotes',
        regionId: 'mid_b',
        notes: [{ pitch: 67, velocity: 100, channel: 1, startTick: 960, lengthTicks: 240 }]
      }
    ]);
  });

  it('transposes a selection as a block, stopping at the keyboard edge', () => {
    expect(transposeNotes([note(1), note(2)], 12).map(n => n.pitch)).toEqual([72, 76]);
    expect(transposeNotes([note(4)], 100).map(n => n.pitch)).toEqual([127]);
    expect(transposeNotes([{ ...note(4), pitch: 127 }], 1)).toEqual([]);
  });

  it('draws a note with the pencil inside the region that holds it', () => {
    expect(pencilNote(spans, 8000, 62, 240, 480, 90)).toEqual({
      regionId: 'mid_b',
      startTick: 240,
      lengthTicks: 480,
      pitch: 62,
      velocity: 90
    });
    expect(pencilNote(spans, 5000, 62, 240, 480, 90)).toBeNull();
    expect(regionAt(spans, 3839)?.id).toBe('mid_a');
  });

  it('deletes and quantises per region', () => {
    expect(deleteCommands([note(1), note(3), note(2)])).toEqual([
      { type: 'deleteNotes', regionId: 'mid_a', noteIds: [1, 2] },
      { type: 'deleteNotes', regionId: 'mid_b', noteIds: [3] }
    ]);
    expect(quantizeCommands(regions, new Set([3]), 240)).toEqual([
      { type: 'quantizeNotes', regionId: 'mid_b', gridTicks: 240, strength: 1, swing: 0, noteIds: [3] }
    ]);
    expect(quantizeCommands(regions, new Set(), 480).map(c => c.type)).toEqual(['quantizeNotes', 'quantizeNotes']);
  });

  it('edits velocities from the lane', () => {
    expect(velocityAtY(0, 100)).toBe(127);
    expect(velocityAtY(100, 100)).toBe(1);
    expect(offsetVelocities([note(1), { ...note(2), velocity: 120 }], 20).map(n => n.velocity)).toEqual([120, 127]);
  });
});
