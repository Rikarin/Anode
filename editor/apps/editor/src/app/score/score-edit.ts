import type {
  Clef,
  EditCommand,
  MidiRegion,
  NewScoreSymbol,
  NoteValue,
  ScoreMeasure,
  ScoreSymbolKind,
  ScoreView,
  StaffStyle
} from '@anode/api-client';
import type { PartBoxTool } from '@anode/audio-ui/lazy';
import { type Accidental, accidentalOf, gridsOf, pitchAt, placementOf, snapToGrids, stepPitch } from './notation-math';

/**
 * The Score Editor's edits as commands (docs/10-phase-9.md, "Score edits"): what a click with a Part box tool, a drag
 * or Delete becomes. Positions are display positions (project ticks); staff positions are diatonic steps above the
 * bottom line.
 */

/** A Part box tool: a note or rest value (rests show what is derived; they are not commands), a symbol, lyrics, chords. */
export type PartTool = PartBoxTool;

const ValueTicks: Record<NoteValue, number> = {
  breve: 7680,
  whole: 3840,
  half: 1920,
  quarter: 960,
  eighth: 480,
  sixteenth: 240,
  thirtySecond: 120,
  sixtyFourth: 60,
  hundredTwentyEighth: 30
};

/** A value's length in ticks, with dots and as a triplet. */
export function valueTicks(value: NoteValue, dots = 0, triplet = false): number {
  const base = ValueTicks[value];
  const dotted = base * (2 - 1 / 2 ** dots);
  return Math.round(triplet ? (dotted * 2) / 3 : dotted);
}

/** Where a drawn note is and what it is. */
export interface IndexedNote {
  noteId: number;
  trackId: string;
  pitch: number;
  elementId: string;
  /** Display tick of its first segment. */
  tick: number;
  /** Global staff index (0 the top staff of the first part). */
  staff: number;
}

/** One staff of the view, in drawing order. */
export interface IndexedStaff {
  part: number;
  trackId: string;
  /** The staff within its part. */
  staff: number;
  clef: Clef;
  /** The written transposition drawn (0 in a concert-pitch score). */
  transpose: number;
  styleId: string;
}

/** A score view indexed for hit testing and conversions. */
export interface ScoreIndex {
  staves: IndexedStaff[];
  notes: Map<number, IndexedNote>;
  /** Every drawn event's and note's element id → display tick. */
  ticks: Map<string, number>;
  measures: readonly ScoreMeasure[];
}

export function indexView(view: ScoreView): ScoreIndex {
  const staves: IndexedStaff[] = [];
  const notes = new Map<number, IndexedNote>();
  const ticks = new Map<string, number>();
  view.parts.forEach((part, p) => {
    const first = staves.length;
    part.staves.forEach((staff, s) =>
      staves.push({
        part: p,
        trackId: part.trackId,
        staff: s,
        clef: staff.clef,
        transpose: part.transpose,
        styleId: part.staffStyleId
      })
    );
    for (const measure of part.measures) {
      measure.staves.forEach((staffMeasure, s) => {
        for (const voice of staffMeasure.voices) {
          for (const event of voice.events) {
            ticks.set(event.id, event.tick);
            for (const note of event.notes) {
              ticks.set(note.id, event.tick);
              if (!notes.has(note.noteId)) {
                notes.set(note.noteId, {
                  noteId: note.noteId,
                  trackId: part.trackId,
                  pitch: note.pitch,
                  elementId: note.id,
                  tick: event.tick,
                  staff: first + s
                });
              }
            }
          }
        }
      });
    }
  });
  return { staves, notes, ticks, measures: view.measures };
}

/** The measure a display tick is in (the last one past the end). */
export function measureAtTick(measures: readonly ScoreMeasure[], tick: number): ScoreMeasure | null {
  let found: ScoreMeasure | null = null;
  for (const measure of measures) {
    if (measure.startTick <= tick) {
      found = measure;
    }
  }
  return found;
}

/** The region of a track a tick is in, if any. */
export function regionAt(regions: Iterable<MidiRegion>, trackId: string, tick: number): MidiRegion | null {
  for (const region of regions) {
    if (region.trackId === trackId && region.startTick <= tick && tick < region.startTick + region.lengthTicks) {
      return region;
    }
  }
  return null;
}

/** The region holding a note. */
export function regionOfNote(regions: Iterable<MidiRegion>, noteId: number): MidiRegion | null {
  for (const region of regions) {
    if (region.notes.some(n => n.id === noteId)) {
      return region;
    }
  }
  return null;
}

/**
 * A tick snapped to the straight display grid where it is drawn (the region's, or the staff style's), counted from the bar
 * line, or to a triplet grid when a triplet value is armed (`tripletTicks`).
 */
export function snapDisplayTick(
  tick: number,
  measure: ScoreMeasure,
  region: MidiRegion | null,
  style: StaffStyle,
  tripletTicks = 0
): number {
  const grids = region
    ? gridsOf(region.notation, style)
    : { straight: style.displayQuantizeTicks, triplet: style.tripletQuantizeTicks };
  const straight = grids.straight > 0 ? grids.straight : 240;
  const snapped =
    tripletTicks > 0
      ? snapToGrids(tick, measure.startTick, 0, tripletTicks)
      : snapToGrids(tick, measure.startTick, straight, 0);
  return Math.min(Math.max(snapped, measure.startTick), measure.startTick + measure.lengthTicks - 1);
}

/**
 * The staff position the model takes for a click: as drawn, except in a concert-pitch score of a transposing staff,
 * where the drawn (concert) position becomes the written one the command converts (`addScoreNote` reads positions
 * with the staff style's transposition).
 */
export function writtenPosition(
  drawn: number,
  clef: Clef,
  drawnTranspose: number,
  styleTranspose: number,
  fifths: number,
  mode: 'major' | 'minor' = 'major'
): number {
  if (drawnTranspose === styleTranspose) {
    return drawn;
  }
  const pitch = pitchAt(clef, drawnTranspose, fifths, drawn);
  return placementOf(clef, styleTranspose, fifths, pitch, mode).staffPosition;
}

/** A note tool's click: `addScoreNote` at the snapped tick, staff position and staff. */
export function insertNote(args: {
  tool: Extract<PartTool, { kind: 'note' }>;
  trackId: string;
  tick: number;
  staffPosition: number;
  staff: number;
  accidental?: Accidental | null;
  regionId?: string | null;
}): EditCommand {
  return {
    type: 'addScoreNote',
    trackId: args.trackId,
    tick: args.tick,
    lengthTicks: valueTicks(args.tool.value, args.tool.dots, args.tool.triplet),
    staffPosition: args.staffPosition,
    staff: args.staff,
    accidental: args.accidental ?? null,
    regionId: args.regionId ?? null
  };
}

/**
 * A vertical drag as `moveScoreNotes` reads it: steps of the written key and a written accidental. As dragged, except in
 * a concert-pitch score of a transposing staff: there the drag reaches a concert pitch (steps of the concert key drawn,
 * the accidental forcing a concert alteration), which becomes the written steps to it and, when the written key does not
 * give it there, the written accidental that does. Without an accidental the step counts agree (transposition keeps the
 * scale's degrees); a forced accidental is where they part (concert B♭ is a B♭ clarinet's C♮, not C♭).
 */
export function writtenMove(
  pitch: number,
  steps: number,
  accidental: Accidental | null,
  drawnTranspose: number,
  styleTranspose: number,
  fifths: number,
  mode: 'major' | 'minor' = 'major'
): { steps: number; accidental: Accidental | null } {
  if (drawnTranspose === styleTranspose || (steps === 0 && !accidental)) {
    return { steps, accidental };
  }
  const target = stepPitch(pitch, steps, drawnTranspose, fifths, accidental, mode);
  const from = placementOf('treble', styleTranspose, fifths, pitch, mode).staffPosition;
  const to = placementOf('treble', styleTranspose, fifths, target, mode);
  const keyed = pitchAt('treble', styleTranspose, fifths, to.staffPosition);
  return { steps: to.staffPosition - from, accidental: keyed === target ? null : accidentalOf(to.written.alter) };
}

/**
 * A drag: `moveScoreNotes` per region (one batch, one undo step), and per written move where notes need different ones
 * (`writtenMove`); nothing for a note that did not move.
 */
export function moveNotes(
  notes: readonly { noteId: number; regionId: string; steps?: number; accidental?: Accidental | null }[],
  steps: number,
  ticks: number,
  accidental: Accidental | null
): EditCommand[] {
  const groups = new Map<
    string,
    { regionId: string; steps: number; accidental: Accidental | null; noteIds: number[] }
  >();
  for (const note of notes) {
    const own = {
      steps: note.steps ?? steps,
      accidental: note.accidental === undefined ? accidental : note.accidental
    };
    if (own.steps === 0 && ticks === 0 && !own.accidental) {
      continue;
    }
    const key = `${note.regionId}\u0000${own.steps}\u0000${own.accidental ?? ''}`;
    const group = groups.get(key) ?? { regionId: note.regionId, ...own, noteIds: [] };
    group.noteIds.push(note.noteId);
    groups.set(key, group);
  }
  return [...groups.values()].map(group => ({
    type: 'moveScoreNotes' as const,
    regionId: group.regionId,
    noteIds: [...group.noteIds].sort((a, b) => a - b),
    steps: group.steps,
    ticks,
    accidental: group.accidental
  }));
}

/** What is selected in the score, for Delete. */
export interface ScoreSelection {
  notes: readonly { noteId: number; regionId: string }[];
  symbols: readonly string[];
  lyrics: readonly { noteId: number; verse: number }[];
  chords: readonly string[];
}

/** Delete: the notes (per region), symbols, syllables and chord symbols, in one batch. */
export function deleteSelection(selection: ScoreSelection): EditCommand[] {
  const commands: EditCommand[] = [];
  const byRegion = new Map<string, number[]>();
  for (const note of selection.notes) {
    byRegion.set(note.regionId, [...(byRegion.get(note.regionId) ?? []), note.noteId]);
  }
  for (const [regionId, noteIds] of byRegion) {
    commands.push({ type: 'deleteNotes', regionId, noteIds });
  }
  if (selection.symbols.length) {
    commands.push({ type: 'deleteScoreSymbols', symbolIds: [...selection.symbols] });
  }
  const verses = new Map<number, number[]>();
  for (const lyric of selection.lyrics) {
    verses.set(lyric.verse, [...(verses.get(lyric.verse) ?? []), lyric.noteId]);
  }
  for (const [verse, noteIds] of verses) {
    commands.push({
      type: 'setLyrics',
      verse,
      syllables: noteIds.map(noteId => ({ noteId, text: '', syllabic: 'single', extender: false }))
    });
  }
  if (selection.chords.length) {
    commands.push({ type: 'deleteChordSymbols', chordIds: [...selection.chords] });
  }
  return commands;
}

/** Symbols attached to a note only (articulations, fermata, trill, hidden ties). */
export const NoteOnlySymbols: ReadonlySet<ScoreSymbolKind> = new Set([
  'staccato',
  'staccatissimo',
  'accent',
  'marcato',
  'tenuto',
  'fermata',
  'trill',
  'tieHidden'
]);
/** Symbols that span two notes or two positions (a slur spans notes only). */
export const SpanningSymbols: ReadonlySet<ScoreSymbolKind> = new Set([
  'slur',
  'crescendo',
  'diminuendo',
  'octaveUp',
  'octaveDown'
]);
/** Symbols with their text (a dynamic's marking, text, tempo text, a rehearsal mark). */
export const TextSymbols: ReadonlySet<ScoreSymbolKind> = new Set(['dynamic', 'text', 'tempoText', 'rehearsal']);

/**
 * A symbol tool applied to selected notes: note-only symbols on each, spanning ones from the first to the last (by
 * display position), positioned ones at the first. Null when the selection cannot take it.
 */
export function symbolsForNotes(
  kind: ScoreSymbolKind,
  notes: readonly IndexedNote[],
  text: string | null = null
): NewScoreSymbol[] | null {
  const sorted = [...notes].sort((a, b) => a.tick - b.tick || b.pitch - a.pitch);
  const first = sorted[0];
  const last = sorted.at(-1);
  if (!first || !last) {
    return null;
  }
  if (NoteOnlySymbols.has(kind)) {
    return sorted.map(n => ({ kind, trackId: n.trackId, noteId: n.noteId }));
  }
  if (SpanningSymbols.has(kind)) {
    if (first.noteId === last.noteId || first.trackId !== last.trackId) {
      return null;
    }
    return [{ kind, trackId: first.trackId, noteId: first.noteId, endNoteId: last.noteId }];
  }
  return [{ kind, trackId: first.trackId, noteId: first.noteId, text }];
}

/** A positioned symbol (a dynamic, text, pedal, or a span between two clicks) on a staff of a track. */
export function symbolAt(
  kind: ScoreSymbolKind,
  trackId: string,
  tick: number,
  staff: number,
  text: string | null = null,
  endTick: number | null = null
): NewScoreSymbol {
  return {
    kind,
    trackId,
    tick,
    staff: staff > 0 ? staff : null,
    text: TextSymbols.has(kind) ? text : null,
    endTick: SpanningSymbols.has(kind) ? endTick : null
  };
}
