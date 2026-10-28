import { Injectable, inject } from '@angular/core';
import type { InsertReading, TrackMeter } from '@anode/api-client';
import { EventStream } from '../core/events';

/** The key the master strip's level is kept under. */
export const MasterMeter = 'master';

/** A stereo level, linear (0…1, above 1 clips). */
export interface StereoLevel {
  left: number;
  right: number;
}

/**
 * The latest meter levels per track and for the master, straight from the Server's `meters` frames. Deliberately not
 * signals: about 30 frames a second would otherwise run change detection for every strip. Meters read {@link level}
 * from their own animation frames (docs/01-implementation-plan.md §6.2).
 */
@Injectable({ providedIn: 'root' })
export class MeterBus {
  private readonly levels = new Map<string, StereoLevel>();
  private readonly readings = new Map<string, number>();
  /** When the last frame arrived (performance.now()), so a meter can tell a stalled stream. */
  lastFrame = 0;

  constructor() {
    inject(EventStream).events$.subscribe(message => {
      if (message.type === 'meters') {
        this.apply(message.tracks, message.master);
        this.applyReadings(message.readings ?? []);
      }
    });
  }

  apply(tracks: readonly TrackMeter[], master: TrackMeter, now = performance.now()): void {
    for (const meter of tracks) {
      this.set(meter.trackId, meter);
    }
    this.set(MasterMeter, master);
    this.lastFrame = now;
  }

  /** Insert readings (gain reduction, loudness) of the latest frame; the ones it lacks are gone. */
  applyReadings(readings: readonly InsertReading[]): void {
    this.readings.clear();
    for (const reading of readings) {
      this.readings.set(readingKey(reading.trackId, reading.insertId), reading.value);
    }
  }

  /** An insert's latest reading (a compressor's gain reduction in dB, a meter's LUFS), or null when it sent none. */
  reading(trackId: string | null, insertId: string): number | null {
    return this.readings.get(readingKey(trackId, insertId)) ?? null;
  }

  /** The latest level of a track (or {@link MasterMeter}); silence when none arrived. */
  level(key: string): StereoLevel {
    return this.levels.get(key) ?? Silence;
  }

  private set(key: string, meter: TrackMeter): void {
    const level = this.levels.get(key);
    if (level) {
      level.left = meter.peakLeft;
      level.right = meter.peakRight;
    } else {
      this.levels.set(key, { left: meter.peakLeft, right: meter.peakRight });
    }
  }
}

function readingKey(trackId: string | null, insertId: string): string {
  return `${trackId ?? MasterMeter}/${insertId}`;
}

const Silence: StereoLevel = Object.freeze({ left: 0, right: 0 });
