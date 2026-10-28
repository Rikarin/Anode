import type { AudioRegion, PluginInfo, Track } from '@anode/api-client';
import type { LaneRow } from '../state/lanes';
import { chainEntry, processingEffects, processRequest, processTarget } from './processing';

const plugin = (id: string, extra: Partial<PluginInfo> = {}): PluginInfo => ({
  id,
  name: id,
  vendor: 'Anode',
  version: '1',
  format: 'builtin',
  isInstrument: false,
  fileTypes: [],
  parameters: [{ id: 'gain', name: 'Gain', min: -96, max: 24, default: 0, unit: 'dB', steps: 0 }],
  category: 'Utility',
  hasSidechain: false,
  hasEditor: false,
  ...extra
});

const region = (id: string, trackId: string, startTick: number): AudioRegion =>
  ({ id, trackId, startTick, length: 48_000 }) as AudioRegion;
const row = (id: string): LaneRow => ({ kind: 'track', id, track: { id } as Track, depth: 0 });

describe('selection-based processing', () => {
  it('offers built-in effects only', () => {
    const effects = processingEffects([
      plugin('anode.gain'),
      plugin('anode.synth', { isInstrument: true }),
      plugin('com.x.fx', { format: 'clap' }),
      plugin('anode.loudness-meter', { category: 'Metering' })
    ]);
    expect(effects.map(p => p.id)).toEqual(['anode.gain']);
  });

  it('processes the marquee range of the regions it overlaps', () => {
    const regions = [region('a', 't1', 0), region('b', 't2', 0), region('c', 't1', 10_000)];
    const target = processTarget(
      { startTick: 960, endTick: 1920, laneFrom: 0, laneTo: 0 },
      [row('t1'), row('t2')],
      regions,
      new Set(),
      r => r.startTick + 3840
    );
    expect(target).toEqual({ regionIds: ['a'], startTick: 960, endTick: 1920 });
  });

  it('falls back to the selected regions, whole', () => {
    const regions = [region('a', 't1', 0), region('b', 't2', 3840)];
    expect(processTarget(null, [], regions, new Set(['a', 'b']), r => r.startTick + 3840)).toEqual({
      regionIds: ['a', 'b'],
      startTick: 0,
      endTick: 7680
    });
    expect(processTarget(null, [], regions, new Set(), r => r.startTick)).toBeNull();
  });

  it('sends only the parameters that changed', () => {
    const gain = plugin('anode.gain');
    const eq = plugin('anode.channel-eq');
    const chain = [{ ...chainEntry(gain, 1), values: { gain: 6 } }, chainEntry(eq, 2)];
    expect(processRequest(chain, [gain, eq], { regionIds: ['a'], startTick: 0.4, endTick: 960 }, false)).toEqual({
      regionIds: ['a'],
      startTick: 0,
      endTick: 960,
      includeTail: false,
      chain: [
        { pluginId: 'anode.gain', parameters: { gain: 6 } },
        { pluginId: 'anode.channel-eq', parameters: null }
      ]
    });
  });
});
