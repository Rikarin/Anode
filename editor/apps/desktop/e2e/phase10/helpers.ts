import { execFileSync, spawn } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

/*
 * Helpers for the Phase 10 golden path (docs/11-phase-10.md): DNS-SD discovery, the media tools (FFmpeg and FFprobe
 * through workstream E's helper), the EBU ADM Renderer (`ear`, through workstream D's helper), and the WAV and signal
 * maths the checks need. The external tools are the repository's scripts (`scripts/media-check.mjs`,
 * `scripts/ear-check.py`) over the pinned FFmpeg and `ear`; the signal maths is plain TypeScript.
 */

const Root = resolve(__dirname, '../../../../..');

// ---- discovery (DNS-SD, `_anode._tcp`)

/** A service found by DNS-SD: its instance name, where it is, and its TXT record. */
export interface FoundService {
  name: string;
  host: string;
  port: number;
  txt: Record<string, string>;
}

/** Runs a command for at most `ms`, returning what it printed (DNS-SD browsers never exit on their own). */
function runFor(command: string, args: string[], ms: number): Promise<string> {
  return new Promise((resolvePromise, reject) => {
    const child = spawn(command, args);
    let out = '';
    child.stdout.on('data', (chunk: Buffer) => (out += chunk.toString()));
    child.on('error', reject);
    setTimeout(() => {
      child.kill();
      resolvePromise(out);
    }, ms);
  });
}

/**
 * Finds the studio as a remote would: browses `_anode._tcp` and resolves the instance whose TXT `project` is
 * `projectName` (any, when null).
 *
 * macOS uses `dns-sd -B` / `dns-sd -L` (the Phase 8 `DNSServiceRegister` path advertises it); Linux would
 * use `avahi-browse -rpt _anode._tcp`, Windows `dns-sd.exe` from Bonjour. The parsing matches macOS's `dns-sd` output
 * of 2026 (`… Add … local. _anode._tcp. <name>` and `<name>._anode._tcp.local. can be reached at <host>:<port>`).
 */
export async function browseAnode(projectName: string | null, ms = 4000): Promise<FoundService> {
  if (process.platform !== 'darwin') {
    throw new Error('DNS-SD browsing is implemented for macOS (dns-sd) only.');
  }
  const browse = await runFor('dns-sd', ['-B', '_anode._tcp', 'local.'], ms);
  const names = [...browse.matchAll(/\sAdd\s+\S+\s+\S+\s+local\.\s+_anode\._tcp\.\s+(.+)$/gm)].map(
    m => m[1]?.trim() ?? ''
  );
  for (const name of names) {
    const lookup = await runFor('dns-sd', ['-L', name, '_anode._tcp', 'local.'], ms);
    const at = /can be reached at (\S+?)\.?:(\d+)/.exec(lookup);
    if (!at?.[1] || !at[2]) {
      continue;
    }
    // The TXT record is the line after the address: key=value pairs split at unescaped spaces (dns-sd writes a space
    // in a value as `\ ` and other bytes as `\DDD`).
    const record =
      lookup.split('\n')[lookup.split('\n').findIndex(line => line.includes('can be reached at')) + 1] ?? '';
    const txt: Record<string, string> = {};
    for (const pair of record.trim().split(/(?<!\\) /)) {
      const equals = pair.indexOf('=');
      if (equals > 0) {
        txt[pair.slice(0, equals)] = pair
          .slice(equals + 1)
          .replace(/\\(\d{3})/g, (_, code: string) => String.fromCharCode(Number(code)))
          .replace(/\\(.)/g, '$1');
      }
    }
    if (projectName === null || txt['project'] === projectName) {
      return { name, host: at[1], port: Number(at[2]), txt };
    }
  }
  throw new Error(`No _anode._tcp service${projectName ? ` for ${projectName}` : ''} found (${names.length} seen).`);
}

// ---- media (workstream E's helper around its LGPL FFmpeg)

/** What the test movie is: the spec the helper made it from (E's `TestMovieSpec`). */
export interface TestMovie {
  path: string;
  frameRate: number;
  frames: number;
  durationSeconds: number;
  sceneCutFrames: number[];
  /** The beeps' onsets, in seconds of movie time (one a second). */
  beepSeconds: number[];
  beepHz: number;
}

/**
 * `ANODE_MEDIA_HELPER` (default `scripts/media-check.mjs`, run with node) wraps E's `Anode.Media`
 * (`MovieMedia.GenerateTestMovieAsync`) and the fetched `ffmpeg`/`ffprobe` (`scripts/fetch-ffmpeg.mjs`):
 * - `make-movie <out.mov> <seconds> <fps> <cut,cut,…>` writes the golden path's movie (a frame counter and timecode
 *   burned in, a 1 kHz beep of 50 ms at every whole second, scene cuts at the frames) and prints a {@link TestMovie};
 * - `probe <movie>` prints FFprobe's `-show_streams -of json`;
 * - `audio <movie> <out.wav>` extracts the first audio stream as 48 kHz 32-bit float WAV;
 * - `video-md5 <movie>` prints the MD5 of the video stream's packets (`-c:v copy -f md5`), for "stream copied".
 */
const MediaHelper = process.env['ANODE_MEDIA_HELPER'] ?? join(Root, 'scripts/media-check.mjs');

/** This environment without the dynamic loader's search variables (Homebrew's DYLD_LIBRARY_PATH would load GPL libraries). */
function withoutLoader(): NodeJS.ProcessEnv {
  return Object.fromEntries(
    Object.entries(process.env).filter(
      ([key]) => !key.startsWith('DYLD_') && key !== 'LD_LIBRARY_PATH' && key !== 'LD_PRELOAD'
    )
  );
}

function media(args: string[]): string {
  return execFileSync('node', [MediaHelper, ...args], { encoding: 'utf8', env: withoutLoader() });
}

export function makeTestMovie(path: string, seconds: number, fps: number, cuts: readonly number[]): TestMovie {
  return JSON.parse(media(['make-movie', path, String(seconds), String(fps), cuts.join(',')])) as TestMovie;
}

export function probeStreams(path: string): { codec_type: string; codec_name: string; nb_frames?: string }[] {
  return (JSON.parse(media(['probe', path])) as { streams: { codec_type: string; codec_name: string }[] }).streams;
}

export function extractAudio(movie: string, wav: string): Wav {
  media(['audio', movie, wav]);
  return readWav(wav);
}

export function videoMd5(movie: string): string {
  return media(['video-md5', movie]).trim();
}

// ---- the EBU ADM Renderer (workstream D's opt-in tests' helper)

/**
 * `ANODE_EAR_HELPER` (default `scripts/ear-check.py`, run with `ANODE_EAR_PYTHON`, default python3, in the
 * venv D's opt-in tests set up with `ear`):
 * - `validate <file.wav>`: exit 0 when `chna` and `axml` parse and validate per ITU-R BS.2076 (ear's own checks);
 * - `render <file.wav> <layout> <out.wav>`: `ear-render -s <layout>` (e.g. `4+7+0`), the speakers in BS.2051 order.
 */
const EarHelper = process.env['ANODE_EAR_HELPER'] ?? join(Root, 'scripts/ear-check.py');
const EarPython = process.env['ANODE_EAR_PYTHON'] ?? 'python3';

export function earValidate(path: string): void {
  execFileSync(EarPython, ['-I', EarHelper, 'validate', path], { stdio: 'inherit', env: withoutLoader() });
}

export function earRender(path: string, layout: string, out: string): Wav {
  execFileSync(EarPython, ['-I', EarHelper, 'render', path, layout, out], { stdio: 'inherit', env: withoutLoader() });
  return readWav(out);
}

// ---- WAV files

export interface Wav {
  sampleRate: number;
  channels: Float32Array[];
}

/**
 * WAVE_FORMAT_EXTENSIBLE speaker bits and the ADM labels they stand for. A file stores its channels by bit; Anode's
 * buffers, ADM and `ear` order them by layout (7.x: the sides, M±090, before the rears, M±135).
 */
const MaskLabels: [number, string][] = [
  [0x1, 'M+030'],
  [0x2, 'M-030'],
  [0x4, 'M+000'],
  [0x8, 'LFE1'],
  [0x10, 'M+135'],
  [0x20, 'M-135'],
  [0x200, 'M+090'],
  [0x400, 'M-090'],
  [0x1000, 'U+045'],
  [0x4000, 'U-045'],
  [0x8000, 'U+135'],
  [0x20000, 'U-135']
];
/** 7.1.4 and 7.1 in ADM order (the layouts the golden path reads). */
const AdmOrders = [
  ['M+030', 'M-030', 'M+000', 'LFE1', 'M+090', 'M-090', 'M+135', 'M-135', 'U+045', 'U-045', 'U+135', 'U-135'],
  ['M+030', 'M-030', 'M+000', 'LFE1', 'M+090', 'M-090', 'M+135', 'M-135']
];

/** An extensible file's channels in ADM order when its mask is one of {@link AdmOrders}, else as stored. */
function admOrder(channels: Float32Array[], mask: number): Float32Array[] {
  const stored = MaskLabels.filter(([bit]) => (mask & bit) !== 0).map(([, label]) => label);
  const order = AdmOrders.find(o => o.length === channels.length && o.every(l => stored.includes(l)));
  return order && stored.length === channels.length
    ? order.map(label => channels[stored.indexOf(label)] as Float32Array)
    : channels;
}

/**
 * Reads PCM 16/24/32-bit and float 32-bit WAV; a WAVE_FORMAT_EXTENSIBLE surround file's channels come back in ADM
 * order (BW64/RF64 `ds64` is not needed here).
 */
export function readWav(path: string): Wav {
  const bytes = readFileSync(path);
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  let offset = 12;
  let format = 1;
  let channelCount = 0;
  let sampleRate = 0;
  let bits = 0;
  let mask = 0;
  while (offset + 8 <= bytes.length) {
    const id = bytes.toString('ascii', offset, offset + 4);
    const size = view.getUint32(offset + 4, true);
    const body = offset + 8;
    if (id === 'fmt ') {
      format = view.getUint16(body, true);
      channelCount = view.getUint16(body + 2, true);
      sampleRate = view.getUint32(body + 4, true);
      bits = view.getUint16(body + 14, true);
      if (format === 0xfffe) {
        mask = view.getUint32(body + 20, true);
        format = view.getUint16(body + 24, true); // the sub-format GUID's first two bytes
      }
    } else if (id === 'data') {
      const frameBytes = (bits / 8) * channelCount;
      const frames = Math.floor(Math.min(size, bytes.length - body) / frameBytes);
      const channels = Array.from({ length: channelCount }, () => new Float32Array(frames));
      for (let f = 0; f < frames; f++) {
        for (let c = 0; c < channelCount; c++) {
          const at = body + f * frameBytes + (c * bits) / 8;
          const channel = channels[c] as Float32Array;
          if (format === 3) {
            channel[f] = view.getFloat32(at, true);
          } else if (bits === 16) {
            channel[f] = view.getInt16(at, true) / 32768;
          } else if (bits === 24) {
            const v = view.getUint8(at) | (view.getUint8(at + 1) << 8) | (view.getInt8(at + 2) << 16);
            channel[f] = v / 8388608;
          } else {
            channel[f] = view.getInt32(at, true) / 2147483648;
          }
        }
      }
      return { sampleRate, channels: admOrder(channels, mask) };
    }
    offset = body + size + (size % 2);
  }
  throw new Error(`${path}: no data chunk.`);
}

/** Writes a mono 32-bit float WAV. */
export function writeWav(path: string, samples: Float32Array, sampleRate: number): void {
  const data = samples.length * 4;
  const buffer = Buffer.alloc(44 + data);
  buffer.write('RIFF', 0, 'ascii');
  buffer.writeUInt32LE(36 + data, 4);
  buffer.write('WAVEfmt ', 8, 'ascii');
  buffer.writeUInt32LE(16, 16);
  buffer.writeUInt16LE(3, 20);
  buffer.writeUInt16LE(1, 22);
  buffer.writeUInt32LE(sampleRate, 24);
  buffer.writeUInt32LE(sampleRate * 4, 28);
  buffer.writeUInt16LE(4, 32);
  buffer.writeUInt16LE(32, 34);
  buffer.write('data', 36, 'ascii');
  buffer.writeUInt32LE(data, 40);
  samples.forEach((s, i) => buffer.writeFloatLE(s, 44 + i * 4));
  writeFileSync(path, buffer);
}

/** A sine of `hz` at `levelDb` dBFS (peak) for `seconds`. */
export function sine(hz: number, levelDb: number, seconds: number, sampleRate: number): Float32Array {
  const amplitude = 10 ** (levelDb / 20);
  return Float32Array.from(
    { length: Math.round(seconds * sampleRate) },
    (_, i) => amplitude * Math.sin((2 * Math.PI * hz * i) / sampleRate)
  );
}

/** White noise (a fixed seed) at `levelDb` dBFS peak, for `seconds`. */
export function noise(levelDb: number, seconds: number, sampleRate: number): Float32Array {
  const amplitude = 10 ** (levelDb / 20);
  let seed = 12345;
  return Float32Array.from({ length: Math.round(seconds * sampleRate) }, () => {
    seed = (Math.imul(seed, 1103515245) + 12345) >>> 0;
    return amplitude * ((seed / 4294967296) * 2 - 1);
  });
}

// ---- signal maths

/** The peak amplitude of the `hz` component of `samples` (a single-bin DFT over whole periods). */
export function amplitude(samples: Float32Array, hz: number, sampleRate: number): number {
  const n = Math.floor(Math.floor((samples.length * hz) / sampleRate) * (sampleRate / hz));
  let re = 0;
  let im = 0;
  for (let i = 0; i < n; i++) {
    const phase = (2 * Math.PI * hz * i) / sampleRate;
    re += (samples[i] ?? 0) * Math.cos(phase);
    im += (samples[i] ?? 0) * Math.sin(phase);
  }
  return (2 * Math.hypot(re, im)) / Math.max(1, n);
}

export function rms(samples: Float32Array, from = 0, to = samples.length): number {
  let sum = 0;
  for (let i = from; i < to; i++) {
    sum += (samples[i] ?? 0) ** 2;
  }
  return Math.sqrt(sum / Math.max(1, to - from));
}

export function db(value: number): number {
  return 20 * Math.log10(Math.max(value, 1e-12));
}

/** Each whole second's RMS in dB. */
export function rmsPerSecond(samples: Float32Array, sampleRate: number): number[] {
  const out: number[] = [];
  for (let s = 0; (s + 1) * sampleRate <= samples.length; s++) {
    out.push(db(rms(samples, s * sampleRate, (s + 1) * sampleRate)));
  }
  return out;
}

/**
 * A steady tone's frequency (zero crossings, interpolated between samples, over whole periods) and its level as a
 * sine's peak in dBFS (from its RMS).
 */
export function toneOf(samples: Float32Array, sampleRate: number): { hz: number; peakDb: number } {
  const crossings: number[] = [];
  for (let i = 1; i < samples.length; i++) {
    const a = samples[i - 1] ?? 0;
    const b = samples[i] ?? 0;
    if (a < 0 && b >= 0) {
      crossings.push(i - 1 + a / (a - b));
    }
  }
  const first = crossings[0] ?? 0;
  const last = crossings.at(-1) ?? 0;
  const hz = crossings.length > 1 ? ((crossings.length - 1) * sampleRate) / (last - first) : 0;
  return { hz, peakDb: db(rms(samples) * Math.SQRT2) };
}

/** Where a burst starts (the first sample of each run above `threshold` after at least `gap` seconds below it). */
export function onsets(samples: Float32Array, sampleRate: number, threshold = 0.05, gap = 0.2): number[] {
  const out: number[] = [];
  let quiet = gap * sampleRate;
  for (let i = 0; i < samples.length; i++) {
    if (Math.abs(samples[i] ?? 0) >= threshold) {
      if (quiet >= gap * sampleRate) {
        out.push(i);
      }
      quiet = 0;
    } else {
      quiet++;
    }
  }
  return out;
}

/** The lag (in samples, `b` behind `a` positive) that best lines `b` up with `a`, searched within ±`max`. */
export function bestLag(a: Float32Array, b: Float32Array, max: number): number {
  let best = 0;
  let bestScore = -Infinity;
  for (let lag = -max; lag <= max; lag++) {
    let score = 0;
    for (let i = Math.max(0, -lag); i < a.length && i + lag < b.length; i += 4) {
      score += (a[i] ?? 0) * (b[i + lag] ?? 0);
    }
    if (score > bestScore) {
      bestScore = score;
      best = lag;
    }
  }
  return best;
}

/** Interaural level and time differences of a binaural pair: left minus right in dB, right's delay in ms. */
export function interaural(
  left: Float32Array,
  right: Float32Array,
  sampleRate: number
): { ildDb: number; itdMs: number } {
  const lag = bestLag(left, right, Math.round(0.002 * sampleRate));
  return { ildDb: db(rms(left)) - db(rms(right)), itdMs: (lag / sampleRate) * 1000 };
}
