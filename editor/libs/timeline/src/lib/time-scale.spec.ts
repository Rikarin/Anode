import { TimeScale } from './time-scale';

describe('TimeScale', () => {
  const scale = new TimeScale(960, 40);

  it('converts ticks to pixels and back', () => {
    expect(scale.toPixels(960)).toBe(40);
    expect(scale.toTicks(20)).toBe(480);
    expect(scale.scrolledTo(960).toPixels(1920)).toBe(40);
  });

  it('keeps the tick under the anchor when zooming', () => {
    const before = scale.scrolledTo(960);
    const after = before.zoomed(2, 100);
    expect(after.toTicks(100)).toBeCloseTo(before.toTicks(100));
    expect(after.pixelsPerQuarter).toBe(80);
  });

  it('refuses a scale without a positive zoom', () => {
    expect(() => new TimeScale(960, 0)).toThrow(RangeError);
  });
});
