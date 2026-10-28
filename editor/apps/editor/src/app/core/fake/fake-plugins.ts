import type { MidiDevices, ParameterInfo, PluginInfo } from '@anode/api-client';

export { clampParameter, defaultParameters } from '../../instruments/parameters';

function continuous(id: string, name: string, min: number, max: number, value: number, unit = ''): ParameterInfo {
  return { id, name, min, max, default: value, unit, steps: 0, valueNames: null };
}

function choice(id: string, name: string, names: string[], value = 0): ParameterInfo {
  return {
    id,
    name,
    min: 0,
    max: names.length - 1,
    default: value,
    unit: '',
    steps: names.length - 1,
    valueNames: names
  };
}

function stepped(id: string, name: string, min: number, max: number, value: number, unit = ''): ParameterInfo {
  return { id, name, min, max, default: value, unit, steps: max - min, valueNames: null };
}

const Waves = ['Saw', 'Square', 'Triangle', 'Sine', 'Noise'];

/**
 * The fake engine's plugin catalogue: Anode's two built-in instruments with parameters like workstream D's
 * (docs/03-phase-2.md), then a CLAP instrument like the engine's test plugin (parameter ids are CLAP's decimal
 * `clap_param_info.id`s). Built-ins first, as the host sends them.
 */
export const FakeCatalogue: readonly PluginInfo[] = [
  {
    id: 'anode.synth',
    name: 'Anode Synth',
    vendor: 'Anode',
    version: '0.2.0',
    format: 'builtin',
    isInstrument: true,
    fileTypes: [],
    parameters: [
      choice('osc1.wave', 'Osc 1 Wave', Waves),
      stepped('osc1.octave', 'Osc 1 Octave', -2, 2, 0, 'oct'),
      continuous('osc1.pulseWidth', 'Osc 1 Pulse Width', 0.05, 0.95, 0.5),
      choice('osc2.wave', 'Osc 2 Wave', Waves, 1),
      stepped('osc2.octave', 'Osc 2 Octave', -2, 2, -1, 'oct'),
      continuous('osc2.fine', 'Osc 2 Fine', -100, 100, 7, 'ct'),
      continuous('osc.mix', 'Osc Mix', 0, 1, 0.5),
      choice('filter.type', 'Filter Type', ['Low-pass 24', 'High-pass', 'Band-pass']),
      continuous('filter.cutoff', 'Cutoff', 20, 20_000, 2_400, 'Hz'),
      continuous('filter.resonance', 'Resonance', 0, 1, 0.2),
      continuous('filter.envAmount', 'Filter Env', -1, 1, 0.35),
      continuous('filter.keyTrack', 'Key Track', 0, 1, 0.5),
      continuous('amp.attack', 'Attack', 0.001, 10, 0.005, 's'),
      continuous('amp.decay', 'Decay', 0.001, 10, 0.3, 's'),
      continuous('amp.sustain', 'Sustain', 0, 1, 0.7),
      continuous('amp.release', 'Release', 0.001, 10, 0.4, 's'),
      continuous('filterEnv.attack', 'Filter Attack', 0.001, 10, 0.01, 's'),
      continuous('filterEnv.decay', 'Filter Decay', 0.001, 10, 0.5, 's'),
      continuous('filterEnv.sustain', 'Filter Sustain', 0, 1, 0.3),
      continuous('filterEnv.release', 'Filter Release', 0.001, 10, 0.5, 's'),
      choice('lfo.shape', 'LFO Shape', ['Sine', 'Triangle', 'Saw', 'Square', 'S&H']),
      continuous('lfo.rate', 'LFO Rate', 0.05, 20, 4, 'Hz'),
      choice('lfo.target', 'LFO Target', ['Pitch', 'Filter', 'Amp'], 1),
      continuous('lfo.depth', 'LFO Depth', 0, 1, 0),
      continuous('glide', 'Glide', 0, 2, 0, 's'),
      stepped('unison.voices', 'Unison', 1, 4, 1),
      continuous('unison.detune', 'Unison Detune', 0, 100, 12, 'ct'),
      continuous('velocity', 'Velocity Sens', 0, 1, 0.6),
      stepped('bendRange', 'Bend Range', 0, 24, 2, 'st'),
      continuous('volume', 'Volume', -60, 6, -6, 'dB')
    ],
    category: 'Instrument',
    hasSidechain: false,
    hasEditor: false
  },
  {
    id: 'anode.sampler',
    name: 'Anode Sampler',
    vendor: 'Anode',
    version: '0.2.0',
    format: 'builtin',
    isInstrument: true,
    fileTypes: ['.sfz', '.wav'],
    parameters: [
      continuous('volume', 'Volume', -60, 6, 0, 'dB'),
      continuous('tune', 'Tune', -24, 24, 0, 'st'),
      continuous('attack', 'Attack', 0, 10, 0.002, 's'),
      continuous('release', 'Release', 0, 10, 0.3, 's'),
      continuous('velocity', 'Velocity Sens', 0, 1, 1)
    ],
    category: 'Instrument',
    hasSidechain: false,
    hasEditor: false
  },
  {
    id: 'org.anode.test-sine',
    name: 'Test Sine',
    vendor: 'Anode Tests',
    version: '1.0.0',
    format: 'clap',
    isInstrument: true,
    fileTypes: [],
    parameters: [continuous('0', 'Gain', -48, 0, -12, 'dB'), continuous('1', 'Detune', -50, 50, 0, 'ct')],
    category: 'Instrument',
    hasSidechain: false,
    hasEditor: true
  }
];

/** The fake engine's MIDI ports. */
export const FakeMidiDevices: MidiDevices = {
  inputs: [
    { id: 'in:keystation-49', name: 'Keystation 49 MK3' },
    { id: 'in:iac-1', name: 'IAC Driver Bus 1' }
  ],
  outputs: [
    { id: 'out:iac-1', name: 'IAC Driver Bus 1' },
    { id: 'out:rev2', name: 'Prophet Rev2' }
  ]
};
