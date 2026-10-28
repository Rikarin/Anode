import type { AudioRegion, PluginInfo, ProcessRequest } from '@anode/api-client';
import type { LaneRow } from '../state/lanes';
import type { MarqueeRange } from '../state/tracks-view.store';

/** One effect of a processing chain, with the values it is set to. */
export interface ChainEntry {
  /** A key of its own (the same effect may be in the chain twice). */
  readonly key: number;
  readonly pluginId: string;
  readonly values: Readonly<Record<string, number>>;
}

/** The effects selection-based processing may use: built-in, not instruments, not meters (they change nothing). */
export function processingEffects(plugins: readonly PluginInfo[]): PluginInfo[] {
  return plugins
    .filter(p => p.format === 'builtin' && !p.isInstrument && p.category !== 'Metering')
    .sort((a, b) => a.category.localeCompare(b.category) || a.name.localeCompare(b.name));
}

/** A new chain entry with the effect's defaults. */
export function chainEntry(plugin: PluginInfo, key: number): ChainEntry {
  return { key, pluginId: plugin.id, values: Object.fromEntries(plugin.parameters.map(p => [p.id, p.default])) };
}

/** What is processed: audio regions and the range of them, in ticks. */
export interface ProcessTarget {
  regionIds: string[];
  startTick: number;
  endTick: number;
}

/**
 * The target: with a marquee selection, the audio regions on its rows that it overlaps, over its range; otherwise the
 * selected audio regions, whole. Null when there is nothing to process.
 */
export function processTarget(
  marquee: MarqueeRange | null,
  rows: readonly LaneRow[],
  regions: readonly AudioRegion[],
  selected: ReadonlySet<string>,
  endOf: (region: AudioRegion) => number
): ProcessTarget | null {
  if (marquee && marquee.endTick > marquee.startTick) {
    const tracks = new Set(
      rows.slice(marquee.laneFrom, marquee.laneTo + 1).flatMap(row => (row.kind === 'track' ? [row.track.id] : []))
    );
    const hit = regions.filter(
      r => tracks.has(r.trackId) && r.startTick < marquee.endTick && endOf(r) > marquee.startTick
    );
    if (hit.length > 0) {
      return { regionIds: hit.map(r => r.id), startTick: marquee.startTick, endTick: marquee.endTick };
    }
  }
  const chosen = regions.filter(r => selected.has(r.id));
  if (chosen.length === 0) {
    return null;
  }
  return {
    regionIds: chosen.map(r => r.id),
    startTick: Math.min(...chosen.map(r => r.startTick)),
    endTick: Math.max(...chosen.map(endOf))
  };
}

/** The request: the chain in order with the parameters that differ from their defaults. */
export function processRequest(
  chain: readonly ChainEntry[],
  plugins: readonly PluginInfo[],
  target: ProcessTarget,
  includeTail: boolean
): ProcessRequest {
  const byId = new Map(plugins.map(p => [p.id, p]));
  return {
    regionIds: target.regionIds,
    startTick: Math.round(target.startTick),
    endTick: Math.round(target.endTick),
    includeTail,
    chain: chain.map(entry => {
      const defaults = new Map(byId.get(entry.pluginId)?.parameters.map(p => [p.id, p.default]) ?? []);
      const changed = Object.entries(entry.values).filter(([id, value]) => defaults.get(id) !== value);
      return { pluginId: entry.pluginId, parameters: changed.length ? Object.fromEntries(changed) : null };
    })
  };
}
