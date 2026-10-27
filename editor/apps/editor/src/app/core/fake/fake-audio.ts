import { encodePeaks, peakLevelFor } from '@anode/timeline';

/** What a WAV header says about its audio. */
export interface WavInfo {
  sampleRate: number;
  channels: number;
  bitDepth: number;
  frames: number;
}

/**
 * Reads a RIFF/WAVE header (`fmt ` and the size of `data`), as the Server checks an import. Null for anything else.
 * Only the first `bytes` of the file are needed when `data` is the last chunk, which it nearly always is.
 */
export function readWavHeader(bytes: ArrayBuffer): WavInfo | null {
  const view = new DataView(bytes);
  const tag = (at: number) => (at + 4 <= bytes.byteLength ? String.fromCharCode(...new Uint8Array(bytes, at, 4)) : '');
  if (tag(0) !== 'RIFF' || tag(8) !== 'WAVE') {
    return null;
  }

  let format: Omit<WavInfo, 'frames'> | null = null;
  for (let at = 12; at + 8 <= bytes.byteLength;) {
    const id = tag(at);
    const size = view.getUint32(at + 4, true);
    if (id === 'fmt ' && at + 24 <= bytes.byteLength) {
      format = {
        channels: view.getUint16(at + 10, true),
        sampleRate: view.getUint32(at + 12, true),
        bitDepth: view.getUint16(at + 22, true)
      };
    } else if (id === 'data' && format) {
      const frameBytes = (format.channels * format.bitDepth) / 8;
      return frameBytes > 0 ? { ...format, frames: Math.floor(size / frameBytes) } : null;
    }

    at += 8 + size + (size % 2);
  }

  return null;
}

/** A small PCM WAV of silence (tests, and the e2e fixture's shape). */
export function silentWav(frames: number, sampleRate = 48_000, channels = 1, bitDepth = 16): ArrayBuffer {
  const dataBytes = (frames * channels * bitDepth) / 8;
  const buffer = new ArrayBuffer(44 + dataBytes);
  const view = new DataView(buffer);
  const write = (at: number, text: string) => {
    for (let i = 0; i < text.length; i++) {
      view.setUint8(at + i, text.charCodeAt(i));
    }
  };
  write(0, 'RIFF');
  view.setUint32(4, 36 + dataBytes, true);
  write(8, 'WAVE');
  write(12, 'fmt ');
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, channels, true);
  view.setUint32(24, sampleRate, true);
  view.setUint32(28, (sampleRate * channels * bitDepth) / 8, true);
  view.setUint16(32, (channels * bitDepth) / 8, true);
  view.setUint16(34, bitDepth, true);
  write(36, 'data');
  view.setUint32(40, dataBytes, true);
  return buffer;
}

/** A deterministic 32-bit hash of a string (FNV-1a), to give each fake file its own waveform. */
export function hash(text: string): number {
  let h = 0x811c9dc5;
  for (let i = 0; i < text.length; i++) {
    h = Math.imul(h ^ text.charCodeAt(i), 0x01000193);
  }
  return h >>> 0;
}

/**
 * Peaks for a fake file: a run of decaying hits with a little noise, seeded by `seed`, so every file looks like audio
 * and like itself at every zoom. Answered at the kept level nearest `framesPerPeak`, as the Server does.
 */
export function fakePeaks(seed: number, frames: number, channels: number, framesPerPeak: number): ArrayBuffer {
  const level = peakLevelFor(framesPerPeak);
  const count = Math.ceil(frames / level);
  const data = new Int16Array(count * channels * 2);
  const hitEvery = 6_000 + (seed % 18_000); // frames between hits
  let noise = seed || 1;
  for (let p = 0; p < count; p++) {
    const frame = p * level;
    const sinceHit = frame % hitEvery;
    const hit = Math.floor(frame / hitEvery);
    const accent = 0.45 + 0.5 * (((seed >>> (hit % 24)) & 7) / 7);
    const envelope = accent * Math.exp(-sinceHit / (hitEvery / 3.5)) + 0.04;
    for (let c = 0; c < channels; c++) {
      noise = (Math.imul(noise, 1_103_515_245) + 12_345) >>> 0;
      const jitter = 0.8 + 0.2 * ((noise >>> 16) / 65_535);
      const amplitude = Math.min(1, envelope * jitter) * 32_767;
      data[2 * (p * channels + c)] = -Math.round(amplitude * (0.9 + 0.1 * c));
      data[2 * (p * channels + c) + 1] = Math.round(amplitude);
    }
  }

  return encodePeaks({ channels, framesPerPeak: level, firstPeak: 0, data });
}
