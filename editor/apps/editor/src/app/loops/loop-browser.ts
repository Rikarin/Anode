import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { ProjectStore } from '../state/project.store';

/**
 * The Loop Browser (Logic's, O): keyword buttons, columns, search, key and scale filters, favourites, in-sync preview,
 * and dragging loops to the Tracks area and to Live Loops cells (docs/07-phase-6.md). Phase 6 placeholder.
 */
@Component({
  selector: 'an-loop-browser',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'flex h-full min-h-0 flex-col', role: 'region', 'aria-label': 'Loop Browser' },
  template: `<p class="text-foreground-muted p-3 text-xs">No loops yet.</p>`
})
export class LoopBrowser {
  protected readonly project = inject(ProjectStore);
}
