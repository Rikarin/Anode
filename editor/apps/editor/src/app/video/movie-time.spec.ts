import { TempoMap } from '../music/tempo-map';
import {
  driftCorrection,
  frameAtSeconds,
  MaxRateNudge,
  movieFrameAt,
  movieSecondsAt,
  movieSpan,
  parseMovieOffset,
  seekTimeForFrame,
  thumbnailFrames,
  tickAtFrame,
  tickAtMovieSeconds,
  timecode,
  type MovieTiming
} from './movie-time';

const Bar = 3840;
const Beat = 960;

function movie(change: Partial<MovieTiming> = {}): MovieTiming {
  return {
    startTick: 0,
    offsetSeconds: 0,
    frameRate: { numerator: 25, denominator: 1 },
    durationSeconds: 60,
    frames: 1500,
    ...change
  };
}

describe('the movie time mapping (docs/11-phase-10.md, "Video")', () => {
  const steady = TempoMap.constant(120);

  it('maps project ticks to movie time through the start position and offset', () => {
    // The movie's first frame at bar 2 (2 s at 120 bpm).
    const atBar2 = movie({ startTick: Bar });
    expect(movieSecondsAt(atBar2, steady, Bar)).toBeCloseTo(0);
    expect(movieSecondsAt(atBar2, steady, Bar + Beat)).toBeCloseTo(0.5);
    expect(movieFrameAt(atBar2, steady, Bar + Beat)).toBe(12);
    expect(movieFrameAt(atBar2, steady, Bar - 1)).toBeNull();
    // With a 1 s offset its frame 25 plays at bar 2, and its first frame two beats before.
    const offset = movie({ startTick: Bar, offsetSeconds: 1 });
    expect(movieFrameAt(offset, steady, Bar)).toBe(25);
    expect(movieSpan(offset, steady).startTick).toBeCloseTo(Bar - 2 * Beat);
    expect(tickAtMovieSeconds(offset, steady, 1)).toBeCloseTo(Bar);
  });

  it('follows tempo changes', () => {
    const tempo = new TempoMap([
      { tick: 0, bpm: 120, curve: 'step', shape: 0 },
      { tick: Bar, bpm: 60, curve: 'step', shape: 0 }
    ]);
    // 2 s to bar 2, then a beat a second.
    expect(movieSecondsAt(movie(), tempo, Bar + Beat)).toBeCloseTo(3);
    expect(movieFrameAt(movie(), tempo, Bar + Beat)).toBe(75);
    expect(tickAtFrame(movie(), tempo, 75)).toBeCloseTo(Bar + Beat);
    // Starting after the change: seconds count from the start position.
    expect(movieSecondsAt(movie({ startTick: Bar + Beat }), tempo, Bar + 3 * Beat)).toBeCloseTo(2);
  });

  it('lands on frame boundaries, at fractional rates too', () => {
    const ntsc = movie({ frameRate: { numerator: 30_000, denominator: 1001 }, frames: 1798 });
    const tempo = new TempoMap([
      { tick: 0, bpm: 97, curve: 'step', shape: 0 },
      { tick: 4 * Bar, bpm: 131, curve: 'ramp', shape: 0 },
      { tick: 8 * Bar, bpm: 131, curve: 'step', shape: 0 }
    ]);
    for (const frame of [0, 1, 29, 30, 299, 1001, 1797]) {
      // A frame's start shows that frame; a tick before it the one before.
      const tick = tickAtFrame(ntsc, tempo, frame);
      expect(movieFrameAt(ntsc, tempo, tick)).toBe(frame);
      if (frame > 0) {
        expect(movieFrameAt(ntsc, tempo, tick - 0.5)).toBe(frame - 1);
      }
    }
    // The seek time of a frame is its middle, inside it.
    expect(frameAtSeconds(ntsc, seekTimeForFrame(ntsc, 1000))).toBe(1000);
  });

  it('writes timecode from the movie timecode start, and reads offsets', () => {
    const tc = { frameRate: { numerator: 25, denominator: 1 }, timecodeStartSeconds: 3600 };
    expect(timecode(tc, 1.48)).toBe('01:00:01:12');
    expect(timecode(tc, 0)).toBe('01:00:00:00');
    expect(parseMovieOffset('00:00:01:12', tc)).toBeCloseTo(1.48);
    expect(parseMovieOffset('1.5', tc)).toBe(1.5);
    expect(parseMovieOffset('1:30', tc)).toBeNull();
    expect(parseMovieOffset('soon', tc)).toBeNull();
  });
});

describe('drift correction', () => {
  const rate = { frameRate: { numerator: 25, denominator: 1 } };
  const frame = 1 / 25;

  it('leaves the video alone within half a frame', () => {
    expect(driftCorrection(rate, 10, 10 + 0.4 * frame)).toEqual({ kind: 'none', rate: 1 });
    expect(driftCorrection(rate, 10, 10 - 0.4 * frame, 1.2)).toEqual({ kind: 'none', rate: 1.2 });
  });

  it('nudges the rate for a small drift, at most 5 %, the way the gap closes', () => {
    const behind = driftCorrection(rate, 10, 10 + 2 * frame);
    expect(behind.kind).toBe('rate');
    expect(behind.rate).toBeGreaterThan(1);
    expect(behind.rate).toBeLessThanOrEqual(1 + MaxRateNudge);
    const ahead = driftCorrection(rate, 10, 10 - 5 * frame, 0.5);
    expect(ahead.kind).toBe('rate');
    expect(ahead.rate).toBeLessThan(0.5);
    expect(ahead.rate).toBeGreaterThanOrEqual(0.5 * (1 - MaxRateNudge));
  });

  it('seeks to the target frame for a large drift (a locate or a cycle jump)', () => {
    const action = driftCorrection(rate, 10, 4.02);
    expect(action.kind).toBe('seek');
    expect(action.kind === 'seek' && frameAtSeconds(rate, action.time)).toBe(100);
  });
});

describe('the movie track thumbnails', () => {
  const steady = TempoMap.constant(120);
  // 48 px a quarter: 0.05 px a tick; a 2-bar view (192 px) of the movie from the start.
  const view = (startTick: number, widthPx: number, pxPerTick: number) => ({
    startTick,
    endTick: startTick + widthPx / pxPerTick,
    widthPx
  });

  it('places one thumbnail per thumbnail width when zoomed out', () => {
    const thumbs = thumbnailFrames(movie(), steady, view(0, 640, 0.05), 64);
    expect(thumbs).toHaveLength(10);
    // 64 px is 1280 ticks: 0.667 s, frame 16 and so on.
    expect(thumbs.slice(0, 3).map(t => t.frame)).toEqual([0, 16, 33]);
    expect(thumbs[1]?.tick).toBeCloseTo(1280);
  });

  it('shows each frame once when zoomed in past a frame per thumbnail', () => {
    const thumbs = thumbnailFrames(movie(), steady, view(0, 640, 2), 64);
    const frames = thumbs.map(t => t.frame);
    expect(new Set(frames).size).toBe(frames.length);
    expect(frames[0]).toBe(0);
  });

  it('starts on its grid where the view starts, and stops at the movie end', () => {
    const scrolled = thumbnailFrames(movie(), steady, view(12_800, 640, 0.05), 64);
    expect(scrolled[0]?.tick).toBeCloseTo(12_800);
    const short = movie({ durationSeconds: 1, frames: 25 });
    expect(thumbnailFrames(short, steady, view(0, 640, 0.05), 64)).toHaveLength(2);
  });
});
