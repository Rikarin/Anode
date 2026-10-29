import { signal } from '@angular/core';
import type { SampleRange } from '@anode/api-client';
import { type FileView, frameAtX, tileRequest, TileValues } from './audio-file-geometry';

/** One fetched tile: raw samples (bucket 1) or min and max per bucket, per channel. */
export interface SampleTile {
  start: number;
  bucket: number;
  /** Values per channel (frames at bucket 1, buckets otherwise). */
  length: number;
  samples: Float32Array[] | null;
  min: Float32Array[] | null;
  max: Float32Array[] | null;
}

/** Fetches a range of a file's current version: `/project/files/{fileId}/samples`. */
export type FetchSamples = (fileId: string, start: number, frames: number, bucket: number) => Promise<SampleRange>;

/** A tile from the Server's answer. */
export function tileOf(range: SampleRange): SampleTile {
  const floats = (values: number[][] | null | undefined) => values?.map(c => Float32Array.from(c)) ?? null;
  const samples = floats(range.samples);
  const min = floats(range.min);
  const max = floats(range.max);
  return {
    start: range.startFrame,
    bucket: range.bucket,
    length: (samples ?? min)?.[0]?.length ?? 0,
    samples,
    min,
    max
  };
}

/**
 * The Audio File Editor's cache of `/samples` tiles of one file version, by bucket and tile index (a tile is
 * {@link TileValues} values). A new file or version empties it, so the waveform refetches; at most `limit` tiles are
 * kept (the oldest go first). `revision` changes when a tile arrives, for the canvas to redraw.
 */
export class SampleTiles {
  private readonly tiles = new Map<string, SampleTile>();
  private readonly pending = new Set<string>();
  private source = '';
  private readonly changed = signal(0);
  readonly revision = this.changed.asReadonly();

  constructor(
    private readonly fetchSamples: FetchSamples,
    private readonly limit = 160
  ) {}

  /** Points the cache at a file version (and its length): a different one empties it. */
  setSource(fileId: string, version: number, frames: number): void {
    const source = `${fileId}@${version}:${frames}`;
    if (source !== this.source) {
      this.source = source;
      this.tiles.clear();
      this.pending.clear();
      this.changed.update(n => n + 1);
    }
  }

  get(bucket: number, tile: number): SampleTile | undefined {
    return this.tiles.get(`${bucket}:${tile}`);
  }

  /** Fetches the tiles not cached or on their way; resolves when they have all arrived (or failed). */
  async ensure(fileId: string, bucket: number, tiles: readonly number[], totalFrames: number): Promise<void> {
    const source = this.source;
    const missing = tiles.filter(t => !this.tiles.has(`${bucket}:${t}`) && !this.pending.has(`${bucket}:${t}`));
    await Promise.all(
      missing.map(async tile => {
        const key = `${bucket}:${tile}`;
        const { start, frames } = tileRequest(tile, bucket, totalFrames);
        if (frames <= 0) {
          return;
        }
        this.pending.add(key);
        try {
          const range = await this.fetchSamples(fileId, start, frames, bucket);
          if (this.source !== source) {
            return; // the file changed meanwhile: this answer is of an old version
          }
          this.tiles.set(key, tileOf(range));
          while (this.tiles.size > this.limit) {
            const oldest = this.tiles.keys().next().value;
            if (oldest === undefined) {
              break;
            }
            this.tiles.delete(oldest);
          }
          this.changed.update(n => n + 1);
        } catch {
          // left missing: the next scroll or zoom asks again
        } finally {
          if (this.source === source) {
            this.pending.delete(key);
          }
        }
      })
    );
  }

  /** A raw sample (bucket-1 tiles), undefined when not fetched. */
  sample(channel: number, frame: number): number | undefined {
    const tile = this.get(1, Math.floor(frame / TileValues));
    return tile?.samples?.[channel]?.[frame - tile.start];
  }

  /** The min and max of bucket `index` (at `bucket` frames a bucket), undefined when not fetched. */
  bucketPeak(channel: number, bucket: number, index: number): [number, number] | undefined {
    const tile = this.get(bucket, Math.floor(index / TileValues));
    if (!tile) {
      return undefined;
    }
    const i = index - tile.start / bucket;
    const lo = tile.min?.[channel]?.[i];
    const hi = tile.max?.[channel]?.[i];
    return lo === undefined || hi === undefined ? undefined : [lo, hi];
  }
}

/**
 * Min and max per pixel column over `width` px of the view, from `bucket`-frame peaks (NaN where nothing is fetched,
 * or past the file).
 */
export function columnPeaks(
  tiles: Pick<SampleTiles, 'bucketPeak'>,
  channel: number,
  view: FileView,
  width: number,
  bucket: number,
  totalFrames: number
): { min: Float32Array; max: Float32Array } {
  const min = new Float32Array(width).fill(Number.NaN);
  const max = new Float32Array(width).fill(Number.NaN);
  for (let x = 0; x < width; x++) {
    const from = frameAtX(x, view);
    const to = Math.min(totalFrames, frameAtX(x + 1, view));
    if (to <= 0 || from >= totalFrames) {
      continue;
    }
    const first = Math.floor(Math.max(0, from) / bucket);
    const last = Math.max(first, Math.ceil(to / bucket) - 1);
    let lo = Infinity;
    let hi = -Infinity;
    for (let b = first; b <= last; b++) {
      const peak = tiles.bucketPeak(channel, bucket, b);
      if (peak) {
        lo = Math.min(lo, peak[0]);
        hi = Math.max(hi, peak[1]);
      }
    }
    if (lo <= hi) {
      min[x] = lo;
      max[x] = hi;
    }
  }
  return { min, max };
}
