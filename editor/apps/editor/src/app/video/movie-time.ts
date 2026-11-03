import type { Movie, VideoFrameRate } from '@anode/api-client';
import type { TempoMap } from '../music/tempo-map';

/**
 * The project tick ↔ movie time mapping (docs/11-phase-10.md, "Video"), mirroring the Server's: at project position p
 * the movie shows movie time `seconds(p) − seconds(startTick) + offsetSeconds` (through the tempo map), frame
 * `floor(m × rate)`. Varispeed does not enter: it scales both the project's and the movie's clocks (the player's
 * `playbackRate` follows it).
 */
export type MovieTiming = Pick<Movie, 'startTick' | 'offsetSeconds' | 'frameRate' | 'durationSeconds' | 'frames'>;

/** A frame rate in frames per second (`VideoFrameRate.FramesPerSecond`). */
export function framesPerSecond(rate: VideoFrameRate): number {
  return rate.denominator > 0 ? rate.numerator / rate.denominator : 0;
}

/** Movie time (seconds from the movie's first frame) at project tick `tick`; may be outside the movie. */
export function movieSecondsAt(movie: MovieTiming, tempo: TempoMap, tick: number): number {
  return tempo.secondsAt(tick) - tempo.secondsAt(movie.startTick) + movie.offsetSeconds;
}

/** The project tick where movie time `seconds` plays: the inverse of {@link movieSecondsAt}. */
export function tickAtMovieSeconds(movie: MovieTiming, tempo: TempoMap, seconds: number): number {
  return tempo.tickAt(seconds - movie.offsetSeconds + tempo.secondsAt(movie.startTick));
}

/** A tolerance for floating-point noise at frame boundaries (a microsecond's worth of a frame at 1000 fps). */
const Epsilon = 1e-6;

/** The frame shown at movie time `seconds` (`floor(m × rate)`); may be outside 0…frames − 1. */
export function frameAtSeconds(movie: Pick<MovieTiming, 'frameRate'>, seconds: number): number {
  return Math.floor(seconds * framesPerSecond(movie.frameRate) + Epsilon);
}

/** The frame the movie shows at project tick `tick`, or null when the movie is not there (before its start or after). */
export function movieFrameAt(movie: MovieTiming, tempo: TempoMap, tick: number): number | null {
  const frame = frameAtSeconds(movie, movieSecondsAt(movie, tempo, tick));
  return frame >= 0 && frame < movie.frames ? frame : null;
}

/** Movie time of a frame's start. */
export function frameStartSeconds(movie: Pick<MovieTiming, 'frameRate'>, frame: number): number {
  const fps = framesPerSecond(movie.frameRate);
  return fps > 0 ? frame / fps : 0;
}

/** The project tick where a frame starts. */
export function tickAtFrame(movie: MovieTiming, tempo: TempoMap, frame: number): number {
  return tickAtMovieSeconds(movie, tempo, frameStartSeconds(movie, frame));
}

/**
 * Where a `<video>` element should be for a frame: the middle of the frame, so a decoder that rounds either way still
 * shows it (seeking to the exact boundary can land on the frame before).
 */
export function seekTimeForFrame(movie: Pick<MovieTiming, 'frameRate'>, frame: number): number {
  const fps = framesPerSecond(movie.frameRate);
  return fps > 0 ? (frame + 0.5) / fps : 0;
}

/** The project tick span the movie covers: its first frame (at `offsetSeconds` 0) to its end. */
export function movieSpan(movie: MovieTiming, tempo: TempoMap): { startTick: number; endTick: number } {
  return {
    startTick: tickAtMovieSeconds(movie, tempo, 0),
    endTick: tickAtMovieSeconds(movie, tempo, movie.durationSeconds)
  };
}

/** A timecode `hh:mm:ss:ff` for movie time `seconds` from a start timecode (seconds), at the movie's rate. */
export function timecode(movie: Pick<Movie, 'frameRate' | 'timecodeStartSeconds'>, seconds: number): string {
  const fps = framesPerSecond(movie.frameRate);
  const nominal = Math.max(1, Math.round(fps));
  const total = Math.max(0, Math.floor((seconds + movie.timecodeStartSeconds) * fps + Epsilon));
  const frames = total % nominal;
  const wholeSeconds = Math.floor(total / nominal);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${pad(Math.floor(wholeSeconds / 3600))}:${pad(Math.floor(wholeSeconds / 60) % 60)}:${pad(wholeSeconds % 60)}:${pad(frames)}`;
}

/** A frame rate as people write it: 25, 29.97, 23.976. */
export function formatFrameRate(rate: VideoFrameRate): string {
  const fps = framesPerSecond(rate);
  return Number.isInteger(fps) ? String(fps) : fps.toFixed(3).replace(/0+$/, '');
}

// ---- drift correction while playing

/** What the player does about the difference between where the video is and where the transport says it should be. */
export type DriftAction =
  { kind: 'none'; rate: number } | { kind: 'rate'; rate: number } | { kind: 'seek'; time: number; rate: number };

/** Drift beyond which the player seeks rather than nudging its rate, in frames. */
export const SeekDriftFrames = 6;
/** The most the rate is nudged, as a fraction of the nominal speed (±5 %: not audible on picture, quick to catch up). */
export const MaxRateNudge = 0.05;
/** The seconds a rate nudge aims to close the gap in. */
const CatchUpSeconds = 1;

/**
 * Drift correction (ADR-0013): the video's media time `videoSeconds` (from `requestVideoFrameCallback`'s metadata, or
 * `currentTime`) against the movie time the transport is at, `targetSeconds`. Within half a frame: play at `speed`
 * (varispeed). Beyond {@link SeekDriftFrames} frames: seek to the target's frame. In between: nudge the rate (at most
 * {@link MaxRateNudge}) to close the gap in about a second.
 */
export function driftCorrection(
  movie: Pick<MovieTiming, 'frameRate'>,
  videoSeconds: number,
  targetSeconds: number,
  speed = 1
): DriftAction {
  const fps = framesPerSecond(movie.frameRate) || 25;
  const frame = 1 / fps;
  const drift = targetSeconds - videoSeconds; // > 0: the video is behind
  if (Math.abs(drift) <= frame / 2) {
    return { kind: 'none', rate: speed };
  }
  if (Math.abs(drift) > SeekDriftFrames * frame) {
    return { kind: 'seek', time: seekTimeForFrame(movie, frameAtSeconds(movie, targetSeconds)), rate: speed };
  }
  const nudge = Math.max(-MaxRateNudge, Math.min(MaxRateNudge, drift / CatchUpSeconds));
  return { kind: 'rate', rate: speed * (1 + nudge) };
}

/** Thumbnails for the movie track: the frames to show for a lane of `widthPx` pixels with thumbnails `thumbPx` wide. */
export function thumbnailFrames(
  movie: MovieTiming,
  tempo: TempoMap,
  view: { startTick: number; endTick: number; widthPx: number },
  thumbPx: number
): { frame: number; tick: number }[] {
  if (view.widthPx <= 0 || view.endTick <= view.startTick || thumbPx <= 0 || movie.frames <= 0) {
    return [];
  }
  const span = movieSpan(movie, tempo);
  const ticksPerPx = (view.endTick - view.startTick) / view.widthPx;
  const step = thumbPx * ticksPerPx;
  // Thumbnails sit on a grid from the movie's start, so they stay put while the view scrolls.
  const first = Math.max(0, Math.floor((view.startTick - span.startTick) / step));
  const out: { frame: number; tick: number }[] = [];
  let lastFrame = -1;
  for (let i = first; ; i++) {
    const tick = span.startTick + i * step;
    if (tick >= view.endTick || tick >= span.endTick) {
      break;
    }
    const frame = Math.min(movie.frames - 1, Math.max(0, frameAtSeconds(movie, movieSecondsAt(movie, tempo, tick))));
    // Zoomed in further than one frame per thumbnail: a frame once.
    if (frame !== lastFrame) {
      out.push({ frame, tick });
      lastFrame = frame;
    }
    if (out.length > 500) {
      break;
    }
  }
  return out;
}
