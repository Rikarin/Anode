import { Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { ProjectStore } from './project.store';

/**
 * What is selected: regions in the Tracks area (audio, MIDI and pattern regions, by id), the track whose header was
 * clicked last, and notes in the Piano Roll (by note id, unique in the project). Ids that disappear from the project
 * (deleted, undone) leave the selection.
 */
@Injectable({ providedIn: 'root' })
export class SelectionStore {
  private readonly project = inject(ProjectStore);
  readonly regions = signal<ReadonlySet<string>>(new Set());
  readonly track = signal<string | null>(null);
  /** Tracks selected in the Tracks area (⇧- or ⌘-click adds), for track stacks; the last clicked is {@link track}. */
  readonly tracks = signal<ReadonlySet<string>>(new Set());
  readonly notes = signal<ReadonlySet<number>>(new Set());
  readonly hasRegions = computed(() => this.regions().size > 0);

  constructor() {
    effect(() => {
      const regions = this.project.regions();
      const midi = this.project.midiRegions();
      const folders = this.project.takeFolders();
      const patterns = this.project.patternRegions();
      const tracks = this.project.tracks();
      untracked(() => {
        const selected = this.regions();
        const kept = [...selected].filter(id => regions.has(id) || midi.has(id) || folders.has(id) || patterns.has(id));
        if (kept.length !== selected.size) {
          this.regions.set(new Set(kept));
        }

        const track = this.track();
        if (track && !tracks.has(track)) {
          this.track.set(null);
        }
        const selectedTracks = this.tracks();
        if ([...selectedTracks].some(id => !tracks.has(id))) {
          this.tracks.set(new Set([...selectedTracks].filter(id => tracks.has(id))));
        }

        const notes = this.notes();
        if (notes.size > 0) {
          const all = new Set<number>();
          for (const region of midi.values()) {
            for (const note of region.notes) {
              all.add(note.id);
            }
          }
          const keptNotes = [...notes].filter(id => all.has(id));
          if (keptNotes.length !== notes.size) {
            this.notes.set(new Set(keptNotes));
          }
        }
      });
    });
  }

  /** Selects a track (the clicked header or lane); with `add`, adds it to (or removes it from) the selected tracks. */
  selectTrack(id: string, add = false): void {
    this.track.set(id);
    if (add) {
      this.tracks.update(set => {
        const next = new Set(set);
        if (!next.delete(id)) {
          next.add(id);
        }
        return next;
      });
    } else {
      this.tracks.set(new Set([id]));
    }
  }

  /** The tracks a track command applies to: the selected ones, or the last clicked. */
  trackSelection(): string[] {
    const set = this.tracks();
    const last = this.track();
    return set.size > 0 ? [...set] : last ? [last] : [];
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

  /** Selects exactly these notes. */
  selectNotes(ids: Iterable<number>): void {
    this.notes.set(new Set(ids));
  }

  toggleNote(id: number): void {
    this.notes.update(selected => {
      const next = new Set(selected);
      if (!next.delete(id)) {
        next.add(id);
      }
      return next;
    });
  }

  clearNotes(): void {
    if (this.notes().size > 0) {
      this.notes.set(new Set());
    }
  }
}
