import type { TempoEvent } from '@anode/api-client';

/** Ticks per quarter note (the contract's time base). */
const Ppq = 960;

/**
 * The tempo track as time (ADR-0005 with Phase 3's ramps, docs/04-phase-3.md): a step event holds its tempo until the
 * next event; a ramp event's tempo changes linearly in BPM over ticks to the next event's, and the seconds it covers
 * are the exact integral of 60 / (PPQ × BPM). The last event always holds. Converts ticks to seconds and back, and
 * gives the tempo at a tick.
 */
export class TempoMap {
  readonly events: readonly TempoEvent[];
  /** Seconds at each event's tick. */
  private readonly seconds: number[];

  constructor(events: readonly TempoEvent[], fallbackBpm = 120) {
    const sorted = [...events].filter(e => e.bpm > 0).sort((a, b) => a.tick - b.tick);
    if (sorted.length === 0 || (sorted[0]?.tick ?? 0) > 0) {
      sorted.unshift({ tick: 0, bpm: sorted[0]?.bpm ?? fallbackBpm, curve: 'step', shape: 0 });
    }
    this.events = sorted;
    this.seconds = [0];
    for (let i = 0; i < sorted.length - 1; i++) {
      this.seconds.push((this.seconds[i] ?? 0) + this.span(i, (sorted[i + 1]?.tick ?? 0) - (sorted[i]?.tick ?? 0)));
    }
  }

  /** A map with one tempo throughout. */
  static constant(bpm: number): TempoMap {
    return new TempoMap([{ tick: 0, bpm, curve: 'step', shape: 0 }]);
  }

  /** The tempo at `tick`, in BPM. */
  bpmAt(tick: number): number {
    const i = this.segmentAtTick(tick);
    const event = this.events[i] as TempoEvent;
    const next = this.events[i + 1];
    if (event.curve !== 'ramp' || !next || next.tick <= event.tick) {
      return event.bpm;
    }
    const f = Math.min(1, Math.max(0, (tick - event.tick) / (next.tick - event.tick)));
    return event.bpm + (next.bpm - event.bpm) * f;
  }

  /** Seconds from the project start to `tick` (negative ticks extrapolate the first tempo). */
  secondsAt(tick: number): number {
    if (tick <= 0) {
      return (tick / Ppq) * (60 / (this.events[0]?.bpm ?? 120));
    }
    const i = this.segmentAtTick(tick);
    return (this.seconds[i] ?? 0) + this.span(i, tick - (this.events[i]?.tick ?? 0));
  }

  /** The tick at `seconds` from the project start: the inverse of {@link secondsAt}. */
  tickAt(seconds: number): number {
    if (seconds <= 0) {
      return (seconds * (this.events[0]?.bpm ?? 120) * Ppq) / 60;
    }
    let i = 0;
    while (i + 1 < this.events.length && (this.seconds[i + 1] ?? Infinity) <= seconds) {
      i++;
    }
    const event = this.events[i] as TempoEvent;
    const next = this.events[i + 1];
    const s = seconds - (this.seconds[i] ?? 0);
    if (event.curve === 'ramp' && next && next.bpm !== event.bpm && next.tick > event.tick) {
      const length = next.tick - event.tick;
      const delta = next.bpm - event.bpm;
      const bpm = event.bpm * Math.exp((s * Ppq * delta) / (60 * length));
      return event.tick + ((bpm - event.bpm) * length) / delta;
    }
    return event.tick + (s * event.bpm * Ppq) / 60;
  }

  /** Audio frames from `tick` to `tick + ticks` at `sampleRate`. */
  framesBetween(tick: number, ticks: number, sampleRate: number): number {
    return (this.secondsAt(tick + ticks) - this.secondsAt(tick)) * sampleRate;
  }

  /** The tick `frames` audio frames after `tick` (a region's end from its start and length). */
  tickAfterFrames(tick: number, frames: number, sampleRate: number): number {
    return this.tickAt(this.secondsAt(tick) + frames / sampleRate);
  }

  /** Whether the tempo is the same everywhere. */
  get constant(): boolean {
    return this.events.every(e => e.bpm === this.events[0]?.bpm);
  }

  /** Seconds that `ticks` ticks from segment `i`'s start take. */
  private span(i: number, ticks: number): number {
    const event = this.events[i] as TempoEvent;
    const next = this.events[i + 1];
    if (event.curve === 'ramp' && next && next.bpm !== event.bpm && next.tick > event.tick) {
      const length = next.tick - event.tick;
      const delta = next.bpm - event.bpm;
      const bpm = event.bpm + (delta * ticks) / length;
      return ((60 * length) / (Ppq * delta)) * Math.log(bpm / event.bpm);
    }
    return (ticks / Ppq) * (60 / event.bpm);
  }

  private segmentAtTick(tick: number): number {
    let lo = 0;
    let hi = this.events.length - 1;
    while (lo < hi) {
      const mid = (lo + hi + 1) >> 1;
      if ((this.events[mid]?.tick ?? 0) <= tick) {
        lo = mid;
      } else {
        hi = mid - 1;
      }
    }
    return lo;
  }
}
