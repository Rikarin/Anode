import {
  ApiError,
  type AudioFile,
  type AudioRegion,
  type AutomationLane,
  type ChannelFormat,
  type ChannelStrip,
  type DiscoveryInfo,
  type EditCommand,
  type ImportedContent,
  type ImportedTrack,
  ListenIn,
  type ListenInFormat,
  type Marker,
  type MonitoringSettings,
  type Movie,
  type Panner,
  type PannerMode,
  type Project,
  type RemotePads,
  SpeakerLayouts,
  type Track
} from '@anode/api-client';
import { movieSpan, tickAtFrame } from '../../video/movie-time';
import { tempoOf } from './fake-arrange';
import { type MixContext, record } from './fake-mix';
import { baseName } from './fake-phase9';

/** A command of one type. */
type Command<T extends EditCommand['type']> = Extract<EditCommand, { type: T }>;

function refuse(message: string): never {
  throw new ApiError(422, message);
}

// ---- formats and panners

/** Whether a format is wider than stereo (`SpeakerLayouts.IsSurround`). */
export function isSurround(format: ChannelFormat): boolean {
  return SpeakerLayouts[format].speakers.length > 2;
}

/** The most objects a project has (`ObjectRenderer.MaxObjects`, the ADM profile for 7.1.4 beds). */
export const MaxObjects = 118;

const Formats = new Set(Object.keys(SpeakerLayouts) as ChannelFormat[]);

function checkFormat(format: unknown): ChannelFormat {
  return typeof format === 'string' && Formats.has(format as ChannelFormat)
    ? (format as ChannelFormat)
    : refuse(`${String(format)} is not a channel format.`);
}

/** The panner parameters' ranges, by automation address (`PannerTargets`): angles −180…180, levels −144…0 dB. */
export const PannerRanges: Readonly<Record<string, { min: number; max: number }>> = {
  'surround:angle': { min: -180, max: 180 },
  'surround:diversity': { min: 0, max: 1 },
  'surround:elevation': { min: 0, max: 90 },
  'surround:centre': { min: -144, max: 0 },
  'surround:lfe': { min: -144, max: 0 },
  'surround:spread': { min: 0, max: 180 },
  'object:azimuth': { min: -180, max: 180 },
  'object:elevation': { min: -90, max: 90 },
  'object:distance': { min: 0, max: 1 },
  'object:size': { min: 0, max: 1 }
};

/** The flat `setPanner` fields, each with its panner, property and automation address. */
const PannerFields = [
  { field: 'angle', panner: 'surround', property: 'angle', target: 'surround:angle' },
  { field: 'diversity', panner: 'surround', property: 'diversity', target: 'surround:diversity' },
  { field: 'surroundElevation', panner: 'surround', property: 'elevation', target: 'surround:elevation' },
  { field: 'centreLevelDb', panner: 'surround', property: 'centreLevelDb', target: 'surround:centre' },
  { field: 'lfeLevelDb', panner: 'surround', property: 'lfeLevelDb', target: 'surround:lfe' },
  { field: 'spread', panner: 'surround', property: 'spread', target: 'surround:spread' },
  { field: 'azimuth', panner: 'object', property: 'azimuth', target: 'object:azimuth' },
  { field: 'objectElevation', panner: 'object', property: 'elevation', target: 'object:elevation' },
  { field: 'distance', panner: 'object', property: 'distance', target: 'object:distance' },
  { field: 'size', panner: 'object', property: 'size', target: 'object:size' }
] as const;

function inRange(value: number, target: string, label: string): number {
  const range = PannerRanges[target] ?? { min: -Infinity, max: Infinity };
  if (!Number.isFinite(value) || value < range.min || value > range.max) {
    refuse(`${label} is ${range.min} to ${range.max}.`);
  }
  return value;
}

/** The format of what a strip's output feeds: the master's (the project's), a bus's (its aux's input), a device pair. */
export function destinationFormat(project: Project, output: string): ChannelFormat {
  if (output === 'master') {
    return project.settings.surroundFormat;
  }
  const bus = /^bus:(\d+)$/.exec(output);
  if (bus) {
    const aux = project.tracks.find(t => t.kind === 'aux' && t.inputBus === Number(bus[1]));
    return aux?.strip.inputFormat ?? 'stereo';
  }
  return 'stereo';
}

/** The panner mode a strip can have with its output: object needs the master of a surround project. */
function validMode(mode: PannerMode, output: string, outputFormat: ChannelFormat, project: Project): PannerMode {
  if (mode === 'object') {
    return output === 'master' && isSurround(project.settings.surroundFormat)
      ? 'object'
      : isSurround(outputFormat)
        ? 'surround'
        : 'stereo';
  }
  // The Surround Panner for a surround output, stereo pan for a stereo one.
  return isSurround(outputFormat) ? 'surround' : 'stereo';
}

/**
 * Keeps every strip's output format equal to its destination's (docs/11-phase-10.md, "Strips": stored, kept by the
 * Server), the master's input and output the project's format, and every panner's mode one its output allows (a
 * stereo output pans in stereo; a surround output uses the Surround Panner unless the strip is an object).
 */
export function syncFormats(project: Project): Project {
  const format = project.settings.surroundFormat;
  let changed = false;
  const master: ChannelStrip =
    project.master.inputFormat === format && project.master.outputFormat === format
      ? project.master
      : ((changed = true), { ...project.master, inputFormat: format, outputFormat: format });
  const tracks = project.tracks.map(track => {
    if (track.kind === 'vca') {
      return track;
    }
    const outputFormat = destinationFormat(project, track.strip.output);
    const mode = validMode(track.strip.panner.mode, track.strip.output, outputFormat, project);
    if (outputFormat === track.strip.outputFormat && mode === track.strip.panner.mode) {
      return track;
    }
    changed = true;
    return {
      ...track,
      strip: { ...track.strip, outputFormat, panner: { ...track.strip.panner, mode } }
    };
  });
  return changed ? { ...project, master, tracks } : project;
}

/** A strip's input formats by track kind: instruments, MIDI tracks and stacks stay stereo (panned by the strip). */
function checkInputFormat(track: Track, format: ChannelFormat): void {
  if (track.kind === 'vca') {
    refuse(`${track.name} is a VCA: it has no signal.`);
  }
  if (format !== 'stereo' && track.kind !== 'audio' && track.kind !== 'aux') {
    refuse(`${track.name} plays in stereo: only audio tracks and auxes take other input formats.`);
  }
}

function objectCount(project: Project): number {
  return project.tracks.filter(t => t.strip.panner.mode === 'object').length;
}

// ---- the movie

function checkMovie(movie: Movie): Movie {
  if (!movie.path || !movie.name) {
    refuse('A movie needs a path and a name.');
  }
  if (!(movie.frameRate.numerator > 0 && movie.frameRate.denominator > 0)) {
    refuse('A movie needs a frame rate.');
  }
  if (!(movie.durationSeconds > 0) || !(movie.frames > 0) || !(movie.width > 0) || !(movie.height > 0)) {
    refuse('A movie needs a duration, frames and a size.');
  }
  if (movie.startTick < 0 || movie.offsetSeconds < 0 || movie.offsetSeconds >= movie.durationSeconds) {
    refuse('A movie starts at a project position of 0 or more, with an offset inside it.');
  }
  return structuredClone(movie);
}

/** The Phase 10 commands (docs/11-phase-10.md, "Commands"). */
const Phase10Commands: ReadonlySet<EditCommand['type']> = new Set([
  'setSurroundFormat',
  'setStripFormat',
  'setPanner',
  'setMovie',
  'setMoviePosition',
  'removeMovie',
  'addSceneMarkers'
]);

export function isPhase10Command(command: EditCommand): boolean {
  return Phase10Commands.has(command.type);
}

/** What the Phase 10 commands need from the fake Server. */
export interface Phase10Context {
  id(prefix: string): string;
  /** Automation recording (as `setStrip` records pan). */
  mix: MixContext;
}

function findTrack(project: Project, id: string): Track {
  return project.tracks.find(t => t.id === id) ?? refuse(`No track ${id}.`);
}

function withTrack(project: Project, track: Track): Project {
  return { ...project, tracks: project.tracks.map(t => (t.id === track.id ? track : t)) };
}

export function applyPhase10Command(project: Project, command: EditCommand, ctx: Phase10Context): Project {
  switch (command.type) {
    case 'setSurroundFormat': {
      const format = checkFormat(command.format);
      return syncFormats({ ...project, settings: { ...project.settings, surroundFormat: format } });
    }
    case 'setStripFormat':
      return setStripFormat(project, command);
    case 'setPanner':
      return setPanner(project, command, ctx);
    case 'setMovie':
      return { ...project, movie: checkMovie(command.movie) };
    case 'setMoviePosition': {
      const movie = project.movie ?? refuse('The project has no movie.');
      const next = {
        ...movie,
        startTick: Math.round(command.startTick ?? movie.startTick),
        offsetSeconds: command.offsetSeconds ?? movie.offsetSeconds
      };
      return { ...project, movie: checkMovie(next) };
    }
    case 'removeMovie':
      if (!project.movie) {
        refuse('The project has no movie.');
      }
      return { ...project, movie: null };
    case 'addSceneMarkers':
      return addSceneMarkers(project, command, ctx);
    default:
      return project;
  }
}

function setStripFormat(project: Project, command: Command<'setStripFormat'>): Project {
  const track = findTrack(project, command.trackId);
  const format = checkFormat(command.inputFormat);
  checkInputFormat(track, format);
  let next = withTrack(project, { ...track, strip: { ...track.strip, inputFormat: format } });
  if (track.kind === 'aux') {
    // Every aux on a bus has the bus's format.
    next = {
      ...next,
      tracks: next.tracks.map(t =>
        t.kind === 'aux' && t.inputBus === track.inputBus ? { ...t, strip: { ...t.strip, inputFormat: format } } : t
      )
    };
  }
  return syncFormats(next);
}

function setPanner(project: Project, command: Command<'setPanner'>, ctx: Phase10Context): Project {
  const track = findTrack(project, command.trackId);
  if (track.kind === 'vca') {
    refuse(`${track.name} is a VCA: it has no panner.`);
  }
  const strip = track.strip;
  let panner: Panner = structuredClone(strip.panner);
  if (command.mode != null) {
    if (command.mode === 'surround' && !isSurround(strip.outputFormat)) {
      refuse(
        `${track.name} outputs in ${SpeakerLayouts[strip.outputFormat].name}: the Surround Panner needs surround.`
      );
    }
    if (command.mode === 'object') {
      if (strip.output !== 'master' || !isSurround(project.settings.surroundFormat)) {
        refuse('An object goes to the master of a surround project.');
      }
      if (strip.panner.mode !== 'object' && objectCount(project) >= MaxObjects) {
        refuse(`A project has at most ${MaxObjects} objects.`);
      }
    }
    if (command.mode === 'stereo' && isSurround(strip.outputFormat)) {
      refuse(`${track.name} outputs in surround: its panner is the Surround Panner or an object.`);
    }
    panner = { ...panner, mode: command.mode };
  }
  const recorded: { target: string; value: number }[] = [];
  for (const { field, panner: which, property, target } of PannerFields) {
    const value = command[field];
    if (value === null || value === undefined) {
      continue;
    }
    const checked = inRange(value, target, field);
    panner = { ...panner, [which]: { ...panner[which], [property]: checked } };
    recorded.push({ target, value: checked });
  }
  let next = withTrack(project, { ...track, strip: { ...strip, panner } });
  for (const { target, value } of recorded) {
    next = record(next, track.id, target, value, ctx.mix);
  }
  return next;
}

function addSceneMarkers(project: Project, command: Command<'addSceneMarkers'>, ctx: Phase10Context): Project {
  const ticks = [...new Set(command.ticks.map(t => Math.round(t)))].sort((a, b) => a - b);
  if (ticks.some(t => !Number.isFinite(t) || t < 0)) {
    refuse('Scene markers are at ticks of 0 or more.');
  }
  const replace = command.replaceExisting;
  const kept = replace ? project.markers.filter(m => m.kind !== 'scene') : project.markers;
  const scenes: Marker[] = ticks.map(tick => ({
    id: ctx.id('mrk'),
    name: '',
    tick,
    lengthTicks: 0,
    color: null,
    text: '',
    kind: 'scene'
  }));
  const markers = [...kept, ...scenes].sort((a, b) => a.tick - b.tick);
  // Scene markers are named Scene 1, … in time order (the new ones; kept ones keep their names).
  let n = 0;
  const named = markers.map(m => {
    if (m.kind !== 'scene') {
      return m;
    }
    n++;
    return scenes.includes(m) ? { ...m, name: `Scene ${n}` } : m;
  });
  return { ...project, markers: named };
}

// ---- importTracks' Phase 10 fields (an ADM import's beds and objects, a movie's audio)

/**
 * Applies an imported track's input format, panner and automation to the track `importTracks` made for it (`trackId`):
 * the format and panner as given (then kept valid by {@link syncFormats}), the lanes with real ids on the new track.
 */
export function applyImportedTrack(
  project: Project,
  imported: ImportedTrack,
  trackId: string,
  ctx: Pick<Phase10Context, 'id'>
): Project {
  let next = project;
  const track = findTrack(next, trackId);
  if (imported.inputFormat != null) {
    const format = checkFormat(imported.inputFormat);
    checkInputFormat(track, format);
    next = withTrack(next, { ...track, strip: { ...track.strip, inputFormat: format } });
  }
  if (imported.panner != null) {
    const current = findTrack(next, trackId);
    const panner = structuredClone(imported.panner);
    if (panner.mode === 'object' && objectCount(next) >= MaxObjects) {
      refuse(`A project has at most ${MaxObjects} objects.`);
    }
    next = withTrack(next, { ...current, strip: { ...current.strip, panner } });
  }
  next = syncFormats(next);
  for (const lane of imported.automation ?? []) {
    const range = PannerRanges[lane.target];
    if (!range && lane.target !== 'volume' && lane.target !== 'pan' && lane.target !== 'mute') {
      refuse(`${lane.target} is not an automation target an import brings.`);
    }
    const added: AutomationLane = { ...structuredClone(lane), id: ctx.id('aut'), trackId };
    next = { ...next, automation: [...next.automation, added] };
  }
  return next;
}

// ---- jobs' content: the fake FFprobe, FFmpeg and ADM reader

const MovieExtensions = /\.(mov|mp4|m4v)$/i;

/** What the fake FFprobe reads from a movie: 25 fps, a minute, 1280 × 720 H.264 with stereo AAC (a ProRes name: MOV). */
export function probeMovie(path: string, copyIntoProject: boolean, startTick: number): Movie {
  if (!MovieExtensions.test(path)) {
    refuse(`${path} is not a movie Anode imports (MOV, MP4 or M4V).`);
  }
  const file = path.split(/[\\/]/).pop() ?? path;
  const prores = /prores/i.test(file);
  return {
    path: copyIntoProject ? `Movies/${file}` : path,
    name: baseName(file),
    inProject: copyIntoProject,
    startTick: Math.max(0, Math.round(startTick)),
    offsetSeconds: 0,
    frameRate: { numerator: 25, denominator: 1 },
    timecodeStartSeconds: 3600,
    durationSeconds: 60,
    frames: 1500,
    width: 1280,
    height: 720,
    codec: prores ? 'prores' : 'h264',
    audio: { codec: prores ? 'pcm_s24le' : 'aac', channels: 2, sampleRate: 48_000 }
  };
}

/** The fake movie's scene cuts, in movie frames: every 4 s, then 2 s in (as the golden path's test movie has). */
export function sceneCutFrames(movie: Movie, threshold: number): number[] {
  if (!(threshold > 0 && threshold < 1)) {
    refuse('The scene threshold is between 0 and 1.');
  }
  const fps = movie.frameRate.numerator / movie.frameRate.denominator;
  const cuts: number[] = [];
  // A lower threshold finds softer cuts too: every 2 s instead of 4.
  const every = threshold < 0.25 ? 2 : 4;
  for (let s = every; s < movie.durationSeconds; s += every) {
    cuts.push(Math.round(s * fps));
  }
  return cuts;
}

/** The scene cuts as project ticks (only those the project shows: at or after the project start). */
export function sceneTicks(project: Project, frames: readonly number[]): number[] {
  const movie = project.movie ?? refuse('The project has no movie.');
  const tempo = tempoOf(project);
  return frames.map(f => Math.round(tickAtFrame(movie, tempo, f))).filter(t => t >= 0);
}

/** The thumbnails' width at `height` (the movie's aspect ratio, even). */
export function thumbnailWidth(movie: Movie, height: number): number {
  return Math.max(2, Math.round((height * movie.width) / movie.height / 2) * 2);
}

/**
 * A movie frame as the fake Server's JPEG would show it: an SVG data URL with the frame number and timecode, on a
 * colour that changes at each scene cut, so the movie window and track show something that follows the transport.
 */
export function frameImage(movie: Movie, frame: number, height: number): string {
  const width = thumbnailWidth(movie, height);
  const fps = movie.frameRate.numerator / movie.frameRate.denominator;
  const scene = Math.floor(frame / (4 * fps));
  const hue = (scene * 67) % 360;
  const seconds = frame / fps + movie.timecodeStartSeconds;
  const pad = (n: number) => String(Math.floor(n)).padStart(2, '0');
  const tc = `${pad(seconds / 3600)}:${pad((seconds / 60) % 60)}:${pad(seconds % 60)}:${pad(frame % Math.round(fps))}`;
  const big = Math.round(height * 0.32);
  const small = Math.round(height * 0.14);
  const svg =
    `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}">` +
    `<rect width="100%" height="100%" fill="hsl(${hue} 45% 32%)"/>` +
    `<text x="50%" y="48%" fill="#fff" font-family="ui-monospace,monospace" font-size="${big}" text-anchor="middle">${frame}</text>` +
    `<text x="50%" y="80%" fill="#fff" font-family="ui-monospace,monospace" font-size="${small}" text-anchor="middle">${tc}</text>` +
    `</svg>`;
  return `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`;
}

function wavFile(id: string, name: string, sampleRate: number, channels: number, seconds: number): AudioFile {
  return {
    id,
    name,
    path: `Audio Files/${name}`,
    sampleRate,
    channels,
    bitDepth: 24,
    frames: Math.round(seconds * sampleRate),
    tempo: null,
    version: 0
  };
}

function wholeRegion(id: string, trackId: string, file: AudioFile, startTick: number): AudioRegion {
  return {
    id,
    trackId,
    fileId: file.id,
    name: baseName(file.name),
    startTick,
    fileOffset: 0,
    length: file.frames,
    gainDb: 0,
    fadeIn: 0,
    fadeOut: 0,
    fadeInCurve: 0,
    fadeOutCurve: 0,
    muted: false,
    follow: 'off',
    flexMarkers: [],
    transpose: 0,
    fineTune: 0,
    pitchNotes: []
  };
}

/**
 * Import Audio from Movie: the movie's audio extracted at the project's rate (the fake writes nothing) and, through
 * `importTracks`, a track with a region at the movie's first frame. Returns the file to add and the content.
 */
export function movieAudioContent(
  project: Project,
  trackName: string | null | undefined,
  fileId: string
): { file: AudioFile; content: ImportedContent } {
  const movie = project.movie ?? refuse('The project has no movie.');
  if (!movie.audio) {
    refuse(`${movie.name} has no audio.`);
  }
  const file = wavFile(fileId, `${movie.name}.wav`, project.sampleRate, movie.audio.channels, movie.durationSeconds);
  const start = Math.max(0, Math.round(movieSpan(movie, tempoOf(project)).startTick));
  return {
    file,
    content: {
      tracks: [
        {
          id: 't1',
          name: trackName?.trim() || movie.name,
          kind: 'audio',
          midiRegions: [],
          regions: [wholeRegion('r1', 't1', file, start)],
          inputFormat: movie.audio.channels === 1 ? 'mono' : 'stereo',
          panner: null
        }
      ],
      score: null
    }
  };
}

/**
 * An ADM BWF as the fake reader sees it: a 7.1.4 bed and two objects, one moving from front left to rear right
 * overhead (an `audioBlockFormat` per beat as an `object:` lane). Returns the files and the content.
 */
export function admContent(project: Project, path: string, id: (prefix: string) => string) {
  if (!/\.(wav|bwf)$/i.test(path)) {
    refuse(`${path} is not an ADM BWF (.wav).`);
  }
  const name = baseName(path);
  const rate = project.sampleRate;
  const bed = wavFile(id('aud'), `${name} Bed.wav`, rate, 12, 8);
  const objects = ['Object 1', 'Object 2'].map(o => wavFile(id('aud'), `${name} ${o}.wav`, rate, 1, 8));
  const surround = { angle: 0, diversity: 1, elevation: 0, centreLevelDb: 0, lfeLevelDb: -144, spread: 60 };
  const point = (tick: number, value: number) => ({ tick, value, curve: 0 });
  const beats = 16;
  const lanes = (trackId: string): AutomationLane[] => [
    {
      id: `l-${trackId}-az`,
      trackId,
      target: 'object:azimuth',
      points: Array.from({ length: beats + 1 }, (_, i) => point(i * 960, 30 - (165 * i) / beats))
    },
    {
      id: `l-${trackId}-el`,
      trackId,
      target: 'object:elevation',
      points: Array.from({ length: beats + 1 }, (_, i) => point(i * 960, (30 * i) / beats))
    }
  ];
  const content: ImportedContent = {
    tracks: [
      {
        id: 'bed',
        name: `${name} Bed`,
        kind: 'audio',
        midiRegions: [],
        regions: [wholeRegion('r-bed', 'bed', bed, 0)],
        inputFormat: 'surround714',
        panner: { mode: 'surround', surround, object: { azimuth: 0, elevation: 0, distance: 1, size: 0 } }
      },
      ...objects.map((file, i) => ({
        id: `obj${i}`,
        name: `${name} Object ${i + 1}`,
        kind: 'audio' as const,
        midiRegions: [],
        regions: [wholeRegion(`r-obj${i}`, `obj${i}`, file, 0)],
        inputFormat: 'mono' as const,
        panner: {
          mode: 'object' as const,
          surround,
          object: { azimuth: i === 0 ? 30 : -110, elevation: 0, distance: 1, size: i === 0 ? 0 : 0.2 }
        },
        automation: i === 0 ? lanes(`obj${i}`) : null
      }))
    ],
    score: null
  };
  return { files: [bed, ...objects], content, format: 'surround714' as ChannelFormat };
}

// ---- monitoring, discovery, pads

/** Checks monitoring settings (`PUT /settings/monitoring`): a known down-mix format, outputs 0 (not sent) to 64. */
export function checkMonitoring(settings: MonitoringSettings): MonitoringSettings {
  if (settings.downmixTo != null) {
    checkFormat(settings.downmixTo);
  }
  if (settings.speakerMap.length > 64 || settings.speakerMap.some(o => !Number.isInteger(o) || o < 0 || o > 64)) {
    refuse('The speaker map gives each channel a device output from 1 to 64 (0: not sent).');
  }
  return { binaural: settings.binaural, downmixTo: settings.downmixTo ?? null, speakerMap: [...settings.speakerMap] };
}

/** The remote's pad sets out of the box (`RemotePads`, user settings): Logic Remote's transport and editing pads. */
export const DefaultRemotePads: RemotePads = {
  sets: [
    {
      id: 'transport',
      name: 'Transport',
      pads: [
        { id: 'p1', label: 'Play', commandId: 'transport.play', color: '#3fae4a' },
        { id: 'p2', label: 'Record', commandId: 'transport.record', color: '#e5483a' },
        { id: 'p3', label: 'Cycle', commandId: 'transport.cycle', color: '#e6b422' },
        { id: 'p4', label: 'Go to Start', commandId: 'transport.goToStart' },
        { id: 'p5', label: 'Metronome', commandId: 'transport.metronome' },
        { id: 'p6', label: 'Capture Recording', commandId: 'transport.flashback' },
        { id: 'p7', label: 'Undo', commandId: 'edit.undo' },
        { id: 'p8', label: 'Redo', commandId: 'edit.redo' },
        { id: 'p9', label: 'Save', commandId: 'project.save' }
      ]
    },
    {
      id: 'editing',
      name: 'Editing',
      pads: [
        { id: 'e1', label: 'Create Marker', commandId: 'track.marker', color: '#3b82c4' },
        { id: 'e2', label: 'Split at Playhead', commandId: 'region.split' },
        { id: 'e3', label: 'Join', commandId: 'region.join' },
        { id: 'e4', label: 'Mute Regions', commandId: 'region.mute' },
        { id: 'e5', label: 'Automation Read', commandId: 'automation.read' },
        { id: 'e6', label: 'Automation Touch', commandId: 'automation.touch' },
        { id: 'e7', label: 'Automation Latch', commandId: 'automation.latch' },
        { id: 'e8', label: 'Live Loops', commandId: 'view.liveLoops' }
      ]
    }
  ]
};

/** Checks pad sets (`PUT /remote/pads`): unique set ids, each pad with a label and a command, at most 64 pads a set. */
export function checkPads(pads: RemotePads): RemotePads {
  const ids = new Set<string>();
  for (const set of pads.sets) {
    if (!set.id || ids.has(set.id) || !set.name.trim()) {
      refuse('Each pad set has its own id and a name.');
    }
    ids.add(set.id);
    if (set.pads.length > 64) {
      refuse(`${set.name} has more than 64 pads.`);
    }
    const padIds = new Set<string>();
    for (const pad of set.pads) {
      if (!pad.id || padIds.has(pad.id) || !pad.label.trim() || !pad.commandId) {
        refuse(`Each pad of ${set.name} has its own id, a label and a key command.`);
      }
      padIds.add(pad.id);
      if (pad.color != null && !/^#[0-9a-f]{6}$/i.test(pad.color)) {
        refuse(`${pad.color} is not a colour (#rrggbb).`);
      }
    }
  }
  return structuredClone(pads);
}

/** This fake Server as remotes find it (the `_anode._tcp` advertisement's TXT). */
export function fakeDiscovery(project: Project | null): DiscoveryInfo {
  return {
    name: 'Anode (in-memory)',
    host: 'localhost',
    port: 5190,
    projectName: project?.name ?? null,
    pairingRequired: false,
    version: '0.10.0-fake'
  };
}

// ---- listen-in: a fake stream

/**
 * The browser-side socket the listen-in client reads, as much of a `WebSocket` as it uses: the fake Server's stream
 * implements it, a real `WebSocket` is one.
 */
export interface ListenInSocket {
  binaryType: BinaryType;
  onopen: ((event: Event) => void) | null;
  onmessage: ((event: MessageEvent<string | ArrayBuffer>) => void) | null;
  onclose: ((event: CloseEvent) => void) | null;
  onerror: ((event: Event) => void) | null;
  close(): void;
}

/**
 * The fake listen-in stream's packets are not Opus: each is the frame as 16-bit little-endian interleaved PCM (960
 * stereo samples, 3840 bytes), which the client decodes with its PCM test decoder (`codec: 'pcm16'` beside the
 * format). The header is the real one (`ListenIn`).
 */
export const FakeListenInCodec = 'pcm16';

/** The tone the fake master plays while the transport runs: 1 kHz at −12 dBFS (the golden path's test tone's frequency). */
export const FakeToneHz = 1000;
export const FakeToneLevel = 10 ** (-12 / 20);

/**
 * Encodes one fake listen-in frame: the 16-byte header (`ListenIn.WriteHeader`'s layout) and the PCM packet of
 * `samples` (interleaved stereo floats, 960 frames).
 */
export function encodeFakeFrame(
  sequence: number,
  transportSample: number,
  flags: number,
  samples: Float32Array
): ArrayBuffer {
  const length = samples.length * 2;
  const buffer = new ArrayBuffer(ListenIn.headerBytes + length);
  const view = new DataView(buffer);
  view.setUint32(0, sequence >>> 0, true);
  view.setBigInt64(4, BigInt(Math.round(transportSample)), true);
  view.setUint16(12, length, true);
  view.setUint8(14, flags);
  view.setUint8(15, 0);
  for (let i = 0; i < samples.length; i++) {
    const s = Math.max(-1, Math.min(1, samples[i] ?? 0));
    view.setInt16(ListenIn.headerBytes + i * 2, Math.round(s * 32767), true);
  }
  return buffer;
}

/** What the fake stream asks of the fake Server each frame: the transport (−1 stopped) at 48 kHz, and the master's level. */
export interface ListenInSource {
  /** The project sample of now (−1 stopped) and the project's sample rate. */
  transport(): { sample: number; sampleRate: number; playing: boolean };
  /** Called when the socket closes (the listener count drops). */
  closed(socket: FakeListenInSocket): void;
}

/**
 * One listener's fake stream: the format message, then a frame every 20 ms (in realtime mode; tests call
 * {@link pump}): the fake master's tone while playing (silence stopped), stamped with the transport's sample.
 */
export class FakeListenInSocket implements ListenInSocket {
  binaryType: BinaryType = 'arraybuffer';
  onopen: ((event: Event) => void) | null = null;
  onmessage: ((event: MessageEvent<string | ArrayBuffer>) => void) | null = null;
  onclose: ((event: CloseEvent) => void) | null = null;
  onerror: ((event: Event) => void) | null = null;
  private sequence = 0;
  private phase = 0;
  private wasPlaying = false;
  private timer: ReturnType<typeof setInterval> | null = null;
  private open = true;
  readonly format: ListenInFormat & { codec: string };

  constructor(
    private readonly source: ListenInSource,
    realtime: boolean
  ) {
    this.format = {
      sampleRate: ListenIn.sampleRate,
      channels: ListenIn.channels,
      frameSamples: ListenIn.frameSamples,
      bitrate: ListenIn.bitrate,
      projectSampleRate: source.transport().sampleRate,
      codec: FakeListenInCodec
    };
    setTimeout(() => {
      if (!this.open) {
        return;
      }
      this.onopen?.(new Event('open'));
      this.onmessage?.(new MessageEvent('message', { data: JSON.stringify(this.format) }));
      if (realtime) {
        this.timer = setInterval(() => this.pump(1), 20);
      }
    }, 0);
  }

  /** Sends `frames` frames (20 ms each). */
  pump(frames: number): void {
    for (let f = 0; f < frames && this.open; f++) {
      const { sample, sampleRate, playing } = this.source.transport();
      const n = ListenIn.frameSamples;
      const samples = new Float32Array(n * 2);
      if (playing) {
        for (let i = 0; i < n; i++) {
          const v = FakeToneLevel * Math.sin(this.phase);
          this.phase += (2 * Math.PI * FakeToneHz) / ListenIn.sampleRate;
          samples[i * 2] = v;
          samples[i * 2 + 1] = v;
        }
        this.phase %= 2 * Math.PI;
      }
      const flags =
        (playing ? ListenIn.flagPlaying : 0) | (playing !== this.wasPlaying ? ListenIn.flagDiscontinuity : 0);
      this.wasPlaying = playing;
      // The transport sample is the project's (its rate), not the stream's 48 kHz.
      const transport = playing ? Math.max(0, sample - Math.round((n * sampleRate) / ListenIn.sampleRate)) : -1;
      const frame = encodeFakeFrame(this.sequence++, transport, flags, samples);
      this.onmessage?.(new MessageEvent('message', { data: frame }));
    }
  }

  close(): void {
    if (!this.open) {
      return;
    }
    this.open = false;
    if (this.timer) {
      clearInterval(this.timer);
      this.timer = null;
    }
    this.source.closed(this);
    this.onclose?.(new CloseEvent('close', { code: 1000 }));
  }
}
