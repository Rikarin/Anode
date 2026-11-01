import type { Clef, Notehead, ScoreLayout, Staff, StaffStyle } from '@anode/api-client';

/** A new client-chosen id with the model's prefix (`sts_…` a staff style, `scs_…` a score set). */
export function newScoreId(prefix: 'sts' | 'scs'): string {
  const random =
    typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'
      ? crypto.randomUUID().replaceAll('-', '')
      : Math.random().toString(16).slice(2) + Math.random().toString(16).slice(2);
  return `${prefix}_${random.slice(0, 12)}`;
}

export const Clefs: readonly { value: Clef; label: string }[] = [
  { value: 'treble', label: 'Treble' },
  { value: 'bass', label: 'Bass' },
  { value: 'alto', label: 'Alto' },
  { value: 'tenor', label: 'Tenor' },
  { value: 'treble8vb', label: 'Treble 8vb' },
  { value: 'treble8va', label: 'Treble 8va' },
  { value: 'bass8vb', label: 'Bass 8vb' },
  { value: 'percussion', label: 'Percussion' },
  { value: 'none', label: 'None' }
];

export const Noteheads: readonly Notehead[] = ['normal', 'cross', 'circleCross', 'diamond', 'triangle', 'slash'];

/** Display quantise grids (ticks; 0 off), as Logic lists them. */
export const StraightGrids: readonly { ticks: number; label: string }[] = [
  { ticks: 0, label: 'Style' },
  { ticks: 3840, label: '1/1' },
  { ticks: 1920, label: '1/2' },
  { ticks: 960, label: '1/4' },
  { ticks: 480, label: '1/8' },
  { ticks: 240, label: '1/16' },
  { ticks: 120, label: '1/32' },
  { ticks: 60, label: '1/64' }
];
export const TripletGrids: readonly { ticks: number; label: string }[] = [
  { ticks: 0, label: 'None' },
  { ticks: 640, label: '1/6 (quarter triplets)' },
  { ticks: 320, label: '1/12 (eighth triplets)' },
  { ticks: 160, label: '1/24 (sixteenth triplets)' },
  { ticks: 80, label: '1/48' }
];

/** A copy of a style as a project style (a factory one duplicated, or a project one). */
export function duplicateStyle(style: StaffStyle, id = newScoreId('sts')): StaffStyle {
  return { ...structuredClone(style), id, name: `${style.name} Copy` };
}

/** A new project style: one treble staff. */
export function newStyle(id = newScoreId('sts')): StaffStyle {
  return {
    id,
    name: 'New Style',
    staves: [{ clef: 'treble', transpose: 0, splitPitch: null, voices: 2 }],
    drumMap: null,
    displayQuantizeTicks: 240,
    tripletQuantizeTicks: 160,
    brace: false
  };
}

/**
 * A style's staves after a change, kept valid: every staff but the last has a split point (a new one splits an octave
 * below the one above), the last has none.
 */
export function normaliseStaves(staves: readonly Staff[]): Staff[] {
  return staves.map((staff, i) => {
    const last = i === staves.length - 1;
    if (last) {
      return { ...staff, splitPitch: null };
    }
    const above = i > 0 ? (staves[i - 1]?.splitPitch ?? 72) : 72;
    return { ...staff, splitPitch: staff.splitPitch ?? Math.max(0, above - 12) };
  });
}

/** The page presets the layout window offers (portrait, millimetres). */
export const PagePresets: readonly { name: string; width: number; height: number }[] = [
  { name: 'A4', width: 210, height: 297 },
  { name: 'Letter', width: 215.9, height: 279.4 },
  { name: 'A3', width: 297, height: 420 },
  { name: 'Legal', width: 215.9, height: 355.6 },
  { name: 'Tabloid', width: 279.4, height: 431.8 },
  { name: 'B4', width: 250, height: 353 }
];

/** The preset a page size is (either orientation), else "Custom". */
export function presetOf(layout: Pick<ScoreLayout, 'pageWidthMm' | 'pageHeightMm'>): {
  name: string;
  landscape: boolean;
} {
  const near = (a: number, b: number) => Math.abs(a - b) < 0.5;
  for (const preset of PagePresets) {
    if (near(preset.width, layout.pageWidthMm) && near(preset.height, layout.pageHeightMm)) {
      return { name: preset.name, landscape: false };
    }
    if (near(preset.height, layout.pageWidthMm) && near(preset.width, layout.pageHeightMm)) {
      return { name: preset.name, landscape: true };
    }
  }
  return { name: 'Custom', landscape: layout.pageWidthMm > layout.pageHeightMm };
}

/** A layout with a preset page in an orientation. */
export function withPreset(layout: ScoreLayout, name: string, landscape: boolean): ScoreLayout {
  const preset = PagePresets.find(p => p.name === name);
  if (!preset) {
    return layout;
  }
  return {
    ...layout,
    pageWidthMm: landscape ? preset.height : preset.width,
    pageHeightMm: landscape ? preset.width : preset.height
  };
}

/** A track's instrument names in a layout (the entry, or the track's name for both). */
export function instrumentNameOf(
  layout: ScoreLayout,
  trackId: string,
  trackName: string
): { full: string; short: string } {
  const entry = layout.instrumentNames.find(n => n.trackId === trackId);
  return { full: entry?.full || trackName, short: entry?.short || trackName.slice(0, 5) };
}

/** A layout with a track's names set (dropped when both equal the track's own, as the contract lists only differences). */
export function withInstrumentName(
  layout: ScoreLayout,
  trackId: string,
  trackName: string,
  names: { full: string; short: string }
): ScoreLayout {
  const others = layout.instrumentNames.filter(n => n.trackId !== trackId);
  const same =
    (names.full === '' || names.full === trackName) && (names.short === '' || names.short === trackName.slice(0, 5));
  return { ...layout, instrumentNames: same ? others : [...others, { trackId, full: names.full, short: names.short }] };
}
