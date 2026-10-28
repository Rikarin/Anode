import { Injectable, computed, inject, signal } from '@angular/core';
import type { PatchInfo, TrackKind } from '@anode/api-client';
import { ANODE_API } from '../core/api';
import { EventStream } from '../core/events';
import { Notify } from '../core/notify';
import { ProjectStore } from './project.store';

/** Patches by category, in name order, the categories sorted too. */
export function patchCategories(patches: readonly PatchInfo[]): { name: string; patches: PatchInfo[] }[] {
  const byCategory = new Map<string, PatchInfo[]>();
  for (const patch of patches) {
    const name = patch.category.trim() || 'Uncategorised';
    byCategory.set(name, [...(byCategory.get(name) ?? []), patch]);
  }
  return [...byCategory]
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([name, list]) => ({ name, patches: list.sort((a, b) => a.name.localeCompare(b.name)) }));
}

/** Whether a patch suits a track: patches are for a kind of track; audio and aux strips take each other's. */
export function patchFits(patch: PatchInfo, kind: TrackKind): boolean {
  const family = (k: TrackKind) => (k === 'aux' ? 'audio' : k);
  return family(patch.kind) === family(kind);
}

/**
 * The Library's channel-strip patches (`/library/patches`, docs/05-phase-4.md): listed on every (re)connection, saved
 * from a track, applied to a track (`applyPatch`, one undo step) and deleted.
 */
@Injectable({ providedIn: 'root' })
export class PatchesStore {
  private readonly api = inject(ANODE_API);
  private readonly notify = inject(Notify);
  private readonly project = inject(ProjectStore);
  readonly patches = signal<readonly PatchInfo[]>([]);
  readonly loading = signal(false);
  readonly categories = computed(() => patchCategories(this.patches()));

  constructor() {
    const events = inject(EventStream);
    events.connected$.subscribe(() => void this.refetch());
    if (events.status() === 'open') {
      void this.refetch();
    }
  }

  async refetch(): Promise<void> {
    this.loading.set(true);
    try {
      this.patches.set(await this.api.patches());
    } catch {
      this.patches.set([]); // an older Server keeps no patches
    } finally {
      this.loading.set(false);
    }
  }

  /** Saves a track's channel strip as a patch; resolves with it, or null when refused (reported). */
  async save(trackId: string, name: string, category: string): Promise<PatchInfo | null> {
    const patch = await this.notify.run(
      () => this.api.savePatch({ trackId, name: name.trim(), category: category.trim() }),
      'Could not save the patch'
    );
    if (patch) {
      this.patches.update(list => [...list.filter(p => p.id !== patch.id), patch]);
    }
    return patch;
  }

  apply(trackId: string, patch: PatchInfo): Promise<unknown> {
    return this.project.execute([{ type: 'applyPatch', trackId, patchId: patch.id }], `Patch ${patch.name}`);
  }

  async remove(patch: PatchInfo): Promise<void> {
    const done = await this.notify.run(
      () => this.api.deletePatch(patch.id).then(() => true),
      'Could not delete the patch'
    );
    if (done) {
      this.patches.update(list => list.filter(p => p.id !== patch.id));
    }
  }
}
