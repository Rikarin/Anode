import { applyAudioEdit, convertRate, fadeGain, nearestZeroCrossing, searchLoops } from './audio-edit-math';

const ramp = () => [Float32Array.from([0.1, -0.2, 0.3, -0.4, 0.5, -0.25])];

describe('Audio File Editor maths', () => {
  it('normalises the selection’s peak, changes gain, inverts and silences', () => {
    const [n] = applyAudioEdit(ramp(), { kind: 'normalize', startFrame: 0, endFrame: 4, value: 0 });
    [0.25, -0.5, 0.75, -1, 0.5, -0.25].forEach((v, k) => expect(n?.[k]).toBeCloseTo(v, 6));
    const [g] = applyAudioEdit(ramp(), { kind: 'gain', startFrame: 4, endFrame: 6, value: -6.0206 });
    expect(g?.[4]).toBeCloseTo(0.25, 4);
    expect(g?.[0]).toBeCloseTo(0.1, 6);
    const [i] = applyAudioEdit(ramp(), { kind: 'invert', startFrame: 0, endFrame: 2 });
    expect(i?.[0]).toBeCloseTo(-0.1, 6);
    expect(i?.[2]).toBeCloseTo(0.3, 6);
    const [s] = applyAudioEdit(ramp(), { kind: 'silence', startFrame: 1, endFrame: 3 });
    expect(
      Array.from(s ?? [])
        .slice(0, 4)
        .map(v => Math.round(v * 10) / 10)
    ).toEqual([0.1, 0, 0, -0.4]);
  });

  it('fades in and out with curves, reverses, removes DC and trims', () => {
    expect(fadeGain(0.5, 0)).toBe(0.5);
    expect(fadeGain(0.5, 1)).toBeGreaterThan(0.5);
    expect(fadeGain(0.5, -1)).toBeLessThan(0.5);
    const ones = [new Float32Array(5).fill(1)];
    expect(Array.from(applyAudioEdit(ones, { kind: 'fadeIn', startFrame: 0, endFrame: 4 })[0] ?? [])).toEqual([
      0, 0.25, 0.5, 0.75, 1
    ]);
    expect(Array.from(applyAudioEdit(ones, { kind: 'fadeOut', startFrame: 1, endFrame: 5 })[0] ?? [])).toEqual([
      1, 1, 0.75, 0.5, 0.25
    ]);
    const [r] = applyAudioEdit(ramp(), { kind: 'reverse', startFrame: 0, endFrame: 3 });
    expect(r?.[0]).toBeCloseTo(0.3, 6);
    expect(r?.[2]).toBeCloseTo(0.1, 6);
    const [d] = applyAudioEdit([Float32Array.from([1, 2, 3])], { kind: 'removeDc', startFrame: 0, endFrame: 3 });
    expect(Array.from(d ?? [])).toEqual([-1, 0, 1]);
    expect(applyAudioEdit(ramp(), { kind: 'trim', startFrame: 2, endFrame: 4 })[0]?.length).toBe(2);
    expect(convertRate([new Float32Array(480)], 48_000, 44_100)[0]?.length).toBe(441);
  });

  it('snaps to the nearest zero crossing', () => {
    const wave = Float32Array.from({ length: 200 }, (_, i) => Math.sin((2 * Math.PI * i) / 100 + 0.01));
    expect(nearestZeroCrossing(wave, 48)).toBe(50);
    expect(nearestZeroCrossing(wave, 1048, 1000)).toBe(1050);
    expect(nearestZeroCrossing(new Float32Array(10).fill(0.5), 4)).toBe(4);
  });

  it('finds a periodic tone’s loop within a period', () => {
    const period = 480;
    const tone = Float32Array.from({ length: 48_000 }, (_, i) => Math.sin((2 * Math.PI * i) / period));
    const [best] = searchLoops(tone, 1000, 20_000, 3, 4000);
    expect(best).toBeDefined();
    const length = (best?.endFrame ?? 0) - (best?.startFrame ?? 0);
    expect(Math.abs(length / period - Math.round(length / period)) * period).toBeLessThanOrEqual(period);
    expect(best?.score ?? 0).toBeGreaterThan(0.99);
  });
});
