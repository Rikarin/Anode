import { type MeterMap, snapStep } from '@anode/timeline';
import type { TempoMap } from '../music/tempo-map';

/**
 * Logic's snap modes: Smart follows the grid the zoom shows; Bar, Beat and Division snap to those lines of the
 * signature in force; Ticks to whole ticks; Frames to SMPTE frames and Samples to audio samples of the clock time.
 */
export type SnapMode = 'smart' | 'bar' | 'beat' | 'division' | 'ticks' | 'frames' | 'samples';

export const SnapModes: readonly { value: SnapMode; label: string }[] = [
  { value: 'smart', label: 'Smart' },
  { value: 'bar', label: 'Bar' },
  { value: 'beat', label: 'Beat' },
  { value: 'division', label: 'Division' },
  { value: 'ticks', label: 'Ticks' },
  { value: 'frames', label: 'Frames' },
  { value: 'samples', label: 'Samples' }
];

/** What snapping needs to know besides the position. */
export interface SnapContext {
  meter: MeterMap;
  tempo: TempoMap;
  sampleRate: number;
  /** The Tracks area's zoom (Smart snaps to the grid it draws). */
  pixelsPerTick: number;
  /** The division, as a note value (16: sixteenths). */
  division?: number;
  /** SMPTE frames per second. */
  frameRate?: number;
}

/** `tick` snapped by `mode`, never below 0. */
export function snapBy(tick: number, mode: SnapMode, context: SnapContext): number {
  const t = Math.max(0, tick);
  const { meter } = context;
  switch (mode) {
    case 'smart': {
      const step = smartStep(t, context);
      return step >= meter.barTicksAt(t) ? meter.snapBar(t) : meter.snapStep(t, step);
    }
    case 'bar':
      return meter.snapBar(t);
    case 'beat':
      return meter.snapBeat(t);
    case 'division':
      return meter.snapStep(t, Math.min(meter.beatTicksAt(t), (meter.ppq * 4) / (context.division ?? 16)));
    case 'ticks':
      return Math.round(t);
    case 'frames': {
      const fps = context.frameRate ?? 25;
      return Math.max(0, Math.round(context.tempo.tickAt(Math.round(context.tempo.secondsAt(t) * fps) / fps)));
    }
    case 'samples': {
      const rate = context.sampleRate;
      return Math.max(0, context.tempo.tickAt(Math.round(context.tempo.secondsAt(t) * rate) / rate));
    }
  }
}

/** The grid step Smart snaps to at this zoom (a sixteenth, an eighth, a beat, or bars). */
export function smartStep(tick: number, context: SnapContext): number {
  const { meter } = context;
  return snapStep(meter.ppq, meter.barTicksAt(tick) / meter.ppq, context.pixelsPerTick);
}

/** The step a snap mode moves by at `tick`, for minimum lengths and nudges (0 for the free modes). */
export function snapUnit(tick: number, mode: SnapMode, context: SnapContext): number {
  switch (mode) {
    case 'smart':
      return smartStep(tick, context);
    case 'bar':
      return context.meter.barTicksAt(tick);
    case 'beat':
      return context.meter.beatTicksAt(tick);
    case 'division':
      return Math.min(context.meter.beatTicksAt(tick), (context.meter.ppq * 4) / (context.division ?? 16));
    default:
      return 0;
  }
}
