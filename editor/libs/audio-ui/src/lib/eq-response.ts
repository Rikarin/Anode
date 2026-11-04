/** What a band of an EQ does. */
export type EqBandKind = 'highPass' | 'lowPass' | 'lowShelf' | 'bell' | 'highShelf';

/** One band of an EQ, as the graph draws it and the curve adds it up. */
export interface EqBand {
  /** A stable key (the parameter stem: `peak2`, `hpf`). */
  readonly id: string;
  readonly label: string;
  readonly kind: EqBandKind;
  readonly on: boolean;
  /** Hz. */
  readonly frequency: number;
  /** dB; the cuts have none. */
  readonly gainDb: number;
  readonly q: number;
  /** The cuts' slope: 0…3 for 12, 24, 36 and 48 dB/octave. */
  readonly slope: number;
  /** What a dynamic band is adding to its gain right now, dB (0 otherwise). */
  readonly dynamicDb?: number;
}

/** The qualities of a slope's Butterworth sections (slope 0…3: order 2, 4, 6, 8): the engine's EQs' table. */
function butterworth(slope: number): number[] {
  const order = 2 * (slope + 1);
  return Array.from({ length: slope + 1 }, (_, k) => 1 / (2 * Math.cos((Math.PI * (2 * k + 1)) / (2 * order))));
}

/** Normalised biquad coefficients. */
interface Biquad {
  b0: number;
  b1: number;
  b2: number;
  a1: number;
  a2: number;
}

/**
 * The RBJ cookbook design the engine's EQs share: the Channel EQ's trapezoidal SVFs are the same bilinear transforms of
 * the same analog prototypes, and the Linear Phase EQ samples these very magnitudes (`BiquadCoefficients.Design`).
 */
function design(kind: EqBandKind, rate: number, frequency: number, q: number, gainDb: number): Biquad {
  const w = (2 * Math.PI * Math.min(Math.max(frequency, 1), rate * 0.49)) / rate;
  const cos = Math.cos(w);
  const alpha = Math.sin(w) / (2 * Math.max(q, 1e-3));
  const a = 10 ** (gainDb / 40);
  let b0: number, b1: number, b2: number, a0: number, a1: number, a2: number;
  switch (kind) {
    case 'lowPass':
      b0 = (1 - cos) / 2;
      b1 = 1 - cos;
      b2 = b0;
      a0 = 1 + alpha;
      a1 = -2 * cos;
      a2 = 1 - alpha;
      break;
    case 'highPass':
      b0 = (1 + cos) / 2;
      b1 = -(1 + cos);
      b2 = b0;
      a0 = 1 + alpha;
      a1 = -2 * cos;
      a2 = 1 - alpha;
      break;
    case 'bell':
      b0 = 1 + alpha * a;
      b1 = -2 * cos;
      b2 = 1 - alpha * a;
      a0 = 1 + alpha / a;
      a1 = -2 * cos;
      a2 = 1 - alpha / a;
      break;
    case 'lowShelf': {
      const s = 2 * Math.sqrt(a) * alpha;
      b0 = a * (a + 1 - (a - 1) * cos + s);
      b1 = 2 * a * (a - 1 - (a + 1) * cos);
      b2 = a * (a + 1 - (a - 1) * cos - s);
      a0 = a + 1 + (a - 1) * cos + s;
      a1 = -2 * (a - 1 + (a + 1) * cos);
      a2 = a + 1 + (a - 1) * cos - s;
      break;
    }
    case 'highShelf': {
      const s = 2 * Math.sqrt(a) * alpha;
      b0 = a * (a + 1 + (a - 1) * cos + s);
      b1 = -2 * a * (a - 1 + (a + 1) * cos);
      b2 = a * (a + 1 + (a - 1) * cos - s);
      a0 = a + 1 - (a - 1) * cos + s;
      a1 = 2 * (a - 1 - (a + 1) * cos);
      a2 = a + 1 - (a - 1) * cos - s;
      break;
    }
  }
  return { b0: b0 / a0, b1: b1 / a0, b2: b2 / a0, a1: a1 / a0, a2: a2 / a0 };
}

/** |H(f)|² of a biquad. */
function power(c: Biquad, cos1: number, cos2: number): number {
  const num = c.b0 * c.b0 + c.b1 * c.b1 + c.b2 * c.b2 + 2 * (c.b0 * c.b1 + c.b1 * c.b2) * cos1 + 2 * c.b0 * c.b2 * cos2;
  const den = 1 + c.a1 * c.a1 + c.a2 * c.a2 + 2 * (c.a1 + c.a1 * c.a2) * cos1 + 2 * c.a2 * cos2;
  return Math.max(num, 0) / Math.max(den, 1e-30);
}

/** What a band's sections are: one biquad, or a cut's Butterworth cascade. */
function sections(band: EqBand, rate: number): Biquad[] {
  if (!band.on) {
    return [];
  }
  if (band.kind === 'highPass' || band.kind === 'lowPass') {
    return butterworth(Math.min(3, Math.max(0, band.slope))).map(q => design(band.kind, rate, band.frequency, q, 0));
  }
  const gain = band.gainDb + (band.dynamicDb ?? 0);
  return gain === 0 ? [] : [design(band.kind, rate, band.frequency, band.q, gain)];
}

/** The EQ's response in dB at each of `frequencies`: `bands` in series, plus `outputDb`. */
export function eqResponse(
  bands: readonly EqBand[],
  frequencies: readonly number[],
  rate: number,
  outputDb = 0
): number[] {
  const all = bands.flatMap(band => sections(band, rate));
  return frequencies.map(hz => {
    const w = (2 * Math.PI * hz) / rate;
    const cos1 = Math.cos(w);
    const cos2 = Math.cos(2 * w);
    const total = all.reduce((product, c) => product * power(c, cos1, cos2), 1);
    return 10 * Math.log10(Math.max(total, 1e-30)) + outputDb;
  });
}

/** `count` frequencies evenly spaced in log frequency from `low` to `high` Hz, ends included. */
export function logFrequencies(count: number, low = 20, high = 20_000): number[] {
  return Array.from({ length: count }, (_, i) => low * (high / low) ** (i / Math.max(1, count - 1)));
}

/** The centres of `count` analyser bands (`SpectrumAnalyzer.Read`: 20 Hz to 20 kHz, evenly spaced in log frequency). */
export function analyserBands(count: number, low = 20, high = 20_000): number[] {
  return Array.from({ length: count }, (_, i) => low * (high / low) ** ((i + 0.5) / count));
}
