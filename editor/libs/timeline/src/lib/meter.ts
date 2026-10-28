import { GridLevel, type GridLine } from './grid';

/** A time signature change: from `tick` on, bars of `numerator` notes of 1/`denominator`. */
export interface MeterEvent {
  readonly tick: number;
  readonly numerator: number;
  readonly denominator: number;
}

/** A position as Logic counts it: bar, beat, division and tick, all from 1. */
export interface MeterPosition {
  bar: number;
  beat: number;
  division: number;
  tick: number;
}

interface Segment {
  tick: number;
  /** The bar index (from 0) at `tick`. */
  bar: number;
  barTicks: number;
  beatTicks: number;
}

/**
 * The signature track (docs/04-phase-3.md: bars and beats follow it). Each change starts on a bar line of the meter
 * before it; positions, bar lines, beat lines and the grid follow whichever signature is in force.
 */
export class MeterMap {
  private readonly segments: Segment[];

  constructor(
    events: readonly MeterEvent[],
    readonly ppq = 960
  ) {
    const sorted = [...events]
      .filter(e => e.numerator > 0 && e.denominator > 0)
      .sort((a, b) => a.tick - b.tick);
    if (sorted.length === 0 || (sorted[0]?.tick ?? 0) > 0) {
      sorted.unshift({ tick: 0, numerator: sorted[0]?.numerator ?? 4, denominator: sorted[0]?.denominator ?? 4 });
    }
    this.segments = [];
    for (const e of sorted) {
      const beatTicks = (ppq * 4) / e.denominator;
      const previous = this.segments.at(-1);
      // A change off a bar line moves to the next bar line of the meter before it.
      const tick = previous
        ? previous.tick + Math.ceil((e.tick - previous.tick) / previous.barTicks) * previous.barTicks
        : 0;
      const bar = previous ? previous.bar + (tick - previous.tick) / previous.barTicks : 0;
      if (previous && previous.tick === tick) {
        this.segments.pop();
      }
      this.segments.push({ tick, bar, barTicks: beatTicks * e.numerator, beatTicks });
    }
  }

  /** A map with one signature throughout. */
  static constant(numerator: number, denominator: number, ppq = 960): MeterMap {
    return new MeterMap([{ tick: 0, numerator, denominator }], ppq);
  }

  /** The bar (from 0, fractional) at `tick`. */
  barAt(tick: number): number {
    const s = this.segmentAt(tick);
    return s.bar + (tick - s.tick) / s.barTicks;
  }

  /** The tick where bar `bar` (from 0) starts. */
  barStart(bar: number): number {
    let s = this.segments[0] as Segment;
    for (const segment of this.segments) {
      if (segment.bar <= bar) {
        s = segment;
      }
    }
    return s.tick + (bar - s.bar) * s.barTicks;
  }

  /** Ticks per bar and per beat at `tick`. */
  barTicksAt(tick: number): number {
    return this.segmentAt(tick).barTicks;
  }

  beatTicksAt(tick: number): number {
    return this.segmentAt(tick).beatTicks;
  }

  /** The signature at `tick`. */
  signatureAt(tick: number): { numerator: number; denominator: number } {
    const s = this.segmentAt(tick);
    return { numerator: Math.round(s.barTicks / s.beatTicks), denominator: Math.round((this.ppq * 4) / s.beatTicks) };
  }

  /** Bars, beats, divisions (1/`division` notes, at most a beat) and ticks at `tick`, each from 1. */
  position(tick: number, division = 16): MeterPosition {
    const whole = Math.max(0, Math.floor(tick));
    const s = this.segmentAt(whole);
    const inSegment = whole - s.tick;
    const divisionTicks = Math.min(s.beatTicks, (this.ppq * 4) / division);
    const inBar = inSegment % s.barTicks;
    const inBeat = inBar % s.beatTicks;
    return {
      bar: s.bar + Math.floor(inSegment / s.barTicks) + 1,
      beat: Math.floor(inBar / s.beatTicks) + 1,
      division: Math.floor(inBeat / divisionTicks) + 1,
      tick: (inBeat % divisionTicks) + 1
    };
  }

  /** The nearest bar line to `tick`. */
  snapBar(tick: number): number {
    const bar = this.barAt(Math.max(0, tick));
    return Math.max(0, this.barStart(Math.round(bar)));
  }

  /** The nearest beat line to `tick` (within its bar's meter). */
  snapBeat(tick: number): number {
    return this.snapWithin(tick, s => s.beatTicks);
  }

  /** The nearest multiple of `step` ticks counted from the bar line before `tick`. */
  snapStep(tick: number, step: number): number {
    return this.snapWithin(tick, () => step);
  }

  /** Whether a tick is a bar line. */
  isBarLine(tick: number): boolean {
    const s = this.segmentAt(tick);
    return (tick - s.tick) % s.barTicks === 0;
  }

  /**
   * Grid lines across `[from, to]`: every `step` ticks from each bar line when `step` is shorter than a bar, else
   * every `bars` bars (bar numbers that are multiples of it).
   */
  gridLines(from: number, to: number, step: number, bars = 1): GridLine[] {
    const lines: GridLine[] = [];
    const first = Math.max(0, Math.floor(this.barAt(Math.max(0, from))));
    for (let bar = first; ; bar++) {
      const start = this.barStart(bar);
      if (start > to) {
        break;
      }
      const s = this.segmentAt(start);
      if (step >= s.barTicks) {
        if (bar % bars === 0 && start >= from) {
          lines.push({ tick: start, level: GridLevel.Bar });
        }
        continue;
      }
      for (let t = 0; t < s.barTicks; t += step) {
        const tick = start + t;
        if (tick >= from && tick <= to) {
          lines.push({ tick, level: t === 0 ? GridLevel.Bar : t % s.beatTicks === 0 ? GridLevel.Beat : GridLevel.Division });
        }
      }
    }
    return lines;
  }

  private snapWithin(tick: number, unit: (s: Segment) => number): number {
    const t = Math.max(0, tick);
    const barStart = this.barStart(Math.floor(this.barAt(t)));
    const s = this.segmentAt(barStart);
    const u = unit(s);
    const snapped = barStart + Math.round((t - barStart) / u) * u;
    return Math.max(0, Math.min(snapped, barStart + s.barTicks));
  }

  private segmentAt(tick: number): Segment {
    let found = this.segments[0] as Segment;
    for (const s of this.segments) {
      if (s.tick <= tick) {
        found = s;
      } else {
        break;
      }
    }
    return found;
  }
}
