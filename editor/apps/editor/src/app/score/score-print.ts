import { inject, Injectable, signal } from '@angular/core';
import type { RenderedPage } from '@anode/notation';
import { ANODE_API, type ScoreQuery } from '../core/api';
import { ConnectionService } from '../core/connection';
import { describeError, Notify } from '../core/notify';
import { NOTATION_RENDERER } from './score.store';

/** A file name from a score's title: no folder separators, at most 200 characters. */
export function pdfName(title: string): string {
  return (
    title
      .replace(/[\\/\0]/g, '-')
      .trim()
      .slice(0, 200) || 'Score'
  );
}

/**
 * Prints a score (ADR-0011, "Printing"): its page view, engraved for the layout's page, laid out as a print view of
 * one page per sheet (`@page` at the layout's size, no margins: the layout has its own), then the desktop app's
 * `printToPDF` into a file the person names, or the browser's print dialog. Verovio draws titles, names, lyrics and
 * chord symbols as SVG text, so they are text in the PDF.
 */
@Injectable({ providedIn: 'root' })
export class ScorePrint {
  private readonly api = inject(ANODE_API);
  private readonly notify = inject(Notify);
  private readonly renderer = inject(NOTATION_RENDERER);
  private readonly desktop = inject(ConnectionService).desktop;

  readonly printing = signal(false);
  /** The pages the last print laid out (for tests and the golden path). */
  readonly lastPages = signal<readonly RenderedPage[]>([]);

  /** Prints a part or score set; resolves the PDF's path in the desktop app (null when cancelled or in a browser). */
  async print(query: Pick<ScoreQuery, 'set' | 'part'>, fallbackTitle: string): Promise<string | null> {
    if (this.printing()) {
      return null;
    }
    this.printing.set(true);
    const renderer = this.renderer();
    let cleanup = (): void => undefined;
    try {
      const document = await this.api.score({ ...query, view: 'page' });
      const layout = document.view.layout;
      const pages = await renderer.render(document.mei, { view: 'page', layout });
      this.lastPages.set(pages);
      cleanup = this.layOut(pages, layout.pageWidthMm, layout.pageHeightMm);
      const title = pdfName(layout.title || fallbackTitle);
      if (this.desktop?.printToPdf) {
        const path = await this.desktop.printToPdf({
          title,
          pageWidthMm: layout.pageWidthMm,
          pageHeightMm: layout.pageHeightMm
        });
        if (path) {
          const reveal = this.desktop.reveal;
          this.notify.success(
            `Saved ${pages.length} ${pages.length === 1 ? 'page' : 'pages'} as ${path.split(/[\\/]/).pop() ?? path}`,
            reveal ? { text: 'Show in Finder', run: () => void reveal(path) } : undefined
          );
        }
        return path;
      }
      window.print();
      return null;
    } catch (error) {
      this.notify.error(`Could not print the score: ${describeError(error)}`);
      return null;
    } finally {
      cleanup();
      renderer.dispose();
      this.printing.set(false);
    }
  }

  /** The print view: the pages at the page size, and `@page` to match; returns what removes it. */
  private layOut(pages: readonly RenderedPage[], widthMm: number, heightMm: number): () => void {
    const style = document.createElement('style');
    style.textContent = `@page { size: ${widthMm}mm ${heightMm}mm; margin: 0; }`;
    const root = document.createElement('div');
    root.className = 'an-print-root';
    root.setAttribute('data-testid', 'score-print-view');
    for (const page of pages) {
      const sheet = document.createElement('div');
      sheet.className = 'an-print-page';
      sheet.style.width = `${widthMm}mm`;
      sheet.style.height = `${heightMm}mm`;
      // Verovio's own SVG (from the Server's MEI, its text escaped by Verovio): not Angular's sanitiser, which drops SVG.
      sheet.innerHTML = page.svg;
      root.append(sheet);
    }
    document.head.append(style);
    document.body.append(root);
    document.documentElement.classList.add('an-printing');
    return () => {
      document.documentElement.classList.remove('an-printing');
      root.remove();
      style.remove();
    };
  }
}
