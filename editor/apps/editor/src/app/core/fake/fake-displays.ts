import type { Insert } from '@anode/api-client';
import { analyserBands, eqResponse } from '@anode/audio-ui/lazy';
import { EqOutput, eqBandsOf } from '../../plugin-panels/eq-bands';

/** How long a window's `watchInsertDisplays` lasts, as the Server's lease. */
export const DisplayLeaseMs = 3_000;

/** The key of a lease on an insert's display. */
export function displayKey(trackId: string | null, insertId: string): string {
  return `${trackId ?? 'master'}/${insertId}`;
}

/** The bands an EQ's display sends (`SpectrumAnalyzer.DisplayBands`). */
const SpectrumBands = 64;

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
  const bands = analyserBands(SpectrumBands);
  if (level <= 0) {
    return bands.map(() => silence);
  }
  const db = 20 * Math.log10(level);
  const eq = eqBandsOf(insert.pluginId, id => eqValue(insert, id, defaults));
  const curve = eqResponse(eq, bands, 48_000, eqValue(insert, EqOutput, defaults));
  return bands.map((hz, i) => {
    const stir = 2 * Math.sin(i * 0.9 + tick / 480);
    return Math.max(silence, db - 12 - 3 * Math.log2(hz / 100) + stir + (curve[i] ?? 0));
  });
}
