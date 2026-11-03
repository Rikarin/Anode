import {
  ApiError,
  type ChordSymbol,
  DefaultRegionNotation,
  type DeltaOp,
  type EditCommand,
  type ImportedContent,
  type LyricSyllable,
  type MidiRegion,
  type Note,
  type PitchName,
  type Project,
  type ProjectScore,
  type ScoreLayout,
  type ScoreSymbol,
  type ScoreSymbolKind,
  type StaffStyle,
  type Track
} from '@anode/api-client';
import { NeutralPlayback } from '../../midi/midi-math';
import { fifthsOfKey, pitchAt, stepPitch } from '../../score/notation-math';
import { findFactoryStyle, resolveStaffStyle } from './fake-staff-styles';

/** A command of one type. */
type Command<T extends EditCommand['type']> = Extract<EditCommand, { type: T }>;

const Ppq = 960;

function refuse(message: string): never {
  throw new ApiError(422, message);
}

/** The Phase 9 commands (docs/10-phase-9.md, "Commands"), but `importTracks`, which the fake Server applies itself. */
const Phase9Commands: ReadonlySet<EditCommand['type']> = new Set([
  'setStaffStyle',
  'deleteStaffStyle',
  'assignStaffStyle',
  'setRegionNotation',
  'addScoreSymbols',
  'editScoreSymbols',
  'deleteScoreSymbols',
  'setLyrics',
  'addChordSymbols',
  'editChordSymbols',
  'deleteChordSymbols',
  'setScoreSet',
  'deleteScoreSet',
  'setScoreLayout',
  'moveScoreNotes',
  'addScoreNote'
]);

export function isPhase9Command(command: EditCommand): boolean {
  return Phase9Commands.has(command.type);
}

/** What the Phase 9 commands need from the fake Server. */
export interface Phase9Context {
  id(prefix: string): string;
  noteId(): number;
}

// ---- the anchor rules (`ScoreSymbols` in Contracts)

export const NoteOnlyKinds: ReadonlySet<ScoreSymbolKind> = new Set([
  'staccato',
  'staccatissimo',
  'accent',
  'marcato',
  'tenuto',
  'fermata',
  'trill',
  'tieHidden'
]);
export const SpanningKinds: ReadonlySet<ScoreSymbolKind> = new Set([
  'slur',
  'crescendo',
  'diminuendo',
  'octaveUp',
  'octaveDown'
]);
export const WithTextKinds: ReadonlySet<ScoreSymbolKind> = new Set(['dynamic', 'text', 'tempoText', 'rehearsal']);
export const Dynamics: readonly string[] = [
  'pppp',
  'ppp',
  'pp',
  'p',
  'mp',
  'mf',
  'f',
  'ff',
  'fff',
  'ffff',
  'sf',
  'sfz',
  'sffz',
  'sfp',
  'fp',
  'fz',
  'rf',
  'rfz'
];
export const MaxTextLength = 200;

function trackNotes(project: Project, trackId: string): Map<number, Note> {
  return new Map(project.midiRegions.filter(r => r.trackId === trackId).flatMap(r => r.notes.map(n => [n.id, n])));
}

function midiTrack(project: Project, id: string): Track {
  const track = project.tracks.find(t => t.id === id) ?? refuse(`No track ${id}.`);
  if (track.kind !== 'instrument' && track.kind !== 'midi') {
    refuse(`${track.name} is not a MIDI track: the score shows MIDI.`);
  }
  return track;
}

/** Checks a symbol against the anchor rules (the Server's 422s). */
export function checkSymbol(project: Project, symbol: Omit<ScoreSymbol, 'id'>): void {
  midiTrack(project, symbol.trackId);
  const notes = trackNotes(project, symbol.trackId);
  const hasNote = symbol.noteId != null;
  const hasTick = symbol.tick != null;
  if (hasNote && !notes.has(symbol.noteId as number)) {
    refuse(`No note ${symbol.noteId} on this track.`);
  }
  if (symbol.endNoteId != null && !notes.has(symbol.endNoteId)) {
    refuse(`No note ${symbol.endNoteId} on this track.`);
  }
  if (hasTick && (symbol.tick as number) < 0) {
    refuse('A symbol cannot be before the start.');
  }
  if (NoteOnlyKinds.has(symbol.kind) && (!hasNote || hasTick)) {
    refuse(`A ${symbol.kind} is attached to a note.`);
  }
  if (!NoteOnlyKinds.has(symbol.kind) && hasNote === hasTick) {
    refuse(`A ${symbol.kind} is attached to a note or a position, one of them.`);
  }
  if (SpanningKinds.has(symbol.kind)) {
    if (symbol.kind === 'slur' && (!hasNote || symbol.endNoteId == null)) {
      refuse('A slur spans two notes.');
    }
    if ((symbol.endNoteId == null) === (symbol.endTick == null)) {
      refuse(`A ${symbol.kind} ends at a note or a position, one of them.`);
    }
  } else if (symbol.endNoteId != null || symbol.endTick != null) {
    refuse(`A ${symbol.kind} does not span.`);
  }
  const text = symbol.text ?? '';
  if (WithTextKinds.has(symbol.kind)) {
    if (!text.trim()) {
      refuse(`A ${symbol.kind} needs its text.`);
    }
    if (symbol.kind === 'dynamic' && !Dynamics.includes(text)) {
      refuse(`${text} is not a dynamic (${Dynamics.join(', ')}).`);
    }
  } else if (text) {
    refuse(`A ${symbol.kind} has no text.`);
  }
  if (text.length > MaxTextLength) {
    refuse(`Score text is at most ${MaxTextLength} characters.`);
  }
  if (symbol.staff != null && (symbol.staff < 0 || symbol.staff > 3)) {
    refuse('No such staff.');
  }
}

function checkStyle(style: StaffStyle): void {
  if (!style.id.trim() || style.id.startsWith('factory.')) {
    refuse('A project staff style needs its own id (not factory.…).');
  }
  if (!style.name.trim()) {
    refuse('A staff style needs a name.');
  }
  if (style.staves.length < 1 || style.staves.length > 4) {
    refuse('A staff style has one to four staves.');
  }
  style.staves.forEach((staff, i) => {
    if (staff.voices < 1 || staff.voices > 4) {
      refuse('A staff has one to four voices.');
    }
    if (Math.abs(staff.transpose) > 48) {
      refuse('Transposition is within four octaves.');
    }
    const last = i === style.staves.length - 1;
    if (!last && (staff.splitPitch == null || staff.splitPitch < 0 || staff.splitPitch > 127)) {
      refuse('Every staff but the last needs a split point (0…127).');
    }
  });
  if (style.displayQuantizeTicks < 0 || style.tripletQuantizeTicks < 0) {
    refuse('Display quantise grids are 0 (off) or more ticks.');
  }
  for (const entry of style.drumMap ?? []) {
    if (entry.pitch < 0 || entry.pitch > 127) {
      refuse('A drum map pitch is 0…127.');
    }
  }
}

function checkLayout(layout: ScoreLayout): void {
  if (!(
    layout.pageWidthMm >= 50 &&
    layout.pageHeightMm >= 50 &&
    layout.pageWidthMm <= 2000 &&
    layout.pageHeightMm <= 2000
  )) {
    refuse('A page is 50…2000 mm a side.');
  }
  const m = layout.margins;
  if ([m.top, m.bottom, m.left, m.right].some(v => !(v >= 0) || v > 50)) {
    refuse('Margins are 0…50 mm.');
  }
  if (m.left + m.right >= layout.pageWidthMm || m.top + m.bottom >= layout.pageHeightMm) {
    refuse('The margins leave no room on the page.');
  }
  if (!(layout.staffHeightMm >= 3 && layout.staffHeightMm <= 12)) {
    refuse('A staff is 3…12 mm high.');
  }
}

function checkChord(project: Project, chord: Omit<ChordSymbol, 'id'>): void {
  midiTrack(project, chord.trackId);
  if (chord.tick < 0) {
    refuse('A chord symbol cannot be before the start.');
  }
  for (const name of [chord.root, chord.bass].filter((p): p is PitchName => !!p)) {
    if (Math.abs(name.alter) > 2) {
      refuse('A chord root or bass is altered by at most two semitones.');
    }
  }
  if (chord.quality.length > 20) {
    refuse('A chord quality is at most 20 characters.');
  }
}

function withScore(project: Project, change: Partial<ProjectScore>): Project {
  return { ...project, score: { ...project.score, ...change } };
}

function upsert<T extends { id: string }>(list: readonly T[], item: T): T[] {
  const index = list.findIndex(x => x.id === item.id);
  return index < 0 ? [...list, item] : list.map((x, i) => (i === index ? item : x));
}

function keyFifthsAt(project: Project, tick: number): number {
  const keys = [...project.settings.keys].sort((a, b) => a.tick - b.tick).filter(k => k.tick <= tick);
  return fifthsOfKey(keys.at(-1));
}

function barTicks(project: Project): number {
  const { numerator, denominator } = project.settings.signature;
  return ((Ppq * 4) / denominator) * numerator;
}

export function applyPhase9Command(project: Project, command: EditCommand, ctx: Phase9Context): Project {
  switch (command.type) {
    case 'setStaffStyle':
      checkStyle(command.style);
      return withScore(project, { staffStyles: upsert(project.score.staffStyles, structuredClone(command.style)) });
    case 'deleteStaffStyle': {
      if (!project.score.staffStyles.some(s => s.id === command.styleId)) {
        refuse(`No staff style ${command.styleId}.`);
      }
      const tracks = project.tracks.map(t => (t.staffStyleId === command.styleId ? withoutStyle(t) : t));
      return withScore(
        { ...project, tracks },
        {
          staffStyles: project.score.staffStyles.filter(s => s.id !== command.styleId)
        }
      );
    }
    case 'assignStaffStyle': {
      const styleId = command.styleId ?? null;
      if (styleId && !project.score.staffStyles.some(s => s.id === styleId) && !findFactoryStyle(styleId)) {
        refuse(`No staff style ${styleId}.`);
      }
      const ids = new Set(command.trackIds);
      ids.forEach(id => midiTrack(project, id));
      return {
        ...project,
        tracks: project.tracks.map(t =>
          ids.has(t.id) ? (styleId ? { ...t, staffStyleId: styleId } : withoutStyle(t)) : t
        )
      };
    }
    case 'setRegionNotation':
      return setRegionNotation(project, command);
    case 'addScoreSymbols': {
      const added = command.symbols.map(s => {
        const symbol: ScoreSymbol = { ...structuredClone(s), id: ctx.id('sym') };
        checkSymbol(project, symbol);
        return symbol;
      });
      return withScore(project, { symbols: [...project.score.symbols, ...added] });
    }
    case 'editScoreSymbols': {
      let symbols = project.score.symbols;
      for (const symbol of command.symbols) {
        if (!symbols.some(s => s.id === symbol.id)) {
          refuse(`No score symbol ${symbol.id}.`);
        }
        checkSymbol(project, symbol);
        symbols = upsert(symbols, structuredClone(symbol));
      }
      return withScore(project, { symbols });
    }
    case 'deleteScoreSymbols': {
      const ids = new Set(command.symbolIds);
      for (const id of ids) {
        if (!project.score.symbols.some(s => s.id === id)) {
          refuse(`No score symbol ${id}.`);
        }
      }
      return withScore(project, { symbols: project.score.symbols.filter(s => !ids.has(s.id)) });
    }
    case 'setLyrics':
      return setLyrics(project, command, ctx);
    case 'addChordSymbols': {
      const added = command.chords.map(c => {
        checkChord(project, c);
        return { ...structuredClone(c), id: ctx.id('chd') };
      });
      return withScore(project, { chords: [...project.score.chords, ...added] });
    }
    case 'editChordSymbols': {
      let chords = project.score.chords;
      for (const chord of command.chords) {
        if (!chords.some(c => c.id === chord.id)) {
          refuse(`No chord symbol ${chord.id}.`);
        }
        checkChord(project, chord);
        chords = upsert(chords, structuredClone(chord));
      }
      return withScore(project, { chords });
    }
    case 'deleteChordSymbols': {
      const ids = new Set(command.chordIds);
      for (const id of ids) {
        if (!project.score.chords.some(c => c.id === id)) {
          refuse(`No chord symbol ${id}.`);
        }
      }
      return withScore(project, { chords: project.score.chords.filter(c => !ids.has(c.id)) });
    }
    case 'setScoreSet': {
      const set = command.set;
      if (!set.id.trim() || !set.name.trim()) {
        refuse('A score set needs an id and a name.');
      }
      set.trackIds.forEach(id => midiTrack(project, id));
      if (new Set(set.trackIds).size !== set.trackIds.length) {
        refuse('A track is in a score set once.');
      }
      return withScore(project, { sets: upsert(project.score.sets, structuredClone(set)) });
    }
    case 'deleteScoreSet':
      if (!project.score.sets.some(s => s.id === command.setId)) {
        refuse(`No score set ${command.setId}.`);
      }
      return withScore(project, { sets: project.score.sets.filter(s => s.id !== command.setId) });
    case 'setScoreLayout':
      checkLayout(command.layout);
      command.layout.instrumentNames.forEach(n => midiTrack(project, n.trackId));
      return withScore(project, { layout: structuredClone(command.layout) });
    case 'moveScoreNotes':
      return moveScoreNotes(project, command);
    case 'addScoreNote':
      return addScoreNote(project, command, ctx);
    default:
      return project;
  }
}

function withoutStyle(track: Track): Track {
  const { staffStyleId: _, ...rest } = track;
  return rest;
}

function setRegionNotation(project: Project, command: Command<'setRegionNotation'>): Project {
  const ids = new Set(command.regionIds);
  for (const id of ids) {
    if (!project.midiRegions.some(r => r.id === id)) {
      refuse(`No MIDI region ${id}.`);
    }
  }
  if (command.maxDots != null && (command.maxDots < 0 || command.maxDots > 3)) {
    refuse('Max dots is 0…3.');
  }
  for (const grid of [command.displayQuantizeTicks, command.tripletQuantizeTicks]) {
    if (grid != null && grid < 0) {
      refuse('Display quantise grids are 0 or more ticks.');
    }
  }
  return {
    ...project,
    midiRegions: project.midiRegions.map(r =>
      ids.has(r.id)
        ? {
            ...r,
            notation: {
              displayQuantizeTicks: command.displayQuantizeTicks ?? r.notation.displayQuantizeTicks,
              tripletQuantizeTicks: command.tripletQuantizeTicks ?? r.notation.tripletQuantizeTicks,
              interpretation: command.interpretation ?? r.notation.interpretation,
              noOverlap: command.noOverlap ?? r.notation.noOverlap,
              syncopation: command.syncopation ?? r.notation.syncopation,
              maxDots: command.maxDots ?? r.notation.maxDots
            }
          }
        : r
    )
  };
}

function setLyrics(project: Project, command: Command<'setLyrics'>, ctx: Phase9Context): Project {
  if (!(command.verse >= 1 && command.verse <= 8)) {
    refuse('Verses are 1…8.');
  }
  const notes = new Set(project.midiRegions.flatMap(r => r.notes.map(n => n.id)));
  let lyrics = project.score.lyrics;
  for (const change of command.syllables) {
    if (!notes.has(change.noteId)) {
      refuse(`No note ${change.noteId}.`);
    }
    if (change.text.length > MaxTextLength) {
      refuse(`A syllable is at most ${MaxTextLength} characters.`);
    }
    const existing = lyrics.find(l => l.noteId === change.noteId && l.verse === command.verse);
    if (!change.text) {
      lyrics = lyrics.filter(l => l !== existing);
      continue;
    }
    const lyric: LyricSyllable = {
      id: existing?.id ?? ctx.id('lyr'),
      noteId: change.noteId,
      verse: command.verse,
      text: change.text,
      syllabic: change.syllabic,
      extender: change.extender
    };
    lyrics = upsert(lyrics, lyric);
  }
  return withScore(project, { lyrics });
}

/** The staff (and its clef and transposition) a note of a track is drawn on. */
function staffFor(project: Project, trackId: string, pitch: number) {
  const style = resolveStaffStyle(project.score, project.tracks.find(t => t.id === trackId)?.staffStyleId);
  const index = style.staves.findIndex(s => s.splitPitch != null && pitch >= s.splitPitch);
  return { style, staff: style.staves[index < 0 ? style.staves.length - 1 : index] ?? style.staves[0] };
}

function moveScoreNotes(project: Project, command: Command<'moveScoreNotes'>): Project {
  const region =
    project.midiRegions.find(r => r.id === command.regionId) ?? refuse(`No MIDI region ${command.regionId}.`);
  const ids = new Set(command.noteIds);
  for (const id of ids) {
    if (!region.notes.some(n => n.id === id)) {
      refuse(`No note ${id} in ${region.name}.`);
    }
  }
  const ticks = Math.round(command.ticks);
  const notes = region.notes.map(note => {
    if (!ids.has(note.id)) {
      return note;
    }
    const { staff, style } = staffFor(project, region.trackId, note.pitch);
    const fifths = keyFifthsAt(project, region.startTick + note.startTick);
    let pitch = note.pitch;
    if (command.steps !== 0 || command.accidental) {
      if (style.drumMap?.length) {
        // A drum staff moves between the drum map's lines.
        const positions = [...new Set(style.drumMap.map(d => d.staffPosition))].sort((a, b) => a - b);
        const at = style.drumMap.find(d => d.pitch === note.pitch)?.staffPosition ?? 0;
        const target = positions.find(p => p === at + command.steps) ?? at;
        pitch = style.drumMap.find(d => d.staffPosition === target)?.pitch ?? pitch;
      } else {
        pitch = stepPitch(note.pitch, command.steps, staff?.transpose ?? 0, fifths, command.accidental ?? null);
      }
    }
    if (pitch < 0 || pitch > 127) {
      refuse('A note goes outside the MIDI range.');
    }
    const startTick = note.startTick + ticks;
    if (region.startTick + startTick < 0) {
      refuse('A note cannot move before the start.');
    }
    return { ...note, pitch, startTick };
  });
  return {
    ...project,
    midiRegions: project.midiRegions.map(r =>
      r.id === region.id ? { ...r, notes: notes.sort((a, b) => a.startTick - b.startTick || a.pitch - b.pitch) } : r
    )
  };
}

function addScoreNote(project: Project, command: Command<'addScoreNote'>, ctx: Phase9Context): Project {
  const track = midiTrack(project, command.trackId);
  const tick = Math.round(command.tick);
  const length = Math.round(command.lengthTicks);
  if (tick < 0 || !(length > 0)) {
    refuse('A note needs a position and a length.');
  }
  const velocity = command.velocity ?? 80;
  if (velocity < 1 || velocity > 127) {
    refuse('Velocity is 1…127.');
  }
  const style = resolveStaffStyle(project.score, track.staffStyleId);
  const staffIndex = command.staff;
  const staff = style.staves[staffIndex] ?? refuse(`No staff ${staffIndex} in ${style.name}.`);
  let pitch: number;
  if (style.drumMap?.length) {
    pitch =
      style.drumMap.find(d => d.staffPosition === command.staffPosition)?.pitch ??
      refuse('No drum on that line of the drum map.');
  } else {
    pitch = pitchAt(
      staff.clef,
      staff.transpose,
      keyFifthsAt(project, tick),
      command.staffPosition,
      command.accidental ?? null
    );
  }
  if (pitch < 0 || pitch > 127) {
    refuse('That staff position is outside the MIDI range.');
  }
  let region: MidiRegion | undefined;
  if (command.regionId) {
    region = project.midiRegions.find(r => r.id === command.regionId && r.trackId === track.id);
    if (!region) {
      refuse(`No MIDI region ${command.regionId} on ${track.name}.`);
    }
  } else {
    region = project.midiRegions.find(
      r => r.trackId === track.id && r.startTick <= tick && tick < r.startTick + r.lengthTicks
    );
  }
  let midiRegions = project.midiRegions;
  if (!region) {
    // A new region of whole bars around the note.
    const bar = barTicks(project);
    const start = Math.floor(tick / bar) * bar;
    const end = Math.ceil((tick + length) / bar) * bar;
    region = {
      id: ctx.id('mid'),
      trackId: track.id,
      name: track.name,
      startTick: start,
      lengthTicks: Math.max(bar, end - start),
      playback: { ...NeutralPlayback },
      notation: { ...DefaultRegionNotation },
      notes: [],
      controls: [],
      muted: false,
      pitchSource: 'off',
      key: null
    };
    midiRegions = [...midiRegions, region];
  }
  const target = region;
  const note: Note = {
    id: ctx.noteId(),
    pitch,
    velocity,
    channel: 1,
    startTick: tick - target.startTick,
    lengthTicks: length,
    articulationId: 0
  };
  return {
    ...project,
    midiRegions: midiRegions.map(r =>
      r.id === target.id
        ? { ...r, notes: [...r.notes, note].sort((a, b) => a.startTick - b.startTick || a.pitch - b.pitch) }
        : r
    )
  };
}

/**
 * Removes score content whose anchor is gone, as the model does when notes or tracks are deleted (undo restores it
 * with them): note-attached symbols and syllables, a track's positioned symbols and chord symbols, set members and
 * instrument names.
 */
export function pruneScore(project: Project): Project {
  const tracks = new Set(project.tracks.map(t => t.id));
  const notes = new Set(project.midiRegions.flatMap(r => r.notes.map(n => n.id)));
  const score = project.score;
  const symbols = score.symbols.filter(
    s =>
      tracks.has(s.trackId) &&
      (s.noteId == null || notes.has(s.noteId)) &&
      (s.endNoteId == null || notes.has(s.endNoteId))
  );
  const lyrics = score.lyrics.filter(l => notes.has(l.noteId));
  const chords = score.chords.filter(c => tracks.has(c.trackId));
  const sets = score.sets.map(s => ({ ...s, trackIds: s.trackIds.filter(id => tracks.has(id)) }));
  const instrumentNames = score.layout.instrumentNames.filter(n => tracks.has(n.trackId));
  const unchanged =
    symbols.length === score.symbols.length &&
    lyrics.length === score.lyrics.length &&
    chords.length === score.chords.length &&
    sets.every((s, i) => s.trackIds.length === score.sets[i]?.trackIds.length) &&
    instrumentNames.length === score.layout.instrumentNames.length;
  return unchanged
    ? project
    : { ...project, score: { ...score, symbols, lyrics, chords, sets, layout: { ...score.layout, instrumentNames } } };
}

/** The score deltas from one snapshot to the next. */
export function diffScore(before: ProjectScore, after: ProjectScore, ops: DeltaOp[]): void {
  const same = (a: unknown, b: unknown) => JSON.stringify(a) === JSON.stringify(b);
  const list = <T extends { id: string }>(
    a: readonly T[],
    b: readonly T[],
    removed: (id: string) => DeltaOp,
    upserted: (item: T) => DeltaOp
  ) => {
    const kept = new Set(b.map(x => x.id));
    a.filter(x => !kept.has(x.id)).forEach(x => ops.push(removed(x.id)));
    const previous = new Map(a.map(x => [x.id, x]));
    b.filter(x => !same(previous.get(x.id), x)).forEach(x => ops.push(upserted(x)));
  };
  list(
    before.staffStyles,
    after.staffStyles,
    id => ({ op: 'staffStyleRemoved', id }),
    style => ({ op: 'staffStyleUpserted', style })
  );
  list(
    before.symbols,
    after.symbols,
    id => ({ op: 'scoreSymbolRemoved', id }),
    symbol => ({ op: 'scoreSymbolUpserted', symbol })
  );
  list(
    before.lyrics,
    after.lyrics,
    id => ({ op: 'lyricRemoved', id }),
    lyric => ({ op: 'lyricUpserted', lyric })
  );
  list(
    before.chords,
    after.chords,
    id => ({ op: 'chordSymbolRemoved', id }),
    chord => ({ op: 'chordSymbolUpserted', chord })
  );
  list(
    before.sets,
    after.sets,
    id => ({ op: 'scoreSetRemoved', id }),
    set => ({ op: 'scoreSetUpserted', set })
  );
  if (!same(before.layout, after.layout)) {
    ops.push({ op: 'scoreLayoutChanged', layout: after.layout });
  }
}

// ---- interchange

/** A file name with its extension (added when missing, as the Server does). */
export function withExtension(name: string, extension: string): string {
  return name.toLowerCase().endsWith(`.${extension}`) ? name : `${name}.${extension}`;
}

/** The base name of a path without its extension. */
export function baseName(path: string): string {
  return (path.split(/[\\/]/).pop() ?? path).replace(/\.[^.]+$/, '');
}

function region(id: string, trackId: string, name: string, bars: number, notes: Note[]): MidiRegion {
  return {
    id,
    trackId,
    name,
    startTick: 0,
    lengthTicks: bars * 4 * Ppq,
    playback: { ...NeutralPlayback },
    notation: { ...DefaultRegionNotation },
    notes,
    controls: [],
    muted: false,
    pitchSource: 'off',
    key: null
  };
}

function note(id: number, pitch: number, startTick: number, lengthTicks: number): Note {
  return { id, pitch, velocity: 80, channel: 1, startTick, lengthTicks, articulationId: 0 };
}

/**
 * What the fake reads from a MusicXML file (D's reader stands behind the real one): a string quartet when the file is
 * named so (four parts, a key change and a meter change), else one piano part with a dynamic, a slur, lyrics and chord
 * symbols. Ids are provisional, as the reader's are.
 */
export function musicXmlContent(path: string): ImportedContent {
  const name = baseName(path);
  if (/quartet/i.test(name)) {
    const parts: [string, string, number][] = [
      ['Violin I', 'factory.treble', 76],
      ['Violin II', 'factory.treble', 69],
      ['Viola', 'factory.viola', 62],
      ['Cello', 'factory.cello', 48]
    ];
    return {
      tracks: parts.map(([part, style, base], p) => ({
        id: `t${p + 1}`,
        name: part,
        kind: 'instrument',
        staffStyleId: style,
        midiRegions: [
          region(
            `r${p + 1}`,
            `t${p + 1}`,
            part,
            4,
            Array.from({ length: 8 }, (_, i) => note(p * 100 + i + 1, base + ((i * 2) % 7), i * 2 * Ppq, 2 * Ppq))
          )
        ],
        regions: [],
        panner: null
      })),
      signatures: [
        { tick: 0, numerator: 4, denominator: 4 },
        { tick: 8 * Ppq, numerator: 3, denominator: 4 }
      ],
      keys: [
        { tick: 0, tonic: 7, mode: 'major' },
        { tick: 8 * Ppq, tonic: 2, mode: 'major' }
      ],
      score: { staffStyles: [], symbols: [], lyrics: [], chords: [], title: 'String Quartet', composer: '' }
    };
  }
  const notes = [60, 62, 64, 65, 67, 65, 64, 62].map((pitch, i) => note(i + 1, pitch, i * Ppq, Ppq));
  return {
    tracks: [
      {
        id: 't1',
        name: name || 'Piano',
        kind: 'instrument',
        staffStyleId: 'factory.piano',
        midiRegions: [region('r1', 't1', name || 'Piano', 2, notes)],
        regions: [],
        panner: null
      }
    ],
    score: {
      staffStyles: [],
      symbols: [
        { id: 's1', kind: 'dynamic', trackId: 't1', tick: 0, text: 'mf' },
        { id: 's2', kind: 'slur', trackId: 't1', noteId: 1, endNoteId: 3 },
        { id: 's3', kind: 'staccato', trackId: 't1', noteId: 4 }
      ],
      lyrics: ['Hap', 'py', 'day'].map((text, i) => ({
        id: `l${i + 1}`,
        noteId: i + 1,
        verse: 1,
        text,
        syllabic: i === 0 ? ('begin' as const) : i === 1 ? ('end' as const) : ('single' as const),
        extender: false
      })),
      chords: [
        { id: 'c1', trackId: 't1', tick: 0, root: { step: 'c', alter: 0 }, quality: '', bass: null, showGrid: false },
        {
          id: 'c2',
          trackId: 't1',
          tick: 4 * Ppq,
          root: { step: 'g', alter: 0 },
          quality: '7',
          bass: null,
          showGrid: false
        }
      ],
      title: name,
      composer: null
    }
  };
}
