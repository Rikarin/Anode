import type { EditCommand, MidiRegion, Note, NoteChange } from '@anode/api-client';
import { snapTick } from '@anode/timeline';

/** The shortest note a resize leaves, in ticks, when snapping is off. */
export const MinNoteTicks = 10;

/** A note as the Piano Roll places it: in project ticks (its region's start plus its own), on a pitch. */
export interface PlacedNote {
  /** The note's id (unique in the project). */
  id: number;
  regionId: string;
  /** Absolute start, in ticks. */
  start: number;
  length: number;
  pitch: number;
  velocity: number;
  /** MIDI channel, 1…16. */
  channel: number;
}

/** A region's span in project ticks. */
export interface RegionSpan {
  id: string;
  start: number;
  end: number;
}

/** The notes of several regions in project time, in region order. */
export function placeNotes(regions: readonly MidiRegion[]): PlacedNote[] {
  return regions.flatMap(region =>
    region.notes.map(note => ({
      id: note.id,
      regionId: region.id,
      start: region.startTick + note.startTick,
      length: note.lengthTicks,
      pitch: note.pitch,
      velocity: note.velocity,
      channel: note.channel
    }))
  );
}

/** Whether a placed note plays: it starts inside its region (a note before the start or at the end does not). */
export function notePlays(note: Pick<PlacedNote, 'start'>, region: RegionSpan): boolean {
  return note.start >= region.start && note.start < region.end;
}

/** The region whose span holds `tick`; where several do, the one starting last (it wins playback too). */
export function regionAt(regions: readonly RegionSpan[], tick: number): RegionSpan | undefined {
  let found: RegionSpan | undefined;
  for (const region of regions) {
    if (tick >= region.start && tick < region.end && (!found || region.start >= found.start)) {
      found = region;
    }
  }
  return found;
}

/**
 * Where a drag moves the selected notes: by the same time and pitch. The grabbed note's start snaps to the grid and
 * the others keep their offsets from it; no note goes before the project start or off the keyboard.
 */
export function moveNotes(
  notes: readonly PlacedNote[],
  grabbed: Pick<PlacedNote, 'start'>,
  deltaTick: number,
  deltaPitch: number,
  snap: number
): PlacedNote[] {
  if (notes.length === 0) {
    return [];
  }

  let delta = snapTick(grabbed.start + deltaTick, snap) - grabbed.start;
  delta = Math.max(delta, -Math.min(...notes.map(n => n.start)));
  const lowest = Math.min(...notes.map(n => n.pitch));
  const highest = Math.max(...notes.map(n => n.pitch));
  const pitch = Math.min(Math.max(Math.round(deltaPitch), -lowest), 127 - highest);
  return notes.map(n => ({ ...n, start: Math.round(n.start + delta), pitch: n.pitch + pitch }));
}

/** A note's end moved to `tick` (snapped), keeping at least a grid step (or {@link MinNoteTicks}). */
export function resizeEnd(note: PlacedNote, tick: number, snap: number): PlacedNote {
  const minimum = snap > 0 ? snap : MinNoteTicks;
  const end = snap > 0 ? snapTick(tick, snap) : Math.round(tick);
  return { ...note, length: Math.max(minimum, end - note.start) };
}

/** A note's start moved to `tick` (snapped), its end kept, at least a grid step (or {@link MinNoteTicks}) long. */
export function resizeStart(note: PlacedNote, tick: number, snap: number): PlacedNote {
  const minimum = snap > 0 ? snap : MinNoteTicks;
  const end = note.start + note.length;
  const start = Math.min(end - minimum, snapTick(tick, snap));
  return { ...note, start, length: end - start };
}

/**
 * The commands that make `edited` so: an `editNotes` per region for the notes that changed, positions relative to
 * their region's start (`regions` by id). With `copy`, the edited notes are added as new ones (⌥-drag) instead.
 */
export function noteCommands(
  edited: readonly PlacedNote[],
  original: ReadonlyMap<number, PlacedNote>,
  regions: ReadonlyMap<string, Pick<MidiRegion, 'startTick'>>,
  copy = false
): EditCommand[] {
  const changes = new Map<string, NoteChange[]>();
  const added = new Map<string, Note[]>();
  for (const note of edited) {
    const before = original.get(note.id);
    const region = regions.get(note.regionId);
    if (!before || !region) {
      continue;
    }

    const relative = note.start - region.startTick;
    if (copy) {
      const list = added.get(note.regionId) ?? [];
      list.push({ ...noteOf(note, relative), id: 0 });
      added.set(note.regionId, list);
      continue;
    }

    const change: NoteChange = { id: note.id };
    if (note.start !== before.start) {
      change.startTick = relative;
    }
    if (note.length !== before.length) {
      change.lengthTicks = note.length;
    }
    if (note.pitch !== before.pitch) {
      change.pitch = note.pitch;
    }
    if (note.velocity !== before.velocity) {
      change.velocity = note.velocity;
    }
    if (Object.keys(change).length > 1) {
      const list = changes.get(note.regionId) ?? [];
      list.push(change);
      changes.set(note.regionId, list);
    }
  }

  const commands: EditCommand[] = [];
  for (const [regionId, list] of changes) {
    commands.push({ type: 'editNotes', regionId, changes: list });
  }
  for (const [regionId, list] of added) {
    commands.push({
      type: 'addNotes',
      regionId,
      notes: list.map(n => ({
        pitch: n.pitch,
        velocity: n.velocity,
        channel: n.channel,
        startTick: n.startTick,
        lengthTicks: n.lengthTicks
      }))
    });
  }
  return commands;
}

function noteOf(note: PlacedNote, relative: number): Note {
  return {
    id: note.id,
    pitch: note.pitch,
    velocity: note.velocity,
    channel: note.channel,
    startTick: relative,
    lengthTicks: note.length
  };
}

/** Notes transposed by `semitones`, none off the keyboard (the whole selection stops at the edge, as in Logic). */
export function transposeNotes(notes: readonly PlacedNote[], semitones: number): PlacedNote[] {
  if (notes.length === 0) {
    return [];
  }
  const lowest = Math.min(...notes.map(n => n.pitch));
  const highest = Math.max(...notes.map(n => n.pitch));
  const by = Math.min(Math.max(semitones, -lowest), 127 - highest);
  return by === 0 ? [] : notes.map(n => ({ ...n, pitch: n.pitch + by }));
}

/**
 * The note the pencil puts at `tick` and `pitch`: its start snapped (to the grid line at or before the click, as Logic
 * does), the length given, in the region that holds that start; null outside every region.
 */
export function pencilNote(
  regions: readonly RegionSpan[],
  tick: number,
  pitch: number,
  snap: number,
  length: number,
  velocity: number
): { regionId: string; startTick: number; lengthTicks: number; pitch: number; velocity: number } | null {
  const start = snap > 0 ? Math.max(0, Math.floor(tick / snap) * snap) : Math.max(0, Math.round(tick));
  const region = regionAt(regions, start);
  if (!region || pitch < 0 || pitch > 127) {
    return null;
  }
  return {
    regionId: region.id,
    startTick: start - region.start,
    lengthTicks: Math.max(MinNoteTicks, Math.round(length)),
    pitch,
    velocity
  };
}

/** Commands deleting `notes`, one `deleteNotes` per region. */
export function deleteCommands(notes: readonly Pick<PlacedNote, 'id' | 'regionId'>[]): EditCommand[] {
  const byRegion = new Map<string, number[]>();
  for (const note of notes) {
    byRegion.set(note.regionId, [...(byRegion.get(note.regionId) ?? []), note.id]);
  }
  return [...byRegion].map(([regionId, noteIds]) => ({ type: 'deleteNotes', regionId, noteIds }));
}

/**
 * Commands quantising notes to `grid` (destructive, `quantizeNotes`): the selected notes of each region, or every note
 * of the regions shown when none is selected.
 */
export function quantizeCommands(
  regions: readonly Pick<MidiRegion, 'id' | 'notes'>[],
  selected: ReadonlySet<number>,
  grid: number
): EditCommand[] {
  if (!(grid > 0)) {
    return [];
  }

  const commands: EditCommand[] = [];
  for (const region of regions) {
    if (selected.size === 0) {
      if (region.notes.length > 0) {
        commands.push({ type: 'quantizeNotes', regionId: region.id, gridTicks: grid, strength: 1, swing: 0 });
      }
      continue;
    }
    const ids = region.notes.filter(n => selected.has(n.id)).map(n => n.id);
    if (ids.length > 0) {
      commands.push({
        type: 'quantizeNotes',
        regionId: region.id,
        gridTicks: grid,
        strength: 1,
        swing: 0,
        noteIds: ids
      });
    }
  }
  return commands;
}

/** A velocity from a pointer's height in the velocity lane (top: 127, bottom: 1). */
export function velocityAtY(y: number, height: number): number {
  const fraction = 1 - Math.min(1, Math.max(0, y / Math.max(1, height)));
  return Math.max(1, Math.min(127, Math.round(1 + fraction * 126)));
}

/** Velocities after a drag of `delta` on a selection: each moved by the same amount, kept within 1…127. */
export function offsetVelocities(notes: readonly PlacedNote[], delta: number): PlacedNote[] {
  return notes.map(n => ({ ...n, velocity: Math.max(1, Math.min(127, Math.round(n.velocity + delta))) }));
}

/** "Move Note" or "Move 3 Notes". */
export function notesLabel(verb: string, count: number): string {
  return count === 1 ? `${verb} Note` : `${verb} ${count} Notes`;
}
