import type { Type } from '@angular/core';
import type { PluginInfo } from '@anode/api-client';
import { DelayTapsPanel } from './delay-taps-panel';
import { DrawbarsPanel } from './drawbars-panel';
import { FmAlgorithmPanel } from './fm-algorithm-panel';
import { MultipressorPanel } from './multipressor-panel';
import { type PanelKind, matchPanel } from './panel-patterns';

/**
 * What a custom plugin panel works through: the plugin, a parameter's value to show, and setting one (coalesced, as
 * the generic editor's knobs). The generic plugin window and the instrument parameter editor provide it.
 */
export interface PluginPanelHost {
  readonly plugin: PluginInfo;
  value(parameterId: string): number;
  set(parameterId: string, value: number): void;
}

/** The T3 panels (Phase 11): drawn by `an-t3-panel` (t3-panel.ts), which the plugin window loads when first shown. */
export type DeferredPanelKind = Extract<PanelKind, 'physicalModel' | 'sampleAlchemy'>;

const Deferred: ReadonlySet<PanelKind> = new Set<DeferredPanelKind>(['physicalModel', 'sampleAlchemy']);

/** The panel component of each kind loaded with the window (the deferred ones are not here). */
const Panels: Readonly<Record<Exclude<PanelKind, DeferredPanelKind>, Type<unknown>>> = {
  drawbars: DrawbarsPanel,
  fmAlgorithm: FmAlgorithmPanel,
  delayTaps: DelayTapsPanel,
  multipressorBands: MultipressorPanel
};

/**
 * The custom panel for a plugin, where a generic editor is unusable (docs/07-phase-6.md: the drawbars, the FM
 * algorithm picker, the Delay Designer taps, the Multipressor bands), or null: then only the generic editor shows. A
 * panel is a component with a required `host` input ({@link PluginPanelHost}); it is chosen by the plugin's parameter
 * ids (plugin-panels/panel-patterns.ts), so a plugin whose ids do not match falls back to the generic editor.
 */
export function pluginPanel(plugin: PluginInfo | null | undefined): Type<unknown> | null {
  const match = matchPanel(plugin);
  return match && !Deferred.has(match.kind) ? Panels[match.kind as Exclude<PanelKind, DeferredPanelKind>] : null;
}

/**
 * The T3 panel for a plugin (Phase 11: the physical model's object and pickups, Sample Alchemy's source view), or null.
 * They load with their own chunk (`@defer` in the plugin window), so the initial bundle carries none of them.
 */
export function deferredPanel(plugin: PluginInfo | null | undefined): DeferredPanelKind | null {
  const kind = matchPanel(plugin)?.kind;
  return kind && Deferred.has(kind) ? (kind as DeferredPanelKind) : null;
}
