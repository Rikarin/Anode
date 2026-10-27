import { type RectBatch, RectStyle } from '../frame-builder';
import { colorToCss } from '../items';
import { RendererUnavailableError, type RenderTarget, type TimelineRenderer } from './renderer';

type Context2D = CanvasRenderingContext2D | OffscreenCanvasRenderingContext2D;

/**
 * The fallback: the same {@link RectBatch} through Canvas2D. Rectangles are grouped by colour within each layer so a
 * frame costs one path and one fill per colour rather than a `fillStyle` change per item.
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
    const { context, groups } = this;
    context.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    const { rects, colors, styles } = batch;
    let begin = 0;
    for (const end of batch.layerEnds) {
      for (const list of groups.values()) {
        list.length = 0;
      }
      for (let i = begin; i < end; i++) {
        const color = colors[i] ?? 0;
        let list = groups.get(color);
        if (!list) {
          list = [];
          groups.set(color, list);
        }
        list.push(i);
      }
      for (const [color, list] of groups) {
        if (list.length === 0) {
          continue;
        }
        const style = (styles[list[0] ?? 0] ?? RectStyle.Flat) as RectStyle;
        if (style === RectStyle.Flat) {
          this.fill(list, rects, color, 0, 0);
        } else {
          this.fill(list, rects, darken(color), 0, 0);
          this.fill(list, rects, color, 1, 0);
          if (style === RectStyle.Region) {
            this.fill(list, rects, lighten(color), 1, 11);
          }
        }
      }
      begin = end;
    }

    context.fillStyle = '#b8bbc6';
    context.font = '10px sans-serif';
    context.textBaseline = 'top';
    for (let i = 0; i < batch.labelCount; i++) {
      context.fillText(String(batch.labelValue[i] ?? 0), batch.labelX[i] ?? 0, (batch.labelY[i] ?? 0) + 1);
    }
  }

  finish(): void {
    this.context.getImageData(0, 0, 1, 1);
  }

  dispose(): void {
    this.groups.clear();
  }

  /** One path of the listed rectangles, inset by `inset` and cut to `maxHeight` (if non-zero), filled with `color`. */
  private fill(list: number[], rects: Float32Array, color: number, inset: number, maxHeight: number): void {
    const { context } = this;
    context.beginPath();
    for (const i of list) {
      const w = (rects[4 * i + 2] ?? 0) - 2 * inset;
      const h = (rects[4 * i + 3] ?? 0) - 2 * inset;
      if (w > 0 && h > 0) {
        context.rect(
          (rects[4 * i] ?? 0) + inset,
          (rects[4 * i + 1] ?? 0) + inset,
          w,
          maxHeight ? Math.min(h, maxHeight) : h
        );
      }
    }
    context.fillStyle = this.cssColor(color);
    context.fill();
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

/** The shader's border shade (RGB × 0.55). */
function darken(color: number): number {
  return scale(color, c => c * 0.55);
}

/** The shader's name-bar tint (30% towards white). */
function lighten(color: number): number {
  return scale(color, c => c + (255 - c) * 0.3);
}

function scale(color: number, f: (channel: number) => number): number {
  const r = Math.round(f(color & 0xff));
  const g = Math.round(f((color >>> 8) & 0xff));
  const b = Math.round(f((color >>> 16) & 0xff));
  return ((color & 0xff000000) + (b << 16) + (g << 8) + r) >>> 0;
}
