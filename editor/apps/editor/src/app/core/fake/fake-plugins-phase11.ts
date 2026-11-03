import { MasteringParameters, type ParameterInfo, type PluginInfo, SpeakerLayouts } from '@anode/api-client';

/**
 * The fake engine's Phase 11 plugins (docs/12-phase-11.md), with the host's ids, ranges and defaults: the T3 instruments
 * (the physical model, tape replay and Sample Alchemy, matched by the custom panels' patterns,
 * plugin-panels/panel-patterns.ts), the T3 effects (Saturation, the Down Mixer, Binaural Post-Processing) and the
 * Mastering Assistant (`MasteringParameters`).
 */

function p(id: string, name: string, min: number, max: number, value: number, unit = ''): ParameterInfo {
  return { id, name, min, max, default: value, unit, steps: 0, valueNames: null, macro: null };
}

function c(id: string, name: string, names: readonly string[], value = 0): ParameterInfo {
  return {
    id,
    name,
    min: 0,
    max: names.length - 1,
    default: value,
    unit: '',
    steps: names.length - 1,
    valueNames: [...names],
    macro: null
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
    version: '0.11.0',
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

/** The physical model's objects and exciters (Sculpture's), by index. */
export const PhysicalObjects = ['String', 'Bar', 'Membrane', 'Tube'];
export const PhysicalExciters = ['Off', 'Pick', 'Bow', 'Strike', 'Blow'];
/** Saturation's models (`anode.saturation`'s fixed `model`). */
export const SaturationModels = ['Tube', 'Tape', 'Transformer', 'Modern'];
/** Sample Alchemy's resynthesis modes, in its `mode`'s order. */
export const AlchemyModes = ['Additive', 'Granular', 'Spectral'];
/** The Mastering Assistant's characters, in `character`'s order (`MasteringCharacter`). */
export const MasteringCharacters = ['Clean', 'Valve', 'Punch', 'Transparent'];

const FilterModes = ['Low-pass 24 dB', 'Low-pass 12 dB', 'High-pass 12 dB', 'Band-pass 12 dB'];
const LfoShapes = ['Sine', 'Triangle', 'Saw', 'Square', 'Sample & Hold'];

const stepped = (id: string, name: string, min: number, max: number, value: number, unit = ''): ParameterInfo => ({
  ...p(id, name, min, max, value, unit),
  steps: max - min
});

const envelope = (n: number, attack: number, decay: number, sustain: number, release: number): ParameterInfo[] => [
  p(`env${n}Attack`, `Env ${n} Attack`, 0, 10_000, attack, 'ms'),
  p(`env${n}Decay`, `Env ${n} Decay`, 1, 10_000, decay, 'ms'),
  p(`env${n}Sustain`, `Env ${n} Sustain`, 0, 100, sustain, '%'),
  p(`env${n}Release`, `Env ${n} Release`, 1, 10_000, release, 'ms')
];

const filter = (mode: number, keyTracking: number, cutoff: number): ParameterInfo[] => [
  c('filterMode', 'Filter Mode', FilterModes, mode),
  p('cutoff', 'Cutoff', 20, 20_000, cutoff, 'Hz'),
  p('resonance', 'Resonance', 0, 100, 0, '%'),
  p('keyTracking', 'Key Tracking', 0, 100, keyTracking, '%'),
  p('filterEnvAmount', 'Filter Env Amount', -100, 100, 0, '%')
];

/** The T3 instruments, as `Anode.Dsp`'s descriptors list them (docs/instruments/T3.md). */
export const FakeT3Instruments: readonly PluginInfo[] = [
  plugin('anode.physical-model', 'Physical Model', 'Instrument', [
    c('object', 'Object', PhysicalObjects),
    p('stiffness', 'Stiffness', 0, 100, 50, '%'),
    p('damping', 'Damping', 0, 100, 30, '%'),
    p('inharmonicity', 'Inharmonicity', 0, 100, 10, '%'),
    ...[15, 30, 50].flatMap((position, i) => [
      c(`exciter${i + 1}Type`, `Exciter ${i + 1} Type`, PhysicalExciters, i === 0 ? 1 : 0),
      p(`exciter${i + 1}Position`, `Exciter ${i + 1} Position`, 0, 100, position, '%'),
      p(`exciter${i + 1}Level`, `Exciter ${i + 1} Level`, 0, 100, 100, '%'),
      p(`exciter${i + 1}Tone`, `Exciter ${i + 1} Tone`, 0, 100, 50, '%')
    ]),
    p('pickup1Position', 'Pickup A Position', 0, 100, 20, '%'),
    p('pickup2Position', 'Pickup B Position', 0, 100, 70, '%'),
    p('pickupSpread', 'Pickup Spread', 0, 100, 50, '%'),
    c('body', 'Body', ['Off', 'Guitar', 'Violin', 'Piano', 'Box'], 1),
    p('bodyAmount', 'Body Amount', 0, 100, 40, '%'),
    ...filter(1, 50, 12_000),
    ...envelope(1, 1, 1000, 100, 200),
    ...envelope(2, 2, 600, 30, 400),
    c('lfo1Shape', 'LFO 1 Shape', LfoShapes, 1),
    p('lfo1Rate', 'LFO 1 Rate', 0.05, 20, 0.6, 'Hz'),
    p('lfo1Cutoff', 'LFO 1 to Cutoff', 0, 100, 0, '%'),
    c('lfo2Shape', 'LFO 2 Shape', LfoShapes),
    p('lfo2Rate', 'LFO 2 Rate', 0.05, 20, 0.3, 'Hz'),
    p('lfo2Pickup', 'LFO 2 to Pickups', 0, 100, 0, '%'),
    p('vibratoRate', 'Vibrato Rate', 0.1, 12, 5.5, 'Hz'),
    p('vibratoDepth', 'Vibrato Depth', 0, 100, 0, 'ct'),
    stepped('bendRange', 'Pitch Bend Range', 0, 24, 2, 'st'),
    p('velocitySensitivity', 'Velocity Sensitivity', 0, 100, 70, '%'),
    p('volume', 'Volume', -60, 6, -6, 'dB')
  ]),
  plugin(
    'anode.tape-replay',
    'Tape Replay',
    'Instrument',
    [
      p('speed', 'Tape Speed', 70, 130, 100, '%'),
      p('tune', 'Tune', -100, 100, 0, 'ct'),
      p('wow', 'Wow', 0, 100, 25, '%'),
      p('flutter', 'Flutter', 0, 100, 25, '%'),
      p('motorStart', 'Start Motor Time', 0, 250, 15, 'ms'),
      p('attack', 'Attack', 0, 1000, 5, 'ms'),
      p('release', 'Release', 5, 2000, 150, 'ms'),
      p('tone', 'Tone', 500, 20_000, 10_000, 'Hz'),
      p('hiss', 'Tape Hiss', 0, 100, 10, '%'),
      p('velocitySensitivity', 'Velocity Sensitivity', 0, 100, 0, '%'),
      stepped('bendRange', 'Pitch Bend Range', 0, 12, 2, 'st'),
      p('volume', 'Volume', -60, 6, -6, 'dB')
    ],
    { fileTypes: ['.antape'] }
  ),
  plugin(
    'anode.sample-alchemy',
    'Sample Alchemy',
    'Instrument',
    [
      c('mode', 'Mode', AlchemyModes),
      stepped('rootNote', 'Root Note', 0, 127, 60),
      p('position', 'Start Position', 0, 100, 0, '%'),
      c('loop', 'Loop', ['Off', 'On'], 1),
      p('loopStart', 'Loop Start', 0, 100, 30, '%'),
      p('loopEnd', 'Loop End', 0, 100, 80, '%'),
      p('speed', 'Speed', 0, 200, 100, '%'),
      stepped('partials', 'Partials', 1, 64, 32),
      p('partialSpread', 'Partial Spread', -100, 100, 0, '%'),
      p('grainSize', 'Grain Size', 10, 500, 80, 'ms'),
      p('grainDensity', 'Grain Density', 1, 100, 30, '/s'),
      p('grainSpray', 'Grain Spray', 0, 500, 20, 'ms'),
      p('grainPitch', 'Grain Pitch Spread', 0, 1200, 0, 'ct'),
      p('noise', 'Noise', 0, 100, 20, '%'),
      ...filter(0, 0, 20_000),
      ...envelope(1, 5, 600, 100, 300),
      ...envelope(2, 2, 600, 30, 400),
      p('tune', 'Tune', -100, 100, 0, 'ct'),
      stepped('bendRange', 'Pitch Bend Range', 0, 24, 2, 'st'),
      p('velocitySensitivity', 'Velocity Sensitivity', 0, 100, 70, '%'),
      p('volume', 'Volume', -60, 12, 0, 'dB')
    ],
    { fileTypes: ['.wav', '.aif', '.aiff'] }
  )
];

/** The Down Mixer's target formats: a `SpeakerFormat` index (the contract's `ChannelFormat` order). */
const Formats = Object.values(SpeakerLayouts).map(layout => layout.name);

export const FakeT3Effects: readonly PluginInfo[] = [
  plugin('anode.saturation', 'Saturation', 'Distortion', [
    c('model', 'Model', SaturationModels),
    p('drive', 'Drive', 0, 100, 30, '%'),
    p('tone', 'Tone', -100, 100, 0, '%'),
    p('mix', 'Mix', 0, 100, 100, '%'),
    p('output', 'Output', -24, 24, 0, 'dB')
  ]),
  plugin('anode.down-mixer', 'Down Mixer', 'Utility', [
    c('format', 'Format', Formats, Formats.indexOf('2.0')),
    p('lfe', 'LFE', -144, 0, -144, 'dB')
  ]),
  plugin('anode.binaural', 'Binaural Post-Processing', 'Utility', []),
  plugin(MasteringParameters.pluginId, 'Mastering Assistant', 'Dynamics', [
    c(MasteringParameters.character, 'Character', MasteringCharacters),
    p(MasteringParameters.targetLufs, 'Loudness', -24, -6, -14, 'LUFS'),
    p(MasteringParameters.ceilingDbtp, 'Ceiling', -3, 0, -1, 'dBTP'),
    p(MasteringParameters.eq, 'EQ', 0, 1, 1),
    p(MasteringParameters.dynamics, 'Dynamics', 0, 1, 1),
    p(MasteringParameters.width, 'Width', 0, 2, 1),
    { ...c(MasteringParameters.matchedBypass, 'Level-Matched Bypass', ['Off', 'On']), kind: 'checkbox' }
  ])
];
