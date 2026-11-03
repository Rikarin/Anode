import {
  type ChannelStrip,
  type Insert,
  type MasteringAnalysis,
  type MasteringCharacter,
  MasteringParameters,
  type MasteringSettings,
  type ProjectSettings
} from '@anode/api-client';

/** The characters in their parameter's order (`character` 0…3), with their names. */
export const MasteringCharacters: readonly { value: MasteringCharacter; name: string }[] = [
  { value: 'clean', name: 'Clean' },
  { value: 'valve', name: 'Valve' },
  { value: 'punch', name: 'Punch' },
  { value: 'transparent', name: 'Transparent' }
];

/** The settings' ranges (`MasteringParameters`; the contract's). */
export const MasteringRanges = {
  targetLufs: { min: -24, max: -6, default: -14 },
  ceilingDbtp: { min: -3, max: 0, default: -1 },
  eq: { min: 0, max: 1, default: 1 },
  dynamics: { min: 0, max: 1, default: 1 },
  width: { min: 0, max: 2, default: 1 }
} as const;

export type MasteringAmount = keyof typeof MasteringRanges;

/** The master's Mastering Assistant (the last, as the Server picks it when no insert is named), or null. */
export function masteringInsert(master: ChannelStrip): Insert | null {
  return [...master.inserts].reverse().find(i => i.pluginId === MasteringParameters.pluginId) ?? null;
}

/** An assistant's settings, from its insert parameters (missing ones at their defaults). */
export function masteringSettingsOf(insert: Insert): MasteringSettings {
  const v = insert.parameters;
  return {
    character: MasteringCharacters[Math.round(v[MasteringParameters.character] ?? 0)]?.value ?? 'clean',
    targetLufs: v[MasteringParameters.targetLufs] ?? MasteringRanges.targetLufs.default,
    ceilingDbtp: v[MasteringParameters.ceilingDbtp] ?? MasteringRanges.ceilingDbtp.default,
    eq: v[MasteringParameters.eq] ?? MasteringRanges.eq.default,
    dynamics: v[MasteringParameters.dynamics] ?? MasteringRanges.dynamics.default,
    width: v[MasteringParameters.width] ?? MasteringRanges.width.default,
    matchedBypass: (v[MasteringParameters.matchedBypass] ?? 0) >= 0.5
  };
}

/**
 * The level-matched bypass' gain: the unprocessed master raised (or lowered) to the target, so bypassing compares the
 * sound, not the loudness. Null before an analysis.
 */
export function bypassGainDb(analysis: MasteringAnalysis | null, settings: MasteringSettings): number | null {
  return analysis ? Math.round((settings.targetLufs - analysis.integratedLufs) * 10) / 10 : null;
}

/** A signed dB change as text: "+6.2 dB", "−1.5 dB", "0.0 dB", "—" for none. */
export function signedDb(value: number | null, unit = 'dB'): string {
  if (value === null || !Number.isFinite(value)) {
    return '—';
  }
  const text = Math.abs(value).toFixed(1);
  return `${value > 0.04 ? '+' : value < -0.04 ? '−' : ''}${text} ${unit}`;
}

/** A value as text in its unit, negative with a true minus: "−14.0 LUFS". */
export function readingText(value: number | null | undefined, unit: string, digits = 1): string {
  if (value === null || value === undefined || !Number.isFinite(value)) {
    return '—';
  }
  const text = value.toFixed(digits);
  return `${value < 0 && Number(text) !== 0 ? '−' + text.slice(1) : text.replace('-', '')}${unit ? ' ' + unit : ''}`;
}

/** What Analyse covers: the whole song, or the cycle area (when it is on). */
export type AnalysisSpan = 'song' | 'cycle';

/** The ticks a span covers, for the request (the whole song: the Server's own range, the project's end). */
export function analysisRange(span: AnalysisSpan, settings: ProjectSettings): { startTick?: number; endTick?: number } {
  if (span === 'cycle' && settings.cycle.enabled && settings.cycle.endTick > settings.cycle.startTick) {
    return { startTick: settings.cycle.startTick, endTick: settings.cycle.endTick };
  }
  return { startTick: 0, endTick: settings.endTick };
}

/** The analysis' summary rows, in the window's order. */
export function analysisRows(analysis: MasteringAnalysis | null): { label: string; value: string }[] {
  return [
    { label: 'Integrated', value: readingText(analysis?.integratedLufs, 'LUFS') },
    { label: 'Max short-term', value: readingText(analysis?.maxShortTermLufs, 'LUFS') },
    { label: 'Loudness range', value: readingText(analysis?.loudnessRange, 'LU') },
    { label: 'True peak', value: readingText(analysis?.truePeakDbtp, 'dBTP') },
    { label: 'Crest factor', value: readingText(analysis?.crestFactorDb, 'dB') },
    { label: 'Width', value: analysis ? `${Math.round(analysis.width * 100)} %` : '—' }
  ];
}
