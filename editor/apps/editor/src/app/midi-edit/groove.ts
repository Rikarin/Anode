import type { GroovePoint, GrooveTemplate, Note } from '@anode/api-client';
import { roundAway } from './transform';

/** An onset a template is made from: a project tick and a strength 0…1 (a note's velocity ÷ 127). */
export interface GrooveOnset {
  tick: number;
  strength: number;
}

/**
 * Groove templates, a TypeScript port of the Model's `Grooves` (docs/08-phase-7.md, "Grooves"; the rules are the
 * `about` of engine/tests/Anode.Model.Tests/GrooveVectors.json, which this port reproduces exactly):
 * - extraction: an onset falls in step n = floor((2(tick − start) + step) / (2 step)); a step's winner is its
 *   strongest onset (ties: the nearer the grid, then the earlier); point n is (winner − start, strength ÷ the
 *   strongest winner's); a step without onsets, or every step when all strengths are 0, is (n·step, 1);
 * - target: step k = 0 for t ≤ 0, else floor((2t + step − 1) / (2 step)); the landing L = (k div N)·length +
 *   points[k mod N].tick; the result max(0, t + round((L − t)·strength)), rounded half away from zero.
 */
export function grooveFromOnsets(
  id: string,
  name: string,
  onsets: readonly GrooveOnset[],
  startTick: number,
  lengthTicks: number,
  stepTicks: number
): GrooveTemplate {
  const steps = Math.max(1, Math.round(lengthTicks / stepTicks));
  const winners: (GrooveOnset | null)[] = Array.from({ length: steps }, () => null);
  for (const onset of onsets) {
    const n = Math.floor((2 * (onset.tick - startTick) + stepTicks) / (2 * stepTicks));
    if (n < 0 || n >= steps) {
      continue;
    }
    const grid = startTick + n * stepTicks;
    const best = winners[n];
    const better =
      !best ||
      onset.strength > best.strength ||
      (onset.strength === best.strength &&
        (Math.abs(onset.tick - grid) < Math.abs(best.tick - grid) ||
          (Math.abs(onset.tick - grid) === Math.abs(best.tick - grid) && onset.tick < best.tick)));
    if (better) {
      winners[n] = onset;
    }
  }
  const strongest = Math.max(0, ...winners.map(o => o?.strength ?? 0));
  const points: GroovePoint[] = winners.map((onset, n) =>
    onset && strongest > 0
      ? { tick: onset.tick - startTick, velocityScale: onset.strength / strongest }
      : { tick: n * stepTicks, velocityScale: 1 }
  );
  return { id, name, lengthTicks: steps * stepTicks, stepTicks, points };
}

/** A template from a MIDI region's notes (relative to the region's start `startTick`). */
export function grooveFromNotes(
  id: string,
  name: string,
  notes: readonly Pick<Note, 'startTick' | 'velocity'>[],
  startTick: number,
  lengthTicks: number,
  stepTicks: number
): GrooveTemplate {
  return grooveFromOnsets(
    id,
    name,
    notes.map(n => ({ tick: n.startTick + startTick, strength: n.velocity / 127 })),
    startTick,
    lengthTicks,
    stepTicks
  );
}

/** Where a project tick quantises to under a template, by `strength` (0…1), and its step's velocity scale. */
export function grooveTarget(
  groove: Pick<GrooveTemplate, 'lengthTicks' | 'stepTicks' | 'points'>,
  tick: number,
  strength: number
): { tick: number; velocityScale: number } {
  const steps = groove.points.length;
  const step = groove.stepTicks;
  if (steps === 0 || !(step > 0)) {
    return { tick, velocityScale: 1 };
  }
  const k = tick <= 0 ? 0 : Math.floor((2 * tick + step - 1) / (2 * step));
  const point = groove.points[k % steps] ?? { tick: (k % steps) * step, velocityScale: 1 };
  const landing = Math.floor(k / steps) * groove.lengthTicks + point.tick;
  return { tick: Math.max(0, tick + roundAway((landing - tick) * strength)), velocityScale: point.velocityScale };
}

/**
 * A region's notes (relative to `regionStartTick`) quantised to a template by `strength`; with `velocity`, a
 * velocity v becomes round(v·(1 + strength·(scale − 1))), clamped to 1…127. Only `ids` when given; order kept.
 */
export function quantizeToGroove<T extends Pick<Note, 'id' | 'startTick' | 'velocity'>>(
  notes: readonly T[],
  regionStartTick: number,
  groove: Pick<GrooveTemplate, 'lengthTicks' | 'stepTicks' | 'points'>,
  strength: number,
  velocity: boolean,
  ids?: ReadonlySet<number>
): T[] {
  return notes.map(n => {
    if (ids && !ids.has(n.id)) {
      return n;
    }
    const target = grooveTarget(groove, n.startTick + regionStartTick, strength);
    const scaled = velocity ? roundAway(n.velocity * (1 + strength * (target.velocityScale - 1))) : n.velocity;
    return {
      ...n,
      startTick: target.tick - regionStartTick,
      velocity: Math.min(127, Math.max(1, scaled))
    };
  });
}
