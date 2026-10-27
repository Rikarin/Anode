import { colorToCss, createItems, itemBuffers, packColor, parseHexColor } from './items';

describe('colours', () => {
  it('pack little-endian RGBA, the byte order WebGL reads', () => {
    const color = packColor(0x11, 0x22, 0x33, 0x44);
    expect(Array.from(new Uint8Array(new Uint32Array([color]).buffer))).toEqual([0x11, 0x22, 0x33, 0x44]);
  });

  it('parse #rrggbb and #rrggbbaa', () => {
    expect(parseHexColor('#ff8000')).toBe(packColor(255, 128, 0));
    expect(parseHexColor('#ff800080')).toBe(packColor(255, 128, 0, 128));
    expect(() => parseHexColor('red')).toThrow(RangeError);
  });

  it('print as CSS', () => {
    expect(colorToCss(packColor(1, 2, 3))).toBe('rgb(1 2 3)');
    expect(colorToCss(packColor(1, 2, 3, 0))).toBe('rgb(1 2 3 / 0.000)');
  });
});

describe('createItems', () => {
  it('allocates one transferable buffer per column', () => {
    const items = createItems(10);
    expect(items.start.length).toBe(10);
    expect(new Set(itemBuffers(items)).size).toBe(4);
  });
});
