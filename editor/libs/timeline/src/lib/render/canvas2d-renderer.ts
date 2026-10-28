import {
  CornerRadius,
  NAME_BAR_HEIGHT,
  NAME_BAR_MIN_HEIGHT,
  RECT_SHAPE_MASK,
  type RectBatch,
  RectStyle,
  RegionShade
} from '../frame-builder';
import { colorToCss } from '../items';
import { RendererUnavailableError, type RenderTarget, type TimelineRenderer } from './renderer';
import { TEXT_FONT } from './webgl2-renderer';

type Context2D = CanvasRenderingContext2D | OffscreenCanvasRenderingContext2D;

/**
 * The fallback: the same {@link RectBatch} through Canvas2D. Runs of flat rectangles are grouped by colour, so a frame
 * costs one path and one fill per colour rather than a `fillStyle` change per item; the shaped ones (notes, regions,
 * the cycle area, the playhead's head, the ruler) are drawn one by one with the WebGL2 shader's shading, in order.
 */
export class Canvas2DRenderer implements TimelineRenderer {
  readonly backend = 'canvas2d' as const;
  readonly description = 'Canvas2D';
  private readonly context: Context2D;
  private readonly groups = new Map<number, number[]>();
  private readonly css = new Map<number, string>();
  private dpr = 1;

  constructor(private readonly canvas: RenderTarget) {
    const context = canvas.getContext('2d', { alpha: false });
    if (!context) {
      throw new RendererUnavailableError('canvas2d', 'This canvas has no 2D context.');
    }
    this.context = context;
  }

  resize(width: number, height: number, dpr: number): void {
    this.dpr = dpr;
    this.canvas.width = Math.round(width * dpr);
    this.canvas.height = Math.round(height * dpr);
  }

  draw(batch: RectBatch): void {
    const { context } = this;
    context.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    const { styles } = batch;
    let begin = 0;
    for (const end of batch.layerEnds) {
      for (let i = begin; i < end; i++) {
        const style = styles[i] ?? RectStyle.Flat;
        if ((style & RECT_SHAPE_MASK) === RectStyle.Flat) {
          this.collect(batch, i);
        } else {
          this.flush(batch);
          this.shaped(batch, i, style);
        }
      }
      this.flush(batch);
      begin = end;
    }

    context.font = TEXT_FONT;
    context.textBaseline = 'top';
    context.textAlign = 'left';
    for (let i = 0; i < batch.textCount; i++) {
      context.fillStyle = this.cssColor(batch.textColor[i] ?? 0xffffffff);
      context.fillText(batch.texts[i] ?? '', batch.textX[i] ?? 0, (batch.textY[i] ?? 0) + 1);
    }
  }

  finish(): void {
    this.context.getImageData(0, 0, 1, 1);
  }

  dispose(): void {
    this.groups.clear();
  }

  private collect(batch: RectBatch, i: number): void {
    const color = batch.colors[i] ?? 0;
    let list = this.groups.get(color);
    if (!list) {
      list = [];
      this.groups.set(color, list);
    }
    list.push(i);
  }

  /** Fills the flat rectangles collected so far, one path per colour. */
  private flush(batch: RectBatch): void {
    const { context, groups } = this;
    for (const [color, list] of groups) {
      if (list.length === 0) {
        continue;
      }
      context.beginPath();
      for (const i of list) {
        const w = batch.rects[4 * i + 2] ?? 0;
        const h = batch.rects[4 * i + 3] ?? 0;
        if (w > 0 && h > 0) {
          context.rect(batch.rects[4 * i] ?? 0, batch.rects[4 * i + 1] ?? 0, w, h);
        }
      }
      context.fillStyle = this.cssColor(color);
      context.fill();
      list.length = 0;
    }
  }

  /** One shaped rectangle, as the WebGL2 fragment shader shades it. */
  private shaped(batch: RectBatch, i: number, style: number): void {
    const { context } = this;
    const x = batch.rects[4 * i] ?? 0;
    const y = batch.rects[4 * i + 1] ?? 0;
    const w = batch.rects[4 * i + 2] ?? 0;
    const h = batch.rects[4 * i + 3] ?? 0;
    if (w <= 0 || h <= 0) {
      return;
    }
    const color = batch.colors[i] ?? 0;
    const shape = style & RECT_SHAPE_MASK;
    const selected = (style & RectStyle.Selected) !== 0;

    if (shape === RectStyle.Ruler) {
      context.fillStyle = this.gradient(color, y, h, [
        [0, 1.12],
        [1, 0.9]
      ]);
      context.fillRect(x, y, w, h);
      return;
    }
    if (shape === RectStyle.PlayheadHead) {
      context.beginPath();
      context.moveTo(x, y);
      context.lineTo(x + w, y);
      context.lineTo(x + w / 2, y + h);
      context.closePath();
      context.fillStyle = this.cssColor(color);
      context.fill();
      return;
    }

    const radius = Math.min(
      shape === RectStyle.Note
        ? CornerRadius.Note
        : shape === RectStyle.Cycle
          ? CornerRadius.Cycle
          : CornerRadius.Region,
      Math.min(w, h) / 2
    );
    // The edge: the whole shape in the edge shade (or white when selected), then the inside over it.
    const ring = selected ? (shape === RectStyle.Note ? 1.25 : 1.75) : 1;
    const edge =
      selected && shape !== RectStyle.Cycle
        ? 0xffffffff
        : scale(color, shape === RectStyle.Region ? RegionShade.edge : shape === RectStyle.Note ? 0.5 : 0.62);
    context.beginPath();
    context.roundRect(x, y, w, h, radius);
    context.fillStyle = this.cssColor(edge);
    context.fill();
    if (w <= 2 * ring || h <= 2 * ring) {
      return;
    }

    const ix = x + ring;
    const iy = y + ring;
    const iw = w - 2 * ring;
    const ih = h - 2 * ring;
    context.beginPath();
    context.roundRect(ix, iy, iw, ih, Math.max(0, radius - ring));
    if (shape === RectStyle.Region) {
      const named = h >= NAME_BAR_MIN_HEIGHT;
      const bar = named ? NAME_BAR_HEIGHT / h : 0;
      const stops: [number, number][] = named
        ? [
            [0, RegionShade.nameTop],
            [bar - 0.001, RegionShade.nameBottom],
            [bar, RegionShade.bodyTop],
            [1, RegionShade.bodyBottom]
          ]
        : [
            [0, RegionShade.bodyTop],
            [1, RegionShade.bodyBottom]
          ];
      context.fillStyle = this.gradient(color, y, h, stops);
      context.fill();
      if (!selected) {
        context.fillStyle = this.cssColor(lighten(scale(color, named ? RegionShade.nameTop : RegionShade.bodyTop)));
        context.fillRect(ix + radius / 2, y + 1, Math.max(0, iw - radius), 1);
      }
    } else {
      context.fillStyle = this.gradient(
        color,
        y,
        h,
        shape === RectStyle.Note
          ? [
              [0, 1.06],
              [1, 0.84]
            ]
          : [
              [0, 1.08],
              [1, 0.86]
            ]
      );
      context.fill();
    }
  }

  /** A vertical gradient of `color` scaled by each stop's factor, over `y`…`y + h`. */
  private gradient(color: number, y: number, h: number, stops: readonly [number, number][]): CanvasGradient {
    const gradient = this.context.createLinearGradient(0, y, 0, y + h);
    for (const [offset, factor] of stops) {
      gradient.addColorStop(Math.min(1, Math.max(0, offset)), this.cssColor(scale(color, factor)));
    }
    return gradient;
  }

  private cssColor(color: number): string {
    let css = this.css.get(color);
    if (css === undefined) {
      css = colorToCss(color);
      this.css.set(color, css);
    }
    return css;
  }
}

/** A packed colour with its RGB multiplied by `factor` (clamped). */
function scale(color: number, factor: number): number {
  const f = (c: number) => Math.min(255, Math.round(c * factor));
  return channels(color, f);
}

/** The shader's top highlight: {@link RegionShade.gloss} of the way towards white. */
function lighten(color: number): number {
  return channels(color, c => Math.round(c + (255 - c) * RegionShade.gloss));
}

function channels(color: number, f: (channel: number) => number): number {
  const r = f(color & 0xff);
  const g = f((color >>> 8) & 0xff);
  const b = f((color >>> 16) & 0xff);
  return ((color & 0xff000000) + (b << 16) + (g << 8) + r) >>> 0;
}
