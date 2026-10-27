/** How a control's travel maps to its range: evenly, or by ratio (frequencies, times) so each octave gets equal room. */
export type ParameterTaper = 'linear' | 'log';

/** A control's range and how it moves through it. */
export interface ParameterRange {
  min: number;
  max: number;
  /** Discrete values: `steps` + 1 of them from min to max; 0 for continuous. */
  steps?: number;
  taper?: ParameterTaper;
}

/**
 * The taper that suits a range when none is given: logarithmic for a positive range spanning two decades or more
 * (cutoff in Hz, an envelope time in seconds), linear otherwise.
 */
export function autoTaper(min: number, max: number): ParameterTaper {
  return min > 0 && max / min >= 100 ? 'log' : 'linear';
}

/** Where a value sits on a control's travel, 0…1. */
export function toNormal(value: number, range: ParameterRange): number {
  const { min, max } = range;
  if (!(max > min)) {
    return 0;
  }
  const clamped = Math.min(max, Math.max(min, value));
  const taper = range.taper ?? autoTaper(min, max);
  return taper === 'log' ? Math.log(clamped / min) / Math.log(max / min) : (clamped - min) / (max - min);
}

/** The value at a point of a control's travel, on one of its steps when it is discrete. */
export function fromNormal(normal: number, range: ParameterRange): number {
  const { min, max } = range;
  const n = Math.min(1, Math.max(0, normal));
  const taper = range.taper ?? autoTaper(min, max);
  const value = taper === 'log' ? min * (max / min) ** n : min + n * (max - min);
  return snapToStep(value, range);
}

/** A value on the nearest of a discrete range's steps (unchanged when continuous), within the range. */
export function snapToStep(value: number, range: ParameterRange): number {
  const { min, max } = range;
  const clamped = Math.min(max, Math.max(min, value));
  const steps = range.steps ?? 0;
  if (steps > 0 && max > min) {
    const step = (max - min) / steps;
    return min + Math.round((clamped - min) / step) * step;
  }
  return clamped;
}

/**
 * A value with its unit as a plugin's display would show it: Hz as kHz from 1000, seconds as ms below 1, dB, cents and
 * semitones signed, unitless fractions to two places.
 */
export function formatParameter(value: number, unit = '', steps = 0): string {
  if (!Number.isFinite(value)) {
    return '–';
  }

  switch (unit) {
    case 'Hz':
      return value >= 1000 ? `${(value / 1000).toFixed(value >= 10_000 ? 1 : 2)} kHz` : `${value.toFixed(0)} Hz`;
    case 's':
      return value < 1 ? `${(value * 1000).toFixed(value < 0.01 ? 1 : 0)} ms` : `${value.toFixed(2)} s`;
    case 'dB':
      return `${value > 0 ? '+' : ''}${value.toFixed(1)} dB`;
    case 'ct':
    case 'st':
    case 'oct':
      return `${value > 0 ? '+' : ''}${steps > 0 || unit === 'oct' ? Math.round(value) : value.toFixed(1)} ${unit}`;
    case '%':
      return `${Math.round(value)} %`;
    default:
      return steps > 0 ? String(Math.round(value)) : value.toFixed(2);
  }
}
