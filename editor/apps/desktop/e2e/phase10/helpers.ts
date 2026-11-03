import { execFileSync, spawn } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

/*
 * Helpers for the Phase 10 golden path (docs/11-phase-10.md): DNS-SD discovery, the media tools (FFmpeg and FFprobe
 * through workstream E's helper), the EBU ADM Renderer (`ear`, through workstream D's helper), and the WAV and signal
 * maths the checks need. The external tools are the INTEGRATOR's to provide (see each `INTEGRATOR` note); the
 * signal maths is plain TypeScript.
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
 * INTEGRATOR: macOS uses `dns-sd -B` / `dns-sd -L` (the Phase 8 `DNSServiceRegister` path advertises it); Linux would
 * use `avahi-browse -rpt _anode._tcp`, Windows `dns-sd.exe` from Bonjour. The parsing matches macOS's `dns-sd` output
 * of 2026 (`… Add … local. _anode._tcp. <name>` and `<name>._anode._tcp.local. can be reached at <host>:<port>`).
 */
export async function browseAnode(projectName: string | null, ms = 4000): Promise<FoundService> {
  if (process.platform !== 'darwin') {
    throw new Error('INTEGRATOR: DNS-SD browsing is implemented for macOS (dns-sd) only.');
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
    const txt: Record<string, string> = {};
    for (const pair of lookup.matchAll(/(\w+)=(\S*)/g)) {
      if (pair[1]) {
        txt[pair[1]] = pair[2] ?? '';
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
 * INTEGRATOR: `ANODE_MEDIA_HELPER` (default `scripts/media-check.mjs`, run with node) wraps E's `Anode.Media`
 * (`MovieMedia.GenerateTestMovieAsync`) and the fetched `ffmpeg`/`ffprobe` (`scripts/fetch-ffmpeg.mjs`):
 * - `make-movie <out.mov> <seconds> <fps> <cut,cut,…>` writes the golden path's movie (a frame counter and timecode
 *   burned in, a 1 kHz beep of 50 ms at every whole second, scene cuts at the frames) and prints a {@link TestMovie};
 * - `probe <movie>` prints FFprobe's `-show_streams -of json`;
 * - `audio <movie> <out.wav>` extracts the first audio stream as 48 kHz 32-bit float WAV;
 * - `video-md5 <movie>` prints the MD5 of the video stream's packets (`-c:v copy -f md5`), for "stream copied".
 */
const MediaHelper = process.env['ANODE_MEDIA_HELPER'] ?? join(Root, 'scripts/media-check.mjs');

function media(args: string[]): string {
  return execFileSync('node', [MediaHelper, ...args], { encoding: 'utf8' });
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
 * INTEGRATOR: `ANODE_EAR_HELPER` (default `scripts/ear-check.py`, run with `ANODE_EAR_PYTHON`, default python3, in the
 * venv D's opt-in tests set up with `ear`):
 * - `validate <file.wav>`: exit 0 when `chna` and `axml` parse and validate per ITU-R BS.2076 (ear's own checks);
 * - `render <file.wav> <layout> <out.wav>`: `ear-render -s <layout>` (e.g. `4+7+0`), the speakers in BS.2051 order.
 */
const EarHelper = process.env['ANODE_EAR_HELPER'] ?? join(Root, 'scripts/ear-check.py');
const EarPython = process.env['ANODE_EAR_PYTHON'] ?? 'python3';

export function earValidate(path: string): void {
  execFileSync(EarPython, [EarHelper, 'validate', path], { stdio: 'inherit' });
}

export function earRender(path: string, layout: string, out: string): Wav {
  execFileSync(EarPython, [EarHelper, 'render', path, layout, out], { stdio: 'inherit' });
  return readWav(out);
}

// ---- WAV files

export interface Wav {
  sampleRate: number;
  channels: Float32Array[];
}

/** Reads PCM 16/24/32-bit and float 32-bit WAV (WAVE_FORMAT_EXTENSIBLE too; BW64/RF64 `ds64` is not needed here). */
export function readWav(path: string): Wav {
  const bytes = readFileSync(path);
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  let offset = 12;
  let format = 1;
  let channelCount = 0;
  let sampleRate = 0;
  let bits = 0;
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
      return { sampleRate, channels };
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

// ---- signal maths

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
