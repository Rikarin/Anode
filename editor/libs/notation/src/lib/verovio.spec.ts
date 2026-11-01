import type { ScoreLayout } from '@anode/api-client';
import { VerovioToolkit } from 'verovio/esm';
import createVerovioModule from 'verovio/wasm';
import {
  eventPoints,
  hitOf,
  measureEndX,
  notePoints,
  notesInRect,
  staffAt,
  staffBoxes,
  staffPositionAt,
  tickAtX,
  toScorePoint
} from './geometry';
import { spikeMei } from './spec-mei';
import { VerovioEngine, type VerovioToolkitLike } from './verovio-engine';

/** `ScoreLayout.Default` (the api-client's `DefaultScoreLayout`, not imported: this library's tests run without it). */
const Layout: ScoreLayout = {
  pageWidthMm: 210,
  pageHeightMm: 297,
  margins: { top: 15, bottom: 15, left: 15, right: 15 },
  staffHeightMm: 7,
  title: '',
  subtitle: '',
  composer: '',
  header: '',
  footer: '',
  transposingScore: false,
  instrumentNames: []
};

let toolkit: VerovioToolkitLike;

/** jsdom by name: it has no type declarations here, and only these tests parse SVG with it. */
async function parse(svg: string): Promise<{ document: Document; root: SVGSVGElement }> {
  const name = 'jsdom';
  const { JSDOM } = (await import(/* @vite-ignore */ name)) as {
    JSDOM: new (html: string) => { window: { document: Document } };
  };
  const document = new JSDOM(`<!doctype html><body>${svg}</body>`).window.document;
  return { document, root: document.querySelector('svg') as SVGSVGElement };
}

beforeAll(async () => {
  const module = await createVerovioModule();
  toolkit = new VerovioToolkit(module);
}, 30_000);

describe('VerovioEngine (the real Verovio, in Node)', () => {
  it('engraves the linear view as one page that keeps the ids and draws text as text', () => {
    const engine = new VerovioEngine(toolkit);
    const result = engine.render(spikeMei(4, 2), { view: 'linear', layout: Layout });
    expect(result.path).toBe('load');
    expect(result.pages).toHaveLength(1);
    const svg = result.pages[0]?.svg ?? '';
    expect(svg).toContain('id="n1" class="note"');
    expect(svg).toContain('id="llyr_6"');
    expect(svg).toContain('id="hchd_1"');
    expect(svg).toContain('id="ssym_1"');
    // Text is <text>, not outlines: lyrics, chord symbols and instrument names stay text in a PDF.
    expect(svg).toMatch(/<text[^>]*>(?:<tspan[^>]*>)*la</);
    expect(svg).toMatch(/<text[^>]*>(?:<tspan[^>]*>)*Am7</);
    expect(svg).toMatch(/<text[^>]*>(?:<tspan[^>]*>)*Part 1</);
    // No page header in the linear view.
    expect(svg).not.toContain('Spike Title');
    expect(result.pages[0]?.width).toBeGreaterThan(0);
  });

  it('breaks pages by the layout, with the title and composer as text (MEI 5.1 is read as 5.0)', () => {
    const engine = new VerovioEngine(toolkit);
    const pages = engine.render(spikeMei(40, 4), { view: 'page', layout: Layout }).pages;
    expect(pages.length).toBeGreaterThan(1);
    expect(pages[0]?.svg).toContain('Spike Title');
    expect(pages[0]?.svg).toContain('Ann Composer');
    // A4 at 100 %: 210 mm is 793.7 CSS pixels.
    expect(pages[0]?.width).toBeCloseTo(793.7, 0);
    expect(pages[0]?.height).toBeCloseTo(1122.5, 0);
  });

  it('answers unchanged requests from the cache, re-lays out for new options, and renders only the pages asked for', () => {
    const engine = new VerovioEngine(toolkit);
    const mei = spikeMei(40, 4);
    const first = engine.render(mei, { view: 'page', layout: Layout, pages: [1] });
    expect(first.path).toBe('load');
    expect(first.pages[0]?.svg).not.toBe('');
    expect(first.pages[1]?.svg).toBe('');
    const rest = engine.render(mei, { view: 'page', layout: Layout });
    expect(rest.path).toBe('cached');
    expect(rest.pages.every(p => p.svg !== '')).toBe(true);
    const zoomed = engine.render(mei, { view: 'page', layout: Layout, zoom: 200 });
    expect(zoomed.path).toBe('cached');
    expect(zoomed.pages[0]?.width).toBeCloseTo(2 * (rest.pages[0]?.width ?? 0), 1);
    const smaller = engine.render(mei, { view: 'page', layout: { ...Layout, staffHeightMm: 5 } });
    expect(smaller.path).toBe('layout');
    expect(smaller.pages.length).toBeLessThan(rest.pages.length);
    const edited = engine.render(spikeMei(40, 4, 3), { view: 'page', layout: { ...Layout, staffHeightMm: 5 } });
    expect(edited.path).toBe('load');
  });

  it('reports MEI it cannot read', () => {
    const engine = new VerovioEngine(toolkit);
    expect(() => engine.render('not mei', { view: 'linear', layout: Layout })).toThrow(/could not read/);
  });
});

describe('reading the SVG back', () => {
  it('maps elements to notes, syllables, chord symbols, symbols and measures', async () => {
    const svg = new VerovioEngine(toolkit).render(spikeMei(2, 2), { view: 'page', layout: Layout }).pages[0]?.svg;
    const { root } = await parse(svg ?? '');
    const head = root.querySelector('#n3 .notehead use');
    expect(hitOf(head)).toEqual({ kind: 'note', noteId: 3, elementId: 'n3' });
    expect(hitOf(root.querySelector('#llyr_6 tspan'))).toEqual({
      kind: 'lyric',
      lyricId: 'lyr_6',
      elementId: 'llyr_6'
    });
    expect(hitOf(root.querySelector('#hchd_2 tspan'))).toMatchObject({ kind: 'chordSymbol', chordId: 'chd_2' });
    expect(hitOf(root.querySelector('#ssym_1'))).toMatchObject({ kind: 'symbol', symbolId: 'sym_1' });
    expect(hitOf(root.querySelector('#m2 .barLine'))).toMatchObject({ kind: 'measure', measure: 2 });
    // A clef's generated id never reads as a note or symbol.
    expect(hitOf(root.querySelector('.clef'))).toMatchObject({ kind: 'measure', measure: 1 });
  });

  it('finds note heads, staves, staff positions and display ticks', async () => {
    const svg = new VerovioEngine(toolkit).render(spikeMei(2, 2), { view: 'page', layout: Layout }).pages[0]?.svg;
    const { root } = await parse(svg ?? '');
    const points = notePoints(root);
    expect(points.size).toBe(24);
    const boxes = staffBoxes(root);
    expect(boxes.map(b => [b.measure, b.staff])).toEqual([
      [1, 0],
      [1, 1],
      [2, 0],
      [2, 1]
    ]);
    const treble = boxes[0];
    expect(treble?.lines).toHaveLength(5);
    // Note 5 is C5 in the treble staff: the third space, position 5; note 6 is E4, the bottom line.
    const c5 = points.get(5);
    const e4 = points.get(6);
    expect(staffPositionAt(c5?.y ?? 0, treble?.lines ?? [])).toBe(5);
    expect(staffPositionAt(e4?.y ?? 0, treble?.lines ?? [])).toBe(0);
    expect(staffAt(boxes, c5 ?? { x: 0, y: 0 })).toBe(treble);
    // The bass staff's first note is below it.
    const bassNote = points.get(7);
    expect(staffAt(boxes, bassNote ?? { x: 0, y: 0 })?.staff).toBe(1);

    // Display ticks: the events of measure 1's treble staff are at 0, 480, 960, 1440, 1920 and 2880.
    const ticks = new Map([
      ['n1', 0],
      ['n2', 480],
      ['n3', 960],
      ['n4', 1440],
      ['n5', 1920],
      ['n6', 2880]
    ]);
    const anchors = eventPoints(treble?.element as Element)
      .filter(e => ticks.has(e.elementId))
      .map(e => ({ x: e.point.x, tick: ticks.get(e.elementId) ?? 0 }));
    expect(anchors).toHaveLength(6);
    const measure = { startTick: 0, endTick: 3840, startX: treble?.left ?? 0, endX: measureEndX(boxes, 1) };
    const n3 = points.get(3)?.x ?? 0;
    const n4 = points.get(4)?.x ?? 0;
    expect(tickAtX(n3, anchors, measure)).toBe(960);
    expect(tickAtX((n3 + n4) / 2, anchors, measure)).toBe(1200);
    expect(tickAtX(measure.startX, anchors, measure)).toBe(0);
    expect(tickAtX(measure.endX, anchors, measure)).toBe(3840);

    // The marquee around the first two heads.
    const n1 = points.get(1);
    const n2 = points.get(2);
    const top = (treble?.lines[0] ?? 0) - 1000;
    const rect = { x: (n1?.x ?? 0) - 10, y: top, width: (n2?.x ?? 0) - (n1?.x ?? 0) + 20, height: 2000 };
    expect(notesInRect(root, rect)).toEqual([1, 2]);
  });

  it('converts a pointer position into drawing units without layout', async () => {
    const svg = new VerovioEngine(toolkit).render(spikeMei(2, 2), { view: 'page', layout: Layout }).pages[0]?.svg;
    const { root } = await parse(svg ?? '');
    root.getBoundingClientRect = () => ({ left: 100, top: 50, width: 210, height: 297 }) as DOMRect;
    // 1 CSS pixel is 1 mm here: 100 drawing units; the margin (15 mm) is 1500 units.
    expect(toScorePoint(root, 100 + 15, 50 + 15)).toEqual({ x: 0, y: 0 });
    expect(toScorePoint(root, 100 + 25, 50 + 20)).toEqual({ x: 1000, y: 500 });
  });
});

// ADR-0011's spike criteria, measured on demand (`nx run notation:spike`): a 4-staff, 100-bar score in under 500 ms,
// a one-note edit re-rendered in under 150 ms. The worker runs exactly this engine.
describe.runIf(process.env['ANODE_NOTATION_SPIKE'] === '1')('ADR-0011 spike', () => {
  function median(values: number[]): number {
    const sorted = [...values].sort((a, b) => a - b);
    return sorted[Math.floor(sorted.length / 2)] ?? 0;
  }

  for (const view of ['linear', 'page'] as const) {
    it(`renders 4 staves × 100 bars and a one-note edit (${view})`, () => {
      const engine = new VerovioEngine(toolkit);
      const full: number[] = [];
      const edit: number[] = [];
      const visible: number[] = [];
      for (let run = 0; run < 7; run++) {
        // A fresh document each run: the first full render.
        engine.render(spikeMei(1, 1), { view, layout: Layout });
        full.push(engine.render(spikeMei(100, 4), { view, layout: Layout }).ms);
        // The one-note edit: new MEI, same options, every page.
        edit.push(engine.render(spikeMei(100, 4, 1 + 6 * run), { view, layout: Layout }).ms);
        // The same edit with only the first page visible (page view: the re-render path).
        visible.push(engine.render(spikeMei(100, 4, 301 + 6 * run), { view, layout: Layout, pages: [1] }).ms);
      }
      const result = { view, fullMs: median(full), editMs: median(edit), editVisibleMs: median(visible) };
      console.warn(`ADR-0011 spike ${JSON.stringify(result)}`);
      expect(result.fullMs).toBeLessThan(500);
      expect(Math.min(result.editMs, result.editVisibleMs)).toBeLessThan(150);
    }, 120_000);
  }
});
