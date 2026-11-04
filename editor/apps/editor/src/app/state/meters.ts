import { Injectable, inject } from '@angular/core';
import type { InsertReading, MeterType, TrackMeter } from '@anode/api-client';
import { EventStream } from '../core/events';

/** The key the master strip's level is kept under. */
export const MasterMeter = 'master';

/** A stereo level, linear (0…1, above 1 clips). */
export interface StereoLevel {
  left: number;
  right: number;
}

/** A strip's levels as its meter type shows them: peaks, and the RMS or loudness the Server sent for its type. */
interface StripLevel extends StereoLevel {
  rmsLeft: number | null;
  rmsRight: number | null;
  lufs: number | null;
}

/** What AES17 adds to true RMS, so a K-system meter reads a sine at its peak, in gain (+3.01 dB). */
const Aes17 = Math.SQRT2;

/** Where a K-system meter's 0 sits on a dBFS scale, by type; null for the others. */
export function meterZeroDb(type: MeterType): number | null {
  return type === 'k20' ? -20 : type === 'k14' ? -14 : type === 'k12' ? -12 : null;
}

/** The linear level a meter of `type` shows for a strip's levels; peaks until its first RMS or loudness frame. */
export function meterLevel(level: StereoLevel & Partial<StripLevel>, type: MeterType): StereoLevel {
  if (type === 'peak') {
    return level;
  }
  if (type === 'lufs') {
    const linear = level.lufs === null || level.lufs === undefined ? 0 : 10 ** (level.lufs / 20);
    return { left: linear, right: linear };
  }
  const scale = type === 'rms' ? 1 : Aes17;
  return { left: (level.rmsLeft ?? 0) * scale, right: (level.rmsRight ?? 0) * scale };
}

/**
 * The latest meter levels per track and for the master, straight from the Server's `meters` frames. Deliberately not
 * signals: about 30 frames a second would otherwise run change detection for every strip. Meters read {@link level}
 * from their own animation frames (docs/01-implementation-plan.md §6.2).
 */
@Injectable({ providedIn: 'root' })
export class MeterBus {
  private readonly levels = new Map<string, StripLevel>();
  /** Phase 10: per-channel peaks of strips wider than stereo (`TrackMeter.peaks`), when the Server sends them. */
  private readonly channels = new Map<string, number[]>();
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

  /** The latest level of a track (or {@link MasterMeter}) as a meter of `type` shows it. */
  levelAs(key: string, type: MeterType): StereoLevel {
    return meterLevel(this.levels.get(key) ?? Silence, type);
  }

  /**
   * The latest per-channel levels of a strip with `count` channels (a surround strip's multichannel meter): the
   * Server's `peaks` when it sends them, else left and right on the first two channels.
   */
  channelLevels(key: string, count: number): readonly number[] {
    const peaks = this.channels.get(key);
    if (peaks && peaks.length >= count) {
      return peaks;
    }
    const level = this.level(key);
    return Array.from({ length: count }, (_, i) => peaks?.[i] ?? (i === 0 ? level.left : i === 1 ? level.right : 0));
  }

  private set(key: string, meter: TrackMeter): void {
    if (meter.peaks) {
      this.channels.set(key, meter.peaks);
    } else {
      this.channels.delete(key);
    }
    const level = this.levels.get(key);
    const rmsLeft = meter.rmsLeft ?? null;
    const rmsRight = meter.rmsRight ?? null;
    const lufs = meter.lufs ?? null;
    if (level) {
      level.left = meter.peakLeft;
      level.right = meter.peakRight;
      level.rmsLeft = rmsLeft;
      level.rmsRight = rmsRight;
      level.lufs = lufs;
    } else {
      this.levels.set(key, { left: meter.peakLeft, right: meter.peakRight, rmsLeft, rmsRight, lufs });
    }
  }
}

function readingKey(trackId: string | null, insertId: string): string {
  return `${trackId ?? MasterMeter}/${insertId}`;
}

const Silence: StripLevel = Object.freeze({ left: 0, right: 0, rmsLeft: null, rmsRight: null, lufs: null });
