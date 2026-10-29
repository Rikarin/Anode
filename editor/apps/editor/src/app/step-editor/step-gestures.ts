import type { EditCommand, Lane, LaneKind, LaneSet, MidiRegion, NoteChange } from '@anode/api-client';
import type { MeterMap } from '@anode/timeline';
import {
  DefaultLaneSet,
  drawControls,
  inLane,
  laneControlKind,
  laneName,
  laneNotes,
  laneRange,
  lineSteps,
  lineVelocities,
  stepOf,
  type LanePoint
} from '../midi-edit/lanes';
import { formatPosition } from '../midi-edit/position';

/**
 * The Step Editor's gestures and geometry (docs/08-phase-7.md, "Step Editor"): where a tick and a value sit in a lane
 * (on the Piano Roll's time scale), the bars a lane draws, the commands a pencil or line stroke makes, and lane set
 * edits. Pure, so the component stays a view.
 *
 * Pixel maths, for a lane body `height` pixels tall at the Piano Roll's `originTick` and `pixelsPerTick`:
 * - x = (tick − originTick) × pixelsPerTick; tick = originTick + x ÷ pixelsPerTick (project ticks);
 * - y = Pad + (1 − (value − min) ÷ (max − min)) × (height − 2 × Pad); value = min + (1 − (y − Pad) ÷ (height − 2 × Pad))
 *   × (max − min), rounded and clamped, with {@link LanePad} = 4 px (so the top 4 px are the maximum and the bottom 4
 *   px the minimum).
 */

/** The lane's inset at the top and bottom: a pointer within it draws the maximum or the minimum. */
export const LanePad = 4;
/** A lane's height and its header's width, in CSS pixels. */
export const LaneHeight = 64;
export const LaneHeaderWidth = 128;

/** The lane kinds "Add Lane" offers, in order. */
export const LaneKinds: readonly { kind: LaneKind; label: string }[] = [
  { kind: 'velocity', label: 'Velocity' },
  { kind: 'controlChange', label: 'Control Change' },
  { kind: 'pitchBend', label: 'Pitch Bend' },
  { kind: 'channelPressure', label: 'Channel Pressure' },
  { kind: 'polyPressure', label: 'Poly Pressure' },
  { kind: 'program', label: 'Program' },
  { kind: 'note', label: 'Note' }
];

/** Whether a lane kind takes a number: a CC's controller, a poly pressure's key, a note lane's pitch. */
export function needsNumber(kind: LaneKind): boolean {
  return kind === 'controlChange' || kind === 'polyPressure' || kind === 'note';
}

/** A lane's `data-testid`: `step-lane-<kind>`, with `-<number>` for kinds that take one (`step-lane-controlChange-74`). */
export function laneTestId(lane: Pick<Lane, 'kind' | 'number'>): string {
  return needsNumber(lane.kind) ? `step-lane-${lane.kind}-${lane.number ?? 0}` : `step-lane-${lane.kind}`;
}

/** The time scale a lane is drawn on: the Piano Roll's. */
export interface LaneScale {
  originTick: number;
  pixelsPerTick: number;
  height: number;
}

export function xAtTick(tick: number, scale: Pick<LaneScale, 'originTick' | 'pixelsPerTick'>): number {
  return (tick - scale.originTick) * scale.pixelsPerTick;
}

export function tickAtX(x: number, scale: Pick<LaneScale, 'originTick' | 'pixelsPerTick'>): number {
  return scale.originTick + x / scale.pixelsPerTick;
}

export function yAtValue(value: number, height: number, [min, max]: [number, number]): number {
  const usable = Math.max(1, height - 2 * LanePad);
  return LanePad + (1 - (value - min) / (max - min)) * usable;
}

export function valueAtY(y: number, height: number, [min, max]: [number, number]): number {
  const usable = Math.max(1, height - 2 * LanePad);
  const fraction = 1 - Math.min(1, Math.max(0, (y - LanePad) / usable));
  return Math.min(max, Math.max(min, Math.round(min + fraction * (max - min)))) || 0;
}

/** What a lane's bar stands for: a note (velocity) or a controller event. */
export interface BarRef {
  kind: 'note' | 'control';
  id: number;
  regionId: string;
  value: number;
}

/** A bar as the lane draws it, with what it stands for. */
export interface LaneBar {
  x: number;
  width: number;
  y: number;
  value: number;
  valueText: string;
  selected: boolean;
  ref: BarRef;
}

/**
 * The bars of a lane within `width` pixels: a 3 px bar at each note's start for velocity and note lanes; for
 * controller lanes a beam from each event to the next one, at most a grid step long.
 */
export function laneBars(
  lane: Pick<Lane, 'kind' | 'number' | 'name' | 'resolutionTicks'>,
  regions: readonly MidiRegion[],
  scale: LaneScale,
  width: number,
  meter: MeterMap,
  selected: { notes: ReadonlySet<number>; controls: ReadonlySet<number> }
): LaneBar[] {
  const range = laneRange(lane.kind);
  const bars: LaneBar[] = [];
  const text = (value: number, tick: number) => `${value} at ${formatPosition(tick, meter)}`;
  if (laneControlKind(lane.kind) === null) {
    for (const region of regions) {
      for (const note of laneNotes(lane, region.notes)) {
        const tick = region.startTick + note.startTick;
        const x = xAtTick(tick, scale);
        if (x < -4 || x > width + 4) {
          continue;
        }
        bars.push({
          x: x - 1,
          width: 3,
          y: yAtValue(note.velocity, scale.height, range),
          value: note.velocity,
          valueText: text(note.velocity, tick),
          selected: selected.notes.has(note.id),
          ref: { kind: 'note', id: note.id, regionId: region.id, value: note.velocity }
        });
      }
    }
    return bars;
  }

  const step = lane.resolutionTicks * scale.pixelsPerTick;
  for (const region of regions) {
    const events = region.controls.filter(e => inLane(lane, e)).sort((a, b) => a.tick - b.tick);
    const end = xAtTick(region.startTick + region.lengthTicks, scale);
    events.forEach((event, i) => {
      const tick = region.startTick + event.tick;
      const x = xAtTick(tick, scale);
      const next = events[i + 1];
      const nextX = next ? xAtTick(region.startTick + next.tick, scale) : end;
      const beam = Math.max(2, Math.min(nextX - x, Math.max(step, 2)) - 1);
      if (x + beam < 0 || x > width) {
        return;
      }
      bars.push({
        x,
        width: beam,
        y: yAtValue(event.value, scale.height, range),
        value: event.value,
        valueText: text(event.value, tick),
        selected: selected.controls.has(event.id),
        ref: { kind: 'control', id: event.id, regionId: region.id, value: event.value }
      });
    });
  }
  return bars;
}

/** A stroke's points as lane points: project ticks and values. */
export function strokePoints(
  points: readonly { x: number; y: number }[],
  lane: Pick<Lane, 'kind'>,
  scale: LaneScale
): LanePoint[] {
  const range = laneRange(lane.kind);
  return points.map(p => ({
    tick: Math.max(0, tickAtX(p.x, scale)),
    value: valueAtY(p.y, scale.height, range)
  }));
}

/** The pencil's points within a region (relative ticks): one per grid step it touched, the last value there. */
function pencilSteps(points: readonly LanePoint[], region: MidiRegion, resolution: number): LanePoint[] {
  const steps = new Map<number, number>();
  for (const point of points) {
    const relative = point.tick - region.startTick;
    if (relative < 0 || relative >= region.lengthTicks) {
      continue;
    }
    steps.set(stepOf(relative, resolution), point.value);
  }
  return [...steps].sort((a, b) => a[0] - b[0]).map(([tick, value]) => ({ tick, value }));
}

/** The channel a lane's new events use: its existing events' (the first one's), else 1. */
function laneChannel(lane: Pick<Lane, 'kind' | 'number'>, region: MidiRegion): number {
  return region.controls.find(e => inLane(lane, e))?.channel ?? region.notes[0]?.channel ?? 1;
}

/**
 * The commands a stroke makes (one undo step).
 * - Controller lanes: each grid step the stroke covers gets one event, replacing the lane's events in that step
 *   (`deleteControls` and `addControls` per region). The pencil sets the steps it passed; the line sets every step
 *   between its ends to the line's value there (`lineSteps`).
 * - Velocity and note lanes: the line gives every note of the lane starting between its ends the line's value at its
 *   start (`lineVelocities`); the pencil gives the notes starting in each grid step it touched the value it drew there
 *   (`editNotes` per region).
 */
export function strokeCommands(
  lane: Pick<Lane, 'kind' | 'number' | 'resolutionTicks'>,
  tool: 'pencil' | 'line',
  points: readonly LanePoint[],
  regions: readonly MidiRegion[]
): EditCommand[] {
  if (points.length === 0) {
    return [];
  }
  const range = laneRange(lane.kind);
  const first = points[0] as LanePoint;
  const last = points[points.length - 1] as LanePoint;
  const commands: EditCommand[] = [];

  if (laneControlKind(lane.kind) === null) {
    for (const region of regions) {
      const changes: NoteChange[] = [];
      const notes = laneNotes(lane, region.notes);
      const drawn = new Map<number, number>();
      if (tool === 'line') {
        const placed = notes.map(n => ({ id: n.id, startTick: region.startTick + n.startTick }));
        for (const { id, velocity } of lineVelocities(placed, first, last)) {
          drawn.set(id, velocity);
        }
      } else {
        const pencil = pencilSteps(points, region, lane.resolutionTicks);
        for (const note of notes) {
          const value = pencil.find(p => p.tick === stepOf(note.startTick, lane.resolutionTicks))?.value;
          if (value !== undefined) {
            drawn.set(note.id, Math.min(range[1], Math.max(range[0], Math.round(value))));
          }
        }
      }
      for (const note of notes) {
        const velocity = drawn.get(note.id);
        if (velocity !== undefined && velocity !== note.velocity) {
          changes.push({ id: note.id, velocity });
        }
      }
      if (changes.length > 0) {
        commands.push({ type: 'editNotes', regionId: region.id, changes });
      }
    }
    return commands;
  }

  for (const region of regions) {
    let relative: LanePoint[];
    if (tool === 'line') {
      const from = { tick: first.tick - region.startTick, value: first.value };
      const to = { tick: last.tick - region.startTick, value: last.value };
      relative = lineSteps(from, to, lane.resolutionTicks, range).filter(
        p => p.tick >= 0 && p.tick < region.lengthTicks
      );
    } else {
      relative = pencilSteps(points, region, lane.resolutionTicks);
    }
    if (relative.length === 0) {
      continue;
    }
    const draw = drawControls(lane, region.controls, relative, laneChannel(lane, region));
    if (draw.deleteIds.length > 0) {
      commands.push({ type: 'deleteControls', regionId: region.id, eventIds: draw.deleteIds });
    }
    if (draw.add.length > 0) {
      commands.push({ type: 'addControls', regionId: region.id, events: draw.add });
    }
  }
  return commands;
}

/** The command a key on a bar makes: its value changed by `by`, clamped to the lane's range; null when unchanged. */
export function barKeyCommand(lane: Pick<Lane, 'kind'>, ref: BarRef, by: number): EditCommand | null {
  const [min, max] = laneRange(lane.kind);
  const value = Math.min(max, Math.max(min, ref.value + by));
  if (value === ref.value) {
    return null;
  }
  return ref.kind === 'note'
    ? { type: 'editNotes', regionId: ref.regionId, changes: [{ id: ref.id, velocity: value }] }
    : { type: 'setControls', regionId: ref.regionId, changes: [{ id: ref.id, value }] };
}

/** The undo label of a stroke ("Draw CC 74", "Draw Velocity"). */
export function strokeLabel(lane: Pick<Lane, 'name'>, tool: 'pencil' | 'line'): string {
  return `${tool === 'line' ? 'Draw Line on' : 'Draw'} ${lane.name}`;
}

/** The lane set shown: the chosen one, else the project's first, else the default. */
export function currentLaneSet(sets: readonly LaneSet[], chosen: string | null): LaneSet {
  return sets.find(s => s.id === chosen) ?? sets[0] ?? DefaultLaneSet;
}

/** Whether a set is the unstored default (its first edit stores it under a new id). */
export function isUnstored(set: LaneSet, sets: readonly LaneSet[]): boolean {
  return !sets.some(s => s.id === set.id);
}

/** A new lane set id (`lns_` and random letters). */
export function newLaneSetId(): string {
  return `lns_${Array.from({ length: 2 }, () => Math.random().toString(36).slice(2, 8)).join('')}`;
}

/** A lane of `kind` (with its number for CC, poly pressure and note lanes) at a 1/16 resolution. */
export function newLane(kind: LaneKind, number: number | null): Lane {
  const n = needsNumber(kind) ? Math.max(0, Math.min(127, Math.round(number ?? (kind === 'note' ? 60 : 1)))) : null;
  return { kind, number: n, name: laneName(kind, n), resolutionTicks: 240 };
}

/** A set with `lane` added at the end. */
export function withLane(set: LaneSet, lane: Lane): LaneSet {
  return { ...set, lanes: [...set.lanes, lane] };
}

export function withoutLane(set: LaneSet, index: number): LaneSet {
  return { ...set, lanes: set.lanes.filter((_, i) => i !== index) };
}

export function withResolution(set: LaneSet, index: number, resolutionTicks: number): LaneSet {
  return { ...set, lanes: set.lanes.map((l, i) => (i === index ? { ...l, resolutionTicks } : l)) };
}

/** A name for a new set that no set has yet ("Lane Set 2"). */
export function newSetName(sets: readonly LaneSet[]): string {
  const names = new Set(sets.map(s => s.name));
  let n = sets.length + 1;
  while (names.has(`Lane Set ${n}`)) {
    n++;
  }
  return `Lane Set ${n}`;
}
