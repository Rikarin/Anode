import type {
  ChordSymbol,
  Clef,
  LyricSyllable,
  MidiRegion,
  Notehead,
  NoteValue,
  Project,
  ScoreDocument,
  ScoreDuration,
  ScoreEvent,
  ScoreLayout,
  ScoreMeasure,
  ScoreNote,
  ScorePart,
  ScoreStaffMeasure,
  ScoreSymbol,
  ScoreView,
  ScoreViewMode,
  ScoreVoice,
  StaffStyle,
  TimeSignature,
  Track
} from '@anode/api-client';
import {
  accidentalOf,
  clefOctave,
  fifthsOfKey,
  gridsOf,
  keyAlter,
  placementOf,
  snapToGrids,
  Steps,
  writtenFifths
} from '../../score/notation-math';
import { resolveStaffStyle } from './fake-staff-styles';

/**
 * The fake Server's notation stand-in (docs/10-phase-9.md: "a small notation stand-in so the editor works without the
 * engine"): MIDI → `ScoreView` → MEI 5 with the engine's element ids, plausible rather than Logic's. Display quantise
 * with the region's (or style's) two grids, chords, up to the style's voices by overlap, staves by split point, ties
 * across bar lines and long values, triplets on the triplet grid, rests, beams per beat, stems, spelling by key,
 * clefs, transposition, symbols, lyrics and chord symbols. Deterministic: the same project gives the same MEI.
 */

const Ppq = 960;
const WholeTicks = Ppq * 4;

const Values: readonly { value: NoteValue; ticks: number; mei: string }[] = [
  { value: 'whole', ticks: 3840, mei: '1' },
  { value: 'half', ticks: 1920, mei: '2' },
  { value: 'quarter', ticks: 960, mei: '4' },
  { value: 'eighth', ticks: 480, mei: '8' },
  { value: 'sixteenth', ticks: 240, mei: '16' },
  { value: 'thirtySecond', ticks: 120, mei: '32' },
  { value: 'sixtyFourth', ticks: 60, mei: '64' },
  { value: 'hundredTwentyEighth', ticks: 30, mei: '128' }
];
const Beamable: ReadonlySet<NoteValue> = new Set([
  'eighth',
  'sixteenth',
  'thirtySecond',
  'sixtyFourth',
  'hundredTwentyEighth'
]);

/** What to render: a score set, one track's part, or every MIDI track; linear or page. */
export interface FakeScoreQuery {
  set?: string | null | undefined;
  part?: string | null | undefined;
  view?: ScoreViewMode | null | undefined;
}

/** A note placed for the score: display start and end, its staff. */
interface Placed {
  noteId: number;
  pitch: number;
  start: number;
  end: number;
  staff: number;
}

/** An event in display time before it is cut into drawn pieces. */
interface Run {
  start: number;
  end: number;
  notes: Placed[];
}

/** A drawn piece: an event's segment of one note value. */
interface Piece {
  start: number;
  ticks: number;
  duration: ScoreDuration;
  notes: Placed[];
  /** 1-based segment of the run (ties). */
  segment: number;
  last: boolean;
}

/** The tracks a score shows. */
export function scoreTracks(project: Project, query: FakeScoreQuery): Track[] {
  const midi = project.tracks.filter(t => t.kind === 'instrument' || t.kind === 'midi');
  if (query.part) {
    return midi.filter(t => t.id === query.part);
  }
  if (query.set) {
    const set = project.score.sets.find(s => s.id === query.set);
    return set ? set.trackIds.map(id => midi.find(t => t.id === id)).filter((t): t is Track => !!t) : [];
  }
  return midi;
}

function signatureAt(project: Project, tick: number): TimeSignature {
  const events = [...project.settings.signatures].sort((a, b) => a.tick - b.tick);
  let current: TimeSignature = project.settings.signature;
  for (const event of events) {
    if (event.tick <= tick) {
      current = { numerator: event.numerator, denominator: event.denominator };
    }
  }
  return current;
}

function fifthsAt(project: Project, tick: number): { fifths: number; mode: 'major' | 'minor' } {
  const events = [...project.settings.keys].sort((a, b) => a.tick - b.tick);
  let current = { fifths: 0, mode: 'major' as 'major' | 'minor' };
  for (const event of events) {
    if (event.tick <= tick) {
      current = { fifths: fifthsOfKey(event), mode: event.mode };
    }
  }
  return current;
}

function barLength(time: TimeSignature): number {
  return (WholeTicks / time.denominator) * time.numerator;
}

function beatLength(time: TimeSignature): number {
  return time.denominator === 8 && time.numerator % 3 === 0 ? 1440 : WholeTicks / time.denominator;
}

/** The measures up to the end of the last region of the shown tracks (at least one), with the project's meter, keys, tempos and markers. */
export function scoreMeasures(project: Project, tracks: readonly Track[]): ScoreMeasure[] {
  const ids = new Set(tracks.map(t => t.id));
  let end = 0;
  for (const region of project.midiRegions) {
    if (ids.has(region.trackId)) {
      end = Math.max(end, region.startTick + region.lengthTicks);
    }
  }
  const measures: ScoreMeasure[] = [];
  let tick = 0;
  let previous: { time: TimeSignature; fifths: number } | null = null;
  const tempoEvents = project.settings.tempoEvents.length
    ? project.settings.tempoEvents
    : [{ tick: 0, bpm: project.settings.tempo, curve: 'jump' as const, shape: 0 }];
  do {
    const time = signatureAt(project, tick);
    const key = fifthsAt(project, tick);
    const length = barLength(time);
    measures.push({
      number: measures.length + 1,
      startTick: tick,
      lengthTicks: length,
      time,
      key: { fifths: key.fifths, mode: key.mode },
      showTime: previous?.time.numerator !== time.numerator || previous.time.denominator !== time.denominator,
      showKey: previous?.fifths !== key.fifths,
      tempos: tempoEvents
        .filter(t => t.tick >= tick && t.tick < tick + length)
        .map(t => ({ tick: t.tick, bpm: t.bpm })),
      rehearsals: project.markers
        .filter(m => m.tick >= tick && m.tick < tick + length)
        .sort((a, b) => a.tick - b.tick || a.id.localeCompare(b.id))
        .map(m => ({ tick: m.tick, markerId: m.id, text: m.name })),
      barline: 'single'
    });
    previous = { time, fifths: key.fifths };
    tick += length;
  } while (tick < end);
  const last = measures.at(-1);
  if (last) {
    last.barline = 'final';
  }
  return measures;
}

function measureAt(measures: readonly ScoreMeasure[], tick: number): ScoreMeasure {
  let found = measures[0] as ScoreMeasure;
  for (const measure of measures) {
    if (measure.startTick <= tick) {
      found = measure;
    }
  }
  return found;
}

/** The staff a concert pitch goes to: the first whose split point it reaches; the last takes the rest. */
function staffOf(style: StaffStyle, pitch: number): number {
  const index = style.staves.findIndex(s => s.splitPitch != null && pitch >= s.splitPitch);
  return index < 0 ? style.staves.length - 1 : index;
}

/** A track's notes as drawn: display-quantised starts and ends (never moved in MIDI), on their staves. */
export function placeNotes(
  project: Project,
  track: Track,
  style: StaffStyle,
  measures: readonly ScoreMeasure[]
): Placed[] {
  const placed: Placed[] = [];
  const regions = project.midiRegions
    .filter(r => r.trackId === track.id && !r.muted)
    .sort((a, b) => a.startTick - b.startTick || a.id.localeCompare(b.id));
  for (const region of regions) {
    const { straight, triplet } = gridsOf(region.notation, style);
    const smallest = Math.min(...[straight, triplet].filter(g => g > 0), Infinity);
    const minimum = Number.isFinite(smallest) ? smallest : 30;
    const regionEnd = region.startTick + region.lengthTicks;
    for (const note of region.notes) {
      const start = region.startTick + note.startTick;
      if (start >= regionEnd) {
        continue;
      }
      const end = Math.min(regionEnd, start + note.lengthTicks);
      const displayStart = snapToGrids(start, measureAt(measures, start).startTick, straight, triplet);
      let displayEnd = snapToGrids(end, measureAt(measures, Math.max(start, end - 1)).startTick, straight, triplet);
      if (displayEnd <= displayStart) {
        displayEnd = displayStart + minimum;
      }
      placed.push({
        noteId: note.id,
        pitch: note.pitch,
        start: displayStart,
        end: displayEnd,
        staff: staffOf(style, note.pitch)
      });
    }
    interpret(placed, region);
  }
  return placed.sort((a, b) => a.start - b.start || b.pitch - a.pitch || a.noteId - b.noteId);
}

/** Interpretation: a note that nearly reaches the next start is drawn to it (no tiny rests). */
function interpret(placed: Placed[], region: MidiRegion): void {
  if (!region.notation.interpretation) {
    return;
  }
  const starts = [...new Set(placed.map(p => p.start))].sort((a, b) => a - b);
  for (const note of placed) {
    const next = starts.find(s => s > note.start);
    if (next !== undefined && note.end < next && next - note.end <= Ppq / 4) {
      note.end = next;
    }
  }
}

/** The runs (notes and chords) of each voice of one staff: chords by start and end, voices by overlap. */
function voicesOf(notes: readonly Placed[], maxVoices: number, noOverlap: boolean): Run[][] {
  const groups: Run[] = [];
  for (const note of notes) {
    const group = groups.find(g => g.start === note.start && g.end === note.end);
    if (group) {
      group.notes.push(note);
    } else {
      groups.push({ start: note.start, end: note.end, notes: [note] });
    }
  }
  groups.sort(
    (a, b) => a.start - b.start || Math.max(...b.notes.map(n => n.pitch)) - Math.max(...a.notes.map(n => n.pitch))
  );
  const voices: Run[][] = [[]];
  for (const group of groups) {
    let voice = voices.find(v => (v.at(-1)?.end ?? 0) <= group.start);
    if (!voice && voices.length < Math.max(1, maxVoices)) {
      voice = [];
      voices.push(voice);
    }
    if (!voice) {
      voice = voices[voices.length - 1] ?? [];
      const previous = voice.at(-1) as Run;
      if (previous.start === group.start) {
        // Same start, no voice left: one chord, as long as the shorter.
        previous.notes.push(...group.notes);
        previous.end = Math.min(previous.end, group.end);
        continue;
      }
      if (noOverlap || previous.end > group.start) {
        previous.end = group.start;
      }
    }
    voice.push(group);
  }
  for (const voice of voices) {
    for (const run of voice) {
      run.notes.sort((a, b) => a.pitch - b.pitch);
    }
  }
  return voices.filter(v => v.length > 0);
}

/**
 * Durations that add up to `ticks`: straight values (dotted up to `maxDots`) for what 1/128 steps express, then
 * triplet values (2/3 of a straight value) for the rest.
 */
export function durationsOf(ticks: number, maxDots: number): { ticks: number; duration: ScoreDuration }[] {
  // The split between straight and triplet values with the fewest pieces (more straight on a tie).
  let best: { ticks: number; duration: ScoreDuration }[] | null = null;
  for (let triplet = 0; triplet <= ticks; triplet += 20) {
    if ((ticks - triplet) % 30 !== 0) {
      continue;
    }
    const pieces = [...straightValues(ticks - triplet, maxDots), ...tripletValues(triplet)];
    if (pieces.reduce((sum, p) => sum + p.ticks, 0) === ticks && (!best || pieces.length < best.length)) {
      best = pieces;
    }
  }
  return best ?? straightValues(ticks - (ticks % 30), maxDots);
}

function straightValues(ticks: number, maxDots: number): { ticks: number; duration: ScoreDuration }[] {
  const out: { ticks: number; duration: ScoreDuration }[] = [];
  let left = ticks;
  while (left >= 30) {
    let chosen: { ticks: number; duration: ScoreDuration } | null = null;
    for (const value of Values) {
      for (let dots = Math.max(0, maxDots); dots >= 0 && !chosen; dots--) {
        const length = value.ticks * (2 - 1 / 2 ** dots);
        if (Number.isInteger(length) && length <= left && (left - length) % 30 === 0) {
          chosen = { ticks: length, duration: { value: value.value, dots, tuplet: null } };
        }
      }
      if (chosen) {
        break;
      }
    }
    if (!chosen) {
      break;
    }
    out.push(chosen);
    left -= chosen.ticks;
  }
  return out;
}

function tripletValues(ticks: number): { ticks: number; duration: ScoreDuration }[] {
  const out: { ticks: number; duration: ScoreDuration }[] = [];
  let left = ticks;
  for (const value of Values) {
    const length = (value.ticks * 2) / 3;
    while (length >= 20 && left >= length) {
      out.push({
        ticks: length,
        duration: { value: value.value, dots: 0, tuplet: { group: 0, actual: 3, normal: 2 } }
      });
      left -= length;
    }
  }
  return out;
}

/** A run cut at bar lines and into note values. */
function piecesOf(run: Run, measures: readonly ScoreMeasure[], maxDots: number): Piece[] {
  const pieces: Piece[] = [];
  let tick = run.start;
  let segment = 1;
  while (tick < run.end) {
    const measure = measureAt(measures, tick);
    const until = Math.min(run.end, measure.startTick + measure.lengthTicks);
    for (const part of durationsOf(until - tick, maxDots)) {
      pieces.push({
        start: tick,
        ticks: part.ticks,
        duration: part.duration,
        notes: run.notes,
        segment: segment++,
        last: false
      });
      tick += part.ticks;
    }
    if (tick < until) {
      tick = until;
    }
  }
  const last = pieces.at(-1);
  if (last) {
    last.last = true;
  }
  return pieces;
}

/** Rests that fill `from`…`to` within one measure. */
function restPieces(from: number, to: number): Piece[] {
  const pieces: Piece[] = [];
  let tick = from;
  for (const part of durationsOf(to - from, 1)) {
    pieces.push({ start: tick, ticks: part.ticks, duration: part.duration, notes: [], segment: 1, last: true });
    tick += part.ticks;
  }
  return pieces;
}

const DrumHeads: Record<Notehead, string | null> = {
  normal: null,
  cross: 'x',
  circleCross: 'circlex',
  diamond: 'diamond',
  triangle: 'isotriangle',
  slash: 'slash'
};

interface PartContext {
  project: Project;
  track: Track;
  style: StaffStyle;
  transpose: number;
  measures: readonly ScoreMeasure[];
  symbols: readonly ScoreSymbol[];
  lyrics: readonly LyricSyllable[];
  chords: readonly ChordSymbol[];
}

/** One part of the view. */
function partOf(ctx: PartContext): ScorePart {
  const { project, track, style, measures } = ctx;
  const placed = placeNotes(project, track, style, measures);
  const notation = project.midiRegions.find(r => r.trackId === track.id)?.notation;
  const maxDots = notation?.maxDots ?? 1;
  const noOverlap = notation?.noOverlap ?? true;
  const partMeasures = measures.map(() => ({ staves: style.staves.map(() => ({ voices: [] as ScoreVoice[] })) }));
  style.staves.forEach((staff, staffIndex) => {
    const notes = placed.filter(p => p.staff === staffIndex);
    const voices = voicesOf(notes, staff.voices, noOverlap);
    voices.forEach((runs, voiceIndex) => {
      const pieces = runs.flatMap(run => piecesOf(run, measures, maxDots));
      measures.forEach((measure, m) => {
        const end = measure.startTick + measure.lengthTicks;
        const inMeasure = pieces.filter(p => p.start >= measure.startTick && p.start < end);
        if (voiceIndex > 0 && inMeasure.length === 0) {
          return;
        }
        const filled: Piece[] = [];
        let tick = measure.startTick;
        for (const piece of inMeasure) {
          if (piece.start > tick) {
            filled.push(...restPieces(tick, piece.start));
          }
          filled.push(piece);
          tick = piece.start + piece.ticks;
        }
        if (tick < end) {
          filled.push(...restPieces(tick, end));
        }
        const events = eventsOf(ctx, filled, measure, staffIndex, voiceIndex + 1, voices.length > 1);
        (partMeasures[m]?.staves[staffIndex] as ScoreStaffMeasure).voices.push({ number: voiceIndex + 1, events });
      });
    });
    measures.forEach((measure, m) => {
      const staffMeasure = partMeasures[m]?.staves[staffIndex] as ScoreStaffMeasure;
      if (staffMeasure.voices.length === 0) {
        const events = eventsOf(
          ctx,
          restPieces(measure.startTick, measure.startTick + measure.lengthTicks),
          measure,
          staffIndex,
          1,
          false
        );
        staffMeasure.voices.push({ number: 1, events });
      }
      staffMeasure.clef = m === 0 ? staff.clef : null;
    });
  });
  const names = project.score.layout.instrumentNames.find(n => n.trackId === track.id);
  return {
    trackId: track.id,
    name: names?.full || track.name,
    shortName: names?.short || track.name.slice(0, 5),
    staffStyleId: style.id,
    transpose: ctx.transpose,
    brace: style.brace,
    staves: style.staves,
    measures: partMeasures
  };
}

function eventsOf(
  ctx: PartContext,
  pieces: readonly Piece[],
  measure: ScoreMeasure,
  staffIndex: number,
  voice: number,
  polyphonic: boolean
): ScoreEvent[] {
  const staff = ctx.style.staves[staffIndex] ?? { clef: 'treble' as Clef, transpose: 0, voices: 1 };
  const fifths = measure.key.fifths;
  const transpose = ctx.transpose + 0;
  const keyFifths = writtenFifths(fifths, transpose);
  let rests = 0;
  const events: ScoreEvent[] = pieces.map(piece => {
    if (piece.notes.length === 0) {
      rests++;
      return {
        id: `r${ctx.track.id}-${measure.number}-${staffIndex}-${voice}-${rests}`,
        kind: 'rest',
        tick: piece.start,
        durationTicks: piece.ticks,
        duration: piece.duration,
        notes: [],
        stem: 'none',
        beam: null,
        symbolIds: [],
        lyricIds: [],
        chordIds: []
      };
    }
    const notes: ScoreNote[] = piece.notes.map(note => {
      const drum = ctx.style.drumMap?.find(d => d.pitch === note.pitch);
      let staffPosition: number;
      let written;
      if (drum) {
        staffPosition = drum.staffPosition;
        const number = 4 * 7 + 2 + staffPosition;
        written = { step: Steps[((number % 7) + 7) % 7] ?? 'c', alter: 0, octave: Math.floor(number / 7) };
      } else {
        const placement = placementOf(staff.clef, transpose, fifths, note.pitch);
        staffPosition = placement.staffPosition;
        written = placement.written;
      }
      const shown = drum ? 0 : keyAlter(keyFifths, written.step);
      return {
        id: piece.segment <= 1 ? `n${note.noteId}` : `n${note.noteId}-${piece.segment}`,
        noteId: note.noteId,
        pitch: note.pitch,
        written,
        staffPosition,
        accidental: !drum && written.alter !== shown && piece.segment === 1 ? accidentalOf(written.alter) : null,
        tieStart: !piece.last && !hiddenTie(ctx, note.noteId),
        tieStop: piece.segment > 1 && !hiddenTie(ctx, note.noteId),
        notehead: drum?.notehead ?? 'normal'
      };
    });
    const lowest = Math.min(...piece.notes.map(n => n.noteId));
    const chord = notes.length > 1;
    const average = notes.reduce((sum, n) => sum + n.staffPosition, 0) / notes.length;
    const first = piece.segment === 1;
    const noteIds = new Set(piece.notes.map(n => n.noteId));
    return {
      id: chord ? (first ? `c${lowest}` : `c${lowest}-${piece.segment}`) : (notes[0]?.id ?? ''),
      kind: chord ? 'chord' : 'note',
      tick: piece.start,
      durationTicks: piece.ticks,
      duration: piece.duration,
      notes,
      stem: polyphonic ? (voice % 2 === 1 ? 'up' : 'down') : average < 4 ? 'up' : 'down',
      beam: null,
      symbolIds: first
        ? ctx.symbols
            .filter(s => s.noteId != null && noteIds.has(s.noteId))
            .map(s => s.id)
            .sort()
        : [],
      lyricIds: first
        ? ctx.lyrics
            .filter(l => noteIds.has(l.noteId))
            .map(l => l.id)
            .sort()
        : [],
      chordIds:
        first && staffIndex === 0 && voice === 1
          ? ctx.chords
              .filter(c => c.trackId === ctx.track.id && c.tick === piece.start)
              .map(c => c.id)
              .sort()
          : []
    };
  });
  groupBeams(events, measure);
  groupTuplets(events);
  return events;
}

function hiddenTie(ctx: PartContext, noteId: number): boolean {
  return ctx.symbols.some(s => s.kind === 'tieHidden' && s.noteId === noteId);
}

/** Beams: consecutive eighths and shorter within one beat. */
function groupBeams(events: ScoreEvent[], measure: ScoreMeasure): void {
  const beat = beatLength(measure.time);
  let group = 0;
  let i = 0;
  while (i < events.length) {
    const start = events[i] as ScoreEvent;
    const beatOf = (e: ScoreEvent) => Math.floor((e.tick - measure.startTick) / beat);
    let j = i;
    while (
      j < events.length &&
      (events[j] as ScoreEvent).kind !== 'rest' &&
      Beamable.has((events[j] as ScoreEvent).duration.value) &&
      beatOf(events[j] as ScoreEvent) === beatOf(start)
    ) {
      j++;
    }
    if (j - i >= 2) {
      group++;
      for (let k = i; k < j; k++) {
        (events[k] as ScoreEvent).beam = group;
      }
    }
    i = Math.max(j, i + 1);
  }
}

/** Tuplet groups: consecutive triplet values within one beat (or half bar for longer ones). */
function groupTuplets(events: ScoreEvent[]): void {
  let group = 0;
  let open: { span: number; filled: number } | null = null;
  for (const event of events) {
    const tuplet = event.duration.tuplet;
    if (!tuplet) {
      open = null;
      continue;
    }
    if (!open || open.filled >= open.span) {
      group++;
      const value = Values.find(v => v.value === event.duration.value)?.ticks ?? Ppq;
      open = { span: value * 2, filled: 0 };
    }
    open.filled += event.durationTicks;
    event.duration = { ...event.duration, tuplet: { group, actual: 3, normal: 2 } };
  }
}

/** The rendered score: the view and its MEI (`GET /project/score`). */
export function renderScore(project: Project, query: FakeScoreQuery): ScoreDocument {
  const mode: ScoreViewMode = query.view === 'page' ? 'page' : 'linear';
  const tracks = scoreTracks(project, query);
  const transposing = query.part ? true : project.score.layout.transposingScore;
  const measures = scoreMeasures(project, tracks);
  const trackIds = new Set(tracks.map(t => t.id));
  const noteIds = new Set(
    project.midiRegions.filter(r => trackIds.has(r.trackId)).flatMap(r => r.notes.map(n => n.id))
  );
  const symbols = project.score.symbols.filter(s => trackIds.has(s.trackId));
  const lyrics = project.score.lyrics.filter(l => noteIds.has(l.noteId));
  const chords = project.score.chords.filter(c => trackIds.has(c.trackId));
  const parts = tracks.map(track => {
    const style = resolveStaffStyle(project.score, track.staffStyleId);
    const transpose = transposing ? (style.staves[0]?.transpose ?? 0) : 0;
    return partOf({
      project,
      track,
      style: transposing ? style : { ...style, staves: style.staves.map(s => ({ ...s, transpose: 0 })) },
      transpose,
      measures,
      symbols,
      lyrics,
      chords
    });
  });
  const view: ScoreView = {
    setId: query.set ?? null,
    partTrackId: query.part ?? null,
    mode,
    transposing,
    layout: project.score.layout,
    measures,
    parts,
    symbols: [...symbols].sort((a, b) => a.id.localeCompare(b.id)),
    lyrics: [...lyrics].sort((a, b) => a.id.localeCompare(b.id)),
    chords: [...chords].sort((a, b) => a.id.localeCompare(b.id))
  };
  return { view, mei: meiOf(view, project.name) };
}

// ---- MEI

function escape(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

function keySig(fifths: number): string {
  return fifths > 0 ? `${fifths}s` : fifths < 0 ? `${-fifths}f` : '0';
}

function clefAttributes(clef: Clef): string {
  switch (clef) {
    case 'bass':
      return 'clef.shape="F" clef.line="4"';
    case 'alto':
      return 'clef.shape="C" clef.line="3"';
    case 'tenor':
      return 'clef.shape="C" clef.line="4"';
    case 'treble8vb':
      return 'clef.shape="G" clef.line="2" clef.dis="8" clef.dis.place="below"';
    case 'treble8va':
      return 'clef.shape="G" clef.line="2" clef.dis="8" clef.dis.place="above"';
    case 'bass8vb':
      return 'clef.shape="F" clef.line="4" clef.dis="8" clef.dis.place="below"';
    case 'percussion':
      return 'clef.shape="perc"';
    case 'none':
      return 'clef.shape="G" clef.line="2" clef.visible="false"';
    default:
      return 'clef.shape="G" clef.line="2"';
  }
}

function dur(duration: ScoreDuration): string {
  const value = Values.find(v => v.value === duration.value)?.mei ?? (duration.value === 'breve' ? 'breve' : '4');
  return `dur="${value}"${duration.dots > 0 ? ` dots="${duration.dots}"` : ''}`;
}

const Accid: Record<string, string> = { natural: 'n', sharp: 's', flat: 'f', doubleSharp: 'x', doubleFlat: 'ff' };
const Artic: Partial<Record<ScoreSymbol['kind'], string>> = {
  staccato: 'stacc',
  staccatissimo: 'stacciss',
  accent: 'acc',
  marcato: 'marc',
  tenuto: 'ten'
};
const Wordpos: Record<LyricSyllable['syllabic'], string> = { single: '', begin: 'i', middle: 'm', end: 't' };

function noteXml(note: ScoreNote, event: ScoreEvent, ctx: MeiContext, inChord: boolean): string {
  const attributes = [
    `xml:id="${note.id}"`,
    inChord ? '' : dur(event.duration),
    `pname="${note.written.step}"`,
    `oct="${note.written.octave}"`,
    note.accidental ? `accid="${Accid[note.accidental]}"` : `accid.ges="${Accid[accidentalOf(note.written.alter)]}"`,
    note.tieStart && note.tieStop ? 'tie="m"' : note.tieStart ? 'tie="i"' : note.tieStop ? 'tie="t"' : '',
    !inChord && event.stem !== 'none' ? `stem.dir="${event.stem}"` : '',
    note.notehead !== 'normal' && DrumHeads[note.notehead] ? `head.shape="${DrumHeads[note.notehead]}"` : ''
  ].filter(Boolean);
  const children: string[] = [];
  if (!inChord) {
    children.push(...artics(event, ctx));
  }
  for (const lyric of ctx.lyrics.filter(l => l.noteId === note.noteId && event.lyricIds.includes(l.id))) {
    const pos = Wordpos[lyric.syllabic];
    const con = lyric.extender ? 'u' : lyric.syllabic === 'begin' || lyric.syllabic === 'middle' ? 'd' : '';
    children.push(
      `<verse n="${lyric.verse}"><syl xml:id="l${lyric.id}"${pos ? ` wordpos="${pos}"` : ''}${con ? ` con="${con}"` : ''}>${escape(lyric.text)}</syl></verse>`
    );
  }
  return children.length
    ? `<note ${attributes.join(' ')}>${children.join('')}</note>`
    : `<note ${attributes.join(' ')}/>`;
}

function artics(event: ScoreEvent, ctx: MeiContext): string[] {
  return event.symbolIds
    .map(id => ctx.symbols.get(id))
    .filter((s): s is ScoreSymbol => !!s && !!Artic[s.kind])
    .map(s => `<artic xml:id="s${s.id}" artic="${Artic[s.kind]}"${s.placement ? ` place="${s.placement}"` : ''}/>`);
}

function eventXml(event: ScoreEvent, ctx: MeiContext): string {
  if (event.kind === 'rest') {
    return `<rest xml:id="${event.id}" ${dur(event.duration)}/>`;
  }
  if (event.kind === 'note') {
    return noteXml(event.notes[0] as ScoreNote, event, ctx, false);
  }
  const inner = event.notes.map(n => noteXml(n, event, ctx, true)).join('');
  const stem = event.stem !== 'none' ? ` stem.dir="${event.stem}"` : '';
  return `<chord xml:id="${event.id}" ${dur(event.duration)}${stem}>${artics(event, ctx).join('')}${inner}</chord>`;
}

/** A voice's events with their tuplets and beams as MEI containers. */
function layerXml(events: readonly ScoreEvent[], ctx: MeiContext): string {
  const out: string[] = [];
  let i = 0;
  while (i < events.length) {
    const event = events[i] as ScoreEvent;
    const tuplet = event.duration.tuplet;
    if (tuplet) {
      const members: ScoreEvent[] = [];
      while (i < events.length && (events[i] as ScoreEvent).duration.tuplet?.group === tuplet.group) {
        members.push(events[i] as ScoreEvent);
        i++;
      }
      out.push(`<tuplet num="${tuplet.actual}" numbase="${tuplet.normal}">${beamed(members, ctx)}</tuplet>`);
      continue;
    }
    const members: ScoreEvent[] = [];
    while (i < events.length && !(events[i] as ScoreEvent).duration.tuplet) {
      members.push(events[i] as ScoreEvent);
      i++;
    }
    out.push(beamed(members, ctx));
  }
  return out.join('');
}

function beamed(events: readonly ScoreEvent[], ctx: MeiContext): string {
  const out: string[] = [];
  let i = 0;
  while (i < events.length) {
    const event = events[i] as ScoreEvent;
    if (event.beam != null) {
      const group: ScoreEvent[] = [];
      while (i < events.length && (events[i] as ScoreEvent).beam === event.beam) {
        group.push(events[i] as ScoreEvent);
        i++;
      }
      out.push(group.length > 1 ? `<beam>${group.map(e => eventXml(e, ctx)).join('')}</beam>` : eventXml(event, ctx));
      continue;
    }
    out.push(eventXml(event, ctx));
    i++;
  }
  return out.join('');
}

interface MeiContext {
  symbols: Map<string, ScoreSymbol>;
  lyrics: readonly LyricSyllable[];
}

/** Where each note's first drawn element is, for control events' `startid`. */
interface NoteSite {
  elementId: string;
  tick: number;
  staff: number;
}

function tstamp(measure: ScoreMeasure, tick: number): string {
  const unit = WholeTicks / measure.time.denominator;
  return String(Math.round((1 + (tick - measure.startTick) / unit) * 1000) / 1000);
}

function tstamp2(measures: readonly ScoreMeasure[], from: ScoreMeasure, tick: number): string {
  const to = measureAt(measures, tick);
  return `${to.number - from.number}m+${tstamp(to, tick)}`;
}

const DirElement: Partial<Record<ScoreSymbol['kind'], string>> = { text: 'dir', tempoText: 'tempo', rehearsal: 'reh' };

function controlXml(
  symbol: ScoreSymbol,
  measures: readonly ScoreMeasure[],
  sites: ReadonlyMap<number, NoteSite>,
  staffOffset: number
): { tick: number; xml: string } | null {
  const start = symbol.noteId != null ? sites.get(symbol.noteId) : null;
  const tick = start?.tick ?? symbol.tick ?? null;
  if (tick === null || (symbol.noteId != null && !start)) {
    return null;
  }
  const measure = measureAt(measures, tick);
  const staff = start?.staff ?? staffOffset + (symbol.staff ?? 0) + 1;
  const end = symbol.endNoteId != null ? sites.get(symbol.endNoteId) : null;
  const endTick = end?.tick ?? symbol.endTick ?? null;
  const at = start ? `startid="#${start.elementId}"` : `tstamp="${tstamp(measure, tick)}"`;
  const until = end
    ? `endid="#${end.elementId}"`
    : endTick !== null
      ? `tstamp2="${tstamp2(measures, measure, endTick)}"`
      : '';
  const place = symbol.placement ? ` place="${symbol.placement}"` : '';
  const id = `xml:id="s${symbol.id}"`;
  const text = escape(symbol.text ?? '');
  switch (symbol.kind) {
    case 'dynamic':
      return { tick, xml: `<dynam ${id} staff="${staff}" ${at}${place}>${text}</dynam>` };
    case 'text':
    case 'tempoText':
    case 'rehearsal':
      return {
        tick,
        xml: `<${DirElement[symbol.kind]} ${id} staff="${staff}" ${at}${place}>${text}</${DirElement[symbol.kind]}>`
      };
    case 'pedalDown':
    case 'pedalUp':
      return {
        tick,
        xml: `<pedal ${id} staff="${staff}" ${at} dir="${symbol.kind === 'pedalDown' ? 'down' : 'up'}"/>`
      };
    case 'fermata':
      return { tick, xml: `<fermata ${id} staff="${staff}" ${at}${place}/>` };
    case 'trill':
      return { tick, xml: `<trill ${id} staff="${staff}" ${at}${place}/>` };
    case 'slur':
      return until ? { tick, xml: `<slur ${id} staff="${staff}" ${at} ${until}${place}/>` } : null;
    case 'crescendo':
    case 'diminuendo':
      return until
        ? {
            tick,
            xml: `<hairpin ${id} staff="${staff}" ${at} ${until} form="${symbol.kind === 'crescendo' ? 'cres' : 'dim'}"${place}/>`
          }
        : null;
    case 'octaveUp':
    case 'octaveDown':
      return until
        ? {
            tick,
            xml: `<octave ${id} staff="${staff}" ${at} ${until} dis="8" dis.place="${symbol.kind === 'octaveUp' ? 'above' : 'below'}"/>`
          }
        : null;
    default:
      return null;
  }
}

function chordText(chord: ChordSymbol): string {
  const name = (p: { step: string; alter: number }) =>
    `${p.step.toUpperCase()}${p.alter > 0 ? '#'.repeat(p.alter) : 'b'.repeat(-p.alter)}`;
  return `${name(chord.root)}${chord.quality}${chord.bass ? `/${name(chord.bass)}` : ''}`;
}

function pageHead(layout: ScoreLayout, title: string): string {
  const lines: string[] = [];
  if (layout.header) {
    lines.push(`<rend halign="left" valign="top">${escape(layout.header)}</rend>`);
  }
  lines.push(
    `<rend halign="center" valign="top"><rend fontsize="x-large">${escape(title)}</rend>${
      layout.subtitle ? `<lb/><rend fontsize="large">${escape(layout.subtitle)}</rend>` : ''
    }</rend>`
  );
  if (layout.composer) {
    lines.push(`<rend halign="right" valign="bottom">${escape(layout.composer)}</rend>`);
  }
  return `<pgHead>${lines.join('')}</pgHead>${
    layout.footer ? `<pgFoot><rend halign="center" valign="bottom">${escape(layout.footer)}</rend></pgFoot>` : ''
  }`;
}

/** The view as MEI 5 with the engine's ids (`n…`, `c…`, `r…`, `s…`, `l…`, `h…`, `m…`). */
export function meiOf(view: ScoreView, projectName: string): string {
  const title = view.layout.title || projectName;
  const ctx: MeiContext = { symbols: new Map(view.symbols.map(s => [s.id, s])), lyrics: view.lyrics };
  const first = view.measures[0];
  const out: string[] = [
    '<?xml version="1.0" encoding="UTF-8"?>',
    '<mei xmlns="http://www.music-encoding.org/ns/mei" meiversion="5.1">',
    `<meiHead><fileDesc><titleStmt><title>${escape(title)}</title>`,
    view.layout.composer
      ? `<respStmt><persName role="composer">${escape(view.layout.composer)}</persName></respStmt>`
      : '',
    '</titleStmt><pubStmt/></fileDesc></meiHead><music><body><mdiv><score>',
    `<scoreDef meter.count="${first?.time.numerator ?? 4}" meter.unit="${first?.time.denominator ?? 4}" key.sig="${keySig(first?.key.fifths ?? 0)}">`,
    view.mode === 'page' ? pageHead(view.layout, title) : '',
    '<staffGrp>'
  ];
  let n = 0;
  const offsets: number[] = [];
  for (const part of view.parts) {
    offsets.push(n);
    const written = writtenFifths(first?.key.fifths ?? 0, part.transpose);
    const keyAttr = part.transpose !== 0 ? ` key.sig="${keySig(written)}"` : '';
    const label = `<label>${escape(part.name)}</label><labelAbbr>${escape(part.shortName)}</labelAbbr>`;
    if (part.staves.length > 1) {
      out.push(`<staffGrp${part.brace ? ' symbol="brace"' : ''} bar.thru="true">${label}`);
    }
    part.staves.forEach((staff, i) => {
      n++;
      out.push(
        `<staffDef n="${n}" lines="5" ${clefAttributes(staff.clef)}${keyAttr}>${part.staves.length === 1 && i === 0 ? label : ''}</staffDef>`
      );
    });
    if (part.staves.length > 1) {
      out.push('</staffGrp>');
    }
  }
  out.push('</staffGrp></scoreDef><section>');

  // Where every note is first drawn, for control events.
  const sites = new Map<number, NoteSite>();
  view.parts.forEach((part, p) => {
    part.measures.forEach(m =>
      m.staves.forEach((staff, s) =>
        staff.voices.forEach(voice =>
          voice.events.forEach(event =>
            event.notes.forEach(note => {
              if (!note.id.includes('-') && !sites.has(note.noteId)) {
                sites.set(note.noteId, { elementId: note.id, tick: event.tick, staff: (offsets[p] ?? 0) + s + 1 });
              }
            })
          )
        )
      )
    );
  });
  const controls = new Map<number, string[]>();
  const add = (tick: number, xml: string) => {
    const measure = measureAt(view.measures, tick).number;
    controls.set(measure, [...(controls.get(measure) ?? []), xml]);
  };
  for (const symbol of view.symbols) {
    if (Artic[symbol.kind] || symbol.kind === 'tieHidden') {
      continue;
    }
    const p = view.parts.findIndex(part => part.trackId === symbol.trackId);
    const control = controlXml(symbol, view.measures, sites, offsets[p] ?? 0);
    if (control) {
      add(control.tick, control.xml);
    }
  }
  for (const chord of view.chords) {
    const p = view.parts.findIndex(part => part.trackId === chord.trackId);
    const measure = measureAt(view.measures, chord.tick);
    add(
      chord.tick,
      `<harm xml:id="h${chord.id}" staff="${(offsets[p] ?? 0) + 1}" tstamp="${tstamp(measure, chord.tick)}">${escape(chordText(chord))}</harm>`
    );
  }

  view.measures.forEach((measure, m) => {
    if (m > 0 && (measure.showTime || measure.showKey)) {
      out.push(
        `<scoreDef${measure.showTime ? ` meter.count="${measure.time.numerator}" meter.unit="${measure.time.denominator}"` : ''}${
          measure.showKey ? ` key.sig="${keySig(measure.key.fifths)}"` : ''
        }/>`
      );
    }
    const right = measure.barline === 'final' ? ' right="end"' : measure.barline === 'double' ? ' right="dbl"' : '';
    out.push(`<measure n="${measure.number}" xml:id="m${measure.number}"${right}>`);
    view.parts.forEach((part, p) => {
      part.measures[m]?.staves.forEach((staff, s) => {
        out.push(`<staff n="${(offsets[p] ?? 0) + s + 1}">`);
        for (const voice of staff.voices) {
          out.push(`<layer n="${voice.number}">${layerXml(voice.events, ctx)}</layer>`);
        }
        out.push('</staff>');
      });
    });
    for (const tempo of measure.tempos) {
      out.push(
        `<tempo staff="1" tstamp="${tstamp(measure, tempo.tick)}" midi.bpm="${tempo.bpm}">♩ = ${Math.round(tempo.bpm * 100) / 100}</tempo>`
      );
    }
    for (const rehearsal of measure.rehearsals) {
      out.push(`<reh staff="1" tstamp="${tstamp(measure, rehearsal.tick)}">${escape(rehearsal.text)}</reh>`);
    }
    out.push(...(controls.get(measure.number) ?? []));
    out.push('</measure>');
  });
  out.push('</section></score></mdiv></body></music></mei>');
  return out.join('');
}

/** Every note id a view draws, by its first element id: the golden path's "every note has an element" check. */
export function noteElementIds(view: ScoreView): Map<number, string> {
  const ids = new Map<number, string>();
  for (const part of view.parts) {
    for (const measure of part.measures) {
      for (const staff of measure.staves) {
        for (const voice of staff.voices) {
          for (const event of voice.events) {
            for (const note of event.notes) {
              if (!ids.has(note.noteId)) {
                ids.set(note.noteId, note.id);
              }
            }
          }
        }
      }
    }
  }
  return ids;
}

/** `clefOctave`, re-exported for the stand-in's tests. */
export { clefOctave };
