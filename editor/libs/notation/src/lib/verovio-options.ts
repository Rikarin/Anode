import type { ScoreLayout } from '@anode/api-client';
import type { RenderOptions } from './renderer';

/**
 * The Verovio options the renderer uses (a subset of the `verovio` package's `VerovioOptions`, which are typed there;
 * only these are set). Verovio's page units are tenths of a millimetre at `scale` 100.
 */
export interface EngraveOptions {
  breaks: 'none' | 'auto';
  pageWidth: number;
  pageHeight: number;
  pageMarginTop: number;
  pageMarginBottom: number;
  pageMarginLeft: number;
  pageMarginRight: number;
  adjustPageWidth: boolean;
  adjustPageHeight: boolean;
  /** Half a staff space in Verovio units: the staff size. */
  unit: number;
  scale: number;
  header: 'none' | 'auto';
  footer: 'none' | 'encoded';
  svgViewBox: boolean;
  svgRemoveXlink: boolean;
  svgFormatRaw: boolean;
  /** Text (titles, names, lyrics, chord symbols) stays SVG `<text>`, so it is text in a printed PDF. */
  smuflTextFont: 'embedded';
  /** Generated ids (for elements without an `xml:id`) are the same on every render. */
  xmlIdSeed: number;
  /**
   * Empty staves are never hidden: the k-th staff of every measure in the SVG is the k-th staff of the view (counted
   * across the parts), which hit testing relies on.
   */
  condense: 'none';
}

/** Verovio units per millimetre (tenths of a millimetre). */
export const UnitsPerMm = 10;

/** CSS pixels per millimetre (96 per inch). */
export const PixelsPerMm = 96 / 25.4;

/** A long system's page: as tall as Verovio allows, cut to the music by `adjustPageHeight`. */
const LinearPageHeight = 60_000;
const LinearPageWidth = 100_000;

function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}

/** The MEI unit for a staff height: a staff is 8 units (4 spaces of 2 half-spaces), within Verovio's 4.5…12. */
export function unitForStaff(staffHeightMm: number): number {
  return clamp((staffHeightMm * UnitsPerMm) / 8, 4.5, 12);
}

/**
 * The Verovio options for a view of a layout. The linear view is one system as long as the music (no breaks, no
 * header); the page view breaks systems and pages within the layout's page and margins, with the title, subtitle and
 * composer (the MEI's `pgHead`, else its `titleStmt`) and the footer the MEI encodes. Zoom is not an engraving option:
 * the pages scale as CSS (see {@link pageSize}), so zooming never re-engraves.
 */
export function engraveOptions(options: Pick<RenderOptions, 'view' | 'layout'>): EngraveOptions {
  const layout: ScoreLayout = options.layout;
  const page = options.view === 'page';
  const margin = (mm: number) => clamp(Math.round(mm * UnitsPerMm), 0, 500);
  return {
    breaks: page ? 'auto' : 'none',
    pageWidth: page ? clamp(Math.round(layout.pageWidthMm * UnitsPerMm), 100, 60_000) : LinearPageWidth,
    pageHeight: page ? clamp(Math.round(layout.pageHeightMm * UnitsPerMm), 100, 60_000) : LinearPageHeight,
    pageMarginTop: page ? margin(layout.margins.top) : 50,
    pageMarginBottom: page ? margin(layout.margins.bottom) : 50,
    pageMarginLeft: page ? margin(layout.margins.left) : 50,
    pageMarginRight: page ? margin(layout.margins.right) : 50,
    adjustPageWidth: !page,
    adjustPageHeight: !page,
    unit: unitForStaff(layout.staffHeightMm),
    scale: 100,
    header: page ? 'auto' : 'none',
    footer: page ? 'encoded' : 'none',
    svgViewBox: true,
    svgRemoveXlink: true,
    svgFormatRaw: true,
    smuflTextFont: 'embedded',
    xmlIdSeed: 1,
    condense: 'none'
  };
}

/** A key that changes exactly when the engraving options do. */
export function optionsKey(options: EngraveOptions): string {
  return JSON.stringify(options);
}

/** A page's CSS size from its SVG `viewBox` (Verovio units) at a zoom in percent. */
export function pageSize(svg: string, zoom = 100): { width: number; height: number } {
  const match = /<svg\b[^>]*\bviewBox="\s*[-\d.]+\s+[-\d.]+\s+([\d.]+)\s+([\d.]+)\s*"/.exec(svg);
  const scale = (PixelsPerMm / UnitsPerMm) * (zoom / 100);
  const width = match?.[1] ? Number(match[1]) : 0;
  const height = match?.[2] ? Number(match[2]) : 0;
  return { width: Math.round(width * scale * 100) / 100, height: Math.round(height * scale * 100) / 100 };
}

/**
 * The MEI as Verovio 6.4 reads it: it ignores `pgHead` (titles) in a document that says `meiversion="5.1"`, so the
 * version is given as 5.0, whose page header encoding is the same (the rest of the document is unchanged).
 */
export function forVerovio(mei: string): string {
  return mei.replace(/(<mei\b[^>]*\bmeiversion=")5\.1(")/, '$15.0$2');
}
