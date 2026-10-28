import { darkTheme, FrameBuilder, Layer, RULER_HEIGHT, shadeColor } from './frame-builder';
import { hitTest } from './hit-test';
import {
  cloneScene,
  createItems,
  createNoteMiniatures,
  createRegionDetails,
  ItemFlag,
  sceneBuffers,
  type TimelineScene,
  type TimelineView
} from './items';
import { LaneIntervalIndex } from './lane-interval-index';

/** The rectangles of one layer, as [x, y, w, h, color] rows. */
function layer(batch: ReturnType<FrameBuilder['build']>, which: number): number[][] {
  const from = which === 0 ? 0 : (batch.layerEnds[which - 1] ?? 0);
  const to = batch.layerEnds[which] ?? 0;
  const rows: number[][] = [];
  for (let i = from; i < to; i++) {
    rows.push([...Array.from(batch.rects.subarray(4 * i, 4 * i + 4)), batch.colors[i] ?? 0]);
  }
  return rows;
}

const view: TimelineView = { originTick: 0, pixelsPerTick: 40 / 960, firstLane: 0, laneHeight: 60, playheadTick: 0 };

// A MIDI region over beats 1–4 with two notes and one past its end, then a 2-beat audio region from beat 6.
function regionsScene(): TimelineScene {
  const items = createItems(2);
  items.start.set([0, 5 * 960]);
  items.length.set([4 * 960, 2 * 960]);
  items.color.set([0xff0000ff, 0xff00ff00]);
  const details = createRegionDetails(2, 50, ['Keys', 'Audio']);
  details.flags[0] = ItemFlag.Midi;
  const notes = createNoteMiniatures([
    [
      { start: 0, length: 960, pitch: 60 },
      { start: 1920, length: 960, pitch: 72 },
      { start: 5000, length: 960, pitch: 66 } // past the region's end: not drawn
    ],
    []
  ]);
  return { kind: 'regions', ppq: 960, beatsPerBar: 4, laneCount: 1, items, regions: { ...details, notes } };
}

describe('note miniatures', () => {
  it('keeps at least an octave of range, centred on the notes', () => {
    const minis = createNoteMiniatures([[{ start: 0, length: 1, pitch: 64 }], []]);
    expect([minis.low[0], minis.high[0]]).toEqual([58, 70]);
    expect([minis.low[1], minis.high[1]]).toEqual([60, 72]);
    expect(Array.from(minis.offset)).toEqual([0, 1, 1]);
  });

  it('draws the notes inside a MIDI region, highest at the top, cut at its end', () => {
    const builder = new FrameBuilder(regionsScene());
    const batch = builder.build(view, 400, RULER_HEIGHT + 60);
    const color = shadeColor(0xff0000ff, 0.35);
    const notes = layer(batch, Layer.Details).filter(row => row[4] === color);
    expect(notes).toHaveLength(2);
    const [low = [], high = []] = notes;
    expect(low[0]).toBe(0);
    expect(low[2]).toBeCloseTo(40);
    expect(high[0]).toBeCloseTo(80);
    expect(high[1] ?? 0).toBeLessThan(low[1] ?? 0);
  });

  it('keeps the notes in place when a trim moves the start (fileOffset in ticks)', () => {
    const builder = new FrameBuilder(regionsScene());
    const color = shadeColor(0xff0000ff, 0.35);
    const trimmed = builder.build(
      { ...view, overrides: [{ index: 0, start: 960, length: 3 * 960, fileOffset: 960 }] },
      400,
      RULER_HEIGHT + 60
    );
    const notes = layer(trimmed, Layer.Details).filter(row => row[4] === color);
    expect(notes.map(row => row[0])).toEqual([80]); // the first note is outside the trimmed region now
  });

  it('copies and transfers the miniature columns with the scene', () => {
    const scene = regionsScene();
    const copy = cloneScene(scene);
    expect(copy.regions?.notes?.pitch).not.toBe(scene.regions?.notes?.pitch);
    expect(Array.from(copy.regions?.notes?.pitch ?? [])).toEqual([60, 72, 66]);
    expect(sceneBuffers(scene)).toContain(scene.regions?.notes?.start.buffer);
  });

  it('trims a MIDI region at its edges in both halves: it has no fades', () => {
    const scene = regionsScene();
    const index = new LaneIntervalIndex(scene.items, 1);
    expect(hitTest(scene, index, view, 2, RULER_HEIGHT + 10)).toEqual({ index: 0, zone: 'trim-start' });
    expect(hitTest(scene, index, view, 158, RULER_HEIGHT + 10)).toEqual({ index: 0, zone: 'trim-end' });
    expect(hitTest(scene, index, view, 202, RULER_HEIGHT + 10)).toEqual({ index: 1, zone: 'fade-in' });
  });
});

describe('a notes scene (the Piano Roll)', () => {
  function notesScene(): TimelineScene {
    const items = createItems(2);
    items.start.set([0, 960]);
    items.length.set([960, 480]);
    items.lane.set([127 - 60, 127 - 64]);
    items.color.set([0xff112233, 0xff445566]);
    const noteFlags = new Uint8Array([ItemFlag.Selected, ItemFlag.Muted]);
    return { kind: 'notes', ppq: 960, beatsPerBar: 4, laneCount: 128, items, noteFlags };
  }
  const notesView: TimelineView = { ...view, firstLane: 127 - 70, laneHeight: 10 };

  it('outlines selected notes and greys out the ones that do not play', () => {
    const batch = new FrameBuilder(notesScene()).build(notesView, 400, RULER_HEIGHT + 200);
    const items = layer(batch, Layer.Items);
    expect(items.map(row => row[4]).sort()).toEqual([shadeColor(0xff112233, 1.25), darkTheme.muted].sort());
    const outline = layer(batch, Layer.Details);
    expect(outline).toHaveLength(4);
    expect(outline.every(row => row[4] === darkTheme.selection)).toBe(true);
  });

  it('shades the time outside the spans in play', () => {
    const batch = new FrameBuilder(notesScene()).build(
      { ...notesView, spans: [{ start: 960, end: 2 * 960 }] },
      400,
      RULER_HEIGHT + 200
    );
    const background = layer(batch, Layer.Background);
    expect(background[0]?.[4]).toBe(darkTheme.afterEnd);
    expect(background[1]).toEqual([40, RULER_HEIGHT, 40, 200, darkTheme.background]);
    // Black-key lanes only inside the span.
    expect(background.slice(2).every(row => row[0] === 40 && row[2] === 40)).toBe(true);
  });
});
