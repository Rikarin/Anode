import type { ParameterInfo } from '@anode/api-client';
import { parameterGroups } from './parameter-editor';

const parameter = (id: string): ParameterInfo => ({
  id,
  name: id,
  min: 0,
  max: 1,
  default: 0,
  unit: '',
  steps: 0,
  valueNames: null
});

describe('parameterGroups', () => {
  it('groups dotted ids by their prefix', () => {
    const groups = parameterGroups(['filter.cutoff', 'filter.q', 'gain'].map(parameter));
    expect(groups.map(g => [g.name, g.parameters.length])).toEqual([
      ['Filter', 2],
      ['Main', 1]
    ]);
  });

  it("groups the built-in synth's camelCase ids by their leading word", () => {
    const ids = [
      'osc1Wave',
      'osc2Fine',
      'oscMix',
      'filterMode',
      'cutoff',
      'resonance',
      'keyTracking',
      'ampAttack',
      'lfoRate',
      'unisonDetune',
      'glide',
      'volume'
    ];
    const groups = Object.fromEntries(
      parameterGroups(ids.map(parameter)).map(g => [g.name, g.parameters.map(p => p.id)])
    );
    expect(groups).toEqual({
      'Oscillator 1': ['osc1Wave'],
      'Oscillator 2': ['osc2Fine'],
      Oscillators: ['oscMix'],
      Filter: ['filterMode', 'cutoff', 'resonance', 'keyTracking'],
      'Amp Envelope': ['ampAttack'],
      LFO: ['lfoRate'],
      Unison: ['unisonDetune'],
      Main: ['glide', 'volume']
    });
  });
});
