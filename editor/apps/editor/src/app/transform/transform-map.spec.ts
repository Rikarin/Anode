import { identityMap } from '../midi-edit/transform';
import { drawMapLine, mapPoint, normalizeMap, nudgeMap } from './transform-map';

describe('Transform map editor', () => {
  it('reads a missing or short map as the identity, clamped to 0…127', () => {
    expect(normalizeMap(null)).toEqual(identityMap());
    const map = normalizeMap([200, -3, 5.4]);
    expect(map.slice(0, 4)).toEqual([127, 0, 5, 3]);
    expect(map).toHaveLength(128);
  });

  it('draws a stroke into every step it crosses, either way', () => {
    const right = drawMapLine(identityMap(), 10, 0, 14, 40);
    expect(right.slice(9, 16)).toEqual([9, 0, 10, 20, 30, 40, 15]);
    const left = drawMapLine(identityMap(), 14, 40, 10, 0);
    expect(left.slice(10, 15)).toEqual([0, 10, 20, 30, 40]);
    const dot = drawMapLine(identityMap(), 3, 0, 3, 99);
    expect(dot[3]).toBe(99);
    expect(drawMapLine(identityMap(), -5, 0, 300, 127)[127]).toBe(127);
  });

  it('nudges a step from the keyboard, within 0…127', () => {
    expect(nudgeMap(identityMap(), 60, 8)[60]).toBe(68);
    expect(nudgeMap(identityMap(), 60, -127)[60]).toBe(0);
    expect(nudgeMap(identityMap(), 127, 10)[127]).toBe(127);
  });

  it('maps a pointer to a step and an output (top 127)', () => {
    expect(mapPoint(0, 0, 256, 128)).toEqual({ index: 0, value: 127 });
    expect(mapPoint(255.9, 128, 256, 128)).toEqual({ index: 127, value: 0 });
    expect(mapPoint(128, 64, 256, 128)).toEqual({ index: 64, value: 64 });
    expect(mapPoint(400, -10, 256, 128)).toEqual({ index: 127, value: 127 });
  });
});
