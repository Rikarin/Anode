import { barMultiple, GridLevel, gridLines, gridStep } from './grid';
import { ItemFlag, type ItemOverride, parseHexColor, type TimelineScene, type TimelineView } from './items';
import { LaneIntervalIndex } from './lane-interval-index';
import { MeterMap } from './meter';
import { peakRange, WaveformSet } from './waveform';

/**
 * How a rectangle is shaded. The low four bits pick a shape the renderers draw as one instance (the WebGL2 fragment
 * shader, or a Canvas2D path): flat; a Piano Roll note (rounded, a soft vertical gradient, a darker edge); a region
 * (rounded, a gradient body under a darker name bar when it is {@link NAME_BAR_MIN_HEIGHT} tall, a darker hairline
 * edge and a highlight along its top); the cycle area (a rounded band); the playhead's head (a triangle pointing down);
 * the ruler (a vertical gradient). {@link RectStyle.Selected} is a flag on notes and regions: a white ring at the edge.
 */
export const RectStyle = { Flat: 0, Note: 1, Region: 2, Cycle: 3, PlayheadHead: 4, Ruler: 5, Selected: 16 } as const;
export type RectStyle = number;

/** The shape bits of a {@link RectStyle}, without its flags. */
export const RECT_SHAPE_MASK = 15;

/** A region shows its name bar from this height (CSS pixels) up; a lower one shows its body only. */
export const NAME_BAR_MIN_HEIGHT = 22;

/** Corner radii in CSS pixels; both renderers round by these (less on a smaller rectangle). */
export const CornerRadius = { Region: 4, Note: 2, Cycle: 3 } as const;

/**
 * How a region is shaded from its colour (multipliers of its RGB), top to bottom, by both renderers. The body is a
 * darker version of the track colour, so the lighter waveform reads on it; the name bar is darker still, so the name in
 * {@link TimelineTheme.regionText} keeps its contrast on every palette colour; the edge is a dark hairline.
 */
export const RegionShade = {
  bodyTop: 0.86,
  bodyBottom: 0.64,
  nameTop: 0.56,
  nameBottom: 0.46,
  edge: 0.34,
  /** The highlight along the top, towards white. */
  gloss: 0.22
} as const;

/** A view's colours, packed ({@link parseHexColor}). */
export interface TimelineTheme {
  readonly background: number;
  readonly laneAlternate: number;
  /** The hairline between two tracks' lanes (and the Piano Roll's octaves), and under the ruler. */
  readonly laneLine: number;
  readonly gridBar: number;
  readonly gridBeat: number;
  readonly gridDivision: number;
  /** The ruler's colour; it is drawn as a vertical gradient around it. */
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
  /** The marquee's outline. */
  readonly selection: number;
  /** The marquee's translucent fill. */
  readonly marqueeFill: number;
  /** Fade lines and handles. */
  readonly fade: number;
  /** Region names, on the region's name bar. */
  readonly regionText: number;
  /** A muted region's fill. */
  readonly muted: number;
  /** The autopunch range in the ruler, on and off. */
  readonly punch: number;
  readonly punchOff: number;
}

/** The dark theme (Anode's primary): graphite lanes, a lighter ruler, a white playhead, Logic's yellow cycle area. */
export const darkTheme: TimelineTheme = {
  background: parseHexColor('#232325'),
  laneAlternate: parseHexColor('#202022'),
  laneLine: parseHexColor('#151516'),
  gridBar: parseHexColor('#3d3d42'),
  gridBeat: parseHexColor('#2e2e32'),
  gridDivision: parseHexColor('#28282b'),
  ruler: parseHexColor('#38383d'),
  rulerTick: parseHexColor('#8e8e95'),
  rulerText: parseHexColor('#dedee2'),
  playhead: parseHexColor('#f4f4f6'),
  cycle: parseHexColor('#f5c400'),
  cycleOff: parseHexColor('#5e5e64'),
  afterEnd: parseHexColor('#1a1a1b'),
  endMarker: parseHexColor('#aaaab0'),
  selection: parseHexColor('#ffffff'),
  marqueeFill: parseHexColor('#ffffff1a'),
  fade: parseHexColor('#ededf0'),
  regionText: parseHexColor('#f8f8fa'),
  muted: parseHexColor('#6c6c72'),
  punch: parseHexColor('#ff453a'),
  punchOff: parseHexColor('#7a3a37')
};

/** The light theme: Logic's light grey lanes, a dark playhead, the same yellow cycle area. */
export const lightTheme: TimelineTheme = {
  background: parseHexColor('#ededf0'),
  laneAlternate: parseHexColor('#e8e8eb'),
  laneLine: parseHexColor('#cdcdd2'),
  gridBar: parseHexColor('#c1c1c7'),
  gridBeat: parseHexColor('#d7d7dc'),
  gridDivision: parseHexColor('#e1e1e5'),
  ruler: parseHexColor('#dadade'),
  rulerTick: parseHexColor('#86868c'),
  rulerText: parseHexColor('#36363a'),
  playhead: parseHexColor('#1d1d1f'),
  cycle: parseHexColor('#f5c400'),
  cycleOff: parseHexColor('#b2b2b8'),
  afterEnd: parseHexColor('#e0e0e4'),
  endMarker: parseHexColor('#6e6e73'),
  selection: parseHexColor('#1d1d1f'),
  marqueeFill: parseHexColor('#0071e31f'),
  fade: parseHexColor('#ffffff'),
  regionText: parseHexColor('#ffffff'),
  muted: parseHexColor('#a2a2a8'),
  punch: parseHexColor('#e0352b'),
  punchOff: parseHexColor('#d9a3a0')
};

/** The palette for a {@link TimelineScene.theme}. */
export function timelineTheme(name: TimelineScene['theme']): TimelineTheme {
  return name === 'light' ? lightTheme : darkTheme;
}

/** A region's waveform colour: a lighter tint of its colour, drawn over its darker body. */
export function waveformColor(base: number): number {
  return shadeColor(base, 1.48);
}

/** A MIDI region's note miniatures: small bars in a lighter tint of its colour. */
export function miniatureColor(base: number): number {
  return shadeColor(base, 1.7);
}

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
  fadeInCurve: number;
  fadeOutCurve: number;
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
  /** The signature track, when the scene has one. */
  private readonly meter: MeterMap | null;

  constructor(
    readonly scene: TimelineScene,
    readonly theme: TimelineTheme = timelineTheme(scene.theme),
    /** Waveforms for {@link TimelineScene.regions}' keys; shared with whoever loads them. */
    readonly waveforms: WaveformSet = new WaveformSet()
  ) {
    this.index = new LaneIntervalIndex(scene.items, scene.laneCount);
    this.visible = new Uint32Array(this.index.size);
    this.meter = scene.meter?.length ? new MeterMap(scene.meter, scene.ppq) : null;
  }

  /** Builds the frame for `view` on a `width` × `height` CSS-pixel canvas. */
  build(view: TimelineView, width: number, height: number): RectBatch {
    const { scene, theme, batch } = this;
    const { ppq, beatsPerBar, laneCount, kind } = scene;
    const { originTick, pixelsPerTick: ppt, firstLane, laneHeight } = view;
    const endTick = originTick + width / ppt;
    const laneTop = view.lanesTop ?? RULER_HEIGHT;
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

    // Grid: bars and beats of the signature track when there is one.
    const meter = this.meter;
    const firstBar = meter ? meter.barTicksAt(0) : ppq * beatsPerBar;
    const step = gridStep(ppq, firstBar / ppq, ppt, 8);
    const lines = meter
      ? meter.gridLines(originTick, endTick, step, Math.max(1, Math.round(step / firstBar)))
      : gridLines(originTick, endTick, step, ppq, beatsPerBar);
    batch.reserve(lines.length);
    for (const { tick, level } of lines) {
      const color =
        level === GridLevel.Bar ? theme.gridBar : level === GridLevel.Beat ? theme.gridBeat : theme.gridDivision;
      batch.push(Math.round(xOf(tick)), laneTop, 1, height - laneTop, color);
    }
    // Hairlines across: between tracks; in the Piano Roll between keys (when they are tall enough), darker at each B|C.
    batch.reserve(laneTo - laneFrom);
    for (let lane = laneFrom; lane < laneTo; lane++) {
      const y = Math.round(laneY(lane) + laneHeight) - 1;
      if (kind === 'regions') {
        batch.push(0, y, width, 1, theme.laneLine);
      } else {
        const octave = (laneCount - 1 - lane) % 12 === 0;
        if (octave || laneHeight >= 8) {
          for (const [a, b] of stretches) {
            batch.push(a, y, b - a, 1, octave ? theme.laneLine : theme.gridDivision);
          }
        }
      }
    }
    batch.endLayer();

    // Items: query, then project. Clamp x to just outside the canvas so float32 keeps sub-pixel precision.
    const found = this.index.query(originTick, endTick, laneFrom, laneTo, this.visible);
    const drawn = this.collect(found, view, originTick, endTick, laneFrom, laneTo);
    const inset = kind === 'notes' ? 0.5 : 1;
    const style = kind === 'notes' ? RectStyle.Note : RectStyle.Region;
    const flags = scene.regions?.flags ?? scene.noteFlags;
    batch.reserve(drawn.length);
    // Two passes: whole items, then the parts drawn inside them (comp segments), so a part is never under its item.
    for (const inner of [false, true]) {
      for (const region of drawn) {
        const flag = flags?.[region.id] ?? 0;
        if (Boolean(flag & ItemFlag.Inner) !== inner) {
          continue;
        }
        const x0 = Math.max(-2, xOf(region.start));
        const x1 = Math.min(width + 2, Math.max(xOf(region.start + region.length), x0 + 1));
        const y = laneY(region.lane) + inset;
        const h = laneHeight - 2 * inset;
        const base = flag & ItemFlag.Muted ? theme.muted : (scene.items.color[region.id] ?? 0);
        if (inner) {
          // A part: flat, between the name bar and the bottom edge, a little lighter than the item's body.
          const top = h >= NAME_BAR_MIN_HEIGHT ? NAME_BAR_HEIGHT : 1;
          batch.push(x0, y + top, x1 - x0, Math.max(1, h - top - 1), shadeColor(base, 0.92));
          continue;
        }
        const selected = flag & ItemFlag.Selected;
        const color = selected ? shadeColor(base, 1.25) : base;
        batch.push(x0, y, x1 - x0, h, color, selected ? style | RectStyle.Selected : style);
      }
    }
    batch.visibleItems = drawn.length;
    batch.endLayer();

    // Region details: waveforms, note miniatures, fades, names. (Selection is the items' ring, drawn by the renderer.)
    if (scene.regions) {
      for (const region of drawn) {
        this.regionDetails(region, view, width, laneY(region.lane) + inset, laneHeight - 2 * inset);
      }
    }
    batch.endLayer();

    // Ruler: background, cycle area, beat and bar ticks, bar numbers, project end.
    const bar = ppq * beatsPerBar;
    if (laneTop > RULER_HEIGHT) {
      // The global tracks' band under the ruler (drawn over by the editor).
      batch.push(0, RULER_HEIGHT, width, laneTop - RULER_HEIGHT, theme.laneAlternate);
      batch.push(0, laneTop - 1, width, 1, theme.laneLine);
    }
    batch.push(0, 0, width, RULER_HEIGHT, theme.ruler, RectStyle.Ruler);
    batch.push(0, RULER_HEIGHT - 1, width, 1, theme.laneLine);
    const cycle = view.cycle;
    if (cycle && cycle.end > cycle.start) {
      const c0 = Math.max(-2, xOf(cycle.start));
      const c1 = Math.min(width + 2, xOf(cycle.end));
      if (c1 > c0) {
        batch.push(
          c0,
          CYCLE_BAND_TOP,
          c1 - c0,
          RULER_HEIGHT - CYCLE_BAND_TOP - 1,
          cycle.enabled ? theme.cycle : theme.cycleOff,
          RectStyle.Cycle
        );
      }
    }
    const labelBars = barMultiple((meter ? firstBar : bar) * ppt, 40);
    for (const { tick, level } of lines) {
      if (level !== GridLevel.Division) {
        const x = Math.round(xOf(tick));
        const tall = level === GridLevel.Bar;
        batch.push(x, tall ? 2 : RULER_HEIGHT - 5, 1, tall ? CYCLE_BAND_TOP - 2 : 4, theme.rulerTick);
        const barIndex = Math.round(meter ? meter.barAt(tick) : tick / bar);
        if (tall && barIndex % labelBars === 0) {
          batch.pushText(x + 3, 2, String(barIndex + 1), theme.rulerText);
        }
      }
    }
    // Autopunch: a red band along the ruler's bottom, with its locators as ticks.
    const punch = view.punch;
    if (punch && punch.end > punch.start) {
      const p0 = Math.max(-2, xOf(punch.start));
      const p1 = Math.min(width + 2, xOf(punch.end));
      if (p1 > p0) {
        const color = punch.enabled ? theme.punch : theme.punchOff;
        batch.push(p0, RULER_HEIGHT - 4, p1 - p0, 3, color);
        batch.push(Math.round(p0), CYCLE_BAND_TOP - 2, 1, RULER_HEIGHT - CYCLE_BAND_TOP + 1, color);
        batch.push(Math.round(p1) - 1, CYCLE_BAND_TOP - 2, 1, RULER_HEIGHT - CYCLE_BAND_TOP + 1, color);
      }
    }
    if (projectEnd !== null && endX < width) {
      batch.push(endX - 1, 0, 2, RULER_HEIGHT, theme.endMarker);
    }
    batch.endLayer();

    // Overlay: the marquee and the playhead.
    const marquee = view.marquee;
    if (marquee && marquee.width >= 1 && marquee.height >= 1) {
      batch.push(marquee.x, marquee.y, marquee.width, marquee.height, theme.marqueeFill);
      batch.outline(marquee.x, marquee.y, marquee.width, marquee.height, theme.selection);
    }
    const playheadX = Math.round(xOf(view.playheadTick));
    if (playheadX >= -6 && playheadX <= width + 6) {
      // Logic's playhead: a hairline with a small downward triangle in the ruler.
      batch.push(playheadX - 5, 0, 11, 9, theme.playhead, RectStyle.PlayheadHead);
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
      fadeOut: o?.fadeOut ?? regions?.fadeOut[id] ?? 0,
      fadeInCurve: o?.fadeInCurve ?? regions?.fadeInCurve[id] ?? 0,
      fadeOutCurve: o?.fadeOutCurve ?? regions?.fadeOutCurve[id] ?? 0
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

  /** A region's waveform (min/max columns) or note miniatures, fade lines and handles, and name. */
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
    const withName = h >= NAME_BAR_MIN_HEIGHT;
    const top = withName ? y + NAME_BAR_HEIGHT : y + 1;
    const bottom = y + h - 1;
    const own = details.rate[id] ?? 0;
    const framesPerTick = own > 0 ? own : details.framesPerTick;
    const inner = (flag & ItemFlag.Inner) !== 0;
    const ownFades = !inner && !(flag & ItemFlag.Folder);

    // Waveform: one column per CSS pixel, from the coarsest peak level that still resolves a pixel.
    const waveform = this.waveforms.best(details.waveform[id] ?? -1, framesPerTick / ppt);
    if (waveform && bottom - top >= 4 && x1 > x0) {
      const mid = (top + bottom) / 2;
      const half = (bottom - top) / 2;
      const gain = details.gain[id] ?? 1;
      const color = waveformColor(base);
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
      const noteHeight = Math.max(1.5, Math.min(3, rowHeight * 0.8));
      const origin = start - fileOffset;
      const color = miniatureColor(base);
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
    const fadeInPx = ownFades ? (region.fadeIn / framesPerTick) * ppt : 0;
    const fadeOutPx = ownFades ? (region.fadeOut / framesPerTick) * ppt : 0;
    if (fadeInPx >= 1) {
      this.fadeCurve(xStart, xStart + fadeInPx, top, bottom, region.fadeInCurve, false, width);
    }
    if (fadeOutPx >= 1) {
      this.fadeCurve(xEnd - fadeOutPx, xEnd, top, bottom, region.fadeOutCurve, true, width);
    }

    if (withName && !inner) {
      const textX = Math.max(xStart, 0) + 4;
      batch.pushText(textX, y + 1, details.names[id] ?? '', theme.regionText, xEnd - textX - 2);
    }
  }

  /**
   * A fade as 1.5 px dots along its gain curve between `xa` and `xb` (silence at the bottom, full level at the top),
   * clipped to the canvas, with a handle where it meets full level. A fade-out is the fade-in's mirror.
   */
  private fadeCurve(
    xa: number,
    xb: number,
    top: number,
    bottom: number,
    curve: number,
    out: boolean,
    width: number
  ): void {
    const { batch, theme } = this;
    const steps = Math.max(2, Math.ceil(Math.hypot(xb - xa, bottom - top) / 2));
    for (let s = 0; s <= steps; s++) {
      const f = s / steps;
      const x = xa + (xb - xa) * f;
      if (x >= -2 && x <= width + 2) {
        const gain = fadeGain(out ? 1 - f : f, curve);
        batch.push(x - 0.75, bottom - gain * (bottom - top) - 0.75, 1.5, 1.5, theme.fade);
      }
    }
    const handleX = out ? xa : xb;
    if (handleX >= -4 && handleX <= width + 4) {
      batch.push(handleX - 2.5, top, 5, 5, theme.fade);
    }
  }
}

/**
 * A fade-in's gain at position `x` (0…1) for curve `c` (-1…1): x^(2^(-2c)) (docs/04-phase-3.md). c = 0 is linear,
 * c = -1 a slow, exponential-looking start, c = 1 a fast, logarithmic-looking one. A fade-out at x is a fade-in at 1 - x.
 */
export function fadeGain(x: number, c: number): number {
  const position = Math.min(1, Math.max(0, x));
  return position ** (2 ** (-2 * Math.min(1, Math.max(-1, c))));
}
