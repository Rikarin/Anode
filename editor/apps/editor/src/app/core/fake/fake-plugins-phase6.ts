import type { ParameterInfo, PluginInfo } from '@anode/api-client';

/**
 * The fake engine's Phase 6 plugins (docs/07-phase-6.md): the eight MIDI FX (category `MIDI Effect`), the T2
 * instruments and effects, and macro hints for Smart Controls' automatic mapping. Ids and parameter ids are the
 * editor's guesses where workstreams D and E had not published theirs; the custom panels match them by pattern
 * (plugin-panels/panel-patterns.ts) and fall back to the generic editor when they do not.
 */

function p(
  id: string,
  name: string,
  min: number,
  max: number,
  value: number,
  unit = '',
  macro: number | null = null
): ParameterInfo {
  return { id, name, min, max, default: value, unit, steps: 0, valueNames: null, macro };
}

function n(id: string, name: string, min: number, max: number, value: number, unit = '', macro: number | null = null) {
  return { ...p(id, name, min, max, value, unit, macro), steps: max - min };
}

function c(id: string, name: string, names: readonly string[], value = 0, macro: number | null = null): ParameterInfo {
  return {
    id,
    name,
    min: 0,
    max: names.length - 1,
    default: value,
    unit: '',
    steps: names.length - 1,
    valueNames: [...names],
    macro
  };
}

function plugin(
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
    version: '0.6.0',
    format: 'builtin',
    isInstrument: category === 'Instrument',
    fileTypes: [],
    parameters,
    category,
    hasSidechain: false,
    hasEditor: false,
    ...extra
  };
}

/** The Arpeggiator's rates, as note values. */
export const ArpRates = ['1/4', '1/8', '1/8T', '1/16', '1/16T', '1/32'];
export const ArpModes = ['Up', 'Down', 'Up/Down', 'Outside In', 'Random', 'As Played'];
const Scales = ['Chromatic', 'Major', 'Minor', 'Dorian', 'Mixolydian', 'Pentatonic'];

export const MidiEffectCategory = 'MIDI Effect';

export const FakeMidiEffects: readonly PluginInfo[] = [
  plugin('anode.arpeggiator', 'Arpeggiator', MidiEffectCategory, [
    c('rate', 'Rate', ArpRates, 3, 1),
    c('mode', 'Mode', ArpModes, 0, 2),
    n('octaves', 'Octaves', 1, 4, 1, 'oct', 3),
    p('gate', 'Gate', 0.05, 1, 0.5, '', 4),
    p('swing', 'Swing', 0, 1, 0),
    c('latch', 'Latch', ['Off', 'On'])
  ]),
  plugin('anode.chord-trigger', 'Chord Trigger', MidiEffectCategory, [
    c('chord', 'Chord', ['Major', 'Minor', 'Sus4', 'Major 7', 'Minor 7', 'Dominant 7', 'Power'], 0, 1),
    n('inversion', 'Inversion', 0, 3, 0),
    n('spread', 'Spread', 0, 2, 0, 'oct')
  ]),
  plugin('anode.modifier', 'Modifier', MidiEffectCategory, [
    n('cc', 'Controller', 0, 127, 1),
    n('remap', 'Remap To', 0, 127, 1),
    p('scale', 'Scale', -2, 2, 1),
    n('add', 'Add', -127, 127, 0)
  ]),
  plugin('anode.modulator', 'Modulator', MidiEffectCategory, [
    c('shape', 'Shape', ['Sine', 'Triangle', 'Saw', 'Square', 'Random']),
    c('rate', 'Rate', ['1/1', '1/2', '1/4', '1/8', '1/16'], 2),
    n('cc', 'Controller', 0, 127, 1),
    p('depth', 'Depth', 0, 1, 0.5, '', 1)
  ]),
  plugin('anode.note-repeater', 'Note Repeater', MidiEffectCategory, [
    c('rate', 'Rate', ['1/4', '1/8', '1/16', '1/32'], 2, 1),
    n('repeats', 'Repeats', 1, 16, 3),
    p('feedback', 'Velocity Feedback', 0, 1, 0.7),
    n('transpose', 'Transpose', -24, 24, 0, 'st')
  ]),
  plugin('anode.randomizer', 'Randomizer', MidiEffectCategory, [
    p('probability', 'Probability', 0, 1, 0.5, '', 1),
    n('pitch', 'Pitch Range', 0, 24, 0, 'st'),
    n('velocity', 'Velocity Range', 0, 127, 20)
  ]),
  plugin('anode.transposer', 'Transposer', MidiEffectCategory, [
    n('transpose', 'Transpose', -24, 24, 0, 'st', 1),
    n('root', 'Root', 0, 11, 0),
    c('scale', 'Scale', Scales)
  ]),
  plugin('anode.velocity-processor', 'Velocity Processor', MidiEffectCategory, [
    p('scale', 'Scale', 0, 2, 1, '', 1),
    n('offset', 'Offset', -127, 127, 0),
    n('min', 'Minimum', 1, 127, 1),
    n('max', 'Maximum', 1, 127, 127)
  ])
];

const DrumModels = ['Kick', 'Snare', 'Clap', 'Hat', 'Tom', 'Cymbal', 'Percussion'];
const QuickModes = ['One Shot', 'Classic', 'Slice', 'Recorder'];
const Waves = ['Sine', 'Saw', 'Square', 'Triangle'];

const operators = (count: number) =>
  Array.from({ length: count }, (_, i) => {
    const op = i + 1;
    return [
      p(`op${op}.ratio`, `Op ${op} Ratio`, 0.5, 16, op === 1 ? 1 : op, ''),
      p(`op${op}.level`, `Op ${op} Level`, 0, 1, op === 1 ? 1 : 0.5, '', op === 2 ? 3 : null),
      p(`op${op}.attack`, `Op ${op} Attack`, 0.001, 5, 0.005, 's'),
      p(`op${op}.decay`, `Op ${op} Decay`, 0.001, 5, 0.4, 's'),
      p(`op${op}.sustain`, `Op ${op} Sustain`, 0, 1, 0.6),
      p(`op${op}.release`, `Op ${op} Release`, 0.001, 5, 0.3, 's')
    ];
  }).flat();

const drawbars = ['16′', '5⅓′', '8′', '4′', '2⅔′', '2′', '1⅗′', '1⅓′', '1′'].map((foot, i) =>
  n(`drawbar${i + 1}`, `Drawbar ${foot}`, 0, 8, i < 3 ? 8 : 0, '', i < 4 ? i + 1 : null)
);

export const FakeT2Instruments: readonly PluginInfo[] = [
  plugin('anode.drum-synth', 'Drum Synth', 'Instrument', [
    c('model', 'Model', DrumModels, 0),
    p('tune', 'Tune', -24, 24, 0, 'st', 1),
    p('decay', 'Decay', 0.01, 2, 0.4, 's', 2),
    p('tone', 'Tone', 0, 1, 0.5, '', 3),
    p('snap', 'Snap', 0, 1, 0.3, '', 4),
    p('drive', 'Drive', 0, 1, 0),
    p('volume', 'Volume', -60, 6, 0, 'dB')
  ]),
  plugin(
    'anode.quick-sampler',
    'Quick Sampler',
    'Instrument',
    [
      c('mode', 'Mode', QuickModes, 1),
      n('root', 'Root Key', 0, 127, 60),
      p('start', 'Start', 0, 1, 0),
      p('end', 'End', 0, 1, 1),
      p('pitch', 'Pitch', -24, 24, 0, 'st', 1),
      p('cutoff', 'Cutoff', 20, 20_000, 20_000, 'Hz', 2),
      p('attack', 'Attack', 0, 5, 0.002, 's', 3),
      p('release', 'Release', 0, 5, 0.2, 's', 4),
      p('volume', 'Volume', -60, 6, 0, 'dB')
    ],
    { fileTypes: ['.wav'] }
  ),
  plugin('anode.fm-synth', 'FM Synth', 'Instrument', [
    n('algorithm', 'Algorithm', 1, 8, 1, '', 1),
    p('feedback', 'Feedback', 0, 1, 0, '', 2),
    ...operators(4),
    p('brightness', 'Brightness', 0, 1, 0.5, '', 4),
    p('volume', 'Volume', -60, 6, -6, 'dB')
  ]),
  plugin('anode.drawbar-organ', 'Drawbar Organ', 'Instrument', [
    ...drawbars,
    c('percussion', 'Percussion', ['Off', '2nd', '3rd']),
    p('click', 'Key Click', 0, 1, 0.3),
    c('scanner', 'Chorus/Vibrato', ['Off', 'V1', 'V2', 'V3', 'C1', 'C2', 'C3']),
    c('rotary.speed', 'Rotary Speed', ['Brake', 'Slow', 'Fast'], 1, 5),
    p('rotary.mix', 'Rotary Mix', 0, 1, 0.8),
    p('drive', 'Drive', 0, 1, 0.2, '', 6),
    p('volume', 'Volume', -60, 6, -6, 'dB')
  ]),
  plugin('anode.electric-piano', 'Electric Piano', 'Instrument', [
    c('model', 'Model', ['Tines', 'Reeds']),
    p('bell', 'Bell', 0, 1, 0.5, '', 1),
    p('bark', 'Bark', 0, 1, 0.3, '', 2),
    p('decay', 'Decay', 0.5, 10, 4, 's', 3),
    p('tremolo.rate', 'Tremolo Rate', 0.1, 10, 4, 'Hz'),
    p('tremolo.depth', 'Tremolo Depth', 0, 1, 0, '', 4),
    p('volume', 'Volume', -60, 6, -6, 'dB')
  ]),
  plugin('anode.wavetable-synth', 'Wavetable Synth', 'Instrument', [
    c('osc1.table', 'Osc 1 Table', ['Basic', 'Vocal', 'Digital', 'Harmonic']),
    p('osc1.position', 'Osc 1 Position', 0, 1, 0, '', 1),
    c('osc2.table', 'Osc 2 Table', ['Basic', 'Vocal', 'Digital', 'Harmonic'], 1),
    p('osc2.position', 'Osc 2 Position', 0, 1, 0.5, '', 2),
    c('sub.wave', 'Sub Wave', Waves),
    p('sub.level', 'Sub Level', 0, 1, 0),
    p('filter.cutoff', 'Cutoff', 20, 20_000, 3_000, 'Hz', 3),
    p('filter.resonance', 'Resonance', 0, 1, 0.2, '', 4),
    p('env1.attack', 'Env 1 Attack', 0.001, 10, 0.005, 's', 5),
    p('env1.release', 'Env 1 Release', 0.001, 10, 0.4, 's', 6),
    p('lfo1.rate', 'LFO 1 Rate', 0.05, 20, 2, 'Hz', 7),
    p('lfo1.depth', 'LFO 1 Depth', 0, 1, 0, '', 8),
    p('volume', 'Volume', -60, 6, -6, 'dB')
  ])
];

const multipressorBands = Array.from({ length: 4 }, (_, i) => {
  const b = i + 1;
  return [
    p(`band${b}.threshold`, `Band ${b} Threshold`, -60, 0, -20, 'dB'),
    p(`band${b}.ratio`, `Band ${b} Ratio`, 1, 20, 2, ':1'),
    p(`band${b}.gain`, `Band ${b} Gain`, -24, 24, 0, 'dB'),
    c(`band${b}.bypass`, `Band ${b} Bypass`, ['Off', 'On'])
  ];
}).flat();

const delayTaps = Array.from({ length: 4 }, (_, i) => {
  const t = i + 1;
  return [
    p(`tap${t}.time`, `Tap ${t} Time`, 0, 10, 0.125 * t, 's'),
    p(`tap${t}.level`, `Tap ${t} Level`, -60, 0, -6 * t, 'dB'),
    p(`tap${t}.pan`, `Tap ${t} Pan`, -1, 1, t % 2 ? -0.5 : 0.5),
    p(`tap${t}.cutoff`, `Tap ${t} Cutoff`, 20, 20_000, 8_000, 'Hz')
  ];
}).flat();

export const RemixFxId = 'anode.remix-fx';

export const FakeT2Effects: readonly PluginInfo[] = [
  plugin(
    'anode.convolution-reverb',
    'Convolution Reverb',
    'Reverb',
    [
      p('predelay', 'Pre-Delay', 0, 0.5, 0.01, 's', 1),
      p('length', 'Length', 0.1, 1, 1, '', 2),
      p('lowcut', 'Low Cut', 20, 2_000, 20, 'Hz'),
      p('highcut', 'High Cut', 1_000, 20_000, 20_000, 'Hz', 3),
      p('mix', 'Mix', 0, 1, 0.3, '', 4)
    ],
    { fileTypes: ['.wav'] }
  ),
  plugin('anode.linear-phase-eq', 'Linear Phase EQ', 'EQ', [
    p('low.gain', 'Low Gain', -24, 24, 0, 'dB', 1),
    p('mid.gain', 'Mid Gain', -24, 24, 0, 'dB', 2),
    p('high.gain', 'High Gain', -24, 24, 0, 'dB', 3)
  ]),
  plugin('anode.multipressor', 'Multipressor', 'Dynamics', [
    p('crossover1', 'Crossover 1', 20, 20_000, 150, 'Hz'),
    p('crossover2', 'Crossover 2', 20, 20_000, 1_500, 'Hz'),
    p('crossover3', 'Crossover 3', 20, 20_000, 6_000, 'Hz'),
    ...multipressorBands,
    p('output', 'Output', -24, 24, 0, 'dB', 1)
  ]),
  plugin('anode.enveloper', 'Enveloper', 'Dynamics', [
    p('attack', 'Attack Gain', -100, 100, 0, '%', 1),
    p('release', 'Release Gain', -100, 100, 0, '%', 2)
  ]),
  plugin('anode.exciter', 'Exciter', 'Distortion', [
    p('frequency', 'Frequency', 1_000, 12_000, 4_000, 'Hz', 1),
    p('amount', 'Harmonics', 0, 1, 0.3, '', 2)
  ]),
  plugin('anode.subbass', 'SubBass', 'Filter', [
    p('frequency', 'Frequency', 30, 200, 60, 'Hz', 1),
    p('mix', 'Mix', 0, 1, 0.5, '', 2)
  ]),
  plugin('anode.delay-designer', 'Delay Designer', 'Delay', [
    n('taps', 'Taps', 1, 26, 4),
    c('sync', 'Sync', ['Off', 'On'], 1),
    ...delayTaps,
    p('feedback', 'Feedback', 0, 1, 0.2, '', 1),
    p('mix', 'Mix', 0, 1, 0.3, '', 2)
  ]),
  plugin('anode.pitch-shifter', 'Pitch Shifter', 'Pitch', [
    p('semitones', 'Semitones', -24, 24, 0, 'st', 1),
    p('cents', 'Cents', -100, 100, 0, 'ct'),
    p('mix', 'Mix', 0, 1, 1, '', 2)
  ]),
  plugin('anode.pitch-correction', 'Pitch Correction', 'Pitch', [
    n('root', 'Root', 0, 11, 0),
    c('scale', 'Scale', Scales, 1),
    p('response', 'Response', 0, 1, 0.3, '', 1),
    p('detune', 'Detune', -100, 100, 0, 'ct')
  ]),
  plugin('anode.ensemble', 'Ensemble', 'Modulation', [
    n('voices', 'Voices', 2, 8, 3),
    p('rate', 'Rate', 0.05, 10, 0.5, 'Hz', 1),
    p('depth', 'Depth', 0, 1, 0.5, '', 2),
    p('mix', 'Mix', 0, 1, 0.5, '', 3)
  ]),
  plugin('anode.rotor-cabinet', 'Rotor Cabinet', 'Modulation', [
    c('speed', 'Speed', ['Brake', 'Slow', 'Fast'], 1, 1),
    p('mix', 'Mix', 0, 1, 1, '', 2)
  ]),
  plugin('anode.phat-fx', 'Phat FX', 'Distortion', [
    p('drive', 'Drive', 0, 1, 0.3, '', 1),
    p('cutoff', 'Cutoff', 20, 20_000, 8_000, 'Hz', 2),
    p('mix', 'Mix', 0, 1, 1, '', 3)
  ]),
  plugin('anode.step-fx', 'Step FX', 'Filter', [
    c('rate', 'Rate', ['1/8', '1/16', '1/32'], 1, 1),
    p('depth', 'Depth', 0, 1, 0.5, '', 2),
    p('mix', 'Mix', 0, 1, 1)
  ]),
  plugin('anode.beat-breaker', 'Beat Breaker', 'Delay', [
    n('pattern', 'Pattern', 1, 16, 1, '', 1),
    p('mix', 'Mix', 0, 1, 1, '', 2)
  ]),
  plugin(RemixFxId, 'Remix FX', 'Filter', [
    p('xy1.x', 'Pad 1 X', 0, 1, 0.5, '', 1),
    p('xy1.y', 'Pad 1 Y', 0, 1, 0.5, '', 2),
    c('xy1.mode', 'Pad 1 Effect', ['Filter', 'Wobble', 'Orbit']),
    c('xy1.on', 'Pad 1 On', ['Off', 'On']),
    p('xy2.x', 'Pad 2 X', 0, 1, 0.5, '', 3),
    p('xy2.y', 'Pad 2 Y', 0, 1, 0.5, '', 4),
    c('xy2.mode', 'Pad 2 Effect', ['Repeater', 'Reverse', 'Scratch', 'Gater', 'Downsampler', 'Tape Stop']),
    c('xy2.on', 'Pad 2 On', ['Off', 'On'])
  ])
];

/** Macro hints for the T1 built-ins (workstreams D and E add them to every built-in), by plugin then parameter. */
export const T1Macros: Readonly<Record<string, readonly string[]>> = {
  'anode.synth': [
    'filter.cutoff',
    'filter.resonance',
    'filter.envAmount',
    'amp.attack',
    'amp.release',
    'lfo.rate',
    'lfo.depth',
    'glide'
  ],
  'anode.sampler': ['tune', 'attack', 'release', 'velocity'],
  'anode.channel-eq': ['low.gain', 'peak2.gain', 'high.gain'],
  'anode.compressor': ['threshold', 'ratio', 'makeup'],
  'anode.reverb': ['size', 'mix'],
  'anode.stereo-delay': ['feedback', 'mix'],
  'anode.chorus': ['rate', 'mix'],
  'anode.overdrive': ['drive', 'tone']
};

/** A plugin with its T1 macro hints applied (in the order listed). */
export function withMacros(plugin: PluginInfo): PluginInfo {
  const hints = T1Macros[plugin.id];
  if (!hints) {
    return plugin;
  }
  return {
    ...plugin,
    parameters: plugin.parameters.map(parameter => {
      const order = hints.indexOf(parameter.id);
      return order >= 0 ? { ...parameter, macro: order + 1 } : parameter;
    })
  };
}
