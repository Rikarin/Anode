/** `PeakFormat` (engine/src/Anode.Contracts/Peaks.cs): the binary body of `GET /project/files/{fileId}/peaks`. */
export const PeakFormat = {
  /** "ANPK", little endian. */
  Magic: 0x4b504e41,
  Version: 1,
  HeaderBytes: 24,
  /** The resolutions the Server keeps, in frames per peak; a request is answered at the nearest one. */
  Levels: [64, 256, 1024, 4096, 16384] as readonly number[],
  /** Full scale of a peak sample. */
  FullScale: 32767
} as const;

/**
 * Waveform peaks of one file at one resolution: for each peak (each `framesPerPeak` frames from `firstPeak`), for each
 * channel, a min and a max sample (i16, full scale ±32767).
 */
export interface Waveform {
  readonly channels: number;
  readonly framesPerPeak: number;
  /** The index of the first peak in `data` (a partial request starts later than 0). */
  readonly firstPeak: number;
  readonly peakCount: number;
  /** `[peak][channel][min, max]`, interleaved. */
  readonly data: Int16Array;
}

/** Why a peaks body could not be read. */
export class PeakFormatError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'PeakFormatError';
  }
}

/** Reads a `PeakFormat` body. The returned waveform views `buffer`'s bytes when they are aligned, else a copy. */
export function parsePeaks(buffer: ArrayBuffer): Waveform {
  if (buffer.byteLength < PeakFormat.HeaderBytes) {
    throw new PeakFormatError(
      `A peaks body has a ${PeakFormat.HeaderBytes}-byte header; this one has ${buffer.byteLength} bytes.`
    );
  }

  const view = new DataView(buffer);
  if (view.getUint32(0, true) !== PeakFormat.Magic) {
    throw new PeakFormatError('Not a peaks body (no ANPK magic).');
  }

  const version = view.getUint16(4, true);
  if (version !== PeakFormat.Version) {
    throw new PeakFormatError(`Peaks format version ${version} is not supported (expected ${PeakFormat.Version}).`);
  }

  const channels = view.getUint16(6, true);
  const framesPerPeak = view.getUint32(8, true);
  const firstPeak = view.getUint32(12, true);
  const peakCount = Number(view.getBigUint64(16, true));
  if (channels < 1 || framesPerPeak < 1) {
    throw new PeakFormatError('A peaks body needs at least one channel and one frame per peak.');
  }

  const values = peakCount * channels * 2;
  if (buffer.byteLength < PeakFormat.HeaderBytes + values * 2) {
    throw new PeakFormatError(`The peaks body is truncated: ${peakCount} peaks need ${values * 2} bytes of data.`);
  }

  // The header is 24 bytes, so the samples are 2-byte aligned and an Int16Array can view them in place.
  const data = new Int16Array(buffer, PeakFormat.HeaderBytes, values);
  return { channels, framesPerPeak, firstPeak, peakCount, data };
}

/** Encodes peaks in `PeakFormat` (tests and the fake Server). `data` is `[peak][channel][min, max]`. */
export function encodePeaks(waveform: Omit<Waveform, 'peakCount'> & { peakCount?: number }): ArrayBuffer {
  const peakCount = waveform.peakCount ?? waveform.data.length / (2 * waveform.channels);
  const buffer = new ArrayBuffer(PeakFormat.HeaderBytes + peakCount * waveform.channels * 4);
  const view = new DataView(buffer);
  view.setUint32(0, PeakFormat.Magic, true);
  view.setUint16(4, PeakFormat.Version, true);
  view.setUint16(6, waveform.channels, true);
  view.setUint32(8, waveform.framesPerPeak, true);
  view.setUint32(12, waveform.firstPeak, true);
  view.setBigUint64(16, BigInt(peakCount), true);
  new Int16Array(buffer, PeakFormat.HeaderBytes).set(waveform.data.subarray(0, peakCount * waveform.channels * 2));
  return buffer;
}

/** The kept resolution closest to `framesPerPixel` without being coarser, so a column never skips peaks. */
export function peakLevelFor(framesPerPixel: number, levels: readonly number[] = PeakFormat.Levels): number {
  let best = levels[0] ?? 64;
  for (const level of levels) {
    if (level <= framesPerPixel) {
      best = level;
    }
  }
  return best;
}

/**
 * Waveforms by key (a file), each at one or more resolutions. The renderer draws from the finest resolution that is
 * not finer than it needs, falling back to whatever it has.
 */
export class WaveformSet {
  private readonly byKey = new Map<number, Waveform[]>();

  /** Adds (or replaces) one resolution of a key's waveform. */
  add(key: number, waveform: Waveform): void {
    const levels = (this.byKey.get(key) ?? []).filter(w => w.framesPerPeak !== waveform.framesPerPeak);
    levels.push(waveform);
    levels.sort((a, b) => a.framesPerPeak - b.framesPerPeak);
    this.byKey.set(key, levels);
  }

  delete(key: number): void {
    this.byKey.delete(key);
  }

  has(key: number): boolean {
    return this.byKey.has(key);
  }

  /** The resolution to draw `framesPerPixel` with: the coarsest not coarser than a pixel, else the finest there is. */
  best(key: number, framesPerPixel: number): Waveform | undefined {
    const levels = this.byKey.get(key);
    if (!levels?.length) {
      return undefined;
    }

    let best = levels[0];
    for (const level of levels) {
      if (level.framesPerPeak <= framesPerPixel) {
        best = level;
      }
    }
    return best;
  }
}

/**
 * The lowest and highest sample over frames `[from, to)` of `waveform`, across its channels, as fractions of full scale
 * (-1…1), written to `out` (`[min, max]`). Returns false (and leaves `out`) where the waveform has no peaks there.
 */
export function peakRange(waveform: Waveform, from: number, to: number, out: Float32Array | number[]): boolean {
  const { framesPerPeak, firstPeak, peakCount, channels, data } = waveform;
  const first = Math.floor(from / framesPerPeak);
  const p0 = Math.max(firstPeak, first);
  const p1 = Math.min(firstPeak + peakCount, Math.max(first + 1, Math.ceil(to / framesPerPeak)));
  if (p0 >= p1) {
    return false;
  }

  let min = Infinity;
  let max = -Infinity;
  for (let p = p0 - firstPeak, end = p1 - firstPeak; p < end; p++) {
    for (let c = 0; c < channels; c++) {
      const at = 2 * (p * channels + c);
      min = Math.min(min, data[at] ?? 0);
      max = Math.max(max, data[at + 1] ?? 0);
    }
  }
  out[0] = min / PeakFormat.FullScale;
  out[1] = max / PeakFormat.FullScale;
  return true;
}
