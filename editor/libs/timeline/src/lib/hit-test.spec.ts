import { RULER_HEIGHT } from './frame-builder';
import { hitTest, itemsInRect, laneAtY, snapStep, snapTick, tickAtX } from './hit-test';
import { createItems, createRegionDetails, type TimelineScene, type TimelineView } from './items';
import { LaneIntervalIndex } from './lane-interval-index';

// Two regions on lane 0 (beats 1–3 and 4–5) and one on lane 1 (beats 2–4); 40 px per beat, 40 px lanes.
function scene(): TimelineScene {
  const items = createItems(3);
  [
    [960, 1920, 0],
    [3840, 960, 0],
    [1920, 1920, 1]
  ].forEach(([start, length, lane], i) => {
    items.start[i] = start ?? 0;
    items.length[i] = length ?? 0;
    items.lane[i] = lane ?? 0;
  });
  const regions = createRegionDetails(3, 1, ['a', 'b', 'c']);
  regions.fadeIn[2] = 480; // 20 px at 1 frame per tick
  return { kind: 'regions', ppq: 960, beatsPerBar: 4, laneCount: 2, items, regions };
}

const view: TimelineView = { originTick: 0, pixelsPerTick: 40 / 960, firstLane: 0, laneHeight: 40, playheadTick: 0 };
const lane0Lower = RULER_HEIGHT + 30;
const lane0Upper = RULER_HEIGHT + 10;
const lane1Upper = RULER_HEIGHT + 50;

describe('hitTest', () => {
  const s = scene();
  const index = new LaneIntervalIndex(s.items, s.laneCount);
  const hit = (x: number, y: number) => hitTest(s, index, view, x, y);

  it('finds the region under the pointer and moves it from the body', () => {
    expect(hit(80, lane0Lower)).toEqual({ index: 0, zone: 'body' });
    expect(hit(170, lane0Upper)).toEqual({ index: 1, zone: 'body' });
    expect(hit(130, lane0Lower)).toBeNull(); // the gap between them
    expect(hit(80, 10)).toBeNull(); // the ruler
  });

  it('trims at the edges in the lower half and fades in the upper half', () => {
    expect(hit(42, lane0Lower)).toEqual({ index: 0, zone: 'trim-start' });
    expect(hit(117, lane0Lower)).toEqual({ index: 0, zone: 'trim-end' });
    expect(hit(42, lane0Upper)).toEqual({ index: 0, zone: 'fade-in' });
    expect(hit(117, lane0Upper)).toEqual({ index: 0, zone: 'fade-out' });
  });

  it('grabs an existing fade by its handle', () => {
    // Region 2 starts at 80 px with a 20 px fade-in: its handle is at 100 px.
    expect(hit(101, lane1Upper)).toEqual({ index: 2, zone: 'fade-in' });
    expect(hit(120, lane1Upper)).toEqual({ index: 2, zone: 'body' });
  });

  it('selects what a marquee touches', () => {
    const rect = { x: 100, y: RULER_HEIGHT + 5, width: 70, height: 50 };
    expect(itemsInRect(s, index, view, rect).sort()).toEqual([0, 1, 2]);
    expect(itemsInRect(s, index, view, { ...rect, height: 20 })).toEqual([0, 1]);
    expect(itemsInRect(s, index, view, { ...rect, x: 125, width: 5 })).toEqual([2]);
  });
});

describe('coordinates and snapping', () => {
  it('maps pixels to ticks and lanes', () => {
    expect(tickAtX({ ...view, originTick: 960 }, 40)).toBe(1920);
    expect(laneAtY(view, RULER_HEIGHT + 60)).toBe(1.5);
    expect(laneAtY(view, 5)).toBe(-1);
  });

  it('snaps to the finest grid that is at least 12 px apart', () => {
    expect(snapStep(960, 4, 40 / 960)).toBe(480); // a sixteenth is 10 px: too fine; an eighth is 20 px
    expect(snapStep(960, 4, 200 / 960)).toBe(240); // sixteenths at 50 px
    expect(snapStep(960, 4, 2 / 960)).toBe(3840 * 2); // a bar is 8 px: two bars
  });

  it('rounds to the step and never below zero', () => {
    expect(snapTick(1000, 240)).toBe(960);
    expect(snapTick(1100, 240)).toBe(1200);
    expect(snapTick(-300, 240)).toBe(0);
    expect(snapTick(1234.6, 0)).toBe(1235);
  });
});
