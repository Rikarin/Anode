import type { ChannelStrip, PluginInfo } from '@anode/api-client';
import { groupTargets, targetOf, targetsOf, unknownTarget } from './targets';

const strip = (extra: Partial<ChannelStrip> = {}): ChannelStrip => ({
  volumeDb: 0,
  pan: 0,
  mute: false,
  solo: false,
  inserts: [],
  sends: [],
  output: 'master',
  groups: [],
  vcaId: null,
  ...extra
});

const compressor: PluginInfo = {
  id: 'anode.compressor',
  name: 'Compressor',
  vendor: 'Anode',
  version: '1',
  format: 'builtin',
  isInstrument: false,
  fileTypes: [],
  parameters: [
    { id: 'threshold', name: 'Threshold', min: -60, max: 0, default: -20, unit: 'dB', steps: 0, valueNames: null },
    {
      id: 'character',
      name: 'Character',
      min: 0,
      max: 2,
      default: 0,
      unit: '',
      steps: 2,
      valueNames: ['VCA', 'FET', 'Opto']
    },
    { id: 'attack', name: 'Attack', min: 0.0001, max: 0.1, default: 0.01, unit: 's', steps: 0, valueNames: null }
  ],
  category: 'Dynamics',
  hasSidechain: true,
  hasEditor: false
};
const catalogue = (id: string) => (id === compressor.id ? compressor : undefined);

describe('automation targets', () => {
  const track = {
    kind: 'audio' as const,
    instrument: null,
    strip: strip({
      sends: [{ id: 'snd_1', bus: 3, levelDb: -6, pan: 0, preFader: false }],
      inserts: [
        {
          id: 'ins_1',
          pluginId: 'anode.compressor',
          name: 'Compressor',
          bypassed: false,
          parameters: {},
          sidechain: null
        },
        { id: 'ins_2', pluginId: 'gone', name: 'Gone', bypassed: false, parameters: {}, sidechain: null }
      ]
    })
  };

  it('lists the strip, its sends and its inserts’ parameters', () => {
    const targets = targetsOf(track, catalogue, { inputs: [], outputs: [], buses: [{ number: 3, name: 'Verb' }] });
    expect(targets.map(t => t.target)).toEqual([
      'volume',
      'pan',
      'mute',
      'send:snd_1',
      'insert:ins_1:threshold',
      'insert:ins_1:character',
      'insert:ins_1:attack'
    ]);
    expect(targets[3]?.name).toBe('Send to Verb');
    expect(groupTargets(targets).map(g => g.group)).toEqual(['Main', 'Sends', 'Compressor']);
  });

  it('gives a VCA volume and mute only', () => {
    expect(targetsOf({ ...track, kind: 'vca' }, catalogue).map(t => t.target)).toEqual(['volume', 'mute']);
  });

  it('lists the master’s targets from its strip', () => {
    expect(targetsOf({ strip: strip() }, catalogue).map(t => t.target)).toEqual(['volume', 'pan', 'mute']);
  });

  it('maps volume through the fader’s taper', () => {
    const volume = targetOf(track, 'volume', catalogue);
    expect(volume?.toNormal(6)).toBeCloseTo(1);
    expect(volume?.toNormal(-144)).toBeCloseTo(0);
    expect(volume?.fromNormal(volume.toNormal(-6))).toBeCloseTo(-6, 1);
    expect(volume?.format(-6)).toContain('6.0');
  });

  it('maps pan and mute', () => {
    const pan = targetOf(track, 'pan', catalogue);
    expect(pan?.toNormal(-1)).toBe(0);
    expect(pan?.fromNormal(0.75)).toBe(0.5);
    const mute = targetOf(track, 'mute', catalogue);
    expect(mute?.stepped).toBe(true);
    expect(mute?.fromNormal(0.7)).toBe(1);
    expect(mute?.format(1)).toBe('On');
  });

  it('maps a parameter by its range, taper and names', () => {
    const attack = targetOf(track, 'insert:ins_1:attack', catalogue);
    expect(attack?.toNormal(0.001)).toBeCloseTo(1 / 3); // logarithmic: three decades
    const character = targetOf(track, 'insert:ins_1:character', catalogue);
    expect(character?.stepped).toBe(true);
    expect(character?.format(1)).toBe('FET');
    expect(character?.fromNormal(0.6)).toBe(1);
  });

  it('describes nothing for a gone target, and falls back for drawing', () => {
    expect(targetOf(track, 'insert:ins_9:x', catalogue)).toBeUndefined();
    expect(unknownTarget('x').toNormal(3)).toBe(1);
  });
});
