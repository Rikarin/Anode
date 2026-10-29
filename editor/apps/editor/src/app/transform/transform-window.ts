import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { FloatingWindow } from '../shell/floating-window';
import { UiStore } from '../state/ui.store';

/** The MIDI Transform window (docs/08-phase-7.md). Placeholder until its workstream lands. */
@Component({
  selector: 'an-transform-window',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FloatingWindow],
  template: `
    <an-floating-window title="MIDI Transform" testId="transform-window" (closed)="ui.transformOpen.set(false)">
      <p class="text-foreground-muted p-4 text-[12px]">MIDI Transform</p>
    </an-floating-window>
  `
})
export class TransformWindow {
  protected readonly ui = inject(UiStore);
}
