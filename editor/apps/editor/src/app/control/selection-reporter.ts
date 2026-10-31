import { DestroyRef, effect, inject, Injectable } from '@angular/core';
import { ANODE_API } from '../core/api';
import { EventStream } from '../core/events';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';

/** How long the selection has to rest before it is reported (clicking through tracks sends the last one). */
export const SelectionDebounceMs = 120;

/**
 * Tells the Server which track is selected (`PUT /project/selection`, docs/09-phase-8.md): selected-track controller
 * assignments, control surfaces and OSC's `/anode/selected/…` follow it. Reported shortly after it changes, once per
 * change, and again after a reconnection or when a project opens (the Server forgets it with the project).
 */
@Injectable({ providedIn: 'root' })
export class SelectionReporter {
  private readonly api = inject(ANODE_API);
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);
  private timer: ReturnType<typeof setTimeout> | null = null;
  /** What the Server was last told, for the open project (`undefined`: nothing yet). */
  private sent: string | null | undefined = undefined;

  constructor() {
    effect(() => {
      const projectId = this.project.info()?.id ?? null;
      const trackId = this.selection.track();
      if (projectId === null) {
        this.sent = undefined;
        this.cancel();
        return;
      }
      this.schedule(trackId);
    });
    inject(EventStream).connected$.subscribe(() => {
      this.sent = undefined;
      if (this.project.isOpen()) {
        this.schedule(this.selection.track());
      }
    });
    inject(DestroyRef).onDestroy(() => this.cancel());
  }

  private schedule(trackId: string | null): void {
    this.cancel();
    this.timer = setTimeout(() => {
      this.timer = null;
      void this.report(trackId);
    }, SelectionDebounceMs);
  }

  private cancel(): void {
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = null;
    }
  }

  private async report(trackId: string | null): Promise<void> {
    if (trackId === this.sent || !this.project.isOpen()) {
      return;
    }
    this.sent = trackId;
    try {
      await this.api.setSelection(trackId);
    } catch {
      // A track deleted meanwhile (or a Server without the endpoint): the next change reports again.
      this.sent = undefined;
    }
  }
}
