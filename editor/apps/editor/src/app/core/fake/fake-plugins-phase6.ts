import type { ParameterInfo, PluginInfo } from '@anode/api-client';
import { FakeEffects } from './fake-plugins';

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
  ]),
  // Phase 7, workstream D: Scripter's parameters come from its script (Insert.runtimeParameters).
  plugin('anode.scripter', 'Scripter', MidiEffectCategory, [])
];

const DrumModels = ['Kick', 'Snare', 'Clap', 'Hat', 'Tom', 'Cymbal', 'Percussion'];
const QuickModes = ['One Shot', 'Classic', 'Slice', 'Recorder'];
const Waves = ['Sine', 'Saw', 'Square', 'Triangle'];

/** FM operators as workstream E names them: `op{n}{Ratio,Fine,Level,Attack,Decay,Sustain,Release,Velocity}`. */
const operators = (count: number) =>
  Array.from({ length: count }, (_, i) => {
    const op = i + 1;
    return [
      p(`op${op}Ratio`, `Op ${op} Ratio`, 0.5, 16, op === 1 ? 1 : op, ''),
      p(`op${op}Fine`, `Op ${op} Fine`, -100, 100, 0, 'ct'),
      p(`op${op}Level`, `Op ${op} Level`, 0, 1, op === 1 ? 1 : 0.5, '', op === 2 ? 3 : null),
      p(`op${op}Attack`, `Op ${op} Attack`, 0.001, 5, 0.005, 's'),
      p(`op${op}Decay`, `Op ${op} Decay`, 0.001, 5, 0.4, 's'),
      p(`op${op}Sustain`, `Op ${op} Sustain`, 0, 1, 0.6),
      p(`op${op}Release`, `Op ${op} Release`, 0.001, 5, 0.3, 's'),
      p(`op${op}Velocity`, `Op ${op} Velocity`, 0, 1, 0.5)
    ];
  }).flat();

/** The FM algorithms as workstream E's value names show them (carrier 1). */
const Algorithms = [
  '1: 4→3→2→1',
  '2: (4+3)→2→1',
  '3: (4→3)+2→1',
  '4: 4→(3+2)→1',
  '5: (4→3)+(2→1)',
  '6: 4→(3,2,1)',
  '7: (4→3)+2+1',
  '8: 4+3+2+1'
];

/** Organ drawbars `drawbar1`…`drawbar9`, 16′ … 1′, 0…8 in 8 steps (workstream E's OrganParameters). */
const drawbars = ['16′', '5⅓′', '8′', '4′', '2⅔′', '2′', '1⅗′', '1⅓′', '1′'].map((foot, i) =>
  n(`drawbar${i + 1}`, `Drawbar ${foot}`, 0, 8, i < 3 ? 8 : 0, '', i < 4 ? i + 1 : null)
);

const envelopes = [1, 2, 3].flatMap(e => [
  p(`env${e}Attack`, `Env ${e} Attack`, 0.001, 10, 0.005, 's', e === 1 ? 5 : null),
  p(`env${e}Decay`, `Env ${e} Decay`, 0.001, 10, 0.3, 's'),
  p(`env${e}Sustain`, `Env ${e} Sustain`, 0, 1, 0.7),
  p(`env${e}Release`, `Env ${e} Release`, 0.001, 10, 0.4, 's', e === 1 ? 6 : null)
]);

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
  plugin('anode.fm', 'FM Synth', 'Instrument', [
    c('algorithm', 'Algorithm', Algorithms, 0, 1),
    p('feedback', 'Feedback', 0, 1, 0, '', 2),
    ...operators(4),
    p('volume', 'Volume', -60, 6, -6, 'dB')
  ]),
  plugin('anode.organ', 'Drawbar Organ', 'Instrument', [
    ...drawbars,
    c('percussion', 'Percussion', ['Off', 'On']),
    c('percussionHarmonic', 'Percussion Harmonic', ['2nd', '3rd']),
    p('keyClick', 'Key Click', 0, 1, 0.3),
    c('vibratoChorus', 'Vibrato/Chorus', ['Off', 'V1', 'V2', 'V3', 'C1', 'C2', 'C3']),
    c('rotary', 'Rotary', ['Off', 'On'], 1),
    c('rotarySpeed', 'Rotary Speed', ['Slow', 'Fast'], 0, 5),
    p('rotaryDrive', 'Rotary Drive', 0, 1, 0.2, '', 6),
    p('rotaryBalance', 'Rotary Balance', 0, 1, 0.5)
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
  plugin('anode.wavetable', 'Wavetable Synth', 'Instrument', [
    ...[1, 2].flatMap(o => [
      c(`osc${o}Table`, `Osc ${o} Table`, ['Basic', 'Vocal', 'Digital', 'Harmonic'], o - 1),
      p(`osc${o}Position`, `Osc ${o} Position`, 0, 1, 0, '', o),
      n(`osc${o}Octave`, `Osc ${o} Octave`, -3, 3, 0, 'oct'),
      n(`osc${o}Semitone`, `Osc ${o} Semitone`, -12, 12, 0, 'st'),
      p(`osc${o}Fine`, `Osc ${o} Fine`, -100, 100, 0, 'ct'),
      p(`osc${o}Level`, `Osc ${o} Level`, 0, 1, 0.8)
    ]),
    ...envelopes,
    ...[1, 2].flatMap(l => [
      c(`lfo${l}Shape`, `LFO ${l} Shape`, Waves),
      p(`lfo${l}Rate`, `LFO ${l} Rate`, 0.05, 20, 2, 'Hz', l === 1 ? 7 : null),
      c(`lfo${l}Sync`, `LFO ${l} Sync`, ['Off', 'On'])
    ]),
    ...[1, 2, 3, 4, 5, 6].flatMap(m => [
      c(`mod${m}Source`, `Mod ${m} Source`, ['Off', 'Env 2', 'Env 3', 'LFO 1', 'LFO 2', 'Velocity', 'Mod Wheel']),
      c(`mod${m}Destination`, `Mod ${m} Destination`, [
        'Off',
        'Osc 1 Position',
        'Osc 2 Position',
        'Cutoff',
        'Pitch',
        'Level'
      ]),
      p(`mod${m}Amount`, `Mod ${m} Amount`, -1, 1, 0)
    ])
  ])
];

/** Multipressor bands as workstream D names them: `band{1-4}{On,Threshold,Ratio,…,Gain}`, low to high. */
const multipressorBands = Array.from({ length: 4 }, (_, i) => {
  const b = i + 1;
  return [
    c(`band${b}On`, `Band ${b} On`, ['Off', 'On'], 1),
    p(`band${b}Threshold`, `Band ${b} Threshold`, -60, 0, -20, 'dB'),
    p(`band${b}Ratio`, `Band ${b} Ratio`, 1, 20, 2, ':1'),
    p(`band${b}ExpThreshold`, `Band ${b} Expander Threshold`, -100, 0, -60, 'dB'),
    p(`band${b}ExpRatio`, `Band ${b} Expander Ratio`, 1, 10, 1, ':1'),
    p(`band${b}Reduction`, `Band ${b} Reduction`, -100, 0, -20, 'dB'),
    p(`band${b}Attack`, `Band ${b} Attack`, 0.0001, 0.2, 0.01, 's'),
    p(`band${b}Release`, `Band ${b} Release`, 0.005, 5, 0.15, 's'),
    p(`band${b}Gain`, `Band ${b} Gain`, -24, 24, 0, 'dB')
  ];
}).flat();

/** Delay Designer taps as workstream D names them: `tap{A-Z}{Time,Level,Pan,LowCut,HighCut,Mute}`. */
const delayTaps = ['A', 'B', 'C', 'D'].flatMap((t, i) => [
  p(`tap${t}Time`, `Tap ${t} Time`, 0, 10, 0.125 * (i + 1), 's'),
  p(`tap${t}Level`, `Tap ${t} Level`, -60, 0, -6 * (i + 1), 'dB'),
  p(`tap${t}Pan`, `Tap ${t} Pan`, -1, 1, i % 2 ? 0.5 : -0.5),
  p(`tap${t}LowCut`, `Tap ${t} Low Cut`, 20, 20_000, 20, 'Hz'),
  p(`tap${t}HighCut`, `Tap ${t} High Cut`, 20, 20_000, 8_000, 'Hz'),
  c(`tap${t}Mute`, `Tap ${t} Mute`, ['Off', 'On'])
]);

export const RemixFxId = 'anode.remix-fx';

export const FakeT2Effects: readonly PluginInfo[] = [
  plugin(
    'anode.space',
    'Space Designer',
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
  // Workstream D: the Linear Phase EQ takes the Channel EQ's parameter ids.
  plugin('anode.linear-eq', 'Linear Phase EQ', 'EQ', [
    ...(FakeEffects.find(e => e.id === 'anode.channel-eq')?.parameters ?? [])
  ]),
  plugin('anode.multipressor', 'Multipressor', 'Dynamics', [
    p('cross1', 'Crossover 1', 20, 20_000, 150, 'Hz'),
    p('cross2', 'Crossover 2', 20, 20_000, 1_500, 'Hz'),
    p('cross3', 'Crossover 3', 20, 20_000, 6_000, 'Hz'),
    ...multipressorBands,
    p('output', 'Output', -24, 24, 0, 'dB', 1),
    p('mix', 'Mix', 0, 1, 1, '', 2)
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
    c('grid', 'Grid', ['1/4', '1/8', '1/16', '1/32'], 2),
    p('tempo', 'Tempo', 20, 300, 120, 'bpm'),
    n('feedbackTap', 'Feedback Tap', 1, 26, 1),
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
  plugin('anode.step-fx', 'Step FX', 'Multi Effects', [
    ...Array.from({ length: 16 }, (_, i) => [
      p(`cutoff${i + 1}`, `Cutoff ${i + 1}`, 20, 20_000, 8_000, 'Hz', i === 0 ? 1 : null),
      p(`volume${i + 1}`, `Volume ${i + 1}`, 0, 1, 1),
      p(`pan${i + 1}`, `Pan ${i + 1}`, -1, 1, 0)
    ]).flat()
  ]),
  plugin('anode.beat-breaker', 'Beat Breaker', 'Multi Effects', [
    ...Array.from({ length: 16 }, (_, i) => [
      n(`slice${i + 1}Source`, `Slice ${i + 1} Source`, 1, 16, i + 1),
      c(`slice${i + 1}Reverse`, `Slice ${i + 1} Reverse`, ['Off', 'On']),
      c(`slice${i + 1}Mute`, `Slice ${i + 1} Mute`, ['Off', 'On'])
    ]).flat()
  ]),
  // Workstream D's ids: `xy{1,2}{Active,Effect,X,Y}`; pressing a pad sets its Active.
  plugin(RemixFxId, 'Remix FX', 'Multi Effects', [
    c('xy1Active', 'Pad 1 Active', ['Off', 'On']),
    c('xy1Effect', 'Pad 1 Effect', ['Filter', 'Wobble', 'Orbit']),
    p('xy1X', 'Pad 1 X', 0, 1, 0.5, '', 1),
    p('xy1Y', 'Pad 1 Y', 0, 1, 0.5, '', 2),
    c('xy2Active', 'Pad 2 Active', ['Off', 'On']),
    c('xy2Effect', 'Pad 2 Effect', ['Repeater', 'Reverse', 'Scratch', 'Gater', 'Downsampler', 'Tape Stop']),
    p('xy2X', 'Pad 2 X', 0, 1, 0.5, '', 3),
    p('xy2Y', 'Pad 2 Y', 0, 1, 0.5, '', 4)
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
