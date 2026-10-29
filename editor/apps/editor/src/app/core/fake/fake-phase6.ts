import {
  ApiError,
  type Cell,
  type CellContent,
  type ChannelStrip,
  type DrumPad,
  type EditCommand,
  type Insert,
  type LiveLoopsGrid,
  type MidiRegion,
  type Note,
  type PatchInfo,
  type Pattern,
  type PatternRegion,
  type PluginInfo,
  type Project,
  type SmartControl,
  type SmartControls,
  type SmartMapping,
  type Track
} from '@anode/api-client';
import { MeterMap } from '@anode/timeline';
import { clampParameter, defaultParameters } from '../../instruments/parameters';
import { NeutralPlayback } from '../../midi/midi-math';
import { defaultRow, patternProblem, unrollPattern } from '../../music/pattern';
import {
  controlIds,
  controlKind,
  controlValueFor,
  emptyControl,
  graphProblem,
  knobCount,
  LinearGraph,
  mappedValue,
  smartControlsOf
} from '../../smart-controls/smart-math';
import { TrackColors } from '../../state/track-colors';
import { type MixContext, record, setLane, targetRange } from './fake-mix';
import { MidiEffectCategory, RemixFxId } from './fake-plugins-phase6';

/** A command of one type. */
type Command<T extends EditCommand['type']> = Extract<EditCommand, { type: T }>;

const Ppq = 960;
const ArpeggiatorId = 'anode.arpeggiator';
const DrumSynthId = 'anode.drum-synth';
/** MIDI FX slots per strip (Logic's). */
const MaxMidiEffects = 8;

/** A pad of a kit patch. */
export interface KitPad {
  note: number;
  name: string;
  pluginId: string;
  parameters?: Record<string, number>;
}

/** A Drum Machine Designer kit: a patch of kind `stack` (docs/07-phase-6.md: "Kits are patches"). */
export interface FakeKit {
  info: PatchInfo;
  pads: KitPad[];
}

/** The kits the fake Library starts with: Drum Synth and Quick Sampler pads on GM drum notes. */
export const FakeKits: readonly FakeKit[] = [
  {
    info: { id: 'kit_electro', name: 'Electro Kit', category: 'Drum Kits', kind: 'stack' },
    pads: [
      { note: 36, name: 'Kick', pluginId: DrumSynthId, parameters: { model: 0 } },
      { note: 38, name: 'Snare', pluginId: DrumSynthId, parameters: { model: 1 } },
      { note: 39, name: 'Clap', pluginId: DrumSynthId, parameters: { model: 2 } },
      { note: 42, name: 'Closed Hat', pluginId: DrumSynthId, parameters: { model: 3 } },
      { note: 46, name: 'Open Hat', pluginId: 'anode.quick-sampler' }
    ]
  },
  {
    info: { id: 'kit_hybrid', name: 'Hybrid Kit', category: 'Drum Kits', kind: 'stack' },
    pads: [
      { note: 36, name: 'Kick', pluginId: DrumSynthId, parameters: { model: 0 } },
      { note: 38, name: 'Snare', pluginId: 'anode.quick-sampler' },
      { note: 42, name: 'Hat', pluginId: DrumSynthId, parameters: { model: 3 } },
      { note: 45, name: 'Tom', pluginId: DrumSynthId, parameters: { model: 4 } }
    ]
  }
];

/** What the Phase 6 commands need from the fake Server. */
export interface Phase6Context {
  /** A new id with this prefix (`pat_7`). */
  id(prefix: string): string;
  /** A new note id (unique project-wide). */
  noteId(): number;
  plugin(id: string): PluginInfo | undefined;
  kit(id: string): FakeKit | undefined;
  mix: MixContext;
  /** Draws a new pattern seed (`reseedPattern` without one). */
  seed(): number;
}

function refuse(message: string): never {
  throw new ApiError(422, message);
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}

function findTrack(project: Project, id: string | null | undefined): Track {
  return project.tracks.find(t => t.id === id) ?? refuse(`No track ${id ?? '(none)'}.`);
}

function withTrack(project: Project, track: Track): Project {
  return { ...project, tracks: project.tracks.map(t => (t.id === track.id ? track : t)) };
}

function barTicksAt(project: Project, tick: number): number {
  return new MeterMap(project.settings.signatures).barTicksAt(tick);
}

/** A track that plays MIDI: an instrument or external MIDI track, or a Drum Machine Designer. */
export function playsMidi(track: Track): boolean {
  return track.kind === 'instrument' || track.kind === 'midi' || track.drumMachine !== null;
}

/** A new track's fields (the fake Server's defaults). */
export function blankTrack(id: string, name: string, kind: Track['kind'], color: string): Track {
  return {
    id,
    name,
    kind,
    color,
    input: { channel: 1, stereo: true },
    recordArmed: false,
    inputMonitoring: false,
    strip: {
      volumeDb: 0,
      pan: 0,
      mute: false,
      solo: false,
      inserts: [],
      sends: [],
      output: 'master',
      groups: [],
      vcaId: null,
      midiEffects: []
    },
    instrument: null,
    midiInput: { device: null, channel: 0 },
    midiOutput: null,
    parentId: null,
    collapsed: false,
    notes: '',
    inputBus: 0,
    automationMode: 'read',
    frozenFileId: null,
    flexOn: false,
    flexMode: 'polyphonic',
    alternatives: [],
    alternativeId: null,
    drumMachine: null,
    smartControls: null,
    liveLoopsActive: false,
    matchGroove: false
  };
}

// ---- pattern regions

function findPatternRegion(project: Project, id: string): PatternRegion {
  return project.patternRegions.find(r => r.id === id) ?? refuse(`No pattern region ${id}.`);
}

function withPatternRegion(project: Project, region: PatternRegion): Project {
  return { ...project, patternRegions: project.patternRegions.map(r => (r.id === region.id ? region : r)) };
}

/** Whether an id names a pattern region. */
export function isPatternRegionId(id: string): boolean {
  return id.startsWith('pat_');
}

/** A track's default pattern: a DMD's one pad row per pad, else one note row on C3 (60); 16 off 16ths. */
export function defaultPattern(track: Track, ctx: Pick<Phase6Context, 'id'>): Pattern {
  const pads = track.drumMachine?.pads ?? [];
  const rows = pads.length
    ? pads.map(pad => defaultRow(ctx.id('row'), 'pad', pad.note, pad.name))
    : [defaultRow(ctx.id('row'), 'note', 60, 'C3')];
  return { rows, swing: 0, seed: 1, stepTicks: 240 };
}

function checkPattern(pattern: Pattern): Pattern {
  const problem = patternProblem(pattern);
  if (problem) {
    refuse(problem);
  }
  return structuredClone(pattern);
}

/** Moves, copies or deletes pattern regions (`moveRegion`, `copyRegions`, `deleteRegions` take them too). */
export function movePatternRegion(project: Project, command: Command<'moveRegion'>): Project {
  const region = findPatternRegion(project, command.regionId);
  const trackId = command.trackId ?? region.trackId;
  if (!playsMidi(findTrack(project, trackId))) {
    refuse('A pattern region goes on an instrument track.');
  }
  if (!(command.startTick >= 0)) {
    refuse('A region start must not be negative.');
  }
  return withPatternRegion(project, { ...region, startTick: Math.round(command.startTick), trackId });
}

export function copyPatternRegions(
  project: Project,
  ids: readonly string[],
  offset: number,
  trackId: string | null | undefined,
  ctx: Pick<Phase6Context, 'id'>
): Project {
  let next = project;
  for (const id of ids) {
    const region = findPatternRegion(project, id);
    const target = trackId ?? region.trackId;
    if (!playsMidi(findTrack(project, target))) {
      refuse('A pattern region goes on an instrument track.');
    }
    if (region.startTick + offset < 0) {
      refuse('A copy cannot start before the project start.');
    }
    next = {
      ...next,
      patternRegions: [
        ...next.patternRegions,
        { ...structuredClone(region), id: ctx.id('pat'), startTick: region.startTick + offset, trackId: target }
      ]
    };
  }
  return next;
}

export function deletePatternRegions(project: Project, ids: readonly string[]): Project {
  ids.forEach(id => findPatternRegion(project, id));
  return { ...project, patternRegions: project.patternRegions.filter(r => !ids.includes(r.id)) };
}

/** The MIDI notes a pattern region plays, relative to `start`, with new ids. */
function patternNotes(project: Project, region: PatternRegion, start: number, ctx: Phase6Context): Note[] {
  return unrollPattern({ ...region, muted: false }, { keys: project.settings.keys }).map(note => ({
    id: ctx.noteId(),
    pitch: note.pitch,
    velocity: note.velocity,
    channel: 1,
    startTick: note.startTick - start,
    lengthTicks: note.lengthTicks,
    articulationId: 0
  }));
}

// ---- Drum Machine Designer

function drumMachineTrack(project: Project, id: string): Track & { drumMachine: { pads: DrumPad[] } } {
  const track = findTrack(project, id);
  if (!track.drumMachine) {
    refuse(`${track.name} is not a Drum Machine Designer track.`);
  }
  return track as Track & { drumMachine: { pads: DrumPad[] } };
}

function instrumentPlugin(ctx: Phase6Context, id: string): PluginInfo {
  const plugin = ctx.plugin(id);
  if (!plugin?.isInstrument) {
    refuse(`No instrument ${id} in the plugin catalogue.`);
  }
  return plugin;
}

function checkNote(note: number): void {
  if (!Number.isInteger(note) || note < 0 || note > 127) {
    refuse('A pad plays a note 0 to 127.');
  }
}

/** A pad's member track: an instrument track in the DMD stack, playing `plugin`. */
function padTrack(
  dmd: Track,
  name: string,
  plugin: PluginInfo,
  ctx: Phase6Context,
  parameters?: Record<string, number>
) {
  const track = blankTrack(ctx.id('trk'), name, 'instrument', dmd.color);
  return {
    ...track,
    parentId: dmd.id,
    instrument: {
      pluginId: plugin.id,
      name: plugin.name,
      parameters: { ...defaultParameters(plugin), ...(parameters ?? {}) },
      file: null
    }
  };
}

/** Puts pad tracks right after the DMD's last member. */
function insertMembers(project: Project, dmdId: string, members: readonly Track[]): Track[] {
  const tracks = [...project.tracks];
  let index = tracks.findIndex(t => t.id === dmdId);
  while (index >= 0 && tracks[index + 1]?.parentId === dmdId) {
    index++;
  }
  tracks.splice(index + 1, 0, ...members);
  return tracks;
}

function sortedPads(pads: readonly DrumPad[]): DrumPad[] {
  return [...pads].sort((a, b) => a.note - b.note);
}

// ---- MIDI FX slots

function midiEffectStrip(project: Project, trackId: string | null | undefined): { track: Track; strip: ChannelStrip } {
  const track = findTrack(project, trackId);
  if (track.kind !== 'instrument' && track.kind !== 'midi') {
    refuse(`${track.name} has no MIDI FX: only instrument and external MIDI tracks do.`);
  }
  return { track, strip: track.strip };
}

function findSlot(strip: ChannelStrip, id: string): Insert {
  return strip.midiEffects.find(i => i.id === id) ?? refuse(`No MIDI FX slot ${id}.`);
}

function withMidiEffects(project: Project, track: Track, midiEffects: Insert[]): Project {
  return withTrack(project, { ...track, strip: { ...track.strip, midiEffects } });
}

function newSlot(plugin: PluginInfo, ctx: Phase6Context): Insert {
  return {
    id: ctx.id('mfx'),
    pluginId: plugin.id,
    name: plugin.name,
    bypassed: false,
    parameters: defaultParameters(plugin),
    sidechain: null
  };
}

/** The Arpeggiator button's state: null without MIDI FX, else whether the first slot is an unbypassed Arpeggiator. */
function arpeggiatorState(track: Track): boolean | null {
  const first = track.strip.midiEffects[0];
  if (track.kind !== 'instrument' && track.kind !== 'midi') {
    return null;
  }
  return first ? first.pluginId === ArpeggiatorId && !first.bypassed : false;
}

/** Keeps a track's Smart Controls' arpeggiator button in step with its MIDI FX. */
function syncArpeggiator(project: Project, trackId: string): Project {
  const track = project.tracks.find(t => t.id === trackId);
  if (!track?.smartControls) {
    return project;
  }
  const arpeggiator = arpeggiatorState(track);
  return arpeggiator === track.smartControls.arpeggiator
    ? project
    : withTrack(project, { ...track, smartControls: { ...track.smartControls, arpeggiator } });
}

/**
 * The five insert commands on a strip's MIDI FX slots (`chain: midiEffects`): same shape, ids and validation as
 * inserts, only plugins of category `MIDI Effect`, never a sidechain; automation targets `midiEffect:{slot}:{param}`.
 */
export function applyMidiEffectCommand(project: Project, command: EditCommand, ctx: Phase6Context): Project {
  switch (command.type) {
    case 'addInsert': {
      const { track, strip } = midiEffectStrip(project, command.trackId);
      const plugin = ctx.plugin(command.pluginId);
      if (plugin?.category !== MidiEffectCategory) {
        refuse(`No MIDI effect ${command.pluginId} in the plugin catalogue.`);
      }
      if (strip.midiEffects.length >= MaxMidiEffects) {
        refuse(`A channel strip has at most ${MaxMidiEffects} MIDI FX slots.`);
      }
      const slots = [...strip.midiEffects];
      slots.splice(clamp(command.index ?? slots.length, 0, slots.length), 0, newSlot(plugin, ctx));
      return syncArpeggiator(withMidiEffects(project, track, slots), track.id);
    }
    case 'removeInsert': {
      const { track, strip } = midiEffectStrip(project, command.trackId);
      findSlot(strip, command.insertId);
      const after = withMidiEffects(
        project,
        track,
        strip.midiEffects.filter(i => i.id !== command.insertId)
      );
      const prefix = `midiEffect:${command.insertId}:`;
      return syncArpeggiator(
        {
          ...after,
          automation: after.automation.filter(l => !(l.trackId === track.id && l.target.startsWith(prefix))),
          tracks: after.tracks.map(t => dropMappings(t, prefix))
        },
        track.id
      );
    }
    case 'moveInsert': {
      const { track, strip } = midiEffectStrip(project, command.trackId);
      const slot = findSlot(strip, command.insertId);
      const slots = strip.midiEffects.filter(i => i.id !== slot.id);
      slots.splice(clamp(Math.round(command.index), 0, slots.length), 0, slot);
      return syncArpeggiator(withMidiEffects(project, track, slots), track.id);
    }
    case 'setInsert': {
      const { track, strip } = midiEffectStrip(project, command.trackId);
      const slot = findSlot(strip, command.insertId);
      if (command.sidechain) {
        refuse('A MIDI FX slot takes no sidechain.');
      }
      const next = { ...slot, bypassed: command.bypassed ?? slot.bypassed };
      return syncArpeggiator(
        withMidiEffects(
          project,
          track,
          strip.midiEffects.map(i => (i.id === slot.id ? next : i))
        ),
        track.id
      );
    }
    case 'setInsertParameter': {
      const { track, strip } = midiEffectStrip(project, command.trackId);
      const slot = findSlot(strip, command.insertId);
      // A run-time list (Scripter's, Phase 7) replaces the plugin's.
      const parameter =
        (slot.runtimeParameters ?? ctx.plugin(slot.pluginId)?.parameters)?.find(p => p.id === command.parameterId) ??
        refuse(`${slot.name} has no parameter ${command.parameterId}.`);
      if (!Number.isFinite(command.value)) {
        refuse('A parameter value must be a number.');
      }
      const value = clampParameter(parameter, command.value);
      const after = withMidiEffects(
        project,
        track,
        strip.midiEffects.map(i =>
          i.id === slot.id ? { ...i, parameters: { ...i.parameters, [parameter.id]: value } } : i
        )
      );
      return record(after, track.id, `midiEffect:${slot.id}:${parameter.id}`, value, ctx.mix);
    }
    default:
      return refuse(`${command.type} does not apply to MIDI FX slots.`);
  }
}

/** A track without Smart Control mappings to targets starting with `prefix` (a removed slot or insert). */
function dropMappings(track: Track, prefix: string): Track {
  if (!track.smartControls) {
    return track;
  }
  return {
    ...track,
    smartControls: {
      ...track.smartControls,
      controls: track.smartControls.controls.map(c => ({
        ...c,
        mappings: c.mappings.filter(m => !m.target.startsWith(prefix))
      }))
    }
  };
}

// ---- Smart Controls

/** A track's parameter (an automation target) set to a value directly (a Smart Control applying its mappings). */
function setTargetValue(project: Project, track: Track, target: string, value: number, ctx: Phase6Context): Project {
  const current = findTrack(project, track.id);
  const range = targetRange(project, track.id, target, id => ctx.plugin(id));
  const v = range.parameter ? clampParameter(range.parameter, value) : clamp(value, range.min, range.max);
  const strip = current.strip;
  if (target === 'volume') {
    return withTrack(project, { ...current, strip: { ...strip, volumeDb: v } });
  }
  if (target === 'pan') {
    return withTrack(project, { ...current, strip: { ...strip, pan: v } });
  }
  if (target === 'mute') {
    return withTrack(project, { ...current, strip: { ...strip, mute: v >= 0.5 } });
  }
  const send = /^send:(.+)$/.exec(target);
  if (send) {
    return withTrack(project, {
      ...current,
      strip: { ...strip, sends: strip.sends.map(s => (s.id === send[1] ? { ...s, levelDb: v } : s)) }
    });
  }
  const instrument = /^instrument:(.+)$/.exec(target);
  if (instrument?.[1] && current.instrument) {
    return withTrack(project, {
      ...current,
      instrument: { ...current.instrument, parameters: { ...current.instrument.parameters, [instrument[1]]: v } }
    });
  }
  const slot = /^(insert|midiEffect):([^:]+):(.+)$/.exec(target);
  if (slot?.[2] && slot[3]) {
    const key = slot[1] === 'insert' ? 'inserts' : 'midiEffects';
    const [, , id, parameterId] = slot;
    return withTrack(project, {
      ...current,
      strip: {
        ...strip,
        [key]: strip[key].map(i => (i.id === id ? { ...i, parameters: { ...i.parameters, [parameterId]: v } } : i))
      }
    });
  }
  return refuse(`${target} is not a target of ${current.name}.`);
}

/** The current value of a track's target. */
function targetValue(track: Track, target: string, ctx: Phase6Context): number | null {
  const strip = track.strip;
  if (target === 'volume') {
    return strip.volumeDb;
  }
  if (target === 'pan') {
    return strip.pan;
  }
  const send = /^send:(.+)$/.exec(target);
  if (send) {
    return strip.sends.find(s => s.id === send[1])?.levelDb ?? null;
  }
  const instrument = /^instrument:(.+)$/.exec(target);
  if (instrument?.[1] && track.instrument) {
    const id = instrument[1];
    return (
      track.instrument.parameters[id] ??
      ctx.plugin(track.instrument.pluginId)?.parameters.find(p => p.id === id)?.default ??
      null
    );
  }
  const slot = /^(insert|midiEffect):([^:]+):(.+)$/.exec(target);
  if (slot?.[2] && slot[3]) {
    const list = slot[1] === 'insert' ? strip.inserts : strip.midiEffects;
    const insert = list.find(i => i.id === slot[2]);
    const id = slot[3];
    return insert
      ? (insert.parameters[id] ?? ctx.plugin(insert.pluginId)?.parameters.find(p => p.id === id)?.default ?? null)
      : null;
  }
  return null;
}

/** Applies every mapping of a control at `value` to its parameter. */
function applyControl(project: Project, track: Track, control: SmartControl, value: number, ctx: Phase6Context) {
  let next = project;
  for (const mapping of control.mappings) {
    try {
      next = setTargetValue(next, track, mapping.target, mappedValue(mapping, value), ctx);
    } catch {
      // A mapping to a parameter that is gone (its plugin was removed) does nothing.
    }
  }
  return next;
}

function withControls(project: Project, track: Track, controls: SmartControls): Project {
  return withTrack(project, { ...findTrack(project, track.id), smartControls: controls });
}

/** A track's Smart Controls, set up (8 knobs, unmapped) when they were null. */
function setUp(track: Track): SmartControls {
  const controls = smartControlsOf(track.smartControls);
  return { ...controls, arpeggiator: arpeggiatorState(track) };
}

function findControl(controls: SmartControls, id: string): SmartControl {
  return controls.controls.find(c => c.id === id) ?? refuse(`No Smart Control ${id}.`);
}

function checkMapping(project: Project, track: Track, mapping: SmartMapping, ctx: Phase6Context): SmartMapping {
  targetRange(project, track.id, mapping.target, id => ctx.plugin(id));
  if (mapping.target.startsWith('smartControl:')) {
    refuse('A Smart Control maps to parameters, not to another control.');
  }
  const graph = mapping.graph.length ? mapping.graph : LinearGraph;
  const problem = graphProblem(graph);
  if (problem) {
    refuse(problem);
  }
  if (!Number.isFinite(mapping.min) || !Number.isFinite(mapping.max)) {
    refuse('A mapping range must be numbers.');
  }
  return { ...mapping, graph: graph.map(p => ({ ...p })) };
}

/** The macro hints of a track's plugins in order: the instrument's, then each insert's, each by ascending `macro`. */
export function macroTargets(track: Track, plugin: (id: string) => PluginInfo | undefined) {
  const hints = (info: PluginInfo | undefined) =>
    (info?.parameters ?? [])
      .filter(p => p.macro !== null && p.macro !== undefined)
      .sort((a, b) => (a.macro ?? 0) - (b.macro ?? 0));
  const out: { target: string; name: string; parameter: PluginInfo['parameters'][number] }[] = [];
  if (track.instrument) {
    for (const parameter of hints(plugin(track.instrument.pluginId))) {
      out.push({ target: `instrument:${parameter.id}`, name: parameter.name, parameter });
    }
  }
  for (const insert of track.strip.inserts) {
    for (const parameter of hints(plugin(insert.pluginId))) {
      out.push({ target: `insert:${insert.id}:${parameter.id}`, name: parameter.name, parameter });
    }
  }
  return out;
}

function applySmartControlsCommand(project: Project, command: EditCommand, ctx: Phase6Context): Project | undefined {
  switch (command.type) {
    case 'setSmartControls': {
      const track = findTrack(project, command.trackId);
      if (track.kind === 'vca') {
        refuse('A VCA has no Smart Controls.');
      }
      let controls = setUp(track);
      let next = project;
      if (command.layout && command.layout !== controls.layout) {
        const ids = controlIds(command.layout);
        const existing = new Map(controls.controls.map(c => [c.id, c]));
        controls = {
          ...controls,
          layout: command.layout,
          controls: ids.map(id => existing.get(id) ?? emptyControl(id))
        };
      }
      if (command.arpeggiator !== null && command.arpeggiator !== undefined) {
        if (track.kind !== 'instrument' && track.kind !== 'midi') {
          refuse(`${track.name} has no MIDI FX for an arpeggiator.`);
        }
        const first = track.strip.midiEffects[0];
        let slots = [...track.strip.midiEffects];
        if (first?.pluginId === ArpeggiatorId) {
          slots = slots.map((s, i) => (i === 0 ? { ...s, bypassed: !command.arpeggiator } : s));
        } else if (command.arpeggiator) {
          const arp = ctx.plugin(ArpeggiatorId) ?? refuse('No Arpeggiator in the plugin catalogue.');
          slots.unshift(newSlot(arp, ctx));
        }
        next = withMidiEffects(next, track, slots);
        controls = { ...controls, arpeggiator: arpeggiatorState(findTrack(next, track.id)) };
      }
      return withControls(next, track, controls);
    }
    case 'setSmartControl': {
      const track = findTrack(project, command.trackId);
      const controls = setUp(track);
      const control = findControl(controls, command.controlId);
      let value = control.value;
      if (command.value !== null && command.value !== undefined) {
        if (!(command.value >= 0 && command.value <= 1)) {
          refuse('A Smart Control value is 0 to 1.');
        }
        value = control.kind === 'switch' ? (command.value >= 0.5 ? 1 : 0) : command.value;
      }
      const next: SmartControl = { ...control, value, name: command.name?.trim() || control.name };
      let after = withControls(project, track, {
        ...controls,
        controls: controls.controls.map(c => (c.id === control.id ? next : c))
      });
      if (command.value !== null && command.value !== undefined) {
        after = applyControl(after, track, next, value, ctx);
        after = record(after, track.id, `smartControl:${control.id}`, value, ctx.mix);
      }
      return after;
    }
    case 'setSmartMappings': {
      const track = findTrack(project, command.trackId);
      const controls = setUp(track);
      const control = findControl(controls, command.controlId);
      const mappings = command.mappings.map(m => checkMapping(project, track, m, ctx));
      return withControls(project, track, {
        ...controls,
        controls: controls.controls.map(c => (c.id === control.id ? { ...c, mappings } : c))
      });
    }
    case 'autoMapSmartControls': {
      const track = findTrack(project, command.trackId);
      const controls = setUp(track);
      const hints = macroTargets(track, id => ctx.plugin(id)).slice(0, knobCount(controls.layout));
      const knobs = controls.controls.filter(c => c.kind === 'knob');
      const mapped = new Map<string, SmartControl>();
      knobs.forEach((knob, i) => {
        const hint = hints[i];
        if (!hint) {
          mapped.set(knob.id, { ...knob, name: emptyControl(knob.id).name, mappings: [] });
          return;
        }
        const mapping: SmartMapping = {
          target: hint.target,
          min: hint.parameter.min,
          max: hint.parameter.max,
          invert: false,
          graph: LinearGraph.map(p => ({ ...p }))
        };
        const current = targetValue(track, hint.target, ctx) ?? hint.parameter.default;
        mapped.set(knob.id, {
          ...knob,
          name: hint.name,
          mappings: [mapping],
          value: Math.round(controlValueFor(mapping, current) * 1000) / 1000
        });
      });
      return withControls(project, track, {
        ...controls,
        controls: controls.controls.map(c => mapped.get(c.id) ?? c)
      });
    }
    case 'compareSmartControls': {
      const track = findTrack(project, command.trackId);
      const controls = setUp(track);
      if (controls.comparing === command.on) {
        return project;
      }
      let after = project;
      const swapped = controls.controls.map(control => {
        if (control.savedValue === null || control.savedValue === undefined) {
          return control;
        }
        const next = { ...control, value: control.savedValue, savedValue: control.value };
        after = applyControl(after, track, next, next.value, ctx);
        return next;
      });
      return withControls(after, track, { ...controls, controls: swapped, comparing: command.on });
    }
    default:
      return undefined;
  }
}

/**
 * Saving: every control's saved value becomes the edited one (`savedValue` null: unchanged since the save), and
 * Compare ends.
 */
export function saveSmartControls(project: Project): Project {
  return {
    ...project,
    tracks: project.tracks.map(track => {
      const controls = track.smartControls;
      if (!controls) {
        return track;
      }
      return {
        ...track,
        smartControls: {
          ...controls,
          comparing: false,
          controls: controls.controls.map(c => ({
            ...c,
            value: controls.comparing && c.savedValue !== null && c.savedValue !== undefined ? c.savedValue : c.value,
            savedValue: null
          }))
        }
      };
    })
  };
}

/** Before an edit of a control's value: the value the project was saved with is kept (once) for Compare. */
export function keepSavedValues(project: Project, command: EditCommand): Project {
  if (command.type !== 'setSmartControl' || command.value === null || command.value === undefined) {
    return project;
  }
  const track = project.tracks.find(t => t.id === command.trackId);
  const controls = track?.smartControls;
  if (!track || !controls || controls.comparing) {
    return project;
  }
  return withTrack(project, {
    ...track,
    smartControls: {
      ...controls,
      controls: controls.controls.map(c =>
        c.id === command.controlId && (c.savedValue === null || c.savedValue === undefined)
          ? { ...c, savedValue: c.value }
          : c
      )
    }
  });
}

// ---- Live Loops

function grid(project: Project): LiveLoopsGrid {
  return project.liveLoops;
}

function withGrid(project: Project, liveLoops: LiveLoopsGrid): Project {
  return { ...project, liveLoops };
}

function findScene(project: Project, id: string) {
  return grid(project).scenes.find(s => s.id === id) ?? refuse(`No scene ${id}.`);
}

function findCell(project: Project, id: string): Cell {
  return grid(project).cells.find(c => c.id === id) ?? refuse(`No cell ${id}.`);
}

/** Whether a track can hold a cell of a kind: audio on an audio track, MIDI or pattern on one that plays MIDI. */
function suits(track: Track, kind: Cell['kind']): boolean {
  if (kind === 'audio') {
    return track.kind === 'audio';
  }
  return kind === 'midi' ? playsMidi(track) : playsMidi(track) && track.kind !== 'midi';
}

/** A cell's content checked and renumbered as the cell's own (ids, track, start 0, fresh note ids). */
function cellContent(content: CellContent, cellId: string, trackId: string, ctx: Phase6Context): CellContent {
  const set = [content.audio, content.midi, content.pattern].filter(x => x !== null);
  if (set.length !== 1) {
    refuse('A cell plays exactly one of audio, MIDI or a pattern.');
  }
  if (content.audio) {
    return {
      audio: { ...structuredClone(content.audio), id: cellId, trackId, startTick: 0 },
      midi: null,
      pattern: null
    };
  }
  if (content.midi) {
    const midi = structuredClone(content.midi);
    return {
      audio: null,
      midi: {
        ...midi,
        id: cellId,
        trackId,
        startTick: 0,
        notes: midi.notes.map(n => ({ ...n, id: ctx.noteId() })),
        controls: midi.controls.map(e => ({ ...e, id: ctx.noteId() }))
      },
      pattern: null
    };
  }
  const pattern = content.pattern as PatternRegion;
  return {
    audio: null,
    midi: null,
    pattern: { ...structuredClone(pattern), pattern: checkPattern(pattern.pattern), id: cellId, trackId, startTick: 0 }
  };
}

function kindOf(content: CellContent): Cell['kind'] {
  return content.audio ? 'audio' : content.midi ? 'midi' : 'pattern';
}

/** A content's natural length in ticks (an audio region's needs the project's tempo). */
function contentLength(content: CellContent, project: Project): number {
  if (content.midi) {
    return content.midi.lengthTicks;
  }
  if (content.pattern) {
    return content.pattern.lengthTicks;
  }
  const audio = content.audio;
  if (!audio) {
    return 4 * Ppq;
  }
  const tempo = project.audioFiles.find(f => f.id === audio.fileId)?.tempo?.bpm ?? project.settings.tempo;
  const beats = (audio.length / project.sampleRate) * (tempo / 60);
  return Math.max(Ppq, Math.round(beats) * Ppq);
}

/** Sets the cell of (track, scene): creates it from content, or changes it. */
export function setCell(project: Project, command: Command<'setCell'>, ctx: Phase6Context): Project {
  const track = findTrack(project, command.trackId);
  findScene(project, command.sceneId);
  const existing = grid(project).cells.find(c => c.trackId === track.id && c.sceneId === command.sceneId);
  if (!existing && !command.content) {
    refuse('A new cell needs content.');
  }
  const id = existing?.id ?? ctx.id('cel');
  let cell: Cell = existing ?? {
    id,
    trackId: track.id,
    sceneId: command.sceneId,
    kind: 'midi',
    name: track.name,
    lengthTicks: 4 * Ppq,
    playMode: 'loop',
    quantize: null,
    content: { audio: null, midi: null, pattern: null }
  };
  if (command.content) {
    const content = cellContent(command.content, id, track.id, ctx);
    const kind = kindOf(content);
    if (!suits(track, kind)) {
      refuse(`${track.name} cannot hold a ${kind} cell.`);
    }
    const name = content.audio?.name ?? content.midi?.name ?? content.pattern?.name ?? cell.name;
    cell = { ...cell, kind, content, name, lengthTicks: contentLength(content, project) };
  }
  if (command.name !== null && command.name !== undefined) {
    cell = { ...cell, name: command.name.trim() || cell.name };
  }
  if (command.lengthTicks !== null && command.lengthTicks !== undefined) {
    if (!(command.lengthTicks > 0)) {
      refuse('A cell needs a length.');
    }
    cell = { ...cell, lengthTicks: Math.round(command.lengthTicks) };
  }
  if (command.playMode) {
    cell = { ...cell, playMode: command.playMode };
  }
  if (command.gridQuantize) {
    cell = { ...cell, quantize: null };
  } else if (command.quantize) {
    cell = { ...cell, quantize: command.quantize };
  }
  const cells = existing
    ? grid(project).cells.map(c => (c.id === existing.id ? cell : c))
    : [...grid(project).cells, cell];
  return withGrid(project, { ...grid(project), cells });
}

function applyLiveLoopsCommand(project: Project, command: EditCommand, ctx: Phase6Context): Project | undefined {
  const g = grid(project);
  switch (command.type) {
    case 'addScene': {
      const scene = { id: ctx.id('scn'), name: command.name?.trim() || `Scene ${g.scenes.length + 1}` };
      const scenes = [...g.scenes];
      scenes.splice(clamp(command.index ?? scenes.length, 0, scenes.length), 0, scene);
      return withGrid(project, { ...g, scenes });
    }
    case 'removeScene':
      findScene(project, command.sceneId);
      return withGrid(project, {
        ...g,
        scenes: g.scenes.filter(s => s.id !== command.sceneId),
        cells: g.cells.filter(c => c.sceneId !== command.sceneId)
      });
    case 'setScene': {
      const scene = findScene(project, command.sceneId);
      const next = { ...scene, name: command.name?.trim() || scene.name };
      const scenes = g.scenes.filter(s => s.id !== scene.id);
      const index = command.index ?? g.scenes.indexOf(scene);
      scenes.splice(clamp(index, 0, scenes.length), 0, next);
      return withGrid(project, { ...g, scenes });
    }
    case 'setCell':
      return setCell(project, command, ctx);
    case 'deleteCells':
      command.cellIds.forEach(id => findCell(project, id));
      return withGrid(project, { ...g, cells: g.cells.filter(c => !command.cellIds.includes(c.id)) });
    case 'moveCell': {
      const cell = findCell(project, command.cellId);
      const track = findTrack(project, command.trackId);
      findScene(project, command.sceneId);
      if (!suits(track, cell.kind)) {
        refuse(`${track.name} cannot hold a ${cell.kind} cell.`);
      }
      const id = command.copy ? ctx.id('cel') : cell.id;
      const moved: Cell = {
        ...structuredClone(cell),
        id,
        trackId: track.id,
        sceneId: command.sceneId,
        content: cellContent(cell.content, id, track.id, ctx)
      };
      const cells = g.cells.filter(
        c => !(c.trackId === track.id && c.sceneId === command.sceneId) && (command.copy || c.id !== cell.id)
      );
      return withGrid(project, { ...g, cells: [...cells, moved] });
    }
    case 'setLiveLoops': {
      let next = withGrid(project, {
        ...g,
        quantize: command.quantize ?? g.quantize,
        shown: command.shown ?? g.shown
      });
      // Remix FX: on the master by default when the grid is first shown.
      if (command.shown && !g.shown && !project.master.inserts.some(i => i.pluginId === RemixFxId)) {
        const remix = ctx.plugin(RemixFxId);
        if (remix) {
          next = {
            ...next,
            master: {
              ...next.master,
              inserts: [
                ...next.master.inserts,
                {
                  id: ctx.id('ins'),
                  pluginId: remix.id,
                  name: remix.name,
                  bypassed: false,
                  parameters: defaultParameters(remix),
                  sidechain: null
                }
              ]
            }
          };
        }
      }
      return next;
    }
    case 'setTrackActivation': {
      command.trackIds.forEach(id => findTrack(project, id));
      return {
        ...project,
        tracks: project.tracks.map(t =>
          command.trackIds.includes(t.id) ? { ...t, liveLoopsActive: command.liveLoopsActive } : t
        )
      };
    }
    default:
      return undefined;
  }
}

/**
 * The Phase 6 commands (engine/src/Anode.Contracts/Commands.cs) on a snapshot: pattern regions, the Drum Machine
 * Designer, Smart Controls and Live Loops, or a 422 for one that does not apply. Undefined for any other command.
 */
export function applyPhase6Command(project: Project, command: EditCommand, ctx: Phase6Context): Project | undefined {
  switch (command.type) {
    case 'addPatternRegion': {
      const track = findTrack(project, command.trackId);
      if (!playsMidi(track) || track.kind === 'midi') {
        refuse(`${track.name} is not an instrument track: pattern regions play instruments.`);
      }
      if (!(command.startTick >= 0)) {
        refuse('A region start must not be negative.');
      }
      const length = command.lengthTicks ?? barTicksAt(project, command.startTick);
      if (!(length > 0)) {
        refuse('A pattern region needs a length.');
      }
      const region: PatternRegion = {
        id: ctx.id('pat'),
        trackId: track.id,
        name: command.name?.trim() || 'Pattern',
        startTick: Math.round(command.startTick),
        lengthTicks: Math.round(length),
        muted: false,
        pattern: command.pattern ? checkPattern(command.pattern) : defaultPattern(track, ctx)
      };
      return { ...project, patternRegions: [...project.patternRegions, region] };
    }
    case 'setPatternRegion': {
      const region = findPatternRegion(project, command.regionId);
      if (command.startTick !== null && command.startTick !== undefined && !(command.startTick >= 0)) {
        refuse('A region start must not be negative.');
      }
      if (command.lengthTicks !== null && command.lengthTicks !== undefined && !(command.lengthTicks > 0)) {
        refuse('A pattern region needs a length.');
      }
      return withPatternRegion(project, {
        ...region,
        startTick: Math.round(command.startTick ?? region.startTick),
        lengthTicks: Math.round(command.lengthTicks ?? region.lengthTicks),
        name: command.name?.trim() || region.name,
        muted: command.muted ?? region.muted
      });
    }
    case 'setPattern': {
      const region = findPatternRegion(project, command.regionId);
      return withPatternRegion(project, { ...region, pattern: checkPattern(command.pattern) });
    }
    case 'reseedPattern': {
      const region = findPatternRegion(project, command.regionId);
      return withPatternRegion(project, {
        ...region,
        pattern: { ...region.pattern, seed: command.seed ?? ctx.seed() }
      });
    }
    case 'convertPatternToMidi': {
      const region = findPatternRegion(project, command.regionId);
      const midi: MidiRegion = {
        id: ctx.id('mid'),
        trackId: region.trackId,
        name: region.name,
        startTick: region.startTick,
        lengthTicks: region.lengthTicks,
        playback: { ...NeutralPlayback },
        notes: patternNotes(project, region, region.startTick, ctx),
        controls: [],
        muted: region.muted
      };
      return {
        ...project,
        patternRegions: project.patternRegions.filter(r => r.id !== region.id),
        midiRegions: [...project.midiRegions, midi]
      };
    }
    case 'createDrumMachine': {
      const kit = command.kitPatchId ? (ctx.kit(command.kitPatchId) ?? refuse(`No kit ${command.kitPatchId}.`)) : null;
      let dmd: Track;
      let tracks: Track[];
      if (command.trackId) {
        const source = findTrack(project, command.trackId);
        if (source.kind !== 'instrument') {
          refuse(`${source.name} is not an instrument track.`);
        }
        if (source.parentId) {
          refuse('Stacks do not nest: take the track out of its stack first.');
        }
        dmd = {
          ...source,
          kind: 'stack',
          name: command.name?.trim() || source.name,
          instrument: null,
          strip: { ...source.strip, midiEffects: [] },
          drumMachine: { pads: [] },
          recordArmed: false
        };
        tracks = project.tracks.map(t => (t.id === source.id ? dmd : t));
      } else {
        const count = project.tracks.length;
        dmd = {
          ...blankTrack(
            ctx.id('trk'),
            command.name?.trim() || 'Drum Machine',
            'stack',
            TrackColors[count % TrackColors.length] ?? '#e5554e'
          ),
          drumMachine: { pads: [] }
        };
        tracks = [...project.tracks, dmd];
      }
      const members = (kit?.pads ?? []).map(pad => {
        const plugin = instrumentPlugin(ctx, pad.pluginId);
        return { pad, track: padTrack(dmd, pad.name, plugin, ctx, pad.parameters) };
      });
      dmd = {
        ...dmd,
        drumMachine: {
          pads: sortedPads(members.map(m => ({ note: m.pad.note, name: m.pad.name, trackId: m.track.id })))
        }
      };
      const withDmd = { ...project, tracks: tracks.map(t => (t.id === dmd.id ? dmd : t)) };
      return {
        ...withDmd,
        tracks: insertMembers(
          withDmd,
          dmd.id,
          members.map(m => m.track)
        )
      };
    }
    case 'addDrumPad': {
      const dmd = drumMachineTrack(project, command.trackId);
      checkNote(command.note);
      if (dmd.drumMachine.pads.some(p => p.note === command.note)) {
        refuse(`${dmd.name} already has a pad on note ${command.note}.`);
      }
      const plugin = instrumentPlugin(ctx, command.pluginId ?? DrumSynthId);
      const name = command.name?.trim() || `Pad ${command.note}`;
      const member = padTrack(dmd, name, plugin, ctx);
      const next = withTrack(project, {
        ...dmd,
        drumMachine: { pads: sortedPads([...dmd.drumMachine.pads, { note: command.note, name, trackId: member.id }]) }
      });
      return { ...next, tracks: insertMembers(next, dmd.id, [member]) };
    }
    case 'setDrumPad': {
      const dmd = drumMachineTrack(project, command.trackId);
      const pad = dmd.drumMachine.pads.find(p => p.note === command.note) ?? refuse(`No pad on note ${command.note}.`);
      let next = project;
      let padNext = pad;
      if (command.toNote !== null && command.toNote !== undefined && command.toNote !== pad.note) {
        checkNote(command.toNote);
        if (dmd.drumMachine.pads.some(p => p.note === command.toNote)) {
          refuse(`${dmd.name} already has a pad on note ${command.toNote}.`);
        }
        padNext = { ...padNext, note: command.toNote };
      }
      const name = command.name?.trim();
      if (name) {
        padNext = { ...padNext, name };
        const member = findTrack(next, pad.trackId);
        next = withTrack(next, { ...member, name });
      }
      if (command.pluginId) {
        const plugin = instrumentPlugin(ctx, command.pluginId);
        const member = findTrack(next, pad.trackId);
        next = withTrack(next, {
          ...member,
          instrument: { pluginId: plugin.id, name: plugin.name, parameters: defaultParameters(plugin), file: null }
        });
      }
      const pads = sortedPads(dmd.drumMachine.pads.map(p => (p.note === pad.note ? padNext : p)));
      return withTrack(next, { ...findTrack(next, dmd.id), drumMachine: { pads } });
    }
    case 'removeDrumPad': {
      const dmd = drumMachineTrack(project, command.trackId);
      const pad = dmd.drumMachine.pads.find(p => p.note === command.note) ?? refuse(`No pad on note ${command.note}.`);
      const next = withTrack(project, {
        ...dmd,
        drumMachine: { pads: dmd.drumMachine.pads.filter(p => p.note !== pad.note) }
      });
      return {
        ...next,
        tracks: next.tracks.filter(t => t.id !== pad.trackId),
        automation: next.automation.filter(l => l.trackId !== pad.trackId)
      };
    }
    default:
      return applySmartControlsCommand(project, command, ctx) ?? applyLiveLoopsCommand(project, command, ctx);
  }
}

/** Whether a command is one of Phase 6's (handled by {@link applyPhase6Command}). */
export function isPhase6Command(command: EditCommand): boolean {
  return Phase6Commands.has(command.type);
}

const Phase6Commands = new Set<string>([
  'addPatternRegion',
  'setPatternRegion',
  'setPattern',
  'convertPatternToMidi',
  'reseedPattern',
  'createDrumMachine',
  'addDrumPad',
  'setDrumPad',
  'removeDrumPad',
  'setSmartControls',
  'setSmartControl',
  'setSmartMappings',
  'autoMapSmartControls',
  'compareSmartControls',
  'addScene',
  'removeScene',
  'setScene',
  'setCell',
  'deleteCells',
  'moveCell',
  'setLiveLoops',
  'setTrackActivation'
]);

/** A Smart Control's mapping as automatic mapping or a new manual mapping starts it: the parameter's range, linear. */
export function newMapping(target: string, min: number, max: number): SmartMapping {
  return { target, min, max, invert: false, graph: LinearGraph.map(p => ({ ...p })) };
}

/** The kind a Smart Control id has (re-exported for the fake's tests). */
export { controlKind };

/** Writes points into a lane (for performance recording: Remix FX moves). */
export { setLane };

/** `loadInsertFile`: a file for an insert whose plugin takes one (the convolution reverb's impulse response). */
export type InsertFileCommand = Extract<EditCommand, { type: 'loadInsertFile' }>;

/** Loads a file into an insert whose plugin takes files (the convolution reverb's IR); 422 otherwise. */
export function loadInsertFile(
  project: Project,
  command: InsertFileCommand,
  plugin: (id: string) => PluginInfo | undefined
): Project {
  const strip = command.trackId ? findTrack(project, command.trackId).strip : project.master;
  const insert = strip.inserts.find(i => i.id === command.insertId) ?? refuse(`No insert ${command.insertId}.`);
  const types = plugin(insert.pluginId)?.fileTypes ?? [];
  const path = command.path.trim();
  if (!types.some(t => path.toLowerCase().endsWith(t.toLowerCase()))) {
    refuse(
      types.length ? `${insert.name} loads ${types.join(', ')} files, not ${path}.` : `${insert.name} loads no files.`
    );
  }
  const next: ChannelStrip = {
    ...strip,
    inserts: strip.inserts.map(i => (i.id === insert.id ? { ...i, file: path } : i))
  };
  return command.trackId
    ? withTrack(project, { ...findTrack(project, command.trackId), strip: next })
    : { ...project, master: next };
}
