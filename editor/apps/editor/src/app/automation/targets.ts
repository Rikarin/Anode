import type { ChannelStrip, IoLabels, PluginInfo, Track } from '@anode/api-client';
import {
  MaxFaderDb,
  SilenceDb,
  faderDb,
  faderPosition,
  formatDb,
  formatPan,
  formatParameter,
  fromNormal,
  toNormal
} from '@anode/audio-ui';
import { busName } from '../mixer/routing';

/** The group a target is listed under in the parameter picker: Main, Sends, or a plugin's name. */
export type TargetGroup = string;

/**
 * Something a track's automation can drive (docs/05-phase-4.md, `AutomationLane.target`): `volume` (dB), `pan`
 * (−1…1), `mute` (0/1), `send:{sendId}` (dB), `insert:{insertId}:{parameterId}` or `instrument:{parameterId}` (the
 * parameter's unit). Values are in the target's unit; `toNormal` and `fromNormal` map them to a lane's height (the
 * fader's taper for decibels, the knob's for parameters).
 */
export interface AutomationTarget {
  readonly target: string;
  readonly name: string;
  readonly group: TargetGroup;
  readonly min: number;
  readonly max: number;
  readonly default: number;
  readonly unit: string;
  /** Discrete: `steps` + 1 values (mute is 1: off and on). */
  readonly steps: number;
  /** Mute holds each value until the next point. */
  readonly stepped: boolean;
  toNormal(value: number): number;
  fromNormal(normal: number): number;
  format(value: number): string;
}

const DbTarget = {
  min: SilenceDb,
  max: MaxFaderDb,
  unit: 'dB',
  steps: 0,
  stepped: false,
  toNormal: (db: number) => faderPosition(db),
  fromNormal: (normal: number) => Math.round(faderDb(normal) * 10) / 10,
  format: (db: number) => formatDb(db)
};

function volumeTarget(current = 0): AutomationTarget {
  return { ...DbTarget, target: 'volume', name: 'Volume', group: 'Main', default: current };
}

function panTarget(current = 0): AutomationTarget {
  return {
    target: 'pan',
    name: 'Pan',
    group: 'Main',
    min: -1,
    max: 1,
    default: current,
    unit: '',
    steps: 0,
    stepped: false,
    toNormal: pan => (Math.min(1, Math.max(-1, pan)) + 1) / 2,
    fromNormal: normal => Math.round((Math.min(1, Math.max(0, normal)) * 2 - 1) * 100) / 100,
    format: pan => formatPan(pan)
  };
}

function muteTarget(current = 0): AutomationTarget {
  return {
    target: 'mute',
    name: 'Mute',
    group: 'Main',
    min: 0,
    max: 1,
    default: current,
    unit: '',
    steps: 1,
    stepped: true,
    toNormal: value => (value >= 0.5 ? 1 : 0),
    fromNormal: normal => (normal >= 0.5 ? 1 : 0),
    format: value => (value >= 0.5 ? 'On' : 'Off')
  };
}

function parameterTarget(
  target: string,
  group: string,
  parameter: PluginInfo['parameters'][number],
  current?: number
): AutomationTarget {
  const range = { min: parameter.min, max: parameter.max, steps: parameter.steps };
  const names = parameter.valueNames;
  return {
    target,
    name: parameter.name,
    group,
    min: parameter.min,
    max: parameter.max,
    default: current ?? parameter.default,
    unit: parameter.unit,
    steps: parameter.steps,
    stepped: parameter.steps > 0,
    toNormal: value => toNormal(value, range),
    fromNormal: normal => fromNormal(normal, range),
    format: value => {
      const index = parameter.steps > 0 ? Math.round(value - parameter.min) : -1;
      return names?.[index] ?? formatParameter(value, parameter.unit, parameter.steps);
    }
  };
}

/** What target lists need of a track: its kind, strip and instrument (the master has a strip only). */
export type TargetSource = Pick<Track, 'kind' | 'strip' | 'instrument'> | { kind?: undefined; strip: ChannelStrip };

/**
 * A track's (or the master's, with only its `strip`) automatable targets in picker order: volume, pan and mute; each
 * send; each insert's parameters, under the insert's name; the instrument's parameters. A VCA has volume and mute;
 * a plugin missing from the catalogue offers nothing. A target's `default` is the control's current value (what an empty
 * lane draws).
 */
export function targetsOf(
  track: TargetSource,
  plugin: (id: string) => PluginInfo | undefined,
  labels?: IoLabels | null
): AutomationTarget[] {
  const { strip } = track;
  const main = [volumeTarget(strip.volumeDb), panTarget(strip.pan), muteTarget(strip.mute ? 1 : 0)];
  if (track.kind === 'stack') {
    return main;
  }
  if (track.kind === 'vca') {
    return [main[0] as AutomationTarget, main[2] as AutomationTarget];
  }
  const list = main;
  for (const send of track.strip.sends) {
    list.push({
      ...DbTarget,
      target: `send:${send.id}`,
      name: `Send to ${busName(send.bus, labels)}`,
      group: 'Sends',
      default: send.levelDb
    });
  }
  for (const insert of track.strip.inserts) {
    for (const parameter of plugin(insert.pluginId)?.parameters ?? []) {
      list.push(
        parameterTarget(`insert:${insert.id}:${parameter.id}`, insert.name, parameter, insert.parameters[parameter.id])
      );
    }
  }
  if (track.kind === 'instrument' && track.instrument) {
    for (const parameter of plugin(track.instrument.pluginId)?.parameters ?? []) {
      list.push(
        parameterTarget(
          `instrument:${parameter.id}`,
          track.instrument.name,
          parameter,
          track.instrument.parameters[parameter.id]
        )
      );
    }
  }
  // Smart Controls (Phase 6): a set-up track's controls, 0…1 each, named as the pane names them.
  const smart = 'smartControls' in track ? (track as Pick<Track, 'smartControls'>).smartControls : null;
  for (const control of smart?.controls ?? []) {
    list.push(smartControlTarget(control.id, control.name, control.value));
  }
  return list;
}

/** A Smart Control's lane, `smartControl:{controlId}`: 0…1 shown as a percentage (a switch as off and on). */
function smartControlTarget(controlId: string, name: string, current: number): AutomationTarget {
  const isSwitch = controlId.startsWith('switch');
  return {
    target: `smartControl:${controlId}`,
    name,
    group: 'Smart Controls',
    min: 0,
    max: 1,
    default: current,
    unit: '',
    steps: isSwitch ? 1 : 0,
    stepped: isSwitch,
    toNormal: value => Math.min(1, Math.max(0, value)),
    fromNormal: normal => Math.round(Math.min(1, Math.max(0, normal)) * 1000) / 1000,
    format: value => (isSwitch ? (value >= 0.5 ? 'On' : 'Off') : `${Math.round(value * 100)} %`)
  };
}

/**
 * One target of a track (or the master): from {@link targetsOf}, or, for a target whose send, insert or parameter is
 * gone, undefined.
 */
export function targetOf(
  track: TargetSource,
  target: string,
  plugin: (id: string) => PluginInfo | undefined,
  labels?: IoLabels | null
): AutomationTarget | undefined {
  return targetsOf(track, plugin, labels).find(t => t.target === target);
}

/**
 * A fallback for a lane whose target the editor cannot describe (its plugin is not in the catalogue): values drawn
 * over 0…1, shown as numbers.
 */
export function unknownTarget(target: string): AutomationTarget {
  return {
    target,
    name: target,
    group: 'Other',
    min: 0,
    max: 1,
    default: 0,
    unit: '',
    steps: 0,
    stepped: false,
    toNormal: value => Math.min(1, Math.max(0, value)),
    fromNormal: normal => Math.min(1, Math.max(0, normal)),
    format: value => value.toFixed(2)
  };
}

/** Targets grouped for the picker, in order. */
export function groupTargets(targets: readonly AutomationTarget[]): { group: string; targets: AutomationTarget[] }[] {
  const groups = new Map<string, AutomationTarget[]>();
  for (const target of targets) {
    groups.set(target.group, [...(groups.get(target.group) ?? []), target]);
  }
  return [...groups].map(([group, list]) => ({ group, targets: list }));
}
