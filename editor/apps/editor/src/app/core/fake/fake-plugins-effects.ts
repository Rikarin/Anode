import type { ParameterInfo, PluginInfo } from '@anode/api-client';

/** The fake engine's parity effects (docs/effects/parity.md), with the engine's ids and ranges. */

function p(id: string, name: string, min: number, max: number, value: number, unit = ''): ParameterInfo {
  return { id, name, min, max, default: value, unit, steps: 0, valueNames: null };
}

function pick(id: string, name: string, names: readonly string[], value = 0): ParameterInfo {
  return {
    id,
    name,
    min: 0,
    max: names.length - 1,
    default: value,
    unit: '',
    steps: names.length - 1,
    valueNames: [...names]
  };
}

const toggle = (id: string, name: string, on = false) => pick(id, name, ['Off', 'On'], on ? 1 : 0);

function effect(
  id: string,
  name: string,
  category: string,
  parameters: ParameterInfo[],
  hasSidechain = false
): PluginInfo {
  return {
    id,
    name,
    vendor: 'Anode',
    version: '1.0.0',
    format: 'builtin',
    isInstrument: false,
    fileTypes: [],
    parameters,
    category,
    hasSidechain,
    hasEditor: false
  };
}

export const OscillatorWaves = ['Sine', 'Triangle', 'Square', 'Sawtooth', 'White Noise', 'Pink Noise'];

export const FakeParityEffects: readonly PluginInfo[] = [
  effect('anode.tuner', 'Tuner', 'Metering', [
    p('reference', 'Reference A', 400, 480, 440, 'Hz'),
    toggle('mute', 'Mute')
  ]),
  effect('anode.test-oscillator', 'Test Oscillator', 'Utility', [
    pick('waveform', 'Waveform', OscillatorWaves),
    p('frequency', 'Frequency', 20, 20_000, 1_000, 'Hz'),
    p('level', 'Level', -96, 0, -18, 'dB'),
    pick('channel', 'Channel', ['Both', 'Left', 'Right']),
    toggle('sweep', 'Sweep'),
    p('sweepTime', 'Sweep Time', 1, 60, 10, 's')
  ]),
  effect('anode.expander', 'Expander', 'Dynamics', [
    p('threshold', 'Threshold', -80, 0, -40, 'dB'),
    p('ratio', 'Ratio', 1, 8, 2, ':1'),
    p('knee', 'Knee', 0, 24, 6, 'dB'),
    p('range', 'Range', -80, 0, -40, 'dB'),
    p('attack', 'Attack', 0.1, 100, 2, 'ms'),
    p('release', 'Release', 5, 2_000, 150, 'ms'),
    p('makeup', 'Make-up', -24, 24, 0, 'dB'),
    p('mix', 'Mix', 0, 100, 100, '%')
  ]),
  effect('anode.frequency-spread', 'Frequency Spread', 'Utility', [
    { ...p('bands', 'Bands', 2, 16, 8), steps: 14 },
    p('low', 'Low', 50, 5_000, 300, 'Hz'),
    p('high', 'High', 500, 20_000, 8_000, 'Hz'),
    p('width', 'Width', 0, 100, 50, '%'),
    p('output', 'Output', -24, 12, 0, 'dB')
  ]),
  effect('anode.scope', 'Oscilloscope', 'Metering', [
    p('time', 'Time', 1, 2_000, 20, 'ms'),
    pick('trigger', 'Trigger', ['Free', 'Rising', 'Falling'], 1),
    p('level', 'Trigger Level', -1, 1, 0)
  ]),
  effect('anode.phase-meter', 'Phase Meter', 'Metering', [p('integration', 'Integration', 50, 3_000, 300, 'ms')]),
  effect(
    'anode.vocoder',
    'Vocoder',
    'Specialized',
    [
      pick('source', 'Source', ['Synth', 'Side Chain Carrier', 'Side Chain Voice']),
      { ...p('bands', 'Bands', 4, 32, 20), steps: 28 },
      p('low', 'Low', 50, 1_000, 100, 'Hz'),
      p('high', 'High', 1_000, 16_000, 8_000, 'Hz'),
      p('formant', 'Formant', -12, 12, 0, 'st'),
      p('sharpness', 'Sharpness', 0.5, 4, 1),
      p('attack', 'Attack', 0.5, 100, 4, 'ms'),
      p('release', 'Release', 5, 1_000, 40, 'ms'),
      { ...p('note', 'Note', 24, 84, 48), steps: 60 },
      p('sibilance', 'Sibilance', 0, 100, 25, '%'),
      p('level', 'Level', -24, 24, 0, 'dB'),
      p('mix', 'Mix', 0, 100, 100, '%')
    ],
    true
  ),
  effect(
    'anode.voice-shaper',
    'Voice Shaper',
    'Pitch',
    [
      p('pitch', 'Pitch', -24, 24, 0, 'st'),
      p('formant', 'Formant', -12, 12, 0, 'st'),
      pick('target', 'Target', ['Pitch', 'Side Chain']),
      p('mix', 'Mix', 0, 100, 100, '%')
    ],
    true
  )
];
