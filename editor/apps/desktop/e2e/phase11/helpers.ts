import { amplitude, db, rms } from '../phase10/helpers';

/*
 * Signal checks for Phase 11's golden path (docs/12-phase-11.md): BS.1770 loudness and an oversampled true peak for
 * the Mastering Assistant, harmonics for saturation and the Valve character, a pitch tracker for the T3 instruments and
 * Pitch Source, a −60 dB decay time, and the interaural level and time differences of a binaural render. Pure
 * TypeScript on the WAVs the Server bounces; no helper processes.
 */

/** A biquad's coefficients (normalised, a0 = 1). */
interface Biquad {
  b0: number;
  b1: number;
  b2: number;
  a1: number;
  a2: number;
}

function filter(samples: Float32Array, q: Biquad): Float32Array {
  const out = new Float32Array(samples.length);
  let x1 = 0;
  let x2 = 0;
  let y1 = 0;
  let y2 = 0;
  for (let i = 0; i < samples.length; i++) {
    const x = samples[i] ?? 0;
    const y = q.b0 * x + q.b1 * x1 + q.b2 * x2 - q.a1 * y1 - q.a2 * y2;
    out[i] = y;
    x2 = x1;
    x1 = x;
    y2 = y1;
    y1 = y;
  }
  return out;
}

/** BS.1770's K-weighting at a sample rate: the high shelf (+4 dB above ~1.5 kHz), then the RLB high-pass. */
function kWeighting(rate: number): [Biquad, Biquad] {
  // The standard's analogue prototypes, bilinear-transformed (as libebur128 derives them for any rate).
  const shelf = (() => {
    const f0 = 1681.974450955533;
    const g = 3.999843853973347;
    const q = 0.7071752369554196;
    const k = Math.tan((Math.PI * f0) / rate);
    const vh = 10 ** (g / 20);
    const vb = vh ** 0.4996667741545416;
    const a0 = 1 + k / q + k * k;
    return {
      b0: (vh + (vb * k) / q + k * k) / a0,
      b1: (2 * (k * k - vh)) / a0,
      b2: (vh - (vb * k) / q + k * k) / a0,
      a1: (2 * (k * k - 1)) / a0,
      a2: (1 - k / q + k * k) / a0
    };
  })();
  const highPass = (() => {
    const f0 = 38.13547087602444;
    const q = 0.5003270373238773;
    const k = Math.tan((Math.PI * f0) / rate);
    const a0 = 1 + k / q + k * k;
    return { b0: 1 / a0, b1: -2 / a0, b2: 1 / a0, a1: (2 * (k * k - 1)) / a0, a2: (1 - k / q + k * k) / a0 };
  })();
  return [shelf, highPass];
}

/** Integrated loudness (LUFS) of a stereo or mono signal: 400 ms blocks, 75 % overlap, the absolute and relative gates. */
export function integratedLufs(channels: readonly Float32Array[], rate: number): number {
  const [shelf, highPass] = kWeighting(rate);
  const weighted = channels.map(c => filter(filter(c, shelf), highPass));
  const block = Math.round(0.4 * rate);
  const hop = Math.round(0.1 * rate);
  const powers: number[] = [];
  const length = weighted[0]?.length ?? 0;
  for (let start = 0; start + block <= length; start += hop) {
    let power = 0;
    for (const channel of weighted) {
      let sum = 0;
      for (let i = start; i < start + block; i++) {
        sum += (channel[i] ?? 0) ** 2;
      }
      power += sum / block;
    }
    powers.push(power);
  }
  const loudness = (p: number) => -0.691 + 10 * Math.log10(p);
  const absolute = powers.filter(p => loudness(p) > -70);
  if (!absolute.length) {
    return -Infinity;
  }
  const threshold = loudness(absolute.reduce((a, b) => a + b, 0) / absolute.length) - 10;
  const gated = absolute.filter(p => loudness(p) > threshold);
  return loudness(gated.reduce((a, b) => a + b, 0) / gated.length);
}

/** True peak (dBTP): the largest sample of a 4× windowed-sinc oversampling (BS.1770's annex 2, approximately). */
export function truePeakDbtp(channels: readonly Float32Array[]): number {
  const taps = 12;
  let peak = 0;
  for (const channel of channels) {
    for (let i = 0; i < channel.length; i++) {
      peak = Math.max(peak, Math.abs(channel[i] ?? 0));
      for (let phase = 1; phase < 4; phase++) {
        const t = phase / 4;
        let value = 0;
        for (let k = -taps + 1; k <= taps; k++) {
          const x = k - t;
          const sinc = Math.sin(Math.PI * x) / (Math.PI * x);
          const window = 0.5 + 0.5 * Math.cos((Math.PI * x) / taps);
          value += (channel[i + k] ?? 0) * sinc * window;
        }
        peak = Math.max(peak, Math.abs(value));
      }
    }
  }
  return db(peak);
}

/** The levels (dB, relative to the fundamental) of a test tone's 2nd to 6th harmonics. */
export function harmonics(samples: Float32Array, fundamental: number, rate: number): number[] {
  const base = amplitude(samples, fundamental, rate);
  return [2, 3, 4, 5, 6].map(n => db(amplitude(samples, fundamental * n, rate) / base));
}

/** The power sum of the even harmonics (2nd, 4th, 6th) relative to the fundamental, in dB. */
export function evenHarmonicsDb(samples: Float32Array, fundamental: number, rate: number): number {
  const [h2 = -200, , h4 = -200, , h6 = -200] = harmonics(samples, fundamental, rate);
  return 10 * Math.log10(10 ** (h2 / 10) + 10 ** (h4 / 10) + 10 ** (h6 / 10));
}

/** Total harmonic distortion (2nd…6th) of a test tone, as a ratio. */
export function thd(samples: Float32Array, fundamental: number, rate: number): number {
  return Math.sqrt(harmonics(samples, fundamental, rate).reduce((sum, h) => sum + 10 ** (h / 10), 0));
}

/** A note's pitch (Hz) by autocorrelation over a steady stretch, refined by parabolic interpolation. */
export function pitchOf(samples: Float32Array, rate: number, from = 0, to = samples.length, low = 30, high = 2000) {
  const frame = samples.subarray(from, Math.min(to, from + Math.round(rate * 0.5)));
  const minLag = Math.floor(rate / high);
  const maxLag = Math.ceil(rate / low);
  let best = minLag;
  let bestValue = -Infinity;
  const values: number[] = [];
  for (let lag = minLag; lag <= maxLag && lag < frame.length / 2; lag++) {
    let sum = 0;
    for (let i = 0; i + lag < frame.length; i++) {
      sum += (frame[i] ?? 0) * (frame[i + lag] ?? 0);
    }
    values[lag] = sum;
    if (sum > bestValue) {
      bestValue = sum;
      best = lag;
    }
  }
  const a = values[best - 1] ?? bestValue;
  const c = values[best + 1] ?? bestValue;
  const shift = (a - c) / (2 * (a - 2 * bestValue + c) || 1);
  return rate / (best + shift);
}

/** The cents between a measured frequency and a MIDI note's. */
export function centsFrom(hz: number, note: number): number {
  return 1200 * Math.log2(hz / (440 * 2 ** ((note - 69) / 12)));
}

/** Seconds from a note's peak until its 50 ms RMS falls 60 dB below it (null when it never does). */
export function decayTime(samples: Float32Array, rate: number): number | null {
  const window = Math.round(rate * 0.05);
  const levels: number[] = [];
  for (let start = 0; start + window <= samples.length; start += window) {
    levels.push(db(rms(samples, start, start + window)));
  }
  const peakAt = levels.indexOf(Math.max(...levels));
  const target = (levels[peakAt] ?? 0) - 60;
  const below = levels.findIndex((level, i) => i > peakAt && level <= target);
  return below < 0 ? null : ((below - peakAt) * window) / rate;
}

/** The interaural level difference (dB, left over right) and time difference (ms, right later) of a binaural render. */
export function interauralDifferences(left: Float32Array, right: Float32Array, rate: number) {
  const ild = db(rms(left) / rms(right));
  const max = Math.round(rate * 0.0015);
  let best = 0;
  let bestValue = -Infinity;
  for (let lag = -max; lag <= max; lag++) {
    let sum = 0;
    for (let i = Math.max(0, -lag); i < left.length && i + lag < right.length; i++) {
      sum += (left[i] ?? 0) * (right[i + lag] ?? 0);
    }
    if (sum > bestValue) {
      bestValue = sum;
      best = lag;
    }
  }
  return { ild, itdMs: (best / rate) * 1000 };
}
