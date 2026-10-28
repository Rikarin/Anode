import type { VarispeedMode, VarispeedSettings } from '@anode/api-client';
import type { TempoMap } from './tempo-map';

export const MinSpeed = 0.5;
export const MaxSpeed = 1.5;

/** How the Varispeed display shows the speed (Logic's): a percentage, the resulting tempo, or the detune. */
export type VarispeedUnit = 'percent' | 'tempo' | 'detune';

export const VarispeedModes: readonly { value: VarispeedMode; label: string }[] = [
  { value: 'speedOnly', label: 'Speed Only' },
  { value: 'speedAndPitch', label: 'Varispeed (Speed and Pitch)' },
  { value: 'speedAndPitchMidi', label: 'Varispeed and MIDI' }
];

export const VarispeedUnits: readonly { value: VarispeedUnit; label: string }[] = [
  { value: 'percent', label: 'Percent' },
  { value: 'tempo', label: 'Resulting Tempo' },
  { value: 'detune', label: 'Detune (Semitones)' }
];

export function clampSpeed(speed: number): number {
  return Math.round(Math.min(MaxSpeed, Math.max(MinSpeed, speed)) * 10_000) / 10_000;
}

/** Semitones a speed detunes by (tape: pitch follows speed). 0.9 is −1.82. */
export function detuneSemitones(speed: number): number {
  return 12 * Math.log2(speed);
}

/** The semitones MIDI notes are transposed by in `speedAndPitchMidi`: round(12·log2(speed)). */
export function midiTranspose(speed: number): number {
  return Math.round(detuneSemitones(speed));
}

/** The speed as Varispeed shows it: "−10.00 %", "108.00 bpm" (at a project tempo), "−1.82 st". */
export function formatSpeed(speed: number, unit: VarispeedUnit, bpm = 120): string {
  const signed = (value: number, digits: number) =>
    `${value > 0 ? '+' : value < 0 ? '−' : ''}${Math.abs(value).toFixed(digits)}`;
  switch (unit) {
    case 'percent':
      return `${signed((speed - 1) * 100, 2)} %`;
    case 'tempo':
      return `${(bpm * speed).toFixed(2)} bpm`;
    case 'detune':
      return `${signed(detuneSemitones(speed), 2)} st`;
  }
}

/** A speed from a typed value in a unit ("−10", "108", "-1.82"), or null. */
export function parseSpeed(text: string, unit: VarispeedUnit, bpm = 120): number | null {
  const value = Number(text.replace('−', '-').replace(/[^\d.+-]/g, ''));
  if (!text.trim() || !Number.isFinite(value)) {
    return null;
  }
  const speed = unit === 'percent' ? 1 + value / 100 : unit === 'tempo' ? value / bpm : 2 ** (value / 12);
  return clampSpeed(speed);
}

/** The speed the project plays at: 1 unless Varispeed is on. */
export function effectiveSpeed(varispeed: VarispeedSettings): number {
  return varispeed.enabled ? varispeed.speed : 1;
}

/**
 * The tick of a compiled sample position (docs/06-phase-5.md): Varispeed compiles every position scaled by 1/speed, so
 * compiled `samples` are `samples · speed` of the tempo track's time.
 */
export function compiledSamplesToTick(samples: number, sampleRate: number, tempo: TempoMap, speed: number): number {
  return tempo.tickAt((samples / sampleRate) * speed);
}
