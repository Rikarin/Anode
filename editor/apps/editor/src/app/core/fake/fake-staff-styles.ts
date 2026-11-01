import { type DrumMapEntry, type ProjectScore, type Staff, type StaffStyle, StaffStyleIds } from '@anode/api-client';

/** The display quantise defaults of the factory styles: Logic's "16,24" (a 1/16 grid and a 1/16-triplet grid). */
const Sixteenth = 240;
const SixteenthTriplet = 160;

function staff(clef: Staff['clef'], transpose = 0, splitPitch: number | null = null, voices = 2): Staff {
  return { clef, transpose, splitPitch, voices };
}

function style(id: string, name: string, staves: Staff[], extra: Partial<StaffStyle> = {}): StaffStyle {
  return {
    id,
    name,
    staves,
    drumMap: null,
    displayQuantizeTicks: Sixteenth,
    tripletQuantizeTicks: SixteenthTriplet,
    brace: false,
    ...extra
  };
}

/** General MIDI drums on a percussion staff (positions above the bottom line). */
const DrumMap: DrumMapEntry[] = [
  { pitch: 36, staffPosition: 1, notehead: 'normal', name: 'Kick' },
  { pitch: 38, staffPosition: 5, notehead: 'normal', name: 'Snare' },
  { pitch: 37, staffPosition: 5, notehead: 'cross', name: 'Side Stick' },
  { pitch: 41, staffPosition: 3, notehead: 'normal', name: 'Floor Tom' },
  { pitch: 45, staffPosition: 6, notehead: 'normal', name: 'Low Tom' },
  { pitch: 48, staffPosition: 7, notehead: 'normal', name: 'High Tom' },
  { pitch: 42, staffPosition: 9, notehead: 'cross', name: 'Closed Hi-Hat' },
  { pitch: 46, staffPosition: 9, notehead: 'circleCross', name: 'Open Hi-Hat' },
  { pitch: 44, staffPosition: -1, notehead: 'cross', name: 'Pedal Hi-Hat' },
  { pitch: 49, staffPosition: 10, notehead: 'cross', name: 'Crash' },
  { pitch: 51, staffPosition: 8, notehead: 'cross', name: 'Ride' }
];

/**
 * The factory staff styles as the fake Server's library answers them (`GET /library/staff-styles`), in
 * `StaffStyleIds.All`'s order: the notation engine's data (`Anode.Notation.FactoryStaffStyles`) as the contract
 * describes it.
 */
export const FactoryStaffStyles: readonly StaffStyle[] = [
  style(StaffStyleIds.treble, 'Treble', [staff('treble')]),
  style(StaffStyleIds.bass, 'Bass', [staff('bass')]),
  style(StaffStyleIds.piano, 'Piano', [staff('treble', 0, 60), staff('bass')], { brace: true }),
  style(StaffStyleIds.drums, 'Drums', [staff('percussion', 0, null, 2)], { drumMap: DrumMap }),
  style(StaffStyleIds.guitar, 'Guitar', [staff('treble8vb')]),
  style(StaffStyleIds.bassGuitar, 'Bass Guitar', [staff('bass8vb')]),
  style(StaffStyleIds.clarinetBb, 'B♭ Clarinet', [staff('treble', 2, null, 1)]),
  style(StaffStyleIds.trumpetBb, 'B♭ Trumpet', [staff('treble', 2, null, 1)]),
  style(StaffStyleIds.hornF, 'F Horn', [staff('treble', 7, null, 1)]),
  style(StaffStyleIds.altoSax, 'Alto Sax', [staff('treble', 9, null, 1)]),
  style(StaffStyleIds.tenorSax, 'Tenor Sax', [staff('treble', 14, null, 1)]),
  style(StaffStyleIds.viola, 'Viola', [staff('alto')]),
  style(StaffStyleIds.cello, 'Cello', [staff('bass')])
];

/** A factory style by id, or null. */
export function findFactoryStyle(id: string): StaffStyle | null {
  return FactoryStaffStyles.find(s => s.id === id) ?? null;
}

/** The style a track uses: the project's own, else a factory one; null or unknown: the default (piano). */
export function resolveStaffStyle(score: ProjectScore, styleId: string | null | undefined): StaffStyle {
  return (
    score.staffStyles.find(s => s.id === styleId) ??
    (styleId ? findFactoryStyle(styleId) : null) ??
    (findFactoryStyle(StaffStyleIds.default) as StaffStyle)
  );
}
