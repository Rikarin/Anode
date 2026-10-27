import { FrameBuilder, RULER_HEIGHT, RectStyle } from './frame-builder';
import { createItems, type TimelineScene, type TimelineView } from './items';

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

describe('FrameBuilder', () => {
  const builder = new FrameBuilder(scene());

  it('draws only the items in view', () => {
    // 400 px = 10 beats; 100 px of lanes = lanes 2..6 (5 lanes).
    const batch = builder.build(view, 400, RULER_HEIGHT + 100);
    expect(batch.visibleItems).toBe(5 * 10);
    expect(batch.layerEnds).toHaveLength(4);
  });

  it('projects items to CSS pixels, inset within their lane', () => {
    const batch = builder.build(view, 400, RULER_HEIGHT + 100);
    const itemsFrom = batch.layerEnds[1] ?? 0;
    expect(Array.from(batch.rects.subarray(4 * itemsFrom, 4 * itemsFrom + 4))).toEqual([0, RULER_HEIGHT + 1, 40, 18]);
    expect(batch.styles[itemsFrom]).toBe(RectStyle.Region);
  });

  it('numbers the bars and draws the playhead', () => {
    const batch = builder.build(view, 400, RULER_HEIGHT + 100);
    expect(Array.from(batch.labelValue.subarray(0, batch.labelCount))).toEqual([2, 3, 4]);
    const last = batch.count - 1;
    expect(Array.from(batch.rects.subarray(4 * last, 4 * last + 4))).toEqual([80, 0, 1, RULER_HEIGHT + 100]);
  });

  it('reuses its buffers frame to frame', () => {
    const first = builder.build(view, 400, RULER_HEIGHT + 100).rects;
    expect(builder.build({ ...view, originTick: 0 }, 400, RULER_HEIGHT + 100).rects).toBe(first);
  });
});
