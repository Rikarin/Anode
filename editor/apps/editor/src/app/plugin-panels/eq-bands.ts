import type { EqBand, EqBandKind, EqPlace } from '@anode/audio-ui/lazy';

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
  /** The Channel EQ's placement (the Linear Phase EQ has none). */
  readonly place: string;
  /** The Channel EQ's dynamics, on a bell or shelf. */
  readonly dynamic?: string;
  readonly threshold?: string;
  readonly ratio?: string;
  readonly range?: string;
}

const bell = (id: string, label: string, kind: EqBandKind = 'bell'): EqBandParameters => ({
  id,
  label,
  kind,
  on: `${id}On`,
  frequency: `${id}Freq`,
  gain: `${id}Gain`,
  q: `${id}Q`,
  place: `${id}Place`,
  dynamic: `${id}Dyn`,
  threshold: `${id}DynThreshold`,
  ratio: `${id}DynRatio`,
  range: `${id}DynRange`
});

const cut = (id: string, label: string, kind: EqBandKind): EqBandParameters => ({
  id,
  label,
  kind,
  on: `${id}On`,
  frequency: `${id}Freq`,
  slope: `${id}Slope`,
  place: `${id}Place`
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

/** The bands an EQ's display sends after the dynamic gains (`SpectrumAnalyzer.DisplayBands`). */
export const EqSpectrumBands = 64;

/** Every parameter id a panel needs, for its pattern. */
export const EqParameterIds: readonly string[] = [
  ...EqBands.flatMap(b => [b.on, b.frequency, b.gain, b.q, b.slope].filter((id): id is string => !!id)),
  EqOutput
];

const Places: readonly EqPlace[] = ['stereo', 'mid', 'side'];

/** The Channel EQ's dynamic bands, in the order of its display's first values. */
export const EqDynamicBands: readonly EqBandParameters[] = EqBands.filter(b => !!b.dynamic);

/**
 * The bands as `value` says they stand; `dynamicDb` what the dynamic ones add now, as the display sends it. The Linear
 * Phase EQ's `value` knows no placement or dynamics, which read 0.
 */
export function eqBandsOf(
  pluginId: string,
  value: (id: string) => number,
  dynamicDb: readonly number[] = []
): EqBand[] {
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
      slope: b.slope ? Math.round(value(b.slope)) : 0,
      place: Places[Math.round(value(b.place))] ?? 'stereo',
      dynamic: !!b.dynamic && value(b.dynamic) >= 0.5,
      dynamicDb: dynamicDb[EqDynamicBands.indexOf(b)] ?? 0
    };
  });
}
