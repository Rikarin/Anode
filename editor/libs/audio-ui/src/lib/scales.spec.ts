import { gmDrumPads, padRows, scaleNotes, touchVelocity } from './scales';

describe('the remote keyboard and pads', () => {
  it('lays a scale out from its root, the root again on top', () => {
    expect(scaleNotes('major', 0, 48, 1)).toEqual([48, 50, 52, 53, 55, 57, 59, 60]);
    expect(scaleNotes('minorPentatonic', 9, 48, 1)).toEqual([57, 60, 62, 64, 67, 69]);
    expect(scaleNotes('chromatic', 0, 120, 1)).toEqual([120, 121, 122, 123, 124, 125, 126, 127]);
  });

  it('takes velocity from where a key is struck', () => {
    expect(touchVelocity(0)).toBe(40);
    expect(touchVelocity(1)).toBe(127);
    expect(touchVelocity(2)).toBe(127);
    expect(touchVelocity(Number.NaN)).toBe(105);
  });

  it('puts the lowest pad at the bottom left', () => {
    const pads = gmDrumPads();
    expect(pads.map(p => p.note)).toEqual(Array.from({ length: 16 }, (_, i) => 36 + i));
    expect(pads[0]?.name).toBe('Kick');
    const rows = padRows(pads);
    expect(rows[3]?.map(p => p?.note)).toEqual([36, 37, 38, 39]);
    expect(rows[0]?.map(p => p?.note)).toEqual([48, 49, 50, 51]);
    expect(padRows([{ note: 40 }, { note: 36 }])).toEqual([[{ note: 36 }, { note: 40 }, null, null]]);
  });
});
