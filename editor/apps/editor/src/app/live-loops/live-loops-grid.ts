import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { ProjectStore } from '../state/project.store';

/**
 * The Live Loops grid beside the Tracks area (Logic's): scenes × tracks of cells, launched at the quantise boundary
 * (docs/07-phase-6.md). Shown while `liveLoops.shown`. Phase 6 placeholder.
 */
@Component({
  selector: 'an-live-loops-grid',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'flex h-full min-h-0 flex-col', role: 'region', 'aria-label': 'Live Loops' },
  template: ``
})
export class LiveLoopsGrid {
  protected readonly project = inject(ProjectStore);
}
