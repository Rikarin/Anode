import type { Waveform } from '@anode/timeline';
import { DefaultSilence, type Envelope, envelopeOf, silenceStretches } from './strip-silence';

const Sr = 1000; // 1 frame per ms, so the maths reads in milliseconds

/** Blocks of 10 frames (10 ms): loud (0.5) where `pattern` has '#', silent where '.'. */
function envelope(pattern: string): Envelope {
  return {
    firstFrame: 0,
    framesPerBlock: 10,
    levels: Array.from({ length: pattern.length }, (_, i) => (pattern.charAt(i) === '#' ? 0.5 : 0.001))
  };
}

const tight = { thresholdDb: -40, minTimeMs: 0, preAttackMs: 0, postReleaseMs: 0 };

describe('Strip Silence preview', () => {
  it('keeps the stretches above the threshold', () => {
    expect(silenceStretches(envelope('##....###.'), tight, Sr, 0, 100)).toEqual([
      [0, 20],
      [60, 90]
    ]);
  });

  it('removes everything under a threshold above the level', () => {
    expect(silenceStretches(envelope('###'), { ...tight, thresholdDb: -3 }, Sr, 0, 30)).toEqual([]);
  });

  it('does not split at silence shorter than the minimum time', () => {
    const e = envelope('##..##......##');
    expect(silenceStretches(e, { ...tight, minTimeMs: 30 }, Sr, 0, 140)).toEqual([
      [0, 60],
      [120, 140]
    ]);
  });

  it('widens by pre-attack and post-release, within the region, joining overlaps', () => {
    const e = envelope('...##....##...');
    expect(silenceStretches(e, { ...tight, preAttackMs: 20, postReleaseMs: 10 }, Sr, 0, 140)).toEqual([
      [10, 60],
      [70, 120]
    ]);
    expect(silenceStretches(e, { ...tight, preAttackMs: 50, postReleaseMs: 30 }, Sr, 0, 140)).toEqual([[0, 140]]);
  });

  it('uses Logic-like defaults', () => {
    expect(DefaultSilence).toEqual({ thresholdDb: -40, minTimeMs: 100, preAttackMs: 20, postReleaseMs: 50 });
  });

  it('reads an envelope from peaks', () => {
    // Two channels, 4 peaks of 64 frames: loud, silent, silent, loud.
    const data = new Int16Array([-16000, 16000, -100, 100, 0, 0, 0, 0, 0, 0, 0, 0, -8000, 32767, 0, 0]);
    const waveform: Waveform = { channels: 2, framesPerPeak: 64, firstPeak: 0, peakCount: 4, data };
    const e = envelopeOf(waveform, 0, 256, 4);
    expect(e.framesPerBlock).toBe(64);
    expect(e.levels.map(l => Math.round(l * 100) / 100)).toEqual([0.49, 0, 0, 1]);
  });
});
