import { Injectable, computed, inject, signal } from '@angular/core';
import type { OfflineCandidate, OfflineFile, RelinkTarget } from '@anode/api-client';
import { ANODE_API } from '../core/api';
import { Notify } from '../core/notify';
import { ProjectStore } from './project.store';

/**
 * The Missing Files window's work: what is offline comes with the project and `offlineChanged`; this looks for it in
 * a folder and relinks it. The Server judges candidates by what they hold (length, rate, channels), so what a search
 * found with a `problem` is shown and never offered.
 */
@Injectable({ providedIn: 'root' })
export class OfflineFilesStore {
  private readonly api = inject(ANODE_API);
  private readonly project = inject(ProjectStore);
  private readonly notify = inject(Notify);

  /** What the last search found. */
  readonly candidates = signal<readonly OfflineCandidate[]>([]);
  readonly truncated = signal(false);
  /** The folder last searched, or null before a search. */
  readonly searched = signal<string | null>(null);
  readonly searching = signal(false);
  readonly relinking = signal(false);

  /** The first candidate of each file that matches, for files still offline. */
  readonly matches = computed(() => {
    const offline = new Set(this.project.offline().files.map(f => f.fileId));
    const matches = new Map<string, OfflineCandidate>();
    for (const candidate of this.candidates()) {
      if (candidate.problem == null && offline.has(candidate.fileId) && !matches.has(candidate.fileId)) {
        matches.set(candidate.fileId, candidate);
      }
    }
    return matches;
  });

  /** Why the files found under a file's name cannot stand in for it, when none can. */
  problemOf(file: OfflineFile): string | null {
    const named = this.candidates().filter(c => c.fileId === file.fileId);
    return named.some(c => c.problem == null) ? null : (named[0]?.problem ?? null);
  }

  /** Reads the disk again (a file put back comes online), then looks in `directory` (left out: the project's folder). */
  async search(directory?: string): Promise<void> {
    this.searching.set(true);
    try {
      const found = await this.notify.run(
        () => this.api.searchOffline({ directory: directory?.trim() ? directory.trim() : null }),
        'Could not search that folder'
      );
      if (found) {
        this.candidates.set(found.candidates);
        this.truncated.set(found.truncated);
        this.searched.set(found.directory);
      }
    } finally {
      this.searching.set(false);
    }
  }

  /** Relinks files, one undo step; false when the Server refused (a file that does not match: it says why). */
  async relink(files: RelinkTarget[]): Promise<boolean> {
    this.relinking.set(true);
    try {
      return (await this.notify.run(() => this.api.relinkFiles({ files }), 'Could not relink')) !== null;
    } finally {
      this.relinking.set(false);
    }
  }

  /** Relinks every file a search found. */
  relinkFound(): Promise<boolean> {
    return this.relink([...this.matches().values()].map(c => ({ fileId: c.fileId, path: c.path })));
  }

  /** Forgets the last search (the window closed, or another project opened). */
  reset(): void {
    this.candidates.set([]);
    this.truncated.set(false);
    this.searched.set(null);
  }
}
