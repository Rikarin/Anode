import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { ProjectStore } from '../state/project.store';

/**
 * The Drum Machine Designer for the selected DMD track: its pad grid and the selected pad's instrument
 * (docs/07-phase-6.md). Phase 6 placeholder.
 */
@Component({
  selector: 'an-drum-machine-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'flex h-full min-h-0 flex-col', role: 'region', 'aria-label': 'Drum Machine Designer' },
  template: `<p class="text-foreground-muted p-3 text-xs">Select a Drum Machine Designer track.</p>`
})
export class DrumMachineEditor {
  protected readonly project = inject(ProjectStore);
}
