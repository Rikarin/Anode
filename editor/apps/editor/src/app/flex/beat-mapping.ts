import type { BeatMapping, TempoEvent } from '@anode/api-client';
import type { MeterMap } from '@anode/timeline';
import { TempoMap } from '../music/tempo-map';

const Ppq = 960;
/** How close (CSS pixels) a dropped beat must be to a transient to map onto it. */
export const BeatSnapPx = 10;

/**
 * Beat mapping (docs/06-phase-5.md): each mapping in order sets the tempo from the last tempo event before its tick
 * (its anchor, whose position stays) to its tick so that the tick lands on its sample, and holds the tempo that was in
 * force at its tick from there (an event is added there unless one is). Positions before the anchor do not move, so
 * earlier mappings keep. Throws a `RangeError` for a mapping that cannot be met (its sample not after its anchor's, or
 * a tempo outside 20…999 bpm).
 */
export function mapBeats(
  events: readonly TempoEvent[],
  mappings: readonly BeatMapping[],
  sampleRate: number
): TempoEvent[] {
  let next = [...events].sort((a, b) => a.tick - b.tick);
  for (const mapping of mappings) {
    const map = new TempoMap(next);
    const tick = Math.round(mapping.tick);
    if (tick <= 0) {
      throw new RangeError('A beat mapping is after the project start.');
    }
    const anchor = next.filter(e => e.tick < tick).pop() ?? { tick: 0, bpm: map.bpmAt(0), curve: 'step', shape: 0 };
    const anchorSample = map.secondsAt(anchor.tick) * sampleRate;
    const samples = mapping.sample - anchorSample;
    if (samples <= 0) {
      throw new RangeError('A beat cannot map before the beat it follows.');
    }
    const bpm = (((tick - anchor.tick) / Ppq) * 60) / (samples / sampleRate);
    if (!(bpm >= 20 && bpm <= 999)) {
      throw new RangeError(`That mapping needs ${bpm.toFixed(1)} bpm (20 to 999).`);
    }
    const held = next.find(e => e.tick === tick) ?? { tick, bpm: map.bpmAt(tick), curve: 'step' as const, shape: 0 };
    next = [
      ...next.filter(e => e.tick < anchor.tick),
      { tick: anchor.tick, bpm: round4(bpm), curve: 'step', shape: 0 },
      held,
      ...next.filter(e => e.tick > tick)
    ];
  }
  return next;
}

/**
 * The beat-mapping gesture: a press at `fromTick` in the Beat Mapping track picks the beat line nearest it; a drop at
 * `toTick` maps that beat onto the transient (a project tick) nearest the drop within {@link BeatSnapPx}, or onto the
 * drop itself when none is near. Null when the beat is the project start.
 */
export function beatMappingGesture(
  meter: MeterMap,
  fromTick: number,
  toTick: number,
  transientTicks: readonly number[],
  ticksPerPixel: number
): { beatTick: number; targetTick: number; onTransient: boolean } | null {
  const beatTick = meter.snapBeat(Math.max(0, fromTick));
  if (beatTick <= 0) {
    return null;
  }
  let target = toTick;
  let onTransient = false;
  let distance = BeatSnapPx * ticksPerPixel;
  for (const t of transientTicks) {
    const d = Math.abs(t - toTick);
    if (d <= distance) {
      target = t;
      distance = d;
      onTransient = true;
    }
  }
  return { beatTick, targetTick: target, onTransient };
}

/** The mapping a gesture makes: the beat at the sample where `targetTick` plays under the current tempo track. */
export function mappingFor(tempo: TempoMap, beatTick: number, targetTick: number, sampleRate: number): BeatMapping {
  return { tick: Math.round(beatTick), sample: Math.round(tempo.secondsAt(targetTick) * sampleRate) };
}

function round4(value: number): number {
  return Math.round(value * 10_000) / 10_000;
}
