import type { RenderedPage, RenderOptions } from './renderer';
import { engraveOptions, forVerovio, optionsKey, pageSize } from './verovio-options';

/** The part of Verovio's toolkit (`VerovioToolkit` of `verovio/esm`) the engine uses. */
export interface VerovioToolkitLike {
  setOptions(options: object): boolean;
  loadData(data: string): boolean;
  getPageCount(): number;
  renderToSVG(pageNo?: number, xmlDeclaration?: boolean): string;
  redoLayout(options?: { resetCache?: boolean }): void;
  getLog(): string;
}

/** How a render was made: from the cache, by re-laying out the loaded MEI (new options), or by loading new MEI. */
export type RenderPath = 'cached' | 'layout' | 'load';

/** A render's pages, how it was made and how long it took in the engine. */
export interface EngineResult {
  pages: RenderedPage[];
  path: RenderPath;
  ms: number;
}

/**
 * Verovio driven for the Score Editor (in the worker; in Node for tests and the spike). It keeps the MEI and options it
 * last loaded and their pages' SVG, so a request that changes nothing answers from the cache, one that changes only the
 * engraving options re-lays out without parsing, and a small edit (new MEI, same options) loads and renders only the
 * pages asked for (the visible ones), the others filled in by a later request from the cache path.
 */
export class VerovioEngine {
  private mei: string | null = null;
  private key: string | null = null;
  private pageCount = 0;
  private svgs = new Map<number, string>();

  constructor(
    private readonly toolkit: VerovioToolkitLike,
    private readonly now: () => number = () => performance.now()
  ) {}

  render(mei: string, options: RenderOptions): EngineResult {
    const started = this.now();
    const engrave = engraveOptions(options);
    const key = optionsKey(engrave);
    let path: RenderPath = 'cached';
    if (key !== this.key) {
      this.toolkit.setOptions(engrave);
      if (this.mei === mei) {
        this.toolkit.redoLayout({ resetCache: false });
        path = 'layout';
      }
      this.key = key;
    }
    if (this.mei !== mei) {
      if (!this.toolkit.loadData(forVerovio(mei))) {
        this.mei = null;
        throw new Error(`Verovio could not read the score: ${this.toolkit.getLog().trim() || 'invalid MEI'}`);
      }
      this.mei = mei;
      path = 'load';
    }
    if (path !== 'cached') {
      this.pageCount = this.toolkit.getPageCount();
      this.svgs = new Map();
    }

    const wanted = new Set(
      (options.pages ?? range(this.pageCount)).filter(p => Number.isInteger(p) && p >= 1 && p <= this.pageCount)
    );
    const pages: RenderedPage[] = [];
    for (let page = 1; page <= this.pageCount; page++) {
      let svg = this.svgs.get(page) ?? '';
      if (!svg && wanted.has(page)) {
        svg = this.toolkit.renderToSVG(page, false);
        this.svgs.set(page, svg);
      }
      const size = svg ? pageSize(svg, options.zoom ?? 100) : { width: 0, height: 0 };
      pages.push({ page, svg, ...size });
    }
    return { pages, path, ms: this.now() - started };
  }
}

function range(count: number): number[] {
  return Array.from({ length: count }, (_, i) => i + 1);
}
