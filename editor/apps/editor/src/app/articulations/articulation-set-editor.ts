import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { FloatingWindow } from '../shell/floating-window';
import { UiStore } from '../state/ui.store';

/** The Articulation Set editor window (docs/08-phase-7.md). Placeholder until its workstream lands. */
@Component({
  selector: 'an-articulation-set-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FloatingWindow],
  template: `
    <an-floating-window
      title="Articulation Set"
      testId="articulation-set-editor"
      (closed)="ui.articulationSetEditor.set(null)"
    >
      <p class="text-foreground-muted p-4 text-[12px]">Articulation Set</p>
    </an-floating-window>
  `
})
export class ArticulationSetEditor {
  protected readonly ui = inject(UiStore);
}
