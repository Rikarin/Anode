import type { AudioRegion, PitchNote } from '@anode/api-client';
import { peakRange, type Waveform } from '@anode/timeline';

/** Cents of a semitone. */
const Semitone = 100;

/**
 * The pitch range the Audio Track Editor shows, in MIDI cents: the notes' and the curve's pitches with three semitones
 * around them, at least an octave and a half, on semitone lines. C2…C5 when there is nothing to show.
 */
export function centsRange(notes: readonly PitchNote[], curve: readonly (number | null)[] = []): [number, number] {
  let lo = Infinity;
  let hi = -Infinity;
  for (const n of notes) {
    lo = Math.min(lo, n.targetCents, n.detectedCents);
    hi = Math.max(hi, n.targetCents, n.detectedCents);
  }
  for (const c of curve) {
    if (c !== null) {
      lo = Math.min(lo, c);
      hi = Math.max(hi, c);
    }
  }
  if (!Number.isFinite(lo)) {
    return [4800, 8400];
  }
  let a = Math.floor(lo / Semitone) * Semitone - 3 * Semitone;
  let b = Math.ceil(hi / Semitone) * Semitone + 3 * Semitone;
  const short = 18 * Semitone - (b - a);
  if (short > 0) {
    a -= Math.floor(short / 2 / Semitone) * Semitone;
    b = a + 18 * Semitone;
  }
  return [Math.max(0, a), Math.min(12_700, b)];
}

/** The y of a pitch in a view `height` pixels tall over `range` (high pitches at the top). */
export function yAtCents(cents: number, range: readonly [number, number], height: number): number {
  const [lo, hi] = range;
  return ((hi - cents) / Math.max(1, hi - lo)) * height;
}

/** Pixels per semitone. */
export function rowHeight(range: readonly [number, number], height: number): number {
  return (height * Semitone) / Math.max(1, range[1] - range[0]);
}

/**
 * The analysed pitch curve as SVG polylines (one per voiced run), over file frames `[from, to)`: hop `h` is frame
 * `h × hop`; `x` and `y` place frames and cents.
 */
export function curvePolylines(
  pitch: readonly (number | null)[],
  hop: number,
  from: number,
  to: number,
  x: (frame: number) => number,
  y: (cents: number) => number
): string[] {
  const lines: string[] = [];
  let run: string[] = [];
  const end = () => {
    if (run.length > 1) {
      lines.push(run.join(' '));
    }
    run = [];
  };
  for (let h = Math.max(0, Math.ceil(from / hop)); h * hop < to && h < pitch.length; h++) {
    const c = pitch[h];
    if (c === null || c === undefined) {
      end();
      continue;
    }
    run.push(`${x(h * hop).toFixed(1)},${y(c).toFixed(1)}`);
  }
  end();
  return lines;
}

/**
 * A waveform as one SVG path (the min/max envelope, a column per pixel) over `width` × `height` pixels, with file frame
 * `frameAt(x)` at pixel x; empty where the waveform has no peaks.
 */
export function waveformPath(
  waveform: Waveform,
  frameAt: (x: number) => number,
  width: number,
  height: number
): string {
  const out = [0, 0];
  const mid = height / 2;
  const top: string[] = [];
  const bottom: string[] = [];
  for (let x = 0; x < width; x++) {
    const a = frameAt(x);
    const b = frameAt(x + 1);
    if (peakRange(waveform, Math.min(a, b), Math.max(a, b, Math.min(a, b) + 1), out)) {
      top.push(`${x},${(mid - (out[1] ?? 0) * mid).toFixed(1)}`);
      bottom.push(`${x},${(mid - (out[0] ?? 0) * mid).toFixed(1)}`);
    }
  }
  if (top.length === 0) {
    return '';
  }
  return `M${top.join(' L')} L${bottom.reverse().join(' L')} Z`;
}

/**
 * The audio region an audio editor shows: the first selected audio region, else the first (in time) of the selected
 * track, else none.
 */
export function editedRegion(
  regions: ReadonlyMap<string, AudioRegion>,
  selected: ReadonlySet<string>,
  track: string | null
): AudioRegion | null {
  for (const id of selected) {
    const region = regions.get(id);
    if (region) {
      return region;
    }
  }
  if (!track) {
    return null;
  }
  let first: AudioRegion | null = null;
  for (const region of regions.values()) {
    if (region.trackId === track && (!first || region.startTick < first.startTick)) {
      first = region;
    }
  }
  return first;
}
