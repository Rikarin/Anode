import {
  type DelayTap,
  type EnvironmentCable,
  type EnvironmentObject,
  type EnvironmentObjectKind,
  EnvironmentObjectKinds,
  type EnvironmentParameter,
  type LiveMidiEvent,
  type MidiEnvironment,
  type TransformCondition,
  type TransformOperation
} from '@anode/api-client';

/*
 * The Environment's rules (docs/12-phase-11.md, ADR-0015), shared by the window's graph store and the fake Server so
 * the editor refuses what the Server would: cables between inlets and outlets that exist on their kinds, no duplicate
 * cable, no cycle; at most one physical input without a port and one sequencer input; parameters in their ranges;
 * the transformer's conditions and operations on status, channel, pitch and velocity only.
 */

/** The most taps a delay line has, and its longest delay (a whole note at 960 PPQ). */
export const MaxTaps = 16;
export const MaxTapTicks = 3840;
/** The most events a monitor shows (`EnvironmentMonitorEvent`). */
export const MonitorEvents = 8;

/** The arpeggiator's and the chord memoriser's MIDI FX, whose parameters they use (the plugin catalogue lists them). */
export const EnvironmentEffectIds: Partial<Record<EnvironmentObjectKind, string>> = {
  arpeggiator: 'anode.arpeggiator',
  chordMemorizer: 'anode.chord-trigger'
};

/** The fields a transformer's conditions test, and its operations change. */
export const ConditionFields = ['status', 'channel', 'pitch', 'velocity'] as const;
export const OperationFields = ['channel', 'pitch', 'velocity'] as const;
/** The operations a transformer cannot run: they need a position in time. */
const TimeOperations = new Set(['crescendo', 'relativeCrescendo']);

/** The fader's messages (`message` 0…3). */
export const FaderMessages = ['Control Change', 'Note', 'Program Change', 'Pitch Bend'] as const;

/** A port id for an inlet (`in0`) or an outlet (`out0`) on a node. */
export function portId(direction: 'in' | 'out', index: number): string {
  return `${direction}${index}`;
}

/** The inlet or outlet index of a port id, or null. */
export function portIndex(id: string, direction: 'in' | 'out'): number | null {
  const match = /^(in|out)(\d+)$/.exec(id);
  return match?.[1] === direction ? Number(match[2]) : null;
}

/** An object kind's numeric parameters: the kind's own, or (arpeggiator, chord memoriser) its MIDI FX's. */
export function parameterSpecs(
  kind: EnvironmentObjectKind,
  effectParameters: (pluginId: string) => readonly EnvironmentParameter[] = () => []
): readonly EnvironmentParameter[] {
  const effect = EnvironmentEffectIds[kind];
  return effect ? effectParameters(effect) : EnvironmentObjectKinds[kind].parameters;
}

/** A kind's parameters at their defaults, with `given` merged over them; a problem when one is unknown or out of range. */
export function mergeParameters(
  kind: EnvironmentObjectKind,
  current: Readonly<Record<string, number>>,
  given: Readonly<Record<string, number>> | null | undefined,
  specs: readonly EnvironmentParameter[]
): { parameters: Record<string, number> } | { problem: string } {
  const parameters: Record<string, number> = {};
  for (const spec of specs) {
    parameters[spec.id] = current[spec.id] ?? spec.default;
  }
  for (const [id, value] of Object.entries(given ?? {})) {
    const spec = specs.find(s => s.id === id);
    if (!spec) {
      return { problem: `${EnvironmentObjectKinds[kind].name} has no parameter ${id}.` };
    }
    if (!Number.isFinite(value) || value < spec.min || value > spec.max) {
      return { problem: `${spec.name} is ${spec.min} to ${spec.max}.` };
    }
    parameters[id] = value;
  }
  return { parameters };
}

/** Why a cable from `from`'s outlet to `to`'s inlet cannot be made, or null when it can. */
export function cableProblem(
  environment: MidiEnvironment,
  fromObjectId: string,
  outlet: number,
  toObjectId: string,
  inlet: number
): string | null {
  const from = environment.objects.find(o => o.id === fromObjectId);
  const to = environment.objects.find(o => o.id === toObjectId);
  if (!from || !to) {
    return `No object ${!from ? fromObjectId : toObjectId}.`;
  }
  const outlets = EnvironmentObjectKinds[from.kind].outlets;
  const inlets = EnvironmentObjectKinds[to.kind].inlets;
  if (!Number.isInteger(outlet) || outlet < 0 || outlet >= outlets.length) {
    return `${from.name} has no outlet ${outlet + 1}.`;
  }
  if (!Number.isInteger(inlet) || inlet < 0 || inlet >= inlets.length) {
    return `${to.name} has no inlet ${inlet + 1}.`;
  }
  if (
    environment.cables.some(
      c => c.fromObjectId === fromObjectId && c.outlet === outlet && c.toObjectId === toObjectId && c.inlet === inlet
    )
  ) {
    return 'That cable is already there.';
  }
  if (fromObjectId === toObjectId || reaches(environment.cables, toObjectId, fromObjectId)) {
    return `A cable from ${from.name} to ${to.name} would make a loop.`;
  }
  return null;
}

/** Whether cables lead from one object to another. */
export function reaches(cables: readonly EnvironmentCable[], fromId: string, toId: string): boolean {
  const seen = new Set<string>();
  const queue = [fromId];
  while (queue.length) {
    const id = queue.shift() as string;
    if (id === toId) {
      return true;
    }
    if (seen.has(id)) {
      continue;
    }
    seen.add(id);
    for (const cable of cables) {
      if (cable.fromObjectId === id) {
        queue.push(cable.toObjectId);
      }
    }
  }
  return false;
}

/** Why an object of `kind` (with `port`) cannot join the Environment (its single objects), or null. */
export function kindLimitProblem(
  environment: MidiEnvironment,
  kind: EnvironmentObjectKind,
  port: string | null | undefined,
  exceptId: string | null = null
): string | null {
  const others = environment.objects.filter(o => o.id !== exceptId);
  if (kind === 'sequencerInput' && others.some(o => o.kind === 'sequencerInput')) {
    return 'The Environment has a Sequencer Input already.';
  }
  if (kind === 'physicalInput' && !port && others.some(o => o.kind === 'physicalInput' && !o.port)) {
    return 'The Environment has a Physical Input for every input already: give this one a port.';
  }
  return null;
}

/** Why a transformer's conditions or operations cannot be used, or null. */
export function transformProblem(
  conditions: readonly TransformCondition[] | null | undefined,
  operations: readonly TransformOperation[] | null | undefined
): string | null {
  for (const condition of conditions ?? []) {
    if (!(ConditionFields as readonly string[]).includes(condition.field)) {
      return `A transformer's conditions test status, channel, pitch and velocity, not ${condition.field}.`;
    }
  }
  for (const operation of operations ?? []) {
    if (!(OperationFields as readonly string[]).includes(operation.field)) {
      return `A transformer's operations change channel, pitch and velocity, not ${operation.field}.`;
    }
    if (TimeOperations.has(operation.op)) {
      return `A transformer cannot ${operation.op === 'crescendo' ? 'make a crescendo' : 'scale by position'}.`;
    }
  }
  return null;
}

/** Why a delay line's taps cannot be used, or null. */
export function tapsProblem(taps: readonly DelayTap[] | null | undefined): string | null {
  if (!taps) {
    return null;
  }
  if (taps.length > MaxTaps) {
    return `A delay line has at most ${MaxTaps} taps.`;
  }
  for (const tap of taps) {
    if (!Number.isInteger(tap.delayTicks) || tap.delayTicks < 1 || tap.delayTicks > MaxTapTicks) {
      return `A tap's delay is 1 to ${MaxTapTicks} ticks.`;
    }
    if (Math.abs(tap.transpose) > 127 || Math.abs(tap.velocity) > 127) {
      return "A tap's transpose and velocity are −127 to 127.";
    }
  }
  return null;
}

/** Why a chord memoriser's chord cannot be used, or null. */
export function chordProblem(chord: readonly number[] | null | undefined): string | null {
  if (!chord) {
    return null;
  }
  if (chord.length > 16 || chord.some(n => !Number.isInteger(n) || n < -36 || n > 36)) {
    return 'A chord is up to 16 notes, each −36 to 36 semitones from the key.';
  }
  return null;
}

// ---- events

const NoteNames = ['C', 'C♯', 'D', 'D♯', 'E', 'F', 'F♯', 'G', 'G♯', 'A', 'A♯', 'B'];

/** A note's name, as the editor's keyboards show it (C3 is middle C). */
export function noteName(pitch: number): string {
  return `${NoteNames[pitch % 12] ?? ''}${Math.floor(pitch / 12) - 2}`;
}

/** A monitored event as a monitor shows it ("Note On C3 100 · 1"). */
export function describeEvent(event: { status: number; data1: number; data2: number }): string {
  const kind = event.status & 0xf0;
  const channel = (event.status & 0x0f) + 1;
  switch (kind) {
    case 0x90:
      return event.data2 > 0
        ? `Note On ${noteName(event.data1)} ${event.data2} · ${channel}`
        : `Note Off ${noteName(event.data1)} · ${channel}`;
    case 0x80:
      return `Note Off ${noteName(event.data1)} · ${channel}`;
    case 0xa0:
      return `Poly Pressure ${noteName(event.data1)} ${event.data2} · ${channel}`;
    case 0xb0:
      return `CC ${event.data1} ${event.data2} · ${channel}`;
    case 0xc0:
      return `Program ${event.data1} · ${channel}`;
    case 0xd0:
      return `Pressure ${event.data1} · ${channel}`;
    case 0xe0:
      return `Pitch Bend ${((event.data2 << 7) | event.data1) - 8192} · ${channel}`;
    default:
      return `${event.status.toString(16).toUpperCase()} ${event.data1} ${event.data2}`;
  }
}

/** What an on-screen fader sends into itself when moved to `value` (0…127), by its message, channel and number. */
export function faderEvent(parameters: Readonly<Record<string, number>>, value: number): LiveMidiEvent {
  const v = Math.round(Math.min(127, Math.max(0, value)));
  const channel = Math.min(15, Math.max(0, Math.round(parameters['channel'] ?? 1) - 1));
  const number = Math.round(parameters['number'] ?? 7);
  switch (Math.round(parameters['message'] ?? 0)) {
    case 1:
      return { status: 0x90 | channel, data1: number, data2: Math.max(1, v) };
    case 2:
      return { status: 0xc0 | channel, data1: v, data2: 0 };
    case 3: {
      // The fader's value is the bend's coarse half: 64 is the centre.
      const bend = Math.min(16383, v * 128);
      return { status: 0xe0 | channel, data1: bend & 0x7f, data2: bend >> 7 };
    }
    default:
      return { status: 0xb0 | channel, data1: number, data2: v };
  }
}

/** The value (0…127) an event moves a fader to, when it is the fader's message, channel and number; else null. */
export function faderValue(parameters: Readonly<Record<string, number>>, event: LiveMidiEvent): number | null {
  const kind = event.status & 0xf0;
  const channel = (event.status & 0x0f) + 1;
  if (channel !== Math.round(parameters['channel'] ?? 1)) {
    return null;
  }
  const number = Math.round(parameters['number'] ?? 7);
  switch (Math.round(parameters['message'] ?? 0)) {
    case 1:
      return kind === 0x90 && event.data1 === number ? event.data2 : null;
    case 2:
      return kind === 0xc0 ? event.data1 : null;
    case 3:
      return kind === 0xe0 ? event.data2 : null;
    default:
      return kind === 0xb0 && event.data1 === number ? event.data2 : null;
  }
}

/** A fader's raw value (0…127) scaled to its range (`min`…`max`), as it sends it. */
export function faderOutput(parameters: Readonly<Record<string, number>>, value: number): number {
  const min = parameters['min'] ?? 0;
  const max = parameters['max'] ?? 127;
  return Math.round(min + (Math.min(127, Math.max(0, value)) / 127) * (max - min));
}

function field(event: LiveMidiEvent, name: string): number {
  switch (name) {
    case 'status':
      return event.status & 0xf0;
    case 'channel':
      return (event.status & 0x0f) + 1;
    case 'pitch':
      return event.data1;
    default:
      return event.data2;
  }
}

function matches(condition: TransformCondition, value: number): boolean {
  const { a, b } = condition;
  switch (condition.op) {
    case 'equal':
      return value === a;
    case 'unequal':
      return value !== a;
    case 'lessEqual':
      return value <= a;
    case 'greaterEqual':
      return value >= a;
    case 'inside':
      return value >= Math.min(a, b) && value <= Math.max(a, b);
    case 'outside':
      return value < Math.min(a, b) || value > Math.max(a, b);
    default:
      return true;
  }
}

function operate(operation: TransformOperation, value: number): number {
  const { a, b } = operation;
  switch (operation.op) {
    case 'fix':
      return a;
    case 'add':
      return value + a;
    case 'subtract':
      return value - a;
    case 'min':
      return Math.max(value, a);
    case 'max':
      return Math.min(value, a);
    case 'flip':
      return 2 * a - value;
    case 'multiply':
      return value * a;
    case 'divide':
      return a === 0 ? value : value / a;
    case 'scale':
      return value * a + b;
    case 'range':
      return Math.min(Math.max(value, Math.min(a, b)), Math.max(a, b));
    default:
      return value;
  }
}

/** A transformer on one event: the changed event and outlet 0 when it matches every condition, else it and outlet 1. */
export function transform(
  object: Pick<EnvironmentObject, 'conditions' | 'operations'>,
  event: LiveMidiEvent
): { outlet: number; event: LiveMidiEvent } {
  if (!(object.conditions ?? []).every(c => matches(c, field(event, c.field)))) {
    return { outlet: 1, event };
  }
  let next = { ...event };
  for (const operation of object.operations ?? []) {
    const value = Math.round(operate(operation, field(next, operation.field)));
    if (operation.field === 'channel') {
      next = { ...next, status: (next.status & 0xf0) | (Math.min(16, Math.max(1, value)) - 1) };
    } else if (operation.field === 'pitch') {
      next = { ...next, data1: Math.min(127, Math.max(0, value)) };
    } else {
      next = { ...next, data2: Math.min(127, Math.max(0, value)) };
    }
  }
  return { outlet: 0, event: next };
}

/** What events reached as they flowed from an object: monitors' events, faders' values, the inputs they left by. */
export interface EnvironmentFlow {
  monitors: Map<string, LiveMidiEvent[]>;
  faders: Map<string, number>;
  /** Events for the sequencer input (the record-enabled or selected track). */
  sequencer: LiveMidiEvent[];
  /** Events for each track input's track. */
  tracks: Map<string, LiveMidiEvent[]>;
  /** Events for each MIDI port. */
  ports: Map<string, LiveMidiEvent[]>;
}

/**
 * Events entering at an object, followed through the graph without time: monitors pass and record them, transformers
 * and channel splitters pick their outlet, faders follow matching events and send their scaled value, chord memorisers
 * play their chord. Delay lines, arpeggiators and chord memorisers without a chord pass their input (the host
 * schedules their output in time; the fake Server and the window's previews do not).
 */
export function flowFrom(
  environment: MidiEnvironment,
  objectId: string,
  events: readonly LiveMidiEvent[]
): EnvironmentFlow {
  const flow: EnvironmentFlow = {
    monitors: new Map(),
    faders: new Map(),
    sequencer: [],
    tracks: new Map(),
    ports: new Map()
  };
  const objects = new Map(environment.objects.map(o => [o.id, o]));
  const push = <K>(map: Map<K, LiveMidiEvent[]>, key: K, event: LiveMidiEvent) =>
    map.set(key, [...(map.get(key) ?? []), event]);
  const send = (from: string, outlet: number, event: LiveMidiEvent, depth: number) => {
    for (const cable of environment.cables) {
      if (cable.fromObjectId === from && cable.outlet === outlet) {
        enter(cable.toObjectId, event, depth + 1);
      }
    }
  };
  const enter = (id: string, event: LiveMidiEvent, depth: number): void => {
    const object = objects.get(id);
    if (!object || depth > environment.objects.length + 1) {
      return;
    }
    switch (object.kind) {
      case 'monitor':
        push(flow.monitors, id, event);
        send(id, 0, event, depth);
        return;
      case 'fader': {
        const value = depth === 0 ? event.data2 : faderValue(object.parameters, event);
        if (value === null) {
          return;
        }
        const raw = depth === 0 && (event.status & 0xf0) === 0xc0 ? event.data1 : value;
        flow.faders.set(id, raw);
        send(id, 0, faderEvent(object.parameters, faderOutput(object.parameters, raw)), depth);
        return;
      }
      case 'transformer': {
        const out = transform(object, event);
        send(id, out.outlet, out.event, depth);
        return;
      }
      case 'channelSplitter': {
        const kind = event.status & 0xf0;
        send(id, kind >= 0x80 && kind < 0xf0 ? event.status & 0x0f : 16, event, depth);
        return;
      }
      case 'chordMemorizer': {
        const kind = event.status & 0xf0;
        if (object.chord?.length && (kind === 0x90 || kind === 0x80)) {
          for (const interval of object.chord) {
            const pitch = event.data1 + interval;
            if (pitch >= 0 && pitch <= 127) {
              send(id, 0, { ...event, data1: pitch }, depth);
            }
          }
          return;
        }
        send(id, 0, event, depth);
        return;
      }
      case 'sequencerInput':
        flow.sequencer.push(event);
        return;
      case 'trackInput':
        if (object.trackId) {
          push(flow.tracks, object.trackId, event);
        }
        return;
      case 'midiPort':
        if (object.port) {
          push(flow.ports, object.port, event);
        }
        return;
      case 'controlTarget':
        return;
      default:
        // Physical input, keyboard (their outlet), delay line and arpeggiator (their input, in no time).
        send(id, 0, object.kind === 'keyboard' ? withChannel(event, object.parameters['channel']) : event, depth);
    }
  };
  for (const event of events) {
    enter(objectId, event, 0);
  }
  return flow;
}

function withChannel(event: LiveMidiEvent, channel: number | undefined): LiveMidiEvent {
  return channel ? { ...event, status: (event.status & 0xf0) | (Math.round(channel) - 1) } : event;
}

/** A canvas spot for a new object: right of the layer's objects, on a 20 px grid. */
export function freeSpot(environment: MidiEnvironment, layerId: string | null): { x: number; y: number } {
  const objects = environment.objects.filter(o => layerId === null || o.layerId === layerId);
  if (objects.length === 0) {
    return { x: 40, y: 40 };
  }
  const right = Math.max(...objects.map(o => o.x));
  return { x: Math.round((right + 220) / 20) * 20, y: 40 };
}
