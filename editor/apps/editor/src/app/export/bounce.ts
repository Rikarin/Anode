import type {
  AudioFormat,
  BounceRequest,
  BounceSource,
  DitherMode,
  NormalizeMode,
  ProjectSettings,
  Track
} from '@anode/api-client';

/** The bounce dialog's fields (Logic's Bounce window), the range in bars from 1 (end exclusive). */
export interface BounceForm {
  fileName: string;
  /** A folder on the Server's disk; empty: the project's Bounces folder. */
  directory: string;
  startBar: number;
  endBar: number;
  source: BounceSource;
  trackIds: string[];
  format: AudioFormat;
  bitDepth: 16 | 24 | 32;
  /** Null: the project's rate. */
  sampleRate: number | null;
  dither: DitherMode;
  normalize: NormalizeMode;
  loudnessLufs: number;
  peakCeilingDb: number;
  realtime: boolean;
  addToProject: boolean;
}

export const SampleRates = [44_100, 48_000, 88_200, 96_000, 176_400, 192_000] as const;
export const BitDepths: readonly { value: 16 | 24 | 32; label: string }[] = [
  { value: 16, label: '16-bit' },
  { value: 24, label: '24-bit' },
  { value: 32, label: '32-bit Float' }
];
export const Dithers: readonly { value: DitherMode; label: string }[] = [
  { value: 'none', label: 'None' },
  { value: 'tpdf', label: 'TPDF' },
  { value: 'shaped', label: 'Noise Shaped' }
];
export const Normalizations: readonly { value: NormalizeMode; label: string }[] = [
  { value: 'off', label: 'Off' },
  { value: 'peak', label: 'Peak' },
  { value: 'loudness', label: 'Loudness (EBU R128)' }
];

/** Bars ↔ ticks, as the project's signature track has them (bars from 0). */
export interface BarMap {
  barAt(tick: number): number;
  barStart(bar: number): number;
}

/** The tracks a stem bounce can take: those that play (not stacks or VCA strips). */
export function stemTracks(tracks: readonly Track[]): Track[] {
  return tracks.filter(t => t.kind !== 'stack' && t.kind !== 'vca');
}

/**
 * The dialog's starting values: the project's name; the cycle when it is on, else the project start to its end marker
 * (in whole bars); a 24-bit WAV at the project's rate with TPDF dither; every playing track for stems.
 */
export function defaultBounceForm(
  name: string,
  settings: ProjectSettings,
  tracks: readonly Track[],
  bars: BarMap
): BounceForm {
  const { cycle } = settings;
  const [start, end] =
    cycle.enabled && cycle.endTick > cycle.startTick ? [cycle.startTick, cycle.endTick] : [0, settings.endTick];
  const startBar = Math.floor(bars.barAt(start) + 1e-6) + 1;
  const endBar = Math.max(startBar + 1, Math.ceil(bars.barAt(end) - 1e-6) + 1);
  return {
    fileName: name,
    directory: '',
    startBar,
    endBar,
    source: 'mix',
    trackIds: stemTracks(tracks).map(t => t.id),
    format: 'wav',
    bitDepth: 24,
    sampleRate: null,
    dither: 'tpdf',
    normalize: 'off',
    loudnessLufs: -14,
    peakCeilingDb: -1,
    realtime: false,
    addToProject: false
  };
}

/** What is wrong with a form, in words; empty when it can be bounced. */
export function bounceProblems(form: BounceForm): string[] {
  const problems: string[] = [];
  if (!form.fileName.trim()) {
    problems.push('Name the file.');
  } else if (/[\\/:]/.test(form.fileName)) {
    problems.push('A file name cannot contain / \\ or :.');
  }
  if (!(form.startBar >= 1) || !(form.endBar > form.startBar)) {
    problems.push('The end must come after the start.');
  }
  if (form.source === 'stems' && form.trackIds.length === 0) {
    problems.push('Pick the tracks to bounce as stems.');
  }
  if (form.normalize === 'loudness' && !(form.loudnessLufs >= -70 && form.loudnessLufs <= 0)) {
    problems.push('The loudness target is −70 to 0 LUFS.');
  }
  if (form.normalize !== 'off' && !(form.peakCeilingDb >= -20 && form.peakCeilingDb <= 0)) {
    problems.push('The true-peak ceiling is −20 to 0 dB.');
  }
  return problems;
}

/** The request a form makes (dither only below 32 bits; normalisation values only when it normalises). */
export function bounceRequest(form: BounceForm, bars: BarMap): BounceRequest {
  return {
    fileName: form.fileName.trim(),
    directory: form.directory.trim() || null,
    startTick: Math.round(bars.barStart(form.startBar - 1)),
    endTick: Math.round(bars.barStart(form.endBar - 1)),
    source: form.source,
    trackIds: form.source === 'stems' ? [...form.trackIds] : null,
    format: form.format,
    bitDepth: form.bitDepth,
    sampleRate: form.sampleRate,
    dither: form.bitDepth === 32 ? 'none' : form.dither,
    normalize: form.normalize,
    loudnessLufs: form.loudnessLufs,
    peakCeilingDb: form.peakCeilingDb,
    realtime: form.realtime,
    addToProject: form.addToProject
  };
}

/** The file(s) a form will write, as the dialog previews them ("Mix.wav", "Mix - Bass.aif"). */
export function bounceFileNames(form: BounceForm, tracks: readonly Track[]): string[] {
  const ext = form.format === 'wav' ? 'wav' : 'aif';
  const name = form.fileName.trim() || 'Untitled';
  if (form.source === 'mix') {
    return [`${name}.${ext}`];
  }
  return tracks.filter(t => form.trackIds.includes(t.id)).map(t => `${name} - ${t.name}.${ext}`);
}
