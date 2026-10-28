import type { TempoEvent } from '@anode/api-client';

/** Ticks per quarter note (the contract's time base). */
const Ppq = 960;

/** Sub-steps of a shaped ramp's precomputed integral (each integrated by Simpson's rule). */
const ShapeSteps = 256;

/**
 * A ramp's tempo at fraction `x` (0…1) of the way from `bpm0` to `bpm1`, for curve `shape` (−1…1): the fade formula
 * bpm₀ + (bpm₁ − bpm₀)·x^(2^(−2·shape)) (docs/06-phase-5.md). `shape` 0 is the linear ramp of Phase 3.
 */
export function rampBpm(bpm0: number, bpm1: number, x: number, shape: number): number {
  const f = Math.min(1, Math.max(0, x));
  const s = Math.min(1, Math.max(-1, shape || 0));
  return bpm0 + (bpm1 - bpm0) * (s === 0 ? f : f ** (2 ** (-2 * s)));
}

/**
 * The tempo track as time (ADR-0005 with Phase 3's ramps, docs/04-phase-3.md, and Phase 5's curve shapes): a step
 * event holds its tempo until the next event; a ramp event's tempo changes in BPM over ticks to the next event's along
 * its `shape` ({@link rampBpm}), and the seconds it covers are the integral of 60 / (PPQ × BPM): exact for the linear
 * ramp, numeric (Simpson's rule over {@link ShapeSteps} sub-steps, then Newton for the inverse) for a curved one. The
 * last event always holds. Converts ticks to seconds and back, and gives the tempo at a tick.
 */
export class TempoMap {
  readonly events: readonly TempoEvent[];
  /** Seconds at each event's tick. */
  private readonly seconds: number[];
  /** Cumulative seconds at each sub-step of a curved ramp, by event index. */
  private readonly shaped = new Map<number, Float64Array>();

  constructor(events: readonly TempoEvent[], fallbackBpm = 120) {
    const sorted = [...events].filter(e => e.bpm > 0).sort((a, b) => a.tick - b.tick);
    if (sorted.length === 0 || (sorted[0]?.tick ?? 0) > 0) {
      sorted.unshift({ tick: 0, bpm: sorted[0]?.bpm ?? fallbackBpm, curve: 'step', shape: 0 });
    }
    this.events = sorted.map(e => ({ ...e, shape: e.shape || 0 }));
    this.seconds = [0];
    this.events.forEach((_, i) => {
      if (this.curved(i)) {
        this.shaped.set(i, this.integrate(i));
      }
    });
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
    return rampBpm(event.bpm, next.bpm, (tick - event.tick) / (next.tick - event.tick), event.shape);
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
    if (this.curved(i) && next) {
      return event.tick + this.curvedTicks(i, s);
    }
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

  /** Whether segment `i` is a ramp with a curved shape (integrated numerically). */
  private curved(i: number): boolean {
    const event = this.events[i] as TempoEvent;
    const next = this.events[i + 1];
    return event.curve === 'ramp' && !!next && next.bpm !== event.bpm && next.tick > event.tick && event.shape !== 0;
  }

  /**
   * Curved segment `i` is integrated over v (0…1) with x = v^q: q = 1/p for a shape above 0 (p = 2^(−2·shape) < 1,
   * whose x^p is steep at 0), so the tempo is linear in v and the integrand smooth; q = 1 otherwise.
   */
  private exponent(i: number): number {
    const shape = this.events[i]?.shape ?? 0;
    return shape > 0 ? 2 ** (2 * shape) : 1;
  }

  /** Seconds per unit of v at `v` in curved segment `i`. */
  private secondsPerV(i: number, v: number): number {
    const event = this.events[i] as TempoEvent;
    const next = this.events[i + 1] as TempoEvent;
    const q = this.exponent(i);
    const length = next.tick - event.tick;
    const x = v ** q;
    const dxdv = q === 1 ? 1 : q * v ** (q - 1);
    return (length * dxdv * 60) / (Ppq * rampBpm(event.bpm, next.bpm, x, event.shape));
  }

  /**
   * Simpson's rule over `[a, b]` of v in curved segment `i`; from 0, over halving pieces toward 0 (the integrand may
   * be steep there).
   */
  private simpson(i: number, a: number, b: number): number {
    if (b <= a) {
      return 0;
    }
    if (a === 0) {
      let sum = 0;
      for (let k = 0, hi = b; k < 48; k++, hi /= 2) {
        sum += this.rule(i, hi / 2, hi);
      }
      return sum;
    }
    return this.rule(i, a, b);
  }

  private rule(i: number, a: number, b: number): number {
    const m = (a + b) / 2;
    return ((b - a) / 6) * (this.secondsPerV(i, a) + 4 * this.secondsPerV(i, m) + this.secondsPerV(i, b));
  }

  /** Cumulative seconds at each of curved segment `i`'s sub-steps of v (two Simpson halves each). */
  private integrate(i: number): Float64Array {
    const h = 1 / ShapeSteps;
    const table = new Float64Array(ShapeSteps + 1);
    for (let k = 0; k < ShapeSteps; k++) {
      const a = k * h;
      table[k + 1] = (table[k] ?? 0) + this.simpson(i, a, a + h / 2) + this.simpson(i, a + h / 2, a + h);
    }
    return table;
  }

  /** Seconds to `v` in curved segment `i`. */
  private secondsToV(i: number, v: number): number {
    const table = this.shaped.get(i) as Float64Array;
    const h = 1 / ShapeSteps;
    const k = Math.min(ShapeSteps - 1, Math.floor(v / h));
    const a = k * h;
    return (table[k] ?? 0) + this.simpson(i, a, (a + v) / 2) + this.simpson(i, (a + v) / 2, v);
  }

  /** Seconds `ticks` into curved segment `i` (past its end at the next event's tempo). */
  private curvedSpan(i: number, ticks: number): number {
    const length = (this.events[i + 1]?.tick ?? 0) - (this.events[i]?.tick ?? 0);
    if (ticks >= length) {
      const table = this.shaped.get(i) as Float64Array;
      return (table[ShapeSteps] ?? 0) + ((ticks - length) * 60) / (Ppq * (this.events[i + 1]?.bpm ?? 120));
    }
    const x = Math.max(0, ticks) / length;
    return this.secondsToV(i, x ** (1 / this.exponent(i)));
  }

  /** Ticks into curved segment `i` at `seconds` into it: the table, then Newton steps in v. */
  private curvedTicks(i: number, seconds: number): number {
    const table = this.shaped.get(i) as Float64Array;
    const length = (this.events[i + 1]?.tick ?? 0) - (this.events[i]?.tick ?? 0);
    const h = 1 / ShapeSteps;
    let lo = 0;
    let hi = ShapeSteps;
    while (hi - lo > 1) {
      const mid = (lo + hi) >> 1;
      if ((table[mid] ?? 0) <= seconds) {
        lo = mid;
      } else {
        hi = mid;
      }
    }
    let v = lo * h + (seconds - (table[lo] ?? 0)) / Math.max(1e-12, this.secondsPerV(i, lo * h + h / 2));
    for (let n = 0; n < 8; n++) {
      v = Math.min(1, Math.max(0, v - (this.secondsToV(i, v) - seconds) / this.secondsPerV(i, Math.max(v, 1e-9))));
    }
    return length * v ** this.exponent(i);
  }

  /** Seconds that `ticks` ticks from segment `i`'s start take. */
  private span(i: number, ticks: number): number {
    if (this.shaped.has(i)) {
      return this.curvedSpan(i, ticks);
    }
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
