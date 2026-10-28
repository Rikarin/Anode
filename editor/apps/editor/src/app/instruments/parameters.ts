import type { ParameterInfo, PluginInfo } from '@anode/api-client';

/** Every parameter of a plugin at its default, by id. */
export function defaultParameters(plugin: PluginInfo): Record<string, number> {
  return Object.fromEntries(plugin.parameters.map(parameter => [parameter.id, parameter.default]));
}

/** A value as a parameter takes it: within its range, on one of its steps when it is discrete. */
export function clampParameter(parameter: ParameterInfo, value: number): number {
  const clamped = Math.min(parameter.max, Math.max(parameter.min, value));
  if (parameter.steps > 0) {
    const step = (parameter.max - parameter.min) / parameter.steps;
    return parameter.min + Math.round((clamped - parameter.min) / step) * step;
  }
  return clamped;
}
