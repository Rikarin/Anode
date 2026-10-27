import {
  encodePeaks,
  parsePeaks,
  PeakFormat,
  PeakFormatError,
  peakLevelFor,
  peakRange,
  type Waveform,
  WaveformSet
} from './waveform';

describe('parsePeaks', () => {
  it('reads the header and the min/max pairs, little endian', () => {
    const buffer = new ArrayBuffer(24 + 2 * 2 * 2 * 2);
    const view = new DataView(buffer);
    view.setUint32(0, 0x4b504e41, true); // "ANPK"
    view.setUint16(4, 1, true);
    view.setUint16(6, 2, true); // stereo
    view.setUint32(8, 256, true);
    view.setUint32(12, 10, true); // from peak 10
    view.setBigUint64(16, 2n, true);
    [-100, 200, -300, 400, -5, 6, -7, 8].forEach((v, i) => view.setInt16(24 + 2 * i, v, true));
    expect(new TextDecoder().decode(new Uint8Array(buffer, 0, 4))).toBe('ANPK');

    const waveform = parsePeaks(buffer);

    expect(waveform).toMatchObject({ channels: 2, framesPerPeak: 256, firstPeak: 10, peakCount: 2 });
    expect(Array.from(waveform.data)).toEqual([-100, 200, -300, 400, -5, 6, -7, 8]);
  });

  it('round-trips what encodePeaks writes', () => {
    const data = Int16Array.from([-1, 1, -2, 2, -3, 3]);
    const waveform = parsePeaks(encodePeaks({ channels: 1, framesPerPeak: 64, firstPeak: 0, data }));
    expect(waveform.peakCount).toBe(3);
    expect(Array.from(waveform.data)).toEqual(Array.from(data));
  });

  it('refuses a short body, another magic, another version and a truncated one', () => {
    expect(() => parsePeaks(new ArrayBuffer(10))).toThrow(PeakFormatError);
    const wrongMagic = encodePeaks({ channels: 1, framesPerPeak: 64, firstPeak: 0, data: new Int16Array(2) });
    new DataView(wrongMagic).setUint32(0, 0x46464952, true); // "RIFF"
    expect(() => parsePeaks(wrongMagic)).toThrow('magic');
    const wrongVersion = encodePeaks({ channels: 1, framesPerPeak: 64, firstPeak: 0, data: new Int16Array(2) });
    new DataView(wrongVersion).setUint16(4, 2, true);
    expect(() => parsePeaks(wrongVersion)).toThrow('version 2');
    const truncated = encodePeaks({ channels: 1, framesPerPeak: 64, firstPeak: 0, data: new Int16Array(4) });
    expect(() => parsePeaks(truncated.slice(0, truncated.byteLength - 2))).toThrow('truncated');
  });
});

describe('peakLevelFor', () => {
  it('picks the coarsest kept level no coarser than a pixel', () => {
    expect(peakLevelFor(10)).toBe(64);
    expect(peakLevelFor(300)).toBe(256);
    expect(peakLevelFor(5000)).toBe(4096);
    expect(peakLevelFor(1e9)).toBe(16384);
    expect(PeakFormat.Levels).toEqual([64, 256, 1024, 4096, 16384]);
  });
});

describe('peakRange and WaveformSet', () => {
  const stereo: Waveform = {
    channels: 2,
    framesPerPeak: 100,
    firstPeak: 2,
    peakCount: 3,
    // peaks 2, 3, 4: [L min, L max, R min, R max]
    data: Int16Array.from([-10, 10, -20, 20, -32767, 0, 0, 5, 0, 32767, -1, 1])
  };

  it('spans the peaks a frame range covers, across channels, as fractions of full scale', () => {
    const out = [0, 0];
    expect(peakRange(stereo, 200, 300, out)).toBe(true);
    expect(out).toEqual([-20 / 32767, 20 / 32767]);
    expect(peakRange(stereo, 250, 450, out)).toBe(true);
    expect(out).toEqual([-1, 1]);
  });

  it('has nothing outside the peaks it holds', () => {
    expect(peakRange(stereo, 0, 150, [0, 0])).toBe(false);
    expect(peakRange(stereo, 500, 600, [0, 0])).toBe(false);
  });

  it('keeps several resolutions per key and draws from the right one', () => {
    const set = new WaveformSet();
    const level = (framesPerPeak: number): Waveform => ({ ...stereo, framesPerPeak });
    set.add(1, level(1024));
    set.add(1, level(64));
    set.add(1, level(256));
    expect(set.best(1, 300)?.framesPerPeak).toBe(256);
    expect(set.best(1, 10)?.framesPerPeak).toBe(64);
    expect(set.best(1, 1e6)?.framesPerPeak).toBe(1024);
    expect(set.best(2, 300)).toBeUndefined();
    set.delete(1);
    expect(set.has(1)).toBe(false);
  });
});
