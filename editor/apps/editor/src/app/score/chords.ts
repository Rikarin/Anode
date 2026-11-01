import type { ChordSymbol, PitchName, PitchStep } from '@anode/api-client';

/** A chord symbol as typed: its root, quality (the text after the root) and bass, as `ChordSymbol` stores them. */
export interface ParsedChord {
  root: PitchName;
  quality: string;
  bass: PitchName | null;
}

const Note = /^([A-Ga-g])(##|bb|#|b|♯♯|♭♭|♯|♭|x)?/;

/** The qualities the chord entry accepts (the text after the root, as Logic and MusicXML's `kind` read it). */
const Qualities = new Set([
  '',
  'm',
  'min',
  '-',
  'maj',
  'M',
  '7',
  'maj7',
  'M7',
  'Δ',
  'Δ7',
  'm7',
  'min7',
  '-7',
  'mMaj7',
  'm(maj7)',
  'dim',
  '°',
  'dim7',
  '°7',
  'm7b5',
  'ø',
  'ø7',
  'aug',
  '+',
  'aug7',
  '+7',
  '7#5',
  'sus',
  'sus2',
  'sus4',
  '7sus4',
  '6',
  'm6',
  '69',
  '6/9',
  '9',
  'maj9',
  'm9',
  '11',
  'm11',
  '13',
  'maj13',
  'm13',
  '7b9',
  '7#9',
  '7#11',
  '7b5',
  'add9',
  'madd9',
  '5'
]);

function alterOf(accidental: string | undefined): number {
  switch (accidental) {
    case '#':
    case '♯':
      return 1;
    case '##':
    case '♯♯':
    case 'x':
      return 2;
    case 'b':
    case '♭':
      return -1;
    case 'bb':
    case '♭♭':
      return -2;
    default:
      return 0;
  }
}

function readNote(text: string): { name: PitchName; rest: string } | null {
  const match = Note.exec(text);
  if (!match?.[1]) {
    return null;
  }
  return {
    name: { step: match[1].toLowerCase() as PitchStep, alter: alterOf(match[2]) },
    rest: text.slice(match[0].length)
  };
}

/**
 * Reads a chord symbol as typed in the Score Editor ("Am7", "Fmaj7/C", "Bb7", "F#m7b5", "C/E"): the root, the quality
 * (normalised: "min" → "m", "M7" and "Δ" → "maj7", "°" → "dim", "ø" → "m7b5", "+" → "aug"), and a bass after a
 * slash. Null when it is not a chord symbol.
 */
export function parseChord(input: string): ParsedChord | null {
  const text = input.trim().replace(/\s+/g, '');
  const root = readNote(text);
  if (!root) {
    return null;
  }
  let quality = root.rest;
  let bass: PitchName | null = null;
  const slash = quality.lastIndexOf('/');
  // "6/9" is a quality, not a bass.
  if (slash >= 0 && !/^6\/9$/.test(quality)) {
    const after = readNote(quality.slice(slash + 1));
    if (after?.rest !== '') {
      return null;
    }
    bass = after.name;
    quality = quality.slice(0, slash);
  }
  if (!Qualities.has(quality)) {
    return null;
  }
  return { root: root.name, quality: normaliseQuality(quality), bass };
}

function normaliseQuality(quality: string): string {
  switch (quality) {
    case 'min':
    case '-':
      return 'm';
    case 'M':
    case 'maj':
      return 'maj';
    case 'M7':
    case 'Δ':
    case 'Δ7':
      return 'maj7';
    case 'min7':
    case '-7':
      return 'm7';
    case '°':
      return 'dim';
    case '°7':
      return 'dim7';
    case 'ø':
    case 'ø7':
      return 'm7b5';
    case '+':
      return 'aug';
    case '+7':
      return 'aug7';
    case 'm(maj7)':
      return 'mMaj7';
    case 'sus':
      return 'sus4';
    case '6/9':
      return '69';
    default:
      return quality;
  }
}

function nameText(name: PitchName): string {
  return `${name.step.toUpperCase()}${name.alter > 0 ? '♯'.repeat(name.alter) : '♭'.repeat(-name.alter)}`;
}

/** A chord symbol as the Score Editor writes it back ("F♯m7", "Fmaj7/C"). */
export function chordText(chord: Pick<ChordSymbol, 'root' | 'quality' | 'bass'>): string {
  return `${nameText(chord.root)}${chord.quality}${chord.bass ? `/${nameText(chord.bass)}` : ''}`;
}
