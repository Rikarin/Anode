import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { ProjectStore } from '../state/project.store';

/**
 * Smart Controls (Logic's, B) for the selected track: its layout of knobs, switches and the XY pad, the inspector with
 * mappings and the mapping graph, automatic mapping, Compare and the arpeggiator button (docs/07-phase-6.md). Phase 6
 * placeholder.
 */
@Component({
  selector: 'an-smart-controls-pane',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'flex h-full min-h-0 flex-col', role: 'region', 'aria-label': 'Smart Controls' },
  template: `<p class="text-foreground-muted p-3 text-xs">Select a track to see its Smart Controls.</p>`
})
export class SmartControlsPane {
  protected readonly project = inject(ProjectStore);
}
