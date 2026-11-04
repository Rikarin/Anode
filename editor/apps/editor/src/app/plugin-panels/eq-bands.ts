import type { EqBand, EqBandKind } from '@anode/audio-ui/lazy';

/** A band of the engine's EQs, by parameter id. Both EQs share the ids (`docs/effects/T2.md`). */
export interface EqBandParameters {
  readonly id: string;
  readonly label: string;
  readonly kind: EqBandKind;
  readonly on: string;
  readonly frequency: string;
  readonly gain?: string;
  readonly q?: string;
  readonly slope?: string;
}

const bell = (id: string, label: string, kind: EqBandKind = 'bell'): EqBandParameters => ({
  id,
  label,
  kind,
  on: `${id}On`,
  frequency: `${id}Freq`,
  gain: `${id}Gain`,
  q: `${id}Q`
});

const cut = (id: string, label: string, kind: EqBandKind): EqBandParameters => ({
  id,
  label,
  kind,
  on: `${id}On`,
  frequency: `${id}Freq`,
  slope: `${id}Slope`
});

/** The eight bands, low to high, as they read on the graph. */
export const EqBands: readonly EqBandParameters[] = [
  cut('hpf', 'Low Cut', 'highPass'),
  bell('lowShelf', 'Low Shelf', 'lowShelf'),
  bell('peak1', 'Peak 1'),
  bell('peak2', 'Peak 2'),
  bell('peak3', 'Peak 3'),
  bell('peak4', 'Peak 4'),
  bell('highShelf', 'High Shelf', 'highShelf'),
  cut('lpf', 'High Cut', 'lowPass')
];

/** The Linear Phase EQ limits a shelf's Q (`BiquadCoefficients.MaxShelfQ`); the Channel EQ's SVF shelves do not. */
const LinearShelfMaxQ = 1;

/** The EQ's output-gain parameter. */
export const EqOutput = 'output';

/** Every parameter id a panel needs, for its pattern. */
export const EqParameterIds: readonly string[] = [
  ...EqBands.flatMap(b => [b.on, b.frequency, b.gain, b.q, b.slope].filter((id): id is string => !!id)),
  EqOutput
];

/** The bands as `value` says they stand. */
export function eqBandsOf(pluginId: string, value: (id: string) => number): EqBand[] {
  return EqBands.map(b => {
    const q = b.q ? value(b.q) : 1;
    const shelf = b.kind === 'lowShelf' || b.kind === 'highShelf';
    return {
      id: b.id,
      label: b.label,
      kind: b.kind,
      on: value(b.on) >= 0.5,
      frequency: value(b.frequency),
      gainDb: b.gain ? value(b.gain) : 0,
      q: shelf && pluginId === 'anode.linear-eq' ? Math.min(q, LinearShelfMaxQ) : q,
      slope: b.slope ? Math.round(value(b.slope)) : 0
    };
  });
}
