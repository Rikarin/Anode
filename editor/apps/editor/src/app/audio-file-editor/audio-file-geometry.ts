/**
 * The Audio File Editor's geometry (docs/08-phase-7.md, "Audio File Editor"): the view (the frame at the left edge and
 * frames per pixel), frame ↔ x, zoom and scroll limits, which bucket of `/samples` to ask for at a zoom, the tiles
 * that cover the visible range, the ruler's ticks, and the selection's gestures. Frames are sample frames of the file.
 */

/** The view: the frame at x = 0 (fractional when zoomed past single samples) and frames per CSS pixel. */
export interface FileView {
  start: number;
  framesPerPixel: number;
}

/** A selected range of frames, `end` exclusive (`end > start`). */
export interface FrameRange {
  start: number;
  end: number;
}

/** The deepest zoom: 1/32 frame per pixel (32 px a sample). */
export const MinFramesPerPixel = 1 / 32;
/** At most this many values per channel per `/samples` request (the Server's limit). */
export const MaxSampleValues = 65_536;
/** Values per channel in one cached tile. */
export const TileValues = 4096;
/** Below this many frames per pixel the waveform is drawn sample by sample (raw samples, bucket 1). */
export const RawBelowFramesPerPixel = 2;
/** From this many pixels a sample, each sample is drawn as a point on the line. */
export const PointsFromPixelsPerSample = 6;

export function xAtFrame(frame: number, view: FileView): number {
  return (frame - view.start) / view.framesPerPixel;
}

export function frameAtX(x: number, view: FileView): number {
  return view.start + x * view.framesPerPixel;
}

/** The coarsest zoom: the whole file fits `width`. */
export function maxFramesPerPixel(totalFrames: number, width: number): number {
  return Math.max(MinFramesPerPixel, totalFrames / Math.max(1, width));
}

/** A view kept inside the file: zoom between one sample per 32 px and the whole file, scroll within the file. */
export function clampView(view: FileView, totalFrames: number, width: number): FileView {
  const framesPerPixel = Math.min(
    maxFramesPerPixel(totalFrames, width),
    Math.max(MinFramesPerPixel, view.framesPerPixel)
  );
  const visible = framesPerPixel * Math.max(1, width);
  const start = Math.max(0, Math.min(Math.max(0, totalFrames - visible), view.start));
  return { start, framesPerPixel };
}

/** The view showing the whole file. */
export function fitView(totalFrames: number, width: number): FileView {
  return { start: 0, framesPerPixel: maxFramesPerPixel(totalFrames, width) };
}

/** Zooms by `factor` (> 1 zooms in) keeping the frame under `anchorX` where it is. */
export function zoomView(
  view: FileView,
  factor: number,
  anchorX: number,
  totalFrames: number,
  width: number
): FileView {
  const anchor = frameAtX(anchorX, view);
  const framesPerPixel = view.framesPerPixel / factor;
  return clampView({ start: anchor - anchorX * framesPerPixel, framesPerPixel }, totalFrames, width);
}

/** The view that shows `range` with a small margin. */
export function viewOfRange(range: FrameRange, totalFrames: number, width: number): FileView {
  const length = Math.max(1, range.end - range.start);
  const framesPerPixel = (length * 1.1) / Math.max(1, width);
  return clampView({ start: range.start - length * 0.05, framesPerPixel }, totalFrames, width);
}

/** The whole frames the view shows (at least partly): `[start, end)`, inside the file. */
export function visibleRange(view: FileView, width: number, totalFrames: number): FrameRange {
  const start = Math.max(0, Math.floor(view.start));
  const end = Math.min(totalFrames, Math.ceil(view.start + Math.max(1, width) * view.framesPerPixel) + 1);
  return { start, end: Math.max(start, end) };
}

/**
 * The `/samples` bucket for a zoom: 1 (raw samples) while a pixel holds fewer than two frames, else the largest power
 * of two not above the frames per pixel, so each pixel spans one or more whole min/max pairs.
 */
export function bucketFor(framesPerPixel: number): number {
  if (framesPerPixel < RawBelowFramesPerPixel) {
    return 1;
  }
  return 2 ** Math.floor(Math.log2(framesPerPixel));
}

/** The tiles (each {@link TileValues} values of `bucket` frames) that cover `range`. */
export function tilesFor(range: FrameRange, bucket: number): number[] {
  const span = TileValues * bucket;
  if (range.end <= range.start) {
    return [];
  }
  const first = Math.floor(range.start / span);
  const last = Math.floor((range.end - 1) / span);
  return Array.from({ length: last - first + 1 }, (_, i) => first + i);
}

/** A tile's request: its first frame and length in frames (clipped to the file). */
export function tileRequest(tile: number, bucket: number, totalFrames: number): { start: number; frames: number } {
  const start = tile * TileValues * bucket;
  return { start, frames: Math.max(0, Math.min(TileValues * bucket, totalFrames - start)) };
}

/** A ruler tick: its frame and label; `major` ticks carry the label. */
export interface RulerTick {
  frame: number;
  x: number;
  label: string;
  major: boolean;
}

const Steps125 = [1, 2, 5];

/** The smallest 1-2-5 step (× 10ⁿ, from `floor`) at least `minimum`. */
function niceStep(minimum: number, floor: number): number {
  for (let exponent = Math.floor(Math.log10(Math.max(floor, minimum))) - 1; ; exponent++) {
    for (const m of Steps125) {
      const step = m * 10 ** exponent;
      if (step >= floor && step >= minimum) {
        return step;
      }
    }
  }
}

/**
 * The ruler's ticks over the view: in time (seconds, labels like `0:01.250`) or in samples (frame numbers), a labelled
 * tick at least `spacing` px apart with four minor ticks between.
 */
export function rulerTicks(
  view: FileView,
  width: number,
  sampleRate: number,
  unit: 'time' | 'samples',
  spacing = 90
): RulerTick[] {
  const minimumFrames = spacing * view.framesPerPixel;
  const stepFrames =
    unit === 'samples' ? niceStep(minimumFrames, 1) : niceStep(minimumFrames / sampleRate, 1 / sampleRate) * sampleRate;
  const minor = stepFrames / 5;
  const ticks: RulerTick[] = [];
  const first = Math.floor(view.start / minor);
  const last = Math.ceil(frameAtX(width, view) / minor);
  for (let i = Math.max(0, first); i <= last && ticks.length < 2000; i++) {
    const frame = i * minor;
    const major = i % 5 === 0;
    ticks.push({
      frame,
      x: xAtFrame(frame, view),
      major,
      label: major ? (unit === 'samples' ? String(Math.round(frame)) : formatTime(frame, sampleRate, stepFrames)) : ''
    });
  }
  return ticks;
}

/**
 * A frame as clock time `m:ss.mmm`; with `resolution` (frames a step) under a millisecond, as many more decimals as
 * the step needs.
 */
export function formatTime(frame: number, sampleRate: number, resolution = sampleRate / 1000): string {
  const seconds = frame / sampleRate;
  const stepSeconds = resolution / sampleRate;
  const decimals = Math.min(6, Math.max(3, Math.ceil(-Math.log10(Math.max(1e-9, stepSeconds)) - 1e-9)));
  const minutes = Math.floor(seconds / 60);
  const rest = seconds - minutes * 60;
  const text = rest.toFixed(decimals);
  return `${minutes}:${rest < 10 ? '0' : ''}${text}`;
}

/** A range from two frames in either order, clipped to the file; null when empty. */
export function rangeOf(a: number, b: number, totalFrames: number): FrameRange | null {
  const start = Math.max(0, Math.min(totalFrames, Math.round(Math.min(a, b))));
  const end = Math.max(0, Math.min(totalFrames, Math.round(Math.max(a, b))));
  return end > start ? { start, end } : null;
}

/** ⇧-click: moves the selection's nearer edge to `frame` (or selects from `frame` to itself when nothing is). */
export function extendRange(range: FrameRange | null, frame: number, totalFrames: number): FrameRange | null {
  if (!range) {
    return null;
  }
  const f = Math.max(0, Math.min(totalFrames, Math.round(frame)));
  const fromStart = Math.abs(f - range.start) <= Math.abs(f - range.end);
  return fromStart ? rangeOf(f, range.end, totalFrames) : rangeOf(range.start, f, totalFrames);
}
