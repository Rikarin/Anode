/** The touch keyboard's scales (Logic Remote's Scale menu): the semitones of one octave from the root. */
export const Scales = {
  chromatic: { name: 'Chromatic', steps: [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11] },
  major: { name: 'Major', steps: [0, 2, 4, 5, 7, 9, 11] },
  minor: { name: 'Minor', steps: [0, 2, 3, 5, 7, 8, 10] },
  harmonicMinor: { name: 'Harmonic Minor', steps: [0, 2, 3, 5, 7, 8, 11] },
  dorian: { name: 'Dorian', steps: [0, 2, 3, 5, 7, 9, 10] },
  mixolydian: { name: 'Mixolydian', steps: [0, 2, 4, 5, 7, 9, 10] },
  majorPentatonic: { name: 'Major Pentatonic', steps: [0, 2, 4, 7, 9] },
  minorPentatonic: { name: 'Minor Pentatonic', steps: [0, 3, 5, 7, 10] },
  blues: { name: 'Blues', steps: [0, 3, 5, 6, 7, 10] }
} as const satisfies Record<string, { name: string; steps: readonly number[] }>;

export type ScaleId = keyof typeof Scales;

export const ScaleIds = Object.keys(Scales) as ScaleId[];

/** The pitch-class names roots are chosen by. */
export const RootNames = ['C', 'C♯', 'D', 'D♯', 'E', 'F', 'F♯', 'G', 'G♯', 'A', 'A♯', 'B'] as const;

/**
 * The notes a scale keyboard plays: `octaves` octaves of `scale` from `root` (a pitch class) in the octave starting at
 * `base` (a C: 48 is C2), plus the root at the top; within 0…127.
 */
export function scaleNotes(scale: ScaleId, root: number, base: number, octaves: number): number[] {
  const steps = Scales[scale].steps;
  const start = base + (((root % 12) + 12) % 12);
  const notes: number[] = [];
  for (let o = 0; o < octaves; o++) {
    for (const step of steps) {
      notes.push(start + o * 12 + step);
    }
  }
  notes.push(start + octaves * 12);
  return notes.filter(n => n >= 0 && n <= 127);
}

/**
 * Velocity from where a key or pad was struck, top to bottom (0…1): its top plays softly (40), its bottom fully (127),
 * as the on-screen keyboard's.
 */
export function touchVelocity(fraction: number): number {
  return Math.round(40 + Math.min(1, Math.max(0, Number.isFinite(fraction) ? fraction : 0.75)) * 87);
}

/** A drum pad as the pad grid shows it. */
export interface DrumPadInfo {
  note: number;
  name: string;
  color?: string | null;
}

/** The General MIDI percussion names of notes 35…81. */
const GmNames: Readonly<Record<number, string>> = {
  35: 'Acoustic Kick',
  36: 'Kick',
  37: 'Side Stick',
  38: 'Snare',
  39: 'Clap',
  40: 'Electric Snare',
  41: 'Low Floor Tom',
  42: 'Closed Hi-Hat',
  43: 'High Floor Tom',
  44: 'Pedal Hi-Hat',
  45: 'Low Tom',
  46: 'Open Hi-Hat',
  47: 'Low-Mid Tom',
  48: 'Hi-Mid Tom',
  49: 'Crash',
  50: 'High Tom',
  51: 'Ride'
};

/** The 16 GM drum pads, notes 36 (C1, the kick) to 51 (the ride), in note order. */
export function gmDrumPads(): DrumPadInfo[] {
  return Array.from({ length: 16 }, (_, i) => ({ note: 36 + i, name: GmNames[36 + i] ?? `Note ${36 + i}` }));
}

/**
 * Pads as rows of `columns`, top row first, lowest note at the bottom left (a pad controller's and Logic's layout);
 * a short last row is padded with nulls.
 */
export function padRows<T extends { note: number }>(pads: readonly T[], columns = 4): (T | null)[][] {
  const sorted = [...pads].sort((a, b) => a.note - b.note);
  const rows: (T | null)[][] = [];
  for (let i = 0; i < sorted.length; i += columns) {
    const row: (T | null)[] = sorted.slice(i, i + columns);
    while (row.length < columns) {
      row.push(null);
    }
    rows.unshift(row);
  }
  return rows;
}
