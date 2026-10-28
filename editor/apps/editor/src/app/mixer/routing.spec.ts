import type { Send, Track } from '@anode/api-client';
import { effectMenu, pluginLabel } from './plugin-menu';
import {
  busChoices,
  busName,
  lowestFreeBus,
  outputPairName,
  parseRoute,
  routeName,
  signalFlow,
  wouldLoop
} from './routing';

function track(id: string, extra: Partial<Track> = {}, output = 'master', sends: number[] = []): Track {
  return {
    id,
    name: id,
    kind: 'audio',
    color: '#0090ff',
    input: { channel: 1, stereo: false },
    recordArmed: false,
    inputMonitoring: false,
    strip: {
      volumeDb: 0,
      pan: 0,
      mute: false,
      solo: false,
      inserts: [],
      sends: sends.map((bus, i): Send => ({ id: `${id}_s${i}`, bus, levelDb: 0, pan: 0, preFader: false })),
      output,
      groups: [],
      vcaId: null
    },
    instrument: null,
    midiInput: { device: null, channel: 0 },
    midiOutput: null,
    parentId: null,
    collapsed: false,
    notes: '',
    inputBus: 0,
    automationMode: 'read',
    frozenFileId: null,
    flexOn: false,
    flexMode: 'polyphonic',
    alternatives: [],
    alternativeId: null,
    ...extra
  };
}

const aux = (id: string, inputBus: number, output = 'master', sends: number[] = []) =>
  track(id, { kind: 'aux', inputBus }, output, sends);

describe('routing', () => {
  it('reads output routes', () => {
    expect(parseRoute('master')).toEqual({ kind: 'master' });
    expect(parseRoute('bus:12')).toEqual({ kind: 'bus', bus: 12 });
    expect(parseRoute('output:2')).toEqual({ kind: 'output', pair: 2 });
    expect(parseRoute('nowhere')).toBeNull();
  });

  it('refuses a route from an aux back to its own bus, directly or through other aux tracks', () => {
    const tracks = [
      track('a', {}, 'master', [1]),
      aux('verb', 1, 'bus:2'),
      aux('delay', 2, 'master', [3]),
      aux('x', 3)
    ];
    // Directly: the reverb (on bus 1) to bus 1.
    expect(wouldLoop(tracks, 'verb', 1)).toBe(true);
    // Indirectly: the delay (bus 2) sends to bus 3, whose aux would feed bus 1 → reverb → bus 2.
    expect(wouldLoop(tracks, 'x', 1)).toBe(true);
    expect(wouldLoop(tracks, 'x', 2)).toBe(true);
    expect(wouldLoop(tracks, 'x', 4)).toBe(false);
    // Tracks fed by no bus never loop.
    expect(wouldLoop(tracks, 'a', 1)).toBe(false);
  });

  it('offers every bus with what is used and what would loop (the pickers disable those)', () => {
    const tracks = [track('a', {}, 'bus:3', [1]), aux('verb', 1, 'bus:2'), aux('sub', 2)];
    const choices = busChoices(tracks, 'sub', 4);
    expect(choices).toEqual([
      { bus: 1, used: true, loops: true },
      { bus: 2, used: true, loops: true },
      { bus: 3, used: true, loops: false },
      { bus: 4, used: false, loops: false }
    ]);
    expect(lowestFreeBus(tracks)).toBe(4);
  });

  it('names buses, outputs and routes by their I/O labels', () => {
    const labels = { inputs: [], outputs: [{ number: 1, name: 'Main' }], buses: [{ number: 1, name: 'Reverb' }] };
    expect(busName(1, labels)).toBe('Reverb');
    expect(busName(2, labels)).toBe('Bus 2');
    expect(outputPairName(1, labels)).toBe('Main');
    expect(outputPairName(2, labels)).toBe('Output 3-4');
    expect(routeName('master', labels)).toBe('Stereo Out');
    expect(routeName('bus:1', labels)).toBe('Reverb');
    expect(routeName('output:2', labels)).toBe('Output 3-4');
  });

  it('follows a strip’s signal through the aux tracks it reaches to the master', () => {
    const tracks = [
      track('a', {}, 'bus:5', [1]),
      aux('verb', 1, 'output:1'),
      aux('group', 5, 'master'),
      track('b'),
      track('vca', { kind: 'vca' })
    ];
    expect([...signalFlow(tracks, 'a')].sort()).toEqual(['a', 'group', 'master', 'verb']);
    expect([...signalFlow(tracks, 'b')].sort()).toEqual(['b', 'master']);
    expect([...signalFlow(tracks, 'vca')]).toEqual(['vca']);
    expect(signalFlow(tracks, null).size).toBe(0);
  });
});

describe('plugin menu', () => {
  const plugin = (
    id: string,
    category: string,
    format: 'builtin' | 'vst3' | 'au' | 'clap' = 'builtin',
    isInstrument = false
  ) => ({
    id,
    name: id,
    vendor: 'V',
    version: '1',
    format,
    isInstrument,
    fileTypes: [],
    parameters: [],
    category,
    hasSidechain: false,
    hasEditor: false
  });

  it('groups effects by category in Logic’s order, built-in first, instruments left out', () => {
    const menu = effectMenu([
      plugin('Zeta', 'Reverb', 'vst3'),
      plugin('Alpha', 'Reverb'),
      plugin('Comp', 'Dynamics'),
      plugin('EQ', 'EQ'),
      plugin('Weird', 'Spectral'),
      plugin('Synth', 'Instrument', 'builtin', true)
    ]);
    expect(menu.map(g => g.category)).toEqual(['EQ', 'Dynamics', 'Reverb', 'Spectral']);
    expect(menu[2]?.plugins.map(p => p.id)).toEqual(['Alpha', 'Zeta']);
  });

  it('labels third-party plugins with their format', () => {
    expect(pluginLabel({ name: 'Gain', format: 'builtin' })).toBe('Gain');
    expect(pluginLabel({ name: 'Test Gain', format: 'vst3' })).toBe('Test Gain (VST3)');
    expect(pluginLabel({ name: 'Delay', format: 'au' })).toBe('Delay (AU)');
  });
});
