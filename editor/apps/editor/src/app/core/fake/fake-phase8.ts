import {
  type AddSurfaceRequest,
  ApiError,
  type ControlAssignment,
  type ControlInput,
  type ControllerAssignments,
  type ControlMessage,
  type ControlSurface,
  type ControlTarget,
  type ControlValueMode,
  type EditCommand,
  type LearnRequest,
  type LearnState,
  type MidiDevices,
  type OscClient,
  type OscSettings,
  type OscStatus,
  type Project,
  type ServerMessage,
  type StemKind,
  type SurfaceStatus,
  type SyncSettings,
  type SyncState
} from '@anode/api-client';

/** A command of one type. */
type Command<T extends EditCommand['type']> = Extract<EditCommand, { type: T }>;

function refuse(message: string): never {
  throw new ApiError(422, message);
}

// ---- assignments (docs/09-phase-8.md, "Contract notes": Assignments, Learn)

/** The input a raw message reads as (7-bit CC, note, pitch bend or program change; channels 1…16). */
export function inputOf(port: string, status: number, data1: number): ControlInput | null {
  const kind = status & 0xf0;
  const channel = (status & 0x0f) + 1;
  switch (kind) {
    case 0xb0:
      return { port, kind: 'controlChange', channel, number: data1 };
    case 0x90:
    case 0x80:
      return { port, kind: 'note', channel, number: data1 };
    case 0xe0:
      return { port, kind: 'pitchBend', channel, number: 0 };
    case 0xc0:
      return { port, kind: 'programChange', channel, number: 0 };
    default:
      return null;
  }
}

/** Whether an assignment's input takes a message's input (a null port or channel is any). */
export function inputMatches(input: ControlInput, message: ControlInput): boolean {
  return (
    input.kind === message.kind &&
    input.number === message.number &&
    (input.port == null || input.port === message.port) &&
    (input.channel == null || input.channel === message.channel)
  );
}

function sameInput(a: ControlInput, b: ControlInput): boolean {
  return a.kind === b.kind && a.number === b.number && (a.port ?? null) === (b.port ?? null) && (a.channel ?? null) === (b.channel ?? null);
}

/** The value mode a learned control gets: a key toggles a switch and presses a transport button; the rest is absolute. */
function learnedMode(input: ControlInput, target: ControlTarget): ControlValueMode {
  if (input.kind !== 'note') {
    return 'absolute';
  }
  return target.kind === 'parameter' || target.kind === 'smartControl' ? 'momentary' : 'toggle';
}

const ValueModes = new Set<ControlValueMode>([
  'absolute',
  'relativeTwosComplement',
  'relativeSignedBit',
  'relativeBinaryOffset',
  'toggle',
  'momentary'
]);

/** Refuses assignments the Server would: duplicate ids, bad inputs, ranges or targets, an unknown active mode. */
export function checkAssignments(assignments: ControllerAssignments): void {
  const ids = new Set<string>();
  const unique = (id: string) => {
    if (!id) {
      refuse('Every zone, mode and assignment needs an id.');
    }
    if (ids.has(id)) {
      refuse(`The id ${id} is used twice.`);
    }
    ids.add(id);
  };
  for (const zone of assignments.zones) {
    unique(zone.id);
    if (!zone.name.trim()) {
      refuse('A zone needs a name.');
    }
    if (zone.modes.length === 0) {
      refuse(`${zone.name} needs a mode.`);
    }
    if (!zone.modes.some(m => m.id === zone.activeModeId)) {
      refuse(`${zone.name}'s active mode is not one of its modes.`);
    }
    for (const mode of zone.modes) {
      unique(mode.id);
      if (!mode.name.trim()) {
        refuse('A mode needs a name.');
      }
      for (const assignment of mode.assignments) {
        unique(assignment.id);
        checkAssignment(assignment);
      }
    }
  }
}

function checkAssignment(assignment: ControlAssignment): void {
  const { input, target } = assignment;
  if (input.channel != null && (input.channel < 1 || input.channel > 16)) {
    refuse('A channel is 1 to 16.');
  }
  const max = input.kind === 'nrpn' ? 16_383 : input.kind === 'controlChange14' ? 31 : 127;
  if (!Number.isInteger(input.number) || input.number < 0 || input.number > max) {
    refuse(`The number must be 0 to ${max}.`);
  }
  if (!ValueModes.has(assignment.mode)) {
    refuse(`${assignment.mode} is not a value mode.`);
  }
  for (const value of [assignment.min, assignment.max]) {
    if (!(value >= 0 && value <= 1)) {
      refuse('A range is 0 to 1 of the target.');
    }
  }
  switch (target.kind) {
    case 'parameter':
    case 'smartControl':
      if (!target.address) {
        refuse('A parameter target needs its address.');
      }
      break;
    case 'trackSwitch':
      if (!target.switch) {
        refuse('A track switch target needs its switch.');
      }
      break;
    case 'transport':
      if (!target.transport) {
        refuse('A transport target needs its action.');
      }
      break;
    case 'keyCommand':
      if (!target.keyCommandId) {
        refuse('A key command target needs the command.');
      }
      break;
    case 'zoneMode':
      if (!target.zoneId || !target.modeId) {
        refuse('A zone mode target needs the zone and the mode.');
      }
      break;
  }
}

/** What a control move does to the project, when the fake Server can act on it. */
export interface ControlEffect {
  assignment: ControlAssignment;
  /** The target with its track resolved (the selected track for a null `trackId`). */
  target: ControlTarget;
  /** 0…1 over the target's range (min/max applied, a toggle flips on press). */
  value: number;
}

// ---- surfaces

const SurfaceKinds = new Set<ControlSurface['kind']>(['mackieControl', 'mackieControlXt', 'hui']);

/** This machine's addresses as the fake OSC server reports them. */
export const FakeOscAddresses = ['192.168.1.42', 'fe80::1c2b:3a4d:5e6f:7a8b'];

/** The models' download sizes, for the first-use phase of a stem split. */
const ModelSizes = { four: 81, six: 53 } as const;

/**
 * The fake Server's Phase 8 state, which belongs to the user settings rather than to a project (as in Logic):
 * controller assignments and Learn, control surfaces and their status, OSC and its subscribers, the editor's
 * selection, Link peers and the external sync the tests inject.
 */
export class FakeControl {
  assignments: ControllerAssignments = { zones: [] };
  learnState: LearnState = { on: false, target: null, zoneId: null, modeId: null, last: null };
  surfaces: ControlSurface[] = [];
  /** Each surface's connection and bank, by id. */
  private readonly status = new Map<string, SurfaceStatus>();
  osc: OscSettings = { enabled: false, port: 8000, clients: [] };
  /** OSC clients that sent `/anode/subscribe` this session. */
  subscribed: OscClient[] = [];
  /** The track the editor reported as selected. */
  selection: string | null = null;
  /** Ableton Link peers the fake session has, and the external clock or timecode a test plays in. */
  linkPeers = 0;
  external: { locked: boolean; tempo: number | null } = { locked: false, tempo: null };
  /** The stem models already downloaded (the first split with each downloads it). */
  readonly models = new Set<'four' | 'six'>();
  private lastSync = '';

  constructor(
    private readonly emit: (message: ServerMessage) => void,
    private readonly id: (prefix: string) => string,
    private readonly devices: () => MidiDevices
  ) {}

  // ---- assignments and Learn

  setAssignments(assignments: ControllerAssignments): ControllerAssignments {
    checkAssignments(assignments);
    this.assignments = structuredClone(assignments);
    return structuredClone(this.assignments);
  }

  startLearn(request: LearnRequest): LearnState {
    if (request.zoneId && !this.assignments.zones.some(z => z.id === request.zoneId)) {
      throw new ApiError(404, `No zone ${request.zoneId}.`);
    }
    if (request.modeId && !this.assignments.zones.some(z => z.modes.some(m => m.id === request.modeId))) {
      throw new ApiError(404, `No mode ${request.modeId}.`);
    }
    if (request.target) {
      checkAssignment({ id: 'learn', input: { kind: 'controlChange', number: 0 }, target: request.target, mode: 'absolute', min: 0, max: 1, pickup: false });
    }
    this.learnState = {
      on: true,
      target: request.target ? structuredClone(request.target) : null,
      zoneId: request.zoneId ?? null,
      modeId: request.modeId ?? null,
      last: this.learnState.on ? this.learnState.last : null
    };
    return structuredClone(this.learnState);
  }

  stopLearn(): LearnState {
    this.learnState = { on: false, target: null, zoneId: null, modeId: null, last: null };
    return structuredClone(this.learnState);
  }

  /**
   * A MIDI message from a control (the host's `controlMidi`, injected by tests). While learning it is offered
   * (`controlLearn`) and, with a target, becomes an assignment; otherwise the first matching assignment among the
   * active modes takes it, and its effect is returned for the Server to apply. Messages on a surface's port are the
   * surface's, never an assignment's.
   */
  receive(port: string, status: number, data1: number, data2: number): ControlEffect | null {
    const input = inputOf(port, status, data1);
    if (!input || this.surfaces.some(s => s.inputPort === port)) {
      return null;
    }
    if (this.learnState.on) {
      const message: ControlMessage = { port, status, data1, data2, input };
      const assignmentId = this.learnState.target ? this.learn(input, this.learnState.target) : null;
      this.learnState = { ...this.learnState, last: message };
      this.emit({ type: 'controlLearn', message, assignmentId });
      return null;
    }
    for (const zone of this.assignments.zones) {
      const mode = zone.modes.find(m => m.id === zone.activeModeId);
      const assignment = mode?.assignments.find(a => inputMatches(a.input, input));
      if (assignment) {
        return this.effectOf(assignment, status, data1, data2);
      }
    }
    return null;
  }

  /** Makes (or replaces) the assignment of `input` in the learning zone's mode; returns its id. */
  private learn(input: ControlInput, target: ControlTarget): string {
    let zone = this.assignments.zones.find(z => z.id === this.learnState.zoneId) ?? this.assignments.zones[0];
    if (!zone) {
      const modeId = this.id('cmd');
      zone = { id: this.id('czn'), name: 'Zone 1', modes: [{ id: modeId, name: 'Mode 1', assignments: [] }], activeModeId: modeId };
      this.assignments = { zones: [zone] };
    }
    const modeId = this.learnState.modeId ?? zone.activeModeId;
    const mode = zone.modes.find(m => m.id === modeId) ?? zone.modes[0];
    if (!mode) {
      refuse(`${zone.name} has no mode.`);
    }
    const existing = mode.assignments.find(a => sameInput(a.input, input));
    const assignment: ControlAssignment = {
      id: existing?.id ?? this.id('cas'),
      name: existing?.name ?? null,
      input,
      target: structuredClone(target),
      mode: learnedMode(input, target),
      min: 0,
      max: 1,
      pickup: false
    };
    const assignments = existing
      ? mode.assignments.map(a => (a.id === existing.id ? assignment : a))
      : [...mode.assignments, assignment];
    const zoneId = zone.id;
    this.assignments = {
      zones: this.assignments.zones.map(z =>
        z.id === zoneId ? { ...z, modes: z.modes.map(m => (m.id === mode.id ? { ...m, assignments } : m)) } : z
      )
    };
    return assignment.id;
  }

  /** Each toggle assignment's state (the server keeps it per assignment). */
  private readonly toggles = new Map<string, boolean>();

  private effectOf(assignment: ControlAssignment, status: number, data1: number, data2: number): ControlEffect | null {
    const kind = status & 0xf0;
    const target = { ...assignment.target, trackId: assignment.target.trackId ?? this.selection };
    const scale = (normal: number) => assignment.min + (assignment.max - assignment.min) * normal;
    switch (assignment.mode) {
      case 'toggle': {
        const press = kind === 0x90 ? data2 > 0 : kind === 0xb0 ? data2 >= 64 : false;
        if (!press) {
          return null;
        }
        const on = !(this.toggles.get(assignment.id) ?? false);
        this.toggles.set(assignment.id, on);
        return { assignment, target, value: scale(on ? 1 : 0) };
      }
      case 'momentary':
        return { assignment, target, value: scale(kind === 0x90 && data2 > 0 ? 1 : kind === 0xb0 && data2 >= 64 ? 1 : 0) };
      default: {
        // Pitch bend is 14-bit (LSB first); the rest are 7-bit values.
        const normal = kind === 0xe0 ? ((data2 << 7) | data1) / 16_383 : data2 / 127;
        return { assignment, target, value: scale(normal) };
      }
    }
  }

  // ---- surfaces

  addSurface(request: AddSurfaceRequest): ControlSurface {
    if (!SurfaceKinds.has(request.kind)) {
      refuse(`${request.kind} is not a control surface kind.`);
    }
    const devices = this.devices();
    if (!devices.inputs.some(p => p.id === request.inputPort)) {
      refuse(`No MIDI input ${request.inputPort}.`);
    }
    if (!devices.outputs.some(p => p.id === request.outputPort)) {
      refuse(`No MIDI output ${request.outputPort}.`);
    }
    if (this.surfaces.some(s => s.inputPort === request.inputPort || s.outputPort === request.outputPort)) {
      refuse('Another control surface uses this port.');
    }
    if (request.kind === 'mackieControlXt' && !this.surfaces.some(s => s.kind === 'mackieControl')) {
      refuse('Add a Mackie Control before its extenders.');
    }
    const surface: ControlSurface = { id: this.id('srf'), kind: request.kind, inputPort: request.inputPort, outputPort: request.outputPort, order: this.surfaces.length };
    this.surfaces = [...this.surfaces, surface];
    // The fake surface answers at once (the real one after its handshake or HUI ping).
    this.status.set(surface.id, { id: surface.id, connected: true, bankOffset: 0 });
    this.emitSurfaces();
    return structuredClone(surface);
  }

  removeSurface(id: string): void {
    if (!this.surfaces.some(s => s.id === id)) {
      throw new ApiError(404, `No control surface ${id}.`);
    }
    this.surfaces = this.surfaces.filter(s => s.id !== id).map((s, order) => ({ ...s, order }));
    this.status.delete(id);
    this.emitSurfaces();
  }

  reorderSurfaces(ids: readonly string[]): ControlSurface[] {
    if (ids.length !== this.surfaces.length || new Set(ids).size !== ids.length || ids.some(id => !this.surfaces.some(s => s.id === id))) {
      refuse('Give every control surface once.');
    }
    this.surfaces = ids.map((id, order) => ({ ...(this.surfaces.find(s => s.id === id) as ControlSurface), order }));
    this.emitSurfaces();
    return structuredClone(this.surfaces);
  }

  surfaceStatus(): SurfaceStatus[] {
    return this.surfaces.map(s => structuredClone(this.status.get(s.id) ?? { id: s.id, connected: false, bankOffset: 0 }));
  }

  /** A surface connects, disconnects or banks (tests): every client hears `surfaces`. */
  setSurfaceStatus(id: string, change: Partial<Omit<SurfaceStatus, 'id'>>): void {
    const current = this.status.get(id);
    if (!current) {
      throw new Error(`No control surface ${id}.`);
    }
    this.status.set(id, { ...current, ...change });
    this.emitSurfaces();
  }

  private emitSurfaces(): void {
    const surfaces = this.surfaceStatus();
    setTimeout(() => this.emit({ type: 'surfaces', surfaces }), 0);
  }

  // ---- OSC

  oscStatus(): OscStatus {
    return {
      settings: structuredClone(this.osc),
      listening: this.osc.enabled,
      addresses: this.osc.enabled ? [...FakeOscAddresses] : [],
      subscribed: this.osc.enabled ? structuredClone(this.subscribed) : []
    };
  }

  setOsc(settings: OscSettings): OscStatus {
    if (!Number.isInteger(settings.port) || settings.port < 1024 || settings.port > 65_535) {
      refuse('The OSC port must be 1024 to 65535.');
    }
    for (const client of settings.clients) {
      if (!client.host.trim()) {
        refuse('A client needs its host.');
      }
      if (!Number.isInteger(client.port) || client.port < 1 || client.port > 65_535) {
        refuse('A client port is 1 to 65535.');
      }
    }
    if (!settings.enabled || settings.port !== this.osc.port) {
      this.subscribed = [];
    }
    this.osc = structuredClone(settings);
    return this.oscStatus();
  }

  /** An OSC client sends `/anode/subscribe port` (tests). */
  oscSubscribe(client: OscClient): void {
    if (this.osc.enabled && !this.subscribed.some(c => c.host === client.host && c.port === client.port)) {
      this.subscribed = [...this.subscribed, { ...client }];
    }
  }

  // ---- sync

  syncState(project: Project | null): SyncState {
    const source = project?.settings.sync.source ?? 'internal';
    const external = source === 'midiClock' || source === 'mtc';
    return {
      source,
      locked: external ? this.external.locked : true,
      externalTempo: external && this.external.locked ? this.external.tempo : null,
      linkPeers: source === 'link' ? this.linkPeers : 0
    };
  }

  /** Reports the sync state when it changed (the host's `syncState`, at most 10 a second on the real Server). */
  publishSync(project: Project | null): void {
    const state = this.syncState(project);
    const key = JSON.stringify(state);
    if (key !== this.lastSync) {
      this.lastSync = key;
      setTimeout(() => this.emit({ type: 'sync', state }), 0);
    }
  }

  // ---- the Stem Splitter

  /** The model a split runs: six stems when guitar or piano is asked for. */
  static modelFor(stems: readonly StemKind[]): 'four' | 'six' {
    return stems.includes('guitar') || stems.includes('piano') ? 'six' : 'four';
  }

  /** What a stem split's job says at a point of its progress: the first use downloads the model first. */
  stemStage(model: 'four' | 'six', download: boolean, progress: number): string {
    if (download && progress < 0.5) {
      return `Downloading the ${model === 'six' ? 'six' : 'four'}-stem model (${ModelSizes[model]} MB)…`;
    }
    return 'Separating stems…';
  }
}

// ---- commands

/** The stems a split makes when none are named, and their order (ADR-0010). */
export const FourStems: readonly StemKind[] = ['vocals', 'drums', 'bass', 'other'];
const StemOrder: readonly StemKind[] = ['vocals', 'drums', 'bass', 'guitar', 'piano', 'other'];
export const StemNames: Record<StemKind, string> = {
  vocals: 'Vocals',
  drums: 'Drums',
  bass: 'Bass',
  other: 'Other',
  guitar: 'Guitar',
  piano: 'Piano'
};

/** The stems asked for, checked and in stem order. */
export function stemList(stems: readonly StemKind[] | null | undefined): StemKind[] {
  const list = stems ?? FourStems;
  if (list.length === 0) {
    refuse('Choose at least one stem.');
  }
  if (new Set(list).size !== list.length) {
    refuse('A stem is named twice.');
  }
  for (const stem of list) {
    if (!StemOrder.includes(stem)) {
      refuse(`${stem} is not a stem.`);
    }
  }
  return StemOrder.filter(s => list.includes(s));
}

/** The sync settings after a `setSyncSettings` (left out stays; an empty port string is "any"). */
export function applySyncSettings(
  current: SyncSettings,
  command: Command<'setSyncSettings'>,
  outputs: readonly string[],
  inputs: readonly string[]
): SyncSettings {
  const next: SyncSettings = {
    ...current,
    ...(command.source != null ? { source: command.source } : {}),
    ...(command.destinations != null ? { destinations: structuredClone(command.destinations) } : {}),
    ...(command.frameRate != null ? { frameRate: command.frameRate } : {}),
    ...(command.startOffsetSeconds != null ? { startOffsetSeconds: command.startOffsetSeconds } : {}),
    ...(command.linkStartStop != null ? { linkStartStop: command.linkStartStop } : {})
  };
  if (command.clockInPort != null) {
    next.clockInPort = command.clockInPort === '' ? null : command.clockInPort;
  }
  if (command.mtcInPort != null) {
    next.mtcInPort = command.mtcInPort === '' ? null : command.mtcInPort;
  }
  if (!['internal', 'midiClock', 'mtc', 'link'].includes(next.source)) {
    refuse(`${next.source} is not a sync source.`);
  }
  if (!['fps24', 'fps25', 'fps2997Drop', 'fps30'].includes(next.frameRate)) {
    refuse(`${next.frameRate} is not an MTC frame rate.`);
  }
  if (!(next.startOffsetSeconds >= 0 && next.startOffsetSeconds <= 86_399)) {
    refuse('The start offset is 00:00:00 to 23:59:59.');
  }
  const ports = new Set<string>();
  for (const destination of next.destinations) {
    if (!outputs.includes(destination.port)) {
      refuse(`No MIDI output ${destination.port}.`);
    }
    if (ports.has(destination.port)) {
      refuse('A destination is listed twice.');
    }
    ports.add(destination.port);
    if (!(destination.delayMs >= -50 && destination.delayMs <= 50)) {
      refuse('A destination delay is −50 to 50 ms.');
    }
  }
  for (const port of [next.clockInPort, next.mtcInPort]) {
    if (port && !inputs.includes(port)) {
      refuse(`No MIDI input ${port}.`);
    }
  }
  return next;
}
