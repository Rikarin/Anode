import { varispeedText } from './varispeed-display';

describe('varispeedText', () => {
  it('shows percent, resulting tempo and detune', () => {
    expect(varispeedText(0.9, 'percent')).toBe('−10.00 %');
    expect(varispeedText(1.25, 'percent')).toBe('+25.00 %');
    expect(varispeedText(1, 'percent')).toBe('0.00 %');
    expect(varispeedText(0.9, 'tempo', 120)).toBe('108.00 bpm');
    expect(varispeedText(0.9, 'detune')).toBe('−1.82 st');
  });
});
