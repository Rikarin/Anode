import type { Insert } from '@anode/api-client';
import { analyserBands, eqResponse } from '@anode/audio-ui/lazy';
import { EqDynamicBands, EqOutput, EqSpectrumBands, eqBandsOf } from '../../plugin-panels/eq-bands';

/** What an insert reports: its reading, and its display (`null` when it has none). */
export interface FakeDisplay {
  value: number;
  values: number[] | null;
}

/** How long a window's `watchInsertDisplays` lasts, as the Server's lease. */
export const DisplayLeaseMs = 3_000;

/** The key of a lease on an insert's display. */
export function displayKey(trackId: string | null, insertId: string): string {
  return `${trackId ?? 'master'}/${insertId}`;
}

/** What an insert's parameter is, or its default in the catalogue when the insert has not set it. */
function eqValue(insert: Insert, id: string, defaults: ReadonlyMap<string, number>): number {
  return insert.parameters[id] ?? defaults.get(id) ?? 0;
}

const silence = -120;

/**
 * An EQ's output spectrum: program material at `level` (falling 3 dB an octave, stirring with the playhead) through the
 * insert's own bands, so the analyser moves with the curve.
 */
export function fakeSpectrum(
  insert: Insert,
  level: number,
  tick: number,
  defaults: ReadonlyMap<string, number>
): number[] {
  const bands = analyserBands(EqSpectrumBands);
  const value = (id: string) => eqValue(insert, id, defaults);
  const db = level > 0 ? 20 * Math.log10(level) : silence;
  // The Channel EQ's display starts with what its dynamic bands add, heard here at the program's level.
  const dynamics =
    insert.pluginId === 'anode.channel-eq'
      ? EqDynamicBands.map(band => {
          if (!band.dynamic || value(band.dynamic) < 0.5) {
            return 0;
          }
          const over = Math.max(0, db - value(band.threshold ?? '')) * (1 - 1 / value(band.ratio ?? ''));
          const range = value(band.range ?? '');
          return Math.sign(range) * Math.min(Math.abs(range), over);
        })
      : [];
  if (level <= 0) {
    return [...dynamics, ...bands.map(() => silence)];
  }
  const eq = eqBandsOf(insert.pluginId, value, dynamics);
  const curve = eqResponse(eq, bands, 48_000, eqValue(insert, EqOutput, defaults));
  const spectrum = bands.map((hz, i) => {
    const stir = 2 * Math.sin(i * 0.9 + tick / 480);
    return Math.max(silence, db - 12 - 3 * Math.log2(hz / 100) + stir + (curve[i] ?? 0));
  });
  return [...dynamics, ...spectrum];
}

/** A sine of `points` samples, `periods` long, at `peak`. */
function sineTrace(points: number, periods: number, peak: number, phase = 0): number[] {
  return Array.from({ length: points }, (_, i) => peak * Math.sin((2 * Math.PI * periods * i) / points + phase));
}

/**
 * What a parity effect reports for a tone at `level` (a concert A a little sharp, as far as the fake has one), as the
 * engine's displays lay them out (docs/effects/parity.md).
 */
export function fakeDisplay(
  insert: Insert,
  level: number,
  tick: number,
  defaults: ReadonlyMap<string, number>
): FakeDisplay {
  const parameter = (id: string) => eqValue(insert, id, defaults);
  const db = level > 0 ? 20 * Math.log10(level) : silence;
  switch (insert.pluginId) {
    case 'anode.tuner':
      return level > 0
        ? { value: 3, values: [parameter('reference') * 2 ** (3 / 1_200), 69, 3] }
        : { value: 0, values: [0, 0, 0] };
    case 'anode.expander': {
      const over = Math.min(0, db - parameter('threshold'));
      return { value: Math.max(parameter('range'), over * (parameter('ratio') - 1)), values: null };
    }
    case 'anode.scope': {
      const periods = (parameter('time') / 1_000) * 220;
      return {
        value: Math.max(silence, db),
        values: [...sineTrace(256, periods, level), ...sineTrace(256, periods, level * 0.8)]
      };
    }
    case 'anode.phase-meter': {
      const left = sineTrace(256, 3, level, tick / 40);
      const right = sineTrace(256, 3, level, tick / 40 + 0.5);
      const points = left.flatMap((l, i) => [
        ((right[i] ?? 0) - l) * Math.SQRT1_2,
        ((right[i] ?? 0) + l) * Math.SQRT1_2
      ]);
      return { value: level > 0 ? Math.cos(0.5) : 0, values: [level > 0 ? Math.cos(0.5) : 0, 0, ...points] };
    }
    case 'anode.test-oscillator':
      return { value: parameter('level'), values: null };
    default:
      return { value: Math.max(silence, db), values: null };
  }
}
