import type { SampleRange } from '@anode/api-client';
import {
  bucketFor,
  clampView,
  extendRange,
  fitView,
  formatTime,
  frameAtX,
  MaxSampleValues,
  MinFramesPerPixel,
  rangeOf,
  rulerTicks,
  tileRequest,
  tilesFor,
  TileValues,
  viewOfRange,
  visibleRange,
  xAtFrame,
  zoomView
} from './audio-file-geometry';
import { columnPeaks, SampleTiles } from './sample-tiles';

describe('Audio File Editor geometry', () => {
  it('maps frames to x and back at every zoom', () => {
    for (const framesPerPixel of [1 / 32, 1 / 4, 1, 3, 480, 60_000]) {
      const view = { start: 12_345.5, framesPerPixel };
      for (const frame of [12_345.5, 12_400, 99_999]) {
        const x = xAtFrame(frame, view);
        expect(frameAtX(x, view)).toBeCloseTo(frame, 6);
      }
      expect(xAtFrame(view.start + framesPerPixel * 10, view)).toBeCloseTo(10, 9);
    }
  });

  it('keeps the view inside the file and the zoom between 32 px a sample and the whole file', () => {
    const total = 480_000;
    expect(fitView(total, 800)).toEqual({ start: 0, framesPerPixel: 600 });
    expect(clampView({ start: -50, framesPerPixel: 1e9 }, total, 800)).toEqual({ start: 0, framesPerPixel: 600 });
    expect(clampView({ start: 10, framesPerPixel: 1e-6 }, total, 800).framesPerPixel).toBe(MinFramesPerPixel);
    // Scrolled past the end: the last screenful.
    expect(clampView({ start: 479_990, framesPerPixel: 1 }, total, 800).start).toBe(total - 800);
    // A file shorter than the view at the deepest zoom still starts at 0.
    expect(clampView({ start: 5, framesPerPixel: 1 / 32 }, 10, 800)).toEqual({ start: 0, framesPerPixel: 1 / 32 });
  });

  it('zooms around the anchor and onto a range', () => {
    const total = 480_000;
    const view = { start: 1000, framesPerPixel: 100 };
    const zoomed = zoomView(view, 4, 200, total, 800);
    expect(zoomed.framesPerPixel).toBe(25);
    expect(frameAtX(200, zoomed)).toBeCloseTo(frameAtX(200, view), 9);
    const onto = viewOfRange({ start: 48_000, end: 48_800 }, total, 800);
    expect(xAtFrame(48_000, onto)).toBeGreaterThan(0);
    expect(xAtFrame(48_800, onto)).toBeLessThan(800);
  });

  it('asks for raw samples when zoomed in and min/max buckets when zoomed out', () => {
    expect(bucketFor(1 / 32)).toBe(1);
    expect(bucketFor(1)).toBe(1);
    expect(bucketFor(1.99)).toBe(1);
    expect(bucketFor(2)).toBe(2);
    expect(bucketFor(3.5)).toBe(2);
    expect(bucketFor(600)).toBe(512);
    // Every tile's request stays under the Server's limit of values.
    for (const bucket of [1, 2, 512, 32_768]) {
      const { frames } = tileRequest(3, bucket, Number.MAX_SAFE_INTEGER);
      expect(Math.ceil(frames / bucket)).toBeLessThanOrEqual(MaxSampleValues);
    }
  });

  it('covers the visible range with tiles', () => {
    const view = { start: 4000.5, framesPerPixel: 1 };
    expect(visibleRange(view, 800, 1_000_000)).toEqual({ start: 4000, end: 4802 });
    expect(visibleRange({ start: 0, framesPerPixel: 600 }, 800, 480_000)).toEqual({ start: 0, end: 480_000 });
    expect(tilesFor({ start: 4000, end: 4802 }, 1)).toEqual([0, 1]);
    expect(tilesFor({ start: 0, end: 480_000 }, 512)).toEqual([0]);
    expect(tilesFor({ start: 5, end: 5 }, 1)).toEqual([]);
    expect(tileRequest(1, 1, 5000)).toEqual({ start: TileValues, frames: 5000 - TileValues });
  });

  it('labels the ruler in time or samples', () => {
    const view = { start: 0, framesPerPixel: 600 };
    const time = rulerTicks(view, 800, 48_000, 'time');
    const majors = time.filter(t => t.major);
    expect(majors[0]?.label).toBe('0:00.000');
    expect(majors[1]?.label).toBe('0:02.000');
    expect((majors[1]?.x ?? 0) - (majors[0]?.x ?? 0)).toBeGreaterThanOrEqual(90);
    const samples = rulerTicks({ start: 100, framesPerPixel: 1 / 16 }, 800, 48_000, 'samples').filter(t => t.major);
    expect(samples.map(t => t.label).slice(0, 3)).toEqual(['100', '110', '120']);
    expect(formatTime(48_000 * 61.5, 48_000)).toBe('1:01.500');
    expect(formatTime(1, 48_000, 1)).toBe('0:00.00002');
  });

  it('selects ranges either way and extends the nearer edge', () => {
    expect(rangeOf(500, 100, 1000)).toEqual({ start: 100, end: 500 });
    expect(rangeOf(-5, 2000, 1000)).toEqual({ start: 0, end: 1000 });
    expect(rangeOf(5, 5, 1000)).toBeNull();
    expect(extendRange({ start: 100, end: 500 }, 50, 1000)).toEqual({ start: 50, end: 500 });
    expect(extendRange({ start: 100, end: 500 }, 700, 1000)).toEqual({ start: 100, end: 700 });
    expect(extendRange(null, 700, 1000)).toBeNull();
  });
});

describe('Sample tiles', () => {
  const answer = (start: number, frames: number, bucket: number): SampleRange => {
    const values = Math.ceil(frames / bucket);
    if (bucket === 1) {
      return {
        startFrame: start,
        frames,
        channels: 1,
        bucket,
        samples: [Array.from({ length: values }, (_, i) => (start + i) / 1e6)]
      };
    }
    return {
      startFrame: start,
      frames,
      channels: 1,
      bucket,
      min: [Array.from({ length: values }, (_, i) => -i / 1e4)],
      max: [Array.from({ length: values }, (_, i) => i / 1e4)]
    };
  };

  it('fetches each tile once per file version and refetches after a new version', async () => {
    const fetch = vi.fn((_: string, start: number, frames: number, bucket: number) =>
      Promise.resolve(answer(start, frames, bucket))
    );
    const tiles = new SampleTiles(fetch);
    tiles.setSource('aud_1', 0, 10_000);
    await tiles.ensure('aud_1', 1, [0, 1], 10_000);
    await tiles.ensure('aud_1', 1, [0, 1], 10_000);
    expect(fetch).toHaveBeenCalledTimes(2);
    expect(fetch).toHaveBeenNthCalledWith(2, 'aud_1', TileValues, TileValues, 1);
    expect(tiles.sample(0, 4100)).toBeCloseTo(0.0041, 9);
    expect(tiles.sample(0, 9000)).toBeUndefined();

    tiles.setSource('aud_1', 1, 10_000);
    expect(tiles.sample(0, 4100)).toBeUndefined();
    await tiles.ensure('aud_1', 1, [0], 10_000);
    expect(fetch).toHaveBeenCalledTimes(3);
  });

  it('drops answers of a version that changed while they were on their way', async () => {
    let resolve: (range: SampleRange) => void = () => undefined;
    const tiles = new SampleTiles(() => new Promise(r => (resolve = r)));
    tiles.setSource('aud_1', 0, 1000);
    const done = tiles.ensure('aud_1', 1, [0], 1000);
    tiles.setSource('aud_1', 1, 1000);
    resolve(answer(0, 1000, 1));
    await done;
    expect(tiles.get(1, 0)).toBeUndefined();
  });

  it('folds min/max buckets into pixel columns', async () => {
    const tiles = new SampleTiles((_, start, frames, bucket) => Promise.resolve(answer(start, frames, bucket)));
    tiles.setSource('aud_1', 0, 8192);
    await tiles.ensure('aud_1', 4, [0], 8192);
    // 8 frames a pixel: two buckets of 4 per column.
    const { min, max } = columnPeaks(tiles, 0, { start: 0, framesPerPixel: 8 }, 4, 4, 8192);
    expect(Array.from(max).map(v => Math.round(v * 1e4))).toEqual([1, 3, 5, 7]);
    expect(Array.from(min).map(v => Math.round(v * 1e4))).toEqual([-1, -3, -5, -7]);
    // Past the file's end: nothing.
    const past = columnPeaks(tiles, 0, { start: 8190, framesPerPixel: 8 }, 3, 4, 8192);
    expect(Number.isNaN(past.max[2] ?? 0)).toBe(true);
  });
});
