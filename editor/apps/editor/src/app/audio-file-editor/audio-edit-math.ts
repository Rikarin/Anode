import type { AudioEditOperation, LoopSearchResult } from '@anode/api-client';

/**
 * The Audio File Editor's sample maths (docs/08-phase-7.md, "Audio File Editor"): the operations as the fake Server
 * applies them (the real ones are the Server's, Anode.Dsp.Editing), zero-crossing snap for the selection, and a
 * simple loop search. Channels are arrays of samples −1…1.
 */

/** A fade's gain at `x` (0…1 through the fade, rising) for curve −1…1 (0 linear; > 0 fast start; < 0 slow start). */
export function fadeGain(x: number, curve: number): number {
  const t = Math.min(1, Math.max(0, x));
  const c = Math.min(1, Math.max(-1, curve));
  if (Math.abs(c) < 1e-9) {
    return t;
  }
  const exponent = Math.pow(4, -c);
  return Math.pow(t, exponent);
}

/** Applies an operation to a copy of `channels`; trim and rate conversion change the length. */
export function applyAudioEdit(channels: readonly Float32Array[], op: AudioEditOperation): Float32Array[] {
  const frames = channels[0]?.length ?? 0;
  const start = Math.max(0, Math.min(frames, Math.round(op.startFrame)));
  const end = Math.max(start, Math.min(frames, Math.round(op.endFrame)));
  const out = channels.map(c => Float32Array.from(c));
  const each = (f: (channel: Float32Array) => void) => out.forEach(f);
  switch (op.kind) {
    case 'normalize': {
      let peak = 0;
      for (const c of out) {
        for (let i = start; i < end; i++) {
          peak = Math.max(peak, Math.abs(c[i] ?? 0));
        }
      }
      if (peak > 0) {
        const gain = Math.pow(10, (op.value ?? -1) / 20) / peak;
        each(c => scale(c, start, end, () => gain));
      }
      return out;
    }
    case 'gain': {
      const gain = Math.pow(10, (op.value ?? 0) / 20);
      each(c => scale(c, start, end, () => gain));
      return out;
    }
    case 'fadeIn':
      each(c => scale(c, start, end, i => fadeGain((i - start) / Math.max(1, end - start), op.curve ?? 0)));
      return out;
    case 'fadeOut':
      each(c => scale(c, start, end, i => fadeGain((end - i) / Math.max(1, end - start), op.curve ?? 0)));
      return out;
    case 'reverse':
      each(c => c.subarray(start, end).reverse());
      return out;
    case 'invert':
      each(c => scale(c, start, end, () => -1));
      return out;
    case 'silence':
      each(c => c.fill(0, start, end));
      return out;
    case 'removeDc':
      each(c => {
        let sum = 0;
        for (let i = start; i < end; i++) {
          sum += c[i] ?? 0;
        }
        const mean = end > start ? sum / (end - start) : 0;
        for (let i = start; i < end; i++) {
          c[i] = (c[i] ?? 0) - mean;
        }
      });
      return out;
    case 'trim':
      return out.map(c => c.slice(start, end));
    case 'convertRate':
      return out;
  }
}

/** Linear-interpolated rate conversion (the fake's; the Server uses the bounce resampler). */
export function convertRate(channels: readonly Float32Array[], from: number, to: number): Float32Array[] {
  return channels.map(c => {
    const length = Math.max(1, Math.round((c.length * to) / from));
    const result = new Float32Array(length);
    for (let i = 0; i < length; i++) {
      const x = (i * from) / to;
      const a = Math.floor(x);
      const t = x - a;
      result[i] = (c[a] ?? 0) * (1 - t) + (c[Math.min(c.length - 1, a + 1)] ?? 0) * t;
    }
    return result;
  });
}

function scale(channel: Float32Array, start: number, end: number, gain: (i: number) => number): void {
  for (let i = start; i < end; i++) {
    channel[i] = (channel[i] ?? 0) * gain(i);
  }
}

/**
 * The zero crossing nearest `frame` in `samples` (frames from `offset`): the first frame of a sign change (a sample
 * at 0 counts), searching at most `radius` frames either way; `frame` itself when there is none.
 */
export function nearestZeroCrossing(samples: ArrayLike<number>, frame: number, offset = 0, radius = 2048): number {
  const index = Math.round(frame - offset);
  const crosses = (i: number) => {
    const a = samples[i - 1];
    const b = samples[i];
    return a !== undefined && b !== undefined && (b === 0 || Math.sign(a) !== Math.sign(b));
  };
  for (let d = 0; d <= radius; d++) {
    if (index - d >= 1 && index - d < samples.length && crosses(index - d)) {
      return index - d + offset;
    }
    if (index + d >= 1 && index + d < samples.length && crosses(index + d)) {
      return index + d + offset;
    }
  }
  return Math.round(frame);
}

/**
 * Loop search (the fake's): candidate loop lengths scored by the normalised correlation of the range's start with
 * what follows each candidate end, best first. The Server's (Anode.Dsp.Editing.LoopSearch) is the real one.
 */
export function searchLoops(
  channel: Float32Array,
  startFrame: number,
  endFrame: number,
  count = 5,
  minFrames = 480
): LoopSearchResult[] {
  const start = Math.max(0, Math.round(startFrame));
  const end = Math.min(channel.length, Math.round(endFrame));
  const window = Math.min(256, Math.floor((end - start) / 4));
  if (window < 8) {
    return [];
  }
  const candidates: LoopSearchResult[] = [];
  const stride = Math.max(1, Math.floor((end - start) / 4000));
  for (let length = Math.max(minFrames, window); start + length + window <= end; length += stride) {
    let dot = 0;
    let a2 = 0;
    let b2 = 0;
    for (let i = 0; i < window; i++) {
      const a = channel[start + i] ?? 0;
      const b = channel[start + length + i] ?? 0;
      dot += a * b;
      a2 += a * a;
      b2 += b * b;
    }
    const score = a2 > 0 && b2 > 0 ? Math.max(0, dot / Math.sqrt(a2 * b2)) : 0;
    candidates.push({ startFrame: start, endFrame: start + length, score });
  }
  // Keep local maxima, best first.
  const peaks = candidates.filter(
    (c, i) => c.score >= (candidates[i - 1]?.score ?? -1) && c.score >= (candidates[i + 1]?.score ?? -1)
  );
  return peaks.sort((a, b) => b.score - a.score || a.endFrame - b.endFrame).slice(0, Math.max(1, Math.min(20, count)));
}
