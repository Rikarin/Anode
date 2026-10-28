import type { PluginInfo } from '@anode/api-client';

/** Logic's Audio FX menu order; categories it doesn't name follow alphabetically. */
export const EffectCategories = [
  'EQ',
  'Dynamics',
  'Filter',
  'Delay',
  'Reverb',
  'Modulation',
  'Distortion',
  'Utility',
  'Metering'
] as const;

const FormatNames: Record<PluginInfo['format'], string> = { builtin: '', clap: 'CLAP', vst3: 'VST3', au: 'AU' };

/** A plugin's name in a menu: the built-in ones bare, third-party ones with their format ("Test Gain (VST3)"). */
export function pluginLabel(plugin: Pick<PluginInfo, 'name' | 'format'>): string {
  const format = FormatNames[plugin.format];
  return format ? `${plugin.name} (${format})` : plugin.name;
}

/**
 * The insert picker's menu: the effects (not instruments) by category in Logic's order, built-in ones first in each,
 * then third-party ones by name. Empty categories are left out.
 */
export function effectMenu(plugins: readonly PluginInfo[]): { category: string; plugins: PluginInfo[] }[] {
  const byCategory = new Map<string, PluginInfo[]>();
  for (const plugin of plugins) {
    if (plugin.isInstrument) {
      continue;
    }
    const category = plugin.category.trim() || 'Other';
    byCategory.set(category, [...(byCategory.get(category) ?? []), plugin]);
  }
  const rank = (category: string) => {
    const index = (EffectCategories as readonly string[]).indexOf(category);
    return index < 0 ? EffectCategories.length : index;
  };
  return [...byCategory]
    .sort(([a], [b]) => rank(a) - rank(b) || a.localeCompare(b))
    .map(([category, list]) => ({
      category,
      plugins: [...list].sort(
        (a, b) => Number(a.format !== 'builtin') - Number(b.format !== 'builtin') || a.name.localeCompare(b.name)
      )
    }));
}
