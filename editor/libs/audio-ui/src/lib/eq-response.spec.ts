import { analyserY, eqBandText, eqFrequency, eqFrequencyText, eqGainAt, eqGainY, eqHandleY } from './eq-graph';
import { type EqBand, analyserBands, eqResponse, logFrequencies } from './eq-response';
import { spectrumX } from './spectrum-graph';

const band = (kind: EqBand['kind'], frequency: number, gainDb = 0, q = 1, slope = 1): EqBand => ({
  id: kind,
  label: kind,
  kind,
  on: true,
  frequency,
  gainDb,
  q,
  slope
});

const Rate = 48_000;

describe('EQ response', () => {
  it('is the engine’s measured response (the table in EqTests)', () => {
    const bands = [
      band('highPass', 80),
      band('lowShelf', 120, 4, 0.71),
      band('bell', 200),
      band('bell', 1_000, -6, 2),
      band('bell', 3_000, 3, 1),
      band('bell', 6_000),
      band('highShelf', 8_000, 2, 0.71),
      band('lowPass', 15_000, 0, 1, 0)
    ];
    const table: [number, number][] = [
      [40, -21.149],
      [100, 1.006],
      [300, -1.042],
      [1_000, -6.634],
      [3_000, 1.812],
      [8_000, 0.286],
      [15_000, -2.0],
      [18_000, -7.891]
    ];
    const response = eqResponse(
      bands,
      table.map(row => row[0]),
      Rate,
      -1
    );
    table.forEach(([, db], i) => expect(response[i]).toBeCloseTo(db, 2));
  });

  it('is flat with nothing engaged, and only the output gain moves it', () => {
    expect(eqResponse([band('bell', 1_000)], [100, 1_000], Rate)).toEqual([0, 0]);
    const off = { ...band('bell', 1_000, 12), on: false };
    expect(eqResponse([off], [1_000], Rate, 3)).toEqual([3]);
  });

  it('peaks at a bell’s gain, and a dynamic band adds to it', () => {
    expect(eqResponse([band('bell', 1_000, 9, 2)], [1_000], Rate)[0]).toBeCloseTo(9, 6);
    expect(eqResponse([{ ...band('bell', 1_000, 9, 2), dynamicDb: -5 }], [1_000], Rate)[0]).toBeCloseTo(4, 6);
  });

  it('gives a cut its slope’s order: 3 dB down at the corner, 6 dB per octave per order below', () => {
    for (const [slope, order] of [
      [0, 2],
      [1, 4],
      [2, 6],
      [3, 8]
    ] as const) {
      const [corner = 0, octave = 0, two = 0] = eqResponse(
        [band('highPass', 1_000, 0, 1, slope)],
        [1_000, 500, 250],
        Rate
      );
      expect(corner).toBeCloseTo(-3.01, 1);
      expect(octave - two).toBeCloseTo(6.02 * order, -0.5);
    }
  });

  it('spaces frequencies and analyser bands evenly in log frequency', () => {
    const axis = logFrequencies(4);
    [20, 200, 2_000, 20_000].forEach((hz, i) => expect(axis[i]).toBeCloseTo(hz, 6));
    const bands = analyserBands(64);
    expect(bands).toHaveLength(64);
    expect((bands[1] ?? 0) / (bands[0] ?? 1)).toBeCloseTo((bands[64 - 1] ?? 0) / (bands[64 - 2] ?? 1), 9);
    expect(bands[0]).toBeGreaterThan(20);
    expect(bands.at(-1)).toBeLessThan(20_000);
  });
});

describe('EQ graph geometry', () => {
  it('round-trips a frequency and a gain through the axes', () => {
    expect(eqFrequency(spectrumX(632))).toBeCloseTo(632);
    expect(eqFrequency(2)).toBeCloseTo(20_000);
    expect(eqGainY(24)).toBe(0);
    expect(eqGainY(-24)).toBe(1);
    expect(eqGainY(0)).toBe(0.5);
    expect(eqGainAt(eqGainY(7.5))).toBeCloseTo(7.5);
    expect(analyserY(0)).toBe(0);
    expect(analyserY(-45)).toBe(0.5);
    expect(analyserY(-200)).toBe(1);
  });

  it('keeps a cut’s handle on the 0 dB line', () => {
    expect(eqHandleY(band('highPass', 80, 12))).toBe(0.5);
    expect(eqHandleY(band('bell', 80, 12))).toBe(0.25);
  });

  it('says a band’s state in words', () => {
    expect(eqFrequencyText(85.4)).toBe('85 Hz');
    expect(eqBandText(band('bell', 2_800, 5))).toBe('2.80 kHz, +5.0 dB, Q 1.00');
    expect(eqBandText({ ...band('bell', 2_800, -5), on: false })).toBe('2.80 kHz, −5.0 dB, Q 1.00, off');
    expect(eqBandText(band('lowPass', 15_000, 0, 1, 0))).toBe('15.00 kHz, 12 dB per octave');
  });
});
