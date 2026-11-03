import type { TempoMap } from '../music/tempo-map';
import { driftCorrection, frameAtSeconds, movieSecondsAt, seekTimeForFrame, type MovieTiming } from './movie-time';

/** As much of an `HTMLVideoElement` as the sync uses (a fake one in tests). */
export interface VideoLike {
  currentTime: number;
  playbackRate: number;
  readonly paused: boolean;
  readonly seeking: boolean;
  play(): Promise<void> | void;
  pause(): void;
}

/** Where the transport is when the sync steps. */
export interface TransportSample {
  /** The playhead, in ticks. */
  tick: number;
  playing: boolean;
  /** Varispeed's speed (1: none). */
  speed: number;
  /** `performance.now()` of the sample (the clock `requestVideoFrameCallback` reports in). */
  now: number;
}

/** What the transport asks of the movie at a step. */
export interface MovieTarget {
  /** The frame of the playhead, clamped to the movie (null: no movie). */
  frame: number | null;
  /** Whether the playhead is inside the movie (else the window shows it is before or after it). */
  inside: boolean;
  /** Movie time of the playhead. */
  seconds: number;
}

/** What a step did, for tests and the window's status. */
export type SyncStep = 'idle' | 'paused' | 'seeked' | 'started' | 'nudged' | 'locked' | 'waiting';

/**
 * Keeps a `<video>` locked to the transport (ADR-0013): the editor plays the movie, seeking to the transport at the
 * frame boundary and correcting drift while playing.
 *
 * - **Stopped, or the playhead outside the movie:** paused, showing the playhead's frame (the first or last frame
 *   outside). Every move of a stopped playhead (a locate, a scrub) seeks when the frame changes.
 * - **Starting to play:** seek to the playhead's movie time, the rate at the transport's speed (varispeed), play.
 * - **Playing:** the video's position is the last frame `requestVideoFrameCallback` presented ({@link framePresented},
 *   carried forward at the playback rate to the step's time), else `currentTime`. {@link driftCorrection} decides: within
 *   half a frame the rate is the speed; past {@link SeekDriftFrames} frames it seeks; in between the rate is nudged by up
 *   to 5 %. A locate or a cycle jump while playing is a large drift, so it seeks.
 * - **While a seek is pending** (`seeking`) nothing more is asked of the element.
 */
export class MovieSync {
  /** The last presented frame: its media time and when it was (or will be) on screen. */
  private presented: { mediaTime: number; at: number } | null = null;

  constructor(
    private readonly movie: () => MovieTiming | null,
    private readonly tempo: () => TempoMap
  ) {}

  /** Where the movie should be for a playhead at `tick`. */
  target(tick: number): MovieTarget {
    const movie = this.movie();
    if (!movie) {
      return { frame: null, inside: false, seconds: 0 };
    }
    const seconds = movieSecondsAt(movie, this.tempo(), tick);
    const frame = frameAtSeconds(movie, seconds);
    const inside = frame >= 0 && frame < movie.frames && seconds < movie.durationSeconds;
    return { frame: Math.min(movie.frames - 1, Math.max(0, frame)), inside, seconds };
  }

  /** A frame the element presented (`requestVideoFrameCallback`'s `mediaTime` and `expectedDisplayTime`). */
  framePresented(mediaTime: number, at: number): void {
    this.presented = { mediaTime, at };
  }

  /** Forgets the presented frame (after a seek, or when the element changes). */
  reset(): void {
    this.presented = null;
  }

  /** One step: called each animation frame with where the transport is. */
  step(video: VideoLike, transport: TransportSample): SyncStep {
    const movie = this.movie();
    if (!movie) {
      return 'idle';
    }
    const target = this.target(transport.tick);
    const frame = target.frame ?? 0;
    if (!transport.playing || !target.inside) {
      if (!video.paused) {
        video.pause();
        this.reset();
      }
      if (video.seeking) {
        return 'waiting';
      }
      if (frameAtSeconds(movie, video.currentTime) !== frame) {
        video.currentTime = seekTimeForFrame(movie, frame);
        this.reset();
        return 'seeked';
      }
      return 'paused';
    }
    const speed = transport.speed > 0 ? transport.speed : 1;
    if (video.paused) {
      video.currentTime = target.seconds;
      video.playbackRate = speed;
      this.reset();
      void Promise.resolve(video.play()).catch(() => undefined);
      return 'started';
    }
    if (video.seeking) {
      return 'waiting';
    }
    const media = this.presented
      ? this.presented.mediaTime + ((transport.now - this.presented.at) / 1000) * video.playbackRate
      : video.currentTime;
    const action = driftCorrection(movie, media, target.seconds, speed);
    switch (action.kind) {
      case 'seek':
        // To the playhead's movie time (not the frame's middle: playback runs on from there).
        video.currentTime = target.seconds;
        video.playbackRate = action.rate;
        this.reset();
        return 'seeked';
      case 'rate':
        video.playbackRate = action.rate;
        return 'nudged';
      default:
        if (video.playbackRate !== action.rate) {
          video.playbackRate = action.rate;
        }
        return 'locked';
    }
  }
}
