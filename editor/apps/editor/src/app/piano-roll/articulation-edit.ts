import type { Articulation, EditCommand, MidiRegion, NoteChange } from '@anode/api-client';
import { offsetVelocities, type PlacedNote, transposeNotes } from './note-edit';

/** A note's articulation as the Piano Roll's articulation lane shows it. */
export interface ArticulatedNote {
  id: number;
  regionId: string;
  pitch: number;
  articulationId: number;
}

/** The notes that start at one tick (a chord shows as one chip in the lane). */
export interface ArticulationGroup {
  /** Project tick. */
  start: number;
  /** Its longest note's length, in ticks. */
  length: number;
  notes: ArticulatedNote[];
}

/** The notes of `regions` grouped by their start (project ticks), in time order. */
export function articulationGroups(regions: readonly MidiRegion[]): ArticulationGroup[] {
  const groups = new Map<number, ArticulationGroup>();
  for (const region of regions) {
    for (const note of region.notes) {
      const start = region.startTick + note.startTick;
      const group = groups.get(start) ?? { start, length: 0, notes: [] };
      group.length = Math.max(group.length, note.lengthTicks);
      group.notes.push({ id: note.id, regionId: region.id, pitch: note.pitch, articulationId: note.articulationId });
      groups.set(start, group);
    }
  }
  return [...groups.values()].sort((a, b) => a.start - b.start);
}

/** The articulation ids among some notes (0: none), lowest first. */
export function articulationIdsOf(notes: readonly Pick<ArticulatedNote, 'articulationId'>[]): number[] {
  return [...new Set(notes.map(n => n.articulationId))].sort((a, b) => a - b);
}

/** What a chip or the inspector shows for some notes' articulations: a symbol or name, "–" for none, "*" mixed. */
export function articulationText(ids: readonly number[], articulations: readonly Articulation[]): string {
  if (ids.length === 0) {
    return '';
  }
  if (ids.length > 1) {
    return '*';
  }
  const id = ids[0] ?? 0;
  if (id === 0) {
    return '–';
  }
  const articulation = articulations.find(a => a.id === id);
  return articulation ? articulation.symbol?.trim() || articulation.name : `#${id}`;
}

/** An articulation's name for an accessible label ("None", "Mixed", "Legato", or "Articulation 7" when unknown). */
export function articulationName(ids: readonly number[], articulations: readonly Articulation[]): string {
  if (ids.length > 1) {
    return 'Mixed';
  }
  const id = ids[0] ?? 0;
  return id === 0 ? 'None' : (articulations.find(a => a.id === id)?.name ?? `Articulation ${id}`);
}

/**
 * The `editNotes` commands (one per region) giving `noteIds` the articulation `articulationId` (0: none); notes that
 * have it already are left out.
 */
export function articulationCommands(
  regions: readonly MidiRegion[],
  noteIds: ReadonlySet<number>,
  articulationId: number
): EditCommand[] {
  const commands: EditCommand[] = [];
  for (const region of regions) {
    const changes: NoteChange[] = region.notes
      .filter(n => noteIds.has(n.id) && n.articulationId !== articulationId)
      .map(n => ({ id: n.id, articulationId }));
    if (changes.length > 0) {
      commands.push({ type: 'editNotes', regionId: region.id, changes });
    }
  }
  return commands;
}

/** A field of the Piano Roll's note inspector. */
export type NoteField = 'position' | 'length' | 'pitch' | 'velocity';

/**
 * The selected notes after the note inspector set `field` to `value` for the first of them (by start): the others move
 * by as much (position, pitch, velocity; none before the project start or off the keyboard), or all take the length.
 * Empty when nothing changes.
 */
export function editNoteField(notes: readonly PlacedNote[], field: NoteField, value: number): PlacedNote[] {
  const first = [...notes].sort((a, b) => a.start - b.start || a.pitch - b.pitch)[0];
  if (!first || !Number.isFinite(value)) {
    return [];
  }
  switch (field) {
    case 'position': {
      const delta = Math.max(Math.round(value) - first.start, -Math.min(...notes.map(n => n.start)));
      return delta === 0 ? [] : notes.map(n => ({ ...n, start: n.start + delta }));
    }
    case 'length': {
      const length = Math.max(1, Math.round(value));
      return notes.some(n => n.length !== length) ? notes.map(n => ({ ...n, length })) : [];
    }
    case 'pitch':
      return transposeNotes(notes, Math.round(value) - first.pitch);
    case 'velocity': {
      const delta = Math.round(value) - first.velocity;
      return delta === 0 ? [] : offsetVelocities(notes, delta);
    }
  }
}
