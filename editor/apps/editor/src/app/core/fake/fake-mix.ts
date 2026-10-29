import {
  ApiError,
  type AutomationLane,
  type AutomationPoint,
  type ChannelStrip,
  type EditCommand,
  type GroupProperties,
  type Insert,
  type InstrumentSlot,
  type MixerGroup,
  type PatchInfo,
  type PluginInfo,
  type Project,
  type Send,
  type Track
} from '@anode/api-client';
import { clampParameter, defaultParameters } from '../../instruments/parameters';
import { BusCount, lowestFreeBus, parseRoute, wouldLoop } from '../../mixer/routing';
import { TrackColors } from '../../state/track-colors';

/** A command of one type. */
type Command<T extends EditCommand['type']> = Extract<EditCommand, { type: T }>;

/** A saved channel strip: what `applyPatch` puts on a track. */
export interface StoredPatch {
  info: PatchInfo;
  instrument: InstrumentSlot | null;
  inserts: Insert[];
  sends: Send[];
}

/** What the mixing commands need from the fake Server. */
export interface MixContext {
  /** A new id with this prefix (`ins_7`). */
  id(prefix: string): string;
  plugin(id: string): PluginInfo | undefined;
  patch(id: string): StoredPatch | undefined;
  /** The playhead while the transport runs (automation records then), else null. */
  playhead: number | null;
  /** The last tick recorded this pass, per lane (`trackId|target`): a move replaces what lies between. */
  pass: Map<string, number>;
}

/** Device output pairs 1…32, groups 1…32, insert slots per strip (Logic's 15). */
const OutputPairs = 32;
const GroupCount = 32;
const MaxInserts = 15;
const MinDb = -144;
const MaxDb = 6;

/** Logic's group colours, by number. */
const GroupColors = ['#e5554e', '#3d8ce8', '#4cb763', '#e9b937', '#a865d9', '#35b6d6', '#ee8a35', '#e0619f'];

/** Group 1…32's settings when it is first used: every property but sends and automation linked, as Logic starts. */
export function defaultGroup(number: number): MixerGroup {
  return {
    number,
    name: `Group ${number}`,
    color: GroupColors[(number - 1) % GroupColors.length] ?? '#e5554e',
    enabled: true,
    properties: {
      volume: true,
      mute: true,
      solo: true,
      pan: true,
      sends: false,
      recordArm: true,
      automation: false,
      selection: true
    }
  };
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}

function refuse(message: string): never {
  throw new ApiError(422, message);
}

function findTrack(project: Project, id: string | null | undefined): Track {
  return project.tracks.find(t => t.id === id) ?? refuse(`No track ${id ?? '(none)'}.`);
}

/** The strip a command names: a track's, or the master's for no track. */
function stripOf(project: Project, trackId: string | null | undefined): ChannelStrip {
  return trackId === null || trackId === undefined ? project.master : findTrack(project, trackId).strip;
}

function withStrip(project: Project, trackId: string | null | undefined, strip: ChannelStrip): Project {
  if (trackId === null || trackId === undefined) {
    return { ...project, master: strip };
  }
  return { ...project, tracks: project.tracks.map(t => (t.id === trackId ? { ...t, strip } : t)) };
}

function withTrack(project: Project, track: Track): Project {
  return { ...project, tracks: project.tracks.map(t => (t.id === track.id ? track : t)) };
}

function findInsert(strip: ChannelStrip, insertId: string): Insert {
  return strip.inserts.find(i => i.id === insertId) ?? refuse(`No insert ${insertId}.`);
}

function checkBus(bus: number): void {
  if (!Number.isInteger(bus) || bus < 1 || bus > BusCount) {
    refuse(`Buses are numbered 1 to ${BusCount}.`);
  }
}

function checkGroup(number: number): void {
  if (!Number.isInteger(number) || number < 1 || number > GroupCount) {
    refuse(`Mixer groups are numbered 1 to ${GroupCount}.`);
  }
}

/** A route that would bring an aux track's signal back to its own bus is refused, as the Server does. */
function checkLoop(project: Project, trackId: string, bus: number): void {
  if (wouldLoop(project.tracks, trackId, bus)) {
    refuse(`Routing to Bus ${bus} would make a feedback loop.`);
  }
}

/** The other tracks that follow `trackId` in `property`: members of an enabled group of its that links it. */
export function linkedTracks(project: Project, trackId: string, property: keyof GroupProperties): Track[] {
  const track = project.tracks.find(t => t.id === trackId);
  if (!track) {
    return [];
  }
  const groups = new Set(
    project.groups
      .filter(g => g.enabled && g.properties[property] && track.strip.groups.includes(g.number))
      .map(g => g.number)
  );
  if (groups.size === 0) {
    return [];
  }
  return project.tracks.filter(t => t.id !== trackId && t.strip.groups.some(g => groups.has(g)));
}

/** The range of an automation target on a strip, or a refusal when the strip has no such parameter. */
export function targetRange(
  project: Project,
  trackId: string | null | undefined,
  target: string,
  plugin: (id: string) => PluginInfo | undefined
): { min: number; max: number; parameter?: PluginInfo['parameters'][number] } {
  const strip = stripOf(project, trackId);
  const track = trackId ? findTrack(project, trackId) : null;
  if (target === 'volume' || (target.startsWith('send:') && track)) {
    if (target !== 'volume' && !strip.sends.some(s => `send:${s.id}` === target)) {
      refuse(`No send ${target.slice(5)} on ${track?.name ?? 'the master'}.`);
    }
    return { min: MinDb, max: MaxDb };
  }
  if (target === 'pan') {
    return { min: -1, max: 1 };
  }
  if (target === 'mute') {
    return { min: 0, max: 1 };
  }
  const insertMatch = /^insert:([^:]+):(.+)$/.exec(target);
  if (insertMatch?.[1] && insertMatch[2]) {
    const insert = findInsert(strip, insertMatch[1]);
    const parameter = plugin(insert.pluginId)?.parameters.find(p => p.id === insertMatch[2]);
    return parameter ? { min: parameter.min, max: parameter.max, parameter } : refuse(`No parameter ${target}.`);
  }
  const midiEffectMatch = /^midiEffect:([^:]+):(.+)$/.exec(target);
  if (midiEffectMatch?.[1] && midiEffectMatch[2]) {
    const slot =
      strip.midiEffects.find(i => i.id === midiEffectMatch[1]) ?? refuse(`No MIDI FX slot ${midiEffectMatch[1]}.`);
    const parameter = plugin(slot.pluginId)?.parameters.find(p => p.id === midiEffectMatch[2]);
    return parameter ? { min: parameter.min, max: parameter.max, parameter } : refuse(`No parameter ${target}.`);
  }
  const controlMatch = /^smartControl:(.+)$/.exec(target);
  if (controlMatch?.[1] && track) {
    if (!/^(knob([1-9]|1[0-2])|switch[12]|pad[XY])$/.test(controlMatch[1])) {
      refuse(`No Smart Control ${controlMatch[1]}.`);
    }
    return { min: 0, max: 1 };
  }
  const instrumentMatch = /^instrument:(.+)$/.exec(target);
  if (instrumentMatch?.[1] && track?.instrument) {
    const parameter = plugin(track.instrument.pluginId)?.parameters.find(p => p.id === instrumentMatch[1]);
    return parameter ? { min: parameter.min, max: parameter.max, parameter } : refuse(`No parameter ${target}.`);
  }
  return refuse(`${target} is not an automation target of ${track?.name ?? 'the master'}.`);
}

/** Replaces a lane's points (an empty list removes the lane). */
export function setLane(
  project: Project,
  trackId: string | null,
  target: string,
  points: AutomationPoint[],
  ctx: MixContext
): Project {
  const existing = project.automation.find(l => (l.trackId ?? null) === trackId && l.target === target);
  if (points.length === 0) {
    return existing ? { ...project, automation: project.automation.filter(l => l !== existing) } : project;
  }
  const lane: AutomationLane = {
    id: existing?.id ?? ctx.id('aut'),
    trackId,
    target,
    points: [...points].sort((a, b) => a.tick - b.tick)
  };
  return existing
    ? { ...project, automation: project.automation.map(l => (l === existing ? lane : l)) }
    : { ...project, automation: [...project.automation, lane] };
}

/**
 * A control move recorded into its lane: on a track in touch, latch or write while the transport runs, a point at the
 * playhead replacing what lies between the pass's previous point of that lane and now.
 */
export function record(project: Project, trackId: string, target: string, value: number, ctx: MixContext): Project {
  if (ctx.playhead === null) {
    return project;
  }
  const track = project.tracks.find(t => t.id === trackId);
  if (
    !track ||
    (track.automationMode !== 'touch' && track.automationMode !== 'latch' && track.automationMode !== 'write')
  ) {
    return project;
  }
  const key = `${trackId}|${target}`;
  const tick = Math.max(0, Math.round(ctx.playhead));
  const from = Math.min(ctx.pass.get(key) ?? tick, tick);
  const lane = project.automation.find(l => l.trackId === trackId && l.target === target);
  const kept = (lane?.points ?? []).filter(p => p.tick > tick || (p.tick <= from && p.tick !== tick));
  ctx.pass.set(key, tick);
  return setLane(project, trackId, target, [...kept, { tick, value, curve: 0 }], ctx);
}

/** Drops the lanes whose target names something that is gone (a removed send or insert). */
function dropLanes(project: Project, trackId: string | null, prefix: string): Project {
  return {
    ...project,
    automation: project.automation.filter(l => !((l.trackId ?? null) === trackId && l.target.startsWith(prefix)))
  };
}

/** `setStrip`: the strip, the members of its groups that link each property, and automation recording. */
export function applyStrip(project: Project, command: Command<'setStrip'>, ctx: MixContext): Project {
  const patch = (s: ChannelStrip): ChannelStrip => ({
    ...s,
    volumeDb: clamp(command.volumeDb ?? s.volumeDb, MinDb, MaxDb),
    pan: clamp(command.pan ?? s.pan, -1, 1),
    mute: command.mute ?? s.mute,
    solo: command.solo ?? s.solo,
    vcaId: s.vcaId ?? null
  });
  if (command.trackId === null || command.trackId === undefined) {
    return { ...project, master: patch(project.master) };
  }
  const track = findTrack(project, command.trackId);
  const next = patch(track.strip);
  let after = withTrack(project, { ...track, strip: next });
  const follow = (property: keyof GroupProperties, change: (s: ChannelStrip) => ChannelStrip) => {
    for (const member of linkedTracks(project, track.id, property)) {
      const current = findTrack(after, member.id);
      after = withTrack(after, { ...current, strip: change(current.strip) });
    }
  };
  if (command.volumeDb !== null && command.volumeDb !== undefined) {
    const delta = next.volumeDb - track.strip.volumeDb;
    follow('volume', s => ({ ...s, volumeDb: clamp(s.volumeDb + delta, MinDb, MaxDb) }));
    after = record(after, track.id, 'volume', next.volumeDb, ctx);
  }
  if (command.pan !== null && command.pan !== undefined) {
    follow('pan', s => ({ ...s, pan: next.pan }));
    after = record(after, track.id, 'pan', next.pan, ctx);
  }
  if (command.mute !== null && command.mute !== undefined) {
    follow('mute', s => ({ ...s, mute: next.mute }));
    after = record(after, track.id, 'mute', next.mute ? 1 : 0, ctx);
  }
  if (command.solo !== null && command.solo !== undefined) {
    follow('solo', s => ({ ...s, solo: next.solo }));
  }
  return after;
}

/** Record-arming a track arms the members of its groups that link record arm. */
export function linkRecordArm(project: Project, trackId: string, armed: boolean): Project {
  let after = project;
  for (const member of linkedTracks(project, trackId, 'recordArm')) {
    if (member.kind !== 'stack' && member.kind !== 'aux' && member.kind !== 'vca') {
      after = withTrack(after, { ...member, recordArmed: armed });
    }
  }
  return after;
}

function newTrack(project: Project, kind: 'aux' | 'vca', name: string, inputBus: number, ctx: MixContext): Track {
  return {
    id: ctx.id('trk'),
    name,
    kind,
    color: TrackColors[project.tracks.length % TrackColors.length] ?? '#8b97a5',
    input: { channel: 0, stereo: true },
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
    inputBus,
    automationMode: 'read',
    frozenFileId: null,
    flexOn: false,
    flexMode: 'polyphonic',
    alternatives: [],
    alternativeId: null,
    drumMachine: null,
    smartControls: null,
    liveLoopsActive: false
  };
}

function newInsert(plugin: PluginInfo, ctx: MixContext): Insert {
  return {
    id: ctx.id('ins'),
    pluginId: plugin.id,
    name: plugin.name,
    bypassed: false,
    parameters: defaultParameters(plugin),
    sidechain: null
  };
}

/** A strip that carries sends and an output: not the master, a VCA or a stack. */
function routable(project: Project, trackId: string): Track {
  const track = findTrack(project, trackId);
  if (track.kind === 'vca' || track.kind === 'stack') {
    refuse(`${track.name} has no sends or output of its own.`);
  }
  return track;
}

/**
 * The 20 mixing, automation and patch commands of Phase 4 (engine/src/Anode.Contracts/Commands.cs), applied to a
 * snapshot, or a 422 for one that does not apply. Returns undefined for any other command.
 */
export function applyMixCommand(project: Project, command: EditCommand, ctx: MixContext): Project | undefined {
  switch (command.type) {
    case 'addInsert': {
      const plugin = ctx.plugin(command.pluginId);
      if (!plugin || plugin.isInstrument) {
        refuse(`No effect ${command.pluginId} in the plugin catalogue.`);
      }
      if (plugin.category === 'MIDI Effect') {
        refuse(`${plugin.name} is a MIDI effect: it goes in a MIDI FX slot.`);
      }
      const strip = stripOf(project, command.trackId);
      if (command.trackId && findTrack(project, command.trackId).kind === 'vca') {
        refuse('A VCA has no inserts.');
      }
      if (strip.inserts.length >= MaxInserts) {
        refuse(`A channel strip has at most ${MaxInserts} inserts.`);
      }
      const inserts = [...strip.inserts];
      const index = clamp(command.index ?? inserts.length, 0, inserts.length);
      inserts.splice(index, 0, newInsert(plugin, ctx));
      return withStrip(project, command.trackId, { ...strip, inserts });
    }
    case 'removeInsert': {
      const strip = stripOf(project, command.trackId);
      findInsert(strip, command.insertId);
      const after = withStrip(project, command.trackId, {
        ...strip,
        inserts: strip.inserts.filter(i => i.id !== command.insertId)
      });
      return dropLanes(after, command.trackId ?? null, `insert:${command.insertId}:`);
    }
    case 'moveInsert': {
      const strip = stripOf(project, command.trackId);
      const insert = findInsert(strip, command.insertId);
      const inserts = strip.inserts.filter(i => i.id !== insert.id);
      inserts.splice(clamp(Math.round(command.index), 0, inserts.length), 0, insert);
      return withStrip(project, command.trackId, { ...strip, inserts });
    }
    case 'setInsert': {
      const strip = stripOf(project, command.trackId);
      const insert = findInsert(strip, command.insertId);
      let sidechain = insert.sidechain ?? null;
      if (command.sidechain !== null && command.sidechain !== undefined) {
        if (command.sidechain === '') {
          sidechain = null;
        } else {
          if (!ctx.plugin(insert.pluginId)?.hasSidechain) {
            refuse(`${insert.name} takes no sidechain.`);
          }
          const route = /^bus:(\d+)$/.exec(command.sidechain);
          if (route) {
            checkBus(Number(route[1]));
          } else if (command.sidechain === command.trackId) {
            refuse('A track cannot sidechain itself.');
          } else {
            findTrack(project, command.sidechain);
          }
          sidechain = command.sidechain;
        }
      }
      const next: Insert = { ...insert, bypassed: command.bypassed ?? insert.bypassed, sidechain };
      return withStrip(project, command.trackId, {
        ...strip,
        inserts: strip.inserts.map(i => (i.id === insert.id ? next : i))
      });
    }
    case 'setInsertParameter': {
      const strip = stripOf(project, command.trackId);
      const insert = findInsert(strip, command.insertId);
      const parameter =
        ctx.plugin(insert.pluginId)?.parameters.find(p => p.id === command.parameterId) ??
        refuse(`${insert.name} has no parameter ${command.parameterId}.`);
      if (!Number.isFinite(command.value)) {
        refuse('A parameter value must be a number.');
      }
      const value = clampParameter(parameter, command.value);
      let after = withStrip(project, command.trackId, {
        ...strip,
        inserts: strip.inserts.map(i =>
          i.id === insert.id ? { ...i, parameters: { ...i.parameters, [parameter.id]: value } } : i
        )
      });
      if (command.trackId) {
        after = record(after, command.trackId, `insert:${insert.id}:${parameter.id}`, value, ctx);
      }
      return after;
    }
    case 'addSend': {
      const track = routable(project, command.trackId);
      checkBus(command.bus);
      if (track.strip.sends.some(s => s.bus === command.bus)) {
        refuse(`${track.name} already sends to Bus ${command.bus}.`);
      }
      checkLoop(project, track.id, command.bus);
      const send: Send = {
        id: ctx.id('snd'),
        bus: command.bus,
        levelDb: clamp(command.levelDb ?? MinDb, MinDb, MaxDb),
        pan: 0,
        preFader: command.preFader ?? false
      };
      return withTrack(project, { ...track, strip: { ...track.strip, sends: [...track.strip.sends, send] } });
    }
    case 'setSend': {
      const track = routable(project, command.trackId);
      const send = track.strip.sends.find(s => s.id === command.sendId) ?? refuse(`No send ${command.sendId}.`);
      if (command.bus !== null && command.bus !== undefined && command.bus !== send.bus) {
        checkBus(command.bus);
        if (track.strip.sends.some(s => s.bus === command.bus)) {
          refuse(`${track.name} already sends to Bus ${command.bus}.`);
        }
        checkLoop(project, track.id, command.bus);
      }
      const next: Send = {
        ...send,
        bus: command.bus ?? send.bus,
        levelDb: clamp(command.levelDb ?? send.levelDb, MinDb, MaxDb),
        pan: clamp(command.pan ?? send.pan, -1, 1),
        preFader: command.preFader ?? send.preFader
      };
      let after = withTrack(project, {
        ...track,
        strip: { ...track.strip, sends: track.strip.sends.map(s => (s.id === send.id ? next : s)) }
      });
      if (command.levelDb !== null && command.levelDb !== undefined) {
        const delta = next.levelDb - send.levelDb;
        for (const member of linkedTracks(project, track.id, 'sends')) {
          const current = findTrack(after, member.id);
          after = withTrack(after, {
            ...current,
            strip: {
              ...current.strip,
              sends: current.strip.sends.map(s =>
                s.bus === send.bus ? { ...s, levelDb: clamp(s.levelDb + delta, MinDb, MaxDb) } : s
              )
            }
          });
        }
        after = record(after, track.id, `send:${send.id}`, next.levelDb, ctx);
      }
      return after;
    }
    case 'removeSend': {
      const track = routable(project, command.trackId);
      if (!track.strip.sends.some(s => s.id === command.sendId)) {
        refuse(`No send ${command.sendId}.`);
      }
      const after = withTrack(project, {
        ...track,
        strip: { ...track.strip, sends: track.strip.sends.filter(s => s.id !== command.sendId) }
      });
      return dropLanes(after, track.id, `send:${command.sendId}`);
    }
    case 'setOutput': {
      const track = routable(project, command.trackId);
      const route = parseRoute(command.output) ?? refuse(`${command.output} is not an output.`);
      if (route.kind === 'bus') {
        checkBus(route.bus);
        checkLoop(project, track.id, route.bus);
      } else if (
        route.kind === 'output' &&
        (!Number.isInteger(route.pair) || route.pair < 1 || route.pair > OutputPairs)
      ) {
        refuse(`Output pairs are numbered 1 to ${OutputPairs}.`);
      }
      return withTrack(project, { ...track, strip: { ...track.strip, output: command.output } });
    }
    case 'addAux': {
      const bus = command.bus ?? lowestFreeBus(project.tracks);
      if (bus === 0) {
        refuse('Every bus is in use.');
      }
      checkBus(bus);
      const count = project.tracks.filter(t => t.kind === 'aux').length;
      const aux = newTrack(project, 'aux', command.name?.trim() || `Aux ${count + 1}`, bus, ctx);
      return { ...project, tracks: [...project.tracks, aux] };
    }
    case 'addVca': {
      const count = project.tracks.filter(t => t.kind === 'vca').length;
      const vca = newTrack(project, 'vca', command.name?.trim() || `VCA ${count + 1}`, 0, ctx);
      return { ...project, tracks: [...project.tracks, vca] };
    }
    case 'setGroup': {
      checkGroup(command.number);
      const group = project.groups.find(g => g.number === command.number) ?? defaultGroup(command.number);
      const next: MixerGroup = {
        number: group.number,
        name: command.name?.trim() || group.name,
        color: command.color ?? group.color,
        enabled: command.enabled ?? group.enabled,
        properties: command.properties ?? group.properties
      };
      const groups = [...project.groups.filter(g => g.number !== next.number), next].sort(
        (a, b) => a.number - b.number
      );
      return { ...project, groups };
    }
    case 'deleteGroup': {
      if (!project.groups.some(g => g.number === command.number)) {
        refuse(`No group ${command.number}.`);
      }
      return {
        ...project,
        groups: project.groups.filter(g => g.number !== command.number),
        tracks: project.tracks.map(t =>
          t.strip.groups.includes(command.number)
            ? { ...t, strip: { ...t.strip, groups: t.strip.groups.filter(g => g !== command.number) } }
            : t
        )
      };
    }
    case 'setTrackGroups': {
      const track = findTrack(project, command.trackId);
      command.groups.forEach(checkGroup);
      const numbers = [...new Set(command.groups)].sort((a, b) => a - b);
      // A group is created when a track first joins it, as Logic's group slot does.
      const missing = numbers.filter(n => !project.groups.some(g => g.number === n)).map(defaultGroup);
      return {
        ...withTrack(project, { ...track, strip: { ...track.strip, groups: numbers } }),
        groups: [...project.groups, ...missing].sort((a, b) => a.number - b.number)
      };
    }
    case 'assignVca': {
      const track = findTrack(project, command.trackId);
      if (track.kind === 'vca') {
        refuse('A VCA cannot be assigned to a VCA.');
      }
      if (command.vcaId) {
        const vca = findTrack(project, command.vcaId);
        if (vca.kind !== 'vca') {
          refuse(`${vca.name} is not a VCA.`);
        }
      }
      return withTrack(project, { ...track, strip: { ...track.strip, vcaId: command.vcaId ?? null } });
    }
    case 'setAutomation': {
      const trackId = command.trackId ?? null;
      const range = targetRange(project, trackId, command.target, id => ctx.plugin(id));
      const points = command.points.map(p => {
        if (!Number.isFinite(p.tick) || !Number.isFinite(p.value) || !Number.isFinite(p.curve)) {
          refuse('An automation point needs a tick, a value and a curve.');
        }
        const value = range.parameter ? clampParameter(range.parameter, p.value) : clamp(p.value, range.min, range.max);
        return {
          tick: Math.max(0, Math.round(p.tick)),
          value: command.target === 'mute' ? (value >= 0.5 ? 1 : 0) : value,
          curve: clamp(p.curve, -1, 1)
        };
      });
      return setLane(project, trackId, command.target, points, ctx);
    }
    case 'setAutomationMode': {
      const track = command.trackId ? findTrack(project, command.trackId) : refuse('Name the track to set its mode.');
      if (track.kind === 'stack') {
        refuse('A track stack has no automation of its own.');
      }
      let after = withTrack(project, { ...track, automationMode: command.mode });
      for (const member of linkedTracks(project, track.id, 'automation')) {
        after = withTrack(after, { ...findTrack(after, member.id), automationMode: command.mode });
      }
      return after;
    }
    case 'setMixerSettings': {
      const m = project.settings.mixer;
      const limit = command.lowLatencyLimitMs ?? m.lowLatencyLimitMs;
      if (!Number.isFinite(limit) || limit < 0.5 || limit > 100) {
        refuse('The low-latency limit is 0.5 to 100 ms.');
      }
      return {
        ...project,
        settings: {
          ...project.settings,
          mixer: { pdc: command.pdc ?? m.pdc, lowLatency: command.lowLatency ?? m.lowLatency, lowLatencyLimitMs: limit }
        }
      };
    }
    case 'applyPatch': {
      const track = findTrack(project, command.trackId);
      const patch = ctx.patch(command.patchId) ?? refuse(`No patch ${command.patchId}.`);
      if (track.kind === 'stack' || track.kind === 'vca') {
        refuse(`A patch does not go on ${track.name}.`);
      }
      const inserts = patch.inserts.map(i => ({ ...structuredClone(i), id: ctx.id('ins') }));
      const sends =
        track.kind === 'aux' ? patch.sends.filter(s => !wouldLoop(project.tracks, track.id, s.bus)) : patch.sends;
      const instrument =
        track.kind === 'instrument' && patch.instrument ? structuredClone(patch.instrument) : track.instrument;
      const after = withTrack(project, {
        ...track,
        instrument,
        strip: { ...track.strip, inserts, sends: sends.map(s => ({ ...s, id: ctx.id('snd') })) }
      });
      return {
        ...after,
        automation: after.automation.filter(
          l => !(l.trackId === track.id && (l.target.startsWith('insert:') || l.target.startsWith('send:')))
        )
      };
    }
    case 'unfreezeTrack': {
      const track = findTrack(project, command.trackId);
      if (!track.frozenFileId) {
        refuse(`${track.name} is not frozen.`);
      }
      return withTrack(project, { ...track, frozenFileId: null });
    }
    default:
      return undefined;
  }
}

/** Removing a track: its automation lanes go, and nothing keeps naming it (VCA assignments, sidechains). */
export function forgetTrack(project: Project, trackId: string): Project {
  const unlink = (strip: ChannelStrip): ChannelStrip => ({
    ...strip,
    vcaId: strip.vcaId === trackId ? null : (strip.vcaId ?? null),
    inserts: strip.inserts.map(i => (i.sidechain === trackId ? { ...i, sidechain: null } : i))
  });
  return {
    ...project,
    master: unlink(project.master),
    tracks: project.tracks.map(t => ({ ...t, strip: unlink(t.strip) })),
    automation: project.automation.filter(l => l.trackId !== trackId)
  };
}

/** A track's strip as a patch: its instrument, inserts and sends. */
export function patchOf(track: Track, info: PatchInfo): StoredPatch {
  return {
    info,
    instrument: track.instrument ? structuredClone(track.instrument) : null,
    inserts: structuredClone(track.strip.inserts),
    sends: structuredClone(track.strip.sends)
  };
}
