/**
 * What a timeline view draws, as columns of typed arrays (structure of arrays) so the whole set can be transferred to
 * the render worker without copying or serialising: regions in the Tracks area, notes in the Piano Roll.
 *
 * Item `i` spans `[start[i], start[i] + length[i])` ticks on lane `lane[i]` (a track, or a MIDI pitch) and is filled
 * with `color[i]`, packed by {@link packColor}.
 */
export interface TimelineItems {
  readonly count: number;
  readonly start: Float64Array;
  readonly length: Float64Array;
  readonly lane: Uint16Array;
  readonly color: Uint32Array;
}

/** How items look: Piano Roll notes (thin, bordered) or Tracks-area regions (bordered, with a name bar). */
export type TimelineItemKind = 'notes' | 'regions';

/** Everything a renderer needs to draw one view, apart from where it is looking ({@link TimelineView}). */
export interface TimelineScene {
  readonly kind: TimelineItemKind;
  /** Ticks per quarter note. */
  readonly ppq: number;
  /** Quarter notes per bar (a fixed meter until the tempo/meter map lands). */
  readonly beatsPerBar: number;
  /** Lanes the items are spread over: 128 pitches, or the track count. */
  readonly laneCount: number;
  readonly items: TimelineItems;
}

/** Where a view is looking, in the units the stores use. Small and cloneable: it crosses to the worker every frame. */
export interface TimelineView {
  /** The tick at the left edge. */
  readonly originTick: number;
  /** Zoom: CSS pixels per tick. */
  readonly pixelsPerTick: number;
  /** The (fractional) lane at the top of the lane area: vertical scroll. */
  readonly firstLane: number;
  /** CSS pixels per lane: vertical zoom. */
  readonly laneHeight: number;
  /** The playhead, in ticks. */
  readonly playheadTick: number;
}

/** Item columns with room for `count` items. */
export function createItems(count: number): TimelineItems {
  return {
    count,
    start: new Float64Array(count),
    length: new Float64Array(count),
    lane: new Uint16Array(count),
    color: new Uint32Array(count)
  };
}

/** The buffers to list as transferables when posting `items` to a worker. */
export function itemBuffers(items: TimelineItems): ArrayBuffer[] {
  return [items.start, items.length, items.lane, items.color].map(column => column.buffer as ArrayBuffer);
}

/**
 * An 8-bit RGBA colour packed into one 32-bit number whose little-endian bytes are R, G, B, A: the layout WebGL reads as
 * a normalised `UNSIGNED_BYTE × 4` attribute straight from a `Uint32Array`.
 */
export function packColor(r: number, g: number, b: number, a = 255): number {
  return ((a & 0xff) * 0x1000000 + ((b & 0xff) << 16) + ((g & 0xff) << 8) + (r & 0xff)) >>> 0;
}

/** `#rrggbb` (or `#rrggbbaa`) to a packed colour. */
export function parseHexColor(hex: string): number {
  const value = /^#([0-9a-f]{6})([0-9a-f]{2})?$/i.exec(hex);
  if (!value?.[1]) {
    throw new RangeError(`Not a #rrggbb colour: ${hex}`);
  }
  const rgb = parseInt(value[1], 16);
  return packColor(rgb >> 16, (rgb >> 8) & 0xff, rgb & 0xff, value[2] ? parseInt(value[2], 16) : 255);
}

/** A packed colour as a CSS colour string (Canvas2D's `fillStyle`). */
export function colorToCss(color: number): string {
  const r = color & 0xff;
  const g = (color >>> 8) & 0xff;
  const b = (color >>> 16) & 0xff;
  const a = (color >>> 24) / 255;
  return a === 1 ? `rgb(${r} ${g} ${b})` : `rgb(${r} ${g} ${b} / ${a.toFixed(3)})`;
}
