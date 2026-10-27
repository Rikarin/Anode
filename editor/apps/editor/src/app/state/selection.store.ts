import { Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { ProjectStore } from './project.store';

/**
 * What is selected in the Tracks area: regions, and the track whose header was clicked last. Ids that disappear from
 * the project (deleted, undone) leave the selection.
 */
@Injectable({ providedIn: 'root' })
export class SelectionStore {
  private readonly project = inject(ProjectStore);
  readonly regions = signal<ReadonlySet<string>>(new Set());
  readonly track = signal<string | null>(null);
  readonly hasRegions = computed(() => this.regions().size > 0);

  constructor() {
    effect(() => {
      const regions = this.project.regions();
      const tracks = this.project.tracks();
      untracked(() => {
        const selected = this.regions();
        const kept = [...selected].filter(id => regions.has(id));
        if (kept.length !== selected.size) {
          this.regions.set(new Set(kept));
        }

        const track = this.track();
        if (track && !tracks.has(track)) {
          this.track.set(null);
        }
      });
    });
  }

  isSelected(regionId: string): boolean {
    return this.regions().has(regionId);
  }

  /** Selects exactly these regions. */
  select(ids: Iterable<string>): void {
    this.regions.set(new Set(ids));
  }

  /** Adds or removes one region (⇧-click). */
  toggle(id: string): void {
    this.regions.update(selected => {
      const next = new Set(selected);
      if (!next.delete(id)) {
        next.add(id);
      }
      return next;
    });
  }

  add(ids: Iterable<string>): void {
    this.regions.update(selected => new Set([...selected, ...ids]));
  }

  clear(): void {
    if (this.regions().size > 0) {
      this.regions.set(new Set());
    }
  }
}
