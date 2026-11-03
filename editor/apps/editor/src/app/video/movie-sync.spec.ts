import { TempoMap } from '../music/tempo-map';
import { MovieSync, type VideoLike } from './movie-sync';
import { frameAtSeconds, type MovieTiming } from './movie-time';

const Bar = 3840;
const Beat = 960;

/** A `<video>` that plays at its rate when its clock moves, and settles a seek on the next step. */
class FakeVideo implements VideoLike {
  private time = 0;
  playbackRate = 1;
  paused = true;
  seeking = false;
  seeks = 0;

  get currentTime(): number {
    return this.time;
  }
  set currentTime(value: number) {
    this.time = value;
    this.seeks++;
  }
  play(): void {
    this.paused = false;
  }
  pause(): void {
    this.paused = true;
  }
  /** Plays on for `ms` milliseconds. */
  advance(ms: number): void {
    if (!this.paused) {
      this.time += (ms / 1000) * this.playbackRate;
    }
  }
}

const movie: MovieTiming = {
  startTick: Bar,
  offsetSeconds: 0,
  frameRate: { numerator: 25, denominator: 1 },
  durationSeconds: 60,
  frames: 1500
};

function setUp(timing: MovieTiming = movie) {
  const tempo = TempoMap.constant(120);
  const sync = new MovieSync(
    () => timing,
    () => tempo
  );
  return { sync, video: new FakeVideo(), tempo };
}

const frameOf = (video: FakeVideo) => frameAtSeconds(movie, video.currentTime);

describe('MovieSync (the movie locked to the transport)', () => {
  it('shows the playhead frame when stopped, seeking only when the frame changes (locate, scrub)', () => {
    const { sync, video } = setUp();
    expect(sync.step(video, { tick: Bar + Beat, playing: false, speed: 1, now: 0 })).toBe('seeked');
    expect(frameOf(video)).toBe(12);
    expect(sync.step(video, { tick: Bar + Beat + 10, playing: false, speed: 1, now: 16 })).toBe('paused');
    expect(video.seeks).toBe(1);
    // A scrub through frames: each one shown, within one frame of the playhead.
    for (let tick = Bar; tick < Bar + 4 * Beat; tick += 77) {
      sync.step(video, { tick, playing: false, speed: 1, now: 0 });
      expect(frameOf(video)).toBe(sync.target(tick).frame);
    }
  });

  it('starts at the playhead at the varispeed rate, and pauses on stop', () => {
    const { sync, video } = setUp();
    expect(sync.step(video, { tick: Bar + 2 * Beat, playing: true, speed: 0.8, now: 0 })).toBe('started');
    expect(video.paused).toBe(false);
    expect(video.currentTime).toBeCloseTo(1);
    expect(video.playbackRate).toBe(0.8);
    expect(sync.step(video, { tick: Bar + 2 * Beat + 100, playing: false, speed: 0.8, now: 50 })).not.toBe('started');
    expect(video.paused).toBe(true);
  });

  it('stays locked while in step, nudges the rate for small drift and seeks on a jump', () => {
    const { sync, video, tempo } = setUp();
    const start = Bar;
    sync.step(video, { tick: start, playing: true, speed: 1, now: 0 });
    // The transport runs at 120 bpm: ticks = 1920 a second.
    const tickAt = (ms: number) => tempo.tickAt(tempo.secondsAt(start) + ms / 1000);
    video.advance(100);
    expect(sync.step(video, { tick: tickAt(100), playing: true, speed: 1, now: 100 })).toBe('locked');
    // The decoder falls 2 frames behind: a rate a little over 1.
    video.currentTime -= 0.08;
    expect(sync.step(video, { tick: tickAt(100), playing: true, speed: 1, now: 100 })).toBe('nudged');
    expect(video.playbackRate).toBeGreaterThan(1);
    expect(video.playbackRate).toBeLessThanOrEqual(1.05);
    // A locate (or a cycle jump back) while playing: a seek to the new place, within a frame.
    expect(sync.step(video, { tick: Bar + 8 * Beat, playing: true, speed: 1, now: 120 })).toBe('seeked');
    expect(frameOf(video)).toBe(sync.target(Bar + 8 * Beat).frame);
  });

  it('reads the presented frame from requestVideoFrameCallback, carried forward to now', () => {
    const { sync, video } = setUp();
    sync.step(video, { tick: Bar, playing: true, speed: 1, now: 0 });
    // currentTime says in step, but the frame on screen at t=1000 was 0.4 s (10 frames) behind the transport.
    video.currentTime = 1;
    sync.framePresented(0.6, 1000);
    expect(sync.step(video, { tick: Bar + 2 * Beat, playing: true, speed: 1, now: 1000 })).toBe('seeked');
  });

  it('waits while a seek is pending, and pauses outside the movie', () => {
    const { sync, video } = setUp();
    sync.step(video, { tick: Bar, playing: true, speed: 1, now: 0 });
    video.seeking = true;
    expect(sync.step(video, { tick: Bar + 5000, playing: true, speed: 1, now: 10 })).toBe('waiting');
    video.seeking = false;
    // Before the movie's start: paused on its first frame.
    sync.step(video, { tick: 0, playing: true, speed: 1, now: 20 });
    expect(video.paused).toBe(true);
    expect(frameOf(video)).toBe(0);
    expect(sync.target(0).inside).toBe(false);
  });
});
