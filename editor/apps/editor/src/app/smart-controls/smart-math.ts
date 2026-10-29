import type {
  GraphPoint,
  SmartControl,
  SmartControlKind,
  SmartControls,
  SmartControlsLayout,
  SmartMapping
} from '@anode/api-client';

/** The default mapping graph: the line (0, 0)–(1, 1). */
export const LinearGraph: readonly GraphPoint[] = [
  { x: 0, y: 0 },
  { x: 1, y: 1 }
];
export const MinGraphPoints = 2;
export const MaxGraphPoints = 16;

/** The knobs of a layout. */
export function knobCount(layout: SmartControlsLayout): number {
  return layout === 'knobs12' ? 12 : 8;
}

/** Every control id of a layout, in order: the knobs, `switch1`, `switch2`, `padX`, `padY`. */
export function controlIds(layout: SmartControlsLayout): string[] {
  return [...Array.from({ length: knobCount(layout) }, (_, i) => `knob${i + 1}`), 'switch1', 'switch2', 'padX', 'padY'];
}

/** A control's kind from its id. */
export function controlKind(id: string): SmartControlKind {
  return id.startsWith('switch') ? 'switch' : id.startsWith('pad') ? 'pad' : 'knob';
}

/** A control as a new layout starts it: unnamed (its id's display name), at 0, unmapped. */
export function emptyControl(id: string): SmartControl {
  return { id, name: controlLabel(id), kind: controlKind(id), value: 0, savedValue: null, mappings: [] };
}

/** "Knob 3", "Switch 1", "Pad X". */
export function controlLabel(id: string): string {
  const knob = /^knob(\d+)$/.exec(id);
  if (knob) {
    return `Knob ${knob[1]}`;
  }
  const sw = /^switch(\d+)$/.exec(id);
  if (sw) {
    return `Switch ${sw[1]}`;
  }
  return id === 'padX' ? 'Pad X' : id === 'padY' ? 'Pad Y' : id;
}

/**
 * A track's Smart Controls as the editor shows them: its own, or, while they are null (not set up yet), the 8-knob
 * layout, unnamed, at 0 and unmapped (docs/07-phase-6.md, "Contract notes").
 */
export function smartControlsOf(controls: SmartControls | null | undefined): SmartControls {
  return (
    controls ?? {
      layout: 'knobs8',
      controls: controlIds('knobs8').map(emptyControl),
      arpeggiator: null,
      comparing: false
    }
  );
}

/** The graph at `x` (0…1): piecewise linear through its points (sorted by x), flat beyond its ends. */
export function graphValue(graph: readonly GraphPoint[], x: number): number {
  const points = graph.length >= 2 ? graph : LinearGraph;
  const t = Math.min(1, Math.max(0, x));
  const first = points[0];
  const last = points[points.length - 1];
  if (!first || !last) {
    return t;
  }
  if (t <= first.x) {
    return first.y;
  }
  for (let i = 1; i < points.length; i++) {
    const a = points[i - 1];
    const b = points[i];
    if (a && b && t <= b.x) {
      return b.x === a.x ? b.y : a.y + ((t - a.x) / (b.x - a.x)) * (b.y - a.y);
    }
  }
  return last.y;
}

/** A control value through a mapping: the graph, inverted (1 − y) when `invert`, then into `min … max`. */
export function mappedValue(mapping: Pick<SmartMapping, 'min' | 'max' | 'invert' | 'graph'>, value: number): number {
  const y = graphValue(mapping.graph, value);
  const shaped = mapping.invert ? 1 - y : y;
  return mapping.min + shaped * (mapping.max - mapping.min);
}

/**
 * The control value that gives a parameter value through a mapping (what automatic mapping sets a knob to), for the
 * default graph: the parameter's place in `min … max`, inverted when the mapping is. Other graphs are searched (the
 * first x whose output is nearest).
 */
export function controlValueFor(
  mapping: Pick<SmartMapping, 'min' | 'max' | 'invert' | 'graph'>,
  parameter: number
): number {
  const span = mapping.max - mapping.min;
  if (span === 0) {
    return 0;
  }
  let best = 0;
  let error = Infinity;
  for (let i = 0; i <= 1000; i++) {
    const x = i / 1000;
    const e = Math.abs(mappedValue(mapping, x) - parameter);
    if (e < error - 1e-12) {
      error = e;
      best = x;
    }
  }
  return best;
}

/** A mapping graph checked as the Server checks it: 2–16 points in 0…1, sorted by x, from x 0 to x 1. */
export function graphProblem(graph: readonly GraphPoint[]): string | null {
  if (graph.length < MinGraphPoints || graph.length > MaxGraphPoints) {
    return `A mapping graph has ${MinGraphPoints} to ${MaxGraphPoints} points.`;
  }
  if (graph.some(p => !(p.x >= 0 && p.x <= 1 && p.y >= 0 && p.y <= 1))) {
    return 'Graph points lie in 0…1.';
  }
  if (graph.some((p, i) => i > 0 && p.x < (graph[i - 1]?.x ?? 0))) {
    return 'Graph points are sorted by x.';
  }
  if (graph[0]?.x !== 0 || graph[graph.length - 1]?.x !== 1) {
    return 'A graph runs from x 0 to x 1.';
  }
  return null;
}

/** A graph with a point added at x (on the curve), kept sorted; unchanged at the limit. */
export function addGraphPoint(graph: readonly GraphPoint[], x: number, y = graphValue(graph, x)): GraphPoint[] {
  if (graph.length >= MaxGraphPoints || x <= 0 || x >= 1) {
    return [...graph];
  }
  return [...graph, { x, y }].sort((a, b) => a.x - b.x);
}

/** A graph without point `index` (never its ends, nor below two points). */
export function removeGraphPoint(graph: readonly GraphPoint[], index: number): GraphPoint[] {
  if (graph.length <= MinGraphPoints || index <= 0 || index >= graph.length - 1) {
    return [...graph];
  }
  return graph.filter((_, i) => i !== index);
}

/**
 * A graph with point `index` moved: y anywhere in 0…1; x between its neighbours (the ends stay at x 0 and x 1).
 */
export function moveGraphPoint(graph: readonly GraphPoint[], index: number, x: number, y: number): GraphPoint[] {
  return graph.map((p, i) => {
    if (i !== index) {
      return p;
    }
    const clampedY = Math.min(1, Math.max(0, y));
    if (i === 0 || i === graph.length - 1) {
      return { x: p.x, y: clampedY };
    }
    const lo = graph[i - 1]?.x ?? 0;
    const hi = graph[i + 1]?.x ?? 1;
    return { x: Math.min(hi, Math.max(lo, x)), y: clampedY };
  });
}

/** An automation target's lane address for a control: `smartControl:{controlId}`. */
export function smartControlTarget(controlId: string): string {
  return `smartControl:${controlId}`;
}
