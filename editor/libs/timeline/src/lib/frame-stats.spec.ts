import { droppedFrames, intervals, percentile, summarize } from './frame-stats';

describe('percentile', () => {
  it('interpolates between closest ranks', () => {
    const sorted = [10, 20, 30, 40, 50];
    expect(percentile(sorted, 0)).toBe(10);
    expect(percentile(sorted, 50)).toBe(30);
    expect(percentile(sorted, 95)).toBeCloseTo(48);
    expect(percentile(sorted, 100)).toBe(50);
  });

  it('is NaN for no samples', () => {
    expect(percentile([], 50)).toBeNaN();
  });
});

describe('summarize', () => {
  it('sorts before ranking', () => {
    const summary = summarize([5, 1, 4, 2, 3]);
    expect(summary).toMatchObject({ count: 5, mean: 3, p50: 3, max: 5 });
  });
});

describe('droppedFrames', () => {
  it('counts the refreshes missed between presented frames, tolerating jitter', () => {
    const refresh = 16.67;
    expect(droppedFrames([16.6, 17.9, 15.2], refresh)).toBe(0);
    expect(droppedFrames([33.3, 16.7, 50.1], refresh)).toBe(3);
  });
});

describe('intervals', () => {
  it('differences consecutive timestamps', () => {
    expect(Array.from(intervals([0, 16, 33, 50]))).toEqual([16, 17, 17]);
    expect(intervals([1]).length).toBe(0);
  });
});
