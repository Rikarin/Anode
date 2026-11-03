/** A point on a Session Player's XY pad (`SessionPlayerPad`): complexity along x, intensity up y, each 0…1. */
export interface SessionPadValue {
  complexity: number;
  intensity: number;
}

function clamp01(value: number): number {
  return Math.min(1, Math.max(0, value));
}

/** A value kept to 0…1 in hundredths (what the pad sends: two numbers, as `setPanner` does). */
export function padRound(value: SessionPadValue): SessionPadValue {
  return {
    complexity: Math.round(clamp01(value.complexity) * 100) / 100,
    intensity: Math.round(clamp01(value.intensity) * 100) / 100
  };
}

/** The value under a pointer on the pad's box (y up): Simple at the left, Loud at the top. */
export function padValueAt(
  clientX: number,
  clientY: number,
  box: { left: number; top: number; width: number; height: number }
): SessionPadValue {
  if (box.width <= 0 || box.height <= 0) {
    return { complexity: 0.5, intensity: 0.5 };
  }
  return padRound({ complexity: (clientX - box.left) / box.width, intensity: 1 - (clientY - box.top) / box.height });
}

/** Where a value sits on the pad, as percentages from the left and from the bottom. */
export function padPosition(value: SessionPadValue): { left: number; bottom: number } {
  return { left: clamp01(value.complexity) * 100, bottom: clamp01(value.intensity) * 100 };
}

/**
 * The value after a key: ←/→ change complexity and ↓/↑ intensity by 5 % (⇧: 1 %), Home goes to the simplest and
 * softest corner, End to the busiest and loudest; null for any other key.
 */
export function padKey(value: SessionPadValue, key: string, fine: boolean): SessionPadValue | null {
  const step = fine ? 0.01 : 0.05;
  const { complexity, intensity } = value;
  const next: Record<string, SessionPadValue> = {
    ArrowLeft: { complexity: complexity - step, intensity },
    ArrowRight: { complexity: complexity + step, intensity },
    ArrowDown: { complexity, intensity: intensity - step },
    ArrowUp: { complexity, intensity: intensity + step },
    Home: { complexity: 0, intensity: 0 },
    End: { complexity: 1, intensity: 1 }
  };
  const moved = next[key];
  return moved ? padRound(moved) : null;
}

/** The pad's region in words, for its value text: simple, moderate or complex; soft, medium or loud. */
export function padZone(value: SessionPadValue): string {
  const third = (v: number, words: readonly [string, string, string]) => words[v < 1 / 3 ? 0 : v < 2 / 3 ? 1 : 2];
  return `${third(value.complexity, ['simple', 'moderate', 'complex'])}, ${third(value.intensity, ['soft', 'medium', 'loud'])}`;
}

/** The value read out: "Complexity 45 %, intensity 60 % (moderate, medium)". */
export function padText(value: SessionPadValue): string {
  const percent = (v: number) => Math.round(clamp01(v) * 100);
  return `Complexity ${percent(value.complexity)} %, intensity ${percent(value.intensity)} % (${padZone(value)})`;
}
