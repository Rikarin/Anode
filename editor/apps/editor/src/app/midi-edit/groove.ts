import type { GroovePoint, GrooveTemplate, Note } from '@anode/api-client';
import { roundEven } from './transform';

/** An onset a template is made from: a project tick and a strength 0…1 (a note's velocity ÷ 127). */
export interface GrooveOnset {
  tick: number;
  strength: number;
}

/**
 * Groove templates, a TypeScript port of the Model's `Grooves` (docs/08-phase-7.md, "Grooves"; checked against the
 * shared engine/tests/Anode.Model.Tests/GrooveVectors.json):
 * - a template holds one point per grid step over its length: the strongest onset within half a step of the step's
 *   grid position (ties: the earlier), as ticks from the template's start; a step without one keeps its grid position
 *   at scale 1; velocity scales are relative to the strongest chosen onset;
 * - quantising moves a tick to its nearest grid step's template position (the template repeating every `lengthTicks`
 *   from the project start), by the strength, rounded half to even.
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
  const chosen: (GrooveOnset | null)[] = [];
  for (let n = 0; n < steps; n++) {
    const grid = startTick + n * stepTicks;
    let best: GrooveOnset | null = null;
    for (const onset of onsets) {
      if (Math.abs(onset.tick - grid) * 2 > stepTicks) {
        continue;
      }
      if (!best || onset.strength > best.strength || (onset.strength === best.strength && onset.tick < best.tick)) {
        best = onset;
      }
    }
    chosen.push(best);
  }
  const strongest = Math.max(0, ...chosen.map(o => o?.strength ?? 0));
  const points: GroovePoint[] = chosen.map((onset, n) =>
    onset && strongest > 0
      ? { tick: Math.round(onset.tick - startTick), velocityScale: onset.strength / strongest }
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
  if (steps === 0 || !(groove.stepTicks > 0)) {
    return { tick, velocityScale: 1 };
  }
  const k = Math.max(0, roundEven(tick / groove.stepTicks));
  const cycle = Math.floor(k / steps);
  const point = groove.points[k % steps] ?? { tick: (k % steps) * groove.stepTicks, velocityScale: 1 };
  const target = cycle * groove.lengthTicks + point.tick;
  const s = Math.min(1, Math.max(0, strength));
  return { tick: roundEven(tick + (target - tick) * s), velocityScale: point.velocityScale };
}

/**
 * A region's notes (relative to `regionStartTick`) quantised to a template by `strength`; with `velocity`, their
 * velocities follow the template's scales by the same strength (clamped to 1…127). Only `ids` when given.
 */
export function quantizeToGroove<T extends Pick<Note, 'id' | 'startTick' | 'velocity'>>(
  notes: readonly T[],
  regionStartTick: number,
  groove: Pick<GrooveTemplate, 'lengthTicks' | 'stepTicks' | 'points'>,
  strength: number,
  velocity: boolean,
  ids?: ReadonlySet<number>
): T[] {
  const s = Math.min(1, Math.max(0, strength));
  return notes.map(n => {
    if (ids && !ids.has(n.id)) {
      return n;
    }
    const target = grooveTarget(groove, n.startTick + regionStartTick, s);
    const scaled = velocity ? roundEven(n.velocity * (1 + (target.velocityScale - 1) * s)) : n.velocity;
    return {
      ...n,
      startTick: target.tick - regionStartTick,
      velocity: Math.min(127, Math.max(1, scaled))
    };
  });
}
