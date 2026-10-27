import { Injectable, computed, signal } from '@angular/core';

/** Ticks per quarter note: the project's musical time base (docs/01-implementation-plan.md §5.1). */
export const Ppq = 960;

export interface TimeSignature {
  numerator: number;
  denominator: number;
}

/** A musical position as Logic's LCD shows it: bar, beat, division and tick, all counted from 1. */
export interface BarsBeats {
  bar: number;
  beat: number;
  division: number;
  tick: number;
}

/**
 * Splits `ticks` into bars, beats, divisions (`division` notes, 16ths by default) and ticks under a constant time
 * signature. A tempo map with signature changes replaces this once the Server owns the project's global tracks.
 */
export function barsBeats(ticks: number, signature: TimeSignature, division = 16): BarsBeats {
  const whole = Math.max(0, Math.floor(ticks));
  const beatTicks = (Ppq * 4) / signature.denominator;
  const barTicks = beatTicks * signature.numerator;
  const divisionTicks = Math.min(beatTicks, (Ppq * 4) / division);
  const inBar = whole % barTicks;
  const inBeat = inBar % beatTicks;
  return {
    bar: Math.floor(whole / barTicks) + 1,
    beat: Math.floor(inBar / beatTicks) + 1,
    division: Math.floor(inBeat / divisionTicks) + 1,
    tick: (inBeat % divisionTicks) + 1
  };
}

/** `hh:mm:ss.mmm` for a time in seconds. */
export function clockTime(seconds: number): string {
  const ms = Math.max(0, Math.round(seconds * 1000));
  const pad = (value: number, width = 2) => String(value).padStart(width, '0');
  return `${pad(Math.floor(ms / 3_600_000))}:${pad(Math.floor(ms / 60_000) % 60)}:${pad(Math.floor(ms / 1000) % 60)}.${pad(ms % 1000, 3)}`;
}

/**
 * The transport as the editor shows it. Local only for now: the engine host owns the real transport, and the playhead
 * will follow its `transport` frames, extrapolated between them (docs/01-implementation-plan.md §4.2). Times are
 * `performance.now()` milliseconds, passed in so tests control the clock.
 */
@Injectable({ providedIn: 'root' })
export class TransportStore {
  readonly playing = signal(false);
  readonly recording = signal(false);
  readonly cycle = signal(false);
  readonly tempo = signal(120);
  readonly signature = signal<TimeSignature>({ numerator: 4, denominator: 4 });
  /** Where playback starts (stopped), or where it started (playing). */
  readonly anchorTicks = signal(0);
  private anchorTime = 0;

  readonly ticksPerSecond = computed(() => (this.tempo() / 60) * Ppq);

  /** The playhead at `now`. Not a signal while playing: the LCD and playhead read it from animation frames. */
  positionAt(now: number): number {
    return this.playing()
      ? this.anchorTicks() + ((now - this.anchorTime) / 1000) * this.ticksPerSecond()
      : this.anchorTicks();
  }

  play(now = performance.now()): void {
    if (this.playing()) {
      return;
    }

    this.anchorTime = now;
    this.playing.set(true);
  }

  /** Stops where the playhead is; stopping while stopped returns it to the start, as Logic's Stop does. */
  stop(now = performance.now()): void {
    if (!this.playing()) {
      this.anchorTicks.set(0);
      return;
    }

    this.anchorTicks.set(this.positionAt(now));
    this.playing.set(false);
    this.recording.set(false);
  }

  record(now = performance.now()): void {
    this.recording.set(true);
    this.play(now);
  }

  toggleCycle(): void {
    this.cycle.update(on => !on);
  }

  /** Moves the playhead, keeping playback running from there. */
  locate(ticks: number, now = performance.now()): void {
    this.anchorTicks.set(Math.max(0, ticks));
    this.anchorTime = now;
  }
}
