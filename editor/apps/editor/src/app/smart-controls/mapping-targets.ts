import type { ParameterInfo, PluginInfo, Track } from '@anode/api-client';
import { formatParameter } from '@anode/audio-ui';
import { type AutomationTarget, targetsOf } from '../automation/targets';

/** A parameter a Smart Control can map to: its address (as automation lanes address it), name, range and unit. */
export interface MappingTarget {
  readonly target: string;
  readonly name: string;
  readonly group: string;
  readonly min: number;
  readonly max: number;
  readonly unit: string;
  readonly steps: number;
  format(value: number): string;
}

/** The picker's group of the instrument's parameters. */
export function instrumentGroup(name: string): string {
  return `Instrument: ${name}`;
}

/** The picker's group of a MIDI FX slot's parameters. */
export function midiEffectGroup(name: string): string {
  return `MIDI FX: ${name}`;
}

function fromAutomation(target: AutomationTarget, group: string): MappingTarget {
  return {
    target: target.target,
    name: target.name,
    group,
    min: target.min,
    max: target.max,
    unit: target.unit,
    steps: target.steps,
    format: value => target.format(value)
  };
}

function fromParameter(target: string, group: string, parameter: ParameterInfo): MappingTarget {
  return {
    target,
    name: parameter.name,
    group,
    min: parameter.min,
    max: parameter.max,
    unit: parameter.unit,
    steps: parameter.steps,
    format: value => {
      const index = parameter.steps > 0 ? Math.round(value - parameter.min) : -1;
      return parameter.valueNames?.[index] ?? formatParameter(value, parameter.unit, parameter.steps);
    }
  };
}

/**
 * What a track's Smart Controls can map to, grouped for the parameter picker in Logic's order: the instrument
 * ("Instrument: Synth"), each insert (by its name), each MIDI FX slot ("MIDI FX: Arpeggiator"), then Main (volume, pan
 * and the sends). Mute and the controls' own lanes are not mapping targets. The automation targets supply the strip and
 * plugin parameters; MIDI FX slots are added here unless the automation list already has them.
 */
export function mappingTargets(
  track: Track,
  plugin: (id: string) => PluginInfo | undefined
): { group: string; targets: MappingTarget[] }[] {
  const automation = targetsOf(track, plugin).filter(t => t.target !== 'mute' && !t.target.startsWith('smartControl:'));
  const instrument: MappingTarget[] = [];
  const inserts = new Map<string, MappingTarget[]>();
  const midiEffects = new Map<string, MappingTarget[]>();
  const main: MappingTarget[] = [];
  for (const t of automation) {
    const slot = /^(insert|midiEffect):([^:]+):/.exec(t.target);
    if (t.target.startsWith('instrument:')) {
      instrument.push(fromAutomation(t, instrumentGroup(track.instrument?.name ?? 'Instrument')));
    } else if (slot?.[1] === 'insert' && slot[2]) {
      const insert = track.strip.inserts.find(i => i.id === slot[2]);
      const group = insert?.name ?? t.group;
      inserts.set(slot[2], [...(inserts.get(slot[2]) ?? []), fromAutomation(t, group)]);
    } else if (slot?.[1] === 'midiEffect' && slot[2]) {
      const fx = track.strip.midiEffects.find(i => i.id === slot[2]);
      const group = midiEffectGroup(fx?.name ?? t.group);
      midiEffects.set(slot[2], [...(midiEffects.get(slot[2]) ?? []), fromAutomation(t, group)]);
    } else {
      main.push(fromAutomation(t, 'Main'));
    }
  }
  for (const fx of track.strip.midiEffects) {
    if (midiEffects.has(fx.id)) {
      continue;
    }
    const group = midiEffectGroup(fx.name);
    const list = (plugin(fx.pluginId)?.parameters ?? []).map(p =>
      fromParameter(`midiEffect:${fx.id}:${p.id}`, group, p)
    );
    if (list.length > 0) {
      midiEffects.set(fx.id, list);
    }
  }
  const groups: { group: string; targets: MappingTarget[] }[] = [];
  const push = (list: MappingTarget[]) => {
    const first = list[0];
    if (first) {
      groups.push({ group: first.group, targets: list });
    }
  };
  push(instrument);
  inserts.forEach(push);
  midiEffects.forEach(push);
  push(main);
  return groups;
}

/** One mapping target of a track, or undefined when its plugin, slot or send is gone. */
export function mappingTarget(
  track: Track,
  target: string,
  plugin: (id: string) => PluginInfo | undefined
): MappingTarget | undefined {
  for (const group of mappingTargets(track, plugin)) {
    const found = group.targets.find(t => t.target === target);
    if (found) {
      return found;
    }
  }
  return undefined;
}
