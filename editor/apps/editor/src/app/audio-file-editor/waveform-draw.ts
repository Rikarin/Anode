import { type FileView, frameAtX, PointsFromPixelsPerSample, type RulerTick, xAtFrame } from './audio-file-geometry';
import { columnPeaks, type SampleTiles } from './sample-tiles';

/** The ruler's height over the lanes (CSS px). */
export const RulerHeight = 20;

/** The colours the canvas draws with, read from the design tokens (both themes). */
export interface WaveformColors {
  background: string;
  ruler: string;
  rulerText: string;
  hairline: string;
  zero: string;
  wave: string;
}

/** Reads {@link WaveformColors} from the tokens in effect at `element`. */
export function waveformColors(element: Element): WaveformColors {
  const styles = getComputedStyle(element);
  // Anode's values are the plain variables (anode-theme.css re-tunes `--surface-sunken`…); xUI's `--color-*` aliases
  // are the fallback, then the dark values.
  const token = (name: string, fallback: string) =>
    styles.getPropertyValue(`--${name}`).trim() || styles.getPropertyValue(`--color-${name}`).trim() || fallback;
  return {
    background: token('surface-sunken', '#171718'),
    ruler: token('surface-raised', '#2c2c2f'),
    rulerText: token('foreground-muted', '#aeaeb4'),
    hairline: token('border', '#3a3a3e'),
    zero: token('border-muted', '#2c2c2f'),
    wave: token('primary', '#0071e3')
  };
}

export interface WaveformScene {
  width: number;
  height: number;
  view: FileView;
  totalFrames: number;
  channels: number;
  bucket: number;
  ticks: readonly RulerTick[];
}

/** Draws the ruler and one lane per channel: min/max columns, or the samples as a line (and points when far in). */
export function drawWaveform(
  context: CanvasRenderingContext2D,
  scene: WaveformScene,
  tiles: Pick<SampleTiles, 'bucketPeak' | 'sample'>,
  colors: WaveformColors
): void {
  const { width, height, view, totalFrames, channels, bucket } = scene;
  context.fillStyle = colors.background;
  context.fillRect(0, 0, width, height);

  // The ruler.
  context.fillStyle = colors.ruler;
  context.fillRect(0, 0, width, RulerHeight);
  context.fillStyle = colors.hairline;
  context.fillRect(0, RulerHeight - 1, width, 1);
  context.font = '10px -apple-system, BlinkMacSystemFont, "SF Pro Text", system-ui, sans-serif';
  context.textBaseline = 'top';
  for (const tick of scene.ticks) {
    const x = Math.round(tick.x) + 0.5;
    context.fillStyle = colors.rulerText;
    context.fillRect(x - 0.5, tick.major ? 4 : RulerHeight - 5, 1, tick.major ? RulerHeight - 5 : 4);
    if (tick.major) {
      context.fillText(tick.label, x + 3, 3);
    }
  }

  const lanes = Math.max(1, channels);
  const laneHeight = (height - RulerHeight) / lanes;
  const fileEnd = xAtFrame(totalFrames, view);
  for (let channel = 0; channel < lanes; channel++) {
    const top = RulerHeight + channel * laneHeight;
    const middle = top + laneHeight / 2;
    const half = laneHeight / 2 - 2;
    if (channel > 0) {
      context.fillStyle = colors.hairline;
      context.fillRect(0, Math.round(top), width, 1);
    }
    context.fillStyle = colors.zero;
    context.fillRect(0, Math.round(middle), Math.min(width, fileEnd), 1);

    if (bucket === 1) {
      drawSamples(context, tiles, channel, view, width, totalFrames, middle, half, colors.wave);
    } else {
      const { min, max } = columnPeaks(tiles, channel, view, width, bucket, totalFrames);
      context.fillStyle = colors.wave;
      for (let x = 0; x < width; x++) {
        const lo = min[x] ?? Number.NaN;
        const hi = max[x] ?? Number.NaN;
        if (Number.isNaN(lo) || Number.isNaN(hi)) {
          continue;
        }
        const y1 = middle - clamp(hi) * half;
        const y2 = middle - clamp(lo) * half;
        context.fillRect(x, y1, 1, Math.max(1, y2 - y1));
      }
    }
  }
}

function drawSamples(
  context: CanvasRenderingContext2D,
  tiles: Pick<SampleTiles, 'sample'>,
  channel: number,
  view: FileView,
  width: number,
  totalFrames: number,
  middle: number,
  half: number,
  color: string
): void {
  const first = Math.max(0, Math.floor(frameAtX(0, view)));
  const last = Math.min(totalFrames - 1, Math.ceil(frameAtX(width, view)));
  const points = 1 / view.framesPerPixel >= PointsFromPixelsPerSample;
  context.strokeStyle = color;
  context.fillStyle = color;
  context.lineWidth = 1.25;
  context.beginPath();
  let drawing = false;
  for (let frame = first; frame <= last; frame++) {
    const value = tiles.sample(channel, frame);
    if (value === undefined) {
      drawing = false;
      continue;
    }
    const x = xAtFrame(frame, view);
    const y = middle - clamp(value) * half;
    if (drawing) {
      context.lineTo(x, y);
    } else {
      context.moveTo(x, y);
      drawing = true;
    }
  }
  context.stroke();
  if (points) {
    for (let frame = first; frame <= last; frame++) {
      const value = tiles.sample(channel, frame);
      if (value !== undefined) {
        context.beginPath();
        context.arc(xAtFrame(frame, view), middle - clamp(value) * half, 2.5, 0, Math.PI * 2);
        context.fill();
      }
    }
  }
}

function clamp(value: number): number {
  return Math.max(-1, Math.min(1, value));
}
