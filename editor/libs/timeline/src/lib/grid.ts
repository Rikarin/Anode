/** A grid line's weight: a bar line, a beat line, or a division (sixteenth or eighth) line. */
export const GridLevel = { Bar: 0, Beat: 1, Division: 2 } as const;
export type GridLevel = (typeof GridLevel)[keyof typeof GridLevel];

export interface GridLine {
  readonly tick: number;
  readonly level: GridLevel;
}

/**
 * The finest grid step (in ticks) whose lines are at least `minSpacingPx` apart at this zoom: a sixteenth, an eighth,
 * a beat, a bar, then 2, 4, 8… bars as the view zooms out.
 */
export function gridStep(ppq: number, beatsPerBar: number, pixelsPerTick: number, minSpacingPx: number): number {
  for (const step of [ppq / 4, ppq / 2, ppq]) {
    if (step * pixelsPerTick >= minSpacingPx) {
      return step;
    }
  }
  return ppq * beatsPerBar * barMultiple(ppq * beatsPerBar * pixelsPerTick, minSpacingPx);
}

/** The smallest power-of-two count of bars spanning at least `minSpacingPx` (how often to number bars in the ruler). */
export function barMultiple(barPx: number, minSpacingPx: number): number {
  let bars = 1;
  while (bars * barPx < minSpacingPx && bars < 1 << 20) {
    bars *= 2;
  }
  return bars;
}

/** The grid lines every `step` ticks across `[fromTick, toTick]`, each with its weight. */
export function gridLines(
  fromTick: number,
  toTick: number,
  step: number,
  ppq: number,
  beatsPerBar: number
): GridLine[] {
  const bar = ppq * beatsPerBar;
  const lines: GridLine[] = [];
  for (let tick = Math.ceil(Math.max(0, fromTick) / step) * step; tick <= toTick; tick += step) {
    const level = tick % bar === 0 ? GridLevel.Bar : tick % ppq === 0 ? GridLevel.Beat : GridLevel.Division;
    lines.push({ tick, level });
  }
  return lines;
}
