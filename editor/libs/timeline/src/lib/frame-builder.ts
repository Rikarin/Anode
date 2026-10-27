import { barMultiple, GridLevel, gridLines, gridStep } from './grid';
import { ItemFlag, type ItemOverride, parseHexColor, type TimelineScene, type TimelineView } from './items';
import { LaneIntervalIndex } from './lane-interval-index';
import { peakRange, WaveformSet } from './waveform';

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
  /** The cycle area in the ruler, on and off. */
  readonly cycle: number;
  readonly cycleOff: number;
  /** Lanes after the project end. */
  readonly afterEnd: number;
  readonly endMarker: number;
  /** Selected regions' outline and the marquee. */
  readonly selection: number;
  /** Fade lines and handles. */
  readonly fade: number;
  /** Region names, on the region's name bar. */
  readonly regionText: number;
  /** A muted region's fill. */
  readonly muted: number;
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
  playhead: parseHexColor('#f04b4b'),
  cycle: parseHexColor('#c9a227'),
  cycleOff: parseHexColor('#4a4c55'),
  afterEnd: parseHexColor('#141518'),
  endMarker: parseHexColor('#8a8d99'),
  selection: parseHexColor('#f2f3f7'),
  fade: parseHexColor('#16171a'),
  regionText: parseHexColor('#111214'),
  muted: parseHexColor('#55575f')
};

/** Height of the bar ruler across the top, in CSS pixels. */
export const RULER_HEIGHT = 24;
/** The cycle band's top within the ruler; bar numbers sit above it. */
export const CYCLE_BAND_TOP = 15;
/** Height of a region's name bar (the fragment shader and the Canvas2D renderer tint it). */
export const NAME_BAR_HEIGHT = 12;
/** Horizontal advance of one glyph of text, in CSS pixels (both renderers draw 10 px monospace). */
export const GLYPH_ADVANCE = 6;

/** Layers of a frame, in painter's order ({@link RectBatch.layerEnds}). */
export const Layer = { Background: 0, Grid: 1, Items: 2, Details: 3, Ruler: 4, Overlay: 5 } as const;

/**
 * One frame as backend-neutral draw lists: rectangles (CSS pixels, painter's order, in layers) and short texts (bar
 * numbers, region names). WebGL2 uploads the columns as instance attributes as they are; Canvas2D walks them. Grows,
 * never shrinks, so a steady view allocates nothing per frame but its texts.
 */
export class RectBatch {
  count = 0;
  /** x, y, width, height per rectangle. */
  rects = new Float32Array(4 * 1024);
  colors = new Uint32Array(1024);
  styles = new Uint8Array(1024);
  /** End (exclusive rectangle index) of each {@link Layer}. */
  readonly layerEnds: number[] = [];

  textCount = 0;
  textX = new Float32Array(64);
  textY = new Float32Array(64);
  textColor = new Uint32Array(64);
  readonly texts: string[] = [];

  /** Items drawn this frame (the visible set after culling). */
  visibleItems = 0;

  reset(): void {
    this.count = 0;
    this.textCount = 0;
    this.texts.length = 0;
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

  /** A 1 px outline of a rectangle, as four rectangles. */
  outline(x: number, y: number, w: number, h: number, color: number): void {
    this.push(x, y, w, 1, color);
    this.push(x, y + h - 1, w, 1, color);
    this.push(x, y + 1, 1, h - 2, color);
    this.push(x + w - 1, y + 1, 1, h - 2, color);
  }

  endLayer(): void {
    this.layerEnds.push(this.count);
  }

  /** Text whose top-left is (x, y), cut to the glyphs that fit in `maxWidth` (nothing when none fits). */
  pushText(x: number, y: number, text: string, color: number, maxWidth = Infinity): void {
    const fits = Math.floor(maxWidth / GLYPH_ADVANCE);
    if (fits < 1 || !text) {
      return;
    }
    if (this.textCount === this.textX.length) {
      this.textX = grow(this.textX, 2 * this.textCount);
      this.textY = grow(this.textY, 2 * this.textCount);
      this.textColor = grow(this.textColor, 2 * this.textCount);
    }
    const i = this.textCount++;
    this.textX[i] = x;
    this.textY[i] = y;
    this.textColor[i] = color;
    this.texts[i] = text.length > fits ? text.slice(0, fits) : text;
  }
}

function grow<T extends Float32Array | Uint32Array | Uint8Array | Int32Array>(array: T, length: number): T {
  const next = new (array.constructor as new (length: number) => T)(length);
  next.set(array);
  return next;
}

/** A packed colour with its RGB scaled by `factor` (above 1 lightens towards white). */
export function shadeColor(color: number, factor: number): number {
  const f = (c: number) => Math.round(factor <= 1 ? c * factor : c + (255 - c) * (factor - 1));
  const r = f(color & 0xff);
  const g = f((color >>> 8) & 0xff);
  const b = f((color >>> 16) & 0xff);
  return ((color & 0xff000000) + (b << 16) + (g << 8) + r) >>> 0;
}

const BLACK_KEYS = new Set([1, 3, 6, 8, 10]);

/** Where one region is drawn this frame: the scene's values, or an {@link ItemOverride}'s. */
interface DrawnRegion {
  id: number;
  start: number;
  length: number;
  lane: number;
  fileOffset: number;
  fadeIn: number;
  fadeOut: number;
}

/**
 * Turns a scene and a view into a {@link RectBatch}: lane backgrounds, the grid, the visible items (culled through a
 * {@link LaneIntervalIndex}, so the cost follows what is on screen, not the project size), the regions' waveforms,
 * fades, names and selection, then the ruler (bar numbers, cycle area, project end) and the playhead on top. Pure CPU
 * work shared by every backend, and the part a worker saves the main thread.
 */
export class FrameBuilder {
  readonly batch = new RectBatch();
  readonly index: LaneIntervalIndex;
  private readonly visible: Uint32Array;
  private readonly drawn: DrawnRegion[] = [];
  private readonly range = new Float32Array(2);

  constructor(
    readonly scene: TimelineScene,
    readonly theme: TimelineTheme = darkTheme,
    /** Waveforms for {@link TimelineScene.regions}' keys; shared with whoever loads them. */
    readonly waveforms: WaveformSet = new WaveformSet()
  ) {
    this.index = new LaneIntervalIndex(scene.items, scene.laneCount);
    this.visible = new Uint32Array(this.index.size);
  }

  /** Builds the frame for `view` on a `width` × `height` CSS-pixel canvas. */
  build(view: TimelineView, width: number, height: number): RectBatch {
    const { scene, theme, batch } = this;
    const { ppq, beatsPerBar, laneCount, kind } = scene;
    const { originTick, pixelsPerTick: ppt, firstLane, laneHeight } = view;
    const endTick = originTick + width / ppt;
    const laneTop = RULER_HEIGHT;
    const laneFrom = Math.max(0, Math.floor(firstLane));
    const laneTo = Math.min(laneCount, Math.ceil(firstLane + (height - laneTop) / laneHeight));
    const laneY = (lane: number) => laneTop + (lane - firstLane) * laneHeight;
    const xOf = (tick: number) => (tick - originTick) * ppt;
    batch.reset();

    // Background: one rectangle, the alternate (or black-key) lanes, and the shade after the project end. With spans
    // (the Piano Roll's regions), lanes are drawn only inside them and the rest is shaded.
    const spans = view.spans ?? null;
    batch.push(0, 0, width, height, spans ? theme.afterEnd : theme.background);
    const stretches: [number, number][] = spans
      ? spans
          .map(span => [Math.max(0, xOf(span.start)), Math.min(width, xOf(span.end))] as [number, number])
          .filter(([a, b]) => b > a)
      : [[0, width]];
    for (const [a, b] of spans ? stretches : []) {
      batch.push(a, laneTop, b - a, height - laneTop, theme.background);
    }
    for (let lane = laneFrom; lane < laneTo; lane++) {
      const shaded = kind === 'notes' ? BLACK_KEYS.has((laneCount - 1 - lane) % 12) : lane % 2 === 1;
      if (shaded) {
        for (const [a, b] of stretches) {
          batch.push(a, laneY(lane), b - a, laneHeight, theme.laneAlternate);
        }
      }
    }
    const projectEnd = view.endTick ?? null;
    const endX = projectEnd === null ? width : Math.max(0, Math.round(xOf(projectEnd)));
    if (endX < width) {
      batch.push(endX, laneTop, width - endX, height - laneTop, theme.afterEnd);
    }
    batch.endLayer();

    // Grid.
    const lines = gridLines(originTick, endTick, gridStep(ppq, beatsPerBar, ppt, 8), ppq, beatsPerBar);
    batch.reserve(lines.length);
    for (const { tick, level } of lines) {
      const color =
        level === GridLevel.Bar ? theme.gridBar : level === GridLevel.Beat ? theme.gridBeat : theme.gridDivision;
      batch.push(Math.round(xOf(tick)), laneTop, 1, height - laneTop, color);
    }
    batch.endLayer();

    // Items: query, then project. Clamp x to just outside the canvas so float32 keeps sub-pixel precision.
    const found = this.index.query(originTick, endTick, laneFrom, laneTo, this.visible);
    const drawn = this.collect(found, view, originTick, endTick, laneFrom, laneTo);
    const inset = kind === 'notes' ? 0.5 : 1;
    const style = kind === 'notes' ? RectStyle.Note : RectStyle.Region;
    const flags = scene.regions?.flags ?? scene.noteFlags;
    batch.reserve(drawn.length);
    for (const region of drawn) {
      const x0 = Math.max(-2, xOf(region.start));
      const x1 = Math.min(width + 2, Math.max(xOf(region.start + region.length), x0 + 1));
      const y = laneY(region.lane) + inset;
      const flag = flags?.[region.id] ?? 0;
      const base = flag & ItemFlag.Muted ? theme.muted : (scene.items.color[region.id] ?? 0);
      const color = flag & ItemFlag.Selected ? shadeColor(base, 1.25) : base;
      batch.push(x0, y, x1 - x0, laneHeight - 2 * inset, color, style);
    }
    batch.visibleItems = drawn.length;
    batch.endLayer();

    // Region details: waveforms, note miniatures, fades, selection outlines, names. Selected notes get an outline.
    if (scene.regions) {
      for (const region of drawn) {
        this.regionDetails(region, view, width, laneY(region.lane) + inset, laneHeight - 2 * inset);
      }
    } else if (scene.noteFlags) {
      for (const note of drawn) {
        if ((scene.noteFlags[note.id] ?? 0) & ItemFlag.Selected) {
          const x0 = Math.max(-2, xOf(note.start));
          const x1 = Math.min(width + 2, Math.max(xOf(note.start + note.length), x0 + 2));
          batch.outline(x0, laneY(note.lane), x1 - x0, laneHeight, theme.selection);
        }
      }
    }
    batch.endLayer();

    // Ruler: background, cycle area, beat and bar ticks, bar numbers, project end.
    const bar = ppq * beatsPerBar;
    batch.push(0, 0, width, laneTop, theme.ruler);
    batch.push(0, laneTop - 1, width, 1, theme.gridBar);
    const cycle = view.cycle;
    if (cycle && cycle.end > cycle.start) {
      const c0 = Math.max(-2, xOf(cycle.start));
      const c1 = Math.min(width + 2, xOf(cycle.end));
      if (c1 > c0) {
        batch.push(
          c0,
          CYCLE_BAND_TOP,
          c1 - c0,
          laneTop - CYCLE_BAND_TOP - 1,
          cycle.enabled ? theme.cycle : theme.cycleOff
        );
      }
    }
    const labelEvery = barMultiple(bar * ppt, 40) * bar;
    for (const { tick, level } of lines) {
      if (level !== GridLevel.Division) {
        const x = Math.round(xOf(tick));
        const tall = level === GridLevel.Bar;
        batch.push(x, tall ? 2 : laneTop - 5, 1, tall ? CYCLE_BAND_TOP - 2 : 4, theme.rulerTick);
        if (tall && tick % labelEvery === 0) {
          batch.pushText(x + 3, 2, String(tick / bar + 1), theme.rulerText);
        }
      }
    }
    if (projectEnd !== null && endX < width) {
      batch.push(endX - 1, 0, 2, laneTop, theme.endMarker);
    }
    batch.endLayer();

    // Overlay: the marquee and the playhead.
    const marquee = view.marquee;
    if (marquee && marquee.width >= 1 && marquee.height >= 1) {
      batch.outline(marquee.x, marquee.y, marquee.width, marquee.height, theme.selection);
    }
    const playheadX = Math.round(xOf(view.playheadTick));
    if (playheadX >= -5 && playheadX <= width + 5) {
      batch.push(playheadX - 4, 0, 9, 6, theme.playhead);
      batch.push(playheadX, 0, 1, height, theme.playhead);
    }
    batch.endLayer();
    return batch;
  }

  /** The regions to draw: the visible ones the view does not override, then the overridden ones where they now are. */
  private collect(
    found: number,
    view: TimelineView,
    from: number,
    to: number,
    laneFrom: number,
    laneTo: number
  ): DrawnRegion[] {
    const { items, regions } = this.scene;
    const drawn = this.drawn;
    drawn.length = 0;
    const overrides = view.overrides ?? null;
    const overridden = overrides?.length ? new Set(overrides.map(o => o.index)) : null;
    const region = (id: number, o?: ItemOverride): DrawnRegion => ({
      id,
      start: o?.start ?? items.start[id] ?? 0,
      length: o?.length ?? items.length[id] ?? 0,
      lane: o?.lane ?? items.lane[id] ?? 0,
      fileOffset: o?.fileOffset ?? regions?.fileOffset[id] ?? 0,
      fadeIn: o?.fadeIn ?? regions?.fadeIn[id] ?? 0,
      fadeOut: o?.fadeOut ?? regions?.fadeOut[id] ?? 0
    });
    for (let k = 0; k < found; k++) {
      const id = this.visible[k] ?? 0;
      if (!overridden?.has(id)) {
        drawn.push(region(id));
      }
    }
    for (const o of overrides ?? []) {
      if (o.index >= 0 && o.index < items.count) {
        const r = region(o.index, o);
        if (r.lane >= laneFrom && r.lane < laneTo && r.start < to && r.start + r.length > from) {
          drawn.push(r);
        }
      }
    }
    return drawn;
  }

  /** A region's waveform (min/max columns), fade lines and handles, selection outline and name. */
  private regionDetails(region: DrawnRegion, view: TimelineView, width: number, y: number, h: number): void {
    const { batch, theme, scene, range } = this;
    const details = scene.regions;
    if (!details || h < 4) {
      return;
    }
    const { originTick, pixelsPerTick: ppt } = view;
    const { id, start, length, fileOffset } = region;
    const xStart = (start - originTick) * ppt;
    const xEnd = (start + length - originTick) * ppt;
    const x0 = Math.max(0, Math.floor(xStart));
    const x1 = Math.min(width, Math.ceil(xEnd));
    const flag = details.flags[id] ?? 0;
    const base = flag & ItemFlag.Muted ? theme.muted : (scene.items.color[id] ?? 0);
    const withName = h >= 22;
    const top = withName ? y + NAME_BAR_HEIGHT : y + 1;
    const bottom = y + h - 1;
    const framesPerTick = details.framesPerTick;

    // Waveform: one column per CSS pixel, from the coarsest peak level that still resolves a pixel.
    const waveform = this.waveforms.best(details.waveform[id] ?? -1, framesPerTick / ppt);
    if (waveform && bottom - top >= 4 && x1 > x0) {
      const mid = (top + bottom) / 2;
      const half = (bottom - top) / 2;
      const gain = details.gain[id] ?? 1;
      const color = shadeColor(base, 0.42);
      const framesPerPixel = framesPerTick / ppt;
      batch.reserve(x1 - x0);
      for (let x = x0; x < x1; x++) {
        const frame = fileOffset + (originTick + x / ppt - start) * framesPerTick;
        if (peakRange(waveform, frame, frame + framesPerPixel, range)) {
          const hi = Math.min(1, Math.max(-1, (range[1] ?? 0) * gain));
          const lo = Math.min(1, Math.max(-1, (range[0] ?? 0) * gain));
          const yHi = mid - hi * half;
          batch.push(x, yHi, 1, Math.max(1, mid - lo * half - yHi), color);
        }
      }
    }

    // A MIDI region's notes, between its lowest and highest pitch.
    const notes = details.notes;
    if (notes && flag & ItemFlag.Midi && bottom - top >= 4) {
      const from = notes.offset[id] ?? 0;
      const to = notes.offset[id + 1] ?? from;
      const low = notes.low[id] ?? 0;
      const rows = (notes.high[id] ?? 127) - low + 1;
      const rowHeight = (bottom - top) / rows;
      const noteHeight = Math.max(1, Math.min(4, rowHeight));
      const origin = start - fileOffset;
      const color = shadeColor(base, 0.35);
      const end = start + length;
      batch.reserve(to - from);
      for (let k = from; k < to; k++) {
        const a = Math.max(start, origin + (notes.start[k] ?? 0));
        const b = Math.min(end, origin + (notes.start[k] ?? 0) + (notes.length[k] ?? 0));
        if (b <= a) {
          continue;
        }
        const nx0 = Math.max(x0, (a - originTick) * ppt);
        const nx1 = Math.min(x1, (b - originTick) * ppt);
        if (nx1 > nx0 - 1) {
          const row = rows - 1 - ((notes.pitch[k] ?? 0) - low);
          const ny = top + row * rowHeight + (rowHeight - noteHeight) / 2;
          batch.push(nx0, ny, Math.max(1, nx1 - nx0), noteHeight, color);
        }
      }
    }

    // Fades: a line from silence to full level, and a handle where it ends.
    const fadeInPx = (region.fadeIn / framesPerTick) * ppt;
    const fadeOutPx = (region.fadeOut / framesPerTick) * ppt;
    if (fadeInPx >= 1) {
      this.fadeLine(xStart, bottom, xStart + fadeInPx, top, width);
    }
    if (fadeOutPx >= 1) {
      this.fadeLine(xEnd - fadeOutPx, top, xEnd, bottom, width);
    }

    if (flag & ItemFlag.Selected && x1 > x0) {
      batch.outline(Math.max(-1, xStart), y, Math.min(width + 1, xEnd) - Math.max(-1, xStart), h, theme.selection);
    }

    if (withName) {
      const textX = Math.max(xStart, 0) + 3;
      batch.pushText(textX, y + 1, details.names[id] ?? '', theme.regionText, xEnd - textX - 2);
    }
  }

  /** A straight fade line as 2 px dots, from (xa, ya) to (xb, yb), clipped to the canvas. */
  private fadeLine(xa: number, ya: number, xb: number, yb: number, width: number): void {
    const { batch, theme } = this;
    const steps = Math.max(1, Math.ceil(Math.hypot(xb - xa, yb - ya) / 2));
    for (let s = 0; s <= steps; s++) {
      const x = xa + ((xb - xa) * s) / steps;
      if (x >= -2 && x <= width + 2) {
        batch.push(x - 0.75, ya + ((yb - ya) * s) / steps - 0.75, 1.5, 1.5, theme.fade);
      }
    }
    const handleX = ya > yb ? xb : xa;
    const handleY = Math.min(ya, yb);
    if (handleX >= -4 && handleX <= width + 4) {
      batch.push(handleX - 2.5, handleY, 5, 5, theme.fade);
    }
  }
}
