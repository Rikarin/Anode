import type { EditCommand } from '@anode/api-client';
import type { RegionTiming } from '../music/region-timing';
import type { SnapContext } from './snap';

/** The values regions nudge by (Logic's Nudge Value); the signature in force sets a bar and a beat. */
export type NudgeValue = 'tick' | 'frame' | 'division' | 'beat' | 'bar';

export const NudgeValues: readonly { value: NudgeValue; label: string }[] = [
  { value: 'tick', label: 'Tick' },
  { value: 'frame', label: 'Frame' },
  { value: 'division', label: 'Division' },
  { value: 'beat', label: 'Beat' },
  { value: 'bar', label: 'Bar' }
];

/**
 * How many ticks a nudge from `tick` moves in `direction` (1 later, −1 earlier). Going back, a bar or beat is the one
 * before `tick`, so a nudge there and a nudge back land where they started across a signature change. A frame is an
 * SMPTE frame of clock time, at least a tick.
 */
export function nudgeTicks(tick: number, value: NudgeValue, direction: 1 | -1, context: SnapContext): number {
  const { meter, tempo } = context;
  const at = direction > 0 ? tick : Math.max(0, tick - 1);
  switch (value) {
    case 'tick':
      return direction;
    case 'frame': {
      const seconds = tempo.secondsAt(tick) + direction / (context.frameRate ?? 25);
      const by = tempo.tickAt(Math.max(0, seconds)) - tick;
      return direction * Math.max(1, Math.round(Math.abs(by)));
    }
    case 'division':
      return direction * Math.min(meter.beatTicksAt(at), (meter.ppq * 4) / (context.division ?? 16));
    case 'beat':
      return direction * meter.beatTicksAt(at);
    case 'bar':
      return direction * meter.barTicksAt(at);
  }
}

/**
 * The moves of a nudge: every region by what the value is at the earliest of them, so they keep their distances. None
 * goes before the project start, and at the start nothing moves.
 */
export function nudgeCommands(
  regions: readonly { id: string; startTick: number }[],
  value: NudgeValue,
  direction: 1 | -1,
  context: SnapContext
): EditCommand[] {
  if (regions.length === 0) {
    return [];
  }

  const earliest = Math.min(...regions.map(r => r.startTick));
  const delta = Math.max(-earliest, Math.round(nudgeTicks(earliest, value, direction, context)));
  return delta === 0
    ? []
    : regions.map(r => ({ type: 'moveRegion', regionId: r.id, startTick: Math.round(r.startTick) + delta }));
}

/** The first of `points` after `tick`; a point within half a tick of it is where the playhead already is. */
export function nextPoint(points: readonly number[], tick: number): number | null {
  const after = points.filter(p => p > tick + 0.5);
  return after.length > 0 ? Math.min(...after) : null;
}

/** The last of `points` before `tick`. */
export function previousPoint(points: readonly number[], tick: number): number | null {
  const before = points.filter(p => p < tick - 0.5);
  return before.length > 0 ? Math.max(...before) : null;
}

/** Where a region's transients play: those of its file inside the part it plays, through its timing. */
export function transientTicks(timing: RegionTiming, transients: readonly number[]): number[] {
  const { fileOffset, length } = timing.region;
  return transients.filter(f => f >= fileOffset && f < fileOffset + length).map(f => timing.tickAt(f));
}
