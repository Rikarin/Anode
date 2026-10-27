import { FrameBuilder, Layer, RULER_HEIGHT, RectStyle, darkTheme, shadeColor } from './frame-builder';
import { ItemFlag, createItems, createRegionDetails, type TimelineScene, type TimelineView } from './items';
import { WaveformSet } from './waveform';

function scene(): TimelineScene {
  // One 1-beat region per beat on each of 8 lanes, across 64 bars.
  const ppq = 960;
  const perLane = 64 * 4;
  const items = createItems(8 * perLane);
  for (let i = 0; i < items.count; i++) {
    items.start[i] = (i % perLane) * ppq;
    items.length[i] = ppq;
    items.lane[i] = Math.floor(i / perLane);
    items.color[i] = 0xff00ff00;
  }
  return { kind: 'regions', ppq, beatsPerBar: 4, laneCount: 8, items };
}

const view: TimelineView = {
  originTick: 4 * 960, // bar 2
  pixelsPerTick: 40 / 960, // 40 px per beat
  firstLane: 2,
  laneHeight: 20,
  playheadTick: 6 * 960
};

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

describe('FrameBuilder', () => {
  const builder = new FrameBuilder(scene());

  it('draws only the items in view', () => {
    // 400 px = 10 beats; 100 px of lanes = lanes 2..6 (5 lanes).
    const batch = builder.build(view, 400, RULER_HEIGHT + 100);
    expect(batch.visibleItems).toBe(5 * 10);
    expect(batch.layerEnds).toHaveLength(6);
  });

  it('projects items to CSS pixels, inset within their lane', () => {
    const batch = builder.build(view, 400, RULER_HEIGHT + 100);
    const itemsFrom = batch.layerEnds[Layer.Grid] ?? 0;
    expect(Array.from(batch.rects.subarray(4 * itemsFrom, 4 * itemsFrom + 4))).toEqual([0, RULER_HEIGHT + 1, 40, 18]);
    expect(batch.styles[itemsFrom]).toBe(RectStyle.Region);
  });

  it('numbers the bars and draws the playhead', () => {
    const batch = builder.build(view, 400, RULER_HEIGHT + 100);
    expect(batch.texts.slice(0, batch.textCount)).toEqual(['2', '3', '4']);
    const last = batch.count - 1;
    expect(Array.from(batch.rects.subarray(4 * last, 4 * last + 4))).toEqual([80, 0, 1, RULER_HEIGHT + 100]);
  });

  it('reuses its buffers frame to frame', () => {
    const first = builder.build(view, 400, RULER_HEIGHT + 100).rects;
    expect(builder.build({ ...view, originTick: 0 }, 400, RULER_HEIGHT + 100).rects).toBe(first);
  });

  it('shades the lanes after the project end and marks it in the ruler', () => {
    const batch = builder.build({ ...view, endTick: 9 * 960 }, 400, RULER_HEIGHT + 100);
    // 9 beats is 5 beats (200 px) into the view.
    expect(layer(batch, Layer.Background)).toContainEqual([200, RULER_HEIGHT, 200, 100, darkTheme.afterEnd]);
    expect(layer(batch, Layer.Ruler)).toContainEqual([199, 0, 2, RULER_HEIGHT, darkTheme.endMarker]);
  });

  it('draws the cycle area in the ruler, dimmed when cycling is off', () => {
    const on = builder.build({ ...view, cycle: { enabled: true, start: 5 * 960, end: 7 * 960 } }, 400, 124);
    const cycleRow = layer(on, Layer.Ruler).find(row => row[4] === darkTheme.cycle);
    expect(cycleRow?.slice(0, 3)).toEqual([40, 15, 80]);
    const off = builder.build({ ...view, cycle: { enabled: false, start: 5 * 960, end: 7 * 960 } }, 400, 124);
    expect(layer(off, Layer.Ruler).some(row => row[4] === darkTheme.cycleOff)).toBe(true);
  });

  it('draws an overridden item where the override puts it, and not where the scene has it', () => {
    // Item 256 + 4 is the region at beat 4 on lane 1… use lane 2's first visible region: index 2 * 256 + 4.
    const index = 2 * 256 + 4;
    const batch = builder.build({ ...view, overrides: [{ index, start: 8 * 960, lane: 3 }] }, 400, 124);
    const items = layer(batch, Layer.Items);
    expect(items).not.toContainEqual([0, RULER_HEIGHT + 1, 40, 18, 0xff00ff00]);
    expect(items).toContainEqual([160, RULER_HEIGHT + 21, 40, 18, 0xff00ff00]);
    expect(batch.visibleItems).toBe(5 * 10);
  });

  it('outlines the marquee', () => {
    const batch = builder.build({ ...view, marquee: { x: 10, y: 30, width: 50, height: 40 } }, 400, 124);
    expect(layer(batch, Layer.Overlay)).toContainEqual([10, 30, 50, 1, darkTheme.selection]);
  });
});

describe('FrameBuilder with region details', () => {
  // One region on one lane: 2 beats from beat 1, playing a file from frame 1000 at 10 frames per tick.
  function regionScene(): TimelineScene {
    const items = createItems(1);
    items.start[0] = 960;
    items.length[0] = 1920;
    items.color[0] = 0xff3366cc;
    const regions = createRegionDetails(1, 10, ['Guitar']);
    regions.waveform[0] = 7;
    regions.fileOffset[0] = 1000;
    regions.fadeIn[0] = 960 * 10 * 0.5; // half a beat
    return { kind: 'regions', ppq: 960, beatsPerBar: 4, laneCount: 1, items, regions };
  }

  const regionView: TimelineView = {
    originTick: 0,
    pixelsPerTick: 40 / 960,
    firstLane: 0,
    laneHeight: 60,
    playheadTick: 0
  };

  it('draws the waveform from the peaks, one column per pixel, at the region gain', () => {
    const waveforms = new WaveformSet();
    // 1 frame per peak, full-scale square wave: every column spans -1…1.
    const data = new Int16Array(2 * 100_000);
    for (let p = 0; p < 100_000; p++) {
      data[2 * p] = -32767;
      data[2 * p + 1] = 32767;
    }
    waveforms.add(7, { channels: 1, framesPerPeak: 64, firstPeak: 0, peakCount: 100_000, data });
    const builder = new FrameBuilder(regionScene(), darkTheme, waveforms);
    const batch = builder.build(regionView, 400, RULER_HEIGHT + 60);
    const color = shadeColor(0xff3366cc, 0.42);
    const columns = layer(batch, Layer.Details).filter(row => row[4] === color);
    expect(columns).toHaveLength(80); // 2 beats at 40 px
    // Lane 0 is y 24…84, inset 1: the waveform spans the name bar's bottom (37) to the region's bottom - 1 (82).
    expect(columns[0]).toEqual([40, 37, 1, 45, color]);
  });

  it('draws no waveform before its peaks arrive, but still the name and fade', () => {
    const builder = new FrameBuilder(regionScene());
    const batch = builder.build(regionView, 400, RULER_HEIGHT + 60);
    expect(batch.texts.slice(0, batch.textCount)).toContain('Guitar');
    expect(layer(batch, Layer.Details).some(row => row[4] === darkTheme.fade)).toBe(true);
  });

  it('cuts a name to the region width and outlines a selected region', () => {
    const scene = regionScene();
    scene.regions?.flags.set([ItemFlag.Selected]);
    const builder = new FrameBuilder(scene);
    const batch = builder.build({ ...regionView, pixelsPerTick: 10 / 960 }, 400, RULER_HEIGHT + 60);
    // 20 px wide: 15 px for text after the 3 px pad and 2 px margin, so two 6 px glyphs.
    expect(batch.texts.slice(0, batch.textCount)).toContain('Gu');
    expect(layer(batch, Layer.Details)).toContainEqual([10, RULER_HEIGHT + 1, 20, 1, darkTheme.selection]);
  });
});
