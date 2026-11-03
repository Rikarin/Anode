import { ChordQualities, pitchClass, type PitchName, type ScaleKind } from '@anode/api-client';

/** A chord as the chord track holds it (`Chord`, `NewChord`): a spelled root, a quality, a spelled slash bass. */
export interface ChordSpelling {
  root: PitchName;
  quality: string;
  bass: PitchName | null;
}

const Accidentals: Readonly<Record<number, string>> = { [-2]: '𝄫', [-1]: '♭', 0: '', 1: '♯', 2: '𝄪' };

/** A spelled note's name: C, F♯, B♭. */
export function pitchNameText(pitch: PitchName): string {
  return pitch.step.toUpperCase() + (Accidentals[pitch.alter] ?? '');
}

/** How each quality is written after the root (the contract's names, with ♭ for `b`). */
const QualityText: Readonly<Record<string, string>> = { m7b5: 'm7♭5' };

/** A quality as written ("", "m", "maj7", "m7♭5"). */
export function qualityText(quality: string): string {
  return QualityText[quality] ?? quality;
}

/** A quality's name for menus. */
const QualityNames: Readonly<Record<string, string>> = {
  '': 'Major',
  m: 'Minor',
  '7': 'Dominant 7th',
  maj7: 'Major 7th',
  m7: 'Minor 7th',
  dim: 'Diminished',
  m7b5: 'Half-diminished',
  aug: 'Augmented',
  sus2: 'Suspended 2nd',
  sus4: 'Suspended 4th',
  '6': 'Major 6th',
  m6: 'Minor 6th',
  '9': 'Dominant 9th'
};

/** The qualities in the contract's order, each with its symbol and name (the chord editor's menu). */
export const ChordQualityOptions: readonly { quality: string; symbol: string; name: string }[] = ChordQualities.map(
  ({ quality }) => ({ quality, symbol: qualityText(quality), name: QualityNames[quality] ?? quality })
);

/** A chord's symbol: "Am7", "G/B", "F♯m7♭5". */
export function chordSymbol(chord: ChordSpelling): string {
  return pitchNameText(chord.root) + qualityText(chord.quality) + (chord.bass ? `/${pitchNameText(chord.bass)}` : '');
}

/** A chord's name for assistive technology: "A minor 7th", "G major over B". */
export function chordLabel(chord: ChordSpelling): string {
  const name = QualityNames[chord.quality] ?? chord.quality;
  return `${pitchNameText(chord.root)} ${name.toLowerCase()}${chord.bass ? ` over ${pitchNameText(chord.bass)}` : ''}`;
}

/** Quality spellings a typed symbol may use, longest first, each to the contract's name. */
const QualityAliases: readonly (readonly [string, string])[] = [
  ['maj7', 'maj7'],
  ['ma7', 'maj7'],
  ['M7', 'maj7'],
  ['Δ7', 'maj7'],
  ['Δ', 'maj7'],
  ['m7b5', 'm7b5'],
  ['m7♭5', 'm7b5'],
  ['ø7', 'm7b5'],
  ['ø', 'm7b5'],
  ['min7', 'm7'],
  ['-7', 'm7'],
  ['m7', 'm7'],
  ['min6', 'm6'],
  ['m6', 'm6'],
  ['sus2', 'sus2'],
  ['sus4', 'sus4'],
  ['sus', 'sus4'],
  ['dim', 'dim'],
  ['°', 'dim'],
  ['aug', 'aug'],
  ['+', 'aug'],
  ['min', 'm'],
  ['maj', ''],
  ['m', 'm'],
  ['-', 'm'],
  ['7', '7'],
  ['6', '6'],
  ['9', '9'],
  ['', '']
];

function parsePitch(step: string, accidentals: string): PitchName | null {
  const alter = (accidentals.match(/[#♯]/g)?.length ?? 0) - (accidentals.match(/[b♭]/g)?.length ?? 0);
  return alter >= -2 && alter <= 2 ? { step: step.toLowerCase() as PitchName['step'], alter } : null;
}

/** A typed chord symbol ("Am7", "F#m7b5", "G/B", "Bbmaj7") as a chord, or null when it is not one. */
export function parseChordSymbol(text: string): ChordSpelling | null {
  const match = /^\s*([A-Ga-g])([#♯b♭]{0,2})(.*?)(?:\s*\/\s*([A-Ga-g])([#♯b♭]{0,2}))?\s*$/.exec(text);
  if (!match) {
    return null;
  }
  const [, step, accidentals, rest, bassStep, bassAccidentals] = match;
  const root = parsePitch(step ?? 'c', accidentals ?? '');
  const quality = QualityAliases.find(([alias]) => alias === (rest ?? '').trim())?.[1];
  const bass = bassStep ? parsePitch(bassStep, bassAccidentals ?? '') : null;
  if (!root || quality === undefined || (bassStep && !bass)) {
    return null;
  }
  return { root, quality, bass: bass && pitchClass(bass) !== pitchClass(root) ? bass : null };
}

/** The spellings the chord editor offers for a root or bass, by pitch class: naturals, then sharps and flats. */
export const PitchSpellings: readonly PitchName[] = [
  { step: 'c', alter: 0 },
  { step: 'c', alter: 1 },
  { step: 'd', alter: -1 },
  { step: 'd', alter: 0 },
  { step: 'd', alter: 1 },
  { step: 'e', alter: -1 },
  { step: 'e', alter: 0 },
  { step: 'f', alter: 0 },
  { step: 'f', alter: 1 },
  { step: 'g', alter: -1 },
  { step: 'g', alter: 0 },
  { step: 'g', alter: 1 },
  { step: 'a', alter: -1 },
  { step: 'a', alter: 0 },
  { step: 'a', alter: 1 },
  { step: 'b', alter: -1 },
  { step: 'b', alter: 0 }
];

/** A spelling as a form value ("f#", "bb") and back. */
export function pitchKey(pitch: PitchName): string {
  return pitch.step + (pitch.alter > 0 ? '#'.repeat(pitch.alter) : 'b'.repeat(-pitch.alter));
}

export function pitchFromKey(key: string): PitchName | null {
  const match = /^([a-g])(#{0,2}|b{0,2})$/.exec(key);
  return match ? parsePitch(match[1] ?? 'c', match[2] ?? '') : null;
}

const ScaleNames: Readonly<Record<ScaleKind, string>> = {
  major: 'major',
  minor: 'minor',
  harmonicMinor: 'harmonic minor',
  melodicMinor: 'melodic minor',
  dorian: 'Dorian',
  phrygian: 'Phrygian',
  lydian: 'Lydian',
  mixolydian: 'Mixolydian',
  locrian: 'Locrian'
};

/** The scales in the contract's order with their names. */
export const ScaleOptions: readonly { scale: ScaleKind; name: string }[] = (Object.keys(ScaleNames) as ScaleKind[]).map(
  scale => ({ scale, name: ScaleNames[scale] })
);

/** A key and scale change's name: "C major", "D Dorian". */
export function scaleText(change: { tonic: PitchName; scale: ScaleKind }): string {
  return `${pitchNameText(change.tonic)} ${ScaleNames[change.scale]}`;
}

/** A chord on the lane: where it starts and ends (each holds to the next; the last for `lastTicks`). */
export interface ChordSpan<T> {
  chord: T;
  startTick: number;
  endTick: number;
}

/** Chords as spans in tick order: each to the next chord's tick, the last `lastTicks` long. */
export function chordSpans<T extends { tick: number }>(chords: readonly T[], lastTicks: number): ChordSpan<T>[] {
  const sorted = [...chords].sort((a, b) => a.tick - b.tick);
  return sorted.map((chord, i) => ({
    chord,
    startTick: chord.tick,
    endTick: sorted[i + 1]?.tick ?? chord.tick + lastTicks
  }));
}

/** The chord sounding at a tick (the last at or before it), if any. */
export function chordAt<T extends { tick: number }>(chords: readonly T[], tick: number): T | null {
  let found: T | null = null;
  for (const chord of chords) {
    if (chord.tick <= tick && (!found || chord.tick >= found.tick)) {
      found = chord;
    }
  }
  return found;
}

/** A chord's block on the lane, in CSS pixels: drawn from its start to the next chord, at least `minWidth` wide. */
export function chordBlock(
  span: ChordSpan<unknown>,
  x: (tick: number) => number,
  minWidth = 6
): { x: number; width: number } {
  const left = x(span.startTick);
  return { x: left, width: Math.max(minWidth, x(span.endTick) - left - 1) };
}
