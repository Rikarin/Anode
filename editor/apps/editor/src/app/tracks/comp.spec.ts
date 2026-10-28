import { canPack, clearComp, normalizeComp, segmentsOf, swipeComp, takeAt, wholeTake } from './comp';

const folder = { startTick: 0, lengthTicks: 3840 };

describe('comp maths', () => {
  it('swipes a range of a take into the comp, splitting what it falls inside', () => {
    const comp = [{ startTick: 0, endTick: 3840, takeId: 'a' }];
    expect(swipeComp(comp, 'b', 1000, 2000, folder)).toEqual([
      { startTick: 0, endTick: 1000, takeId: 'a' },
      { startTick: 1000, endTick: 2000, takeId: 'b' },
      { startTick: 2000, endTick: 3840, takeId: 'a' }
    ]);
  });

  it('swipes backwards and keeps to the folder', () => {
    expect(swipeComp([], 'b', 5000, 3000, folder)).toEqual([{ startTick: 3000, endTick: 3840, takeId: 'b' }]);
  });

  it('merges a swipe that meets the same take', () => {
    const comp = [
      { startTick: 0, endTick: 1000, takeId: 'a' },
      { startTick: 1000, endTick: 2000, takeId: 'b' }
    ];
    expect(swipeComp(comp, 'a', 900, 1500, folder)).toEqual([
      { startTick: 0, endTick: 1500, takeId: 'a' },
      { startTick: 1500, endTick: 2000, takeId: 'b' }
    ]);
  });

  it('ignores a click (too short to be a swipe)', () => {
    const comp = [{ startTick: 0, endTick: 3840, takeId: 'a' }];
    expect(swipeComp(comp, 'b', 100, 104, folder)).toEqual(comp);
  });

  it('clears a range, finds takes and whole takes', () => {
    const comp = clearComp([{ startTick: 0, endTick: 3840, takeId: 'a' }], 1000, 2000);
    expect(comp).toHaveLength(2);
    expect(takeAt(comp, 1500)).toBeNull();
    expect(takeAt(comp, 2500)).toBe('a');
    expect(segmentsOf(comp, 'a')).toHaveLength(2);
    expect(wholeTake({ ...folder, comp: normalizeComp([{ startTick: 0, endTick: 3840, takeId: 'x' }]) })).toBe('x');
    expect(wholeTake({ ...folder, comp })).toBeNull();
  });

  it('packs overlapping or touching regions of one track', () => {
    expect(
      canPack([
        { trackId: 't', startTick: 0, endTick: 100 },
        { trackId: 't', startTick: 100, endTick: 200 }
      ])
    ).toBe(true);
    expect(
      canPack([
        { trackId: 't', startTick: 0, endTick: 100 },
        { trackId: 't', startTick: 300, endTick: 400 }
      ])
    ).toBe(false);
    expect(
      canPack([
        { trackId: 't', startTick: 0, endTick: 100 },
        { trackId: 'u', startTick: 50, endTick: 400 }
      ])
    ).toBe(false);
  });
});
