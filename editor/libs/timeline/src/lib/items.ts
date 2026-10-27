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
  /** Audio regions' file mapping, fades, names and flags (the Tracks area), one entry per item. */
  readonly regions?: RegionDetails;
  /** {@link ItemFlag} bits per item of a notes scene (the Piano Roll): selected notes, notes that do not play. */
  readonly noteFlags?: Uint8Array;
}

/**
 * Item flags ({@link RegionDetails.flags}, {@link TimelineScene.noteFlags}). `Midi` marks a MIDI region: it has no
 * waveform and no fades, so its edges trim in both halves and it draws its note miniatures.
 */
export const ItemFlag = { Selected: 1, Muted: 2, Midi: 4 } as const;

/**
 * The notes MIDI regions draw as miniatures in the Tracks area, as columns: region `i`'s notes are
 * `offset[i]..offset[i + 1]`, positioned in ticks from the region's note origin (its start, less its
 * {@link RegionDetails.fileOffset}, which a MIDI region uses in ticks), and drawn between the region's
 * `low[i]`…`high[i]` pitches.
 */
export interface NoteMiniatures {
  readonly offset: Uint32Array;
  readonly start: Float64Array;
  readonly length: Float64Array;
  readonly pitch: Uint8Array;
  readonly low: Uint8Array;
  readonly high: Uint8Array;
}

/** Miniatures for regions with these notes (one list per region, in item order). */
export function createNoteMiniatures(
  regions: readonly (readonly { start: number; length: number; pitch: number }[])[]
): NoteMiniatures {
  const total = regions.reduce((sum, notes) => sum + notes.length, 0);
  const offset = new Uint32Array(regions.length + 1);
  const start = new Float64Array(total);
  const length = new Float64Array(total);
  const pitch = new Uint8Array(total);
  const low = new Uint8Array(regions.length);
  const high = new Uint8Array(regions.length);
  let k = 0;
  regions.forEach((notes, i) => {
    offset[i] = k;
    let lo = 127;
    let hi = 0;
    for (const note of notes) {
      start[k] = note.start;
      length[k] = note.length;
      pitch[k] = note.pitch;
      lo = Math.min(lo, note.pitch);
      hi = Math.max(hi, note.pitch);
      k++;
    }
    // At least an octave, centred on what is there, so a few notes do not fill the whole height.
    if (notes.length === 0) {
      lo = 60;
      hi = 72;
    }
    const pad = Math.max(0, 12 - (hi - lo));
    low[i] = Math.max(0, lo - Math.ceil(pad / 2));
    high[i] = Math.min(127, hi + Math.floor(pad / 2));
  });
  offset[regions.length] = k;
  return { offset, start, length, pitch, low, high };
}

/**
 * What the Tracks area knows about each region beyond its span: where in its audio file it starts, its fades and gain,
 * its waveform (a key into the {@link WaveformSet} the renderer holds) and its name. Columns, like the items.
 */
export interface RegionDetails {
  /** Waveform key per item, or -1 for none (yet). */
  readonly waveform: Int32Array;
  /** The file frame the region starts at. */
  readonly fileOffset: Float64Array;
  /** Fade lengths, in frames. */
  readonly fadeIn: Float64Array;
  readonly fadeOut: Float64Array;
  /** Linear gain the waveform is drawn at. */
  readonly gain: Float32Array;
  /** {@link ItemFlag} bits. */
  readonly flags: Uint8Array;
  /** Names drawn in the name bar. */
  readonly names: readonly string[];
  /** Audio frames per tick (one tempo and one sample rate per project in Phase 1). */
  readonly framesPerTick: number;
  /** MIDI regions' note miniatures (regions flagged {@link ItemFlag.Midi}). */
  readonly notes?: NoteMiniatures;
}

/** Region details with room for `count` regions. */
export function createRegionDetails(
  count: number,
  framesPerTick: number,
  names: readonly string[] = []
): RegionDetails {
  return {
    waveform: new Int32Array(count).fill(-1),
    fileOffset: new Float64Array(count),
    fadeIn: new Float64Array(count),
    fadeOut: new Float64Array(count),
    gain: new Float32Array(count).fill(1),
    flags: new Uint8Array(count),
    names,
    framesPerTick
  };
}

/** Every buffer of a scene, to list as transferables when posting it to a worker. */
export function sceneBuffers(scene: TimelineScene): ArrayBuffer[] {
  const buffers = itemBuffers(scene.items);
  const r = scene.regions;
  if (r) {
    buffers.push(
      ...[r.waveform, r.fileOffset, r.fadeIn, r.fadeOut, r.gain, r.flags].map(column => column.buffer as ArrayBuffer)
    );
    const n = r.notes;
    if (n) {
      buffers.push(
        ...[n.offset, n.start, n.length, n.pitch, n.low, n.high].map(column => column.buffer as ArrayBuffer)
      );
    }
  }
  if (scene.noteFlags) {
    buffers.push(scene.noteFlags.buffer as ArrayBuffer);
  }
  return buffers;
}

/** A copy of a scene whose buffers are its own, so the original can be transferred and the copy kept (hit-testing). */
export function cloneScene(scene: TimelineScene): TimelineScene {
  const { items, regions, noteFlags } = scene;
  const notes = regions?.notes;
  return {
    ...scene,
    ...(noteFlags ? { noteFlags: noteFlags.slice() } : {}),
    items: {
      count: items.count,
      start: items.start.slice(),
      length: items.length.slice(),
      lane: items.lane.slice(),
      color: items.color.slice()
    },
    ...(regions
      ? {
          regions: {
            ...regions,
            waveform: regions.waveform.slice(),
            fileOffset: regions.fileOffset.slice(),
            fadeIn: regions.fadeIn.slice(),
            fadeOut: regions.fadeOut.slice(),
            gain: regions.gain.slice(),
            flags: regions.flags.slice(),
            ...(notes
              ? {
                  notes: {
                    offset: notes.offset.slice(),
                    start: notes.start.slice(),
                    length: notes.length.slice(),
                    pitch: notes.pitch.slice(),
                    low: notes.low.slice(),
                    high: notes.high.slice()
                  }
                }
              : {})
          }
        }
      : {})
  };
}

/**
 * An item drawn somewhere else than the scene says, for one frame: the local preview while a region is dragged,
 * trimmed or faded, before the edit is sent (docs/01-implementation-plan.md §4.4). Fields left out keep the scene's.
 */
export interface ItemOverride {
  readonly index: number;
  readonly start?: number;
  readonly length?: number;
  readonly lane?: number;
  readonly fileOffset?: number;
  readonly fadeIn?: number;
  readonly fadeOut?: number;
}

/** The cycle area in the ruler, in ticks. */
export interface CycleArea {
  readonly enabled: boolean;
  readonly start: number;
  readonly end: number;
}

/** A rectangle in CSS pixels (the marquee). */
export interface PixelRect {
  readonly x: number;
  readonly y: number;
  readonly width: number;
  readonly height: number;
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
  /** The cycle area, shown in the ruler and as a tint over the lanes when on. */
  readonly cycle?: CycleArea | null;
  /** The project end marker: lanes after it are shaded. */
  readonly endTick?: number | null;
  /** A selection rectangle being dragged out. */
  readonly marquee?: PixelRect | null;
  /** Items drawn as previewed rather than as the scene has them. */
  readonly overrides?: readonly ItemOverride[] | null;
  /**
   * Stretches of time that are in play (the regions a Piano Roll edits): when given, the lanes are drawn only there
   * and the rest is shaded as after the project end.
   */
  readonly spans?: readonly TickSpan[] | null;
}

/** A stretch of ticks, `[start, end)`. */
export interface TickSpan {
  readonly start: number;
  readonly end: number;
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
