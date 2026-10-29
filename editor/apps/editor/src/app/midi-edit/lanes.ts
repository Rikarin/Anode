import type { ControlEvent, ControlKind, Lane, LaneKind, LaneSet, NewControlEvent, Note } from '@anode/api-client';

/**
 * The Step Editor's lane maths (docs/08-phase-7.md, "Step Editor"): the default lane set, each lane's value range and
 * events, and the pencil and line tools on the lane's grid.
 */

/** The set a project without lane sets shows (`LaneSet`'s documented default); its id is not stored until edited. */
export const DefaultLaneSet: LaneSet = {
  id: 'lns_default',
  name: 'Default',
  lanes: [
    { kind: 'velocity', number: null, name: 'Velocity', resolutionTicks: 240 },
    { kind: 'controlChange', number: 1, name: 'Modulation', resolutionTicks: 240 },
    { kind: 'pitchBend', number: null, name: 'Pitch Bend', resolutionTicks: 240 },
    { kind: 'channelPressure', number: null, name: 'Channel Pressure', resolutionTicks: 240 },
    { kind: 'program', number: null, name: 'Program', resolutionTicks: 240 }
  ]
};

/** The control kind a lane draws, or null for velocity and note lanes (which edit notes). */
export function laneControlKind(kind: LaneKind): ControlKind | null {
  switch (kind) {
    case 'controlChange':
      return 'controlChange';
    case 'pitchBend':
      return 'pitchBend';
    case 'channelPressure':
      return 'channelPressure';
    case 'polyPressure':
      return 'polyPressure';
    case 'program':
      return 'programChange';
    case 'velocity':
    case 'note':
      return null;
  }
}

/** A lane's value range: pitch bend −8192…8191, velocity 1…127, the rest 0…127. */
export function laneRange(kind: LaneKind): [number, number] {
  if (kind === 'pitchBend') {
    return [-8192, 8191];
  }
  return kind === 'velocity' || kind === 'note' ? [1, 127] : [0, 127];
}

/** A lane's default name (`CC 74`, `Poly Pressure C3`…). */
export function laneName(kind: LaneKind, number: number | null | undefined): string {
  switch (kind) {
    case 'velocity':
      return 'Velocity';
    case 'controlChange':
      return number === 1 ? 'Modulation' : `CC ${number ?? 0}`;
    case 'pitchBend':
      return 'Pitch Bend';
    case 'channelPressure':
      return 'Channel Pressure';
    case 'polyPressure':
      return `Poly Pressure ${number ?? 0}`;
    case 'program':
      return 'Program';
    case 'note':
      return `Note ${number ?? 0}`;
  }
}

/** Whether a controller event belongs to a lane. */
export function inLane(lane: Pick<Lane, 'kind' | 'number'>, event: Pick<ControlEvent, 'kind' | 'number'>): boolean {
  const kind = laneControlKind(lane.kind);
  if (kind === null || event.kind !== kind) {
    return false;
  }
  return kind === 'controlChange' || kind === 'polyPressure' ? event.number === (lane.number ?? 0) : true;
}

/** The notes a velocity or note lane edits: every note, or a note lane's pitch. */
export function laneNotes<T extends Pick<Note, 'pitch'>>(
  lane: Pick<Lane, 'kind' | 'number'>,
  notes: readonly T[]
): T[] {
  if (lane.kind === 'note') {
    return notes.filter(n => n.pitch === lane.number);
  }
  return lane.kind === 'velocity' ? [...notes] : [];
}

/** A point the pencil or line tool passes through: a region tick and a value. */
export interface LanePoint {
  tick: number;
  value: number;
}

function clamp(value: number, [lo, hi]: [number, number]): number {
  return Math.min(hi, Math.max(lo, value));
}

/**
 * The line tool's values: one per grid step of `resolution` ticks from the step at or after `from.tick` to the last
 * step at or before `to.tick` (either order), each the straight line's value at that step, rounded and clamped.
 */
export function lineSteps(from: LanePoint, to: LanePoint, resolution: number, range: [number, number]): LanePoint[] {
  const [a, b] = from.tick <= to.tick ? [from, to] : [to, from];
  const step = Math.max(1, Math.round(resolution));
  const first = Math.ceil(a.tick / step) * step;
  const points: LanePoint[] = [];
  for (let tick = first; tick <= b.tick; tick += step) {
    points.push({ tick, value: clamp(Math.round(lineValue(a, b, tick)), range) });
  }
  return points;
}

/** The straight line's value at `tick` (the start's value when the line has no length). */
export function lineValue(a: LanePoint, b: LanePoint, tick: number): number {
  if (b.tick === a.tick) {
    return a.value;
  }
  return a.value + ((b.value - a.value) * (tick - a.tick)) / (b.tick - a.tick);
}

/** The grid step a tick falls in (its start). */
export function stepOf(tick: number, resolution: number): number {
  const step = Math.max(1, Math.round(resolution));
  return Math.floor(tick / step) * step;
}

/** The edits a drawn set of controller points makes on a lane: the events they replace, and the new ones. */
export interface ControlDraw {
  deleteIds: number[];
  add: NewControlEvent[];
}

/**
 * Controller events for points drawn on a lane (the pencil's one point, or the line tool's steps): each point
 * replaces the lane's events in its grid step, on `channel`.
 */
export function drawControls(
  lane: Pick<Lane, 'kind' | 'number' | 'resolutionTicks'>,
  existing: readonly ControlEvent[],
  points: readonly LanePoint[],
  channel = 1
): ControlDraw {
  const kind = laneControlKind(lane.kind);
  if (kind === null || points.length === 0) {
    return { deleteIds: [], add: [] };
  }
  const steps = new Set(points.map(p => stepOf(p.tick, lane.resolutionTicks)));
  const deleteIds = existing
    .filter(e => inLane(lane, e) && steps.has(stepOf(e.tick, lane.resolutionTicks)))
    .map(e => e.id);
  const range = laneRange(lane.kind);
  const number = kind === 'controlChange' || kind === 'polyPressure' ? (lane.number ?? 0) : 0;
  const add = points.map(p => ({
    kind,
    channel,
    number,
    value: clamp(Math.round(p.value), range),
    tick: Math.max(0, Math.round(p.tick))
  }));
  return { deleteIds, add };
}

/**
 * Note velocities for a line drawn on a velocity (or note) lane: every note of the lane starting between the line's
 * ends takes the line's value at its start.
 */
export function lineVelocities(
  notes: readonly Pick<Note, 'id' | 'startTick'>[],
  from: LanePoint,
  to: LanePoint
): { id: number; velocity: number }[] {
  const [a, b] = from.tick <= to.tick ? [from, to] : [to, from];
  return notes
    .filter(n => n.startTick >= a.tick && n.startTick <= b.tick)
    .map(n => ({ id: n.id, velocity: clamp(Math.round(lineValue(a, b, n.startTick)), [1, 127]) }));
}
