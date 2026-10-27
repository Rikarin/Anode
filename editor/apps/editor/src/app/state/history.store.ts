import { Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import type { UndoHistory } from '@anode/api-client';
import { ANODE_API } from '../core/api';
import { ProjectStore } from './project.store';

const Empty: UndoHistory = { undo: [], redo: [] };

/**
 * The undo and redo stacks (`GET /project/history`), refetched whenever the revision moves, so the Edit menu and the
 * buttons can say what ⌘Z and ⇧⌘Z will do ("Undo Move Region"). Undo and redo themselves go through the project
 * store's queue, after any edit still in flight.
 */
@Injectable({ providedIn: 'root' })
export class HistoryStore {
  private readonly api = inject(ANODE_API);
  private readonly project = inject(ProjectStore);
  readonly history = signal<UndoHistory>(Empty);
  private fetchedFor: number | null = null;

  readonly canUndo = computed(() => this.history().undo.length > 0);
  readonly canRedo = computed(() => this.history().redo.length > 0);
  readonly undoLabel = computed(() => this.history().undo[0]?.label ?? null);
  readonly redoLabel = computed(() => this.history().redo[0]?.label ?? null);

  constructor() {
    effect(() => {
      const open = this.project.isOpen();
      const revision = this.project.revision();
      untracked(() => {
        if (!open) {
          this.fetchedFor = null;
          this.history.set(Empty);
        } else if (revision !== this.fetchedFor) {
          void this.refresh();
        }
      });
    });
  }

  async refresh(): Promise<void> {
    const revision = this.project.revision();
    this.fetchedFor = revision;
    try {
      const history = await this.api.history();
      if (this.project.revision() === revision) {
        this.history.set(history);
      }
    } catch {
      // No history (no project, or a Server without it): the buttons stay as they were; the next revision retries.
      this.fetchedFor = null;
    }
  }

  async undo(): Promise<void> {
    if (this.project.isOpen()) {
      await this.project.undo();
    }
  }

  async redo(): Promise<void> {
    if (this.project.isOpen()) {
      await this.project.redo();
    }
  }
}
