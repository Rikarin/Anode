import { barMultiple, GridLevel, gridLines, gridStep } from './grid';
import { parseHexColor, type TimelineScene, type TimelineView } from './items';
import { LaneIntervalIndex } from './lane-interval-index';

/** How a rectangle is shaded: flat, a note (1 px darker border) or a region (border plus a name bar). */
export const RectStyle = { Flat: 0, Note: 1, Region: 2 } as const;
export type RectStyle = (typeof RectStyle)[keyof typeof RectStyle];

/** A view's colours, packed ({@link parseHexColor}). */
export interface TimelineTheme {
  readonly background: number;
  readonly laneAlternate: number;
  readonly gridBar: number;
  readonly gridBeat: number;
  readonly gridDivision: number;
  readonly ruler: number;
  readonly rulerTick: number;
  readonly rulerText: number;
  readonly playhead: number;
}

export const darkTheme: TimelineTheme = {
  background: parseHexColor('#1c1d21'),
  laneAlternate: parseHexColor('#18191c'),
  gridBar: parseHexColor('#3a3c44'),
  gridBeat: parseHexColor('#2b2d33'),
  gridDivision: parseHexColor('#232429'),
  ruler: parseHexColor('#26272c'),
  rulerTick: parseHexColor('#6b6e7a'),
  rulerText: parseHexColor('#b8bbc6'),
  playhead: parseHexColor('#f04b4b')
};

/** Height of the bar ruler across the top, in CSS pixels. */
export const RULER_HEIGHT = 24;

/**
 * One frame as backend-neutral draw lists: rectangles (CSS pixels, painter's order, in layers) and bar-number labels.
 * WebGL2 uploads the columns as instance attributes as they are; Canvas2D walks them. Grows, never shrinks, so a steady
 * view allocates nothing per frame.
 */
export class RectBatch {
  count = 0;
  /** x, y, width, height per rectangle. */
  rects = new Float32Array(4 * 1024);
  colors = new Uint32Array(1024);
  styles = new Uint8Array(1024);
  /** End (exclusive rectangle index) of each layer: background, grid, items, overlay. */
  readonly layerEnds: number[] = [];

  labelCount = 0;
  labelX = new Float32Array(64);
  labelY = new Float32Array(64);
  labelValue = new Int32Array(64);

  /** Items drawn this frame (the visible set after culling). */
  visibleItems = 0;

  reset(): void {
    this.count = 0;
    this.labelCount = 0;
    this.layerEnds.length = 0;
    this.visibleItems = 0;
  }

  /** Room for `extra` more rectangles. */
  reserve(extra: number): void {
    const needed = this.count + extra;
    if (needed <= this.colors.length) {
      return;
    }
    let capacity = this.colors.length;
    while (capacity < needed) {
      capacity *= 2;
    }
    this.rects = grow(this.rects, 4 * capacity);
    this.colors = grow(this.colors, capacity);
    this.styles = grow(this.styles, capacity);
  }

  push(x: number, y: number, w: number, h: number, color: number, style: RectStyle = RectStyle.Flat): void {
    this.reserve(1);
    const i = this.count++;
    this.rects[4 * i] = x;
    this.rects[4 * i + 1] = y;
    this.rects[4 * i + 2] = w;
    this.rects[4 * i + 3] = h;
    this.colors[i] = color;
    this.styles[i] = style;
  }

  endLayer(): void {
    this.layerEnds.push(this.count);
  }

  pushLabel(x: number, y: number, value: number): void {
    if (this.labelCount === this.labelValue.length) {
      this.labelX = grow(this.labelX, 2 * this.labelCount);
      this.labelY = grow(this.labelY, 2 * this.labelCount);
      this.labelValue = grow(this.labelValue, 2 * this.labelCount);
    }
    const i = this.labelCount++;
    this.labelX[i] = x;
    this.labelY[i] = y;
    this.labelValue[i] = value;
  }
}

function grow<T extends Float32Array | Uint32Array | Uint8Array | Int32Array>(array: T, length: number): T {
  const next = new (array.constructor as new (length: number) => T)(length);
  next.set(array);
  return next;
}

const BLACK_KEYS = new Set([1, 3, 6, 8, 10]);

/**
 * Turns a scene and a view into a {@link RectBatch}: lane backgrounds, the grid, the visible items (culled through a
 * {@link LaneIntervalIndex}, so the cost follows what is on screen, not the project size), then the ruler, bar numbers
 * and playhead on top. Pure CPU work shared by every backend, and the part a worker saves the main thread.
 */
export class FrameBuilder {
  readonly batch = new RectBatch();
  readonly index: LaneIntervalIndex;
  private readonly visible: Uint32Array;

  constructor(
    readonly scene: TimelineScene,
    readonly theme: TimelineTheme = darkTheme
  ) {
    this.index = new LaneIntervalIndex(scene.items, scene.laneCount);
    this.visible = new Uint32Array(this.index.size);
  }

  /** Builds the frame for `view` on a `width` × `height` CSS-pixel canvas. */
  build(view: TimelineView, width: number, height: number): RectBatch {
    const { scene, theme, batch } = this;
    const { ppq, beatsPerBar, laneCount, items, kind } = scene;
    const { originTick, pixelsPerTick: ppt, firstLane, laneHeight } = view;
    const endTick = originTick + width / ppt;
    const laneTop = RULER_HEIGHT;
    const laneFrom = Math.max(0, Math.floor(firstLane));
    const laneTo = Math.min(laneCount, Math.ceil(firstLane + (height - laneTop) / laneHeight));
    const laneY = (lane: number) => laneTop + (lane - firstLane) * laneHeight;
    batch.reset();

    // Background: one rectangle, then the alternate (or black-key) lanes.
    batch.push(0, 0, width, height, theme.background);
    for (let lane = laneFrom; lane < laneTo; lane++) {
      const shaded = kind === 'notes' ? BLACK_KEYS.has((laneCount - 1 - lane) % 12) : lane % 2 === 1;
      if (shaded) {
        batch.push(0, laneY(lane), width, laneHeight, theme.laneAlternate);
      }
    }
    batch.endLayer();

    // Grid.
    const lines = gridLines(originTick, endTick, gridStep(ppq, beatsPerBar, ppt, 8), ppq, beatsPerBar);
    batch.reserve(lines.length);
    for (const { tick, level } of lines) {
      const color =
        level === GridLevel.Bar ? theme.gridBar : level === GridLevel.Beat ? theme.gridBeat : theme.gridDivision;
      batch.push(Math.round((tick - originTick) * ppt), laneTop, 1, height - laneTop, color);
    }
    batch.endLayer();

    // Items: query, then project. Clamp x to just outside the canvas so float32 keeps sub-pixel precision.
    const found = this.index.query(originTick, endTick, laneFrom, laneTo, this.visible);
    const inset = kind === 'notes' ? 0.5 : 1;
    const style = kind === 'notes' ? RectStyle.Note : RectStyle.Region;
    batch.reserve(found);
    for (let k = 0; k < found; k++) {
      const id = this.visible[k] ?? 0;
      const start = items.start[id] ?? 0;
      const x0 = Math.max(-2, (start - originTick) * ppt);
      const x1 = Math.min(width + 2, Math.max((start + (items.length[id] ?? 0) - originTick) * ppt, x0 + 1));
      const y = laneY(items.lane[id] ?? 0) + inset;
      batch.push(x0, y, x1 - x0, laneHeight - 2 * inset, items.color[id] ?? 0, style);
    }
    batch.visibleItems = found;
    batch.endLayer();

    // Ruler, bar numbers, playhead.
    const bar = ppq * beatsPerBar;
    batch.push(0, 0, width, laneTop, theme.ruler);
    batch.push(0, laneTop - 1, width, 1, theme.gridBar);
    const labelEvery = barMultiple(bar * ppt, 40) * bar;
    for (const { tick, level } of lines) {
      if (level !== GridLevel.Division) {
        const x = Math.round((tick - originTick) * ppt);
        const tall = level === GridLevel.Bar;
        batch.push(x, tall ? 4 : laneTop - 7, 1, tall ? laneTop - 4 : 6, theme.rulerTick);
        if (tall && tick % labelEvery === 0) {
          batch.pushLabel(x + 3, 4, tick / bar + 1);
        }
      }
    }
    const playheadX = Math.round((view.playheadTick - originTick) * ppt);
    if (playheadX >= -5 && playheadX <= width + 5) {
      batch.push(playheadX - 4, 0, 9, 6, theme.playhead);
      batch.push(playheadX, 0, 1, height, theme.playhead);
    }
    batch.endLayer();
    return batch;
  }
}
