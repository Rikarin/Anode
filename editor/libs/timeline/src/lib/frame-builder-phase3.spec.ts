import { darkTheme, fadeGain, FrameBuilder, Layer, RULER_HEIGHT } from './frame-builder';
import { createItems, createRegionDetails, ItemFlag, type TimelineScene, type TimelineView } from './items';

const view: TimelineView = { originTick: 0, pixelsPerTick: 40 / 960, firstLane: 0, laneHeight: 60, playheadTick: 0 };

function rows(batch: ReturnType<FrameBuilder['build']>, which: number): number[][] {
  const from = which === 0 ? 0 : (batch.layerEnds[which - 1] ?? 0);
  const to = batch.layerEnds[which] ?? 0;
  const out: number[][] = [];
  for (let i = from; i < to; i++) {
    out.push([...Array.from(batch.rects.subarray(4 * i, 4 * i + 4)), batch.colors[i] ?? 0]);
  }
  return out;
}

function regions(flags: number[], fadeIn = 0, curve = 0): TimelineScene {
  const items = createItems(flags.length);
  const details = createRegionDetails(flags.length, 25, ['Folder', '']);
  flags.forEach((flag, i) => {
    items.start[i] = 0;
    items.length[i] = 3840;
    items.color[i] = 0xff3366cc;
    details.flags[i] = flag;
    details.fadeIn[i] = fadeIn;
    details.fadeInCurve[i] = curve;
  });
  return { kind: 'regions', ppq: 960, beatsPerBar: 4, laneCount: 1, items, regions: details };
}

describe('fadeGain', () => {
  it('follows x^(2^(-2c))', () => {
    expect(fadeGain(0.5, 0)).toBeCloseTo(0.5);
    expect(fadeGain(0.5, -1)).toBeCloseTo(0.0625);
    expect(fadeGain(0.5, 1)).toBeCloseTo(0.5 ** 0.25);
  });
});

describe('FrameBuilder, Phase 3', () => {
  it('draws the punch range as a red band along the ruler', () => {
    const batch = new FrameBuilder(regions([0])).build(
      { ...view, punch: { enabled: true, start: 960, end: 1920 } },
      400,
      200
    );
    const ruler = rows(batch, Layer.Ruler).filter(r => r[4] === darkTheme.punch);
    expect(ruler[0]).toEqual([40, RULER_HEIGHT - 4, 40, 3, darkTheme.punch]);
    expect(ruler).toHaveLength(3); // the band and its two locators
  });

  it('starts the lanes under whatever sits between them and the ruler', () => {
    const batch = new FrameBuilder(regions([0])).build({ ...view, lanesTop: RULER_HEIGHT + 50 }, 400, 300);
    const [region] = rows(batch, Layer.Items);
    expect(region?.[1]).toBe(RULER_HEIGHT + 50 + 1);
  });

  it('draws a part inside its item, flat and under the name bar, after the item', () => {
    const batch = new FrameBuilder(regions([ItemFlag.Inner, ItemFlag.Folder])).build(view, 400, 200);
    const items = rows(batch, Layer.Items);
    expect(items).toHaveLength(2);
    expect(items[1]?.[1]).toBeGreaterThan(items[0]?.[1] ?? 0); // the part comes second, below the name bar
  });

  it('bends a fade-in by its curve', () => {
    const fade = (curve: number) => {
      const batch = new FrameBuilder(regions([0], 24_000, curve)).build(view, 400, 200);
      // The fade's dots in the details layer: the one nearest the fade's middle (x = 20).
      const dots = rows(batch, Layer.Details).filter(r => r[2] === 1.5);
      return dots.reduce((best, r) => (Math.abs((r[0] ?? 0) - 19.25) < Math.abs((best[0] ?? 0) - 19.25) ? r : best));
    };
    // A slow start (−1) is lower (a larger y) halfway through than a linear fade, a fast one (+1) higher.
    expect(fade(-1)[1]).toBeGreaterThan(fade(0)[1] ?? 0);
    expect(fade(1)[1]).toBeLessThan(fade(0)[1] ?? 0);
  });

  it('numbers bars by the signature track', () => {
    const scene = { ...regions([0]), meter: [{ tick: 0, numerator: 3, denominator: 4 }] };
    const batch = new FrameBuilder(scene).build({ ...view, pixelsPerTick: 60 / 960 }, 800, 200);
    const labels = batch.texts
      .slice(0, batch.textCount)
      .map((text, k) => ({ text, x: batch.textX[k] ?? 0 }))
      .filter(l => /^\d+$/.test(l.text));
    expect(labels.slice(0, 3).map(l => l.text)).toEqual(['1', '2', '3']);
    // 3/4 bars are 3 beats of 60 px.
    expect((labels[1]?.x ?? 0) - (labels[0]?.x ?? 0)).toBeCloseTo(180);
  });
});
