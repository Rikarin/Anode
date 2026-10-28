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

function effect(
  id: string,
  name: string,
  category: string,
  parameters: ParameterInfo[],
  extra: Partial<PluginInfo> = {}
): PluginInfo {
  return {
    id,
    name,
    vendor: 'Anode',
    version: '0.4.0',
    format: 'builtin',
    isInstrument: false,
    fileTypes: [],
    parameters,
    category,
    hasSidechain: false,
    hasEditor: false,
    ...extra
  };
}

const Bands = [
  ['low', 'Low Shelf', 80],
  ['peak1', 'Peak 1', 250],
  ['peak2', 'Peak 2', 1_000],
  ['peak3', 'Peak 3', 3_000],
  ['peak4', 'Peak 4', 6_000],
  ['high', 'High Shelf', 12_000]
] as const;

/**
 * The fake engine's effects (docs/05-phase-4.md, workstream D's T1 suite, by category), then third-party ones as the
 * sandboxed scanner would list them: a VST3 with its own editor (like the in-repo test plugin), an Audio Unit, and a
 * VST3 that crashes when playback starts (the fake reports `pluginProblem` for it).
 */
export const FakeEffects: readonly PluginInfo[] = [
  effect('anode.channel-eq', 'Channel EQ', 'EQ', [
    continuous('hpf.frequency', 'HPF Frequency', 20, 20_000, 30, 'Hz'),
    continuous('lpf.frequency', 'LPF Frequency', 20, 20_000, 20_000, 'Hz'),
    ...Bands.flatMap(([id, label, hz]) => [
      continuous(`${id}.frequency`, `${label} Frequency`, 20, 20_000, hz, 'Hz'),
      continuous(`${id}.gain`, `${label} Gain`, -24, 24, 0, 'dB'),
      continuous(`${id}.q`, `${label} Q`, 0.1, 10, 0.71)
    ]),
    continuous('output.gain', 'Gain', -24, 24, 0, 'dB')
  ]),
  effect(
    'anode.compressor',
    'Compressor',
    'Dynamics',
    [
      choice('character', 'Character', ['VCA', 'FET', 'Opto']),
      continuous('threshold', 'Threshold', -60, 0, -18, 'dB'),
      continuous('ratio', 'Ratio', 1, 30, 4, ':1'),
      continuous('knee', 'Knee', 0, 24, 6, 'dB'),
      continuous('attack', 'Attack', 0.0001, 0.2, 0.01, 's'),
      continuous('release', 'Release', 0.005, 5, 0.15, 's'),
      continuous('makeup', 'Makeup', 0, 30, 0, 'dB'),
      choice('autoGain', 'Auto Gain', ['Off', 'On']),
      continuous('sidechain.hpf', 'Sidechain HPF', 20, 2_000, 20, 'Hz')
    ],
    { hasSidechain: true }
  ),
  effect('anode.limiter', 'Limiter', 'Dynamics', [
    continuous('gain', 'Gain', 0, 24, 0, 'dB'),
    continuous('ceiling', 'Ceiling', -12, 0, -1, 'dB'),
    continuous('lookahead', 'Lookahead', 0.001, 0.01, 0.002, 's'),
    continuous('release', 'Release', 0.01, 1, 0.1, 's'),
    choice('truePeak', 'True Peak', ['Off', 'On'], 1)
  ]),
  effect(
    'anode.noise-gate',
    'Noise Gate',
    'Dynamics',
    [
      continuous('threshold', 'Threshold', -96, 0, -50, 'dB'),
      continuous('reduction', 'Reduction', -100, 0, -100, 'dB'),
      continuous('attack', 'Attack', 0.0001, 0.1, 0.001, 's'),
      continuous('hold', 'Hold', 0, 1, 0.05, 's'),
      continuous('release', 'Release', 0.005, 2, 0.1, 's')
    ],
    { hasSidechain: true }
  ),
  effect('anode.deesser', 'DeEsser', 'Dynamics', [
    continuous('frequency', 'Frequency', 2_000, 12_000, 6_000, 'Hz'),
    continuous('threshold', 'Threshold', -60, 0, -24, 'dB'),
    continuous('reduction', 'Max Reduction', 0, 24, 6, 'dB')
  ]),
  effect('anode.stereo-delay', 'Stereo Delay', 'Delay', [
    choice('sync', 'Sync', ['Off', 'On'], 1),
    continuous('left.time', 'Left Time', 0.001, 4, 0.375, 's'),
    continuous('right.time', 'Right Time', 0.001, 4, 0.5, 's'),
    continuous('feedback', 'Feedback', 0, 1, 0.35),
    continuous('mix', 'Mix', 0, 1, 0.25)
  ]),
  effect('anode.tape-delay', 'Tape Delay', 'Delay', [
    continuous('time', 'Time', 0.01, 2, 0.3, 's'),
    continuous('feedback', 'Feedback', 0, 1, 0.4),
    continuous('wow', 'Wow', 0, 1, 0.2),
    continuous('flutter', 'Flutter', 0, 1, 0.1),
    continuous('saturation', 'Saturation', 0, 1, 0.3),
    continuous('mix', 'Mix', 0, 1, 0.3)
  ]),
  effect('anode.reverb', 'Reverb', 'Reverb', [
    choice('room', 'Room', ['Plate', 'Room', 'Hall'], 2),
    continuous('size', 'Size', 0, 1, 0.6),
    continuous('decay', 'Decay', 0.1, 20, 2.4, 's'),
    continuous('damping', 'Damping', 0, 1, 0.4),
    continuous('predelay', 'Predelay', 0, 0.25, 0.02, 's'),
    continuous('mix', 'Mix', 0, 1, 1)
  ]),
  effect('anode.chorus', 'Chorus', 'Modulation', [
    continuous('rate', 'Rate', 0.05, 10, 0.8, 'Hz'),
    continuous('depth', 'Depth', 0, 1, 0.4),
    continuous('mix', 'Mix', 0, 1, 0.5)
  ]),
  effect('anode.phaser', 'Phaser', 'Modulation', [
    continuous('rate', 'Rate', 0.05, 10, 0.5, 'Hz'),
    continuous('depth', 'Depth', 0, 1, 0.6),
    continuous('feedback', 'Feedback', 0, 0.95, 0.4),
    continuous('mix', 'Mix', 0, 1, 0.5)
  ]),
  effect('anode.overdrive', 'Overdrive', 'Distortion', [
    continuous('drive', 'Drive', 0, 1, 0.4),
    continuous('tone', 'Tone', 200, 8_000, 2_000, 'Hz'),
    continuous('output', 'Output', -24, 12, 0, 'dB')
  ]),
  effect('anode.autofilter', 'AutoFilter', 'Filter', [
    choice('type', 'Type', ['Low-pass', 'High-pass', 'Band-pass']),
    continuous('cutoff', 'Cutoff', 20, 20_000, 1_200, 'Hz'),
    continuous('resonance', 'Resonance', 0, 1, 0.3),
    continuous('lfo.rate', 'LFO Rate', 0.05, 20, 2, 'Hz'),
    continuous('lfo.depth', 'LFO Depth', 0, 1, 0.5)
  ]),
  effect('anode.gain', 'Gain', 'Utility', [
    continuous('gain', 'Gain', -96, 24, 0, 'dB'),
    choice('invert', 'Phase Invert', ['Off', 'On']),
    choice('mono', 'Mono', ['Off', 'On'])
  ]),
  effect('anode.loudness-meter', 'Loudness Meter', 'Metering', [continuous('target', 'Target', -36, 0, -14, 'LUFS')]),
  effect(
    'com.anode.test-gain',
    'Test Gain',
    'Utility',
    [continuous('0', 'Gain', -48, 12, 0, 'dB'), continuous('1', 'Tone', -1, 1, 0)],
    { vendor: 'Anode Tests', version: '1.0.0', format: 'vst3', hasEditor: true }
  ),
  effect(
    'com.example.au-delay',
    'AUDelay',
    'Delay',
    [continuous('time', 'Delay Time', 0, 2, 1, 's'), continuous('feedback', 'Feedback', -100, 100, 50, '%')],
    { vendor: 'Example', version: '1.6.0', format: 'au', hasEditor: true }
  ),
  effect('com.example.crashy', 'Crashy', 'Distortion', [continuous('drive', 'Drive', 0, 1, 0.5)], {
    vendor: 'Example',
    version: '0.0.1',
    format: 'vst3'
  })
];

/** The plugin that crashes in the fake engine as soon as it plays. */
export const CrashyPluginId = 'com.example.crashy';

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
