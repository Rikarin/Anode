import type { ScoreLayout, ScoreView } from '@anode/api-client';
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { VerovioToolkit } from 'verovio/esm';
import createVerovioModule from 'verovio/wasm';
import { staffBoxes } from './geometry';
import { VerovioEngine, type VerovioToolkitLike } from './verovio-engine';

/*
 * The notation engine's own MEI (Anode.Notation's golden views and their MEI, engine/tests/Anode.Notation.Tests/Golden)
 * engraved by the real Verovio (docs/10-phase-9.md, integration; ADR-0011): every id the editor maps back is in the SVG,
 * the k-th staff of a measure is the k-th staff of the view, and what the engine encodes the way Verovio reads it is
 * drawn (the percussion clef and its notes' positions, tempo marks, chord symbols with ♯ and ♭, hairpins and octave
 * lines ending on a bar line, key changes mid-score).
 */

const Golden = join(import.meta.dirname, '../../../../../engine/tests/Anode.Notation.Tests/Golden');

let toolkit: VerovioToolkitLike;

async function parse(svg: string): Promise<SVGSVGElement> {
  const name = 'jsdom';
  const { JSDOM } = (await import(/* @vite-ignore */ name)) as {
    JSDOM: new (html: string) => { window: { document: Document } };
  };
  return new JSDOM(`<!doctype html><body>${svg}</body>`).window.document.querySelector('svg') as SVGSVGElement;
}

function golden(name: string): { view: ScoreView; mei: string } {
  return {
    view: JSON.parse(readFileSync(join(Golden, `${name}.json`), 'utf8')) as ScoreView,
    mei: readFileSync(join(Golden, `${name}.mei`), 'utf8')
  };
}

function render(name: string, view: 'linear' | 'page' = 'linear'): { svg: string; log: string } {
  const { view: score, mei } = golden(name);
  const layout: ScoreLayout = score.layout;
  const engine = new VerovioEngine(toolkit);
  const svg = engine
    .render(mei, { view, layout })
    .pages.map(p => p.svg)
    .join('\n');
  return { svg, log: toolkit.getLog() };
}

/** The x extent of an element's drawn points (polylines and paths). */
function extent(element: Element | null): { min: number; max: number } {
  const xs: number[] = [];
  for (const shape of Array.from(element?.querySelectorAll('polyline, path') ?? [])) {
    const text = shape.getAttribute('points') ?? shape.getAttribute('d') ?? '';
    for (const match of text.matchAll(/(-?[\d.]+)[ ,](-?[\d.]+)/g)) {
      xs.push(Number(match[1]));
    }
  }
  return { min: Math.min(...xs), max: Math.max(...xs) };
}

beforeAll(async () => {
  toolkit = new VerovioToolkit(await createVerovioModule());
}, 30_000);

const Names = readdirSync(Golden)
  .filter(f => f.endsWith('.mei'))
  .map(f => f.replace(/\.mei$/, ''))
  .sort();

describe("the notation engine's MEI in Verovio", () => {
  it.each(Names)('%s: every id the editor maps back is drawn, staff by staff as in the view', async name => {
    const { view, mei } = golden(name);
    for (const mode of ['linear', 'page'] as const) {
      const { svg, log } = render(name, mode);
      expect(log).not.toMatch(/\[Error\]/);
      // Notes, chords, rests, symbols, syllables and chord symbols keep their ids (ties and tempo marks have none).
      for (const [, id] of mei.matchAll(/xml:id="([ncrslh][^"]*)"/g)) {
        expect(svg).toContain(`id="${id}"`);
      }
      // The k-th staff of a measure is the k-th staff of the view, counted across the parts: each event is there.
      const root = await parse(svg);
      const boxes = staffBoxes(root);
      const staves = view.parts.flatMap(p => p.staves).length;
      expect(boxes).toHaveLength(view.measures.length * staves);
      for (const [m, measure] of view.measures.entries()) {
        let k = 0;
        for (const part of view.parts) {
          for (const staff of part.measures[m]?.staves ?? []) {
            const box = boxes.find(b => b.measure === measure.number && b.staff === k);
            const drawn = root.querySelectorAll('g.measure')[m]?.querySelectorAll(':scope > g.staff')[k];
            expect(box).toBeDefined();
            for (const event of staff.voices.flatMap(v => v.events)) {
              const id = event.kind === 'rest' && event.durationTicks === measure.lengthTicks ? null : event.id;
              if (id) {
                expect(drawn?.querySelector(`[id="${id}"]`)).not.toBeNull();
              }
            }
            k++;
          }
        }
      }
    }
  });

  it('draws the percussion clef, and drum notes at their staff positions (loc)', async () => {
    const { svg } = render('drums');
    expect(svg).toMatch(/class="clef"[\s\S]{0,200}#E069/); // SMuFL unpercussionClef1
    const root = await parse(svg);
    const y = (id: string) => {
      const transform = root.querySelector(`[id="${id}"] .notehead use`)?.getAttribute('transform') ?? '';
      return Number(/translate\(\s*[-\d.]+[ ,]+([-\d.]+)/.exec(transform)?.[1]);
    };
    // Kick (loc 1) below snare (loc 5) below the hi-hat (loc 9), two staff steps apart per two positions.
    const { view } = golden('drums');
    const notes = view.parts[0]?.measures[0]?.staves[0]?.voices[0]?.events.flatMap(e => e.notes) ?? [];
    const at = (position: number) => notes.find(n => n.staffPosition === position)?.id ?? '';
    expect(y(at(1))).toBeGreaterThan(y(at(5)));
    expect(y(at(5))).toBeGreaterThan(y(at(9)));
    expect(y(at(1)) - y(at(5))).toBeCloseTo(y(at(5)) - y(at(9)), 0);
  });

  it('draws tempo marks and chord symbols as text, ♯ and ♭ as accidentals', () => {
    const { svg } = render('content');
    expect(svg).toMatch(/>♩ = 96</);
    const harm = /<g id="hchd_5" class="harm">([\s\S]*?)<\/g>/.exec(svg)?.[1] ?? '';
    expect(harm).toMatch(/>F</);
    expect(harm).toMatch(/>m7b5</);
    expect(harm).toMatch(/<tspan font-family="Leipzig"[^>]*>[^<]+<\/tspan>/); // the ♯ in the music font
  });

  it('ends hairpins and octave lines on the bar line (tstamp2 "0m+5"), and draws key changes mid-score', async () => {
    const { svg } = render('content');
    const root = await parse(svg);
    const bar = (m: string) => extent(root.querySelector(`#${m} .barLine`)).max;
    const crescendo = extent(root.querySelector('#ssym_06'));
    expect(crescendo.max).toBeLessThanOrEqual(bar('m1'));
    expect(bar('m1') - crescendo.max).toBeLessThan(300);
    const octave = extent(root.querySelector('#ssym_15'));
    expect(octave.max).toBeLessThanOrEqual(bar('m2'));
    expect(bar('m2') - octave.max).toBeLessThan(300);
    // The key changes at bar 3: key signatures in the system after the first measure's.
    const keys = Array.from(root.querySelectorAll('.keySig')).filter(k => !k.closest('#m1'));
    expect(keys.length).toBeGreaterThan(0);
  });
});
