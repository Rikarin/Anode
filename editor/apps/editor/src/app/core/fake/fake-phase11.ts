import {
  ApiError,
  type AudioRegion,
  type BassSwitches,
  type Chord,
  type ChordIdRequest,
  type ChordIdResult,
  ChordQualities,
  type ChordTrack,
  DefaultRegionNotation,
  type DetectedChord,
  type DetectedKey,
  DrumKitNotes,
  type DrummerSwitches,
  type DrumPiece,
  type EditCommand,
  type KeysSwitches,
  type MidiRegion,
  type NewChord,
  type NewNote,
  type Note,
  type Pattern,
  type PatternRegion,
  pitchClass,
  type PitchName,
  type Project,
  type RegionKey,
  type ScaleChange,
  type ScaleKind,
  type SectionKind,
  sectionKind,
  type SessionPlayerCatalog,
  type SessionPlayerKind,
  type SessionPlayerParameters,
  type SessionPlayerRegion,
  type SessionPlayerStyle,
  type Track
} from '@anode/api-client';
import { MeterMap } from '@anode/timeline';
import { NeutralPlayback } from '../../midi/midi-math';
import { defaultRow, hash32, MaxRowLength } from '../../music/pattern';

/** A command of one type. */
type Command<T extends EditCommand['type']> = Extract<EditCommand, { type: T }>;

const Ppq = 960;

function refuse(message: string): never {
  throw new ApiError(422, message);
}

/** A value the fake's own tables always hold (an index into them, a default). */
function must<T>(value: T | null | undefined): T {
  if (value === null || value === undefined) {
    throw new Error('The fake Server is missing a value it builds itself.');
  }
  return value;
}

function inRange(value: number, min: number, max: number, label: string): number {
  if (!Number.isFinite(value) || value < min || value > max) {
    refuse(`${label} is ${min} to ${max}.`);
  }
  return value;
}

/** What the Phase 11 commands need from the fake Server. */
export interface Phase11Context {
  /** A new id with this prefix (`chd_7`). */
  id(prefix: string): string;
  /** A new note id (unique project-wide). */
  noteId(): number;
  /** Draws a new seed (Regenerate, a new region). */
  seed(): number;
  /** The style library. */
  catalog: SessionPlayerCatalog;
  /** Applies another command: a Drummer's kit through `createDrumMachine`, a player's instrument through `addTrack`. */
  apply(project: Project, command: EditCommand): Project;
  /** Whether a kit patch is installed (the fake Library's kits come with Anode Essentials). */
  hasKit(id: string): boolean;
}

/** A Drummer's pads when its style's kit is not installed: Drum Synth on the kit pieces' main notes. */
const FallbackPads: readonly { note: number; name: string }[] = [
  { note: 36, name: 'Kick' },
  { note: 38, name: 'Snare' },
  { note: 42, name: 'Closed Hat' },
  { note: 46, name: 'Open Hat' },
  { note: 50, name: 'High Tom' },
  { note: 45, name: 'Low Tom' },
  { note: 49, name: 'Crash' },
  { note: 51, name: 'Ride' }
];

// ---- the chord track

const Scales: ReadonlySet<ScaleKind> = new Set([
  'major',
  'minor',
  'harmonicMinor',
  'melodicMinor',
  'dorian',
  'phrygian',
  'lydian',
  'mixolydian',
  'locrian'
]);
const Steps: ReadonlySet<string> = new Set(['c', 'd', 'e', 'f', 'g', 'a', 'b']);

function checkPitchName(pitch: PitchName, label: string): PitchName {
  if (!Steps.has(pitch.step) || !Number.isInteger(pitch.alter) || pitch.alter < -2 || pitch.alter > 2) {
    refuse(`${label} is a note name with an alteration of −2…2.`);
  }
  return { step: pitch.step, alter: pitch.alter };
}

function checkChord<T extends NewChord>(chord: T): T {
  if (!Number.isFinite(chord.tick) || chord.tick < 0) {
    refuse('A chord is at a tick of 0 or more.');
  }
  if (!ChordQualities.some(q => q.quality === chord.quality)) {
    refuse(`${chord.quality} is not a chord quality.`);
  }
  return {
    ...chord,
    tick: Math.round(chord.tick),
    root: checkPitchName(chord.root, 'A chord root'),
    bass: chord.bass ? checkPitchName(chord.bass, 'A chord bass') : null
  };
}

function sortedChords(chords: readonly Chord[]): Chord[] {
  return [...chords].sort((a, b) => a.tick - b.tick);
}

function withChordTrack(project: Project, chordTrack: ChordTrack): Project {
  return { ...project, chordTrack };
}

/** Chords added at ticks: one at the tick of an existing chord replaces it. */
export function addChords(track: ChordTrack, added: readonly NewChord[], id: () => string): ChordTrack {
  const ticks = new Set(added.map(c => Math.round(c.tick)));
  const kept = track.chords.filter(c => !ticks.has(c.tick));
  return { ...track, chords: sortedChords([...kept, ...added.map(c => ({ id: id(), ...checkChord(c) }))]) };
}

function checkScales(scales: readonly ScaleChange[]): ScaleChange[] {
  const ticks = new Set<number>();
  for (const scale of scales) {
    if (!Number.isFinite(scale.tick) || scale.tick < 0) {
      refuse('A key and scale change is at a tick of 0 or more.');
    }
    if (!Scales.has(scale.scale)) {
      refuse(`${scale.scale} is not a scale.`);
    }
    const tick = Math.round(scale.tick);
    if (ticks.has(tick)) {
      refuse('Two key and scale changes at one tick.');
    }
    ticks.add(tick);
  }
  return scales
    .map(s => ({ tick: Math.round(s.tick), tonic: checkPitchName(s.tonic, 'A tonic'), scale: s.scale }))
    .sort((a, b) => a.tick - b.tick);
}

/**
 * Chord ID's result applied (`applyChordId`): `replace` swaps the chords in its range for the detected ones, `merge`
 * adds a detected chord only where the chord track has none within a beat, `none` writes keys only; the detected keys
 * go to their regions.
 */
export function applyChordId(project: Project, result: ChordIdResult, id: () => string): Project {
  let chords = project.chordTrack.chords;
  const detected = result.chords.map(c => ({ tick: c.tick, root: c.root, quality: c.quality, bass: c.bass ?? null }));
  if (result.write === 'replace') {
    chords = chords.filter(c => c.tick < result.startTick || c.tick >= result.endTick);
    chords = [...chords, ...detected.map(c => ({ id: id(), ...checkChord(c) }))];
  } else if (result.write === 'merge') {
    const free = detected.filter(d => !chords.some(c => Math.abs(c.tick - d.tick) < Ppq));
    chords = [...chords, ...free.map(c => ({ id: id(), ...checkChord(c) }))];
  }
  let next = withChordTrack(project, { ...project.chordTrack, chords: sortedChords(chords) });
  for (const key of result.keys) {
    next = setRegionKey(next, [key.regionId], key.key);
  }
  return next;
}

// ---- Pitch Source and region keys

function setRegionKey(project: Project, ids: readonly string[], key: RegionKey | null): Project {
  const checked = key ? { tonic: checkPitchName(key.tonic, 'A key'), mode: key.mode } : null;
  if (checked && !(['major', 'minor'] as const).some(mode => mode === checked.mode)) {
    refuse('A key is major or minor.');
  }
  const wanted = new Set(ids);
  for (const id of ids) {
    if (!project.midiRegions.some(r => r.id === id) && !project.regions.some(r => r.id === id)) {
      refuse(`No MIDI or audio region ${id}.`);
    }
  }
  return {
    ...project,
    midiRegions: project.midiRegions.map(r => (wanted.has(r.id) ? { ...r, key: checked } : r)),
    regions: project.regions.map(r => (wanted.has(r.id) ? { ...r, key: checked } : r))
  };
}

function setPitchSource(project: Project, command: Command<'setPitchSource'>): Project {
  if (!['off', 'root', 'chords'].includes(command.mode)) {
    refuse(`${command.mode} is not a Pitch Source mode.`);
  }
  const wanted = new Set(command.regionIds);
  for (const id of command.regionIds) {
    const audio = project.regions.find(r => r.id === id);
    if (audio && command.mode === 'chords') {
      refuse(`${audio.name} is an audio region: Chords follows MIDI regions only.`);
    }
    if (!audio && !project.midiRegions.some(r => r.id === id)) {
      refuse(`No MIDI or audio region ${id}.`);
    }
  }
  return {
    ...project,
    midiRegions: project.midiRegions.map(r => (wanted.has(r.id) ? { ...r, pitchSource: command.mode } : r)),
    regions: project.regions.map(r => (wanted.has(r.id) ? { ...r, pitchSource: command.mode } : r))
  };
}

// ---- the style library

const Pieces: readonly DrumPiece[] = ['kick', 'snare', 'hiHat', 'toms', 'cymbals', 'percussion'];

/** A player's own switches at their defaults (the others null). */
export function defaultSwitches(player: SessionPlayerKind): Pick<SessionPlayerParameters, 'drums' | 'bass' | 'keys'> {
  return {
    drums:
      player === 'drummer'
        ? {
            pieces: Pieces.map(piece => ({ piece, on: piece !== 'percussion', variation: 0 })),
            rhythmSourceTrackId: null
          }
        : null,
    bass: player === 'bass' ? { lowNote: 28, highNote: 55, slides: false, deadNotes: false } : null,
    keys:
      player === 'keyboard' ? { voicing: 'close', lowNote: 48, highNote: 84, leftHand: 'off', rhythmId: null } : null
  };
}

/** The XY pad, fills, swing and feel per section kind: quiet intros, full choruses, a lighter bridge. */
const SectionPads: Readonly<Record<SectionKind, readonly [number, number]>> = {
  intro: [0.3, 0.35],
  verse: [0.45, 0.5],
  chorus: [0.65, 0.75],
  bridge: [0.5, 0.45],
  outro: [0.35, 0.6]
};

function styleParameters(
  styleId: string,
  player: SessionPlayerKind,
  complexity: number,
  intensity: number,
  extra: Partial<SessionPlayerParameters> = {}
): SessionPlayerParameters {
  return {
    styleId,
    presetId: null,
    pad: { complexity, intensity },
    fillAmount: 0.5,
    fillAtSectionEnds: true,
    swing: 0,
    feel: 0,
    followChords: player !== 'drummer',
    ...defaultSwitches(player),
    ...extra
  };
}

function style(
  id: string,
  name: string,
  player: SessionPlayerKind,
  genre: string,
  description: string,
  options: { kitPatchId?: string; synthPlayer?: boolean; swing?: number } = {}
): SessionPlayerStyle {
  const extra = { swing: options.swing ?? 0 };
  return {
    id,
    name,
    player,
    genre,
    description,
    synthPlayer: options.synthPlayer ?? false,
    kitPatchId: options.kitPatchId ?? null,
    rhythms:
      player === 'keyboard'
        ? [
            { id: 'sustain', name: 'Sustained' },
            { id: 'pulse', name: 'Pulsing Eighths' },
            { id: 'arpeggio', name: 'Arpeggio' }
          ]
        : [],
    presets: [
      { id: `${id}.steady`, name: 'Steady', parameters: styleParameters(id, player, 0.5, 0.55, extra) },
      {
        id: `${id}.laid-back`,
        name: 'Laid Back',
        parameters: styleParameters(id, player, 0.35, 0.4, { ...extra, feel: -0.3 })
      },
      {
        id: `${id}.driving`,
        name: 'Driving',
        parameters: styleParameters(id, player, 0.75, 0.8, { ...extra, feel: 0.2 })
      }
    ],
    sections: (Object.keys(SectionPads) as SectionKind[]).map(kind => ({
      kind,
      parameters: styleParameters(id, player, SectionPads[kind][0], SectionPads[kind][1], extra)
    }))
  };
}

/**
 * The fake style library (generic names, as ADR-0014 asks): four drum styles, three bass styles and three keys styles
 * with a Synth Player, each with three presets and its defaults per section kind; Drummers get the fake kits.
 */
export const FakeSessionPlayerCatalog: SessionPlayerCatalog = {
  version: 'fake-1',
  styles: [
    style('drums.rock', 'Rock — Straight Ahead', 'drummer', 'Rock', 'Steady backbeat, open hats in the chorus.', {
      kitPatchId: 'kit_hybrid'
    }),
    style('drums.pop', 'Pop — Bright', 'drummer', 'Pop', 'Tight kick and claps, sixteenth hats.', {
      kitPatchId: 'kit_hybrid'
    }),
    style('drums.funk', 'Funk — Tight Pocket', 'drummer', 'Funk', 'Syncopated kick, ghost notes, a little swing.', {
      kitPatchId: 'kit_hybrid',
      swing: 0.2
    }),
    style('drums.electronic', 'Electronic — Four on the Floor', 'drummer', 'Electronic', 'Kick on every beat.', {
      kitPatchId: 'kit_electro'
    }),
    style('bass.rock', 'Rock Bass — Root Eighths', 'bass', 'Rock', 'Driving roots and fifths.'),
    style('bass.soul', 'Soul Bass — Melodic', 'bass', 'Soul', 'Walking approach notes between chords.', {
      swing: 0.15
    }),
    style('bass.electronic', 'Electronic Bass — Pulse', 'bass', 'Electronic', 'Octave pulses on the grid.'),
    style('keys.pop', 'Pop Piano — Block Chords', 'keyboard', 'Pop', 'Close voicings on the beat.'),
    style('keys.ballad', 'Ballad Piano — Open', 'keyboard', 'Ballad', 'Spread voicings with a left-hand root.'),
    style('keys.synth', 'Synth Player — Pulsing Pads', 'keyboard', 'Electronic', 'Pulsing pads and arpeggios.', {
      synthPlayer: true
    })
  ]
};

/** The player a track plays (`SessionPlayerGenerator.PlayerOf`); null for other tracks. */
export function playerOf(kind: Track['kind']): SessionPlayerKind | null {
  return kind === 'drummer'
    ? 'drummer'
    : kind === 'bassPlayer'
      ? 'bass'
      : kind === 'keyboardPlayer'
        ? 'keyboard'
        : null;
}

function findStyle(catalog: SessionPlayerCatalog, id: string): SessionPlayerStyle {
  return catalog.styles.find(s => s.id === id) ?? refuse(`No Session Player style ${id}.`);
}

/** A section kind's defaults in a style. */
function sectionDefaults(style: SessionPlayerStyle, kind: SectionKind): SessionPlayerParameters {
  return structuredClone(style.sections.find(s => s.kind === kind)?.parameters ?? must(style.sections[0]).parameters);
}

// ---- Session Player tracks and regions

/** Whether an id names a Session Player region. */
export function isSessionPlayerRegionId(id: string): boolean {
  return id.startsWith('spr_');
}

function findTrack(project: Project, id: string): Track {
  return project.tracks.find(t => t.id === id) ?? refuse(`No track ${id}.`);
}

function findRegion(project: Project, id: string): SessionPlayerRegion {
  return project.sessionPlayerRegions.find(r => r.id === id) ?? refuse(`No Session Player region ${id}.`);
}

function withRegion(project: Project, region: SessionPlayerRegion): Project {
  return {
    ...project,
    sessionPlayerRegions: project.sessionPlayerRegions.map(r => (r.id === region.id ? region : r))
  };
}

function playerTrack(project: Project, id: string): { track: Track; player: SessionPlayerKind } {
  const track = findTrack(project, id);
  const player = playerOf(track.kind) ?? refuse(`${track.name} is not a Session Player track.`);
  return { track, player };
}

/** A track's style: its last region's, else the player's first in the library. */
function trackStyle(project: Project, track: Track, player: SessionPlayerKind, catalog: SessionPlayerCatalog) {
  const last = project.sessionPlayerRegions.filter(r => r.trackId === track.id).at(-1);
  const id = last?.parameters.styleId ?? catalog.styles.find(s => s.player === player)?.id;
  return findStyle(catalog, id ?? refuse(`The library has no ${player} style.`));
}

/** The arrangement section a tick is in, if any. */
function sectionAt(project: Project, tick: number) {
  return project.arrangement.find(s => tick >= s.startTick && tick < s.startTick + s.lengthTicks) ?? null;
}

function checkSwitches(player: SessionPlayerKind, parameters: SessionPlayerParameters, project: Project): void {
  const owned = { drummer: parameters.drums, bass: parameters.bass, keyboard: parameters.keys };
  for (const [kind, switches] of Object.entries(owned)) {
    if (kind !== player && switches) {
      refuse(`A ${player} region has no ${kind} switches.`);
    }
  }
  if (parameters.drums) {
    checkDrums(parameters.drums, project);
  }
  if (parameters.bass) {
    checkRange(parameters.bass, 24, 72, 'The bass range');
  }
  if (parameters.keys) {
    checkRange(parameters.keys, 36, 96, 'The keys range');
    if (!['close', 'spread', 'shell'].includes(parameters.keys.voicing)) {
      refuse(`${parameters.keys.voicing} is not a voicing.`);
    }
    if (!['off', 'root', 'octaves', 'rootFifth'].includes(parameters.keys.leftHand)) {
      refuse(`${parameters.keys.leftHand} is not a left-hand part.`);
    }
  }
}

function checkDrums(drums: DrummerSwitches, project: Project): void {
  const seen = new Set<DrumPiece>();
  for (const piece of drums.pieces) {
    if (!Pieces.includes(piece.piece) || seen.has(piece.piece)) {
      refuse('Each kit piece is set once.');
    }
    seen.add(piece.piece);
    inRange(piece.variation, 0, 3, 'A pattern variation');
  }
  const source = drums.rhythmSourceTrackId;
  if (source && !project.tracks.some(t => t.id === source)) {
    refuse(`No track ${source} to follow.`);
  }
}

function checkRange(range: BassSwitches | KeysSwitches, min: number, max: number, label: string): void {
  if (range.lowNote < min || range.highNote > max || range.highNote - range.lowNote < 12) {
    refuse(`${label} is at least an octave within ${min}…${max}.`);
  }
}

function checkParameters(
  parameters: SessionPlayerParameters,
  player: SessionPlayerKind,
  project: Project,
  catalog: SessionPlayerCatalog
): SessionPlayerParameters {
  const style = findStyle(catalog, parameters.styleId);
  if (style.player !== player) {
    refuse(`${style.name} is a ${style.player} style.`);
  }
  if (parameters.presetId && !style.presets.some(p => p.id === parameters.presetId)) {
    refuse(`${style.name} has no preset ${parameters.presetId}.`);
  }
  inRange(parameters.pad.complexity, 0, 1, 'Complexity');
  inRange(parameters.pad.intensity, 0, 1, 'Intensity');
  inRange(parameters.fillAmount, 0, 1, 'The fill amount');
  inRange(parameters.swing, 0, 1, 'Swing');
  inRange(parameters.feel, -1, 1, 'Feel');
  checkSwitches(player, parameters, project);
  return structuredClone(parameters);
}

/** A new region's length: to the end of the section it starts in, else 8 bars. */
function defaultLength(project: Project, startTick: number): number {
  const section = sectionAt(project, startTick);
  if (section) {
    return section.startTick + section.lengthTicks - startTick;
  }
  const meter = new MeterMap(project.settings.signatures);
  const bar = Math.round(meter.barAt(startTick));
  return meter.barStart(bar + 8) - meter.barStart(bar);
}

function newRegion(
  project: Project,
  track: Track,
  player: SessionPlayerKind,
  start: { startTick: number; lengthTicks?: number | null; name?: string | null; seed?: number | null },
  parameters: SessionPlayerParameters | null,
  style: SessionPlayerStyle,
  ctx: Phase11Context
): SessionPlayerRegion {
  if (!Number.isFinite(start.startTick) || start.startTick < 0) {
    refuse('A region start must not be negative.');
  }
  const startTick = Math.round(start.startTick);
  const length = Math.round(start.lengthTicks ?? defaultLength(project, startTick));
  if (!(length > 0)) {
    refuse('A Session Player region needs a length.');
  }
  const section = sectionAt(project, startTick);
  const chosen = parameters ?? sectionDefaults(style, sectionKind(section?.name));
  return {
    id: ctx.id('spr'),
    trackId: track.id,
    name: start.name?.trim() || section?.name || track.name,
    startTick,
    lengthTicks: length,
    muted: false,
    parameters: checkParameters(chosen, player, project, ctx.catalog),
    seed: Math.round(start.seed ?? ctx.seed())
  };
}

const PlayerTrackKinds: Readonly<Record<SessionPlayerKind, Track['kind']>> = {
  drummer: 'drummer',
  bass: 'bassPlayer',
  keyboard: 'keyboardPlayer'
};
const PlayerNames: Readonly<Record<SessionPlayerKind, string>> = {
  drummer: 'Drummer',
  bass: 'Bass Player',
  keyboard: 'Keyboard Player'
};

/**
 * New Tracks' Session Player (`addSessionPlayerTrack`): a Drummer is a Drum Machine Designer with its style's kit
 * (its pads member tracks: multi-output), a Bass or Keyboard Player an instrument track (a Synth Player style plays the
 * synth, the others the electric piano or bass patch's instrument); with `regions`, one region per arrangement section
 * with its kind's defaults and its own seed, or 8 bars from the start.
 */
function addSessionPlayerTrack(
  project: Project,
  command: Command<'addSessionPlayerTrack'>,
  ctx: Phase11Context
): Project {
  const player = command.player;
  if (!(player in PlayerTrackKinds)) {
    refuse(`${player} is not a Session Player.`);
  }
  const style = command.styleId
    ? findStyle(ctx.catalog, command.styleId)
    : (ctx.catalog.styles.find(s => s.player === player) ?? refuse(`The library has no ${player} style.`));
  if (style.player !== player) {
    refuse(`${style.name} is a ${style.player} style.`);
  }
  const preset = command.presetId
    ? (style.presets.find(p => p.id === command.presetId) ?? refuse(`${style.name} has no preset ${command.presetId}.`))
    : style.presets[0];
  const name = command.name?.trim() || PlayerNames[player];
  const before = new Set(project.tracks.map(t => t.id));
  const kit = command.kitPatchId ?? style.kitPatchId ?? null;
  if (command.kitPatchId && !ctx.hasKit(command.kitPatchId)) {
    refuse(`No kit ${command.kitPatchId}.`);
  }
  let next =
    player === 'drummer'
      ? ctx.apply(project, { type: 'createDrumMachine', kitPatchId: kit && ctx.hasKit(kit) ? kit : null, name })
      : ctx.apply(project, {
          type: 'addTrack',
          kind: 'instrument',
          stereo: true,
          name,
          index: command.index ?? null,
          pluginId:
            command.pluginId ?? (style.synthPlayer || player === 'bass' ? 'anode.synth' : 'anode.electric-piano')
        });
  const added = next.tracks.find(t => !before.has(t.id) && t.parentId === null) ?? refuse('No track was added.');
  if (player === 'drummer' && !added.drumMachine?.pads.length) {
    for (const pad of FallbackPads) {
      next = ctx.apply(next, { type: 'addDrumPad', trackId: added.id, note: pad.note, name: pad.name, pluginId: null });
    }
  }
  const track: Track = { ...findTrack(next, added.id), kind: PlayerTrackKinds[player] };
  next = { ...next, tracks: next.tracks.map(t => (t.id === track.id ? track : t)) };
  if (player === 'drummer' && command.index != null) {
    next = ctx.apply(next, { type: 'moveTrack', trackId: track.id, index: command.index });
  }
  if (!command.regions) {
    return next;
  }
  const spans = next.arrangement.length
    ? next.arrangement.map(s => ({ startTick: s.startTick, lengthTicks: s.lengthTicks, name: s.name }))
    : [{ startTick: 0, lengthTicks: defaultLength({ ...next, arrangement: [] }, 0), name: null }];
  const regions = spans.map(span => {
    const defaults = sectionDefaults(style, sectionKind(span.name));
    const parameters = preset && !next.arrangement.length ? { ...structuredClone(preset.parameters) } : defaults;
    return newRegion(next, track, player, span, { ...parameters, presetId: parameters.presetId ?? null }, style, ctx);
  });
  return { ...next, sessionPlayerRegions: [...next.sessionPlayerRegions, ...regions] };
}

/** `setSessionPlayerRegion`: a preset applies first; any other parameter change clears `presetId`; the seed stays. */
function setSessionPlayerRegion(
  project: Project,
  command: Command<'setSessionPlayerRegion'>,
  ctx: Phase11Context
): Project {
  const region = findRegion(project, command.regionId);
  const { player } = playerTrack(project, region.trackId);
  let parameters = structuredClone(region.parameters);
  if (command.styleId != null && command.styleId !== parameters.styleId) {
    const style = findStyle(ctx.catalog, command.styleId);
    if (style.player !== player) {
      refuse(`${style.name} is a ${style.player} style.`);
    }
    // A new style keeps the region's pad and switches; its preset is the style's own or none.
    parameters = { ...parameters, styleId: style.id, presetId: null };
  }
  if (command.presetId != null) {
    const style = findStyle(ctx.catalog, parameters.styleId);
    const preset =
      style.presets.find(p => p.id === command.presetId) ?? refuse(`${style.name} has no preset ${command.presetId}.`);
    parameters = { ...structuredClone(preset.parameters), presetId: preset.id };
  }
  const changes: Partial<SessionPlayerParameters> = {};
  if (command.complexity != null || command.intensity != null) {
    changes.pad = {
      complexity: command.complexity ?? parameters.pad.complexity,
      intensity: command.intensity ?? parameters.pad.intensity
    };
  }
  const scalars = ['fillAmount', 'fillAtSectionEnds', 'swing', 'feel', 'followChords'] as const;
  for (const key of scalars) {
    const value = command[key];
    if (value != null) {
      Object.assign(changes, { [key]: value });
    }
  }
  if (command.drums) {
    changes.drums = command.drums;
  }
  if (command.bass) {
    changes.bass = command.bass;
  }
  if (command.keys) {
    changes.keys = command.keys;
  }
  if (Object.keys(changes).length) {
    parameters = { ...parameters, ...structuredClone(changes), presetId: null };
  }
  if (command.startTick != null && !(command.startTick >= 0)) {
    refuse('A region start must not be negative.');
  }
  if (command.lengthTicks != null && !(command.lengthTicks > 0)) {
    refuse('A Session Player region needs a length.');
  }
  return withRegion(project, {
    ...region,
    startTick: Math.round(command.startTick ?? region.startTick),
    lengthTicks: Math.round(command.lengthTicks ?? region.lengthTicks),
    name: command.name?.trim() || region.name,
    muted: command.muted ?? region.muted,
    parameters: checkParameters(parameters, player, project, ctx.catalog)
  });
}

/** Convert's result: each region replaced by a MIDI region of its notes (or a Drummer's by a pattern region). */
function convertRegions(project: Project, command: Command<'convertSessionPlayerRegions'>, ctx: Phase11Context) {
  let next = project;
  for (const converted of command.regions) {
    const region = findRegion(next, converted.regionId);
    const { player } = playerTrack(next, region.trackId);
    const rest = next.sessionPlayerRegions.filter(r => r.id !== region.id);
    if (converted.target === 'pattern') {
      if (player !== 'drummer' || !converted.pattern) {
        refuse('Only a Drummer region converts to a pattern.');
      }
      next = {
        ...next,
        sessionPlayerRegions: rest,
        patternRegions: [...next.patternRegions, ...patternRegions(region, converted.pattern, ctx)]
      };
    } else {
      const midi: MidiRegion = {
        id: ctx.id('mid'),
        trackId: region.trackId,
        name: region.name,
        startTick: region.startTick,
        lengthTicks: region.lengthTicks,
        playback: { ...NeutralPlayback },
        notation: { ...DefaultRegionNotation },
        notes: converted.notes.map(n => ({ ...n, id: ctx.noteId(), articulationId: n.articulationId ?? 0 })),
        controls: [],
        muted: region.muted,
        pitchSource: 'off',
        key: null
      };
      next = { ...next, sessionPlayerRegions: rest, midiRegions: [...next.midiRegions, midi] };
    }
  }
  return next;
}

/** `moveRegion` on a Session Player region: a new start (and track, of the same player). */
export function moveSessionPlayerRegion(project: Project, command: Command<'moveRegion'>): Project {
  const region = findRegion(project, command.regionId);
  if (!(command.startTick >= 0)) {
    refuse('A region start must not be negative.');
  }
  const trackId = command.trackId ?? region.trackId;
  if (
    trackId !== region.trackId &&
    playerTrack(project, trackId).player !== playerTrack(project, region.trackId).player
  ) {
    refuse('A Session Player region moves to a track of its player.');
  }
  return withRegion(project, { ...region, startTick: Math.round(command.startTick), trackId });
}

/** `copyRegions` of Session Player regions: copies with their parameters and seeds (the same notes). */
export function copySessionPlayerRegions(
  project: Project,
  ids: readonly string[],
  offset: number,
  trackId: string | null | undefined,
  id: (prefix: string) => string
): Project {
  const copies = ids.map(regionId => {
    const region = findRegion(project, regionId);
    const startTick = region.startTick + offset;
    if (startTick < 0) {
      refuse('A region start must not be negative.');
    }
    return { ...structuredClone(region), id: id('spr'), startTick, trackId: trackId ?? region.trackId };
  });
  return { ...project, sessionPlayerRegions: [...project.sessionPlayerRegions, ...copies] };
}

/** `deleteRegions` of Session Player regions. */
export function deleteSessionPlayerRegions(project: Project, ids: readonly string[]): Project {
  ids.forEach(id => findRegion(project, id));
  const gone = new Set(ids);
  return { ...project, sessionPlayerRegions: project.sessionPlayerRegions.filter(r => !gone.has(r.id)) };
}

/** The chord track, Pitch Source and Session Players' commands (docs/12-phase-11.md). */
export function applyPhase11Command(project: Project, command: EditCommand, ctx: Phase11Context): Project {
  switch (command.type) {
    case 'addChords':
      return withChordTrack(
        project,
        addChords(project.chordTrack, command.chords, () => ctx.id('chd'))
      );
    case 'editChords': {
      const byId = new Map(command.chords.map(c => [c.id, c]));
      for (const id of byId.keys()) {
        if (!project.chordTrack.chords.some(c => c.id === id)) {
          refuse(`No chord ${id}.`);
        }
      }
      const edited = project.chordTrack.chords.map(c => {
        const next = byId.get(c.id);
        return next ? checkChord(next) : c;
      });
      return withChordTrack(project, { ...project.chordTrack, chords: sortedChords(edited) });
    }
    case 'deleteChords': {
      const gone = new Set(command.chordIds);
      for (const id of gone) {
        if (!project.chordTrack.chords.some(c => c.id === id)) {
          refuse(`No chord ${id}.`);
        }
      }
      return withChordTrack(project, {
        ...project.chordTrack,
        chords: project.chordTrack.chords.filter(c => !gone.has(c.id))
      });
    }
    case 'setScales':
      return withChordTrack(project, { ...project.chordTrack, scales: checkScales(command.scales) });
    case 'applyChordId':
      return applyChordId(project, command.result, () => ctx.id('chd'));
    case 'setPitchSource':
      return setPitchSource(project, command);
    case 'setRegionKey':
      return setRegionKey(project, command.regionIds, command.key);
    case 'addSessionPlayerTrack':
      return addSessionPlayerTrack(project, command, ctx);
    case 'addSessionPlayerRegion': {
      const { track, player } = playerTrack(project, command.trackId);
      const style = command.parameters
        ? findStyle(ctx.catalog, command.parameters.styleId)
        : trackStyle(project, track, player, ctx.catalog);
      const region = newRegion(project, track, player, command, command.parameters, style, ctx);
      return { ...project, sessionPlayerRegions: [...project.sessionPlayerRegions, region] };
    }
    case 'setSessionPlayerRegion':
      return setSessionPlayerRegion(project, command, ctx);
    case 'regenerateSessionPlayer': {
      let next = project;
      for (const id of command.regionIds) {
        const region = findRegion(next, id);
        next = withRegion(next, { ...region, seed: Math.round(command.seed ?? ctx.seed()) });
      }
      return next;
    }
    case 'convertSessionPlayerRegions':
      return convertRegions(project, command, ctx);
    default:
      return project;
  }
}

// ---- the fake generator (fixed patterns, ADR-0014's rules in miniature)

/** A seeded random number generator (mulberry32), 0 ≤ x < 1. */
function random(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4_294_967_296;
  };
}

/** A sounding chord: its root's pitch class, its tones as pitch classes (root first) and its bass. */
export interface ChordTones {
  root: number;
  tones: number[];
  bass: number;
}

function tonesOf(root: number, quality: string, bass: number | null): ChordTones {
  const intervals = ChordQualities.find(q => q.quality === quality)?.intervals ?? [0, 4, 7];
  return { root, tones: intervals.map(i => (root + i) % 12), bass: bass ?? root };
}

/** The project's key at a tick: the chord track's scale change before it, else the key track. */
export function keyAt(project: Project, tick: number): { tonic: number; minor: boolean } {
  const scale = project.chordTrack.scales.filter(s => s.tick <= tick).at(-1);
  if (scale) {
    return { tonic: pitchClass(scale.tonic), minor: scale.scale !== 'major' && scale.scale !== 'lydian' };
  }
  const key = project.settings.keys.filter(k => k.tick <= tick).at(-1) ?? project.settings.keys[0];
  return { tonic: key?.tonic ?? 0, minor: key?.mode === 'minor' };
}

/** The chord sounding at a tick (each holds to the next); before the first, or not following, the key's tonic chord. */
export function chordAt(project: Project, tick: number, follow = true): ChordTones {
  const chord = follow ? project.chordTrack.chords.filter(c => c.tick <= tick).at(-1) : undefined;
  if (chord) {
    return tonesOf(pitchClass(chord.root), chord.quality, chord.bass ? pitchClass(chord.bass) : null);
  }
  const key = keyAt(project, tick);
  return tonesOf(key.tonic, key.minor ? 'm' : '', null);
}

/** The lowest MIDI note of a pitch class at or above `low`. */
function noteAbove(pc: number, low: number): number {
  return low + ((((pc - low) % 12) + 12) % 12);
}

/** What one generated note is: relative to the region's start. */
type Generated = Omit<NewNote, 'articulationId'>;

function velocity(intensity: number, accent: number, rng: () => number): number {
  return Math.max(1, Math.min(127, Math.round(48 + intensity * 70 + accent + (rng() - 0.5) * 8)));
}

/** Complexity in three levels: simple, middle, busy. */
function level(complexity: number): 0 | 1 | 2 {
  return complexity < 1 / 3 ? 0 : complexity < 2 / 3 ? 1 : 2;
}

/** Where a region's fills go: the last bar of each section that ends inside it, else its own last bar. */
function fillBars(project: Project, region: SessionPlayerRegion, bars: readonly number[]): Set<number> {
  const end = region.startTick + region.lengthTicks;
  const fills = new Set<number>();
  if (!region.parameters.fillAtSectionEnds) {
    return fills;
  }
  const ends = project.arrangement.map(s => s.startTick + s.lengthTicks).filter(t => t > region.startTick && t <= end);
  for (const sectionEnd of ends.length ? ends : [end]) {
    const bar = bars.filter(b => b < sectionEnd).at(-1);
    if (bar !== undefined) {
      fills.add(bar);
    }
  }
  return fills;
}

/** The kick's sixteenths at each level of complexity. */
const KickSteps: readonly [readonly number[], readonly number[], readonly number[]] = [
  [0, 8],
  [0, 8, 10],
  [0, 3, 8, 10, 14]
];

function drumNote(piece: DrumPiece, variation: number): number {
  const notes = DrumKitNotes[piece];
  return notes[Math.min(notes.length - 1, variation)] ?? notes[0] ?? 36;
}

/** The rhythm source's onsets in the region: its MIDI notes' starts, relative to the region. */
function rhythmOnsets(project: Project, region: SessionPlayerRegion, trackId: string): number[] {
  const end = region.startTick + region.lengthTicks;
  return project.midiRegions
    .filter(r => r.trackId === trackId && !r.muted)
    .flatMap(r => r.notes.map(n => r.startTick + n.startTick))
    .filter(t => t >= region.startTick && t < end)
    .map(t => t - region.startTick)
    .sort((a, b) => a - b);
}

function drums(project: Project, region: SessionPlayerRegion, bars: number[], rng: () => number): Generated[] {
  const p = region.parameters;
  const switches = p.drums ?? must(defaultSwitches('drummer').drums);
  const on = new Map(switches.pieces.map(s => [s.piece, s]));
  const meter = new MeterMap(project.settings.signatures);
  const busy = level(p.pad.complexity);
  const fills = fillBars(project, region, bars);
  const electronic = p.styleId.includes('electronic');
  const follow = switches.rhythmSourceTrackId ? rhythmOnsets(project, region, switches.rhythmSourceTrackId) : null;
  const notes: Generated[] = [];
  const hit = (piece: DrumPiece, tick: number, accent: number, note?: number) => {
    const setting = on.get(piece);
    if (setting?.on === false || (piece === 'percussion' && !setting)) {
      return;
    }
    notes.push({
      pitch: note ?? drumNote(piece, setting?.variation ?? 0),
      velocity: velocity(p.pad.intensity, accent, rng),
      channel: 1,
      startTick: tick,
      lengthTicks: Ppq / 8
    });
  };
  for (const [index, bar] of bars.entries()) {
    const barTicks = meter.barTicksAt(region.startTick + bar);
    const step = barTicks / 16;
    const fill = fills.has(bar);
    const fillFrom = fill ? 16 - Math.min(12, 4 + Math.round(p.fillAmount * 8)) : 16;
    const kicks = electronic ? [0, 4, 8, 12] : KickSteps[busy];
    const extra = rng() < 0.5 ? must([2, 6, 11, 13][Math.floor(rng() * 4)]) : -1;
    const kickSteps = follow
      ? follow.filter(t => t >= bar && t < bar + barTicks).map(t => Math.round((t - bar) / step))
      : [...kicks, ...(extra >= 0 && !kicks.includes(extra) ? [extra] : [])];
    for (let s = 0; s < 16; s++) {
      const tick = bar + s * step;
      if (s >= fillFrom) {
        // The fill: snare and toms down the kit, twice per step when the groove is busy.
        const toms = DrumKitNotes.toms;
        for (let r = 0; r < (busy === 2 ? 2 : 1); r++) {
          const n = s - fillFrom;
          const at = tick + (r * step) / 2;
          if ((n + r) % 3 === 0) {
            hit('snare', at, 6);
          } else {
            hit('toms', at, 4, toms[Math.min(toms.length - 1, Math.floor((n / (16 - fillFrom)) * toms.length))]);
          }
        }
        if (s === fillFrom) {
          hit('kick', tick, 8);
        }
        continue;
      }
      if (kickSteps.includes(s)) {
        hit('kick', tick, s === 0 ? 10 : 0);
      }
      if (s === 4 || s === 12) {
        hit('snare', tick, 8);
      } else if (busy === 2 && (s === 7 || s === 15)) {
        // Ghost notes.
        notes.push({ pitch: drumNote('snare', 0), velocity: 28, channel: 1, startTick: tick, lengthTicks: Ppq / 8 });
      }
      if (s % must([4, 2, 1][busy]) === 0) {
        hit('hiHat', tick, s % 4 === 0 ? 6 : -6);
      }
      if ((s === 4 || s === 12) && on.get('percussion')?.on) {
        hit('percussion', tick, -10);
      }
    }
    // A crash on the downbeat after a fill.
    if (index > 0 && fills.has(must(bars[index - 1]))) {
      hit('cymbals', bar, 10);
    }
  }
  return notes;
}

function bass(project: Project, region: SessionPlayerRegion, rng: () => number): Generated[] {
  const p = region.parameters;
  const range = p.bass ?? must(defaultSwitches('bass').bass);
  const meter = new MeterMap(project.settings.signatures);
  const end = region.startTick + region.lengthTicks;
  const every = must([2 * Ppq, Ppq, Ppq / 2][level(p.pad.complexity)]);
  const changes = p.followChords
    ? project.chordTrack.chords.map(c => c.tick).filter(t => t > region.startTick && t < end)
    : [];
  const grid: number[] = [];
  for (let t = region.startTick; t < end; t += every) {
    grid.push(t);
  }
  const ticks = [...new Set([...grid, ...changes])].sort((a, b) => a - b);
  const notes: Generated[] = [];
  let lastChord = -1;
  let inChord = 0;
  for (const [i, tick] of ticks.entries()) {
    const chordTick = project.chordTrack.chords.filter(c => c.tick <= tick).at(-1)?.tick ?? -1;
    inChord = chordTick === lastChord ? inChord + 1 : 0;
    lastChord = chordTick;
    const chord = chordAt(project, tick, p.followChords);
    const fifth = chord.tones[2] ?? chord.tones[1] ?? chord.root;
    // The chord's root (or slash bass) on its downbeat, then root, fifth and octave.
    const pc = inChord === 0 ? chord.bass : must([chord.root, fifth, chord.root, chord.root][inChord % 4]);
    let pitch = noteAbove(pc, range.lowNote);
    if (inChord % 4 === 3 && pitch + 12 <= range.highNote && rng() < 0.7) {
      pitch += 12;
    }
    const next = ticks[i + 1] ?? end;
    notes.push({
      pitch: Math.min(range.highNote, pitch),
      velocity: velocity(p.pad.intensity, meter.isBarLine(tick) ? 8 : 0, rng),
      channel: 1,
      startTick: tick - region.startTick,
      lengthTicks: Math.max(30, Math.round((next - tick) * 0.85))
    });
  }
  return notes;
}

/** A voicing of a chord in a range, as close as can be to the previous one (voice leading). */
function voice(chord: ChordTones, voicing: KeysSwitches['voicing'], low: number, high: number, previous: number[]) {
  const tones =
    voicing === 'shell' ? [chord.root, must(chord.tones[1]), chord.tones[3] ?? must(chord.tones[2])] : chord.tones;
  const centre = previous.length ? previous.reduce((a, b) => a + b, 0) / previous.length : (low + high) / 2;
  let best: number[] = [];
  let cost = Infinity;
  for (let base = low; base <= high - 12; base++) {
    // Each inversion: the tones stacked upwards from `base`.
    const stacked: number[] = [];
    let at = base;
    for (const pc of tones) {
      at = noteAbove(pc, stacked.length ? at + 1 : at);
      stacked.push(at);
    }
    const spread = voicing === 'spread' && stacked.length > 2 ? [must(stacked[0]) - 12, ...stacked.slice(1)] : stacked;
    if (must(spread[0]) < low - 12 || must(spread.at(-1)) > high) {
      continue;
    }
    const mean = spread.reduce((a, b) => a + b, 0) / spread.length;
    const moves = previous.length
      ? spread.reduce((sum, n, i) => sum + Math.abs(n - (previous[i] ?? must(previous.at(-1)))), 0)
      : Math.abs(mean - centre);
    if (moves < cost) {
      cost = moves;
      best = spread;
    }
  }
  return best;
}

function keys(project: Project, region: SessionPlayerRegion, rng: () => number): Generated[] {
  const p = region.parameters;
  const k = p.keys ?? must(defaultSwitches('keyboard').keys);
  const end = region.startTick + region.lengthTicks;
  const rhythm = k.rhythmId ?? must(['sustain', 'sustain', 'pulse'][level(p.pad.complexity)]);
  const every = rhythm === 'sustain' ? must([4 * Ppq, 2 * Ppq, Ppq][level(p.pad.complexity)]) : Ppq / 2;
  const changes = p.followChords
    ? project.chordTrack.chords.map(c => c.tick).filter(t => t > region.startTick && t < end)
    : [];
  const segments = [region.startTick, ...changes, end];
  const notes: Generated[] = [];
  let previous: number[] = [];
  for (let s = 0; s < segments.length - 1; s++) {
    const from = must(segments[s]);
    const to = must(segments[s + 1]);
    const chord = chordAt(project, from, p.followChords);
    const voices = voice(chord, k.voicing, k.lowNote, k.highNote, previous);
    previous = voices;
    for (let t = from, n = 0; t < to; t += every, n++) {
      const length = Math.max(30, Math.round((Math.min(to, t + every) - t) * 0.9));
      const at = t - region.startTick;
      const played = rhythm === 'arpeggio' ? [must(voices[n % voices.length])] : voices;
      for (const pitch of played) {
        notes.push({
          pitch,
          velocity: velocity(p.pad.intensity, 0, rng),
          channel: 1,
          startTick: at,
          lengthTicks: length
        });
      }
      if (k.leftHand !== 'off' && n % 2 === 0) {
        const root = noteAbove(chord.bass, Math.max(24, k.lowNote - 24));
        const left = { off: [], root: [root], octaves: [root, root + 12], rootFifth: [root, root + 7] }[k.leftHand];
        for (const pitch of left) {
          notes.push({
            pitch,
            velocity: velocity(p.pad.intensity, -6, rng),
            channel: 1,
            startTick: at,
            lengthTicks: length
          });
        }
      }
    }
  }
  return notes;
}

/** Swing (odd 16ths later) and feel (push earlier, lay back later, by a 32nd at the extremes). */
function groove(notes: Generated[], region: SessionPlayerRegion): Generated[] {
  const { swing, feel } = region.parameters;
  const sixteenth = Ppq / 4;
  return notes.map(n => {
    const odd = Math.round(n.startTick / sixteenth) % 2 === 1 && n.startTick % sixteenth === 0;
    const shifted = n.startTick + (odd ? Math.round((swing * sixteenth) / 2) : 0) - Math.round(feel * (Ppq / 8));
    return { ...n, startTick: Math.max(0, shifted) };
  });
}

/** What generation depends on (docs/12-phase-11.md, "Generation is deterministic"). */
function generationInput(project: Project, region: SessionPlayerRegion, catalog: SessionPlayerCatalog): string {
  const end = region.startTick + region.lengthTicks;
  const source = region.parameters.drums?.rhythmSourceTrackId;
  return JSON.stringify({
    parameters: region.parameters,
    seed: region.seed,
    length: region.lengthTicks,
    start: region.startTick,
    chords: project.chordTrack.chords.filter(c => c.tick < end).map(c => [c.tick, c.root, c.quality, c.bass]),
    scales: project.chordTrack.scales,
    keys: project.settings.keys,
    signatures: project.settings.signatures,
    sections: project.arrangement.map(s => [s.startTick, s.lengthTicks, sectionKind(s.name)]),
    rhythm: source ? rhythmOnsets(project, region, source) : null,
    version: catalog.version
  });
}

/** The cache key: 64 hex digits from the generation input (the real Server's is a SHA-256). */
export function generationHash(input: string): string {
  return [0, 1, 2, 3, 4, 5, 6, 7].map(i => (hash32(`${i}:${input}`) >>> 0).toString(16).padStart(8, '0')).join('');
}

/**
 * A Session Player region's notes (relative to its start), as the fake generator plays them: fixed patterns per player
 * that grow busier with complexity and louder with intensity, fills at section ends, bass roots on chord changes and
 * voice-led keys within the range, humanised by the region's seed. The same inputs give the same notes.
 */
export function generateSessionPlayerNotes(
  project: Project,
  region: SessionPlayerRegion,
  catalog: SessionPlayerCatalog
): { hash: string; notes: Note[] } {
  const track = findTrack(project, region.trackId);
  const player = playerOf(track.kind) ?? refuse(`${track.name} is not a Session Player track.`);
  const input = generationInput(project, region, catalog);
  const rng = random(hash32(input) ^ region.seed);
  const meter = new MeterMap(project.settings.signatures);
  const end = region.startTick + region.lengthTicks;
  const bars: number[] = [];
  for (let bar = Math.round(meter.barAt(region.startTick)); meter.barStart(bar) < end; bar++) {
    const start = meter.barStart(bar);
    if (start >= region.startTick) {
      bars.push(start - region.startTick);
    }
  }
  const raw =
    player === 'drummer'
      ? drums(project, region, bars, rng)
      : player === 'bass'
        ? bass(project, region, rng)
        : keys(project, region, rng);
  const notes = groove(raw, region)
    .filter(n => n.startTick < region.lengthTicks)
    .sort((a, b) => a.startTick - b.startTick || a.pitch - b.pitch)
    .map((n, i) => ({ ...n, id: i + 1, articulationId: 0 }));
  return { hash: generationHash(input), notes };
}

/**
 * A converted Drummer region's pattern regions (the Server's `PatternRegions`): one when its rows fit a pattern, else
 * consecutive regions of `MaxRowLength` steps (4 bars of 16ths in 4/4; the last what is left), each with its part of
 * every row.
 */
function patternRegions(region: SessionPlayerRegion, pattern: Pattern, ctx: Phase11Context): PatternRegion[] {
  const part = (startTick: number, lengthTicks: number, rows: Pattern['rows']): PatternRegion => ({
    id: ctx.id('pat'),
    trackId: region.trackId,
    name: region.name,
    startTick,
    lengthTicks,
    muted: region.muted,
    pattern: { ...structuredClone(pattern), rows: structuredClone(rows) }
  });
  if (pattern.rows.every(row => row.length <= MaxRowLength)) {
    return [part(region.startTick, region.lengthTicks, pattern.rows)];
  }
  const parts: PatternRegion[] = [];
  const longest = Math.max(...pattern.rows.map(row => row.steps.length));
  const end = region.startTick + region.lengthTicks;
  for (let from = 0; from < longest; from += MaxRowLength) {
    const start = region.startTick + from * pattern.stepTicks;
    if (end <= start) {
      break;
    }
    const rows = pattern.rows.map(row => {
      const steps = row.steps.slice(from, from + MaxRowLength);
      return steps.length > 0
        ? { ...row, length: steps.length, steps }
        : { ...row, length: 1, steps: [{ ...must(row.steps.at(-1)), on: false }] };
    });
    parts.push(part(start, Math.min(MaxRowLength * pattern.stepTicks, end - start), rows));
  }
  return parts;
}

/** A Drummer's notes as a pattern (`SessionPlayerGenerator.ToPattern`): a row per drum note on 16ths, the region long. */
export function notesToPattern(
  notes: readonly Note[],
  lengthTicks: number,
  id: () => string,
  stepTicks = 240
): Pattern {
  const length = Math.max(1, Math.ceil(lengthTicks / stepTicks));
  const pitches = [...new Set(notes.map(n => n.pitch))].sort((a, b) => a - b);
  const rows = pitches.map(pitch => {
    const row = defaultRow(id(), 'pad', pitch, `Note ${pitch}`, length);
    for (const note of notes.filter(n => n.pitch === pitch)) {
      const step = Math.round(note.startTick / stepTicks);
      const cell = row.steps[step];
      if (cell && step < length) {
        row.steps[step] = { ...cell, on: true, velocity: note.velocity };
      }
    }
    return row;
  });
  return { rows, swing: 0, seed: 1, stepTicks };
}

// ---- the fake Chord ID

/** Pitch classes spelled as the chord track writes them (flats for E♭, A♭ and B♭; sharps for C♯ and F♯). */
const Spellings: readonly PitchName[] = [
  { step: 'c', alter: 0 },
  { step: 'c', alter: 1 },
  { step: 'd', alter: 0 },
  { step: 'e', alter: -1 },
  { step: 'e', alter: 0 },
  { step: 'f', alter: 0 },
  { step: 'f', alter: 1 },
  { step: 'g', alter: 0 },
  { step: 'a', alter: -1 },
  { step: 'a', alter: 0 },
  { step: 'b', alter: -1 },
  { step: 'b', alter: 0 }
];

export function spell(pc: number): PitchName {
  return { ...must(Spellings[((pc % 12) + 12) % 12]) };
}

/** A note Chord ID hears, in project ticks. */
interface Heard {
  startTick: number;
  lengthTicks: number;
  pitch: number;
  velocity: number;
}

/** The best template for a segment's pitch-class weights: chord tones count, others cost; the bass helps its root. */
function bestChord(weights: readonly number[], bass: number): { root: number; quality: string; score: number } {
  const total = weights.reduce((a, b) => a + b, 0) || 1;
  let best = { root: 0, quality: '', score: -Infinity };
  for (let root = 0; root < 12; root++) {
    for (const { quality, intervals } of ChordQualities) {
      const tones = new Set(intervals.map(i => (root + i) % 12));
      let score = 0;
      for (let pc = 0; pc < 12; pc++) {
        score += tones.has(pc) ? must(weights[pc]) : -1.2 * must(weights[pc]);
      }
      // Missing tones cost a little; the bass on the root, or a smaller chord, wins ties.
      score -= [...tones].filter(pc => weights[pc] === 0).length * 0.3 * (total / 4);
      score += bass === root ? total * 0.05 : 0;
      score -= intervals.length * 0.001;
      if (score > best.score) {
        best = { root, quality, score };
      }
    }
  }
  return { ...best, score: best.score / total };
}

const MajorProfile = [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88];
const MinorProfile = [6.33, 2.68, 3.52, 5.38, 2.6, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17];

/** Krumhansl–Schmuckler: the key whose profile best correlates with the pitch-class weights. */
export function detectKey(weights: readonly number[]): { key: RegionKey; confidence: number } {
  const correlate = (profile: readonly number[], tonic: number) => {
    const xs = weights;
    const ys = Array.from({ length: 12 }, (_, pc) => must(profile[(pc - tonic + 12) % 12]));
    const mx = xs.reduce((a, b) => a + b, 0) / 12;
    const my = ys.reduce((a, b) => a + b, 0) / 12;
    let num = 0;
    let dx = 0;
    let dy = 0;
    for (let i = 0; i < 12; i++) {
      num += (must(xs[i]) - mx) * (must(ys[i]) - my);
      dx += (must(xs[i]) - mx) ** 2;
      dy += (must(ys[i]) - my) ** 2;
    }
    return dx && dy ? num / Math.sqrt(dx * dy) : 0;
  };
  let best = { tonic: 0, minor: false, r: -Infinity };
  for (let tonic = 0; tonic < 12; tonic++) {
    for (const minor of [false, true]) {
      const r = correlate(minor ? MinorProfile : MajorProfile, tonic);
      if (r > best.r) {
        best = { tonic, minor, r };
      }
    }
  }
  return {
    key: { tonic: spell(best.tonic), mode: best.minor ? 'minor' : 'major' },
    confidence: Math.max(0, Math.min(1, best.r))
  };
}

function histogram(notes: readonly Heard[]): number[] {
  const weights = new Array<number>(12).fill(0);
  for (const n of notes) {
    weights[n.pitch % 12] = must(weights[n.pitch % 12]) + n.lengthTicks * n.velocity;
  }
  return weights;
}

/**
 * What an audio region holds, to the fake: the MIDI notes that play under it on other tracks (a bounce of them), else
 * a fixed C, Am, F, G progression a bar each.
 */
function audioNotes(project: Project, region: AudioRegion, span: { start: number; end: number }): Heard[] {
  const under = project.midiRegions
    .filter(r => !r.muted && r.startTick < span.end && r.startTick + r.lengthTicks > span.start)
    .flatMap(r => r.notes.map(n => ({ ...n, startTick: r.startTick + n.startTick })))
    .filter(n => n.startTick < span.end && n.startTick + n.lengthTicks > span.start);
  if (under.length) {
    return under;
  }
  const meter = new MeterMap(project.settings.signatures);
  const progression = [
    [48, 60, 64, 67],
    [45, 57, 60, 64],
    [41, 57, 60, 65],
    [43, 55, 59, 62]
  ];
  const heard: Heard[] = [];
  for (let bar = Math.round(meter.barAt(span.start)), i = 0; meter.barStart(bar) < span.end; bar++, i++) {
    const start = Math.max(region.startTick, meter.barStart(bar));
    const length = meter.barStart(bar + 1) - start;
    for (const pitch of must(progression[i % 4])) {
      heard.push({ startTick: start, lengthTicks: length, pitch, velocity: 90 });
    }
  }
  return heard;
}

/**
 * The fake Chord ID (ADR-0014 in miniature): per resolution step (a beat by default) the notes' pitch classes,
 * weighted by duration and velocity, matched against the chord templates (the lowest note as a slash bass), held
 * through steps that keep it nearly as well (a change costs), joined; and each region's key by its notes.
 */
export function fakeChordId(project: Project, request: ChordIdRequest): ChordIdResult {
  if (!request.regionIds.length) {
    refuse('Chord ID needs regions.');
  }
  const notes: Heard[] = [];
  const keys: DetectedKey[] = [];
  let from = Infinity;
  let to = -Infinity;
  for (const id of request.regionIds) {
    const midi = project.midiRegions.find(r => r.id === id);
    const audio = project.regions.find(r => r.id === id);
    if (!midi && !audio) {
      refuse(`No MIDI or audio region ${id}.`);
    }
    const start = midi?.startTick ?? must(audio).startTick;
    const length = midi?.lengthTicks ?? lengthOfAudio(project, must(audio));
    from = Math.min(from, start);
    to = Math.max(to, start + length);
    const heard = midi
      ? midi.notes.map(n => ({ ...n, startTick: midi.startTick + n.startTick }))
      : audioNotes(project, must(audio), { start, end: start + length });
    notes.push(...heard);
    if (request.detectKeys) {
      const detected = detectKey(histogram(heard));
      keys.push({ regionId: id, key: detected.key, confidence: detected.confidence });
    }
  }
  const startTick = Math.round(request.startTick ?? from);
  const endTick = Math.round(request.endTick ?? to);
  if (!(endTick > startTick)) {
    refuse('Chord ID needs a range.');
  }
  const meter = new MeterMap(project.settings.signatures);
  const chords: DetectedChord[] = [];
  let held = null as { root: number; quality: string; bass: number } | null;
  for (let tick = startTick; tick < endTick;) {
    const step = Math.round(request.resolutionTicks ?? meter.beatTicksAt(tick));
    const end = Math.min(endTick, tick + step);
    const weights = new Array<number>(12).fill(0);
    let lowest = Infinity;
    for (const n of notes) {
      const overlap = Math.min(end, n.startTick + n.lengthTicks) - Math.max(tick, n.startTick);
      if (overlap > 0) {
        weights[n.pitch % 12] = must(weights[n.pitch % 12]) + overlap * n.velocity;
        lowest = Math.min(lowest, n.pitch);
      }
    }
    const last = chords.at(-1);
    if (weights.some(w => w > 0)) {
      const bass = lowest % 12;
      const best = bestChord(weights, bass);
      const keep = held ? scoreOf(weights, held.root, held.quality, bass) : -Infinity;
      if (last && held?.bass === bass && keep >= best.score - 0.15) {
        last.lengthTicks = end - last.tick;
      } else {
        held = { root: best.root, quality: best.quality, bass };
        chords.push({
          tick,
          lengthTicks: end - tick,
          root: spell(best.root),
          quality: best.quality,
          bass: bass !== best.root ? spell(bass) : null,
          confidence: Math.max(0, Math.min(1, Math.round(best.score * 100) / 100))
        });
      }
    } else if (last) {
      last.lengthTicks = end - last.tick;
    }
    tick = end;
  }
  return { startTick, endTick, write: request.write, chords, keys };
}

function scoreOf(weights: readonly number[], root: number, quality: string, bass: number): number {
  const total = weights.reduce((a, b) => a + b, 0) || 1;
  const tones = new Set((ChordQualities.find(q => q.quality === quality)?.intervals ?? []).map(i => (root + i) % 12));
  let score = 0;
  for (let pc = 0; pc < 12; pc++) {
    score += tones.has(pc) ? must(weights[pc]) : -1.2 * must(weights[pc]);
  }
  score -= [...tones].filter(pc => weights[pc] === 0).length * 0.3 * (total / 4);
  score += bass === root ? total * 0.05 : 0;
  return score / total;
}

/** An audio region's length in ticks at the project's tempo (the fake's regions do not warp for Chord ID). */
function lengthOfAudio(project: Project, region: AudioRegion): number {
  const seconds = region.length / project.sampleRate;
  return Math.round((seconds * project.settings.tempo * Ppq) / 60);
}
