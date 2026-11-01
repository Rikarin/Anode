import { chordSymbolIdOf, lyricIdOf, noteIdOf, symbolIdOf } from './ids';

/**
 * Reading Verovio's SVG back (ADR-0011, "Mapping back"): what an element is in the model, where note heads, staves and
 * events are. It reads attributes (Verovio writes absolute coordinates: note heads as `<use transform="translate(x,
 * y)">`, staff lines as `<path d="M x1 y L x2 y">`), so it needs no layout and works the same in tests. Coordinates are
 * the SVG's own (its `viewBox` units); {@link toSvgPoint} converts a pointer's.
 */

/** What a click on the score is on. */
export type ScoreHit =
  | { kind: 'note'; noteId: number; elementId: string }
  | { kind: 'rest'; elementId: string }
  | { kind: 'symbol'; symbolId: string; elementId: string }
  | { kind: 'lyric'; lyricId: string; elementId: string }
  | { kind: 'chordSymbol'; chordId: string; elementId: string }
  | { kind: 'measure'; measure: number; elementId: string };

/** Verovio's classes of the elements a score symbol becomes. */
const SymbolClasses = [
  'artic',
  'dynam',
  'slur',
  'hairpin',
  'dir',
  'tempo',
  'pedal',
  'trill',
  'fermata',
  'octave',
  'reh'
];

function classes(element: Element): string[] {
  return (element.getAttribute('class') ?? '').split(/\s+/);
}

/**
 * The model element under an SVG element: the nearest enclosing note, rest, symbol, syllable, chord symbol or measure
 * (up to `stop`). Ids are checked against their class and the model's prefixes (`ssym_`, `llyr_`, `hchd_`), since
 * Verovio gives elements without an `xml:id` generated ids that may start with the same letters.
 */
export function hitOf(element: Element | null, stop: Element | null = null): ScoreHit | null {
  for (let node = element; node && node !== stop; node = node.parentElement) {
    const id = node.getAttribute('id');
    if (!id) {
      continue;
    }
    const kinds = classes(node);
    if (kinds.includes('note')) {
      const noteId = noteIdOf(id);
      if (noteId !== null) {
        return { kind: 'note', noteId, elementId: id };
      }
    } else if (kinds.includes('rest') && id.startsWith('r')) {
      return { kind: 'rest', elementId: id };
    } else if (kinds.includes('syl') && id.startsWith('llyr_')) {
      return { kind: 'lyric', lyricId: lyricIdOf(id) ?? '', elementId: id };
    } else if (kinds.includes('harm') && id.startsWith('hchd_')) {
      return { kind: 'chordSymbol', chordId: chordSymbolIdOf(id) ?? '', elementId: id };
    } else if (kinds.some(k => SymbolClasses.includes(k)) && id.startsWith('ssym_')) {
      return { kind: 'symbol', symbolId: symbolIdOf(id) ?? '', elementId: id };
    } else if (kinds.includes('measure')) {
      const match = /^m([1-9][0-9]*)$/.exec(id);
      if (match?.[1]) {
        return { kind: 'measure', measure: Number(match[1]), elementId: id };
      }
    }
  }
  return null;
}

/** A point in SVG units. */
export interface Point {
  x: number;
  y: number;
}

/** An axis-aligned rectangle in SVG units. */
export interface Rect {
  x: number;
  y: number;
  width: number;
  height: number;
}

const Translate = /translate\(\s*(-?[\d.]+)[\s,]+(-?[\d.]+)\s*\)/;
const Line = /^M\s*(-?[\d.]+)[\s,]+(-?[\d.]+)\s*L\s*(-?[\d.]+)[\s,]+(-?[\d.]+)\s*$/;

/** Where a note head (or a rest's glyph) is: the first glyph's translation inside the element. */
export function glyphPoint(element: Element): Point | null {
  for (const use of Array.from(element.querySelectorAll('use'))) {
    const match = Translate.exec(use.getAttribute('transform') ?? '');
    if (match?.[1] && match[2]) {
      return { x: Number(match[1]), y: Number(match[2]) };
    }
  }
  return null;
}

/** Every note head's point, by note id (a tied note's first segment, `n{id}`). */
export function notePoints(svg: Element): Map<number, Point> {
  const points = new Map<number, Point>();
  for (const element of Array.from(svg.querySelectorAll('g.note[id]'))) {
    const id = element.getAttribute('id') ?? '';
    const noteId = noteIdOf(id);
    const point =
      noteId !== null && !id.includes('-') ? glyphPoint(element.querySelector('.notehead') ?? element) : null;
    if (noteId !== null && point) {
      points.set(noteId, point);
    }
  }
  return points;
}

/** The notes whose heads lie in a rectangle (the marquee). */
export function notesInRect(svg: Element, rect: Rect): number[] {
  const right = rect.x + rect.width;
  const bottom = rect.y + rect.height;
  return [...notePoints(svg)]
    .filter(([, p]) => p.x >= rect.x && p.x <= right && p.y >= rect.y && p.y <= bottom)
    .map(([id]) => id)
    .sort((a, b) => a - b);
}

/** One staff of one measure as drawn: its line heights (top to bottom) and horizontal extent. */
export interface StaffBox {
  measure: number;
  /** 0-based, top to bottom across every part's staves. */
  staff: number;
  lines: number[];
  left: number;
  right: number;
  element: Element;
}

/** The staves of every measure on a page, in order. */
export function staffBoxes(svg: Element): StaffBox[] {
  const boxes: StaffBox[] = [];
  for (const measure of Array.from(svg.querySelectorAll('g.measure[id]'))) {
    const number = /^m([1-9][0-9]*)$/.exec(measure.getAttribute('id') ?? '')?.[1];
    if (!number) {
      continue;
    }
    const staves = Array.from(measure.children).filter(c => classes(c).includes('staff'));
    staves.forEach((staff, index) => {
      const lines: number[] = [];
      let left = Number.POSITIVE_INFINITY;
      let right = Number.NEGATIVE_INFINITY;
      for (const path of Array.from(staff.children).filter(c => c.tagName.toLowerCase() === 'path')) {
        const match = Line.exec((path.getAttribute('d') ?? '').trim());
        if (match?.[1] && match[2] && match[3] && match[4] && match[2] === match[4]) {
          lines.push(Number(match[2]));
          left = Math.min(left, Number(match[1]), Number(match[3]));
          right = Math.max(right, Number(match[1]), Number(match[3]));
        }
      }
      if (lines.length > 0) {
        lines.sort((a, b) => a - b);
        boxes.push({ measure: Number(number), staff: index, lines, left, right, element: staff });
      }
    });
  }
  return boxes;
}

/** The staff box a point is in or nearest to (vertically, among those whose measure spans its x). */
export function staffAt(boxes: readonly StaffBox[], point: Point): StaffBox | null {
  let best: StaffBox | null = null;
  let bestDistance = Number.POSITIVE_INFINITY;
  for (const box of boxes) {
    if (point.x < box.left || point.x > box.right) {
      continue;
    }
    const top = box.lines[0] ?? 0;
    const bottom = box.lines.at(-1) ?? top;
    const distance = point.y < top ? top - point.y : point.y > bottom ? point.y - bottom : 0;
    if (distance < bestDistance) {
      best = box;
      bestDistance = distance;
    }
  }
  return best;
}

/**
 * The staff position at a height (diatonic steps above the bottom line, as MEI's `loc`: 0 the bottom line, 8 the top
 * of five lines; ledger positions continue outside), from the staff's line heights.
 */
export function staffPositionAt(y: number, lines: readonly number[]): number {
  const top = lines[0] ?? 0;
  const bottom = lines.at(-1) ?? top;
  const step = lines.length > 1 ? (bottom - top) / (lines.length - 1) / 2 : 1;
  return Math.round((bottom - y) / step);
}

/** Steps a vertical drag moves by: half a staff space each (up is positive). */
export function stepsOfDrag(dy: number, lines: readonly number[]): number {
  const top = lines[0] ?? 0;
  const bottom = lines.at(-1) ?? top;
  const step = lines.length > 1 ? (bottom - top) / (lines.length - 1) / 2 : 1;
  return Math.round(-dy / step);
}

/** An event's position on the page against its display tick: what horizontal positions are measured against. */
export interface Anchor {
  x: number;
  tick: number;
}

/**
 * The display tick at a horizontal position of a measure: between two drawn events it is interpolated, before the first
 * it is the first's (where the clef and signatures sit), after the last it runs to the bar line at `endX`, `endTick`.
 * Engraving spaces notes by duration only roughly, so the events themselves are the scale.
 */
export function tickAtX(
  x: number,
  anchors: readonly Anchor[],
  measure: { startTick: number; endTick: number; startX: number; endX: number }
): number {
  const sorted = [...anchors].sort((a, b) => a.x - b.x);
  const points: Anchor[] = sorted.length > 0 ? sorted : [{ x: measure.startX, tick: measure.startTick }];
  points.push({ x: measure.endX, tick: measure.endTick });
  const first = points[0] as Anchor;
  if (x <= first.x) {
    return first.tick;
  }
  for (let i = 1; i < points.length; i++) {
    const a = points[i - 1] as Anchor;
    const b = points[i] as Anchor;
    if (x <= b.x) {
      return b.x === a.x ? a.tick : a.tick + ((x - a.x) / (b.x - a.x)) * (b.tick - a.tick);
    }
  }
  return measure.endTick;
}

/** The drawn events of one staff of a measure: element id and glyph point. */
export function eventPoints(staff: Element): { elementId: string; point: Point }[] {
  const events: { elementId: string; point: Point }[] = [];
  for (const element of Array.from(staff.querySelectorAll('g.note[id], g.rest[id], g.chord[id]'))) {
    const point = glyphPoint(element);
    const elementId = element.getAttribute('id');
    if (point && elementId) {
      events.push({ elementId, point });
    }
  }
  return events;
}

/** The x of a measure's closing bar line (its staves' right edge). */
export function measureEndX(boxes: readonly StaffBox[], measure: number): number {
  return Math.max(...boxes.filter(b => b.measure === measure).map(b => b.right));
}

function viewBoxOf(element: Element | null): number[] {
  return (element?.getAttribute('viewBox') ?? '')
    .split(/[\s,]+/)
    .filter(Boolean)
    .map(Number);
}

/**
 * A pointer position in the page's drawing units: those of Verovio's `g.page-margin` (inside the page's
 * `svg.definition-scale`), where note heads and staff lines are. Uses the element's screen matrix when there is one;
 * else (no layout, as in tests) the page's box, the two view boxes and the margin's translation.
 */
export function toScorePoint(page: SVGSVGElement, clientX: number, clientY: number): Point {
  const margin = page.querySelector<SVGGraphicsElement>('g.page-margin');
  const matrix = margin && typeof margin.getScreenCTM === 'function' ? margin.getScreenCTM() : null;
  if (matrix && typeof page.createSVGPoint === 'function') {
    const point = page.createSVGPoint();
    point.x = clientX;
    point.y = clientY;
    const local = point.matrixTransform(matrix.inverse());
    return { x: local.x, y: local.y };
  }
  const rect = page.getBoundingClientRect();
  const inner = viewBoxOf(page.querySelector('svg.definition-scale'));
  const outer = viewBoxOf(page);
  const width = inner[2] ?? outer[2] ?? (rect.width || 1);
  const height = inner[3] ?? outer[3] ?? (rect.height || 1);
  const offset = Translate.exec(margin?.getAttribute('transform') ?? '');
  return {
    x: ((clientX - rect.left) / (rect.width || 1)) * width - Number(offset?.[1] ?? 0),
    y: ((clientY - rect.top) / (rect.height || 1)) * height - Number(offset?.[2] ?? 0)
  };
}

/**
 * A point in the page's drawing units as CSS pixels from the page's top left, for overlays (the marquee, the playhead,
 * the lyric field) at the page's displayed size.
 */
export function fromScorePoint(page: Element, point: Point, width: number, height: number): Point {
  const margin = page.querySelector('g.page-margin');
  const inner = viewBoxOf(page.querySelector('svg.definition-scale'));
  const outer = viewBoxOf(page);
  const units = { width: inner[2] ?? outer[2] ?? 1, height: inner[3] ?? outer[3] ?? 1 };
  const offset = Translate.exec(margin?.getAttribute('transform') ?? '');
  return {
    x: ((point.x + Number(offset?.[1] ?? 0)) / units.width) * width,
    y: ((point.y + Number(offset?.[2] ?? 0)) / units.height) * height
  };
}

/**
 * The note whose head is nearest a point, within `radius` drawing units (a click on a head that sits on a staff line
 * or a ledger line can land on the line).
 */
export function noteNear(svg: Element, point: Point, radius: number): number | null {
  let best: number | null = null;
  let distance = radius;
  for (const [noteId, head] of notePoints(svg)) {
    // A head's point is its left edge, on its middle: measure to its centre (about a staff space wide).
    const d = Math.hypot(head.x + radius / 2 - point.x, head.y - point.y);
    if (d <= distance) {
      best = noteId;
      distance = d;
    }
  }
  return best;
}

/** The distance between two staff lines of a staff box (a staff space), in drawing units. */
export function staffSpace(box: Pick<StaffBox, 'lines'>): number {
  const top = box.lines[0] ?? 0;
  const bottom = box.lines.at(-1) ?? top;
  return box.lines.length > 1 ? (bottom - top) / (box.lines.length - 1) : 180;
}
