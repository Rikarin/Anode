import type { Type } from '@angular/core';
import type { PluginInfo } from '@anode/api-client';

/**
 * What a custom plugin panel works through: the plugin, a parameter's value to show, and setting one (coalesced, as
 * the generic editor's knobs). The generic plugin window and the instrument parameter editor provide it.
 */
export interface PluginPanelHost {
  readonly plugin: PluginInfo;
  value(parameterId: string): number;
  set(parameterId: string, value: number): void;
}

/**
 * The custom panel for a plugin, where a generic editor is unusable (docs/07-phase-6.md: the drawbars, the FM
 * algorithm picker, the Delay Designer taps, the Multipressor bands), or null: then only the generic editor shows. A
 * panel is a component with a required `host` input ({@link PluginPanelHost}); it is chosen by the plugin's parameter
 * ids (plugin-panels/panel-patterns.ts), so a plugin whose ids do not match falls back to the generic editor.
 */
export function pluginPanel(plugin: PluginInfo | null | undefined): Type<unknown> | null {
  return plugin ? null : null;
}
