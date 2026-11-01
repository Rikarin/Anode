import type {
  Clef,
  KeyEvent,
  KeyMode,
  Accidental as NullableAccidental,
  PitchStep,
  RegionNotation,
  ScorePitch,
  StaffStyle
} from '@anode/api-client';

/** An accidental (the generated type allows null, for the optional properties that use it). */
export type Accidental = NonNullable<NullableAccidental>;

/**
 * Staff positions, spelling and display grids as the notation engine defines them (docs/10-phase-9.md, "Score edits";
 * `Anode.Notation.StaffPositions` and `DisplayGrid`), for the editor's previews and conversions and the fake Server's
 * notation stand-in. Pitches are concert MIDI pitches; written = concert + transpose; staff positions are diatonic
 * steps above the bottom line (0 the bottom line, 8 the top, as MEI's `loc`); octaves are MEI's (C4 is 60).
 */

export const Steps: readonly PitchStep[] = ['c', 'd', 'e', 'f', 'g', 'a', 'b'];
const StepPitchClass = [0, 2, 4, 5, 7, 9, 11];
const SharpOrder: readonly PitchStep[] = ['f', 'c', 'g', 'd', 'a', 'e', 'b'];

/** The bottom line's note (diatonic number: octave × 7 + step) of each clef, as sounding (an octave clef's included). */
const BottomLine: Record<Clef, number> = {
  treble: 4 * 7 + 2,
  bass: 2 * 7 + 4,
  alto: 3 * 7 + 3,
  tenor: 3 * 7 + 1,
  treble8vb: 3 * 7 + 2,
  treble8va: 5 * 7 + 2,
  bass8vb: 1 * 7 + 4,
  percussion: 4 * 7 + 2,
  none: 4 * 7 + 2
};

/** The octave an octave clef writes its notes above (8vb: written an octave higher than they sound). */
export function clefOctave(clef: Clef): number {
  return clef === 'treble8vb' || clef === 'bass8vb' ? 1 : clef === 'treble8va' ? -1 : 0;
}

/** Key signature fifths of a key (sharps positive, flats negative; F♯/G♭ major as 6 sharps). */
export function fifthsOfKey(key: Pick<KeyEvent, 'tonic' | 'mode'> | null | undefined): number {
  if (!key) {
    return 0;
  }
  const major = (((key.tonic + (key.mode === 'minor' ? 3 : 0)) % 12) + 12) % 12;
  const fifths = (major * 7) % 12;
  return fifths > 6 ? fifths - 12 : fifths;
}

/** The written key's fifths for a transposing staff (written = concert + transpose). */
export function writtenFifths(fifths: number, transpose: number): number {
  let written = (fifths + ((((transpose * 7) % 12) + 12) % 12)) % 12;
  if (written > 6) {
    written -= 12;
  }
  if (written < -6) {
    written += 12;
  }
  return written;
}

/** How a key signature alters a step: +1 sharpened, −1 flattened, 0. */
export function keyAlter(fifths: number, step: PitchStep): number {
  if (fifths > 0) {
    return SharpOrder.slice(0, fifths).includes(step) ? 1 : 0;
  }
  if (fifths < 0) {
    return [...SharpOrder].reverse().slice(0, -fifths).includes(step) ? -1 : 0;
  }
  return 0;
}

/**
 * A pitch spelled in a key, as the notation engine spells it (`Anode.Notation`'s `Spelling.Spell`): the key's own notes;
 * in minor its raised sixth and seventh; naturals; then sharps in sharp keys and flats in flat keys (C major and A minor:
 * C♯ E♭ F♯ G♯ B♭).
 */
export function spell(pitch: number, fifths: number, mode: 'major' | 'minor' = 'major'): ScorePitch {
  const pc = mod12(pitch);
  for (let s = 0; s < 7; s++) {
    const step = Steps[s] as PitchStep;
    const alter = keyAlter(fifths, step);
    if (mod12((StepPitchClass[s] as number) + alter) === pc) {
      return withOctave(pitch, step, alter);
    }
  }
  if (mode === 'minor') {
    // The tonic is three fifths above the relative major's; its raised seventh and sixth are a semitone and three below.
    const index = fifths + 4;
    const tonicStep = Steps.indexOf(SharpOrder[((index % 7) + 7) % 7] as PitchStep);
    const tonicClass = (StepPitchClass[tonicStep] as number) + Math.floor(index / 7);
    for (const [degreeBelow, semitones] of [
      [1, 1],
      [2, 3]
    ] as const) {
      if (mod12(tonicClass - semitones) === pc) {
        const step = (tonicStep - degreeBelow + 7) % 7;
        let alter = mod12(pc - (StepPitchClass[step] as number));
        if (alter > 6) {
          alter -= 12;
        }
        return withOctave(pitch, Steps[step] as PitchStep, alter);
      }
    }
  }
  const natural = StepPitchClass.indexOf(pc);
  if (natural >= 0) {
    return withOctave(pitch, Steps[natural] as PitchStep, 0);
  }
  const sharp = fifths > 0 || (fifths === 0 && (pc === 1 || pc === 6 || pc === 8));
  return sharp
    ? withOctave(pitch, Steps[StepPitchClass.indexOf(pc - 1)] as PitchStep, 1)
    : withOctave(pitch, Steps[StepPitchClass.indexOf((pc + 1) % 12)] as PitchStep, -1);
}

function mod12(n: number): number {
  return ((n % 12) + 12) % 12;
}

function withOctave(pitch: number, step: PitchStep, alter: number): ScorePitch {
  const natural = pitch - alter;
  return { step, alter, octave: Math.floor(natural / 12) - 1 };
}

/** A written pitch's MIDI number. */
export function midiOf(written: ScorePitch): number {
  return (written.octave + 1) * 12 + (StepPitchClass[Steps.indexOf(written.step)] ?? 0) + written.alter;
}

function diatonic(written: ScorePitch): number {
  return written.octave * 7 + Steps.indexOf(written.step);
}

const AccidentalAlter: Record<Accidental, number> = {
  natural: 0,
  sharp: 1,
  flat: -1,
  doubleSharp: 2,
  doubleFlat: -2
};

/** The accidental an alteration is written with. */
export function accidentalOf(alter: number): Accidental {
  return alter >= 2
    ? 'doubleSharp'
    : alter === 1
      ? 'sharp'
      : alter <= -2
        ? 'doubleFlat'
        : alter === -1
          ? 'flat'
          : 'natural';
}

/** Where a pitch is written on a staff: its position and spelling (in the written key). */
export function placementOf(
  clef: Clef,
  transpose: number,
  fifths: number,
  pitch: number,
  mode: 'major' | 'minor' = 'major'
): { staffPosition: number; written: ScorePitch } {
  const written = spell(pitch + transpose, writtenFifths(fifths, transpose), mode);
  return {
    staffPosition: diatonic(written) - BottomLine[clef],
    written: { ...written, octave: written.octave + clefOctave(clef) }
  };
}

/**
 * The concert pitch at a staff position: the written key's note there (an accidental forcing its alteration), less the
 * transposition.
 */
export function pitchAt(
  clef: Clef,
  transpose: number,
  fifths: number,
  staffPosition: number,
  accidental: Accidental | null = null
): number {
  const number = BottomLine[clef] + staffPosition;
  const step = Steps[((number % 7) + 7) % 7] as PitchStep;
  const octave = Math.floor(number / 7);
  const alter = accidental ? AccidentalAlter[accidental] : keyAlter(writtenFifths(fifths, transpose), step);
  return midiOf({ step, alter, octave }) - transpose;
}

/** A pitch moved by diatonic steps of the written key, as a vertical drag does (an accidental forcing the alteration). */
export function stepPitch(
  pitch: number,
  steps: number,
  transpose: number,
  fifths: number,
  accidental: Accidental | null = null,
  mode: 'major' | 'minor' = 'major'
): number {
  const { staffPosition } = placementOf('treble', transpose, fifths, pitch, mode);
  return pitchAt('treble', transpose, fifths, staffPosition + steps, accidental);
}

/** The accidental a drag's modifier keys force (documented in the Score Editor): ⌥ sharp, ⇧ flat, both natural. */
export function dragAccidental(event: { altKey: boolean; shiftKey: boolean }): Accidental | null {
  if (event.altKey && event.shiftKey) {
    return 'natural';
  }
  return event.altKey ? 'sharp' : event.shiftKey ? 'flat' : null;
}

/** The straight and triplet display grids of a region on a staff style (0 straight: the style's). */
export function gridsOf(notation: RegionNotation, style: StaffStyle): { straight: number; triplet: number } {
  return notation.displayQuantizeTicks > 0
    ? { straight: notation.displayQuantizeTicks, triplet: notation.tripletQuantizeTicks }
    : { straight: style.displayQuantizeTicks, triplet: style.tripletQuantizeTicks };
}

/** A tick snapped to the nearer of two grids counted from a bar line (0: that grid is off). */
export function snapToGrids(tick: number, barStart: number, straight: number, triplet: number): number {
  const candidates: number[] = [];
  for (const grid of [straight, triplet]) {
    if (grid > 0) {
      candidates.push(barStart + Math.round((tick - barStart) / grid) * grid);
    }
  }
  if (candidates.length === 0) {
    return tick;
  }
  return candidates.reduce((best, c) => (Math.abs(c - tick) < Math.abs(best - tick) ? c : best));
}

/** Key names for the key mode, used by labels. */
export function keyName(fifths: number, mode: KeyMode): string {
  const majors = ['C♭', 'G♭', 'D♭', 'A♭', 'E♭', 'B♭', 'F', 'C', 'G', 'D', 'A', 'E', 'B', 'F♯', 'C♯'];
  const minors = ['A♭', 'E♭', 'B♭', 'F', 'C', 'G', 'D', 'A', 'E', 'B', 'F♯', 'C♯', 'G♯', 'D♯', 'A♯'];
  return `${(mode === 'minor' ? minors : majors)[fifths + 7] ?? 'C'} ${mode}`;
}

/** A pitch's name as Logic writes it (C3 is 60). */
export function logicPitchName(pitch: number): string {
  const names = ['C', 'C♯', 'D', 'D♯', 'E', 'F', 'F♯', 'G', 'G♯', 'A', 'A♯', 'B'];
  return `${names[((pitch % 12) + 12) % 12]}${Math.floor(pitch / 12) - 2}`;
}
