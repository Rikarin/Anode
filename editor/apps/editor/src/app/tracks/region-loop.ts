import type { EditCommand } from '@anode/api-client';

/** The kinds of region that loop. */
export type LoopKind = 'audio' | 'midi' | 'pattern';

/** The most repeats a region plays (the engine's `Loops.MaxRepeats`). */
export const MaxRepeats = 10_000;

/** A repeat of a region, drawn after it. */
export interface LoopRepeat {
  readonly startTick: number;
  readonly lengthTicks: number;
}

/** Where a region's repeats stop: `loopTicks` after its end, or where the next region on its track starts. */
export function loopEnd(endTick: number, loopTicks: number, nextStart: number | null): number {
  const wanted = endTick + Math.max(0, loopTicks);
  return nextStart === null ? wanted : Math.max(endTick, Math.min(wanted, nextStart));
}

/** The repeats of a region of `period` ticks from `startTick`, up to `until`, the last one cut there. */
export function loopRepeats(startTick: number, period: number, until: number): LoopRepeat[] {
  const repeats: LoopRepeat[] = [];
  if (period <= 0) {
    return repeats;
  }
  for (let k = 1; k <= MaxRepeats; k++) {
    const start = startTick + k * period;
    if (start >= until) {
      break;
    }
    repeats.push({ startTick: start, lengthTicks: Math.min(period, until - start) });
  }
  return repeats;
}

/** The first of `starts` after `tick`, or null. */
export function nextStartAfter(starts: readonly number[], tick: number): number | null {
  const after = starts.filter(s => s > tick);
  return after.length > 0 ? Math.min(...after) : null;
}

/** The loop a drag of a region's corner to `tick` (snapped) asks for: what lies past its end. */
export function loopAt(endTick: number, tick: number): number {
  return Math.max(0, Math.round(tick - endTick));
}

/** The command that sets a region's loop. */
export function loopCommand(regionId: string, kind: LoopKind, loopTicks: number): EditCommand {
  const type = kind === 'audio' ? 'setRegion' : kind === 'midi' ? 'setMidiRegion' : 'setPatternRegion';
  return { type, regionId, loopTicks: Math.round(loopTicks) };
}

/** A loop's length in repeats of a region of `lengthTicks`, to two places (the inspector's field). */
export function repeatsOf(loopTicks: number, lengthTicks: number): number {
  return lengthTicks > 0 ? Math.round((loopTicks / lengthTicks) * 100) / 100 : 0;
}
