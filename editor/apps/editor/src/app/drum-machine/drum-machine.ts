import type { DrumPad, LiveMidiEvent, PatchInfo, Track } from '@anode/api-client';
import { noteName } from '../midi/midi-math';

/** The pad grid's first note: C1 (36), the GM kick, at the bottom left of the first page (Logic's). */
export const FirstPadNote = 36;
/** Pads per page: 4 × 4. */
export const PadsPerPage = 16;
export const PadColumns = 4;
/** The built-in instrument a new pad plays. */
export const DrumSynthId = 'anode.drum-synth';
export const QuickSamplerId = 'anode.quick-sampler';

/** The pages that hold MIDI notes: 0 is C1…D♯2; −3 starts below note 0 and 5 ends above 127. */
export const FirstPage = Math.floor((0 - FirstPadNote) / PadsPerPage);
export const LastPage = Math.floor((127 - FirstPadNote) / PadsPerPage);

/** A cell of the pad grid: its note (null outside 0…127) and the pad on it. */
export interface PadCell {
  note: number | null;
  pad: DrumPad | null;
}

/** The page a note's pad is on. */
export function pageOf(note: number): number {
  return Math.floor((note - FirstPadNote) / PadsPerPage);
}

/** A page's first note. */
export function pageStart(page: number): number {
  return FirstPadNote + page * PadsPerPage;
}

/**
 * A page's pads as rows, top row first: as on a pad controller (and Logic's grid), the lowest note is at the bottom
 * left and notes rise to the right, then up a row.
 */
export function padGrid(pads: readonly DrumPad[], page: number): PadCell[][] {
  const byNote = new Map(pads.map(p => [p.note, p]));
  const start = pageStart(page);
  const rows = PadsPerPage / PadColumns;
  return Array.from({ length: rows }, (_, r) => {
    const rowStart = start + (rows - 1 - r) * PadColumns;
    return Array.from({ length: PadColumns }, (_, c) => {
      const note = rowStart + c;
      const valid = note >= 0 && note <= 127;
      return { note: valid ? note : null, pad: valid ? (byNote.get(note) ?? null) : null };
    });
  });
}

/** A page's range in note names ("C1–D♯2"), within 0…127. */
export function pageLabel(page: number): string {
  const start = Math.max(0, pageStart(page));
  const end = Math.min(127, pageStart(page) + PadsPerPage - 1);
  return `${noteName(start)}–${noteName(end)}`;
}

/** A pad button's accessible name: "Snare (D1)", or "Empty pad D1". */
export function padLabel(pad: Pick<DrumPad, 'name'> | null, note: number): string {
  return pad ? `${pad.name} (${noteName(note)})` : `Empty pad ${noteName(note)}`;
}

/** The Drum Machine Designer a track belongs to: the DMD itself, or a pad's (its stack's parent). */
export function drumMachineOf(track: Track | null | undefined, tracks: ReadonlyMap<string, Track>): Track | null {
  if (!track) {
    return null;
  }
  if (track.drumMachine) {
    return track;
  }
  const parent = track.parentId ? tracks.get(track.parentId) : undefined;
  return parent?.drumMachine ? parent : null;
}

/** The pad a pad track plays, on its DMD. */
export function padOfTrack(dmd: Track, trackId: string): DrumPad | null {
  return dmd.drumMachine?.pads.find(p => p.trackId === trackId) ?? null;
}

/** The kits a Drum Machine Designer starts from: the Library's patches of kind `stack`, by name. */
export function kitPatches(patches: readonly PatchInfo[]): PatchInfo[] {
  return patches.filter(p => p.kind === 'stack').sort((a, b) => a.name.localeCompare(b.name));
}

/** The note-on and note-off a pad's audition sends (channel 1, the velocity Logic's pads play at a click). */
export function auditionEvents(note: number, velocity = 100): { on: LiveMidiEvent; off: LiveMidiEvent } {
  return {
    on: { status: 0x90, data1: note, data2: velocity },
    off: { status: 0x80, data1: note, data2: 0 }
  };
}
