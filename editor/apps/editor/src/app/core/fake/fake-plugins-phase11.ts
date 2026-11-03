import { MasteringParameters, type ParameterInfo, type PluginInfo, SpeakerLayouts } from '@anode/api-client';

/**
 * The fake engine's Phase 11 plugins (docs/12-phase-11.md): the T3 instruments (the physical model, tape replay and
 * Sample Alchemy, whose parameter lists are workstream E's: these ids are the editor's guesses, matched by the custom
 * panels' patterns, plugin-panels/panel-patterns.ts), and the T3 effects with their fixed parameters (Saturation, the
 * Down Mixer, Binaural Post-Processing) and the Mastering Assistant (`MasteringParameters`).
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
/** Sample Alchemy's resynthesis modes. */
export const AlchemyModes = ['Granular', 'Additive', 'Spectral'];
/** The Mastering Assistant's characters, in `character`'s order (`MasteringCharacter`). */
export const MasteringCharacters = ['Clean', 'Valve', 'Punch', 'Transparent'];

const envelope = (prefix: string, label: string): ParameterInfo[] => [
  p(`${prefix}Attack`, `${label} Attack`, 0.001, 10, 0.005, 's'),
  p(`${prefix}Decay`, `${label} Decay`, 0.001, 10, 0.4, 's'),
  p(`${prefix}Sustain`, `${label} Sustain`, 0, 1, 0.8),
  p(`${prefix}Release`, `${label} Release`, 0.001, 10, 0.5, 's')
];

const filter = (): ParameterInfo[] => [
  c('filterType', 'Filter', ['Off', 'Low Pass', 'High Pass', 'Band Pass']),
  p('cutoff', 'Cutoff', 20, 20_000, 12_000, 'Hz'),
  p('resonance', 'Resonance', 0, 1, 0.1)
];

export const FakeT3Instruments: readonly PluginInfo[] = [
  plugin('anode.physical-model', 'Physical Model', 'Instrument', [
    c('object', 'Object', PhysicalObjects),
    p('stiffness', 'Stiffness', 0, 1, 0.4),
    p('damping', 'Damping', 0, 1, 0.3),
    p('inharmonicity', 'Inharmonicity', 0, 1, 0.1),
    ...[1, 2, 3].flatMap(n => [
      c(`exciter${n}Type`, `Exciter ${n}`, PhysicalExciters, n === 1 ? 1 : 0),
      p(`exciter${n}Position`, `Exciter ${n} Position`, 0, 1, 0.1 + 0.2 * (n - 1)),
      p(`exciter${n}Strength`, `Exciter ${n} Strength`, 0, 1, 0.7)
    ]),
    ...[1, 2].flatMap(n => [
      p(`pickup${n}Position`, `Pickup ${n} Position`, 0, 1, n === 1 ? 0.25 : 0.75),
      p(`pickup${n}Level`, `Pickup ${n} Level`, 0, 1, 0.8)
    ]),
    p('body', 'Body', 0, 1, 0.5),
    ...filter(),
    ...envelope('amp', 'Amp'),
    p('vibratoRate', 'Vibrato Rate', 0.1, 12, 5, 'Hz'),
    p('vibratoDepth', 'Vibrato Depth', 0, 1, 0),
    p('volume', 'Volume', -60, 6, -6, 'dB')
  ]),
  plugin('anode.tape-replay', 'Tape Replay', 'Instrument', [
    c('tapeSet', 'Tape Set', ['Strings', 'Flutes', 'Choir']),
    p('speed', 'Tape Speed', 0.5, 2, 1, '×'),
    p('wow', 'Wow', 0, 1, 0.2),
    p('flutter', 'Flutter', 0, 1, 0.15),
    p('motor', 'Start Motor', 0, 1, 0.05, 's'),
    p('tone', 'Tone', 0, 1, 0.5),
    p('release', 'Release', 0.01, 3, 0.2, 's'),
    p('volume', 'Volume', -60, 6, -6, 'dB')
  ]),
  plugin(
    'anode.sample-alchemy',
    'Sample Alchemy',
    'Instrument',
    [
      c('mode', 'Mode', AlchemyModes),
      p('grainPosition', 'Position', 0, 1, 0.3),
      p('grainSize', 'Grain Size', 0.005, 0.5, 0.08, 's'),
      p('grainDensity', 'Density', 1, 100, 20, 'Hz'),
      p('grainSpray', 'Spray', 0, 1, 0.1),
      p('grainPitch', 'Grain Pitch', -24, 24, 0, 'st'),
      { ...p('partials', 'Partials', 1, 64, 24), steps: 63 },
      p('partialSpread', 'Spread', 0, 1, 0),
      p('spectralSmoothing', 'Smoothing', 0, 1, 0.5),
      p('noise', 'Noise', 0, 1, 0.2),
      ...envelope('amp', 'Amp'),
      ...filter(),
      p('volume', 'Volume', -60, 6, -6, 'dB')
    ],
    { fileTypes: ['.wav', '.aif', '.aiff'] }
  )
];

/** The Down Mixer's target formats: a `SpeakerFormat` index (the contract's `ChannelFormat` order). */
const Formats = Object.values(SpeakerLayouts).map(layout => layout.name);

export const FakeT3Effects: readonly PluginInfo[] = [
  plugin('anode.saturation', 'Saturation', 'Distortion', [
    c('model', 'Model', SaturationModels),
    p('drive', 'Drive', 0, 1, 0.3),
    p('tone', 'Tone', -1, 1, 0),
    p('mix', 'Mix', 0, 1, 1),
    p('output', 'Output', -24, 12, 0, 'dB')
  ]),
  plugin('anode.down-mixer', 'Down Mixer', 'Imaging', [
    c('format', 'Format', Formats, Formats.indexOf('2.0')),
    p('lfe', 'LFE', -144, 10, 0, 'dB')
  ]),
  plugin('anode.binaural', 'Binaural Post-Processing', 'Imaging', []),
  plugin(MasteringParameters.pluginId, 'Mastering Assistant', 'Mastering', [
    c(MasteringParameters.character, 'Character', MasteringCharacters),
    p(MasteringParameters.targetLufs, 'Target', -24, -6, -14, 'LUFS'),
    p(MasteringParameters.ceilingDbtp, 'Ceiling', -3, 0, -1, 'dBTP'),
    p(MasteringParameters.eq, 'EQ', 0, 1, 1),
    p(MasteringParameters.dynamics, 'Dynamics', 0, 1, 1),
    p(MasteringParameters.width, 'Width', 0, 2, 1),
    { ...c(MasteringParameters.matchedBypass, 'Level-Matched Bypass', ['Off', 'On']), kind: 'checkbox' }
  ])
];
