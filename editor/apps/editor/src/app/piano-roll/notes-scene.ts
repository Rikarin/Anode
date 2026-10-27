import type { MidiRegion } from '@anode/api-client';
import { createItems, ItemFlag, parseHexColor, type TimelineScene } from '@anode/timeline';
import { Ppq, velocityColor } from '../midi/midi-math';
import { notePlays, placeNotes, type PlacedNote, type RegionSpan } from './note-edit';

/** The Piano Roll's lanes: one per MIDI pitch, the highest (127) at the top. */
export const PitchLanes = 128;

export function laneOfPitch(pitch: number): number {
  return PitchLanes - 1 - pitch;
}

export function pitchOfLane(lane: number): number {
  return PitchLanes - 1 - Math.floor(lane);
}

/** What the Piano Roll draws, and how item indices map back to notes. */
export interface NotesScene {
  readonly scene: TimelineScene;
  /** The note per item index. */
  readonly notes: readonly PlacedNote[];
  /** Item index per note id. */
  readonly indexOf: ReadonlyMap<number, number>;
  /** The regions' spans in project ticks. */
  readonly spans: readonly RegionSpan[];
}

const velocityColors = Array.from({ length: 128 }, (_, v) => parseHexColor(velocityColor(Math.max(1, v))));

/**
 * The Piano Roll's scene for some regions of one track: a note per item on its pitch's lane, coloured by velocity,
 * selected ones flagged, and notes that do not play (before the region's start, at or past its end) greyed out.
 */
export function buildNotesScene(
  regions: readonly MidiRegion[],
  selected: ReadonlySet<number>,
  beatsPerBar: number
): NotesScene {
  const notes = placeNotes(regions);
  const spans = regions.map(r => ({ id: r.id, start: r.startTick, end: r.startTick + r.lengthTicks }));
  const spanOf = new Map(spans.map(span => [span.id, span]));
  const items = createItems(notes.length);
  const noteFlags = new Uint8Array(notes.length);
  const indexOf = new Map<number, number>();
  notes.forEach((note, i) => {
    items.start[i] = note.start;
    items.length[i] = note.length;
    items.lane[i] = laneOfPitch(note.pitch);
    items.color[i] = velocityColors[note.velocity] ?? 0;
    const span = spanOf.get(note.regionId);
    noteFlags[i] =
      (selected.has(note.id) ? ItemFlag.Selected : 0) | (span && !notePlays(note, span) ? ItemFlag.Muted : 0);
    indexOf.set(note.id, i);
  });

  return {
    scene: { kind: 'notes', ppq: Ppq, beatsPerBar, laneCount: PitchLanes, items, noteFlags },
    notes,
    indexOf,
    spans
  };
}
