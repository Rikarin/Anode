import type { AudioRegion, FileTempo, FlexMarker } from '@anode/api-client';
import type { MeterMap } from '@anode/timeline';
import type { TempoMap } from './tempo-map';

/** Ticks per quarter note (the contract's time base). */
const Ppq = 960;

/** What of a region its timing depends on. */
export type TimedRegion = Pick<
  AudioRegion,
  'startTick' | 'fileOffset' | 'length' | 'follow' | 'flexMarkers' | 'transpose' | 'fineTune' | 'pitchNotes'
>;

/** The project's time: its tempo and signature tracks and its sample rate. */
export interface TimingContext {
  readonly tempo: TempoMap;
  readonly meter: MeterMap;
  readonly sampleRate: number;
}

/** A point of a region's map: file frame `frame` plays at (absolute) tick `tick`. */
export interface WarpKnot {
  readonly frame: number;
  readonly tick: number;
}

/**
 * Whether a region is warped (docs/06-phase-5.md): its track's flex is on, its `follow` is not `off`, or it has a
 * transpose, a fine tune or pitch notes. An unwarped region plays its file 1:1 in time.
 */
export function isWarped(region: Partial<TimedRegion>, flexOn: boolean): boolean {
  return (
    flexOn ||
    (region.follow ?? 'off') !== 'off' ||
    (region.transpose ?? 0) !== 0 ||
    (region.fineTune ?? 0) !== 0 ||
    (region.pitchNotes?.length ?? 0) > 0
  );
}

/** Whether a region may take a transpose, a fine tune or pitch notes: Flex or Follow (Logic's rule). */
export function canPitch(region: Pick<AudioRegion, 'follow'>, flexOn: boolean): boolean {
  return flexOn || region.follow !== 'off';
}

/** A base map: file frames to ticks and back, monotonic. */
interface BaseMap {
  tick(frame: number): number;
  frame(tick: number): number;
}

/** Ticks per file frame when the file's beat is a quarter note (`follow: on`); null without a usable file tempo. */
export function followRate(tempo: FileTempo | null | undefined, sampleRate: number): number | null {
  return tempo && tempo.bpm > 0 ? (Ppq * tempo.bpm) / (60 * sampleRate) : null;
}

/**
 * A region's timing (docs/06-phase-5.md, "Region timing"), mirroring the Model's `RegionTiming`:
 *
 * - Unwarped, or `follow: off`: time. Frame `fileOffset + n` plays n frames after `startTick` under the tempo track.
 * - `follow: on`: the file's beat (`tempo.bpm`) is a quarter note, linear in ticks from `startTick`.
 * - `follow: bars` / `barsAndBeats`: the file's downbeats (or beats) inside the region land on successive bar (or
 *   beat) lines: the first on the line nearest where `follow: on` puts it (but after `startTick`), each next one on the
 *   next line; linear in ticks between them, from `startTick` to the first, and at the `on` rate after the last.
 *   Without a file tempo these fall back to `on`, and `on` to time.
 * - Flex markers move a file frame to `startTick + tick`; between neighbouring knots (the region's start, the markers,
 *   its end) the base map is scaled linearly, so the start and the end stay where the base map puts them.
 *
 * A region's end tick is where its last file frame lands ({@link endTick}); its length in ticks is derived.
 */
export class RegionTiming {
  readonly warped: boolean;
  readonly startTick: number;
  readonly endTick: number;
  /** The knots: the start, the flex markers (in order) and the end, as (file frame, absolute tick). */
  readonly knots: readonly WarpKnot[];
  private readonly base: BaseMap;
  private readonly baseTicks: readonly number[];

  constructor(
    readonly region: TimedRegion,
    flexOn: boolean,
    fileTempo: FileTempo | null | undefined,
    readonly context: TimingContext
  ) {
    this.warped = isWarped(region, flexOn);
    this.startTick = region.startTick;
    this.base = this.warped ? baseMap(region, fileTempo, context) : timeMap(region, context);
    const first = region.fileOffset;
    const last = region.fileOffset + region.length;
    const markers = this.warped ? validMarkers(region.flexMarkers, first, last) : [];
    const knots: WarpKnot[] = [{ frame: first, tick: this.base.tick(first) }];
    for (const m of markers) {
      knots.push({ frame: m.fileFrame, tick: region.startTick + m.tick });
    }
    knots.push({ frame: last, tick: this.base.tick(last) });
    this.knots = knots;
    this.baseTicks = knots.map(k => this.base.tick(k.frame));
    this.endTick = knots[knots.length - 1]?.tick ?? region.startTick;
  }

  /** The region's length in ticks (derived). */
  get lengthTicks(): number {
    return this.endTick - this.startTick;
  }

  /** Where the base map (without flex markers) puts file frame `frame`, in ticks. */
  baseTickAt(frame: number): number {
    return this.base.tick(frame);
  }

  /** The (fractional) tick file frame `frame` plays at. */
  tickAt(frame: number): number {
    const knots = this.knots;
    const i = segment(knots.length, k => (knots[k]?.frame ?? 0) <= frame);
    const a = knots[i] as WarpKnot;
    const b = knots[i + 1] as WarpKnot;
    const ba = this.baseTicks[i] ?? 0;
    const bb = this.baseTicks[i + 1] ?? 0;
    const base = this.base.tick(frame);
    return bb === ba ? a.tick : a.tick + ((base - ba) * (b.tick - a.tick)) / (bb - ba);
  }

  /** The (fractional) file frame that plays at `tick`: the inverse of {@link tickAt}. */
  frameAt(tick: number): number {
    const knots = this.knots;
    const i = segment(knots.length, k => (knots[k]?.tick ?? 0) <= tick);
    const a = knots[i] as WarpKnot;
    const b = knots[i + 1] as WarpKnot;
    const ba = this.baseTicks[i] ?? 0;
    const bb = this.baseTicks[i + 1] ?? 0;
    const base = b.tick === a.tick ? ba : ba + ((tick - a.tick) * (bb - ba)) / (b.tick - a.tick);
    return this.base.frame(base);
  }

  /** Audio frames per tick over the whole region (its average). */
  get averageRate(): number {
    const ticks = this.lengthTicks;
    return ticks > 0 ? this.region.length / ticks : 0;
  }

  /**
   * The map as (tick relative to the start, file frame) points for drawing a warped waveform: every knot, and samples
   * at most every `step` ticks in between (where the base map bends), at most `max` points.
   */
  drawPoints(step = Ppq / 4, max = 512): { tick: number; frame: number }[] {
    const points: { tick: number; frame: number }[] = [];
    const total = Math.max(1, this.lengthTicks);
    const stride = Math.max(step, total / max);
    for (let i = 0; i < this.knots.length - 1; i++) {
      const a = this.knots[i] as WarpKnot;
      const b = this.knots[i + 1] as WarpKnot;
      points.push({ tick: a.tick - this.startTick, frame: a.frame });
      for (let t = a.tick + stride; t < b.tick - stride / 2; t += stride) {
        points.push({ tick: t - this.startTick, frame: this.frameAt(t) });
      }
    }
    const last = this.knots[this.knots.length - 1] as WarpKnot;
    points.push({ tick: last.tick - this.startTick, frame: last.frame });
    return points;
  }
}

/** Markers strictly inside the region, in frame order, with both frames and ticks increasing. */
function validMarkers(markers: readonly FlexMarker[], first: number, last: number): FlexMarker[] {
  const sorted = [...markers].filter(m => m.fileFrame > first && m.fileFrame < last);
  sorted.sort((a, b) => a.fileFrame - b.fileFrame);
  const kept: FlexMarker[] = [];
  for (const m of sorted) {
    const previous = kept[kept.length - 1];
    if (!previous || (m.fileFrame > previous.fileFrame && m.tick > previous.tick)) {
      kept.push(m);
    }
  }
  return kept;
}

/** The last index `i` in `0…count−2` with `atOrBefore(i)`, else 0 (a segment of consecutive knots). */
function segment(count: number, atOrBefore: (i: number) => boolean): number {
  let lo = 0;
  let hi = Math.max(0, count - 2);
  while (lo < hi) {
    const mid = (lo + hi + 1) >> 1;
    if (atOrBefore(mid)) {
      lo = mid;
    } else {
      hi = mid - 1;
    }
  }
  return lo;
}

/** Time: frame `fileOffset + n` n frames after `startTick`. */
function timeMap(region: TimedRegion, context: TimingContext): BaseMap {
  const { tempo, sampleRate } = context;
  const s0 = tempo.secondsAt(region.startTick);
  return {
    tick: frame => tempo.tickAt(s0 + (frame - region.fileOffset) / sampleRate),
    frame: tick => region.fileOffset + (tempo.secondsAt(tick) - s0) * sampleRate
  };
}

function linearMap(region: TimedRegion, rate: number): BaseMap {
  return {
    tick: frame => region.startTick + (frame - region.fileOffset) * rate,
    frame: tick => region.fileOffset + (tick - region.startTick) / rate
  };
}

function baseMap(region: TimedRegion, fileTempo: FileTempo | null | undefined, context: TimingContext): BaseMap {
  const rate = followRate(fileTempo, context.sampleRate);
  if (region.follow === 'off' || rate === null) {
    return timeMap(region, context);
  }
  if (region.follow === 'on' || !fileTempo) {
    return linearMap(region, rate);
  }
  const anchors = (region.follow === 'bars' ? fileTempo.downbeats : fileTempo.beats).filter(
    f => f > region.fileOffset && f < region.fileOffset + region.length
  );
  if (anchors.length === 0) {
    return linearMap(region, rate);
  }
  const beats = region.follow === 'barsAndBeats';
  const meter = context.meter;
  const points: WarpKnot[] = [{ frame: region.fileOffset, tick: region.startTick }];
  let tick = region.startTick;
  anchors.forEach((frame, k) => {
    if (k === 0) {
      const estimate = region.startTick + (frame - region.fileOffset) * rate;
      const nearest = beats ? meter.snapBeat(estimate) : meter.snapBar(estimate);
      tick = nearest > region.startTick ? nearest : nextLine(meter, region.startTick, beats);
    } else {
      tick = nextLine(meter, tick, beats);
    }
    points.push({ frame, tick });
  });
  return piecewiseMap(points, rate);
}

/** The first bar (or beat) line strictly after `tick`. */
export function nextLine(meter: MeterMap, tick: number, beats: boolean): number {
  const bar = Math.floor(meter.barAt(tick) + 1e-9);
  const barStart = meter.barStart(bar);
  const nextBar = meter.barStart(bar + 1);
  if (!beats) {
    return nextBar > tick ? nextBar : meter.barStart(bar + 2);
  }
  const beat = meter.beatTicksAt(tick);
  const next = barStart + (Math.floor((tick - barStart) / beat + 1e-9) + 1) * beat;
  return Math.min(next, nextBar > tick ? nextBar : next);
}

/** Linear between the points, and at `rate` ticks per frame after the last. */
function piecewiseMap(points: readonly WarpKnot[], rate: number): BaseMap {
  const last = points[points.length - 1] as WarpKnot;
  return {
    tick: frame => {
      if (frame >= last.frame) {
        return last.tick + (frame - last.frame) * rate;
      }
      const i = segment(points.length, k => (points[k]?.frame ?? 0) <= frame);
      const a = points[i] as WarpKnot;
      const b = points[i + 1] as WarpKnot;
      return a.tick + ((frame - a.frame) * (b.tick - a.tick)) / (b.frame - a.frame);
    },
    frame: tick => {
      if (tick >= last.tick) {
        return last.frame + (tick - last.tick) / rate;
      }
      const i = segment(points.length, k => (points[k]?.tick ?? 0) <= tick);
      const a = points[i] as WarpKnot;
      const b = points[i + 1] as WarpKnot;
      return a.frame + ((tick - a.tick) * (b.frame - a.frame)) / (b.tick - a.tick);
    }
  };
}
