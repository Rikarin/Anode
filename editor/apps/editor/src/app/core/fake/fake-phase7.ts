import {
  ApiError,
  type ArticulationSet,
  type AudioEditOperation,
  type AudioFile,
  type AudioRegion,
  type ControlEvent,
  type EditCommand,
  type GrooveTemplate,
  type Insert,
  type LaneSet,
  type MidiRegion,
  type Note,
  type ParameterInfo,
  type PluginInfo,
  type Project,
  type SampleRange,
  type StepInputRequest,
  type StepInputState,
  type Track
} from '@anode/api-client';
import { encodePeaks, peakLevelFor } from '@anode/timeline';
import { applyAudioEdit, convertRate } from '../../audio-file-editor/audio-edit-math';
import { grooveFromNotes, grooveFromOnsets, quantizeToGroove } from '../../midi-edit/groove';
import { DefaultStepInput, stepLength } from '../../midi-edit/step-input';
import { applyTransform } from '../../midi-edit/transform';
import { NeutralPlayback } from '../../midi/midi-math';
import { meterOf } from './fake-arrange';
import { hash } from './fake-audio';
import { timingOf } from './fake-flex';

/** A command of one type. */
type Command<T extends EditCommand['type']> = Extract<EditCommand, { type: T }>;

/** Scripter's plugin id (workstream D's `IMidiEffect`). */
export const ScripterId = 'anode.scripter';

/** The commands this module applies (docs/08-phase-7.md: 16 new commands). */
const Phase7Commands = new Set<EditCommand['type']>([
  'setControls',
  'transform',
  'makeGroove',
  'setGroove',
  'deleteGroove',
  'setGrooveTrack',
  'setMatchGroove',
  'setArticulationSet',
  'deleteArticulationSet',
  'assignArticulationSet',
  'setLaneSet',
  'deleteLaneSet',
  'stepNotes',
  'stepTie',
  'setFileVersion',
  'setScript'
]);

export function isPhase7Command(command: EditCommand): boolean {
  return Phase7Commands.has(command.type);
}

/** What the Phase 7 commands need from the fake Server. */
export interface Phase7Context {
  id(prefix: string): string;
  noteId(): number;
  /** A file's transients (frames), as its analysis has them. */
  transients(file: AudioFile): number[];
  /** The step input state; steps advance it. */
  stepInput: StepInputState;
  setStepInput(state: StepInputState): void;
  /** The notes the last step added (a tie lengthens them), and their region. */
  lastStep: { regionId: string; noteIds: number[] } | null;
  setLastStep(step: { regionId: string; noteIds: number[] } | null): void;
  /** A Scripter slot's parameters after a compile, as the host reports them (`slotParameters`). */
  compiled(trackId: string, slotId: string, source: string): ParameterInfo[];
}

function refuse(message: string): never {
  throw new ApiError(422, message);
}

function sortNotes(notes: Note[]): Note[] {
  return notes.sort((a, b) => a.startTick - b.startTick || a.pitch - b.pitch);
}

function findMidiRegion(project: Project, id: string | null | undefined): MidiRegion {
  return project.midiRegions.find(r => r.id === id) ?? refuse(`No MIDI region ${id ?? '(none)'}.`);
}

function findTrack(project: Project, id: string | null | undefined): Track {
  return project.tracks.find(t => t.id === id) ?? refuse(`No track ${id ?? '(none)'}.`);
}

function withMidiRegion(project: Project, region: MidiRegion): Project {
  return { ...project, midiRegions: project.midiRegions.map(r => (r.id === region.id ? region : r)) };
}

function withTrack(project: Project, track: Track): Project {
  return { ...project, tracks: project.tracks.map(t => (t.id === track.id ? track : t)) };
}

function upsert<T extends { id: string }>(list: readonly T[], item: T): T[] {
  return list.some(x => x.id === item.id) ? list.map(x => (x.id === item.id ? item : x)) : [...list, item];
}

/** Checks a controller event's ranges (as `addControls` does). */
export function checkControl(event: Pick<ControlEvent, 'kind' | 'channel' | 'number' | 'value'>): void {
  const [lo, hi] = event.kind === 'pitchBend' ? [-8192, 8191] : [0, 127];
  if (
    !Number.isInteger(event.value) ||
    event.value < lo ||
    event.value > hi ||
    event.channel < 1 ||
    event.channel > 16 ||
    event.number < 0 ||
    event.number > 127
  ) {
    refuse(`A ${event.kind} event is out of range.`);
  }
}

/** Checks a groove template's shape. */
export function checkGroove(groove: GrooveTemplate): void {
  if (!(groove.stepTicks > 0) || !(groove.lengthTicks > 0) || groove.lengthTicks % groove.stepTicks !== 0) {
    refuse('A groove has a step and a length that is a whole number of steps.');
  }
  if (groove.points.length !== groove.lengthTicks / groove.stepTicks) {
    refuse('A groove has one point per step.');
  }
  if (groove.points.some(p => !(p.velocityScale >= 0) || !Number.isFinite(p.tick))) {
    refuse('A groove point has a tick and a velocity scale of 0 or more.');
  }
}

/** Checks an articulation set (ids 1…254, unique; switches and outputs of its articulations, in range). */
export function checkArticulationSet(set: ArticulationSet): void {
  const ids = new Set<number>();
  for (const a of set.articulations) {
    if (!Number.isInteger(a.id) || a.id < 1 || a.id > 254 || ids.has(a.id)) {
      refuse('Articulation ids are 1…254 and unique in their set.');
    }
    ids.add(a.id);
  }
  for (const s of set.switches) {
    if (
      !ids.has(s.articulationId) ||
      s.number < 0 ||
      s.number > 127 ||
      (s.value != null && (s.value < 0 || s.value > 127))
    ) {
      refuse('A switch names an articulation of its set, with a key or controller 0…127.');
    }
  }
  for (const o of set.outputs) {
    const ok =
      ids.has(o.articulationId) &&
      (o.kind === 'note'
        ? o.number >= 0 && o.number <= 127 && o.value >= 1 && o.value <= 127
        : o.kind === 'controlChange'
          ? o.number >= 0 && o.number <= 127 && o.value >= 0 && o.value <= 127
          : o.kind === 'program'
            ? o.value >= 0 && o.value <= 127
            : o.value >= 1 && o.value <= 16);
    if (!ok) {
      refuse('An output names an articulation of its set, with values in its range.');
    }
  }
}

/** A note's articulation id is 0 (none) or 1…254. */
export function checkArticulationId(id: number | null | undefined): void {
  if (id != null && (!Number.isInteger(id) || id < 0 || id > 254)) {
    refuse('An articulation id is 0…254.');
  }
}

const Sixteenth = 240;

/**
 * A groove template from a region of the project over its first `bars` bars: a MIDI region's notes, or an audio
 * region's transients (frames through its timing to ticks).
 */
export function grooveOfRegion(
  project: Project,
  regionId: string,
  id: string,
  name: string | null | undefined,
  bars: number,
  stepTicks: number,
  transients: (file: AudioFile) => number[]
): GrooveTemplate {
  const meter = meterOf(project);
  const midi = project.midiRegions.find(r => r.id === regionId);
  if (midi) {
    const length = meter.barStart(meter.barAt(midi.startTick) + bars) - midi.startTick;
    return grooveFromNotes(id, name || midi.name, midi.notes, midi.startTick, length, stepTicks);
  }
  const audio = project.regions.find(r => r.id === regionId);
  if (!audio) {
    refuse(`No region ${regionId}.`);
  }
  const file = project.audioFiles.find(f => f.id === audio.fileId) ?? refuse(`No audio file ${audio.fileId}.`);
  const timing = timingOf(project, audio);
  const onsets = transients(file)
    .filter(f => f >= audio.fileOffset && f < audio.fileOffset + audio.length)
    .map(f => ({ tick: timing.tickAt(f), strength: 1 }));
  const length = meter.barStart(meter.barAt(audio.startTick) + bars) - audio.startTick;
  return grooveFromOnsets(id, name || audio.name, onsets, audio.startTick, length, stepTicks);
}

/**
 * The groove a quantise on `trackId` uses: `grooveId`'s template of the project, or, without one on a track that
 * matches the groove track, the groove of the groove track's first region (made now, a bar of 1/16s); null: the grid.
 */
export function quantizeGroove(
  project: Project,
  trackId: string,
  grooveId: string | null | undefined,
  transients: (file: AudioFile) => number[]
): GrooveTemplate | null {
  if (grooveId) {
    return project.grooves.find(g => g.id === grooveId) ?? refuse(`No groove ${grooveId}.`);
  }
  const grooveTrack = project.settings.grooveTrackId;
  const track = project.tracks.find(t => t.id === trackId);
  if (!grooveTrack || !track?.matchGroove || grooveTrack === trackId) {
    return null;
  }
  const first = [
    ...project.midiRegions.filter(r => r.trackId === grooveTrack).map(r => ({ id: r.id, start: r.startTick })),
    ...project.regions.filter(r => r.trackId === grooveTrack).map(r => ({ id: r.id, start: r.startTick }))
  ].sort((a, b) => a.start - b.start)[0];
  return first ? grooveOfRegion(project, first.id, 'grv_live', 'Groove Track', 1, Sixteenth, transients) : null;
}

/** Quantises a MIDI region's notes to a groove (the `quantizeNotes` command with a groove). */
export function quantizeRegionToGroove(
  region: MidiRegion,
  groove: GrooveTemplate,
  strength: number,
  velocity: boolean,
  ids?: ReadonlySet<number>
): MidiRegion {
  return {
    ...region,
    notes: sortNotes(quantizeToGroove(region.notes, region.startTick, groove, strength, velocity, ids))
  };
}

/** Applies a Phase 7 command. */
export function applyPhase7Command(project: Project, command: EditCommand, ctx: Phase7Context): Project {
  switch (command.type) {
    case 'setControls':
      return setControls(project, command);
    case 'transform':
      return transform(project, command);
    case 'makeGroove': {
      const bars = command.lengthBars ?? 1;
      const step = command.stepTicks ?? Sixteenth;
      if (!Number.isInteger(bars) || bars < 1 || bars > 4 || !(step > 0)) {
        refuse('A groove is 1…4 bars long, with a grid step of more than 0 ticks.');
      }
      const groove = grooveOfRegion(project, command.regionId, ctx.id('grv'), command.name, bars, step, f =>
        ctx.transients(f)
      );
      return { ...project, grooves: [...project.grooves, groove] };
    }
    case 'setGroove':
      checkGroove(command.groove);
      if (!command.groove.id.startsWith('grv_')) {
        refuse('Groove ids start with grv_.');
      }
      return { ...project, grooves: upsert(project.grooves, command.groove) };
    case 'deleteGroove': {
      if (!project.grooves.some(g => g.id === command.grooveId)) {
        refuse(`No groove ${command.grooveId}.`);
      }
      return {
        ...project,
        grooves: project.grooves.filter(g => g.id !== command.grooveId),
        midiRegions: project.midiRegions.map(r =>
          r.playback.grooveId === command.grooveId ? { ...r, playback: { ...r.playback, grooveId: null } } : r
        )
      };
    }
    case 'setGrooveTrack': {
      if (command.trackId) {
        const track = findTrack(project, command.trackId);
        if (track.kind === 'aux' || track.kind === 'vca') {
          refuse(`${track.name} holds no regions: it cannot be the groove track.`);
        }
      }
      return { ...project, settings: { ...project.settings, grooveTrackId: command.trackId ?? null } };
    }
    case 'setMatchGroove':
      return withTrack(project, { ...findTrack(project, command.trackId), matchGroove: command.matchGroove });
    case 'setArticulationSet':
      checkArticulationSet(command.set);
      if (!command.set.id.startsWith('ars_')) {
        refuse('Articulation set ids start with ars_.');
      }
      return { ...project, articulationSets: upsert(project.articulationSets, command.set) };
    case 'deleteArticulationSet':
      if (!project.articulationSets.some(s => s.id === command.setId)) {
        refuse(`No articulation set ${command.setId}.`);
      }
      return {
        ...project,
        articulationSets: project.articulationSets.filter(s => s.id !== command.setId),
        tracks: project.tracks.map(t => (t.articulationSetId === command.setId ? { ...t, articulationSetId: null } : t))
      };
    case 'assignArticulationSet': {
      const track = findTrack(project, command.trackId);
      if (track.kind !== 'instrument' && track.kind !== 'midi') {
        refuse(`${track.name} does not play MIDI.`);
      }
      if (command.setId && !project.articulationSets.some(s => s.id === command.setId)) {
        refuse(`No articulation set ${command.setId}.`);
      }
      return withTrack(project, { ...track, articulationSetId: command.setId ?? null });
    }
    case 'setLaneSet':
      checkLaneSet(command.set);
      return { ...project, laneSets: upsert(project.laneSets, command.set) };
    case 'deleteLaneSet':
      if (!project.laneSets.some(s => s.id === command.setId)) {
        refuse(`No lane set ${command.setId}.`);
      }
      return { ...project, laneSets: project.laneSets.filter(s => s.id !== command.setId) };
    case 'stepNotes':
      return stepNotes(project, command, ctx);
    case 'stepTie':
      return stepTie(project, command, ctx);
    case 'setFileVersion': {
      const file = project.audioFiles.find(f => f.id === command.fileId) ?? refuse(`No audio file ${command.fileId}.`);
      const next: AudioFile = {
        ...file,
        version: command.version,
        path: command.path,
        frames: command.frames ?? file.frames,
        sampleRate: command.sampleRate ?? file.sampleRate
      };
      return { ...project, audioFiles: project.audioFiles.map(f => (f.id === file.id ? next : f)) };
    }
    case 'setScript':
      return setScript(project, command, ctx);
    default:
      return project;
  }
}

function setControls(project: Project, command: Command<'setControls'>): Project {
  const region = findMidiRegion(project, command.regionId);
  const changes = new Map(command.changes.map(c => [c.id, c]));
  for (const id of changes.keys()) {
    if (!region.controls.some(c => c.id === id)) {
      refuse(`${region.name} has no controller event ${id}.`);
    }
  }
  const controls = region.controls.map(c => {
    const change = changes.get(c.id);
    if (!change) {
      return c;
    }
    const next: ControlEvent = {
      ...c,
      tick: Math.round(change.tick ?? c.tick),
      channel: change.channel ?? c.channel,
      number: change.number ?? c.number,
      value: change.value ?? c.value
    };
    checkControl(next);
    return next;
  });
  return withMidiRegion(project, { ...region, controls: controls.sort((a, b) => a.tick - b.tick) });
}

function transform(project: Project, command: Command<'transform'>): Project {
  if (command.set.mode === 'selectOnly') {
    refuse('Select only changes the editor’s selection: the editor runs it.');
  }
  const regions = command.regionIds.map(id => findMidiRegion(project, id));
  const noteIds = command.noteIds ? new Set(command.noteIds) : null;
  const controlIds = command.controlIds ? new Set(command.controlIds) : null;
  // Positions are project ticks while it runs.
  const notes = regions.flatMap(r =>
    r.notes.filter(n => !noteIds || noteIds.has(n.id)).map(n => ({ ...n, startTick: n.startTick + r.startTick }))
  );
  const controls = regions.flatMap(r =>
    r.controls.filter(c => !controlIds || controlIds.has(c.id)).map(c => ({ ...c, tick: c.tick + r.startTick }))
  );
  const result = applyTransform(notes, controls, command.set, meterOf(project));
  const notesById = new Map(result.notes.map(n => [n.id, n]));
  const controlsById = new Map(result.controls.map(c => [c.id, c]));
  let next = project;
  for (const r of regions) {
    next = withMidiRegion(next, {
      ...r,
      notes: sortNotes(
        r.notes.map(n => {
          const t = notesById.get(n.id);
          return t ? { ...t, startTick: t.startTick - r.startTick } : n;
        })
      ),
      controls: r.controls
        .map(c => {
          const t = controlsById.get(c.id);
          return t ? { ...t, tick: t.tick - r.startTick } : c;
        })
        .sort((a, b) => a.tick - b.tick)
    });
  }
  return next;
}

function checkLaneSet(set: LaneSet): void {
  if (!set.id.startsWith('lns_') || !set.name.trim()) {
    refuse('A lane set has an id starting with lns_ and a name.');
  }
  for (const lane of set.lanes) {
    if (!(lane.resolutionTicks > 0)) {
      refuse('A lane draws on a grid of more than 0 ticks.');
    }
    const numbered = lane.kind === 'controlChange' || lane.kind === 'polyPressure' || lane.kind === 'note';
    if (numbered !== (lane.number != null) || (lane.number != null && (lane.number < 0 || lane.number > 127))) {
      refuse(`A ${lane.kind} lane ${numbered ? 'needs a number 0…127' : 'has no number'}.`);
    }
  }
}

/**
 * A step: the server fills every left-out property from the step input state, adds a region on the track when the
 * state has none (a bar long from the step, lengthened as steps pass its end), then advances unless chord mode is on.
 */
function stepNotes(project: Project, command: Command<'stepNotes'>, ctx: Phase7Context): Project {
  const state = ctx.stepInput;
  const velocity = command.velocity ?? state.velocity;
  const length = command.lengthTicks ?? stepLength(state);
  const position = Math.round(command.positionTick ?? state.positionTick);
  const channel = command.channel ?? 1;
  if (command.pitches.length === 0 || command.pitches.some(p => !Number.isInteger(p) || p < 0 || p > 127)) {
    refuse('A step has one or more pitches 0…127.');
  }
  if (!Number.isInteger(velocity) || velocity < 1 || velocity > 127) {
    refuse('A step’s velocity is 1…127.');
  }
  let next = project;
  let region =
    (command.regionId ?? state.regionId) ? findMidiRegion(project, command.regionId ?? state.regionId) : null;
  if (!region) {
    const track = findTrack(project, command.trackId ?? state.trackId);
    if (track.kind !== 'instrument' && track.kind !== 'midi') {
      refuse(`${track.name} does not play MIDI.`);
    }
    region = {
      id: ctx.id('mid'),
      trackId: track.id,
      name: track.name,
      startTick: position,
      lengthTicks: meterOf(project).barTicksAt(position),
      playback: { ...NeutralPlayback },
      notes: [],
      controls: [],
      muted: false
    };
    next = { ...next, midiRegions: [...next.midiRegions, region] };
  }
  const relative = position - region.startTick;
  const added: Note[] = [...new Set(command.pitches)].map(pitch => ({
    id: ctx.noteId(),
    pitch,
    velocity,
    channel,
    startTick: relative,
    lengthTicks: length,
    articulationId: 0
  }));
  const end = Math.max(region.lengthTicks, relative + length);
  const updated: MidiRegion = { ...region, lengthTicks: end, notes: sortNotes([...region.notes, ...added]) };
  next = withMidiRegion(next, updated);
  const stacking = state.chord && ctx.lastStep?.regionId === updated.id && state.positionTick === position;
  ctx.setLastStep({
    regionId: updated.id,
    noteIds: [...(stacking ? (ctx.lastStep?.noteIds ?? []) : []), ...added.map(n => n.id)]
  });
  ctx.setStepInput({
    ...state,
    regionId: updated.id,
    trackId: updated.trackId,
    positionTick: state.chord ? position : position + length
  });
  return next;
}

function stepTie(project: Project, command: Command<'stepTie'>, ctx: Phase7Context): Project {
  const state = ctx.stepInput;
  const last = ctx.lastStep;
  const regionId = command.regionId ?? last?.regionId ?? state.regionId;
  const region = findMidiRegion(project, regionId);
  const ids = new Set(command.noteIds ?? (last?.regionId === region.id ? last.noteIds : []));
  if (ids.size === 0) {
    refuse('There is no step to tie.');
  }
  const length = command.lengthTicks ?? stepLength(state);
  const notes = region.notes.map(n => (ids.has(n.id) ? { ...n, lengthTicks: n.lengthTicks + length } : n));
  const end = Math.max(region.lengthTicks, ...notes.filter(n => ids.has(n.id)).map(n => n.startTick + n.lengthTicks));
  ctx.setStepInput({ ...state, positionTick: state.positionTick + length });
  return withMidiRegion(project, { ...region, lengthTicks: end, notes });
}

function setScript(project: Project, command: Command<'setScript'>, ctx: Phase7Context): Project {
  const track = findTrack(project, command.trackId);
  const slot = track.strip.midiEffects.find(i => i.id === command.slotId);
  if (!slot) {
    refuse(`${track.name} has no MIDI FX slot ${command.slotId}.`);
  }
  if (slot.pluginId !== ScripterId) {
    refuse(`${slot.name} is not Scripter.`);
  }
  const runtime = ctx.compiled(track.id, slot.id, command.source);
  const values: Record<string, number> = {};
  for (const p of runtime) {
    values[p.id] = slot.parameters[p.id] ?? p.default;
  }
  const next: Insert = { ...slot, source: command.source, runtimeParameters: runtime, parameters: values };
  return withTrack(project, {
    ...track,
    strip: { ...track.strip, midiEffects: track.strip.midiEffects.map(i => (i.id === slot.id ? next : i)) }
  });
}

// ---- step input state

/** The step input state after a request (left-out properties stay; `regionId` empty clears it). */
export function nextStepInput(state: StepInputState, request: StepInputRequest): StepInputState {
  const next: StepInputState = {
    on: request.on ?? state.on,
    regionId:
      request.regionId === undefined || request.regionId === null ? (state.regionId ?? null) : request.regionId || null,
    trackId: request.trackId ?? state.trackId ?? null,
    positionTick: Math.max(0, Math.round(request.positionTick ?? state.positionTick)),
    noteValueTicks: request.noteValueTicks ?? state.noteValueTicks,
    dotted: request.dotted ?? state.dotted,
    triplet: request.triplet ?? state.triplet,
    velocity: request.velocity ?? state.velocity,
    chord: request.chord ?? state.chord,
    midiInput: request.midiInput ?? state.midiInput
  };
  if (!(next.noteValueTicks > 0) || next.velocity < 1 || next.velocity > 127) {
    refuse('Step input takes a note value of more than 0 ticks and a velocity of 1…127.');
  }
  return next;
}

export { DefaultStepInput };

// ---- Scripter's parameters (the host's compile, as far as the fake can tell)

/** Scripter's `PluginParameters` types. */
type ScriptParameterType = 'lin' | 'log' | 'menu' | 'checkbox' | 'momentary' | 'text';

/**
 * The parameters a script declares, read from its `PluginParameters` array literal the way the host reports them
 * (`slotParameters`): ids `p0`, `p1`… in order. The fake does not run scripts; a script it cannot read declares none.
 */
export function scriptParameters(source: string): ParameterInfo[] {
  const match = /PluginParameters\s*=\s*(\[[\s\S]*?\])\s*;?\s*(?:\n|$)/.exec(source);
  if (!match?.[1]) {
    return [];
  }
  let parsed: unknown;
  try {
    const json = match[1]
      .replace(/\/\/[^\n]*/g, '')
      .replace(/'([^'\\]*)'/g, '"$1"')
      .replace(/([{,]\s*)([A-Za-z_][A-Za-z0-9_]*)\s*:/g, '$1"$2":')
      .replace(/,\s*([}\]])/g, '$1');
    parsed = JSON.parse(json);
  } catch {
    return [];
  }
  if (!Array.isArray(parsed)) {
    return [];
  }
  return parsed.flatMap((entry: unknown, index) => {
    if (typeof entry !== 'object' || entry === null) {
      return [];
    }
    const e = entry as Record<string, unknown>;
    const type = (typeof e['type'] === 'string' ? e['type'] : 'lin') as ScriptParameterType;
    const names = Array.isArray(e['valueStrings']) ? e['valueStrings'].map(String) : null;
    const number = (key: string, fallback: number) => (typeof e[key] === 'number' ? e[key] : fallback);
    const min = type === 'menu' || type === 'checkbox' || type === 'momentary' ? 0 : number('minValue', 0);
    const max =
      type === 'menu'
        ? Math.max(0, (names?.length ?? 1) - 1)
        : type === 'checkbox' || type === 'momentary'
          ? 1
          : number('maxValue', 1);
    // `steps` + 1 values (ParameterInfo): a menu's names, a checkbox's off and on.
    const steps =
      type === 'menu'
        ? Math.max(1, (names?.length ?? 1) - 1)
        : type === 'checkbox' || type === 'momentary'
          ? 1
          : number('numberOfSteps', 0);
    return [
      {
        id: `p${index}`,
        name: typeof e['name'] === 'string' ? e['name'] : `Parameter ${index + 1}`,
        min,
        max,
        default: Math.min(max, Math.max(min, number('defaultValue', min))),
        unit: typeof e['unit'] === 'string' ? e['unit'] : '',
        steps,
        valueNames: type === 'menu' ? names : null,
        macro: null
      } satisfies ParameterInfo
    ];
  });
}

/** `Trace("…")` lines with a literal argument, as a script would log them (the fake's console). */
export function scriptTraces(source: string): string[] {
  return [...source.matchAll(/Trace\(\s*(['"])(.*?)\1\s*\)/g)].map(m => m[2] ?? '');
}

/** Whether a script would run away (the fake's stand-in for the budget interrupt). */
export function runsAway(source: string): boolean {
  return runawayLine(source) !== null;
}

/** The 1-based line of a script's endless loop (the line the host's interrupt reports); null: none. */
export function runawayLine(source: string): number | null {
  const match = /while\s*\(\s*true\s*\)\s*\{\s*\}|for\s*\(\s*;\s*;\s*\)\s*\{\s*\}/.exec(source);
  return match ? source.slice(0, match.index).split('\n').length : null;
}

/** Scripter, for the fake catalogue (workstream D adds the real one): parameters come from the script. */
export function scripterInfo(category: string): PluginInfo {
  return {
    id: ScripterId,
    name: 'Scripter',
    vendor: 'Anode',
    version: '1.0',
    format: 'builtin',
    isInstrument: false,
    fileTypes: [],
    parameters: [],
    category,
    hasSidechain: false,
    hasEditor: false
  };
}

// ---- the Audio File Editor's samples

/** Samples of a fake file's first version: decaying tones like its fake peaks. */
export function fakeSamples(file: AudioFile): Float32Array[] {
  const seed = hash(file.id + file.name);
  const hitEvery = 6_000 + (seed % 18_000);
  const frequency = 110 * (1 + (seed % 4));
  const channels: Float32Array[] = [];
  for (let c = 0; c < file.channels; c++) {
    const data = new Float32Array(file.frames);
    for (let i = 0; i < file.frames; i++) {
      const hit = Math.floor(i / hitEvery);
      const accent = 0.45 + 0.5 * (((seed >>> (hit % 24)) & 7) / 7);
      const envelope = accent * Math.exp(-(i % hitEvery) / (hitEvery / 3.5)) + 0.04;
      data[i] = envelope * Math.sin((2 * Math.PI * frequency * i) / file.sampleRate + c * 0.3) * 0.9;
    }
    channels.push(data);
  }
  return channels;
}

/** At most this many values per channel in one `/samples` answer (the contract's limit). */
export const MaxSampleValues = 65_536;

/** A `/samples` answer from a file's channels. */
export function sampleRange(channels: readonly Float32Array[], start: number, frames: number, bucket = 1): SampleRange {
  const total = channels[0]?.length ?? 0;
  const from = Math.max(0, Math.min(total, Math.round(start)));
  const count = Math.max(0, Math.min(total - from, Math.round(frames)));
  const b = Math.max(1, Math.round(bucket));
  if (Math.ceil(count / b) > MaxSampleValues) {
    throw new ApiError(422, `At most ${MaxSampleValues} values per channel: use a larger bucket.`);
  }
  if (b === 1) {
    return {
      startFrame: from,
      frames: count,
      channels: channels.length,
      bucket: 1,
      samples: channels.map(c => Array.from(c.subarray(from, from + count))),
      min: null,
      max: null
    };
  }
  const min: number[][] = [];
  const max: number[][] = [];
  for (const c of channels) {
    const lo: number[] = [];
    const hi: number[] = [];
    for (let i = from; i < from + count; i += b) {
      let a = Infinity;
      let z = -Infinity;
      for (let j = i; j < Math.min(from + count, i + b); j++) {
        const v = c[j] ?? 0;
        a = Math.min(a, v);
        z = Math.max(z, v);
      }
      lo.push(a);
      hi.push(z);
    }
    min.push(lo);
    max.push(hi);
  }
  return { startFrame: from, frames: count, channels: channels.length, bucket: b, samples: null, min, max };
}

/** A file's channels after an operation (rate conversion included). */
export function editedChannels(
  channels: readonly Float32Array[],
  file: AudioFile,
  op: AudioEditOperation
): Float32Array[] {
  if (op.kind === 'convertRate') {
    if (!op.targetRate || op.targetRate < 8_000 || op.targetRate > 384_000) {
      refuse('Sample-rate conversion needs a target rate of 8 kHz…384 kHz.');
    }
    return convertRate(channels, file.sampleRate, op.targetRate);
  }
  return applyAudioEdit(channels, op);
}

/** Peaks (`PeakFormat`) of real channels at the kept level nearest `framesPerPeak`. */
export function peaksOf(channels: readonly Float32Array[], framesPerPeak: number): ArrayBuffer {
  const level = peakLevelFor(framesPerPeak);
  const frames = channels[0]?.length ?? 0;
  const count = Math.ceil(frames / level);
  const data = new Int16Array(count * channels.length * 2);
  for (let p = 0; p < count; p++) {
    channels.forEach((c, ci) => {
      let lo = 0;
      let hi = 0;
      for (let i = p * level; i < Math.min(frames, (p + 1) * level); i++) {
        const v = c[i] ?? 0;
        lo = Math.min(lo, v);
        hi = Math.max(hi, v);
      }
      data[2 * (p * channels.length + ci)] = Math.round(Math.max(-1, lo) * 32_767);
      data[2 * (p * channels.length + ci) + 1] = Math.round(Math.min(1, hi) * 32_767);
    });
  }
  return encodePeaks({ channels: channels.length, framesPerPeak: level, firstPeak: 0, data });
}

/**
 * The region shifts a length-changing operation needs (trim keeps `start…end`; conversion rescales): each region of
 * the file keeps what it played, clipped to what is left, or the operation is refused when one would vanish.
 */
export function shiftRegions(project: Project, file: AudioFile, op: AudioEditOperation, frames: number): AudioRegion[] {
  return project.regions.map(r => {
    if (r.fileId !== file.id) {
      return r;
    }
    if (op.kind === 'trim') {
      const start = Math.max(r.fileOffset, op.startFrame);
      const end = Math.min(r.fileOffset + r.length, op.endFrame);
      if (end <= start) {
        refuse(`Trimming would leave ${r.name} empty.`);
      }
      return { ...r, fileOffset: start - op.startFrame, length: end - start };
    }
    if (op.kind === 'convertRate' && op.targetRate) {
      const ratio = frames / file.frames;
      return { ...r, fileOffset: Math.round(r.fileOffset * ratio), length: Math.max(1, Math.round(r.length * ratio)) };
    }
    return r;
  });
}

/** A file's next version's path: `<name>.v<N>.wav` beside its first version. */
export function versionPath(file: AudioFile, version: number): string {
  const base = file.path.replace(/(\.v\d+)?\.wave?$/i, '');
  return `${base}.v${version}.wav`;
}
