import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { ProjectStore } from '../state/project.store';

/**
 * The Step Sequencer (Logic's pattern editor) for the selected pattern region (docs/07-phase-6.md). Phase 6
 * placeholder.
 */
@Component({
  selector: 'an-step-sequencer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'flex h-full min-h-0 flex-col', role: 'region', 'aria-label': 'Step Sequencer' },
  template: `<p class="text-foreground-muted p-3 text-xs">Select a pattern region to edit its steps.</p>`
})
export class StepSequencer {
  protected readonly project = inject(ProjectStore);
}
