import type {
  ControlAssignment,
  ControlInput,
  ControlInputKind,
  ControllerAssignments,
  ControlMessage,
  ControlMode,
  ControlTarget,
  ControlTargetKind,
  ControlTransportAction,
  ControlValueMode,
  ControlZone,
  TrackSwitchAction
} from '@anode/api-client';

/**
 * Controller assignments as the editor edits them (docs/09-phase-8.md, "Contract notes": Assignments): pure functions
 * over `ControllerAssignments` (zones of modes of assignments, ids unique across all of them), and how inputs,
 * targets and messages read in the Controller Assignments window. Every edit returns a new value the store sends as a
 * whole (`PUT /control/assignments`).
 */

export const InputKinds: readonly { value: ControlInputKind; label: string }[] = [
  { value: 'controlChange', label: 'Control Change' },
  { value: 'controlChange14', label: 'Control Change (14-bit)' },
  { value: 'nrpn', label: 'NRPN' },
  { value: 'note', label: 'Note' },
  { value: 'pitchBend', label: 'Pitch Bend' },
  { value: 'programChange', label: 'Program Change' }
];

export const TargetKinds: readonly { value: ControlTargetKind; label: string }[] = [
  { value: 'parameter', label: 'Parameter' },
  { value: 'smartControl', label: 'Smart Control' },
  { value: 'trackSwitch', label: 'Track Switch' },
  { value: 'transport', label: 'Transport' },
  { value: 'keyCommand', label: 'Key Command' },
  { value: 'zoneMode', label: 'Switch Mode' }
];

export const ValueModes: readonly { value: ControlValueMode; label: string }[] = [
  { value: 'absolute', label: 'Absolute' },
  { value: 'relativeTwosComplement', label: 'Relative (2’s complement)' },
  { value: 'relativeSignedBit', label: 'Relative (signed bit)' },
  { value: 'relativeBinaryOffset', label: 'Relative (binary offset)' },
  { value: 'toggle', label: 'Toggle' },
  { value: 'momentary', label: 'Momentary' }
];

export const TrackSwitches: readonly { value: TrackSwitchAction & string; label: string }[] = [
  { value: 'mute', label: 'Mute' },
  { value: 'solo', label: 'Solo' },
  { value: 'recordArm', label: 'Record Arm' },
  { value: 'selectNext', label: 'Select Next Track' },
  { value: 'selectPrevious', label: 'Select Previous Track' }
];

export const TransportActions: readonly { value: ControlTransportAction & string; label: string }[] = [
  { value: 'play', label: 'Play' },
  { value: 'stop', label: 'Stop' },
  { value: 'record', label: 'Record' },
  { value: 'cycle', label: 'Cycle' },
  { value: 'rewind', label: 'Rewind' },
  { value: 'forward', label: 'Forward' },
  { value: 'goToStart', label: 'Go to Beginning' }
];

/** The Smart Control knobs a selected-track assignment can drive (`smartControl:{knobN}`). */
export const SmartKnobs: readonly string[] = Array.from({ length: 8 }, (_, i) => `knob${i + 1}`);

/** The highest number an input of a kind takes (the 14-bit pair's MSB, an NRPN, a key or controller). */
export function maxNumber(kind: ControlInputKind): number {
  return kind === 'nrpn' ? 16_383 : kind === 'controlChange14' ? 31 : kind === 'pitchBend' || kind === 'programChange' ? 0 : 127;
}

/** Whether an input kind carries a number (pitch bend and program change are one per channel). */
export function hasNumber(kind: ControlInputKind): boolean {
  return maxNumber(kind) > 0;
}

/** An input as the list shows it: `CC 21 · Ch 1 · Anode In`. */
export function describeInput(input: ControlInput, portName: (id: string) => string = id => id): string {
  const what = {
    controlChange: `CC ${input.number}`,
    controlChange14: `CC ${input.number}/${input.number + 32}`,
    nrpn: `NRPN ${input.number}`,
    note: `Note ${noteName(input.number)}`,
    pitchBend: 'Pitch Bend',
    programChange: 'Program'
  }[input.kind];
  const channel = input.channel == null ? 'Any Ch' : `Ch ${input.channel}`;
  const port = input.port == null ? 'Any port' : portName(input.port);
  return `${what} · ${channel} · ${port}`;
}

/** A raw message as the Learn banner shows it: `CC 21 = 64 · Ch 1`. */
export function describeMessage(message: ControlMessage): string {
  const channel = (message.status & 0x0f) + 1;
  switch (message.status & 0xf0) {
    case 0xb0:
      return `CC ${message.data1} = ${message.data2} · Ch ${channel}`;
    case 0x90:
      return message.data2 > 0
        ? `Note ${noteName(message.data1)} on, velocity ${message.data2} · Ch ${channel}`
        : `Note ${noteName(message.data1)} off · Ch ${channel}`;
    case 0x80:
      return `Note ${noteName(message.data1)} off · Ch ${channel}`;
    case 0xe0:
      return `Pitch Bend ${((message.data2 << 7) | message.data1) - 8192} · Ch ${channel}`;
    case 0xc0:
      return `Program ${message.data1} · Ch ${channel}`;
    default:
      return `Status ${message.status.toString(16).toUpperCase()}`;
  }
}

/** A key in Logic's names (C3 = 60). */
export function noteName(key: number): string {
  const names = ['C', 'C♯', 'D', 'D♯', 'E', 'F', 'F♯', 'G', 'G♯', 'A', 'A♯', 'B'];
  return `${names[key % 12] ?? ''}${Math.floor(key / 12) - 2}`;
}

/** Every id in use (zones, modes and assignments share one space). */
export function idsOf(assignments: ControllerAssignments): Set<string> {
  const ids = new Set<string>();
  for (const zone of assignments.zones) {
    ids.add(zone.id);
    for (const mode of zone.modes) {
      ids.add(mode.id);
      for (const assignment of mode.assignments) {
        ids.add(assignment.id);
      }
    }
  }
  return ids;
}

/** A new id with a prefix (`czn`, `cmd`, `cas`) that is not in use. */
export function newId(prefix: string, assignments: ControllerAssignments, random: () => number = Math.random): string {
  const used = idsOf(assignments);
  for (;;) {
    const id = `${prefix}_${Math.floor(random() * 36 ** 6)
      .toString(36)
      .padStart(6, '0')}`;
    if (!used.has(id)) {
      return id;
    }
  }
}

export interface Located {
  zone: ControlZone;
  mode: ControlMode;
  assignment: ControlAssignment;
}

export function findAssignment(assignments: ControllerAssignments, id: string | null): Located | null {
  for (const zone of assignments.zones) {
    for (const mode of zone.modes) {
      const assignment = mode.assignments.find(a => a.id === id);
      if (assignment) {
        return { zone, mode, assignment };
      }
    }
  }
  return null;
}

function mapModes(
  assignments: ControllerAssignments,
  change: (mode: ControlMode, zone: ControlZone) => ControlMode
): ControllerAssignments {
  return { zones: assignments.zones.map(zone => ({ ...zone, modes: zone.modes.map(mode => change(mode, zone)) })) };
}

/** A new assignment: a controller on any port and channel to the selected track's volume. */
export function blankAssignment(id: string, name: string | null = null): ControlAssignment {
  return {
    id,
    name,
    input: { port: null, kind: 'controlChange', channel: null, number: 0 },
    target: { kind: 'parameter', trackId: null, address: 'volume' },
    mode: 'absolute',
    min: 0,
    max: 1,
    pickup: false
  };
}

/** A zone with one mode, so there is somewhere to put assignments. */
export function blankZone(assignments: ControllerAssignments, name: string): ControlZone {
  const zoneId = newId('czn', assignments);
  const modeId = newId('cmd', { zones: [...assignments.zones, { id: zoneId, name, modes: [], activeModeId: '' }] });
  return { id: zoneId, name, modes: [{ id: modeId, name: 'Mode 1', assignments: [] }], activeModeId: modeId };
}

/** Assignments with at least one zone (a new user's are empty). */
export function withZone(assignments: ControllerAssignments): ControllerAssignments {
  return assignments.zones.length > 0 ? assignments : { zones: [blankZone(assignments, 'Zone 1')] };
}

/** Adds an assignment to a mode (the first zone's active mode when none is named). */
export function addAssignment(
  assignments: ControllerAssignments,
  assignment: ControlAssignment,
  modeId?: string | null
): ControllerAssignments {
  const base = withZone(assignments);
  const target = modeId ?? base.zones[0]?.activeModeId;
  return mapModes(base, mode =>
    mode.id === target ? { ...mode, assignments: [...mode.assignments, assignment] } : mode
  );
}

export function updateAssignment(
  assignments: ControllerAssignments,
  id: string,
  change: Partial<Omit<ControlAssignment, 'id'>>
): ControllerAssignments {
  return mapModes(assignments, mode =>
    mode.assignments.some(a => a.id === id)
      ? { ...mode, assignments: mode.assignments.map(a => (a.id === id ? { ...a, ...change } : a)) }
      : mode
  );
}

export function deleteAssignment(assignments: ControllerAssignments, id: string): ControllerAssignments {
  return mapModes(assignments, mode =>
    mode.assignments.some(a => a.id === id)
      ? { ...mode, assignments: mode.assignments.filter(a => a.id !== id) }
      : mode
  );
}

/** Moves an assignment to another mode (zones and modes are where the inspector puts it). */
export function moveAssignment(assignments: ControllerAssignments, id: string, modeId: string): ControllerAssignments {
  const found = findAssignment(assignments, id);
  if (!found || found.mode.id === modeId) {
    return assignments;
  }
  return addAssignment(deleteAssignment(assignments, id), found.assignment, modeId);
}

export function addZone(assignments: ControllerAssignments, name: string): ControllerAssignments {
  return { zones: [...assignments.zones, blankZone(assignments, name)] };
}

export function renameZone(assignments: ControllerAssignments, zoneId: string, name: string): ControllerAssignments {
  const trimmed = name.trim();
  return trimmed
    ? { zones: assignments.zones.map(z => (z.id === zoneId ? { ...z, name: trimmed } : z)) }
    : assignments;
}

/** Deletes a zone with its modes and assignments; mode switches that pointed into it go too. */
export function deleteZone(assignments: ControllerAssignments, zoneId: string): ControllerAssignments {
  const kept = { zones: assignments.zones.filter(z => z.id !== zoneId) };
  return mapModes(kept, mode => ({
    ...mode,
    assignments: mode.assignments.filter(a => !(a.target.kind === 'zoneMode' && a.target.zoneId === zoneId))
  }));
}

export function addMode(assignments: ControllerAssignments, zoneId: string, name: string): ControllerAssignments {
  const id = newId('cmd', assignments);
  return {
    zones: assignments.zones.map(z => (z.id === zoneId ? { ...z, modes: [...z.modes, { id, name, assignments: [] }] } : z))
  };
}

export function renameMode(assignments: ControllerAssignments, modeId: string, name: string): ControllerAssignments {
  const trimmed = name.trim();
  return trimmed ? mapModes(assignments, mode => (mode.id === modeId ? { ...mode, name: trimmed } : mode)) : assignments;
}

/**
 * Deletes a mode and its assignments (a zone keeps at least one mode); the zone's active mode moves to its first when
 * it was the one deleted, and mode switches to it go.
 */
export function deleteMode(assignments: ControllerAssignments, modeId: string): ControllerAssignments {
  const zones = assignments.zones.map(zone => {
    if (!zone.modes.some(m => m.id === modeId) || zone.modes.length < 2) {
      return zone;
    }
    const modes = zone.modes.filter(m => m.id !== modeId);
    return { ...zone, modes, activeModeId: zone.activeModeId === modeId ? (modes[0]?.id ?? '') : zone.activeModeId };
  });
  return mapModes({ zones }, mode => ({
    ...mode,
    assignments: mode.assignments.filter(a => !(a.target.kind === 'zoneMode' && a.target.modeId === modeId))
  }));
}

export function setActiveMode(assignments: ControllerAssignments, zoneId: string, modeId: string): ControllerAssignments {
  return {
    zones: assignments.zones.map(z =>
      z.id === zoneId && z.modes.some(m => m.id === modeId) ? { ...z, activeModeId: modeId } : z
    )
  };
}

/** The next free name of a series: `Mode 3` after `Mode 1` and `Mode 2`. */
export function nextName(base: string, taken: readonly string[]): string {
  for (let n = 1; ; n++) {
    const name = `${base} ${n}`;
    if (!taken.includes(name)) {
      return name;
    }
  }
}

/** A target changed to another kind, with that kind's defaults. */
export function retarget(target: ControlTarget, kind: ControlTargetKind, assignments: ControllerAssignments): ControlTarget {
  const trackId = target.trackId ?? null;
  switch (kind) {
    case 'parameter':
      return { kind, trackId, address: 'volume' };
    case 'smartControl':
      return { kind, trackId: null, address: 'knob1' };
    case 'trackSwitch':
      return { kind, trackId, switch: 'mute' };
    case 'transport':
      return { kind, transport: 'play' };
    case 'keyCommand':
      return { kind, keyCommandId: 'transport.play' };
    case 'zoneMode': {
      const zone = assignments.zones[0];
      return { kind, zoneId: zone?.id ?? null, modeId: zone?.modes[0]?.id ?? null };
    }
  }
}

/** Whether two targets drive the same thing (Learn's target against a clicked control). */
export function sameTarget(a: ControlTarget | null | undefined, b: ControlTarget | null | undefined): boolean {
  return JSON.stringify(normalTarget(a)) === JSON.stringify(normalTarget(b));
}

function normalTarget(target: ControlTarget | null | undefined): unknown {
  if (!target) {
    return null;
  }
  return [
    target.kind,
    target.trackId ?? null,
    target.address ?? null,
    target.switch ?? null,
    target.transport ?? null,
    target.keyCommandId ?? null,
    target.zoneId ?? null,
    target.modeId ?? null
  ];
}

/** A clickable control's learn target, as its `data-learn` attribute carries it. */
export function learnKey(target: ControlTarget): string {
  return JSON.stringify(target);
}

/** The target a `data-learn` attribute names, or null for one this editor cannot read. */
export function parseLearnKey(key: string | null | undefined): ControlTarget | null {
  if (!key) {
    return null;
  }
  try {
    const value = JSON.parse(key) as Partial<ControlTarget>;
    return typeof value.kind === 'string' ? (value as ControlTarget) : null;
  } catch {
    return null;
  }
}

/**
 * The target a click in the editor selects while learning: an element (or an ancestor) with `data-learn`, or a
 * Mixer strip's fader or pan knob (`data-learn-track` on the strip).
 */
export function learnTargetAt(element: Element | null): ControlTarget | null {
  const marked = element?.closest('[data-learn]');
  const strip = element?.closest('[data-learn-track]');
  // The innermost wins: a send inside a strip is the send.
  if (marked && (!strip || strip.contains(marked))) {
    return parseLearnKey(marked.getAttribute('data-learn'));
  }
  const trackId = strip?.getAttribute('data-learn-track');
  if (!trackId) {
    return null;
  }
  if (element?.closest('an-fader')) {
    return { kind: 'parameter', trackId, address: 'volume' };
  }
  if (element?.closest('an-pan-knob')) {
    return { kind: 'parameter', trackId, address: 'pan' };
  }
  return null;
}
