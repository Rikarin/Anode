import { Injectable, inject, untracked } from '@angular/core';
import { ApiError } from '@anode/api-client';
import { parsePeaks, peakLevelFor, type Waveform } from '@anode/timeline';
import { ANODE_API } from '../core/api';
import { ProjectStore } from './project.store';

/** Retries for peaks that are not there yet (the Server makes them in the background after an import or a take). */
export const PeakRetryDelaysMs = [500, 1_000, 2_000, 4_000, 8_000];

/**
 * Waveform peaks per file and resolution, fetched once from the binary endpoint (`PeakFormat`) and kept for the
 * session. Concurrent requests for the same file and level share one fetch; a 404 (peaks still being made) is retried
 * with backoff before giving up. Phase 7: a file's peaks are its current version's (`GET /peaks` serves it), so a new
 * `version` (an Audio File Editor operation, or its undo) forgets the old ones.
 */
@Injectable({ providedIn: 'root' })
export class PeaksCache {
  private readonly api = inject(ANODE_API);
  private readonly project = inject(ProjectStore);
  private readonly cache = new Map<string, Promise<Waveform | null>>();
  /** The version of each file whose peaks are cached. */
  private readonly versions = new Map<string, number>();
  /** Waits between retries; replaced in tests. */
  delay: (ms: number) => Promise<void> = ms => new Promise(resolve => setTimeout(resolve, ms));

  /** The kept level for drawing at `framesPerPixel`. */
  levelFor(framesPerPixel: number): number {
    return peakLevelFor(framesPerPixel);
  }

  /** The cached waveform, if it has arrived (synchronous; null while fetching or missing). */
  peek(fileId: string, level: number): Promise<Waveform | null> | undefined {
    return this.cache.get(key(fileId, level));
  }

  /** The waveform of `fileId` at the kept level `level`, or null when the Server has none. */
  get(fileId: string, level: number): Promise<Waveform | null> {
    const version = untracked(() => this.project.files().get(fileId)?.version ?? 0);
    if (this.versions.get(fileId) !== version) {
      this.forget(fileId);
      this.versions.set(fileId, version);
    }
    const k = key(fileId, level);
    let entry = this.cache.get(k);
    if (!entry) {
      entry = this.fetch(fileId, level);
      this.cache.set(k, entry);
      // A failure is not cached: a later request tries again.
      void entry.then(waveform => {
        if (!waveform) {
          this.cache.delete(k);
        }
      });
    }

    return entry;
  }

  /** Forgets every level of a file (it was replaced), or everything (another project was opened). */
  forget(fileId?: string): void {
    if (fileId === undefined) {
      this.cache.clear();
      this.versions.clear();
      return;
    }

    for (const k of [...this.cache.keys()]) {
      if (k.startsWith(`${fileId}@`)) {
        this.cache.delete(k);
      }
    }
  }

  private async fetch(fileId: string, level: number): Promise<Waveform | null> {
    for (let attempt = 0; ; attempt++) {
      try {
        return parsePeaks(await this.api.peaks(fileId, level));
      } catch (error) {
        const notYet = error instanceof ApiError && (error.status === 404 || error.status === 409);
        const wait = PeakRetryDelaysMs[attempt];
        if (!notYet || wait === undefined) {
          return null;
        }

        await this.delay(wait);
      }
    }
  }
}

function key(fileId: string, level: number): string {
  return `${fileId}@${level}`;
}
