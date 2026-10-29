import type { ParameterInfo, PluginInfo } from '@anode/api-client';

/** The custom panels (docs/07-phase-6.md, "C — Editor": where a generic editor is unusable). */
export type PanelKind = 'drawbars' | 'fmAlgorithm' | 'delayTaps' | 'multipressorBands';

/**
 * A plugin's parameters bound to a panel's roles: `roles` the single ones (a role is absent when no parameter
 * matched), `series` the numbered ones, each in order from 1 for as long as the numbers run without a gap.
 */
export interface PanelBinding {
  readonly roles: Readonly<Record<string, ParameterInfo | undefined>>;
  readonly series: Readonly<Record<string, readonly ParameterInfo[]>>;
}

/**
 * How a panel finds its parameters by id. A role is a regular expression over parameter ids (the first match wins);
 * a series role's first capture group is the 1-based number ("tap3.time" → 3). `accepts` says whether the binding is
 * complete enough to drive the panel; when it is not, the plugin gets the generic editor only.
 */
export interface PanelPattern {
  readonly kind: PanelKind;
  readonly roles: Readonly<Record<string, RegExp>>;
  readonly series: Readonly<Record<string, RegExp>>;
  accepts(binding: PanelBinding): boolean;
}

/** A separator inside an id: none, ".", "_" or "-". */
const S = '[._-]?';

/** Whether a parameter can take `count` whole values (a choice or stepped value with that many, or a range of them). */
function holdsValues(parameter: ParameterInfo | undefined, count: number): boolean {
  if (!parameter) {
    return false;
  }
  return parameter.steps > 0 ? parameter.steps + 1 >= count : parameter.max - parameter.min >= count - 1;
}

/**
 * The parameter-id patterns of the custom panels, the first example of each being the fake catalogue's ids
 * (`core/fake/fake-plugins-phase6.ts`); workstreams D and E publish the real ones, and the integration step aligns
 * them. Matching is case-insensitive and accepts ".", "_", "-" or nothing between words.
 *
 * - **Drawbars** (Drawbar Organ): `drawbar1`…`drawbar9` (also `drawbar.1`, `drawbars_1`, `bar1`), all nine required;
 *   optional `percussion` (`perc`, `percussion.mode`), `click` (`keyClick`, `key.click`), `scanner` (`vibrato`,
 *   `chorusVibrato`, `vibrato.mode`), `rotary.speed` (`leslie.speed`), `rotary.mix` (`leslie.mix`), `drive`.
 * - **FM algorithm** (FM synth): `algorithm` (`algo`, `fm.algorithm`) with at least 8 values; operators
 *   `op1.ratio`, `op1.level` … (also `operator1.ratio`, `op_1_coarse`, `op1.output`), at least 2 operators from 1
 *   with both a ratio and a level; optional `feedback`.
 * - **Delay Designer taps**: workstream D's `tapATime`, `tapALevel`, `tapAPan`, `tapAHighCut` (taps lettered A…Z, A
 *   = 1), or `tap1.time` … (`tap_1_delay`), `tap1.level` (`gain`, `volume`), at least 1 tap with both;
 *   optional per tap `tapN.pan`, `tapN.cutoff` (`filter`, `lowpass`); optional tap count `taps` (`tapCount`,
 *   `numTaps`).
 * - **Multipressor bands**: `band1.threshold` (`thresh`), `band1.ratio`, `band1.gain` (`makeup`, `output`) for at
 *   least 2 bands from 1; `crossover1` … (`xover1`, `split1`, `crossover.1`) one fewer than the bands; optional
 *   `bandN.bypass` (`bandN.off`, `bandN.mute`), or its inverse `bandNOn` (workstream D: `band1Threshold`, `band1On`,
 *   `cross1`…`cross3`).
 */
export const PanelPatterns: readonly PanelPattern[] = [
  {
    kind: 'drawbars',
    roles: {
      percussion: new RegExp(`^perc(ussion)?(${S}(mode|on))?$`, 'i'),
      click: new RegExp(`^(key${S})?click$`, 'i'),
      scanner: new RegExp(`^(scanner|vibrato|chorus${S}vibrato|vibrato${S}chorus)(${S}mode)?$`, 'i'),
      rotarySpeed: new RegExp(`^(rotary|leslie)${S}speed$`, 'i'),
      rotaryMix: new RegExp(`^(rotary|leslie)${S}mix$`, 'i'),
      drive: /^drive$/i
    },
    series: { drawbar: new RegExp(`^(?:drawbars?|bar)${S}([1-9])$`, 'i') },
    accepts: b => b.series['drawbar']?.length === 9
  },
  {
    kind: 'fmAlgorithm',
    roles: {
      algorithm: new RegExp(`^(fm${S})?algo(rithm)?$`, 'i'),
      feedback: new RegExp(`^(fm${S})?feedback$`, 'i')
    },
    series: {
      ratio: new RegExp(`^op(?:erator)?${S}(\\d)${S}(?:ratio|coarse|freq${S}ratio)$`, 'i'),
      level: new RegExp(`^op(?:erator)?${S}(\\d)${S}(?:level|output|out|amp)$`, 'i')
    },
    accepts: b =>
      holdsValues(b.roles['algorithm'], 8) &&
      Math.min(b.series['ratio']?.length ?? 0, b.series['level']?.length ?? 0) >= 2
  },
  {
    kind: 'delayTaps',
    roles: { count: new RegExp(`^(taps|tap${S}count|num${S}taps)$`, 'i') },
    series: {
      time: new RegExp(`^tap${S}(\\d+|[a-z])${S}(?:time|delay)$`, 'i'),
      level: new RegExp(`^tap${S}(\\d+|[a-z])${S}(?:level|gain|volume)$`, 'i'),
      pan: new RegExp(`^tap${S}(\\d+|[a-z])${S}pan$`, 'i'),
      cutoff: new RegExp(`^tap${S}(\\d+|[a-z])${S}(?:cutoff|filter|lowpass|high${S}cut)$`, 'i')
    },
    accepts: b => Math.min(b.series['time']?.length ?? 0, b.series['level']?.length ?? 0) >= 1
  },
  {
    kind: 'multipressorBands',
    roles: {},
    series: {
      threshold: new RegExp(`^band${S}(\\d)${S}thresh(?:old)?$`, 'i'),
      ratio: new RegExp(`^band${S}(\\d)${S}ratio$`, 'i'),
      gain: new RegExp(`^band${S}(\\d)${S}(?:gain|makeup|output)$`, 'i'),
      bypass: new RegExp(`^band${S}(\\d)${S}(?:bypass|off|mute)$`, 'i'),
      enabled: new RegExp(`^band${S}(\\d)${S}(?:on|enabled|active)$`, 'i'),
      crossover: new RegExp(`^(?:crossover|cross|xover|split)${S}(\\d)$`, 'i')
    },
    accepts: b => {
      const bands = bandCount(b);
      return bands >= 2 && (b.series['crossover']?.length ?? 0) >= bands - 1;
    }
  }
];

/** The Multipressor's bands: those with a threshold, a ratio and a gain, from 1. */
export function bandCount(binding: PanelBinding): number {
  return Math.min(
    binding.series['threshold']?.length ?? 0,
    binding.series['ratio']?.length ?? 0,
    binding.series['gain']?.length ?? 0
  );
}

/** A series number: "3" → 3, or a tap letter "A" → 1 (Delay Designer's taps are lettered). */
function seriesNumber(raw: string | undefined): number {
  if (raw === undefined) {
    return NaN;
  }
  return /^[a-z]$/i.test(raw) ? raw.toUpperCase().charCodeAt(0) - 64 : Number(raw);
}

/** Binds a plugin's parameters to a pattern's roles (complete or not: see {@link PanelPattern.accepts}). */
export function bindPattern(pattern: PanelPattern, parameters: readonly ParameterInfo[]): PanelBinding {
  const roles: Record<string, ParameterInfo | undefined> = {};
  for (const [role, regex] of Object.entries(pattern.roles)) {
    roles[role] = parameters.find(p => regex.test(p.id));
  }
  const series: Record<string, ParameterInfo[]> = {};
  for (const [role, regex] of Object.entries(pattern.series)) {
    const numbered = new Map<number, ParameterInfo>();
    for (const parameter of parameters) {
      const n = seriesNumber(regex.exec(parameter.id)?.[1]);
      if (n >= 1 && !numbered.has(n)) {
        numbered.set(n, parameter);
      }
    }
    const run: ParameterInfo[] = [];
    for (let n = 1; numbered.has(n); n++) {
      run.push(numbered.get(n) as ParameterInfo);
    }
    series[role] = run;
  }
  return { roles, series };
}

/** The first panel whose pattern a plugin's parameters satisfy, with the binding; null for the generic editor. */
export function matchPanel(plugin: PluginInfo | null | undefined): { kind: PanelKind; binding: PanelBinding } | null {
  if (!plugin) {
    return null;
  }
  for (const pattern of PanelPatterns) {
    const binding = bindPattern(pattern, plugin.parameters);
    if (pattern.accepts(binding)) {
      return { kind: pattern.kind, binding };
    }
  }
  return null;
}

/** A plugin's binding for one panel, when its parameters satisfy that panel's pattern. */
export function panelBinding(plugin: PluginInfo, kind: PanelKind): PanelBinding | null {
  const pattern = PanelPatterns.find(p => p.kind === kind);
  if (!pattern) {
    return null;
  }
  const binding = bindPattern(pattern, plugin.parameters);
  return pattern.accepts(binding) ? binding : null;
}
